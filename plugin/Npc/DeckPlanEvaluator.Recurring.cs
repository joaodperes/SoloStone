using System;
using System.Collections.Generic;

public sealed partial class DeckPlanEvaluator
{
    // recurring-value (AiFixes): set from the profile by the searches that own this evaluator; default OFF = base.
    internal bool RecurringValue { get; set; }
    // Future rounds of every working printed turn trigger on both boards, priced with this plan's own weights in the
    // current life context. `own` is always the bot (Score and ReplyValue alike).
    internal double RecurringTerm(FairPlayer own, FairPlayer enemy, int turn, long life, long otherLife)
    {
        if (!RecurringValue) return 0;
        double value = 0;
        for (int i = 0; i < own.Board.Count; i++)
            if (global::RecurringValue.Eligible(own.Board[i]) && !(ZeroDoomed && own.Board[i].Doomed))
                value += RecurringPrice(global::RecurringValue.For(own.Board[i], own.Board), true, turn, life, otherLife);
        for (int i = 0; i < enemy.Board.Count; i++)
            if (global::RecurringValue.Eligible(enemy.Board[i]) && !(ZeroDoomed && enemy.Board[i].Doomed))
                value += RecurringPrice(global::RecurringValue.For(enemy.Board[i], enemy.Board), false, turn, life, otherLife);
        return global::RecurringValue.Horizon * value;
    }
    // One round's effect, bot frame. Board deltas reuse Board()'s body/attack/health prices; cards the generic card.
    internal double RecurringPrice(FairRecurringValue d, bool ownerIsBot, int turn, long life, long otherLife)
    {
        if (!d.Any) return 0;
        double botLife = ownerIsBot ? d.OwnLife : d.EnemyLife, enemyLife = ownerIsBot ? d.EnemyLife : d.OwnLife;
        double botBodies = ownerIsBot ? d.OwnBodies : d.EnemyBodies, botAttack = ownerIsBot ? d.OwnAttack : d.EnemyAttack,
            botHealth = ownerIsBot ? d.OwnHealth : d.EnemyHealth;
        double enemyBodies = ownerIsBot ? d.EnemyBodies : d.OwnBodies, enemyAttack = ownerIsBot ? d.EnemyAttack : d.OwnAttack,
            enemyHealth = ownerIsBot ? d.EnemyHealth : d.OwnHealth;
        double botCards = ownerIsBot ? d.OwnCards : d.EnemyCards, enemyCards = ownerIsBot ? d.EnemyCards : d.OwnCards;
        var w = W;
        Func<double, double, double, double> board = (bodies, attack, health) => bodies * 2 * k.MinionBase
            + attack * w.BoardAttack * k.BoardAttack + health * w.BoardHealth * k.BoardHealth;
        return (w.OwnLife * k.OwnLife) * (LifeUtility(life + (long)Math.Round(botLife), turn, true) - LifeUtility(life, turn, true))
            - (w.EnemyLife * k.EnemyLife) * (LifeUtility(otherLife + (long)Math.Round(enemyLife), turn, false) - LifeUtility(otherLife, turn, false))
            + board(botBodies, botAttack, botHealth) * k.OwnBoard - board(enemyBodies, enemyAttack, enemyHealth) * k.EnemyBoard
            + (botCards - enemyCards) * 5.5 * k.Hand * k.UnknownCard;
    }
}
