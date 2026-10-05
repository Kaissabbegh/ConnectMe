// Stand-in for the iMac app, for testing the sender on Windows.
// Usage: ConnectMe.TestReceiver [code] [output.h264]
// Accepts one laptop, prints fps / bitrate, and saves the H.264 stream (play it with: ffplay output.h264).
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ConnectMe;

var code = args.Length > 0 ? args[0] : "1234";
var outPath = args.Length > 1 ? args[1] : "received.h264";

var listener = new TcpListener(IPAddress.Any, Proto.Port);
listener.Start();
Console.WriteLine($"Test receiver on port {Proto.Port}, pairing code {code}. Waiting for the laptop...");

while (true)
{
    using var client = listener.AcceptTcpClient();
    Console.WriteLine($"Connection from {client.Client.RemoteEndPoint}");
    try
    {
        var net = client.GetStream();
        net.ReadTimeout = 5000;
        var (type, payload) = Proto.Read(net);
        if (type != MsgType.Hello) throw new InvalidDataException("Expected HELLO");
        var hello = Proto.ParseJson<HelloMsg>(payload);
        if (hello.Code != code)
        {
            Proto.WriteJson(net, MsgType.Reject, new RejectMsg("Wrong pairing code"));
            Console.WriteLine($"Rejected {hello.Name}: wrong code {hello.Code}");
            continue;
        }
        Proto.WriteJson(net, MsgType.Welcome, new WelcomeMsg(Proto.Version, "Test receiver", 2560, 1440));
        Console.WriteLine($"Paired with {hello.Name}. Saving video to {outPath}");

        using var file = File.Create(outPath);
        var sw = Stopwatch.StartNew();
        long frames = 0, bytes = 0, keys = 0, totalFrames = 0;
        while (true)
        {
            (type, payload) = Proto.Read(net);
            if (type == MsgType.Bye) { Console.WriteLine("Laptop said BYE."); break; }
            if (type != MsgType.Video) continue;
            bool key = (payload[8] & Proto.FlagKeyframe) != 0;
            file.Write(payload, 9, payload.Length - 9);
            frames++; totalFrames++; bytes += payload.Length; if (key) keys++;
            if (sw.ElapsedMilliseconds >= 1000)
            {
                Console.WriteLine($"{frames} fps, {bytes * 8 / 1e6 / sw.Elapsed.TotalSeconds:F1} Mbps, {keys} keyframes, total {totalFrames}");
                frames = bytes = keys = 0;
                sw.Restart();
            }
        }
    }
    catch (Exception ex) { Console.WriteLine("Session ended: " + ex.Message); }
}
