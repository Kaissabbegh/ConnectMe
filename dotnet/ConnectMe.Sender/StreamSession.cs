using System.Diagnostics;
using System.Net.Sockets;

namespace ConnectMe;

public sealed record StreamSettings(string Host, int Port, string Code, DisplayInfo Display, int Fps, int BitrateMbps);

/// <summary>One laptop → iMac stream: handshake, then encoder output pumped to the socket.</summary>
public sealed class StreamSession : IDisposable
{
    const int MaxQueuedFrames = 4;

    readonly StreamSettings _s;
    readonly Action<string> _log;
    readonly TcpClient _tcp = new();
    readonly Queue<(byte[] Au, bool Key, ulong Pts)> _queue = new();
    readonly AutoResetEvent _queueSignal = new(false);
    readonly Stopwatch _clock = Stopwatch.StartNew();
    Encoder? _encoder;
    Thread? _sender, _watcher;
    NetworkStream? _net;
    volatile bool _stopped;
    bool _dropping;
    long _bytesThisSecond, _framesThisSecond, _droppedThisSecond;

    public event Action<string>? Stopped;
    public event Action<string>? Stats;
    public WelcomeMsg? Receiver { get; private set; }

    public StreamSession(StreamSettings s, Action<string> log)
    {
        _s = s;
        _log = log;
    }

    public void Start(string ffmpeg)
    {
        _log($"Connecting to {_s.Host}:{_s.Port}...");
        if (!_tcp.ConnectAsync(_s.Host, _s.Port).Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("The iMac did not answer. Check the IP and that ConnectMe Display is open.");
        _tcp.NoDelay = true;
        _tcp.SendBufferSize = 2 * 1024 * 1024;
        _net = _tcp.GetStream();
        _net.ReadTimeout = 5000;

        Proto.WriteJson(_net, MsgType.Hello, new HelloMsg(Proto.Version, Environment.MachineName, _s.Code.Trim()));
        var (type, payload) = Proto.Read(_net);
        if (type == MsgType.Reject)
            throw new InvalidOperationException("iMac refused: " + Proto.ParseJson<RejectMsg>(payload).Reason);
        if (type != MsgType.Welcome)
            throw new InvalidDataException($"Unexpected reply {type}");
        Receiver = Proto.ParseJson<WelcomeMsg>(payload);
        _net.ReadTimeout = Timeout.Infinite;
        _log($"Paired with \"{Receiver.Name}\" ({Receiver.Width}x{Receiver.Height}).");
        if (Receiver.Width != _s.Display.Width || Receiver.Height != _s.Display.Height)
            _log($"Tip: the screen you're sending is {_s.Display.Width}x{_s.Display.Height}; set it to {Receiver.Width}x{Receiver.Height} for the sharpest image.");

        var profile = Encoder.Probe(ffmpeg, _s.Display.OutputIndex, _log);
        _sender = new Thread(SendLoop) { IsBackground = true, Name = "net-sender" };
        _sender.Start();
        _watcher = new Thread(WatchLoop) { IsBackground = true, Name = "net-watcher" };
        _watcher.Start();
        _encoder = new Encoder(ffmpeg, profile, _s.Display.OutputIndex, _s.Fps, _s.BitrateMbps, OnAccessUnit, _log);
        _encoder.Exited += reason => Stop(reason);
        _log("Streaming.");
    }

    void OnAccessUnit(byte[] au, bool key)
    {
        lock (_queue)
        {
            if (_queue.Count >= MaxQueuedFrames)
            {
                // Network is behind: discard the backlog and resume at the next keyframe
                // (at most 1 s away) instead of letting latency grow.
                _droppedThisSecond += _queue.Count;
                _queue.Clear();
                _dropping = true;
            }
            if (_dropping && !key) { _droppedThisSecond++; return; }
            _dropping = false;
            _queue.Enqueue((au, key, (ulong)(_clock.Elapsed.Ticks / 10)));
        }
        _queueSignal.Set();
    }

    void SendLoop()
    {
        var second = Stopwatch.StartNew();
        try
        {
            while (!_stopped)
            {
                _queueSignal.WaitOne(500);
                while (true)
                {
                    (byte[] Au, bool Key, ulong Pts) item;
                    lock (_queue)
                    {
                        if (_queue.Count == 0) break;
                        item = _queue.Dequeue();
                    }
                    Proto.WriteVideo(_net!, item.Pts, item.Key, item.Au);
                    _bytesThisSecond += item.Au.Length;
                    _framesThisSecond++;
                }
                if (second.ElapsedMilliseconds >= 1000)
                {
                    double mbps = _bytesThisSecond * 8 / 1e6 / second.Elapsed.TotalSeconds;
                    var dropped = _droppedThisSecond > 0 ? $", {_droppedThisSecond} dropped" : "";
                    Stats?.Invoke($"{_framesThisSecond} fps, {mbps:F1} Mbps{dropped}");
                    _bytesThisSecond = _framesThisSecond = _droppedThisSecond = 0;
                    second.Restart();
                }
            }
        }
        catch (Exception ex) when (!_stopped)
        {
            Stop("Connection lost: " + ex.Message);
        }
        catch { }
    }

    void WatchLoop()
    {
        try
        {
            while (!_stopped)
            {
                var (type, _) = Proto.Read(_net!);
                if (type == MsgType.Bye) { Stop("The iMac ended the session."); return; }
            }
        }
        catch when (!_stopped) { Stop("The iMac disconnected."); }
        catch { }
    }

    public void Stop(string reason)
    {
        if (_stopped) return;
        _stopped = true;
        try { if (_net != null) Proto.Write(_net, MsgType.Bye, ReadOnlySpan<byte>.Empty); } catch { }
        _encoder?.Dispose();
        _tcp.Close();
        _queueSignal.Set();
        _log(reason);
        Stopped?.Invoke(reason);
    }

    public void Dispose() => Stop("Stopped.");
}
