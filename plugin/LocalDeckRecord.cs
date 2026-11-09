using System.Collections.Generic;

// Per-deck lifetime record for the deck picker (owner 2026-11-05): wins and losses of one deck across Ranked and Casual, folded from the
// settled ledger (LocalProfileData.Matches), never stored. So there is no schema change and the count is exactly the settlements:
// a settlement is one ledger row (Settle is idempotent per match id and the abandon-on-restart commit clears Pending in the same step),
// which means no double count on restart replay, concede or Rematch (a rematch is its own match row).
// Counting, from the player's side, as for ladder stars and the NPC head-to-head (LocalNpcRecord): Won = W; Lost (including the
// player's concession) and Abandoned (a match the client died in) = L; Tied is kept apart and shown nowhere; unsettled rows and rows
// without a result are skipped. Debug scenario matches never reach the ledger. The key is the match's Deck ("custom:<id>" for a built
// deck, the loaner id for a starter), so the record survives rename and edit (same id) and a deleted deck's record goes with it:
// custom ids are never reused, so nothing can ever ask for that key again.
public sealed class LocalDeckRecord
{
    public static readonly LocalDeckRecord None = new LocalDeckRecord(0, 0, 0, 0, 0);
    public int RankedWins { get; }
    public int RankedLosses { get; }
    public int CasualWins { get; }
    public int CasualLosses { get; }
    public int Ties { get; }
    public int Wins => RankedWins + CasualWins;
    public int Losses => RankedLosses + CasualLosses;
    public int Games => Wins + Losses + Ties;
    public LocalDeckRecord(int rankedWins, int rankedLosses, int casualWins, int casualLosses, int ties)
    { RankedWins = rankedWins; RankedLosses = rankedLosses; CasualWins = casualWins; CasualLosses = casualLosses; Ties = ties; }

    // The "custom" deck key of the pre-multi-deck save belongs to the legacy custom deck id.
    private static readonly string LegacyKey = "custom:" + LocalProfile.LegacyCustomDeckId;

    // The ledger key of a deck: what a match on it settles under (Begin's deck argument). ONE function for the start path
    // (NativeUIProbe.StartPicked) and the picker line (NativeUIProbe.DeckKeyOf), so the line can never look up a key the start never wrote.
    public static string KeyFor(bool customDeck, long nativeDeckId, string loanerId) =>
        customDeck ? "custom:" + nativeDeckId.ToString(System.Globalization.CultureInfo.InvariantCulture) : loanerId;

    public static bool SameDeck(string matchDeck, string deckKey) =>
        matchDeck != null && deckKey != null && (matchDeck == deckKey || (matchDeck == "custom" && deckKey == LegacyKey));

    public static LocalDeckRecord Of(IEnumerable<LocalMatchRecord> settled, string deckKey)
    {
        if (string.IsNullOrEmpty(deckKey) || settled == null) return None;
        int rankedWins = 0, rankedLosses = 0, casualWins = 0, casualLosses = 0, ties = 0;
        foreach (var match in settled)
        {
            if (match == null || !SameDeck(match.Deck, deckKey)) continue;
            bool casual = CasualRules.IsCasual(match.Mode);
            switch (match.Result)
            {
                case "Won": if (casual) casualWins++; else rankedWins++; break;
                case "Lost": case "Abandoned": if (casual) casualLosses++; else rankedLosses++; break;
                case "Tied": ties++; break;
            }
        }
        return new LocalDeckRecord(rankedWins, rankedLosses, casualWins, casualLosses, ties);
    }
}

public sealed partial class LocalProfile
{
    // The selected deck's record; empty for an unknown key. Reads the in-memory ledger only (no I/O, never throws on a bad key).
    public LocalDeckRecord DeckRecord(string deckKey) => LocalDeckRecord.Of(data.Matches, deckKey);
}

// The picker's text (pure, no Unity): "12 W - 7 L". An unplayed deck reads "0 W - 0 L" (shown, not hidden: the label does not pop in
// and out as the player pages through decks, and 0-0 is true). No percentage, no streak.
internal static class DeckRecordText
{
    internal static string Format(LocalDeckRecord record) =>
        (record ?? LocalDeckRecord.None).Wins.ToString(System.Globalization.CultureInfo.InvariantCulture) + " W - "
        + (record ?? LocalDeckRecord.None).Losses.ToString(System.Globalization.CultureInfo.InvariantCulture) + " L";
}
