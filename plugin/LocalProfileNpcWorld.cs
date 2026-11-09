// Plugin-only half of the WP5 persistence seam (spec S9). LocalProfile.cs and LocalProfileMilestones.cs are linked by
// test projects that do not compile plugin/Npc/World, so the NpcEncounter overload and the NpcWorldRuntime wiring live here.
public sealed partial class LocalProfile
{
    // Every lookup is lazy: before NpcWorldRuntime.Install (or after a failed boot) World is null and the seams answer
    // "nothing", which is the legacy behaviour. Exceptions are swallowed by the callers in LocalProfileMilestones.cs.
    static LocalProfile()
    {
        NpcLegendNumber = NpcWorldRuntime.LegendNumber;
        NpcLegendTopRating = matchIndex => NpcWorldRuntime.Legend?.FieldRating(1, matchIndex) ?? NpcPopulationRules.LegendCap;
        NpcChallengers = milestoneId => NpcWorldRuntime.World == null ? null : NpcWorldRuntime.ChallengersFor(milestoneId);
        NpcDisplayName = npcId => NpcWorldRuntime.World?.Find(npcId)?.DisplayName;
    }

    // Agreed with WP4 (LocalLadder.StartSelected): Begin(selected, e.Deck.Id, seed, e). Opponent stays the deck id.
    public LocalMatchRecord Begin(string deck, string opponent, int seed, NpcEncounter encounter) => encounter == null
        ? Begin(deck, opponent, seed)
        : Begin(deck, opponent, seed, encounter.Npc?.Id, encounter.Deck?.Id, encounter.Rating);

    // BeginLaunch (unstarted Pending cleared when the launch throws) with the same encounter mapping as Begin.
    public LocalMatchRecord BeginLaunch(string deck, string opponent, int seed, NpcEncounter encounter, System.Action<LocalMatchRecord> launch)
        => encounter == null
        ? BeginLaunch(deck, opponent, seed, launch)
        : BeginLaunch(deck, opponent, seed, encounter.Npc?.Id, encounter.Deck?.Id, encounter.Rating, launch);

    internal LocalMatchRecord BeginLaunch(string deck, string opponent, int seed, NpcEncounter encounter,
        LocalAchievementPendingContext achievementContext, System.Action<LocalMatchRecord> launch)
        => encounter == null
        ? BeginLaunch(deck, opponent, seed, launch)
        : BeginLaunch(deck, opponent, seed, encounter.Npc?.Id, encounter.Deck?.Id, encounter.Rating, achievementContext, launch);

    // mode: null = Ranked, CasualRules.Mode = Standard Casual (#37). A Casual match without a world encounter still
    // records its mode, so it never touches the ladder.
    internal LocalMatchRecord BeginLaunch(string deck, string opponent, int seed, NpcEncounter encounter,
        LocalAchievementPendingContext achievementContext, string mode, System.Action<LocalMatchRecord> launch)
        => BeginLaunch(deck, opponent, seed, encounter?.Npc?.Id, encounter?.Deck?.Id, encounter?.Rating, achievementContext, mode, launch);
}
