using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// WP3. The NPC population: hand-written notables + a deterministic generated population from a fixed world seed.
// Generation needs no save: same catalogs + same WorldSeed => byte-identical population (ids, names, ratings, decks).
public enum DeckTaste { Netdecker, Brewer, ClassLoyalist, MemeLord, Tryhard }

public sealed class NpcIdentity
{
    public string Id { get; }                   // "npc.n.<slug>" notables, "npc.g.<0000>" generated, "npc.golden.<n>" golden
    public int Version { get; }
    public string DisplayName { get; }
    public string Bio { get; }                  // notables only; generated = null
    public bool Notable { get; }
    public double HomeRating { get; }           // nominal ladder rating (LadderRules scale: Bronze 1000 ... Legend 1600+)
    public string PersonalityId { get; }
    public DeckTaste Taste { get; }
    public IReadOnlyList<string> FavoriteClasses { get; }
    public IReadOnlyList<string> DeckIds { get; }   // NpcDeckLibrary ids, signature deck first
    public IReadOnlyDictionary<string, string> HeroSkins { get; }  // base hero -> skin hero card id ("" = base)
    public IReadOnlyDictionary<string, string> HeroSkinPowers { get; } // skin hero -> hero power override (optional)
    public int CardBack { get; }
    public string SkillId { get; }              // optional fixed tier (notables); null = SkillCatalog.ForRating(HomeRating)
    public NpcIdentity(string id, int version, string displayName, string bio, bool notable, double homeRating,
        string personalityId, DeckTaste taste, IEnumerable<string> favoriteClasses, IEnumerable<string> deckIds,
        IDictionary<string, string> heroSkins, IDictionary<string, string> heroSkinPowers, int cardBack, string skillId)
    { Id = id; Version = version; DisplayName = displayName; Bio = bio; Notable = notable; HomeRating = homeRating;
      PersonalityId = personalityId; Taste = taste; FavoriteClasses = GoldenData.Copy(favoriteClasses);
      DeckIds = GoldenData.Copy(deckIds);
      HeroSkins = new Dictionary<string, string>(heroSkins ?? new Dictionary<string, string>(), StringComparer.Ordinal);
      HeroSkinPowers = new Dictionary<string, string>(heroSkinPowers ?? new Dictionary<string, string>(), StringComparer.Ordinal);
      CardBack = cardBack; SkillId = skillId; }
}

public sealed class NpcWorldCatalogs
{
    public NpcDeckLibrary Decks { get; }
    public SkillCatalog Skills { get; }
    public PersonalityCatalog Personalities { get; }
    public IReadOnlyList<NpcIdentity> Notables { get; }
    public NpcNameTables Names { get; }
    public NpcCosmeticPool Cosmetics { get; }
    public NpcWorldCatalogs(NpcDeckLibrary decks, SkillCatalog skills, PersonalityCatalog personalities,
        IEnumerable<NpcIdentity> notables, NpcNameTables names, NpcCosmeticPool cosmetics)
    { Decks = decks; Skills = skills; Personalities = personalities; Notables = GoldenData.Copy(notables);
      Names = names; Cosmetics = cosmetics; }
}

public sealed class NpcWorld
{
    public const long DefaultWorldSeed = 20261025;
    public const int GeneratedCount = 1500;
    public int WorldVersion { get; }
    public long WorldSeed { get; }
    public NpcWorldCatalogs Catalogs { get; }
    public IReadOnlyList<NpcIdentity> All { get; }       // notables + generated, sorted by HomeRating descending
    private readonly Dictionary<string, NpcIdentity> byId = new Dictionary<string, NpcIdentity>(StringComparer.Ordinal);
    public NpcWorld(int worldVersion, long worldSeed, NpcWorldCatalogs catalogs, IEnumerable<NpcIdentity> all)
    {
        WorldVersion = worldVersion; WorldSeed = worldSeed; Catalogs = catalogs; All = GoldenData.Copy(all);
        foreach (var npc in All)
        {
            if (byId.ContainsKey(npc.Id)) throw new System.IO.InvalidDataException("Duplicate NPC id: " + npc.Id);
            byId.Add(npc.Id, npc);
        }
    }

    public NpcIdentity Find(string id)
    {
        NpcIdentity npc;
        return id != null && byId.TryGetValue(id, out npc) ? npc : null;
    }

    // Notables first, then GeneratedCount generated NPCs (see spec "Rating spread"): names from NpcNameTables (filtered),
    // taste/classes/personality weighted by rating, DeckIds from Decks.ForBand(band of HomeRating) with archetype cap,
    // cosmetics from NpcCosmeticPool. Pure function of (catalogs, worldSeed).
    public static NpcWorld Generate(NpcWorldCatalogs catalogs, long worldSeed = DefaultWorldSeed) =>
        NpcPopulationGenerator.Generate(catalogs, worldSeed);

    public static LadderBand BandForRating(double rating) => BandForStarIndex(LadderRules.IndexForRating(rating));
    public static LadderBand BandForStarIndex(int starIndex) =>
        starIndex >= LadderRules.LegendIndex ? LadderBand.Legend : (LadderBand)Math.Max(0, Math.Min(4, starIndex / 30));
}

// WP3. data/npc/names-v1.json: BattleTag-style generator tables + blocklist (tasteful, filtered, no real people).
public sealed class NpcNameTables
{
    public const string ResourceName = "SoloStone.Npc.names-v1.json";
    public IReadOnlyList<string> Prefixes { get; }
    public IReadOnlyList<string> Roots { get; }
    public IReadOnlyList<string> Suffixes { get; }
    public IReadOnlyList<string> Patterns { get; }   // e.g. "{Root}{Suffix}", "{Prefix}{Root}", "{Root}{Digits2}"
    public IReadOnlyList<string> Blocklist { get; }  // case-insensitive substrings
    public IReadOnlyList<string> BlockedNumbers { get; }  // digit runs never allowed (hate codes); defaults when data omits them
    public static readonly IReadOnlyList<string> DefaultBlockedNumbers = Array.AsReadOnly(new[] { "14", "88", "1488", "8814", "420", "666" });
    public NpcNameTables(IEnumerable<string> prefixes, IEnumerable<string> roots, IEnumerable<string> suffixes,
        IEnumerable<string> patterns, IEnumerable<string> blocklist) : this(prefixes, roots, suffixes, patterns, blocklist, null) { }
    public NpcNameTables(IEnumerable<string> prefixes, IEnumerable<string> roots, IEnumerable<string> suffixes,
        IEnumerable<string> patterns, IEnumerable<string> blocklist, IEnumerable<string> blockedNumbers)
    { Prefixes = GoldenData.Copy(prefixes); Roots = GoldenData.Copy(roots); Suffixes = GoldenData.Copy(suffixes);
      Patterns = GoldenData.Copy(patterns); Blocklist = GoldenData.Copy(blocklist);
      var numbers = GoldenData.Copy(blockedNumbers);
      BlockedNumbers = numbers.Count > 0 ? numbers : DefaultBlockedNumbers;
      if (BlockedNumbers.Any(n => n.Length == 0 || n.Any(c => c < '0' || c > '9'))) throw new System.IO.InvalidDataException("blockedNumbers must be digits."); }
    public const int MinLength = 3, MaxLength = 12;   // BattleTag rule: 3-12 characters, letters and digits, no leading digit

    // Deterministic per (seed, taken). Unique case-insensitively against taken (an OrdinalIgnoreCase set is fastest),
    // blocklist-clean, BattleTag-shaped. The returned name is added to taken.
    public string Generate(int seed, ISet<string> taken)
    {
        if (Roots.Count == 0 || Patterns.Count == 0) throw new InvalidOperationException("Name tables are empty.");
        var random = new NpcPopulationRandom(seed);
        for (int attempt = 0; attempt < 400; attempt++)
        {
            string name = Expand(Patterns[random.Index(Patterns.Count)], random);
            if (name == null || !IsWellFormed(name) || IsBlocked(name) || IsTaken(taken, name)) continue;
            if (taken != null) taken.Add(name);
            return name;
        }
        // Tables nearly exhausted: walk Root + number deterministically (still filtered and unique).
        for (long n = 0; n < MaxFallbackAttempts; n++)
        {
            string name = Roots[(int)((n + (uint)seed) % Roots.Count)] + (100 + n % 9900).ToString(CultureInfo.InvariantCulture);
            if (!IsWellFormed(name) || IsBlocked(name) || IsTaken(taken, name)) continue;
            if (taken != null) taken.Add(name);
            return name;
        }
        throw new InvalidOperationException("Name tables exhausted: no unique clean name after " + MaxFallbackAttempts + " fallbacks.");
    }

    public const int MaxFallbackAttempts = 100000;

    public static bool IsWellFormed(string name)
    {
        if (name == null || name.Length < MinLength || name.Length > MaxLength || !IsAsciiLetter(name[0])) return false;
        foreach (char c in name) if (!IsAsciiLetter(c) && (c < '0' || c > '9')) return false;
        return true;
    }

    // Case-insensitive substring match on the raw and the leet-normalised form (0=o 1=i 3=e 4=a 5=s 7=t 8=b 9=g).
    public bool IsBlocked(string name)
    {
        if (string.IsNullOrEmpty(name)) return true;
        // Hate-code numbers (14, 88, 1488 ...): a digit run equal to, or containing, a blocked number.
        foreach (System.Text.RegularExpressions.Match run in System.Text.RegularExpressions.Regex.Matches(name, "[0-9]+"))
            foreach (string code in BlockedNumbers)
                if (run.Value == code || code.Length >= 4 && run.Value.Contains(code)) return true;
        string lower = name.ToLowerInvariant(), plain = Normalise(lower);
        string squeezed = new string(lower.Where(char.IsLetter).ToArray());
        foreach (string word in Blocklist)
        {
            if (string.IsNullOrEmpty(word)) continue;
            string w = word.ToLowerInvariant();
            if (lower.Contains(w) || plain.Contains(w) || squeezed.Contains(w)) return true;
        }
        return false;
    }

    private static string Normalise(string lower)
    {
        var chars = lower.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            switch (chars[i])
            {
                case '0': chars[i] = 'o'; break; case '1': chars[i] = 'i'; break; case '3': chars[i] = 'e'; break;
                case '4': chars[i] = 'a'; break; case '5': chars[i] = 's'; break; case '7': chars[i] = 't'; break;
                case '8': chars[i] = 'b'; break; case '9': chars[i] = 'g'; break;
            }
        return new string(chars);
    }

    private static bool IsAsciiLetter(char c) => c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z';

    private static bool IsTaken(ISet<string> taken, string name)
    {
        if (taken == null || taken.Count == 0) return false;
        if (taken.Contains(name)) return true;
        var hash = taken as HashSet<string>;
        if (hash != null && ReferenceEquals(hash.Comparer, StringComparer.OrdinalIgnoreCase)) return false;
        return taken.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));
    }

    private string Expand(string pattern, NpcPopulationRandom random)
    {
        var text = new System.Text.StringBuilder();
        string lastRoot = null;
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] != '{') { text.Append(pattern[i]); continue; }
            int end = pattern.IndexOf('}', i);
            if (end < 0) return null;
            string token = pattern.Substring(i + 1, end - i - 1);
            i = end;
            string key = token.ToLowerInvariant(), value;
            switch (key)
            {
                case "prefix": value = Pick(Prefixes, random); break;
                case "suffix": value = Pick(Suffixes, random); break;
                case "root":
                    value = Pick(Roots, random);
                    if (value == lastRoot && Roots.Count > 1) value = Pick(Roots, random);
                    lastRoot = value; break;
                case "digits1": value = (1 + random.Index(9)).ToString(CultureInfo.InvariantCulture); break;
                case "digits2": value = random.Index(100).ToString("D2", CultureInfo.InvariantCulture); break;
                case "digits3": value = random.Index(1000).ToString("D3", CultureInfo.InvariantCulture); break;
                case "digits4": value = (1000 + random.Index(9000)).ToString(CultureInfo.InvariantCulture); break;
                case "year2": value = ((77 + random.Index(36)) % 100).ToString("D2", CultureInfo.InvariantCulture); break;
                default: return null;
            }
            if (value == null) return null;
            // {Root} keeps the table casing, {root} lower-cases, {ROOT} upper-cases.
            if (token == key) value = value.ToLowerInvariant();
            else if (token == token.ToUpperInvariant()) value = value.ToUpperInvariant();
            text.Append(value);
        }
        return text.ToString();
    }

    private static string Pick(IReadOnlyList<string> values, NpcPopulationRandom random) =>
        values.Count == 0 ? null : values[random.Index(values.Count)];

    public static NpcNameTables Parse(string json)
    {
        var top = NpcWorldJson.Object(NpcWorldJson.Parse(json));
        return new NpcNameTables(NpcWorldJson.Strings(top, "prefixes"), NpcWorldJson.Strings(top, "roots", true),
            NpcWorldJson.Strings(top, "suffixes"), NpcWorldJson.Strings(top, "patterns", true), NpcWorldJson.Strings(top, "blocklist"),
            NpcWorldJson.Strings(top, "blockedNumbers"));
    }
}

// WP3. data/npc/cosmetics-v1.json: hero skins / card backs NPCs may wear. Only items the curated shop catalogue
// approves (owner 2026-10-25 curation, feat/shop-products) or golden NPCs already use; verify=true items excluded
// from live use until qualified (NpcPresentation falls back to the base hero / default back).
public sealed class NpcCosmeticPool
{
    public const string ResourceName = "SoloStone.Npc.cosmetics-v1.json";
    public IReadOnlyDictionary<string, IReadOnlyList<string>> HeroSkinsByHero { get; }
    public IReadOnlyDictionary<string, string> HeroSkinPowers { get; }
    public IReadOnlyList<int> CardBacks { get; }
    public NpcCosmeticPool(IDictionary<string, IReadOnlyList<string>> heroSkinsByHero, IDictionary<string, string> heroSkinPowers,
        IEnumerable<int> cardBacks)
    { HeroSkinsByHero = new Dictionary<string, IReadOnlyList<string>>(heroSkinsByHero, StringComparer.Ordinal);
      HeroSkinPowers = new Dictionary<string, string>(heroSkinPowers, StringComparer.Ordinal); CardBacks = GoldenData.Copy(cardBacks); }

    public static NpcCosmeticPool Parse(string json)
    {
        var top = NpcWorldJson.Object(NpcWorldJson.Parse(json));
        var skins = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var powers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (object value in NpcWorldJson.List(top, "heroSkins"))
        {
            var row = NpcWorldJson.Object(value);
            string hero = NpcWorldJson.Text(row, "hero"), skin = NpcWorldJson.Text(row, "skin");
            var list = skins.ContainsKey(hero) ? skins[hero].ToList() : new List<string>();
            list.Add(skin); skins[hero] = list.AsReadOnly();
            string power = NpcWorldJson.Text(row, "heroPower", false);
            if (power != null) powers[skin] = power;
        }
        var backs = NpcWorldJson.List(top, "cardBacks").Cast<object>().Select(v => {
            if (!(v is long)) throw new System.IO.InvalidDataException("cardBacks must be integers.");
            return (int)(long)v;
        });
        return new NpcCosmeticPool(skins, powers, backs);
    }
}

// WP3. data/npc/notables-v1.json loader.
public static class NpcNotables
{
    public const string ResourceName = "SoloStone.Npc.notables-v1.json";
    public static IReadOnlyList<NpcIdentity> Parse(string json)
    {
        var top = NpcWorldJson.Object(NpcWorldJson.Parse(json));
        var result = new List<NpcIdentity>();
        foreach (object value in NpcWorldJson.List(top, "notables"))
        {
            var row = NpcWorldJson.Object(value);
            var skins = new Dictionary<string, string>(StringComparer.Ordinal);
            var powers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (object s in NpcWorldJson.List(row, "heroSkins", false))
            {
                var skin = NpcWorldJson.Object(s);
                skins[NpcWorldJson.Text(skin, "hero")] = NpcWorldJson.Text(skin, "skin");
                string power = NpcWorldJson.Text(skin, "heroPower", false);
                if (power != null) powers[NpcWorldJson.Text(skin, "skin")] = power;
            }
            result.Add(new NpcIdentity(NpcWorldJson.Text(row, "id"), NpcWorldJson.Integer(row, "version", 1),
                NpcWorldJson.Text(row, "name"), NpcWorldJson.Text(row, "bio", false), true,
                NpcWorldJson.Number(row, "homeRating"), NpcWorldJson.Text(row, "personality"),
                NpcDeckLibrary.ParseEnum<DeckTaste>(NpcWorldJson.Text(row, "taste")),
                NpcWorldJson.Strings(row, "classes"), NpcWorldJson.Strings(row, "decks", true), skins, powers,
                NpcWorldJson.Integer(row, "cardBack", 0), NpcWorldJson.Text(row, "skill", false)));
        }
        return result;
    }
}

// WP3. Population numbers (spec "Population and rating spread"). Shared with tests and the matchmaker's climb sim.
public static class NpcPopulationRules
{
    public const long GeneratedTag = 0x47454E;          // "GEN": s = StableSeed.Derive(worldSeed, GeneratedTag, g)
    public const int WorldVersion = 1;
    public const double LegendShare = 0.15, LegendMean = 70, LegendFloor = 1600, LegendCap = 1900;
    public const double ArchetypeCap = 0.15;            // per deck archetype (library deck), per band pool
    public const double BaseHeroShare = 0.40;           // below Legend; Legend NPCs always wear a skin
    public const double DefaultCardBackShare = 0.25;    // below Legend
    public const int EliteBoardSize = 100;
    // Optional diagnostics sink (plugin boot: BepInEx log). Null = silent. Never affects generation.
    public static Action<string> Log { get; set; }
    // Bronze .. Diamond league ranges on the LadderRules scale (Bronze floor 920 = below the first star).
    public static readonly IReadOnlyList<double> LeagueFloors = Array.AsReadOnly(new[] { 920.0, 1120, 1240, 1360, 1480 });
    public static readonly IReadOnlyList<double> LeagueCeilings = Array.AsReadOnly(new[] { 1120.0, 1240, 1360, 1480, 1600 });
    public static readonly IReadOnlyList<double> LeagueWeights = Array.AsReadOnly(new[] { 0.15, 0.18, 0.20, 0.20, 0.27 });
    // Taste weights per band, columns in DeckTaste order: Netdecker, Brewer, ClassLoyalist, MemeLord, Tryhard.
    // Tryhards and netdeckers rise with rating; meme lords and brewers are common low.
    private static readonly double[][] TasteWeights = {
        new[] { 0.30, 0.18, 0.17, 0.30, 0.05 },   // Bronze
        new[] { 0.32, 0.18, 0.18, 0.24, 0.08 },   // Silver
        new[] { 0.36, 0.16, 0.18, 0.18, 0.12 },   // Gold
        new[] { 0.38, 0.14, 0.18, 0.12, 0.18 },   // Platinum
        new[] { 0.40, 0.12, 0.16, 0.08, 0.24 },   // Diamond
        new[] { 0.42, 0.10, 0.14, 0.04, 0.30 } }; // Legend
    public static IReadOnlyList<double> TasteWeightsFor(LadderBand band) => Array.AsReadOnly(TasteWeights[(int)band]);

    // Placeholder until WP2's calibration-v1 deckPowerAdj[5] is wired: strong decks pair with slightly weaker hands.
    public static double DeckPowerAdjustment(int powerTier)
    {
        switch (Math.Max(1, Math.Min(5, powerTier)))
        {
            case 1: return 40; case 2: return 20; case 3: return 0; case 4: return -20; default: return -40;
        }
    }

    // SkillCatalog.ForRating when WP2 provides it, else the highest tier whose RatingMin <= rating (lowest tier below range).
    public static SkillProfile SkillFor(SkillCatalog skills, double rating)
    {
        if (skills == null || skills.Tiers.Count == 0) return null;
        try { var tier = skills.ForRating(rating); if (tier != null) return tier; }
        catch (NotImplementedException) { }
        return skills.Tiers.LastOrDefault(t => t.RatingMin <= rating) ?? skills.Tiers[0];
    }

    // How a taste weighs a deck (power tier 1 = top meta ... 5 = meme; fun 1-5).
    public static double DeckWeight(DeckTaste taste, NpcDeckEntry deck)
    {
        int power = Math.Max(1, Math.Min(5, deck.Tags.PowerTier)), fun = Math.Max(1, Math.Min(5, deck.Tags.Fun));
        double strength = 6 - power;
        switch (taste)
        {
            case DeckTaste.Tryhard: return strength * strength * strength;
            case DeckTaste.Netdecker: return strength * strength * (0.8 + 0.1 * fun);
            case DeckTaste.Brewer: return fun * (power >= 3 ? 2.0 : 1.0);
            case DeckTaste.MemeLord: return fun * fun * (power >= 4 ? 3.0 : power == 3 ? 1.5 : 0.7);
            default: return strength * (0.8 + 0.1 * fun);   // ClassLoyalist: inside its classes
        }
    }
}

// WP3. NpcWorld.Generate body. Stages per generated NPC g draw from independent sub-streams of
// s = StableSeed.Derive(worldSeed, GeneratedTag, g), so a table change in one stage never reshuffles another.
internal static class NpcPopulationGenerator
{
    private const long StageRating = 1, StageTaste = 2, StagePool = 3, StagePersonality = 4, StageDecks = 5,
        StageCosmetics = 6, StageName = 7;

    private sealed class Plan
    {
        internal int Index, Seed, PoolSize;
        internal double Rating;
        internal LadderBand Band;
        internal DeckTaste Taste;
        internal bool OffBand;
        internal PersonalityArchetype Personality;
        internal List<string> Classes;          // class loyalists only
        internal NpcPopulationRandom Random;    // StageDecks stream: class pick, then deck picks
        internal readonly List<NpcDeckEntry> Pool = new List<NpcDeckEntry>();
    }

    private static int CapFor(int total) => Math.Max(1, (int)Math.Floor(NpcPopulationRules.ArchetypeCap * total + 1e-9));

    internal static NpcWorld Generate(NpcWorldCatalogs catalogs, long worldSeed)
    {
        if (catalogs == null) throw new ArgumentNullException(nameof(catalogs));
        if (catalogs.Names == null) throw new System.IO.InvalidDataException("NPC world needs name tables.");
        var personalities = catalogs.Personalities?.Archetypes ?? new PersonalityArchetype[0];
        if (personalities.Count == 0) throw new System.IO.InvalidDataException("NPC world needs personality archetypes.");
        var library = catalogs.Decks ?? new NpcDeckLibrary(1, 1, null, new NpcDeckEntry[0]);
        var cosmetics = catalogs.Cosmetics ?? new NpcCosmeticPool(new Dictionary<string, IReadOnlyList<string>>(),
            new Dictionary<string, string>(), new int[0]);
        var bandDecks = Enumerable.Range(0, 6).Select(b => DecksForBand(library, (LadderBand)b)).ToArray();
        var counts = Enumerable.Range(0, 6).Select(_ => new Dictionary<string, int>(StringComparer.Ordinal)).ToArray();
        var expected = new int[6];
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var all = new List<NpcIdentity>();

        // 1. Notables, as written (unknown deck ids dropped; a class deck stands in when none resolve).
        var notableIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var notable in catalogs.Notables)
        {
            if (!notableIds.Add(notable.Id)) throw new System.IO.InvalidDataException("Duplicate notable id: " + notable.Id);
            if (!taken.Add(notable.DisplayName)) throw new System.IO.InvalidDataException("Duplicate notable name: " + notable.DisplayName);
            if (catalogs.Personalities.Find(notable.PersonalityId) == null)
                throw new System.IO.InvalidDataException("Unknown personality " + notable.PersonalityId + ": " + notable.Id);
            var band = NpcWorld.BandForRating(notable.HomeRating);
            var decks = ResolveNotableDecks(notable, library, bandDecks[(int)band]);
            foreach (string id in decks) Bump(counts[(int)band], id);
            expected[(int)band] += decks.Count;
            string skill = notable.SkillId ?? NpcPopulationRules.SkillFor(catalogs.Skills,
                notable.HomeRating - SignatureAdjustment(library, decks))?.Id;
            all.Add(new NpcIdentity(notable.Id, notable.Version, notable.DisplayName, notable.Bio, true, notable.HomeRating,
                notable.PersonalityId, notable.Taste, notable.FavoriteClasses, decks,
                notable.HeroSkins.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
                notable.HeroSkinPowers.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), notable.CardBack, skill));
        }

        // 2. Plan every generated NPC (rating, taste, classes, pool size, personality) so the archetype cap knows each band's pool.
        var plans = new Plan[NpcWorld.GeneratedCount];
        for (int g = 0; g < plans.Length; g++)
        {
            int s = StableSeed.Derive(worldSeed, NpcPopulationRules.GeneratedTag, g);
            var plan = new Plan { Index = g, Seed = s, Rating = HomeRating(new NpcPopulationRandom(StableSeed.Derive(s, StageRating))) };
            plan.Band = NpcWorld.BandForRating(plan.Rating);
            var candidates = bandDecks[(int)plan.Band];
            var tasteRandom = new NpcPopulationRandom(StableSeed.Derive(s, StageTaste));
            plan.Taste = (DeckTaste)tasteRandom.Weighted(NpcPopulationRules.TasteWeightsFor(plan.Band));
            plan.Random = new NpcPopulationRandom(StableSeed.Derive(s, StageDecks));
            if (plan.Taste == DeckTaste.ClassLoyalist && candidates.Count > 0) plan.Classes = PickClasses(candidates, plan.Random);
            var poolRandom = new NpcPopulationRandom(StableSeed.Derive(s, StagePool));
            double roll = poolRandom.NextDouble();
            int size = plan.Taste == DeckTaste.Tryhard ? (roll < 0.4 ? 1 : 2) : roll < 0.25 ? 1 : roll < 0.70 ? 2 : 3;
            plan.PoolSize = Math.Min(size, candidates.Count(d => plan.Classes == null || plan.Classes.Contains(d.Class)));
            plan.OffBand = plan.Taste == DeckTaste.Brewer && library.Admitted.Any(d => !d.Tags.Allows(plan.Band));
            var personalityRandom = new NpcPopulationRandom(StableSeed.Derive(s, StagePersonality));
            plan.Personality = personalities[personalityRandom.Weighted(personalities
                .Select(p => p.BandWeight(plan.Band) * p.AffinityFor(plan.Taste)).ToArray())];
            expected[(int)plan.Band] += plan.PoolSize + (plan.OffBand ? 1 : 0);
            plans[g] = plan;
        }
        // A band whose library cannot satisfy the cap at all (fewer than ~7 decks) keeps full pools; the matchmaker's queue-level
        // archetype rule still spreads what the player meets.
        var feasible = Enumerable.Range(0, 6).Select(b => bandDecks[b].Count * CapFor(expected[b]) >= expected[b]).ToArray();
        var caps = Enumerable.Range(0, 6).Select(b => feasible[b] ? CapFor(expected[b]) : int.MaxValue).ToArray();

        // 3. Deck pools in generation order, rejecting decks whose archetype already holds its cap of the band pool.
        foreach (var plan in plans)
        {
            int b = (int)plan.Band;
            var candidates = bandDecks[b];
            var pool = plan.Pool;
            for (int k = 0; k < Math.Max(1, plan.PoolSize); k++)
            {
                var pick = Pick(candidates.Where(d => (plan.Classes == null || plan.Classes.Contains(d.Class)) && !pool.Contains(d)
                    && Count(counts[b], d.Id) < caps[b]).ToList(), plan.Taste, plan.Random);
                if (pick == null && pool.Count == 0)
                {
                    // Nothing under the cap that fits: any under-cap deck, else the least used (library too small for the cap).
                    pick = Pick(candidates.Where(d => Count(counts[b], d.Id) < caps[b]).ToList(), plan.Taste, plan.Random)
                        ?? candidates.OrderBy(d => Count(counts[b], d.Id)).FirstOrDefault();
                    if (pick != null && plan.Classes != null && !plan.Classes.Contains(pick.Class)) plan.Classes = new List<string> { pick.Class };
                }
                if (pick == null) break;
                pool.Add(pick); Bump(counts[b], pick.Id);
            }
            if (plan.OffBand)
            {
                var off = Pick(library.Admitted.Where(d => !d.Tags.Allows(plan.Band) && !pool.Contains(d)
                    && Count(counts[b], d.Id) < caps[b]).ToList(), DeckTaste.Brewer, plan.Random);
                if (off != null) { pool.Add(off); Bump(counts[b], off.Id); }
            }
        }

        // 4. Repair: pools that came up short shrink the band total, so re-check the cap against the actual pool and swap
        // (else drop) non-signature copies of an over-cap deck, newest NPC first. Signatures and notables stay as written.
        for (int b = 0; b < 6; b++)
        {
            if (!feasible[b]) continue;
            var members = plans.Where(p => (int)p.Band == b).Reverse().ToList();
            var stuck = new HashSet<string>(StringComparer.Ordinal);
            for (int guard = 0; guard < 100000; guard++)
            {
                int total = counts[b].Values.Sum(), cap = CapFor(total);
                var over = counts[b].Where(p => p.Value > cap && !stuck.Contains(p.Key)).OrderByDescending(p => p.Value)
                    .ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).FirstOrDefault();
                if (over == null) break;
                var holder = members.FirstOrDefault(p => p.Pool.Skip(1).Any(d => d.Id == over));
                // Only signatures/notables hold this deck: leave it and keep repairing the other over-cap decks.
                if (holder == null) { stuck.Add(over); continue; }
                int slot = holder.Pool.FindIndex(1, d => d.Id == over);
                var swap = bandDecks[b].Where(d => !holder.Pool.Contains(d) && Count(counts[b], d.Id) < cap
                        && (holder.Classes == null || holder.Classes.Contains(d.Class)))
                    .OrderBy(d => Count(counts[b], d.Id)).FirstOrDefault();
                counts[b][over]--;
                if (swap != null) { holder.Pool[slot] = swap; Bump(counts[b], swap.Id); }
                else holder.Pool.RemoveAt(slot);
            }
        }

        // 5. Skill, cosmetics and names.
        foreach (var plan in plans)
        {
            var pool = plan.Pool;
            var favoriteClasses = plan.Classes ?? pool.Select(d => d.Class).Distinct().ToList();
            string skill = NpcPopulationRules.SkillFor(catalogs.Skills,
                plan.Rating - (pool.Count == 0 ? 0 : NpcPopulationRules.DeckPowerAdjustment(pool[0].Tags.PowerTier)))?.Id;

            var cosmeticRandom = new NpcPopulationRandom(StableSeed.Derive(plan.Seed, StageCosmetics));
            bool skinned = plan.Band == LadderBand.Legend || cosmeticRandom.NextDouble() >= NpcPopulationRules.BaseHeroShare;
            var skins = new Dictionary<string, string>(StringComparer.Ordinal);
            var powers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string hero in pool.Select(d => d.Hero).Distinct())
            {
                IReadOnlyList<string> options;
                if (!skinned || hero == null || !cosmetics.HeroSkinsByHero.TryGetValue(hero, out options) || options.Count == 0) continue;
                string skin = options[cosmeticRandom.Index(options.Count)];
                skins[hero] = skin;
                string power;
                if (cosmetics.HeroSkinPowers.TryGetValue(skin, out power)) powers[skin] = power;
            }
            var backs = cosmetics.CardBacks.Where(back => back != 0).ToList();
            int cardBack = backs.Count == 0 || plan.Band != LadderBand.Legend
                && cosmeticRandom.NextDouble() < NpcPopulationRules.DefaultCardBackShare ? 0 : backs[cosmeticRandom.Index(backs.Count)];

            string name = catalogs.Names.Generate(StableSeed.Derive(plan.Seed, StageName), taken);
            all.Add(new NpcIdentity("npc.g." + plan.Index.ToString("D4", CultureInfo.InvariantCulture), 1, name, null, false,
                plan.Rating, plan.Personality.Id, plan.Taste, favoriteClasses, pool.Select(d => d.Id), skins, powers, cardBack, skill));
        }

        return new NpcWorld(NpcPopulationRules.WorldVersion, worldSeed, catalogs,
            all.OrderByDescending(npc => npc.HomeRating).ThenBy(npc => npc.Id, StringComparer.Ordinal));
    }

    // 85% piecewise-uniform over [920, 1600) by league weight; 15% Legend field 1600 + Exp(mean 70) truncated below 1900
    // (inverse-CDF truncation, so the cap never piles NPCs up on one number).
    private static double HomeRating(NpcPopulationRandom random)
    {
        if (random.NextDouble() < NpcPopulationRules.LegendShare)
            return Math.Min(NpcPopulationRules.LegendCap - 0.1, Math.Floor((NpcPopulationRules.LegendFloor + random.TruncatedExponential(
                NpcPopulationRules.LegendMean, NpcPopulationRules.LegendCap - NpcPopulationRules.LegendFloor)) * 10) / 10);
        int league = random.Weighted(NpcPopulationRules.LeagueWeights);
        double low = NpcPopulationRules.LeagueFloors[league], high = NpcPopulationRules.LeagueCeilings[league];
        return Math.Min(high - 0.1, Math.Floor((low + random.NextDouble() * (high - low)) * 10) / 10);
    }

    // Decks the band allows; a band the library does not cover borrows the nearest covered band (lower first).
    internal static IReadOnlyList<NpcDeckEntry> DecksForBand(NpcDeckLibrary library, LadderBand band)
    {
        for (int distance = 0; distance < 6; distance++)
            foreach (int b in new[] { (int)band - distance, (int)band + distance })
            {
                if (b < 0 || b > 5) continue;
                var decks = library.ForBand((LadderBand)b).ToList();
                if (decks.Count > 0) return decks.AsReadOnly();
            }
        return new NpcDeckEntry[0];
    }

    private static List<string> ResolveNotableDecks(NpcIdentity notable, NpcDeckLibrary library, IReadOnlyList<NpcDeckEntry> bandDecks)
    {
        var resolution = NotableDeckReport.Resolve(notable, library, bandDecks);
        if (resolution.StandIn != null)
            NpcPopulationRules.Log?.Invoke("npc-world notable " + notable.Id + ": no library deck among [" + string.Join(",", notable.DeckIds)
                + "]; stand-in " + resolution.StandIn + (resolution.OffClass ? " (OFF-CLASS: no " + string.Join("/", notable.FavoriteClasses) + " deck)" : ""));
        return resolution.Decks;
    }

    private static double SignatureAdjustment(NpcDeckLibrary library, IList<string> decks)
    {
        var signature = decks.Count == 0 ? null : library.Find(decks[0]);
        return signature == null ? 0 : NpcPopulationRules.DeckPowerAdjustment(signature.Tags.PowerTier);
    }

    // Class loyalists: one class, sometimes two, weighted by how many band decks the class has.
    private static List<string> PickClasses(IReadOnlyList<NpcDeckEntry> candidates, NpcPopulationRandom random)
    {
        var classes = candidates.Select(d => d.Class).Distinct().ToList();
        var weights = classes.Select(c => (double)candidates.Count(d => d.Class == c)).ToList();
        var chosen = new List<string>();
        int first = random.Weighted(weights);
        chosen.Add(classes[first]);
        if (classes.Count > 1 && random.NextDouble() < 0.35)
        {
            weights[first] = 0;
            chosen.Add(classes[random.Weighted(weights)]);
        }
        return chosen;
    }

    private static NpcDeckEntry Pick(IList<NpcDeckEntry> decks, DeckTaste taste, NpcPopulationRandom random) =>
        decks.Count == 0 ? null : decks[random.Weighted(decks.Select(d => NpcPopulationRules.DeckWeight(taste, d)).ToArray())];

    private static int Count(Dictionary<string, int> counts, string id) { int n; return counts.TryGetValue(id, out n) ? n : 0; }
    private static void Bump(Dictionary<string, int> counts, string id) => counts[id] = Count(counts, id) + 1;
}

// SplitMix64 stream for world generation (independent of the game RNG and of every match stream).
internal sealed class NpcPopulationRandom
{
    private ulong state;
    internal NpcPopulationRandom(int seed) { state = unchecked((ulong)(long)seed * 0xD1B54A32D192ED03UL + 0x2545F4914F6CDD1DUL); }
    internal ulong Next()
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
    internal double NextDouble() => (Next() >> 11) * (1.0 / (1UL << 53));
    internal int Index(int count)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        return (int)(Next() % (ulong)count);
    }
    internal double Exponential(double mean) => -mean * Math.Log(1 - NextDouble());
    internal double TruncatedExponential(double mean, double limit) => -mean * Math.Log(1 - NextDouble() * (1 - Math.Exp(-limit / mean)));
    internal int Weighted(IReadOnlyList<double> weights)
    {
        double total = 0;
        for (int i = 0; i < weights.Count; i++) total += Math.Max(0, weights[i]);
        if (total <= 0) return Index(weights.Count);
        double roll = NextDouble() * total;
        for (int i = 0; i < weights.Count; i++)
        {
            double w = Math.Max(0, weights[i]);
            if (roll < w) return i;
            roll -= w;
        }
        for (int i = weights.Count - 1; i >= 0; i--) if (weights[i] > 0) return i;
        return weights.Count - 1;
    }
}

// WP3. How notables' hand-written deck ids resolve against a library: unresolved ids and stand-ins (class-correct first,
// off-class only when the library has no deck of the notable's classes). Once WP1's decks-v1 lands, all counts must be 0.
public sealed class NotableDeckReport
{
    public sealed class Resolution
    {
        public string NotableId { get; }
        public IReadOnlyList<string> Unresolved { get; }
        public List<string> Decks { get; }
        public string StandIn { get; }
        public bool OffClass { get; }
        internal Resolution(string notableId, IEnumerable<string> unresolved, List<string> decks, string standIn, bool offClass)
        { NotableId = notableId; Unresolved = GoldenData.Copy(unresolved); Decks = decks; StandIn = standIn; OffClass = offClass; }
    }

    public IReadOnlyList<Resolution> Rows { get; }
    public int UnresolvedIds => Rows.Sum(r => r.Unresolved.Count);
    public int StandIns => Rows.Count(r => r.StandIn != null);
    public int OffClassStandIns => Rows.Count(r => r.OffClass);
    private NotableDeckReport(IEnumerable<Resolution> rows) { Rows = GoldenData.Copy(rows); }

    public static NotableDeckReport For(IEnumerable<NpcIdentity> notables, NpcDeckLibrary library) =>
        new NotableDeckReport((notables ?? Enumerable.Empty<NpcIdentity>()).Select(n => Resolve(n, library,
            NpcPopulationGenerator.DecksForBand(library, NpcWorld.BandForRating(n.HomeRating)))));

    internal static Resolution Resolve(NpcIdentity notable, NpcDeckLibrary library, IReadOnlyList<NpcDeckEntry> bandDecks)
    {
        var decks = notable.DeckIds.Where(id => library.Find(id)?.Admitted == true).Distinct(StringComparer.Ordinal).ToList();
        var unresolved = notable.DeckIds.Where(id => library.Find(id)?.Admitted != true).ToList();
        if (decks.Count > 0) return new Resolution(notable.Id, unresolved, decks, null, false);
        var stand = bandDecks.FirstOrDefault(d => notable.FavoriteClasses.Contains(d.Class))
            ?? library.Admitted.FirstOrDefault(d => notable.FavoriteClasses.Contains(d.Class));
        bool offClass = stand == null;
        stand = stand ?? bandDecks.FirstOrDefault() ?? library.Admitted.FirstOrDefault();
        if (stand == null) return new Resolution(notable.Id, unresolved, new List<string>(), null, false);
        return new Resolution(notable.Id, unresolved, new List<string> { stand.Id }, stand.Id, offClass);
    }

    public override string ToString() => "unresolved ids=" + UnresolvedIds + " stand-ins=" + StandIns + " off-class=" + OffClassStandIns;
}
