using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

// "Handheld UI" (owner 2026-10-28): the Steam Deck layout work (mulligan row, larger native menu text) behind one native
// Options checkbox. Default ON when running as the Deck host, OFF elsewhere; persisted in the BepInEx config file
// ([Handheld UI] Enabled). Toggling takes effect immediately: the layout restores or applies on the next frame.
internal static class HandheldUi
{
    private static ManualLogSource log;
    private static ConfigFile config;
    private static ConfigEntry<bool> enabled;
    private static CheckBox checkbox;
    private static bool failed;
    internal static bool Enabled => !failed && enabled != null && enabled.Value;
    // The Options row other clones line up under (NativeStarterDecks); null when unavailable.
    internal static CheckBox Checkbox => checkbox;

    internal static void Initialize(Harmony harmony, ManualLogSource logger, ConfigFile file, OfflineLaunchContext launch)
    {
        log = logger;
        config = file;
        // The default only applies when the key is absent, so a player's own choice survives every restart.
        enabled = file.Bind("Handheld UI", "Enabled", launch.Deck,
            "Steam Deck layout: larger mulligan cards and menu text. Default on for the Steam Deck host, off elsewhere. Also in Options.");
        harmony.Patch(AccessTools.Method(typeof(OptionsMenu), "Awake") ?? throw new MissingMethodException("OptionsMenu", "Awake"),
            postfix: new HarmonyMethod(typeof(HandheldUi), nameof(AddCheckbox)));
        harmony.Patch(AccessTools.Method(typeof(OptionsMenu), "Show") ?? throw new MissingMethodException("OptionsMenu", "Show"),
            postfix: new HarmonyMethod(typeof(HandheldUi), nameof(SyncCheckbox)));
        DeckLayout.Initialize(harmony, logger);
    }

    internal static void Tick()
    {
        if (failed) return;
        DeckLayout.Tick(Enabled);
        DeckLayout.TickMenus(Enabled);
        PlaceCheckbox();
    }

    internal static void Fail(Exception error)
    {
        failed = true;
        try { DeckLayout.Restore(); DeckLayout.RestoreMenus(); } catch { }
        log?.LogWarning("HANDHELD_UI disabled for the session: " + error.Message);
    }

    internal static void Shutdown()
    {
        DeckLayout.Restore();
        DeckLayout.RestoreMenus();
        if (checkbox != null) UnityEngine.Object.Destroy(checkbox.gameObject);
        checkbox = null; template = null;
    }

    internal static void Set(bool value)
    {
        if (enabled == null || enabled.Value == value) return;
        // Save explicitly so a read-only config shows in the log instead of throwing out of the ConfigEntry setter.
        bool automatic = config.SaveOnConfigSet;
        try { config.SaveOnConfigSet = false; enabled.Value = value; }
        finally { config.SaveOnConfigSet = automatic; }
        try { config.Save(); log.LogInfo("HANDHELD_UI " + (value ? "on" : "off")); }
        catch (Exception error) { log.LogWarning("HANDHELD_UI setting NOT SAVED: " + error.Message); }
        if (!value) { DeckLayout.Restore(); DeckLayout.RestoreMenus(); }
    }

    private static CheckBox template;
    private static Vector3 step;
    private static bool placed;

    // A clone of the native "Disable Screen Shake" checkbox in the same parent, one native row step further along. The
    // native layout puts the (hidden) spectator checkbox one row before Screen Shake, so Screen Shake - Spectator is the
    // row step. The old Controls button was added to the pane without re-slicing and overlapped Screen Shake.
    private static void AddCheckbox(OptionsMenu __instance)
    {
        try
        {
            CheckBox source = __instance.m_screenShakeCheckbox, spectator = __instance.m_spectatorOpenJoinCheckbox;
            if (source == null || spectator == null) throw new InvalidOperationException("checkbox template missing");
            Vector3 row = source.transform.localPosition - spectator.transform.localPosition;
            if (row.sqrMagnitude < 1e-6f) throw new InvalidOperationException("no native checkbox row step");
            GameObject clone = UnityEngine.Object.Instantiate(source.gameObject, source.transform.parent, false);
            clone.name = "SoloStoneHandheldUiCheckbox";
            clone.transform.localPosition = source.transform.localPosition + row;
            CheckBox box = clone.GetComponent<CheckBox>();
            if (box == null) { UnityEngine.Object.Destroy(clone); throw new InvalidOperationException("clone has no checkbox"); }
            box.ClearEventListeners();
            box.SetButtonText("Handheld UI");
            box.SetChecked(Enabled);
            box.AddEventListener(UIEventType.RELEASE, _ => Set(box.IsChecked()));
            template = source; step = row; placed = false; checkbox = box;
        }
        catch (Exception error) { checkbox = null; log?.LogWarning("HANDHELD_UI Options checkbox unavailable: " + error.Message); }
    }

    // Once the menu has its final size, make sure the clone sits below Screen Shake (the row step may point either way).
    private static void PlaceCheckbox()
    {
        if (checkbox == null || template == null || placed) return;
        var options = OptionsMenu.Get();
        if (options == null || !options.IsShown()) return;
        Camera view = CameraUtils.FindFirstByLayerMask(1 << template.gameObject.layer);
        if (view == null) return;
        Rect shake = DeckLayout.ScreenRect(template.gameObject, view), mine = DeckLayout.ScreenRect(checkbox.gameObject, view);
        if (shake.height < 12 || mine.height < 12) return;
        placed = true;
        if (mine.center.y > shake.center.y)
        {
            checkbox.transform.localPosition = template.transform.localPosition - step;
            log?.LogInfo("HANDHELD_UI checkbox row step flipped");
        }
    }

    private static void SyncCheckbox()
    {
        try { if (checkbox != null) checkbox.SetChecked(Enabled); }
        catch (Exception error) { log?.LogWarning("HANDHELD_UI checkbox sync: " + error.Message); }
    }
}
