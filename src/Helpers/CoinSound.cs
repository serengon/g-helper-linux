using System.Diagnostics;

namespace GHelper.Linux.Helpers;

/// <summary>
/// Coin sound perfect fourth (B5→E6) on square wave with long decay.
/// Single WAV generated programmatically, cached to disk.
/// Playback via paplay/pw-play/aplay. Kills previous sound before playing new one.
/// </summary>
public static class CoinSound
{
    private const int SampleRate = 22050;

    private const int MinIntervalMs = 50;

    private static string? _wavPath;
    private static string? _player;
    private static bool _ready;
    private static Process? _currentProcess;
    private static long _lastPlayTicks;
    private static readonly object _lock = new();

    /// <summary>Generate the coin WAV and detect audio player. Call once at startup.</summary>
    public static void EnsureReady()
    {
        if (_ready)
            return;
        _ready = true;

        _player = DetectPlayer();
        if (_player == null)
        {
            Logger.WriteLine("CoinSound: no audio player found (tried paplay, pw-play, aplay)");
            return;
        }
        Logger.WriteLine($"CoinSound: using {_player}");

        var cacheDir = AbsoluteUserPaths.CachePath("ghelper");
        Directory.CreateDirectory(cacheDir);

        _wavPath = Path.Combine(cacheDir, "coin.wav");
        if (!File.Exists(_wavPath))
        {
            var wav = GenerateMarioCoin();
            File.WriteAllBytes(_wavPath, wav);
            Logger.WriteLine($"CoinSound: generated {_wavPath} ({wav.Length} bytes)");
        }
    }

    /// <summary>Play the coin sound. Kills any currently playing sound first.</summary>
    public static void Play()
    {
        if (_player == null || _wavPath == null || !File.Exists(_wavPath))
            return;

        lock (_lock)
        {
            // Throttle on the caller: cheap, and avoids queueing tasks.
            long now = Environment.TickCount64;
            if (now - _lastPlayTicks < MinIntervalMs)
                return;
            _lastPlayTicks = now;
        }

        // Process.Start / Kill can block on audio-server negotiation. The
        // arcade calls Play from its 16ms UI-thread game loop, so run the
        // process work on a thread-pool thread to keep the loop smooth.
        Task.Run(() =>
        {
            lock (_lock)
            {
                // Reap the previous player: kill if running, then Dispose so
                // its handle is released now, not at GC time.
                if (_currentProcess != null)
                {
                    try
                    {
                        if (!_currentProcess.HasExited)
                            _currentProcess.Kill();
                    }
                    catch { }
                    try
                    { _currentProcess.Dispose(); }
                    catch { }
                    _currentProcess = null;
                }

                try
                {
                    // No stdout/stderr redirection: unread redirect pipes fill
                    // and leak fds. The player inherits our near-silent streams.
                    _currentProcess = Process.Start(new ProcessStartInfo
                    {
                        FileName = _player,
                        Arguments = _wavPath,
                        UseShellExecute = false,
                    });
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"CoinSound.Play failed: {ex.Message}");
                }
            }
        });
    }

    private static string? DetectPlayer()
    {
        foreach (var cmd in new[] { "paplay", "pw-play", "aplay" })
        {
            var result = Platform.Linux.SysfsHelper.RunCommand("which", cmd);
            if (result != null && result.Trim().Length > 0)
                return cmd;
        }
        return null;
    }

    /// <summary>
    /// Generate the coin sound.
    /// Perfect fourth: B5 (988 Hz) short attack → E6 (1319 Hz) long decay.
    /// Pure square wave for 8-bit character.
    /// 16-bit signed PCM, 22050 Hz, mono.
    /// </summary>
    private static byte[] GenerateMarioCoin()
    {
        const int tone1Hz = 988;   // B5
        const int tone2Hz = 1319;  // E6 (perfect fourth above B5)

        // Tone 1: short attack, ~60ms
        int tone1Samples = SampleRate * 60 / 1000;
        // Tone 2: long ring with decay, ~350ms
        int tone2Samples = SampleRate * 350 / 1000;
        int totalSamples = tone1Samples + tone2Samples;
        int bytesPerSample = 2;
        int dataSize = totalSamples * bytesPerSample;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // RIFF header
        bw.Write("RIFF"u8);
        bw.Write(36 + dataSize);
        bw.Write("WAVE"u8);

        // fmt chunk
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1);              // PCM
        bw.Write((short)1);              // mono
        bw.Write(SampleRate);
        bw.Write(SampleRate * bytesPerSample);
        bw.Write((short)bytesPerSample);
        bw.Write((short)16);

        // data chunk
        bw.Write("data"u8);
        bw.Write(dataSize);

        const double amplitude = 10000; // clean headroom

        // Tone 1: B5, quick attack, short sustain, abrupt end
        for (int i = 0; i < tone1Samples; i++)
        {
            double t = (double)i / SampleRate;
            double pos = (double)i / tone1Samples;

            // Fast attack (2ms), full sustain
            double env = pos < 0.03 ? pos / 0.03 : 1.0;

            double sq = Math.Sign(Math.Sin(2 * Math.PI * tone1Hz * t));
            short s = (short)(sq * amplitude * env);
            bw.Write(s);
        }

        // Tone 2: E6, instant attack, long exponential decay (Mario ring-out)
        for (int i = 0; i < tone2Samples; i++)
        {
            double t = (double)i / SampleRate;
            double pos = (double)i / tone2Samples;

            // Exponential decay: e^(-3*pos) gives ~95% decay by end
            double env = Math.Exp(-3.5 * pos);

            double sq = Math.Sign(Math.Sin(2 * Math.PI * tone2Hz * t));
            short s = (short)(sq * amplitude * env);
            bw.Write(s);
        }

        return ms.ToArray();
    }
}
