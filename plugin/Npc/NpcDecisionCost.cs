using System.Collections.Generic;

// Outlier thresholds for one NPC search decision, shared by the live NPC log and the harness cost reports
// (docs/agent-workflows/npc-decision-cost.md). After the 2026-10-28 memory fix elite decisions stay near 10 s, a few
// hundred MB and lines far below TurnBeam.MaxLineActions; the endless Farseer Wo loop before it took 1,479 s, a 45 GB
// heap and a 5,848-action line. Telemetry only: nothing here changes a search.
public static class NpcDecisionCost
{
    public const double OutlierSeconds = 60, OutlierMegabytes = 1024;
    public const int OutlierDepth = 48;
    // Comma-separated reasons ("time", "heap", "depth"), or null when the decision is not an outlier. Megabytes is the
    // peak managed heap in the harness and the heap growth over the decision in the live client (its own heap is large).
    public static string Flags(double seconds, double megabytes, int deepest)
    {
        var flags = new List<string>();
        if (seconds >= OutlierSeconds) flags.Add("time");
        if (megabytes >= OutlierMegabytes) flags.Add("heap");
        if (deepest >= OutlierDepth) flags.Add("depth");
        return flags.Count == 0 ? null : string.Join(",", flags.ToArray());
    }
}
