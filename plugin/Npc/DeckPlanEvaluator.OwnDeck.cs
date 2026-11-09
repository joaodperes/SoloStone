using System;

public sealed partial class DeckPlanEvaluator
{
    // own-deck-value (AiFixes): set from the profile by the searches that own this evaluator; default OFF = base.
    internal bool OwnDeckValue { get; set; }
    // A hand card is .63 of the same body on board (8-cost 8/8: 14.8 against 23.6 at golden weights), so a stat bonus on a card still to be drawn is too.
    private const double HeldStatShare = .6;
    // Own draws left, from public turn only. 1,499 plan-ab games (2026-11-05) end at turn 17 median, 21 at the 75th percentile;
    // the floor keeps a late cast priced, the cap stops an early one from counting a whole game of draws.
    internal static double DrawHorizon(int turn) => Math.Max(2, Math.Min(6, (21 - turn) / 2.0));
    // Cards this search put into a player's deck, or improved there. Any other draw is the generic unknown card, the same 5.5
    // Hand() gives a hidden one, so an added card pays only what it adds over that, by its chance of being drawn in time; a filler
    // dilutes and a Blight costs. Both sides: a minion shuffled back into its owner's deck is delayed, not removed.
    // `owner` is valued in its owner's own terms, so the caller subtracts the enemy's; RecurringPrice takes the bot frame.
    internal double DeckTerm(FairPlayer owner, bool ownerIsBot, int turn, long life, long otherLife)
    {
        if (!OwnDeckValue || owner.DeckCount == 0 || owner.DeckAdded.Count + owner.DeckUpgraded.Count == 0) return 0;
        double generic = 5.5 * k.UnknownCard * k.Hand, horizon = DrawHorizon(turn);
        int before = owner.DeckCount - owner.DeckAdded.Count;
        double cards = before * generic;
        for (int i = 0; i < owner.DeckAdded.Count; i++) cards += DrawnCard(owner.DeckAdded[i], generic, ownerIsBot, turn, life, otherLife);
        for (int i = 0; i < owner.DeckUpgraded.Count; i++) cards += HeldStats(owner.DeckUpgraded[i]);
        return Math.Min(horizon, owner.DeckCount) / owner.DeckCount * cards - Math.Min(horizon, before) * generic;
    }
    private double DrawnCard(FairCard card, double generic, bool ownerIsBot, int turn, long life, long otherLife)
    {
        // A Casts or Summoned When Drawn card leaves the hand at once and its owner draws again: its effect plus the next card.
        if (!global::DrawnValue.Applies(card.CardId)) return HandCard(card) * k.Hand + HeldStats(card) + global::DrawnValue.CardsWhenPlayed(card.CardId) * generic;
        double effect = RecurringPrice(global::DrawnValue.For(card.CardId), ownerIsBot, turn, life, otherLife);
        return generic + (ownerIsBot ? effect : -effect);
    }
    private double HeldStats(FairCard card)
    {
        var printed = SabberStoneCore.Model.Cards.FromId(card.CardId);
        double stats = card.Type != SabberStoneCore.Enums.CardType.MINION ? 0
            : Math.Max(0, card.Attack - printed.ATK) * W.BoardAttack * k.BoardAttack + Math.Max(0, card.Health - printed.Health) * W.BoardHealth * k.BoardHealth;
        return stats * HeldStatShare + Math.Min(4, Math.Max(0, printed.Cost - card.Cost)) * k.Discount;
    }
}
