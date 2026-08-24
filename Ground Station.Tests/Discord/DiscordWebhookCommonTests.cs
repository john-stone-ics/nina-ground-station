using DaleGhent.NINA.GroundStation.DiscordWebhook;
using System;
using System.Threading.Tasks;
using Xunit;

namespace DaleGhent.NINA.GroundStation.Tests.Discord {

    [Collection("Discord")]
    public sealed class DiscordWebhookCommonTests : IDisposable {
        private readonly DiscordTestContext context = new();

        public void Dispose() => context.Dispose();

        [Fact]
        public void CommonValidation_RequiresUrlAndBotName() {
            context.Settings.WebhookDefaultUrl = string.Empty;
            context.Settings.WebhookDefaultBotName = string.Empty;
            var errors = DiscordWebhookCommon.CommonValidation();
            Assert.Contains(errors, error => error.Contains("webhook URL", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(errors, error => error.Contains("bot name", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void GetThreadKey_ResolvesSessionTemplate() {
            context.Settings.ThreadNameTemplate = "$$FORMAT_SESSIONDATETIME yyyy-MM-dd$$";
            var key = context.Common.GetThreadKey(new DateTime(2026, 8, 23, 20, 0, 0));
            Assert.Equal("2026-08-23", key);
        }

        [Fact]
        public async Task SendDiscordWebhook_BypassPostsToParentChannel() {
            await context.Common.SendDiscordWebhook("hello", bypassSessionThread: true);

            Assert.Contains(context.Api.Requests, request =>
                request.Method == System.Net.Http.HttpMethod.Post
                && request.Url.StartsWith(FakeDiscordApi.WebhookUrl, StringComparison.Ordinal)
                && !request.Url.Contains("thread_id=", StringComparison.Ordinal));
            Assert.DoesNotContain(context.Api.Requests, request => request.Url.Contains("/threads", StringComparison.Ordinal));
        }

        [Fact]
        public async Task SendDiscordWebhook_WithoutSessionThreadsPostsToParent() {
            context.Settings.UseSessionThreads = false;
            await context.Common.SendDiscordWebhook("hello");
            Assert.DoesNotContain(context.Api.Requests, request => request.Url.Contains("/threads", StringComparison.Ordinal));
        }

        [Fact]
        public async Task SendDiscordWebhook_TextChannelWithBotCreatesSessionThread() {
            await context.Common.SendDiscordWebhook("hello");

            Assert.Contains(context.Api.Channels.Values, channel => channel.Type == 11 && channel.Name == "test-session");
            Assert.Contains(context.Api.Requests, request => request.Url.Contains("thread_id=", StringComparison.Ordinal));
        }

        [Fact]
        public async Task SendDiscordWebhook_FailureMarksStarterWhenUsingDefaultWebhook() {
            await context.Common.SendDiscordWebhook("failed", isFailure: true);

            Assert.Contains(context.Api.Messages, message => DiscordSeed.IsFailedSeedText(message.Content));
        }

        [Fact]
        public async Task SendDiscordWebhook_FailureDoesNotMarkStarterWhenFailureWebhookIsSet() {
            context.Settings.FailureWebhookUrl = FakeDiscordApi.WebhookUrl;
            await context.Common.SendDiscordWebhook("failed", isFailure: true);
            Assert.DoesNotContain(context.Api.Messages, message => DiscordSeed.IsFailedSeedText(message.Content));
        }

        [Fact]
        public async Task SendDiscordWebhook_WithoutBotTokenFallsBackOnTextChannel() {
            context.Settings.BotToken = string.Empty;
            context.Api.RejectWebhookThreadName = true;
            await context.Common.SendDiscordWebhook("hello");
            Assert.Contains(context.Api.Requests, request => request.Method == System.Net.Http.HttpMethod.Post && request.Url.StartsWith(FakeDiscordApi.WebhookUrl, StringComparison.Ordinal));
        }

        [Fact]
        public async Task SendDiscordWebhook_ForumUsesNativeThreadName() {
            context.Api.ParentChannelType = 15;
            context.Settings.BotToken = "bot-token";
            await context.Common.SendDiscordWebhook("hello");
            Assert.Contains(context.Api.Channels.Values, channel => channel.Type == 11 && channel.Name == "test-session");
        }
    }
}
