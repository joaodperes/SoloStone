using EngineGame = SabberStoneCore.Model.Game;
using EngineMinion = SabberStoneCore.Model.Entities.Minion;
using EngineEntity = SabberStoneCore.Model.Entities.Entity;
using EngineState = SabberStoneCore.Enums.State;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using SabberStoneCore.Config;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using SabberStoneCore.Model.Entities;
using SabberStoneCore.Tasks.PlayerTasks;
using SabberStoneCore.Triggers;

// recurring-value: what one round of a board minion's printed turn trigger (TURN_END / TURN_START, own turn or
// either turn) does, in public primitives from the minion owner's frame. Measured, never parsed from text: a canonical
// probe game holds the minion plus its owner's public board peers (aura and count scaling such as Hideous Husk), a
// damaged hero on each side and one sturdy enemy minion; two forks play one full round of END_TURNs, one with the
// minion's trigger removed, and the difference is the effect. Pure function of (card id, peer card ids): cached,
// deterministic, no hidden information. Enchantment-granted triggers are not seen (printed definition only).
public struct FairRecurringValue
{
    public double OwnLife, EnemyLife, OwnBodies, OwnAttack, OwnHealth, EnemyBodies, EnemyAttack, EnemyHealth, OwnCards, EnemyCards;
    public bool Any => OwnLife != 0 || EnemyLife != 0 || OwnBodies != 0 || OwnAttack != 0 || OwnHealth != 0
        || EnemyBodies != 0 || EnemyAttack != 0 || EnemyHealth != 0 || OwnCards != 0 || EnemyCards != 0;
    public override string ToString() => string.Format(System.Globalization.CultureInfo.InvariantCulture,
        "life {0}/{1} own board {2}/{3}/{4} enemy board {5}/{6}/{7} cards {8}/{9}", OwnLife, EnemyLife, OwnBodies, OwnAttack, OwnHealth,
        EnemyBodies, EnemyAttack, EnemyHealth, OwnCards, EnemyCards);
}

internal static class RecurringValue
{
    // Future rounds credited beyond the one the search itself simulates (own END_TURN endpoint, reply END_TURN).
    // An unanswered engine keeps working; at a 2/3 chance per round to survive, p/(1-p) = 2 further rounds.
    internal const double Horizon = 2;
    private const int MaxPeers = 6;
    private static readonly ConcurrentDictionary<string, bool> printed = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, FairRecurringValue> measured = new ConcurrentDictionary<string, FairRecurringValue>(StringComparer.Ordinal);
    private static readonly Func<string, bool> describe = id => {
        try { return Turn(Cards.FromId(id).Power?.Trigger); } catch (Exception) { return false; }
    };
    private static readonly Func<string, FairRecurringValue> measure = Measure;
    // Cost telemetry (Interlocked, read by the harness): probes run, total and maximum probe ticks.
    internal static int Probes;
    private static long probeTicks, maxProbeTicks;
    internal static double ProbeMs => probeTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    internal static double MaxProbeMs => maxProbeTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    internal static int CacheSize => measured.Count;
    internal static void ResetForMeasurement() { measured.Clear(); Probes = 0; probeTicks = maxProbeTicks = 0; }
    private static bool Turn(Trigger t)
    {
        var multi = t as MultiTrigger;
        if (multi != null) return multi.Any(Turn);
        return t != null && (t.Type == TriggerType.TURN_END || t.Type == TriggerType.TURN_START);
    }
    internal static bool HasPrintedTurnTrigger(string cardId) => cardId != null && printed.GetOrAdd(cardId, describe);
    // Public eligibility: a silenced, dormant or untouchable copy has no working printed trigger.
    internal static bool Eligible(FairCard c) => !c.Unknown && c.Type == CardType.MINION && !c.Untouchable
        && DeckPlanEvaluator.Tag(c, GameTag.SILENCED) == 0 && DeckPlanEvaluator.Tag(c, GameTag.DORMANT) == 0
        && HasPrintedTurnTrigger(c.CardId);
    internal static string Key(FairCard source, IReadOnlyList<FairCard> board)
    {
        var peers = new List<string>();
        for (int i = 0; i < board.Count; i++)
            if (board[i].EntityId != source.EntityId && !board[i].Unknown && board[i].Type == CardType.MINION
                && DeckPlanEvaluator.Tag(board[i], GameTag.SILENCED) == 0) peers.Add(board[i].CardId);
        peers.Sort(StringComparer.Ordinal);
        if (peers.Count > MaxPeers) peers.RemoveRange(MaxPeers, peers.Count - MaxPeers);
        return source.CardId + "|" + string.Join(",", peers);
    }
    // Peer combinations make keys numerous (about 900 per master game with engine decks, 2026-11-01); a long harness or
    // live process clears the memo past this bound. Values are a pure function of the key, so clearing never changes them.
    internal const int MaxEntries = 32768;
    internal static FairRecurringValue For(FairCard source, IReadOnlyList<FairCard> board) => ForKey(Key(source, board));
    internal static FairRecurringValue ForKey(string key)
    {
        FairRecurringValue value;
        if (measured.TryGetValue(key, out value)) return value;
        if (measured.Count >= MaxEntries) measured.Clear();
        return measured.GetOrAdd(key, measure);
    }

#if ENGINE_SOURCE
    [ThreadStatic] private static EngineGame template;
    internal static EngineGame Template()
    {
        if (template != null) return template;
        var deck = Enumerable.Range(0, 30).Select(i => Cards.FromId("CS2_120")).ToList();
        var g = new EngineGame(new GameConfig { Player1HeroClass = CardClass.WARRIOR, Player2HeroClass = CardClass.WARRIOR,
            Player1Deck = deck, Player2Deck = deck.ToList(), StartPlayer = 1, Shuffle = false,
            SkipMulligan = true, History = false, Logging = false, RandomSeed = 20261101 });
        g.StartGame();
        foreach (var p in new[] { g.Player1, g.Player2 }) {
            foreach (var c in p.HandZone.ToArray()) { p.HandZone.Remove(c); p.SetasideZone.Add(c); }
            p.BaseMana = 10; p.UsedMana = 0; p.Hero.Damage = 10;
        }
        EngineEntity.FromCard(g.Player2, Cards.FromId("CS2_200"), zone: g.Player2.BoardZone);
        g.SummonedMinions.Clear();
        return template = g;
    }
    internal struct Totals { internal double OwnLife, EnemyLife, OwnBodies, OwnAttack, OwnHealth, EnemyBodies, EnemyAttack, EnemyHealth, OwnCards, EnemyCards; }
    internal static Totals Read(EngineGame g)
    {
        var own = g.Player1; var enemy = g.Player2;
        return new Totals { OwnLife = own.Hero.Health + own.Hero.Armor, EnemyLife = enemy.Hero.Health + enemy.Hero.Armor,
            OwnBodies = own.BoardZone.Count, OwnAttack = own.BoardZone.Sum(m => (double)m.AttackDamage), OwnHealth = own.BoardZone.Sum(m => (double)m.Health),
            EnemyBodies = enemy.BoardZone.Count, EnemyAttack = enemy.BoardZone.Sum(m => (double)m.AttackDamage), EnemyHealth = enemy.BoardZone.Sum(m => (double)m.Health),
            OwnCards = own.HandZone.Count, EnemyCards = enemy.HandZone.Count };
    }
    private static bool Round(EngineGame g)
    {
        if (!g.Process(EndTurnTask.Any(g.Player1))) return false;
        if (g.State == EngineState.COMPLETE) return false;
        return g.Process(EndTurnTask.Any(g.Player2)) && g.State != EngineState.COMPLETE;
    }
#endif
    private static FairRecurringValue Measure(string key)
    {
        System.Threading.Interlocked.Increment(ref Probes);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        try { return MeasureCore(key); }
        finally {
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - start;
            System.Threading.Interlocked.Add(ref probeTicks, ticks);
            long max;
            while (ticks > (max = System.Threading.Interlocked.Read(ref maxProbeTicks))
                && System.Threading.Interlocked.CompareExchange(ref maxProbeTicks, ticks, max) != max) { }
        }
    }
    private static FairRecurringValue MeasureCore(string key)
    {
#if ENGINE_SOURCE
        try {
            var parts = key.Split('|');
            var with = Template().CloneExact();
            foreach (var peer in parts[1].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                EngineEntity.FromCard(with.Player1, Cards.FromId(peer), zone: with.Player1.BoardZone);
            var source = (EngineMinion)EngineEntity.FromCard(with.Player1, Cards.FromId(parts[0]), zone: with.Player1.BoardZone);
            with.SummonedMinions.Clear();
            var without = with.CloneExact();
            var quiet = (EngineMinion)without.IdEntityDic[source.Id];
            quiet.ActivatedTrigger?.Remove(); quiet.ActivatedTrigger = null;
            if (!Round(with) || !Round(without)) return default(FairRecurringValue);
            // A trigger that removes its own source (self-destruct, transform, return) does not recur.
            if (!with.Player1.BoardZone.Any(m => m.Id == source.Id)) return default(FairRecurringValue);
            var a = Read(with); var b = Read(without);
            return new FairRecurringValue { OwnLife = a.OwnLife - b.OwnLife, EnemyLife = a.EnemyLife - b.EnemyLife,
                OwnBodies = a.OwnBodies - b.OwnBodies, OwnAttack = a.OwnAttack - b.OwnAttack, OwnHealth = a.OwnHealth - b.OwnHealth,
                EnemyBodies = a.EnemyBodies - b.EnemyBodies, EnemyAttack = a.EnemyAttack - b.EnemyAttack, EnemyHealth = a.EnemyHealth - b.EnemyHealth,
                OwnCards = a.OwnCards - b.OwnCards, EnemyCards = a.EnemyCards - b.EnemyCards };
        }
        catch (Exception error) when (!(error is OutOfMemoryException)) { return default(FairRecurringValue); }
#else
        return default(FairRecurringValue);
#endif
    }
}
