// Harness-only diagnostic switches (sim/AiHarness pilot variants golden-N-<variant>@1).
// Shipping code never sets them; every default reproduces the shipping golden brain.
internal static class GoldenAblation
{
    internal static bool NoReply, LegacyEval, NoReuse, MeanReply, ReplyHandCost, LegacyWeights, SoftVeto, NoHolds;
    // Paired mistake regression only: compare with the same brain before target filtering.
    internal static bool NoMistakeTargetGuard;
    internal static int OwnBeamWidth = -1; // > 0 overrides Budget.OwnBeamWidth
    // Test/A-B only: own-beam lines keep their game clones (the pre-2026-10-28 memory profile). Decisions are identical.
    internal static bool HoldLineStates;
    // Weight of DeckPlanEvaluator.ClearExposure. Off in shipping: weights 1 and 3 cost Dragon Warrior 9-11 points
    // paired over 45 games without helping another deck (2026-10-25 diagnosis, Round 2).
    internal static double ClearRisk = DefaultClearRisk;
    internal const double DefaultClearRisk = 0;
    internal static bool ClearRiskByLife;
    internal static int PlanNodesOverride = -1, LethalNodesOverride = -1;
    // Penalty per Timethief Rafaam (TIME_005) on the bot's board (DeckPlanEvaluator.EarlyTimethief). Shipped 100: paired over
    // 120 Rafaam games it cut early 10/10 slams 17 -> 2 and raised wins 52 -> 58 (docs/roadmap/2026-10-28-rafaam-decks.md).
    // 0 = the pre-rule brain (harness HS_AI_FABLED_HOLD=0).
    internal static double FabledHold = DefaultFabledHold;
    internal const double DefaultFabledHold = 100;
    // Live-runner late-lethal timing (2026-10-27 owner comparison, `hs ai late-lethal`). Shipped: per-turn late-lethal pool
    // and lethal-worker wall charged only while workers run. SharedDeadline: 1257a643 (no extension, worker wall charged for
    // the whole decision). DecisionFloor: the pre-1257a643 1.5 s per-decision floor. PoolOnly: the pool with 1257a643 charging.
    internal enum LateLethalTiming { Shipped, SharedDeadline, DecisionFloor, PoolOnly }
    internal static LateLethalTiming LateLethal = LateLethalTiming.Shipped;
    internal static void Reset() { LateLethal = LateLethalTiming.Shipped; NoReply = LegacyEval = NoReuse = MeanReply = ReplyHandCost = LegacyWeights = SoftVeto = NoHolds = ClearRiskByLife = NoMistakeTargetGuard = false; OwnBeamWidth = -1; ClearRisk = DefaultClearRisk; FabledHold = DefaultFabledHold; PlanNodesOverride = -1; LethalNodesOverride = -1; }
}
