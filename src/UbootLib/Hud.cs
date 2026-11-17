using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace UbootLib;

/// <summary>Screen corner a HUD element is anchored to.</summary>
public enum HudAnchor
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

/// <summary>
/// Screen-space uGUI overlay owned by the kit. IMGUI is unusable on this build
/// (see P2 notes), so all HUD drawing goes through a Canvas rebuilt per scene.
/// </summary>
public sealed class Hud
{
    private static GameObject _canvasGo;
    private static Font _font;
    private static readonly Dictionary<string, HudLabel> Labels = new();
    private static bool _pendingRebuild;
    private static bool _visible = true;

    /// <summary>Reference resolution used by the CanvasScaler.</summary>
    public static Vector2 ReferenceResolution { get; set; } = new(1920, 1080);

    public static bool IsAlive => _canvasGo != null;
    public static bool Visible => _visible;

    internal static void MarkForRebuild() => _pendingRebuild = true;

    /// <summary>Called once per frame from the kit pump; (re)creates the canvas when needed.</summary>
    internal static void Tick()
    {
        if (!_pendingRebuild && IsAlive) return;
        _pendingRebuild = false;
        Build();
    }

    private static void Build()
    {
        try
        {
            DestroyCanvas();
            Labels.Clear();

            var canvas = new GameObject("UbootLib.Hud");
            var c = canvas.AddComponent<Canvas>();
            c.renderMode = RenderMode.ScreenSpaceOverlay;
            c.sortingOrder = short.MaxValue;

            var scaler = canvas.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = ReferenceResolution;
            scaler.matchWidthOrHeight = 0.5f;

            _canvasGo = canvas;
            _canvasGo.SetActive(_visible);

            UbootLibPlugin.KitLog.LogInfo("[kit] HUD canvas created (uGUI)");
        }
        catch (Exception e)
        {
            UbootLibPlugin.KitLog.LogError($"[kit] HUD build failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void DestroyCanvas()
    {
        if (_canvasGo == null) return;
        try { UnityEngine.Object.Destroy(_canvasGo); } catch { /* ignore */ }
        _canvasGo = null;
    }

    /// <summary>Shared uGUI font (builtin Arial). Used by the kit's own panels as well.</summary>
    internal static Font GetFont()
    {
        if (_font != null) return _font;
        foreach (var name in new[] { "LegacyRuntime.ttf", "Arial.ttf" })
        {
            try
            {
                _font = Resources.GetBuiltinResource<Font>(name);
                if (_font != null)
                {
                    UbootLibPlugin.KitLog.LogInfo($"[kit] HUD font: builtin '{name}'");
                    return _font;
                }
            }
            catch (Exception e) { UbootLibPlugin.KitLog.LogWarning($"[kit] builtin font '{name}' failed: {e.Message}"); }
        }

        foreach (var family in new[] { "Microsoft YaHei", "Segoe UI" })
        {
            try
            {
                _font = Font.CreateDynamicFontFromOSFont(family, 16);
                if (_font != null)
                {
                    UbootLibPlugin.KitLog.LogInfo($"[kit] HUD font: OS '{family}'");
                    return _font;
                }
            }
            catch (Exception e) { UbootLibPlugin.KitLog.LogWarning($"[kit] OS font '{family}' failed: {e.Message}"); }
        }

        UbootLibPlugin.KitLog.LogError("[kit] HUD font unavailable — overlay will be invisible");
        return null;
    }

    public static void SetVisible(bool visible)
    {
        _visible = visible;
        if (_canvasGo != null) _canvasGo.SetActive(visible);
    }

    /// <summary>Adds a text label anchored to the top-left of the overlay.</summary>
    public HudLabel AddLabel(string id, Vector2 position, int fontSize = 18, Color? color = null, HudAnchor anchor = HudAnchor.TopLeft)
    {
        if (!IsAlive) Build();
        if (!IsAlive) return null;

        try
        {
            var go = new GameObject("label." + id);
            go.transform.SetParent(_canvasGo.transform, false);

            var (ax, ay) = anchor switch
            {
                HudAnchor.TopRight => (1f, 1f),
                HudAnchor.BottomLeft => (0f, 0f),
                HudAnchor.BottomRight => (1f, 0f),
                _ => (0f, 1f)
            };

            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(ax, ay);
            rt.pivot = new Vector2(ax, ay);
            rt.anchoredPosition = position;
            rt.sizeDelta = new Vector2(900, fontSize + 8);

            var text = go.AddComponent<Text>();
            text.font = GetFont();
            text.fontSize = fontSize;
            text.color = color ?? new Color(0.92f, 0.95f, 1f, 0.95f);
            text.alignment = anchor switch
            {
                HudAnchor.TopRight => TextAnchor.UpperRight,
                HudAnchor.BottomRight => TextAnchor.LowerRight,
                HudAnchor.BottomLeft => TextAnchor.LowerLeft,
                _ => TextAnchor.UpperLeft
            };
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            text.text = "";

            var label = new HudLabel(text, rt);
            Labels[id] = label;
            UbootLibPlugin.KitLog.LogInfo($"[kit] label '{id}' font={(text.font != null ? text.font.name : "null")} size={fontSize} canvas={( _canvasGo != null ? _canvasGo.activeInHierarchy.ToString() : "null")}");
            return label;
        }
        catch (Exception e)
        {
            UbootLibPlugin.KitLog.LogError($"[kit] AddLabel('{id}') failed: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    public HudLabel Get(string id) => Labels.TryGetValue(id, out var l) ? l : null;

    public bool SetText(string id, string value)
    {
        var label = Get(id);
        if (label == null) return false;
        label.SetText(value);
        return true;
    }

    public void Remove(string id)
    {
        if (!Labels.TryGetValue(id, out var label)) return;
        Labels.Remove(id);
        try { UnityEngine.Object.Destroy(label.Text.gameObject); } catch { /* ignore */ }
    }
}

/// <summary>A single overlay text element.</summary>
public sealed class HudLabel
{
    public Text Text { get; }
    public RectTransform Rect { get; }

    internal HudLabel(Text text, RectTransform rect)
    {
        Text = text;
        Rect = rect;
    }

    public void SetText(string value)
    {
        try
        {
            if (Text == null) return;
            Text.text = value;
            // Keep the rect exactly as tall as the text so bottom-anchored labels
            // never overflow past the screen edge.
            var h = Text.preferredHeight;
            if (h > 0) Rect.sizeDelta = new Vector2(Rect.sizeDelta.x, h);
        }
        catch (Exception e) { UbootLibPlugin.KitLog.LogWarning($"[kit] label update failed: {e.Message}"); }
    }

    public void SetColor(Color color)
    {
        try { if (Text != null) Text.color = color; } catch { /* ignore */ }
    }

    public void SetPosition(Vector2 anchoredPosition)
    {
        try { if (Rect != null) Rect.anchoredPosition = anchoredPosition; } catch { /* ignore */ }
    }
}
