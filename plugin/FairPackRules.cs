using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using EngineCard = SabberStoneCore.Model.Card;

public sealed class FairPackCounters
{
    public int PacksOpened;
    public int PacksSinceLegendary;
    public int PacksSinceEpic;
    public bool FirstLegendaryReceived;
    public FairPackCounters Copy() => (FairPackCounters)MemberwiseClone();
}

public sealed class FairPackDraw
{
    public List<string> CardIds;
    public List<int> Premiums;
    public FairPackCounters After;
}

public static class FairPackRules
{
    public const int RuleVersion = 2;
    public const string Standard = "STANDARD";
    public const string Legacy = "LEGACY";
    public const string SignatureHashV2 = "123334e1e60aef43b02fc32de5e240c48d017f21ce0bd3ed712cea0a2500aa25";
    public const string ManifestHash = "7ce9533f2bd8ae14d49eec7d392a9224db134bdf592a027da80caf8838dc0611";

    // Calibration v1: per-slot integer weights / 1e9, including hard pity in measured rates.
    internal const int LegendaryWeight = 9505953, EpicWeight = 41688853, RareWeight = 200000000;
    internal const int WeightTotal = 1000000000;
    internal const int GoldenCommonWeight = 15000000, GoldenRareWeight = 20000000,
        GoldenEpicWeight = 50000000, GoldenLegendaryWeight = 120000000, SignatureWeight = 166666667;
    private static readonly Lazy<PackCatalogue> Catalogue = new Lazy<PackCatalogue>(() => {
#if ENGINE_SOURCE
        var policy = OfflinePoolPolicy.Load();
        return LoadCatalogue(policy.ActiveMetaId, policy.PackEligibleSets, policy.NewestSet);
#else
        throw new InvalidOperationException("Fair packs require the source engine's offline pool policy.");
#endif
    });
    public static string MetaHash => Catalogue.Value.MetaHash;
    public static IReadOnlyList<string> PackTypes => Catalogue.Value.PackTypes;
    public static string NewestSet => Catalogue.Value.NewestSet;
    public static bool IsKnown(string packType) => Catalogue.Value.IsKnown(packType);

    // Pinned Assembly-CSharp.dll BoosterDbId enum, carried by the generated policy catalogue.
    public static int NativeBoosterId(string packType) => Catalogue.Value.NativeBoosterId(packType);

    public static IReadOnlyList<EngineCard> Eligible(string packType, int ruleVersion) => GetPool(packType, ruleVersion).Cards;

    public static FairPackDraw Generate(string packType, int ruleVersion, ulong packSeed, long packSequence,
        FairPackCounters before, Func<string, int> acquired)
    {
        if (packSequence < 0) throw new ArgumentOutOfRangeException(nameof(packSequence));
        if (before == null) throw new ArgumentNullException(nameof(before));
        if (acquired == null) throw new ArgumentNullException(nameof(acquired));
        if (before.PacksOpened < 0 || before.PacksOpened == int.MaxValue
            || before.PacksSinceLegendary < 0 || before.PacksSinceLegendary > 39 || before.PacksSinceLegendary > before.PacksOpened
            || before.PacksSinceEpic < 0 || before.PacksSinceEpic > 9 || before.PacksSinceEpic > before.PacksOpened
            || (!before.FirstLegendaryReceived && (before.PacksOpened > 9 || before.PacksSinceLegendary != before.PacksOpened))
            || (before.FirstLegendaryReceived && before.PacksSinceLegendary >= before.PacksOpened))
            throw new ArgumentException("Invalid fair-pack counters.", nameof(before));
        Pool pool = GetPool(packType, ruleVersion);
        var rng = new PackRandom(packSeed, packSequence, packType);
        var rarities = new Rarity[5];
        // Roll every rarity before card identity: collection history cannot affect rarity RNG consumption.
        for (int i = 0; i < rarities.Length; i++)
        {
            int roll = rng.Below(WeightTotal);
            rarities[i] = roll < LegendaryWeight ? Rarity.LEGENDARY
                : roll < LegendaryWeight + EpicWeight ? Rarity.EPIC
                : i == 0 || roll < LegendaryWeight + EpicWeight + RareWeight ? Rarity.RARE : Rarity.COMMON;
        }
        if ((before.PacksSinceLegendary == 39 || (!before.FirstLegendaryReceived && before.PacksOpened == 9))
            && !rarities.Contains(Rarity.LEGENDARY)) Guarantee(rarities, Rarity.LEGENDARY);
        if (before.PacksSinceEpic == 9 && !rarities.Contains(Rarity.EPIC)) Guarantee(rarities, Rarity.EPIC);

        var result = new List<string>(5);
        var candidates = new string[pool.MaxRarityCount];
        for (int slot = 0; slot < rarities.Length; slot++)
        {
            Rarity rarity = rarities[slot];
            int cap = rarity == Rarity.LEGENDARY ? 1 : 2, count = 0;
            string[] bucket = pool.ByRarity[rarity];
            foreach (string id in bucket)
            {
                int owned = acquired(id);
                if (owned < 0) throw new ArgumentOutOfRangeException(nameof(acquired), "Acquired count outside playset: " + id);
                // Copies acquired under an older rarity (an epic pair later corrected to legendary) count as a full
                // playset. This only changes draws that used to throw, so existing draws stay identical.
                owned = Math.Min(owned, cap);
                int inPack = 0;
                foreach (string drawn in result) if (drawn == id) inPack++;
                if (owned + inPack < cap) candidates[count++] = id;
            }
            if (count == 0)
            {
                foreach (string id in bucket)
                {
                    int inPack = 0;
                    foreach (string drawn in result) if (drawn == id) inPack++;
                    if (inPack < cap) candidates[count++] = id;
                }
            }
            result.Add(candidates[rng.Below(count)]);
        }
        var after = before.Copy();
        after.PacksOpened++;
        bool legendary = rarities.Contains(Rarity.LEGENDARY), epic = rarities.Contains(Rarity.EPIC);
        after.PacksSinceLegendary = legendary ? 0 : before.PacksSinceLegendary + 1;
        after.PacksSinceEpic = epic ? 0 : before.PacksSinceEpic + 1;
        after.FirstLegendaryReceived |= legendary;
        var premiums = new List<int>(5);
        var quality = new PackRandom(packSeed, packSequence, packType + ":quality-v2");
        for (int slot = 0; slot < result.Count; slot++) {
            int premium = 0;
            if (ruleVersion >= 2) {
                int roll = quality.Below(WeightTotal);
                int golden = rarities[slot] == Rarity.COMMON ? GoldenCommonWeight : rarities[slot] == Rarity.RARE ? GoldenRareWeight
                    : rarities[slot] == Rarity.EPIC ? GoldenEpicWeight : GoldenLegendaryWeight;
                int signature = rarities[slot] == Rarity.LEGENDARY && LocalCardPremium.HasPackSignature(result[slot]) ? SignatureWeight : 0;
                if (roll < signature) premium = 3;
                else if (roll < signature + (long)(WeightTotal - signature) * golden / WeightTotal) premium = 1;
            }
            premiums.Add(premium);
        }
        return new FairPackDraw { CardIds = result, Premiums = premiums, After = after };
    }

    private static void Guarantee(Rarity[] rarities, Rarity rarity)
    {
        // Replace a low rarity first; if all five are the other high rarity, four still survive.
        int slot = Array.FindIndex(rarities, r => r == Rarity.COMMON || r == Rarity.RARE);
        rarities[slot < 0 ? 0 : slot] = rarity;
    }

    private static Pool GetPool(string packType, int version) => Catalogue.Value.GetPool(packType, version);

    internal sealed class PackCatalogue
    {
        internal readonly IReadOnlyList<string> PackTypes;
        internal readonly string MetaHash;
        internal readonly string NewestSet;
        private readonly Dictionary<string, Pool> pools;
        private readonly Dictionary<string, object> boosters;

        internal PackCatalogue(IReadOnlyList<string> types, string newestSet, string metaHash, Dictionary<string, Pool> pools,
            Dictionary<string, object> boosters)
        { PackTypes = types; NewestSet = newestSet; MetaHash = metaHash; this.pools = pools; this.boosters = boosters; }

        internal bool IsKnown(string type) => type != null && (type == Legacy || pools.ContainsKey(type));
        internal int NativeBoosterId(string type)
        {
            if (!IsKnown(type)) throw new ArgumentOutOfRangeException(nameof(type), "Unknown pack type.");
            return type == Legacy ? 1 : Convert.ToInt32(boosters[type]);
        }
        internal IReadOnlyList<EngineCard> Eligible(string type, int version) => GetPool(type, version).Cards;
        internal Pool GetPool(string type, int version)
        {
            if (version < 1 || version > RuleVersion) throw new ArgumentOutOfRangeException(nameof(version), "Unknown fair-pack rule version.");
            if (type == null || !pools.ContainsKey(type)) throw new ArgumentOutOfRangeException(nameof(type), "Not a fair-pack type.");
            return pools[type];
        }
    }

    internal sealed class Pool
    {
        internal readonly IReadOnlyList<EngineCard> Cards;
        internal readonly Dictionary<Rarity, string[]> ByRarity;
        internal readonly int MaxRarityCount;
        internal Pool(IEnumerable<EngineCard> cards)
        {
            Cards = Array.AsReadOnly(cards.OrderBy(c => c.Id, StringComparer.Ordinal).ToArray());
            var extras = ActiveMetaExtraCards();
            foreach (var card in Cards)
                // EVENT = 1941 in the pinned client; the engine's older enum has no named value.
                if ((int)card.Set == 1941 || extras.Contains(card.Id))
                    throw new InvalidOperationException("Pack pool contains excluded card: " + card.Id);
            ByRarity = Cards.GroupBy(c => c.Rarity).ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToArray());
            foreach (Rarity r in new[] { Rarity.COMMON, Rarity.RARE, Rarity.EPIC, Rarity.LEGENDARY })
                if (!ByRarity.ContainsKey(r) || ByRarity[r].Length * (r == Rarity.LEGENDARY ? 1 : 2) < 5)
                    throw new InvalidOperationException("Pack manifest cannot fill every rarity without exceeding playset.");
            MaxRarityCount = ByRarity.Values.Max(ids => ids.Length);
        }
    }

    private static IReadOnlyList<string> ActiveMetaExtraCards()
    {
#if ENGINE_SOURCE
        return OfflinePoolPolicy.Load().ExtraCards;
#else
        throw new InvalidOperationException("Fair packs require the source engine's offline pool policy.");
#endif
    }

    private static byte[] ReadResource(string name)
    {
        using (var stream = typeof(FairPackRules).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Embedded fair-pack resource missing: " + name))
        using (var output = new MemoryStream()) { stream.CopyTo(output); return output.ToArray(); }
    }

    private static string Hash(byte[] bytes)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }

    internal static PackCatalogue LoadCatalogue(string metaId, IEnumerable<string> activeSets, string newestSet)
    {
        LocalCardPremium.Validate();
        byte[] bytes = ReadResource("FairPackEligibilityV1.json");
        if (Hash(bytes) != ManifestHash) throw new InvalidOperationException("Fair-pack manifest hash mismatch.");
        var root = (Dictionary<string, object>)MiniJSON.Json.Deserialize(Encoding.UTF8.GetString(bytes));
        var sets = (Dictionary<string, object>)root["sets"];
        var boosters = (Dictionary<string, object>)root["boosters"];
        var known = ((List<object>)root["knownSets"]).Cast<string>().ToArray();
        if (Convert.ToInt32(root["ruleVersion"]) != 1 || (string)root["quality"] != "NORMAL"
            || !boosters.ContainsKey(Standard) || known.Distinct(StringComparer.Ordinal).Count() != known.Length
            || known.Any(t => !sets.ContainsKey(t) || !boosters.ContainsKey(t)))
            throw new InvalidOperationException("Fair-pack manifest metadata mismatch.");
        if (string.IsNullOrWhiteSpace(metaId) || metaId.IndexOfAny(new[] { '\r', '\n' }) >= 0 || activeSets == null)
            throw new InvalidOperationException("Fair-pack active meta requires an id and set names.");
        var active = new HashSet<string>(activeSets.Where(t => t != "CORE"), StringComparer.Ordinal);
        if (active.Count == 0) throw new InvalidOperationException("Fair-pack active meta has no expansion sets.");
        foreach (string set in active)
            if (string.IsNullOrEmpty(set) || !sets.ContainsKey(set) || !boosters.ContainsKey(set) || !known.Contains(set))
                throw new InvalidOperationException("Active set missing from pack manifest or policy: " + set);
        if (newestSet == null || !active.Contains(newestSet))
            throw new InvalidOperationException("Fair-pack newestSet must be an active expansion set.");
        var types = Array.AsReadOnly(new[] { Standard }.Concat(known.Where(active.Contains)).ToArray());
        var pools = new Dictionary<string, Pool>(StringComparer.Ordinal);
        foreach (string set in types.Skip(1))
        {
            var entry = (Dictionary<string, object>)sets[set];
            var rows = (List<object>)entry["cards"];
            var cards = new List<EngineCard>();
            foreach (Dictionary<string, object> row in rows)
            {
                string id = (string)row["id"];
                EngineCard card = Cards.FromId(id);
                if (card == null || card.Id != id || !card.Collectible || (int)card.Set != Convert.ToInt32(entry["setId"])
                    || card.Rarity.ToString() != (string)row["rarity"])
                    throw new InvalidOperationException("Pack manifest definition mismatch: " + id);
                cards.Add(card);
            }
            pools.Add(set, new Pool(cards));
        }
        pools.Add(Standard, new Pool(pools.Values.SelectMany(p => p.Cards)));
        // Canonical UTF-8: id LF, comma-separated policy-ordered expansion sets LF, newestSet LF.
        string canonicalMeta = metaId + "\n" + string.Join(",", types.Skip(1)) + "\n" + newestSet + "\n";
        return new PackCatalogue(types, newestSet, Hash(Encoding.UTF8.GetBytes(canonicalMeta)), pools, boosters);
    }

    private struct PackRandom
    {
        private ulong state;
        internal PackRandom(ulong seed, long sequence, string type)
        {
            // FNV-1a over ASCII type; SplitMix64 arithmetic is explicitly modulo 2^64.
            ulong hash = 14695981039346656037UL;
            unchecked { foreach (char c in type) { hash ^= c; hash *= 1099511628211UL; }
                state = Avalanche(seed ^ Avalanche(hash ^ Avalanche((ulong)sequence))); }
        }
        private static ulong Avalanche(ulong z)
        {
            unchecked {
                z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9UL;
                z = (z ^ (z >> 27)) * 0x94d049bb133111ebUL;
                return z ^ (z >> 31);
            }
        }
        private ulong Next()
        {
            unchecked { return Avalanche(state += 0x9e3779b97f4a7c15UL); }
        }
        internal int Below(int bound)
        {
            if (bound <= 0) throw new InvalidOperationException("Empty pack rarity pool.");
            ulong size = (ulong)bound, threshold = unchecked(0UL - size) % size, value;
            do { value = Next(); } while (value < threshold);
            return (int)(value % size);
        }
    }
}

// Mastery uses meta capabilities; pack rule 2 pins their Signature union.
internal static class LocalCardPremium
{
    private static readonly Dictionary<string, Dictionary<string, HashSet<string>>> Metas = Load();
    private static readonly HashSet<string> PackSignatures = new HashSet<string>(SignatureCards, StringComparer.Ordinal);
    internal static IEnumerable<string> SignatureCards => Metas.Values.SelectMany(row => row["signature"]).Distinct(StringComparer.Ordinal);
    static LocalCardPremium() { ValidatePackSignatures(PackSignatures); }
    internal static bool HasPackSignature(string id) => PackSignatures.Contains(id);
    internal static void ValidatePackSignatures(IEnumerable<string> ids)
    {
        string canonical = string.Join("\n", ids.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal)) + "\n";
        using (var sha = SHA256.Create())
            if (BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", "").ToLowerInvariant() != FairPackRules.SignatureHashV2)
                throw new InvalidDataException("Fair-pack rule 2 Signature hash mismatch; a changed set requires a new pack rule version.");
    }
    internal static void Validate() { if (Metas.Count == 0) throw new InvalidDataException("Missing card mastery metas."); }
    internal static bool HasSignature(string id, string meta = null) => Supports(id, "signature", meta);
    internal static bool HasDiamond(string id, string meta) => Supports(id, "diamond", meta);
    private static bool Supports(string id, string quality, string meta)
    {
#if ENGINE_SOURCE
        meta = meta ?? OfflinePoolPolicy.Load().ActiveMetaId;
#else
        meta = meta ?? "violet-hold";
#endif
        if (!Metas.TryGetValue(meta, out var row)) throw new InvalidDataException("Unknown mastery meta.");
        return row[quality].Contains(id);
    }
    private static Dictionary<string, Dictionary<string, HashSet<string>>> Load()
    {
        using (var stream = typeof(LocalCardPremium).Assembly.GetManifestResourceStream("SoloStone.Progression.card-mastery.json")) {
            if (stream == null) throw new InvalidDataException("Missing card mastery data.");
            using (var reader = new StreamReader(stream)) {
                var root = (Dictionary<string, object>)MiniJSON.Json.Deserialize(reader.ReadToEnd());
                if (Convert.ToInt32(root["schemaVersion"]) != 1) throw new InvalidDataException("Unknown card mastery schema.");
                var metas = (Dictionary<string, object>)root["metas"];
                return metas.ToDictionary(pair => pair.Key, pair => {
                    var row = (Dictionary<string, object>)pair.Value;
                    return new[] { "signature", "diamond" }.ToDictionary(quality => quality, quality => {
                        var ids = ((List<object>)row[quality]).Cast<string>().ToArray();
                        if (ids.Any(id => id == null || id.Length == 0 || id.Length > 64
                                || !id.All(c => c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '_'))
                            || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length
                            || !ids.SequenceEqual(ids.OrderBy(id => id, StringComparer.Ordinal))) throw new InvalidDataException("Invalid mastery card ids.");
                        return new HashSet<string>(ids, StringComparer.Ordinal);
                    }, StringComparer.Ordinal);
                }, StringComparer.Ordinal);
            }
        }
    }
}
