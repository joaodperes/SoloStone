using System;

// WP3. Builds the frozen-W1b NpcProfile the existing pilot/presentation code consumes, from world data.
// NpcProfile.Id = identity id; DeckId = library deck id; PlanId = deck.EffectivePlanId; Budget = skill budget;
// Personality = archetype.ToPersonality(skill.ReplyBlendScale); HeroSkin/CardBack from the identity's cosmetics.
public static class NpcEncounterProfiles
{
    public static NpcProfile Build(NpcIdentity npc, NpcDeckEntry deck, PersonalityArchetype personality, SkillProfile skill)
    {
        if (npc == null) throw new ArgumentNullException(nameof(npc));
        if (deck == null) throw new ArgumentNullException(nameof(deck));
        if (personality == null) throw new ArgumentNullException(nameof(personality));
        if (skill == null) throw new ArgumentNullException(nameof(skill));
        // Skin for this deck's base hero only: a Warrior skin never lands on a Shaman deck. "" / missing = base hero.
        string skin, power = null;
        if (deck.Hero == null || !npc.HeroSkins.TryGetValue(deck.Hero, out skin) || string.IsNullOrEmpty(skin)) skin = null;
        if (skin != null && !npc.HeroSkinPowers.TryGetValue(skin, out power)) power = null;
        return new NpcProfile(npc.Id, npc.Version, npc.DisplayName, deck.Id, skin, npc.CardBack,
            personality.ToPersonality(skill.ReplyBlendScale), StyledBudget(skill, personality), deck.EffectivePlanId, power, new AiFixes().ForSoftmax(skill.Temperature));
    }
    public static NpcProfile WithReplyPrior(NpcProfile profile, ReplyPrior prior) =>
        profile.WithBudget(profile.Budget.WithBrain((profile.Budget.Brain ?? new BrainOptions()).WithReplyPrior(prior)));
    // The skill budget, with the personality's evaluation style multiplied onto the tier brain when the tier opts in.
    public static Budget StyledBudget(SkillProfile skill, PersonalityArchetype personality)
    {
        if (!skill.PersonalityStyle || personality.Style.Count == 0) return skill.Budget;
        return skill.Budget.WithBrain((skill.Budget.Brain ?? new BrainOptions()).WithScales(personality.Style));
    }
}

// WP4 output, consumed by WP3 (profile), WP5 (persistence) and WP6 (presentation). One per ladder match.
public sealed class NpcEncounter
{
    public NpcIdentity Npc { get; }
    public NpcDeckEntry Deck { get; }
    public SkillProfile Skill { get; }
    public PersonalityArchetype Personality { get; }
    public NpcProfile Profile { get; }          // NpcEncounterProfiles.Build(...)
    public double Rating { get; }               // passed to LocalProfile.Settle(opponentRating)
    public int MatchSeed { get; }               // mistake/emote stream root; also the engine seed source
    public string Reason { get; }               // "hidden-rating" | "fixed-field" | "legend-field" | "playtest" (logs, tests)
    public NpcEncounter(NpcIdentity npc, NpcDeckEntry deck, SkillProfile skill, PersonalityArchetype personality,
        NpcProfile profile, double rating, int matchSeed, string reason)
    { Npc = npc; Deck = deck; Skill = skill; Personality = personality; Profile = profile; Rating = rating;
      MatchSeed = matchSeed; Reason = reason; }
    // Same encounter, its NPC searching with this reply prior: identity, deck, skill, node budgets and clocks stay.
    // Live pilots run Profile.Budget (SkilledPilot's live path keeps the inner pilot's budget), so Skill stays as loaded.
    public NpcEncounter WithReplyPrior(ReplyPrior prior) => new NpcEncounter(Npc, Deck, Skill, Personality,
        NpcEncounterProfiles.WithReplyPrior(Profile, prior), Rating, MatchSeed, Reason);
}
