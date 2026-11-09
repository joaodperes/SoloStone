using System;
using System.Diagnostics;

// Startup profile: one STARTUP_TIMING line per Awake stage plus a total. Stopwatch-based, so BepInEx's batched disk
// log does not blur the numbers. Logging only; a failing sink never breaks startup.
internal static class StartupTiming
{
    private static readonly Stopwatch Clock = new Stopwatch();
    private static long last;
    private static Action<string> sink;

    internal static void Begin(Action<string> log, float unitySeconds)
    {
        sink = log;
        last = 0;
        Clock.Restart();
        Emit("STARTUP_TIMING stage=awake-entry unitySeconds=" + unitySeconds.ToString("0.000"));
    }

    internal static void Mark(string stage)
    {
        if (sink == null) return;
        long now = Clock.ElapsedMilliseconds;
        Emit("STARTUP_TIMING stage=" + stage + " ms=" + (now - last) + " at=" + now);
        last = now;
    }

    // Launch milestones after Awake (first frame, save picker, HUB), in ms since Awake began; each logs once.
    private static readonly System.Collections.Generic.HashSet<string> milestones = new System.Collections.Generic.HashSet<string>();
    internal static void Milestone(string name)
    {
        lock (milestones) if (!milestones.Add(name)) return;
        Emit("STARTUP_TIMING milestone=" + name + " sinceAwakeMs=" + Clock.ElapsedMilliseconds);
    }

    internal static void End()
    {
        if (sink == null) return;
        Mark("tail");
        Emit("STARTUP_TIMING total ms=" + Clock.ElapsedMilliseconds);
    }

    private static void Emit(string line)
    {
        try { sink?.Invoke(line); } catch (Exception) { }
    }
}
