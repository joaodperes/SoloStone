using System.Collections.Generic;
using System.Linq;

// Pure test-control rules with no Unity or protocol dependencies: compiled by the plugin and by the test projects.
internal static class TestControlRules
{
    // The unrevealed-entity audit may judge a client entity only when nothing is queued. An open choice is not a queue: at a
    // choice boundary with empty queues the choice is answerable, so a real disagreement must show there, not wait as "pending".
    internal static bool AuditDrained(IEnumerable<string> blockers) => blockers.All(blocker => blocker == "needsChoice");
    // Debounce for the unrevealed-entity verdict. "No client queue blocker" is not proof the client is current: QueueDispatcher
    // posts a host packet to the main thread, and HasGamePackets() only sees it once that post runs, so for about a frame the host is
    // ahead of the client while every blocker is empty. An entity is a real disagreement only when it was ALSO unrevealed on the
    // previous sampled frame and no packet was injected since (a pending packet always bumps the counter); until then it is pending.
    // A persistent disagreement therefore still shows on the second sampled frame. Reads inside one frame are one sample.
    internal sealed class UnrevealedConfirmation
    {
        private readonly HashSet<int> previous = new HashSet<int>(), current = new HashSet<int>();
        private string match; private int frame = int.MinValue; private long previousPackets = -1, packets = -1;
        // Once per view read, before the audit (also on reads that skip it, so a skipped frame forgets what it did not see).
        internal void Begin(string matchId, int frameCount, long injectedPackets)
        {
            if (matchId != match) { previous.Clear(); current.Clear(); match = matchId; frame = int.MinValue; packets = -1; }
            if (frameCount != frame)
            {
                previous.Clear(); previous.UnionWith(current); current.Clear();
                previousPackets = packets; frame = frameCount;
            }
            packets = injectedPackets;
        }
        // Records the entity as unrevealed on this frame; true when that disagreement has now persisted across two samples.
        internal bool Confirm(int entityId)
        {
            current.Add(entityId);
            return previous.Contains(entityId) && previousPackets == packets;
        }
    }
    // What the unrevealed audit may judge on a view, from its blockers: it runs on an empty queue or at a choice boundary, and only counts
    // an entity as a possible real disagreement when nothing but the open choice is queued (AuditDrained).
    internal static void PlanUnrevealedAudit(IList<string> blockers, bool choiceBoundary, out bool audit, out bool drained)
    {
        audit = blockers.Count == 0 || choiceBoundary;
        drained = AuditDrained(blockers);
    }
    // A drained view whose audit left an entity pending (seen once, not yet confirmed) is not settled: the host may be one in-flight packet ahead.
    internal static bool HoldsBackForPending(bool audited, bool drained, IEnumerable<string> unrevealed) =>
        audited && drained && unrevealed.Any(row => row.StartsWith("pending:"));
    // The effect of that hold on a view: a blocker (so readiness and the board-actor judgement wait a frame) and no choice readiness.
    internal static void HoldBack(IList<string> blockers, IList<string> actionBlockers, ref bool choiceReady, ref bool choiceBoundary)
    {
        blockers.Add("unrevealedPending"); actionBlockers.Add("unrevealedPending");
        choiceReady = false; choiceBoundary = false;
    }
    // The client's own record of the last mouse press, fed one sample per frame. `held` is the entity id of the card the client holds
    // (-1 none). A card already held when the button went down is not this press's pick-up: the baseline is the PREVIOUS frame's held
    // card (the client may pick the new card up before or after this runs on the press frame).
    internal sealed class PressRecord
    {
        internal bool Down;
        internal int Number, DownFrame, UpFrame, DownLosses, UpLosses, HeldEntity = -1;
        internal bool DownFocused, UpFocused;
        private int lastHeld = -1, baseline = -1;
        internal void Sample(bool down, int held, int frame, bool focused, int focusLosses)
        {
            bool edge = down != Down;
            if (edge)
            {
                Down = down;
                if (down) { Number++; DownFrame = frame; DownLosses = focusLosses; DownFocused = focused; UpFrame = -1; HeldEntity = -1; baseline = lastHeld; }
                else { UpFrame = frame; UpLosses = focusLosses; UpFocused = focused; }
            }
            // While the button is down, and on the frame it comes up (not the press frame itself).
            if (down != edge && HeldEntity < 0 && held > 0 && held != baseline) HeldEntity = held;
            lastHeld = held;
        }
    }
    // Where to press a crowded hand card. `own[column, row]` marks the samples (a grid over the card's stand-in collider on screen) where
    // the client's own pick lands on this card, not a neighbour's. The best cells have the most clearance (Chebyshev distance to the nearest
    // sample that is NOT this card, the grid edge counting as not this card), so a press survives a pixel of drift and the fan's overlap;
    // ties go to the cell nearest the card's own centroid. Later picks are at least 3 cells from earlier ones: a different point to try
    // when the first does not hover the right card. Each result is {column, row, clearance}; empty when the card is nowhere pickable.
    internal static List<int[]> BestAimCells(bool[,] own, int count)
    {
        int columns = own.GetLength(0), rows = own.GetLength(1);
        double sumX = 0, sumY = 0; int owned = 0;
        for (int x = 0; x < columns; x++) for (int y = 0; y < rows; y++) if (own[x, y]) { sumX += x; sumY += y; owned++; }
        var picks = new List<int[]>();
        if (owned == 0 || count <= 0) return picks;
        double centreX = sumX / owned, centreY = sumY / owned;
        var scored = new List<KeyValuePair<int[], double>>();
        for (int x = 0; x < columns; x++)
            for (int y = 0; y < rows; y++)
            {
                if (!own[x, y]) continue;
                int clearance = System.Math.Min(System.Math.Min(x + 1, columns - x), System.Math.Min(y + 1, rows - y));
                for (int ox = 0; ox < columns && clearance > 1; ox++)
                    for (int oy = 0; oy < rows; oy++)
                        if (!own[ox, oy]) clearance = System.Math.Min(clearance, System.Math.Max(System.Math.Abs(ox - x), System.Math.Abs(oy - y)));
                scored.Add(new KeyValuePair<int[], double>(new[] { x, y, clearance }, (x - centreX) * (x - centreX) + (y - centreY) * (y - centreY)));
            }
        foreach (var candidate in scored.OrderByDescending(entry => entry.Key[2]).ThenBy(entry => entry.Value))
        {
            if (picks.Any(pick => System.Math.Max(System.Math.Abs(pick[0] - candidate.Key[0]), System.Math.Abs(pick[1] - candidate.Key[1])) < 3)) continue;
            picks.Add(candidate.Key);
            if (picks.Count == count) break;
        }
        return picks;
    }
    // Client TAG_CARDTYPE values that take a board slot: MINION (4) and LOCATION (39). The client records a position only for those
    // drops (InputManager.DropHeldMinionLikeCard), and the host rejects one for any other source (NativeMatch.ValidateOption).
    internal static int OptionPosition(int cardType, int requested) => cardType == 4 || cardType == 39 ? requested : 0;
}
