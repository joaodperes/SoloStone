using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

// Keeps test lanes (1-3) silent without touching the owner's lane 0 or any persisted Hearthstone options.
// Mechanism: the native option readers (Options.GetBoolImpl/GetFloatImpl) answer "sound off, every volume 0" for
// lanes 1-3 only, so the saved options file and registry are never written. On top of that Tick() silences Unity
// directly every few seconds: AudioListener.volume = 0 (music, ambience, VO and SFX all pass through it) and every
// AudioSource muted at volume 0, plus the manager's cached enable flags. It deliberately does NOT call the native
// UpdateAllMutes/UpdateAllCategoryVolumes: those read a SoundDef from each registered source and throw a
// NullReferenceException (IsCategoryEnabled(AudioSource)) for any source without one, so they cannot be trusted.
// Each step is guarded separately, so one failing step never skips the others. Lane 0 never installs a hook and
// never ticks: byte-identical behaviour. Nothing here may throw into Update.
internal static class LaneMute
{
    private const float RefreshSeconds = 2f;
    private static bool active;
    private static ManualLogSource log;
    private static float nextRefresh;
    private static bool reportedFailure;
    private static bool reportedApplied;
    private static FieldInfo masterEnabled, musicEnabled;
    // The audio module is not among the build references (only the game ships it), so Unity audio is reached by reflection.
    private static PropertyInfo listenerVolume, sourceMute, sourceVolume;
    private static Type audioSourceType;

    internal static bool Active { get { return active; } }

    internal static void Initialize(Harmony harmony, ManualLogSource logger, OfflineLaunchContext launch)
    {
        if (!LaneMutePolicy.ShouldMute(launch.LaneId)) return;
        log = logger;
        try
        {
            // A missing native member must never stop a test lane from launching; native-bindings pins these at build time.
            harmony.Patch(AccessTools.Method(typeof(Options), "GetBoolImpl", new[] { typeof(Option), typeof(bool).MakeByRefType() }),
                postfix: new HarmonyMethod(typeof(LaneMute), nameof(MuteBool)));
            harmony.Patch(AccessTools.Method(typeof(Options), "GetFloatImpl", new[] { typeof(Option), typeof(float).MakeByRefType() }),
                postfix: new HarmonyMethod(typeof(LaneMute), nameof(MuteFloat)));
        }
        catch (Exception error)
        {
            logger.LogWarning("LANE_MUTE hooks failed; this test copy is NOT muted: " + error);
            return;
        }
        masterEnabled = AccessTools.Field(typeof(SoundManager), "m_isMasterEnabled");
        musicEnabled = AccessTools.Field(typeof(SoundManager), "m_isMusicEnabled");
        active = true;
        logger.LogInfo("LANE_MUTE installed lane=" + launch.LaneId + "; all sound forced off for this test copy");
        SilenceListener();
    }

    private static void MuteBool(Option option, ref bool val)
    {
        if (LaneMutePolicy.ForcesBoolOff(option.ToString())) val = false;
    }

    private static void MuteFloat(Option option, ref float val)
    {
        if (LaneMutePolicy.ForcesFloatZero(option.ToString())) val = 0f;
    }

    internal static void Tick()
    {
        if (!active) return;
        try
        {
            if (Time.realtimeSinceStartup < nextRefresh) return;
            nextRefresh = Time.realtimeSinceStartup + RefreshSeconds;
            bool ok = SilenceListener();
            ok &= SilenceManagerFlags();
            ok &= SilenceSources();
            if (ok)
            {
                if (!reportedApplied) { reportedApplied = true; log.LogInfo("LANE_MUTE applied: listener volume 0, all audio sources muted"); }
            }
            else ReportFailure("a silencing step failed; retrying every " + RefreshSeconds + "s");
        }
        catch (Exception error)
        {
            ReportFailure(error.ToString());
        }
    }

    private static void ReportFailure(string detail)
    {
        if (reportedFailure) return;
        reportedFailure = true;
        log.LogWarning("LANE_MUTE refresh failed (option hooks still active): " + detail);
    }

    // Master output: nothing audible passes the listener at volume 0, whichever source or category plays it.
    private static bool SilenceListener()
    {
        try
        {
            if (!ResolveAudio()) { ReportFailure("UnityEngine.AudioModule types not found"); return false; }
            listenerVolume.SetValue(null, 0f, null);
            return true;
        }
        catch (Exception error) { ReportFailure("listener: " + error); return false; }
    }

    // The manager caches the enable options; reset the cached copies so its own mute logic agrees (cheap, field writes only).
    private static bool SilenceManagerFlags()
    {
        try
        {
            var manager = SoundManager.Get();
            if (manager == null || !manager.IsInitialized()) return true; // not up yet: the listener is already silent
            if (masterEnabled != null) masterEnabled.SetValue(manager, false);
            if (musicEnabled != null) musicEnabled.SetValue(manager, false);
            return true;
        }
        catch (Exception error) { ReportFailure("manager flags: " + error); return false; }
    }

    // Belt and braces for sources the manager never registered or that skip the listener: per-source, null-safe.
    private static bool SilenceSources()
    {
        bool ok = true;
        UnityEngine.Object[] sources;
        try
        {
            if (!ResolveAudio()) return false;
            sources = UnityEngine.Object.FindObjectsOfType(audioSourceType);
        }
        catch (Exception error) { ReportFailure("source scan: " + error); return false; }
        foreach (UnityEngine.Object source in sources)
        {
            try
            {
                if (source == null) continue;
                sourceMute.SetValue(source, true, null);
                sourceVolume.SetValue(source, 0f, null);
            }
            catch (Exception error) { ok = false; ReportFailure("source: " + error); }
        }
        return ok;
    }

    private static bool ResolveAudio()
    {
        if (listenerVolume != null && sourceMute != null && sourceVolume != null && audioSourceType != null) return true;
        const string module = ", UnityEngine.AudioModule";
        Type listener = Type.GetType("UnityEngine.AudioListener" + module);
        audioSourceType = Type.GetType("UnityEngine.AudioSource" + module);
        if (listener == null || audioSourceType == null) return false;
        listenerVolume = listener.GetProperty("volume", BindingFlags.Public | BindingFlags.Static);
        sourceMute = audioSourceType.GetProperty("mute", BindingFlags.Public | BindingFlags.Instance);
        sourceVolume = audioSourceType.GetProperty("volume", BindingFlags.Public | BindingFlags.Instance);
        return listenerVolume != null && sourceMute != null && sourceVolume != null;
    }
}
