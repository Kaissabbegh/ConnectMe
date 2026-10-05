using System.Diagnostics;

namespace ConnectMe;

public sealed record EncoderProfile(string Name, string Filter, string Codec, string Options);

/// <summary>
/// Captures one display with FFmpeg's ddagrab (Desktop Duplication) and encodes it to H.264,
/// using the GPU encoder when one works. Emits whole access units (split on AUD NALs).
/// </summary>
public sealed class Encoder : IDisposable
{
    // Captured frames are BGRA on the GPU. Encoding them directly yields full-range ("yuvj420p") H.264,
    // which some receivers render green/garbled (seen with mpv's software output on Linux iMacs), and the
    // GPU-side converters aren't available everywhere. So convert on the CPU to standard limited-range
    // BT.709 4:2:0 for every encoder. Costs some CPU: an i5-8365U holds 1080p at 30 fps, not 60.
    const string ToNv12 = "hwdownload,format=bgra,scale=out_color_matrix=bt709:out_range=tv,format=nv12";
    const string ToYuv420 = "hwdownload,format=bgra,scale=out_color_matrix=bt709:out_range=tv,format=yuv420p";

    // Tried in order; the first that survives a short real capture is used.
    static readonly EncoderProfile[] Profiles =
    {
        new("NVIDIA NVENC", ToNv12, "h264_nvenc", "-preset p1 -tune ull -rc cbr -zerolatency 1"),
        new("AMD AMF", ToNv12, "h264_amf", "-usage ultralowlatency -quality speed -rc cbr"),
        new("Intel Quick Sync", ToNv12, "h264_qsv", "-preset veryfast -look_ahead 0 -async_depth 1"),
        new("CPU (x264)", ToYuv420, "libx264", "-preset ultrafast -tune zerolatency"),
    };

    static readonly Dictionary<int, EncoderProfile> ProbeCache = new();

    public static string? FindFfmpeg()
    {
        var dir = AppContext.BaseDirectory;
        foreach (var p in new[] { Path.Combine(dir, "ffmpeg.exe"), Path.Combine(dir, "ffmpeg", "ffmpeg.exe"), Path.Combine(dir, "ffmpeg", "bin", "ffmpeg.exe") })
            if (File.Exists(p)) return p;
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(d.Trim(), "ffmpeg.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    static string BuildArgs(EncoderProfile p, int outputIdx, int fps, int bitrateMbps, string output)
    {
        var filter = string.IsNullOrEmpty(p.Filter) ? "" : $"-vf {p.Filter} ";
        int kbps = bitrateMbps * 1000;
        return "-hide_banner -loglevel warning -nostdin " +
               $"-f lavfi -i ddagrab=output_idx={outputIdx}:framerate={fps}:draw_mouse=1 " +
               filter +
               $"-c:v {p.Codec} {p.Options} -b:v {kbps}k -maxrate {kbps}k -bufsize {kbps / 2}k " +
               $"-g {fps} -bf 0 -an -color_range tv -colorspace bt709 -color_primaries bt709 -color_trc bt709 " +
               "-bsf:v h264_metadata=aud=insert " +
               output;
    }

    /// <summary>Finds a working encoder for this display. Slow (a few seconds) the first time.</summary>
    public static EncoderProfile Probe(string ffmpeg, int outputIdx, Action<string> log)
    {
        lock (ProbeCache)
            if (ProbeCache.TryGetValue(outputIdx, out var cached)) return cached;

        foreach (var p in Profiles)
        {
            log($"Testing encoder: {p.Name} ({p.Codec})...");
            var psi = new ProcessStartInfo(ffmpeg, BuildArgs(p, outputIdx, 30, 10, "-t 1.5 -f null -"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using var proc = Process.Start(psi)!;
            var err = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(15000)) { try { proc.Kill(true); } catch { } continue; }
            if (proc.ExitCode == 0)
            {
                log($"Using {p.Name}.");
                lock (ProbeCache) ProbeCache[outputIdx] = p;
                return p;
            }
            var lastLine = err.Result.Trim().Split('\n').LastOrDefault()?.Trim();
            log($"  not available: {lastLine}");
        }
        throw new InvalidOperationException("No working H.264 encoder. Is this FFmpeg 6.1 or newer (full build)?");
    }

    readonly Process _proc;
    readonly Thread _reader;
    readonly Action<byte[], bool> _onAccessUnit;
    readonly Action<string> _log;
    volatile bool _stopping;

    public event Action<string>? Exited;

    public Encoder(string ffmpeg, EncoderProfile profile, int outputIdx, int fps, int bitrateMbps,
        Action<byte[], bool> onAccessUnit, Action<string> log)
    {
        _onAccessUnit = onAccessUnit;
        _log = log;
        var psi = new ProcessStartInfo(ffmpeg, BuildArgs(profile, outputIdx, fps, bitrateMbps, "-flush_packets 1 -f h264 pipe:1"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        _proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start FFmpeg");
        _proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _log("ffmpeg: " + e.Data); };
        _proc.BeginErrorReadLine();
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "ffmpeg-reader" };
        _reader.Start();
    }

    void ReadLoop()
    {
        var stream = _proc.StandardOutput.BaseStream;
        var splitter = new AccessUnitSplitter(_onAccessUnit);
        var chunk = new byte[256 * 1024];
        try
        {
            int n;
            while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
                splitter.Push(chunk.AsSpan(0, n));
        }
        catch (Exception ex) when (_stopping) { _ = ex; }
        catch (Exception ex) { _log("Encoder read error: " + ex.Message); }
        if (!_stopping)
        {
            _proc.WaitForExit(2000);
            Exited?.Invoke(_proc.HasExited ? $"FFmpeg stopped (exit code {_proc.ExitCode})" : "FFmpeg output ended");
        }
    }

    public void Dispose()
    {
        _stopping = true;
        try { if (!_proc.HasExited) _proc.Kill(true); } catch { }
        if (Thread.CurrentThread != _reader) _reader.Join(2000);
        _proc.Dispose();
    }
}

/// <summary>Splits an Annex-B H.264 byte stream into access units at each AUD (NAL type 9).</summary>
public sealed class AccessUnitSplitter
{
    readonly Action<byte[], bool> _emit;
    byte[] _buf = new byte[1024 * 1024];
    int _len;
    int _scan;
    int _auStart = -1;

    public AccessUnitSplitter(Action<byte[], bool> emit) => _emit = emit;

    public void Push(ReadOnlySpan<byte> data)
    {
        if (_len + data.Length > _buf.Length)
            Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + data.Length));
        data.CopyTo(_buf.AsSpan(_len));
        _len += data.Length;

        for (; _scan + 3 < _len; _scan++)
        {
            if (_buf[_scan] != 0 || _buf[_scan + 1] != 0 || _buf[_scan + 2] != 1) continue;
            if ((_buf[_scan + 3] & 0x1F) != 9) continue;
            int start = _scan > 0 && _buf[_scan - 1] == 0 ? _scan - 1 : _scan;
            if (_auStart >= 0 && start > _auStart)
                Emit(_auStart, start);
            _auStart = start;
            _scan += 3;
        }

        // Compact: drop everything before the current access unit.
        int keep = _auStart >= 0 ? _auStart : Math.Max(0, _len - 4);
        if (keep > 0)
        {
            Buffer.BlockCopy(_buf, keep, _buf, 0, _len - keep);
            _len -= keep;
            _scan -= keep;
            if (_auStart >= 0) _auStart = 0;
        }
    }

    void Emit(int from, int to)
    {
        var au = _buf.AsSpan(from, to - from).ToArray();
        _emit(au, ContainsIdr(au));
    }

    public static bool ContainsIdr(ReadOnlySpan<byte> au)
    {
        for (int i = 0; i + 3 < au.Length; i++)
            if (au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 1 && (au[i + 3] & 0x1F) == 5)
                return true;
        return false;
    }
}
