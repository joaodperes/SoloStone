using System;
using System.Collections.Generic;

// Live 2026-10-25: a SkilledPilot that cannot run (NotImplementedException from a stub, or any failure on its first
// use) used to send every decision to the runner's fallback, i.e. the legacy LocalBot. This guard keeps the NPC on its
// undecorated GoldenPilot instead: it logs once, then serves every later call from the plain pilot. A decision whose
// skilled job breaks mid-search restarts on the plain pilot with the same view and context.
public sealed class GuardedSkilledPilot : IFrameSlicedGoldenPilot, IGoldenPilotHost
{
    private readonly string npcId;
    private readonly IFrameSlicedGoldenPilot plain;
    private readonly Action<string> log;
    private bool used;
    public IFrameSlicedGoldenPilot Skilled { get; }
    public IFrameSlicedGoldenPilot Plain => plain;
    public bool Disabled { get; private set; }
    public string DisabledReason { get; private set; }

    public GuardedSkilledPilot(string npcId, IFrameSlicedGoldenPilot skilled, IFrameSlicedGoldenPilot plain, Action<string> log = null)
    {
        this.npcId = npcId ?? "";
        Skilled = skilled ?? throw new ArgumentNullException(nameof(skilled));
        this.plain = plain ?? throw new ArgumentNullException(nameof(plain));
        this.log = log;
    }

    public DriverOptions Options => (Active as IGoldenPilotHost)?.Options ?? (plain as IGoldenPilotHost)?.Options;
    public GoldenPublicInputs PublicInputs => (Active as IGoldenPilotHost)?.PublicInputs ?? (plain as IGoldenPilotHost)?.PublicInputs;
    private IFrameSlicedGoldenPilot Active => Disabled ? plain : Skilled;

    // NotImplementedException always disables (a stub never starts working); any other exception disables only on the
    // first use, when the decorator has never produced a result. Later failures propagate to the runner as before.
    internal bool ShouldDisable(Exception error) => !Disabled && (error is NotImplementedException || !used);

    internal void Disable(Exception error)
    {
        if (Disabled) return;
        Disabled = true;
        DisabledReason = error.GetType().Name + ": " + error.Message;
        try { log?.Invoke("npc=" + npcId + " skill decorator failed; plain golden pilot for the rest of the match: " + DisabledReason); }
        catch (Exception) { }
    }

    public IGoldenDecisionJob Begin(FairBotState view, GoldenContext context)
    {
        if (Disabled) return plain.Begin(view, context);
        IGoldenDecisionJob job;
        try { job = Skilled.Begin(view, context); }
        catch (Exception error)
        {
            if (!ShouldDisable(error)) throw;
            Disable(error);
            return plain.Begin(view, context);
        }
        return new GuardedJob(this, job, view, context);
    }

    public GoldenDecision Decide(FairBotState view, GoldenContext ctx)
    {
        if (Disabled) return plain.Decide(view, ctx);
        try { var result = Skilled.Decide(view, ctx); used = true; return result; }
        catch (Exception error)
        {
            if (!ShouldDisable(error)) throw;
            Disable(error);
            return plain.Decide(view, ctx);
        }
    }

    public IReadOnlyList<int> Mulligan(IGoldenMulliganView view, GoldenContext ctx)
    {
        if (Disabled) return plain.Mulligan(view, ctx);
        try { return Skilled.Mulligan(view, ctx); }
        catch (Exception error)
        {
            if (!ShouldDisable(error)) throw;
            Disable(error);
            return plain.Mulligan(view, ctx);
        }
    }

    internal void MarkUsed() { used = true; }

    // Forwards to the skilled job until it throws a disabling exception, then to a fresh plain job for the same decision.
    // The runner re-enqueues this forwarder when a replacement revives a completed lethal search.
    private sealed class GuardedJob : IGoldenDecisionJob, INpcThinkingCandidates, INpcLethalSearch, INpcSearchContinuation, INpcPassValidation, INpcPlanNodeCap,
        INpcBackgroundSearch, INpcLethalBound
    {
        private readonly GuardedSkilledPilot owner;
        private readonly FairBotState view;
        private readonly GoldenContext context;
        private IGoldenDecisionJob current;
        private readonly LethalForwarder lethal;

        internal GuardedJob(GuardedSkilledPilot owner, IGoldenDecisionJob job, FairBotState view, GoldenContext context)
        { this.owner = owner; current = job; this.view = view; this.context = context; lethal = new LethalForwarder(this); }

        private T Guard<T>(Func<IGoldenDecisionJob, T> read)
        {
            try { return read(current); }
            catch (Exception error)
            {
                if (!Recover(error)) throw;
                return read(current);
            }
        }
        // True when the failure came from the skilled job and the decision now runs on the plain pilot.
        private bool Recover(Exception error)
        {
            if (IsPlain) return false;
            if (owner.Disabled) { if (!(error is NotImplementedException)) return false; }
            else if (owner.ShouldDisable(error)) owner.Disable(error);
            else return false;
            // Join the failed job's lethal workers before replacing it.
            try { (current as INpcBackgroundSearch)?.StopBackground(); } catch (Exception) { }
            int spent = 0;
            try { spent = (current as INpcBackgroundSearch)?.BackgroundNodes ?? 0; } catch (Exception) { }
            // Commit the replacement and the old job's worker cost together: a throwing Begin keeps neither.
            var next = owner.plain.Begin(view, context);
            previousBackgroundNodes += spent;
            current = next;
            IsPlain = true;
            if (planCap != null) (current as INpcPlanNodeCap)?.CapPlanNodes(planCap);
            deadline.Apply(current);
            return true;
        }
        private bool IsPlain { get; set; }
        private Func<int> planCap;
        // Live clock budget: forwarded to the current job and to a plain restart.
        public void CapPlanNodes(Func<int> cap)
        {
            planCap = cap;
            try { (current as INpcPlanNodeCap)?.CapPlanNodes(cap); }
            catch (Exception error) { if (!Recover(error)) throw; }
        }

        private GoldenDecision done;
        private bool finished;
        // A finished job's result is read here, so a failure while producing it restarts the decision on the plain
        // pilot (which then reports not done) instead of surfacing as an unfinished result.
        public bool IsDone
        {
            get
            {
                if (finished) return true;
                if (!Guard(j => j.IsDone)) return false;
                try { done = current.Result; }
                catch (Exception error)
                {
                    if (!Recover(error)) throw;
                    return false;
                }
                if (!IsPlain) owner.MarkUsed();
                finished = true;
                return true;
            }
        }
        public void StepNode()
        {
            try { current.StepNode(); }
            catch (Exception error)
            {
                if (!Recover(error)) throw;
                current.StepNode();
            }
        }
        public GoldenDecision Result => IsDone ? done : Guard(j => j.Result);
        public string LethalResult => Guard(j => j.LethalResult);
        public bool PlanReused => Guard(j => j.PlanReused);
        public IReadOnlyList<BotAction> Candidates =>
            Guard(j => (j as INpcThinkingCandidates)?.Candidates) ?? new BotAction[0];
        public INodeJob LethalSearch => lethal;
        // Deck parallel lethal, forwarded to the current job (see SkilledPilot.SkilledJob); a plain restart inherits
        // what is left of the runner's worker deadline.
        private readonly BackgroundDeadline deadline = new BackgroundDeadline();
        private int previousBackgroundNodes;
        public int BackgroundNodes {
            get {
                int nodes = Guard(j => (j as INpcBackgroundSearch)?.BackgroundNodes ?? 0);
                return previousBackgroundNodes + nodes;
            }
        }
        public bool Waiting => !finished && Guard(j => (j as INpcBackgroundSearch)?.Waiting == true);
        public bool BackgroundClock => Guard(j => (j as INpcBackgroundSearch)?.BackgroundClock == true);
        public bool BackgroundRunning => Guard(j => (j as INpcBackgroundSearch)?.BackgroundRunning == true);
        public bool LethalPlausible => Guard(j => (j as INpcLethalBound)?.LethalPlausible == true);
        public void LimitBackground(double milliseconds)
        {
            deadline.Limit(milliseconds);
            try { (current as INpcBackgroundSearch)?.LimitBackground(milliseconds); }
            catch (Exception error) { if (!Recover(error)) throw; }
        }
        public void PollBackground()
        {
            try { (current as INpcBackgroundSearch)?.PollBackground(); }
            catch (Exception error) { if (!Recover(error)) throw; }
        }
        public void StopBackground() { (current as INpcBackgroundSearch)?.StopBackground(); }
        public bool Validating => !finished && Guard(j => (j as INpcPassValidation)?.Validating == true);
        public GoldenDecision StopValidation() => Guard(j => (j as INpcPassValidation)?.StopValidation());
        public GoldenDecision StopSearch(SearchStop stop) => Guard(j => (j as INpcSearchContinuation)?.StopSearch(stop));

        private sealed class LethalForwarder : INodeJob
        {
            private readonly GuardedJob job;
            internal LethalForwarder(GuardedJob job) { this.job = job; }
            public bool IsDone => job.Guard(j => (j as INpcLethalSearch)?.LethalSearch?.IsDone ?? true);
            public void StepNode()
            {
                try { (job.current as INpcLethalSearch)?.LethalSearch?.StepNode(); }
                catch (Exception error)
                {
                    if (!job.Recover(error)) throw;
                }
            }
        }
    }
}
