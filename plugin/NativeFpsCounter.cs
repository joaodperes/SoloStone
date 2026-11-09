using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using MiniJSON;
using UnityEngine;

// Options "FPS Counter" checkbox (a clone of Disable Screen Shake, like Turn Timer and Handheld UI) and the overlay it shows: one
// IMGUI label in the top-right corner (FpsCounterLayout), directly under the always-on brand label (NativeBrandLabel), drawn from
// the plugin's OnGUI so it covers every scene, in a match too. It takes no input (a Label never does) and only reads Time.unscaledDeltaTime: no frame-rate, VSync or quality setting is
// touched. Off, Tick is one bool test and Draw returns at the top. The setting applies at once and persists in the BepInEx
// config ([Display] FpsCounter).
// Layout: the right column's Other group is Turn Timer (the hidden spectator row), Screen Shake, Handheld UI; this row is the
// next one down (RowsBelowShake), and Restore Starter Decks moves one row under it (NativeStarterDecks). Like the Pace plank the
// clone hangs off the Options root, not a pane: Show lays every pane out over all its child meshes, so a clone inside one would
// stretch or shift it. It stays hidden until the menu has its size, then takes one fixed position from the rows' rendered
// positions, so a wrong constant shows up in options-fps (overlap, outside the body) instead of silently moving.
// Every path fails closed: nothing throws into Options or the frame.
internal static class NativeFpsCounter
{
    private static FpsCounterSettings settings;
    private static ManualLogSource log;
    private static bool enabled, failed, placed;
    private static readonly FpsAverager average = new FpsAverager();
    internal static CheckBox Checkbox { get; private set; }
    // The checkbox once it is on screen (null before: it is placed after the menu has its size).
    internal static CheckBox Shown => placed && Checkbox != null && Checkbox.gameObject.activeInHierarchy ? Checkbox : null;
    internal static bool Enabled => enabled;
    internal static bool Checked => Checkbox != null && Checkbox.IsChecked();

    // Rows under Screen Shake along the native row step (spectator minus Screen Shake is one row up): Handheld UI is 1.
    internal const float RowsBelowShake = 2f;

    private static GUIStyle text, shadow;
    private static int styleSize;
    private static readonly GUIContent content = new GUIContent();
    private static bool labelReady;
    private static int labelFps = -1;
    private static Vector2 extent;
    private static FpsRect drawn;
    private static int drawnFrame = -100, drawnWidth, drawnHeight;

    internal static void Initialize(Harmony harmony, ManualLogSource logger, ConfigFile file)
    {
        settings = new FpsCounterSettings(file); log = logger; enabled = settings.Enabled;
        harmony.Patch(AccessTools.Method(typeof(OptionsMenu), "Awake") ?? throw new MissingMethodException("OptionsMenu", "Awake"),
            postfix: new HarmonyMethod(typeof(NativeFpsCounter), nameof(AddCheckbox)));
        harmony.Patch(AccessTools.Method(typeof(OptionsMenu), "Show") ?? throw new MissingMethodException("OptionsMenu", "Show"),
            postfix: new HarmonyMethod(typeof(NativeFpsCounter), nameof(Sync)));
    }

    // Native Options is destroyed on every close and re-created on open, so this runs per open.
    private static void AddCheckbox(OptionsMenu __instance)
    {
        try
        {
            CheckBox source = __instance.m_screenShakeCheckbox;
            if (source == null || __instance.m_spectatorOpenJoinCheckbox == null) throw new InvalidOperationException("checkbox template missing");
            GameObject clone = UnityEngine.Object.Instantiate(source.gameObject, __instance.transform, true);
            clone.name = NativeFpsCounterText.ObjectName;
            CheckBox box = clone.GetComponent<CheckBox>();
            if (box == null) { UnityEngine.Object.Destroy(clone); throw new InvalidOperationException("clone has no checkbox"); }
            box.ClearEventListeners(); box.SetButtonText(NativeFpsCounterText.Label);
            box.AddEventListener(UIEventType.RELEASE, _ => {
                try { settings.Set(box.IsChecked()); log.LogInfo("FPS_COUNTER_OPTION enabled=" + settings.Enabled + " applies=now"); }
                catch (Exception error) { log.LogWarning("FPS_COUNTER_OPTION not saved: " + error.Message); }
                Apply(box);
            });
            clone.SetActive(false);
            Checkbox = box; placed = false;
        }
        catch (Exception error) { Checkbox = null; log?.LogWarning("FPS_COUNTER_OPTION checkbox unavailable: " + error.Message); }
    }

    private static void Apply(CheckBox box)
    {
        bool now = settings.Enabled;
        if (now != enabled) average.Reset();
        enabled = now;
        if (box != null) box.SetChecked(now);
    }

    private static void Sync() { try { if (Checkbox != null) Apply(Checkbox); } catch (Exception error) { log?.LogWarning("FPS_COUNTER_OPTION sync: " + error.Message); } }

    // Called from OfflineProbe.Update, once per frame.
    internal static void Tick()
    {
        if (enabled) average.Add(Time.unscaledDeltaTime);
        if (Checkbox == null || placed) return; // Unity null: also true once Options is destroyed.
        try { Place(); }
        catch (Exception error) { placed = true; log?.LogWarning("FPS_COUNTER_OPTION placement: " + error.Message); }
    }

    // Once per Options instance, when Screen Shake has its rendered size (the menu is past its Show punch): the row under Handheld UI,
    // from Screen Shake and the spectator row (one row up). World positions of siblings under one rigid root, so the result holds
    // through the punch and at any resolution.
    private static void Place()
    {
        var options = OptionsMenu.Get();
        if (options == null || !options.IsShown()) return;
        CheckBox shake = options.m_screenShakeCheckbox, spectator = options.m_spectatorOpenJoinCheckbox;
        Camera view = CameraUtils.FindFirstByLayerMask(1 << shake.gameObject.layer);
        if (view == null || DeckLayout.ScreenRect(shake.gameObject, view).height < 12) return;
        Vector3 row = shake.transform.position - spectator.transform.position;
        if (row.sqrMagnitude < 1e-8f) throw new InvalidOperationException("no native checkbox row step");
        Checkbox.transform.position = shake.transform.position + row * RowsBelowShake;
        Checkbox.gameObject.SetActive(true);
        Apply(Checkbox);
        placed = true;
    }

#if !SOLOSTONE_RELEASE
    // Every other visible Options row the checkbox overlaps in rendered bounds (a tenth of each row's height is slack: native
    // rows' bounds include their own glow), plus "outsideBody" when it is not inside the dialog body: the rendered union of
    // the Options meshes (frame included, the full-screen blocker not), inset by the frame's thickness.
    internal static List<string> Overlaps(OptionsMenu options, out bool measured) => Overlaps(options, Checkbox, out measured);
#endif
#if !SOLOSTONE_RELEASE
    internal static List<string> Overlaps(OptionsMenu options, CheckBox box, out bool measured)
    {
        var names = new List<string>(); measured = false;
        Camera view = CameraUtils.FindFirstByLayerMask(1 << box.gameObject.layer);
        if (view == null) return names;
        Rect mine = DeckLayout.ScreenRect(box.gameObject, view);
        if (mine.height < 12) return names;
        measured = true;
        DeckRect own = Core(mine);
        foreach (var row in NativeOptionsPace.Rows(options))
        {
            Component other = row.Value;
            if (other == null || other.gameObject == box.gameObject || !other.gameObject.activeInHierarchy) continue;
            Rect rect = DeckLayout.ScreenRect(other.gameObject, view);
            if (rect.width > 0 && OptionsPaceLayout.Overlap(own, Core(rect))) names.Add(row.Key);
        }
        if (!Body(options, box, view, out Rect body) || mine.xMin < body.xMin || mine.xMax > body.xMax || mine.yMin < body.yMin || mine.yMax > body.yMax) names.Add("outsideBody");
        return names;
    }
#endif

    private static bool Body(OptionsMenu options, CheckBox box, Camera view, out Rect body)
    {
        body = Rect.zero; bool any = false;
        float screen = Screen.width * (float)Screen.height;
        foreach (Renderer renderer in options.GetComponentsInChildren<Renderer>(false))
        {
            if (!renderer.enabled || renderer.transform.IsChildOf(box.transform)) continue;
            Rect one = DeckLayout.Project(renderer.bounds, view);
            if (one.width * one.height > .9f * screen) continue;
            body = any ? Rect.MinMaxRect(Mathf.Min(body.xMin, one.xMin), Mathf.Min(body.yMin, one.yMin), Mathf.Max(body.xMax, one.xMax), Mathf.Max(body.yMax, one.yMax)) : one;
            any = true;
        }
        if (!any) return false;
        float dx = body.width * .04f, dy = body.height * .07f;
        body = Rect.MinMaxRect(body.xMin + dx, body.yMin + dy, body.xMax - dx, body.yMax - dy);
        return true;
    }

    private static DeckRect Core(Rect rect) => new DeckRect(rect.x, rect.y + rect.height * .1f, rect.width, rect.height * .8f);

    // Called from OfflineProbe.OnGUI. Repaint only: Layout and input events never draw.
    internal static void Draw()
    {
        if (!enabled || failed || Event.current.type != EventType.Repaint) return;
        try
        {
            int size = FpsCounterLayout.FontSize(Screen.height);
            if (text == null || styleSize != size)
            {
                text = new GUIStyle(GUI.skin.label) { fontSize = size, wordWrap = false, clipping = TextClipping.Overflow };
                text.padding = new RectOffset(0, 0, 0, 0);
                text.normal.textColor = new Color(1f, 1f, 1f, .92f);
                shadow = new GUIStyle(text);
                shadow.normal.textColor = new Color(0f, 0f, 0f, .85f);
                styleSize = size; extent = Vector2.zero;
            }
            // The string and the native text layout change only when the reading does (twice a second), not on every repaint.
            if (average.Ready != labelReady || average.Fps != labelFps || extent.x <= 0)
            {
                labelReady = average.Ready; labelFps = average.Fps;
                content.text = NativeFpsCounterText.Overlay(labelReady, labelFps);
                extent = text.CalcSize(content);
            }
            FpsRect place = FpsCounterLayout.PlaceFps(Screen.width, Screen.height, NativeBrandLabel.PlacedThisFrame, extent.x, extent.y);
            var area = new Rect(place.X, place.Y, place.Width, place.Height);
            float offset = Mathf.Max(1f, size / 14f);
            GUI.Label(new Rect(area.x + offset, area.y + offset, area.width, area.height), content, shadow);
            GUI.Label(area, content, text);
            drawn = place; drawnFrame = Time.frameCount; drawnWidth = Screen.width; drawnHeight = Screen.height;
        }
        catch (Exception error)
        {
            failed = true; // One warning, not one per frame; the setting stays what the player chose.
            log?.LogWarning("FPS_COUNTER overlay disabled for the session: " + error.Message);
        }
    }

#if !SOLOSTONE_RELEASE
    // Test control (state.fpsCounter): the setting; `drawn`: a repaint in the last frames issued the label's draw call (the call
    // returned: not proof of pixels, so look at a screenshot) and where (normalized 1280x720, top-left origin, like every ui
    // rect); and the checkbox with `checked` (the widget's own state) and every Options row it overlaps or leaves.
    internal static JsonNode State()
    {
        int width = Screen.width, height = Screen.height;
        bool issued = enabled && !failed && Time.frameCount - drawnFrame <= 2 && drawnWidth == width && drawnHeight == height
            && FpsCounterLayout.OnScreen(drawn, width, height);
        var node = new JsonNode {
            { "enabled", enabled }, { "failed", failed }, { "drawn", issued }, { "fps", enabled && average.Ready ? average.Fps : 0 },
            { "screen", TestControlProtocol.List(new object[] { (long)width, (long)height }) }
        };
        node["screenRect"] = !issued ? null : TestControlProtocol.List(FpsCounterLayout.Normalized(drawn, width, height).Select(value => (object)Math.Round((double)value, 1)));
        var box = new JsonNode { { "shown", Shown != null }, { "placed", placed } };
        node["checkbox"] = box;
        var options = OptionsMenu.Get();
        if (Shown == null || options == null) return node;
        try
        {
            box["checked"] = Checkbox.IsChecked();
            box["overlaps"] = TestControlProtocol.List(Overlaps(options, out bool measured).Cast<object>());
            box["measured"] = measured;
        }
        catch (Exception error) { box["error"] = error.Message; }
        return node;
    }
#endif
}
