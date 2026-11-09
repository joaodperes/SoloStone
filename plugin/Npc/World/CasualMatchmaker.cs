using System;
using System.Collections.Generic;
using System.Linq;

// #37 Standard Casual. Pure + deterministic per (profileSeed, casual match index). Inputs are the player's hidden Casual
// rating and Casual history only: never the ladder state, the player's deck, hand or collection (no covert counter-picking).
// Casual never advances the ladder queue: NPC rating drift is read at the ranked match index, and the history it checks
// holds Casual encounters only.
public sealed class CasualRequest
{
    public long ProfileSeed { get; }
    public int CasualIndex { get; }                  // count of Casual matches begun on this profile
    public int RankedIndex { get; }                  // count of ranked matches (NpcLegendField drift epoch)
    public double CasualRating { get; }              // hidden Casual MMR (CasualRules), never shown
    public IReadOnlyList<PastEncounter> History { get; }   // Casual encounters, oldest first
    public CasualRequest(long profileSeed, int casualIndex, int rankedIndex, double casualRating, IEnumerable<PastEncounter> history)
    { ProfileSeed = profileSeed; CasualIndex = casualIndex; RankedIndex = rankedIndex; CasualRating = casualRating;
      History = GoldenData.Copy(history); }
}

// Casual deck catalog: data/npc/casual-decks-v1.json. Same deck schema as decks-v1.json, every deck admission "casual"
// (the ladder never serves them); an optional per-deck "host" names the notable NPC whose signature deck it is, and an
// optional "family" groups variants of one archetype (e.g. three Rafaam lists) so they count as one deck for variety.
public sealed class CasualDeckCatalog
{
    public const string ResourceName = "SoloStone.Npc.casual-decks-v1.json";
    public NpcDeckLibrary Library { get; }
    public IReadOnlyDictionary<string, string> HostDeck { get; }   // npc id -> casual deck id
    public IReadOnlyDictionary<string, string> Family { get; }     // casual deck id -> family (absent: its own id)
    public CasualDeckCatalog(NpcDeckLibrary library, IDictionary<string, string> hostDeck, IDictionary<string, string> family = null)
    {
        Library = library ?? throw new ArgumentNullException(nameof(library));
        if (library.Decks.Count == 0) throw new System.IO.InvalidDataException("The Casual deck catalog is empty.");
        var bad = library.Decks.FirstOrDefault(d => d.Admission != "casual");
        if (bad != null) throw new System.IO.InvalidDataException("Casual deck must have admission \"casual\": " + bad.Id);
        HostDeck = new Dictionary<string, string>(hostDeck ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var pair in HostDeck)
            if (library.Find(pair.Value) == null) throw new System.IO.InvalidDataException("Unknown Casual host deck: " + pair.Value);
        Family = new Dictionary<string, string>(family ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var pair in Family)
        {
            if (library.Find(pair.Key) == null) throw new System.IO.InvalidDataException("Unknown Casual family deck: " + pair.Key);
            // A family named like a deck id would silently merge with that deck's own (default) family.
            if (library.Find(pair.Value) != null) throw new System.IO.InvalidDataException("Casual family name is a deck id: " + pair.Value);
        }
    }
    public IReadOnlyList<NpcDeckEntry> Decks => Library.Decks;
    // Any deck id (ranked own decks too) maps to its family; decks outside a family are their own.
    public string FamilyOf(string deckId) => deckId != null && Family.TryGetValue(deckId, out string family) ? family : deckId;

    public static CasualDeckCatalog Parse(string json) => Load(NpcWorldJson.Parse(json));
    public static CasualDeckCatalog Load(object root)
    {
        var library = NpcDeckLibrary.Load(root);
        var hosts = new Dictionary<string, string>(StringComparer.Ordinal);
        var families = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (object value in NpcWorldJson.List(NpcWorldJson.Object(root), "decks"))
        {
            var row = NpcWorldJson.Object(value);
            string family = NpcWorldJson.Text(row, "family", false);
            if (!string.IsNullOrEmpty(family)) families[NpcWorldJson.Text(row, "id")] = family;
            string host = NpcWorldJson.Text(row, "host", false);
            if (string.IsNullOrEmpty(host)) continue;
            if (hosts.ContainsKey(host)) throw new System.IO.InvalidDataException("Casual host with two decks: " + host);
            hosts.Add(host, NpcWorldJson.Text(row, "id"));
        }
        return new CasualDeckCatalog(library, hosts, families);
    }

    // The ranked library plus these decks, one lookup for plans and LocalDecks.Get. Ranked generation, the matchmaker and
    // deck inference read admitted decks only, so adding "casual" decks changes no ranked pool.
    // A Casual id that collides with a ranked deck is left out (never replaces it), so bad Casual data cannot take down the
    // ranked world; NpcWorldRuntime.TryInstallClient then drops it from the Casual queue too.
    public NpcDeckLibrary MergeInto(NpcDeckLibrary ranked)
    {
        if (ranked == null) throw new ArgumentNullException(nameof(ranked));
        return new NpcDeckLibrary(ranked.Schema, ranked.LibraryVersion, ranked.ActiveMeta,
            ranked.Decks.Concat(Library.Decks.Where(d => ranked.Find(d.Id) == null)));
    }

    // The catalog restricted to the decks keep accepts (hosts of dropped decks lose their signature deck).
    public CasualDeckCatalog Keep(Func<NpcDeckEntry, bool> keep)
    {
        var decks = Library.Decks.Where(keep).ToList();
        var kept = new HashSet<string>(decks.Select(d => d.Id), StringComparer.Ordinal);
        return new CasualDeckCatalog(new NpcDeckLibrary(Library.Schema, Library.LibraryVersion, Library.ActiveMeta, decks),
            HostDeck.Where(pair => kept.Contains(pair.Value)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            Family.Where(pair => kept.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }
}

// Queue numbers for Casual (owner-approved 2026-10-28): 75% fun NPC + meme deck, 25% any NPC + one of its own fun decks.
public sealed class CasualQueueRules
{
    public double FunShare { get; set; } = 0.75;
    public int MinOwnDeckFun { get; set; } = 4;
    public int NoSameNpcWithin { get; set; } = 5;
    public int NoSameDeckWithin { get; set; } = 3;
    public int CandidatePool { get; set; } = 24;
    public double JitterSigma { get; set; } = 35;
    public double NotableWeight { get; set; } = 1.5;
    // #66 named cast, same numbers as QueueRules.
    public double RegularShare { get; set; } = 0.65;
    public double RegularWindow { get; set; } = 90;
    public double RegularSigma { get; set; } = 45;
    public double RegularMinTarget { get; set; } = 1000;
    public double SignatureDeckShare { get; set; } = 0.65;   // a host's own deck; other NPCs pick their Casual decks uniformly
    public int DecksPerNpc { get; set; } = 2;
    public int HistoryDepth => Math.Max(NoSameNpcWithin, NoSameDeckWithin);
    public static CasualQueueRules Default => new CasualQueueRules();
}

public sealed class CasualMatchmaker
{
    private const long CasualTag = 0x434153;   // "CAS"
    private const int StreamTarget = 1, StreamNpc = 2, StreamDeck = 3, StreamMatch = 4, StreamKind = 5, StreamRegular = 6;
    // Casual-leaning NPCs (owner-approved roster rule): meme or brewer taste, or a playful archetype.
    public static readonly IReadOnlyList<string> FunArchetypes = Array.AsReadOnly(new[] { "chaotic", "showboat", "gambler", "professor", "friendly" });
    public NpcWorld World { get; }
    public CasualDeckCatalog Catalog { get; }
    public CasualQueueRules Rules { get; }
    public NpcLegendField Legend { get; }
    private readonly NpcProfileBuilder buildProfile;
    private readonly object gate = new object();
    private readonly Dictionary<string, NpcDeckEntry[]> casualDecks = new Dictionary<string, NpcDeckEntry[]>(StringComparer.Ordinal);
    private readonly Dictionary<string, NpcDeckEntry[]> ownFunDecks = new Dictionary<string, NpcDeckEntry[]>(StringComparer.Ordinal);

    public CasualMatchmaker(NpcWorld world, CasualDeckCatalog catalog, NpcLegendField legend = null, CasualQueueRules rules = null,
        NpcProfileBuilder profileBuilder = null)
    {
        World = world ?? throw new ArgumentNullException(nameof(world));
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Legend = legend ?? new NpcLegendField(world); Rules = rules ?? CasualQueueRules.Default;
        buildProfile = profileBuilder ?? CasualProfiles.Build;
    }

    public static bool IsFunNpc(NpcIdentity npc) => npc != null
        && (npc.Taste == DeckTaste.MemeLord || npc.Taste == DeckTaste.Brewer || FunArchetypes.Contains(npc.PersonalityId));

    // A fun NPC's Casual decks, signature first: a host's own deck, then picks by a stable hash of (world seed, npc id).
    // A family weighs as one deck (split across its variants) and an NPC holds at most one deck per family.
    public IReadOnlyList<NpcDeckEntry> CasualDecksFor(NpcIdentity npc)
    {
        if (npc == null || !IsFunNpc(npc) && !Catalog.HostDeck.ContainsKey(npc.Id)) return new NpcDeckEntry[0];
        lock (gate)
        {
            NpcDeckEntry[] decks;
            if (casualDecks.TryGetValue(npc.Id, out decks)) return decks;
            var pool = Catalog.Decks.ToList();
            var chosen = new List<NpcDeckEntry>();
            string hosted;
            if (Catalog.HostDeck.TryGetValue(npc.Id, out hosted)) chosen.Add(Catalog.Library.Find(hosted));
            var random = new MatchRng(StableSeed.Derive(World.WorldSeed, CasualTag, Hash(npc.Id)), StreamDeck);
            var size = pool.GroupBy(d => Catalog.FamilyOf(d.Id)).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var rest = pool.Where(d => chosen.All(c => Catalog.FamilyOf(c.Id) != Catalog.FamilyOf(d.Id))).ToList();
            while (chosen.Count < Math.Min(Rules.DecksPerNpc, pool.Count) && rest.Count > 0)
            {
                int pick = random.Pick(rest.Select(d => 1.0 / size[Catalog.FamilyOf(d.Id)]).ToArray());
                var deck = rest[pick]; chosen.Add(deck);
                rest.RemoveAll(d => Catalog.FamilyOf(d.Id) == Catalog.FamilyOf(deck.Id));
            }
            decks = chosen.ToArray();
            casualDecks[npc.Id] = decks;
            return decks;
        }
    }

    private NpcDeckEntry[] OwnFunDecks(NpcIdentity npc)
    {
        lock (gate)
        {
            NpcDeckEntry[] decks;
            if (ownFunDecks.TryGetValue(npc.Id, out decks)) return decks;
            decks = npc.DeckIds.Select(id => World.Catalogs.Decks.Find(id))
                .Where(d => d != null && d.Admitted && d.Tags.Fun >= Rules.MinOwnDeckFun).ToArray();
            ownFunDecks[npc.Id] = decks;
            return decks;
        }
    }

    public NpcEncounter Next(CasualRequest request) => Choose(request).Encounter;

    public MatchPick Choose(CasualRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (request.CasualIndex < 0 || request.RankedIndex < 0) throw new ArgumentException("Match index must be >= 0.");
        if (double.IsNaN(request.CasualRating) || double.IsInfinity(request.CasualRating)) throw new ArgumentException("Invalid Casual rating.");
        int seed = StableSeed.Derive(request.ProfileSeed, CasualTag, request.CasualIndex);
        bool fun = new MatchRng(seed, StreamKind).NextDouble() < Rules.FunShare;
        double target = request.CasualRating + new MatchRng(seed, StreamTarget).Gauss() * Rules.JitterSigma;
        var ranked = Legend.Ranked(request.RankedIndex);
        int count = Math.Min(request.History.Count, Rules.HistoryDepth);
        var recent = request.History.Skip(request.History.Count - count).Reverse().ToArray();   // most recent first

        List<Option> options = null;
        int level = 0;
        bool regularRoll = new MatchRng(seed, StreamRegular).NextDouble() < Rules.RegularShare, regularDraw = false;
        foreach (bool funKind in new[] { fun, !fun })
        {
            Func<NpcIdentity, NpcDeckEntry[]> decksOf = funKind ? (Func<NpcIdentity, NpcDeckEntry[]>)(npc => CasualDecksFor(npc).ToArray()) : OwnFunDecks;
            if (regularRoll)
            {
                double centre = Math.Max(target, Rules.RegularMinTarget);
                var regulars = Build(Regulars(ranked, centre, decksOf), centre, recent, 0, decksOf, Rules.RegularSigma);
                if (regulars.Count != 0) { options = regulars; regularDraw = true; fun = funKind; level = 0; break; }
            }
            var candidates = Nearest(ranked, target, decksOf);
            // Levels: 0 both rules; 1 drop the deck rule; 2 NPC window 2; 3 ignore both (tiny worlds only).
            for (level = 0; level <= 3 && options == null; level++)
            {
                var built = Build(candidates, target, recent, level, decksOf);
                if (built.Count != 0) options = built;
            }
            if (options != null) { fun = funKind; level--; break; }
        }
        if (options == null) throw new InvalidOperationException("The NPC world has no Casual opponent.");

        var chosen = options[new MatchRng(seed, StreamNpc).Pick(options.Select(o => o.Weight).ToArray())];
        var deck = chosen.Decks[new MatchRng(seed, StreamDeck).Pick(chosen.Decks.Select(d => d.Weight).ToArray())].Deck;
        var skill = chosen.Npc.SkillId != null ? World.Catalogs.Skills.Find(chosen.Npc.SkillId)
            : NpcPopulationRules.SkillFor(World.Catalogs.Skills, chosen.Npc.HomeRating);
        var personality = World.Catalogs.Personalities.Find(chosen.Npc.PersonalityId);
        if (skill == null || personality == null)
            throw new InvalidOperationException("NPC " + chosen.Npc.Id + " references an unknown skill or personality.");
        var profile = buildProfile(chosen.Npc, deck, personality, skill);
        int matchSeed = StableSeed.Derive(seed, StreamMatch) & int.MaxValue;
        var encounter = new NpcEncounter(chosen.Npc, deck, skill, personality, profile, chosen.Rating, matchSeed,
            fun ? "casual-fun" : "casual-own");
        return new MatchPick(encounter, target, level, false, false, regularDraw);
    }

    private sealed class DeckOption { public NpcDeckEntry Deck; public double Weight; }
    private sealed class Option { public NpcIdentity Npc; public double Rating; public double Weight; public List<DeckOption> Decks; }

    private List<RankedNpc> Nearest(IReadOnlyList<RankedNpc> ranked, double target, Func<NpcIdentity, NpcDeckEntry[]> decksOf)
    {
        var result = new List<RankedNpc>();
        int low = 0, high = ranked.Count;
        while (low < high) { int mid = (low + high) / 2; if (ranked[mid].Rating < target) low = mid + 1; else high = mid; }
        int left = low - 1, right = low;
        while (result.Count < Rules.CandidatePool && (left >= 0 || right < ranked.Count))
        {
            bool takeLeft = right >= ranked.Count || (left >= 0 && target - ranked[left].Rating <= ranked[right].Rating - target);
            var next = takeLeft ? ranked[left--] : ranked[right++];
            if (decksOf(next.Npc).Length > 0) result.Add(next);
        }
        return result;
    }

    private List<RankedNpc> Regulars(IReadOnlyList<RankedNpc> ranked, double target, Func<NpcIdentity, NpcDeckEntry[]> decksOf)
    {
        var result = new List<RankedNpc>();
        int low = 0, high = ranked.Count;
        while (low < high) { int mid = (low + high) / 2; if (ranked[mid].Rating < target - Rules.RegularWindow) low = mid + 1; else high = mid; }
        for (int i = low; i < ranked.Count && ranked[i].Rating <= target + Rules.RegularWindow; i++)
            if (ranked[i].Npc.Notable && decksOf(ranked[i].Npc).Length > 0) result.Add(ranked[i]);
        return result;
    }

    private List<Option> Build(List<RankedNpc> candidates, double target, PastEncounter[] recent, int level,
        Func<NpcIdentity, NpcDeckEntry[]> decksOf, double sigma = 0)
    {
        int npcWindow = level >= 3 ? 0 : level == 2 ? 2 : Rules.NoSameNpcWithin;
        int deckWindow = level >= 1 ? 0 : Rules.NoSameDeckWithin;
        var options = new List<Option>();
        var logWeights = new List<double>();
        foreach (var candidate in candidates)
        {
            var npc = candidate.Npc;
            if (recent.Take(npcWindow).Any(p => p.NpcId == npc.Id)) continue;
            var all = decksOf(npc);
            var decks = all.Where(d => !recent.Take(deckWindow).Any(p => Catalog.FamilyOf(p.DeckId) == Catalog.FamilyOf(d.Id)))
                .Select(d => new DeckOption { Deck = d }).ToList();
            if (decks.Count == 0) continue;
            string hosted;
            bool signature = decks.Count > 1 && Catalog.HostDeck.TryGetValue(npc.Id, out hosted) && decks[0].Deck.Id == hosted;
            for (int i = 0; i < decks.Count; i++)
                decks[i].Weight = decks.Count == 1 ? 1 : signature
                    ? (i == 0 ? Rules.SignatureDeckShare : (1 - Rules.SignatureDeckShare) / (decks.Count - 1)) : 1.0 / decks.Count;
            double z = (candidate.Rating - target) / (sigma > 0 ? sigma : Rules.JitterSigma);
            options.Add(new Option { Npc = npc, Rating = candidate.Rating, Decks = decks });
            logWeights.Add(-0.5 * z * z + Math.Log(npc.Notable ? Rules.NotableWeight : 1));
        }
        if (options.Count == 0) return options;
        double max = logWeights.Max();
        for (int i = 0; i < options.Count; i++) options[i].Weight = Math.Exp(logWeights[i] - max);
        return options;
    }

    private static long Hash(string text)
    {
        unchecked { uint h = 2166136261; foreach (char c in text) h = (h ^ c) * 16777619; return h; }
    }
}

// Casual NPC profiles: the ranked build (same pilot, skill, budget, plan, cosmetics) with a Casual emote overlay for the
// fun archetypes only. Emotes are presentation: they never reach the pilot, its inference or the engine (fairness).
public static class CasualProfiles
{
    public static NpcProfile Build(NpcIdentity npc, NpcDeckEntry deck, PersonalityArchetype personality, SkillProfile skill)
    {
        var profile = NpcEncounterProfiles.Build(npc, deck, personality, skill);
        if (!CasualMatchmaker.FunArchetypes.Contains(personality.Id)) return profile;
        var p = profile.Personality;
        var casual = new Personality(p.PreTurnMinSeconds, p.PreTurnMaxSeconds, p.ActionMinSeconds, p.ActionMaxSeconds,
            Overlay(p.Emotes), p.ReplyBlendBeta, p.ConcessionWillingness, p.ConcedeFromTurn);
        return new NpcProfile(profile.Id, profile.Version, profile.DisplayName, profile.DeckId, profile.HeroSkin, profile.CardBack,
            casual, profile.Budget, profile.PlanId, profile.HeroSkinPower, profile.Fixes);
    }

    // Overlay rules run first for their trigger (same-trigger rules are ordered alternatives), so the archetype's own rule
    // still fires when the overlay declines. Friendly emotes only: no Threaten, no mock (warm archetypes stay warm).
    public static readonly IReadOnlyList<EmoteRule> OverlayRules = Array.AsReadOnly(new[] {
        new EmoteRule(NpcReaction.MatchStart, "Greetings", 0.9),
        new EmoteRule(NpcReaction.OwnBigTurn, "Wow", 0.5, 2),
        new EmoteRule(NpcReaction.HumanBigTurn, "Wow", 0.35),
        new EmoteRule(NpcReaction.BotDoomed, "Oops", 0.5),
    });

    public static EmoteProfile Overlay(EmoteProfile emotes)
    {
        var own = emotes?.Rules ?? (IReadOnlyList<EmoteRule>)new EmoteRule[0];
        var rules = new List<EmoteRule>();
        foreach (var trigger in OverlayRules.Select(r => r.Trigger).Concat(own.Select(r => r.Trigger)).Distinct())
        {
            rules.AddRange(OverlayRules.Where(r => r.Trigger == trigger));
            rules.AddRange(own.Where(r => r.Trigger == trigger));
        }
        int budget = emotes == null ? 3 : emotes.Budget + 1;
        double gap = emotes == null ? 6 : emotes.MinGapMilliseconds / 1000;
        return new EmoteProfile(budget, gap, rules.ToArray());
    }
}
