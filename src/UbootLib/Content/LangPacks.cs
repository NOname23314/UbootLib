using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UbootLib.Assets;

public sealed class LangPackInfo
{
    public string Path { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Header { get; init; } = "";
    public int Pairs { get; init; }
    public bool Valid { get; init; }
    public string Error { get; init; } = "";
}

/// <summary>
/// Language packs are plain JSON files the game already understands
/// (<c>{ "header": "...", "pairs": [{ "key": int, "value": "..." }] }</c>).
/// The game loads every <c>*.lang</c> in <c>Wolfpack_Data\Languages</c> during Setup,
/// so a mod only has to drop its file there before that scene runs — which is exactly
/// when this pipeline executes.
/// </summary>
public static class LangPacks
{
    public static string GameLanguagesDir(string gameRoot) =>
        System.IO.Path.Combine(gameRoot, "Wolfpack_Data", "Languages");

    /// <summary>Validates a .lang file without touching the game folder.</summary>
    public static LangPackInfo Inspect(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            if (!root.TryGetProperty("header", out var header) || header.ValueKind != JsonValueKind.String)
                return new LangPackInfo { Path = path, FileName = System.IO.Path.GetFileName(path), Valid = false, Error = "missing 'header'" };

            if (!root.TryGetProperty("pairs", out var pairs) || pairs.ValueKind != JsonValueKind.Array)
                return new LangPackInfo { Path = path, FileName = System.IO.Path.GetFileName(path), Valid = false, Error = "missing 'pairs' array" };

            var count = 0;
            foreach (var p in pairs.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) return Bad(path, header.GetString(), "pair is not an object");
                if (!p.TryGetProperty("key", out var k) || k.ValueKind != JsonValueKind.Number) return Bad(path, header.GetString(), "pair without numeric key");
                if (!p.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.String) return Bad(path, header.GetString(), "pair without string value");
                count++;
            }

            return new LangPackInfo
            {
                Path = path,
                FileName = System.IO.Path.GetFileName(path),
                Header = header.GetString() ?? "",
                Pairs = count,
                Valid = count > 0
            };
        }
        catch (Exception e)
        {
            return new LangPackInfo { Path = path, FileName = System.IO.Path.GetFileName(path), Valid = false, Error = $"{e.GetType().Name}: {e.Message}" };
        }

        static LangPackInfo Bad(string path, string header, string error) =>
            new() { Path = path, FileName = System.IO.Path.GetFileName(path), Header = header, Valid = false, Error = error };
    }

    /// <summary>
    /// Copies every <c>Mods/&lt;Id&gt;/lang/*.lang</c> into the game's Languages folder.
    /// Returns the packs that were installed. Idempotent: identical files are skipped.
    /// </summary>
    public static IReadOnlyList<LangPackInfo> InstallAll(string gameRoot, string modsRoot, Action<string> log)
    {
        var installed = new List<LangPackInfo>();
        var target = GameLanguagesDir(gameRoot);

        try
        {
            Directory.CreateDirectory(target);
        }
        catch (Exception e)
        {
            log($"[content] languages folder unavailable: {e.GetType().Name}: {e.Message}");
            return installed;
        }

        if (!Directory.Exists(modsRoot)) return installed;

        foreach (var modDir in Directory.GetDirectories(modsRoot))
        {
            var langDir = System.IO.Path.Combine(modDir, "lang");
            if (!Directory.Exists(langDir)) continue;

            foreach (var file in Directory.GetFiles(langDir, "*.lang"))
            {
                var info = Inspect(file);
                if (!info.Valid)
                {
                    log($"[content] skip lang '{info.FileName}': {info.Error}");
                    continue;
                }

                try
                {
                    var dest = System.IO.Path.Combine(target, info.FileName);
                    if (!File.Exists(dest) || !FilesEqual(file, dest))
                    {
                        File.Copy(file, dest, overwrite: true);
                        log($"[content] language pack installed: {info.FileName} (header '{info.Header}', {info.Pairs} entries)");
                    }
                    installed.Add(info);
                }
                catch (Exception e)
                {
                    log($"[content] language pack '{info.FileName}' failed: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        if (installed.Count == 0) log("[content] no language packs found in Mods/*/lang");
        return installed;
    }

    private static bool FilesEqual(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            return fa.Length == fb.Length && File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b));
        }
        catch { return false; }
    }
}
