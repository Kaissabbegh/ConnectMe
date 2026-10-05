using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace ConnectMe;

/// <summary>
/// Accepts laptops on TCP 47800, checks the pairing code, and pipes their video into mpv.
/// One session at a time; same rules as the macOS receiver (mac/ConnectMeDisplay/Server.swift).
/// </summary>
public sealed class ReceiverServer
{
    readonly MpvPlayer _player;
    readonly Action<string> _log;
    readonly object _lock = new();
    readonly TcpListener _listener = new(IPAddress.Any, Proto.Port);
    TcpClient? _active;
    int _wrongAttempts;

    public string Name { get; }
    public int Width { get; set; } = 2560;
    public int Height { get; set; } = 1440;
    public string Code { get; private set; } = NewCode();
    public string Status { get; private set; } = "Waiting for a laptop...";
    public bool ActiveSession { get { lock (_lock) return _active != null; } }

    /// <summary>Raised when the idle screen should be redrawn (new code, status change).</summary>
    public event Action? IdleChanged;

    public ReceiverServer(string name, MpvPlayer player, Action<string> log)
    {
        Name = name;
        _player = player;
        _log = log;
    }

    static string NewCode() => RandomNumberGenerator.GetInt32(0, 10000).ToString("D4");

    public void Start()
    {
        _listener.Start();
        new Thread(AcceptLoop) { IsBackground = true, Name = "accept" }.Start();
        _log($"Listening on TCP {Proto.Port}.");
    }

    void AcceptLoop()
    {
        while (true)
        {
            TcpClient client;
            try { client = _listener.AcceptTcpClient(); }
            catch { return; }
            new Thread(() => RunSession(client)) { IsBackground = true, Name = "session" }.Start();
        }
    }

    void RunSession(TcpClient client)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        var paired = false;
        string reason = "The laptop disconnected";
        try
        {
            using var _ = client;
            client.NoDelay = true;
            client.ReceiveBufferSize = 4 * 1024 * 1024;
            var net = client.GetStream();
            net.ReadTimeout = 5000;

            var (type, payload) = Proto.Read(net);
            if (type != MsgType.Hello) return;
            var hello = Proto.ParseJson<HelloMsg>(payload);

            lock (_lock)
            {
                if (_active != null)
                {
                    Proto.WriteJson(net, MsgType.Reject, new RejectMsg("This iMac is already in use"));
                    return;
                }
                if (hello.Code.Trim() != Code)
                {
                    Proto.WriteJson(net, MsgType.Reject, new RejectMsg("Wrong pairing code"));
                    // A 4-digit code is guessable, so rotate it after a few misses.
                    if (++_wrongAttempts >= 5) { _wrongAttempts = 0; Code = NewCode(); }
                    SetStatus($"{hello.Name} entered a wrong code.");
                    _log($"Wrong code from {hello.Name} ({remote})");
                    return;
                }
                _wrongAttempts = 0;
                _active = client;
            }
            paired = true;

            Proto.WriteJson(net, MsgType.Welcome, new WelcomeMsg(Proto.Version, Name, Width, Height));
            _log($"Streaming from {hello.Name} ({remote})");
            using var sink = _player.BeginStream();

            long frames = 0;
            while (true)
            {
                (type, payload) = Proto.Read(net);
                if (type == MsgType.Bye) break;
                if (type != MsgType.Video || payload.Length <= 9) continue;
                try { sink.Write(payload, 9, payload.Length - 9); }
                catch (IOException)
                {
                    reason = $"The video player stopped accepting video after {frames} frames (see mpv.log)";
                    break;
                }
                if (++frames == 1 || frames % 300 == 0) _log($"Passed {frames} frames to mpv");
            }
        }
        catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut })
        {
            reason = "The laptop stopped sending";
        }
        catch (Exception ex)
        {
            if (paired) _log($"Session error: {ex.Message}");
        }
        finally
        {
            if (paired)
            {
                lock (_lock)
                {
                    _active = null;
                    Code = NewCode();
                }
                _player.EndStream();
                _log($"Session with {remote} ended: {reason}");
                SetStatus(reason + ".");
            }
        }
    }

    void SetStatus(string status)
    {
        Status = status;
        IdleChanged?.Invoke();
    }

    public void Stop()
    {
        _listener.Stop();
        lock (_lock) _active?.Close();
    }
}
