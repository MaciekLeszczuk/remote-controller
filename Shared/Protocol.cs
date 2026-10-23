using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

// Snapshot of an XInput gamepad. Button bits use the XInput/XUSB layout (0x0400 = Guide).
readonly record struct GamepadState(
    ushort Buttons, byte LeftTrigger, byte RightTrigger,
    short LeftX, short LeftY, short RightX, short RightY);

// Wire format shared by Client and Server. All integers are little-endian.
// Every packet except discovery ends with the first 16 bytes of HMAC-SHA256(secret, payload).
//
//   Input     [1][session u32][seq u32][time u32][buttons u16][lt u8][rt u8][lx i16][ly i16][rx i16][ry i16]
//   Feedback  [2][session u32][large u8][small u8][echoTime u32][holdMs u16]
//   Bye       [3][session u32][seq u32]
//   Discovery request   [0xAA]
//   Discovery response  [0xBB][port i32][version u8][hostname utf8...]
//
// The client streams Input on every change and at least every KeepAliveMs. The server answers with
// Feedback (current rumble + echo of the newest client timestamp, for round-trip measurement)
// whenever the game changes rumble and at least every FeedbackIntervalMs.
static class Protocol
{
    public const byte Version = 2;
    public const int DiscoveryPort = 7776;
    public const int DefaultPort = 7777;
    public const byte DiscoveryRequest = 0xAA;
    public const byte DiscoveryResponse = 0xBB;

    public const byte MsgInput = 1;
    public const byte MsgFeedback = 2;
    public const byte MsgBye = 3;

    public const int MacSize = 16;
    public const int InputSize = 25;
    public const int FeedbackSize = 13;
    public const int ByeSize = 9;
    public const int MaxPacketSize = 64;

    // Resending state regularly means a lost datagram can never leave a button stuck.
    public const int KeepAliveMs = 50;
    public const int FeedbackIntervalMs = 200;
    // Server releases every input when a client goes quiet this long...
    public const int NeutralTimeoutMs = 500;
    // ...and unplugs its virtual controller after this long.
    public const int DisconnectTimeoutMs = 10_000;

    public static int WriteInput(byte[] b, uint session, uint seq, uint time, GamepadState s)
    {
        b[0] = MsgInput;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), session);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(5), seq);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(9), time);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(13), s.Buttons);
        b[15] = s.LeftTrigger;
        b[16] = s.RightTrigger;
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(17), s.LeftX);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(19), s.LeftY);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(21), s.RightX);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(23), s.RightY);
        return InputSize;
    }

    public static void ReadInput(byte[] b, out uint session, out uint seq, out uint time, out GamepadState s)
    {
        session = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(1));
        seq = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(5));
        time = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(9));
        s = new GamepadState(
            BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(13)), b[15], b[16],
            BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(17)),
            BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(19)),
            BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(21)),
            BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(23)));
    }

    public static int WriteFeedback(byte[] b, uint session, byte large, byte small, uint echoTime, ushort holdMs)
    {
        b[0] = MsgFeedback;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), session);
        b[5] = large;
        b[6] = small;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(7), echoTime);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(11), holdMs);
        return FeedbackSize;
    }

    public static void ReadFeedback(byte[] b, out uint session, out byte large, out byte small, out uint echoTime, out ushort holdMs)
    {
        session = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(1));
        large = b[5];
        small = b[6];
        echoTime = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(7));
        holdMs = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(11));
    }

    public static int WriteBye(byte[] b, uint session, uint seq)
    {
        b[0] = MsgBye;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), session);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(5), seq);
        return ByeSize;
    }

    public static uint ReadSession(byte[] b) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(1));

    public static byte[] WriteDiscoveryResponse(int port, string hostName)
    {
        var name = Encoding.UTF8.GetBytes(hostName);
        var b = new byte[6 + name.Length];
        b[0] = DiscoveryResponse;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(1), port);
        b[5] = Version;
        name.CopyTo(b, 6);
        return b;
    }

    // Version 1 servers answer with just [0xBB][port], so version and name are optional.
    public static bool TryReadDiscoveryResponse(byte[] b, int length, out int port, out byte version, out string hostName)
    {
        port = 0;
        version = 1;
        hostName = "";
        if (length < 5 || b[0] != DiscoveryResponse) return false;
        port = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(1));
        if (length >= 6) version = b[5];
        if (length > 6) hostName = Encoding.UTF8.GetString(b, 6, length - 6);
        return port is > 0 and <= 65535;
    }

    // Appends the MAC after the payload; returns the full packet length.
    public static int Seal(byte[] key, byte[] buffer, int payloadLength)
    {
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(key, buffer.AsSpan(0, payloadLength), mac);
        mac[..MacSize].CopyTo(buffer.AsSpan(payloadLength));
        return payloadLength + MacSize;
    }

    // Returns the payload length, or -1 if the packet is too short or the MAC does not match.
    public static int Open(byte[] key, byte[] buffer, int length)
    {
        int payloadLength = length - MacSize;
        if (payloadLength < 1) return -1;
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(key, buffer.AsSpan(0, payloadLength), mac);
        return CryptographicOperations.FixedTimeEquals(mac[..MacSize], buffer.AsSpan(payloadLength, MacSize)) ? payloadLength : -1;
    }

    // Sequence numbers wrap around, so compare by signed distance.
    public static bool IsNewer(uint seq, uint last) => (int)(seq - last) > 0;

    // Windows reports an ICMP "port unreachable" caused by an earlier send as an exception on the
    // next receive (WSAECONNRESET), which would otherwise break the listen loop when the peer exits.
    public static void DisableConnectionReset(Socket socket)
    {
        const int SIO_UDP_CONNRESET = -1744830452;
        try { socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null); } catch (SocketException) { }
    }
}
