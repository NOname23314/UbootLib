using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace UbootLib.Assets;

/// <summary>
/// One mission definition in the game's own text format: a header comment followed by
/// <c>Key(value)</c> lines (MissionTitle(...), TonnageGoal(...), TargetList(...), ...).
/// Fields are kept in order and untyped so unknown/new keys survive a round-trip.
/// </summary>
public sealed class MissionDoc
{
    public const string Header =
        "// Mission file for the game Wolfpack. https://store.steampowered.com/app/490920/Wolfpack/";

    public List<KeyValuePair<string, string>> Fields { get; } = new();

    public string Title => Get("MissionTitle");
    public bool Has(string key) => Fields.Any(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    public string Get(string key) => Fields.FirstOrDefault(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    public MissionDoc Set(string key, string value)
    {
        for (var i = 0; i < Fields.Count; i++)
            if (Fields[i].Key.Equals(key, StringComparison.OrdinalIgnoreCase)) { Fields[i] = new(key, value); return this; }
        Fields.Add(new(key, value));
        return this;
    }

    public static MissionDoc Parse(string text)
    {
        var doc = new MissionDoc();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//")) continue;

            var open = line.IndexOf('(');
            var close = line.LastIndexOf(')');
            if (open <= 0 || close < open) continue;

            var key = line[..open].Trim();
            var value = line[(open + 1)..close].Trim();
            if (key.Length > 0) doc.Fields.Add(new(key, value));
        }
        return doc;
    }

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Header);
        sb.AppendLine();
        foreach (var f in Fields) sb.AppendLine($"{f.Key}({f.Value})");
        return sb.ToString();
    }
}

public sealed class MissionIssue
{
    public string File { get; init; } = "";
    public string Message { get; init; } = "";
    public override string ToString() => $"{System.IO.Path.GetFileName(File)}: {Message}";
}

/// <summary>Validates and discovers mission files shipped by mods.</summary>
public static class Missions
{
    /// <summary>Required keys for a mission to be playable.</summary>
    public static readonly string[] Required = { "MissionTitle", "MissionDescription", "TonnageGoal" };

    public static IReadOnlyList<string> Validate(string text, out MissionDoc doc)
    {
        var errors = new List<string>();
        doc = MissionDoc.Parse(text);

        if (doc.Fields.Count == 0) errors.Add("no Key(value) fields found");

        foreach (var key in Required)
            if (!doc.Has(key)) errors.Add($"missing required key '{key}'");

        var duplicates = doc.Fields.GroupBy(f => f.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1);
        foreach (var d in duplicates) errors.Add($"duplicate key '{d.Key}'");

        if (doc.Has("TonnageGoal") && !int.TryParse(doc.Get("TonnageGoal"), out _))
            errors.Add("TonnageGoal is not a number");

        foreach (var line in (text ?? "").Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith("//")) continue;
            if (t.Count(c => c == '(') != t.Count(c => c == ')')) { errors.Add($"unbalanced parentheses: {t}"); break; }
        }

        return errors;
    }

    /// <summary>Scans <c>Mods/&lt;Id&gt;/missions/*.txt</c> and reports what is usable.</summary>
    public static IReadOnlyList<(string Path, MissionDoc Doc, IReadOnlyList<string> Errors)> Scan(string modsRoot, Action<string> log)
    {
        var found = new List<(string, MissionDoc, IReadOnlyList<string>)>();
        if (!Directory.Exists(modsRoot)) return found;

        foreach (var modDir in Directory.GetDirectories(modsRoot))
        {
            var dir = System.IO.Path.Combine(modDir, "missions");
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.GetFiles(dir, "*.txt"))
            {
                try
                {
                    var text = File.ReadAllText(file);
                    var errors = Validate(text, out var doc);
                    found.Add((file, doc, errors));

                    log(errors.Count == 0
                        ? $"[content] mission ok: {System.IO.Path.GetFileName(file)} (\"{doc.Title}\", {doc.Fields.Count} fields)"
                        : $"[content] mission invalid: {System.IO.Path.GetFileName(file)} -> {string.Join("; ", errors)}");
                }
                catch (Exception e)
                {
                    log($"[content] mission unreadable '{file}': {e.GetType().Name}: {e.Message}");
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Puts a mission on the clipboard. The in-game Mission Editor has
    /// "Import mission from clipboard", which turns this text into an editable mission.
    /// </summary>
    public static bool CopyToClipboard(MissionDoc doc)
    {
        try
        {
            GUIUtility.systemCopyBuffer = doc.ToText();
            return true;
        }
        catch (Exception e)
        {
            UbootLibPlugin.KitLog?.LogWarning($"[content] clipboard failed: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    /// <summary>Writes a mission next to the mod so it can be edited/uploaded later.</summary>
    public static string Save(MissionDoc doc, string path)
    {
        File.WriteAllText(path, doc.ToText(), new UTF8Encoding(false));
        return path;
    }
}
