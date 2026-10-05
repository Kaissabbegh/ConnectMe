using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace ConnectMe;

/// <summary>
/// Answers mDNS PTR queries for _connectme._tcp.local so laptops can find this iMac.
/// Shares UDP 5353 with the OS responder (Windows DNS Client / avahi) via SO_REUSEADDR.
/// </summary>
public sealed class MdnsResponder : IDisposable
{
    static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    const string ServiceName = Proto.ServiceType + ".local";

    readonly Func<(string Name, int Width, int Height)> _info;
    readonly Action<string> _log;
    UdpClient? _udp;
    volatile bool _stopped;

    public MdnsResponder(Func<(string Name, int Width, int Height)> info, Action<string> log)
    {
        _info = info;
        _log = log;
    }

    public void Start()
    {
        try
        {
            var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
            int joined = 0;
            foreach (var local in LocalIPv4())
            {
                try { udp.JoinMulticastGroup(Group, local); joined++; } catch { }
            }
            if (joined == 0) udp.JoinMulticastGroup(Group);
            _udp = udp;
            new Thread(Loop) { IsBackground = true, Name = "mdns" }.Start();
            _log($"Discovery on: answering mDNS on {joined} interface(s).");
        }
        catch (Exception ex)
        {
            _log("Discovery off (laptops must type this iMac's IP): " + ex.Message);
        }
    }

    static IEnumerable<IPAddress> LocalIPv4() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork);

    void Loop()
    {
        var from = new IPEndPoint(IPAddress.Any, 0);
        while (!_stopped)
        {
            try
            {
                var msg = _udp!.Receive(ref from);
                if (IsOurQuery(msg, out ushort id))
                    Answer(id, from);
            }
            catch when (!_stopped) { Thread.Sleep(100); }
            catch { return; }
        }
    }

    static bool IsOurQuery(byte[] msg, out ushort id)
    {
        id = 0;
        if (msg.Length < 12 || (msg[2] & 0x80) != 0) return false; // too short, or a response
        id = BinaryPrimitives.ReadUInt16BigEndian(msg);
        int qd = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(4));
        int pos = 12;
        for (int i = 0; i < qd && pos < msg.Length; i++)
        {
            var name = ReadName(msg, ref pos);
            if (pos + 4 > msg.Length) return false;
            int type = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(pos));
            pos += 4;
            if ((type == 12 || type == 255) && name.Equals(ServiceName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    void Answer(ushort queryId, IPEndPoint from)
    {
        var (name, w, h) = _info();
        bool legacy = from.Port != 5353; // RFC 6762 §6.7: reply unicast, echo id and question
        uint ttl = legacy ? 10u : 120u;
        var instanceLabel = Sanitize(name);
        var instance = new[] { instanceLabel }.Concat(ServiceName.Split('.')).ToArray();
        var host = new[] { Sanitize(Environment.MachineName), "local" };
        var service = ServiceName.Split('.');
        var ip = LocalAddressFor(from.Address);

        var ms = new MemoryStream();
        void U16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        void U32(uint v) { U16((int)(v >> 16)); U16((int)(v & 0xFFFF)); }
        void Name(string[] labels)
        {
            foreach (var l in labels)
            {
                var b = Encoding.UTF8.GetBytes(l);
                ms.WriteByte((byte)Math.Min(63, b.Length));
                ms.Write(b, 0, Math.Min(63, b.Length));
            }
            ms.WriteByte(0);
        }
        void Record(string[] owner, int type, bool unique, byte[] rdata)
        {
            Name(owner);
            U16(type);
            U16(unique && !legacy ? 0x8001 : 0x0001);
            U32(ttl);
            U16(rdata.Length);
            ms.Write(rdata);
        }
        byte[] NameBytes(string[] labels)
        {
            var inner = new MemoryStream();
            foreach (var l in labels)
            {
                var b = Encoding.UTF8.GetBytes(l);
                inner.WriteByte((byte)Math.Min(63, b.Length));
                inner.Write(b, 0, Math.Min(63, b.Length));
            }
            inner.WriteByte(0);
            return inner.ToArray();
        }

        U16(legacy ? queryId : 0);
        U16(0x8400); // response, authoritative
        U16(legacy ? 1 : 0); U16(1); U16(0); U16(ip != null ? 3 : 2);
        if (legacy) { Name(service); U16(12); U16(1); }

        Record(service, 12, false, NameBytes(instance));

        var srv = new MemoryStream();
        srv.Write(new byte[] { 0, 0, 0, 0, (byte)(Proto.Port >> 8), (byte)(Proto.Port & 0xFF) });
        srv.Write(NameBytes(host));
        Record(instance, 33, true, srv.ToArray());

        var txt = new MemoryStream();
        foreach (var kv in new[] { $"v={Proto.Version}", $"name={name}", $"w={w}", $"h={h}" })
        {
            var b = Encoding.UTF8.GetBytes(kv);
            txt.WriteByte((byte)Math.Min(255, b.Length));
            txt.Write(b, 0, Math.Min(255, b.Length));
        }
        Record(instance, 16, true, txt.ToArray());
        if (ip != null) Record(host, 1, true, ip.GetAddressBytes());

        var reply = ms.ToArray();
        try { _udp!.Send(reply, reply.Length, legacy ? from : new IPEndPoint(Group, 5353)); }
        catch (Exception ex) { _log("mDNS reply failed: " + ex.Message); }
    }

    static IPAddress? LocalAddressFor(IPAddress remote)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(remote, 9);
            return ((IPEndPoint)s.LocalEndPoint!).Address;
        }
        catch { return null; }
    }

    static string Sanitize(string s)
    {
        var clean = new string(s.Select(c => c == '.' ? '-' : c).ToArray()).Trim();
        return clean.Length == 0 ? "iMac" : clean;
    }

    static string ReadName(byte[] msg, ref int pos)
    {
        var labels = new List<string>();
        int p = pos;
        bool jumped = false;
        for (int guard = 0; guard < 128 && p < msg.Length; guard++)
        {
            int len = msg[p];
            if (len == 0) { p++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (p + 1 >= msg.Length) break;
                if (!jumped) pos = p + 2;
                jumped = true;
                p = ((len & 0x3F) << 8) | msg[p + 1];
                continue;
            }
            if (p + 1 + len > msg.Length) break;
            labels.Add(Encoding.UTF8.GetString(msg, p + 1, len));
            p += 1 + len;
        }
        if (!jumped) pos = p;
        return string.Join('.', labels);
    }

    public void Dispose()
    {
        _stopped = true;
        _udp?.Dispose();
    }
}
