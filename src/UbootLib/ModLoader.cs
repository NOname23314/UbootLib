using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;

namespace UbootLib;

/// <summary>
/// Discovers, orders, loads and ticks mods. Every mod runs inside an isolation
/// boundary: a failing mod is disabled and quarantined instead of crashing the game.
///
/// Enable/disable state lives in Mods/modkit.disabled (one id per line, '#' starts a
/// comment) and is re-read by <see cref="Rescan"/> so the main-menu manager can toggle
/// mods without restarting the game to edit files by hand.
/// </summary>
public static class ModLoader
{
    private const int MaxConsecutiveFailures = 60;

    private static readonly List<LoadedMod> Mods = new();
    private static readonly List<ModManifest> AllManifests = new();
    private static readonly System.Collections.Concurrent.ConcurrentQueue<(string ModId, string Key)> ConfigQueue = new();
    // Watchers must stay referenced, otherwise the GC silently stops the events.
    private static readonly List<FileSystemWatcher> Watchers = new();

    private static readonly HashSet<string> DisabledIds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> Failures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> StartupIds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> StartupDisabled = new(StringComparer.OrdinalIgnoreCase);

    private static ManualLogSource _log;
    private static Events _events;

    public static string ModsRoot { get; private set; }
    public static string DisabledFile => Path.Combine(ModsRoot ?? ".", "modkit.disabled");
    public static IReadOnlyList<LoadedMod> Loaded => Mods;
    public static IReadOnlyList<ModManifest> All => AllManifests;
    public static int TickCount { get; private set; }
    public static int DisabledCount => DisabledIds.Count;

    public sealed class LoadedMod
    {
        public ModManifest Manifest;
        public ModContext Context;
        public IMod Instance;
        public int ConsecutiveFailures;
        public bool Disabled;
        public double UpdateMs;
    }

    public static void Initialize(string modsRoot, ManualLogSource log, Events events)
    {
        ModsRoot = modsRoot;
        _log = log;
        _events = events;
        Directory.CreateDirectory(modsRoot);
    }

    // ---------------------------------------------------------------- state queries

    /// <summary>True when the mod's assembly was loaded in this session (it may be quarantined).</summary>
    public static bool IsLoaded(string id) =>
        Mods.Any(m => string.Equals(m.Manifest.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the mod is loaded and still ticking (not quarantined).</summary>
    public static bool IsActive(string id) =>
        Mods.Any(m => !m.Disabled && string.Equals(m.Manifest.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>State that will be used on the next launch.</summary>
    public static bool IsDisabledOnDisk(string id) => DisabledIds.Contains(id);

    /// <summary>True when the manifest existed when the game started (vs. added during this session).</summary>
    public static bool WasKnownAtStartup(string id) => StartupIds.Contains(id);

    public static bool WasDisabledAtStartup(string id) => StartupDisabled.Contains(id);

    // ---------------------------------------------------------------- startup / rescan

    public static void LoadAll()
    {
        Rescan();

        StartupIds.Clear();
        foreach (var m in AllManifests) StartupIds.Add(m.Id);
        StartupDisabled.Clear();
        foreach (var id in DisabledIds) StartupDisabled.Add(id);

        foreach (var manifest in Order(AllManifests.ToList()).Where(m => m.Enabled))
            LoadOne(manifest);

        _log.LogInfo($"[kit] {Mods.Count(m => !m.Disabled)}/{AllManifests.Count} mods active ({DisabledIds.Count} disabled on disk)");
    }

    /// <summary>
    /// Re-reads Mods/*/manifest.json and Mods/modkit.disabled from disk. Loaded mods are
    /// never unloaded here (assemblies cannot be unloaded); the caller compares the result
    /// with <see cref="Loaded"/> to tell the user a restart is required.
    /// </summary>
    public static int Rescan()
    {
        if (string.IsNullOrEmpty(ModsRoot)) return 0;

        Directory.CreateDirectory(ModsRoot);
        LoadDisabledFile();

        var manifests = new List<ModManifest>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in Directory.GetDirectories(ModsRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                _log.LogWarning($"[kit] skip '{Path.GetFileName(dir)}': no manifest.json");
                continue;
            }

            try
            {
                var manifest = ModManifest.Load(manifestPath);
                if (!seen.Add(manifest.Id))
                {
                    _log.LogWarning($"[kit] duplicate mod id '{manifest.Id}' in '{Path.GetFileName(dir)}' ignored");
                    continue;
                }

                manifest.Directory = dir;
                var asmName = string.IsNullOrWhiteSpace(manifest.Assembly) ? manifest.Id + ".dll" : manifest.Assembly;
                manifest.AssemblyPath = Path.Combine(dir, asmName);
                manifest.Enabled = !DisabledIds.Contains(manifest.Id);
                if (Failures.TryGetValue(manifest.Id, out var failure)) manifest.Failure = failure;
                manifests.Add(manifest);
            }
            catch (Exception e)
            {
                _log.LogError($"[kit] bad manifest in '{Path.GetFileName(dir)}': {e.Message}");
            }
        }

        AllManifests.Clear();
        AllManifests.AddRange(manifests.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase));
        _log.LogInfo($"[kit] scanned {AllManifests.Count} manifest(s), {DisabledIds.Count} disabled");
        return AllManifests.Count;
    }

    /// <summary>
    /// Writes the enable/disable state for one mod to modkit.disabled. The change is
    /// applied on the next launch: the kit never unloads assemblies at runtime.
    /// </summary>
    public static bool SetEnabled(string id, bool enabled)
    {
        var manifest = AllManifests.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
        if (manifest == null) return false;

        if (enabled) DisabledIds.Remove(id);
        else DisabledIds.Add(id);

        manifest.Enabled = enabled;
        PersistDisabled();
        _log.LogInfo($"[kit] '{id}' {(enabled ? "enabled" : "disabled")} on disk (applies after restart)");
        return true;
    }

    private static void LoadDisabledFile()
    {
        DisabledIds.Clear();
        var path = DisabledFile;
        if (!File.Exists(path)) return;

        try
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                DisabledIds.Add(line);
            }
        }
        catch (Exception e)
        {
            _log.LogWarning($"[kit] modkit.disabled unreadable ({e.Message}); treating all mods as enabled");
        }
    }

    private static void PersistDisabled()
    {
        try
        {
            var path = DisabledFile;
            var tmp = path + ".tmp";
            File.WriteAllLines(tmp, DisabledIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
            File.Move(tmp, path, true);
        }
        catch (Exception e)
        {
            _log.LogError($"[kit] could not write modkit.disabled: {e.GetType().Name}: {e.Message}");
        }
    }

    private static IEnumerable<ModManifest> Order(List<ModManifest> manifests)
    {
        // Topological sort on DependsOn, honouring LoadOrder between independent mods.
        var byId = manifests.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        var result = new List<ModManifest>();
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 0=new 1=visiting 2=done

        void Visit(ModManifest m)
        {
            if (state.TryGetValue(m.Id, out var s))
            {
                if (s == 1) _log.LogWarning($"[kit] dependency cycle at '{m.Id}'");
                return;
            }

            state[m.Id] = 1;
            foreach (var dep in m.DependsOn)
            {
                var depId = dep.Split('>', '=', '<')[0].Trim();
                if (byId.TryGetValue(depId, out var depMod)) Visit(depMod);
                else _log.LogWarning($"[kit] '{m.Id}' depends on missing '{depId}'");
            }
            state[m.Id] = 2;
            result.Add(m);
        }

        foreach (var m in manifests.OrderBy(m => m.LoadOrder).ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase))
            Visit(m);

        return result;
    }

    private static void LoadOne(ModManifest manifest)
    {
        var loaded = new LoadedMod { Manifest = manifest };
        try
        {
            if (!File.Exists(manifest.AssemblyPath))
                throw new FileNotFoundException($"assembly not found: {Path.GetFileName(manifest.AssemblyPath)}");

            var asm = Assembly.LoadFrom(manifest.AssemblyPath);
            var type = asm.GetType(manifest.Entry, throwOnError: false);
            if (type == null)
                throw new TypeLoadException($"entry type '{manifest.Entry}' not found in {asm.GetName().Name}");
            if (!typeof(IMod).IsAssignableFrom(type))
                throw new InvalidCastException($"'{manifest.Entry}' does not implement IMod");

            var instance = (IMod)Activator.CreateInstance(type);
            var context = new ModContext(manifest, _log, _events);
            loaded.Context = context;
            loaded.Instance = instance;

            instance.OnLoad(context);
            instance.OnEnable();
            Mods.Add(loaded);
            Failures.Remove(manifest.Id);
            WatchConfig(manifest, context);
            _log.LogInfo($"[kit] loaded {manifest.Id} {manifest.Version} (network={manifest.NetworkMode})");
        }
        catch (Exception e)
        {
            var failure = $"{e.GetType().Name}: {e.Message}";
            manifest.Failure = failure;
            Failures[manifest.Id] = failure;
            manifest.Enabled = false;
            _log.LogError($"[kit] FAILED {manifest.Id}: {failure}");
            Quarantine(manifest);
        }
    }

    /// <summary>Watches config/ so edits apply without restarting the game.</summary>
    private static void WatchConfig(ModManifest manifest, ModContext context)
    {
        try
        {
            var dir = context.ConfigDirectory;
            Directory.CreateDirectory(dir);
            var watcher = new FileSystemWatcher(dir, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            void Handler(object sender, FileSystemEventArgs e)
            {
                var key = Path.GetFileNameWithoutExtension(e.Name ?? "");
                if (key.Length > 0) ConfigQueue.Enqueue((manifest.Id, key));
            }
            watcher.Changed += Handler;
            watcher.Created += Handler;
            Watchers.Add(watcher);
            _log.LogInfo($"[kit] watching config for '{manifest.Id}'");
        }
        catch (Exception e)
        {
            _log.LogWarning($"[kit] config watcher for '{manifest.Id}' failed: {e.Message}");
        }
    }

    private static void DrainConfigQueue()
    {
        while (ConfigQueue.TryDequeue(out var change))
        {
            var mod = Mods.FirstOrDefault(m => m.Manifest.Id == change.ModId && !m.Disabled);
            if (mod == null) continue;
            try
            {
                _log.LogInfo($"[kit] config reloaded: {change.ModId}/{change.Key}");
                mod.Instance.OnConfigChanged(change.Key);
                mod.Context.RaiseConfigChanged(change.Key);
            }
            catch (Exception e)
            {
                _log.LogError($"[kit] {change.ModId}.OnConfigChanged('{change.Key}') failed: {e.GetType().Name}: {e.Message}");
            }
        }
    }

    private static void Quarantine(ModManifest manifest)
    {
        try
        {
            var path = Path.Combine(ModsRoot, "modkit.quarantined.log");
            File.AppendAllText(path, $"{DateTime.Now:u}	{manifest.Id}	{manifest.Failure}{Environment.NewLine}");
        }
        catch { /* diagnostics must never throw */ }
    }

    internal static void ReportHandlerFailure(string evt, Exception e)
        => _log?.LogError($"[kit] event handler '{evt}' threw: {e.GetType().Name}: {e.Message}");

    public static void Tick()
    {
        TickCount++;
        DrainConfigQueue();
        foreach (var m in Mods)
        {
            if (m.Disabled) continue;
            var start = DateTime.UtcNow;
            try
            {
                m.Instance.OnUpdate();
                m.ConsecutiveFailures = 0;
            }
            catch (Exception e)
            {
                m.ConsecutiveFailures++;
                m.UpdateMs = 0;
                if (m.ConsecutiveFailures == 1 || m.ConsecutiveFailures % 30 == 0)
                    _log.LogError($"[kit] {m.Manifest.Id}.OnUpdate failed ({m.ConsecutiveFailures}x): {e.GetType().Name}: {e.Message}");
                if (m.ConsecutiveFailures >= MaxConsecutiveFailures)
                {
                    m.Disabled = true;
                    m.Instance.OnDisable();
                    _log.LogError($"[kit] {m.Manifest.Id} disabled after {MaxConsecutiveFailures} consecutive failures");
                    Quarantine(m.Manifest);
                }
            }
            m.UpdateMs = (DateTime.UtcNow - start).TotalMilliseconds;
        }
    }


    public static void Shutdown()
    {
        foreach (var m in Mods.Where(m => !m.Disabled))
        {
            try { m.Instance.OnDisable(); } catch { /* ignore during shutdown */ }
        }
    }
}
