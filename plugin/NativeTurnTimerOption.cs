using System;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using MiniJSON;
using UnityEngine;

internal static class NativeTurnTimerOption
{
    internal const string Id = "options.turn-timer";
    private static TurnTimerSettings settings;
    private static ManualLogSource log;
    internal static CheckBox Checkbox { get; private set; }
#if !SOLOSTONE_RELEASE
    private static readonly System.Reflection.FieldInfo inputBlocker = AccessTools.Field(typeof(OptionsMenu), "m_inputBlocker");
    internal static PegUIElement Dismiss => OptionsMenu.Get() == null ? null : inputBlocker?.GetValue(OptionsMenu.Get()) as PegUIElement;
#endif
    internal static bool Enabled => settings?.Enabled == true;
    private static string lastSound;
    private static int sounds;

    internal static void Initialize(Harmony harmony, ManualLogSource logger, ConfigFile file)
    {
        settings = new TurnTimerSettings(file); log = logger;
        harmony.Patch(AccessTools.Method(typeof(OptionsMenu), "Awake"),
            postfix: new HarmonyMethod(typeof(NativeTurnTimerOption), nameof(AddCheckbox)));
        harmony.Patch(AccessTools.Method(typeof(OptionsMenu), "Show"),
            postfix: new HarmonyMethod(typeof(NativeTurnTimerOption), nameof(Sync)));
        // Observe accepted native playback, never synthesize timer sounds. AudioModule is not in the reference subset.
        var play = AccessTools.GetDeclaredMethods(typeof(SoundManager)).Single(m => m.Name == "Play" && m.GetParameters().Length == 4);
        harmony.Patch(play, postfix: new HarmonyMethod(typeof(NativeTurnTimerOption), nameof(SoundPlayed)));
    }

    private static void AddCheckbox(OptionsMenu __instance)
    {
        try
        {
            var source = __instance.m_screenShakeCheckbox;
            var clone = UnityEngine.Object.Instantiate(source.gameObject, source.transform.parent, false);
            clone.name = "SoloStoneTurnTimerCheckbox";
            // Reuse the hidden spectator row, above Screen Shake; no extra rows or overlap with Handheld/Restore.
            clone.transform.position = __instance.m_spectatorOpenJoinCheckbox.transform.position;
            Checkbox = clone.GetComponent<CheckBox>();
            Checkbox.ClearEventListeners(); Checkbox.SetButtonText("Turn Timer");
            Checkbox.AddEventListener(UIEventType.RELEASE, _ => {
                try { settings.Set(Checkbox.IsChecked()); log.LogInfo("TURN_TIMER_OPTION enabled=" + Enabled + " applies=next-match"); }
                catch (Exception error) { log.LogWarning("TURN_TIMER_OPTION not saved: " + error.Message); }
                Sync();
            });
            clone.SetActive(true); Sync();
        }
        catch (Exception error) { log.LogWarning("TURN_TIMER_OPTION checkbox unavailable: " + error.Message); }
    }

    private static void Sync() { if (Checkbox != null) Checkbox.SetChecked(Enabled); }

    private static void SoundPlayed(object __0, bool __result)
    {
        if (!NativeTestControl.DiagnosticsEnabled || !__result) return;
        try
        {
            var source = __0 as Component;
            string name = source?.name ?? "";
            if (name.IndexOf("rope", StringComparison.OrdinalIgnoreCase) < 0
                && name.IndexOf("fuse", StringComparison.OrdinalIgnoreCase) < 0
                && name.IndexOf("turntimer", StringComparison.OrdinalIgnoreCase) < 0) return;
            lastSound = name; sounds++;
            log.LogInfo("NATIVE_TURN_TIMER_SFX accepted=true source=" + name);
        }
        catch (Exception error) { log.LogWarning("TURN_TIMER sound telemetry: " + error.Message); }
    }

#if !SOLOSTONE_RELEASE
    internal static JsonNode State(NativeMatch match)
    {
        var timer = TurnTimer.Get();
        return new JsonNode {
            { "enabledNextMatch", Enabled }, { "enabledThisMatch", match?.PlayerTurnTimerEnabled == true },
            { "nativeState", timer?.GetCurrentTurnTimerState().ToString() },
            { "ropeActive", timer != null && timer.IsRopeActive() },
            { "secondsRemaining", timer == null ? 0 : timer.ComputeCountdownRemainingSec() },
            { "soundCount", sounds }, { "lastSound", lastSound }
        };
    }
#endif
}
