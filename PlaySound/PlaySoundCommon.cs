#region "copyright"

/*
    Copyright (c) 2024 Dale Ghent <daleg@elemental.org>

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/
*/

#endregion "copyright"

using NAudio.Wave;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DaleGhent.NINA.GroundStation.PlaySound {

    public class PlaySoundCommon {
        public static string FileTypeFilter { get; } = "Audio files|*.wav;*.aiff;*.aif;*.mp3;*.mp4;*.wma;*.ogg;*.flac|All files|*.*";

        public string SoundFile { get; set; } = string.Empty;

        public bool WaitUntilFinished { get; set; } = true;

        [SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "Windows-only NINA plugin for now")]
        public async Task<bool> PlaySound(CancellationToken ct) {
            if (string.IsNullOrEmpty(SoundFile)) {
                throw new ArgumentException("Audio file not specified");
            }

            if (!File.Exists(SoundFile)) {
                throw new FileNotFoundException($"{SoundFile} not found");
            }

            using var audioFile = new AudioFileReader(SoundFile);
            using var player = new WaveOutEvent();
            player.Init(audioFile);
            player.Play();

            if (WaitUntilFinished) {
                try {
                    do {
                        await Task.Delay(250, ct);
                    } while (player.PlaybackState == PlaybackState.Playing);
                } catch (OperationCanceledException) {
                    player.Stop();
                    return false;
                }
            }

            return true;
        }
    }
}