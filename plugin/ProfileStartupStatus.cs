using System;
using System.IO;

// Status and ownership evidence that exists before the profile-dependent native bootstrap.
// Keep this Unity-free so the deferred-startup boundary is covered by ordinary unit tests.
internal static class ProfileStartupStatus
{
    internal const string BoundMarkerName = "save-bound.txt";

    // held: V1 and V2 have not passed, so nothing can be selected yet. Tools must not read that as an idle picker.
    internal static void WriteWaiting(string reports, string runId, string scene, bool transitioning, bool selectorOwned, bool held = false)
    {
        string utc = DateTime.UtcNow.ToString("o"), phase = held ? "ClientChecks" : "SaveSelection";
        File.WriteAllLines(Path.Combine(reports, "native-ui-state.txt"), new[] {
            "runId=" + runId, "utc=" + utc, "phase=" + phase, "ready=False",
            "profileBound=False", "saveSelection=" + (held ? "False" : "True"), "selectorOwned=" + selectorOwned,
            "scene=" + (scene ?? "none"), "transition=" + transitioning, "failure="
        });
        File.WriteAllLines(Path.Combine(reports, "ladder-state.txt"), new[] {
            "runId=" + runId, "utc=" + utc, "screen=" + phase, "matchId=", "pending=",
            "actionBusy=false", "error=", "profileBound=false"
        });
    }

    internal static void ClearBound(string reports)
    {
        string path = Path.Combine(reports, BoundMarkerName);
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
    }

    internal static void WriteBound(string reports, string runId, string slotId, string profilePath)
    {
        if (!Guid.TryParseExact(runId, "D", out _) || string.IsNullOrEmpty(slotId))
            throw new InvalidOperationException("Invalid bound-save identity.");
        string canonical = Path.GetFullPath(profilePath ?? throw new ArgumentNullException(nameof(profilePath)));
        if (canonical.IndexOfAny(new[] { '\r', '\n' }) >= 0 || slotId.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            throw new InvalidOperationException("Invalid bound-save marker value.");
        string path = Path.Combine(reports, BoundMarkerName), temporary = path + ".tmp";
        if (File.Exists(temporary)) File.Delete(temporary);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.WriteLine("schema=1"); writer.WriteLine("runId=" + runId); writer.WriteLine("slotId=" + slotId);
                writer.WriteLine("profilePath=" + canonical); writer.WriteLine("boundUtc=" + DateTime.UtcNow.ToString("o"));
                writer.Flush(); stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        catch { if (File.Exists(temporary)) File.Delete(temporary); throw; }
    }
}
