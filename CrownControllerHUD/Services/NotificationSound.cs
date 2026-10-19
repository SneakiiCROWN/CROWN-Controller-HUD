using System;
using System.Collections.Generic;
using System.IO;
using System.Media;

namespace CrownControllerHUD.Services
{
    /// <summary>
    /// Short, gentle two-note chimes synthesised in memory.
    ///
    /// Deliberately NOT a Windows system sound (requirement: the controller
    /// notifications are the CROWN card, not a stock OS ding). Playback is
    /// asynchronous and completely non-blocking, and every failure is swallowed -
    /// audio must never be able to break a notification or the UI thread.
    /// </summary>
    internal static class NotificationSound
    {
        private const int SampleRate = 22050;
        private static readonly Dictionary<CrownNotificationKind, byte[]> Cache = new();
        private static readonly object Gate = new();

        public static void Play(CrownNotificationKind kind)
        {
            try
            {
                byte[] wav = GetWav(kind);

                // SoundPlayer.Play() plays on its own thread: an exception there would be
                // an unhandled background-thread exception and would terminate the app.
                // Load() validates the stream here, inside the try/catch, so Play() can
                // no longer fail on malformed audio data.
                var player = new SoundPlayer(new MemoryStream(wav));
                player.Load();
                player.Play();
            }
            catch
            {
                // No audio device / stream problem / unsupported platform: ignore.
            }
        }

        private static byte[] GetWav(CrownNotificationKind kind)
        {
            lock (Gate)
            {
                if (Cache.TryGetValue(kind, out var cached))
                    return cached;

                var wav = Build(kind);
                Cache[kind] = wav;
                return wav;
            }
        }

        private static byte[] Build(CrownNotificationKind kind)
        {
            // (frequency Hz, duration ms). Low amplitude, quick exponential decay.
            (double freq, double ms)[] notes = kind switch
            {
                CrownNotificationKind.Connected => new[] { (587.33, 105.0), (880.00, 165.0) },
                CrownNotificationKind.Disconnected => new[] { (440.00, 105.0), (329.63, 175.0) },
                CrownNotificationKind.BatteryLow => new[] { (698.46, 95.0), (587.33, 150.0) },
                CrownNotificationKind.BatteryCritical => new[] { (523.25, 110.0), (392.00, 190.0) },
                CrownNotificationKind.Charging => new[] { (659.25, 95.0), (987.77, 150.0) },
                _ => new[] { (659.25, 100.0), (880.00, 160.0) }
            };

            int total = 0;
            foreach (var note in notes)
                total += (int)(note.ms * SampleRate / 1000.0);

            var samples = new short[total];
            int index = 0;

            foreach (var note in notes)
            {
                int count = (int)(note.ms * SampleRate / 1000.0);
                int attackSamples = Math.Max(1, (int)(0.004 * SampleRate));

                for (int i = 0; i < count && index < samples.Length; i++)
                {
                    double t = i / (double)SampleRate;
                    double attack = Math.Min(1.0, i / (double)attackSamples);
                    double envelope = attack * Math.Exp(-4.4 * t);
                    double value = Math.Sin(2 * Math.PI * note.freq * t) * envelope * 0.28;
                    samples[index++] = (short)(value * short.MaxValue);
                }
            }

            return WriteWav(samples);
        }

        private static byte[] WriteWav(short[] samples)
        {
            const short channels = 1;
            const short bitsPerSample = 16;
            int dataBytes = samples.Length * sizeof(short);

            using var memory = new MemoryStream(44 + dataBytes);
            using var writer = new BinaryWriter(memory);

            writer.Write(new[] { 'R', 'I', 'F', 'F' });
            writer.Write(36 + dataBytes);
            writer.Write(new[] { 'W', 'A', 'V', 'E' });
            writer.Write(new[] { 'f', 'm', 't', ' ' });
            writer.Write(16);                                   // PCM chunk size
            writer.Write((short)1);                             // PCM
            writer.Write(channels);
            writer.Write(SampleRate);
            writer.Write(SampleRate * channels * bitsPerSample / 8); // byte rate
            writer.Write((short)(channels * bitsPerSample / 8));     // block align
            writer.Write(bitsPerSample);
            writer.Write(new[] { 'd', 'a', 't', 'a' });
            writer.Write(dataBytes);

            foreach (var sample in samples)
                writer.Write(sample);

            writer.Flush();
            return memory.ToArray();
        }
    }
}
