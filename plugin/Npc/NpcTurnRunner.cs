using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;

// Optional planner progress hook; final-result candidates remain available for existing decision jobs.
public interface INpcThinkingCandidates
{
    IReadOnlyList<BotAction> Candidates { get; }
}

public interface INpcSearchContinuation
{
    GoldenDecision StopSearch(SearchStop stop);
}

// Independent pass reserve: StopSearch can leave a comparison pending. The runner owns slicing and hard cutoff.
internal interface INpcPassValidation
{
    bool Validating { get; }
    GoldenDecision StopValidation();
}

// Live wall-clock budget: the runner caps the plan search's node allowance when the planner starts, from the node rate
// measured on this client. Returns a negative value for no cap. Never set on the node-budget (harness) path.
public interface INpcPlanNodeCap
{
    void CapPlanNodes(Func<int> cap);
}

internal interface INpcPreparedDecision { bool Preparing { get; } }
internal interface INpcBackgroundSearch
{
    int BackgroundNodes { get; }
    bool Waiting { get; }
    bool BackgroundClock { get; }
    bool BackgroundRunning { get; }
    void LimitBackground(double milliseconds);
    void PollBackground();
    void StopBackground();
}
// Decision cost telemetry: actions in the deepest line this decision's searches built.
internal interface INpcSearchDepth { int Deepest { get; } }
// Cheap, generous gate (never a pruning bound): the owner's damage this turn could reach the opponent's health + armour.
internal interface INpcLethalBound { bool LethalPlausible { get; } }

public enum NpcTurnState { PreTurnPause, Thinking, Act, WaitForIdle, ActionPause, Complete, Cancelled }

// All callbacks run on the pumping thread. Clocks bound slices, never the number/order of search nodes.
public sealed class NpcTurnRunner
{
    // Guard on the wait for the native board to settle before the next action. Live 2026-10-25 (lane 1, 63/106 waits hit
    // it): the probe was right, the client was still playing earlier NPC actions (opponent big cards block the native power
    // queue for 1-4 s at ~14 fps). Acting then only queues the packet behind them, so the guard costs no wall time; on an
    // idle board the wait ends on the first tick. The timeout line names what was still busy.
    internal const double IdleWaitMilliseconds = 1500;
    // Live 2026-10-25 round 2 (lane 1, ~14 fps): search p95 10.4 s per NPC turn, up to 4 s per decision. Budget: about
    // 5 s of search per turn. Before the planner starts, its node allowance is sized from this client's measured node
    // rate times what is left of the turn budget (less a reserve for later replans when enough is left), so a fast
    // client gets the full plan budget and a slow one a proportionally smaller search that still finishes by nodes
    // (reply scoring included). The clock cut at the turn budget is the safety net and takes the golden search's best
    // line so far. Decisions after the budget is spent still search briefly (plan continuations stay free). Clocks
    // never change the node-budget harness path.
    internal const double TurnSearchMilliseconds = 5000;
    // Shipped tiers set Budget.TurnSearchSeconds (owner 2026-11-01: quality over speed within a retail turn; elite since
    // 2026-10-25); a profile without it keeps 5 s. TurnWallSeconds stops new search that late in the turn (wall time since the turn began, less pauses for human
    // choices made during it), so the remaining actions and their animations finish before the 75 s retail turn timer.
    // A finite turn deadline (Nozdormu: 15 s) clamps the guard to the deadline less DeadlineReserveMilliseconds.
    public double TurnSearchBudgetMilliseconds => profile.Budget.TurnSearchSeconds > 0 ? 1000 * profile.Budget.TurnSearchSeconds : TurnSearchMilliseconds;
    public double TurnWallBudgetMilliseconds => profile.Budget.TurnWallSeconds > 0 ? 1000 * profile.Budget.TurnWallSeconds : double.PositiveInfinity;
    // This turn entered fast finish (FastFinish): later decisions skip search while the turn budget stays exhausted.
    private bool fastFinishing;
    public bool FastFinishing => fastFinishing;
    // Every wall-aware rule below applies to guarded (elite+) profiles only; other profiles behave exactly as before.
    private bool WallGuarded => profile.Budget.TurnWallSeconds > 0;
    // Guarded profiles: search shares the wall with rendering and native work (live think wall ~1.5-1.8x search), so a
    // decision's search limit and plan cap use (wall left before the guard - margin) x the measured search share; the
    // plan then finishes by nodes, replies included, before the guard. Share: search / think wall over this match, else
    // slice / (slice + frame rest), else 0.5.
    internal const double WallMarginMilliseconds = 1000;
    internal const double DefaultSearchShare = 0.5;
    internal const double DeadlineReserveMilliseconds = 5000;
    internal const double ReplanReserveMilliseconds = 1000;
    internal const double LateDecisionMilliseconds = 300;
    // Guarded profiles, past the wall guard (owner 2026-10-27, replaces the per-decision 1.5 s floor that made every late
    // action take ~1.4 s): while a plausible lethal search is unfinished, the decisions of one NPC turn may together search
    // this much beyond their nominal limit. Spent only while lethal runs (an interrupted extension still counts); reset by
    // Start(); never into the hard deadline's reserve and not when that deadline is the binding guard (Nozdormu).
    internal const double LateLethalPoolMilliseconds = 1500;
    // The pre-2026-10-27 per-decision floor. Used only by the harness comparison variant GoldenAblation.LateLethal =
    // DecisionFloor; the shipped rule above does not read it.
    internal const double LateLethalMilliseconds = 1500;
    internal const double NodeRateMargin = 0.8;
    // Actions per NPC turn; the next decision ends the turn instead (an endless free loop, e.g. Farseer Wo + a 0-cost
    // Ancestral Healing, could otherwise act until the 75 s deadline). The harness (MatchRunner.ActionLimit) uses it too.
    internal const int MaxTurnActions = 127;
    internal const int MinLivePlanNodes = 100;
    private readonly NpcProfile profile;
    private readonly string matchId;
    private readonly Func<double> clock;
    private readonly Func<bool> canAct, idle;
    private readonly Func<GoldenContext> context;
    private readonly Func<GoldenContext, IGoldenDecisionJob> begin;
    private readonly Func<string, GoldenDecision> fallback;
    private readonly Action<GoldenDecision> act;
    private readonly Action<string> log;
    private readonly IMatchRecorder recorder;
    private readonly Func<int> publicEventIndex;
    private NodeScheduler scheduler;
    private NodeScheduler lethalScheduler;
    private int lethalNodes, lethalWorkers;
    private IGoldenDecisionJob job;
    private GoldenContext current;
    private GoldenDecision decision;
    private double until, waitStarted, decisionSearch, decisionFreeze, decisionSlice, decisionLimit, sliceStart, thinkStarted, decisionWall;
    private int actions, pauseIndex, decisionNodes, decisionCap = -1;
    // Decision cost telemetry: managed heap at the decision's first slice and its peak over slice ends, deepest line.
    private long decisionHeapStart, decisionHeapPeak;
    private int decisionDeepest;
    // Nodes per search millisecond on this client, measured over completed decisions of this match.
    private double nodeRate;
    private string lethal = "unavailable";
    internal bool LethalFound => lethal == "found";
    private bool reuse;
    private readonly int turnNodes;
    private int fallbackNodes, decisionFallbackStart;
    // Late-lethal pool: spent this turn, left at this decision's start, nominal (reserve inside) at the last extended check.
    private double latePoolSpent, decisionPoolStart, decisionPoolNominal, decisionPoolUsed;
    private bool decisionPlausible, decisionExtended, decisionClockCut;
    // Lethal workers search between frames: wall since the previous slice of this decision while they were still running
    // (-1: they were not), charged with the next slice.
    private double workersSince = -1, sliceGap;
    public double LatePoolSpentMilliseconds => latePoolSpent;
    public int LateExtendedDecisions { get; private set; }
    internal bool SpendFallbackNode() { if (fallbackNodes <= 0 || SearchLimitReached(sliceStart, true, false)) return false; fallbackNodes--; return true; }
    private readonly Func<BotAction, double> actedCardDwell;
    // A native-display wait before acting (opposing choice backs being dealt). It bounds itself and is not pacing, so it
    // never spends the 4 s artificial-delay budget.
    private readonly Func<BotAction, double> actHold;
    private readonly Func<string> busyReason;
    private double turnStarted;
    // Wall time paused for human choices made during this turn (Cancel .. Resume), excluded from the wall guard.
    private double pausedMilliseconds, pausedAt = -1;
    // Search share accounting over completed decisions of this match (search ms, think wall ms), last frame slice.
    private double matchSearch, matchThinkWall, lastSlice = -1;
    // Remaining wall ms to a hard turn deadline (Nozdormu), +infinity or null for none.
    private readonly Func<double> turnDeadlineLeft;
    private int validationReserveNodes;
    private bool? clockLimitedTurn;
    private int gcTurnStart, gcFullTurnStart;
    public bool IdleTimedOut { get; private set; }
    // Per-turn wait accounting (reset by Start): waits that ended idle, waits that hit the guard, total wall ms waited.
    public int IdleWaits { get; private set; }
    public int IdleTimeouts { get; private set; }
    public double IdleWaitedMilliseconds { get; private set; }
    public NpcTurnState State { get; private set; } = NpcTurnState.Complete;
    public bool Active => State != NpcTurnState.Complete && State != NpcTurnState.Cancelled;
    // A decision of this turn hit its search clock (the search stopped on its best line so far).
    public bool ClockCut { get; private set; }
    public int ClockCuts { get; private set; }
    public int Decisions { get; private set; }
    public int CappedDecisions { get; private set; }
    public int TurnSearchNodes { get; private set; }
    public double NodeRate => nodeRate;
    internal void SeedNodeRate(double rate) { nodeRate = rate; }
    // Owner-thread search nodes this turn, finished and running decisions, lethal excluded (workers on the Deck): the
    // harness virtual clock (sim/AiHarness/DeckClock.cs).
    private int ownerNodes;
    internal int OwnerNodes => ownerNodes + (job == null || decision != null ? 0
        : Math.Max(0, scheduler.Nodes - decisionNodes) + validationReserveNodes + decisionFallbackStart - fallbackNodes);
    // Wall time from a decision's first search slice to its result (search plus the frames between slices): the
    // visible think when the board is idle. Telemetry only; budgets use search time.
    public double ThinkWallMilliseconds { get; private set; }
    public double SearchMilliseconds { get; private set; }
    public double FreezeSearchMilliseconds { get; private set; }
    public double CoveredSearchMilliseconds { get; private set; }
    public double ArtificialDelayMilliseconds { get; private set; }
    public double MaxSliceMilliseconds { get; private set; }
    // Search per frame while the board animates (keeps animations smooth).
    public double SliceMilliseconds { get; set; } = 10;
    // Search per frame while the NPC visibly thinks on an idle board: at least this much, more (up to
    // MaxIdleSliceMilliseconds) when the rest of the frame is short enough to keep FrameTargetMilliseconds (25 fps).
    // Round 2: fixed 10 ms slices stretched 5 s of search over ~500 frames (~36 s at 14 fps).
    public double IdleSliceMilliseconds { get; set; } = 30;
    public double MaxIdleSliceMilliseconds { get; set; } = 40;
    internal const double FrameTargetMilliseconds = 40;
    // Non-search part of a frame between two consecutive search slices (EMA, -1 unknown), and search frames this turn.
    private double frameOther = -1, lastThinkEnd = -1;
    public int SearchFrames { get; private set; }
    public double FrameOtherMilliseconds => frameOther;
    // Only candidates actually returned by the current planner; no invented random hand hover.
    public IReadOnlyList<BotAction> Candidates => decision == null
        ? (job as INpcThinkingCandidates)?.Candidates ?? new BotAction[0] : new[] { decision.Action }
            .Concat((decision.Candidates ?? new KeyValuePair<BotAction, double>[0]).Select(pair => pair.Key))
            .Where(action => action != null).Distinct().Take(3).ToArray();

    public NpcTurnRunner(NpcProfile profile, string matchId, Func<double> clock, Func<bool> canAct,
        Func<GoldenContext> context, Func<GoldenContext, IGoldenDecisionJob> begin,
        Func<string, GoldenDecision> fallback, Action<GoldenDecision> act, Func<bool> idle,
        Action<string> log = null, IMatchRecorder recorder = null, Func<int> publicEventIndex = null, int? turnNodes = null,
        Func<BotAction, double> actedCardDwell = null, Func<string> busyReason = null, Func<double> turnDeadlineLeft = null,
        Func<BotAction, double> actHold = null)
    {
        this.busyReason = busyReason; this.turnDeadlineLeft = turnDeadlineLeft; this.actHold = actHold;
        this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
        this.turnNodes = turnNodes ?? profile.Budget.TurnNodes;
        this.matchId = matchId ?? ""; this.clock = clock; this.canAct = canAct;
        this.context = context; this.begin = begin; this.fallback = fallback; this.act = act;
        this.publicEventIndex = publicEventIndex;
        this.actedCardDwell = actedCardDwell;
        this.idle = idle ?? (() => true); this.log = log; this.recorder = recorder ?? NullMatchRecorder.Instance;
    }
    public void Start()
    {
        (job as INpcBackgroundSearch)?.StopBackground();
        // Reserve bounded fair one-ply work so a planner cutoff still has a safe fallback.
        fallbackNodes = turnNodes >= 100 ? Math.Min(16, turnNodes / 20) : 0;
        scheduler = new NodeScheduler(turnNodes - fallbackNodes);
        actions = pauseIndex = 0;
        SearchMilliseconds = FreezeSearchMilliseconds = CoveredSearchMilliseconds = ArtificialDelayMilliseconds = MaxSliceMilliseconds = 0;
        ClockCut = IdleTimedOut = false; job = null; decision = null;
        ClockCuts = Decisions = CappedDecisions = TurnSearchNodes = ownerNodes = SearchFrames = LateExtendedDecisions = 0; ThinkWallMilliseconds = 0;
        latePoolSpent = 0; workersSince = -1;
        IdleWaits = IdleTimeouts = 0; IdleWaitedMilliseconds = 0;
        turnStarted = clock(); pausedMilliseconds = 0; pausedAt = -1;
        fastFinishing = false; clockLimitedTurn = null;
        gcTurnStart = GC.CollectionCount(0); gcFullTurnStart = GC.CollectionCount(GC.MaxGeneration);
        Pause(true);
    }
    // A human-owned choice can interrupt the bot's turn; preserve its search/delay budgets on resumption.
    // The interruption's wall time (human choice) is excluded from the wall guard.
    public void Resume()
    {
        if (State != NpcTurnState.Complete && State != NpcTurnState.Cancelled) return;
        if (pausedAt >= 0) { pausedMilliseconds += Math.Max(0, clock() - pausedAt); pausedAt = -1; }
        job = null; decision = null; State = NpcTurnState.Thinking;
    }
    public void Cancel()
    {
        // Discard queued continuations, but never refund work consumed before an interrupt.
        if (scheduler != null) scheduler = new NodeScheduler(scheduler.RemainingNodes);
        if (pausedAt < 0) pausedAt = clock();
        (job as INpcBackgroundSearch)?.StopBackground();
        // An extension interrupted by a human choice was still spent: Resume() starts a fresh decision on the same pool.
        if (decision == null) ChargeLatePool();
        decisionExtended = false;
        job = null; decision = null; State = NpcTurnState.Cancelled;
    }
    public void RunSynchronously() { while (Active) Tick(true); }
    public void Tick(bool synchronous = false)
    {
        if (!Active) return;
        try { TickCore(synchronous); }
        catch { Cancel(); throw; }
    }
    private void TickCore(bool synchronous)
    {
        if (!canAct()) { Cancel(); State = NpcTurnState.Complete; return; }
        switch (State)
        {
            case NpcTurnState.PreTurnPause:
            case NpcTurnState.ActionPause:
                if (synchronous || clock() >= until) State = NpcTurnState.Thinking;
                return;
            case NpcTurnState.Thinking:
                Think(synchronous);
                return;
            case NpcTurnState.Act:
                if (!synchronous && clock() < until) return;
                if (!synchronous && actHold != null)
                {
                    double hold;
                    try { hold = actHold(decision.Action); } catch (Exception) { hold = 0; }
                    if (hold > 0) { until = clock() + Math.Min(hold, 1000); return; }
                }
                if (!synchronous && actedCardDwell != null)
                {
                    double dwell = Math.Min(Math.Max(0, 4000 - ArtificialDelayMilliseconds), actedCardDwell(decision.Action));
                    if (dwell > 0) { ArtificialDelayMilliseconds += dwell; until = clock() + dwell; return; }
                }
                var selected = decision;
                selected.SearchInfo.SearchBudgetMilliseconds = decisionLimit;
                decision = null; job = null; State = NpcTurnState.WaitForIdle;
                int searched = scheduler.Nodes - decisionNodes + lethalNodes + decisionFallbackStart - fallbackNodes + selected.SearchInfo.UnscheduledNodes;
                if (recorder.Enabled) recorder.OnDecision(current, selected, lethal, reuse,
                    searched, decisionSearch, decisionSlice);
                double heapMb = decisionHeapPeak / 1048576.0, growthMb = Math.Max(0, decisionHeapPeak - decisionHeapStart) / 1048576.0;
                log?.Invoke(string.Format(CultureInfo.InvariantCulture,
                    "npc={0} turn={1} reuse={2} lethal={3} slice={4:F2} ms={5:F2} nodes={6} action={7} fallback={8} freeze={9:F2} limit={10:F0} cap={11} stop={12} wall={13:F0} lethalWorkers={14} latePool={15:F0} heapMB={16:F0} heapGrowthMB={17:F0} deepest={18} planCap={19} interrupted={20} validationCap={21} validationNodes={22} mistake={23} unscheduledNodes={24} validationCompared={25} validationDeadline={26} validationFallback={27} fastFinish={28} replan={29}",
                    profile.Id, current.Turn, reuse ? 1 : 0, lethal, decisionSlice, decisionSearch,
                    searched, selected.Action, selected.FallbackReason ?? "none", decisionFreeze, decisionLimit, decisionCap, selected.Stop, decisionWall, lethalWorkers, decisionPoolUsed,
                    heapMb, growthMb, decisionDeepest, selected.SearchInfo.PlanNodeCap, selected.SearchInfo.Interrupted ? 1 : 0,
                    selected.SearchInfo.ValidationNodeCap, selected.SearchInfo.ValidationNodes, selected.SearchInfo.Mistake,
                    selected.SearchInfo.UnscheduledNodes, selected.SearchInfo.ValidationCompared ? 1 : 0,
                    selected.SearchInfo.ValidationDeadline ? 1 : 0, selected.SearchInfo.ValidationFallback ? 1 : 0,
                    selected.SearchInfo.FastFinish ?? "-", selected.SearchInfo.Replan ?? "-"));
                string costFlags = NpcDecisionCost.Flags(decisionWall / 1000, growthMb, decisionDeepest);
                if (costFlags != null) log?.Invoke(string.Format(CultureInfo.InvariantCulture,
                    "npc={0} WARNING decision-cost={1} turn={2} turnDecision={3} thinkMs={4:F0} searchMs={5:F0} heapMB={6:F0} heapGrowthMB={7:F0} deepest={8} nodes={9} action={10}",
                    profile.Id, costFlags, current.Turn, Decisions, decisionWall, decisionSearch, heapMb, growthMb, decisionDeepest, searched, selected.Action));
                if (!Active || !canAct()) { Cancel(); return; }
                act(selected); actions++;
                if (State == NpcTurnState.Cancelled) return;
                // Completing without Cancel(): mark the pause start too, so a later Resume() excludes this wall time.
                if (!canAct()) { if (pausedAt < 0) pausedAt = clock(); State = NpcTurnState.Complete; LogTurnSummary(); return; }
                Pause(false);
                return;
            case NpcTurnState.WaitForIdle:
                bool ready = synchronous;
                if (!ready) { try { ready = idle(); } catch { ready = false; } }
                if (!ready && clock() - waitStarted < IdleWaitMilliseconds) return;
                if (!synchronous)
                {
                    IdleWaitedMilliseconds += Math.Max(0, clock() - waitStarted);
                    if (ready) IdleWaits++; else IdleTimeouts++;
                }
                if (!ready)
                {
                    IdleTimedOut = true;
                    string busy = null;
                    try { busy = busyReason?.Invoke(); } catch (Exception) { busy = "probe-error"; }
                    log?.Invoke("npc=" + profile.Id + " idle-timeout=" + IdleWaitMilliseconds + (busy != null ? " busy=" + busy : ""));
                }
                State = NpcTurnState.Act;
                goto case NpcTurnState.Act;
        }
    }
    private void Think(bool synchronous)
    {
        double start = clock();
        bool freeze = true;
        // Charge the whole slice using its starting animation state; failed probes get no free time.
        if (!synchronous) { try { freeze = idle(); } catch { freeze = true; } }
        double slice = Math.Max(1, SliceMilliseconds);
        if (!synchronous)
        {
            // Consecutive search frames measure the rest of the frame (render, native work); a gap means other states ran.
            if (lastThinkEnd >= 0 && start - lastThinkEnd >= 0 && start - lastThinkEnd < 250)
                frameOther = frameOther < 0 ? start - lastThinkEnd : .7 * frameOther + .3 * (start - lastThinkEnd);
            if (freeze) slice = Math.Max(IdleSliceMilliseconds, frameOther < 0 ? IdleSliceMilliseconds
                : Math.Min(MaxIdleSliceMilliseconds, FrameTargetMilliseconds - frameOther));
            slice = Math.Max(1, slice);
            SearchFrames++; lastSlice = slice;
        }
        bool selectingFallback = false;
        int fallbackBefore = fallbackNodes;
        Func<string, GoldenDecision> chooseFallback = reason => { selectingFallback = true; return fallback(reason); };
        sliceGap = job != null && workersSince >= 0 ? Math.Max(0, start - workersSince) : 0;
        try
        {
            if (job == null)
            {
                current = context(); decisionNodes = scheduler.Nodes; decisionFallbackStart = fallbackNodes;
                decisionSearch = decisionFreeze = decisionSlice = 0; lethal = "unavailable"; reuse = false; lethalNodes = lethalWorkers = 0; lethalScheduler = null;
                decisionLimit = Math.Max(LateDecisionMilliseconds, TurnSearchBudgetMilliseconds - SearchMilliseconds);
                decisionCap = -1; validationReserveNodes = 0; Decisions++; thinkStarted = start;
                decisionHeapStart = decisionHeapPeak = GC.GetTotalMemory(false); decisionDeepest = 0;
                workersSince = -1; decisionPoolUsed = 0; decisionExtended = decisionPlausible = decisionClockCut = false;
                decisionPoolStart = UsesLatePool && WallGuarded ? Math.Max(0, LateLethalPoolMilliseconds - latePoolSpent) : 0;
                // Wall guard (elite+ only): search must also end before the guard, in search time at the measured share.
                if (WallGuarded) decisionLimit = Math.Max(LateDecisionMilliseconds, Math.Min(decisionLimit, WallSearchLeft(start, 0)));
                if (actions >= MaxTurnActions)
                { decision = chooseFallback("action-limit"); }
                // Fast finish already fired this turn and the budget is still spent: search-free completion.
                else if (fastFinishing && TurnBudgetSpent(start))
                { decision = chooseFallback(FastFinish.Reason); }
                else
                {
                    job = begin(current);
                    decisionPlausible = decisionPoolStart > 0 && (job as INpcLethalBound)?.LethalPlausible == true;
                    lethalWorkers = (job as INpcBackgroundSearch)?.BackgroundClock == true ? 2
                        : (job as INpcLethalSearch)?.LethalSearch != null ? 1 : 0;
                    (job as INpcPlanNodeCap)?.CapPlanNodes(LivePlanNodes);
                    if (job is INpcBackgroundSearch background && background.BackgroundClock)
                    {
                        // Same decision window as the owner (late-lethal pool included), including setup time already spent.
                        background.LimitBackground(BackgroundWindow() - Math.Max(0, clock() - start));
                    }
                    var lethalSearch = (job as INpcLethalSearch)?.LethalSearch;
                    if (lethalSearch != null)
                    {
                        // The fresh probe has its own small allowance; ordinary lethal strength is unchanged.
                        lethalScheduler = new NodeScheduler(profile.Budget.LethalNodes + GoldenDecisionJob.LethalRecheckNodes);
                        lethalScheduler.Enqueue(lethalSearch);
                    }
                    scheduler.Enqueue(job);
                }
            }
            sliceStart = start;
            while (decision == null)
            {
                // Preparation and every expansion count, even when a single node exceeds a slice.
                if ((job as INpcPassValidation)?.Validating == true) {
                    if (WallLeft(clock()) <= 0) {
                        decision = ((INpcPassValidation)job).StopValidation();
                        MarkClockCut();
                    } else {
                        validationReserveNodes++; job.StepNode();
                        if (job.IsDone) decision = job.Result;
                    }
                    if (decision != null) { lethal = job.LethalResult; reuse = job.PlanReused; break; }
                    if (!synchronous && clock() - start >= slice) break;
                    continue;
                }
                if (SearchLimitReached(start, freeze))
                {
                    CutClock(chooseFallback);
                    if (decision != null || !synchronous && clock() - start >= slice) break;
                    continue;
                }
                var currentLethal = (job as INpcLethalSearch)?.LethalSearch;
                if (currentLethal != null && !currentLethal.IsDone
                    && ((job as INpcPreparedDecision)?.Preparing != true || scheduler.RemainingNodes == 0))
                {
                    if (lethalScheduler == null) lethalScheduler = new NodeScheduler(profile.Budget.LethalNodes + GoldenDecisionJob.LethalRecheckNodes);
                    // Wrappers can replace a finished search, including behind a stable forwarder already dequeued.
                    // Refresh the queue, retaining the decision's spent nodes and original bounded allowance.
                    lethalScheduler.DiscardJobs();
                    lethalScheduler.Enqueue(currentLethal);
                    if (lethalScheduler.RemainingNodes > 0) {
                        lethalScheduler.Step(1); lethalNodes = lethalScheduler.Nodes;
                        if (!synchronous && clock() - start >= slice) break;
                        continue;
                    }
                    // Once the separate allowance is spent, ordinary job steps use the remaining turn allocation.
                }
                if (job is INpcBackgroundSearch waiting && (waiting.Waiting || scheduler.RemainingNodes == 0 && waiting.BackgroundRunning))
                {
                    waiting.PollBackground(); lethalNodes = waiting.BackgroundNodes + (lethalScheduler?.Nodes ?? 0);
                    if (waiting.Waiting || scheduler.RemainingNodes == 0 && waiting.BackgroundRunning) break;
                }
                // Completing a proven lethal is bookkeeping, even after planner allocation is exhausted.
                if ((lethalScheduler != null || job is INpcBackgroundSearch) && job.LethalResult == "found" && !job.IsDone) job.StepNode();
                var status = job.IsDone ? NodeStepResult.Done : scheduler.Step(1);
                if (job is INpcBackgroundSearch background && background.BackgroundClock) lethalNodes = background.BackgroundNodes + (lethalScheduler?.Nodes ?? 0);
                if (job.IsDone)
                {
                    decision = job.Result;
                    if (lethalScheduler != null && lethalNodes > 0) decision = new GoldenDecision(decision.Action, decision.Value, decision.Nodes + lethalScheduler.Nodes,
                        decision.Depth, decision.Stop, decision.FallbackReason, decision.Candidates, decision.DriverKind) { SearchInfo = decision.SearchInfo };
                    lethal = job.LethalResult; reuse = job.PlanReused;
                    if (decision.FallbackReason == "turn-nodes") decision = chooseFallback("turn-nodes");
                }
                else if (status == NodeStepResult.Done) {
                    decision = (job as INpcSearchContinuation)?.StopSearch(SearchStop.Nodes);
                    if (decision == null && (job as INpcPassValidation)?.Validating != true) decision = chooseFallback("turn-nodes");
                    if (!selectingFallback && decision?.FallbackReason == "turn-nodes") decision = chooseFallback("turn-nodes");
                    lethal = job.LethalResult; reuse = job.PlanReused;
                }
                if (!synchronous && clock() - start >= slice) break;
            }
            if (decision == null && (job as INpcPassValidation)?.Validating != true && SearchLimitReached(start, freeze))
                CutClock(chooseFallback);
        }
        catch (Exception error)
        {
            if (selectingFallback) throw;
            (job as INpcBackgroundSearch)?.StopBackground();
            if (job is INpcBackgroundSearch background && background.BackgroundClock) lethalNodes = background.BackgroundNodes + (lethalScheduler?.Nodes ?? 0);
            else if (lethalScheduler != null) lethalNodes = lethalScheduler.Nodes;
            var original = error is SimulationFailureException wrapped ? wrapped.InnerException : error;
            decision = chooseFallback(original.GetType().Name + ":" + original.Message);
            // Do not revisit a failed continuation on the next decision.
            scheduler.DiscardJobs();
        }
        finally
        {
            double elapsed = Charged(start);
            SearchMilliseconds += elapsed; decisionSearch += elapsed;
            workersSince = decision == null && job is INpcBackgroundSearch workers && workers.BackgroundClock && workers.BackgroundRunning ? clock() : -1;
            if (freeze) { FreezeSearchMilliseconds += elapsed; decisionFreeze += elapsed; }
            else CoveredSearchMilliseconds += elapsed;
            decisionSlice = Math.Max(decisionSlice, elapsed); MaxSliceMilliseconds = Math.Max(MaxSliceMilliseconds, elapsed);
            decisionHeapPeak = Math.Max(decisionHeapPeak, GC.GetTotalMemory(false));
            if (!synchronous) lastThinkEnd = clock();
        }
        if (decision != null)
        {
            decision.SearchInfo.UnscheduledNodes += validationReserveNodes;
            decisionDeepest = (job as INpcSearchDepth)?.Deepest ?? 0;
            int searched = Math.Max(0, scheduler.Nodes - decisionNodes) + lethalNodes + fallbackBefore - fallbackNodes + decision.SearchInfo.UnscheduledNodes;
            TurnSearchNodes += searched; ownerNodes += searched - lethalNodes;
            ChargeLatePool();
            decisionWall = Math.Max(0, clock() - thinkStarted); ThinkWallMilliseconds += decisionWall;
            matchSearch += decisionSearch; matchThinkWall += decisionWall;
            // Plan-dominated decisions only: reused continuations and tiny searches say nothing about the node rate.
            if (decisionSearch >= 50 && searched >= 50)
            {
                double rate = searched / decisionSearch;
                nodeRate = nodeRate > 0 ? (nodeRate + rate) / 2 : rate;
            }
            if (decision.DriverKind == FastFinish.DriverKind && decision.Action != null
                && decision.Action.Type != SabberStoneCore.Tasks.PlayerTasks.PlayerTaskType.END_TURN) fastFinishing = true;
            until = waitStarted = clock(); State = NpcTurnState.WaitForIdle;
        }
    }
    // Called once by the job when its planner starts (after lethal search and plan reuse), so the allowance fits what is
    // left of this decision's clock. Uses the match's measured rate, else this decision's rate so far; no estimate, no cap.
    private int LivePlanNodes()
    {
        double spent = decisionSearch + Math.Max(0, clock() - sliceStart);
        int searched = Math.Max(0, scheduler.Nodes - decisionNodes) + lethalNodes;
        double rate = nodeRate > 0 ? nodeRate : spent >= 10 && searched >= 20 ? searched / spent : 0;
        if (rate <= 0 || double.IsNaN(rate) || double.IsInfinity(rate)) return -1;
        double left = decisionLimit - spent;
        // Guarded profiles: the plan, replies included, must finish by nodes before the wall guard (late slice as floor).
        if (WallGuarded) left = Math.Min(left, Math.Max(LateDecisionMilliseconds - spent, WallSearchLeft(clock(), spent)));
        if (left > 2 * ReplanReserveMilliseconds) left -= ReplanReserveMilliseconds;
        double nodes = WithReplanReserve(rate * Math.Max(0, left) * NodeRateMargin);
        decisionCap = nodes >= int.MaxValue ? int.MaxValue : Math.Max(MinLivePlanNodes, (int)nodes);
        if (decisionCap < profile.Budget.PlanNodes) CappedDecisions++;
        return decisionCap;
    }
    // replan-reserve: a replan on a clock-limited turn leaves the next one a share, so a chain of replans keeps reply searches
    // (a replan under about 1,750 nodes has none); under ViableReplanNodes it may spend all, nothing later could use the rest.
    // The turn's first search is never cut: TurnPlanner only holds back its extra finalists. Tiers whose plan budget fits the
    // clock, and deadlines that bind before the wall guard (Nozdormu), keep the old chain.
    internal const double ReplanShare = .6;
    internal const int ViableReplanNodes = 2400;
    private double WithReplanReserve(double nodes)
    {
        if (!WallGuarded || !profile.Fixes.On("replan-reserve")) return nodes;
        if (clockLimitedTurn == null) {
            // Hard only if it binds before the wall guard: every live NPC turn has the 75 s retail deadline, Nozdormu's 15 s binds.
            double deadline = turnDeadlineLeft?.Invoke() ?? double.PositiveInfinity;
            bool hard = !double.IsNaN(deadline) && deadline - DeadlineReserveMilliseconds < TurnWallBudgetMilliseconds - Math.Max(0, clock() - turnStarted - pausedMilliseconds);
            clockLimitedTurn = nodes < profile.Budget.PlanNodes && !hard;
            return nodes;
        }
        return clockLimitedTurn.Value ? Math.Max(nodes * ReplanShare, Math.Min(nodes, ViableReplanNodes)) : nodes;
    }
    // Search share of the think wall: this match's completed decisions plus the current one, else the frame split.
    public double SearchShare => Share(0, 0);
    private double Share(double spent, double wall)
    {
        double search = matchSearch + spent, total = matchThinkWall + wall;
        if (total >= 500 && search > 0) return Math.Min(1, Math.Max(.05, search / total));
        if (lastSlice > 0 && frameOther >= 0) return Math.Min(1, Math.Max(.05, lastSlice / (lastSlice + frameOther)));
        return DefaultSearchShare;
    }
    // Wall left before the guard: the profile's wall budget less this turn's unpaused wall, clamped to a finite turn
    // deadline less its reserve.
    private double WallLeft(double now)
    {
        double left = TurnWallBudgetMilliseconds - Math.Max(0, now - turnStarted - pausedMilliseconds);
        double deadline = turnDeadlineLeft?.Invoke() ?? double.PositiveInfinity;
        if (!double.IsNaN(deadline) && !double.IsInfinity(deadline)) left = Math.Min(left, deadline - DeadlineReserveMilliseconds);
        return left;
    }
    // The turn's clock allowance is used up: guarded profiles past the wall guard, others past the turn search budget.
    private bool TurnBudgetSpent(double now) => WallGuarded ? WallLeft(now) <= 0 : SearchMilliseconds >= TurnSearchBudgetMilliseconds;
    // Search time this decision may still use before the guard (spent: this decision's search so far).
    private double WallSearchLeft(double now, double spent) =>
        (WallLeft(now) - WallMarginMilliseconds) * Share(spent, Math.Max(0, now - thinkStarted));
    private void LogTurnSummary()
    {
        log?.Invoke(string.Format(CultureInfo.InvariantCulture,
            "npc={0} turn-summary turn={1} actions={2} wallMs={3:F0} searchMs={4:F0} pauseMs={5:F0} idleWaits={6} idleTimeouts={7} idleWaitMs={8:F0}"
            + " freezeMs={9:F0} nodes={10} decisions={11} clockCut={12} clockCuts={13} cappedDecisions={14} nodeRate={15:F2} thinkWallMs={16:F0}"
            + " searchFrames={17} msPerFrame={18:F1} frameOtherMs={19:F1} pausedMs={20:F0} searchShare={21:F2} gcBytes={22} gcCollections={23} gcFullCollections={24}",
            profile.Id, current?.Turn ?? -1, actions, Math.Max(0, clock() - turnStarted), SearchMilliseconds, ArtificialDelayMilliseconds,
            IdleWaits, IdleTimeouts, IdleWaitedMilliseconds, FreezeSearchMilliseconds, TurnSearchNodes, Decisions, ClockCut ? 1 : 0,
            ClockCuts, CappedDecisions, nodeRate, ThinkWallMilliseconds,
            SearchFrames, SearchFrames == 0 ? 0 : SearchMilliseconds / SearchFrames, frameOther, pausedMilliseconds, SearchShare,
            GC.GetTotalMemory(false), GC.CollectionCount(0) - gcTurnStart, GC.CollectionCount(GC.MaxGeneration) - gcFullTurnStart));
    }
    // Reserve belongs inside the one decision budget and is shared with the background deadline.
    private double SearchWindow(bool reserveFallback) => decisionLimit
        - (WallGuarded && reserveFallback && fallbackNodes > 0 ? Math.Min(100, decisionLimit / 3) : 0);
    private void ChargeLatePool()
    {
        if (!decisionExtended) return;
        decisionPoolUsed = Math.Min(decisionPoolStart, Math.Max(0, decisionSearch - decisionPoolNominal));
        latePoolSpent += decisionPoolUsed; LateExtendedDecisions++; decisionExtended = false;
    }
    private static bool UsesLatePool => GoldenAblation.LateLethal == GoldenAblation.LateLethalTiming.Shipped
        || GoldenAblation.LateLethal == GoldenAblation.LateLethalTiming.PoolOnly;
    private bool LethalRunning => lethalScheduler != null && (job as INpcLethalSearch)?.LethalSearch is INodeJob l && !l.IsDone
        || (job as INpcBackgroundSearch)?.BackgroundRunning == true;
    // Search charged for the slice that began at start.
    private double Charged(double start)
    {
        double elapsed = Math.Max(0, clock() - start);
        if ((job as INpcBackgroundSearch)?.BackgroundClock != true) return elapsed;
        // Workers keep searching between frames: charge that wall once (not per worker), and only while they still run;
        // after they finish only the owner's slices count, as on the serial path.
        if (GoldenAblation.LateLethal == GoldenAblation.LateLethalTiming.Shipped) return elapsed + sliceGap;
        return Math.Max(elapsed, Math.Max(0, clock() - thinkStarted - decisionSearch));
    }
    // This decision's nominal limit: the late-decision slice past the guard, else its window (fallback reserve inside).
    private double NominalWindow(bool reserveFallback)
    {
        double window = SearchWindow(reserveFallback);
        return WallLeft(clock()) <= 0 ? Math.Min(LateDecisionMilliseconds, window) : window;
    }
    // Extension this decision may still add from the turn's late-lethal pool; 0 unless a plausible lethal search is
    // unfinished past the wall guard, with the hard deadline's reserve still ahead and not the binding guard.
    private double LatePoolLeft()
    {
        if (decisionPoolStart <= 0 || !decisionPlausible || !LethalRunning || WallLeft(clock()) > 0) return 0;
        double deadline = turnDeadlineLeft?.Invoke() ?? double.PositiveInfinity;
        if (!double.IsNaN(deadline) && !double.IsInfinity(deadline))
        {
            double hard = deadline - DeadlineReserveMilliseconds;
            if (hard <= 0 || hard < TurnWallBudgetMilliseconds - Math.Max(0, clock() - turnStarted - pausedMilliseconds)) return 0;
        }
        return decisionPoolStart;
    }
    private double BackgroundWindow()
    {
        if (GoldenAblation.LateLethal != GoldenAblation.LateLethalTiming.DecisionFloor)
            return SearchWindow(true) + (WallGuarded ? LatePoolLeft() : 0);
        double deadline = turnDeadlineLeft?.Invoke() ?? double.PositiveInfinity;
        bool tight = !double.IsNaN(deadline) && !double.IsInfinity(deadline) && deadline <= DeadlineReserveMilliseconds + LateLethalMilliseconds;
        double window = WallGuarded ? Math.Max(decisionLimit, tight ? LateDecisionMilliseconds : LateLethalMilliseconds) : decisionLimit;
        if (WallGuarded && fallbackNodes > 0) window -= Math.Min(100, window / 3);
        return window;
    }
    private bool SearchLimitReached(double start, bool freeze, bool reserveFallback = true)
    {
        double spent = decisionSearch + Charged(start);
        if (!WallGuarded) return spent >= decisionLimit;
        if (GoldenAblation.LateLethal == GoldenAblation.LateLethalTiming.DecisionFloor)
        {
            double deadline = turnDeadlineLeft?.Invoke() ?? double.PositiveInfinity;
            bool tight = !double.IsNaN(deadline) && !double.IsInfinity(deadline) && deadline <= DeadlineReserveMilliseconds + LateLethalMilliseconds;
            double floor = LethalRunning && !tight ? LateLethalMilliseconds : LateDecisionMilliseconds;
            if (spent >= floor && WallLeft(clock()) <= 0) return true;
            double reserve = reserveFallback && fallbackNodes > 0 ? Math.Min(100, Math.Max(decisionLimit, floor) / 3) : 0;
            return spent >= Math.Max(decisionLimit, floor) - reserve;
        }
        double nominal = NominalWindow(reserveFallback);
        if (spent < nominal) return false;
        // A lethal proven during an extension completes as a lethal (bookkeeping, one step), not as a clock cut.
        if (decisionExtended && job != null && !job.IsDone && job.LethalResult == "found") return false;
        // A running lethal search cannot create its own extension; only the turn's late-lethal pool can.
        double pool = LatePoolLeft();
        if (pool <= 0 || spent >= nominal + pool) return true;
        decisionExtended = true; decisionPoolNominal = NominalWindow(true);
        return false;
    }
    private void MarkClockCut()
    {
        ClockCut = true;
        if (!decisionClockCut) { ClockCuts++; decisionClockCut = true; }
    }
    private void CutClock(Func<string, GoldenDecision> chooseFallback)
    {
        log?.Invoke(string.Format(CultureInfo.InvariantCulture, "npc={0} clock-cut=1 turn={1} decisionMs={2:F0} limit={3:F0} turnSearchMs={4:F0}",
            profile.Id, current?.Turn ?? -1, decisionSearch + Math.Max(0, clock() - sliceStart), decisionLimit, SearchMilliseconds));
        MarkClockCut();
        var continuation = job as INpcSearchContinuation;
        decision = job != null && job.IsDone ? job.Result
            : continuation?.StopSearch(SearchStop.Clock);
        if (decision == null && (job as INpcPassValidation)?.Validating != true) decision = chooseFallback("clock-cut");
        if (continuation != null && decision?.FallbackReason == "clock-cut") decision = chooseFallback("clock-cut");
        if (job is INpcBackgroundSearch background && background.BackgroundClock) lethalNodes = background.BackgroundNodes + (lethalScheduler?.Nodes ?? 0);
        lethal = job?.LethalResult ?? "incomplete"; reuse = job?.PlanReused ?? false;
    }
    private void Pause(bool preTurn)
    {
        var personality = profile.Personality;
        double low = preTurn ? personality.PreTurnMinSeconds : personality.ActionMinSeconds;
        double high = preTurn ? personality.PreTurnMaxSeconds : personality.ActionMaxSeconds;
        double roll = MatchRecorder.PersonalityRoll(profile.Version, matchId, publicEventIndex != null ? publicEventIndex() : ++pauseIndex, preTurn ? "pause-pre" : "pause-act");
        double duration = Math.Min(Math.Max(0, 4000 - ArtificialDelayMilliseconds), 1000 * (low + (high - low) * roll));
        ArtificialDelayMilliseconds += duration;
        until = clock() + duration;
        State = preTurn ? NpcTurnState.PreTurnPause : NpcTurnState.ActionPause;
    }
}
