using System;
using System.Collections.Generic;
using System.Linq;

// Rivals-lite (backlog #9). What an NPC "remembers" of the player at match start: the settled head-to-head (player's side),
// the last meeting and whether it is the player's rival. Presentation only: it reaches the native name plate and the
// NPC's emote choice, never the pilot, deck selection, matchmaking, draws, the engine config or hidden information.
public enum NpcLastMeeting { PlayerWon, PlayerLost, Tied }

public sealed class NpcMemory
{
    public const int RespectGames = 5;
    public int Wins { get; }
    public int Losses { get; }
    public int Ties { get; }
    public int Games => Wins + Losses + Ties;
    public NpcLastMeeting LastMeeting { get; }
    public bool Rival { get; }
    public NpcMemory(int wins, int losses, int ties, NpcLastMeeting lastMeeting, bool rival)
    {
        if (wins < 0 || losses < 0 || ties < 0 || wins + losses + ties == 0)
            throw new ArgumentException("A memory needs at least one settled meeting.");
        Wins = wins; Losses = losses; Ties = ties; LastMeeting = lastMeeting; Rival = rival;
    }
    // Long, even history: at least RespectGames meetings and within one game of each other.
    public bool Respect => Games >= RespectGames && Math.Abs(Wins - Losses) <= 1;
}

public static class NpcMemoryText
{
    // Glyphs live here only: an ASCII fallback (" - ", "-") is a one-line change if the native font lacks them.
    public const string Separator = " · ";
    public const string Dash = "–";

    // First meeting: plain name. Repeat: "Name  W–L" ("–T" only with ties). Rival: "Name · Rival W–L".
    public static string Plate(string name, NpcMemory memory)
    {
        if (string.IsNullOrEmpty(name) || memory == null) return name;
        string record = memory.Wins + Dash + memory.Losses + (memory.Ties > 0 ? Dash + memory.Ties : "");
        return memory.Rival ? name + Separator + "Rival " + record : name + "  " + record;
    }
}

// Emote choice by memory. Uses only emotes the profile's own rules already send (distinct rule.Emote); no randomness.
// Warm archetypes never carry Threaten or mock rules (PersonalityArchetype enforces it), so "non-warm with Threaten or
// a mock rule" is read from the rules alone and works for hand-built (golden) profiles that have no Warm flag.
public static class NpcMemoryEmotes
{
    private static HashSet<string> Set(EmoteProfile emotes) => new HashSet<string>(emotes.Rules.Select(rule => rule.Emote), StringComparer.Ordinal);
    private static bool CanMock(EmoteProfile emotes) => emotes.Rules.Any(rule => rule.Mock);
    private static bool Greets(EmoteProfile emotes) => emotes.Rules.Any(rule => rule.Trigger == NpcReaction.MatchStart);

    // The repeat-meeting greeting, or null: first meeting, no MatchStart rules (silent types stay silent) or nothing fits.
    // Priority: rival > respect > last result. NpcPresentation sends it in place of the MatchStart rule that fires.
    public static string Greeting(EmoteProfile emotes, NpcMemory memory)
    {
        if (emotes == null || memory == null || !Greets(emotes)) return null;
        var set = Set(emotes);
        bool mock = CanMock(emotes);
        string Pick(params string[] order) => order.FirstOrDefault(set.Contains);
        if (memory.Rival)
            return set.Contains("Threaten") ? "Threaten" : mock && set.Contains("Wow") ? "Wow" : Pick("Greetings");
        if (memory.Respect && set.Contains("WellPlayed")) return "WellPlayed";
        switch (memory.LastMeeting)
        {
            case NpcLastMeeting.PlayerWon: return Pick("Threaten", "Greetings");    // grudge
            case NpcLastMeeting.PlayerLost: return mock ? Pick("Thanks", "Wow", "Greetings") : Pick("Greetings");  // smug
            default: return Pick("Greetings");
        }
    }

    // The farewell when the NPC concedes to a rival player, in send order; empty = the profile's normal rules (no rival,
    // or no MatchStart rules). BotDoomed is not a loss (the NPC can still win) and always keeps the normal rules.
    public static IReadOnlyList<string> BeatRival(EmoteProfile emotes, NpcMemory memory)
    {
        if (emotes == null || memory == null || !memory.Rival || !Greets(emotes)) return new string[0];
        var set = Set(emotes);
        var order = set.Contains("Threaten") || CanMock(emotes) ? new[] { "Wow", "WellPlayed" } : new[] { "WellPlayed" };
        return order.Where(set.Contains).ToArray();
    }
}
