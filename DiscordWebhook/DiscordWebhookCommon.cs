#region "copyright"

/*
    Copyright (c) 2024 Dale Ghent <daleg@elemental.org>

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/
*/

#endregion "copyright"

using DaleGhent.NINA.GroundStation.Images;
using Discord;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GSUtilities = DaleGhent.NINA.GroundStation.Utilities.Utilities;

namespace DaleGhent.NINA.GroundStation.DiscordWebhook {
    public sealed class DiscordApiException : Exception {
        public DiscordApiException(string message, Exception innerException = null) : base(message, innerException) {
        }
    }

    /// <summary>
    /// Progress snapshot for old-thread cleanup. While the channel scan is running <see cref="TotalCount"/>
    /// is 0 and only <see cref="LogLine"/> is meaningful; once deletion starts, counts and the time
    /// estimate are populated.
    /// </summary>
    public sealed record ThreadCleanupProgress(string LogLine, int CompletedCount, int TotalCount, TimeSpan? EstimatedTimeRemaining);

    public class DiscordWebhookCommon {
        private static readonly HttpClient httpClient = new();
        private static readonly SemaphoreSlim sessionThreadLock = new(1, 1);

        // Only read or written while holding sessionThreadLock.
        private static string cachedBotUserId;
        private static string cachedBotUserToken;
        private static readonly string[] allowedMentionTypes = new[] { "everyone", "users", "roles" };
        private const string failureSeedPrefix = "❌ ";
        private const string seedMarkerPrefix = "\u2060\u2063";
        private const int publicTextChannelType = 0;
        private const int announcementChannelType = 5;
        private const int announcementThreadChannelType = 10;
        private const int publicThreadChannelType = 11;
        private const int privateThreadChannelType = 12;
        private const int defaultAutoArchiveDurationMinutes = 1440;
        private const int maxDiscordRateLimitRetries = 5;
        private const int maxParentScanMessages = 100;
        private static readonly TimeSpan defaultDiscordRateLimitDelay = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan visibleParentScanWindow = TimeSpan.FromMinutes(defaultAutoArchiveDurationMinutes);

        public async Task SendDiscordWebhook(string text, bool isFailure = false, bool bypassSessionThread = false) {
            try {
                await SendWebhookPayload(isFailure ? DiscordWebhookRoute.Failure : DiscordWebhookRoute.Default, text, null, bypassSessionThread: bypassSessionThread);
            } catch (DiscordApiException) {
                throw;
            } catch (Exception ex) {
                throw new Exception($"Failed to send Discord webhook: {ex.Message}", ex);
            }
        }

        public async Task SendDiscordWebhook(string message, IList<Embed> embeds, bool isFailure = false, bool bypassSessionThread = false) {
            try {
                await SendWebhookPayload(isFailure ? DiscordWebhookRoute.Failure : DiscordWebhookRoute.Default, message, embeds, bypassSessionThread: bypassSessionThread);
            } catch (DiscordApiException) {
                throw;
            } catch (Exception ex) {
                throw new Exception($"Failed to send Discord webhook: {ex.Message}", ex);
            }
        }

        public async Task SendDiscordImage(ImageData imageData, string fileName, IList<Embed> embeds) {
            try {
                if (string.IsNullOrEmpty(GroundStation.GroundStationConfig.DiscordImageWebhookUrl) && string.IsNullOrEmpty(GroundStation.GroundStationConfig.DiscordWebhookDefaultUrl)) {
                    throw new Exception("No webhook URL is set");
                }

                await SendWebhookPayload(DiscordWebhookRoute.Image, string.Empty, embeds, imageData.Bitmap, fileName);
            } catch (DiscordApiException) {
                throw;
            } catch (Exception ex) {
                throw new Exception($"Failed to send Discord webhook: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Deletes session threads that Ground Station created at least <paramref name="minimumAgeDays"/> days ago,
        /// along with the top-level starter messages that anchor them and every message inside them. Only messages
        /// carrying Ground Station's invisible session-seed marker are eligible: messages posted directly to the
        /// channel (for example end-of-session summaries sent with "Post directly to channel") are always kept, even
        /// if a thread was later attached to them, as is anything a person or another integration posted.
        /// </summary>
        /// <returns>The number of session threads deleted.</returns>
        public async Task<int> DeleteOldSessionThreads(int minimumAgeDays, IProgress<ThreadCleanupProgress> progress = null, CancellationToken cancellationToken = default) {
            if (string.IsNullOrEmpty(GroundStation.GroundStationConfig.DiscordWebhookDefaultUrl)) {
                throw CreateDiscordApiException("Discord Webhook Error", "No webhook URL is set.");
            }

            if (string.IsNullOrWhiteSpace(GroundStation.GroundStationConfig.DiscordBotToken)) {
                throw CreateDiscordApiException("Discord Bot Error", "Deleting old session threads requires a bot token.");
            }

            var webhookMetadata = await GetWebhookMetadata(GroundStation.GroundStationConfig.DiscordWebhookDefaultUrl);
            var parentChannel = await GetChannel(webhookMetadata.ChannelId);
            if (!RequiresBotThreadCreation(parentChannel)) {
                throw CreateDiscordApiException("Discord Bot Error", "Old-thread cleanup is only available when the webhook points to a normal text or announcement channel.");
            }

            // Age is measured from local midnight so "1 day" spares everything posted today. The cutoff
            // is additionally capped at the current session's start so a session that began yesterday
            // evening and is still running can never lose its own thread.
            var now = DateTime.Now;
            var rollover = GroundStation.GroundStationConfig.SessionRolloverTimeSpan;
            var currentSessionStart = now.TimeOfDay >= rollover ? now.Date + rollover : now.Date.AddDays(-1) + rollover;
            var cutoffDateTime = now.Date.AddDays(-minimumAgeDays);
            if (cutoffDateTime > currentSessionStart) {
                cutoffDateTime = currentSessionStart;
            }
            var cutoff = new DateTimeOffset(cutoffDateTime);
            Logger.Info($"Discord old-thread cleanup starting parentChannelId='{webhookMetadata.ChannelId}' minimumAgeDays={minimumAgeDays} cutoff='{cutoff:O}'");

            await sessionThreadLock.WaitAsync(cancellationToken);

            try {
                var candidates = await FindOldSessionThreads(webhookMetadata, cutoff, progress, cancellationToken);

                if (candidates.Count == 0) {
                    progress?.Report(new ThreadCleanupProgress("No session threads are old enough to delete.", 0, 0, TimeSpan.Zero));
                    Logger.Info($"Discord old-thread cleanup found nothing to delete in parentChannelId='{webhookMetadata.ChannelId}'");
                    return 0;
                }

                progress?.Report(new ThreadCleanupProgress($"Found {candidates.Count} session thread(s) to delete.", 0, candidates.Count, null));

                var deletedThreadCount = 0;
                var attemptedCount = 0;
                var stopwatch = Stopwatch.StartNew();

                foreach (var candidate in candidates) {
                    cancellationToken.ThrowIfCancellationRequested();
                    attemptedCount++;
                    string logLine;

                    try {
                        Logger.Info($"Discord old-thread cleanup deleting thread '{candidate.ThreadId}' and starter message '{candidate.MessageId}' created '{candidate.CreatedAt:O}'");

                        try {
                            await SendDiscordBotRequest(HttpMethod.Delete, $"https://discord.com/api/v10/channels/{candidate.ThreadId}", notifyOnFailure: false);
                        } catch (DiscordApiException ex) when (ex.Message.Contains("returned 404", StringComparison.Ordinal)) {
                            Logger.Info($"Discord old-thread cleanup found thread '{candidate.ThreadId}' already gone.");
                        }

                        await DeleteChannelMessageWithBot(webhookMetadata.ChannelId, candidate.MessageId, notifyOnFailure: false);
                        deletedThreadCount++;
                        logLine = $"Deleted '{candidate.DisplayName}' from {candidate.CreatedAt.ToLocalTime():yyyy-MM-dd}";
                    } catch (DiscordApiException ex) {
                        Logger.Error($"Discord old-thread cleanup could not delete thread '{candidate.ThreadId}' or its starter message '{candidate.MessageId}'. Continuing.", ex);
                        logLine = $"FAILED to delete '{candidate.DisplayName}' — see the NINA log for details";
                    }

                    var remainingCount = candidates.Count - attemptedCount;
                    var estimatedTimeRemaining = remainingCount > 0
                        ? TimeSpan.FromTicks(stopwatch.Elapsed.Ticks / attemptedCount * remainingCount)
                        : TimeSpan.Zero;
                    progress?.Report(new ThreadCleanupProgress(logLine, attemptedCount, candidates.Count, estimatedTimeRemaining));
                }

                progress?.Report(new ThreadCleanupProgress($"Finished. Deleted {deletedThreadCount} of {candidates.Count} session thread(s).", candidates.Count, candidates.Count, TimeSpan.Zero));
                Logger.Info($"Discord old-thread cleanup finished parentChannelId='{webhookMetadata.ChannelId}' deletedThreadCount={deletedThreadCount}");
                return deletedThreadCount;
            } finally {
                sessionThreadLock.Release();
            }
        }

        private async Task<List<OldSessionThreadCandidate>> FindOldSessionThreads(WebhookMetadata webhookMetadata, DateTimeOffset cutoff, IProgress<ThreadCleanupProgress> progress, CancellationToken cancellationToken) {
            var candidates = new List<OldSessionThreadCandidate>();
            var scannedMessageCount = 0;
            string beforeMessageId = null;
            JArray messages;

            progress?.Report(new ThreadCleanupProgress($"Scanning channel history for session threads created before {cutoff.LocalDateTime:g}...", 0, 0, null));

            do {
                cancellationToken.ThrowIfCancellationRequested();
                var previousCursor = beforeMessageId;
                messages = await GetChannelMessages(webhookMetadata.ChannelId, beforeMessageId);

                foreach (var message in messages.OfType<JObject>()) {
                    var messageId = message["id"]?.Value<string>();
                    if (string.IsNullOrWhiteSpace(messageId)) {
                        continue;
                    }

                    // Pages arrive newest first, so the last id seen becomes the next page's cursor.
                    beforeMessageId = messageId;
                    scannedMessageCount++;

                    var createdAt = GetMessageCreatedAt(message);
                    if (!createdAt.HasValue || createdAt.Value >= cutoff) {
                        continue;
                    }

                    var threadId = message["thread"]?["id"]?.Value<string>()?.Trim();
                    if (string.IsNullOrWhiteSpace(threadId)) {
                        // Plain top-level messages, such as end-of-session summaries, are kept.
                        continue;
                    }

                    // Only session-thread seed messages carry Ground Station's invisible marker.
                    // Requiring it guarantees that messages posted directly to the channel are
                    // never deleted, even if someone manually started a thread from one of them.
                    var messageContent = message["content"]?.Value<string>()?.Trim();
                    var hasSeedMarker = !string.IsNullOrEmpty(messageContent)
                        && messageContent.StartsWith(seedMarkerPrefix, StringComparison.Ordinal);
                    if (!hasSeedMarker) {
                        continue;
                    }

                    var threadName = message["thread"]?["name"]?.Value<string>()?.Trim();
                    var displayName = string.IsNullOrWhiteSpace(threadName) ? $"thread {threadId}" : threadName;
                    candidates.Add(new OldSessionThreadCandidate(threadId, messageId, displayName, createdAt.Value));
                }

                progress?.Report(new ThreadCleanupProgress($"Scanned {scannedMessageCount} channel message(s), found {candidates.Count} old session thread(s) so far...", 0, 0, null));

                if (string.Equals(beforeMessageId, previousCursor, StringComparison.Ordinal)) {
                    break;
                }
            } while (messages.Count == maxParentScanMessages);

            return candidates;
        }

        private async Task SendWebhookPayload(DiscordWebhookRoute route, string message, IList<Embed> embeds, Stream attachmentStream = null, string attachmentFileName = null, bool bypassSessionThread = false) {
            var webhookUrl = GetWebhookUrl(route);
            var webhookMetadata = await GetWebhookMetadata(webhookUrl);
            var hasBotToken = !string.IsNullOrWhiteSpace(GroundStation.GroundStationConfig.DiscordBotToken);
            var useSessionThreads = GroundStation.GroundStationConfig.DiscordUseSessionThreads;

            Logger.Debug($"Discord send start route={route} useSessionThreads={useSessionThreads} bypassSessionThread={bypassSessionThread} hasBotToken={hasBotToken} parentChannelId='{webhookMetadata.ChannelId}' guildId='{webhookMetadata.GuildId}'");

            if (bypassSessionThread || !useSessionThreads) {
                await SendValidatedWebhookMessage(webhookUrl, message, embeds, attachmentStream, attachmentFileName, expectedChannelId: webhookMetadata.ChannelId);
                return;
            }

            // Serialized so that concurrent sends cannot each create or reconcile the same session thread.
            await sessionThreadLock.WaitAsync();

            try {
                var threadKey = GetThreadKey(DateTime.Now);

                if (!hasBotToken) {
                    Logger.Info($"Discord send route={route} attempting native webhook thread behavior without a bot token");

                    try {
                        var nativeResponse = await ExecuteWebhookRequest(webhookUrl, message, embeds, attachmentStream, attachmentFileName, waitForResponse: true, threadName: threadKey, notifyOnFailure: false);
                        var nativeChannelId = GetRequiredString(nativeResponse, "channel_id", "Discord Webhook Error", "Discord did not return a channel id in the webhook response.", notifyOnFailure: false);

                        if (!string.Equals(nativeChannelId, webhookMetadata.ChannelId, StringComparison.Ordinal)) {
                            ValidateWebhookMessage(nativeResponse, nativeChannelId, "Discord Webhook Error", "Discord returned an invalid webhook thread response.", notifyOnFailure: false);
                            Logger.Info($"Discord send route={route} posted through native webhook thread behavior to thread '{nativeChannelId}'");
                            return;
                        }

                        ValidateWebhookMessage(nativeResponse, webhookMetadata.ChannelId, "Discord Webhook Error", "Discord returned an invalid webhook message response.", notifyOnFailure: false);
                        Logger.Info($"Discord send route={route} used plain webhook behavior because the native webhook thread request posted to the parent channel");
                        return;
                    } catch (DiscordApiException ex) when (CanFallBackToPlainWebhook(ex)) {
                        Logger.Info($"Discord send route={route} falling back to plain webhook behavior because native webhook thread creation is unsupported for this channel type");
                        await SendValidatedWebhookMessage(webhookUrl, message, embeds, attachmentStream, attachmentFileName, expectedChannelId: webhookMetadata.ChannelId);
                        return;
                    }
                }

                var parentChannel = await GetChannel(webhookMetadata.ChannelId);
                ValidateParentChannel(parentChannel, webhookMetadata.ChannelId);
                var usesBotManagedTextThread = RequiresBotThreadCreation(parentChannel);
                var shouldMarkFailureSeedMessage = route == DiscordWebhookRoute.Failure
                    && string.IsNullOrWhiteSpace(GroundStation.GroundStationConfig.DiscordFailureWebhookUrl)
                    && usesBotManagedTextThread;
                Logger.Debug($"Discord send route={route} resolved threadKey='{threadKey}'");

                if (usesBotManagedTextThread) {
                    var botManagedThread = await ResolveBotManagedTextThread(webhookMetadata, threadKey);
                    await SendValidatedWebhookMessage(webhookUrl, message, embeds, attachmentStream, attachmentFileName, expectedChannelId: botManagedThread.ThreadId, threadId: botManagedThread.ThreadId);
                    if (shouldMarkFailureSeedMessage) {
                        await TryMarkThreadStarterMessageAsFailed(webhookMetadata, botManagedThread.ThreadId, threadKey, botManagedThread.CanEditFailureSeed);
                    }
                    Logger.Info($"Discord send route={route} posted to session thread '{botManagedThread.ThreadId}'");
                    return;
                }

                var nonBotExistingThread = await FindActiveThreadByName(webhookMetadata.GuildId, webhookMetadata.ChannelId, threadKey);
                if (nonBotExistingThread != null) {
                    var existingThreadId = GetRequiredString(nonBotExistingThread, "id", "Discord Bot Error", $"Discord returned an active thread without an id for name '{threadKey}'.");
                    Logger.Info($"Discord send route={route} resolved active thread '{existingThreadId}' by name '{threadKey}'");
                    await SendValidatedWebhookMessage(webhookUrl, message, embeds, attachmentStream, attachmentFileName, expectedChannelId: existingThreadId, threadId: existingThreadId);
                    Logger.Info($"Discord send route={route} posted to active thread '{existingThreadId}'");
                    return;
                }

                var responseJson = await ExecuteWebhookRequest(webhookUrl, message, embeds, attachmentStream, attachmentFileName, waitForResponse: true, threadName: threadKey);
                var createdThreadId = GetRequiredString(responseJson, "channel_id", "Discord Webhook Error", "Discord did not return a thread id in the webhook response.");

                if (createdThreadId == webhookMetadata.ChannelId) {
                    throw CreateDiscordApiException("Discord Webhook Error", "Discord accepted the webhook message but did not create a thread. A normal text channel requires a bot token to create new session threads.");
                }

                ValidateWebhookMessage(responseJson, createdThreadId, "Discord Webhook Error", "Discord returned an invalid forum/media thread webhook response.");
                var createdThread = await GetChannel(createdThreadId);
                ValidateThreadChannel(createdThread, webhookMetadata.ChannelId, "Discord Webhook Error", "Discord returned an invalid thread after webhook thread creation.");
                await ValidateActiveThreadInGuild(webhookMetadata.GuildId, createdThreadId, webhookMetadata.ChannelId, "Discord Webhook Error", "Discord did not report the created thread as active.");
                Logger.Info($"Discord send route={route} created forum/media thread '{createdThreadId}'");
            } finally {
                sessionThreadLock.Release();
            }
        }

        private async Task<JObject> ExecuteWebhookRequest(string webhookUrl, string message, IList<Embed> embeds, Stream attachmentStream, string attachmentFileName, bool waitForResponse = false, string threadId = null, string threadName = null, bool notifyOnFailure = true) {
            var requestUrl = BuildWebhookUrl(webhookUrl, waitForResponse, threadId);

            // Buffered up front because a rate-limit retry rebuilds the request, and the
            // previous attempt's disposal would have taken the source stream with it.
            byte[] attachmentBytes = null;
            if (attachmentStream != null) {
                attachmentStream.Position = 0;
                using var buffer = new MemoryStream();
                await attachmentStream.CopyToAsync(buffer);
                attachmentBytes = buffer.ToArray();
            }

            var (response, responseBody) = await SendDiscordRequestWithRateLimitRetry(() => {
                var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
                var payload = new {
                    content = message,
                    username = GroundStation.GroundStationConfig.DiscordWebhookDefaultBotName,
                    allowed_mentions = new {
                        parse = allowedMentionTypes,
                    },
                    embeds = embeds?.Select(ToDiscordPayload).ToArray(),
                    thread_name = threadName,
                };

                if (attachmentBytes != null) {
                    var multipartContent = new MultipartFormDataContent();
                    multipartContent.Add(new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json"), "payload_json");

                    var fileContent = new ByteArrayContent(attachmentBytes);
                    fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    multipartContent.Add(fileContent, "files[0]", attachmentFileName);
                    request.Content = multipartContent;
                } else {
                    request.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
                }

                return request;
            }, "webhook", HttpMethod.Post, requestUrl, "Failed to send webhook request to Discord", notifyOnFailure);

            using (response) {
                if (!response.IsSuccessStatusCode) {
                    throw CreateDiscordApiException("Discord Webhook Error", $"Discord webhook API returned {(int)response.StatusCode} {response.ReasonPhrase}: {responseBody}", null, notifyOnFailure);
                }

                if (!waitForResponse || string.IsNullOrWhiteSpace(responseBody)) {
                    return null;
                }

                try {
                    return JObject.Parse(responseBody);
                } catch (Exception ex) {
                    throw CreateDiscordApiException("Discord Webhook Error", $"Discord returned an invalid webhook response: {ex.Message}", ex, notifyOnFailure);
                }
            }
        }

        private async Task<JObject> SendValidatedWebhookMessage(string webhookUrl, string message, IList<Embed> embeds, Stream attachmentStream, string attachmentFileName, string expectedChannelId, string threadId = null, string threadName = null, bool notifyOnFailure = true) {
            var responseJson = await ExecuteWebhookRequest(webhookUrl, message, embeds, attachmentStream, attachmentFileName, waitForResponse: true, threadId: threadId, threadName: threadName, notifyOnFailure: notifyOnFailure);
            ValidateWebhookMessage(responseJson, expectedChannelId, "Discord Webhook Error", "Discord returned an invalid webhook message response.", notifyOnFailure);
            Logger.Debug($"Discord webhook post validated expectedChannelId='{expectedChannelId}' responseMessageId='{responseJson?["id"]?.Value<string>()}' responseChannelId='{responseJson?["channel_id"]?.Value<string>()}' threadId='{threadId}' threadName='{threadName}'");
            return responseJson;
        }

        private object ToDiscordPayload(Embed embed) {
            return new {
                title = embed.Title,
                description = embed.Description,
                url = embed.Url,
                timestamp = embed.Timestamp?.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                color = embed.Color?.RawValue,
                author = embed.Author == null ? null : new {
                    name = embed.Author.Value.Name,
                    url = embed.Author.Value.Url,
                    icon_url = embed.Author.Value.IconUrl,
                },
                footer = embed.Footer == null ? null : new {
                    text = embed.Footer.Value.Text,
                    icon_url = embed.Footer.Value.IconUrl,
                },
                fields = embed.Fields.Select(field => new {
                    name = field.Name,
                    value = field.Value,
                    inline = field.Inline,
                }).ToArray(),
                image = embed.Image == null ? null : new {
                    url = embed.Image.Value.Url,
                },
                thumbnail = embed.Thumbnail == null ? null : new {
                    url = embed.Thumbnail.Value.Url,
                },
            };
        }

        private string BuildWebhookUrl(string webhookUrl, bool waitForResponse, string threadId) {
            var query = new List<string>();

            if (waitForResponse) {
                query.Add("wait=true");
            }

            if (!string.IsNullOrWhiteSpace(threadId)) {
                query.Add($"thread_id={Uri.EscapeDataString(threadId)}");
            }

            if (query.Count == 0) {
                return webhookUrl;
            }

            var separator = webhookUrl.Contains("?") ? "&" : "?";
            return $"{webhookUrl}{separator}{string.Join("&", query)}";
        }

        private string GetWebhookUrl(DiscordWebhookRoute route) {
            return route switch {
                DiscordWebhookRoute.Failure when !string.IsNullOrWhiteSpace(GroundStation.GroundStationConfig.DiscordFailureWebhookUrl) => GroundStation.GroundStationConfig.DiscordFailureWebhookUrl,
                DiscordWebhookRoute.Image when !string.IsNullOrWhiteSpace(GroundStation.GroundStationConfig.DiscordImageWebhookUrl) => GroundStation.GroundStationConfig.DiscordImageWebhookUrl,
                _ => GroundStation.GroundStationConfig.DiscordWebhookDefaultUrl,
            };
        }

        private string GetThreadKey(DateTime now) {
            var template = GroundStation.GroundStationConfig.DiscordThreadNameTemplate;
            return GSUtilities.ResolveTokens(template, nowOverride: now).Trim();
        }

        private bool RequiresBotThreadCreation(JObject parentChannel) {
            var channelType = parentChannel["type"]?.Value<int>()
                ?? throw CreateDiscordApiException("Discord Bot Error", "Discord did not return a channel type for the webhook channel.");
            return channelType == publicTextChannelType || channelType == announcementChannelType;
        }

        private async Task<string> CreateThreadWithBot(WebhookMetadata webhookMetadata, string threadName) {
            var starterMessageId = await CreateStarterMessageWithWebhook(webhookMetadata, threadName);
            return await CreateThreadFromStarterMessageWithBot(webhookMetadata, starterMessageId, threadName);
        }

        private async Task<string> CreateThreadFromStarterMessageWithBot(WebhookMetadata webhookMetadata, string starterMessageId, string threadName) {
            Logger.Info($"Discord bot creating thread from starterMessageId='{starterMessageId}' parentChannelId='{webhookMetadata.ChannelId}' threadName='{threadName}'");
            var thread = await SendDiscordBotRequest(HttpMethod.Post,
                $"https://discord.com/api/v10/channels/{webhookMetadata.ChannelId}/messages/{starterMessageId}/threads",
                new {
                    name = threadName,
                    auto_archive_duration = defaultAutoArchiveDurationMinutes,
                });

            var threadId = GetRequiredString(thread, "id", "Discord Bot Error", "Discord did not return a thread id after thread creation.");
            ValidateThreadChannel(thread, webhookMetadata.ChannelId, "Discord Bot Error", "Discord returned an invalid thread creation response.");

            var validatedThread = await GetChannel(threadId);
            ValidateThreadChannel(validatedThread, webhookMetadata.ChannelId, "Discord Bot Error", "Discord could not validate the created thread.");
            await ValidateActiveThreadInGuild(webhookMetadata.GuildId, threadId, webhookMetadata.ChannelId, "Discord Bot Error", "Discord did not report the created thread as active.");
            Logger.Info($"Discord bot created threadId='{threadId}' for parentChannelId='{webhookMetadata.ChannelId}'");
            return threadId;
        }

        private async Task<WebhookMetadata> GetWebhookMetadata(string webhookUrl, bool notifyOnFailure = true) {
            var (response, responseBody) = await SendDiscordRequestWithRateLimitRetry(
                () => new HttpRequestMessage(HttpMethod.Get, webhookUrl),
                "webhook-metadata",
                HttpMethod.Get,
                webhookUrl,
                "Failed to fetch Discord webhook metadata",
                notifyOnFailure);

            using (response) {
                if (!response.IsSuccessStatusCode) {
                    throw CreateDiscordApiException("Discord Webhook Error", $"Discord webhook metadata request returned {(int)response.StatusCode} {response.ReasonPhrase}: {responseBody}", null, notifyOnFailure);
                }

                JObject responseJson;
                try {
                    responseJson = JObject.Parse(responseBody);
                } catch (Exception ex) {
                    throw CreateDiscordApiException("Discord Webhook Error", $"Discord returned invalid webhook metadata: {ex.Message}", ex, notifyOnFailure);
                }

                var webhookId = GetRequiredString(responseJson, "id", "Discord Webhook Error", "Discord did not return an id for the configured webhook.", notifyOnFailure);
                var channelId = GetRequiredString(responseJson, "channel_id", "Discord Webhook Error", "Discord did not return a channel id for the configured webhook.", notifyOnFailure);
                var guildId = GetRequiredString(responseJson, "guild_id", "Discord Webhook Error", "Discord did not return a guild id for the configured webhook.", notifyOnFailure);
                return new WebhookMetadata(webhookUrl, webhookId, channelId, guildId);
            }
        }

        private async Task<JObject> GetChannel(string channelId, bool notifyOnFailure = true) {
            return await SendDiscordBotRequest(HttpMethod.Get, $"https://discord.com/api/v10/channels/{channelId}", notifyOnFailure: notifyOnFailure);
        }

        private async Task<string> CreateStarterMessageWithWebhook(WebhookMetadata webhookMetadata, string threadName) {
            var message = await SendValidatedWebhookMessage(webhookMetadata.Url, BuildSeedText(threadName), null, null, null, expectedChannelId: webhookMetadata.ChannelId);

            return GetRequiredString(message, "id", "Discord Webhook Error", "Discord did not return a starter message id.");
        }

        private async Task<BotManagedThreadResolution> ResolveBotManagedTextThread(WebhookMetadata webhookMetadata, string threadName) {
            var visibleEntries = await GetVisibleParentEntries(webhookMetadata);
            visibleEntries = await PurgeUnattachedFailedSeedMessages(webhookMetadata, visibleEntries);
            var candidateThreads = await GetOrphanCleanupCandidates(webhookMetadata.GuildId, webhookMetadata.ChannelId);
            var existingThread = await ResolveActiveThreadFromSnapshot(candidateThreads, visibleEntries, webhookMetadata, threadName);
            var reusableStarterMessage = FindReusableStarterMessage(visibleEntries, webhookMetadata.ChannelId, threadName);

            if (existingThread != null && reusableStarterMessage != null && string.Equals(existingThread["id"]?.Value<string>(), reusableStarterMessage.ThreadId, StringComparison.Ordinal)) {
                if (reusableStarterMessage.DuplicateMessageIdsToDelete.Length > 0) {
                    await TryDeleteDuplicateParentMessages(webhookMetadata, reusableStarterMessage.DuplicateMessageIdsToDelete, threadName);
                }

                var existingThreadId = GetRequiredString(existingThread, "id", "Discord Bot Error", $"Discord returned an active thread without an id for name '{threadName}'.");
                Logger.Info($"Discord resolved active session thread '{existingThreadId}' directly from reconciliation snapshot for thread '{threadName}'");
                return new BotManagedThreadResolution(existingThreadId, reusableStarterMessage.CanEditFailureSeed);
            }

            if (reusableStarterMessage != null) {
                return await TryCreateThreadFromExistingStarterMessage(webhookMetadata, reusableStarterMessage, threadName);
            }

            Logger.Info($"Discord did not find a visible starter message for thread '{threadName}'. Creating a new starter message and thread.");
            var botCreatedThreadId = await CreateThreadWithBot(webhookMetadata, threadName);
            if (string.IsNullOrWhiteSpace(botCreatedThreadId)) {
                throw CreateDiscordApiException("Discord Bot Error", "The Discord bot could not create a thread for this text channel.");
            }

            Logger.Info($"Discord created new session thread '{botCreatedThreadId}' for thread '{threadName}'");
            return new BotManagedThreadResolution(botCreatedThreadId, true);
        }

        private async Task<List<VisibleParentEntry>> PurgeUnattachedFailedSeedMessages(WebhookMetadata webhookMetadata, List<VisibleParentEntry> visibleEntries) {
            if (visibleEntries == null || visibleEntries.Count == 0) {
                return visibleEntries ?? [];
            }

            var unattachedFailedEntries = visibleEntries
                .Where(entry =>
                    string.IsNullOrWhiteSpace(entry.ThreadId)
                    && IsFailedSeedText(entry.SeedText))
                .OrderBy(entry => entry.Order)
                .ToList();

            if (unattachedFailedEntries.Count == 0) {
                return visibleEntries;
            }

            Logger.Warning($"Discord found {unattachedFailedEntries.Count} unattached failed seed message(s) in parent channel '{webhookMetadata.ChannelId}'. Deleting them before thread reconciliation.");
            await TryDeleteDuplicateParentMessages(webhookMetadata, unattachedFailedEntries.Select(entry => entry.MessageId), "unattached failed seed cleanup");

            var unattachedFailedMessageIds = unattachedFailedEntries
                .Select(entry => entry.MessageId)
                .ToHashSet(StringComparer.Ordinal);

            return visibleEntries
                .Where(entry => !unattachedFailedMessageIds.Contains(entry.MessageId))
                .ToList();
        }

        private async Task<BotManagedThreadResolution> TryCreateThreadFromExistingStarterMessage(WebhookMetadata webhookMetadata, ReusableStarterMessage reusableStarterMessage, string threadName) {
            if (reusableStarterMessage == null) {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(reusableStarterMessage.ThreadId)) {
                var reusableThreadId = await TryReuseOrReopenThreadReferencedByParentMessage(webhookMetadata, reusableStarterMessage, threadName);
                if (!string.IsNullOrWhiteSpace(reusableThreadId)) {
                    if (reusableStarterMessage.DuplicateMessageIdsToDelete.Length > 0) {
                        await TryDeleteDuplicateParentMessages(webhookMetadata, reusableStarterMessage.DuplicateMessageIdsToDelete, threadName);
                    }
                    return new BotManagedThreadResolution(reusableThreadId, reusableStarterMessage.CanEditFailureSeed);
                }
            }

            Logger.Info($"Discord attempting to recreate thread '{threadName}' from existing parent message '{reusableStarterMessage.MessageId}'");
            var recreatedThreadId = await CreateThreadFromStarterMessageWithBot(webhookMetadata, reusableStarterMessage.MessageId, threadName);
            if (reusableStarterMessage.DuplicateMessageIdsToDelete.Length > 0) {
                await TryDeleteDuplicateParentMessages(webhookMetadata, reusableStarterMessage.DuplicateMessageIdsToDelete, threadName);
            }
            return new BotManagedThreadResolution(recreatedThreadId, reusableStarterMessage.CanEditFailureSeed);
        }

        private async Task<string> TryReuseOrReopenThreadReferencedByParentMessage(WebhookMetadata webhookMetadata, ReusableStarterMessage reusableStarterMessage, string threadName) {
            try {
                var existingThread = await GetChannel(reusableStarterMessage.ThreadId, notifyOnFailure: false);
                ValidateThreadChannel(existingThread, webhookMetadata.ChannelId, "Discord Bot Error", $"Discord parent message '{reusableStarterMessage.MessageId}' pointed at an invalid thread.", notifyOnFailure: false);
                Logger.Info($"Discord reusing thread '{reusableStarterMessage.ThreadId}' from existing parent message '{reusableStarterMessage.MessageId}' for thread '{threadName}'");
                return reusableStarterMessage.ThreadId;
            } catch (Exception ex) {
                Logger.Warning($"Discord parent message '{reusableStarterMessage.MessageId}' referenced thread '{reusableStarterMessage.ThreadId}', but it could not be reused directly. Attempting to reopen it before recreation. {ex.Message}");
            }

            try {
                await SendDiscordBotRequest(new HttpMethod("PATCH"),
                    $"https://discord.com/api/v10/channels/{reusableStarterMessage.ThreadId}",
                    new {
                        archived = false,
                        locked = false,
                    },
                    notifyOnFailure: false);

                var reopenedThread = await GetChannel(reusableStarterMessage.ThreadId, notifyOnFailure: false);
                ValidateThreadChannel(reopenedThread, webhookMetadata.ChannelId, "Discord Bot Error", $"Discord could not reopen thread '{reusableStarterMessage.ThreadId}' referenced by parent message '{reusableStarterMessage.MessageId}'.", notifyOnFailure: false);
                Logger.Info($"Discord reopened thread '{reusableStarterMessage.ThreadId}' from existing parent message '{reusableStarterMessage.MessageId}' for thread '{threadName}'");
                return reusableStarterMessage.ThreadId;
            } catch (Exception ex) {
                Logger.Warning($"Discord could not reopen thread '{reusableStarterMessage.ThreadId}' referenced by parent message '{reusableStarterMessage.MessageId}'. Attempting recreation from the existing parent message. {ex.Message}");
                return null;
            }
        }

        private ReusableStarterMessage FindReusableStarterMessage(IEnumerable<VisibleParentEntry> visibleEntries, string parentChannelId, string threadName) {
            var matchingEntries = GetMatchingVisibleParentEntries(visibleEntries, threadName).ToList();
            if (matchingEntries.Count == 0) {
                return null;
            }

            var authoritativeEntry = GetAuthoritativeVisibleParentEntry(visibleEntries, threadName, parentChannelId);

            if (authoritativeEntry == null) {
                return null;
            }

            var duplicateMessageIdsToDelete = matchingEntries
                .Where(entry => !string.Equals(entry.MessageId, authoritativeEntry.MessageId, StringComparison.Ordinal))
                .OrderByDescending(entry => entry.Order)
                .Select(entry => entry.MessageId)
                .ToArray();

            // GetAuthoritativeVisibleParentEntry already warned about duplicate seeds and the selection rule.
            Logger.Debug($"Discord found reusable starter message '{authoritativeEntry.MessageId}' for thread '{threadName}' with visibleThreadId='{authoritativeEntry.ThreadId ?? string.Empty}' seedText='{authoritativeEntry.SeedText}' duplicatesToDelete={duplicateMessageIdsToDelete.Length}");
            return new ReusableStarterMessage(authoritativeEntry.MessageId, authoritativeEntry.ThreadId, authoritativeEntry.IsWebhookOwned, duplicateMessageIdsToDelete);
        }

        private async Task TryMarkThreadStarterMessageAsFailed(WebhookMetadata webhookMetadata, string threadId, string threadName, bool canEditFailureSeed) {
            if (!canEditFailureSeed) {
                Logger.Info($"Discord left starter message '{threadId}' unchanged after failure because the selected seed row for thread '{threadName}' is not owned by the configured webhook.");
                return;
            }

            var failedSeedText = BuildFailedSeedText(threadName);

            try {
                // A thread created from a message shares its id with that starter message, so the thread id addresses the starter message here.
                await EditWebhookMessage(webhookMetadata.Url, threadId, new {
                    content = failedSeedText,
                    allowed_mentions = new {
                        parse = Array.Empty<string>(),
                    },
                }, notifyOnFailure: false);
                Logger.Info($"Discord marked starter message '{threadId}' as failed with content '{failedSeedText}'");
            } catch (Exception ex) {
                Logger.Error($"Failed to mark Discord starter message '{threadId}' as failed", ex);
            }
        }

        private async Task ValidateActiveThreadInGuild(string guildId, string threadId, string expectedParentChannelId, string title, string failureMessage, bool notifyOnFailure = true) {
            var response = await SendDiscordBotRequest(HttpMethod.Get, $"https://discord.com/api/v10/guilds/{guildId}/threads/active", notifyOnFailure: notifyOnFailure);
            var threads = response["threads"] as JArray
                ?? throw CreateDiscordApiException(title, $"{failureMessage} Discord did not return an active thread list for guild {guildId}.", notifyUser: notifyOnFailure);

            Logger.Debug($"Discord active-thread validation guildId='{guildId}' threadId='{threadId}' activeThreadCount={threads.Count}");

            var matchingThread = threads
                .OfType<JObject>()
                .FirstOrDefault(thread => string.Equals(thread["id"]?.Value<string>(), threadId, StringComparison.Ordinal));

            if (matchingThread == null) {
                throw CreateDiscordApiException(title, $"{failureMessage} Thread {threadId} is not present in the guild's active thread list.", notifyUser: notifyOnFailure);
            }

            ValidateThreadChannel(matchingThread, expectedParentChannelId, title, failureMessage, notifyOnFailure);
        }

        private async Task<JObject> FindActiveThreadByName(string guildId, string expectedParentChannelId, string threadName) {
            var response = await SendDiscordBotRequest(HttpMethod.Get, $"https://discord.com/api/v10/guilds/{guildId}/threads/active");
            var threads = response["threads"] as JArray
                ?? throw CreateDiscordApiException("Discord Bot Error", $"Discord did not return an active thread list for guild {guildId}.");

            Logger.Debug($"Discord active-thread lookup guildId='{guildId}' parentChannelId='{expectedParentChannelId}' threadName='{threadName}' activeThreadCount={threads.Count}");

            var matchingThreads = threads
                .OfType<JObject>()
                .Where(thread =>
                    string.Equals(thread["parent_id"]?.Value<string>(), expectedParentChannelId, StringComparison.Ordinal)
                    && string.Equals(thread["name"]?.Value<string>(), threadName, StringComparison.Ordinal))
                .OrderByDescending(thread => GetSnowflakeSortKey(thread["id"]?.Value<string>()))
                .ToList();

            if (matchingThreads.Count == 0) {
                Logger.Debug($"Discord active-thread lookup found no active thread named '{threadName}' for parentChannelId='{expectedParentChannelId}'");
                return null;
            }

            var matchingThread = matchingThreads[0];
            if (matchingThreads.Count > 1) {
                Logger.Warning($"Discord active-thread lookup found {matchingThreads.Count} active threads named '{threadName}' for parentChannelId='{expectedParentChannelId}'. Using newest thread '{matchingThread["id"]?.Value<string>()}'.");
            }

            ValidateThreadChannel(matchingThread, expectedParentChannelId, "Discord Bot Error", $"Discord active thread '{threadName}' is invalid.");
            return matchingThread;
        }

        private async Task<JObject> ResolveActiveThreadFromSnapshot(IEnumerable<JObject> candidateThreads, IEnumerable<VisibleParentEntry> visibleEntries, WebhookMetadata webhookMetadata, string targetThreadName) {
            var candidateThreadList = candidateThreads?.ToList() ?? new List<JObject>();
            var visibleEntryList = visibleEntries?.ToList() ?? new List<VisibleParentEntry>();
            var parentChannelId = webhookMetadata.ChannelId;

            if (candidateThreadList.Count == 0) {
                return null;
            }

            JObject resolvedThread = null;
            var resolvedThreadOrder = int.MaxValue;

            foreach (var thread in candidateThreadList) {
                var threadId = thread["id"]?.Value<string>();
                var threadName = thread["name"]?.Value<string>();

                if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(threadName)) {
                    continue;
                }

                // Only reconcile the session thread being sent right now.
                // A send for a new day must not delete yesterday's still-active thread.
                if (!string.Equals(threadName, targetThreadName, StringComparison.Ordinal)) {
                    continue;
                }

                var authoritativeEntry = GetAuthoritativeVisibleParentEntry(visibleEntryList, threadName, parentChannelId);

                if (authoritativeEntry == null) {
                    await TryDeleteOwnedThread(thread, visibleEntryList,
                        $"no seed message created by Ground Station exists for seed text '{threadName}' inside the non-archived scan window");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(authoritativeEntry.ThreadId)) {
                    await TryDeleteOwnedThread(thread, visibleEntryList,
                        $"seed message '{authoritativeEntry.MessageId}' with seed text '{authoritativeEntry.SeedText}' has no attached thread and should be reused");
                    continue;
                }

                if (!string.Equals(authoritativeEntry.ThreadId, threadId, StringComparison.Ordinal)) {
                    await TryDeleteOwnedThread(thread, visibleEntryList,
                        $"seed message '{authoritativeEntry.MessageId}' points to thread '{authoritativeEntry.ThreadId}' for seed text '{authoritativeEntry.SeedText}'");
                    continue;
                }

                var isArchived = thread["thread_metadata"]?["archived"]?.Value<bool>() ?? true;
                if (isArchived) {
                    continue;
                }

                if (resolvedThread == null || authoritativeEntry.Order < resolvedThreadOrder) {
                    if (resolvedThread != null) {
                        Logger.Warning($"Discord found multiple visible active threads named '{targetThreadName}' for parent channel '{parentChannelId}'. Using newer authoritative visible thread '{threadId}' instead of '{resolvedThread["id"]?.Value<string>()}'.");
                    }
                    resolvedThread = thread;
                    resolvedThreadOrder = authoritativeEntry.Order;
                } else {
                    Logger.Warning($"Discord found multiple visible active threads named '{targetThreadName}' for parent channel '{parentChannelId}'. Keeping newer authoritative visible thread '{resolvedThread["id"]?.Value<string>()}' and ignoring '{threadId}'.");
                }
            }

            return resolvedThread;
        }

        private async Task<List<JObject>> GetOrphanCleanupCandidates(string guildId, string parentChannelId) {
            var candidates = new Dictionary<string, JObject>(StringComparer.Ordinal);

            var activeResponse = await SendDiscordBotRequest(HttpMethod.Get, $"https://discord.com/api/v10/guilds/{guildId}/threads/active", notifyOnFailure: true);
            AddOrphanCleanupCandidates(candidates, activeResponse["threads"] as JArray, parentChannelId);

            Logger.Debug($"Discord seed cleanup parentChannelId='{parentChannelId}' activeCandidateThreadCount={candidates.Count}");
            return candidates.Values.ToList();
        }

        private void AddOrphanCleanupCandidates(IDictionary<string, JObject> candidates, JArray threads, string expectedParentChannelId) {
            if (threads == null) {
                return;
            }

            foreach (var thread in threads.OfType<JObject>()) {
                var threadId = thread["id"]?.Value<string>();
                var parentId = thread["parent_id"]?.Value<string>();
                var channelType = thread["type"]?.Value<int>();

                if (string.IsNullOrWhiteSpace(threadId) || !channelType.HasValue) {
                    continue;
                }

                if (!string.Equals(parentId, expectedParentChannelId, StringComparison.Ordinal)) {
                    continue;
                }

                if (!IsMessageBasedThreadChannelType(channelType.Value)) {
                    continue;
                }

                candidates[threadId] = thread;
            }
        }

        private async Task<List<VisibleParentEntry>> GetVisibleParentEntries(WebhookMetadata webhookMetadata) {
            var visibleEntries = new List<VisibleParentEntry>();
            var scanOrder = 0;
            var cutoffUtc = DateTimeOffset.UtcNow - visibleParentScanWindow;
            var parentChannelId = webhookMetadata.ChannelId;
            var messages = await GetChannelMessages(parentChannelId);
            var reachedCutoff = false;

            foreach (var message in messages.OfType<JObject>()) {
                var messageCreatedAt = GetMessageCreatedAt(message);
                if (messageCreatedAt.HasValue && messageCreatedAt.Value < cutoffUtc) {
                    reachedCutoff = true;
                    Logger.Debug($"Discord parent message scan channelId='{parentChannelId}' reached archive cutoff at messageId='{message["id"]?.Value<string>() ?? string.Empty}' createdAtUtc='{messageCreatedAt.Value:O}' cutoffUtc='{cutoffUtc:O}'");
                    break;
                }

                var messageId = message["id"]?.Value<string>();
                var messageWebhookId = message["webhook_id"]?.Value<string>()?.Trim();
                var messageContent = message["content"]?.Value<string>()?.Trim();
                var threadId = message["thread"]?["id"]?.Value<string>()?.Trim();
                var threadName = message["thread"]?["name"]?.Value<string>()?.Trim();

                // The invisible marker is written only by Ground Station, so it identifies our own
                // seed messages even when they were posted through a different configured webhook.
                var hasSeedMarker = !string.IsNullOrEmpty(messageContent)
                    && messageContent.StartsWith(seedMarkerPrefix, StringComparison.Ordinal);
                var isWebhookOwned = !string.IsNullOrWhiteSpace(messageWebhookId)
                    && string.Equals(messageWebhookId, webhookMetadata.WebhookId, StringComparison.Ordinal);

                var seedText = NormalizeSeedText(messageContent);

                Logger.Debug($"Discord parent message scan channelId='{parentChannelId}' messageId='{messageId}' createdAtUtc='{messageCreatedAt?.ToString("O") ?? string.Empty}' threadId='{threadId ?? string.Empty}' threadName='{threadName ?? string.Empty}' hasSeedMarker={hasSeedMarker} isWebhookOwned={isWebhookOwned}");

                // Reconciliation may only ever act on Ground Station's own seed messages, which are
                // the only messages that carry the invisible marker. Anything else — a person's post,
                // or a message sent with "Post directly to channel", even when its text matches a
                // session thread name — is ignored entirely so it can never be reused as a thread
                // starter or deleted as a duplicate.
                if (!string.IsNullOrWhiteSpace(messageId) && !string.IsNullOrWhiteSpace(seedText) && hasSeedMarker) {
                    visibleEntries.Add(new VisibleParentEntry(messageId, seedText, threadId, scanOrder, isWebhookOwned));
                }

                scanOrder++;
            }

            if (messages.Count == maxParentScanMessages && !reachedCutoff) {
                Logger.Warning($"Discord parent message scan channelId='{parentChannelId}' hit the single-request limit before reaching the archive cutoff. Older messages inside the active window were not scanned.");
            }

            Logger.Debug($"Discord seed cleanup parentChannelId='{parentChannelId}' visibleParentEntryCount={visibleEntries.Count}");
            return visibleEntries;
        }

        private static DateTimeOffset? GetMessageCreatedAt(JObject message) {
            var timestampToken = message?["timestamp"];
            if (timestampToken != null) {
                switch (timestampToken.Type) {
                    case JTokenType.Date:
                        if (timestampToken.Value<DateTime?>() is DateTime timestampDateTime) {
                            if (timestampDateTime.Kind == DateTimeKind.Unspecified) {
                                timestampDateTime = DateTime.SpecifyKind(timestampDateTime, DateTimeKind.Utc);
                            }

                            return new DateTimeOffset(timestampDateTime.ToUniversalTime(), TimeSpan.Zero);
                        }
                        break;

                    default:
                        var timestampString = timestampToken.Value<string>();
                        if (!string.IsNullOrWhiteSpace(timestampString)
                            && DateTimeOffset.TryParse(timestampString, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedTimestamp)) {
                            return parsedTimestamp.ToUniversalTime();
                        }
                        break;
                }
            }

            var messageId = message?["id"]?.Value<string>();
            if (ulong.TryParse(messageId, out var snowflake)) {
                var millisecondsSinceDiscordEpoch = (long)(snowflake >> 22);
                return DateTimeOffset.FromUnixTimeMilliseconds(millisecondsSinceDiscordEpoch + 1420070400000L);
            }

            return null;
        }

        private async Task<JArray> GetChannelMessages(string channelId, string beforeMessageId = null) {
            var url = $"https://discord.com/api/v10/channels/{channelId}/messages?limit={maxParentScanMessages}";
            if (!string.IsNullOrWhiteSpace(beforeMessageId)) {
                url += $"&before={Uri.EscapeDataString(beforeMessageId)}";
            }

            var (response, responseBody) = await SendDiscordRequestWithRateLimitRetry(() => {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bot", GroundStation.GroundStationConfig.DiscordBotToken);
                return request;
            }, "bot", HttpMethod.Get, url, $"Failed to list messages for Discord channel {channelId}", notifyOnFailure: true);

            using (response) {
                if (!response.IsSuccessStatusCode) {
                    throw CreateDiscordApiException("Discord Bot Error", $"Discord channel message list returned {(int)response.StatusCode} {response.ReasonPhrase}: {responseBody}", notifyUser: true);
                }

                if (string.IsNullOrWhiteSpace(responseBody)) {
                    return [];
                }

                try {
                    return JArray.Parse(responseBody);
                } catch (Exception ex) {
                    throw CreateDiscordApiException("Discord Bot Error", $"Discord returned an invalid channel message list response: {ex.Message}", ex, notifyUser: true);
                }
            }
        }

        private VisibleParentEntry GetAuthoritativeVisibleParentEntry(IEnumerable<VisibleParentEntry> visibleEntries, string threadName, string parentChannelId) {
            var matches = GetMatchingVisibleParentEntries(visibleEntries, threadName).ToList();

            if (matches.Count == 0) {
                return null;
            }

            var threadedMatches = matches
                .Where(entry => !string.IsNullOrWhiteSpace(entry.ThreadId))
                .OrderBy(entry => entry.Order)
                .ToList();
            var authoritativeEntry = threadedMatches.FirstOrDefault()
                ?? matches.OrderBy(entry => entry.Order).First();

            if (matches.Count > 1) {
                var selectionMode = threadedMatches.Count > 0
                    ? "newest message with an attached thread"
                    : "newest message because none of the duplicates have an attached thread";
                Logger.Warning($"Discord found multiple visible parent messages for seed text '{threadName}' in parent channel '{parentChannelId}'. Using {selectionMode} '{authoritativeEntry.MessageId}'.");
            }

            return authoritativeEntry;
        }

        private IEnumerable<VisibleParentEntry> GetMatchingVisibleParentEntries(IEnumerable<VisibleParentEntry> visibleEntries, string threadName) {
            return visibleEntries.Where(entry => SeedTextMatchesThreadName(entry.SeedText, threadName));
        }

        private static bool SeedTextMatchesThreadName(string seedText, string threadName) {
            var normalizedSeedText = NormalizeSeedText(seedText);
            if (string.IsNullOrWhiteSpace(normalizedSeedText) || string.IsNullOrWhiteSpace(threadName)) {
                return false;
            }

            return string.Equals(normalizedSeedText, threadName, StringComparison.Ordinal)
                || string.Equals(normalizedSeedText, $"{failureSeedPrefix}{threadName}", StringComparison.Ordinal);
        }

        private static bool IsFailedSeedText(string seedText) {
            var normalizedSeedText = NormalizeSeedText(seedText);
            return !string.IsNullOrWhiteSpace(normalizedSeedText)
                && normalizedSeedText.StartsWith(failureSeedPrefix, StringComparison.Ordinal);
        }

        private static string BuildSeedText(string threadName) {
            return $"{seedMarkerPrefix}{threadName}";
        }

        private static string BuildFailedSeedText(string threadName) {
            return $"{seedMarkerPrefix}{failureSeedPrefix}{threadName}";
        }

        private static string NormalizeSeedText(string seedText) {
            if (string.IsNullOrWhiteSpace(seedText)) {
                return seedText;
            }

            return seedText.StartsWith(seedMarkerPrefix, StringComparison.Ordinal)
                ? seedText.Substring(seedMarkerPrefix.Length)
                : seedText;
        }

        private static bool CanFallBackToPlainWebhook(DiscordApiException ex) {
            return Regex.IsMatch(ex.Message, "\"code\":\\s*220003")
                || ex.Message.Contains("Webhooks can only create threads in forum channels", StringComparison.Ordinal);
        }

        /// <summary>
        /// Deletes a stale session thread, but only when it can be proven to belong to Ground Station.
        /// Ownership holds when the thread grew out of one of our seed messages, or when our own bot
        /// user created it. A thread that cannot be attributed is left untouched.
        /// </summary>
        private async Task TryDeleteOwnedThread(JObject thread, IEnumerable<VisibleParentEntry> visibleEntries, string reason) {
            var threadId = thread["id"]?.Value<string>();

            if (string.IsNullOrWhiteSpace(threadId)) {
                return;
            }

            if (!await IsOwnedSessionThread(thread, threadId, visibleEntries)) {
                Logger.Warning($"Discord left thread '{threadId}' in place even though {reason}, because it was not created by Ground Station.");
                return;
            }

            Logger.Info($"Discord deleting session thread '{threadId}' because {reason}.");

            // Cleanup is best-effort: a failed delete must not abort the send that triggered it.
            try {
                await SendDiscordBotRequest(HttpMethod.Delete, $"https://discord.com/api/v10/channels/{threadId}", notifyOnFailure: false);
            } catch (DiscordApiException ex) when (ex.Message.Contains("returned 404", StringComparison.Ordinal)) {
                Logger.Info($"Discord cleanup delete for thread '{threadId}' returned 404 because the thread was already gone. Treating it as successfully removed.");
            } catch (DiscordApiException ex) {
                Logger.Error($"Failed to delete stale session thread '{threadId}'. Continuing without cleanup.", ex);
            }
        }

        private async Task<bool> IsOwnedSessionThread(JObject thread, string threadId, IEnumerable<VisibleParentEntry> visibleEntries) {
            // Threads started from a message share that message's id, so a scanned seed row that
            // matches on either id proves the thread grew out of our own seed message.
            if (visibleEntries.Any(entry =>
                    string.Equals(entry.MessageId, threadId, StringComparison.Ordinal)
                    || string.Equals(entry.ThreadId, threadId, StringComparison.Ordinal))) {
                return true;
            }

            var botUserId = await TryGetBotUserId();
            if (string.IsNullOrWhiteSpace(botUserId)) {
                return false;
            }

            return string.Equals(thread["owner_id"]?.Value<string>(), botUserId, StringComparison.Ordinal);
        }

        private async Task<string> TryGetBotUserId() {
            var botToken = GroundStation.GroundStationConfig.DiscordBotToken;
            if (!string.IsNullOrWhiteSpace(cachedBotUserId) && string.Equals(cachedBotUserToken, botToken, StringComparison.Ordinal)) {
                return cachedBotUserId;
            }

            try {
                var botUser = await SendDiscordBotRequest(HttpMethod.Get, "https://discord.com/api/v10/users/@me", notifyOnFailure: false);
                cachedBotUserId = botUser["id"]?.Value<string>();
                cachedBotUserToken = botToken;
                return cachedBotUserId;
            } catch (Exception ex) {
                Logger.Warning($"Discord could not identify its own bot user, so thread cleanup will be skipped. {ex.Message}");
                return null;
            }
        }

        private async Task TryDeleteDuplicateParentMessages(WebhookMetadata webhookMetadata, IEnumerable<string> duplicateMessageIds, string threadName) {
            foreach (var messageId in duplicateMessageIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal)) {
                try {
                    await DeleteChannelMessageWithBot(webhookMetadata.ChannelId, messageId, notifyOnFailure: false);
                    Logger.Info($"Discord deleted older duplicate seed message '{messageId}' for seed text '{threadName}'");
                } catch (DiscordApiException ex) {
                    Logger.Error($"Failed to delete older duplicate seed message '{messageId}' for seed text '{threadName}'", ex);
                }
            }
        }

        private async Task<JObject> EditWebhookMessage(string webhookUrl, string messageId, object payload, bool notifyOnFailure = true) {
            var requestUrl = $"{webhookUrl}/messages/{Uri.EscapeDataString(messageId)}";
            var patchMethod = new HttpMethod("PATCH");
            var (response, responseBody) = await SendDiscordRequestWithRateLimitRetry(() => {
                var request = new HttpRequestMessage(patchMethod, requestUrl);
                request.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
                return request;
            }, "webhook-edit", patchMethod, requestUrl, "Failed to edit Discord webhook message", notifyOnFailure);

            using (response) {
                if (!response.IsSuccessStatusCode) {
                    throw CreateDiscordApiException("Discord Webhook Error", $"Discord webhook message edit returned {(int)response.StatusCode} {response.ReasonPhrase}: {responseBody}", null, notifyOnFailure);
                }

                if (string.IsNullOrWhiteSpace(responseBody)) {
                    return new JObject();
                }

                try {
                    return JObject.Parse(responseBody);
                } catch (Exception ex) {
                    throw CreateDiscordApiException("Discord Webhook Error", $"Discord returned an invalid webhook message edit response: {ex.Message}", ex, notifyOnFailure);
                }
            }
        }

        private async Task DeleteChannelMessageWithBot(string channelId, string messageId, bool notifyOnFailure = true) {
            try {
                await SendDiscordBotRequest(HttpMethod.Delete, $"https://discord.com/api/v10/channels/{channelId}/messages/{messageId}", notifyOnFailure: notifyOnFailure);
            } catch (DiscordApiException ex) when (ex.Message.Contains("returned 404", StringComparison.Ordinal)) {
                Logger.Info($"Discord cleanup delete for parent message '{messageId}' in channel '{channelId}' returned 404 because the message was already gone. Treating it as successfully removed.");
            }
        }

        private async Task<JObject> SendDiscordBotRequest(HttpMethod method, string url, object payload = null, bool notifyOnFailure = true) {
            var (response, responseBody) = await SendDiscordRequestWithRateLimitRetry(() => {
                var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bot", GroundStation.GroundStationConfig.DiscordBotToken);

                if (payload != null) {
                    request.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
                }

                return request;
            }, "bot", method, url, "Failed to send Discord bot request", notifyOnFailure);

            using (response) {
                if (!response.IsSuccessStatusCode) {
                    throw CreateDiscordApiException("Discord Bot Error", $"Discord bot API returned {(int)response.StatusCode} {response.ReasonPhrase}: {responseBody}", null, notifyOnFailure);
                }

                if (string.IsNullOrWhiteSpace(responseBody)) {
                    return new JObject();
                }

                try {
                    return JObject.Parse(responseBody);
                } catch (Exception ex) {
                    throw CreateDiscordApiException("Discord Bot Error", $"Discord returned an invalid bot API response: {ex.Message}", ex, notifyOnFailure);
                }
            }
        }

        private async Task<(HttpResponseMessage Response, string ResponseBody)> SendDiscordRequestWithRateLimitRetry(Func<HttpRequestMessage> requestFactory, string source, HttpMethod method, string url, string failureOperation, bool notifyOnFailure) {
            for (var attempt = 1; ; attempt++) {
                using var request = requestFactory();
                HttpResponseMessage response;

                try {
                    response = await httpClient.SendAsync(request);
                } catch (Exception ex) {
                    throw CreateDiscordApiException(source == "bot" ? "Discord Bot Error" : "Discord Webhook Error", $"{failureOperation}: {ex.Message}", ex, notifyOnFailure);
                }

                var responseBody = await response.Content.ReadAsStringAsync();
                LogDiscordApiResponse(source, method, url, (int)response.StatusCode, responseBody);

                if ((int)response.StatusCode != 429 || attempt > maxDiscordRateLimitRetries) {
                    return (response, responseBody);
                }

                var retryDelay = GetDiscordRetryDelay(response, responseBody);
                Logger.Warning($"Discord rate limit source='{source}' method='{method}' url='{SanitizeDiscordUrl(url)}' attempt='{attempt}' retryDelayMs='{Math.Ceiling(retryDelay.TotalMilliseconds)}'");
                response.Dispose();
                await Task.Delay(retryDelay);
            }
        }

        private static TimeSpan GetDiscordRetryDelay(HttpResponseMessage response, string responseBody) {
            if (response.Headers.RetryAfter?.Delta is TimeSpan retryAfterDelta && retryAfterDelta > TimeSpan.Zero) {
                return retryAfterDelta;
            }

            if (response.Headers.RetryAfter?.Date is DateTimeOffset retryAfterDate) {
                var retryDelay = retryAfterDate - DateTimeOffset.UtcNow;
                if (retryDelay > TimeSpan.Zero) {
                    return retryDelay;
                }
            }

            if (response.Headers.TryGetValues("Retry-After", out var retryAfterValues)) {
                var retryAfterValue = retryAfterValues.FirstOrDefault();
                if (double.TryParse(retryAfterValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var retryAfterSeconds) && retryAfterSeconds > 0) {
                    return TimeSpan.FromSeconds(retryAfterSeconds);
                }
            }

            try {
                var responseJson = JObject.Parse(responseBody);
                var retryAfterToken = responseJson["retry_after"];
                if (retryAfterToken != null && double.TryParse(retryAfterToken.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var retryAfterSeconds) && retryAfterSeconds > 0) {
                    return TimeSpan.FromSeconds(retryAfterSeconds);
                }
            } catch {
            }

            return defaultDiscordRateLimitDelay;
        }

        private static ulong GetSnowflakeSortKey(string snowflake) {
            return ulong.TryParse(snowflake, out var value) ? value : 0;
        }

        private void ValidateWebhookMessage(JObject message, string expectedChannelId, string title, string failureMessage, bool notifyOnFailure = true) {
            var messageId = GetRequiredString(message, "id", title, $"{failureMessage} Discord did not return a message id.", notifyOnFailure);
            var messageChannelId = GetRequiredString(message, "channel_id", title, $"{failureMessage} Discord did not return a channel id for message {messageId}.", notifyOnFailure);

            if (!string.Equals(messageChannelId, expectedChannelId, StringComparison.Ordinal)) {
                throw CreateDiscordApiException(title, $"{failureMessage} Expected channel {expectedChannelId}, but Discord returned channel {messageChannelId} for message {messageId}.", notifyUser: notifyOnFailure);
            }
        }

        private void ValidateParentChannel(JObject channel, string expectedChannelId, bool notifyOnFailure = true) {
            var channelId = GetRequiredString(channel, "id", "Discord Bot Error", "Discord did not return an id for the webhook channel.", notifyOnFailure);
            if (!string.Equals(channelId, expectedChannelId, StringComparison.Ordinal)) {
                throw CreateDiscordApiException("Discord Bot Error", $"Discord returned webhook channel {channelId}, but expected {expectedChannelId}.", notifyUser: notifyOnFailure);
            }

            _ = channel["type"]?.Value<int>()
                ?? throw CreateDiscordApiException("Discord Bot Error", "Discord did not return a channel type for the webhook channel.", notifyUser: notifyOnFailure);
        }

        private void ValidateThreadChannel(JObject channel, string expectedParentChannelId, string title, string failureMessage, bool notifyOnFailure = true) {
            var channelId = GetRequiredString(channel, "id", title, $"{failureMessage} Discord did not return a thread id.", notifyOnFailure);
            var channelType = channel["type"]?.Value<int>()
                ?? throw CreateDiscordApiException(title, $"{failureMessage} Discord did not return a thread channel type for {channelId}.", notifyUser: notifyOnFailure);

            if (!IsThreadChannelType(channelType)) {
                throw CreateDiscordApiException(title, $"{failureMessage} Discord returned channel {channelId} with non-thread type {channelType}.", notifyUser: notifyOnFailure);
            }

            var parentId = GetRequiredString(channel, "parent_id", title, $"{failureMessage} Discord did not return a parent channel for thread {channelId}.", notifyOnFailure);
            if (!string.Equals(parentId, expectedParentChannelId, StringComparison.Ordinal)) {
                throw CreateDiscordApiException(title, $"{failureMessage} Discord returned parent channel {parentId} for thread {channelId}, expected {expectedParentChannelId}.", notifyUser: notifyOnFailure);
            }

            var threadMetadata = channel["thread_metadata"] as JObject
                ?? throw CreateDiscordApiException(title, $"{failureMessage} Discord did not return thread metadata for thread {channelId}.", notifyUser: notifyOnFailure);

            var isArchived = threadMetadata["archived"]?.Value<bool>() ?? true;
            var isLocked = threadMetadata["locked"]?.Value<bool>() ?? false;

            if (isArchived || isLocked) {
                throw CreateDiscordApiException(title, $"{failureMessage} Discord thread {channelId} is archived or locked.", notifyUser: notifyOnFailure);
            }
        }

        private static bool IsThreadChannelType(int channelType) {
            return channelType == announcementThreadChannelType
                || channelType == publicThreadChannelType
                || channelType == privateThreadChannelType;
        }

        private static bool IsMessageBasedThreadChannelType(int channelType) {
            return channelType == announcementThreadChannelType
                || channelType == publicThreadChannelType;
        }

        private static string GetRequiredString(JObject value, string propertyName, string title, string failureMessage, bool notifyOnFailure = true) {
            var propertyValue = value?[propertyName]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(propertyValue)) {
                return propertyValue;
            }

            throw CreateDiscordApiException(title, failureMessage, notifyUser: notifyOnFailure);
        }

        private static DiscordApiException CreateDiscordApiException(string title, string message, Exception innerException = null, bool notifyUser = true) {
            Logger.Error($"{title}: {message}{(innerException == null ? string.Empty : $"{Environment.NewLine}{innerException}")}");

            if (notifyUser) {
                Notification.ShowExternalError(message, title);
            }

            return new DiscordApiException(message, innerException);
        }

        private static void LogDiscordApiResponse(string source, HttpMethod method, string url, int statusCode, string responseBody) {
            Logger.Debug($"Discord API response source='{source}' method='{method}' url='{SanitizeDiscordUrl(url)}' statusCode='{statusCode}' bodySummary='{SummarizeDiscordApiResponseBody(responseBody)}'");
        }

        private static string SanitizeDiscordUrl(string url) {
            if (string.IsNullOrWhiteSpace(url)) {
                return string.Empty;
            }

            try {
                var uri = new Uri(url);
                var segments = uri.AbsolutePath.Trim('/').Split('/');
                var webhookIndex = Array.FindIndex(segments, segment => string.Equals(segment, "webhooks", StringComparison.OrdinalIgnoreCase));

                if (webhookIndex >= 0 && segments.Length > webhookIndex + 2) {
                    segments[webhookIndex + 2] = "REDACTED";
                }

                var sanitizedPath = string.Join("/", segments);
                return $"{uri.Scheme}://{uri.Host}/{sanitizedPath}{uri.Query}";
            } catch {
                return url;
            }
        }

        private static string SummarizeDiscordApiResponseBody(string responseBody) {
            if (string.IsNullOrWhiteSpace(responseBody)) {
                return "empty";
            }

            try {
                var token = JToken.Parse(responseBody);
                return token switch {
                    JObject obj => SummarizeDiscordObjectBody(obj),
                    JArray array => $"json-array count={array.Count}",
                    _ => $"json-{token.Type.ToString().ToLowerInvariant()}"
                };
            } catch {
                return $"non-json length={responseBody.Length}";
            }
        }

        private static string SummarizeDiscordObjectBody(JObject obj) {
            var summaryParts = new List<string>();
            AddSummaryPart(summaryParts, "keys", string.Join(",", obj.Properties().Select(property => property.Name).Take(12)));
            AddSummaryPart(summaryParts, "id", obj["id"]?.Value<string>());
            AddSummaryPart(summaryParts, "channel_id", obj["channel_id"]?.Value<string>());
            AddSummaryPart(summaryParts, "guild_id", obj["guild_id"]?.Value<string>());
            AddSummaryPart(summaryParts, "parent_id", obj["parent_id"]?.Value<string>());
            AddSummaryPart(summaryParts, "type", obj["type"]?.ToString());
            AddSummaryPart(summaryParts, "name", obj["name"]?.Value<string>());

            if (obj["threads"] is JArray threads) {
                AddSummaryPart(summaryParts, "threadsCount", threads.Count.ToString(CultureInfo.InvariantCulture));
            }

            if (obj["thread_metadata"] is JObject threadMetadata) {
                AddSummaryPart(summaryParts, "archived", threadMetadata["archived"]?.ToString());
                AddSummaryPart(summaryParts, "locked", threadMetadata["locked"]?.ToString());
            }

            return string.Join(" ", summaryParts);
        }

        private static void AddSummaryPart(ICollection<string> parts, string key, string value) {
            if (!string.IsNullOrWhiteSpace(value)) {
                parts.Add($"{key}={value}");
            }
        }

        public static List<string> CommonValidation() {
            var errors = new List<string>();

            if (string.IsNullOrEmpty(GroundStation.GroundStationConfig.DiscordWebhookDefaultUrl)) {
                errors.Add("Discord webhook URL is not set");
            }

            if (string.IsNullOrEmpty(GroundStation.GroundStationConfig.DiscordWebhookDefaultBotName)) {
                errors.Add("Discord webhook bot name is not set");
            }

            return errors;
        }

        private enum DiscordWebhookRoute {
            Default,
            Failure,
            Image,
        }

        private sealed record WebhookMetadata(string Url, string WebhookId, string ChannelId, string GuildId);
        private sealed record OldSessionThreadCandidate(string ThreadId, string MessageId, string DisplayName, DateTimeOffset CreatedAt);
        private sealed record BotManagedThreadResolution(string ThreadId, bool CanEditFailureSeed);
        private sealed record ReusableStarterMessage(string MessageId, string ThreadId, bool CanEditFailureSeed, string[] DuplicateMessageIdsToDelete);
        private sealed record VisibleParentEntry(string MessageId, string SeedText, string ThreadId, int Order, bool IsWebhookOwned);
    }
}