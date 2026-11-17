using System;
using System.Collections.Generic;
using System.Text;

namespace UbootLib.Net;

/// <summary>One mod-to-mod network message.</summary>
public sealed class ModPacket
{
    public string ModId = "";
    public byte Type;
    public byte[] Payload = Array.Empty<byte>();

    public override string ToString() => $"{ModId}/{Type} ({Payload.Length} B)";
}

/// <summary>
/// Wire format for mod traffic. Kept deliberately small and self-describing so it
/// can ride on any transport (Steam P2P channel, loopback test, file replay):
///
///   0..1   magic  'W' 'M'
///   2      version (1)
///   3      message type
///   4      modId length (bytes, UTF-8)
///   5..    modId
///   ..     payload length (uint32 LE)
///   ..     payload
///   ..     FNV-1a checksum (uint32 LE) over everything before it
/// </summary>
public static class ModCodec
{
    public const byte Magic0 = (byte)'W';
    public const byte Magic1 = (byte)'M';
    public const byte Version = 1;
    public const int MaxFrame = 64 * 1024;

    public static byte[] Encode(string modId, byte type, byte[] payload)
    {
        modId ??= "";
        payload ??= Array.Empty<byte>();

        var idBytes = Encoding.UTF8.GetBytes(modId);
        if (idBytes.Length > 255) throw new ArgumentException("modId too long (max 255 bytes)", nameof(modId));
        if (payload.Length > MaxFrame) throw new ArgumentException($"payload too large (max {MaxFrame})", nameof(payload));

        var buf = new byte[5 + idBytes.Length + 4 + payload.Length + 4];
        var i = 0;
        buf[i++] = Magic0;
        buf[i++] = Magic1;
        buf[i++] = Version;
        buf[i++] = type;
        buf[i++] = (byte)idBytes.Length;
        Buffer.BlockCopy(idBytes, 0, buf, i, idBytes.Length); i += idBytes.Length;
        WriteU32(buf, ref i, (uint)payload.Length);
        Buffer.BlockCopy(payload, 0, buf, i, payload.Length); i += payload.Length;
        WriteU32(buf, ref i, Fnv1a(buf, 0, i));
        return buf;
    }

    public static bool TryDecode(byte[] frame, out ModPacket packet, out string error)
    {
        packet = null;
        error = null;

        if (frame == null || frame.Length < 10) { error = "frame too short"; return false; }
        if (frame[0] != Magic0 || frame[1] != Magic1) { error = "bad magic"; return false; }
        if (frame[2] != Version) { error = $"unsupported version {frame[2]}"; return false; }

        var type = frame[3];
        var idLen = frame[4];
        var i = 5;
        if (frame.Length < i + idLen + 8) { error = "truncated header"; return false; }

        var modId = Encoding.UTF8.GetString(frame, i, idLen);
        i += idLen;

        var payloadLen = (int)ReadU32(frame, ref i);
        if (payloadLen < 0 || payloadLen > MaxFrame) { error = "bad payload length"; return false; }
        if (frame.Length < i + payloadLen + 4) { error = "truncated payload"; return false; }

        var payload = new byte[payloadLen];
        Buffer.BlockCopy(frame, i, payload, 0, payloadLen);
        i += payloadLen;

        var checksumPos = i;
        var expected = ReadU32(frame, ref i);
        var actual = Fnv1a(frame, 0, checksumPos);
        if (expected != actual) { error = "checksum mismatch"; return false; }
        if (i != frame.Length) { error = "trailing bytes"; return false; }

        packet = new ModPacket { ModId = modId, Type = type, Payload = payload };
        return true;
    }

    private static void WriteU32(byte[] b, ref int i, uint v)
    {
        b[i++] = (byte)v; b[i++] = (byte)(v >> 8); b[i++] = (byte)(v >> 16); b[i++] = (byte)(v >> 24);
    }

    private static uint ReadU32(byte[] b, ref int i)
    {
        var v = (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24));
        i += 4;
        return v;
    }

    private static uint Fnv1a(byte[] data, int offset, int length)
    {
        var hash = 2166136261u;
        for (var k = offset; k < offset + length; k++)
        {
            hash ^= data[k];
            hash *= 16777619u;
        }
        return hash;
    }
}

/// <summary>Transport abstraction so the same code runs over loopback and Steam P2P.</summary>
public interface IModTransport
{
    string Name { get; }
    bool Send(ModPacket packet);
    void Poll(Action<ModPacket> onPacket);
}

/// <summary>In-process transport used for self-tests and single-player mods.</summary>
public sealed class LoopbackTransport : IModTransport
{
    private readonly Queue<ModPacket> _queue = new();

    public string Name => "loopback";
    public int Dropped { get; private set; }

    public bool Send(ModPacket packet)
    {
        if (packet == null) return false;
        _queue.Enqueue(packet);
        return true;
    }

    public bool SendRaw(byte[] frame)
    {
        if (!ModCodec.TryDecode(frame, out var pkt, out _)) { Dropped++; return false; }
        _queue.Enqueue(pkt);
        return true;
    }

    public void Poll(Action<ModPacket> onPacket)
    {
        while (_queue.Count > 0) onPacket(_queue.Dequeue());
    }
}

/// <summary>Startup self-test proving the codec handles real-world edge cases.</summary>
public static class NetSelfTest
{
    public static void Run(Action<string> log)
    {
        var cases = 0;
        var failures = 0;

        void Check(string name, bool ok)
        {
            cases++;
            if (!ok) { failures++; log($"[net] FAIL {name}"); }
        }

        var lc = new LoopbackTransport();
        void Roundtrip(string modId, byte type, byte[] payload)
        {
            var packet = new ModPacket { ModId = modId, Type = type, Payload = payload };
            var frame = ModCodec.Encode(modId, type, payload);
            var decoded = ModCodec.TryDecode(frame, out var back, out var err);
            Check($"{modId}/{type} decode [{err ?? "ok"}] len={frame.Length}", decoded && err == null);
            Check($"{modId}/{type} payload", back != null && back.ModId == modId && back.Type == type && back.Payload.Length == payload.Length);
            Check($"{modId}/{type} loopback", lc.SendRaw(frame) && lc.Send(packet));
        }

        Roundtrip("wolfpack.samplehud", 1, new byte[] { 0x01, 0x02, 0x03 });
        Roundtrip("wolfpack.samplehud", 2, Array.Empty<byte>());
        Roundtrip("狼群.mod", 3, Encoding.UTF8.GetBytes("中文 payload"));
        Roundtrip("mod.with.a.really.long.identifier.v1", 4, new byte[1024]);

        var good = ModCodec.Encode("m", 9, new byte[] { 1, 2, 3 });
        var bad = (byte[])good.Clone();
        bad[^3] ^= 0xFF;
        Check("corrupt frame rejected", !ModCodec.TryDecode(bad, out _, out var err) && err == "checksum mismatch");
        Check("truncated frame rejected", !ModCodec.TryDecode(new byte[] { 0x57 }, out _, out _));
        Check("bad magic rejected", !ModCodec.TryDecode(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, out _, out _));

        var received = 0;
        lc.Poll(_ => received++);
        Check("poll drained queue", received == 8);

        log($"[net] codec self-test: {cases - failures}/{cases} passed" + (failures > 0 ? " — FAILURES PRESENT" : ""));
    }
}
