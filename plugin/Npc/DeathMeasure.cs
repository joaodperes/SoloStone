using EngineCard = SabberStoneCore.Model.Card;
using EngineCards = SabberStoneCore.Model.Cards;
using EngineGame = SabberStoneCore.Model.Game;
using EngineMinion = SabberStoneCore.Model.Entities.Minion;
using EngineEntity = SabberStoneCore.Model.Entities.Entity;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;

// death-value (AiFixes): what a minion's death hands its owner, in public primitives, measured ahead of time by a
// canonical kill probe (DeathProbe) and shipped as generated data (DeathValueData). Never parsed from card text and never
// executed at decision time. A new card needs no code: regenerate, and the sync test (DeathValueTests) proves the table
// matches the card set. A value of the owner's frame: positive helps whoever controls the minion.
public struct DeathVector
{
    public double Bodies, Attack, Health, Cost, Taunt, Shield, Cards, OpponentCards, OwnLife, EnemyLife,
        EnemyBodies, EnemyAttack, EnemyHealth, HandStats, HandCost, DeckCards, Weapon, Mana, Secrets, Other;
    // Health of summoned shells, outside Health so death-chip decides at pricing time whether it pays (never serialized).
    public double ShellHealth;
    public bool Any => Bodies != 0 || Attack != 0 || Health != 0 || Cost != 0 || Taunt != 0 || Shield != 0 || Cards != 0
        || OpponentCards != 0 || OwnLife != 0 || EnemyLife != 0 || EnemyBodies != 0 || EnemyAttack != 0 || EnemyHealth != 0
        || HandStats != 0 || HandCost != 0 || DeckCards != 0 || Weapon != 0 || Mana != 0 || Secrets != 0 || Other != 0;
    public DeathVector Plus(in DeathVector b, double scale = 1) => new DeathVector {
        Bodies = Bodies + scale * b.Bodies, Attack = Attack + scale * b.Attack, Health = Health + scale * b.Health,
        Cost = Cost + scale * b.Cost, Taunt = Taunt + scale * b.Taunt, Shield = Shield + scale * b.Shield,
        Cards = Cards + scale * b.Cards, OpponentCards = OpponentCards + scale * b.OpponentCards,
        OwnLife = OwnLife + scale * b.OwnLife, EnemyLife = EnemyLife + scale * b.EnemyLife, EnemyBodies = EnemyBodies + scale * b.EnemyBodies,
        EnemyAttack = EnemyAttack + scale * b.EnemyAttack, EnemyHealth = EnemyHealth + scale * b.EnemyHealth,
        HandStats = HandStats + scale * b.HandStats, HandCost = HandCost + scale * b.HandCost, DeckCards = DeckCards + scale * b.DeckCards,
        Weapon = Weapon + scale * b.Weapon, Mana = Mana + scale * b.Mana, Secrets = Secrets + scale * b.Secrets, Other = Other + scale * b.Other,
        ShellHealth = ShellHealth + scale * b.ShellHealth };
    internal static DeathVector Body(EngineCard c, int health = 0) => new DeathVector {
        Bodies = 1, Attack = c.ATK, Health = health > 0 ? health : c.Health, Cost = c.Cost,
        Taunt = c[GameTag.TAUNT] != 0 ? 1 : 0, Shield = c[GameTag.DIVINE_SHIELD] != 0 ? 1 : 0 };
    internal static DeathVector FromLegacy(in FairDeathValue d) => new DeathVector {
        Bodies = d.Bodies, Attack = d.Attack, Health = d.Health, Cost = d.Cost, Taunt = d.Taunt, Shield = d.Shield,
        Cards = d.Cards, OpponentCards = d.OpponentCards, Other = d.Other };
    internal string Serialize() => string.Join(",", Fields().Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)));
    internal IEnumerable<double> Fields() => new[] { Bodies, Attack, Health, Cost, Taunt, Shield, Cards, OpponentCards, OwnLife, EnemyLife,
        EnemyBodies, EnemyAttack, EnemyHealth, HandStats, HandCost, DeckCards, Weapon, Mana, Secrets, Other };
    internal static DeathVector Parse(string text)
    {
        if (text.Length == 0) return default(DeathVector);
        var f = text.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        return new DeathVector { Bodies = f[0], Attack = f[1], Health = f[2], Cost = f[3], Taunt = f[4], Shield = f[5], Cards = f[6], OpponentCards = f[7],
            OwnLife = f[8], EnemyLife = f[9], EnemyBodies = f[10], EnemyAttack = f[11], EnemyHealth = f[12], HandStats = f[13], HandCost = f[14],
            DeckCards = f[15], Weapon = f[16], Mana = f[17], Secrets = f[18], Other = f[19] };
    }
    // Reference price at neutral weights: sorting, chip-rule eligibility and reports only, never a line's value.
    internal double Worth => 2 * Bodies + 2.2 * Attack + 1.2 * Health + .6 * Cost + 2 * Taunt + 2 * Shield + 5.5 * (Cards - OpponentCards)
        + OwnLife - EnemyLife - (2 * EnemyBodies + 2.2 * EnemyAttack + 1.2 * EnemyHealth) + .6 * HandStats - HandCost
        + 1.4 * DeckCards + .6 * Weapon + 2 * Mana + 4 * Secrets + Other;
}

// Immutable, cached per card id. Now resolves on this death; Later is the discounted rest of the chain (a summoned minion's
// own death value through its row, never "Break N times" text).
public sealed class FairDeathMeasure
{
    public readonly DeathVector Now, Later;
    internal readonly double Worth;
    internal FairDeathMeasure(in DeathVector now, in DeathVector later) { Now = now; Later = later; Worth = now.Worth + later.Worth; }
    internal FairDeathMeasure Plus(FairDeathMeasure b) => b == null ? this : new FairDeathMeasure(Now.Plus(b.Now), Later.Plus(b.Later));
    internal string Key => Now.Serialize() + "+" + Later.Serialize();
}

internal static class DeathMeasures
{
    internal sealed class Row
    {
        internal string Id;
        internal bool Opaque, Reborn;
        internal DeathVector Now;
        internal KeyValuePair<string, double>[] Next = new KeyValuePair<string, double>[0];
        internal string Serialize() => Id + "|" + (Opaque ? "o" : "") + (Reborn ? "r" : "") + "|" + (Now.Any ? Now.Serialize() : "")
            + "|" + string.Join(";", Next.Select(n => n.Key + ":" + n.Value.ToString("0.###", CultureInfo.InvariantCulture)));
        internal static Row Parse(string line)
        {
            var p = line.Split('|');
            return new Row { Id = p[0], Opaque = p[1].Contains('o'), Reborn = p[1].Contains('r'), Now = DeathVector.Parse(p[2]),
                Next = p[3].Length == 0 ? new KeyValuePair<string, double>[0] : p[3].Split(';').Select(n => n.Split(':'))
                    .Select(n => new KeyValuePair<string, double>(n[0], double.Parse(n[1], CultureInfo.InvariantCulture))).ToArray() };
        }
    }
    // Chain discount per stage, order of magnitude only: npc-ai-tuning.md "Death value" (hs ai death-calibrate).
    internal const double ShippedChainDiscount = .65;
    internal const int MaxDepth = 6;
    // A probe found no public effect at the canonical board (conditional, delayed or stored-card scripts): keep the old prior.
    internal const double OpaquePrior = 3;
    private static double chainDiscount = ShippedChainDiscount;
    // Harness calibration only; a live process never sets it.
    internal static double ChainDiscount
    {
        get => chainDiscount;
        set { chainDiscount = value; cache.Clear(); }
    }
    private static readonly Lazy<Dictionary<string, Row>> rows = new Lazy<Dictionary<string, Row>>(() =>
        DeathValueData.Rows.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Select(Row.Parse).ToDictionary(r => r.Id, StringComparer.Ordinal));
    private static readonly Lazy<HashSet<string>> holders = new Lazy<HashSet<string>>(() =>
        new HashSet<string>(DeathValueData.Holders.Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal));
    private static readonly ConcurrentDictionary<string, FairDeathMeasure> cache = new ConcurrentDictionary<string, FairDeathMeasure>(StringComparer.Ordinal);
    private static readonly Func<string, FairDeathMeasure> build = Build;
    internal static IReadOnlyDictionary<string, Row> Rows => rows.Value;
    // Hand cards the generated sweep proved resummon the minions captured on them (the CapturedCard shape).
    internal static bool Holds(string cardId) => holders.Value.Contains(cardId);
    private static FairDeathMeasure Build(string id)
    {
        Row row;
        if (!rows.Value.TryGetValue(id, out row)) return new FairDeathMeasure(DeathVector.FromLegacy(FairDeathValue.For(EngineCards.FromId(id), EngineCards.FromId(id))), default(DeathVector));
        return new FairDeathMeasure(Effect(row), Chain(row, 0));
    }
    private static DeathVector Effect(Row row)
    {
        if (row.Opaque) return new DeathVector { Other = OpaquePrior };
        var value = row.Now;
        // The share of a summoned body's health the board rule refuses to price (DeckPlanEvaluator.ChipRamp) moves to ShellHealth.
        foreach (var next in row.Next) {
            var body = EngineCards.FromId(next.Key);
            if (body[GameTag.TAUNT] != 0) continue;
            double shell = next.Value * body.Health * (1 - DeckPlanEvaluator.ChipRamp(body.ATK));
            value.Health -= shell; value.ShellHealth += shell;
        }
        return value;
    }
    // The rest of this row's chain: every summoned minion with its own death value, weighted by how often the probe saw it.
    private static DeathVector Chain(Row row, int depth)
    {
        var later = default(DeathVector);
        if (depth >= MaxDepth) return later;
        foreach (var next in row.Next) {
            Row stage;
            if (!rows.Value.TryGetValue(next.Key, out stage)) continue;
            later = later.Plus(Stage(stage, depth + 1), chainDiscount * next.Value);
        }
        return later;
    }
    private static DeathVector Stage(Row row, int depth)
    {
        var value = Effect(row).Plus(Chain(row, depth));
        // A summoned stage's printed Reborn comes back once at 1 health and repeats the printed Deathrattle.
        if (row.Reborn && depth < MaxDepth) {
            var card = EngineCards.FromId(row.Id);
            value = value.Plus(DeathVector.Body(card, 1), chainDiscount).Plus(Effect(row), chainDiscount * chainDiscount);
        }
        return value;
    }
    internal static FairDeathMeasure For(EngineCard effect, EngineCard host)
    {
        if (FairDeathValue.HostBound(effect.Id))
            return new FairDeathMeasure(DeathVector.FromLegacy(FairDeathValue.For(effect, host)), default(DeathVector));
        return cache.GetOrAdd(effect.Id, build);
    }
    // Printed death value exists (silenced or not): a silenced shell is priced like the shell it was, so Silence never scores below chipping.
    internal static bool PrintedPayoff(string cardId)
    {
        Row row;
        return cardId != null && rows.Value.TryGetValue(cardId, out row) && (row.Opaque || row.Now.Worth + Chain(row, 0).Worth > 0);
    }
}

#if ENGINE_SOURCE
// The canonical kill probe, run by `hs ai death-values` and the sync test, never at decision time. Board: the minion between
// two sturdy peers, a sturdy and a fragile enemy, damaged heroes, cards in both hands and decks, ten mana. Two forks per seed kill the
// minion: one as printed, one silenced first (dead either way). The difference is the payoff. Random effects average over
// seeds. Printed Reborn is stripped in both forks and priced separately from the public tag (it can also be granted).
internal static class DeathProbe
{
    internal const int Seeds = 8;
    private const string Peer = "CS2_200", Filler = "CS2_120", HandMinion = "CS2_182", HandSpell = "CS2_029", DeadDeathrattle = "EX1_096", Fragile = "CS2_172";
    // A mixed top of deck so conditional draws find something: a big minion, cheap and costly spells of each school a Fire or
    // Holy draw names, a Beast filler. An ordinary "draw a card" takes a filler.
    private static readonly string[] DeckMix = { "CS2_186", "CS2_032", "CS2_029", "CS2_093", "EX1_308" };
    [ThreadStatic] private static EngineGame template;
    internal static EngineGame Template()
    {
        if (template != null) return template;
        var deck = Enumerable.Range(0, 30).Select(i => EngineCards.FromId(i >= 30 - DeckMix.Length ? DeckMix[i - 30 + DeckMix.Length] : Filler)).ToList();
        var g = new EngineGame(new SabberStoneCore.Config.GameConfig { Player1HeroClass = CardClass.WARRIOR, Player2HeroClass = CardClass.WARRIOR,
            Player1Deck = deck, Player2Deck = deck.ToList(), StartPlayer = 1, Shuffle = false,
            SkipMulligan = true, History = false, Logging = false, RandomSeed = 20261104 });
        g.StartGame();
        foreach (var p in new[] { g.Player1, g.Player2 }) {
            foreach (var c in p.HandZone.ToArray()) { p.HandZone.Remove(c); p.SetasideZone.Add(c); }
            p.BaseMana = 10; p.UsedMana = 0; p.Hero.Damage = 10;
            foreach (var id in new[] { HandMinion, HandMinion, HandSpell })
                EngineEntity.FromCard(p, EngineCards.FromId(id), zone: p.HandZone);
            EngineEntity.FromCard(p, EngineCards.FromId(Peer), zone: p.BoardZone);
            // The enemy's second body is fragile, so a ping can kill and an AoE does not get paid for two sturdy minions.
            EngineEntity.FromCard(p, EngineCards.FromId(p == g.Player1 ? Peer : Fragile), zone: p.BoardZone);
        }
        g.SummonedMinions.Clear();
        // One dead friendly Deathrattle minion, for resurrect effects.
        var dead = (EngineMinion)EngineEntity.FromCard(g.Player1, EngineCards.FromId(DeadDeathrattle), zone: g.Player1.BoardZone);
        g.SummonedMinions.Clear();
        dead.ToBeDestroyed = true; g.DeathProcessingAndAuraUpdate();
        return template = g;
    }
    internal static DeathVector Read(EngineGame g)
    {
        var own = g.Player1; var enemy = g.Player2;
        return new DeathVector {
            Bodies = own.BoardZone.Count, Attack = own.BoardZone.Sum(m => (double)m.AttackDamage), Health = own.BoardZone.Sum(m => (double)m.Health),
            Cost = own.BoardZone.Sum(m => (double)m.Card.Cost), Taunt = own.BoardZone.Count(m => m.HasTaunt), Shield = own.BoardZone.Count(m => m.HasDivineShield),
            Cards = own.HandZone.Count, OpponentCards = enemy.HandZone.Count,
            OwnLife = (double)own.Hero.Health + own.Hero.Armor, EnemyLife = (double)enemy.Hero.Health + enemy.Hero.Armor,
            EnemyBodies = enemy.BoardZone.Count, EnemyAttack = enemy.BoardZone.Sum(m => (double)m.AttackDamage), EnemyHealth = enemy.BoardZone.Sum(m => (double)m.Health),
            // Printed differences, so an ordinary draw is not read as a buff, a discount or a thinner deck.
            HandStats = own.HandZone.OfType<EngineMinion>().Sum(m => (double)m.AttackDamage + m.Health - m.Card.ATK - m.Card.Health),
            HandCost = own.HandZone.Sum(c => (double)c.Cost - c.Card.Cost),
            DeckCards = own.DeckZone.Count,
            Weapon = own.Hero.Weapon == null ? 0 : (double)own.Hero.Weapon.AttackDamage * own.Hero.Weapon.Durability,
            Mana = own.BaseMana, Secrets = own.SecretZone.Count };
    }
    private static DeathVector Delta(in DeathVector a, in DeathVector b) => a.Plus(b, -1);
    internal static void Resolve(EngineGame g)
    {
        for (int i = 0; i < 6; i++) {
            var chooser = g.Player1.Choice != null ? g.Player1 : g.Player2.Choice != null ? g.Player2 : null;
            if (chooser == null) return;
            g.Process(SabberStoneCore.Tasks.PlayerTasks.ChooseTask.Pick(chooser, chooser.Choice.Choices[0]));
        }
    }
    private static EngineMinion Place(EngineGame g, EngineCard subject)
    {
        // An enchantment row is measured on a vanilla host; a minion row on itself.
        bool grant = subject.Type != CardType.MINION;
        var m = (EngineMinion)EngineEntity.FromCard(g.Player1, grant ? EngineCards.FromId("CS2_182") : subject, zone: g.Player1.BoardZone, zonePos: 1);
        g.SummonedMinions.Clear();
        m[GameTag.REBORN] = 0;
        if (grant) SabberStoneCore.Actions.Generic.AddEnchantmentBlock(g, subject, m, m);
        return m;
    }
    internal static DeathMeasures.Row Measure(string id)
    {
        var card = EngineCards.FromId(id);
        var row = new DeathMeasures.Row { Id = id, Reborn = card.Type == CardType.MINION && card[GameTag.REBORN] != 0 };
        bool hasDeathrattle = card.Power?.DeathrattleTask != null;
        if (!hasDeathrattle) return row;
        var total = default(DeathVector); var stages = new Dictionary<string, double>(StringComparer.Ordinal);
        try {
            for (int seed = 0; seed < Seeds; seed++) {
                var live = Template().CloneExact(); live.SetRandomSeed(1000 + seed);
                var dead = Template().CloneExact(); dead.SetRandomSeed(1000 + seed);
                var m = Place(live, card); var quiet = Place(dead, card);
                quiet.Silence();
                var before = new HashSet<int>(live.Player1.BoardZone.Select(x => x.Id));
                m.ToBeDestroyed = true; live.DeathProcessingAndAuraUpdate(); Resolve(live);
                quiet.ToBeDestroyed = true; dead.DeathProcessingAndAuraUpdate();
                total = total.Plus(Delta(Read(live), Read(dead)));
                foreach (var body in live.Player1.BoardZone.Where(x => !before.Contains(x.Id) && !x.IsSilenced)) {
                    if (body.Card.Power?.DeathrattleTask == null && body[GameTag.REBORN] == 0) continue;
                    double seen; stages.TryGetValue(body.Card.Id, out seen); stages[body.Card.Id] = seen + 1;
                }
            }
        }
        catch (Exception error) when (!(error is OutOfMemoryException)) { row.Opaque = true; row.Now = default(DeathVector); return row; }
        row.Now = default(DeathVector).Plus(total, 1.0 / Seeds);
        row.Now.DeckCards = Math.Max(0, row.Now.DeckCards + row.Now.Cards);
        row.Next = stages.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new KeyValuePair<string, double>(p.Key, p.Value / Seeds)).ToArray();
        row.Opaque = !row.Now.Any && row.Next.Length == 0;
        return row;
    }
    // Reborn is never in the table's Now: this measured body checks the structural price (printed attack, 1 health).
    internal static DeathVector MeasureReborn(string id)
    {
        var live = Template().CloneExact(); var dead = Template().CloneExact();
        var m = (EngineMinion)EngineEntity.FromCard(live.Player1, EngineCards.FromId(id), zone: live.Player1.BoardZone, zonePos: 1);
        var quiet = (EngineMinion)EngineEntity.FromCard(dead.Player1, EngineCards.FromId(id), zone: dead.Player1.BoardZone, zonePos: 1);
        live.SummonedMinions.Clear(); dead.SummonedMinions.Clear();
        quiet.Silence();
        foreach (var strip in new[] { m, quiet }) strip[GameTag.REBORN] = strip == m ? 1 : 0;
        m.ToBeDestroyed = true; live.DeathProcessingAndAuraUpdate(); Resolve(live);
        quiet.ToBeDestroyed = true; dead.DeathProcessingAndAuraUpdate();
        return Delta(Read(live), Read(dead));
    }
    // The table's domain: every implemented card with a Deathrattle task, a minion's Reborn, or a Deathrattle enchantment.
    internal static IEnumerable<string> Domain() => EngineCards.All
        .Where(c => c.Id != null && (c.Type == CardType.MINION || c.Type == CardType.ENCHANTMENT)
            && (c.Power?.DeathrattleTask != null || c.Type == CardType.MINION && c[GameTag.REBORN] != 0))
        .Select(c => c.Id).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal);
    internal static IEnumerable<DeathMeasures.Row> Generate() => Domain().Select(Measure);
}
#endif
