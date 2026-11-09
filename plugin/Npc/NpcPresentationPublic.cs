using System;
using System.Collections.Generic;
using System.Linq;

// Value-only public boundary: no human card identities, hand objects, deck, or engine clone enters this detector.
public sealed class NpcPublicMinion
{
    public int Id { get; }
    public int Attack { get; }
    public int Health { get; }
    public bool Plain { get; }
    public NpcPublicMinion(int id, int attack, int health, bool plain)
    { Id = id; Attack = attack; Health = health; Plain = plain; }
}
public sealed class NpcPublicPosition
{
    public IReadOnlyList<NpcPublicMinion> Own { get; }
    public IReadOnlyList<NpcPublicMinion> Enemy { get; }
    public long OwnLife { get; }
    public long EnemyLife { get; }
    public int Mana { get; }
    public int HandCount { get; }
    public long Value => Own.Sum(m => (long)m.Attack + m.Health) - Enemy.Sum(m => (long)m.Attack + m.Health) + OwnLife - EnemyLife;
    public NpcPublicPosition(IEnumerable<NpcPublicMinion> own, IEnumerable<NpcPublicMinion> enemy,
        long ownLife, long enemyLife, int mana, int handCount)
    { Own = GoldenData.Copy(own); Enemy = GoldenData.Copy(enemy); OwnLife = ownLife;
      EnemyLife = enemyLife; Mana = mana; HandCount = handCount; }
}
public enum NpcPublicAction { Other, Attack, Spell, EndTurn }
public sealed class NpcPublicTurn
{
    private int turn = -1, actions;
    private readonly List<NpcPublicMinion> trades = new List<NpcPublicMinion>();
    private bool selfHarm;
    private long beforeSelfHarmValue;
    public string[] Observe(int publicTurn, NpcPublicAction action, NpcPublicPosition before, NpcPublicPosition after,
        int source = 0, int target = 0)
    {
        if (turn != publicTurn) { turn = publicTurn; actions = 0; trades.Clear(); selfHarm = false; }
        if (action == NpcPublicAction.EndTurn)
        {
            var result = new List<string>();
            if (trades.Any(m => after.Enemy.Any(e => e.Id == m.Id && e.Health == m.Health))) result.Add("BadTrade");
            // shortcut: an idle pass is suspicious only under public board pressure; extend only with a proven public test.
            if (actions == 0 && before.Mana >= 4 && before.HandCount > 0 && before.Enemy.Sum(m => (long)m.Attack) >= 4)
                result.Add("IdleMana");
            if (selfHarm && after.Value <= beforeSelfHarmValue - 6) result.Add("SelfHarm");
            trades.Clear(); selfHarm = false; actions = 0;
            return result.ToArray();
        }
        actions++;
        if (action == NpcPublicAction.Attack)
        {
            var attacker = before.Own.FirstOrDefault(m => m.Id == source);
            var defender = before.Enemy.FirstOrDefault(m => m.Id == target);
            var surviving = after.Enemy.FirstOrDefault(m => m.Id == target);
            if (attacker?.Plain == true && defender?.Plain == true && attacker.Attack > 0
                && defender.Health > attacker.Attack && defender.Attack >= attacker.Health
                && !after.Own.Any(m => m.Id == source) && surviving != null
                && after.Enemy.Count == before.Enemy.Count && after.Value < before.Value)
                trades.Add(surviving);
        }
        if (action == NpcPublicAction.Spell)
        {
            var hurt = before.Own.Where(m => !after.Own.Any(n => n.Id == m.Id && n.Health >= m.Health)).ToArray();
            long damage = before.Own.Sum(m => Math.Max(0, (long)m.Health - (after.Own.FirstOrDefault(n => n.Id == m.Id)?.Health ?? 0)));
            if (hurt.Length >= 2 && hurt.All(m => m.Plain) && damage >= 6
                && before.Enemy.All(m => after.Enemy.Any(n => n.Id == m.Id && n.Health == m.Health))
                && after.Enemy.Count == before.Enemy.Count
                && before.EnemyLife == after.EnemyLife && before.OwnLife >= after.OwnLife
                && after.HandCount <= before.HandCount - 1 && after.Value <= before.Value - 6)
            { selfHarm = true; beforeSelfHarmValue = before.Value; }
        }
        return new string[0];
    }
}
