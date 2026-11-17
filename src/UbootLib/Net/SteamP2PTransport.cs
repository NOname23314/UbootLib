using System;
using System.Collections.Generic;
using Steamworks;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UbootLib.Net;

namespace UbootLib.Net;

/// <summary>
/// Steam P2P transport for mod traffic. Uses logical channel 5 so the game's own
/// channels (1 protocol / 2 voice / 3 text chat / 4 morse) are never touched.
/// </summary>
public sealed class SteamP2PTransport : IModTransport
{
    public const int Channel = 5;
    public const int MaxPacket = 60 * 1024;

    private Il2CppStructArray<byte> _native;
    private Il2CppStructArray<byte> Native => _native ??= new Il2CppStructArray<byte>(MaxPacket);

    public CSteamID LocalId { get; }
    public string Name => "steam-p2p";

    public SteamP2PTransport(CSteamID localId) => LocalId = localId;

    public bool Send(ModPacket packet) => SendTo(LocalId, packet);

    public bool SendTo(CSteamID target, ModPacket packet)
    {
        try
        {
            var frame = ModCodec.Encode(packet.ModId, packet.Type, packet.Payload);
            if (frame.Length > MaxPacket) return false;
            return SteamNetworking.SendP2PPacket(target, frame, (uint)frame.Length,
                EP2PSend.k_EP2PSendReliable, Channel);
        }
        catch (Exception e)
        {
            UbootLibPlugin.KitLog.LogWarning($"[net] send failed: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    public void Poll(Action<ModPacket> onPacket) => PollFrom((p, _) => onPacket(p));

    public void PollFrom(Action<ModPacket, CSteamID> onPacket)
    {
        try
        {
            while (SteamNetworking.IsP2PPacketAvailable(out var size, Channel))
            {
                var buffer = Native;
                if (!SteamNetworking.ReadP2PPacket(buffer, (uint)buffer.Length, out var read,
                        out var sender, Channel))
                    break;

                var frame = new byte[read];
                for (var k = 0; k < read; k++) frame[k] = buffer[k];

                if (ModCodec.TryDecode(frame, out var packet, out var error))
                    onPacket(packet, sender);
                else
                {
                    var hex = BitConverter.ToString(frame, 0, Math.Min(8, frame.Length));
                    UbootLibPlugin.KitLog.LogWarning($"[net] dropped frame from {sender.m_SteamID}: {error} (len={frame.Length}, head={hex})");
                }
            }
        }
        catch (Exception e)
        {
            UbootLibPlugin.KitLog.LogWarning($"[net] poll failed: {e.GetType().Name}: {e.Message}");
        }
    }
}

/// <summary>
/// Kit-level networking façade: lazy Steam init, packet fan-out, counters.
/// Nothing here is enabled until Steam is confirmed running.
/// </summary>
public static class ModNet
{
    public static bool Ready { get; private set; }
    public static string Status { get; private set; } = "not initialised";
    public static int Sent { get; private set; }
    public static int Received { get; private set; }

    public static event Action<ModPacket> PacketReceived;

    private static SteamP2PTransport _transport;
    private static int _ticks;
    private static string _lastReport = "";
    public static string LastReport => _lastReport;
    public static string PeerSummary { get; private set; } = "-";
    private static bool _selfCheckDone;

    public static CSteamID LocalId => _transport?.LocalId ?? CSteamID.Nil;

    public static void Tick()
    {
        if (!Ready)
        {
            TryInit();
            return;
        }

        _transport?.PollFrom(OnPacket);

        if (++_ticks % 300 == 0) SendHelloToLobby();
    }

    private static string _lastInitLog;
    private static int _initAttempts;

    private static void LogOnce(string message)
    {
        _initAttempts++;
        if (_lastInitLog != message)
        {
            _lastInitLog = message;
            UbootLibPlugin.KitLog.LogInfo($"[net] {message} (attempt {_initAttempts})");
        }
    }

    private static void TryInit()
    {
        try
        {
            if (!SteamAPI.IsSteamRunning())
            {
                Status = "waiting for Steam";
                LogOnce("waiting for Steam");
                return;
            }

            var id = SteamUser.GetSteamID();
            if (!id.IsValid()) { Status = "waiting for Steam user"; return; }

            _transport = new SteamP2PTransport(id);
            Ready = true;
            Status = $"ready as {id.m_SteamID}";
            UbootLibPlugin.KitLog.LogInfo($"[net] Steam P2P transport ready (channel {SteamP2PTransport.Channel}), local id {id.m_SteamID}");

            if (!_selfCheckDone)
            {
                _selfCheckDone = true;
                SelfCheck();
            }
        }
        catch (Exception e)
        {
            Status = "waiting for Steam";
            LogOnce($"waiting for Steam ({e.GetType().Name})");
        }
    }

    /// <summary>
    /// Verifies channel isolation. A self-addressed packet is expected to be rejected
    /// by Steam (you cannot P2P with yourself) — that is a valid, non-fatal result.
    /// </summary>
    private static void SelfCheck()
    {
        try
        {
            var clean = !SteamNetworking.IsP2PPacketAvailable(out _, SteamP2PTransport.Channel);
            UbootLibPlugin.KitLog.LogInfo($"[net] channel {SteamP2PTransport.Channel} idle at startup: {clean}");

            var probe = new ModPacket { ModId = "uboot.lib", Type = 0, Payload = Array.Empty<byte>() };
            var frame = ModCodec.Encode(probe.ModId, probe.Type, probe.Payload);
            var selfSend = SteamNetworking.SendP2PPacket(LocalId, frame, (uint)frame.Length,
                EP2PSend.k_EP2PSendReliable, SteamP2PTransport.Channel);
            UbootLibPlugin.KitLog.LogInfo($"[net] self-addressed send accepted by API: {selfSend} " +
                                        "(Steam normally refuses self P2P; two clients needed for a real round-trip)");
        }
        catch (Exception e)
        {
            UbootLibPlugin.KitLog.LogWarning($"[net] self-check failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void OnPacket(ModPacket packet, CSteamID sender)
    {
        Received++;
        try
        {
            if (packet.ModId == "uboot.lib" && packet.Type == Handshake.Hello)
            {
                var ack = Handshake.CreatePacket(Handshake.HelloAck);
                SendTo(sender, ack);
                UbootLibPlugin.KitLog.LogInfo($"[net] hello from {sender.m_SteamID}, replied with our mod list");
            }
            else if (packet.ModId == "uboot.lib" && packet.Type == Handshake.HelloAck)
            {
                if (Handshake.TryParse(packet.Payload, out var remote))
                {
                    PeerSummary = $"{remote.Mods.Count} mod(s), game {remote.Game}";
                    var report = Handshake.Compare(remote);
                    if (report != _lastReport)
                    {
                        _lastReport = report;
                        if (report.Length == 0)
                            UbootLibPlugin.KitLog.LogInfo("[net] mod lists match");
                        else
                            UbootLibPlugin.KitLog.LogWarning($"[net] mod mismatch (non-blocking): {report}");
                    }
                }
            }

            PacketReceived?.Invoke(packet);
        }
        catch (Exception e)
        {
            UbootLibPlugin.KitLog.LogError($"[net] packet handler threw: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Sends our mod list to the lobby peers, if the game has a lobby.</summary>
    private static void SendHelloToLobby()
    {
        if (!Ready) return;
        try
        {
            var mgr = W_NetworkManager.instance;
            if (mgr == null) return;

            var hello = Handshake.CreatePacket(Handshake.Hello);
            if (W_NetworkManager.IsServer)
            {
                foreach (var p in mgr.clients) SendTo(p.id, hello);
            }
            else
            {
                SendTo(mgr.getServerId(), hello);
            }
        }
        catch (Exception e)
        {
            UbootLibPlugin.KitLog.LogWarning($"[net] hello send skipped: {e.GetType().Name}: {e.Message}");
        }
    }

    public static bool SendTo(CSteamID target, ModPacket packet)
    {
        if (!Ready) return false;
        var ok = _transport.SendTo(target, packet);
        if (ok) Sent++;
        return ok;
    }

    public static IEnumerable<string> Describe()
    {
        yield return $"status: {Status}";
        yield return $"sent: {Sent}  received: {Received}";
    }
}
