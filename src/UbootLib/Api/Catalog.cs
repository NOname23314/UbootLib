using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UbootLib.Api;

/// <summary>One game type discovered from the IL2CPP metadata.</summary>
public sealed class GameTypeInfo
{
    [JsonPropertyName("t")] public string FullName { get; init; } = "";
    [JsonPropertyName("n")] public string Name { get; init; } = "";
    [JsonPropertyName("ns")] public string Namespace { get; init; } = "";
    [JsonPropertyName("k")] public string Kind { get; init; } = "";
    [JsonPropertyName("m")] public int Methods { get; init; }
    [JsonPropertyName("b")] public string Base { get; init; } = "";

    private Type _clr;
    private bool _resolved;

    /// <summary>Interop (managed proxy) type, resolved lazily from Assembly-CSharp.</summary>
    public Type Clr
    {
        get
        {
            if (_resolved) return _clr;
            _resolved = true;
            try { _clr = Type.GetType($"{FullName}, Assembly-CSharp", throwOnError: false); }
            catch { _clr = null; }
            return _clr;
        }
    }

    public override string ToString() => FullName;
}

/// <summary>
/// Searchable index of every game type in the build. The catalog is embedded in the
/// assembly, so mods can discover APIs without shipping a copy of dump.cs.
/// </summary>
public static class Catalog
{
    private static readonly List<GameTypeInfo> Types = new();
    private static readonly Dictionary<string, GameTypeInfo> ByFull = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<GameTypeInfo>> ByShort = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    public static int Count { get { EnsureLoaded(); return Types.Count; } }
    public static IReadOnlyList<GameTypeInfo> All { get { EnsureLoaded(); return Types; } }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            var asm = typeof(Catalog).Assembly;
            using var stream = asm.GetManifestResourceStream("UbootLib.api-catalog.json");
            if (stream == null)
            {
                UbootLibPlugin.KitLog?.LogWarning("[api] catalog resource missing");
                return;
            }

            var items = JsonSerializer.Deserialize<List<GameTypeInfo>>(stream) ?? new List<GameTypeInfo>();
            foreach (var t in items)
            {
                Types.Add(t);
                ByFull[t.FullName] = t;
                if (!ByShort.TryGetValue(t.Name, out var list))
                    ByShort[t.Name] = list = new List<GameTypeInfo>();
                list.Add(t);
            }

            UbootLibPlugin.KitLog?.LogInfo($"[api] catalog loaded: {Types.Count} game types");
        }
        catch (Exception e)
        {
            UbootLibPlugin.KitLog?.LogError($"[api] catalog load failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Exact full name, otherwise a unique short-name match.</summary>
    public static GameTypeInfo Find(string nameOrFullName)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(nameOrFullName)) return null;
        var key = nameOrFullName.Trim();

        if (ByFull.TryGetValue(key, out var exact)) return exact;
        if (ByShort.TryGetValue(key, out var list) && list.Count == 1) return list[0];
        return null;
    }

    /// <summary>Fuzzy search over full names (case-insensitive substring).</summary>
    public static IEnumerable<GameTypeInfo> Search(string fragment, int limit = 40)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(fragment)) return Array.Empty<GameTypeInfo>();
        fragment = fragment.Trim();
        return Types
            .Where(t => t.FullName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.FullName.Length)
            .Take(Math.Max(1, limit));
    }

    /// <summary>Resolved interop type, or null when the type was stripped.</summary>
    public static Type Resolve(string nameOrFullName) => Find(nameOrFullName)?.Clr;
}
