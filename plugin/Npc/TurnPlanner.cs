using System;
using System.Collections.Generic;
using System.Linq;
using SabberStoneCore.Enums;
using SabberStoneCore.Auras;
using SabberStoneCore.Model;
using SabberStoneCore.Tasks.PlayerTasks;

public sealed class PlannedTurn
{
    public IReadOnlyList<BotAction> Line { get; internal set; } = new BotAction[0];
    public double Value { get; internal set; }
    public int Nodes { get; internal set; }
    public bool Interrupted { get; internal set; }
    public SearchStop Stop { get; internal set; }
    public IReadOnlyList<KeyValuePair<BotAction, double>> Candidates { get; internal set; } = new KeyValuePair<BotAction, double>[0];
}
internal sealed class TurnLine
{
    // Own-beam lines release their game clone once scored and keep only the summaries that ranking reads: a late,
    // wide elite turn holds thousands of scored children (one full engine clone each, GBs; docs/roadmap/
    // 2026-10-28-elite-search-memory.md). A released line re-applies its last action to its parent on first use;
    // Apply is deterministic, and an expanded line re-reads its options exactly as its original did.
    private FairBotState state;
    private TurnLine parent;
    // The line this one extends (own beam only); kept so a released line can rebuild its clone.
    internal TurnLine Origin;
    private string effectsKey, combatKey, attacksKey, survivorsKey, combatOutcomeKey;
    private int[] handIds;
    private string handCommitment;
    internal string HandCommitment => handCommitment ?? (handCommitment = TurnPlannerJob.HandCommitment(
        HandIds, TurnPlannerJob.HandKinds(State.Facts)));
    private int boardCount = -1;
    internal bool Expanded, Completed, UnverifiedWin, UncertifiedEndTurnWin;
    internal bool PublicBoardSafe;
    internal FairBotState State
    {
        get { while (!RebuildStep()) { } return state; }
        set { state = value; parent = null; pending = null; partial = null; }
    }
    private Stack<TurnLine> pending;
    private FairBotState partial;
    // One Apply per call, true once State is held (State loops it; a finalist's chain is at most MaxLineActions long).
    // Released ancestors are rebuilt transiently from the nearest line that still holds a clone (ultimately the root
    // view): a deep finalist holds one clone, not its chain. This relies on Apply, Options and Facts reads leaving the
    // parent's game unchanged; the paired replay fingerprints of 2026-10-28 cover it.
    internal bool RebuildStep()
    {
        if (state != null || parent == null) return true;
        if (pending == null) {
            pending = new Stack<TurnLine>(); var at = this;
            while (at.state == null) { pending.Push(at); at = at.parent; }
            partial = at.state;
        }
        var line = pending.Peek();
        var action = line.Actions[line.Actions.Count - 1];
        // Match initial expansion: END_TURN is a sentinel; endpoint/reply scoring advances the turn.
        var applied = action.Type == PlayerTaskType.END_TURN ? partial : partial.Apply(action);
        if (line.Expanded) _ = applied.OrderedActions;
        partial = applied; pending.Pop();
        if (pending.Count > 0) return false;
        state = partial; partial = null; pending = null; parent = null;
        return true;
    }
    internal bool Holds => state != null;
    internal int RebuildNodes {
        get {
            if (pending != null) return pending.Count;
            int count = 0;
            for (var at = this; at.state == null && at.parent != null; at = at.parent) count++;
            return count;
        }
    }
    private string publicKey;
    // Elite+ dedupe key; a released line keeps it so ranking never rebuilds clones just to compare them.
    internal string PublicKey => publicKey ?? (publicKey = GoldenPublicKey.For(State));
    internal string EffectsKey => effectsKey ?? (effectsKey = State.OwnVisibleEffectsKey());
    // Partial trades can lose static value before another attacker finishes the exchange. Keep
    // different remaining attack resources and enemy damage/shields in the diversity frontier.
    internal string SurvivorsKey => survivorsKey ?? (survivorsKey = string.Join(",", State.Facts.Opponent.Board.Select(c => c.EntityId)));
    internal string AttacksKey {
        get {
            if (attacksKey != null) return attacksKey;
            var f = State.Facts;
            var own = f.Own.Board.Concat(new[] { f.Own.Hero }).ToDictionary(c => c.EntityId);
            return attacksKey = string.Join(",", State.Actions.Where(IsAttack).Select(a => a.Source).Distinct().OrderBy(id => id)
                .Select(id => id + ":" + own[id].Attack + ":" + (own[id].AttackLimit - own[id].AttacksThisTurn)));
        }
    }
    // Protect distinct kills and intercepting blockers; ordinary body changes use the normal reply slots.
    internal string CombatOutcomeKey => combatOutcomeKey ?? (combatOutcomeKey = SurvivorsKey + "|blockers:"
        + string.Join(",", State.Facts.Own.Board.Where(c => c.Taunt && !c.Stealth && !c.Untouchable && !c.Immune).Select(c => c.CardId + ":" + c.Attack + ":" + c.Health + ":" + c.DivineShield)));
    internal string CombatKey {
        get {
            if (combatKey != null) return combatKey;
            var f = State.Facts;
            return combatKey = EffectsKey + "|attacks:" + AttacksKey + "|survivors:" + SurvivorsKey + "|targets:"
                + string.Join(",", f.Opponent.Board.Select(c => c.EntityId + ":" + c.Health + ":" + c.DivineShield));
        }
    }
    internal static bool IsAttack(BotAction a) => a.Type == PlayerTaskType.MINION_ATTACK || a.Type == PlayerTaskType.HERO_ATTACK;
    internal int[] HandIds => handIds ?? (handIds = State.Facts.Own.Hand.Select(c => c.EntityId).ToArray());
    internal int BoardCount => boardCount >= 0 ? boardCount : (boardCount = State.Facts.Own.Board.Count);
    internal void Release(bool keepPublicKey = false, bool keepCombatKey = false)
    {
        if (state == null || Origin == null) return;
        _ = EffectsKey; _ = HandIds; boardCount = BoardCount;
        if (keepPublicKey) _ = PublicKey;
        if (keepCombatKey) { _ = CombatKey; _ = CombatOutcomeKey; _ = HandCommitment; }
        state = null; parent = Origin;
    }
    internal List<BotAction> Actions = new List<BotAction>();
    internal double Value, Preference;
    internal double PublicAttackMargin, PublicAttackDamage;
    internal double? Survival;
    internal double Resistance;
    internal long FaceDamage;
    internal int UnusedResources;
    // face-tiebreak (0 when the flag is OFF): enemy-hero damage since the decision root, before end-of-turn effects, minus a
    // dominant penalty per decision-root friendly minion no longer on board. Tie-break only, after Value and Preference: an
    // exactly tied face hit (e.g. healed back by an own end-of-turn heal) beats the shorter passive line instead of losing
    // on Actions.Count. Relative to the root and never zeroed by a loss, so continuations of one prefix order the same in a
    // cached plan and in a fresh plan from that prefix. A minion played and lost within the line is deliberately not a loss.
    internal long TurnFace;
    internal static IOrderedEnumerable<TurnLine> Rank(IEnumerable<TurnLine> lines) => Ranked(lines, true);
    // Rank without face-tiebreak: what the flag-OFF search would rank first over the same lines.
    internal static IOrderedEnumerable<TurnLine> RankWithoutFace(IEnumerable<TurnLine> lines) => Ranked(lines, false);
    private static IOrderedEnumerable<TurnLine> Ranked(IEnumerable<TurnLine> lines, bool face)
    {
        var ranked = lines
            .OrderByDescending(n => n.Survival.HasValue ? -1000000 : n.Value)
            .ThenByDescending(n => n.Survival ?? 0).ThenByDescending(n => n.Resistance)
            .ThenByDescending(n => n.FaceDamage).ThenBy(n => n.UnusedResources)
            .ThenByDescending(n => n.Preference);
        if (face) ranked = ranked.ThenByDescending(n => n.TurnFace);
        return ranked.ThenBy(n => n.Actions.Count).ThenBy(n => n.Key, StringComparer.Ordinal);
    }
    internal string Key => string.Join(";", Actions.Select(a => a.ToString()));
}

// Both own-turn and opponent beams retain their cursor. One StepNode applies at most one action.
internal static class TurnSearchTrace
{
    [ThreadStatic] internal static Action<string, IReadOnlyList<BotAction>, double> Observe;
}

internal sealed class TurnBeam : INodeJob
{
    internal const int SettlementLimit = 4;
    // A turn line ends here even when actions remain. Free repeatable plays (a 0-cost spell that Discovers another one)
    // otherwise drive a beam thousands of actions deep in one turn: game 741 decision 97 reached 5,848 actions, 17,614
    // leaves and a 45 GB heap. Normal turns stay far below it; the live runner stops a turn at 127 actions anyway.
    internal const int MaxLineActions = 64;
    private readonly Func<FairBotState, double> evaluate;
    private readonly DeckPlanEvaluator plan;
    private readonly GoldenPublicInputs inputs;
    private readonly bool endTurn, dedupe, prune, release, defensiveDiversity, allocation, faceTie, faceCounts;
    private readonly long rootEnemyLife;
    private readonly HashSet<int> rootFriendlyMinions;
    private readonly int cap, width;
    private readonly BotAction firstAction;
    private readonly HashSet<int> heldSources;
    private readonly int[] originalHand;
    private List<TurnLine> frontier, next = new List<TurnLine>(), expanded = new List<TurnLine>(), older = new List<TurnLine>();
    private int parent, action, horizon = -1, settledChoices;
    private BotAction[] actions;
    private TurnLine pendingScore, pendingWin;
    private readonly FairBotState rootState;
    private LethalJob verification;
    private bool projectedWin;
    internal readonly List<TurnLine> Leaves = new List<TurnLine>();
    internal bool SettleFallback { get; set; }
    internal IEnumerable<TurnLine> ScoredLeaves => Leaves.Where(n => n != pendingScore && n != pendingWin);
    internal IEnumerable<TurnLine> Progress => ScoredLeaves.Concat(next).Concat(frontier)
        .Where(n => n != pendingScore && n != pendingWin && n.Actions.Count > 0);
    public bool IsDone { get; private set; }
    internal int Nodes { get; private set; }
    internal int Expansions { get; private set; }
    internal int EndpointScores { get; private set; }
    internal bool Capped { get; private set; }
    // Completed beam depths (frontier replacements); read-only progress telemetry for the hover cache.
    internal int Depth { get; private set; }
    // Actions in the longest line built so far (decision cost telemetry, NpcDecisionCost.OutlierDepth).
    internal int Deepest { get; private set; }
    internal TurnBeam(FairBotState state, Func<FairBotState, double> evaluate, int cap, int width,
        bool endTurn, DeckPlanEvaluator plan = null, GoldenPublicInputs inputs = null, bool dedupe = false, bool prune = false,
        BotAction firstAction = null, IEnumerable<int> heldSources = null, bool defensiveDiversity = false, bool attackAllocation = false,
        bool faceTieBreak = false)
    {
        allocation = attackAllocation && !endTurn && state.Facts.Opponent.Board.Count > 0;
        faceTie = faceTieBreak && !endTurn;
        if (faceTie) {
            rootEnemyLife = (long)state.Facts.Opponent.Hero.Health + state.Facts.Opponent.Hero.Armor;
            // A tied face hit can walk into an enemy Secret, so it is not free; the loss penalty still applies.
            faceCounts = state.Facts.OpponentSecretCount == 0;
            rootFriendlyMinions = new HashSet<int>(state.Facts.Own.Board.Select(c => c.EntityId));
        }
        this.firstAction = firstAction; this.defensiveDiversity = defensiveDiversity;
        this.heldSources = new HashSet<int>(heldSources ?? Enumerable.Empty<int>());
        rootState = state; this.evaluate = evaluate; this.cap = Math.Max(1, cap); this.width = width; this.endTurn = endTurn; this.plan = plan; this.inputs = inputs;
        this.dedupe = dedupe; this.prune = prune; release = !endTurn && !GoldenAblation.HoldLineStates;
        originalHand = state.Facts.Own.Hand.Select(c => c.EntityId).ToArray();
        frontier = new List<TurnLine> { new TurnLine { State = state, Value = evaluate(state), PublicBoardSafe = !endTurn && DeckPlanEvaluator.PublicBoardSurvives(state.Facts) } };
        if (defensiveDiversity) SetAttackMargin(frontier[0], state.Facts);
        if (!endTurn && state.NeedsEndpointProjection(plan, GoldenAblation.LegacyEval)) pendingScore = frontier[0];
    }
    public void StepNode()
    {
        if (IsDone) return;
        if (horizon >= 0) {
            if (horizon == Leaves.Count) { IsDone = true; return; }
            Nodes++;
            var leaf = Leaves[horizon];
            try {
                // A capped prefix can stop inside a Discover; settle it with the engine-ordered pick so the
                // sample keeps its probability mass instead of failing the whole reply.
                if (settledChoices < SettlementLimit && leaf.State.HasPendingChoice && leaf.State.CanExpand) {
                    var choice = leaf.State.OrderedActions.First();
                    leaf.State = leaf.State.Apply(choice); leaf.Actions.Add(choice);
                    settledChoices++;
                    return;
                }
                if (!leaf.State.IsTerminal && leaf.State.Facts.CurrentPlayerId == rootState.Facts.Own.PlayerId) {
                    var end = leaf.State.ReplyEndTurnAction;
                    leaf.State = leaf.State.ForReplyEndTurn();
                    // Expanded END_TURN is already a sentinel in Actions; capped prefixes need it appended.
                    if (leaf.Actions.LastOrDefault()?.Type != PlayerTaskType.END_TURN) leaf.Actions.Add(end);
                }
                leaf.Value = evaluate(leaf.State);
            }
            catch (SimulationFailureException error) { leaf.State.ReportFault(error); leaf.Value = double.NegativeInfinity; }
            // settle-fallback: a settlement the fair view refuses (an end-of-turn effect reading a hidden card) keeps the
            // reply's unsettled value; dropping the leaf left lines whose every reply was refused at their static value.
            catch (Exception error) when (SettleFallback && FairHistory.IsPrivate(error)) { leaf.Value = evaluate(leaf.State); }
            catch (Exception error) when (!(error is OutOfMemoryException)) { leaf.Value = double.NegativeInfinity; }
            horizon++; settledChoices = 0;
            return;
        }
        if (Nodes >= cap) { Capped = true; Finish(); return; }
        Nodes++;
        if (verification != null) {
            verification.StepNode();
            if (verification.IsDone) {
                var winner = pendingWin;
                bool found = verification.Result.Outcome == LethalOutcome.Found;
                double? refutedValue = verification.RefutedValue;
                double? secretValue = verification.SecretHeuristicValue;
                verification = null; pendingWin = null;
                if (found) {
                    if (projectedWin) winner.Actions.Add(winner.State.Actions.First(a => a.Type == PlayerTaskType.END_TURN));
                    Win(winner); return;
                }
                // Secret replay supplies a heuristic expectation; other uncertified terminal samples keep their prefix estimate.
                RejectWin(winner, refutedValue, secretValue);
            }
            return;
        }
        if (pendingScore != null) {
            var pending = pendingScore; pendingScore = null;
            EndpointScores++;
            try {
                var endpoint = pending.State.ForReplyEndTurn();
                pending.Value = evaluate(endpoint);
                pending.PublicBoardSafe = DeckPlanEvaluator.PublicBoardSurvives(endpoint.Facts);
                if (defensiveDiversity) SetAttackMargin(pending, endpoint.Facts);
            }
            catch (Exception error) when (FairHistory.IsPrivate(error)) {
                pending.Value = pending.State.EndpointFailureValue(pending.Value, GoldenAblation.LegacyEval ? null : plan);
            }
            catch (SimulationFailureException error) {
                pending.State.ReportFault(error);
                pending.Value = pending.State.EndpointFailureValue(pending.Value, GoldenAblation.LegacyEval ? null : plan);
            }
            if (pending.Value >= 999000) { VerifyWin(pending, true); return; }
            pending.Release(dedupe && pending.State.CanExpand, allocation);
            return;
        }
        while (parent >= frontier.Count) {
            if (next.Count == 0) { Finish(); return; }
            var ranked = next.OrderByDescending(n => n.Value).ThenByDescending(n => n.Preference).ThenByDescending(n => n.TurnFace).ThenBy(n => n.Key, StringComparer.Ordinal).ToList();
            // Elite+ dedupe: one line per reached public state, best-ranked first. Own-beam lines key themselves before
            // releasing their clone (one key per child, below one node expansion each); ranking never rebuilds a clone.
            if (dedupe) ranked = Distinct(ranked, 2 * width);
            // Spell-only/Discover branches need the existing frontier until an attack becomes possible or is used.
            bool protectCombat = allocation && ranked.Any(n => n.AttacksKey.Length > 0 || n.Actions.Any(TurnLine.IsAttack));
            // Reserve frontier slots for different public outcomes; remaining slots retain hand diversity.
            frontier = !endTurn
                ? ranked.GroupBy(n => n.EffectsKey).Select(g => g.First()).Take(Math.Max(1, width / 3)).ToList()
                : new List<TurnLine>();
            if (protectCombat) {
                // Leave explicit slots after these groups for partial damage/shields plus hand commitment.
                foreach (var candidate in ranked.GroupBy(n => n.SurvivorsKey).Select(g => g.First())) {
                    if (frontier.Count >= Math.Max(1, width / 2)) break;
                    if (!frontier.Contains(candidate)) frontier.Add(candidate);
                }
                foreach (var candidate in ranked.GroupBy(n => n.AttacksKey).Select(g => g.First())) {
                    if (frontier.Count >= Math.Max(1, width * 2 / 3)) break;
                    if (!frontier.Contains(candidate)) frontier.Add(candidate);
                }
            }
            if (!endTurn && (originalHand.Length > 0 || protectCombat)) {
                // Preserve different visible effects for the same hand commitment.
                foreach (var candidate in ranked.GroupBy(n => (protectCombat ? n.CombatKey : n.EffectsKey) + "|hand:" +
                    string.Join(",", n.HandIds.Where(originalHand.Contains).OrderBy(id => id))).Select(g => g.First())) {
                    if (frontier.Count >= (protectCombat ? width * 3 / 4 : width * 2 / 3)) break;
                    if (!frontier.Contains(candidate)) frontier.Add(candidate);
                }
                foreach (var candidate in ranked.GroupBy(n => string.Join(",", n.HandIds
                    .Where(originalHand.Contains).OrderBy(id => id))).Select(g => g.First())) {
                    if (frontier.Count == width) break;
                    if (!frontier.Contains(candidate)) frontier.Add(candidate);
                }
            }
            foreach (var candidate in ranked)
            {
                if (frontier.Count == width) break;
                if (!frontier.Contains(candidate)) frontier.Add(candidate);
            }
            // Preserve a tempo line that retains the original hand for fair comparison against clears.
            if (!endTurn && originalHand.Length > 0) {
                var hold = ranked.FirstOrDefault(n => !originalHand.Except(n.HandIds).Any());
                if (hold != null && !frontier.Contains(hold)) { frontier.RemoveAt(frontier.Count - 1); frontier.Add(hold); }
            }
            if (release) {
                // Keep the parents of the new frontier until that depth is expanded: each frontier line then rebuilds
                // its clone (one Apply) in the step that expands it, never all of them in one step.
                foreach (var done in older) done.Release(false, allocation);
                older = expanded; expanded = new List<TurnLine>(frontier);
            }
            if (!endTurn && TurnSearchTrace.Observe != null)
                foreach (var kept in frontier) TurnSearchTrace.Observe("retained", kept.Actions, kept.Value);
            next = new List<TurnLine>(); parent = 0; actions = null; Depth++;
        }
        var node = frontier[parent];
        if (actions == null && node.Actions.Count >= MaxLineActions) { Leaves.Add(node); parent++; return; }
        if (actions == null) {
            var legal = node.State.OrderedActions.Where(a =>
                (firstAction == null || node.Actions.Count > 0 || a.Equals(firstAction))
                && (a.Type != PlayerTaskType.PLAY_CARD || !heldSources.Contains(a.Source))).ToArray(); node.Expanded = true;
            if (prune) legal = Prune(node.State.Facts, legal);
            if (!endTurn && (firstAction == null || node.Actions.Count > 0)
                && (legal.Any(a => a.Type == PlayerTaskType.END_TURN) || legal.Length == 0)) Leaves.Add(node);
            if (legal.Length == 0) { node.Completed = true; if (endTurn) Leaves.Add(node); parent++; return; }
            // Bound action families first, then retain meaningful placements within each family.
            var facts = node.State.Facts;
            var families = legal.Select((a, i) => new { Action = a, Score = Order(facts, a, -i) })
                .Where(x => endTurn || x.Action.Type != PlayerTaskType.END_TURN || firstAction != null && node.Actions.Count == 0)
                .OrderByDescending(x => x.Score).ThenBy(x => x.Action.ToString(), StringComparer.Ordinal)
                .Select(x => x.Action).GroupBy(a => a.WithoutPosition).ToArray();
            var selectedFamilies = families.Take(width * 2);
            // An available cheap hit must not disappear behind dozens of spell targets/placements.
            if (allocation) selectedFamilies = selectedFamilies.Concat(families.Skip(width * 2).Where(g => TurnLine.IsAttack(g.First())));
            actions = selectedFamilies.SelectMany(g => Placements(facts, g)).ToArray();
            action = 0;
        }
        if (action >= actions.Length) { node.Completed = true; parent++; actions = null; return; }
        var selected = actions[action++];
        FairBotState child;
        try { child = selected.Type == PlayerTaskType.END_TURN ? node.State : node.State.Apply(selected); }
        catch (Exception e) when (FairHistory.IsPrivate(e)) { return; }
        catch (SimulationFailureException e) { node.State.ReportFault(e); return; }
        Expansions++;
        var line = new TurnLine { State = child, Actions = new List<BotAction>(node.Actions), Value = evaluate(child), Preference = node.Preference,
            Origin = release ? node : null, PublicBoardSafe = !endTurn && DeckPlanEvaluator.PublicBoardSurvives(child.Facts) };
        if (defensiveDiversity) SetAttackMargin(line, child.Facts);
        if (faceTie) line.TurnFace = TurnFace(child.Facts);
        line.Actions.Add(selected);
        if (!endTurn) TurnSearchTrace.Observe?.Invoke("generated", line.Actions, line.Value);
        if (line.Actions.Count > Deepest) Deepest = line.Actions.Count;
        if (selected.Type == PlayerTaskType.CHOOSE && plan != null) {
            var before = node.State.Facts.Own.Hand.Select(c => c.EntityId).ToArray();
            var added = child.Facts.Own.Hand.Where(c => !before.Contains(c.EntityId)).ToArray();
            line.Preference += added.Sum(c => plan.DiscoverBonus(c.CardId, inputs?.DiscoverSource));
            if (added.Length == 0 && inputs != null) {
                string text = selected.ToString(); string choice = text.Substring(text.LastIndexOf(':') + 1);
                int id;
                if (int.TryParse(choice, out id)) line.Preference += inputs.Offered.Where(o => o.Key == id)
                    .Sum(o => plan.DiscoverBonus(o.Value, inputs.DiscoverSource));
            }
        }
        bool ends = !child.CanExpand || selected.Type == PlayerTaskType.END_TURN;
        if (ends) { line.Completed = selected.Type == PlayerTaskType.END_TURN || child.IsTerminal; Leaves.Add(line); }
        else next.Add(line);
        if (line.Value >= 999000) { VerifyWin(line, false); return; }
        if (!endTurn && child.NeedsEndpointProjection(plan, GoldenAblation.LegacyEval)) pendingScore = line;
        else line.Release(dedupe && !ends, allocation);
        if (Nodes >= cap) { Capped = true; Finish(); }
    }
    // Coverage heuristic only; the unchanged reply search decides whether a public/prior threat is real.
    // Node accounting: a node is one built child state plus its evaluation. Like PublicBoardSurvives, margin is
    // per-node evaluation work (exact blocker allocation + cached public allowance), not a new state, so it adds no
    // node; charging it would shrink only the ON beam and confound the A/B. Wall guards bound its real cost.
    private static double AttackMargin(FairFacts f, double damage) => f.Own.Hero.Health + (double)f.Own.Hero.Armor - damage;
    private void SetAttackMargin(TurnLine line, FairFacts f)
    {
        line.PublicAttackDamage = plan == null ? DeckPlanEvaluator.PublicFaceDamage(f.Own, f.Opponent, f.CurrentPlayerId)
            : plan.PublicIncomingDamage(f);
        line.PublicAttackMargin = AttackMargin(f, line.PublicAttackDamage);
    }
    // Read from the line's own (pre-end-of-turn) state: an end-of-turn heal must not erase the hit it is breaking ties for.
    private const long LostMinionPenalty = 1000000;
    private long TurnFace(FairFacts f)
    {
        long lost = rootFriendlyMinions.Count - f.Own.Board.Count(c => rootFriendlyMinions.Contains(c.EntityId));
        long face = faceCounts ? Math.Min(LostMinionPenalty - 1, Math.Max(0, rootEnemyLife - f.Opponent.Hero.Health - f.Opponent.Hero.Armor)) : 0;
        return face - lost * LostMinionPenalty;
    }
    private void VerifyWin(TurnLine line, bool projected)
    {
        if (endTurn) { Win(line); return; } // ReplyModel verifies opponent lethal itself.
        var witness = new List<BotAction>(line.Actions);
        if (projected) {
            var end = line.State.Actions.FirstOrDefault(a => a.Type == PlayerTaskType.END_TURN);
            // Unknown draws can still resolve an endpoint, but cannot provide a verified executable witness.
            if (end == null) { RejectWin(line); return; }
            witness.Add(end);
        }
        pendingWin = line; projectedWin = projected;
        verification = LethalJob.Verify(rootState, witness, Math.Max(1, cap - Nodes),
            rootState.Facts.OpponentSecretCount > 0 ? evaluate : null);
    }
    private void RejectWin(TurnLine line, double? refutedValue = null, double? secretValue = null)
    {
        line.UnverifiedWin = true;
        line.UncertifiedEndTurnWin = rootState.Facts.OpponentSecretCount > 0 && projectedWin;
        double fallback = line.Origin?.Value ?? evaluate(rootState);
        line.Value = secretValue ?? Math.Max(-998000, Math.Min(998000, Math.Min(fallback, refutedValue ?? fallback)));
        line.Release(dedupe && line.State.CanExpand, allocation);
    }
    private void Win(TurnLine line)
    {
        pendingScore = null; Leaves.Clear(); Leaves.Add(line);
        next.Clear(); frontier.Clear(); IsDone = true;
    }
    // AI-leap research (BrainOptions.Prune; the 2019 MCGS competition winner's filters): expand one action per equivalent
    // (type, source card, target card, choice, position) class. Two copies of the same card in hand, two identical
    // attackers or two identical targets are interchangeable; positions stay when adjacency or Outcast can matter.
    private static readonly HashSet<GameTag> VolatileTags = new HashSet<GameTag> { GameTag.ZONE_POSITION, GameTag.ENTITY_ID };
    private static BotAction[] Prune(FairFacts f, BotAction[] legal)
    {
        var cards = new Dictionary<int, FairCard>();
        foreach (var p in new[] { f.Own, f.Opponent })
            foreach (var c in p.Hand.Concat(p.Board).Concat(p.Locations).Concat(new[] { p.Hero, p.HeroPower, p.Weapon }))
                if (c != null) cards[c.EntityId] = c;
        Func<int, bool, string> sig = (id, keepPosition) => {
            FairCard c;
            if (id == 0 || !cards.TryGetValue(id, out c) || c.Unknown || c.Type == CardType.HERO) return "#" + id;
            var b = new System.Text.StringBuilder(c.CardId).Append(':').Append(c.Cost).Append(':').Append(c.Attack).Append(':').Append(c.Health)
                .Append(':').Append(c.Armor).Append(':').Append(c.Durability).Append(':').Append(c.Taunt).Append(c.DivineShield).Append(c.Frozen).Append(c.Exhausted);
            bool positional = keepPosition || DeckPlanEvaluator.Tag(c, (GameTag)1333 /* OUTCAST; absent from the vendor DLL enum */) != 0 || Positional(c);
            if (positional) b.Append("@").Append(c.Position).Append(f.Own.Hand.Contains(c) ? "h" : "b");
            foreach (var tag in c.Tags.Where(t => !VolatileTags.Contains(t.Key)).OrderBy(t => (int)t.Key)) b.Append(',').Append((int)tag.Key).Append('=').Append(tag.Value);
            return b.ToString();
        };
        var seen = new HashSet<string>(StringComparer.Ordinal); var kept = new List<BotAction>();
        foreach (var a in legal) {
            if (a.Type == PlayerTaskType.CHOOSE || a.Type == PlayerTaskType.END_TURN) { kept.Add(a); continue; }
            FairCard source; cards.TryGetValue(a.Source, out source);
            bool adjacency = source != null && !source.Unknown && (Cards.FromId(source.CardId).Text ?? "").IndexOf("adjacent", StringComparison.OrdinalIgnoreCase) >= 0;
            string key = a.Type + "|" + sig(a.Source, false) + "|" + sig(a.Target, adjacency) + "|" + a.ChooseOne + "|" + a.Position + "|" + a.WithoutPosition.Substring(a.WithoutPosition.LastIndexOf(':') + 1);
            if (seen.Add(key)) kept.Add(a);
        }
        return kept.Count == legal.Length ? legal : kept.ToArray();
    }
    private static List<TurnLine> Distinct(List<TurnLine> ranked, int keep)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal); var result = new List<TurnLine>();
        foreach (var line in ranked) {
            if (result.Count >= keep) { result.Add(line); continue; } // the tail stays for the hold-line scan
            if (seen.Add(line.PublicKey)) result.Add(line);
        }
        return result;
    }
    // Native 1576 marks effects on adjacent minions (including Battlecries), without a named engine enum.
    private static readonly GameTag[] PositionTags = new[] { (GameTag)1576 }.Concat(
        Enum.GetValues(typeof(GameTag)).Cast<GameTag>().Where(tag => {
            string name = tag.ToString().Replace("_", "");
            return name.Contains("ADJACENT") || name.Contains("LEFTMOST") || name.Contains("RIGHTMOST");
        })).Distinct().ToArray();
    private static bool AdjacentAura(IAura aura) => aura is SabberStoneCore.Auras.AdjacentAura
        || aura is Aura ranged && ranged.Type == AuraType.ADJACENT
        || aura is IEnumerable<IAura> children && children.Any(AdjacentAura);
    private static bool Positional(FairCard card)
    {
        if (card.Unknown || DeckPlanEvaluator.Tag(card, GameTag.SILENCED) != 0) return false;
        var definition = Cards.FromId(card.CardId);
        return PositionTags.Any(tag => DeckPlanEvaluator.Tag(card, tag) != 0 || definition[tag] != 0)
            || AdjacentAura(definition.Power?.Aura);
    }
    private static double PartnerValue(FairCard card) => card.Attack * 2.2 + card.Health * 1.2
        + card.Cost * .6 + (card.DivineShield ? 4 : 0) + (card.Taunt ? 2 : 0);
    private static IEnumerable<BotAction> Placements(FairFacts facts, IEnumerable<BotAction> family)
    {
        var options = family.OrderBy(a => a.Position).ToArray();
        var first = options[0];
        if (options.Length == 1 || first.Type != PlayerTaskType.PLAY_CARD) return new[] { first };
        var source = facts.Own.Hand.FirstOrDefault(c => c.EntityId == first.Source);
        if (source == null || source.Type != CardType.MINION) return new[] { first };
        var board = facts.Own.Board;
        bool positional = Positional(source);
        var partners = board.Where(Positional).ToArray();
        if (!positional && !source.Taunt && partners.Length == 0) return new[] { options.Last() };
        if (positional || partners.Length > 0) return options;
        // Plain Taunts keep the existing bounded positioning heuristic.
        var partner = board
            .OrderByDescending(PartnerValue).ThenBy(c => c.Position).ThenBy(c => c.EntityId).FirstOrDefault();
        var interior = options.Where(a => a.Position != first.Position && a.Position != options.Last().Position
            && partner != null && (a.Position == partner.Position || a.Position == partner.Position + 1))
            .OrderByDescending(a => board.Where(c => c.Position == a.Position - 1 || c.Position == a.Position).Sum(PartnerValue))
            .ThenBy(a => a.Position).FirstOrDefault();
        // Both ends plus at most one interior neighbor fit the three-position allowance.
        return new[] { first, options.Last(), interior }.Where(a => a != null).Distinct().OrderBy(a => a.Position);
    }
    private double Order(FairFacts f, BotAction a, double score)
    {
        // Preserve face branches when lethal is plausible, without crowding out clears against a healthy hero.
        if (endTurn && a.Target == f.Opponent.Hero.EntityId && (long)f.Opponent.Hero.Health + f.Opponent.Hero.Armor
            <= f.Own.Mana + f.Own.Board.Where(c => !c.Frozen && !c.Exhausted).Sum(c => (long)c.Attack)
                + (f.Own.Hero.Frozen || f.Own.Hero.Exhausted ? 0 : f.Own.Hero.Attack)) return 1;
        // The engine's ordering covers attacks/removal. Plan hints move enablers ahead of payoffs at bounded widths.
        if (plan == null) return score;
        var source = f.Own.Hand.Concat(f.Own.Board).Concat(f.Own.Locations).FirstOrDefault(c => c.EntityId == a.Source);
        if (source != null && a.Type == PlayerTaskType.PLAY_CARD && QuestValue.IsQuest(source.CardId)) score += 40;
        if (source != null && plan.Roles(source.CardId, CardRole.Fire | CardRole.DamageEnabler)) score += 20;
        if (source != null && source.CardId == "CATA_584" && a.Type != PlayerTaskType.PLAY_CARD) score -= 20;
        if (source != null && a.Type == PlayerTaskType.PLAY_CARD && source.Type == CardType.MINION
            && f.Own.HeroPower.CardId == "JAIL_800hp2") {
            bool fifth = DeckPlanEvaluator.Tag(f.Own.HeroPower, GameTag.TAG_SCRIPT_DATA_NUM_1) == 4; // progress 4 = next play is the doubled fifth
            bool payoff = plan.UsefulBattlecry(source, f.Own, f.OwnDeckKnowledge);
            score += fifth ? (payoff ? 30 : -20) : (payoff ? -10 : 10);
        }
        return score;
    }
    private void Finish()
    {
        // Unknown-draw boundaries and capped prefixes are useful: the live pilot replans after the action.
        foreach (var node in next.Concat(frontier))
            if (node.Actions.Count > 0 || firstAction == null && node.State.Actions.Any(a => a.Type == PlayerTaskType.END_TURN)) Leaves.Add(node);
        if (endTurn) {
            var finalists = Leaves.GroupBy(n => n.Key).Select(g => g.First())
                .OrderByDescending(n => n.Value).ThenBy(n => n.Key, StringComparer.Ordinal).Take(width).ToArray();
            Leaves.Clear(); Leaves.AddRange(finalists); horizon = 0;
        } else IsDone = true;
    }
}

public static class TurnPlanner
{
    public static TurnPlannerJob Job(FairBotState state, NpcProfile profile, DeckPlan plan, int seed,
        int cap = -1, IEnumerable<string> publiclyPlayed = null, GoldenPublicInputs inputs = null,
        BotAction firstAction = null, IEnumerable<int> heldSources = null, bool comparePass = false) =>
        new TurnPlannerJob(state, profile, plan, seed, cap < 0 ? profile.Budget.PlanNodes : cap, publiclyPlayed, inputs, firstAction, heldSources, comparePass);
}
public sealed class TurnPlannerJob : INodeJob
{
    [ThreadStatic] internal static Action<TurnPlannerJob> DiagnosticFinished;
    internal IEnumerable<TurnLine> DiagnosticBeam => beam.ScoredLeaves;
    internal IEnumerable<TurnLine> DiagnosticReplies => scored;
    internal TurnLine[] DiagnosticOriginalFinalists { get; private set; } = Array.Empty<TurnLine>();
    internal TurnLine DiagnosticDefense { get; private set; }
    internal TurnLine DiagnosticLeader { get; private set; }
    internal TurnLine DiagnosticHold { get; private set; }
    internal TurnLine DiagnosticBestDefense { get; private set; }
    internal TurnLine DiagnosticEvicted { get; private set; }
    internal int DiagnosticReplyLimit => replyLimit;
    internal int DiagnosticReplyK => replyK;
    internal int DiagnosticFinalists => scored.Count;
    internal int DiagnosticOwnNodes => beam.Nodes;
    internal Action<TurnLine, string> ObserveLeaf { get; set; }
    private readonly FairBotState root;
    private readonly NpcProfile profile;
    private readonly DeckPlanEvaluator evaluator;
    private readonly int seed, cap, verificationReserve;
    private int replyLimit, normalReplyLimit, replyNodes, perReplyAllowance, replyK;
    // starved-replies: capped below the tier's finalists, so samples and finalists are refitted to what the own beam left.
    private readonly bool refit;
    private bool refitted;
    private int repliesFrom;
    private List<TurnLine> refills;
    private readonly bool comparePass;
    private TurnLine holdLine;
    private double holdStaticValue;
    private int[] originalHand;
    private readonly IEnumerable<string> played;
    private readonly TurnBeam beam;
    private List<TurnLine> leaves;
    private ReplyJob reply;
    private int index, steps, originalFinalistCount;
    private readonly List<TurnLine> scored = new List<TurnLine>();
    internal static string HandCommitment(IEnumerable<int> hand, IReadOnlyDictionary<int, string> kinds) =>
        string.Join(",", hand.Select(id => kinds.TryGetValue(id, out var kind) ? kind : "<new>"));
    // Masked cards (own draws of hidden cards inside a search) all carry EntityId 0; they map to "<new>".
    internal static IReadOnlyDictionary<int, string> HandKinds(FairFacts facts) => facts.Own.Hand.Where(c => !c.Unknown).ToDictionary(c => c.EntityId,
        c => c.CardId + ":" + c.Cost + ":" + c.Attack + ":" + c.Health + ":" + c.GeneratedQuest
            // Attached triggers may change future cost without changing today's stats or tags.
            + (c.HasAttachedEffects ? ":entity:" + c.EntityId : "") + ":" + string.Join(",", c.Tags
                .Where(t => t.Key != GameTag.ENTITY_ID && t.Key != GameTag.ZONE_POSITION).OrderBy(t => (int)t.Key)
                .Select(t => (int)t.Key + "=" + t.Value)));
    public bool IsDone { get; private set; }
    public PlannedTurn Result { get; private set; }
    internal bool HasPassComparison => ComparisonLines.Any(n => n.Actions.Count == 0)
        && ComparisonLines.Any(n => n.Actions.Count > 0 && n.Actions[0].Type != PlayerTaskType.END_TURN);
    internal bool HasVerifiedLethal => beam.IsDone && beam.ScoredLeaves.Any(n => n.Value >= 999000 && !n.UnverifiedWin);
    private IEnumerable<TurnLine> ComparisonLines => replyLimit == 0 ? beam.ScoredLeaves : scored;
    internal bool HasEvaluatedAction => scored.Any(line => line.Actions.Count > 0) || beam.Progress.Any();
    internal int Deepest => beam.Deepest;
    internal int Expansions => beam.Expansions;
    internal int EndpointScores => beam.EndpointScores;
    public TurnPlannerJob(FairBotState state, NpcProfile profile, DeckPlan plan, int seed, int cap, IEnumerable<string> publiclyPlayed, GoldenPublicInputs inputs = null,
        BotAction firstAction = null, IEnumerable<int> heldSources = null, bool comparePass = false)
    {
        this.comparePass = comparePass; root = state; this.profile = profile; evaluator = new DeckPlanEvaluator(plan, profile.Budget.Brain) { RecurringValue = profile.Fixes.On("recurring-value"), ZeroDoomed = profile.Fixes.On("doomed-minions"), DeathFixes = profile.Fixes, TriggerFixes = profile.Fixes, OwnDeckValue = profile.Fixes.On("own-deck-value") }; this.seed = seed;
        this.cap = Math.Max(1, cap); played = (publiclyPlayed ?? Enumerable.Empty<string>()).ToArray();
        evaluator.SetOpponentEvidence(state.ReplyClass, played);
        if (evaluator.TriggerValue) evaluator.SetTriggerEvidence(state.OwnEvidence, state.OpponentEvidence);
        // The opponent model keeps its full strength R on every replan: a smaller allowance scores fewer
        // finalists instead of a weaker reply, which would otherwise make late-turn overextension look safe.
        int reserve = this.cap * 2 / 3, perLeaf = ReplyModel.SearchAllowance(profile.Budget.ReplyK, profile.Budget.ReplyR, state.HasOwnSecrets);
        // An extra hold comparison is admitted like a normal finalist: search allowance, verification from the shared reserve.
        perReplyAllowance = perLeaf;
        // The two-thirds share buys reply search; settlement gets its own reservation from the remaining nodes.
        replyLimit = Math.Min(profile.Budget.ReplyL, Math.Max(0, (reserve - 2) / (profile.Budget.ReplyK * profile.Budget.ReplyR + 3)));
        replyLimit = Math.Min(replyLimit, Math.Max(0, (this.cap - 3) / perLeaf));
        if (replyLimit == 0 && this.cap - 2 - perLeaf >= this.cap / 4) replyLimit = 1;
        if (GoldenAblation.NoReply) replyLimit = 0;
        // A pass and a real continuation must both receive the tier's full reply search.
        if (comparePass && !GoldenAblation.NoReply && profile.Budget.ReplyL > 0) replyLimit = Math.Max(2, replyLimit);
        replyNodes = replyLimit == 0 ? 0 : profile.Budget.ReplyR;
        // Share a full longest-witness reservation across finalists; unused beam/sample nodes refill it.
        // Reserving the worst witness for every hand would erase master finalist diversity on quiet boards.
        // The hard scheduler cap can leave later replies unfinished; only completed replies enter ranking.
        verificationReserve = ReplyModel.VerificationAllowance(ReplyModel.WitnessLimit(profile.Budget.ReplyR, state.HasOwnSecrets));
        replyK = profile.Budget.ReplyK;
        // Pass validation and mistake pricing (firstAction) compare two arms: both keep the same samples at any cap.
        refit = !comparePass && firstAction == null && !GoldenAblation.NoReply && profile.Fixes.On("starved-replies") && replyLimit < profile.Budget.ReplyL;
        if (refit && replyLimit < Math.Min(ComparisonFinalists, profile.Budget.ReplyL)) {
            // Plan the own beam's share as usual, three quarters at most for replies; a tier reply at this cap only prices the leader.
            var fit = FitReplies(this.cap * 3 / 4 - verificationReserve - 2, profile.Budget, state.HasOwnSecrets);
            replyK = fit.K; replyLimit = fit.Finalists;
            perLeaf = perReplyAllowance = ReplyModel.SearchAllowance(replyK, profile.Budget.ReplyR, state.HasOwnSecrets);
            replyNodes = replyLimit == 0 ? 0 : profile.Budget.ReplyR;
        }
        normalReplyLimit = replyLimit;
        // Spend spare nodes on one extra combat reply without reducing own coverage or the normal commitment/hold slots.
        if (profile.Fixes.On("attack-allocation") && state.Facts.Opponent.Board.Count > 0 && replyLimit >= 3) replyLimit++;
        int replyReserve = normalReplyLimit * perLeaf + verificationReserve + 2;
        // Secret settlement's pessimistic allowance can exceed this reserve for two finalists. Keep an own beam;
        // if both full-strength replies cannot finish inside cap, the caller retains its original decision.
        if (comparePass) replyReserve = Math.Min(replyReserve, reserve);
        int own = replyLimit == 0 ? this.cap : Math.Max(1, this.cap - replyReserve);
        Func<FairBotState, double> score = GoldenAblation.LegacyEval ? (Func<FairBotState, double>)(s => s.Evaluate()) : s => s.Evaluate(evaluator);
        beam = new TurnBeam(state, score, own, GoldenAblation.OwnBeamWidth > 0 ? GoldenAblation.OwnBeamWidth : profile.Budget.OwnBeamWidth, false, evaluator, inputs, profile.Budget.Dedupe,
            profile.Budget.Brain != null && profile.Budget.Brain.Prune, firstAction, heldSources,
            (normalReplyLimit > 2 || refit && this.cap >= 3 * ReplyModel.SearchAllowance(MinReplySamples, profile.Budget.ReplyR, state.HasOwnSecrets) + verificationReserve + 2) && profile.Fixes.On("survival-scoring"), profile.Fixes.On("attack-allocation"), profile.Fixes.On("face-tiebreak"));
        Result = new PlannedTurn { Value = state.Evaluate(evaluator), Stop = SearchStop.Nodes };
    }
    // Fewer samples of an unchanged reply beat no reply: K4/R160 agreed with K14 on the diagnosed position, and K1 swung
    // by 50 points, so never below MinReplySamples. A lone finalist compares nothing; below two the nodes go to the own beam.
    internal const int MinReplySamples = 4, ComparisonFinalists = 3;
    internal static (int K, int Finalists) FitReplies(int nodes, Budget budget, bool ownSecrets)
    {
        for (int finalists = Math.Min(ComparisonFinalists, budget.ReplyL); finalists >= 2; finalists--)
            for (int k = budget.ReplyK; k >= Math.Min(budget.ReplyK, MinReplySamples); k--)
                if (finalists * ReplyModel.SearchAllowance(k, budget.ReplyR, ownSecrets) <= nodes) return (k, finalists);
        return (budget.ReplyK, 0);
    }
    // The beam is done. Finalists the cap already paid at the tier's samples stay as they are; a smaller plan is refitted to
    // the nodes the beam actually left. Passing is all there is: nothing to compare, no reply.
    private void Refit()
    {
        var budget = profile.Budget;
        repliesFrom = steps;
        bool choice = beam.ScoredLeaves.Skip(1).Any(), acts = beam.ScoredLeaves.Any(n => n.Actions.Any(a => a.Type != PlayerTaskType.END_TURN));
        if (replyK == budget.ReplyK && normalReplyLimit >= Math.Min(ComparisonFinalists, budget.ReplyL) && choice && acts) return;
        // This step counts in steps, so a beam that used exactly its share leaves the planned replies.
        var fit = choice && acts ? FitReplies(cap - steps - verificationReserve, budget, root.HasOwnSecrets) : (K: budget.ReplyK, Finalists: 0);
        replyK = fit.K;
        perReplyAllowance = ReplyModel.SearchAllowance(replyK, budget.ReplyR, root.HasOwnSecrets);
        normalReplyLimit = replyLimit = fit.Finalists;
        replyNodes = replyLimit == 0 ? 0 : budget.ReplyR;
    }
    // Replies usually spend about half their reservation; the rest buys further finalists, up to the tier's, by commitment.
    // replan-reserve: about half of the turns search again, so on a clock-limited search these extras (never the finalists the
    // cap paid for, never the first three) stop at ExtraFinalistShare of the cap, unless the best line ends after one action.
    internal const double ExtraFinalistShare = .6;
    private bool Refill()
    {
        if (!refit || refills == null || scored.Count >= profile.Budget.ReplyL) return false;
        var next = refills.FirstOrDefault(n => !leaves.Contains(n));
        if (next == null || cap - steps - verificationReserve < next.RebuildNodes + perReplyAllowance + 1) return false;
        if (scored.Count >= ComparisonFinalists && profile.Fixes.On("replan-reserve") && cap < profile.Budget.PlanNodes
            && steps + (steps - repliesFrom) / scored.Count > ExtraFinalistShare * cap
            && TurnLine.Rank(scored).First().Actions.Count(a => a.Type != PlayerTaskType.END_TURN) > 1) return false;
        leaves.Add(next); ObserveLeaf?.Invoke(next, "shortlisted");
        return true;
    }
    public void StepNode()
    {
        if (IsDone) return;
        steps++;
        if (!beam.IsDone) {
            beam.StepNode();
            if (beam.IsDone && beam.ScoredLeaves.Any(n => n.Value >= 999000)) { Finish(); return; }
        }
        else if (refit && !refitted) {
            refitted = true; Refit();
            if (replyLimit == 0) { Finish(); return; }
        }
        else if (replyLimit == 0) { Finish(); return; }
        else if (leaves == null) {
            // Retain a hold line alongside different levels of commitment; static board value alone would prune every AoE escape.
            var sorted = beam.ScoredLeaves.Where(n => !n.UnverifiedWin).OrderByDescending(n => n.Value).ThenByDescending(n => n.Preference).ThenByDescending(n => n.TurnFace).ThenBy(n => n.Actions.Count).ThenBy(n => n.Key, StringComparer.Ordinal).ToList();
            if (ObserveLeaf != null)
                foreach (var generated in sorted) ObserveLeaf(generated, "generated");
            originalHand = root.Facts.Own.Hand.Select(c => c.EntityId).ToArray();
            bool allocation = profile.Fixes.On("attack-allocation") && root.Facts.Opponent.Board.Count > 0;
            // Equivalent copies share a commitment; retain hand order because Outcast and edge cards depend on it.
            Func<TurnLine, string> commitment = n => allocation
                ? n.HandCommitment
                : string.Join(",", n.HandIds.Where(originalHand.Contains).OrderBy(id => id));
            leaves = sorted.Take(1).ToList();
            // Ordered without face-tiebreak: the leader may be the face hit, the hold stays the pass, so both get priced
            // and Rank only decides a true tie between completed replies.
            var hold = sorted.Where(n => originalHand.Length == 0 ? n.Actions.Count == 0
                    : !originalHand.Except(n.HandIds).Any())
                .OrderByDescending(n => n.Value).ThenByDescending(n => n.Preference).ThenBy(n => n.Actions.Count).ThenBy(n => n.Key, StringComparer.Ordinal)
                .FirstOrDefault();
            holdLine = hold; holdStaticValue = hold?.Value ?? double.PositiveInfinity;
            int available = normalReplyLimit - (hold != null && !leaves.Contains(hold) && normalReplyLimit > 1 ? 1 : 0);
            // Give reply search both equally priced effect states for the same commitment.
            if (normalReplyLimit > 2 && originalHand.Length > 0) {
                var pair = sorted.GroupBy(n => (n.Value, Hand: commitment(n)))
                    .Select(g => g.GroupBy(n => n.EffectsKey).Select(x => x.First()).Take(2).ToArray())
                    .FirstOrDefault(g => g.Length == 2);
                if (pair != null) {
                    var missing = pair.Where(n => !leaves.Contains(n)).ToArray();
                    if (leaves.Count + missing.Length <= available) leaves.AddRange(missing);
                }
            }
            foreach (var candidate in sorted.GroupBy(n => n.EffectsKey).Select(g => g.First())) {
                if (leaves.Count >= Math.Max(1, normalReplyLimit / 3)) break;
                if (!leaves.Contains(candidate)) leaves.Add(candidate);
            }
            foreach (var candidate in sorted.GroupBy(n => originalHand.Length == 0
                    ? n.BoardCount + ":" + n.HandIds.Length
                    : commitment(n))
                .Select(g => g.First())) {
                if (leaves.Count == normalReplyLimit) break;
                if (!leaves.Contains(candidate)) leaves.Add(candidate);
            }
            if (hold != null && !leaves.Contains(hold) && normalReplyLimit > 1) {
                if (leaves.Count == normalReplyLimit) leaves.RemoveAt(leaves.Count - 1); leaves.Add(hold);
            }
            foreach (var candidate in sorted) {
                if (leaves.Count >= normalReplyLimit) break;
                if (!leaves.Contains(candidate)) leaves.Add(candidate);
            }
            if (DiagnosticFinished != null) {
                DiagnosticOriginalFinalists = leaves.ToArray();
                DiagnosticLeader = sorted.FirstOrDefault(); DiagnosticHold = hold;
                if (normalReplyLimit > 2 && profile.Fixes.On("survival-scoring"))
                    DiagnosticBestDefense = sorted.OrderByDescending(n => n.PublicAttackMargin).FirstOrDefault();
            }
            var reserved = ReserveFinalists(leaves, sorted, hold, normalReplyLimit, comparePass,
                profile.Fixes.On("survival-scoring"));
            DiagnosticDefense = reserved.Defense; DiagnosticEvicted = reserved.Evicted;
            if (allocation && replyLimit > normalReplyLimit) {
                var outcomes = new HashSet<string>(leaves.Select(n => n.CombatOutcomeKey), StringComparer.Ordinal);
                var combat = sorted.FirstOrDefault(n => !outcomes.Contains(n.CombatOutcomeKey));
                if (combat != null) leaves.Add(combat);
            }
            originalFinalistCount = leaves.Count;
            if (refit) refills = sorted.GroupBy(commitment).Select(g => g.First()).Concat(sorted.GroupBy(n => n.EffectsKey).Select(g => g.First())).Concat(sorted).Distinct().ToList();
            if (ObserveLeaf != null)
                foreach (var finalist in leaves) ObserveLeaf(finalist, "shortlisted");
            // Score the best static line first and the hold/AoE-escape line second on wall-guarded profiles.
            // Candidates also preserves a public-board-screened escape if completed replies all lose. Scores do not depend
            // on this order (each reply is seeded alike and Rank ends on the line key), so full searches are unchanged;
            // a node cap inside reply scoring can differ, so only wall-guarded (elite+) profiles reorder.
            int at = hold == null ? -1 : leaves.IndexOf(hold);
            if (!comparePass && at > 1 && profile.Budget.TurnWallSeconds > 0) { leaves.RemoveAt(at); leaves.Insert(1, hold); }
        }
        else if (index < leaves.Count) {
            if (reply == null) {
                if (profile.Fixes.On("attack-allocation")) {
                    var finalist = leaves[index];
                    // An optional reply (combat slot or hold extension) must fit in the remaining budget, including every
                    // released ancestor. It is admitted like a normal finalist: search allowance here, verification from the
                    // shared reserve (NodeAllowance adds a per-sample worst-case witness since 4a1a88d4, ~2.5x at master).
                    // An unadmitted, never-priced line must not stay a finalist.
                    if (index >= normalReplyLimit && cap - steps < finalist.RebuildNodes +
                        ReplyModel.SearchAllowance(replyK, replyNodes, root.HasOwnSecrets)) { leaves.RemoveAt(index); Finish(); return; }
                    if (!finalist.Holds) {
                        finalist.RebuildStep();
                        if (steps >= cap) Finish();
                        return;
                    }
                }
                TurnSearchTrace.Observe?.Invoke("finalist", leaves[index].Actions, leaves[index].Value);
                reply = ReplyModel.Job(leaves[index].State, profile, seed, evaluator, played, replyNodes, replyK);
            }
            if (!reply.IsDone) reply.StepNode();
            if (reply.IsDone) {
                var line = leaves[index++]; line.Value = reply.Value; line.Survival = reply.Survival;
                if (line.Survival.HasValue && GoldenAblation.SoftVeto) {
                    // Probability-weighted loss instead of a lexicographic veto of every risky line.
                    line.Value = reply.Value - (1 - line.Survival.Value) * 400; line.Survival = null;
                }
                if (line.Survival.HasValue) {
                    line.Resistance = evaluator.Resistance(line.State.Facts);
                    var enemy = line.State.Facts.Opponent.Hero; var before = root.Facts.Opponent.Hero;
                    line.FaceDamage = (long)before.Health + before.Armor - enemy.Health - enemy.Armor;
                    line.UnusedResources = DeckPlanEvaluator.UnusedResources(line.State);
                    // Numeric telemetry stays in the terminal-loss band; Rank keeps exact lexicographic priority.
                    line.Value = -1000000 + line.Survival.Value * 500 + Math.Atan(line.Resistance) * 100
                        + Math.Atan(line.FaceDamage) + Math.Atan(-line.UnusedResources) * .01;
                }
                ObserveLeaf?.Invoke(line, "reply");
                TurnSearchTrace.Observe?.Invoke("reply", line.Actions, line.Value);
                scored.Add(line); reply = null;
            }
        }
        else {
            if (!ExtendHoldComparison() && !Refill()) { Finish(); return; }
        }
        if (steps >= cap) Finish();
    }
    // Pass validation owns pass + static leader (or best continuation if pass leads). Resolve those
    // slots first; at three replies the optional hold must yield to defense, never the reserved defense.
    internal static (TurnLine Defense, TurnLine Evicted) ReserveFinalists(List<TurnLine> leaves,
        List<TurnLine> sorted, TurnLine hold, int replyLimit, bool comparePass, bool defenseEnabled)
    {
        var leader = sorted.FirstOrDefault();
        var pass = comparePass ? sorted.FirstOrDefault(n => n.Actions.Count == 0) : null;
        if (pass != null) {
            leaves.Remove(pass);
            if (leaves.Count >= replyLimit) leaves.RemoveAt(leaves.Count - 1);
            leaves.Insert(0, pass);
            if (replyLimit > 1 && !leaves.Any(n => n.Actions.Count > 0)) {
                var continuation = sorted.FirstOrDefault(n => n.Actions.Count > 0);
                if (continuation != null) leaves.Add(continuation);
            }
            // Base capped-pass only guarantees some continuation. The fix retains the best one even
            // when hand diversity already supplied weaker continuations; OFF, and replyLimit <= 2 where
            // no defense can be reserved, keep that base behavior.
            if (defenseEnabled && replyLimit > 2) {
                var continuation = leader == pass ? sorted.FirstOrDefault(n => n.Actions.Count > 0) : leader;
                if (continuation != null) {
                    leaves.Remove(continuation);
                    if (leaves.Count >= replyLimit) leaves.RemoveAt(leaves.Count - 1);
                    leaves.Insert(1, continuation);
                }
            }
        }
        var protectedLine = leader == pass ? sorted.FirstOrDefault(n => n.Actions.Count > 0) : leader;
        // 50% of effective life in public/prior incoming damage is a coverage heuristic, not a lethal proof.
        if (!defenseEnabled || replyLimit <= 2 || leader == null || leader.PublicAttackDamage <= 0
            || leader.PublicAttackDamage * 2 < leader.PublicAttackMargin + leader.PublicAttackDamage) return (null, null);
        var defense = sorted.OrderByDescending(n => n.PublicAttackMargin).First();
        if (leaves.Contains(defense) || defense.PublicAttackMargin <= leaves.Max(n => n.PublicAttackMargin)) return (null, null);
        int replace = leaves.FindLastIndex(n => n != protectedLine && n != pass && (comparePass || n != hold));
        if (leaves.Count >= replyLimit && replace < 0) return (null, null);
        var evicted = leaves.Count >= replyLimit ? leaves[replace] : null;
        if (evicted != null) leaves.RemoveAt(replace);
        leaves.Add(defense);
        return (defense, evicted);
    }
    private bool ExtendHoldComparison()
    {
        if (!profile.Fixes.On("commit-useful-plays") || holdLine == null || scored.Count == 0
            // Without face-tiebreak: a tied face hit must not skip the extension, nor a strict hand-keeping winner gain one.
            || steps + perReplyAllowance + 1 > cap || TurnLine.RankWithoutFace(scored).First() != holdLine) return false;
        // A hold only beat the initial shortlist. Price an affordable, statically useful smaller commitment
        // before trusting it; a costly draw/combo prefix must not crowd out its quiet development alternative.
        // Compare recorded action keys: rebuilding released engine states here would bypass frame/node accounting.
        var tested = new HashSet<string>(leaves.Select(n => n.Key), StringComparer.Ordinal);
        var next = beam.ScoredLeaves.Where(n => !n.UnverifiedWin && n.Value > holdStaticValue
                && !leaves.Contains(n) && originalHand.Except(n.HandIds).Any())
            .OrderBy(n => originalHand.Except(n.HandIds).Count()).ThenByDescending(n => n.Value)
            .ThenByDescending(n => n.Preference).ThenByDescending(n => n.TurnFace).ThenBy(n => n.Actions.Count).ThenBy(n => n.Key, StringComparer.Ordinal)
            .FirstOrDefault(n => !tested.Contains(n.Key));
        if (next == null) return false;
        leaves.Add(next); ObserveLeaf?.Invoke(next, "shortlisted"); return true;
    }
    // Read-only progress view for "hover while thinking": reply finalists or the checked defensive escape, otherwise
    // the own beam's statically best lines so far. Root actions only; never alters the search. Ranked at most once
    // per beam depth / scored finalist (hover polls it every gated frame, outside search accounting).
    private IReadOnlyList<KeyValuePair<BotAction, double>> leaders;
    private long leadersKey = -1;
    public IReadOnlyList<KeyValuePair<BotAction, double>> Leaders
    {
        get {
            if (IsDone) return Result.Candidates;
            long key = ((long)scored.Count << 32) | ((long)beam.Depth << 1) | (beam.IsDone ? 1L : 0L);
            if (leaders != null && key == leadersKey) return leaders;
            var lines = Candidates();
            leaders = GoldenData.Copy(TurnLine.Rank(lines.Where(n => n.Actions.Count > 0)).Select(n => new KeyValuePair<BotAction, double>(n.Actions[0], n.Value))
                .GroupBy(p => p.Key).Select(g => g.First()).Take(3));
            leadersKey = key;
            return leaders;
        }
    }
    internal PlannedTurn StopSearch()
    {
        if (!IsDone) Finish();
        return Result;
    }
    private IEnumerable<TurnLine> Candidates()
    {
        if (scored.Count == 0) return beam.ScoredLeaves.Concat(beam.Progress);
        if (comparePass) return scored; // Validation never compares a sampled pass with an unchecked static escape.
        // A terminal Secret-free sample cannot take a reply, but remains a normal, uncertified candidate.
        var uncertainWins = root.Facts.OpponentSecretCount > 0
            ? beam.ScoredLeaves.Where(n => n.UnverifiedWin).ToArray() : Array.Empty<TurnLine>();
        // Compare like with like. A completed losing reply cannot erase an unchecked defensive finalist.
        if (scored.All(n => n.Survival == 0) && leaves != null) {
            var safe = leaves.Concat(beam.ScoredLeaves).Distinct()
                .Where(n => !scored.Contains(n) && n.PublicBoardSafe && !n.UnverifiedWin).ToArray();
            if (safe.Length > 0) return safe;
        }
        return uncertainWins.Length == 0 ? scored : scored.Concat(uncertainWins);
    }
    private void Finish()
    {
        var candidates = Candidates().ToList();
        var best = TurnLine.Rank(candidates).FirstOrDefault();
        if (best == null) {
            var action = root.OrderedActions.FirstOrDefault();
            Result.Line = action == null ? new BotAction[0] : new[] { action };
        } else {
            var line = new List<BotAction>(best.Actions);
            // A line stopped by TurnBeam.MaxLineActions is not a finished turn: play its prefix, then replan.
            bool finished = (!best.UnverifiedWin || best.UncertifiedEndTurnWin) && best.Actions.LastOrDefault()?.Type != PlayerTaskType.END_TURN
                && best.Actions.Count < TurnBeam.MaxLineActions
                && (best.UncertifiedEndTurnWin || best.Completed);
            // A released finished line rebuilds its clone for END_TURN (at most MaxLineActions Applies, as OFF always does).
            // Under the flag an unfinished prefix, such as a partially rebuilt finalist, is returned without rebuilding.
            var end = finished || !profile.Fixes.On("attack-allocation")
                ? best.State.Actions.FirstOrDefault(a => a.Type == PlayerTaskType.END_TURN) : null;
            if (end != null && finished) line.Add(end);
            Result.Line = GoldenData.Copy(line); Result.Value = best.Value;
        }
        Result.Nodes = steps;
        DiagnosticFinished?.Invoke(this);
        // Fewer samples are not the tier's verdict: a pass from them must face pass validation.
        Result.Interrupted = steps >= cap || beam.Capped || !beam.IsDone || replyK < profile.Budget.ReplyK && scored.Count > 0;
        Result.Stop = Math.Abs(Result.Value) >= 999000 ? SearchStop.Terminal : steps >= cap || beam.Capped ? SearchStop.Nodes : SearchStop.Exhausted;
        var endRoot = root.Actions.FirstOrDefault(a => a.Type == PlayerTaskType.END_TURN);
        Result.Candidates = GoldenData.Copy(candidates.Select(n => new KeyValuePair<BotAction, double>(
                n.Actions.Count == 0 ? endRoot : n.Actions[0], n.Value)).Where(p => p.Key != null)
            .GroupBy(p => p.Key).Select(g => new KeyValuePair<BotAction, double>(g.Key, g.Max(p => p.Value)))
            .OrderBy(p => p.Key.ToString(), StringComparer.Ordinal));
        IsDone = true;
    }
}
