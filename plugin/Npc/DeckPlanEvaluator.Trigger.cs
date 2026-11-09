using System;
using System.Collections.Generic;
using SabberStoneCore.Enums;

public sealed partial class DeckPlanEvaluator
{
    // trigger-value (AiFixes) is the main switch; each family has a sub-flag so an A/B can narrow without a code change.
    // Set from the profile by the searches that own this evaluator; default OFF = base.
    internal bool TriggerValue { get; set; }
    internal bool TriggerAttack { get; set; }
    internal bool TriggerPlays { get; set; }
    internal bool TriggerDamage { get; set; }
    internal AiFixes TriggerFixes
    {
        set {
            TriggerValue = value.On("trigger-value");
            TriggerAttack = TriggerValue && value.On("trigger-attack");
            TriggerPlays = TriggerValue && value.On("trigger-plays");
            TriggerDamage = TriggerValue && value.On("trigger-damage");
        }
    }
    private TriggerEvidence ownEvidence, enemyEvidence;
    // Public counts for one search, like SetOpponentEvidence; never recomputed per node.
    internal void SetTriggerEvidence(in TriggerEvidence own, in TriggerEvidence enemy) { ownEvidence = own; enemyEvidence = enemy; }
    // Future firings of every working printed event trigger on both boards, priced with this plan's weights. `own` is always the bot.
    internal double TriggerTerm(FairPlayer own, FairPlayer enemy) =>
        Saturate(TriggerSide(own, enemy, ownEvidence, true)) * k.OwnBoard - Saturate(TriggerSide(enemy, own, enemyEvidence, false)) * k.EnemyBoard;
    private double TriggerSide(FairPlayer owner, FairPlayer opponent, in TriggerEvidence evidence, bool ownerIsBot)
    {
        double value = 0, draws = DrawCapacity(owner);
        for (int i = 0; i < owner.Board.Count; i++) {
            var c = owner.Board[i];
            if (c.Triggers == null || c.Untouchable || Tag(c, GameTag.DORMANT) != 0 || ZeroDoomed && c.Doomed) continue;
            for (int j = 0; j < c.Triggers.Length; j++) {
                var m = c.Triggers[j];
                if (!FamilyOn(m.Kind)) continue;
                double rounds = global::TriggerRates.Horizon * m.Share * global::TriggerRates.PerRound(m.Kind, c, owner, opponent, evidence);
                if (m.Kind == TriggerKind.SelfDamage) rounds = Math.Min(rounds, c.Health);
                if (rounds <= 0) continue;
                double cards = Math.Min(rounds * m.Fire.Cards, Math.Max(0, draws));
                if (m.Fire.Cards > 0) draws -= cards;
                value += FirePrice(m, rounds, cards, owner, opponent, ownerIsBot);
            }
        }
        return value;
    }
    private bool FamilyOn(TriggerKind kind)
    {
        switch (kind) {
            case TriggerKind.HeroAttack: case TriggerKind.SelfAttack: return TriggerAttack;
            case TriggerKind.SelfDamage: return TriggerDamage;
            default: return TriggerPlays;
        }
    }
    private double FirePrice(FairTriggerMeasure m, double rounds, double cards, FairPlayer owner, FairPlayer opponent, bool ownerIsBot)
    {
        var d = m.Fire;
        // No room or no deck means no card, and nothing a card carries.
        if (d.Cards > 0) {
            double landed = cards / (rounds * d.Cards);
            d.DeckCards *= landed; d.HandStats *= landed; d.HandCost *= landed;
        }
        // The probe measured two enemy minions and three own bodies; a board effect has the real boards to hit.
        if (opponent.Board.Count < 2 && (d.EnemyBodies != 0 || d.EnemyAttack != 0 || d.EnemyHealth != 0)) {
            double reach = opponent.Board.Count / 2.0;
            d.EnemyBodies *= reach; d.EnemyAttack *= reach; d.EnemyHealth *= reach;
        }
        if (d.Health < 0) d.Health *= owner.Board.Count / 3.0;
        int slots = Math.Max(0, 7 - owner.Board.Count - owner.Locations.Count);
        double fit = d.Bodies > 0 ? Math.Min(1, slots / (rounds * d.Bodies)) : 1;
        double value = rounds * Vector(d, fit, cards / rounds, d.OpponentCards, ownerIsBot);
        // The owner times its own spells, plays and swings and can skip a harmful one; the opponent chooses when a hit lands.
        return m.Kind == TriggerKind.SelfDamage ? value : Math.Max(0, value);
    }
    // A hit on this body hands its owner a card, which the evaluator cannot see on the opponent's side (its hand is not valued), so
    // chipping it must earn nothing. Priced at full health: a chip moves nothing and a kill still removes the whole body. Summons and
    // armor are on the board for the evaluator to see, so those bodies keep the ordinary price. A Taunt must be chipped through.
    private bool HitPays(FairCard c)
    {
        if (!TriggerDamage || c.Triggers == null || c.Taunt) return false;
        for (int i = 0; i < c.Triggers.Length; i++)
            if (c.Triggers[i].Kind == TriggerKind.SelfDamage && c.Triggers[i].Fire.Cards > 0) return true;
        return false;
    }
}
