using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace UbootLib.Net;

public sealed class ModEntryDto
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Net { get; set; } = "client-only";
}

/// <summary>What a peer sends us: its game version and the mods it runs.</summary>
public sealed class ModListDto
{
    public string Game { get; set; } = "";
    public List<ModEntryDto> Mods { get; set; } = new();
}

/// <summary>
/// Mod-list handshake. Deliberately non-blocking: a mismatch is reported to the
/// player and the kit keeps running — we never prevent someone from playing.
/// </summary>
public static class Handshake
{
    public const byte Hello = 1;
    public const byte HelloAck = 2;
    private const string ModId = "uboot.lib";

    public static ModListDto BuildLocalList()
    {
        var dto = new ModListDto { Game = UnityEngine.Application.version };
        foreach (var m in ModLoader.Loaded)
            dto.Mods.Add(new ModEntryDto
            {
                Id = m.Manifest.Id,
                Version = m.Manifest.Version,
                Net = m.Manifest.NetworkMode.ToString()
            });
        return dto;
    }

    public static byte[] Encode(ModListDto dto) => JsonSerializer.SerializeToUtf8Bytes(dto);

    public static bool TryParse(byte[] payload, out ModListDto dto)
    {
        dto = null;
        try
        {
            dto = JsonSerializer.Deserialize<ModListDto>(payload);
            return dto != null;
        }
        catch
        {
            return false;
        }
    }

    public static ModPacket CreatePacket(byte type) =>
        new() { ModId = ModId, Type = type, Payload = Encode(BuildLocalList()) };

    /// <summary>Human readable difference report; empty string when both sides match.</summary>
    public static string Compare(ModListDto remote)
    {
        if (remote == null) return "no handshake payload";

        var local = BuildLocalList();
        var mine = local.Mods.ToDictionary(m => m.Id, m => m, StringComparer.OrdinalIgnoreCase);
        var theirs = remote.Mods.ToDictionary(m => m.Id, m => m, StringComparer.OrdinalIgnoreCase);

        var missing = mine.Keys.Where(k => !theirs.ContainsKey(k)).ToList();
        var extra = theirs.Keys.Where(k => !mine.ContainsKey(k)).ToList();
        var diff = mine.Keys
            .Where(k => theirs.ContainsKey(k) && theirs[k].Version != mine[k].Version)
            .Select(k => $"{k} {mine[k].Version}≠{theirs[k].Version}")
            .ToList();

        if (missing.Count == 0 && extra.Count == 0 && diff.Count == 0 && local.Game == remote.Game)
            return string.Empty;

        var parts = new List<string>();
        if (local.Game != remote.Game) parts.Add($"game {local.Game}≠{remote.Game}");
        if (missing.Count > 0) parts.Add($"missing here: {string.Join(",", missing)}");
        if (extra.Count > 0) parts.Add($"extra here: {string.Join(",", extra)}");
        if (diff.Count > 0) parts.Add($"version: {string.Join(",", diff)}");
        return string.Join("; ", parts);
    }
}
