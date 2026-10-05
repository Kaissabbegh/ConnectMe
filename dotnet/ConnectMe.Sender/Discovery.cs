using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace ConnectMe;

public sealed record FoundReceiver(string Name, IPAddress Address, int Port, int Width, int Height)
{
    public override string ToString() =>
        Width > 0 ? $"{Name}  ({Address}, {Width}x{Height})" : $"{Name}  ({Address})";
}

/// <summary>
/// Minimal mDNS browser for _connectme._tcp. Sends legacy-unicast PTR queries (from an ephemeral
/// port) on every IPv4 interface, so the responders reply straight to us and we never need to bind 5353.
/// </summary>
public static class Discovery
{
    static readonly IPEndPoint MdnsGroup = new(IPAddress.Parse("224.0.0.251"), 5353);
    const string QueryName = Proto.ServiceType + ".local";

    public static async Task<List<FoundReceiver>> BrowseAsync(TimeSpan duration, CancellationToken ct = default)
    {
        var found = new Dictionary<string, FoundReceiver>();
        var sockets = new List<UdpClient>();
        var query = BuildQuery();
        foreach (var local in LocalIPv4Addresses())
        {
            try
            {
                var udp = new UdpClient(new IPEndPoint(local, 0));
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                await udp.SendAsync(query, query.Length, MdnsGroup);
                sockets.Add(udp);
            }
            catch { /* interface without multicast */ }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(duration);
        var tasks = sockets.Select(async udp =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var res = await udp.ReceiveAsync(cts.Token);
                    var r = Parse(res.Buffer, res.RemoteEndPoint.Address);
                    if (r != null) lock (found) found[r.Name + "|" + r.Address] = r;
                }
            }
            catch { /* timeout or socket closed */ }
        }).ToList();
        await Task.WhenAll(tasks);
        foreach (var s in sockets) s.Dispose();
        return found.Values.OrderBy(r => r.Name).ToList();
    }

    static IEnumerable<IPAddress> LocalIPv4Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.SupportsMulticast &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork);

    static byte[] BuildQuery()
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0x12, 0x34, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }); // id, flags, QD=1
        foreach (var label in QueryName.Split('.'))
        {
            var b = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)b.Length);
            ms.Write(b);
        }
        ms.WriteByte(0);
        ms.Write(new byte[] { 0, 12, 0, 1 }); // PTR, IN
        return ms.ToArray();
    }

    static FoundReceiver? Parse(byte[] msg, IPAddress source)
    {
        try
        {
            if (msg.Length < 12) return null;
            int qd = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(4));
            int rrCount = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(6))
                          + BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(8))
                          + BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(10));
            int pos = 12;
            for (int i = 0; i < qd; i++) { ReadName(msg, ref pos); pos += 4; }

            string? instance = null;
            int port = Proto.Port, w = 0, h = 0;
            string? txtName = null;
            IPAddress? aRecord = null;
            for (int i = 0; i < rrCount; i++)
            {
                var name = ReadName(msg, ref pos);
                int type = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(pos));
                int rdLen = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(pos + 8));
                int rd = pos + 10;
                switch (type)
                {
                    case 12 when name.Equals(QueryName, StringComparison.OrdinalIgnoreCase):
                        int p = rd;
                        instance = ReadName(msg, ref p);
                        break;
                    case 33: // SRV: priority, weight, port, target
                        port = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(rd + 4));
                        break;
                    case 16: // TXT
                        for (int t = rd; t < rd + rdLen;)
                        {
                            int l = msg[t];
                            var kv = Encoding.UTF8.GetString(msg, t + 1, l);
                            t += 1 + l;
                            var eq = kv.IndexOf('=');
                            if (eq <= 0) continue;
                            var (k, v) = (kv[..eq], kv[(eq + 1)..]);
                            if (k == "name") txtName = v;
                            else if (k == "w") int.TryParse(v, out w);
                            else if (k == "h") int.TryParse(v, out h);
                        }
                        break;
                    case 1 when rdLen == 4:
                        aRecord = new IPAddress(msg.AsSpan(rd, 4));
                        break;
                }
                pos = rd + rdLen;
            }
            if (instance == null) return null;
            var display = txtName ?? instance.Split('.')[0];
            // The reply's source is an address we can actually reach; the A record may name another interface.
            return new FoundReceiver(display, source.Equals(IPAddress.Any) && aRecord != null ? aRecord : source, port, w, h);
        }
        catch { return null; }
    }

    static string ReadName(byte[] msg, ref int pos)
    {
        var labels = new List<string>();
        int p = pos;
        bool jumped = false;
        for (int guard = 0; guard < 128; guard++)
        {
            int len = msg[p];
            if (len == 0) { p++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                int ptr = ((len & 0x3F) << 8) | msg[p + 1];
                if (!jumped) pos = p + 2;
                jumped = true;
                p = ptr;
                continue;
            }
            labels.Add(Encoding.UTF8.GetString(msg, p + 1, len));
            p += 1 + len;
        }
        if (!jumped) pos = p;
        return string.Join('.', labels);
    }
}
