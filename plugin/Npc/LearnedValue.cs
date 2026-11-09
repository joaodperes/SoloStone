using System;
using System.Collections.Generic;
using System.Linq;
using SabberStoneCore.Model;

// AI-leap research: a learned residual on top of the hand-written evaluator. Offline, a logistic model of
// P(win | public features of one side's fair view) is fitted on self-play positions (`AiHarness positions` + `learn`):
//   logit = a * H + sum_i b_i * f_i     (H = DeckPlanEvaluator score of that side)
// so in evaluator units the learned value is H + (sum_i b_i * f_i) / a. Features use only what Score already
// reads (both public boards, life, mana, counts, the side's own hand); never hidden cards.
public sealed class LearnedValue
{
    public static readonly IReadOnlyList<string> FeatureNames = Array.AsReadOnly(new[] {
        "bias", "turn", "ownLife", "enemyLife", "ownLowLife", "enemyLowLife", "ownCount", "enemyCount",
        "ownAttack", "enemyAttack", "ownHealth", "enemyHealth", "ownTauntHealth", "enemyTauntHealth", "ownShields", "enemyShields",
        "ownHand", "enemyHand", "ownHandCost", "ownMana", "enemyMana", "ownDeck", "enemyDeck", "ownWeapon", "enemyWeapon",
        "ownCost", "enemyCost", "ownDeathrattles", "enemyDeathrattles", "pressureOnUs", "pressureOnThem", "ownReady", "enemyReady" });
    public IReadOnlyList<double> Weights { get; }
    public double HandcraftedCoefficient { get; }
    public string Id { get; }
    public LearnedValue(string id, IEnumerable<double> weights, double handcraftedCoefficient)
    {
        Id = id; Weights = GoldenData.Copy(weights); HandcraftedCoefficient = handcraftedCoefficient;
        if (Weights.Count != FeatureNames.Count) throw new ArgumentException("Learned value needs " + FeatureNames.Count + " weights.");
        if (!(handcraftedCoefficient > 0)) throw new ArgumentException("Learned value handcrafted coefficient must be > 0.");
    }
    // Correction in evaluator units (added to H).
    internal double Correction(FairPlayer own, FairPlayer enemy, int turn, IReadOnlyList<FairCard> hand)
    {
        var f = Features(own, enemy, turn, hand); double sum = 0;
        for (int i = 0; i < f.Length; i++) sum += Weights[i] * f[i];
        return sum / HandcraftedCoefficient;
    }
    public static double[] Features(FairPlayer own, FairPlayer enemy, int turn, IReadOnlyList<FairCard> hand)
    {
        long life = (long)own.Hero.Health + own.Hero.Armor, other = (long)enemy.Hero.Health + enemy.Hero.Armor;
        Func<FairPlayer, long> ready = p => p.Board.Where(c => !c.Frozen).Sum(c => (long)c.Attack) + (p.Weapon == null ? 0 : p.Weapon.Attack);
        return new double[] {
            1, Math.Min(turn, 30) / 10.0, life / 10.0, other / 10.0, Math.Max(0, 12 - life) / 10.0, Math.Max(0, 12 - other) / 10.0,
            own.Board.Count, enemy.Board.Count, own.Board.Sum(c => (long)c.Attack) / 5.0, enemy.Board.Sum(c => (long)c.Attack) / 5.0,
            own.Board.Sum(c => (long)c.Health) / 5.0, enemy.Board.Sum(c => (long)c.Health) / 5.0,
            own.Board.Where(c => c.Taunt).Sum(c => (long)c.Health) / 5.0, enemy.Board.Where(c => c.Taunt).Sum(c => (long)c.Health) / 5.0,
            own.Board.Count(c => c.DivineShield), enemy.Board.Count(c => c.DivineShield),
            hand.Count, enemy.Hand.Count, hand.Where(c => !c.Unknown).Sum(c => Math.Min(10, c.Cost)) / 10.0,
            own.BaseMana / 5.0, enemy.BaseMana / 5.0, Math.Min(30, own.DeckCount) / 10.0, Math.Min(30, enemy.DeckCount) / 10.0,
            own.Weapon == null ? 0 : (long)own.Weapon.Attack * own.Weapon.Durability / 5.0, enemy.Weapon == null ? 0 : (long)enemy.Weapon.Attack * enemy.Weapon.Durability / 5.0,
            own.Board.Sum(c => (long)c.Cost) / 10.0, enemy.Board.Sum(c => (long)c.Cost) / 10.0,
            own.Board.Count(DeckPlanEvaluator.Deathrattle), enemy.Board.Count(DeckPlanEvaluator.Deathrattle),
            Math.Max(0, ready(enemy) - life + 1) / 5.0, Math.Max(0, ready(own) - other + 1) / 5.0,
            ready(own) / 5.0, ready(enemy) / 5.0 };
    }
}
