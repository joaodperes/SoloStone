using System;
using System.Collections.Generic;

// IGoldenPilot.Decide alone cannot yield. Live implementations additionally expose this contract.
// Begin must only construct a continuation; each StepNode does at most one expansion, including replies/lethal.
public interface IFrameSlicedGoldenPilot : IGoldenPilot
{
    IGoldenDecisionJob Begin(FairBotState view, GoldenContext context);
}

public interface IGoldenDecisionJob : INodeJob
{
    GoldenDecision Result { get; }
    string LethalResult { get; }
    bool PlanReused { get; }
}

public static class NpcPilots
{
    // Register once at startup. A new pilot is created per match, retaining plans between decisions.
    public static Func<NpcProfile, IGoldenPilot> Factory { get; set; }
    public static IGoldenPilot Create(NpcProfile profile, Action<string> log = null)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        var pilot = Factory?.Invoke(profile);
        // Any profile with a resolvable plan (golden, or a world deck's "auto:" plan) gets the golden brain.
        // ForId never throws (S2 guard); a null plan keeps the legacy search. A throwing decorator keeps the plain pilot.
        var plan = pilot == null ? DeckPlans.ForId(profile.PlanId) : null;
        if (pilot == null && plan != null) pilot = new GoldenPilot(profile, plan);
        if (pilot == null && profile.PlanId != null) log?.Invoke("npc=" + profile.Id + " plan " + profile.PlanId + " unresolved"
            + (DeckPlans.LastSeamError != null ? " (" + DeckPlans.LastSeamError + ")" : ""));
        var decorate = NpcWorldHooks.DecoratePilot;
        if (pilot != null && decorate != null)
        {
            try { pilot = decorate(profile, pilot) ?? pilot; }
            catch (Exception error)
            {
                DeckPlans.Fail("NpcPilots.DecoratePilot(" + profile.Id + ") failed: " + error.GetType().Name + ": " + error.Message);
                log?.Invoke("npc=" + profile.Id + " ERROR skill decorator failed; plain golden pilot: " + error.Message);
            }
        }
        if (pilot is IFrameSlicedGoldenPilot) return pilot;
        log?.Invoke("npc=" + profile.Id + " ERROR pilot is not frame-sliced; using legacy BotDriver search");
        return null;
    }
}
