using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using MiniJSON;
using UnityEngine;

// Native Options plank "Pace: <tier>" that opens the Pace page (NativeProfileMenu.BuildPace); Back returns to Options the way
// native Sound Options does. Layout (OptionsPaceLayout): the free band under the left column's Signature Card Appearance
// dropdown, away from the right column's growing Other stack (Turn Timer, Screen Shake, Handheld UI, Restore Starter Decks, FPS Counter).
// The clone of the Miscellaneous plank hangs off the Options root rather than a pane: panes measure their slices over all
// child meshes, so a clone inside one would re-lay it. Stays hidden until measured, and while any left-column list is open: a
// list drops over what is below it, and its reach depends on the machine (Resolution lists every mode). Every path fails
// closed: nothing throws into Options.
internal static class NativeOptionsPace
{
    private static ManualLogSource log;
    private static UIBButton button;
    private static bool wanted, placed, failed;

    internal static UIBButton OptionsButton => button != null && button.gameObject.activeInHierarchy ? button : null;

    internal static void Initialize(Harmony harmony, ManualLogSource logger)
    {
        log = logger;
        harmony.Patch(AccessTools.Method(typeof(OptionsMenu), "Awake") ?? throw new MissingMethodException("OptionsMenu", "Awake"),
            postfix: new HarmonyMethod(typeof(NativeOptionsPace), nameof(AddButton)));
        harmony.Patch(AccessTools.Method(typeof(OptionsMenu), "Show") ?? throw new MissingMethodException("OptionsMenu", "Show"),
            postfix: new HarmonyMethod(typeof(NativeOptionsPace), nameof(SyncButton)));
    }

    private static bool Wanted() => NativeOptionsPaceText.Wanted(LocalLadder.Profile != null, NativeProfileSelector.InMatch, NativeAchievements.Active);

    internal static bool ListOpen(params DropdownControl[] lists)
    {
        foreach (var list in lists) if (list != null && list.isMenuShown()) return true;
        return false;
    }

    // Native Options is destroyed on every close and re-created on open, so this runs per open.
    private static void AddButton(OptionsMenu __instance)
    {
        try
        {
            UIBButton source = __instance.m_miscellaneousButton;
            if (source == null) throw new InvalidOperationException("Options plank template missing");
            GameObject clone = UnityEngine.Object.Instantiate(source.gameObject, __instance.transform, true);
            clone.name = NativeOptionsPaceText.ObjectName;
            UIBButton plank = clone.GetComponent<UIBButton>();
            if (plank == null) { UnityEngine.Object.Destroy(clone); throw new InvalidOperationException("clone has no plank"); }
            plank.ClearEventListeners();
            plank.AddEventListener(UIEventType.RELEASE, _ => Open());
            clone.SetActive(false);
            button = plank; wanted = placed = failed = false;
        }
        catch (Exception error) { button = null; log?.LogWarning("OPTIONS_PACE button unavailable: " + error.Message); }
    }

    private static void SyncButton()
    {
        try
        {
            if (button == null) return;
            wanted = Wanted();
            if (wanted) button.SetText(NativeOptionsPaceText.Label(LocalLadder.Profile.GrindTier.Name));
            else button.gameObject.SetActive(false);
        }
        catch (Exception error) { log?.LogWarning("OPTIONS_PACE sync: " + error.Message); }
    }

    internal static void Tick()
    {
        try
        {
            if (failed || !wanted || button == null) return;
            var options = OptionsMenu.Get();
            if (options == null || !options.IsShown()) return;
            if (!placed && !Place(options.m_signatureTextDisplayDropdown)) return;
            bool show = !ListOpen(options.m_graphicsRes, options.m_graphicsQuality, options.m_graphicsFps, options.m_signatureTextDisplayDropdown);
            if (button.gameObject.activeSelf != show) button.gameObject.SetActive(show);
        }
        catch (Exception error)
        {
            failed = true;
            if (button != null) button.gameObject.SetActive(false);
            log?.LogWarning("OPTIONS_PACE placement: " + error.Message);
        }
    }

    // Once per Options instance, in rendered bounds (pivots differ between prefabs). Everything hangs off the same root, so the
    // result holds through the Show punch and at any screen size: nothing here depends on the resolution.
    private static bool Place(DropdownControl anchor)
    {
        if (anchor == null) throw new InvalidOperationException("Signature dropdown missing");
        Camera view = CameraUtils.FindFirstByLayerMask(1 << anchor.gameObject.layer);
        if (view == null || DeckLayout.ScreenRect(anchor.gameObject, view).height < 12) return false; // Still punching in.
        Vector3 right = view.transform.right, up = view.transform.up;
        GameObject plank = button.gameObject;
        if (!ProfileFormKit.Span(anchor.gameObject, right, out float ax0, out float ax1) || !ProfileFormKit.Span(anchor.gameObject, up, out float ay0, out float ay1)
            || !ProfileFormKit.Span(plank, right, out float px0, out float px1) || !ProfileFormKit.Span(plank, up, out float py0, out float py1)) return false;
        DeckRect target = OptionsPaceLayout.Place(new DeckRect(ax0, ay0, ax1 - ax0, ay1 - ay0), px1 - px0, py1 - py0);
        ProfileFormKit.Center(plank, right, up, target.X + target.Width / 2, target.Y + target.Height / 2);
        placed = true;
        return true;
    }

#if !SOLOSTONE_RELEASE
    // Every Options row a new control must clear, by the name test control reports (also NativeFpsCounter's placement). Entries may be null.
    internal static List<KeyValuePair<string, Component>> Rows(OptionsMenu options) => new List<KeyValuePair<string, Component>> {
        Row("signature", options.m_signatureTextDisplayDropdown), Row("frameRate", options.m_graphicsFps), Row("fullscreen", options.m_fullScreenCheckbox),
        Row("resolution", options.m_graphicsRes), Row("quality", options.m_graphicsQuality), Row("graphicsGroup", options.m_graphicsGroup?.transform),
        Row("masterVolume", options.m_masterVolume), Row("musicVolume", options.m_musicVolume), Row("soundGroup", options.m_soundGroup?.transform),
        Row("soundOptions", options.m_soundOptionsButton), Row("otherPane", options.m_middleBottomRightPane), Row("versionLabel", VersionLabel.GetValue(options) as Component),
        Row("turnTimer", NativeTurnTimerOption.Checkbox), Row("screenShake", options.m_screenShakeCheckbox),
        Row("handheld", HandheldUi.Checkbox), Row("restoreStarters", NativeStarterDecks.OptionsButton), Row("fpsCounter", NativeFpsCounter.Checkbox),
        Row("deckInference", NativeDeckInferenceOption.Checkbox), Row("pace", OptionsButton)
    };
#endif
#if !SOLOSTONE_RELEASE
    // UberText's assembly is not referenced here, so the field is read by name.
    private static readonly System.Reflection.FieldInfo VersionLabel = AccessTools.Field(typeof(OptionsMenu), "m_versionLabel");
#endif
    private static KeyValuePair<string, Component> Row(string name, Component row) => new KeyValuePair<string, Component>(name, row);

#if !SOLOSTONE_RELEASE
    // Test control (save-catalog.paceOption): what is on screen now, and every Options row the plank overlaps.
    internal static JsonNode State()
    {
        var node = new JsonNode { { "shown", OptionsButton != null }, { "wanted", wanted }, { "placed", placed } };
        var options = OptionsMenu.Get();
        if (OptionsButton == null || options == null) return node;
        try
        {
            Camera view = CameraUtils.FindFirstByLayerMask(1 << button.gameObject.layer);
            if (view == null) return node;
            Rect mine = DeckLayout.ScreenRect(button.gameObject, view);
            node["rect"] = TestControlProtocol.List(new object[] { mine.x, mine.y, mine.width, mine.height });
            var overlaps = new JsonList();
            foreach (var row in Rows(options))
            {
                Component other = row.Value;
                if (other == null || other.gameObject == button.gameObject || !other.gameObject.activeInHierarchy) continue;
                Rect rect = DeckLayout.ScreenRect(other.gameObject, view);
                if (OptionsPaceLayout.Overlap(new DeckRect(mine.x, mine.y, mine.width, mine.height), new DeckRect(rect.x, rect.y, rect.width, rect.height))) overlaps.Add(row.Key);
            }
            node["overlaps"] = overlaps;
        }
        catch (Exception error) { node["error"] = error.Message; }
        return node;
    }
#endif

    private static void Open()
    {
        try
        {
            SyncButton();
            if (!wanted) return;
            OptionsMenu.Get()?.Hide(false); // No hide handler: that would destroy Options (native Sound Options does the same).
            NativeProfileSelector.OpenPace();
        }
        catch (Exception error) { log?.LogWarning("OPTIONS_PACE open: " + error.Message); }
    }
}
