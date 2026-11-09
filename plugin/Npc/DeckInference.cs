using System;
using System.Collections.Generic;
using System.Linq;
using SabberStoneCore.Enums;

// One deck of the public NPC population (a list the world could field), for fair deck inference.
public sealed class PopulationDeck
{
    public string Id { get; }
    public CardClass Class { get; }
    public IReadOnlyList<string> Cards { get; } // one entry per copy
    public PopulationDeck(string id, CardClass heroClass, IEnumerable<string> cards) { Id = id; Class = heroClass; Cards = GoldenData.Copy(cards); }
}

// AI-leap research (BrainOptions.ReplyPrior = Inferred). Fair Bayesian deck inference, the way a ladder player reads an
// opponent: which population lists of this class explain the cards the opponent has PUBLICLY played from its deck?
//   P(d | plays) ∝ Π_card [(1 - e) * copies_d(card) / 30 + e / 30]    (e: off-list chance; the human's list is not in the population)
// The reply prior is then (1 - floor) * Σ_d P(d) * copies_d + floor * the class population table (CardFrequencies).
// Inputs: the opponent's class, its public plays (GoldenPublicInputs.OpponentPlayed: started in deck, definition and
// origin public) and the public population (NpcWorldHooks.Population, else the CardFrequencies decks, plus StarterDecks).
// Never its hand or list.
public static class DeckInference
{
    internal const double OffList = .1;
    // Owner 2026-10-28 (#63): the 11 Core starter loaners (#57) are every new player's default lists, so they are public
    // deck knowledge like a library list. Inference candidates only: never an NPC deck, NPC deck pool, matchmaking or
    // CardFrequencies class-table input. A starter is scored by revealed cards like any other list and never reads which
    // deck the player actually selected or saved.
    // Flat prior: each starter weighs exactly one library list (3-6 per class, so 14-25 % before any card is seen).
    // Starters share few cards with meta lists, so one or two revealed starter-only cards decide it either way; a
    // rank-tied prior would only move the first turns and the inference has no rank input (NPC library lists are not
    // band-weighted either). docs/agent-workflows/npc-ai-tuning.md has the measurement.
    internal static IEnumerable<PopulationDeck> StarterDecks() =>
        LocalDecks.PlayerCoreStarterDecks.Select(d => new PopulationDeck(d.Id, LocalDecks.HeroClass(d.Hero), d.CardIds));
    private static readonly Dictionary<string, IReadOnlyList<CardFrequency>> Cache = new Dictionary<string, IReadOnlyList<CardFrequency>>(StringComparer.Ordinal);
    private static Func<IReadOnlyList<PopulationDeck>> cachedSource;
    private static IReadOnlyList<PopulationDeck> cachedDecks;
    // Telemetry for logs and test control, written only on this path: a class-prior search never comes here.
    private static long queries, reads;
    private static string lastRead;
    internal static long Queries { get { lock (Cache) return queries; } }
    internal static long Reads { get { lock (Cache) return reads; } }
    internal static string LastRead { get { lock (Cache) return lastRead; } }

    // A match begins (NpcWorldRuntime.Begin, client only): the read and log start over. A table is a pure function of its key, so
    // emptying the cache changes no decision; building the lists here keeps their first use out of the NPC's first search.
    internal static void NewMatch(bool inferred)
    {
        lock (Cache) { Cache.Clear(); lastRead = null; if (inferred) Decks(); }
    }

    private static IReadOnlyList<PopulationDeck> Decks()
    {
        var source = NpcWorldHooks.Population;
        if (cachedDecks != null && source == cachedSource) return cachedDecks;
        IReadOnlyList<PopulationDeck> decks = null;
        try { decks = source?.Invoke(); } catch (Exception) { decks = null; }
        if (decks == null) decks = GoldenData.Copy(CardFrequencies.PopulationDecks());
        decks = WithStarters(decks);
        cachedSource = source; cachedDecks = decks; Cache.Clear();
        return decks;
    }

    // The candidate lists: the installed population, then each Core starter whose id it does not already hold.
    internal static IReadOnlyList<PopulationDeck> WithStarters(IReadOnlyList<PopulationDeck> population)
    {
        var ids = new HashSet<string>(population.Select(d => d.Id), StringComparer.Ordinal);
        return GoldenData.Copy(population.Concat(StarterDecks().Where(d => !ids.Contains(d.Id))));
    }
    // The current candidate set (research probes: hs ai infer-probe).
    internal static IReadOnlyList<PopulationDeck> Candidates() { lock (Cache) return Decks(); }

    public static IReadOnlyList<KeyValuePair<string, double>> Posterior(CardClass heroClass, IEnumerable<string> played,
        IEnumerable<string> excluded = null)
    {
        lock (Cache) return PosteriorCore(Decks(), heroClass, played, excluded);
    }
    // Same posterior over an explicit candidate set (research probes compare pools and priors offline).
    internal static IReadOnlyList<KeyValuePair<string, double>> Posterior(IReadOnlyList<PopulationDeck> population, CardClass heroClass,
        IEnumerable<string> played, IEnumerable<string> excluded = null, Func<PopulationDeck, double> prior = null) =>
        PosteriorCore(population, heroClass, played, excluded, prior);
    private static IReadOnlyList<KeyValuePair<string, double>> PosteriorCore(IReadOnlyList<PopulationDeck> population, CardClass heroClass,
        IEnumerable<string> played, IEnumerable<string> excluded, Func<PopulationDeck, double> prior = null)
    {
        var skip = new HashSet<string>(excluded ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        var decks = population.Where(d => d.Class == heroClass && !skip.Contains(d.Id)).OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();
        if (decks.Length == 0) return new KeyValuePair<string, double>[0];
        var seen = (played ?? Enumerable.Empty<string>()).GroupBy(x => x, StringComparer.Ordinal).ToArray();
        var logs = decks.Select(d => {
            double log = prior == null ? 0 : Math.Log(prior(d));
            foreach (var card in seen) {
                int copies = d.Cards.Count(c => c == card.Key);
                // Each further copy needs the list to hold it: copies beyond the list's count are off-list plays.
                for (int i = 0; i < card.Count(); i++) log += Math.Log((1 - OffList) * Math.Max(0, copies - i) / 30.0 + OffList / 30.0);
            }
            return log;
        }).ToArray();
        double top = logs.Max(); var weights = logs.Select(l => Math.Exp(l - top)).ToArray(); double sum = weights.Sum();
        return decks.Select((d, i) => new KeyValuePair<string, double>(d.Id, weights[i] / sum)).ToArray();
    }

    // The inferred reply table (same shape as CardFrequencies.For): cached per class, play multiset, floor and exclusions.
    public static IReadOnlyList<CardFrequency> Table(CardClass heroClass, IEnumerable<string> played, BrainOptions brain)
    {
        var plays = (played ?? Enumerable.Empty<string>()).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var excluded = brain?.ExcludedDecks ?? new string[0];
        double floor = brain?.InferenceFloor ?? .15;
        lock (Cache) {
            var decks = Decks();
            string key = (int)heroClass + "|" + floor.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "|"
                + string.Join(",", excluded) + "|" + string.Join(",", plays);
            IReadOnlyList<CardFrequency> table;
            queries++;
            if (Cache.TryGetValue(key, out table)) return table;
            var prior = CardFrequencies.ForOrEmpty(heroClass);
            var posterior = PosteriorCore(decks, heroClass, plays, excluded);
            var top = posterior.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).FirstOrDefault();
            reads++; lastRead = "class=" + heroClass + " plays=" + plays.Length + " top=" + top.Key + " p=" + top.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            var byId = decks.ToDictionary(d => d.Id, StringComparer.Ordinal);
            var expected = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var p in posterior) foreach (string card in byId[p.Key].Cards) {
                double old; expected.TryGetValue(card, out old); expected[card] = old + p.Value;
            }
            double inferred = posterior.Count == 0 ? 0 : 1 - floor;
            var rows = new List<CardFrequency>();
            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in prior) {
                double copies; expected.TryGetValue(f.CardId, out copies);
                rows.Add(f.WithWeight((1 - inferred) * f.Weight + inferred * copies)); known.Add(f.CardId);
            }
            // A population card outside the class table (e.g. a non-Standard tech card) still joins the pool.
            foreach (var pair in expected.Where(p => !known.Contains(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal))
                rows.Add(new CardFrequency(pair.Key, inferred * pair.Value));
            table = GoldenData.Copy(rows.Where(r => r.Weight > 0));
            if (Cache.Count > 256) Cache.Clear();
            Cache[key] = table;
            return table;
        }
    }
}
