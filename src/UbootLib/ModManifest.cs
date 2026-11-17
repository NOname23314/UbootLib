using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UbootLib;

/// <summary>Network policy declared by a mod; used to gate public lobbies.</summary>
public enum NetworkPolicy
{
    ClientOnly,
    HostRequired,
    AllClients
}

/// <summary>Contents of Mods/&lt;ModId&gt;/manifest.json.</summary>
public sealed class ModManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "0.0.0";
    public string Author { get; set; } = "";
    /// <summary>Managed type implementing <see cref="IMod"/>, e.g. "SampleMod.SamplePlugin".</summary>
    public string Entry { get; set; } = "";
    /// <summary>Assembly file name; defaults to &lt;ModId&gt;.dll.</summary>
    public string Assembly { get; set; } = "";
    public string GameVersion { get; set; } = "";
    public List<string> DependsOn { get; set; } = new();
    /// <summary>"client-only" | "host-required" | "all-clients".</summary>
    public string Network { get; set; } = "client-only";
    public int LoadOrder { get; set; } = 100;

    [JsonIgnore]
    public NetworkPolicy NetworkMode => (Network ?? "").Trim().ToLowerInvariant() switch
    {
        "host-required" => NetworkPolicy.HostRequired,
        "all-clients" => NetworkPolicy.AllClients,
        _ => NetworkPolicy.ClientOnly
    };

    [JsonIgnore] public string Directory { get; internal set; } = "";
    [JsonIgnore] public string AssemblyPath { get; internal set; } = "";
    [JsonIgnore] public bool Enabled { get; internal set; } = true;
    [JsonIgnore] public string Failure { get; internal set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static ModManifest Load(string path)
    {
        var json = File.ReadAllText(path);
        var manifest = JsonSerializer.Deserialize<ModManifest>(json, Options)
                       ?? throw new InvalidDataException("manifest.json is empty");
        if (string.IsNullOrWhiteSpace(manifest.Id))
            throw new InvalidDataException("manifest.json is missing 'id'");
        return manifest;
    }
}

/// <summary>A mod entry point. One instance per manifest.</summary>
public interface IMod
{
    /// <summary>Called once on the Unity main thread after the assembly is loaded.</summary>
    void OnLoad(ModContext context);
    /// <summary>Called right before the runner starts ticking; safe place for Harmony patches.</summary>
    void OnEnable();
    void OnDisable();
    /// <summary>Per-frame callback, driven by the kit frame pump.</summary>
    void OnUpdate();
    /// <summary>Raised on the Unity main thread when config/&lt;key&gt;.json changes on disk.</summary>
    void OnConfigChanged(string key);
}

/// <summary>Base class that turns all optional callbacks into no-ops.</summary>
public abstract class ModBase : IMod
{
    protected ModContext Context { get; private set; }
    public virtual void OnLoad(ModContext context) { Context = context; }
    public virtual void OnEnable() { }
    public virtual void OnDisable() { }
    public virtual void OnUpdate() { }
    public virtual void OnConfigChanged(string key) { }
}

/// <summary>Everything a mod may touch: paths, logging, config, harmony.</summary>
public sealed class ModContext
{
    private readonly BepInEx.Logging.ManualLogSource _log;
    private readonly Dictionary<string, object> _configValues = new();

    public ModManifest Manifest { get; }
    public string Directory { get; }
    public HarmonyLib.Harmony Harmony { get; }
    public Events GameEvents { get; }
    /// <summary>Fires when a config file under config/ is edited while the game runs.</summary>
    public event Action<string> ConfigChanged;
    public string ConfigDirectory => Path.Combine(Directory, "config");
    /// <summary>Screen-space overlay owned by the kit; survives scene changes.</summary>
    public Hud Hud { get; } = new();

    internal ModContext(ModManifest manifest, BepInEx.Logging.ManualLogSource log, Events events)
    {
        Manifest = manifest;
        Directory = manifest.Directory;
        _log = log;
        GameEvents = events;
        Harmony = new HarmonyLib.Harmony(manifest.Id);
        System.IO.Directory.CreateDirectory(Path.Combine(Directory, "config"));
    }

    public void Log(string message) => _log.LogInfo($"[{Manifest.Id}] {message}");
    public void Warn(string message) => _log.LogWarning($"[{Manifest.Id}] {message}");
    public void Error(string message) => _log.LogError($"[{Manifest.Id}] {message}");

    /// <summary>Simple JSON-backed key/value config, persisted under Mods/&lt;Id&gt;/config/.</summary>
    internal void RaiseConfigChanged(string key)
    {
        try { ConfigChanged?.Invoke(key); }
        catch (Exception e) { Warn($"ConfigChanged handler threw: {e.GetType().Name}: {e.Message}"); }
    }

    public T GetConfig<T>(string key, T fallback)
    {
        var path = Path.Combine(Directory, "config", key + ".json");
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) is { } v ? v : fallback;
        }
        catch (Exception e)
        {
            Warn($"config '{key}' unreadable ({e.Message}); using default");
        }
        return fallback;
    }

    public void SetConfig<T>(string key, T value)
    {
        var path = Path.Combine(Directory, "config", key + ".json");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            Warn($"config '{key}' not saved ({e.Message})");
        }
    }
}
