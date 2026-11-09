using System;
using System.Collections.Generic;
using System.Linq;

// WP4. Replaces LocalDecks.NextOpponent for ladder matches. Pure + deterministic per (profileSeed, matchIndex).
// Inputs are the player's OWN ladder state and match history (opponent ids/decks/results) — never the player's
// current deck list, hand or collection (no covert counter-picking).
public sealed class PastEncounter
{
    public string NpcId { get; }
    public string DeckId { get; }
    public DeckArchetype? Archetype { get; }
    public string Result { get; }   // "Won" | "Lost" | "Tied" | ... from LocalMatchRecord.Result (player perspective)
    public PastEncounter(string npcId, string deckId, DeckArchetype? archetype, string result)
    { NpcId = npcId; DeckId = deckId; Archetype = archetype; Result = result; }
}

public sealed class MatchRequest
{
    public long ProfileSeed { get; }                 // StableSeed root for this profile (spec: derived from PackSeed)
    public int MatchIndex { get; }                   // count of ladder matches begun on this profile
    public int StarIndex { get; }                    // LadderPosition.StarIndex
    public double HiddenRating { get; }              // LadderPosition.Rating
    public int? LegendNumber { get; }                // current number if in Legend
    // Oldest first. The queue rules read at most the last QueueRules.HistoryDepth entries; callers may pass only those.
    public IReadOnlyList<PastEncounter> History { get; }
    public MatchRequest(long profileSeed, int matchIndex, int starIndex, double hiddenRating, int? legendNumber,
        IEnumerable<PastEncounter> history)
    { ProfileSeed = profileSeed; MatchIndex = matchIndex; StarIndex = starIndex; HiddenRating = hiddenRating;
      LegendNumber = legendNumber; History = GoldenData.Copy(history); }
}

// Queue rules (spec "Matchmaker"); numbers live here so the climb sim and tests share them.
public sealed class QueueRules
{
    public int NoSameNpcWithin { get; set; } = 5;
    public int NoSameDeckWithin { get; set; } = 3;
    public int AggroWindow { get; set; } = 4;          // at most 1 aggro deck per 4 matches
    public int HighAnnoyance { get; set; } = 2;        // annoyance >= this is "high"
    public int HighAnnoyanceWindow { get; set; } = 6;  // at most 1 high-annoyance deck per 6 matches
    public double ArchetypeCap { get; set; } = 0.15;   // per-band archetype share cap over ArchetypeWindow
    public int ArchetypeWindow { get; set; } = 20;
    public double JitterSigma { get; set; } = 35;      // rating jitter, Elo points
    public int CandidatePool { get; set; } = 24;       // nearest-N NPCs considered before rules/weights
    // Soft archetype cap: weight x ArchetypeSoftWeight at ArchetypeCap, excluded at ArchetypeHardCap (spec: 0.25 / 25%).
    public double ArchetypeSoftWeight { get; set; } = 0.25;
    public double ArchetypeHardCap { get; set; } = 0.25;
    public int MinNoSameNpcWithin { get; set; } = 2;   // the NPC rule is never relaxed below this
    public double SignatureDeckShare { get; set; } = 0.5;
    public double NotableWeight { get; set; } = 1.5;
    // #66 named cast: this share of games tries a regular near the target first; the rest, or a roll no regular fits, is the full pool.
    public double RegularShare { get; set; } = 0.65;
    public double RegularWindow { get; set; } = 90;    // +- Elo: a home range about 1.5 ranks wide (a rank is 120)
    public double RegularSigma { get; set; } = 45;     // wider than JitterSigma: regulars are sparse, so the rating weight stays gentle
    // Early losses drop the hidden rating below every regular (7 losses on a new save reach about 850): the draw never centres lower than this.
    public double RegularMinTarget { get; set; } = 1000;
    public double FieldJitterSigma { get; set; } = 20; // Diamond 5+ target jitter
    public int LegendSpread { get; set; } = 15;       // Legend: opponents within +-N numbers of the player's number
    // Elo offsets below the hidden rating per league group (data-driven: defaults from MatchmakerTuning) and the Diamond 5+ field offset.
    public double BronzeSilverOffset { get; set; } = MatchmakerTuning.BronzeSilverOffset;
    public double GoldPlatinumOffset { get; set; } = MatchmakerTuning.GoldPlatinumOffset;
    public double DiamondOffset { get; set; } = MatchmakerTuning.DiamondOffset;
    public double FieldOffset { get; set; } = MatchmakerTuning.DiamondFieldOffset;
    public double BandOffset(LadderBand band)
    {
        switch (band)
        {
            case LadderBand.Bronze: case LadderBand.Silver: return BronzeSilverOffset;
            case LadderBand.Gold: case LadderBand.Platinum: return GoldPlatinumOffset;
            case LadderBand.Diamond: return DiamondOffset;
            default: return 0;
        }
    }
    public int HistoryDepth => Math.Max(Math.Max(NoSameNpcWithin, NoSameDeckWithin),
        Math.Max(Math.Max(AggroWindow, HighAnnoyanceWindow), ArchetypeWindow));
    public static QueueRules Default => new QueueRules();
}

// Data-driven matchmaker constants (the climb sim in tests/ladder-rules reports their effect; owner backlog #52).
// PROVISIONAL: tuned against a simulation that models win odds by Elo expectation against the NPC's nominal (drifted) rating, not
// by the real skill tier and deck of the opponent, over a synthetic world. Re-tune with the real world (WP2/WP3) and human playtests.
// The spec's equilibrium offsets are 400*log10(p/(1-p)) for the #52 midpoints (67.5% -> 127, 57.5% -> 53, 52.5% -> 17) and a Diamond 5+
// field of RatingForIndex + 20. They assume the hidden rating already equals the player's strength. The simulation shows it does not:
// stars come far faster than K=24 can move the rating, so a 1550 player's hidden rating lags ~100 (Bronze-Silver) to ~25 (Gold-Platinum)
// below their strength while climbing. With the spec values a 1550 player wins 78% / 61% / 47% / 44% (Bronze-Silver / Gold-Platinum /
// Diamond / Legend) and needs a median ~1000 games. Owner rule: the Diamond 5+ field is never softer than Diamond 10-6 (D5-1 mean
// opponent rating >= D10-6 mean), field offset within -20..0. Final values (reference 1575, 200 seeds): see the spec, "Tuned offsets".
public static class MatchmakerTuning
{
    // #52 reference-player win-rate midpoints, kept for documentation and OffsetForWinRate.
    public const double BronzeSilverWinRate = 0.675, GoldPlatinumWinRate = 0.575, DiamondWinRate = 0.525;
    // Elo points the target sits below the hidden rating, per league group (Diamond 10-6 for DiamondOffset).
    public const double BronzeSilverOffset = 55, GoldPlatinumOffset = 40, DiamondOffset = 45;
    // Diamond 5 and up: target = RatingForIndex(starIndex) + DiamondFieldOffset (fixed field, never softened).
    public const double DiamondFieldOffset = -20;
    public static double OffsetForWinRate(double p) => 400 * Math.Log10(p / (1 - p));
}

public interface IMatchmaker
{
    NpcEncounter Next(MatchRequest request);
}

// A pick plus how it was reached (tests and logs). RelaxLevel 0 = every queue rule held.
public sealed class MatchPick
{
    public NpcEncounter Encounter { get; }
    public double Target { get; }
    public int RelaxLevel { get; }       // 0 none, 1 archetype, 2 +annoyance, 3 +aggro, 4 +deck, 5.. NPC window shrinking, last = ignore all
    public bool PoolWidened { get; }     // candidates came from beyond the nearest-N pool
    public bool BandRelaxed { get; }     // no NPC owned a deck allowed in the band (tiny worlds only)
    public bool RegularDraw { get; }     // came from the #66 regular draw (a notable can also come from the full pool)
    public MatchPick(NpcEncounter encounter, double target, int relaxLevel, bool poolWidened, bool bandRelaxed, bool regularDraw = false)
    { Encounter = encounter; Target = target; RelaxLevel = relaxLevel; PoolWidened = poolWidened; BandRelaxed = bandRelaxed; RegularDraw = regularDraw; }
}

public delegate NpcProfile NpcProfileBuilder(NpcIdentity npc, NpcDeckEntry deck, PersonalityArchetype personality, SkillProfile skill);

public sealed class LadderMatchmaker : IMatchmaker
{
    private const int StreamTarget = 1, StreamNpc = 2, StreamDeck = 3, StreamMatch = 4, StreamRegular = 5;
    public NpcWorld World { get; }
    public QueueRules Rules { get; }
    public NpcLegendField Legend { get; }
    private readonly NpcProfileBuilder buildProfile;
    private readonly object gate = new object();
    private readonly Dictionary<string, NpcDeckEntry[]> allowedCache = new Dictionary<string, NpcDeckEntry[]>(StringComparer.Ordinal);

    // buildProfile defaults to WP3's NpcEncounterProfiles.Build; tests inject a stub.
    public LadderMatchmaker(NpcWorld world, QueueRules rules = null, NpcProfileBuilder profileBuilder = null)
    { World = world ?? throw new ArgumentNullException(nameof(world)); Rules = rules ?? QueueRules.Default;
      Legend = new NpcLegendField(world); buildProfile = profileBuilder ?? NpcEncounterProfiles.Build; }

    public NpcEncounter Next(MatchRequest request) => Choose(request).Encounter;

    // Below Diamond 5: hidden - BandOffset(league) (+ jitter). Diamond 5+: RatingForIndex(starIndex) + FieldOffset,
    // independent of the hidden rating (fixed, never softened). Legend: neighbourhood of the player's Legend number.
    public double TargetRating(MatchRequest request, int seed)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        var rng = new MatchRng(seed, StreamTarget);
        int index = request.StarIndex;
        if (index < LadderRules.DiamondFiveIndex)
            return request.HiddenRating - Rules.BandOffset(NpcWorld.BandForStarIndex(index)) + rng.Gauss() * Rules.JitterSigma;
        if (index < LadderRules.LegendIndex)
            return LadderRules.RatingForIndex(index) + Rules.FieldOffset + rng.Gauss() * Rules.FieldJitterSigma;
        int count = Legend.Field(request.MatchIndex).Count;
        if (count == 0) return LadderRules.RatingForIndex(LadderRules.LegendIndex) + Rules.FieldOffset;
        int number = request.LegendNumber ?? Legend.LegendNumber(request.HiddenRating, request.MatchIndex);
        int spread = (int)Math.Round((rng.NextDouble() * 2 - 1) * Rules.LegendSpread);
        int position = Math.Max(1, Math.Min(count, number + spread));
        return Legend.FieldRating(position, request.MatchIndex);
    }

    // #52 targets for a reference player: Bronze-Silver ~65-70%, Gold-Plat ~55-60%, Diamond ~50-55%. See MatchmakerTuning.
    public static double BandOffset(LadderBand band) { return QueueRules.Default.BandOffset(band); }

    public MatchPick Choose(MatchRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (request.MatchIndex < 0) throw new ArgumentException("MatchIndex must be >= 0.");
        if (request.StarIndex < 0 || request.StarIndex > LadderRules.LegendIndex) throw new ArgumentException("StarIndex out of range.");
        if (double.IsNaN(request.HiddenRating) || double.IsInfinity(request.HiddenRating)) throw new ArgumentException("Invalid hidden rating.");
        lock (gate)
        {
            int seed = StableSeed.Derive(request.ProfileSeed, request.MatchIndex);
            LadderBand band = NpcWorld.BandForStarIndex(request.StarIndex);
            double target = TargetRating(request, seed);
            var ranked = Legend.Ranked(request.MatchIndex);
            var history = new HistoryView(request.History, World.Catalogs.Decks, Rules);

            bool bandRelaxed = false, widened = false;
            List<Option> options = null;
            int level = 0;
            if (new MatchRng(seed, StreamRegular).NextDouble() < Rules.RegularShare)
            {
                // Level 1 too: a capped archetype alone should not lose the roll. CastSuite reports how often this relaxes the cap.
                double centre = Math.Max(target, Rules.RegularMinTarget);
                var near = Regulars(ranked, centre, band);
                for (level = 0; level <= 1 && options == null; level++)
                {
                    var regulars = Build(near, centre, history, level, band, false, Rules.RegularSigma);
                    if (regulars.Count > 0) { options = regulars; break; }
                }
                if (options == null) level = 0;
            }
            bool regularDraw = options != null;
            int[] pools = { Rules.CandidatePool, Rules.CandidatePool * 4, int.MaxValue };
            for (int pool = 0; pool < pools.Length && options == null; pool++)
            {
                var candidates = Nearest(ranked, target, band, pools[pool], bandRelaxed);
                if (candidates.Count == 0 && pool == 0)
                {
                    bandRelaxed = true;   // tiny worlds: no NPC owns a deck allowed in this band
                    candidates = Nearest(ranked, target, band, pools[pool], true);
                }
                for (level = 0; level <= FinalLevel; level++)
                {
                    var built = Build(candidates, target, history, level, band, bandRelaxed);
                    if (built.Count == 0) continue;
                    options = built; widened = pool > 0; break;
                }
            }
            if (options == null) throw new InvalidOperationException("The NPC world has no opponent with an admitted deck.");

            var npcRng = new MatchRng(seed, StreamNpc);
            Option chosen = options[npcRng.Pick(options.Select(o => o.Weight).ToArray())];
            var deckRng = new MatchRng(seed, StreamDeck);
            DeckOption deck = chosen.Decks[deckRng.Pick(chosen.Decks.Select(d => d.Weight).ToArray())];

            string reason = request.StarIndex < LadderRules.DiamondFiveIndex ? "hidden-rating"
                : request.StarIndex < LadderRules.LegendIndex ? "fixed-field" : "legend-field";
            var encounter = Encounter(chosen.Npc, deck.Deck, chosen.Rating, seed, reason);
            return new MatchPick(encounter, target, level, widened, bandRelaxed, regularDraw);
        }
    }

    private NpcEncounter Encounter(NpcIdentity npc, NpcDeckEntry deck, double rating, int seed, string reason)
    {
        var skill = npc.SkillId != null ? World.Catalogs.Skills.Find(npc.SkillId) : World.Catalogs.Skills.ForRating(npc.HomeRating);
        var personality = World.Catalogs.Personalities.Find(npc.PersonalityId);
        if (skill == null || personality == null)
            throw new InvalidOperationException("NPC " + npc.Id + " references an unknown skill or personality.");
        var profile = buildProfile(npc, deck, personality, skill);
        int matchSeed = StableSeed.Derive(seed, StreamMatch) & int.MaxValue;
        return new NpcEncounter(npc, deck, skill, personality, profile, rating, matchSeed, reason);
    }

    // #28 (owner 2026-10-28): the NPC and deck of the player's last match, again, as a normal ranked game. Allowed only while
    // they are still a fair pick for the player's state after that result: the same NPC/deck as the newest history entry, the
    // deck admitted and allowed in the current band, fewer than MaxRematchSeries games in a row against that NPC, and the
    // NPC's drifted rating at or above RematchFloor. Harder NPCs always pass. Returns null with a reason when refused.
    public const int MaxRematchSeries = 3;
    public const double RematchSigmas = 2;

    public NpcEncounter Rematch(MatchRequest request, string npcId, string deckId, out string refusal)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        refusal = RematchRefusal(request, npcId, deckId, out NpcIdentity npc, out NpcDeckEntry deck, out double rating);
        if (refusal != null) return null;
        lock (gate) return Encounter(npc, deck, rating, StableSeed.Derive(request.ProfileSeed, request.MatchIndex), "rematch");
    }

    private string RematchRefusal(MatchRequest request, string npcId, string deckId, out NpcIdentity npc, out NpcDeckEntry deck, out double rating)
    {
        npc = null; deck = null; rating = 0;
        if (request.StarIndex < 0 || request.StarIndex > LadderRules.LegendIndex) return "ladder state out of range";
        var history = request.History;
        if (string.IsNullOrEmpty(npcId) || string.IsNullOrEmpty(deckId) || history.Count == 0
            || history[history.Count - 1].NpcId != npcId || history[history.Count - 1].DeckId != deckId) return "not the last opponent";
        int series = 0;
        for (int i = history.Count - 1; i >= 0 && history[i].NpcId == npcId; i--) series++;
        if (series >= MaxRematchSeries) return "series limit";
        npc = World.Find(npcId);
        if (npc == null) return "unknown NPC";
        deck = npc.DeckIds.Contains(deckId) ? World.Catalogs.Decks.Find(deckId) : null;
        if (deck == null || !deck.Admitted) return "deck unavailable";
        if (!deck.Tags.Allows(NpcWorld.BandForStarIndex(request.StarIndex))) return "deck not allowed in this band";
        if (double.IsNaN(request.HiddenRating) || double.IsInfinity(request.HiddenRating)) return "invalid hidden rating";
        rating = Legend.DriftedRating(npc, request.MatchIndex);
        // A regular may be drawn well below the target, so its floor is RegularWindow below the un-jittered target. The jittered tail stays refused.
        double floor = RematchFloor(request);
        if (npc.Notable) floor = Math.Min(floor, RematchFloor(request, 0) - Rules.RegularWindow);
        if (!(rating >= floor - 1e-9)) return "opponent now too weak";
        return null;
    }

    // The lowest NPC rating a rematch may have: the matchmaker's un-jittered target minus RematchSigmas of its jitter. In Legend,
    // the rating of the field NPC LegendSpread places below the player (the far edge of the normal Legend neighbourhood); with an
    // empty field (tiny worlds) the normal target has no jitter, so the floor is that target.
    public double RematchFloor(MatchRequest request) => RematchFloor(request, RematchSigmas);

    private double RematchFloor(MatchRequest request, double sigmas)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        int index = request.StarIndex;
        if (index < LadderRules.DiamondFiveIndex)
            return request.HiddenRating - Rules.BandOffset(NpcWorld.BandForStarIndex(index)) - sigmas * Rules.JitterSigma;
        double field = LadderRules.RatingForIndex(Math.Min(index, LadderRules.LegendIndex)) + Rules.FieldOffset - sigmas * Rules.FieldJitterSigma;
        if (index < LadderRules.LegendIndex) return field;
        lock (gate)
        {
            int count = Legend.Field(request.MatchIndex).Count;
            if (count == 0) return LadderRules.RatingForIndex(LadderRules.LegendIndex) + Rules.FieldOffset;
            int number = request.LegendNumber ?? Legend.LegendNumber(request.HiddenRating, request.MatchIndex);
            return Legend.FieldRating(Math.Min(count, number + Rules.LegendSpread), request.MatchIndex);
        }
    }

    // Levels: 0 all rules; 1 drop archetype; 2 +annoyance; 3 +aggro; 4 +deck; 5.. shrink the NPC window toward its floor;
    // FinalLevel ignores every rule (only reachable in worlds too small to satisfy the NPC floor).
    private int MaxNpcRelax => Math.Max(0, Rules.NoSameNpcWithin - Rules.MinNoSameNpcWithin);
    private int FinalLevel => 5 + MaxNpcRelax;
    private int NpcWindow(int level) => level <= 4 ? Rules.NoSameNpcWithin : Math.Max(Rules.MinNoSameNpcWithin, Rules.NoSameNpcWithin - (level - 4));

    private NpcDeckEntry[] Allowed(NpcIdentity npc, LadderBand band, bool anyBand)
    {
        string key = npc.Id + "|" + (anyBand ? "*" : band.ToString());
        NpcDeckEntry[] decks;
        if (allowedCache.TryGetValue(key, out decks)) return decks;
        decks = npc.DeckIds.Select(id => World.Catalogs.Decks.Find(id)).Where(d => d != null && d.Admitted && (anyBand || d.Tags.Allows(band))).ToArray();
        allowedCache[key] = decks;
        return decks;
    }

    // Nearest-N by drifted rating among NPCs owning at least one allowed deck. Ties break by id (the order is stable).
    private List<RankedNpc> Nearest(IReadOnlyList<RankedNpc> ranked, double target, LadderBand band, int count, bool anyBand)
    {
        var result = new List<RankedNpc>();
        int low = 0, high = ranked.Count;
        while (low < high) { int mid = (low + high) / 2; if (ranked[mid].Rating < target) low = mid + 1; else high = mid; }
        int left = low - 1, right = low;
        while (result.Count < count && (left >= 0 || right < ranked.Count))
        {
            bool takeLeft = right >= ranked.Count || (left >= 0 && target - ranked[left].Rating <= ranked[right].Rating - target);
            var next = takeLeft ? ranked[left--] : ranked[right++];
            if (Allowed(next.Npc, band, anyBand).Length > 0) result.Add(next);
        }
        return result;
    }

    // Regulars with a deck allowed in the band, within RegularWindow of the target by drifted rating.
    private List<RankedNpc> Regulars(IReadOnlyList<RankedNpc> ranked, double target, LadderBand band)
    {
        var result = new List<RankedNpc>();
        int low = 0, high = ranked.Count;
        while (low < high) { int mid = (low + high) / 2; if (ranked[mid].Rating < target - Rules.RegularWindow) low = mid + 1; else high = mid; }
        for (int i = low; i < ranked.Count && ranked[i].Rating <= target + Rules.RegularWindow; i++)
            if (ranked[i].Npc.Notable && Allowed(ranked[i].Npc, band, false).Length > 0) result.Add(ranked[i]);
        return result;
    }

    private sealed class DeckOption { public NpcDeckEntry Deck; public double Weight; }
    private sealed class Option { public NpcIdentity Npc; public double Rating; public double Weight; public List<DeckOption> Decks; }

    private List<Option> Build(List<RankedNpc> candidates, double target, HistoryView history, int level, LadderBand band, bool anyBand, double sigma = 0)
    {
        var options = new List<Option>();
        bool ignoreAll = level >= FinalLevel;
        int npcWindow = NpcWindow(level);
        var logWeights = new List<double>();
        foreach (var candidate in candidates)
        {
            var npc = candidate.Npc;
            if (!ignoreAll && history.NpcWithin(npc.Id, npcWindow)) continue;
            var decks = new List<DeckOption>();
            double rawTotal = 0, factoredTotal = 0;
            var allowed = Allowed(npc, band, anyBand);
            for (int i = 0; i < allowed.Length; i++)
            {
                var deck = allowed[i];
                double factor = 1;
                if (!ignoreAll)
                {
                    if (level < 4 && history.DeckWithin(deck.Id, Rules.NoSameDeckWithin)) continue;
                    if (level < 3 && deck.Tags.Archetype == DeckArchetype.Aggro && history.AggroWithin(Rules.AggroWindow - 1)) continue;
                    if (level < 2 && deck.Tags.Annoyance >= Rules.HighAnnoyance && history.AnnoyanceWithin(Rules.HighAnnoyance, Rules.HighAnnoyanceWindow - 1)) continue;
                    if (level < 1)
                    {
                        double share = history.ArchetypeCount(deck.Tags.Archetype, Rules.ArchetypeWindow) / (double)Rules.ArchetypeWindow;
                        if (share >= Rules.ArchetypeHardCap - 1e-9) continue;
                        if (share >= Rules.ArchetypeCap - 1e-9) factor = Rules.ArchetypeSoftWeight;
                    }
                }
                decks.Add(new DeckOption { Deck = deck, Weight = factor });
            }
            if (decks.Count == 0) continue;
            // Signature deck (first in the NPC's pool) takes SignatureDeckShare when it survives; the rest split the remainder.
            bool signature = decks[0].Deck.Id == npc.DeckIds[0] && decks.Count > 1;
            for (int i = 0; i < decks.Count; i++)
            {
                double raw = decks.Count == 1 ? 1 : signature ? (i == 0 ? Rules.SignatureDeckShare : (1 - Rules.SignatureDeckShare) / (decks.Count - 1)) : 1.0 / decks.Count;
                rawTotal += raw; factoredTotal += raw * decks[i].Weight; decks[i].Weight *= raw;
            }
            double fun = decks.Average(d => (double)d.Deck.Tags.Fun);
            double z = (candidate.Rating - target) / (sigma > 0 ? sigma : Rules.JitterSigma);
            double logW = -0.5 * z * z + Math.Log((0.8 + 0.1 * fun) * (npc.Notable ? Rules.NotableWeight : 1) * (factoredTotal / rawTotal));
            options.Add(new Option { Npc = npc, Rating = candidate.Rating, Decks = decks });
            logWeights.Add(logW);
        }
        if (options.Count == 0) return options;
        double max = logWeights.Max();   // log-space avoids underflow when every candidate is far from the target
        for (int i = 0; i < options.Count; i++) options[i].Weight = Math.Exp(logWeights[i] - max);
        return options;
    }

    // The last QueueRules.HistoryDepth encounters, most recent first.
    private sealed class HistoryView
    {
        private readonly string[] npcs, decks;
        private readonly DeckArchetype?[] archetypes;
        private readonly int[] annoyance;
        public HistoryView(IReadOnlyList<PastEncounter> history, NpcDeckLibrary library, QueueRules rules)
        {
            int count = Math.Min(history.Count, rules.HistoryDepth);
            npcs = new string[count]; decks = new string[count]; archetypes = new DeckArchetype?[count]; annoyance = new int[count];
            for (int i = 0; i < count; i++)
            {
                var past = history[history.Count - 1 - i];
                var deck = library.Find(past.DeckId);
                npcs[i] = past.NpcId; decks[i] = past.DeckId;
                archetypes[i] = past.Archetype ?? (deck == null ? (DeckArchetype?)null : deck.Tags.Archetype);
                annoyance[i] = deck == null ? 0 : deck.Tags.Annoyance;
            }
        }
        public bool NpcWithin(string id, int window) { for (int i = 0; i < Math.Min(window, npcs.Length); i++) if (npcs[i] == id) return true; return false; }
        public bool DeckWithin(string id, int window) { for (int i = 0; i < Math.Min(window, decks.Length); i++) if (decks[i] == id) return true; return false; }
        public bool AggroWithin(int window) { for (int i = 0; i < Math.Min(window, archetypes.Length); i++) if (archetypes[i] == DeckArchetype.Aggro) return true; return false; }
        public bool AnnoyanceWithin(int atLeast, int window) { for (int i = 0; i < Math.Min(window, annoyance.Length); i++) if (annoyance[i] >= atLeast) return true; return false; }
        public int ArchetypeCount(DeckArchetype archetype, int window)
        {
            int n = 0;
            for (int i = 0; i < Math.Min(window, archetypes.Length); i++) if (archetypes[i] == archetype) n++;
            return n;
        }
    }
}

// #66: the picks the queue would make next from a real state, nothing stored. The first is the real next match; later ones assume the
// history grows by these picks and the rank stays put. Test control and tests only.
public static class QueueForecast
{
    public static IReadOnlyList<MatchPick> Ranked(LadderMatchmaker matchmaker, MatchRequest first, int count)
    {
        var history = first.History.ToList();
        return Enumerable.Range(0, count).Select(i =>
        {
            var pick = matchmaker.Choose(new MatchRequest(first.ProfileSeed, first.MatchIndex + i, first.StarIndex, first.HiddenRating, first.LegendNumber,
                history.Skip(Math.Max(0, history.Count - matchmaker.Rules.HistoryDepth))));
            history.Add(new PastEncounter(pick.Encounter.Npc.Id, pick.Encounter.Deck.Id, pick.Encounter.Deck.Tags.Archetype, "Won"));
            return pick;
        }).ToArray();
    }

    public static IReadOnlyList<MatchPick> Casual(CasualMatchmaker matchmaker, CasualRequest first, int count)
    {
        var history = first.History.ToList();
        return Enumerable.Range(0, count).Select(i =>
        {
            var pick = matchmaker.Choose(new CasualRequest(first.ProfileSeed, first.CasualIndex + i, first.RankedIndex, first.CasualRating,
                history.Skip(Math.Max(0, history.Count - matchmaker.Rules.HistoryDepth))));
            history.Add(new PastEncounter(pick.Encounter.Npc.Id, pick.Encounter.Deck.Id, pick.Encounter.Deck.Tags.Archetype, "Won"));
            return pick;
        }).ToArray();
    }
}

// Deterministic stream (SplitMix64) seeded from a derived seed + stream tag. Platform-independent: no System.Random.
internal sealed class MatchRng
{
    private ulong state;
    public MatchRng(int seed, int stream) { state = unchecked((ulong)(long)StableSeed.Derive(seed, stream)); }
    private ulong Next()
    {
        unchecked
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
    public double NextDouble() { return (Next() >> 11) * (1.0 / 9007199254740992.0); }
    public double Gauss()
    {
        double u1 = 1 - NextDouble(), u2 = NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
    public int Pick(double[] weights)
    {
        double total = 0;
        for (int i = 0; i < weights.Length; i++) total += weights[i];
        if (!(total > 0)) return (int)(NextDouble() * weights.Length) % weights.Length;
        double roll = NextDouble() * total;
        for (int i = 0; i < weights.Length; i++) { roll -= weights[i]; if (roll < 0) return i; }
        return weights.Length - 1;
    }
}

public sealed class RankedNpc
{
    public NpcIdentity Npc { get; }
    public double Rating { get; }
    public RankedNpc(NpcIdentity npc, double rating) { Npc = npc; Rating = rating; }
}

// WP4. Legend numbering against the NPC field (no background real games). Field ratings drift deterministically with the
// player's match count (no wall-clock time: no-FOMO), so the board feels alive while staying reproducible.
public sealed class NpcLegendField
{
    public const int StepEvery = 10;         // one drift step per this many player matches
    public const double StepSize = 8, MaxDrift = 60;
    public NpcWorld World { get; }
    private readonly object gate = new object();
    private readonly Dictionary<string, List<int>> walks = new Dictionary<string, List<int>>(StringComparer.Ordinal);
    private int cachedEpoch = -1;
    private RankedNpc[] cachedRanked;
    private NpcIdentity[] cachedField;
    private double[] cachedFieldRatings;
    public NpcLegendField(NpcWorld world) { World = world ?? throw new ArgumentNullException(nameof(world)); }

    private static int Epoch(int matchIndex) { return Math.Max(0, matchIndex) / StepEvery; }

    private static int Hash(string text)
    {
        unchecked { uint h = 2166136261; foreach (char c in text) h = (h ^ c) * 16777619; return (int)h; }
    }

    // Random walk of +-1 steps (prefix sums), one per epoch; extended lazily so a 10k-match queue stays cheap.
    private int WalkSum(NpcIdentity npc, int epoch)
    {
        if (epoch <= 0) return 0;
        List<int> walk;
        if (!walks.TryGetValue(npc.Id, out walk)) { walk = new List<int>(); walks[npc.Id] = walk; }
        int hash = Hash(npc.Id);
        while (walk.Count < epoch)
        {
            int step = (StableSeed.Derive(World.WorldSeed, 0x44524654, hash, walk.Count) & 1) == 0 ? 1 : -1;
            walk.Add((walk.Count == 0 ? 0 : walk[walk.Count - 1]) + step);
        }
        return walk[epoch - 1];
    }

    public double DriftedRating(NpcIdentity npc, int matchIndex)
    {
        if (npc == null) throw new ArgumentNullException(nameof(npc));
        lock (gate)
        {
            double drift = WalkSum(npc, Epoch(matchIndex)) * StepSize;
            return npc.HomeRating + Math.Max(-MaxDrift, Math.Min(MaxDrift, drift));
        }
    }

    private void Refresh(int matchIndex)
    {
        int epoch = Epoch(matchIndex);
        if (epoch == cachedEpoch) return;
        var all = World.All.Select(npc => new RankedNpc(npc, npc.HomeRating + Math.Max(-MaxDrift, Math.Min(MaxDrift, WalkSum(npc, epoch) * StepSize))))
            .OrderBy(r => r.Rating).ThenBy(r => r.Npc.Id, StringComparer.Ordinal).ToArray();
        var field = all.Where(r => IsLegendRating(r.Rating)).Reverse().ToArray();   // descending, ids descending on ties
        cachedRanked = all; cachedField = field.Select(r => r.Npc).ToArray(); cachedFieldRatings = field.Select(r => r.Rating).ToArray();
        cachedEpoch = epoch;
    }

    // Every NPC by drifted rating, ascending. Shared by the matchmaker's nearest-N search.
    public IReadOnlyList<RankedNpc> Ranked(int matchIndex) { lock (gate) { Refresh(matchIndex); return cachedRanked; } }

    // Membership follows the drifted rating, the same test as an opponent's Legend medal (NativeUIProbe.ProjectOpponentLadder):
    // every NPC that can show a Legend medal is counted, so no two NPCs a player can see share a number by omission.
    public static bool IsLegendRating(double rating) => LadderRules.IndexForRating(rating) == LadderRules.LegendIndex;

    // NPCs whose drifted rating is Legend (IsLegendRating), sorted by drifted rating (highest first).
    public IReadOnlyList<NpcIdentity> Field(int matchIndex) { lock (gate) { Refresh(matchIndex); return cachedField; } }

    // Drifted rating of the field NPC at 1-based position (1 = top).
    public double FieldRating(int position, int matchIndex)
    {
        lock (gate)
        {
            Refresh(matchIndex);
            if (cachedFieldRatings.Length == 0) throw new InvalidOperationException("The Legend field is empty.");
            return cachedFieldRatings[Math.Max(1, Math.Min(cachedFieldRatings.Length, position)) - 1];
        }
    }

    // 1 + number of field NPCs above the player's rating.
    public int LegendNumber(double playerRating, int matchIndex)
    {
        lock (gate)
        {
            Refresh(matchIndex);
            int low = 0, high = cachedFieldRatings.Length;   // descending: count of ratings strictly above the player
            while (low < high) { int mid = (low + high) / 2; if (cachedFieldRatings[mid] > playerRating) low = mid + 1; else high = mid; }
            return 1 + low;
        }
    }

    // An NPC opponent's number beside a Legend player (playerRating; null outside Legend). LegendNumber counts NPCs only, so a
    // player rated at or above the NPC (the player wins ties) takes the place above it. A rating outside this epoch's field
    // (a pending match saved under another world) could still land on the player's number; it moves down one instead.
    public int OpponentLegendNumber(double npcRating, int matchIndex, double? playerRating)
    {
        int number = LegendNumber(npcRating, matchIndex);
        if (!playerRating.HasValue) return number;
        return playerRating.Value >= npcRating || number == LegendNumber(playerRating.Value, matchIndex) ? number + 1 : number;
    }
}

// WP4. "You're 3-1 vs X": derived from match records only (no extra save state).
public sealed class HeadToHead
{
    public string NpcId { get; }
    public int Wins { get; }
    public int Losses { get; }
    public int Ties { get; }
    public int NpcConcessions { get; }
    public HeadToHead(string npcId, int wins, int losses, int ties, int npcConcessions)
    { NpcId = npcId; Wins = wins; Losses = losses; Ties = ties; NpcConcessions = npcConcessions; }

    public int Games => Wins + Losses + Ties;

    public static HeadToHead For(string npcId, IEnumerable<PastEncounter> history, IEnumerable<string> npcConcededMatchNpcIds = null)
    {
        var rows = (history ?? Enumerable.Empty<PastEncounter>()).Where(h => h.NpcId == npcId).ToArray();
        return new HeadToHead(npcId, rows.Count(h => h.Result == "Won"), rows.Count(h => h.Result == "Lost" || h.Result == "Abandoned"),
            rows.Count(h => h.Result != "Won" && h.Result != "Lost" && h.Result != "Abandoned"),
            (npcConcededMatchNpcIds ?? Enumerable.Empty<string>()).Count(id => id == npcId));
    }
    public string Label => Wins + "-" + Losses + (Ties > 0 ? "-" + Ties : "");
}
