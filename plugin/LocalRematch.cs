using System;
using System.Collections.Generic;
using System.Linq;

// #28 Rematch (owner 2026-10-28). Pure (no Unity): LocalLadder starts from it, the deck picker button asks it whether to show,
// and tests drive it directly. The offer is derived from the newest settled match, never from a timer (no-FOMO): it survives
// restarts and ends only when another match is played. Only losses and ties qualify (owner 2026-10-31).
// LadderMatchmaker.Rematch owns the fairness gate.
internal static class LocalRematch
{
    // #28 x #37: Rematch is Ranked-only. A Casual match (or the picker in Casual) has no rematch offer: the fairness gate is the
    // ladder's (LadderMatchmaker.Rematch), Casual decks are not ladder decks, and Casual keeps its own queue and history.
    internal const string CasualRefusal = "rematch is Ranked-only";

    // The matchmaker request for the player's next ladder match: ladder state and match history only (seam S7).
    internal static MatchRequest Request(LocalProfile profile, IReadOnlyList<LocalMatchRecord> matches, NpcDeckLibrary decks,
        int depth, NpcLegendField legend)
    {
        var ladder = profile.Ladder;
        int? legendNumber = ladder.LegendReached && legend != null ? legend.LegendNumber(ladder.Rating, matches.Count) : (int?)null;
        return new MatchRequest(profile.NpcProfileSeed, matches.Count, ladder.StarIndex, ladder.Rating, legendNumber,
            matches.Skip(Math.Max(0, matches.Count - depth)).Select(match => Past(match, decks)));
    }

    internal static PastEncounter Past(LocalMatchRecord match, NpcDeckLibrary decks) => new PastEncounter(
        match.NpcId ?? "deck:" + match.Opponent, match.Opponent, decks?.Find(match.Opponent)?.Tags.Archetype, match.Result);

    // The last match's NPC and deck again, for the deck the player used in it. Null with a refusal when there is no offer.
    internal static NpcEncounter Encounter(LocalProfile profile, LadderMatchmaker matchmaker, string selected, out string refusal)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        refusal = null;
        if (matchmaker == null) refusal = "no NPC world";
        else if (profile.Pending != null) refusal = "a match is pending";
        else if (string.IsNullOrEmpty(selected) || selected.StartsWith("test-", StringComparison.Ordinal)) refusal = "not a ladder deck";
        if (refusal != null) return null;
        var all = profile.History;
        LocalMatchRecord last = all.Count == 0 ? null : all[all.Count - 1];
        if (last != null && CasualRules.IsCasual(last.Mode)) refusal = CasualRefusal + " (last match was Casual)";
        else if (last?.NpcId == null || (last.NpcDeckId != null && last.NpcDeckId != last.Opponent)) refusal = "last opponent is not a world NPC";
        // Interrupted matches already count as losses for ladder and head-to-head; keep that treatment here.
        else if (last.Result != "Lost" && last.Result != "Tied" && last.Result != "Abandoned") refusal = "last match was not a loss or tie";
        else if (last.Deck != selected) refusal = "a different deck is selected";
        else refusal = profile.PlayedDeckChange(last);
        if (refusal != null) return null;
        // The ladder queue's history: Ranked only, like TryWorldEncounter (same match index and drift epoch). The last match is
        // Ranked (refused above otherwise), so it is also the newest row here.
        var matches = profile.RankedHistory;
        // The series count reads this history: never fewer rows than the cap, whatever the queue windows are tuned to.
        int depth = Math.Max(matchmaker.Rules.HistoryDepth, LadderMatchmaker.MaxRematchSeries);
        var request = Request(profile, matches, matchmaker.World.Catalogs.Decks, depth, matchmaker.Legend);
        return matchmaker.Rematch(request, last.NpcId, last.Opponent, out refusal);
    }
}

internal enum LocalRematchStep { Idle, Wait, Start, Drop }

// A REMATCH pressed on the results screen (reward boxes or the no-reward card): the results flow acknowledges and routes to the
// deck picker as usual, then this starts the rematch there once the picker is ready, through the same validated start as the
// picker's own button. Any refusal, another match, or a picker that never gets ready drops it: the player is then simply on
// the picker. Seconds are the caller's clock.
internal sealed class LocalRematchRequest
{
    internal const float TimeoutSeconds = 45f, SettleSeconds = 0.5f;
    private float requested, eligibleSince = -1;
    private bool reached;
    internal string Match { get; private set; }

    internal void Request(string match, float now)
    {
        if (string.IsNullOrEmpty(match)) throw new ArgumentException("Settled match required.", nameof(match));
        Match = match; requested = now; eligibleSince = -1; reached = false;
    }
    internal void Clear() { Match = null; eligibleSince = -1; reached = false; }

    // lastMatch: the newest settled match. onPicker: the picker scene is up (possibly still busy). ready: on the picker and
    // free to start. refusal: why the picker offer was refused while ready, null when it stands; judged only once the
    // picker has settled, so a tray still loading its selection does not drop the request. Leaving the picker the route
    // reached drops it: a later visit the player opens is never hijacked.
    internal LocalRematchStep Next(float now, string lastMatch, bool onPicker, bool ready, string refusal, out string why)
    {
        why = null;
        if (Match == null) return LocalRematchStep.Idle;
        if (lastMatch != Match) why = "another match was played";
        else if (now - requested > TimeoutSeconds) why = "picker not ready in time";
        else if (!onPicker && reached) why = "picker left";
        else if (!ready) { reached |= onPicker; eligibleSince = -1; return LocalRematchStep.Wait; }
        else
        {
            reached = true;
            if (eligibleSince < 0) eligibleSince = now;
            if (now - eligibleSince < SettleSeconds) return LocalRematchStep.Wait;
            why = refusal;
        }
        Clear();
        return why == null ? LocalRematchStep.Start : LocalRematchStep.Drop;
    }
}

public sealed partial class LocalProfile
{
    // Matchmaker seed root: PackSeed through a distinct derive tag (spec: ProfileSeed = Derive(PackSeed, 0x4E5043)), so no new save field.
    internal long NpcProfileSeed => StableSeed.Derive(data.PackSeed, 0x4E5043);
    // The deck of the newest settled match, without History's deep copy (the picker asks every frame).
    internal string LastMatchDeck => data.Matches.Count == 0 ? null : data.Matches[data.Matches.Count - 1].Deck;
    internal string LastMatchId => data.Matches.Count == 0 ? null : data.Matches[data.Matches.Count - 1].Id;

    // A personal deck keeps its custom: id across edits, so a rematch also needs its cards, sideboards and hero to be the
    // ones the match recorded (its achievement context); editing it in between would counter-pick the NPC deck just seen.
    // Loaners and fixed decks cannot change in place. Null when unchanged, else the refusal.
    internal string PlayedDeckChange(LocalMatchRecord match)
    {
        if (match?.Deck == null || !match.Deck.StartsWith("custom:", StringComparison.Ordinal)) return null;
        var played = match.AchievementContext;
        if (played == null || !played.PersonalDeck) return "played deck not recorded";
        LocalCustomDeck saved = data.CustomDecks.SingleOrDefault(deck => "custom:" + deck.Id == match.Deck);
        if (saved == null) return "deck deleted";
        LocalDeck now = LedgerDeck(saved.Name, saved.Hero, saved.Cards, saved.Sideboards, false);
        bool same = now.Hero == played.StartingHero && SameCards(AchievementCards(now.CardIds), played.MainDeck)
            && SameCards(AchievementCards(now.Sideboards.SelectMany(row => row.Value)), played.Sideboards);
        return same ? null : "deck changed since the match";
    }

    private static bool SameCards(IReadOnlyList<LocalAchievementDeckCard> now, IReadOnlyList<LocalAchievementDeckCard> played)
    {
        Func<IReadOnlyList<LocalAchievementDeckCard>, IEnumerable<string>> rows = cards => (cards ?? new List<LocalAchievementDeckCard>())
            .GroupBy(card => LocalCardCatalog.CopyGroup(card.BaseCardId), StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Key + "\u001F" + group.Sum(card => card.Copies));
        return rows(now).SequenceEqual(rows(played), StringComparer.Ordinal);
    }
}
