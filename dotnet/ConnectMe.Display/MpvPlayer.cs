using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ConnectMe;

/// <summary>
/// One long-lived, full-screen mpv window driven over its JSON IPC. While idle it shows the pairing
/// text; for a session, mpv opens a loopback TCP URL that we feed with the raw H.264 stream.
/// </summary>
public sealed class MpvPlayer : IDisposable
{
    readonly string _mpvPath;
    readonly Action<string> _log;
    readonly string _ipcName = $"connectme-mpv-{Environment.ProcessId}";
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    Process? _proc;
    Stream? _ipc;
    readonly object _ipcWriteLock = new();
    int _nextRequestId;
    TcpListener? _streamListener;
    Socket? _streamSocket;

    public event Action? Exited;

    readonly string _hwdec;
    readonly string _mpvLogPath;

    /// <param name="hwdec">mpv --hwdec value. Linux defaults to "no": on 2013 iMacs (NVIDIA Kepler + nouveau)
    /// hardware decoding can "succeed" yet show only black frames, and the CPU copes with H.264 fine.</param>
    readonly string? _vo;

    /// <param name="vo">mpv --vo override. 2013 iMacs on Linux (nouveau) can garble mpv's default GPU
    /// renderer, so the receiver lets the installer pick a working one (gpu, wlshm, x11, ...).</param>
    public MpvPlayer(string mpvPath, Action<string> log, string mpvLogPath, string? hwdec = null, string? vo = null)
    {
        _mpvPath = mpvPath;
        _log = log;
        _mpvLogPath = mpvLogPath;
        _hwdec = hwdec ?? (OperatingSystem.IsWindows() ? "auto-safe" : "no");
        _vo = vo ?? DefaultVo();
    }

    /// <summary>
    /// On Linux under Wayland, use mpv's software output (wlshm): on the office's 2013 iMacs
    /// (NVIDIA Kepler + nouveau) the default GPU renderer garbles video. Elsewhere keep mpv's default.
    /// </summary>
    static string? DefaultVo() =>
        OperatingSystem.IsLinux() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
            ? "wlshm"
            : null;

    static bool IsSoftwareVo(string? vo) => vo is "wlshm" or "x11" or "sdl" or "xv";

    public static string? FindMpv()
    {
        var dir = AppContext.BaseDirectory;
        var exe = OperatingSystem.IsWindows() ? "mpv.exe" : "mpv";
        foreach (var p in new[] { Path.Combine(dir, exe), Path.Combine(dir, "mpv", exe) })
            if (File.Exists(p)) return p;
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(d.Trim(), exe);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    string IpcPath => OperatingSystem.IsWindows() ? $@"\\.\pipe\{_ipcName}" : Path.Combine(Path.GetTempPath(), _ipcName + ".sock");

    public void Start()
    {
        var args = new[]
        {
            "--no-config", "--no-terminal", "--idle=yes", "--force-window=yes", "--fullscreen",
            "--title=ConnectMe Display", "--no-osc", "--cursor-autohide=always", "--keep-open=no",
            $"--input-ipc-server={IpcPath}",
            // Low latency: no buffering, display frames as soon as they are decoded.
            "--profile=low-latency", "--untimed", "--cache=no", $"--hwdec={_hwdec}",
            $"--log-file={_mpvLogPath}",
            "--demuxer-lavf-format=h264", "--demuxer-lavf-probesize=32", "--demuxer-lavf-analyzeduration=0",
            "--osd-font-size=46", "--osd-align-x=center", "--osd-align-y=center", "--osd-border-size=2",
        };
        var psi = new ProcessStartInfo(_mpvPath) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // With hardware decoding off, don't let mpv probe GPU interops either (it tries libcuda on Linux).
        if (_hwdec == "no") psi.ArgumentList.Add("--gpu-hwdec-interop=no");
        if (!string.IsNullOrWhiteSpace(_vo)) psi.ArgumentList.Add($"--vo={_vo}");
        if (IsSoftwareVo(_vo))
        {
            // Scaling/colour conversion runs on the CPU with these outputs: favour speed over quality.
            psi.ArgumentList.Add("--sws-scaler=fast-bilinear");
            psi.ArgumentList.Add("--sws-fast=yes");
        }
        _log($"mpv video output: {_vo ?? "default"}, hardware decoding: {_hwdec}");
        _proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start mpv");
        _proc.EnableRaisingEvents = true;
        _proc.Exited += (_, _) => Exited?.Invoke();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_ipc == null)
        {
            try { _ipc = ConnectIpc(); }
            catch when (DateTime.UtcNow < deadline && !_proc.HasExited) { Thread.Sleep(100); }
        }
        new Thread(ReadIpcLoop) { IsBackground = true, Name = "mpv-ipc" }.Start();
    }

    Stream ConnectIpc()
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", _ipcName, PipeDirection.InOut);
            pipe.Connect(200);
            return pipe;
        }
        var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        sock.Connect(new UnixDomainSocketEndPoint(IpcPath));
        return new NetworkStream(sock, ownsSocket: true);
    }

    void ReadIpcLoop()
    {
        try
        {
            // leaveOpen: the stream is closed once, in Dispose, not when mpv's side hangs up.
            using var reader = new StreamReader(_ipc!, Encoding.UTF8, false, 4096, leaveOpen: true);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                JsonNode? msg;
                try { msg = JsonNode.Parse(line); } catch { continue; }
                if (msg?["request_id"] is JsonNode idNode && _pending.TryRemove(idNode.GetValue<int>(), out var tcs))
                    tcs.TrySetResult(msg["error"]?.GetValue<string>() == "success" ? msg["data"] : null);
                else if (msg?["event"]?.GetValue<string>() is "file-loaded" or "end-file" or "video-reconfig")
                    _log($"mpv: {line}");
            }
        }
        catch { /* mpv went away */ }
    }

    Task<JsonNode?> Command(params object[] command)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var json = JsonSerializer.Serialize(new { command, request_id = id }) + "\n";
        try
        {
            lock (_ipcWriteLock)
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                _ipc!.Write(bytes);
                _ipc.Flush();
            }
        }
        catch (Exception ex)
        {
            _pending.TryRemove(id, out _);
            // Once mpv has quit (window closed, `q`), failing commands are expected; don't log them.
            if (_proc is { HasExited: false } && _disposed == 0) _log("mpv IPC error: " + ex.Message);
            tcs.TrySetResult(null);
        }
        return tcs.Task;
    }

    public void ShowText(string text) => _ = Command("show-text", text, 86_400_000);

    /// <summary>mpv can drop out of fullscreen (focus changes, Esc); the receiver re-asserts it.</summary>
    public void EnsureFullscreen() => _ = Command("set_property", "fullscreen", true);

    /// <summary>The pixel size of the screen mpv is on, if mpv can tell.</summary>
    public (int Width, int Height)? DisplaySize()
    {
        var w = Command("get_property", "display-width");
        var h = Command("get_property", "display-height");
        if (!Task.WaitAll(new Task[] { w, h }, 2000)) return null;
        try
        {
            if (w.Result is JsonNode wn && h.Result is JsonNode hn)
                return (wn.GetValue<int>(), hn.GetValue<int>());
        }
        catch { }
        return null;
    }

    /// <summary>Starts playback and returns the stream to write Annex-B H.264 into.</summary>
    public Stream BeginStream()
    {
        EndStream();
        _streamListener = new TcpListener(IPAddress.Loopback, 0);
        _streamListener.Start(1);
        var port = ((IPEndPoint)_streamListener.LocalEndpoint).Port;
        ShowText("");
        EnsureFullscreen();
        _ = Command("loadfile", $"tcp://127.0.0.1:{port}");
        var accept = _streamListener.AcceptSocketAsync();
        if (!accept.Wait(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("mpv did not open the video stream");
        _streamSocket = accept.Result;
        _streamSocket.NoDelay = true;
        // If mpv stops reading (hung renderer), fail the write instead of blocking the session forever,
        // which would leave the iMac stuck as "already in use".
        _streamSocket.SendTimeout = 5000;
        _streamListener.Stop();
        _streamListener = null;
        return new NetworkStream(_streamSocket, ownsSocket: false);
    }

    public void EndStream()
    {
        try { _streamSocket?.Close(); } catch { }
        try { _streamListener?.Stop(); } catch { }
        _streamSocket = null;
        _streamListener = null;
        _ = Command("stop");
    }

    int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        EndStream();
        try { _ = Command("quit"); } catch { }
        try { if (_proc is { HasExited: false } && !_proc.WaitForExit(2000)) _proc.Kill(entireProcessTree: true); } catch { }
        _ipc?.Dispose();
    }

    /// <summary>
    /// Closes mpv windows left behind by an earlier receiver that was killed hard (e.g. kill -9),
    /// recognised by our IPC socket name in their command line. Linux only (reads /proc).
    /// </summary>
    public static void KillOrphans(Action<string> log)
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid == Environment.ProcessId) continue;
            try
            {
                var cmd = File.ReadAllText(Path.Combine(dir, "cmdline")).Replace('\0', ' ');
                if (!cmd.Contains("--input-ipc-server=") || !cmd.Contains("connectme-mpv-")) continue;
                Process.GetProcessById(pid).Kill();
                log($"Closed leftover mpv window (pid {pid}).");
            }
            catch { /* process gone or not ours */ }
        }
    }
}
