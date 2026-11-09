using System;
using System.Collections.Generic;
using SabberStoneCore.Enums;
using EngineCards = SabberStoneCore.Model.Cards;

public sealed partial class DeckPlanEvaluator
{
    // death-value (AiFixes) is the main switch; each mechanism has its own sub-flag so an A/B can narrow without a code change.
    // Set from the profile by the searches that own this evaluator; default OFF = base.
    internal bool DeathMeasured { get; set; }
    internal bool DeathChip { get; set; }
    internal bool DeathHeld { get; set; }
    // The evaluator a search on this profile uses, for the search-free fallbacks: they read the same flags or none of them.
    internal static DeckPlanEvaluator ForFallback(DeckPlan plan, NpcProfile profile) =>
        profile.Fixes.On("death-value") && profile.Fixes.On("death-chip")
            ? new DeckPlanEvaluator(plan, profile.Budget.Brain) { RecurringValue = profile.Fixes.On("recurring-value"), ZeroDoomed = profile.Fixes.On("doomed-minions"), DeathFixes = profile.Fixes, TriggerFixes = profile.Fixes, OwnDeckValue = profile.Fixes.On("own-deck-value") }
            : null;
    internal AiFixes DeathFixes
    {
        set {
            bool main = value.On("death-value");
            DeathMeasured = main && value.On("death-measured");
            DeathChip = main && value.On("death-chip");
            DeathHeld = main && value.On("death-held");
        }
    }
    // Pending death value above this is paid at a quarter rate: no stack of chains may outweigh life and board by itself.
    internal const double DeathCap = 40;
    // The chip rule's share of a payoff minion's health: nothing at 0 Attack, half at 1, all from 2, so ordinary bodies keep their
    // chip reward and the first point of Attack adds at most half a health term.
    internal static double ChipRamp(int attack) => Math.Min(1, Math.Max(0, attack) / 2.0);
    // A body that threatens little and blocks nothing: its health mostly delays what its death pays, so removing health earns
    // little. A Taunt keeps its chip credit and a silenced shell its price, so Silence never scores below chipping.
    internal static double ChipShare(FairCard c)
    {
        if (c.Attack >= 2 || c.Taunt || c.Type != CardType.MINION) return 1;
        bool payoff = c.Death != null ? c.Death.Worth > 0 || c.RebornValue.Any
            : c.RebornValue.Any || Tag(c, GameTag.SILENCED) != 0 && DeathMeasures.PrintedPayoff(c.CardId);
        return payoff ? ChipRamp(c.Attack) : 1;
    }
    internal static bool ChipFree(FairCard c) => ChipShare(c) == 0;
    // What a pending payoff is worth today. Pressure means a death only when the attacker wants one, so it adds credit only up
    // to two turns of the body's own threat: one point of Attack moves this by a fraction of a point, and a shell gets none.
    private double PendingCredit(FairCard c, double payoff, double pressure)
    {
        if (payoff <= 0) return (.65 + .25 * pressure) * payoff;
        return .65 * payoff + .25 * pressure * (c.Taunt ? payoff : Math.Min(payoff, 2 * c.Attack * W.BoardAttack * k.BoardAttack));
    }
    private double MeasuredDeath(FairPlayer owner, FairPlayer enemy, ref double ownDraws, ref double enemyDraws, bool ownerIsBot)
    {
        double value = 0;
        for (int i = 0; i < owner.Board.Count; i++) {
            var c = owner.Board[i];
            if ((c.Death == null && !c.RebornValue.Any) || c.Untouchable) continue;
            int attack = 0;
            // doomed-minions: its delayed self-destroy already guarantees the death, so killability earns no pressure credit
            // (the payoff itself still counts: it is certain). Board() is zeroed by Priced.
            if (!c.Immune && !c.Stealth && !(ZeroDoomed && c.Doomed))
                for (int j = 0; j < enemy.Board.Count; j++) {
                    var attacker = enemy.Board[j];
                    if (!attacker.CantAttack && !attacker.Untouchable && !attacker.Frozen)
                        attack = Math.Max(attack, attacker.Attack);
                }
            double pressure = Math.Min(1, attack / (double)Math.Max(1, (long)c.Health + (c.DivineShield ? attack : 0)));
            int slots = Math.Max(0, 8 - owner.Board.Count - owner.Locations.Count);
            double bodies = 0, payoff = 0;
            if (c.Death != null) {
                bodies = c.Death.Now.Bodies;
                payoff += Measured(c.Death, slots, enemy.Board.Count, ref ownDraws, ref enemyDraws, ownerIsBot);
                value += c.Death.Now.Other + c.Death.Later.Other;
            }
            if (c.RebornValue.Any && bodies < slots) {
                payoff += Payload(c.RebornValue, 1, ref ownDraws, ref enemyDraws);
                if (c.RebornDeath != null) {
                    payoff += .65 * Measured(c.RebornDeath, Math.Max(1, slots - (int)bodies), enemy.Board.Count, ref ownDraws, ref enemyDraws, ownerIsBot);
                    value += .65 * (c.RebornDeath.Now.Other + c.RebornDeath.Later.Other);
                }
            }
            value += PendingCredit(c, payoff, pressure);
        }
        return Saturate(value) * k.Deathrattle;
    }
    // Only an owner's gain saturates; a drawback stays whole.
    private static double Saturate(double value) => value > DeathCap ? DeathCap + (value - DeathCap) * .25 : value;
    private double Measured(FairDeathMeasure m, int slots, int enemyBodies, ref double ownDraws, ref double enemyDraws, bool ownerIsBot)
    {
        double cards = Math.Min(m.Now.Cards, ownDraws), opponentCards = Math.Min(m.Now.OpponentCards, enemyDraws);
        ownDraws -= Math.Max(0, cards); enemyDraws -= Math.Max(0, opponentCards);
        double fit = m.Now.Bodies > 0 ? Math.Min(1, slots / m.Now.Bodies) : m.Later.Bodies > 0 && slots == 0 ? 0 : 1;
        double reach = Math.Min(1, enemyBodies / 2.0);
        return Vector(m.Now, fit, cards, opponentCards, ownerIsBot, reach) + Vector(m.Later, fit, m.Later.Cards, m.Later.OpponentCards, ownerIsBot, reach);
    }
    // Owner frame. Other is added undiscounted by the caller, as the flat prior always was.
    // reach: the probe measured damage against two enemies; a smaller real board loses that much less.
    private double Vector(in DeathVector d, double fit, double cards, double opponentCards, bool ownerIsBot, double reach = 1)
    {
        var w = W;
        double ownLife = ownerIsBot ? w.OwnLife * k.OwnLife : w.EnemyLife * k.EnemyLife, enemyLife = ownerIsBot ? w.EnemyLife * k.EnemyLife : w.OwnLife * k.OwnLife;
        double attack = w.BoardAttack * k.BoardAttack, health = w.BoardHealth * k.BoardHealth;
        return fit * (2 * k.MinionBase * d.Bodies + d.Attack * attack + (d.Health + (DeathChip ? 0 : d.ShellHealth)) * health + .6 * k.BoardCost * d.Cost
                + 2 * k.Taunt * d.Taunt + w.DivineShield * k.DivineShield * d.Shield)
            - (d.EnemyBodies > 0 ? 1 : reach) * (2 * k.MinionBase * d.EnemyBodies + d.EnemyAttack * attack + d.EnemyHealth * health)
            + d.OwnLife * ownLife - d.EnemyLife * enemyLife
            + (cards - opponentCards) * 5.5 * k.Hand * k.UnknownCard
            // Delayed, conditional resources: modest, bounded by what the canonical probe actually saw.
            + .5 * health * d.HandStats - k.Discount * d.HandCost + .25 * 5.5 * k.Hand * d.DeckCards
            + .6 * d.Weapon * k.Weapon + 2 * d.Mana + 4 * d.Secrets;
    }
    // Cards that hold captured minions are worth what they bring back: printed body and that body's own death value, for
    // both players when public. Same delay factor as a held weapon; a board holds seven; saturates like pending value.
    internal double HeldValue(FairCard card, bool ownerIsBot)
    {
        double value = 0; int count = 0;
        var w = W;
        for (int i = card.Held.Count - 1; i >= 0 && count < 7; i--, count++) {
            var body = EngineCards.FromId(card.Held[i]);
            value += 2 * k.MinionBase + body.ATK * w.BoardAttack * k.BoardAttack + .6 * k.BoardCost * body.Cost
                + (body[GameTag.TAUNT] != 0 ? 2 * k.Taunt : 0) + (body[GameTag.DIVINE_SHIELD] != 0 ? w.DivineShield * k.DivineShield : 0);
            double chip = !DeathChip || body[GameTag.TAUNT] != 0 || !DeathMeasures.PrintedPayoff(body.Id) ? 1 : ChipRamp(body.ATK);
            value += chip * body.Health * w.BoardHealth * k.BoardHealth;
            if (body.Power?.DeathrattleTask != null) {
                var death = DeathMeasures.For(body, body);
                double worth = Vector(death.Now, 1, death.Now.Cards, death.Now.OpponentCards, ownerIsBot) + Vector(death.Later, 1, death.Later.Cards, death.Later.OpponentCards, ownerIsBot);
                value += .65 * worth
                    + death.Now.Other + death.Later.Other;
            }
        }
        return Saturate(.75 * value);
    }
    private double EnemyHeld(FairPlayer enemy)
    {
        double value = 0;
        for (int i = 0; i < enemy.Hand.Count; i++)
            if (enemy.Hand[i].Held != null) value += HeldValue(enemy.Hand[i], false);
        return value;
    }
}
