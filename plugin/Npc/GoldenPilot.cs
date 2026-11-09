using System;
using System.Collections.Generic;
using System.Linq;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using SabberStoneCore.Tasks.PlayerTasks;

public sealed class GoldenPilot : IFrameSlicedGoldenPilot, IGoldenPilotHost
{
    // Defaults off; validated source-engine Deck startup opts in. Windows lanes stay serial.
    public static bool ParallelLethalEnabled { get; set; }
    internal static void ConfigureLethalHost(bool deck)
    {
#if ENGINE_SOURCE
        ParallelLethalEnabled = deck;
#else
        ParallelLethalEnabled = false;
#endif
    }
    internal Action<TurnPlannerJob> ObservePlanner { get; set; }
    internal Action<ParallelLethalSearch> ObserveLethalSearch { get; set; }
    public NpcProfile Profile { get; }
    public DeckPlan Plan { get; }
    public DriverOptions Options { get; }
    public int RemainingNodes { get; private set; }
    public int RemainingLethalNodes { get; private set; }
    public bool PlanReused { get; private set; }
    public LethalOutcome LastLethal { get; private set; }
    // Deepest line of the last completed decision's searches (decision cost telemetry); harness callers may reset it.
    public int LastDeepest { get; internal set; }
    private int turn = -1, turnSeed;
    // Common reply samples across one turn's decisions: replans compare lines on the same hands.
    internal int TurnSeed => turnSeed;
    private string expected;
    // Plan reuse telemetry: the expected child's named parts and whether its step drew on randomness (two artificial
    // seeds disagree), so a miss can say why it replans. Never read by the search.
    private List<KeyValuePair<string, string>> expectedParts;
    private bool expectedRandom;
    private bool turnStart;
    // Why the cache is empty when no continuation is armed: plan-end (the line ran out) or not-cached (Accept refused:
    // choice boundary, Rewind source, one-action line, simulation fault).
    private string emptyReason;
    // Why the last Job could not continue its cached line ("reused" on a hit); see Diagnose.
    internal string ReplanReason { get; private set; }
    // AiFixes "plan-reuse": the reuse key leaves out the sampled own-deck prior. Off: the full public key (base).
    private bool PlanReuseFix => Profile.Fixes.On("plan-reuse");
    internal bool CachedInterrupted { get; private set; }
    private List<BotAction> cached = new List<BotAction>();
    private GoldenPublicInputs factoryInputs;
#if ENGINE_SOURCE
    private OwnDeckLedger deckObservations;
#endif
    public GoldenPublicInputs PublicInputs => factoryInputs;
    private readonly Func<MulliganView, IReadOnlyList<int>> mulligan;
    public GoldenPilot(NpcProfile profile, DeckPlan plan = null, Func<MulliganView, IReadOnlyList<int>> mulligan = null)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        Plan = plan ?? DeckPlans.ForId(profile.PlanId) ?? throw new ArgumentException("Unknown deck plan.");
        this.mulligan = mulligan;
        Options = new DriverOptions { Rewind = KeepRewind, FairViewFactory = (game, owner, seed, style) => {
            var fairOptions = new FairOptions {
                RemoveOpponentSecrets = true, ShowOwnCarriedCards = Profile.Fixes.On("carried-cards"), ShowPublicCaptures = Profile.Fixes.On("death-value") && Profile.Fixes.On("death-held"),
                FallbackEvaluator = DeckPlanEvaluator.ForFallback(Plan, Profile),
#if ENGINE_SOURCE
                TrackOwnDeck = true, MaterializeOwnDeckTriggers = true, Observations = deckObservations
#endif
            };
            var factoryView = FairBotState.Create(game, owner, seed, style, fairOptions);
#if ENGINE_SOURCE
            deckObservations = fairOptions.Observations;
#endif
            factoryInputs = new GoldenPublicInputs();
#if ENGINE_SOURCE
            // Project first: even public-play history contains unrevealed Secret identities before projection.
            var projected = factoryView.FingerprintGame;
            var bot = projected.Player1.PlayerId == owner ? projected.Player1 : projected.Player2;
            var enemy = bot == projected.Player1 ? projected.Player2 : projected.Player1;
            var played = projected.HistoryFacts.Query.Plays().Where(f => f.ControllerId == enemy.Id && f.DefinitionKnown && f.OriginKnown && f.StartedInDeck)
                .GroupBy(f => f.EntityId).Select(g => g.First())
                .Select(f => Cards.FromAssetId(f.DbfId).Id).ToArray();
            string source = null; var offers = new List<KeyValuePair<int, string>>();
            if (bot.Choice != null) {
                SabberStoneCore.Model.Entities.IPlayable actor;
                if (projected.IdEntityDic.TryGetValue(bot.Choice.SourceId, out actor)) source = actor.Card.Id;
                foreach (int id in bot.Choice.Choices) {
                    if (projected.IdEntityDic.TryGetValue(id, out actor)) offers.Add(new KeyValuePair<int, string>(id, actor.Card.Id));
                }
            }
            factoryInputs = new GoldenPublicInputs(played, source, offers);
#endif
            return factoryView;
        }};
    }
    // Preserve cached continuations and remaining turn budgets in both recheck arms.
    internal GoldenPilot ForkForRecheck(AiFixes fixes)
    {
        var copy = new GoldenPilot(Profile.WithFixes(fixes), Plan, mulligan);
        copy.turn = turn; copy.turnSeed = turnSeed; copy.expected = expected; copy.cached = new List<BotAction>(cached);
        copy.factoryInputs = factoryInputs; copy.RemainingNodes = RemainingNodes; copy.RemainingLethalNodes = RemainingLethalNodes;
        copy.LastLine = LastLine.ToArray(); copy.LastLineStamp = LastLineStamp; copy.CachedInterrupted = CachedInterrupted;
        copy.expectedParts = expectedParts; copy.expectedRandom = expectedRandom; copy.ReplanReason = ReplanReason; copy.emptyReason = emptyReason;
        return copy;
    }
    public GoldenDecisionJob Job(FairBotState view, GoldenContext context, GoldenPublicInputs publicInputs = null)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));
        if (context.NpcProfile.Id != Profile.Id) throw new ArgumentException("Context profile differs from pilot.");
        if (turn != context.Turn) { turn = context.Turn; turnSeed = context.ArtificialSeed; RemainingNodes = Profile.Budget.TurnNodes; RemainingLethalNodes = GoldenAblation.LethalNodesOverride > 0 ? 3 * GoldenAblation.LethalNodesOverride : Profile.Budget.TurnLethalNodes; cached.Clear(); expected = null; emptyReason = null; turnStart = true; }
        PlanReused = false; LastDeepest = 0;
        ReplanReason = Diagnose(view); turnStart = false;
        return new GoldenDecisionJob(this, view, context, publicInputs ?? context.PublicInputs ?? factoryInputs);
    }
    public IGoldenDecisionJob Begin(FairBotState view, GoldenContext context) => Job(view, context);
    public GoldenDecision Decide(FairBotState view, GoldenContext ctx)
    {
        var job = Begin(view, ctx); var scheduler = new NodeScheduler(RemainingNodes + Profile.Budget.LethalNodes + GoldenDecisionJob.LethalRecheckNodes + GoldenDecisionJob.PassValidationNodes + 8); scheduler.Enqueue(job);
        try
        {
            while (!job.IsDone && !scheduler.IsBudgetExhausted)
            {
                if (job is INpcBackgroundSearch background && background.Waiting)
                { background.PollBackground(); System.Threading.Thread.Sleep(1); }
                else scheduler.Step((job as INpcBackgroundSearch)?.BackgroundClock == true ? 1 : 1000);
            }
        }
        finally { (job as INpcBackgroundSearch)?.StopBackground(); }
        if (!job.IsDone) throw new InvalidOperationException("Golden decision exceeded its node allowance.");
        return job.Result;
    }
    public PilotDecision DecidePilot(FairBotState view, GoldenContext ctx) => Decide(view, ctx).ToPilotDecision();
    internal void MarkReplanned() { PlanReused = false; }
    internal void ChargeLethal() { RemainingLethalNodes--; }
    internal void ChargePlan(int nodes) { RemainingNodes = Math.Max(0, RemainingNodes - nodes); }
    // Clock cuts obey the same expected-state contract as ordinary reuse.
    internal IReadOnlyList<BotAction> Reuse(FairBotState view)
    {
        if (CanReuse(view)) { PlanReused = true; return cached.ToArray(); }
        cached.Clear(); expected = null; return null;
    }
    internal bool CanReuse(FairBotState view) =>
        !GoldenAblation.NoReuse && !ChoiceBoundary(view) && expected != null && cached.Count > 0
        && expected == PlanKey(view)
        && view.Actions.Any(a => a.Equals(cached[0]));
    private static bool ChoiceBoundary(FairBotState view) => view.HasPendingChoice || view.RewindKeep != null;
    // reused | turn-start | choice | plan-end | not-cached | ablation | random-outcome:<part> |
    // divergence:<part> | illegal-next (plus lethal, set by the job). Telemetry only; never read by the search.
    private string Diagnose(FairBotState view)
    {
        if (GoldenAblation.NoReuse) return "ablation";
        if (ChoiceBoundary(view)) return "choice";
        if (expected == null || cached.Count == 0) return turnStart ? "turn-start" : emptyReason ?? "not-cached";
        if (expected != PlanKey(view))
        {
            var parts = KeyParts(view);
            string part = expectedParts?.FirstOrDefault(p => !parts.Any(q => q.Key == p.Key && q.Value == p.Value)).Key ?? "key";
            return (expectedRandom ? "random-outcome:" : "divergence:") + part;
        }
        return view.Actions.Any(a => a.Equals(cached[0])) ? "reused" : "illegal-next";
    }
    // Named slices of the public state in key order, for the replan reason only. deck-prior (last) only when the key
    // still contains the sampled own-deck prior (plan-reuse off).
    private List<KeyValuePair<string, string>> KeyParts(FairBotState view)
    {
        var f = view.Facts;
        string Cards(IEnumerable<FairCard> cards) => string.Join(",", cards.OrderBy(c => c.EntityId).Select(c => c.EntityId + ":" + c.CardId + ":"
            + c.Cost + ":" + c.Attack + "/" + c.Health + ":" + c.Exhausted + ":" + string.Join(".", c.Tags.OrderBy(t => (int)t.Key).Select(t => (int)t.Key + "=" + t.Value))));
        string Player(FairPlayer p) => p.Mana + ":" + p.BaseMana + ":" + p.DeckCount + ":" + p.OverloadOwed + ":" + p.OverloadLocked + ":" + p.Hero.Health + "+" + p.Hero.Armor
            + ":" + p.Hand.Count + ":" + string.Join(",", p.SecretEntityIds) + ":" + Cards(new[] { p.Hero, p.HeroPower, p.Weapon }.Where(c => c != null));
        var parts = new List<KeyValuePair<string, string>>();
        void Add(string name, string value) => parts.Add(new KeyValuePair<string, string>(name, value));
        Add("turn", f.Turn + ":" + f.CurrentPlayerId + ":" + f.DistinctFriendlyCharactersDamagedThisTurn + ":" + f.OpponentSecretCount);
        Add("own", Player(f.Own)); Add("opponent", Player(f.Opponent)); Add("hand", Cards(f.Own.Hand)); Add("board", Cards(f.Own.Board));
        Add("enemy-board", Cards(f.Opponent.Board));
        Add("other", Cards(f.Own.Locations.Concat(f.Own.Auras).Concat(f.Own.Secrets).Concat(f.Own.Quests)
            .Concat(f.Opponent.Locations).Concat(f.Opponent.Auras).Concat(f.Opponent.Quests)));
        Add("effects", view.OwnVisibleEffectsKey());
        Add("actions", string.Join(",", view.Actions.Select(a => a.ToString()).OrderBy(a => a, StringComparer.Ordinal)));
        if (Profile.Fixes.On("carried-reuse")) Add("carried", view.OwnCarriedKey());
        if (!PlanReuseFix) Add("deck-prior", DeckPrior(f));
        return parts;
    }
    // GoldenPublicKey's own-deck segment, exactly as it appends it.
    private static string DeckPrior(FairFacts f)
    {
        var b = new System.Text.StringBuilder();
        foreach (var pair in f.OwnDeckKnowledge.OrderBy(p => p.Key, StringComparer.Ordinal)) b.Append('|').Append(pair.Key).Append('=').Append(pair.Value);
        return b.ToString();
    }
    // plan-reuse: the public key less its sampled own-deck prior. OwnDeckKnowledge is OwnDeckLedger.Sample with each
    // decision's artificial seed, not an observation: live 2026-11-01 (tater96) it differed between the planned child and
    // the next real view at every decision once the own deck had unidentified slots, so no plan was ever reused. Real deck
    // changes still show in deck count, hands, boards and actions. The segment sits right before the single "|effects:"
    // marker; when that check fails the full key is kept (a miss, never a false hit).
    internal static string WithoutDeckPrior(string key, FairFacts facts)
    {
        string prior = DeckPrior(facts);
        if (prior.Length == 0) return key;
        int marker = key.LastIndexOf("|effects:", StringComparison.Ordinal);
        if (marker < prior.Length || string.CompareOrdinal(key, marker - prior.Length, prior, 0, prior.Length) != 0) return key;
        return key.Remove(marker - prior.Length, prior.Length);
    }
    // The key this pilot compares: plan-reuse strips the sampled own-deck prior, off keeps the base key exactly.
    // carried-reuse: an entity-reference host (carved Treant, ...) shows only its payload's entity id, equal in every simulated world, so
    // a plan built on rolled spells also matched the real, different ones. The owner sees the payload, so its identity joins the key.
    internal string PlanKey(FairBotState view) => (PlanReuseFix ? WithoutDeckPrior(ReuseKey(view), view.Facts) : ReuseKey(view))
        + (Profile.Fixes.On("carried-reuse") ? "|carried:" + view.OwnCarriedKey() : "");
    // Every exposed value and tag on both sides plus the legal options; a mismatch forces a replan.
    private static string ReuseKey(FairBotState view)
    {
        string key = GoldenPublicKey.For(view);
        if (!view.HasPendingChoice) return key;
        var game = view.FingerprintGame;
        var bot = game.Player1.PlayerId == view.Facts.Own.PlayerId ? game.Player1 : game.Player2;
        // Offers live in Setaside and can reuse entity ids for different cards. Read only the fair copy.
        return key + "|offers:" + string.Join(";", bot.Choice.Choices.Select(id => {
            SabberStoneCore.Model.Entities.IPlayable card;
            return id + ":" + (game.IdEntityDic.TryGetValue(id, out card) ? card.Card.Id : "");
        }));
    }
    // Read-only mining telemetry: latest accepted search line, including cached continuations.
    internal IReadOnlyList<BotAction> LastLine { get; private set; } = new BotAction[0];
    internal long LastLineStamp { get; private set; }
    private static long lineStamp;
    internal void Accept(FairBotState view, IReadOnlyList<BotAction> line, int nodes, LethalOutcome lethal, bool interrupted = false)
    {
        LastLine = line.ToArray(); LastLineStamp = System.Threading.Interlocked.Increment(ref lineStamp);
        RemainingNodes = Math.Max(0, RemainingNodes - nodes); LastLethal = lethal;
        CachedInterrupted = interrupted;
        cached.Clear(); expected = null; expectedParts = null; expectedRandom = false;
        emptyReason = line.Count < 2 && !ChoiceBoundary(view) ? "plan-end" : "not-cached";
        if (ChoiceBoundary(view) || line.Count < 2) return;
#if ENGINE_SOURCE
        // Simulation disables Rewind offers, so the public source tag must invalidate those continuations.
        SabberStoneCore.Model.Entities.IPlayable source;
        if ((line[0].Type == PlayerTaskType.PLAY_CARD || line[0].Type == PlayerTaskType.HERO_POWER)
            && view.FingerprintGame.IdEntityDic.TryGetValue(line[0].Source, out source)
            && ((SabberStoneCore.Model.Entities.Entity)source).GetNativeGameTag(GameTag.REWIND) != 0) return;
#endif
        // The continuation stays valid while the real post-action fair view equals the simulated child:
        // draws, random outcomes and hidden triggers all change the public key and force a replan.
        try {
            var child = view.Apply(line[0]);
            if (ChoiceBoundary(child)) return;
            cached = line.Skip(1).ToList(); expected = PlanKey(child); emptyReason = null;
            expectedParts = KeyParts(child);
            // Telemetry only: a second artificial seed tells a random step (draw, random target/damage, generated card)
            // from a deterministic one when the real outcome later disagrees.
            try { expectedRandom = PlanKey(view.WithArtificialSeed(0x52414E44).Apply(line[0])) != expected; }
            catch (Exception error) when (!(error is OutOfMemoryException)) { expectedRandom = true; }
        }
        catch (Exception error) when (!(error is OutOfMemoryException)) { cached.Clear(); expected = null; expectedParts = null; }
    }
    public bool KeepRewind(RewindView view)
    {
        var policy = Plan.RewindPreferences.FirstOrDefault(p => p.SourceCardId == view.SourceCardId);
        if (policy == null) return true;
        Func<RewindWeapon, double> score = weapon => {
            if (weapon == null) return 0;
            double stats = (long)weapon.Attack * weapon.Durability;
            var rated = policy.Weapons.FirstOrDefault(w => w.CardId == weapon.CardId);
            if (rated == null) return stats;
            var definition = Cards.FromId(weapon.CardId);
            // The table prices printed weapons; retain its effect value while pricing visible buffs and damage.
            return rated.Value + stats - (long)definition.ATK * definition[GameTag.DURABILITY];
        };
        double own = score(view.OwnWeapon), enemy = score(view.OpponentWeapon);
        var ordered = policy.Weapons.Select(w => {
            var definition = Cards.FromId(w.CardId);
            // Every Stadium reroll also buffs our weapon +1/+1; compare its percentile in the same units.
            return w.Value + (view.SourceCardId == "TIME_034" ? definition.ATK + definition[GameTag.DURABILITY] + 1 : 0);
        }).OrderBy(v => v).ToArray();
        double bottom = ordered.Length == 0 ? 0 : ordered[Math.Min(ordered.Length - 1, (int)(ordered.Length * policy.RewindBottomFraction))];
        return own > bottom && enemy - own < policy.OpponentAdvantageThreshold;
    }
    public IReadOnlyList<int> Mulligan(IGoldenMulliganView view, GoldenContext ctx) => Mulligan(new MulliganView(
        view.Offered.Select(c => new MulliganCard(c.EntityId, c.CardId, c.Cost)), view.HasCoin, view.OwnDeckIds, view.OpponentClass));
    public IReadOnlyList<int> Mulligan(MulliganView view)
    {
        return mulligan != null ? mulligan(view) : BotDriver.Mulligan(view, new DeckPlanMulligan(Plan));
    }
}

public sealed class GoldenDecisionJob : IGoldenDecisionJob, INpcThinkingCandidates, INpcLethalSearch, INpcSearchContinuation, INpcPassValidation, INpcPlanNodeCap, INpcPreparedDecision, INpcBackgroundSearch, INpcLethalBound, INpcSearchDepth
{
    private readonly GoldenPilot pilot;
    private readonly FairBotState view;
    private readonly GoldenContext context;
    private readonly GoldenPublicInputs inputs;
    private readonly LethalJob lethal, recheck;
    private readonly INodeJob serialLethal;
    internal const int LethalRecheckNodes = 128;
    private readonly ParallelLethalSearch parallel;
    private readonly BoundedFallbackJob minimum;
    public bool Preparing => minimum != null && !minimum.IsDone && minimum.Nodes < cap;
    public int BackgroundNodes => parallel?.Nodes ?? 0;
    public bool BackgroundClock => parallel != null;
    public bool BackgroundRunning => parallel != null && !parallel.IsDone;
    public void LimitBackground(double milliseconds) { parallel?.LimitMilliseconds(milliseconds); }
    public bool Waiting => parallel != null && !parallel.IsDone
        && (planner != null && planner.IsDone || !Preparing && cap <= (minimum?.Nodes ?? 0));
    public void PollBackground() { parallel?.Poll(); }
    public void StopBackground() { parallel?.Stop(); }
    // Late-lethal pool gate (NpcTurnRunner): computed once, from this decision's fair facts only.
    private bool? plausible;
    public bool LethalPlausible => plausible ?? (plausible = LethalBound.Plausible(view.Facts)).Value;
    private LethalResult Lethal => recheck?.Result.Outcome == LethalOutcome.Found ? recheck.Result : parallel?.Result ?? lethal.Result;
    private readonly int cap;
    private TurnPlannerJob planner;
    private TurnPlannerJob validation;
    private bool validated;
    private IReadOnlyList<BotAction> originalLine;
    private double originalValue;
    private SearchStop originalStop;
    private IEnumerable<KeyValuePair<BotAction, double>> originalCandidates;
    public bool Validating => validation != null && !IsDone;
    public GoldenDecision StopValidation()
    {
        if (IsDone) return Result;
        searchInfo.ValidationDeadline = true;
        FinishValidation();
        return Result;
    }
    private void FinishValidation()
    {
        var best = validation.StopSearch();
        validated = true; searchInfo.ValidationNodes = best.Nodes;
        searchInfo.ValidationCompared = validation.HasPassComparison;
        // A verified win ends the own beam before replies; it needs no pass comparison.
        bool accept = validation.HasVerifiedLethal || searchInfo.ValidationCompared;
        searchInfo.ValidationFallback = !accept;
        if (accept) searchInfo.FallbackWhy = null; // The reserve search decided after all.
        interrupted |= best.Interrupted || searchInfo.ValidationDeadline;
        Complete(accept ? best.Line : originalLine,
            accept ? best.Value : originalValue,
            searchInfo.ValidationDeadline ? SearchStop.Clock : accept ? best.Stop : originalStop,
            accept ? best.Candidates : originalCandidates);
    }
    private bool interrupted;
    // Independent of the primary turn/cap/clock allocation. Same tier evaluator, width and reply strength.
    internal const int PassValidationNodes = 7000;
    private readonly DecisionSearchInfo searchInfo = new DecisionSearchInfo();
    private int nodes;
    // Live runner only (wall-clock budget); null on the node-budget path, which therefore searches exactly as before.
    private Func<int> livePlanCap;
    public bool IsDone { get; private set; }
    public GoldenDecision Result { get; private set; }
    public string LethalResult => Lethal.Outcome.ToString().ToLowerInvariant();
    public bool PlanReused { get; private set; }
    public INodeJob LethalSearch => parallel == null ? serialLethal : recheck;
    public int Deepest => Math.Max(recheck?.Deepest ?? 0, Math.Max(validation?.Deepest ?? 0, Math.Max(planner?.Deepest ?? 0, parallel?.Deepest ?? lethal.Deepest)));
    internal GoldenPublicInputs PublicInputs => inputs;
    internal int PlanningCap { get; private set; }
    public void CapPlanNodes(Func<int> cap) { if (planner == null) livePlanCap = cap; }
    // Final scores once published, else the planner's live leaders, then lethal and root order as filler.
    public IReadOnlyList<BotAction> Candidates => GoldenData.Copy(
        (Result?.Candidates ?? validation?.Leaders ?? planner?.Leaders ?? new KeyValuePair<BotAction, double>[0])
            .OrderByDescending(p => p.Value).ThenBy(p => p.Key.ToString(), StringComparer.Ordinal).Select(p => p.Key)
            .Concat(Lethal.Line.Take(1)).Concat(view.OrderedActions).Distinct().Take(3));
    internal GoldenDecisionJob(GoldenPilot pilot, FairBotState view, GoldenContext context, GoldenPublicInputs inputs)
    {
        this.pilot = pilot; this.view = view; this.context = context; this.inputs = inputs ?? new GoldenPublicInputs();
        cap = pilot.RemainingNodes;
        int decisionLethalCap = GoldenAblation.LethalNodesOverride > 0 ? GoldenAblation.LethalNodesOverride : pilot.Profile.Budget.LethalNodes;
        // A capped turn-start search must not spend later decisions' ability to notice an immediate win.
        int recheckCap = pilot.Profile.Fixes.On("lethal-recheck") && decisionLethalCap > 0 && !view.HasPendingChoice && LethalBound.Plausible(view.Facts)
            ? LethalRecheckNodes : 0;
        if (recheckCap > 0) recheck = new LethalJob(view, recheckCap, null, null, immediate: true);
        int lethalCap = Math.Min(pilot.RemainingLethalNodes, decisionLethalCap);
        if (GoldenPilot.ParallelLethalEnabled) parallel = new ParallelLethalSearch(view, lethalCap, pilot.ChargeLethal);
        else lethal = new LethalJob(view, lethalCap, pilot.ChargeLethal);
        if (parallel != null) pilot.ObserveLethalSearch?.Invoke(parallel);
        if (parallel == null) serialLethal = recheck == null ? (INodeJob)lethal : new LethalSequence(recheck, lethal);
        if (cap > 0 && !pilot.CanReuse(view)) minimum = new BoundedFallbackJob(view, view.Actions, pilot.Profile.Fixes.On("doomed-minions"));
    }
    public void StepNode()
    {
        if (IsDone) return; nodes++;
        if (validation != null) {
            validation.StepNode();
            if (validation.IsDone) FinishValidation();
            return;
        }
        if (Lethal.Outcome == LethalOutcome.Found && Lethal.Line.Count > 0)
        { Complete(Lethal.Line, 1000000, SearchStop.Terminal, null); return; }
        if (Preparing) { minimum.StepNode(); return; }
        if (recheck != null && !recheck.IsDone) { recheck.StepNode(); return; }
        if (parallel != null) parallel.Poll();
        else if (!serialLethal.IsDone) { serialLethal.StepNode(); return; }
        if (Lethal.Outcome == LethalOutcome.Found && Lethal.Line.Count > 0) {
            Complete(Lethal.Line, 1000000, SearchStop.Terminal, null); return;
        }
        if (planner != null && planner.IsDone) {
            if (parallel != null && !parallel.IsDone) return;
            Complete(planner.Result.Line, planner.Result.Value, planner.Result.Stop, planner.Result.Candidates); return;
        }
        if (planner == null) {
            var cached = pilot.Reuse(view);
            if (cached != null) { interrupted = pilot.CachedInterrupted; Complete(cached, view.Evaluate(new DeckPlanEvaluator(pilot.Plan, pilot.Profile.Budget.Brain) { DeathFixes = pilot.Profile.Fixes }), SearchStop.Exhausted, null); return; }
            if (cap <= (minimum?.Nodes ?? 0)) { Greedy(); return; }
            int allowance = Math.Min(GoldenAblation.PlanNodesOverride > 0 ? GoldenAblation.PlanNodesOverride : pilot.Profile.Budget.PlanNodes, cap - (minimum?.Nodes ?? 0));
            int live = livePlanCap?.Invoke() ?? -1;
            if (live >= 0) allowance = Math.Max(1, Math.Min(allowance, live));
            PlanningCap = allowance;
            planner = TurnPlanner.Job(view, pilot.Profile, pilot.Plan, pilot.TurnSeed, allowance, inputs.OpponentPlayed, inputs);
            pilot.ObservePlanner?.Invoke(planner);
        }
        planner.StepNode();
    }
    public GoldenDecision StopSearch(SearchStop stop)
    {
        if (IsDone) return Result;
        if (validation != null) return Result; // Reserve continues through the runner's normal frame slices.
        parallel?.Stop();
        var line = Lethal.Outcome == LethalOutcome.Found ? Lethal.Line : pilot.Reuse(view);
        if (line != null && line.Count > 0) { interrupted |= pilot.CachedInterrupted; Complete(line, view.Evaluate(new DeckPlanEvaluator(pilot.Plan, pilot.Profile.Budget.Brain) { DeathFixes = pilot.Profile.Fixes }), stop, null); }
        else if (planner != null) {
            bool useMinimum = minimum != null && minimum.Nodes > 0 && !planner.HasEvaluatedAction;
            var best = planner.StopSearch();
            if (useMinimum) searchInfo.FallbackWhy = "minimum-answer:" + stop + ":planner-empty";
            Complete(useMinimum ? new[] { minimum.Result } : best.Line, best.Value, stop, useMinimum ? minimum.Compared : best.Candidates);
        } else {
            searchInfo.FallbackWhy = (minimum != null && minimum.Nodes > 0 ? "minimum-answer:" : "first-legal:") + stop + ":no-planner";
            Complete(new[] { minimum != null && minimum.Nodes > 0 ? minimum.Result : FallbackAction(view, new DeckPlanEvaluator(pilot.Plan, pilot.Profile.Budget.Brain) { DeathFixes = pilot.Profile.Fixes }) }, view.Evaluate(new DeckPlanEvaluator(pilot.Plan, pilot.Profile.Budget.Brain) { DeathFixes = pilot.Profile.Fixes }), stop, minimum?.Compared);
            if (validation == null) Result = new GoldenDecision(Result.Action, Result.Value, Result.Nodes, Result.Depth, stop.ToString(),
                minimum != null && minimum.Nodes > 0 ? null : stop == SearchStop.Clock ? "clock-cut" : "turn-nodes", Result.Candidates, "fallback") { SearchInfo = searchInfo };
        }
        // A pending pass comparison returns null; the live runner slices its independent reserve.
        return Result;
    }
    // Location override only when activating evaluates no worse than ending (some locations hurt the owner).
    internal static BotAction FallbackAction(FairBotState state, DeckPlanEvaluator evaluator)
    {
        var actions = state.OrderedActions; var first = actions.First();
#if ENGINE_SOURCE
        if (first.Type == PlayerTaskType.END_TURN)
        {
            double now = state.Evaluate(evaluator);
            foreach (var location in actions.Where(a => a.Type == PlayerTaskType.ACTIVATE_LOCATION))
            {
                // The spent durability point (3 per point in the evaluator) is the price of acting, not a loss.
                try { if (state.Apply(location).Evaluate(evaluator) + 3 >= now) return location; }
                // Debug engines wrap the fair view's refusal in TaskException; keep every former NotSupported skip too.
                catch (Exception e) when (e is NotSupportedException || FairHistory.IsPrivate(e)) { }
            }
        }
#endif
        return first;
    }
    private void Greedy()
    {
        // Golden-only wrapper fills LocalBot's unscored-location gap.
        searchInfo.FallbackWhy = (minimum != null ? "minimum-answer:" : "first-legal:") + "Nodes:turn-budget-spent";
        Complete(new[] { minimum?.Result ?? FallbackAction(view, new DeckPlanEvaluator(pilot.Plan, pilot.Profile.Budget.Brain) { DeathFixes = pilot.Profile.Fixes }) }, view.Evaluate(new DeckPlanEvaluator(pilot.Plan, pilot.Profile.Budget.Brain) { DeathFixes = pilot.Profile.Fixes }), SearchStop.Nodes, minimum?.Compared);
    }
    private void Complete(IReadOnlyList<BotAction> line, double value, SearchStop stop, IEnumerable<KeyValuePair<BotAction, double>> candidates)
    {
        // Joining can collect a late worker witness; select proof only after all workers have stopped.
        parallel?.Stop();
        if (Lethal.Outcome == LethalOutcome.Found && Lethal.Line.Count > 0) {
            line = Lethal.Line; value = 1000000; pilot.MarkReplanned(); searchInfo.ValidationFallback = false; searchInfo.FallbackWhy = null;
        }
        if (line.Count == 0) searchInfo.FallbackWhy = "first-legal:" + stop + ":empty-line";
        if (line.Count == 0) line = new[] { FallbackAction(view, new DeckPlanEvaluator(pilot.Plan, pilot.Profile.Budget.Brain) { DeathFixes = pilot.Profile.Fixes }) };
        interrupted |= !pilot.PlanReused && (stop == SearchStop.Clock || stop == SearchStop.Nodes) || planner?.Result.Interrupted == true;
        if (!validated && interrupted && Lethal.Outcome != LethalOutcome.Found && value < 999000 && line[0].Type == PlayerTaskType.END_TURN && view.CanExpand) {
            pilot.MarkReplanned();
            originalLine = line; originalValue = value; originalStop = stop; originalCandidates = candidates;
            searchInfo.ValidationNodeCap = PassValidationNodes;
            validation = TurnPlanner.Job(view, pilot.Profile, pilot.Plan, pilot.TurnSeed, PassValidationNodes,
                inputs.OpponentPlayed, inputs, comparePass: true);
            return;
        }
        PlanReused = pilot.PlanReused;
        pilot.Accept(view, line, (minimum?.Nodes ?? 0) + (planner == null ? 0 : planner.Result.Nodes), Lethal.Outcome, validation != null ? searchInfo.ValidationFallback || searchInfo.ValidationDeadline || validation.Result.Interrupted : interrupted);
        pilot.LastDeepest = Deepest;
        Result = new GoldenDecision(line[0], value, nodes + BackgroundNodes, line.Count, stop.ToString(), null, candidates);
        searchInfo.PlanNodeCap = PlanningCap; searchInfo.Interrupted = interrupted; searchInfo.Reused = PlanReused;
        searchInfo.Replan = PlanReused ? "reused" : Lethal.Outcome == LethalOutcome.Found ? "lethal" : pilot.ReplanReason;
        Result.SearchInfo = searchInfo;
        IsDone = true;
    }
}
