using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace UbootLib;

/// <summary>
/// Main-menu mod manager: per-mod Enable/Disable buttons, list refresh and a manual
/// restart. It exists only in the main menu scene (SteamTestMain) and is driven by the
/// kit frame pump, because injected MonoBehaviours crash this build (see P2 notes).
///
/// Clicks are hit-tested by hand against RectTransforms instead of using Button
/// components: that keeps the UI free of EventSystem / UnityAction delegate
/// marshalling, which is the fragile part of IL2CPP interop. A full-screen
/// GraphicRaycaster-backed blocker still sits on top so the game's own EventSystem
/// cannot receive clicks meant for this panel.
/// </summary>
internal static class ModsPanel
{
    private const string MainMenuScene = "SteamTestMain";
    private const float PanelWidth = 780f;
    private const float Pad = 24f;
    private const float RowHeight = 46f;
    private const float HeaderTop = 108f;
    private const int MaxVisibleRows = 7;
    private const float ViewportWidth = PanelWidth - Pad * 2f;

    private static readonly Vector2 ReferenceResolution = new(1920, 1080);

    private static readonly Color PanelBg = new(0.055f, 0.07f, 0.10f, 0.97f);
    private static readonly Color TextPrimary = new(0.92f, 0.95f, 1f, 1f);
    private static readonly Color TextDim = new(0.58f, 0.65f, 0.74f, 1f);
    private static readonly Color TextHeader = new(0.45f, 0.53f, 0.63f, 1f);
    private static readonly Color TextWarn = new(0.98f, 0.72f, 0.30f, 1f);
    private static readonly Color TextOn = new(0.40f, 0.86f, 0.56f, 1f);
    private static readonly Color TextOff = new(0.55f, 0.61f, 0.69f, 1f);
    private static readonly Color TextFail = new(0.95f, 0.42f, 0.42f, 1f);
    private static readonly Color TextNew = new(0.45f, 0.80f, 0.97f, 1f);
    private static readonly Color RowEven = new(1f, 1f, 1f, 0.025f);
    private static readonly Color RowOdd = new(1f, 1f, 1f, 0.055f);
    private static readonly Color ButtonSlate = new(0.20f, 0.25f, 0.33f, 1f);
    private static readonly Color ButtonAccent = new(0.16f, 0.40f, 0.78f, 1f);
    private static readonly Color ButtonWarn = new(0.62f, 0.40f, 0.12f, 1f);
    private static readonly Color ButtonDanger = new(0.72f, 0.20f, 0.20f, 1f);
    private static readonly Color LauncherColor = new(0.12f, 0.24f, 0.42f, 0.92f);

    private sealed class UiButton
    {
        public RectTransform Rect;
        public Image Image;
        public Text Label;
        public Action Action;
        public Color Normal;
        public Color Hover;
        public Color Pressed;
        public bool ClipToViewport;
    }

    private sealed class Row
    {
        public ModManifest Manifest;
        public string Title;
        public string Subtitle;
        public string Version;
        public string State;
        public Color StateColor;
        public bool Pending;
        public bool EnabledOnDisk;
    }

    private static readonly List<UiButton> PanelButtons = new();

    private static GameObject _canvasGo;
    private static GameObject _panelRootGo;
    private static RectTransform _viewport;
    private static RectTransform _content;
    private static GameObject _blockerGo;
    private static GameObject _launcherGo;
    private static UiButton _launcher;
    private static Text _statusText;
    private static Text _netText;
    private static string _statusShown;
    private static string _netShown;
    private static float _contentHeight;
    private static float _viewportHeight;
    private static float _scroll;
    private static UiButton _pressed;
    private static bool _open;
    private static bool _isMainMenu;
    private static bool _sceneInited;
    private static bool _pendingScene;
    private static string _scene = "";
    private static string _appliedScene = "";
    private static bool _restartArmed;
    private static float _restartArmAt;
    private static string _toast;
    private static float _toastUntil;
    private static int _pendingCount;
    private static int _errorCount;

    // ------------------------------------------------------------------ lifecycle

    internal static void OnSceneChanged(string sceneName)
    {
        _scene = sceneName ?? "";
        _pendingScene = true;
    }

    public static void Tick()
    {
        try
        {
            if (!_sceneInited)
            {
                _sceneInited = true;
                _scene = SceneManager.GetActiveScene().name;
                ApplyScene();
                _appliedScene = _scene;
            }

            if (_pendingScene)
            {
                _pendingScene = false;
                if (_scene != _appliedScene)
                {
                    ApplyScene();
                    _appliedScene = _scene;
                }
            }

            if (!_isMainMenu) return;

            if (_canvasGo == null) EnsureCanvas();
            if (_canvasGo == null) return;

            if (Input.GetKeyDown(KeyCode.F9)) SetOpen(!_open);
            else if (_open && Input.GetKeyDown(KeyCode.Escape)) SetOpen(false);

            if (_restartArmed && Time.unscaledTime - _restartArmAt > 6f)
            {
                _restartArmed = false;
                BuildPanel();
            }

            HandlePointer();
            UpdateStatus();
            if (Time.frameCount % 30 == 0) UpdateNetLine();
        }
        catch (Exception e)
        {
            _errorCount++;
            if (_errorCount == 1 || _errorCount % 300 == 0)
                UbootLibPlugin.KitLog.LogError($"[mods] tick threw ({_errorCount}x): {e.GetType().Name}: {e.Message}");
        }
    }

    private static void ApplyScene()
    {
        _isMainMenu = string.Equals(_scene, MainMenuScene, StringComparison.OrdinalIgnoreCase);
        _open = false;
        _pressed = null;
        _restartArmed = false;
        _toast = null;
        DestroyCanvas();
        if (_isMainMenu)
        {
            EnsureCanvas();
            UbootLibPlugin.KitLog.LogInfo($"[mods] main menu detected ('{_scene}') - mod manager available");
        }
        else
        {
            UbootLibPlugin.KitLog.LogInfo($"[mods] scene '{_scene}' - mod manager hidden");
        }
    }

    private static void DestroyCanvas()
    {
        PanelButtons.Clear();
        _pressed = null;
        _statusText = null;
        _netText = null;
        _statusShown = null;
        _netShown = null;
        _viewport = null;
        _content = null;
        _panelRootGo = null;
        _blockerGo = null;
        _launcher = null;
        _launcherGo = null;

        if (_canvasGo != null)
        {
            try { UnityEngine.Object.Destroy(_canvasGo); } catch { /* ignore */ }
        }
        _canvasGo = null;
    }

    private static void EnsureCanvas()
    {
        if (_canvasGo != null) return;

        try
        {
            var go = new GameObject("UbootLib.ModsUI");
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MaxValue - 1;

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = ReferenceResolution;
            scaler.matchWidthOrHeight = 0.5f;

            go.AddComponent<GraphicRaycaster>();
            _canvasGo = go;

            BuildBlocker();
            BuildLauncher();
            BuildPanel();
            if (_blockerGo != null) _blockerGo.SetActive(_open);
            if (_launcherGo != null) _launcherGo.SetActive(!_open);
            UbootLibPlugin.KitLog.LogInfo("[mods] main-menu UI created");
        }
        catch (Exception e)
        {
            UbootLibPlugin.KitLog.LogError($"[mods] UI build failed: {e.GetType().Name}: {e.Message}");
            DestroyCanvas();
        }
    }

    // ------------------------------------------------------------------ scene UI

    private static void BuildBlocker()
    {
        var go = new GameObject("mods.blocker");
        go.transform.SetParent(_canvasGo.transform, false);

        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        var img = go.AddComponent<Image>();
        img.color = new Color(0f, 0f, 0f, 0.55f);
        img.raycastTarget = true;

        go.SetActive(false);
        _blockerGo = go;
    }

    private static void BuildLauncher()
    {
        var go = new GameObject("mods.launcher");
        go.transform.SetParent(_canvasGo.transform, false);

        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(1f, 1f);
        rt.anchoredPosition = new Vector2(-28f, -28f);
        rt.sizeDelta = new Vector2(132f, 46f);

        var img = go.AddComponent<Image>();
        img.color = LauncherColor;
        img.raycastTarget = true;

        var label = AddLabel(go.transform, "text", "MODS", 18, TextPrimary, TextAnchor.MiddleCenter, stretch: true);

        _launcher = new UiButton
        {
            Rect = rt,
            Image = img,
            Label = label,
            Normal = LauncherColor,
            Hover = Lighten(LauncherColor, 0.16f),
            Pressed = Lighten(LauncherColor, -0.18f),
            Action = () => SetOpen(true)
        };
        _launcherGo = go;
        UpdateLauncherLabel();
    }

    private static void BuildPanel()
    {
        if (_panelRootGo != null)
        {
            try { UnityEngine.Object.Destroy(_panelRootGo); } catch { /* ignore */ }
            _panelRootGo = null;
        }
        PanelButtons.Clear();
        _pressed = null;
        _statusText = null;
        _statusShown = null;
        _netText = null;
        _netShown = null;
        _viewport = null;
        _content = null;

        if (_canvasGo == null || !_open) return;

        var rows = BuildRows();
        _pendingCount = rows.Count(r => r.Pending);
        _viewportHeight = Mathf.Min(Mathf.Max(rows.Count, 1), MaxVisibleRows) * RowHeight;
        _contentHeight = rows.Count * RowHeight;
        var panelHeight = HeaderTop + _viewportHeight + 14f + 36f + 20f + 16f;

        var root = new GameObject("mods.panel");
        root.transform.SetParent(_canvasGo.transform, false);

        var rt = root.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(0f, -10f);
        rt.sizeDelta = new Vector2(PanelWidth, panelHeight);

        var bg = root.AddComponent<Image>();
        bg.color = PanelBg;
        bg.raycastTarget = true;

        _panelRootGo = root;

        AddLabel(root.transform, "title", "Mods", 26, TextPrimary, TextAnchor.MiddleLeft,
            x: Pad, y: -12f, w: 500f, h: 38f);
        AddLabel(root.transform, "subtitle",
            "Toggles are saved to Mods/modkit.disabled and apply after a restart.   F9 / Esc to close.",
            13, TextDim, TextAnchor.MiddleLeft,
            x: Pad, y: -50f, w: PanelWidth - Pad * 2f, h: 20f);

        AddPanelButton("close", "X", PanelWidth - Pad - 36f, -12f, 36f, 36f, ButtonSlate, () => SetOpen(false), 16);

        AddLabel(root.transform, "h.mod", "MOD", 12, TextHeader, TextAnchor.MiddleLeft,
            x: Pad + 12f, y: -82f, w: 320f, h: 18f);
        AddLabel(root.transform, "h.ver", "VERSION", 12, TextHeader, TextAnchor.MiddleLeft,
            x: Pad + 350f, y: -82f, w: 70f, h: 18f);
        AddLabel(root.transform, "h.state", "STATE", 12, TextHeader, TextAnchor.MiddleLeft,
            x: Pad + 424f, y: -82f, w: 150f, h: 18f);
        AddLabel(root.transform, "h.action", "ACTION", 12, TextHeader, TextAnchor.MiddleRight,
            x: Pad + 594f, y: -82f, w: 126f, h: 18f);

        var viewportGo = new GameObject("mods.viewport");
        viewportGo.transform.SetParent(root.transform, false);
        var vp = viewportGo.AddComponent<RectTransform>();
        vp.anchorMin = vp.anchorMax = new Vector2(0f, 1f);
        vp.pivot = new Vector2(0f, 1f);
        vp.anchoredPosition = new Vector2(Pad, -HeaderTop);
        vp.sizeDelta = new Vector2(ViewportWidth, _viewportHeight);
        viewportGo.AddComponent<RectMask2D>();
        _viewport = vp;

        var contentGo = new GameObject("mods.content");
        contentGo.transform.SetParent(viewportGo.transform, false);
        var content = contentGo.AddComponent<RectTransform>();
        content.anchorMin = content.anchorMax = new Vector2(0f, 1f);
        content.pivot = new Vector2(0f, 1f);
        content.anchoredPosition = new Vector2(0f, 0f);
        content.sizeDelta = new Vector2(ViewportWidth, Mathf.Max(_contentHeight, _viewportHeight));
        _content = content;

        if (rows.Count == 0)
        {
            AddLabel(viewportGo.transform, "empty",
                "No mods found. Put a folder with manifest.json into Mods/ and click Refresh List.",
                14, TextDim, TextAnchor.MiddleCenter, x: 0f, y: 0f, w: ViewportWidth, h: _viewportHeight);
        }
        else
        {
            for (var i = 0; i < rows.Count; i++) BuildRow(content, rows[i], i);
        }

        var footerY = -(HeaderTop + _viewportHeight + 14f);
        AddPanelButton("refresh", "Refresh List", Pad, footerY, 150f, 36f, ButtonSlate, Refresh, 14);
        AddPanelButton("restart", _restartArmed ? "Confirm Restart" : "Restart Game",
            Pad + 162f, footerY, 170f, 36f,
            _restartArmed ? ButtonDanger : ButtonWarn, ConfirmRestart, 14);

        _statusText = AddLabel(root.transform, "status", "", 13, TextDim, TextAnchor.MiddleLeft,
            x: Pad + 350f, y: footerY, w: PanelWidth - Pad * 2f - 350f, h: 36f);

        _netText = AddLabel(root.transform, "net", "", 11, TextDim, TextAnchor.MiddleLeft,
            x: Pad, y: footerY - 38f, w: PanelWidth - Pad * 2f, h: 16f);

        _scroll = Mathf.Min(_scroll, Mathf.Max(0f, _contentHeight - _viewportHeight));
        ApplyScroll();
        UpdateStatus(force: true);
        UpdateNetLine();
        UpdateLauncherLabel();
    }

    private static void BuildRow(RectTransform parent, Row row, int index)
    {
        var y = -index * RowHeight;

        var rowGo = new GameObject("row." + index);
        rowGo.transform.SetParent(parent, false);
        var rect = rowGo.AddComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(0f, y);
        rect.sizeDelta = new Vector2(ViewportWidth, RowHeight - 2f);

        var bg = rowGo.AddComponent<Image>();
        bg.color = index % 2 == 0 ? RowEven : RowOdd;
        bg.raycastTarget = false;

        AddLabel(rowGo.transform, "name", Clip(row.Title, 34), 16, TextPrimary, TextAnchor.MiddleLeft,
            x: 12f, y: -3f, w: 330f, h: 22f);
        AddLabel(rowGo.transform, "id", Clip(row.Subtitle, 44), 11, TextDim, TextAnchor.MiddleLeft,
            x: 12f, y: -23f, w: 330f, h: 16f);
        AddLabel(rowGo.transform, "ver", Clip(row.Version, 12), 13, TextDim, TextAnchor.MiddleLeft,
            x: 350f, y: 0f, w: 70f, h: RowHeight - 2f);
        AddLabel(rowGo.transform, "state", row.State, 13, row.StateColor, TextAnchor.MiddleLeft,
            x: 424f, y: 0f, w: 150f, h: RowHeight - 2f);

        var enabled = row.EnabledOnDisk;
        AddPanelButton("action." + index, enabled ? "Disable" : "Enable",
            x: 594f, y: -(RowHeight - 2f - 32f) * 0.5f, w: 126f, h: 32f,
            color: enabled ? ButtonSlate : ButtonAccent,
            action: () => ToggleMod(row.Manifest.Id, !enabled),
            fontSize: 14,
            parent: rowGo.transform,
            clip: true);
    }

    private static List<Row> BuildRows()
    {
        var rows = new List<Row>();

        foreach (var m in ModLoader.All)
        {
            var enabled = !ModLoader.IsDisabledOnDisk(m.Id);
            var loaded = ModLoader.IsLoaded(m.Id);
            var active = ModLoader.IsActive(m.Id);
            var known = ModLoader.WasKnownAtStartup(m.Id);
            var disabledAtStartup = ModLoader.WasDisabledAtStartup(m.Id);

            string state;
            Color color;
            var pending = false;

            if (!known)
            {
                if (enabled) { state = "NEW"; color = TextNew; pending = true; }
                else { state = "OFF"; color = TextOff; }
            }
            else if (active)
            {
                state = enabled ? "ON" : "ON*";
                color = enabled ? TextOn : TextWarn;
                pending = !enabled;
            }
            else if (loaded)
            {
                state = "FAILED";
                color = TextFail;
            }
            else if (!enabled)
            {
                state = "OFF";
                color = TextOff;
            }
            else if (disabledAtStartup)
            {
                state = "OFF*";
                color = TextWarn;
                pending = true;
            }
            else if (!string.IsNullOrEmpty(m.Failure))
            {
                state = "FAILED";
                color = TextFail;
            }
            else
            {
                state = "NOT LOADED";
                color = TextOff;
            }

            rows.Add(new Row
            {
                Manifest = m,
                Title = string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name,
                Subtitle = m.Id + (string.IsNullOrWhiteSpace(m.Author) ? "" : "  -  " + m.Author),
                Version = m.Version,
                State = state,
                StateColor = color,
                Pending = pending,
                EnabledOnDisk = enabled
            });
        }

        return rows;
    }

    // ------------------------------------------------------------------ actions

    private static void SetOpen(bool open)
    {
        if (!_isMainMenu || _canvasGo == null || _open == open) return;

        _open = open;
        _pressed = null;
        _restartArmed = false;
        _toast = null;

        if (_blockerGo != null) _blockerGo.SetActive(open);
        if (_launcherGo != null) _launcherGo.SetActive(!open);

        BuildPanel();
        if (!open) UpdateLauncherLabel();

        UbootLibPlugin.KitLog.LogInfo($"[mods] panel {(open ? "opened" : "closed")}");
    }

    private static void Refresh()
    {
        var count = ModLoader.Rescan();
        Toast($"List refreshed: {count} mod(s), {ModLoader.DisabledCount} disabled");
        BuildPanel();
        UbootLibPlugin.KitLog.LogInfo($"[mods] list refreshed: {count} mod(s), {ModLoader.DisabledCount} disabled");
    }

    private static void ToggleMod(string id, bool enable)
    {
        if (!ModLoader.SetEnabled(id, enable))
        {
            Toast("Mod not found: " + id);
            BuildPanel();
            return;
        }

        Toast($"'{id}' will be {(enable ? "enabled" : "disabled")} after restart");
        BuildPanel();
        var pending = _pendingCount;
        UbootLibPlugin.KitLog.LogInfo($"[mods] '{id}' -> {(enable ? "enabled" : "disabled")} on disk; {pending} change(s) pending restart");
    }

    private static void ConfirmRestart()
    {
        if (!_restartArmed)
        {
            _restartArmed = true;
            _restartArmAt = Time.unscaledTime;
            Toast("Click Restart again to confirm");
            BuildPanel();
            return;
        }

        _restartArmed = false;
        if (!GameRestart.TryRestart())
        {
            Toast("Restart failed - see BepInEx log");
            BuildPanel();
        }
    }

    private static void Toast(string message)
    {
        _toast = message;
        _toastUntil = Time.unscaledTime + 4f;
        _statusShown = null;
    }

    private static void UpdateStatus(bool force = false)
    {
        if (_statusText == null) return;

        string text;
        Color color;

        if (_restartArmed)
        {
            text = "Click Restart again to confirm (auto-cancels in 6s)";
            color = TextWarn;
        }
        else if (_toast != null && Time.unscaledTime < _toastUntil)
        {
            text = _toast;
            color = TextPrimary;
        }
        else if (_pendingCount > 0)
        {
            text = $"{_pendingCount} change(s) pending - restart to apply";
            color = TextWarn;
        }
        else
        {
            text = "All changes applied";
            color = TextDim;
        }

        if (!force && text == _statusShown) return;
        _statusShown = text;
        _statusText.text = text;
        _statusText.color = color;
    }

    private static void UpdateNetLine()
    {
        if (_netText == null) return;

        var text = $"net: {Net.ModNet.Status}   recv {Net.ModNet.Received}";
        var report = Net.ModNet.LastReport;
        if (!string.IsNullOrEmpty(report)) text += "   mismatch: " + Clip(report, 70);

        if (text == _netShown) return;
        _netShown = text;
        _netText.text = text;
    }

    private static void UpdateLauncherLabel()
    {
        if (_launcher == null || _launcher.Label == null) return;
        _launcher.Label.text = _pendingCount > 0 ? $"MODS ({_pendingCount})" : "MODS";
    }

    // ------------------------------------------------------------------ input

    private static void HandlePointer()
    {
        var mouse = (Vector2)Input.mousePosition;

        if (_open && _viewport != null && RectTransformUtility.RectangleContainsScreenPoint(_viewport, mouse, null))
        {
            var wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > 0.01f)
            {
                _scroll = Mathf.Clamp(_scroll - wheel * 48f, 0f, Mathf.Max(0f, _contentHeight - _viewportHeight));
                ApplyScroll();
            }
        }

        if (_pressed != null && _pressed.Rect == null) _pressed = null;

        var hovered = FindButton(mouse);
        if (Input.GetMouseButtonDown(0)) _pressed = hovered;

        UiButton clicked = null;
        if (Input.GetMouseButtonUp(0))
        {
            if (_pressed != null && _pressed == hovered) clicked = _pressed;
            _pressed = null;
        }

        if (_launcher != null && _launcherGo != null && _launcherGo.activeSelf && _launcher.Image != null)
            _launcher.Image.color = _launcher == _pressed ? _launcher.Pressed
                                  : _launcher == hovered ? _launcher.Hover
                                  : _launcher.Normal;

        foreach (var b in PanelButtons)
        {
            if (b.Image == null) continue;
            b.Image.color = b == _pressed ? b.Pressed : b == hovered ? b.Hover : b.Normal;
        }

        if (clicked == null) return;

        try
        {
            clicked.Action?.Invoke();
        }
        catch (Exception e)
        {
            UbootLibPlugin.KitLog.LogError($"[mods] button action failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static UiButton FindButton(Vector2 point)
    {
        if (!_open)
            return _launcher != null && Contains(_launcher, point) ? _launcher : null;

        UiButton found = null;
        foreach (var b in PanelButtons)
            if (Contains(b, point)) found = b;
        return found;
    }

    private static bool Contains(UiButton button, Vector2 point)
    {
        if (button?.Rect == null) return false;
        if (!RectTransformUtility.RectangleContainsScreenPoint(button.Rect, point, null)) return false;

        if (button.ClipToViewport &&
            (_viewport == null || !RectTransformUtility.RectangleContainsScreenPoint(_viewport, point, null)))
            return false;

        return true;
    }

    private static void ApplyScroll()
    {
        if (_content == null) return;
        _content.anchoredPosition = new Vector2(0f, _scroll);
    }

    // ------------------------------------------------------------------ UI helpers

    private static Text AddLabel(Transform parent, string name, string value, int size, Color color,
        TextAnchor align, float x = 0f, float y = 0f, float w = 100f, float h = 20f, bool stretch = false)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var rt = go.AddComponent<RectTransform>();
        if (stretch)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
        else
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(w, h);
        }

        var text = go.AddComponent<Text>();
        text.font = Hud.GetFont();
        text.fontSize = size;
        text.color = color;
        text.alignment = align;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false;
        text.text = value ?? "";
        return text;
    }

    private static UiButton AddPanelButton(string id, string text, float x, float y, float w, float h,
        Color color, Action action, int fontSize = 15, Transform parent = null, bool clip = false)
    {
        var go = new GameObject("btn." + id);
        go.transform.SetParent(parent != null ? parent : _panelRootGo.transform, false);

        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);

        var img = go.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = true;

        var label = AddLabel(go.transform, "text", text, fontSize, TextPrimary, TextAnchor.MiddleCenter, stretch: true);

        var button = new UiButton
        {
            Rect = rt,
            Image = img,
            Label = label,
            Action = action,
            Normal = color,
            Hover = Lighten(color, 0.16f),
            Pressed = Lighten(color, -0.18f),
            ClipToViewport = clip
        };
        PanelButtons.Add(button);
        return button;
    }

    private static Color Lighten(Color color, float amount) =>
        amount >= 0f ? Color.Lerp(color, Color.white, amount) : Color.Lerp(color, Color.black, -amount);

    private static string Clip(string value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= max ? value : value.Substring(0, Mathf.Max(0, max - 3)) + "...";
    }
}
