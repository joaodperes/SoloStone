using System;
using System.Collections.Generic;
using System.Linq;

public static class NpcCatalog
{
    // Keep the 28000-node planning cap. K=8/R=160 needs ~4400 plan nodes to reply-score all three finalists,
    // including settlement. Round 2 moved 1000 lethal nodes per decision to the own beam at the same 7500 total
    // (lethal 500/decision, 1500/turn; plan 7000, own beam ~2600 nodes) and widened it 6 -> 8: +2.7 points pooled over
    // 225 paired games, Paladin +6.7, Shaman +4.0, Warrior -2.7 (n.s.), turn nodes p95 -12..20 %; every corpus lethal
    // still passes (docs/roadmap/2026-10-25-npc-strength-diagnosis.md, rounds 1 and 2).
    // 2026-10-31: width 16 restores Owl828's removal/discount order; corpus scores and paired pooled smoke preserved.
    // Live clock guards match skills-v1.json (owner 2026-11-01): node budget, not device speed, sets the search.
    private static readonly Budget GoldenBudget = new Budget(28000, 7000, 500, 8, 3, 160, 16, turnSearchSeconds: 45, turnWallSeconds: 55);
    private static readonly EmoteProfile ProEmotes = new EmoteProfile(3, 30,
        new EmoteRule(NpcReaction.MatchStart, "Greetings", .9),
        new EmoteRule(NpcReaction.HumanBigTurn, "Wow", .35),
        new EmoteRule(NpcReaction.BotDoomed, "WellPlayed", 1),
        new EmoteRule(NpcReaction.Concession, "WellPlayed", 1),
        new EmoteRule(NpcReaction.ReplyToHumanEmote, "Greetings", .8, humanEmote: "Greetings"),
        new EmoteRule(NpcReaction.ReplyToHumanEmote, "Thanks", .8, humanEmote: "WellPlayed"));
    private static readonly EmoteProfile VeteranEmotes = new EmoteProfile(4, 25,
        new EmoteRule(NpcReaction.MatchStart, "Greetings", 1),
        new EmoteRule(NpcReaction.HumanBigTurn, "WellPlayed", .6),
        new EmoteRule(NpcReaction.BotDoomed, "WellPlayed", 1),
        new EmoteRule(NpcReaction.Concession, "WellPlayed", 1),
        new EmoteRule(NpcReaction.ReplyToHumanEmote, "Greetings", 1, humanEmote: "Greetings"),
        new EmoteRule(NpcReaction.ReplyToHumanEmote, "Thanks", 1, humanEmote: "WellPlayed"),
        new EmoteRule(NpcReaction.ReplyToHumanEmote, "Thanks", 1, humanEmote: "Wow"),
        new EmoteRule(NpcReaction.ReplyToHumanEmote, "WellPlayed", .5, humanEmote: "Threaten"),
        new EmoteRule(NpcReaction.ReplyToHumanEmote, "Thanks", 1, humanEmote: "Oops"),
        new EmoteRule(NpcReaction.ReplyToHumanEmote, "Thanks", 1, humanEmote: "Sorry"));
    // Conditional fallbacks give Oops (.6 after .5) and sarcastic Wow (.75 after .6) a .3 overall chance before use caps.
    private static readonly EmoteProfile GamblerEmotes = new EmoteProfile(6, 15,
        new EmoteRule(NpcReaction.MatchStart, "Threaten", .6),
        new EmoteRule(NpcReaction.MatchStart, "Greetings", 1),
        new EmoteRule(NpcReaction.OwnBigTurn, "Wow", .7, 2),
        new EmoteRule(NpcReaction.OwnBigTurn, "Threaten", .5),
        new EmoteRule(NpcReaction.BotLethalFound, "Threaten", .8),
        new EmoteRule(NpcReaction.HumanBigTurn, "Wow", .5),
        new EmoteRule(NpcReaction.HumanBigTurn, "Oops", .6, mock: true),
        new EmoteRule(NpcReaction.BotDoomed, "WellPlayed", .5),
        new EmoteRule(NpcReaction.Concession, "WellPlayed", .5),
        new EmoteRule(NpcReaction.HumanMisplay, "Oops", .6, 2, mock: true),
        new EmoteRule(NpcReaction.HumanMisplay, "Wow", .75, 2, mock: true),
        new EmoteRule(NpcReaction.ReplyToHumanEmote, "Threaten", .5, humanEmote: "Greetings"),
        new EmoteRule(NpcReaction.ReplyToHumanEmote, "Greetings", 1, humanEmote: "Greetings"),
        new EmoteRule(NpcReaction.ReplyToHumanEmote, "Threaten", .8, humanEmote: "Threaten"),
        new EmoteRule(NpcReaction.ReplyToHumanEmote, "Thanks", .6, humanEmote: "WellPlayed"));
    public static readonly IReadOnlyList<NpcProfile> All = Array.AsReadOnly(new[] {
        new NpcProfile("npc.golden.1", 1, "Dragonyyyy", "npc-dragon-warrior", "HERO_01c", 0,
            new Personality(.15, .45, .1, .3, ProEmotes, .35, 1, 8), GoldenBudget, "dragon-warrior.v1", "CS2_102_H3"),
        new NpcProfile("npc.golden.2", 1, "FatherUther", "npc-pure-paladin", "HERO_04ay", 0,
            new Personality(.8, 1.4, .3, .5, VeteranEmotes, .55, .25, 10), GoldenBudget, "pure-paladin.v1", "HERO_04ayhp"),
        new NpcProfile("npc.golden.3", 1, "ZeeCashh", "npc-zee-shaman", "HERO_02c", 0,
            new Personality(.1, .3, .08, .2, GamblerEmotes, .15, .25, 12), GoldenBudget, "zee-shaman.v1", "CS2_049_H3")
    });
    // Seam S1 (NPC World v1): the active world encounter's profile wins; no hook, no match or a failing hook = golden
    // catalog as before. A hook failure must never break match start: it is reported (boot may log it) and ignored.
    public static Action<Exception> ProfileHookFailed { get; set; }
    public static NpcProfile ForDeck(string deckId)
    {
        NpcProfile world = null;
        var hook = NpcWorldHooks.ProfileForDeck;
        if (hook != null)
            try { world = hook(deckId); }
            catch (Exception error)
            {
                try { ProfileHookFailed?.Invoke(error); } catch (Exception) { }
                world = null;
            }
        return world ?? All.SingleOrDefault(profile => profile.DeckId == deckId);
    }
}
