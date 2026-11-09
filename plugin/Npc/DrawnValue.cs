using EngineGame = SabberStoneCore.Model.Game;
using EngineEntity = SabberStoneCore.Model.Entities.Entity;
using System;
using System.Collections.Concurrent;
using SabberStoneCore.Actions;
using SabberStoneCore.Model;

// own-deck-value: what a Casts or Summoned When Drawn card does when its owner draws it, in public primitives. Measured,
// never parsed from text: RecurringValue's canonical probe game draws the card, the board and life difference is the effect.
// Pure function of the card id over fixed seeds: cached, deterministic, no hidden information.
internal static class DrawnValue
{
    private static readonly int[] Seeds = { 20261105, 20261106, 20261107, 20261108 };
    private static readonly ConcurrentDictionary<string, bool> applies = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, FairRecurringValue> measured = new ConcurrentDictionary<string, FairRecurringValue>(StringComparer.Ordinal);
    private static readonly Func<string, bool> describe = id => {
        try { return Cards.FromId(id).Power?.TopdeckTask != null; } catch (Exception) { return false; }
    };
    private static readonly Func<string, FairRecurringValue> measure = Measure;
    private static readonly ConcurrentDictionary<string, double> replaced = new ConcurrentDictionary<string, double>(StringComparer.Ordinal);
    private static readonly Func<string, double> play = MeasurePlay;
    internal static bool Applies(string cardId) => cardId != null && applies.GetOrAdd(cardId, describe);
    internal static FairRecurringValue For(string cardId) => measured.GetOrAdd(cardId, measure);
    // Cards a Battlecry minion hands its owner when played: a 1-Cost Raptor that draws is worth more than its Cost says. Not for
    // a target-needing or failing play, which is 0.
    internal static double CardsWhenPlayed(string cardId) => replaced.GetOrAdd(cardId, play);
    private static double MeasurePlay(string cardId)
    {
#if ENGINE_SOURCE
        try {
            var card = Cards.FromId(cardId);
            if (card.Type != SabberStoneCore.Enums.CardType.MINION || card[SabberStoneCore.Enums.GameTag.BATTLECRY] == 0) return 0;
            var g = RecurringValue.Template().CloneExact();
            var minion = (SabberStoneCore.Model.Entities.Minion)EngineEntity.FromCard(g.Player1, card, zone: g.Player1.HandZone);
            int before = g.Player1.HandZone.Count;
            if (!g.Process(SabberStoneCore.Tasks.PlayerTasks.PlayCardTask.Minion(g.Player1, minion)) || g.Player1.Choice != null) return 0;
            return Math.Max(0, g.Player1.HandZone.Count - (before - 1));
        }
        catch (Exception error) when (!(error is OutOfMemoryException)) { return 0; }
#else
        return 0;
#endif
    }
    private static FairRecurringValue Measure(string cardId)
    {
#if ENGINE_SOURCE
        var sum = default(FairRecurringValue);
        foreach (int seed in Seeds) {
            try {
                var g = RecurringValue.Template().CloneExact();
                g.SetRandomSeed(seed);
                var before = RecurringValue.Read(g);
                EngineEntity.FromCard(g.Player1, Cards.FromId(cardId), zone: g.Player1.DeckZone);
                Generic.Draw(g.Player1);
                g.DeathProcessingAndAuraUpdate();
                var after = RecurringValue.Read(g);
                sum.OwnLife += after.OwnLife - before.OwnLife; sum.EnemyLife += after.EnemyLife - before.EnemyLife;
                sum.OwnBodies += after.OwnBodies - before.OwnBodies; sum.OwnAttack += after.OwnAttack - before.OwnAttack; sum.OwnHealth += after.OwnHealth - before.OwnHealth;
                sum.EnemyBodies += after.EnemyBodies - before.EnemyBodies; sum.EnemyAttack += after.EnemyAttack - before.EnemyAttack; sum.EnemyHealth += after.EnemyHealth - before.EnemyHealth;
                // The replacement draw is priced by the caller; only cards beyond it are the effect's.
                sum.OwnCards += after.OwnCards - before.OwnCards - 1; sum.EnemyCards += after.EnemyCards - before.EnemyCards;
            }
            catch (Exception error) when (!(error is OutOfMemoryException)) { return default(FairRecurringValue); }
        }
        int n = Seeds.Length;
        return new FairRecurringValue { OwnLife = sum.OwnLife / n, EnemyLife = sum.EnemyLife / n, OwnBodies = sum.OwnBodies / n, OwnAttack = sum.OwnAttack / n,
            OwnHealth = sum.OwnHealth / n, EnemyBodies = sum.EnemyBodies / n, EnemyAttack = sum.EnemyAttack / n, EnemyHealth = sum.EnemyHealth / n,
            OwnCards = sum.OwnCards / n, EnemyCards = sum.EnemyCards / n };
#else
        return default(FairRecurringValue);
#endif
    }
}
