using System;
using System.Collections.Generic;

// Presentation only: premium finishes on an NPC's deck copies (owner, 2026-10-28). Values are native PREMIUM:
// 0 Normal, 1 Golden, 2 Diamond, 3 Signature. Each copy has fixed rolls from (NPC, deck, card, copy), so the same
// NPC and deck always look the same, and a higher displayed rank only adds premium copies or upgrades them.
public static class NpcPremiums
{
    private const long Salt = 0x50524D; // "PRM"
    private const int ShareStream = 1, UpgradeStream = 2;

    // Share of premium copies at the tier midpoints (Bronze..Diamond), Diamond 1, and Legend.
    private static readonly int[] AnchorIndex = { 0, 15, 45, 75, 105, 135, 149, LadderRules.LegendIndex };
    private static readonly double[] AnchorShare = { 0, 0.05, 0.10, 0.20, 0.35, 0.55, 0.70, 0.95 };
    private const double MaxUpgradeBelowLegend = 0.5;

    // Collectors show off, budget players barely craft; unknown or missing personalities are neutral.
    private static readonly Dictionary<string, double> Flair = new Dictionary<string, double>(StringComparer.Ordinal) {
        ["showboat"] = 1.6, ["pro"] = 1.3, ["veteran"] = 1.2, ["chaotic"] = 1.1,
        ["grinder"] = 0.8, ["speedster"] = 0.8, ["friendly"] = 0.5
    };

    // The rank an NPC's finishes follow: its medal's rank in Ranked. Casual shows no medal and its rating is hidden,
    // so there they follow the player's visible ladder rank (owner 2026-10-28) and never reveal the Casual rating.
    public static int SeatStarIndex(string mode, double npcRating, int playerStarIndex) =>
        CasualRules.IsCasual(mode) ? playerStarIndex : LadderRules.IndexForRating(npcRating);

    public static double FlairFor(string personalityId) =>
        personalityId != null && Flair.TryGetValue(personalityId, out double flair) ? flair : 1;

    // Chance that a copy is premium. Flair applies below Legend only and never reaches Legend's share,
    // so Legend always looks like an endgame collection and the share never falls with rank.
    public static double Share(int starIndex, double flair = 1)
    {
        int index = Math.Max(0, Math.Min(LadderRules.LegendIndex, starIndex));
        if (index == LadderRules.LegendIndex) return AnchorShare[AnchorShare.Length - 1];
        int segment = 1;
        while (AnchorIndex[segment] < index) segment++;
        double t = (double)(index - AnchorIndex[segment - 1]) / (AnchorIndex[segment] - AnchorIndex[segment - 1]);
        double share = AnchorShare[segment - 1] + t * (AnchorShare[segment] - AnchorShare[segment - 1]);
        return Math.Min(share * Math.Max(0, flair), AnchorShare[AnchorShare.Length - 2]);
    }

    // Chance that a premium copy below Legend shows the card's best finish instead of Golden; Legend always does.
    public static double Upgrade(int starIndex)
    {
        int index = Math.Max(0, Math.Min(LadderRules.LegendIndex, starIndex));
        return index == LadderRules.LegendIndex ? 1 : MaxUpgradeBelowLegend * index / (LadderRules.LegendIndex - 1);
    }

    // Per-copy premium in deck order. bestPremium(cardId) is the highest finish the card has (2 Diamond, 3 Signature,
    // else 1 Golden). A missing NPC id or deck id shows every copy Normal.
    public static int[] Assign(string npcId, string deckId, int starIndex, string personalityId,
        IReadOnlyList<string> cardIds, Func<string, int> bestPremium)
    {
        if (cardIds == null) throw new ArgumentNullException(nameof(cardIds));
        if (bestPremium == null) throw new ArgumentNullException(nameof(bestPremium));
        var premiums = new int[cardIds.Count];
        if (string.IsNullOrEmpty(npcId) || string.IsNullOrEmpty(deckId)) return premiums;
        double share = Share(starIndex, FlairFor(personalityId)), upgrade = Upgrade(starIndex);
        int npc = SkilledPilot.StableHashOf(npcId), deck = SkilledPilot.StableHashOf(deckId);
        var copies = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < cardIds.Count; i++)
        {
            string id = cardIds[i];
            if (string.IsNullOrEmpty(id)) continue;
            copies.TryGetValue(id, out int copy);
            copies[id] = copy + 1;
            int card = SkilledPilot.StableHashOf(id);
            if (Roll(npc, deck, card, copy, ShareStream) >= share) continue;
            int best = bestPremium(id);
            if (best != 2 && best != 3) best = 1;
            premiums[i] = Roll(npc, deck, card, copy, UpgradeStream) < upgrade ? best : 1;
        }
        return premiums;
    }

    private static double Roll(int npc, int deck, int card, int copy, int stream) =>
        unchecked((uint)StableSeed.Derive(Salt, npc, deck, card, copy, stream)) / 4294967296.0;
}

// The NPC seat a native match shows premium finishes for: its world id, deck and displayed ladder position.
public sealed class NpcPremiumSeat
{
    public string NpcId { get; }
    public string DeckId { get; }
    public int StarIndex { get; }
    public string PersonalityId { get; }
    public NpcPremiumSeat(string npcId, string deckId, int starIndex, string personalityId)
    { NpcId = npcId; DeckId = deckId; StarIndex = starIndex; PersonalityId = personalityId; }
}
