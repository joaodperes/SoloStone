using System;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using MiniJSON;
using UnityEngine;

// Options checkbox for the experimental fair deck inference (#63; a clone of Disable Screen Shake). The right column is full: Restore
// Starter Decks takes the row under FPS Counter when shown. So it sits in the left column beside Fullscreen, its box from the middle of
// Frame Rate, and hides while a dropdown list is open over it, like the Pace plank. On, each
// world encounter begun afterwards searches with BrainOptions.ReplyPrior = Inferred (NpcWorldRuntime.Begin); a running match keeps what
// it began with. Node budgets and clocks do not change, and the inference reads public plays only, never a saved decklist.
// Every match logs NPC_REPLY_PRIOR and its journal header carries encounter.replyPrior, so a report says which mode ran.
internal static class NativeDeckInferenceOption
{
    private static DeckInferenceSettings settings;
    private static ManualLogSource log;
    private static bool placed, failed;
    private static long loggedReads;
    internal static CheckBox Checkbox { get; private set; }
    internal static CheckBox Shown => placed && Checkbox != null && Checkbox.gameObject.activeInHierarchy ? Checkbox : null;
    internal static bool Enabled => settings?.Enabled == true;
    internal static bool Checked => Checkbox != null && Checkbox.IsChecked();
    // What the active encounter's NPC searches with: "inferred" or "class" (also a legacy opponent, which has no encounter).
    internal static string ThisMatch => NpcWorldRuntime.Active?.Profile?.Budget.Brain?.ReplyPrior == ReplyPrior.Inferred ? "inferred" : "class";

    internal static void Initialize(Harmony harmony, ManualLogSource logger, ConfigFile file)
    {
        settings = new DeckInferenceSettings(file); log = logger;
        NpcWorldRuntime.InferredReplyPrior = () => Enabled;
        harmony.Patch(AccessTools.Method(typeof(OptionsMenu), "Awake") ?? throw new MissingMethodException("OptionsMenu", "Awake"),
            postfix: new HarmonyMethod(typeof(NativeDeckInferenceOption), nameof(AddCheckbox)));
        harmony.Patch(AccessTools.Method(typeof(OptionsMenu), "Show") ?? throw new MissingMethodException("OptionsMenu", "Show"),
            postfix: new HarmonyMethod(typeof(NativeDeckInferenceOption), nameof(Sync)));
    }

    // Native Options is destroyed on every close and re-created on open, so this runs per open.
    private static void AddCheckbox(OptionsMenu __instance)
    {
        try
        {
            CheckBox source = __instance.m_screenShakeCheckbox;
            if (source == null || __instance.m_spectatorOpenJoinCheckbox == null) throw new InvalidOperationException("checkbox template missing");
            GameObject clone = UnityEngine.Object.Instantiate(source.gameObject, __instance.transform, true);
            clone.name = NativeDeckInferenceText.ObjectName;
            CheckBox box = clone.GetComponent<CheckBox>();
            if (box == null) { UnityEngine.Object.Destroy(clone); throw new InvalidOperationException("clone has no checkbox"); }
            box.ClearEventListeners(); box.SetButtonText(NativeDeckInferenceText.Label);
            box.AddEventListener(UIEventType.RELEASE, _ => {
                try { settings.Set(box.IsChecked()); log.LogInfo("NPC_DECK_INFERENCE_OPTION enabled=" + Enabled + " applies=next-match"); }
                catch (Exception error) { log.LogWarning("NPC_DECK_INFERENCE_OPTION not saved: " + error.Message); }
                box.SetChecked(Enabled);
            });
            clone.SetActive(false);
            Checkbox = box; placed = false; failed = false;
        }
        catch (Exception error) { Checkbox = null; log?.LogWarning("NPC_DECK_INFERENCE_OPTION checkbox unavailable: " + error.Message); }
    }

    private static void Sync() { try { if (Checkbox != null) Checkbox.SetChecked(Enabled); } catch (Exception error) { log?.LogWarning("NPC_DECK_INFERENCE_OPTION sync: " + error.Message); } }

    // Called from OfflineProbe.Update, once per frame: places the row, and logs each new read on the main thread.
    internal static void Tick()
    {
        long reads = DeckInference.Reads;
        if (reads != loggedReads) { loggedReads = reads; log?.LogInfo("NPC_DECK_READ " + DeckInference.LastRead); }
        if (Checkbox == null || failed) return; // Unity null: also true once Options is destroyed.
        try
        {
            if (!placed) { Place(); return; }
            var options = OptionsMenu.Get();
            if (options == null) return;
            bool show = options.IsShown() && !NativeOptionsPace.ListOpen(options.m_graphicsRes, options.m_graphicsQuality, options.m_graphicsFps, options.m_signatureTextDisplayDropdown);
            if (Checkbox.gameObject.activeSelf != show) Checkbox.gameObject.SetActive(show);
        }
        catch (Exception error) { failed = true; Checkbox.gameObject.SetActive(false); log?.LogWarning("NPC_DECK_INFERENCE_OPTION placement: " + error.Message); }
    }

    private static void Place()
    {
        var options = OptionsMenu.Get();
        if (options == null || !options.IsShown()) return;
        CheckBox fullscreen = options.m_fullScreenCheckbox;
        Camera view = CameraUtils.FindFirstByLayerMask(1 << options.m_screenShakeCheckbox.gameObject.layer);
        if (view == null || DeckLayout.ScreenRect(options.m_screenShakeCheckbox.gameObject, view).height < 12) return;
        Vector3 right = view.transform.right, up = view.transform.up;
        Checkbox.transform.position = fullscreen.transform.position;
        // Rendered bounds, as Pace does: pivots differ between prefabs. Spans read inactive children too, so this holds if Fullscreen is hidden.
        if (!ProfileFormKit.Span(options.m_graphicsFps.gameObject, right, out float x0, out float x1) || !ProfileFormKit.Span(fullscreen.gameObject, up, out float y0, out float y1)
            || !ProfileFormKit.Span(Checkbox.gameObject, right, out float boxX, out float _) || !ProfileFormKit.Span(Checkbox.gameObject, up, out float boxY0, out float boxY1))
            throw new InvalidOperationException("no rendered bounds");
        Checkbox.transform.position += right * ((x0 + x1) / 2 - boxX) + up * ((y0 + y1) / 2 - (boxY0 + boxY1) / 2);
        Checkbox.gameObject.SetActive(true);
        Checkbox.SetChecked(Enabled);
        placed = true;
    }

#if !SOLOSTONE_RELEASE
    // Test control (state.deckInference): the setting, what this match runs, the inference's own counters (`queries` moves only
    // while an inferred search asks for its table) and the checkbox with every Options row it overlaps or leaves. The checkbox is meant
    // to sit inside the Graphics group's free band, so that group alone is left out of `overlaps`.
    internal static JsonNode State()
    {
        var box = new JsonNode { { "shown", Shown != null }, { "placed", placed } };
        var node = new JsonNode {
            { "enabledNextMatch", Enabled }, { "thisMatch", ThisMatch }, { "queries", DeckInference.Queries }, { "reads", DeckInference.Reads },
            { "lastRead", DeckInference.LastRead }, { "checkbox", box }
        };
        var options = OptionsMenu.Get();
        if (Shown == null || options == null) return node;
        try
        {
            box["checked"] = Checkbox.IsChecked();
            box["overlaps"] = TestControlProtocol.List(NativeFpsCounter.Overlaps(options, Checkbox, out bool measured).Where(row => row != "graphicsGroup").Cast<object>());
            box["measured"] = measured;
        }
        catch (Exception error) { box["error"] = error.Message; }
        return node;
    }
#endif
}
