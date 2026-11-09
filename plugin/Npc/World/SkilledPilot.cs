using System;
using System.Collections.Generic;
using System.Linq;
using SabberStoneCore.Tasks.PlayerTasks;

// Search variants a SkilledPilot may run for one decision. All share the deck, plan and personality; only the
// planning flaw differs. NoReply = same plan without the opponent-reply model (IgnoreAoeRisk), NoLethal = no
// exhaustive lethal solver (LethalMiss: the beam may still see an obvious kill, like a human would).
[Flags] public enum SkilledVariant { None = 0, NoReply = 1, NoLethal = 2 }

// WP2. Decorator over the (strength-owned) GoldenPilot: applies SkillProfile mistakes AFTER the fair search,
// proposing searched alternatives, then validating their constrained full-turn continuations against the best.
// Both arms use the same root, budget, reply samples and evaluator. Holds exclude their card throughout the turn;
// accepted continuations are cached by GoldenPilot. Missing candidates or interrupted validation mean fewer mistakes.
// Targeted substitutions cannot harm friendlies or help enemies unless the search chose that polarity on the same entity.
// It never reads engine state, so it cannot leak hidden information. Rolls come from
// StableSeed(matchSeed, npc, turn, decision, kind): replays and harness runs are reproducible.
// Never: perturb a line that wins on the spot or any decision after a lethal was taken this turn, pick a candidate
// that loses on the spot when another exists, or stray more than the rule's maxValueLoss below the best.
// Any exception after the search returns the search's own decision unchanged.
//
// Budgets: in the harness Decide runs its own NodeScheduler (two turn allowances: a missed lethal replans). Live,
// NpcTurnRunner drives Begin/StepNode under the profile's TurnNodes plus its clock.
public sealed class SkilledPilot : IFrameSlicedGoldenPilot, IGoldenPilotHost
{
    public IFrameSlicedGoldenPilot Inner { get; }
    // Same deck/plan without the reply model: used for turns where IgnoreAoeRisk fires (overextension is a planning error).
    public IFrameSlicedGoldenPilot NoReplyInner { get; }
    // Decision cost telemetry: every GoldenPilot a decision may search with (GoldenPilot.LastDeepest).
    internal IEnumerable<GoldenPilot> Searchers => variants.Values.OfType<GoldenPilot>().Distinct();
    public SkillProfile Skill { get; }
    public int MatchSeed { get; }
    public DeckPlan Plan { get; }
    public int NpcHash { get; }
    private readonly Dictionary<SkilledVariant, IFrameSlicedGoldenPilot> variants = new Dictionary<SkilledVariant, IFrameSlicedGoldenPilot>();
    private readonly int[] mistakeCounts = new int[Enum.GetValues(typeof(MistakeKind)).Length];
    private int turn = int.MinValue;
    private bool ignoreAoe, lethalCommitted;
    private int lethalState; // 0 = not rolled this turn, 1 = seen (commit), 2 = missed for the rest of the turn
    private readonly HashSet<int> held = new HashSet<int>();

    // Live hook path (NpcWorldRuntime): variants are rebuilt from the inner GoldenPilot's own profile and plan, so the
    // inner budget and personality stay exactly what the host built. noReplyInner is used only for a non-golden inner.
    public SkilledPilot(IFrameSlicedGoldenPilot inner, IFrameSlicedGoldenPilot noReplyInner, SkillProfile skill, int matchSeed)
        : this(skill, matchSeed, (inner as GoldenPilot)?.Plan, Legacy(inner, noReplyInner), (inner as GoldenPilot)?.Profile?.Id) { }

    private static Func<SkilledVariant, IFrameSlicedGoldenPilot> Legacy(IFrameSlicedGoldenPilot inner, IFrameSlicedGoldenPilot noReply)
    {
        var golden = inner as GoldenPilot;
        return v => v == SkilledVariant.None ? inner
            : golden != null ? new GoldenPilot(Tune(golden.Profile, null, v), golden.Plan)
            : v == SkilledVariant.NoReply ? noReply : null;
    }

    // Every variant this skill can need is built here, so a failure surfaces at construction, not mid-match.
    public SkilledPilot(SkillProfile skill, int matchSeed, DeckPlan plan, Func<SkilledVariant, IFrameSlicedGoldenPilot> factory, string npcId)
    {
        Skill = skill ?? throw new ArgumentNullException(nameof(skill));
        if (factory == null) throw new ArgumentNullException(nameof(factory));
        MatchSeed = matchSeed; Plan = plan; NpcHash = StableHash(npcId ?? "");
        Inner = factory(SkilledVariant.None) ?? throw new ArgumentNullException("inner");
        variants[SkilledVariant.None] = Inner;
        bool noReply = Skill.Chance(MistakeKind.IgnoreAoeRisk) > 0, noLethal = Skill.Chance(MistakeKind.LethalMiss) > 0;
        if (noReply) variants[SkilledVariant.NoReply] = factory(SkilledVariant.NoReply);
        if (noLethal) variants[SkilledVariant.NoLethal] = factory(SkilledVariant.NoLethal);
        if (noReply && noLethal) variants[SkilledVariant.NoReply | SkilledVariant.NoLethal] = factory(SkilledVariant.NoReply | SkilledVariant.NoLethal);
        NoReplyInner = Variant(SkilledVariant.NoReply);
    }

    // Builds GoldenPilot variants of an encounter profile with this skill's budget and reply-risk scaling.
    public static SkilledPilot Wrap(NpcProfile profile, SkillProfile skill, int matchSeed, DeckPlan plan = null,
        Func<MulliganView, IReadOnlyList<int>> mulligan = null)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        plan = plan ?? DeckPlans.ForId(profile.PlanId) ?? NpcWorldHooks.PlanForId?.Invoke(profile.PlanId)
            ?? throw new ArgumentException("Unknown deck plan: " + profile.PlanId);
        return new SkilledPilot(skill, matchSeed, plan, v => new GoldenPilot(Tune(profile, skill, v), plan, mulligan), profile.Id);
    }

    // Same id (GoldenPilot checks the context profile id), skill budget, scaled reply-risk aversion; skill null keeps both.
    public static NpcProfile Tune(NpcProfile profile, SkillProfile skill, SkilledVariant variant)
    {
        var b = skill?.Budget ?? profile.Budget; var p = profile.Personality;
        double scale = skill == null ? 1 : skill.ReplyBlendScale;
        var personality = Math.Abs(scale - 1) < 1e-12 ? p : new Personality(p.PreTurnMinSeconds, p.PreTurnMaxSeconds,
            p.ActionMinSeconds, p.ActionMaxSeconds, p.Emotes, Math.Max(0, Math.Min(1, p.ReplyBlendBeta * scale)),
            p.ConcessionWillingness, p.ConcedeFromTurn);
        int lethal = (variant & SkilledVariant.NoLethal) != 0 ? 0 : b.LethalNodes;
        // No reply: ReplyL = 0 and a per-leaf reply allowance larger than any plan cap, so TurnPlanner keeps the
        // whole allowance for its own beam and scores lines statically (ReplyK = 0 would hit CardFrequencies' k > 0 guard).
        var budget = (variant & SkilledVariant.NoReply) != 0
            ? new Budget(b.TurnNodes, b.PlanNodes, lethal, 1, 0, Math.Max(b.ReplyR, 2 * b.PlanNodes + 64), b.OwnBeamWidth, b.Dedupe, b.TurnSearchSeconds, b.TurnWallSeconds, b.Brain)
            : new Budget(b.TurnNodes, b.PlanNodes, lethal, b.ReplyK, b.ReplyL, b.ReplyR, b.OwnBeamWidth, b.Dedupe, b.TurnSearchSeconds, b.TurnWallSeconds, b.Brain);
        return new NpcProfile(profile.Id, profile.Version, profile.DisplayName, profile.DeckId, profile.HeroSkin,
            profile.CardBack, personality, budget, profile.PlanId, profile.HeroSkinPower, skill == null ? profile.Fixes : profile.Fixes.ForSoftmax(skill.Temperature));
    }

    // Fork only decision state; views and public inputs are supplied by the harness, never authoritative state.
    internal SkilledPilot ForkForRecheck(AiFixes fixes)
    {
        var copy = new SkilledPilot(Skill, MatchSeed, Plan, v => ((GoldenPilot)variants[v]).ForkForRecheck(fixes), Profile.Id);
        copy.turn = turn; copy.ignoreAoe = ignoreAoe; copy.lethalCommitted = lethalCommitted; copy.lethalState = lethalState;
        copy.held.UnionWith(held); Array.Copy(mistakeCounts, copy.mistakeCounts, mistakeCounts.Length);
        copy.SoftmaxDeviations = SoftmaxDeviations; copy.PerturbFailures = PerturbFailures;
        return copy;
    }

    // The primary variant owns the fair-view factory (own-deck tracking, opponent Secret removal, Rewind).
    public DriverOptions Options => (Inner as IGoldenPilotHost)?.Options;
    public GoldenPublicInputs PublicInputs => (Inner as IGoldenPilotHost)?.PublicInputs;
    public NpcProfile Profile => (Inner as GoldenPilot)?.Profile;
    public int MistakeCount(MistakeKind kind) => mistakeCounts[(int)kind];
    public IReadOnlyDictionary<MistakeKind, int> MistakeCounts =>
        Enum.GetValues(typeof(MistakeKind)).Cast<MistakeKind>().ToDictionary(k => k, k => mistakeCounts[(int)k]);
    // Untyped deviations: softmax noise picked a searched candidate other than the best.
    public int SoftmaxDeviations { get; private set; }
    public int PerturbFailures { get; private set; }
    public double LastMistakeLoss { get; private set; }
    public double LastMistakeBound { get; private set; }
    internal IReadOnlyList<BotAction> LastMistakeLine { get; private set; }
    internal double LastMistakeBest { get; private set; }

    internal IFrameSlicedGoldenPilot Variant(SkilledVariant variant)
    {
        IFrameSlicedGoldenPilot pilot;
        return variants.TryGetValue(variant, out pilot) ? pilot : null;
    }

    public IGoldenDecisionJob Begin(FairBotState view, GoldenContext context)
    {
        if (view == null) throw new ArgumentNullException(nameof(view));
        if (context == null) throw new ArgumentNullException(nameof(context));
        if (context.Turn != turn)
        {
            turn = context.Turn; lethalState = 0; lethalCommitted = false; held.Clear();
            ignoreAoe = NoReplyInner != null && Roll(BaseSeed(context.Turn, -1), MistakeKind.IgnoreAoeRisk) < Skill.Chance(MistakeKind.IgnoreAoeRisk);
        }
        LastMistakeLine = null; LastMistakeLoss = LastMistakeBound = 0;
        return new SkilledJob(this, view, context);
    }

    public GoldenDecision Decide(FairBotState view, GoldenContext ctx)
    {
        var job = Begin(view, ctx);
        var budget = (Inner as GoldenPilot)?.Profile.Budget ?? Skill.Budget;
        // Covers the primary/variant search plus two bounded continuation validations.
        var scheduler = new NodeScheduler(2 * (budget.TurnNodes + budget.LethalNodes) + 16); scheduler.Enqueue(job);
        while (scheduler.Step(1000) == NodeStepResult.Running) { }
        if (!job.IsDone) throw new InvalidOperationException("Skilled decision exceeded its node allowance.");
        return job.Result;
    }
    public PilotDecision DecidePilot(FairBotState view, GoldenContext ctx) => Decide(view, ctx).ToPilotDecision();

    public IReadOnlyList<int> Mulligan(IGoldenMulliganView view, GoldenContext ctx)
    {
        var plan = Plan ?? (Inner as GoldenPilot)?.Plan;
        if (plan == null) return Inner.Mulligan(view, ctx);
        return new SkilledMulligan(plan, Skill, StableSeed.Derive(MatchSeed, NpcHash, 0x4D554C)).Keep(new MulliganView(
            view.Offered.Select(c => new MulliganCard(c.EntityId, c.CardId, c.Cost)), view.HasCoin, view.OwnDeckIds, view.OpponentClass));
    }

    // Necessary condition for Select to deviate at this decision, from its own seed alone (no candidates needed): a typed
    // roll under its chance, a sticky hold, or a softmax roll that the best weight (1) cannot absorb with at most TopN
    // pooled weights <= 1. False means Select returns the best whatever the candidates are.
    internal bool RollsCouldFire(GoldenContext ctx)
    {
        if (Skill.IsPerfect || lethalCommitted) return false;
        int seed = BaseSeed(ctx.Turn, ctx.DecisionIndex);
        foreach (var kind in Order)
        {
            var rule = Skill.Rule(kind);
            if (rule == null || rule.Chance <= 0) continue;
            if (kind == MistakeKind.HoldTooLong && held.Count > 0 || Roll(seed, kind) < rule.Chance) return true;
        }
        return Skill.Temperature > 0 && Skill.TopN > 1 && Unit(StableSeed.Derive(seed, 0x534D58)) * Skill.TopN >= 1;
    }
    internal int BaseSeed(int turnNumber, int decisionIndex) => StableSeed.Derive(MatchSeed, NpcHash, turnNumber, decisionIndex);
    internal static double Roll(int seed, MistakeKind kind) => Unit(StableSeed.Derive(seed, (int)kind + 1));
    private static double Unit(int seed) => unchecked((uint)seed) / 4294967296.0;
    public static int StableHashOf(string text) => StableHash(text ?? "");
    // FNV-1a: string.GetHashCode is randomized per process on .NET Core.
    internal static int StableHash(string text)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in text) { hash ^= c; hash *= 16777619; }
            return (int)hash;
        }
    }

    private GoldenContext ContextFor(IFrameSlicedGoldenPilot pilot, GoldenContext ctx)
    {
        if (pilot == Inner) return ctx;
        // Variants never run the fair-view factory; reuse the primary's public inputs for this decision.
        return new GoldenContext(ctx.Turn, ctx.DecisionIndex, ctx.ArtificialSeed, ctx.NpcProfile, ctx.PublicInputs ?? PublicInputs);
    }

    private bool Untouched(GoldenDecision result, string lethalResult) =>
        result == null || Skill.IsPerfect || lethalCommitted || result.DriverKind != "search" || result.Stop == "Forced"
        // A fallback's comparison is for the log: it is not a search shortlist to draw a human-like mistake from.
        || result.SearchInfo.FallbackWhy != null
        || result.Value >= 999000 || lethalResult == "found";

    // A found lethal of >= MinLethalActions is rolled once per turn: seen = commit to it, missed = the rest of the
    // turn ignores solver lethals that long. Shorter lethals are always taken.
    internal bool MissesLethal(GoldenDecision result, string lethalResult, GoldenContext ctx)
    {
        var rule = Skill.Rule(MistakeKind.LethalMiss);
        if (rule == null || rule.Chance <= 0 || lethalResult != "found" || result == null || result.Value < 999000) return false;
        if (result.Depth < Math.Max(SkillCatalog.MinMissableLethalActions, rule.MinLethalActions)) return false;
        if (Variant((ignoreAoe ? SkilledVariant.NoReply : SkilledVariant.None) | SkilledVariant.NoLethal) == null) return false;
        if (lethalState == 1) return false;
        if (lethalState == 0)
        {
            bool miss = Roll(BaseSeed(ctx.Turn, ctx.DecisionIndex), MistakeKind.LethalMiss) < rule.Chance;
            lethalState = miss ? 2 : 1;
            if (!miss) return false;
        }
        return true;
    }

    private sealed class SkilledJob : IGoldenDecisionJob, INpcThinkingCandidates, INpcLethalSearch, INpcSearchContinuation, INpcPassValidation, INpcPlanNodeCap,
        INpcBackgroundSearch, INpcLethalBound
    {
        private static readonly INodeJob DoneJob = new FinishedJob();
        private readonly SkilledPilot owner;
        private readonly FairBotState view;
        private readonly GoldenContext context;
        private IGoldenDecisionJob current;
        private bool switched, missed;
        private readonly GoldenPilot primary;
        private GoldenDecision baseline;
        private TurnPlannerJob pricing;
        private PlannedTurn reference;
        private Choice choice;
        private BotAction proposed;
        private int pricingCap, pricingNodes;
        private double bound;
        private int[] heldForPrice;
        private MistakeKind? variantKind;
        // Search scores propose mistakes; the paired continuation searches certify their loss.
        private GoldenDecision raw;
        private string rawLethal;
        private List<KeyValuePair<BotAction, double>> ranked;
        private MistakeContext mistakes;
        private Func<int> planCap;
        // plan-reuse mistake probe: candidates for a continued plan step (see StartProbe); the reused decision it serves.
        private readonly bool probeWanted;
        private TurnPlannerJob probe;
        private GoldenDecision probed;
        private int probeNodes;
        // Live clock budget: forwarded to the search job, including a lethal-miss replacement.
        public void CapPlanNodes(Func<int> cap) { planCap = cap; (current as INpcPlanNodeCap)?.CapPlanNodes(cap); }
        public bool IsDone { get; private set; }
        public GoldenDecision Result { get; private set; }
        // Deck parallel lethal, forwarded to the current search job: the runner then waits for running workers instead
        // of stepping a parked search (each step costs a turn node, so one slice could burn the turn's node budget), and
        // gives them its deadline. A lethal-miss replacement job inherits what is left of that deadline.
        private readonly BackgroundDeadline deadline = new BackgroundDeadline();
        private int previousBackgroundNodes;
        public int BackgroundNodes => previousBackgroundNodes + ((current as INpcBackgroundSearch)?.BackgroundNodes ?? 0);
        public bool Waiting => !IsDone && pricing == null && (current as INpcBackgroundSearch)?.Waiting == true;
        public bool BackgroundClock => (current as INpcBackgroundSearch)?.BackgroundClock == true;
        public bool BackgroundRunning => (current as INpcBackgroundSearch)?.BackgroundRunning == true;
        public void LimitBackground(double milliseconds) { deadline.Limit(milliseconds); (current as INpcBackgroundSearch)?.LimitBackground(milliseconds); }
        public void PollBackground() { (current as INpcBackgroundSearch)?.PollBackground(); }
        public void StopBackground() { (current as INpcBackgroundSearch)?.StopBackground(); }
        public bool LethalPlausible => (current as INpcLethalBound)?.LethalPlausible == true;
        public string LethalResult => missed ? "missed" : current.LethalResult;
        public bool PlanReused => IsDone ? Result.SearchInfo.Reused : current.PlanReused;
        public INodeJob LethalSearch => (current as INpcLethalSearch)?.LethalSearch ?? DoneJob;
        public IReadOnlyList<BotAction> Candidates => IsDone ? new[] { Result.Action }
            : raw != null ? new[] { raw.Action } : (current as INpcThinkingCandidates)?.Candidates ?? new BotAction[0];
        internal SkilledJob(SkilledPilot owner, FairBotState view, GoldenContext context)
        {
            this.owner = owner; this.view = view; this.context = context;
            primary = owner.Inner as GoldenPilot;
            // plan-reuse: a continued plan step has no searched candidates, so it takes no mistake, as before plan-reuse.
            // reuse-mistake-rate (default ON) keeps it that way. OFF restores the per-decision probe (StartProbe): a
            // quarter-plan search on reused steps. Skilled 2026-11-02, 128 units: probe 12.7 mistakes / 100 decisions vs
            // 4.0 without plan-reuse (shallow candidates sit close, so softmax deviates 3.5x as often); no probe 3.7.
            probeWanted = primary != null && primary.Profile.Fixes.On("plan-reuse") && !primary.Profile.Fixes.On("reuse-mistake-rate")
                && owner.RollsCouldFire(context);
            current = owner.Inner.Begin(view, context);
        }
        public void StepNode()
        {
            if (IsDone) return;
            try
            {
                if (pricing != null) { PriceStep(); return; }
                if (probe != null) { ProbeStep(); return; }
                if (!current.IsDone) { current.StepNode(); if (!current.IsDone) return; }
                var result = current.Result;
                if (ImprovesWithLethal(result)) { Finish(result); return; }
                if (!switched)
                {
                    baseline = result;
                    pricingCap = (current as GoldenDecisionJob)?.PlanningCap ?? context.NpcProfile.Budget.PlanNodes;
                    var variant = SkilledVariant.None;
                    if (owner.MissesLethal(result, current.LethalResult, context))
                    { variant = SkilledVariant.NoLethal; variantKind = MistakeKind.LethalMiss; }
                    else if (owner.ignoreAoe && !owner.Untouched(result, current.LethalResult) && !current.PlanReused)
                    { variant = SkilledVariant.NoReply; variantKind = MistakeKind.IgnoreAoeRisk; }
                    switched = true;
                    var pilot = owner.Variant(variant);
                    if (variant != SkilledVariant.None && pilot != null)
                    {
                        raw = baseline; rawLethal = current.LethalResult;
                        StopBackground();
                        // Commit the replacement and the old job's worker cost together: a throwing Begin keeps neither.
                        int spent = (current as INpcBackgroundSearch)?.BackgroundNodes ?? 0;
                        var next = pilot.Begin(view, owner.ContextFor(pilot, context));
                        previousBackgroundNodes += spent;
                        current = next;
                        if (planCap != null) (current as INpcPlanNodeCap)?.CapPlanNodes(planCap);
                        deadline.Apply(current); return;
                    }
                }
                if (variantKind.HasValue && !result.Action.Equals(baseline.Action))
                {
                    raw = baseline;
                    choice = new Choice { Index = 1, Kind = variantKind };
                    StartPrice(result.Action); return;
                }
                if (probeWanted && baseline.SearchInfo.Reused && !owner.Untouched(baseline, current.LethalResult) && StartProbe()) return;
                Prepare(baseline, current.LethalResult);
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                if (baseline == null) throw;
                owner.PerturbFailures++; Finish(baseline);
            }
        }
        public bool Validating => !IsDone && (current as INpcPassValidation)?.Validating == true;
        private bool ImprovesWithLethal(GoldenDecision result) => baseline != null && result != null
            && result.Value >= 999000 && result.Value > baseline.Value;
        public GoldenDecision StopValidation()
        {
            var result = (current as INpcPassValidation)?.StopValidation();
            if (ImprovesWithLethal(result)) { Finish(result); return Result; }
            // A completed variant reply still does not certify the pending mistake's loss.
            if (baseline != null && result != null) {
                baseline.SearchInfo.ValidationNodeCap = Math.Max(baseline.SearchInfo.ValidationNodeCap, result.SearchInfo.ValidationNodeCap);
                baseline.SearchInfo.ValidationNodes += result.SearchInfo.ValidationNodes;
                baseline.SearchInfo.ValidationDeadline = baseline.SearchInfo.ValidationFallback = true;
            }
            if (result != null) Finish(baseline ?? result);
            return Result;
        }
        public GoldenDecision StopSearch(SearchStop stop)
        {
            if (IsDone) return Result;
            // An interrupted validation is not evidence that a deviation is bounded.
            var result = current.IsDone ? current.Result : (current as INpcSearchContinuation)?.StopSearch(stop);
            if (result == null && (current as INpcPassValidation)?.Validating == true) return null;
            if (baseline != null && !ImprovesWithLethal(result)) result = baseline;
            if (result != null) Finish(result);
            return Result;
        }
        // Clock cuts retain the baseline unless a variant proves lethal.
        private void Prepare(GoldenDecision result, string lethalResult)
        {
            if (owner.Untouched(result, lethalResult)) { Finish(result); return; }
            // Keeping the deck's quest unplayed is not a plausible small execution mistake.
            if (result.Action.Type == PlayerTaskType.PLAY_CARD && view.Facts.Own.Hand.Any(c =>
                c.EntityId == result.Action.Source && QuestValue.IsQuest(c.CardId))) { Finish(result); return; }
            raw = result; rawLethal = lethalResult;
            ranked = Ranked(result);
            var facts = view.Facts; var best = ranked[0].Key;
            bool onlyHeld = facts.Own.Hand.Any(c => c.EntityId == best.Source && c.Cost > 0)
                && view.Actions.All(a => a.Type == PlayerTaskType.END_TURN || a.Source == best.Source);
            mistakes = new MistakeContext(context.Turn, false, 0, false, facts.Opponent.Hero.EntityId,
                facts.Opponent.Board.Select(c => c.EntityId), null, owner.held, onlyHeld,
                facts.Own.Hand.Any(c => c.EntityId == best.Source && c.Cost <= 0), facts);
            Select();
        }
        private void Select()
        {
            choice = SkilledPilot.Select(ranked, owner.Skill, owner.BaseSeed(context.Turn, context.DecisionIndex), mistakes, ranked.Count);
            var picked = ranked[choice.Index].Key;
            if (choice.Index == 0 || !view.Actions.Contains(picked)) { Finish(raw); return; }
            StartPrice(picked);
        }
        private TurnPlannerJob Price(BotAction action, IEnumerable<int> held = null)
        {
            var inputs = context.PublicInputs ?? owner.PublicInputs;
            return TurnPlanner.Job(view, primary?.Profile ?? context.NpcProfile, owner.Plan,
                primary?.TurnSeed ?? context.ArtificialSeed, pricingCap, inputs?.OpponentPlayed, inputs, action, held);
        }
        private void StartPrice(BotAction action)
        {
            proposed = action;
            bound = choice.Kind.HasValue ? owner.Skill.Rule(choice.Kind.Value).MaxValueLoss : owner.Skill.SoftmaxMaxValueLoss;
            heldForPrice = owner.held.Concat(choice.Kind == MistakeKind.HoldTooLong ? new[] { raw.Action.Source } : new int[0]).Distinct().ToArray();
            // Same root, cap, samples and evaluator on both arms; never transplant one-ply differences.
            if (pricingCap <= 0 || primary != null && primary.RemainingNodes < 2 * pricingCap) { Finish(raw); return; }
            pricing = Price(raw.Action);
        }
        private void PriceStep()
        {
            pricing.StepNode(); pricingNodes++; primary?.ChargePlan(1);
            if (!pricing.IsDone) return;
            if (reference == null)
            {
                reference = pricing.Result;
                if (!Complete(reference)) { Finish(raw); return; }
                pricing = Price(proposed, heldForPrice); return;
            }
            var result = pricing.Result;
            double best = Math.Max(raw.Value, reference.Value);
            // A forced action must actually have been searched. A cap/fault fallback cannot certify it.
            if (!Complete(result) || !result.Line[0].Equals(proposed)
                || !result.Candidates.Any(c => c.Key.Equals(proposed))
                || double.IsNaN(result.Value) || result.Value < best - bound - 1e-9
                || best > -999000 && result.Value <= -999000) { Finish(raw); return; }
            if (choice.Kind.HasValue)
            {
                owner.mistakeCounts[(int)choice.Kind.Value]++;
                if (choice.Kind == MistakeKind.HoldTooLong) owner.held.Add(raw.Action.Source);
                missed = choice.Kind == MistakeKind.LethalMiss;
            }
            else owner.SoftmaxDeviations++;
            owner.LastMistakeBest = best; owner.LastMistakeLoss = Math.Max(0, best - result.Value);
            owner.LastMistakeBound = bound; owner.LastMistakeLine = result.Line;
            // Execute the priced continuation, including the entire hold. Ordinary public-key invalidation still applies.
            primary?.MarkReplanned();
            primary?.Accept(view, result.Line, 0, LethalOutcome.None);
            raw.SearchInfo.Mistake = choice.Kind?.ToString() ?? "softmax"; raw.SearchInfo.Reused = false;
            if (probed != null) raw.SearchInfo.Replan = "mistake-roll";
            Finish(new GoldenDecision(proposed, result.Value, raw.Nodes + pricingNodes, result.Line.Count,
                raw.Stop, null, result.Candidates, raw.DriverKind) { SearchInfo = raw.SearchInfo });
        }
        // A capped/unknown-draw prefix cannot certify the rest of a sticky hold or a removal combination.
        private static bool Complete(PlannedTurn result) => result.Stop != SearchStop.Nodes && result.Line.Count > 0
            && (result.Line[result.Line.Count - 1].Type == PlayerTaskType.END_TURN || result.Value >= 999000);
        // A quarter plan, never more than the live cap allows, and only while the turn's nodes cover it plus both pricing
        // arms; otherwise the step continues its plan without a mistake roll (the clock cannot afford one).
        internal const int MinProbeNodes = 200;
        private bool StartProbe()
        {
            int live = planCap?.Invoke() ?? -1;
            int cap = Math.Min(primary.Profile.Budget.PlanNodes / 4, live < 0 ? int.MaxValue : live);
            if (cap < MinProbeNodes || primary.RemainingNodes < 3 * cap) return false;
            var inputs = context.PublicInputs ?? owner.PublicInputs;
            pricingCap = cap; probed = baseline;
            probe = TurnPlanner.Job(view, primary.Profile, owner.Plan, primary.TurnSeed, cap, inputs?.OpponentPlayed, inputs);
            return true;
        }
        private void ProbeStep()
        {
            probe.StepNode(); probeNodes++; primary.ChargePlan(1);
            if (!probe.IsDone) return;
            var found = probe.Result; probe = null;
            var candidates = found.Candidates?.ToList() ?? new List<KeyValuePair<BotAction, double>>();
            // The plan's own step stays the best candidate (Ranked); the probe scores the alternatives Select may take.
            double value = candidates.Where(c => c.Key.Equals(probed.Action)).Select(c => c.Value).DefaultIfEmpty(found.Value).Max();
            Prepare(new GoldenDecision(probed.Action, value, probed.Nodes, probed.Depth, probed.Stop, null, candidates, probed.DriverKind)
                { SearchInfo = probed.SearchInfo }, current.LethalResult);
        }
        private void Finish(GoldenDecision decision)
        {
            StopBackground();
            // No probed mistake taken: publish the continued plan step itself (its value, candidates and reuse).
            if (probed != null && decision == raw) decision = probed;
            int extra = (decision == raw || decision == baseline ? pricingNodes : 0) + probeNodes;
            if (variantKind.HasValue && current.IsDone && current.Result != baseline)
                extra += decision == current.Result ? baseline.Nodes : current.Result.Nodes;
            if (decision != null && extra > 0) decision = new GoldenDecision(decision.Action, decision.Value,
                decision.Nodes + extra, decision.Depth, decision.Stop, decision.FallbackReason, decision.Candidates, decision.DriverKind) { SearchInfo = decision.SearchInfo };
            Result = decision; IsDone = true;
            // Once a kill is under way the rest of the turn plays it out untouched.
            if (decision != null && (decision.Value >= 999000 || (raw == null ? current.LethalResult : rawLethal) == "found")) owner.lethalCommitted = true;
        }
    }
    private sealed class FinishedJob : INodeJob { public bool IsDone => true; public void StepNode() { } }

    // The search's own pick first (at the best value), then the rest best-first; ties break on the action text.
    internal static List<KeyValuePair<BotAction, double>> Ranked(GoldenDecision result)
    {
        var listed = result.Candidates.GroupBy(c => c.Key).Select(g => new KeyValuePair<BotAction, double>(g.Key, g.Max(c => c.Value))).ToList();
        double best = listed.Count == 0 ? result.Value : listed.Max(c => c.Value);
        var ranked = new List<KeyValuePair<BotAction, double>> { new KeyValuePair<BotAction, double>(result.Action, best) };
        ranked.AddRange(listed.Where(c => !c.Key.Equals(result.Action))
            .OrderByDescending(c => c.Value).ThenBy(c => c.Key.ToString(), StringComparer.Ordinal));
        return ranked;
    }

    // Pure, unit-testable core: given the search's candidates (best first), return the chosen candidate index.
    public static int Choose(IReadOnlyList<KeyValuePair<BotAction, double>> candidates, SkillProfile skill, int seed,
        MistakeContext context) => Select(candidates, skill, seed, context).Index;

    public struct Choice
    {
        public int Index;
        public MistakeKind? Kind;   // the typed mistake that fired, null = best or softmax noise
    }

    // Typed mistakes in a fixed order (first eligible roll that fires wins; it takes the best eligible candidate),
    // then bounded softmax noise over searched candidates. Never drop the rest of the turn; only an explicit
    // single-card hold may pass when no other work remains.
    public static Choice Select(IReadOnlyList<KeyValuePair<BotAction, double>> candidates, SkillProfile skill, int seed,
        MistakeContext context, int searched = int.MaxValue)
    {
        var none = new Choice { Index = 0 };
        if (candidates == null || candidates.Count <= 1 || skill == null || skill.IsPerfect) return none;
        context = context ?? new MistakeContext(0, false, 0);
        searched = Math.Max(1, Math.Min(searched, candidates.Count));
        var best = candidates[0];
        if (best.Value >= 999000 || context.LethalFound) return none;
        bool doomed = best.Value <= -999000;
        Func<int, double, bool> allowed = (i, loss) => i > 0 && i < searched && (doomed || candidates[i].Value > -999000)
            && context.AllowsTarget(best.Key, candidates[i].Key)
            && (candidates[i].Key.Type != PlayerTaskType.END_TURN
                || best.Key.Type == PlayerTaskType.PLAY_CARD && context.EndTurnDropsOnlyHeld)
            && candidates[i].Value >= best.Value - loss - 1e-9;
        foreach (var kind in Order)
        {
            var rule = skill.Rule(kind);
            bool sticky = kind == MistakeKind.HoldTooLong && best.Key.Type == PlayerTaskType.PLAY_CARD && context.HeldSources.Contains(best.Key.Source);
            if (rule == null || rule.Chance <= 0) continue;
            double loss = rule.MaxValueLoss;
            int pick = -1;
            for (int i = 1; i < searched; i++)
                if (allowed(i, loss) && Matches(kind, best.Key, candidates[i].Key, context)
                    && (pick < 0 || candidates[i].Value > candidates[pick].Value)) pick = i;
            if (pick < 0) continue;
            if (!sticky && Roll(seed, kind) >= rule.Chance) continue;
            return new Choice { Index = pick, Kind = kind };
        }
        if (skill.Temperature <= 0 || skill.TopN <= 1) return none;
        var pool = new List<int> { 0 };
        for (int i = 1; i < searched && pool.Count < skill.TopN; i++)
            if (candidates[i].Key.Type != PlayerTaskType.END_TURN && allowed(i, skill.SoftmaxMaxValueLoss)) pool.Add(i);
        if (pool.Count == 1) return none;
        // Weights <= 1 by construction: every pooled value is at most the best.
        var weights = pool.Select(i => Math.Exp(Math.Min(0, candidates[i].Value - best.Value) / skill.Temperature)).ToArray();
        double roll = Unit(StableSeed.Derive(seed, 0x534D58)) * weights.Sum();
        for (int i = 0; i < pool.Count; i++) { roll -= weights[i]; if (roll < 0) return new Choice { Index = pool[i] }; }
        return new Choice { Index = pool[pool.Count - 1] };
    }

    private static readonly MistakeKind[] Order = { MistakeKind.HoldTooLong, MistakeKind.TradeTarget, MistakeKind.FaceBias, MistakeKind.Misorder };

    private static bool IsTrade(BotAction best, MistakeContext context) =>
        best.Target != 0 && best.Target != context.OpponentHeroId && context.OpponentEntityIds.Contains(best.Target);

    private static bool Matches(MistakeKind kind, BotAction best, BotAction other, MistakeContext context)
    {
        switch (kind)
        {
            case MistakeKind.TradeTarget:
                // Same attacker or spell from one enemy minion to another: never face, never the bot's own side.
                return IsTrade(best, context) && other.Type == best.Type && other.Source == best.Source
                    && other.Target != best.Target && other.Target != context.OpponentHeroId && context.OpponentEntityIds.Contains(other.Target);
            case MistakeKind.FaceBias:
                return context.OpponentHeroId != 0 && best.Target != context.OpponentHeroId && other.Target == context.OpponentHeroId
                    && (other.Type == PlayerTaskType.MINION_ATTACK || other.Type == PlayerTaskType.HERO_ATTACK);
            case MistakeKind.HoldTooLong:
                // Keeps only that card: anything not playing it (other plays, attacks, hero power, a location).
                // Ending the turn qualifies only when the held card is all that is left to do.
                return !context.FreeBestCard && best.Type == PlayerTaskType.PLAY_CARD && other.Source != best.Source && other.Type != PlayerTaskType.CHOOSE
                    && (other.Type != PlayerTaskType.END_TURN || context.EndTurnDropsOnlyHeld);
            case MistakeKind.Misorder:
                // Needs the search's own line (a later plan step); without it the kind never fires.
                return context.PlanLine != null && best.Type != PlayerTaskType.END_TURN && best.Type != PlayerTaskType.CHOOSE
                    && other.Type != PlayerTaskType.END_TURN && context.PlanLine.Skip(1).Contains(other);
            default: return false;
        }
    }
}

// Public facts the mistake model may condition on (all derivable from the fair view / own decision trace).
public sealed class MistakeContext
{
    private static readonly int[] NoIds = new int[0];
    public int Turn { get; }
    public bool LethalFound { get; }
    public int LethalActions { get; }       // 0 if unknown (strength package exposes line length later)
    public bool HumanMisplayDetected { get; }
    public int OpponentHeroId { get; }
    public IReadOnlyCollection<int> OpponentEntityIds { get; }   // public enemy board minions (and hero, if listed)
    public IReadOnlyList<BotAction> PlanLine { get; }            // the search's own line when known (Misorder), else null
    public IReadOnlyCollection<int> HeldSources { get; }         // cards this turn's HoldTooLong already kept back
    private readonly MistakeTargetGuard targets;
    internal bool AllowsTarget(BotAction best, BotAction alternative) => targets == null || targets.Allows(best, alternative);
    public bool FreeBestCard { get; }
    public bool EndTurnDropsOnlyHeld { get; }                    // every non-END_TURN legal action plays the best's card
    public MistakeContext(int turn, bool lethalFound, int lethalActions, bool humanMisplayDetected = false,
        int opponentHeroId = 0, IEnumerable<int> opponentEntityIds = null, IEnumerable<BotAction> planLine = null,
        IEnumerable<int> heldSources = null, bool endTurnDropsOnlyHeld = false, bool freeBestCard = false, FairFacts facts = null)
    { Turn = turn; LethalFound = lethalFound; LethalActions = lethalActions; HumanMisplayDetected = humanMisplayDetected;
      OpponentHeroId = opponentHeroId; OpponentEntityIds = new HashSet<int>(opponentEntityIds ?? NoIds);
      PlanLine = planLine == null ? null : GoldenData.Copy(planLine); HeldSources = new HashSet<int>(heldSources ?? NoIds);
      EndTurnDropsOnlyHeld = endTurnDropsOnlyHeld; FreeBestCard = freeBestCard;
      targets = facts == null || GoldenAblation.NoMistakeTargetGuard ? null : new MistakeTargetGuard(facts); }
    public bool IsOpponent(int entityId) => entityId == OpponentHeroId || OpponentEntityIds.Contains(entityId);
}

// WP2. Skill-aware mulligan (GreedyMulligan); installed through NpcWorldHooks.MulliganFor.
// A greedy human keeps the shiny expensive card, or throws back a keep that needed the right partner.
public sealed class SkilledMulligan : MulliganPolicy
{
    public DeckPlan Plan { get; }
    public SkillProfile Skill { get; }
    public int Seed { get; }
    public SkilledMulligan(DeckPlan plan, SkillProfile skill, int seed) { Plan = plan; Skill = skill; Seed = seed; }
    public bool Greedy => Skill != null && SkilledPilot.Roll(Seed, MistakeKind.GreedyMulligan) < Skill.Chance(MistakeKind.GreedyMulligan);
    public override IReadOnlyList<int> Keep(MulliganView view)
    {
        var kept = new DeckPlanMulligan(Plan).Keep(view).ToList();
        if (!Greedy) return kept;
        try
        {
            var offered = view.Offered.Where(c => c.CardId != "GAME_005").ToArray();
            var expensive = offered.Where(c => c.Cost >= 4 && !kept.Contains(c.EntityId))
                .OrderByDescending(c => c.Cost).ThenBy(c => c.EntityId).FirstOrDefault();
            if (expensive != null) kept.Add(expensive.EntityId);
            else
            {
                var unconditional = new DeckPlanMulligan(new DeckPlan(Plan.Id, Plan.DeckId, Plan.MatchupClasses,
                    Plan.MulliganRules.Where(r => r.Condition == MulliganCondition.Always), Plan.CardRoles, Plan.Holds,
                    Plan.RemovalThresholds, Plan.AggressionCurve, Plan.Hints, Plan.DiscoverPreferences, Plan.RewindPreferences,
                    Plan.Weights)).Keep(view);
                var conditional = offered.Where(c => kept.Contains(c.EntityId) && !unconditional.Contains(c.EntityId))
                    .OrderByDescending(c => c.Cost).ThenBy(c => c.EntityId).FirstOrDefault();
                if (conditional != null) kept.Remove(conditional.EntityId);
            }
            return Array.AsReadOnly(offered.Where(c => kept.Contains(c.EntityId)).Select(c => c.EntityId).ToArray());
        }
        catch (Exception error) when (!(error is OutOfMemoryException)) { return new DeckPlanMulligan(Plan).Keep(view); }
    }
}

// A runner deadline for lethal workers, kept on a wrapper job so a replacement inner job gets what is left of it.
internal sealed class BackgroundDeadline
{
    private long at = long.MaxValue;
    internal void Limit(double milliseconds)
    {
        long limit = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(Math.Max(0, milliseconds) * System.Diagnostics.Stopwatch.Frequency / 1000);
        if (limit < at) at = limit;
    }
    internal void Apply(IGoldenDecisionJob job)
    {
        if (at == long.MaxValue || !(job is INpcBackgroundSearch background)) return;
        background.LimitBackground(Math.Max(0, (at - System.Diagnostics.Stopwatch.GetTimestamp()) * 1000.0 / System.Diagnostics.Stopwatch.Frequency));
    }
}
