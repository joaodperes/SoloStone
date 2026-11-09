using System;
using System.Collections.Generic;
using System.Linq;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using SabberStoneCore.Tasks.PlayerTasks;

// Bounded own-turn search. Found is checked on four artificial seeds; this is sampling, not a proof.
public enum LethalOutcome { None, Found, Incomplete }

public sealed class LethalResult
{
    public LethalOutcome Outcome { get; }
    public IReadOnlyList<BotAction> Line { get; }
    public int Nodes { get; }
    public LethalResult(LethalOutcome outcome, IReadOnlyList<BotAction> line, int nodes)
    { Outcome = outcome; Line = line ?? new BotAction[0]; Nodes = nodes; }
    public static readonly LethalResult None = new LethalResult(LethalOutcome.None, null, 0);
}

public sealed class LethalJob : INodeJob
{
    private sealed class Node
    {
        internal FairBotState State;
        internal readonly BotAction Action;
        internal readonly Node Parent;
        internal readonly int Depth;
        internal Node(FairBotState state, BotAction action = null, Node parent = null)
        { State = state; Action = action; Parent = parent; Depth = parent == null ? 0 : parent.Depth + 1; }
    }

    private readonly Stack<Node> frontier = new Stack<Node>();
    private readonly int turn;
    private int nodes, sample, replayIndex;
    private bool unknownBoundary;
    private BotAction[] candidate;
    private FairBotState replay;
    private IReadOnlyList<string[]> secretWorlds;
    private int secretWorld;
    private bool worldsLoaded;
    internal bool SecretCoverageUnavailable { get; private set; }
    private Func<FairBotState, double> evaluate;
    internal double? RefutedValue { get; private set; }
    private double[] worldWeights;
    private double replayWeight, replayValue, totalWeight, baselineValue;
    private bool measureReplies, allRepliesWon = true;
    private double verifiedReplyWins, testedReplyWeight;
    // Unknown mass is distinct from both verified wins and completed counterexamples.
    internal double VerifiedReplyWinShare => measureReplies && totalWeight > 0
        ? Math.Max(0, Math.Min(1, verifiedReplyWins / totalWeight)) : 0;
    internal double UntestedReplyShare => measureReplies
        ? totalWeight > 0 ? Math.Max(0, Math.Min(1, 1 - testedReplyWeight / totalWeight)) : 1 : 0;
    internal double? SecretHeuristicValue => evaluate != null && State.Facts.OpponentSecretCount > 0 && replayWeight > 0
        ? (replayValue + (totalWeight - replayWeight) * baselineValue) / totalWeight : (double?)null;

    private readonly Action charge;
    private readonly BotAction[] rootActions;
    private readonly bool immediate;
    public LethalJob(FairBotState state, int cap, Action charge = null)
        : this(state, cap, charge, null) { }
    internal LethalJob(FairBotState state, int cap, Action charge, BotAction[] rootActions, bool immediate = false)
    {
        if (state == null) throw new ArgumentNullException(nameof(state));
        if (cap < 0) throw new ArgumentOutOfRangeException(nameof(cap));
        this.charge = charge; this.rootActions = rootActions; this.immediate = immediate; State = state; Cap = cap; turn = state.Facts.Turn;
        Result = new LethalResult(LethalOutcome.Incomplete, null, 0);
        if (cap == 0) IsDone = true;
        else frontier.Push(new Node(state));
    }
    public FairBotState State { get; }
    public int Cap { get; }
    public bool IsDone { get; private set; }
    public LethalResult Result { get; private set; }
    // Actions in the deepest node expanded so far (decision cost telemetry).
    internal int Deepest { get; private set; }

    internal static LethalJob Verify(FairBotState state, IReadOnlyList<BotAction> line, int cap, Func<FairBotState, double> evaluate = null,
        bool measureReplies = false)
    {
        var job = new LethalJob(state, cap);
        job.frontier.Clear(); job.candidate = line.ToArray(); job.sample = 1; job.evaluate = evaluate; job.measureReplies = measureReplies;
        return job;
    }

    public void StepNode()
    {
        if (IsDone) return;
        nodes++; charge?.Invoke();
        // Also expose honest accounting when a shared scheduler stops before this job's own cap.
        Result = new LethalResult(LethalOutcome.Incomplete, null, nodes);
        try {
            if (candidate != null) ReplayNode();
            else ExpandNode();
        }
        catch (Exception error) when (FairHistory.IsPrivate(error)) {
            unknownBoundary = true;
            RejectCandidate();
        }
        catch (SimulationFailureException error) {
            State.ReportFault(error); unknownBoundary = true; RejectCandidate();
        }
        if (IsDone) return;
        if (candidate == null && frontier.Count == 0)
            Finish(unknownBoundary ? LethalOutcome.Incomplete : LethalOutcome.None);
        else if (nodes == Cap) Finish(LethalOutcome.Incomplete);
    }

    private void ExpandNode()
    {
        var node = frontier.Pop();
        if (node.Depth > Deepest) Deepest = node.Depth;
        if (node.Action != null) node.State = node.State.Apply(node.Action);
        var facts = node.State.Facts;
        // Terminal wins cannot erase an unknown draw earlier in the same action.
        if (HasUnknownOwnCards(facts))
        {
            unknownBoundary = true;
            return;
        }
        if (facts.Own.PlayState == PlayState.WON)
        {
            var line = new List<BotAction>();
            for (var step = node; step.Action != null; step = step.Parent) line.Add(step.Action);
            line.Reverse();
            candidate = line.ToArray();
            if (candidate.Length == 0) Finish(LethalOutcome.Found);
            else { sample = 1; replayIndex = 0; replay = null; }
            return;
        }
        // The domain ends after END_TURN (including its triggers), even when it grants an extra turn.
        if (node.Action?.Type == PlayerTaskType.END_TURN || facts.Turn != turn
            || facts.CurrentPlayerId != facts.Own.PlayerId || facts.Own.PlayState != PlayState.PLAYING) return;
        // A free repeatable play would otherwise hold one state per action down an endless path (TurnBeam.MaxLineActions).
        if (!node.State.CanExpand || node.Depth >= TurnBeam.MaxLineActions)
        {
            unknownBoundary = true;
            return;
        }
        // No damage bound is sound for arbitrary triggers. Ordinary search keeps all legal actions and placements.
        // Describe/facts omit history and attachments, so no unsafe transposition merging either.
        var actions = node.Parent == null && rootActions != null ? rootActions : immediate ? ImmediateActions(node.State) : OrderedActions(node.State);
        for (int i = actions.Length - 1; i >= 0; i--)
            frontier.Push(new Node(node.State, actions[i], node));
    }

    private void ReplayNode()
    {
        // Replay every candidate, including apparently deterministic actions with random triggers.
        // Each replay action costs one scheduler node; never hide an entire line inside one step.
        if (!worldsLoaded) {
#if ENGINE_SOURCE
            secretWorlds = State.PlausibleOpponentSecretWorlds(out bool complete, out double priorMass);
            SecretCoverageUnavailable = !complete;
#else
            secretWorlds = Array.Empty<string[]>();
            SecretCoverageUnavailable = State.Facts.OpponentSecretCount != 0;
            double priorMass = 1;
#endif
            worldsLoaded = true;
            if ((evaluate != null || measureReplies) && State.Facts.OpponentSecretCount > 0 && secretWorlds.Count > 0) {
                if (evaluate != null) baselineValue = Math.Max(-10000, Math.Min(10000, evaluate(State)));
                var prior = CardFrequencies.ForOrEmpty(State.ReplyClass).ToDictionary(f => f.CardId, f => f.Weight);
                worldWeights = secretWorlds.Select(world => world.Aggregate(1.0,
                    (weight, id) => weight * (prior.TryGetValue(id, out double frequency) ? frequency : .001))).ToArray();
                totalWeight = 4 * (priorMass > 0 ? priorMass : 1);
            }
            if (measureReplies && totalWeight <= 0) totalWeight = 4 * Math.Max(1, secretWorlds.Count);
        }
        unknownBoundary |= SecretCoverageUnavailable;
        if (replay == null) {
#if ENGINE_SOURCE
            replay = secretWorlds.Count == 0 ? State.WithArtificialSeed(sample)
                : State.WithOpponentSecrets(secretWorlds[secretWorld], sample);
#else
            replay = State.WithArtificialSeed(sample);
#endif
        }
        var action = candidate[replayIndex];
        if (!replay.Actions.Contains(action)) { ReplayFailed(); return; }
        replay = replay.Apply(action);
        replayIndex++;
        var facts = replay.Facts;
        if (HasUnknownOwnCards(facts))
        {
            unknownBoundary = true;
            RejectCandidate();
            return;
        }
        if (facts.Own.PlayState == PlayState.WON)
        {
            AdvanceReplay(true);
        }
        else if (replayIndex == candidate.Length || !replay.CanExpand
            || action.Type == PlayerTaskType.END_TURN || replay.Facts.Turn != turn) ReplayFailed();
    }

    private void ReplayFailed()
    {
        if (measureReplies) { AdvanceReplay(false); return; }
        if (worldWeights == null) { RejectCandidate(); return; }
        AdvanceReplay(false);
    }
    private void AdvanceReplay(bool? won)
    {
        if (measureReplies) {
            double weight = worldWeights == null ? 1 : worldWeights[secretWorld];
            if (won.HasValue) testedReplyWeight += weight;
            if (won == true) verifiedReplyWins += weight;
            allRepliesWon &= won == true;
        }
        if (worldWeights != null && evaluate != null) {
            double weight = worldWeights.Length == 0 ? 1 : worldWeights[secretWorld];
            // A sampled win earns a bounded heuristic bonus, never a terminal score. Untested mass keeps the root estimate.
            double value = won == true ? baselineValue + 200 : Math.Max(-10000, Math.Min(10000, evaluate(replay)));
            replayValue += weight * value; replayWeight += weight;
        }
        // Certificates stop at one counterexample; reply risk measures the remaining holdouts within the same cap.
        if ((!measureReplies && won != true) || sample == 4 && secretWorld + 1 >= secretWorlds.Count)
            Finish((measureReplies ? unknownBoundary : SecretCoverageUnavailable) ? LethalOutcome.Incomplete
                : won == true && (!measureReplies || allRepliesWon) ? LethalOutcome.Found : LethalOutcome.None);
        else {
            if (sample == 4) { secretWorld++; sample = 1; } else sample++;
            replayIndex = 0; replay = null;
        }
    }

    private static bool HasUnknownOwnCards(FairFacts facts) =>
        facts.Own.Hand.Concat(facts.Own.Board).Concat(facts.Own.Locations).Any(c => c.Unknown);

    private void RejectCandidate()
    {
        if (measureReplies && candidate != null) {
            if (worldsLoaded && replay != null) AdvanceReplay(null);
            else Finish(LethalOutcome.Incomplete);
            return;
        }
        if (replay != null && evaluate != null) RefutedValue = evaluate(replay);
        candidate = null; replay = null; secretWorld = 0;
    }

    private void Finish(LethalOutcome outcome)
    {
        Result = new LethalResult(outcome,
            outcome == LethalOutcome.Found ? Array.AsReadOnly(candidate) : null, nodes);
        IsDone = true;
        frontier.Clear();
        candidate = null; replay = null;
    }

    // shortcut: this bounded probe can miss indirect non-Taunt combat combos; ordinary search remains unrestricted.
    // Fresh per-decision probe: face first, then removal/reach and cheap Taunt hits. Legal options and Apply own
    // every combat restriction and trigger; arithmetic alone cannot certify damage through Secrets or retaliation.
    internal static BotAction[] ImmediateActions(FairBotState state)
    {
        var facts = state.Facts;
        var attackers = facts.Own.Board.Concat(new[] { facts.Own.Hero }).ToDictionary(c => c.EntityId);
        bool Attack(BotAction a) => a.Type == PlayerTaskType.MINION_ATTACK || a.Type == PlayerTaskType.HERO_ATTACK;
        int Damage(BotAction a) => attackers.TryGetValue(a.Source, out var c) ? c.Attack : 0;
        return OrderedActions(state)
            .Where(a => !Attack(a) || a.Target == facts.Opponent.Hero.EntityId
                || facts.Opponent.Board.Any(c => c.EntityId == a.Target && c.Taunt))
            .OrderBy(a => Attack(a) && a.Target == facts.Opponent.Hero.EntityId ? 0 : Attack(a) ? 2 : 1)
            .ThenBy(a => Attack(a) && a.Target == facts.Opponent.Hero.EntityId ? -(long)Damage(a) : Attack(a) ? Damage(a) : 0)
            .ToArray();
    }

    internal static BotAction[] OrderedActions(FairBotState state) => state.Actions.OrderBy(a => Priority(a, state.Facts))
        .ThenBy(a => a.ToString(), StringComparer.Ordinal).ToArray();

    private static int Priority(BotAction action, FairFacts facts)
    {
        if (action.Type == PlayerTaskType.END_TURN) return 100;
        if (action.Type == PlayerTaskType.CHOOSE) return -2;
        var source = facts.Own.Hand.Concat(facts.Own.Board).Concat(facts.Own.Locations)
            .FirstOrDefault(c => c.EntityId == action.Source);
        // Fissure enables hero damage, Warptooth and the Fire bonus before attacks/Volcano.
        if (source?.CardId == "CATA_582") return -1;
        bool attack = action.Type == PlayerTaskType.MINION_ATTACK || action.Type == PlayerTaskType.HERO_ATTACK;
        if (attack && action.Target == facts.Opponent.Hero.EntityId) return 0;
        if (facts.Opponent.Board.Any(c => c.EntityId == action.Target && c.Taunt)) return 1;
        if (attack) return 2;
        if (action.Type == PlayerTaskType.PLAY_CARD && source != null)
        {
            // All spells precede location use: includes damage, buffs and Fire enablers without card-text parsing.
            if (source.Type == CardType.SPELL || source.Type == CardType.WEAPON
                || source.Tags.TryGetValue(GameTag.CHARGE, out int charge) && charge != 0
#if ENGINE_SOURCE
                || source.Tags.TryGetValue(GameTag.RUSH, out int rush) && rush != 0
#endif
                || source.CardId == "EDR_492" || source.CardId == "CAP_107") return 2;
        }
        if (action.Type == PlayerTaskType.HERO_POWER) return 3;
#if ENGINE_SOURCE
        if (action.Type == PlayerTaskType.ACTIVATE_LOCATION) return 3;
#endif
        return 4;
    }
}

public static class LethalSolver
{
    public static LethalJob Job(FairBotState state, int cap) => new LethalJob(state, cap);

    public static LethalResult Find(FairBotState state, int cap)
    {
        var job = Job(state, cap);
        var scheduler = new NodeScheduler(cap);
        scheduler.Enqueue(job);
        while (scheduler.Step(cap) == NodeStepResult.Running) { }
        return job.Result;
    }
}

// Gate for extra lethal work (NpcTurnRunner's late pool and the fresh decision probe): a cheap, generous estimate of the
// most damage the owner could still deal this turn. Arbitrary triggers make no bound sound, so it never prunes ordinary LethalJob;
// a wrong "implausible" only withholds extra work; ordinary lethal search is unchanged.
// Known gaps (2026-10-28 review; accepted, a miss falls back to the no-pool behaviour): board-wide attack buffs count
// once, not per attacker; multi-summon Charge counts the largest printed number only; mana gain and in-turn cost
// reduction are ignored except The Coin (GAME_005), and cards costing more than the mana left are dropped; a Windfury
// weapon or Charge+Windfury minion in hand with no ready board gets one swing; "damage equal to" text gets 2 x cost + 2;
// tag-held dynamic numbers, spell power played this turn and readying exhausted attackers are not counted. It reads
// English card text, so a text-free engine would make most spells look implausible.
internal static class LethalBound
{
    private static readonly System.Text.RegularExpressions.Regex Number = new System.Text.RegularExpressions.Regex(@"\d+");
    internal static bool Plausible(FairFacts f) =>
        f != null && f.Own.PlayState == PlayState.PLAYING && UpperBound(f) >= (long)f.Opponent.Hero.Health + f.Opponent.Hero.Armor;
    internal static long UpperBound(FairFacts f)
    {
        var own = f.Own;
        if (own.Hand.Concat(own.Board).Concat(own.Locations).Any(c => c.Unknown)) return long.MaxValue;
        int mana = own.Mana + own.Hand.Count(c => c.CardId == "GAME_005");
        var castable = own.Hand.Where(c => c.Cost <= mana).ToArray();
        long board = 0, damage = 0;
        foreach (var minion in own.Board)
        {
            if (minion.Frozen || minion.Exhausted || Tag(minion, GameTag.CANT_ATTACK) != 0) continue;
            board += (long)Math.Max(0, minion.Attack) * Swings(minion);
        }
        var hero = own.Hero;
        if (!hero.Frozen && !hero.Exhausted)
            board += (long)Math.Max(Math.Max(0, hero.Attack), own.Weapon?.Attack ?? 0) * Math.Max(Swings(hero), own.Weapon == null ? 1 : Swings(own.Weapon));
        damage += board;
        // Triggered and spell damage: every printed amount on the board, once per castable card plus once more.
        foreach (var minion in own.Board.Concat(own.Locations))
        {
            string text = Text(minion);
            if (text.Contains("damage")) damage += (long)MaxNumber(text) * (castable.Length + 1);
            damage += (long)Math.Max(0, Tag(minion, GameTag.SPELLPOWER)) * castable.Count(c => c.Type == CardType.SPELL);
        }
        // Hand, hero power and locations: best credit per mana within the mana left (fractional, so an upper bound).
        var items = new List<KeyValuePair<int, long>>();
        foreach (var card in castable)
        {
            string text = Text(card);
            long credit = Math.Max(Math.Max(0, card.Attack), MaxNumber(text));
            if (credit == 0 && (text.Contains("damage") || text.Contains("attack"))) credit = 2 * card.Cost + 2;
            if (text.Contains("windfury") || text.Contains("attack again") || text.Contains("double")) credit += board;
            items.Add(new KeyValuePair<int, long>(Math.Max(0, card.Cost), credit));
        }
        if (!own.HeroPower.Exhausted && own.HeroPower.Cost <= mana)
            items.Add(new KeyValuePair<int, long>(Math.Max(0, own.HeroPower.Cost), Math.Max(2, MaxNumber(Text(own.HeroPower)))));
        foreach (var location in own.Locations.Where(l => !l.Exhausted && l.Durability > 0))
            items.Add(new KeyValuePair<int, long>(0, Math.Max(3, MaxNumber(Text(location)))));
        double left = mana;
        foreach (var item in items.OrderByDescending(i => i.Key == 0 ? double.PositiveInfinity : (double)i.Value / i.Key))
        {
            if (item.Key == 0) { damage += item.Value; continue; }
            if (left <= 0) break;
            double share = Math.Min(1, left / item.Key);
            damage += (long)Math.Ceiling(item.Value * share); left -= item.Key;
        }
        return damage;
    }
    private static int Swings(FairCard card) => Tag(card, GameTag.MEGA_WINDFURY) != 0 ? 4 : Tag(card, GameTag.WINDFURY) != 0 ? 2 : 1;
    private static int Tag(FairCard card, GameTag tag) => card.Tags.TryGetValue(tag, out int value) ? value : 0;
    private static string Text(FairCard card)
    {
        if (card?.CardId == null) return "";
        try { return (Cards.FromId(card.CardId)?.Text ?? "").ToLowerInvariant(); }
        catch (Exception error) when (!(error is OutOfMemoryException)) { return ""; }
    }
    private static int MaxNumber(string text)
    {
        int best = 0;
        foreach (System.Text.RegularExpressions.Match match in Number.Matches(text))
            if (int.TryParse(match.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int value)) best = Math.Max(best, value);
        return best;
    }
}

// Keeps the fresh probe inside the runner's existing lethal scheduler, before the shared turn reserve.
internal sealed class LethalSequence : INodeJob
{
    private readonly LethalJob first, next;
    internal LethalSequence(LethalJob first, LethalJob next) { this.first = first; this.next = next; }
    public bool IsDone => first != null && first.Result.Outcome == LethalOutcome.Found
        || (first == null || first.IsDone) && next.IsDone;
    public void StepNode() { if (IsDone) return; if (first != null && !first.IsDone) first.StepNode(); else next.StepNode(); }
}
