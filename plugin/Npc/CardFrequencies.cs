using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;

public sealed class CardFrequency
{
    public string CardId { get; }
    public double Weight { get; }
    public int Copies { get; }
    public bool Threat { get; }
    public bool AreaThreat { get; }
    public CardFrequency(string cardId, double weight)
    {
        CardId = cardId; Weight = weight;
        var card = Cards.FromId(cardId); Copies = card.Rarity == Rarity.LEGENDARY ? 1 : 2;
        string text = (card.Text ?? "").Replace('_', ' ').ToLowerInvariant();
        AreaThreat = IsAreaThreat(text);
        Threat = card.Type == CardType.SPELL && (AreaThreat || text.Contains("damage") && text.Contains("minion")
            || text.Contains("destroy") || text.Contains("transform"));
    }
    private CardFrequency(CardFrequency source, double weight)
    { CardId = source.CardId; Weight = weight; Copies = source.Copies; Threat = source.Threat; AreaThreat = source.AreaThreat; }
    internal CardFrequency WithWeight(double weight) => new CardFrequency(this, weight);
    internal static bool IsAreaThreat(string text) => Regex.IsMatch(text, @"\ball\b") && Regex.IsMatch(text, @"\b(minions?|enemies|characters)\b")
        && (Regex.IsMatch(text, @"\bdamage\b") || Regex.IsMatch(text, @"\bdestroy\b"));
}
public sealed class HandSample
{
    public IReadOnlyList<string> Cards { get; }
    public double Weight { get; }
    public HandSample(IEnumerable<string> cards, double weight) { Cards = GoldenData.Copy(cards); Weight = weight; }
}

public static class CardFrequencies
{
    // One deterministic, immutable class prior per process; no opponent list is an input.
    private static readonly IReadOnlyDictionary<CardClass, IReadOnlyList<CardFrequency>> Tables = Build();
    private static LocalDeck[] SourceDecks() => LocalDecks.All.Concat(LocalDecks.TestLoaners.Where(d => d.Id.StartsWith("showcase-", StringComparison.Ordinal)))
            .Concat(LocalDecks.TestOpponents.Where(d => d.Id.StartsWith("showcase-", StringComparison.Ordinal)))
            .Concat(LocalDecks.NpcDecks).OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();
    // The class-table decks as an inference population (DeckInference fallback when no world population is installed).
    internal static IEnumerable<PopulationDeck> PopulationDecks() =>
        SourceDecks().Select(d => new PopulationDeck(d.Id, LocalDecks.HeroClass(d.Hero), d.CardIds));
    private static IReadOnlyDictionary<CardClass, IReadOnlyList<CardFrequency>> Build()
    {
        var result = new Dictionary<CardClass, IReadOnlyList<CardFrequency>>();
        var decks = SourceDecks();
        foreach (var heroClass in decks.Select(d => LocalDecks.HeroClass(d.Hero)).Distinct().OrderBy(c => (int)c)) {
            var weights = new SortedDictionary<string, double>(StringComparer.Ordinal);
#if ENGINE_SOURCE
            foreach (string id in SabberStoneCore.CardSets.Client.StandardSets.CollectibleIds.OrderBy(x => x, StringComparer.Ordinal)) {
                var card = Cards.FromId(id);
                if (card.Implemented && LocalDecks.IsAllowedInDeck(card, heroClass)) weights[id] = .001;
            }
#endif
            var known = decks.Where(d => LocalDecks.HeroClass(d.Hero) == heroClass).ToArray();
            foreach (var deck in known) foreach (string id in deck.CardIds) {
                double old; weights.TryGetValue(id, out old); weights[id] = old + 1.0 / known.Length;
            }
            result[heroClass] = GoldenData.Copy(weights.Select(x => new CardFrequency(x.Key, x.Value)));
        }
        return result;
    }
    internal static IReadOnlyList<CardFrequency> ForOrEmpty(CardClass heroClass)
    {
        IReadOnlyList<CardFrequency> result;
        return Tables.TryGetValue(heroClass, out result) ? result : new CardFrequency[0];
    }
    public static IReadOnlyList<CardFrequency> For(CardClass heroClass)
    {
        IReadOnlyList<CardFrequency> result;
        if (!Tables.TryGetValue(heroClass, out result)) throw new ArgumentException("No class frequency pool: " + heroClass);
        return result;
    }
    // Castable area clears of one class prior at a mana limit: (damage, expected copies in a 30-card list).
    // Destroy-all clears carry int.MaxValue damage. Immutable per (class, mana); population data only.
    private static readonly Dictionary<long, KeyValuePair<int, double>[]> ClearCache = new Dictionary<long, KeyValuePair<int, double>[]>();
    internal static IReadOnlyList<KeyValuePair<int, double>> Clears(CardClass heroClass, int mana)
    {
        long key = (long)heroClass * 64 + Math.Max(0, Math.Min(10, mana));
        lock (ClearCache) {
            KeyValuePair<int, double>[] cached;
            if (ClearCache.TryGetValue(key, out cached)) return cached;
            IReadOnlyList<CardFrequency> pool;
            if (!Tables.TryGetValue(heroClass, out pool)) pool = new CardFrequency[0];
            double total = pool.Sum(f => f.Weight);
            cached = pool.Where(f => f.Threat && f.AreaThreat && Cards.FromId(f.CardId).Cost <= mana)
                .Select(f => new KeyValuePair<int, double>(ClearDamage(f.CardId), Math.Min(f.Copies, 30 * f.Weight / total)))
                .Where(c => c.Key > 0 && c.Value >= .01).ToArray();
            ClearCache[key] = cached;
            return cached;
        }
    }
    // Same over any prior table (DeckInference.Table); cached per table instance and mana.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<CardFrequency>, KeyValuePair<int, double>[][]> TableClears =
        new System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<CardFrequency>, KeyValuePair<int, double>[][]>();
    internal static IReadOnlyList<KeyValuePair<int, double>> Clears(IReadOnlyList<CardFrequency> pool, int mana)
    {
        var byMana = TableClears.GetValue(pool, _ => new KeyValuePair<int, double>[11][]);
        int m = Math.Max(0, Math.Min(10, mana));
        lock (byMana) {
            if (byMana[m] != null) return byMana[m];
            double total = pool.Sum(f => f.Weight);
            return byMana[m] = pool.Where(f => f.Threat && f.AreaThreat && Cards.FromId(f.CardId).Cost <= m)
                .Select(f => new KeyValuePair<int, double>(ClearDamage(f.CardId), Math.Min(f.Copies, 30 * f.Weight / total)))
                .Where(c => c.Key > 0 && c.Value >= .01).ToArray();
        }
    }
    // Printed area damage ("$5 damage"), or int.MaxValue for a destroy clear; 0 when no amount is printed.
    internal static int ClearDamage(string cardId)
    {
        string text = (Cards.FromId(cardId).Text ?? "").Replace('_', ' ').Replace("$", "").ToLowerInvariant();
        var amount = Regex.Match(text, @"\b(\d+) damage\b");
        if (amount.Success) return int.Parse(amount.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return Regex.IsMatch(text, @"\bdestroy\b") ? int.MaxValue : 0;
    }
    // Same printed-damage convention as Clears, restricted to immediate damage that can reach face.
    internal static int FaceDamage(string cardId)
    {
        var card = Cards.FromId(cardId);
        string text = Regex.Replace(card.Text ?? "", @"<[^>]*>|\[x\]|[$#@]", "").Replace('_', ' ').ToLowerInvariant();
        if (card.IsSecret || Regex.IsMatch(text, @"\b(deathrattle|after|whenever|when|next)\b|at the (start|end)")) return 0;
        if (!(text.Contains("enemy hero") || text.Contains("your opponent") || text.Contains("to all enemies")
            || (card.Type == CardType.SPELL || card.Type == CardType.HERO_POWER) && card.RequiresTarget && !text.Contains("minion"))) return 0;
        var amount = Regex.Match(text, @"\bdeal (\d+) damage\b");
        return amount.Success ? int.Parse(amount.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
    }
    public static IReadOnlyList<HandSample> Sample(CardClass heroClass, int handSize, int k, int mana, int seed,
        IEnumerable<string> publiclyPlayed = null) => Sample(For(heroClass), handSize, k, mana, seed, publiclyPlayed);
    // Same sampler over any prior table (DeckInference.Table for the inferred reply prior).
    public static IReadOnlyList<HandSample> Sample(IReadOnlyList<CardFrequency> table, int handSize, int k, int mana, int seed,
        IEnumerable<string> publiclyPlayed = null)
    {
        if (handSize < 0 || handSize > 10 || k <= 0) throw new ArgumentOutOfRangeException();
        var played = (publiclyPlayed ?? Enumerable.Empty<string>()).GroupBy(x => x, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var pool = table.Where(f => !played.ContainsKey(f.CardId) || played[f.CardId] < f.Copies).ToArray();
        var available = pool.Select(f => f.Copies - (played.ContainsKey(f.CardId) ? played[f.CardId] : 0)).ToArray();
        var random = new GoldenRandom(seed); var samples = new List<HandSample>();
        double residual = 1; var excluded = new HashSet<string>(StringComparer.Ordinal);
        double total = pool.Sum(f => f.Weight);
        var castable = pool.Where(f => f.Threat && Cards.FromId(f.CardId).Cost <= mana)
            .OrderByDescending(f => f.Weight).ThenBy(f => f.CardId, StringComparer.Ordinal).ToArray();
        var threats = castable.Where(f => f.AreaThreat).Take(1).Concat(castable).Distinct()
            .Take(Math.Min(2, k - 1)).ToArray();
        foreach (var threat in threats) {
            int index = Array.IndexOf(pool, threat);
            // Hypergeometric inclusion in a 30-card prior, with fractional expected copies from deck frequency.
            double copies = Math.Min(available[index], 30 * threat.Weight / total);
            int population = Math.Max(handSize, 30 - played.Values.Sum());
            double miss = 1;
            for (int i = 0; i < handSize; i++) miss *= Math.Max(0, (population - copies - i) / (population - i));
            double weight = residual * (1 - miss);
            if (handSize > 0 && weight > 0) samples.Add(new HandSample(Draw(pool, available, handSize, threat.CardId, excluded, random), weight));
            residual -= weight; excluded.Add(threat.CardId);
        }
        int remaining = k - samples.Count;
        for (int i = 0; i < remaining; i++)
            samples.Add(new HandSample(Draw(pool, available, handSize, null, excluded, random), residual / remaining));
        return GoldenData.Copy(samples);
    }
    private static string[] Draw(CardFrequency[] pool, int[] available, int count, string forced,
        HashSet<string> excluded, GoldenRandom random)
    {
        var left = (int[])available.Clone(); var hand = new List<string>();
        if (forced != null) { hand.Add(forced); left[Array.FindIndex(pool, f => f.CardId == forced)]--; }
        while (hand.Count < count) {
            double total = 0;
            for (int i = 0; i < pool.Length; i++) if (!excluded.Contains(pool[i].CardId)) total += pool[i].Weight * left[i];
            if (total <= 0) throw new InvalidOperationException("Frequency pool cannot fill the hand.");
            double pick = random.Next() * total;
            for (int i = 0; i < pool.Length; i++) {
                if (excluded.Contains(pool[i].CardId) || left[i] == 0) continue;
                pick -= pool[i].Weight * left[i];
                if (pick <= 0) { hand.Add(pool[i].CardId); left[i]--; break; }
            }
        }
        return hand.ToArray();
    }
}
// SplitMix64: artificial seeds only, no runtime Random/GetHashCode or engine RNG state.
internal sealed class GoldenRandom
{
    private ulong state;
    internal GoldenRandom(int seed) { state = unchecked((ulong)(long)seed); }
    internal double Next()
    {
        unchecked {
            ulong z = (state += 0x9E3779B97F4A7C15UL);
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return ((z ^ (z >> 31)) >> 11) * (1.0 / 9007199254740992.0);
        }
    }
}
