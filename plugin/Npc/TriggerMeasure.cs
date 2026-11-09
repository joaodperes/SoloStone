using EngineCard = SabberStoneCore.Model.Card;
using EngineCards = SabberStoneCore.Model.Cards;
using EngineGame = SabberStoneCore.Model.Game;
using EngineMinion = SabberStoneCore.Model.Entities.Minion;
using EngineEntity = SabberStoneCore.Model.Entities.Entity;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SabberStoneCore.Enums;
using SabberStoneCore.Triggers;
using SabberStoneCore.Tasks.PlayerTasks;

// trigger-value (AiFixes): what one firing of a board minion's printed event trigger hands its owner, in public primitives,
// measured ahead of time by a canonical event probe (TriggerProbe) and shipped as generated data (TriggerValueData). Never
// parsed from card text and never executed at decision time. How often it fires is a separate, public estimate (TriggerRates).
// Turn triggers belong to RecurringValue and Deathrattles to DeathMeasure; this is everything that fires on an event.
internal enum TriggerKind { HeroAttack, SelfAttack, OwnSpell, OwnMinion, OwnCard, OwnHeroPower, SelfDamage, OwnDraw }

public sealed class FairTriggerMeasure
{
    internal readonly TriggerKind Kind;
    // Per firing, owner's frame, and the share of the probe's event mix that fires it.
    public readonly DeathVector Fire;
    public readonly double Share;
    internal FairTriggerMeasure(TriggerKind kind, in DeathVector fire, double share) { Kind = kind; Fire = fire; Share = share; }
}

internal static class TriggerMeasures
{
    // Why a row is not priced. Partial: fires on under half of the generic event mix, so its rate is the hidden deck's (a tribe or school).
    internal enum Status { Priced, NoEffect, Partial, SelfRemoving, Error }
    internal const double MinShare = .5;
    internal sealed class Row
    {
        internal string Id;
        internal TriggerKind Kind;
        internal Status Status;
        internal double Share;
        internal DeathVector Fire;
        internal string Serialize() => Id + "|" + Kind + "|" + Status + "|" + Share.ToString("0.###", CultureInfo.InvariantCulture) + "|" + (Fire.Any ? Fire.Serialize() : "");
        internal static Row Parse(string line)
        {
            var p = line.Split('|');
            return new Row { Id = p[0], Kind = (TriggerKind)Enum.Parse(typeof(TriggerKind), p[1]), Status = (Status)Enum.Parse(typeof(Status), p[2]),
                Share = double.Parse(p[3], CultureInfo.InvariantCulture), Fire = DeathVector.Parse(p[4]) };
        }
    }
    private static readonly Lazy<Row[]> rows = new Lazy<Row[]>(() =>
        TriggerValueData.Rows.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Select(Row.Parse).ToArray());
    private static readonly Lazy<Dictionary<string, FairTriggerMeasure[]>> priced = new Lazy<Dictionary<string, FairTriggerMeasure[]>>(() =>
        rows.Value.Where(r => r.Status == Status.Priced).GroupBy(r => r.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(r => new FairTriggerMeasure(r.Kind, r.Fire, r.Share)).ToArray(), StringComparer.Ordinal));
    private static readonly Lazy<HashSet<string>> armed = new Lazy<HashSet<string>>(() =>
        new HashSet<string>(TriggerValueData.HeroPowerAttack.Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal));
    internal static IReadOnlyList<Row> Rows => rows.Value;
    // Null when the card has no priced trigger: most minions, so this stays one failed lookup per board minion.
    internal static FairTriggerMeasure[] For(string cardId)
    {
        FairTriggerMeasure[] found;
        return cardId != null && priced.Value.TryGetValue(cardId, out found) ? found : null;
    }
    // Hero powers the generated sweep saw arm the hero (Attack or a weapon): a public, class-level hero-attack enabler.
    internal static bool HeroPowerArms(string cardId) => cardId != null && armed.Value.Contains(cardId);
}

// One player's public record so far: own turns taken, and the two event counts that predict that player's next turn.
public struct TriggerEvidence
{
    internal int Turns, HeroAttacks, Spells;
#if ENGINE_SOURCE
    // Completed turns only, so both players read the same frame and a mid-turn replan reads what the turn's first decision read.
    internal static TriggerEvidence Of(EngineGame game, SabberStoneCore.Model.Entities.Controller player)
    {
        var e = new TriggerEvidence(); var q = game.HistoryFacts.Query;
        e.Turns = q.TurnsTaken(player.Id) - (game.CurrentPlayer == player ? 1 : 0);
        foreach (var f in q.Plays())
            if (f.Turn < game.Turn && f.ControllerId == player.Id && f.ActiveControllerId == player.Id && f.CardType == CardType.SPELL) e.Spells++;
        foreach (var f in q.Events<SabberStoneCore.Model.AttackStartedFact>())
            if (f.Turn < game.Turn && f.ControllerId == player.Id && f.AttackerType == CardType.HERO) e.HeroAttacks++;
        return e;
    }
#endif
}

// How often a kind of event happens per owner round, from public facts only, both players alike: no card in a hidden hand or deck
// is read, for the NPC's own side either, so its own view is no richer than the opponent's. Constants are master self-play rates
// with trigger-value off (`hs ai trigger-values --rates`, tools/ai_mine/trigger_rates.py); see npc-ai-tuning.md "Trigger value".
internal static class TriggerRates
{
    // Rounds credited beyond the one the search itself simulates: minions with a priced trigger survive to their owner's next turn
    // at about half (pooled .475), so p/(1-p) is about 1. An order of magnitude, not a measurement.
    internal const double Horizon = 1;
    // Armed is conditional on a hero-attack engine being on board, the only time it is read (.45-.49 without that condition).
    private const double WeaponReady = .84, Armed = .67, Unarmed = .08, SelfAttacks = .8, Spell = 1.1, Minion = .95, Card = 2.1, Power = .33, HitBase = .4, HitPerAttacker = .13;
    // Pseudo-turns of the prior, by mean squared error on this turn's events with the prior at the family's own mean. A record
    // predicts spells (6); unarmed heroes tie flat at 12, and that weak term keeps a weapon deck between weapons above a deck
    // without one. Armed heroes, minions, cards and hero powers read no record: flat beats or ties it.
    private const double UnarmedTurns = 12, SpellTurns = 6;
    private static double Shrunk(double prior, double priorTurns, int count, int turns) => (prior * priorTurns + count) / (priorTurns + Math.Max(0, turns));
    internal static double PerRound(TriggerKind kind, FairCard c, FairPlayer owner, FairPlayer opponent, in TriggerEvidence e)
    {
        switch (kind) {
            case TriggerKind.HeroAttack:
                if (owner.Weapon != null && owner.Weapon.Attack > 0 && owner.Weapon.Durability > 0) return WeaponReady;
                return TriggerMeasures.HeroPowerArms(owner.HeroPower.CardId) ? Armed : Shrunk(Unarmed, UnarmedTurns, e.HeroAttacks, e.Turns);
            case TriggerKind.SelfAttack: return c.Attack > 0 && !c.CantAttack ? SelfAttacks : 0;
            case TriggerKind.OwnSpell: return Shrunk(Spell, SpellTurns, e.Spells, e.Turns);
            case TriggerKind.OwnMinion: return Minion;
            case TriggerKind.OwnCard: return Card;
            case TriggerKind.OwnHeroPower: return Power;
            case TriggerKind.SelfDamage: return HitBase + HitPerAttacker * Math.Min(3, Attackers(opponent));
            case TriggerKind.OwnDraw: return owner.DeckCount > 0 ? 1 : 0;
            default: return 0;
        }
    }
    private static int Attackers(FairPlayer player)
    {
        int n = 0;
        for (int i = 0; i < player.Board.Count; i++) if (player.Board[i].Attack > 0) n++;
        return n;
    }
}

#if ENGINE_SOURCE
// The canonical event probe, run by `hs ai trigger-values` and the sync test, never at decision time. Board: DeathProbe's. Two forks
// per seed differ only in the minion's trigger being removed (as RecurringValue does, so stats stay comparable); each then takes
// the same event. The difference is one firing. An event mix stands in for a deck: share = how much of the mix fires it.
internal static class TriggerProbe
{
    internal const int Seeds = 8;
    private const string Yeti = "CS2_182", Axe = "CS2_106";
    // One castable spell of each school and one without, at the enemy hero when it takes a target.
    internal static readonly string[] Spells = { "CS2_008", "CORE_DS1_185", "FIR_909", "CORE_CS2_024", "CORE_EX1_238", "CORE_CS2_093", "BE_223", "CORE_BT_035" };
    // A plain body of each common tribe (no vanilla Dragon or Undead exists), a plain Battlecry and a plain Deathrattle.
    internal static readonly string[] Minions = { Yeti, "CS2_120", "CS2_168", "GVG_044", "CS2_118", "GVG_070", "CS2_065", "EX1_015", "EX1_096" };
    internal static IEnumerable<TriggerKind> Kinds(EngineCard card)
    {
        var kinds = new SortedSet<TriggerKind>();
        foreach (var t in Flatten(card.Power?.Trigger)) { var k = KindOf(t); if (k.HasValue) kinds.Add(k.Value); }
        return kinds;
    }
    internal static IEnumerable<Trigger> Flatten(Trigger t)
    {
        var multi = t as MultiTrigger;
        if (multi != null) { foreach (var inner in multi) foreach (var x in Flatten(inner)) yield return x; }
        else if (t != null) yield return t;
    }
    // Only board triggers, from the engine's own definition. Anything else in the card set stays out of the table: see npc-ai-tuning.md.
    internal static TriggerKind? KindOf(Trigger t)
    {
        if (t.TriggerActivation != TriggerActivation.PLAY) return null;
        var s = t.TriggerSource;
        switch (t.Type) {
            case TriggerType.AFTER_ATTACK: case TriggerType.ATTACK:
                return s == TriggerSource.HERO ? TriggerKind.HeroAttack : s == TriggerSource.SELF ? TriggerKind.SelfAttack : (TriggerKind?)null;
            case TriggerType.AFTER_CAST: case TriggerType.CAST_SPELL:
                return s == TriggerSource.FRIENDLY ? TriggerKind.OwnSpell : (TriggerKind?)null;
            case TriggerType.AFTER_PLAY_MINION: case TriggerType.PLAY_MINION: case TriggerType.AFTER_SUMMON: case TriggerType.SUMMON:
                return s == TriggerSource.FRIENDLY || s == TriggerSource.MINIONS || s == TriggerSource.MINIONS_EXCEPT_SELF ? TriggerKind.OwnMinion : (TriggerKind?)null;
            case TriggerType.AFTER_PLAY_CARD: case TriggerType.PLAY_CARD:
                return s == TriggerSource.FRIENDLY ? TriggerKind.OwnCard : (TriggerKind?)null;
            case TriggerType.INSPIRE:
                return s == TriggerSource.FRIENDLY ? TriggerKind.OwnHeroPower : (TriggerKind?)null;
            case TriggerType.TAKE_DAMAGE:
                return s == TriggerSource.SELF ? TriggerKind.SelfDamage : (TriggerKind?)null;
            case TriggerType.DRAW:
                return s == TriggerSource.FRIENDLY ? TriggerKind.OwnDraw : (TriggerKind?)null;
            default: return null;
        }
    }
    private static int EventCount(TriggerKind kind)
    {
        switch (kind) {
            case TriggerKind.HeroAttack: return 2;
            case TriggerKind.SelfAttack: return 3;
            case TriggerKind.OwnSpell: return Spells.Length;
            case TriggerKind.OwnMinion: return Minions.Length;
            case TriggerKind.OwnCard: return 2;
            default: return 1;
        }
    }
    private static void Prepare(EngineGame g, TriggerKind kind, EngineMinion m)
    {
        var own = g.Player1;
        if (kind == TriggerKind.HeroAttack) {
            own.Hero.AddWeapon((SabberStoneCore.Model.Entities.Weapon)EngineEntity.FromCard(own, EngineCards.FromId(Axe)));
            own.Hero.IsExhausted = false;
        }
        // A future round's swing, so a Rush minion is past its first turn.
        if (kind == TriggerKind.SelfAttack) { m.IsExhausted = false; m[GameTag.JUST_PLAYED] = 0; m.AttackableByRush = false; }
    }
    private static bool Play(EngineGame g, string id)
    {
        var own = g.Player1;
        var held = EngineEntity.FromCard(own, EngineCards.FromId(id), zone: own.HandZone);
        var option = own.Options().FirstOrDefault(o => o.Source == held && (o.Target == null || o.Target == own.Opponent.Hero));
        return option != null && g.Process(option);
    }
    // Each fork takes the same event. False when the event itself is impossible here: it leaves the mix.
    private static bool Fire(EngineGame g, TriggerKind kind, int index, EngineMinion m)
    {
        var own = g.Player1; var enemy = g.Player2;
        var targets = enemy.BoardZone.ToArray();
        switch (kind) {
            case TriggerKind.HeroAttack: return g.Process(HeroAttackTask.Any(own, index == 0 ? enemy.Hero : (SabberStoneCore.Model.Entities.ICharacter)targets[0]));
            case TriggerKind.SelfAttack: return g.Process(MinionAttackTask.Any(own, m, index == 0 ? enemy.Hero : (SabberStoneCore.Model.Entities.ICharacter)targets[index - 1]));
            case TriggerKind.OwnSpell: return Play(g, Spells[index]);
            case TriggerKind.OwnMinion: return Play(g, Minions[index]);
            case TriggerKind.OwnCard: return Play(g, index == 0 ? Yeti : Spells[2]);
            case TriggerKind.OwnHeroPower: return g.Process(HeroPowerTask.Any(own));
            case TriggerKind.SelfDamage: m.TakeDamage(enemy.Hero, 1); g.DeathProcessingAndAuraUpdate(); return true;
            case TriggerKind.OwnDraw: SabberStoneCore.Actions.Generic.Draw(own); g.DeathProcessingAndAuraUpdate(); return true;
            default: return false;
        }
    }
    internal static TriggerMeasures.Row Measure(string id, TriggerKind kind)
    {
        var card = EngineCards.FromId(id);
        var row = new TriggerMeasures.Row { Id = id, Kind = kind };
        var total = default(DeathVector); int events = EventCount(kind), possible = 0, fired = 0; bool gone = false;
        try {
            for (int e = 0; e < events; e++) {
                bool any = false, ran = false; var sum = default(DeathVector);
                for (int seed = 0; seed < Seeds; seed++) {
                    var live = DeathProbe.Template().CloneExact();
                    var m = (EngineMinion)EngineEntity.FromCard(live.Player1, card, zone: live.Player1.BoardZone, zonePos: 1);
                    live.SummonedMinions.Clear();
                    Prepare(live, kind, m);
                    var quiet = live.CloneExact();
                    live.SetRandomSeed(2000 + seed); quiet.SetRandomSeed(2000 + seed);
                    var still = (EngineMinion)quiet.IdEntityDic[m.Id];
                    still.ActivatedTrigger?.Remove(); still.ActivatedTrigger = null;
                    if (!Fire(live, kind, e, m) | !Fire(quiet, kind, e, still)) continue;
                    ran = true;
                    DeathProbe.Resolve(live); DeathProbe.Resolve(quiet);
                    var delta = DeathProbe.Read(live).Plus(DeathProbe.Read(quiet), -1);
                    // A trigger that removes its own source does not recur (RecurringValue's rule).
                    if (quiet.Player1.BoardZone.Any(x => x.Id == m.Id) && !live.Player1.BoardZone.Any(x => x.Id == m.Id)) gone = true;
                    if (delta.Any) { any = true; sum = sum.Plus(delta); }
                }
                if (ran) possible++;
                if (any) { fired++; total = total.Plus(sum, 1.0 / Seeds); }
            }
        }
        catch (Exception error) when (!(error is OutOfMemoryException)) { row.Status = TriggerMeasures.Status.Error; return row; }
        row.Share = possible == 0 ? 0 : fired / (double)possible;
        if (fired > 0) row.Fire = default(DeathVector).Plus(total, 1.0 / fired);
        row.Fire.DeckCards = Math.Max(0, row.Fire.DeckCards + row.Fire.Cards);
        row.Status = gone ? TriggerMeasures.Status.SelfRemoving : fired == 0 || !row.Fire.Any ? TriggerMeasures.Status.NoEffect
            : row.Share < TriggerMeasures.MinShare ? TriggerMeasures.Status.Partial : TriggerMeasures.Status.Priced;
        return row;
    }
    // Every implemented minion with a board trigger of a measured kind.
    internal static IEnumerable<TriggerMeasures.Row> Generate() => EngineCards.All
        .Where(c => c.Id != null && c.Type == CardType.MINION && c.Implemented).OrderBy(c => c.Id, StringComparer.Ordinal)
        .GroupBy(c => c.Id, StringComparer.Ordinal).Select(g => g.First())
        .SelectMany(c => Kinds(c).Select(k => Measure(c.Id, k)));
    // Hero powers that arm the hero when used on the canonical board.
    internal static IReadOnlyList<string> ArmingHeroPowers()
    {
        var found = new List<string>();
        foreach (var card in EngineCards.All.Where(c => c.Id != null && c.Type == CardType.HERO_POWER && c.Implemented)
            .OrderBy(c => c.Id, StringComparer.Ordinal).GroupBy(c => c.Id, StringComparer.Ordinal).Select(g => g.First())) {
            try {
                var g = DeathProbe.Template().CloneExact(); var own = g.Player1;
                own.Hero.HeroPower = (SabberStoneCore.Model.Entities.HeroPower)EngineEntity.FromCard(own, card);
                var option = own.Options().FirstOrDefault(o => o.PlayerTaskType == PlayerTaskType.HERO_POWER && (o.Target == null || o.Target == own.Opponent.Hero));
                if (option == null || !g.Process(option)) continue;
                DeathProbe.Resolve(g);
                if (own.Hero.AttackDamage > 0) found.Add(card.Id);
            }
            catch (Exception error) when (!(error is OutOfMemoryException)) { }
        }
        return found;
    }
}
#endif
