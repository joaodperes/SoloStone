using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

// Steam Deck host support that is not UI: the bounded frame trace and the native-dialog probe the save picker reads.
// The handheld layout lives in HandheldUi/DeckLayout. The Deck native controller input and navigation were removed on
// 2026-10-28 (touch and the trackpad mouse are the supported inputs); docs/design/deck-native-controls-archive.md
// says how to restore them.
internal static class DeckExperience
{
    private static ManualLogSource log;
    private static readonly DeckInputTraceWindow trace = new DeckInputTraceWindow();
    private static double TraceTime => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
    internal static bool Tracing => trace.Active;
    internal static double TraceClock => Tracing ? TraceTime : 0;
    internal static double TraceStage(string stage, double started)
    {
        if (!Tracing) return 0;
        double now = TraceTime;
        if (started > 0 && now - started >= .1)
        {
            Trace("stage=" + stage + " elapsedMs=" + ((now - started) * 1000).ToString("F3", CultureInfo.InvariantCulture));
            return TraceTime; // Keep this timing record's own logging cost out of the next stage.
        }
        return now;
    }
    internal static void Trace(string detail)
    {
        double now = TraceTime;
        if (!trace.Take(now)) return;
        try
        {
            log?.LogInfo("DECK_TRACE utc=" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                + " time=" + now.ToString("F6", CultureInfo.InvariantCulture) + " frame=" + Time.frameCount + " " + detail);
        }
        catch { } // Diagnostic sink failure must not change input handling.
    }
    private static readonly FieldInfo GameDialogCountField = AccessTools.Field(typeof(UniversalInputManager), "m_gameDialogActiveCount");
#if !SOLOSTONE_RELEASE
    private static readonly FieldInfo SystemDialogField = AccessTools.Field(typeof(UniversalInputManager), "m_systemDialogActive");
    private static readonly FieldInfo NotificationDialogField = AccessTools.Field(typeof(UniversalInputManager), "m_notificationDialogActive");
    private static readonly FieldInfo CurrentDialogField = AccessTools.Field(typeof(DialogManager), "m_currentDialog");
    private static readonly FieldInfo LoadingDialogField = AccessTools.Field(typeof(DialogManager), "m_loadingDialog");
#endif
    private static bool initialized;
    internal static bool GuardedDeck { get; private set; }
    internal static bool LayoutEnabled => HandheldUi.Enabled;
    internal static int GameDialogCount => UniversalInputManager.Get() == null ? -1 : (int)GameDialogCountField.GetValue(UniversalInputManager.Get());
#if !SOLOSTONE_RELEASE
    // Used by the save picker: true while exactly one native button list is open with no alert above it.
    internal static bool ProfileSelectorOwnsInput => ReadModalContext().OwnsSingleButtonList
        && UniversalInputManager.Get()?.IsTextInputActive() == false;
#endif
#if !SOLOSTONE_RELEASE
    private static DeckModalContext ReadModalContext()
    {
        var input = UniversalInputManager.Get();
        var dialogs = DialogManager.Get();
        var current = dialogs == null ? null : CurrentDialogField.GetValue(dialogs) as DialogBase;
        return new DeckModalContext(GameDialogCount, current == null ? null : current,
            dialogs == null || (bool)LoadingDialogField.GetValue(dialogs),
            input == null || (bool)SystemDialogField.GetValue(input),
            input == null || (bool)NotificationDialogField.GetValue(input));
    }
#endif

    internal static void Initialize(Harmony harmony, ManualLogSource logger, ConfigFile file, OfflineLaunchContext launch)
    {
        log = logger;
        GuardedDeck = launch.Deck;
        try
        {
            HandheldUi.Initialize(harmony, logger, file, launch);
            initialized = true;
            log.LogInfo("DECK guarded=" + GuardedDeck + " handheldUi=" + HandheldUi.Enabled);
        }
        catch (Exception error) { log.LogError("DECK initialization unavailable: " + error); }
    }

    internal static void Tick()
    {
        bool diagnostics = NativeTestControl.DiagnosticsEnabled;
        trace.Update(diagnostics, diagnostics ? TraceTime : 0);
        if (!initialized) return;
        try { HandheldUi.Tick(); }
        catch (Exception error) { HandheldUi.Fail(error); }
    }

    internal static void Shutdown()
    {
        initialized = false;
        try { HandheldUi.Shutdown(); }
        catch (Exception error) { log?.LogWarning("DECK shutdown: " + error.Message); }
    }
}
