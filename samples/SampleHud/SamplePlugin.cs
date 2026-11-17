using System;
using System.IO;
using UnityEngine;
using UbootLib;
using UbootLib.Api;

namespace SampleHud;

/// <summary>
/// P3 reference mod: manifest, assembly load, uGUI overlay, per-frame updates,
/// scene events and runtime texture loading — the whole kit chain in one file.
/// </summary>
public class SamplePlugin : ModBase
{
    private const string LabelId = "status";

    private int _updates;
    private int _scenes;
    private string _lastScene = "-";
    private Texture2D _icon;
    private HudLabel _label;
    private int _lastFps;
    private bool _showHud = true;

    public override void OnLoad(ModContext context)
    {
        base.OnLoad(context);
        Context.Log($"loaded from {context.Directory}");

        _icon = Content.LoadTexture(Path.Combine(context.Directory, "icon.png"));
        Context.Log(_icon != null
            ? $"texture loaded at runtime: {_icon.width}x{_icon.height}"
            : "no icon.png present");

        _showHud = Context.GetConfig("showHud", true);
        Context.Log($"config showHud={_showHud} (edit config/showHud.json while playing to hot-reload)");

        Context.GameEvents.SceneLoaded += (name, index) =>
        {
            _scenes++;
            _lastScene = name;
            Context.Log($"scene event: {name} (#{index})");
        };
    }

    public override void OnEnable() => Context.Log("enabled");

    public override void OnUpdate()
    {
        _updates++;
        EnsureLabel();

        // ~2 Hz text refresh; the HUD is rebuilt on every scene change.
        if (_updates % 30 != 0) return;

        _lastFps = Mathf.RoundToInt(1f / Mathf.Max(Time.smoothDeltaTime, 1e-4f));
        _label?.SetText(
            $"UbootLib 0.2.0   {_lastFps} fps\n" +
            $"scene: {_lastScene} ({_scenes} changes)\n" +
            $"updates: {_updates}   game: {Application.version}\n" +
            $"texture: {(_icon != null ? $"{_icon.width}x{_icon.height}" : "n/a")}");

        if (_updates % 600 == 0)
            Context.Log($"heartbeat: {_updates} updates, scene '{_lastScene}', fps {_lastFps}  {Hub.Summary()}");
    }

    public override void OnConfigChanged(string key)
    {
        if (key != "showHud") return;
        _showHud = Context.GetConfig("showHud", true);
        Hud.SetVisible(_showHud);
        Context.Log($"hot-reloaded showHud={_showHud}");
    }

    private void EnsureLabel()
    {
        _label = Context.Hud.Get(LabelId);
        if (_label != null) return;

        _label = Context.Hud.AddLabel(LabelId, new Vector2(-24, 96), 20, null, HudAnchor.BottomRight);
        Context.Log(_label != null ? "HUD label created" : "HUD label creation failed");
    }
}
