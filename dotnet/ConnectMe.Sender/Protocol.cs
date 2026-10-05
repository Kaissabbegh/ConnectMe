using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConnectMe;

public enum MsgType : byte
{
    Hello = 0x01,
    Welcome = 0x02,
    Reject = 0x03,
    Video = 0x10,
    Bye = 0x7F,
}

public sealed record HelloMsg(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("code")] string Code);

public sealed record WelcomeMsg(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height);

public sealed record RejectMsg([property: JsonPropertyName("reason")] string Reason);

/// <summary>Framing shared by sender and receivers; see docs/PROTOCOL.md.</summary>
public static class Proto
{
    public const int Version = 1;
    public const int Port = 47800;
    public const string ServiceType = "_connectme._tcp";
    public const int MaxMessage = 16 * 1024 * 1024;
    public const byte FlagKeyframe = 0x01;

    public static void Write(Stream s, MsgType type, ReadOnlySpan<byte> payload)
    {
        var buf = new byte[5 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)(payload.Length + 1));
        buf[4] = (byte)type;
        payload.CopyTo(buf.AsSpan(5));
        s.Write(buf);
        s.Flush();
    }

    public static void WriteJson<T>(Stream s, MsgType type, T msg) =>
        Write(s, type, JsonSerializer.SerializeToUtf8Bytes(msg));

    public static void WriteVideo(Stream s, ulong ptsUs, bool keyframe, ReadOnlySpan<byte> accessUnit)
    {
        var buf = new byte[5 + 9 + accessUnit.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)(1 + 9 + accessUnit.Length));
        buf[4] = (byte)MsgType.Video;
        BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(5), ptsUs);
        buf[13] = keyframe ? FlagKeyframe : (byte)0;
        accessUnit.CopyTo(buf.AsSpan(14));
        s.Write(buf);
    }

    public static (MsgType Type, byte[] Payload) Read(Stream s)
    {
        Span<byte> header = stackalloc byte[5];
        s.ReadExactly(header);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length < 1 || length > MaxMessage)
            throw new InvalidDataException($"Bad message length {length}");
        var payload = new byte[length - 1];
        s.ReadExactly(payload);
        return ((MsgType)header[4], payload);
    }

    public static T ParseJson<T>(byte[] payload) =>
        JsonSerializer.Deserialize<T>(payload) ?? throw new InvalidDataException("Empty JSON message");

    public static string Utf8(byte[] b) => Encoding.UTF8.GetString(b);
}
