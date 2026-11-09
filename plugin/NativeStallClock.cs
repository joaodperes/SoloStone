using System;

internal sealed class NativeStallClock
{
    private object controller, task;
    private string match, progress;
    private double since, last;
    private object claimedController, claimedTask;
    private string claimedMatch;
    internal double Elapsed(double now) => now - since;
    internal void Invalidate() => controller = null;
    internal bool Observe(bool eligible, string matchId, object owner, object list, string revision, double now, double deadline = 30)
    {
        if (!eligible || owner == null || list == null || string.IsNullOrEmpty(matchId)
            || double.IsNaN(now) || double.IsInfinity(now)) { controller = null; return false; }
        if (!ReferenceEquals(controller, owner) || !ReferenceEquals(task, list) || match != matchId || now < last
            || now - last > 2) // Sampling runs every .5s; gaps are not observed running time.
        { controller = owner; task = list; match = matchId; progress = revision; since = now; }
        else if (progress != revision) { progress = revision; since = now; }
        last = now;
        if (ReferenceEquals(claimedController, owner) && ReferenceEquals(claimedTask, list) && claimedMatch == matchId
            || now - since < deadline) return false;
        // Keep the claim through pause/choice/clock changes, even when recovery itself failed.
        claimedController = owner; claimedTask = list; claimedMatch = matchId;
        return true;
    }
}

