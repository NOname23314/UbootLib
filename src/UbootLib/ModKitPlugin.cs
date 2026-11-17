using System;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace UbootLib;

/// <summary>
/// Kit entry point. Owns the mod root, the frame pump and the scene hook.
///
/// NOTE: an injected MonoBehaviour (ClassInjector) was tried first and crashes this
/// build — Il2CppInterop's generated finalizer throws "Handle is not initialized"
/// when Unity destroys the injected component during a scene change. The pump is
/// therefore driven by a Harmony prefix on an existing per-frame method.
/// </summary>
[BepInPlugin(Guid, "UbootLib", "0.2.0")]
public class UbootLibPlugin : BasePlugin
{
    public const string Guid = "uboot.lib";

    internal static ManualLogSource KitLog;
    internal static Events Events;

    public override void Load()
    {
        KitLog = Log;
        Log.LogInfo("==================================================");
        Log.LogInfo(" UbootLib 0.2.0  (P5 main-menu mod manager)");
        Log.LogInfo("==================================================");

        var modsRoot = Path.Combine(Paths.GameRootPath, "Mods");
        Events = new Events();
        ModLoader.Initialize(modsRoot, Log, Events);
        Log.LogInfo($"[kit] mods root: {modsRoot}");

        PatchScenes();
        PatchPump();
        TryLoadCatalog();
        ModLoader.LoadAll();
        RunContentPipelines();
        Net.NetSelfTest.Run(msg => Log.LogInfo(msg));
        Log.LogInfo("[kit] startup complete");
    }

    private void TryLoadCatalog()
    {
        try
        {
            var total = Api.Catalog.Count;
            var sample = string.Join(", ", Api.Catalog.Search("Uboat", 6).Select(t => t.Name));
            Log.LogInfo($"[api] catalog: {total} game types (e.g. {sample})");
        }
        catch (Exception e)
        {
            Log.LogWarning($"[api] catalog probe failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Language packs + mission discovery run before the Setup scene loads languages.</summary>
    private void RunContentPipelines()
    {
        try
        {
            var langs = Assets.LangPacks.InstallAll(Paths.GameRootPath, ModLoader.ModsRoot, m => Log.LogInfo(m));
            Log.LogInfo($"[content] language packs active: {langs.Count}");

            var missions = Assets.Missions.Scan(ModLoader.ModsRoot, m => Log.LogInfo(m));
            Log.LogInfo($"[content] missions found: {missions.Count} (valid: {missions.Count(x => x.Errors.Count == 0)})");
        }
        catch (Exception e)
        {
            Log.LogWarning($"[content] pipeline failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private void PatchScenes()
    {
        try
        {
            var target = AccessTools.Method(typeof(Wolfpack.CursorControl.CursorController), "OnSceneLoaded");
            if (target == null) { Log.LogError("[kit] scene hook target not found"); return; }

            new Harmony(Guid + ".scenes")
                .Patch(target, prefix: new HarmonyMethod(typeof(SceneHook).GetMethod(nameof(SceneHook.Prefix))));
            Log.LogInfo("[kit] scene hook installed");
        }
        catch (Exception e)
        {
            Log.LogError($"[kit] scene hook failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private void PatchPump()
    {
        try
        {
            var target = AccessTools.Method(typeof(Wolfpack.CursorControl.CursorController), "Update");
            if (target == null) { Log.LogError("[kit] pump target not found"); return; }

            new Harmony(Guid + ".pump")
                .Patch(target, prefix: new HarmonyMethod(typeof(TickHook).GetMethod(nameof(TickHook.Prefix))));
            Log.LogInfo("[kit] frame pump installed on CursorController.Update");
        }
        catch (Exception e)
        {
            Log.LogError($"[kit] pump failed: {e.GetType().Name}: {e.Message}");
        }
    }
}

internal static class SceneHook
{
    public static void Prefix()
    {
        try
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            UbootLibPlugin.KitLog.LogInfo($"[kit] scene: '{scene.name}' (buildIndex={scene.buildIndex})");
            Hud.MarkForRebuild();
            ModsPanel.OnSceneChanged(scene.name);
            UbootLibPlugin.Events?.RaiseSceneLoaded(scene.name, scene.buildIndex);
        }
        catch (Exception e)
        {
            UbootLibPlugin.KitLog.LogWarning($"[kit] scene hook threw: {e.GetType().Name}: {e.Message}");
        }
    }
}

internal static class TickHook
{
    private static int _lastFrame = -1;

    public static void Prefix()
    {
        try
        {
            // Guard against multiple hooked Update methods ticking the same frame.
            if (Time.frameCount == _lastFrame) return;
            _lastFrame = Time.frameCount;
            ModLoader.Tick();
            Hud.Tick();
            ModsPanel.Tick();
            Net.ModNet.Tick();
        }
        catch (Exception e)
        {
            UbootLibPlugin.KitLog.LogError($"[kit] tick threw: {e.GetType().Name}: {e.Message}");
        }
    }
}

/// <summary>Content helpers for mods that need runtime assets without AssetBundles.</summary>
public static class Content
{
    /// <summary>Loads a PNG/JPG from disk into a Texture2D. Returns null on failure.</summary>
    public static Texture2D LoadTexture(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                UbootLibPlugin.KitLog.LogWarning($"[kit] texture not found: {path}");
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(tex, bytes))
            {
                UbootLibPlugin.KitLog.LogWarning($"[kit] unsupported image: {path}");
                return null;
            }

            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }
        catch (Exception e)
        {
            UbootLibPlugin.KitLog.LogError($"[kit] texture load failed ({path}): {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    public static Sprite ToSprite(Texture2D tex) =>
        tex == null ? null : Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
}
