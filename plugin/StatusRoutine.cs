using System;
using System.IO;

// Pure helpers for the 3-second status routine (OfflineProbe.Update). No Unity/game types, so the native-bindings tests compile this file.

/// <summary>Remembers the SHA-256 of the saved profile per file identity so the routine re-reads and re-hashes it only after it changed.</summary>
internal sealed class SavedHashCache
{
    // A file written this recently may still change within the clock's timestamp granularity without changing its identity.
    internal const double SettleSeconds = 2;
    private string identity, hash;
    internal int Computations { get; private set; }

    /// <param name="fileIdentity">Length + write/creation times of the saved file, null when unknown (never cached).</param>
    /// <param name="lastWriteUtc">Write time inside <paramref name="fileIdentity"/>.</param>
    internal string Get(string fileIdentity, DateTime lastWriteUtc, DateTime nowUtc, Func<string> compute)
    {
        if (fileIdentity != null && hash != null && fileIdentity == identity) return hash;
        string value = compute();
        Computations++;
        bool settled = fileIdentity != null && (nowUtc - lastWriteUtc).TotalSeconds >= SettleSeconds;
        identity = settled ? fileIdentity : null;
        hash = settled ? value : null;
        return value;
    }
}

internal static class StatusRoutine
{
#if !SOLOSTONE_INSTALLER || SOLOSTONE_TEST_HOOKS
    internal const string ScreenshotRequestFile = "screenshot-request.txt";
#endif
    internal const string ScreenshotFile = "client.png";
    internal const float ScreenshotPollSeconds = 0.5f;
    // Outside the pack/collection scenes the deep diagnostics refresh this rarely (cached lines carry deepScanAgeSeconds).
    internal const float DeepScanIdleSeconds = 30f;

    /// <summary>True (and advances <paramref name="next"/>) when a periodic action with the given interval is due.</summary>
    internal static bool Due(float now, ref float next, float interval)
    {
        if (now < next) return false;
        next = now + interval;
        return true;
    }

#if !SOLOSTONE_INSTALLER || SOLOSTONE_TEST_HOOKS
    /// <summary>
    /// Consumes a screenshot request file dropped by a tool. Returns true when a capture should be taken now.
    /// Capturing is on demand because a full-resolution PNG encode on the main thread is a visible hitch.
    /// </summary>
    internal static bool ConsumeScreenshotRequest(string reports)
    {
        try
        {
            string path = Path.Combine(reports, ScreenshotRequestFile);
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception) { return false; }
    }
#endif

    /// <summary>
    /// Deep scene scans (FindObjects scans, renderer visibility, deck rule validation) run every tick only where their fields matter
    /// (pack opening, collection, deck picker), on a phase or scene change, or rarely elsewhere.
    /// </summary>
    internal static bool DeepScanDue(string scene, string lastScanScene, bool forced, float now, float lastDeepScan, bool haveScan)
    {
        if (forced || !haveScan || scene != lastScanScene) return true;
        if (scene == "PACKOPENING" || scene == "COLLECTIONMANAGER" || scene == "TOURNAMENT") return true;
        return now - lastDeepScan >= DeepScanIdleSeconds;
    }
}
