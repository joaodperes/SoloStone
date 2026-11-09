using System;

// Seam over the native managers (RewardXpNotificationManager PET bar, PetsManager scroll queue) so the step logic
// below is testable without Unity. NativePetMoment supplies the real implementation.
internal interface ILocalPetNative
{
    // Discards stale PET bar entries and queued (not showing) scrolls; also terminates a bar in flight.
    void ClearQueues();
    // Fires the one native PetsXpLevelChanged event. Throws when it cannot be delivered.
    void Notify(LocalPetXpEvent move);
    // Starts the PET bar; false when the event queued nothing to show.
    bool BeginBar(Action finished);
    bool BarReady { get; }
    // Native TerminateEarly. May invoke the bar's finished callback synchronously.
    void TerminateBar();
    // Shows the next queued scroll; false when none is left.
    bool ShowNextScroll(Action hidden);
    bool ScrollOnScreen { get; }
}

// Retail EndGameScreen.ShowPetXpGains + ShowPetRewards, driven from the settled receipt.
internal sealed class LocalPetMomentStep : ILocalPetStep
{
    private enum Stage { Idle, Bar, Scrolls }
    internal const float BarReadyTimeout = 20f;
    // A loaded bar that never finishes (cap, or the handler returning early) would otherwise wait on the player forever
    // behind a hidden prompt; after this long the bar is ended and the scrolls still show.
    internal const float BarStallTimeout = 180f;
    // The manager's wait loop resumes one frame after the widget is ready; terminating inside that frame would orphan it.
    internal const float SkipGrace = 0.4f;
    private readonly ILocalPetNative host;
    private readonly Func<float> clock;
    private readonly Action<string> info, warn;
    private Action completed, failed;
    private Stage stage;
    private int token, scrollsShown;
    private float began, readyAt;
    private bool scrollShown, fired;
    private string detail = "idle";

    internal LocalPetMomentStep(ILocalPetNative host, Func<float> clock, Action<string> info = null, Action<string> warn = null)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.info = info ?? (_ => { }); this.warn = warn ?? (_ => { });
    }

    internal bool BarStage => stage == Stage.Bar;
    internal string Diagnostics => "stage=" + stage + ";eventFired=" + fired + ";scrollsShown=" + scrollsShown
        + ";scrollVisible=" + (scrollShown && SafeScrollOnScreen()) + ";" + detail;
    internal bool Skippable => stage == Stage.Bar && readyAt > 0 && clock() - readyAt > SkipGrace;
    // A ready bar waits for the player too (at the cap it only finishes on a click), as does a visible scroll.
    public bool AwaitingPlayer => (stage == Stage.Bar && SafeBarReady()) || (stage == Stage.Scrolls && scrollShown && SafeScrollOnScreen());

    public void Present(LocalResultsReceipt receipt, Action done, Action failure)
    {
        if (done == null || failure == null) throw new ArgumentNullException(done == null ? nameof(done) : nameof(failure));
        Abort();
        int mine = ++token;
        completed = done; failed = failure; began = clock(); readyAt = 0; scrollShown = fired = false; scrollsShown = 0;
        try
        {
            if (!LocalPetXpEvent.TryFrom(receipt, out LocalPetXpEvent move)) { Finish(mine); return; }
            // Nothing queued earlier may mix into this match's moment.
            host.ClearQueues();
            host.Notify(move);
            fired = true;
            detail = "pet=" + move.PetId + ";level=" + move.PreviousLevel + ">" + move.NewLevel + ";xp=" + move.PreviousXp + ">" + move.NewXp;
            info("NATIVE_PET_MOMENT_START match=" + receipt.MatchId + ";" + detail);
            if (!host.BeginBar(() => Guard(mine, () => { if (stage == Stage.Bar) EnterScrolls(mine); }))) { EnterScrolls(mine); return; }
            stage = Stage.Bar;
        }
        catch (Exception error) { Fail(mine, error); }
    }

    internal void Update()
    {
        if (stage != Stage.Bar) return;
        int mine = token;
        Guard(mine, () => {
            if (readyAt == 0 && host.BarReady) readyAt = clock();
            bool loadTimeout = readyAt == 0 && clock() - began > BarReadyTimeout;
            bool stalled = readyAt != 0 && clock() - readyAt > BarStallTimeout;
            if (!loadTimeout && !stalled) return;
            // The XP is durable and the scrolls are the part that matters: end the bar and still show them.
            warn((stalled ? "NATIVE_PET_MOMENT_BAR_STALL " : "NATIVE_PET_MOMENT_BAR_TIMEOUT ") + detail);
            host.TerminateBar();
            EnterScrolls(mine);
        });
    }

    // Native second click while the bar animates: terminate it and carry on to the scrolls.
    public void ContinueClicked()
    {
        if (!Skippable) return;
        int mine = token;
        Guard(mine, () => {
            info("NATIVE_PET_MOMENT_SKIP");
            host.TerminateBar();   // may already have run the bar callback and entered the scrolls
            EnterScrolls(mine);
        });
    }

    public void Retire() => Abort();

    // Idempotent: the bar's finished callback and a skip click can both arrive for the same transition.
    private void EnterScrolls(int mine)
    {
        if (mine != token || completed == null || stage == Stage.Scrolls) return;
        stage = Stage.Scrolls;
        NextScroll(mine);
    }

    private void NextScroll(int mine)
    {
        if (mine != token) return;
        scrollShown = true; scrollsShown++;
        if (!host.ShowNextScroll(() => Guard(mine, () => { if (stage == Stage.Scrolls) { scrollShown = false; NextScroll(mine); } })))
        {
            scrollShown = false; scrollsShown--;
            Finish(mine);
        }
    }

    // Native callbacks outlive an aborted attempt; stale or throwing ones must neither advance nor wedge the flow.
    private void Guard(int mine, Action action)
    {
        if (mine != token || completed == null) return;
        try { action(); }
        catch (Exception error)
        {
            if (mine == token) Fail(mine, error);
            else warn("NATIVE_PET_MOMENT_ERROR " + error.GetType().Name + ": " + error.Message);
        }
    }

    private void Finish(int mine)
    {
        if (mine != token || completed == null) return;
        var callback = completed;
        info("NATIVE_PET_MOMENT_DONE " + detail + ";scrolls=" + scrollsShown);
        Reset();
        Continue(callback);
    }

    private void Fail(int mine, Exception error)
    {
        if (mine != token || failed == null) return;
        var callback = failed;
        warn("NATIVE_PET_MOMENT_FALLBACK " + error.GetType().Name + ": " + error.Message);
        Abort();
        Continue(callback);
    }

    // The flow continuation must never be swallowed silently (it moves tier/boxes along).
    private void Continue(Action callback)
    {
        try { callback(); }
        catch (Exception error) { warn("NATIVE_PET_MOMENT_CONTINUE " + error.GetType().Name + ": " + error.Message + "\n" + error.StackTrace); }
    }

    // A true failure or retire clears both native queues so nothing lingers for a hub popup or the next end screen.
    private void Abort()
    {
        bool active = stage != Stage.Idle || completed != null;
        Reset();
        if (!active) return;
        try { host.ClearQueues(); }
        catch (Exception error) { warn("NATIVE_PET_MOMENT_CLEANUP " + error.Message); }
    }

    private void Reset()
    {
        token++; completed = failed = null; stage = Stage.Idle; readyAt = 0; scrollShown = false;
    }

    private bool SafeBarReady() { try { return host.BarReady; } catch { return false; } }
    private bool SafeScrollOnScreen() { try { return host.ScrollOnScreen; } catch { return false; } }
}
