using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MiniJSON;
using System.Xml.Serialization;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using EngineCard = SabberStoneCore.Model.Card;

public sealed class LocalPetProgress { public string Pet; public int Xp; }
public sealed class LocalPackCount { public string PackType; public int Count; }
public sealed class LocalPackProgress { public string PackType; public FairPackCounters Counters = new FairPackCounters(); }
public sealed class LocalHeroFavorite { public string Class; public string HeroCardId; }
public sealed class LocalCraftCard { public string CardId; public int CountDelta; public int AcquiredBefore; public int Premium; }
public enum LocalCraftOp { Craft, Disenchant, MassDisenchant, Undo, TokenCraft, Upgrade }
public enum LocalTokenRarity { Common, Rare, Epic, Legendary }
public sealed class LocalCraftReceipt {
    public long Sequence; public LocalCraftOp Op; public List<LocalCraftCard> Cards = new List<LocalCraftCard>();
    public long DustDelta; public LocalTokenRarity? TokenRarity; public int TokenDelta; public bool Undoable;
}
public sealed class LocalLadderCalibration { public int Version; public int MatchCount; }
public sealed class LocalLadderState {
    public int StarIndex; public int Multiplier; public int StreakWins; public double? HiddenRating;
    public int PlacementGames; public int BestStarIndex; public bool LegendReached;
    public List<string> FloorsRewarded = new List<string>();
}
public sealed class LocalCardTokens { public int Common; public int Rare; public int Epic; public int Legendary; }
public sealed class LocalDeckKit { public string Id; public string MilestoneId; public int GoldCap; public long? RedeemedPurchaseSequence; }
public sealed class LocalTrophy { public string Id; public string MilestoneId; public string MatchId; public string Utc; }
public sealed class LocalLegendDeck {
    public string MatchId; public string DeckName; public string Hero;
    public List<string> Cards = new List<string>();
    public List<LocalSideboard> Sideboards = new List<LocalSideboard>();
    public bool Legacy;
}
public enum LocalTierGrantKind { Pack, Token, DeckKit, CardBack, Trophy, LegendDeck, Recipe, Challengers }
public sealed class LocalTierGrant { public LocalTierGrantKind Kind; public string Ref; public int Count; }
public sealed class LocalTierReward {
    public string MilestoneId; public bool FirstTime; public string OpponentNpcId; public string OpponentName;
    public List<LocalTierGrant> Grants = new List<LocalTierGrant>();
}
public sealed class LocalMilestoneCatchUp {
    public string Id; public List<LocalTierReward> TierRewards = new List<LocalTierReward>(); public bool Acknowledged;
}
public sealed class LocalLadderReceipt {
    public int StarsBefore; public int StarsAfter; public int MultiplierBefore; public int MultiplierAfter;
    public int StreakBefore; public int StreakAfter; public double? RatingBefore; public double? RatingAfter;
    public int StarsGained; public List<string> FloorsReached = new List<string>(); public bool LegendReached; public int LadderRuleVersion;
    // Ladder rules (LadderRuleVersion >= 2, LocalProfileLadder.cs); FloorsReached holds floor star indices ("15" ... "135").
    public string Outcome; public int BestBefore; public int BestAfter; public int PlacementBefore; public int PlacementAfter;
    public double? OpponentRating; public bool StreakBonus; public bool StoppedAtDiamondFive;
}
public sealed class LocalPurchaseCard { public string CardId; public int CountBefore; public int CountAfter; public int AcquiredBefore; }
public sealed class LocalPurchaseGrants {
    public List<LocalPackCount> Packs = new List<LocalPackCount>();
    public List<LocalPurchaseCard> Cards = new List<LocalPurchaseCard>();
    public string RecipeId; public long? DeckId; public int? CardBack; public int? Coin; public string HeroSkin;
    public string HubTheme; public int? Board; public string Pet;
    public bool ShouldSerializeCoin() => Coin.HasValue;
    // V12 field: older receipts keep their exact bytes (no nil element) when the profile is saved again.
    public bool ShouldSerializeBoard() => Board.HasValue;
    public bool ShouldSerializePet() => Pet != null;
}
public sealed class LocalPurchaseReceipt {
    public long Sequence; public string RequestId; public int CatalogVersion; public string ProductId;
    public string Kind; public int Quantity; public int GoldPaid;
    public string KitId; public int PriceBeforeKit;
    public LocalPurchaseGrants Granted = new LocalPurchaseGrants();
}
public sealed class LocalShopProduct {
    public string Id; public string Kind; public int NativeId; public int Price; public int MinQuantity; public int MaxQuantity;
    public string PackType; public string RecipeId; public int? CardBack; public int? Coin; public string HeroSkin; public string Class;
    public string Description; public string NativeStatus; public string Curation; public bool verify;
    public string HubTheme; public int? Board; public string Pet;
}
public sealed class LocalShopCatalog {
    public int Version; public List<LocalShopProduct> Products;
    internal Dictionary<string, string> ClientHeroSkins = new Dictionary<string, string>(StringComparer.Ordinal);
    internal HashSet<int> ClientCardBacks = new HashSet<int>();
    internal HashSet<int> ClientCoins = new HashSet<int>();
    internal Dictionary<string, string[]> PetForms = new Dictionary<string, string[]>(StringComparer.Ordinal);
    internal Dictionary<string, int> PetNativeIds = new Dictionary<string, int>(StringComparer.Ordinal);
    internal Dictionary<string, int[]> PetThresholds = new Dictionary<string, int[]>(StringComparer.Ordinal);
    internal Dictionary<string, int> PetMatchXp = new Dictionary<string, int>(StringComparer.Ordinal);
    // Append-only CosmeticsSource.PriceHistory: every price a cosmetic kind ever sold at.
    internal Dictionary<string, HashSet<int>> CosmeticPriceHistory = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
}

// Owner 2026-10-25: main-menu themes (native hub box dressings) and constructed game boards are shop cosmetics. Classic,
// boards 1-7 and both "Random owned" selectors are free and owned by everyone; buying a theme or board equips it.
public static class LocalHubCosmetics
{
    public const string RandomTheme = "random", ClassicTheme = "CLASSIC";
    public const int RandomBoard = 0;
    // Native BoxDressing library names, one per distinct look. The order fixes native shop ids: append only.
    internal static readonly string[] ThemeKeys = { "CLASSIC", "HALLOWS_END", "DK_LAUNCH", "WINTER_VEIL", "FIRE_FESTIVAL",
        "CATCH_UP_WILD_WEST", "ANNIVERSARY", "STARCRAFT", "CATA", "VIOLET", "BLACKEMPIRE" };
    // Owner 2026-10-25: release meta is pre-Black Empire, so its look is not sold yet. TEST_EVENT (dev alias) is never a key.
    internal static readonly string[] UnsoldThemes = { "BLACKEMPIRE" };
    public static readonly IReadOnlyList<int> DefaultBoards = Array.AsReadOnly(new[] { 1, 2, 3, 4, 5, 6, 7 });
    internal const int MaximumBoardId = 999;
    internal static bool KnownTheme(string key) => key != null && Array.IndexOf(ThemeKeys, key) >= 0;
    internal static bool FreeTheme(string key) => key == RandomTheme || key == ClassicTheme;
    internal static bool FreeBoard(int id) => id == RandomBoard || DefaultBoards.Contains(id);
    internal static bool PaidTheme(string key) => KnownTheme(key) && !FreeTheme(key);
    internal static bool PaidBoard(int id) => id > 0 && id <= MaximumBoardId && !FreeBoard(id);
    internal static int ThemeNativeId(string key) => key == RandomTheme ? 99 : KnownTheme(key) ? Array.IndexOf(ThemeKeys, key) + 1
        : throw new InvalidOperationException("Unknown hub theme.");
    internal static int BoardNativeId(int id) => id == RandomBoard ? 100000 : id;
    // Uniform pick among owned themes for one launch; a re-roll avoids the previous launch's look when another is owned.
    public static string RollTheme(IEnumerable<string> owned, string previous, int roll)
    {
        var pool = owned.Where(KnownTheme).Distinct().OrderBy(key => Array.IndexOf(ThemeKeys, key)).ToList();
        if (!pool.Contains(ClassicTheme)) pool.Insert(0, ClassicTheme);
        if (pool.Count > 1) pool.Remove(previous);
        return pool[(int)((uint)roll % (uint)pool.Count)];
    }
}

internal sealed class AchievementExclusiveCosmetic
{
    internal string Id { get; }
    internal string Kind { get; }
    internal string Name { get; }
    internal int CardBackId { get; }
    internal string HeroSkinId { get; }
    internal string Class { get; }

    internal AchievementExclusiveCosmetic(int cardBackId, string name)
    {
        Id = "cardback-" + cardBackId; Kind = "CardBack"; Name = name; CardBackId = cardBackId;
    }

    internal AchievementExclusiveCosmetic(string heroSkinId, string name, string heroClass)
    {
        Id = "heroskin-" + heroSkinId; Kind = "HeroSkin"; Name = name; HeroSkinId = heroSkinId; Class = heroClass;
    }
}

internal static class AchievementExclusiveCosmetics
{
    internal static IReadOnlyList<AchievementExclusiveCosmetic> All { get; } = Array.AsReadOnly(new[] {
        new AchievementExclusiveCosmetic(519, "Wyrmrest"),
        new AchievementExclusiveCosmetic(516, "End Time"),
        new AchievementExclusiveCosmetic(166, "Stolen Thunder"),
        new AchievementExclusiveCosmetic(452, "Warlord Draka"),
        new AchievementExclusiveCosmetic(138, "Lightforged"),
        new AchievementExclusiveCosmetic(189, "Book of Explorers"),
        new AchievementExclusiveCosmetic("HERO_11n", "Zombie Queen Scarlet", "HERO_11"),
        new AchievementExclusiveCosmetic("HERO_10bm", "Gunglaive Kurtrus", "HERO_10"),
        new AchievementExclusiveCosmetic("HERO_06k", "Guff Runetotem", "HERO_06"),
        new AchievementExclusiveCosmetic("HERO_05ao", "Hakkar the Houndmaster", "HERO_05"),
        new AchievementExclusiveCosmetic("HERO_08f", "Scholar Jaina", "HERO_08"),
        new AchievementExclusiveCosmetic("HERO_04am_Uther", "Lightbringer Uther", "HERO_04"),
        new AchievementExclusiveCosmetic("HERO_09bc", "Field Medic Xyrella", "HERO_09"),
        new AchievementExclusiveCosmetic("HERO_03i", "Chef Scabbs", "HERO_03"),
        new AchievementExclusiveCosmetic("HERO_02k", "Ten Storm Thrall", "HERO_02"),
        new AchievementExclusiveCosmetic("HERO_07e", "Felheart Gul'dan", "HERO_07"),
        new AchievementExclusiveCosmetic("HERO_01ap_Boulderfist_h", "Boulderfist the Ogre", "HERO_01")
    });

    static AchievementExclusiveCosmetics()
    {
        var rewardIds = LocalAchievementCatalog.All.Select(row => row.Reward)
            .Where(row => row.CardBackId > 0 || row.HeroSkinId != null)
            .Select(row => row.CardBackId > 0 ? "cardback-" + row.CardBackId : "heroskin-" + row.HeroSkinId);
        if (All.Count != 17 || All.Count(row => row.Kind == "CardBack") != 6 || All.Count(row => row.Kind == "HeroSkin") != 11
            || All.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() != All.Count
            || All.Any(row => string.IsNullOrWhiteSpace(row.Name) || row.Kind == "CardBack" && row.CardBackId <= 0
                || row.Kind == "HeroSkin" && (!LocalCoreProgression.Heroes.Contains(row.Class) || string.IsNullOrWhiteSpace(row.HeroSkinId)))
            || !rewardIds.ToHashSet(StringComparer.Ordinal).SetEquals(All.Select(row => row.Id)))
            throw new InvalidOperationException("Invalid achievement-exclusive cosmetic registry.");
    }

    internal static AchievementExclusiveCosmetic Find(string id) => All.SingleOrDefault(row => row.Id == id);
    internal static bool ContainsCardBack(int id) => id > 0 && All.Any(row => row.CardBackId == id);
    internal static bool ContainsHeroSkin(string id) => id != null && All.Any(row => row.HeroSkinId == id);
    internal static bool IsProduct(LocalShopProduct product) => product != null
        && (product.Kind == "CardBack" && product.CardBack.HasValue && ContainsCardBack(product.CardBack.Value)
            || product.Kind == "HeroSkin" && ContainsHeroSkin(product.HeroSkin));
    internal static LocalShopProduct DiagnosticProduct(string id)
    {
        var row = Find(id);
        return row == null ? null : new LocalShopProduct { Id = row.Id, Kind = row.Kind, CardBack = row.CardBackId > 0 ? row.CardBackId : null,
            HeroSkin = row.HeroSkinId, Class = row.Class, Description = row.Name };
    }
}

public sealed class LocalShop
{
    // Recipe/Deck NativeId bound: 72 recipes in the catalogue (2026-10-29), room for 128. Ranges it must fit:
    // PMT 1930000000/1940000000 + NativeId (each block reserves 10,000,000 ids; NativeShop.IsLocalPmtId);
    // synthetic DBF NativeShopDecks: Deck/DeckTemplate/SellableDeck FirstId + NativeId (+10000 for decks), so NativeId < 10000;
    // DeckCard/SideboardCard FirstId + (NativeId (+10000)) * 1000 + 0..999, below LastId (FirstId + 20,000,000).
    internal const int MaxDeckNativeId = 128;
    // Owner-chosen ranked-themed backs (2026-10-25); generated catalogue excludes them (tools/gen_shop_cosmetics.py).
    public static IReadOnlyDictionary<string, int> LadderExclusiveCardBacks { get; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(new Dictionary<string, int> {
            ["Silver"] = 32, ["Gold"] = 232, ["Platinum"] = 94, ["Diamond"] = 153,
            ["Legend"] = 5, ["Elite"] = 553, ["Champion"] = 500 });
    // Flat cosmetic prices before the 2026-10-25 price tiers (tools/gen_shop_cosmetics.py LEGACY_PRICES).
    internal const int LegacyHeroSkinPrice = 1000, LegacyCardBackPrice = 500;
    // Past purchases keep the price they paid: a cosmetic receipt at the flat price or at any price its kind ever sold
    // at stays valid after the item moved tier or left the curated sale.
    internal bool IsHistoricCosmeticPrice(string kind, int price) =>
        (kind == "HeroSkin" && price == LegacyHeroSkinPrice) || (kind == "CardBack" && price == LegacyCardBackPrice)
        || (kind != null && catalog.CosmeticPriceHistory.TryGetValue(kind, out var prices) && prices.Contains(price));
    private readonly LocalShopCatalog catalog;
    private readonly Dictionary<string, LocalShopProduct> productsById;
    private readonly Dictionary<string, LocalShopProduct> recipeProducts;
    private readonly Dictionary<string, string> heroSkinClasses;
    private readonly Lazy<RecipeLibrary> library;
    private static readonly Lazy<LocalShop> instance = new Lazy<LocalShop>(() => {
        var assembly = typeof(LocalShop).Assembly;
        using (var products = assembly.GetManifestResourceStream("SoloStone.Shop.catalog-v1.json"))
            return Load(products, () => {
                using (var recipes = assembly.GetManifestResourceStream("SoloStone.Shop.recipes-v1.json")) {
                    if (recipes == null) throw new InvalidOperationException("Missing recipe library resource.");
                    using (var reader = new StreamReader(recipes)) return RecipeLibrary.LoadText(reader.ReadToEnd());
                }
            });
    });
    internal static LocalShop TestOverride { get; set; }
    public static LocalShop Default => TestOverride ?? instance.Value;
    private static IEnumerable<LocalShopProduct> SupportedCheatCosmetics(string kind) => Default.catalog.Products
        .Where(product => product.Kind == kind && !product.verify);
    internal static IReadOnlyList<int> SupportedCheatCardBacks => SupportedCheatCosmetics("CardBack")
        .Select(product => product.CardBack.Value).Concat(LadderExclusiveCardBacks.Values)
        .Concat(AchievementExclusiveCosmetics.All.Where(row => row.Kind == "CardBack").Select(row => row.CardBackId))
        .Distinct().ToArray();
    internal static IReadOnlyList<int> SupportedCheatCoins => SupportedCheatCosmetics("Coin")
        .Select(product => product.Coin.Value).Distinct().ToArray();
    internal static IReadOnlyList<string> SupportedCheatHeroSkins => SupportedCheatCosmetics("HeroSkin")
        .Select(product => product.HeroSkin).Concat(LocalClassRewards.Portraits.Select(portrait => portrait.Honored))
        .Concat(AchievementExclusiveCosmetics.All.Where(row => row.Kind == "HeroSkin").Select(row => row.HeroSkinId))
        .Distinct(StringComparer.Ordinal).ToArray();
    internal static IReadOnlyList<string> SupportedCheatHubThemes => SupportedCheatCosmetics("HubTheme")
        .Select(product => product.HubTheme).Where(LocalHubCosmetics.PaidTheme).Distinct(StringComparer.Ordinal).ToArray();
    internal static IReadOnlyList<int> SupportedCheatBoards => SupportedCheatCosmetics("Board")
        .Select(product => product.Board.Value).Where(LocalHubCosmetics.PaidBoard).Distinct().ToArray();
    internal static IReadOnlyList<string> SupportedCheatPets => Default.catalog.PetForms.Keys
        .OrderBy(pet => pet, StringComparer.Ordinal).ToArray();
    internal static IReadOnlyList<string> SupportedCheatPetForms => Default.catalog.PetForms.Values
        .SelectMany(forms => forms).Distinct(StringComparer.Ordinal).OrderBy(form => form, StringComparer.Ordinal).ToArray();
    public int Version => catalog.Version;
    public IReadOnlyList<LocalShopProduct> Products => catalog.Products.Select(LocalProfile.Clone).ToArray();
    internal IEnumerable<string> HeroSkinIds => heroSkinClasses.Keys;
    internal string HeroSkinClass(string cardId) => cardId != null && (heroSkinClasses.TryGetValue(cardId, out string found)
        || catalog.ClientHeroSkins.TryGetValue(cardId, out found)) ? found : null;
    internal bool IsHeroSkinForClass(string heroClass, string cardId) =>
        cardId != null && (heroSkinClasses.TryGetValue(cardId, out string found)
            || catalog.ClientHeroSkins.TryGetValue(cardId, out found)) && found == heroClass;
    internal int RecipePrice(string id) => recipeProducts[id].Price;
    public IReadOnlyList<DeckRecipe> Recipes => Library.Recipes.Select(LocalProfile.Clone).ToArray();
    private LocalShop(LocalShopCatalog catalog, Func<RecipeLibrary> loadRecipes) {
        this.catalog = catalog;
        Validate();
        foreach (var row in AchievementExclusiveCosmetics.All)
            if (row.Kind == "CardBack" ? !catalog.ClientCardBacks.Contains(row.CardBackId)
                : !catalog.ClientHeroSkins.TryGetValue(row.HeroSkinId, out string hero) || hero != row.Class)
                throw new InvalidOperationException("Missing achievement-exclusive cosmetic metadata: " + row.Id);
        // Earn-only portraits and achievement cosmetics are never listed or sold, even if a catalogue contains them.
        this.catalog = new LocalShopCatalog { Version = catalog.Version,
            ClientHeroSkins = catalog.ClientHeroSkins, ClientCardBacks = catalog.ClientCardBacks, ClientCoins = catalog.ClientCoins, CosmeticPriceHistory = catalog.CosmeticPriceHistory,
            PetForms = catalog.PetForms, PetNativeIds = catalog.PetNativeIds, PetThresholds = catalog.PetThresholds, PetMatchXp = catalog.PetMatchXp,
            Products = catalog.Products.Where(p => !(p.Kind == "HeroSkin" && LocalClassRewards.IsEarnOnlySkin(p.HeroSkin))
                && !AchievementExclusiveCosmetics.IsProduct(p)).ToList() };
        catalog = this.catalog;
        // Per-instance indexes: TestOverride/replacement catalogues must never reuse another shop's entries.
        productsById = catalog.Products.ToDictionary(p => p.Id, StringComparer.Ordinal);
        recipeProducts = catalog.Products.Where(p => p.Kind == "Recipe").ToDictionary(p => p.RecipeId, StringComparer.Ordinal);
        heroSkinClasses = catalog.Products.Where(p => p.Kind == "HeroSkin").ToDictionary(p => p.HeroSkin, p => p.Class, StringComparer.Ordinal);
        // Earned portraits remain valid skins for their class (selection, favourites, cosmetic pool).
        foreach (var portrait in LocalClassRewards.Portraits) heroSkinClasses[portrait.Honored] = portrait.Hero;
        foreach (var portrait in AchievementExclusiveCosmetics.All.Where(row => row.Kind == "HeroSkin"))
            heroSkinClasses[portrait.HeroSkinId] = portrait.Class;
        library = new Lazy<RecipeLibrary>(() => {
            try {
                var loaded = loadRecipes() ?? throw new InvalidOperationException("Missing recipe library.");
                if (loaded.Validate().Count != 0 || loaded.Recipes.Any(r => !ValidId(r.Id))
                    || catalog.Products.Where(p => p.Kind == "Recipe" || p.Kind == "Deck").Any(p => !loaded.HasRecipe(p.RecipeId) && !loaded.IsWithheld(p.RecipeId))
                    || loaded.Recipes.Concat(loaded.Withheld).Any(r => catalog.Products.Count(p => p.RecipeId == r.Id && p.Kind == "Recipe") != 1
                        || catalog.Products.Count(p => p.RecipeId == r.Id && p.Kind == "Deck") != 1))
                    throw new InvalidOperationException("Invalid shop recipe targets.");
                return loaded;
            }
            catch (Exception error) { throw new InvalidOperationException("Recipe library unavailable; recipe and deck purchases and recipe saves are disabled.", error); }
        });
    }
    internal RecipeLibrary Library => library.Value;
    // A recipe or deck whose list waits on another branch's card or rule (RecipePending without a fallback): never offered.
    // An unavailable library answers false, so callers keep their own per-offer failure handling.
    internal bool Withheld(LocalShopProduct product)
    {
        if (product == null || (product.Kind != "Recipe" && product.Kind != "Deck")) return false;
        try { return Library.IsWithheld(product.RecipeId); }
        catch (InvalidOperationException) { return false; }
    }
    internal static LocalShop Load(Stream catalog, Func<RecipeLibrary> loadRecipes) {
        if (catalog == null) throw new InvalidOperationException("Missing shop catalogue resource.");
        var root = EmbeddedJson.Object(EmbeddedJson.Read(catalog));
        root.TryGetValue("Products", out object products);
        var parsed = new LocalShopCatalog { Version = EmbeddedJson.Integer(root, "Version") ?? 0,
            Products = products == null ? null : EmbeddedJson.Array(products).Select(value => {
                if (value == null) return null;
                var row = EmbeddedJson.Object(value);
                return new LocalShopProduct {
                    Id = EmbeddedJson.Text(row, "Id"), Kind = EmbeddedJson.Text(row, "Kind"),
                    Price = EmbeddedJson.Integer(row, "Price") ?? 0,
                    NativeId = EmbeddedJson.Integer(row, "NativeId") ?? 0,
                    MinQuantity = EmbeddedJson.Integer(row, "MinQuantity") ?? 0,
                    MaxQuantity = EmbeddedJson.Integer(row, "MaxQuantity") ?? 0,
                    PackType = EmbeddedJson.Text(row, "PackType"), RecipeId = EmbeddedJson.Text(row, "RecipeId"),
                    CardBack = EmbeddedJson.Integer(row, "CardBack", true), Coin = EmbeddedJson.Integer(row, "Coin", true), HeroSkin = EmbeddedJson.Text(row, "HeroSkin"),
                    Class = EmbeddedJson.Text(row, "Class"), Description = EmbeddedJson.Text(row, "Description"),
                    NativeStatus = EmbeddedJson.Text(row, "NativeStatus"), Curation = EmbeddedJson.Text(row, "Curation"), verify = EmbeddedJson.Boolean(row, "verify"),
                    HubTheme = EmbeddedJson.Text(row, "HubTheme"), Board = EmbeddedJson.Integer(row, "Board", true), Pet = EmbeddedJson.Text(row, "Pet")
                };
            }).ToList() };
        if (root.TryGetValue("CosmeticsSource", out object source)) {
            var metadata = EmbeddedJson.Object(source);
            if (metadata.TryGetValue("ClientHeroSkins", out object skins)) {
                var heroes = EmbeddedJson.Object(skins);
                foreach (var id in heroes.Keys) {
                    string hero = EmbeddedJson.Text(heroes, id);
                    if (!LocalCoreProgression.Heroes.Contains(hero) || !ValidId(id) || !id.StartsWith(hero, StringComparison.Ordinal) || id == hero)
                        throw new InvalidOperationException("Invalid client hero skin.");
                    parsed.ClientHeroSkins.Add(id, hero);
                }
            }
            if (metadata.TryGetValue("ClientCardBacks", out object backs))
                foreach (var value in EmbeddedJson.Array(backs)) {
                    if (!(value is long id) || id <= 0 || id > int.MaxValue || LadderExclusiveCardBacks.Values.Contains((int)id)
                        || !parsed.ClientCardBacks.Add((int)id)) throw new InvalidOperationException("Invalid client card back.");
                }
            if (metadata.TryGetValue("ClientCoins", out object coins))
                foreach (var value in EmbeddedJson.Array(coins)) {
                    if (!(value is long id) || id <= 1 || id >= 10000000 || !parsed.ClientCoins.Add((int)id))
                        throw new InvalidOperationException("Invalid client coin.");
                }
            if (metadata.TryGetValue("PriceHistory", out object history)) {
                var kinds = EmbeddedJson.Object(history);
                foreach (var kind in kinds.Keys) {
                    var prices = new HashSet<int>();
                    if (kind != "HeroSkin" && kind != "CardBack" && kind != "Coin") throw new InvalidOperationException("Invalid cosmetic price history.");
                    var rows = EmbeddedJson.Array(kinds[kind]);
                    if (rows.Count == 0
                        || rows.Any(value => !(value is long price) || price <= 0 || price > int.MaxValue / 50 || !prices.Add((int)price)))
                        throw new InvalidOperationException("Invalid cosmetic price history.");
                    parsed.CosmeticPriceHistory.Add(kind, prices);
                }
            }
        }
        if (root.TryGetValue("PetsSource", out object petSource)) {
            var sourceRows = EmbeddedJson.Object(petSource);
            foreach (var value in EmbeddedJson.Array(sourceRows["Families"])) {
                var row = EmbeddedJson.Object(value);
                string pet = EmbeddedJson.Text(row, "Pet");
                int nativeId = EmbeddedJson.Integer(row, "NativeId") ?? 0;
                var forms = EmbeddedJson.Array(row["Forms"]).Select(form => form as string).ToArray();
                var thresholds = EmbeddedJson.Array(row["Thresholds"]).Select(value => value is long n && n >= 0 && n <= int.MaxValue ? (int)n : -1).ToArray();
                int matchXp = EmbeddedJson.Integer(row, "MatchXp") ?? 0;
                if (!ValidId(pet) || nativeId <= 0 || nativeId >= 1000000 || forms.Length == 0 || forms[0] != pet
                    || forms.Any(form => !ValidId(form)) || forms.Distinct(StringComparer.Ordinal).Count() != forms.Length
                    || thresholds.Length != forms.Length || thresholds[0] != 0 || thresholds.Skip(1).Where((n, i) => n <= thresholds[i]).Any() || matchXp <= 0
                    || parsed.PetNativeIds.Values.Contains(nativeId) || parsed.PetForms.Values.SelectMany(f => f).Intersect(forms).Any())
                    throw new InvalidOperationException("Invalid client pet family.");
                parsed.PetForms.Add(pet, forms); parsed.PetNativeIds.Add(pet, nativeId);
                parsed.PetThresholds.Add(pet, thresholds); parsed.PetMatchXp.Add(pet, matchXp);
            }
        }
        return new LocalShop(parsed, loadRecipes);
    }
    public static LocalShop Load(Stream catalog, Stream recipes) {
        string text = null;
        if (recipes != null) using (var reader = new StreamReader(recipes)) text = reader.ReadToEnd();
        return Load(catalog, () => RecipeLibrary.LoadText(text));
    }
    internal string PetFamily(string cardId) => cardId == null ? null : catalog.PetForms.FirstOrDefault(pair => pair.Value.Contains(cardId)).Key;
    internal bool KnownPet(string pet) => pet != null && catalog.PetForms.ContainsKey(pet);
    internal IReadOnlyList<string> FormsForPet(string pet) => catalog.PetForms.TryGetValue(pet, out var forms) ? forms : new string[0];
    internal int PetNativeId(string pet) => catalog.PetNativeIds.TryGetValue(pet, out int id) ? id : throw new InvalidOperationException("Unknown pet family.");
    internal IReadOnlyList<int> ThresholdsForPet(string pet) => catalog.PetThresholds[pet];
    internal int PetXpCap(string pet) => catalog.PetThresholds[pet].Last();
    internal int PetLevel(string pet, int xp) => catalog.PetThresholds[pet].Count(threshold => threshold <= xp);
    internal int PetLevelXp(string pet, int xp) => xp >= PetXpCap(pet) ? 0 : xp - catalog.PetThresholds[pet][PetLevel(pet, xp) - 1];
    // Pet growth rules are frozen into each settled receipt (LocalMatchRecord.PetRules) so retuning the policy, MatchXp,
    // thresholds or the eligibility code never invalidates history. Every rule number keeps its case below.
    //   rule=1  flat award per eligible match (legacy; also preset C): rule=1;xp=100;cap=6750
    //   rule=2  event XP from the recorded counters (PetSmall/PetMedium/PetLarge), one tick per event, bound `events=`.
    //   rule=3  rule 2 plus `liketicks=`/`treatticks=` (ticks one like / one feed adds) and the bound counts ticks (`ticks=`).
    //           Rule 2 receipts (settled before the 20-XP ruling) validate unchanged.
    internal const int FlatPetRule = 1, EventPetRule = 2, TickPetRule = 3;
    // Receipts settled before stamping (V14 pets, 58376ced..) all used these rules.
    internal const string LegacyPetRules = "rule=1;xp=100;cap=6750";
    internal int PetXpAward(string pet, LocalMatchRecord match) => PetRuleAward(ParsePetRules(PetRules(pet)), match);
    // pacingPercent: the one-shot per-match pacing test knob frozen at Begin (null = the policy's own, 100 today).
    internal string PetRules(string pet, int? pacingPercent = null) => PetXpPolicy.Current.Flat
        ? string.Format(CultureInfo.InvariantCulture, "rule=1;xp={0};cap={1}", catalog.PetMatchXp[pet], PetXpCap(pet))
        : PetXpPolicy.Current.RuleString(PetXpCap(pet), PetXpPolicy.Current.MaxTicks(PetNativeId(pet)), pacingPercent);
    private struct PetRule {
        internal int Rule, Xp, Cap, Small, Medium, Large, GameCap, Turns, Percent, Events, LikeTicks, TreatTicks;
        internal bool PayConceded, PayShort;
        internal string Like, Treat;
    }
    private static readonly string[] PetCounterNames = { "small", "medium", "large" };
    private static PetRule ParsePetRules(string rules) {
        var parts = (rules ?? "").Split(';');
        var rule = new PetRule();
        if (parts.Length >= 1 && PetRulePart(parts[0], "rule", out rule.Rule) && rule.Rule == FlatPetRule) {
            if (parts.Length != 3 || !PetRulePart(parts[1], "xp", out rule.Xp) || !PetRulePart(parts[2], "cap", out rule.Cap) || rule.Xp <= 0 || rule.Cap <= 0
                || rules != string.Format(CultureInfo.InvariantCulture, "rule=1;xp={0};cap={1}", rule.Xp, rule.Cap))
                throw new InvalidOperationException("Invalid pet growth rules.");
            return rule;
        }
        if (rule.Rule == TickPetRule) {
            int concede, shortGame;
            if (parts.Length != 15 || !PetRuleText(parts[1], "like", out rule.Like) || !PetRuleText(parts[2], "treat", out rule.Treat)
                || !PetRulePart(parts[3], "liketicks", out rule.LikeTicks) || !PetRulePart(parts[4], "treatticks", out rule.TreatTicks)
                || !PetRulePart(parts[5], "small", out rule.Small) || !PetRulePart(parts[6], "medium", out rule.Medium)
                || !PetRulePart(parts[7], "large", out rule.Large) || !PetRulePart(parts[8], "gamecap", out rule.GameCap)
                || !PetRulePart(parts[9], "concede", out concede) || !PetRulePart(parts[10], "short", out shortGame)
                || !PetRulePart(parts[11], "turns", out rule.Turns) || !PetRulePart(parts[12], "mult", out rule.Percent)
                || !PetRulePart(parts[13], "ticks", out rule.Events) || !PetRulePart(parts[14], "cap", out rule.Cap)
                || !PetCounterNames.Contains(rule.Like) || !PetCounterNames.Contains(rule.Treat)
                || rule.LikeTicks <= 0 || rule.LikeTicks > 10 || rule.TreatTicks <= 0 || rule.TreatTicks > 10
                || rule.Small <= 0 || rule.Medium <= 0 || rule.Large <= 0 || rule.Small > 1000 || rule.Medium > 1000 || rule.Large > 1000
                || rule.GameCap > 1000000 || concede > 1 || shortGame > 1 || rule.Turns > 1000
                || rule.Percent <= 0 || rule.Percent > 100000 || rule.Events <= 0 || rule.Events > 1000 || rule.Cap <= 0)
                throw new InvalidOperationException("Invalid pet growth rules.");
            rule.PayConceded = concede == 1; rule.PayShort = shortGame == 1;
            if (rules != PetXpPolicy.FormatTicks(rule.Like, rule.Treat, rule.LikeTicks, rule.TreatTicks, rule.Small, rule.Medium, rule.Large,
                    rule.GameCap, rule.PayConceded, rule.PayShort, rule.Turns, rule.Percent, rule.Events, rule.Cap))
                throw new InvalidOperationException("Invalid pet growth rules.");
            return rule;
        }
        if (rule.Rule == EventPetRule) {
            int concede, shortGame;
            if (parts.Length != 13 || !PetRuleText(parts[1], "like", out rule.Like) || !PetRuleText(parts[2], "treat", out rule.Treat)
                || !PetRulePart(parts[3], "small", out rule.Small) || !PetRulePart(parts[4], "medium", out rule.Medium)
                || !PetRulePart(parts[5], "large", out rule.Large) || !PetRulePart(parts[6], "gamecap", out rule.GameCap)
                || !PetRulePart(parts[7], "concede", out concede) || !PetRulePart(parts[8], "short", out shortGame)
                || !PetRulePart(parts[9], "turns", out rule.Turns) || !PetRulePart(parts[10], "mult", out rule.Percent)
                || !PetRulePart(parts[11], "events", out rule.Events) || !PetRulePart(parts[12], "cap", out rule.Cap)
                || !PetCounterNames.Contains(rule.Like) || !PetCounterNames.Contains(rule.Treat)
                || rule.Small <= 0 || rule.Medium <= 0 || rule.Large <= 0 || rule.Small > 1000 || rule.Medium > 1000 || rule.Large > 1000
                || rule.GameCap > 1000000 || concede > 1 || shortGame > 1 || rule.Turns > 1000
                || rule.Percent <= 0 || rule.Percent > 100000 || rule.Events <= 0 || rule.Events > 1000 || rule.Cap <= 0)
                throw new InvalidOperationException("Invalid pet growth rules.");
            rule.PayConceded = concede == 1; rule.PayShort = shortGame == 1;
            if (rules != PetXpPolicy.FormatEvents(rule.Like, rule.Treat, rule.Small, rule.Medium, rule.Large, rule.GameCap, rule.PayConceded,
                    rule.PayShort, rule.Turns, rule.Percent, rule.Events, rule.Cap))
                throw new InvalidOperationException("Invalid pet growth rules.");
            return rule;
        }
        throw new InvalidOperationException("Invalid pet growth rules.");
    }
    private static int PetRuleAward(PetRule rule, LocalMatchRecord match) {
        bool finished = match.Result == "Won" || match.Result == "Lost" || match.Result == "Tied";
        if (rule.Rule == FlatPetRule)
            return !match.Conceded && match.Turns >= 4 && finished ? rule.Xp : 0;
        // Event XP: the recorded counters (a missing field is zero), under the eligibility knobs frozen in the rule string.
        if (!finished || match.Conceded && !rule.PayConceded || !rule.PayShort && match.Turns < rule.Turns) return 0;
        long events = (long)Math.Max(0, match.PetSmall ?? 0) * rule.Small + (long)Math.Max(0, match.PetMedium ?? 0) * rule.Medium
            + (long)Math.Max(0, match.PetLarge ?? 0) * rule.Large;
        if (rule.GameCap > 0) events = Math.Min(events, rule.GameCap);
        // Pacing multiplier hook (backlog #52): a percent, 100 today; whole XP, rounded down.
        return (int)Math.Min(int.MaxValue, events * rule.Percent / 100);
    }
    // XP after one settled match under the recorded rules. Never below the starting XP, so a lowered cap keeps progress.
    internal static int PetXpAfter(string rules, int before, LocalMatchRecord match) {
        var rule = ParsePetRules(rules);
        return Math.Max(before, (int)Math.Min(rule.Cap, (long)before + PetRuleAward(rule, match)));
    }
    internal struct ParsedPetRule { internal string Like, Treat; internal int Events; }
    internal static ParsedPetRule ParseRuleForValidation(string rules) {
        var rule = ParsePetRules(rules);
        return new ParsedPetRule { Like = rule.Like, Treat = rule.Treat, Events = rule.Events };
    }
    // Whether the recorded rules are the event rules (counter fields required on the receipt).
    internal static bool PetRulesUseCounters(string rules) => rules != null && ParsePetRules(rules).Rule >= EventPetRule;
    private static bool PetRulePart(string part, string name, out int value) {
        value = 0;
        return part.StartsWith(name + "=", StringComparison.Ordinal)
            && int.TryParse(part.Substring(name.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
    private static bool PetRuleText(string part, string name, out string value) {
        value = part.StartsWith(name + "=", StringComparison.Ordinal) ? part.Substring(name.Length + 1) : null;
        return value != null;
    }
    internal bool PetUnlocked(string cardId, int xp) {
        string family = PetFamily(cardId);
        return family != null && xp >= catalog.PetThresholds[family][Array.IndexOf(catalog.PetForms[family], cardId)];
    }
    public LocalShopProduct Product(string id) => LocalProfile.Clone(ProductRef(id));
    // Read-only internal lookups for quote computation: the catalogue's own objects, never handed out or mutated.
    internal LocalShopProduct ProductRef(string id) => id != null && productsById.TryGetValue(id, out var product) ? product
        : throw new InvalidOperationException("Unknown shop product.");
    // V10 receipts are immutable; retired placeholders remain readable, never purchasable.
    internal LocalShopProduct ReceiptProduct(string id)
    {
        if (AchievementExclusiveCosmetics.Find(id) != null)
            throw new InvalidOperationException("Achievement-exclusive cosmetics cannot have purchase receipts.");
        var current = id != null && productsById.TryGetValue(id, out var product) ? product : null;
        if (current != null) return LocalProfile.Clone(current);
        if (id == "hero-mage") return Product("heroskin-HERO_08a");
        if (id == "hero-warrior") return Product("heroskin-HERO_01a");
        // Skins that became earn-only keep their historic purchase receipts valid whatever the catalogue lists.
        if (id != null && id.StartsWith("heroskin-", StringComparison.Ordinal) && LocalClassRewards.IsEarnOnlySkin(id.Substring(9)))
            return new LocalShopProduct { Id = id, Kind = "HeroSkin", Price = LegacyHeroSkinPrice, MinQuantity = 1, MaxQuantity = 1,
                HeroSkin = id.Substring(9), Class = LocalClassRewards.EarnOnlySkinClass(id.Substring(9)) };
        if (id == "card-back-1" || id == "card-back-2")
            return new LocalShopProduct { Id = id, Kind = "CardBack", Price = LegacyCardBackPrice, MinQuantity = 1, MaxQuantity = 1,
                CardBack = id == "card-back-1" ? 1 : 2 };
        // Sale curation cannot revoke valid historical cosmetic receipts.
        if (id != null && id.StartsWith("heroskin-", StringComparison.Ordinal)
            && catalog.ClientHeroSkins.TryGetValue(id.Substring(9), out string hero))
            return new LocalShopProduct { Id = id, Kind = "HeroSkin", Price = LegacyHeroSkinPrice, MinQuantity = 1, MaxQuantity = 1,
                HeroSkin = id.Substring(9), Class = hero };
        if (id != null && id.StartsWith("cardback-", StringComparison.Ordinal)
            && int.TryParse(id.Substring(9), out int back) && id == "cardback-" + back && catalog.ClientCardBacks.Contains(back))
            return new LocalShopProduct { Id = id, Kind = "CardBack", Price = LegacyCardBackPrice, MinQuantity = 1, MaxQuantity = 1, CardBack = back };
        if (id != null && id.StartsWith("coin-", StringComparison.Ordinal)
            && int.TryParse(id.Substring(5), out int coin) && id == "coin-" + coin && catalog.ClientCoins.Contains(coin))
            return new LocalShopProduct { Id = id, Kind = "Coin", MinQuantity = 1, MaxQuantity = 1, Coin = coin };
        // A theme or board later withdrawn from sale (or found broken) keeps its receipts valid.
        if (id != null && id.StartsWith("theme-", StringComparison.Ordinal) && LocalHubCosmetics.PaidTheme(id.Substring(6)))
            return new LocalShopProduct { Id = id, Kind = "HubTheme", Price = 1000, MinQuantity = 1, MaxQuantity = 1, HubTheme = id.Substring(6) };
        if (id != null && id.StartsWith("board-", StringComparison.Ordinal) && int.TryParse(id.Substring(6), out int board)
            && id == "board-" + board && LocalHubCosmetics.PaidBoard(board))
            return new LocalShopProduct { Id = id, Kind = "Board", Price = 500, MinQuantity = 1, MaxQuantity = 1, Board = board };
        if (id != null && id.StartsWith("pet-", StringComparison.Ordinal) && KnownPet(id.Substring(4)))
            return new LocalShopProduct { Id = id, Kind = "Pet", Price = 5000, MinQuantity = 1, MaxQuantity = 1,
                Pet = id.Substring(4), NativeId = PetNativeId(id.Substring(4)) };
        return Product(id);
    }
    public DeckRecipe Recipe(string id) => LocalProfile.Clone(RecipeRef(id));
    internal DeckRecipe RecipeRef(string id) => Library.FindRecipe(id) ?? throw new InvalidOperationException("Unknown shop recipe.");
    internal const string GrantOnlyDeckMessage = "This deck needs event cards you don't own. They come from the welcome gift and can't be bought.";
    private static readonly HashSet<string> grantOnlyIds = new HashSet<string>(LocalWelcomeGift.HistoricalCardIds, StringComparer.Ordinal);
    internal static bool GrantOnly(string id) => grantOnlyIds.Contains(id);
    internal static string DeckPurchaseBlocker(RecipeMissingCopies missing) =>
        missing.Cards.Any(card => card.Missing > 0 && GrantOnly(card.Id)) ? GrantOnlyDeckMessage : null;
    public static int CopyPrice(EngineCard card) => GrantOnly(card.Id)
        ? throw new InvalidOperationException(GrantOnlyDeckMessage) : LocalCardCatalog.IsCore(card.Id)
        ? throw new InvalidOperationException("Core cards unlock through class progression.") : CopyPrice(card.Rarity);
    // Decision 2026-10-25 (owner may overrule): a ready-made deck is playable at once, so it sells every missing copy,
    // Core copies class level has not unlocked yet included, at the per-rarity price. Only a Core card with no unlock
    // schedule entry stays missing and unpriced. Grant-only cards block purchases; recipes still grant no cards.
    internal static int DeckGrant(RecipeCardCopies card) =>
        LocalCardCatalog.IsCore(card.Id) && !LocalCoreProgression.IsAvailable(card.Id) ? 0 : card.Missing;
    internal static int CopyPrice(Rarity rarity) {
        switch (rarity) {
            case Rarity.COMMON: return 20; case Rarity.RARE: return 50;
            case Rarity.EPIC: return 200; case Rarity.LEGENDARY: return 800;
            default: throw new InvalidOperationException("Card is not sold by the shop.");
        }
    }
    // Pack catalogue and active meta are process constants; rebuild these together if runtime reload is introduced.
    private static readonly Lazy<HashSet<CardSet>> standardSets = new Lazy<HashSet<CardSet>>(() =>
        new HashSet<CardSet>(FairPackRules.Eligible(FairPackRules.Standard, FairPackRules.RuleVersion).Select(card => card.Set)));
#if ENGINE_SOURCE
    private static readonly Lazy<HashSet<string>> activeExtraIds = new Lazy<HashSet<string>>(() =>
        new HashSet<string>(LocalCardCatalog.ActiveMetaExtraCards, StringComparer.Ordinal));
    internal static bool IsActiveExtraCard(string id) => activeExtraIds.Value.Contains(id);
#endif
    internal static bool IsStandardCard(EngineCard card) => card != null && card.Collectible && card.Implemented
        && (LocalCardCatalog.IsCore(card.Id) || standardSets.Value.Contains(card.Set)
#if ENGINE_SOURCE
            || activeExtraIds.Value.Contains(card.Id)
#endif
        );
    internal static IEnumerable<EngineCard> StandardCollectionCards =>
        FairPackRules.Eligible(FairPackRules.Standard, FairPackRules.RuleVersion)
#if ENGINE_SOURCE
        // Pack exclusions still belong to the collection and can be bought in ready-made decks.
        .Concat(OfflinePoolPolicy.Load().Members.Where(member => member.Scope == "Standard").Select(member => member.CardId)
            .Concat(SabberStoneCore.CardSets.Client.StandardSets.CollectibleIds).Select(Cards.FromId).Where(IsStandardCard))
        .Concat(Cards.All.Where(card => IsActiveExtraCard(card.Id) && IsStandardCard(card)))
#endif
        ;
    internal static void ValidateDeck(string name, string hero, IEnumerable<string> ids, IEnumerable<LocalSideboard> sideboards, bool complete, bool stored = false, bool standardOnly = true) {
        var cards = ids?.ToArray() ?? throw new InvalidOperationException("Missing deck cards.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 32 || name.Any(char.IsControl) || !LocalCoreProgression.Heroes.Contains(hero)
            || cards.Length > (complete ? LocalDecks.DeckSize(cards) : LocalDecks.DraftLimit(cards)) || (complete && cards.Length != LocalDecks.DeckSize(cards))
            || cards.Any(id => !LocalProfile.KnownLedgerCard(id))
            // A stored deck may hold copies allowed under an older rarity; play and saving enforce today's limit.
            || cards.GroupBy(id => id).Any(group => group.Count() > (stored ? LocalEconomy.MaximumPlayset : LocalEconomy.PlayableCap(Cards.FromId(group.Key))))
            || (!stored && !LocalCardCatalog.WithinCopyLimits(cards))
            || cards.Any(id => (standardOnly && !IsStandardCard(Cards.FromId(id))) || !LocalDecks.IsAllowedInDeck(Cards.FromId(id), LocalDecks.HeroClass(hero))))
            throw new InvalidOperationException((standardOnly ? "Invalid Standard recipe deck: " : "Invalid local deck: ") + name);
        LocalDecks.ValidateRunes(cards.Select(Cards.FromId), LocalDecks.HeroClass(hero));
        new LocalDeck(name, hero, cards).WithSideboards(sideboards, complete);
    }
    private void Validate() {
        if (catalog == null || catalog.Version != 1 || catalog.Products == null || catalog.Products.Count == 0
            || catalog.Products.Any(p => p == null || !ValidId(p.Id))
            || catalog.Products.Select(p => p.Id).Distinct().Count() != catalog.Products.Count)
            throw new InvalidOperationException("Invalid shop catalogue.");
        foreach (var p in catalog.Products) {
            bool free = p.Kind == "Pet" ? p.Pet == null : p.Kind == "HubTheme" ? LocalHubCosmetics.FreeTheme(p.HubTheme) : p.Kind == "Board" && p.Board.HasValue && LocalHubCosmetics.FreeBoard(p.Board.Value);
            if (!new[] { "Pack", "Recipe", "Deck", "CardBack", "Coin", "HeroSkin", "HubTheme", "Board", "Pet" }.Contains(p.Kind)
                || p.Price < (p.Kind == "Deck" || free ? 0 : 1) || (free && p.Price != 0) || p.Price > int.MaxValue / 50 || p.MinQuantity != 1 || p.MaxQuantity != (p.Kind == "Pack" ? 50 : 1)
                || (p.Kind == "HubTheme" ? !(LocalHubCosmetics.KnownTheme(p.HubTheme) || p.HubTheme == LocalHubCosmetics.RandomTheme)
                    || LocalHubCosmetics.UnsoldThemes.Contains(p.HubTheme) : p.HubTheme != null)
                || (p.Kind == "Board" ? !p.Board.HasValue || p.Board.Value < 0 || p.Board.Value > LocalHubCosmetics.MaximumBoardId : p.Board.HasValue)
                || (p.Kind == "Pet" ? (p.Pet == null ? p.NativeId != 0 || p.Id != "pet-none" : !KnownPet(p.Pet) || p.NativeId != PetNativeId(p.Pet) || p.Id != "pet-" + p.Pet) : p.Pet != null)
                || ((p.Kind == "Recipe" || p.Kind == "Deck") ? p.NativeId < 1 || p.NativeId > LocalShop.MaxDeckNativeId : p.Kind != "Pet" && p.NativeId != 0)
                || string.IsNullOrWhiteSpace(p.Description)
                || (p.Kind == "Pack" ? !FairPackRules.PackTypes.Contains(p.PackType) : p.PackType != null)
                || ((p.Kind == "Recipe" || p.Kind == "Deck") ? !ValidId(p.RecipeId) : p.RecipeId != null)
                || (p.Kind == "CardBack" ? !p.CardBack.HasValue || p.CardBack.Value <= 0 || LadderExclusiveCardBacks.Values.Contains(p.CardBack.Value) : p.CardBack.HasValue)
                || (p.Kind == "Coin" ? !p.Coin.HasValue || p.Coin.Value <= 1 || p.Coin.Value >= 10000000
                    || p.Id != "coin-" + p.Coin.Value || !catalog.ClientCoins.Contains(p.Coin.Value) : p.Coin.HasValue)
                || (p.Kind == "HeroSkin" ? string.IsNullOrWhiteSpace(p.HeroSkin) || !LocalCoreProgression.Heroes.Contains(p.Class)
                    || p.HeroSkin == p.Class : p.HeroSkin != null || p.Class != null))
                throw new InvalidOperationException("Invalid shop product.");
        }
        if (catalog.Products.Where(p => p.Kind == "Recipe" || p.Kind == "Deck")
                .GroupBy(p => p.NativeId).Any(g => g.Count() != 2 || g.Select(p => p.Kind).Distinct().Count() != 2
                    || g.Select(p => p.RecipeId).Distinct().Count() != 1)
            || catalog.Products.Count(p => p.Kind == "Pack") != FairPackRules.PackTypes.Count
            || catalog.Products.Where(p => p.Kind == "Pack").Select(p => p.PackType).Distinct().Count() != FairPackRules.PackTypes.Count
            || catalog.Products.Where(p => p.Kind == "CardBack").GroupBy(p => p.CardBack).Any(g => g.Count() > 1)
            || catalog.Products.Where(p => p.Kind == "Coin").GroupBy(p => p.Coin).Any(g => g.Count() > 1)
            || catalog.Products.Where(p => p.Kind == "HeroSkin").GroupBy(p => p.HeroSkin).Any(g => g.Count() > 1)
            || catalog.Products.Where(p => p.Kind == "HubTheme").GroupBy(p => p.HubTheme).Any(g => g.Count() > 1)
            || catalog.Products.Where(p => p.Kind == "Board").GroupBy(p => p.Board).Any(g => g.Count() > 1)
            || catalog.Products.Where(p => p.Kind == "Pet").GroupBy(p => p.Pet).Any(g => g.Count() > 1)
            || catalog.Products.Any(p => p.Kind == "Pet" && p.Pet == null && p.verify)
            || catalog.Products.Any(p => (p.Kind == "HubTheme" || p.Kind == "Board") && p.verify
                && (p.Kind == "HubTheme" ? LocalHubCosmetics.FreeTheme(p.HubTheme) : LocalHubCosmetics.FreeBoard(p.Board.Value)))
            || catalog.Products.Where(p => p.Kind == "Recipe" || p.Kind == "Deck").Any(p =>
                catalog.Products.Count(r => r.Kind == "Recipe" && r.RecipeId == p.RecipeId) != 1
                || catalog.Products.Count(r => r.Kind == "Deck" && r.RecipeId == p.RecipeId) != 1))
            throw new InvalidOperationException("Shop product targets must be unique and complete.");
    }
    internal static bool ValidId(string id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 128 && !id.Any(char.IsControl);
}

public sealed partial class LocalProfile
{
    public IReadOnlyList<LocalPurchaseReceipt> Purchases => data.Purchases.Select(Clone).ToArray();
    public long PurchaseSequence => data.PurchaseSequence;
    public IReadOnlyList<string> OwnedRecipes => data.OwnedRecipes.ToArray();
    public IReadOnlyList<int> OwnedCardBacks => data.OwnedCardBacks.Concat(profileOptions.AllCosmetics
        ? LocalShop.SupportedCheatCardBacks : Array.Empty<int>()).Distinct().ToArray();
    public IReadOnlyList<string> OwnedHeroSkins => data.OwnedHeroSkins.Concat(profileOptions.AllCosmetics
        ? LocalShop.SupportedCheatHeroSkins : Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
    public IReadOnlyList<int> OwnedCoins => data.OwnedCoins.Concat(profileOptions.AllCosmetics
        ? LocalShop.SupportedCheatCoins : Array.Empty<int>()).Distinct().OrderBy(id => id).ToArray();
    public IReadOnlyList<int> FavoriteCoins => data.FavoriteCoins.OrderBy(id => id).ToArray();
    public IReadOnlyList<int> FavoriteCardBacks => data.FavoriteCardBacks.OrderBy(id => id).ToArray();
    public IReadOnlyList<string> OwnedHubThemes => data.OwnedHubThemes.Concat(profileOptions.AllCosmetics
        ? LocalShop.SupportedCheatHubThemes : Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
    public IReadOnlyList<int> OwnedBoards => data.OwnedBoards.Concat(profileOptions.AllCosmetics
        ? LocalShop.SupportedCheatBoards : Array.Empty<int>()).Distinct().ToArray();
    // "random" or an owned theme key; Classic for profiles that never chose.
    public string SelectedHubTheme => data.SelectedHubTheme ?? LocalHubCosmetics.ClassicTheme;
    // 0 = random among owned boards, else an owned board id.
    public int SelectedBoard => data.SelectedBoard;
    public IReadOnlyList<string> OwnedPets => data.OwnedPets.Concat(profileOptions.AllCosmetics
        ? LocalShop.SupportedCheatPets : Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
    public string SelectedPet => data.SelectedPet;
    public IReadOnlyList<LocalPetProgress> PetProgress => data.PetProgress.Select(row => new LocalPetProgress { Pet = row.Pet, Xp = row.Xp }).ToArray();
    public bool OwnsPet(string pet) {
        if (pet == null) return true;
        string family = LocalShop.Default.KnownPet(pet) ? pet : LocalShop.Default.PetFamily(pet);
        return family != null && (data.OwnedPets.Contains(family)
            || profileOptions.AllCosmetics && LocalShop.SupportedCheatPets.Contains(family));
    }
    public bool CanUsePetForm(string cardId) => cardId == null || OwnsPet(cardId)
        && (profileOptions.AllCosmetics && LocalShop.SupportedCheatPetForms.Contains(cardId)
            || LocalShop.Default.PetUnlocked(cardId, PetXp(LocalShop.Default.PetFamily(cardId))));
    public string EffectiveSelectedPet => CanUsePetForm(data.SelectedPet) ? data.SelectedPet : null;
    public int PetXp(string pet) => data.PetProgress.SingleOrDefault(row => row.Pet == pet)?.Xp ?? 0;
    public int PetLevel(string pet) => LocalShop.Default.PetLevel(pet, PetXp(pet));
    public int PetLevelXp(string pet) => LocalShop.Default.PetLevelXp(pet, PetXp(pet));
    public bool OwnsHubTheme(string key) => LocalHubCosmetics.FreeTheme(key) || (key != null && (data.OwnedHubThemes.Contains(key)
        || profileOptions.AllCosmetics && LocalShop.SupportedCheatHubThemes.Contains(key)));
    public bool OwnsBoard(int id) => LocalHubCosmetics.FreeBoard(id) || data.OwnedBoards.Contains(id)
        || profileOptions.AllCosmetics && LocalShop.SupportedCheatBoards.Contains(id);
    // Every concrete theme / board this profile may show (free ones included); never the random selectors.
    public IReadOnlyList<string> UsableHubThemes => new[] { LocalHubCosmetics.ClassicTheme }.Concat(OwnedHubThemes).Distinct(StringComparer.Ordinal).ToArray();
    public IReadOnlyList<int> UsableBoards => LocalHubCosmetics.DefaultBoards.Concat(OwnedBoards).Distinct().ToArray();
    public IReadOnlyDictionary<string, string> FavoriteHeroSkins => data.FavoriteHeroSkins.ToDictionary(f => f.Class, f => f.HeroCardId);
    // The chosen portrait for a class; a stored favorite equal to the vanilla hero means "no skin".
    public string FavoriteHero(string baseHero) =>
        data.FavoriteHeroSkins.FirstOrDefault(f => f.Class == baseHero)?.HeroCardId ?? baseHero;
    // Both native deck display and match start use override, then class favourite, then base hero.
    internal string ResolveHero(string baseHero, string heroSkin) =>
        heroSkin != null && ValidFavorite(data, baseHero, heroSkin, profileOptions.AllCosmetics) ? heroSkin : FavoriteHero(baseHero);

    private static void GrantHeroSkin(LocalProfileData profile, string heroSkin)
    {
        if (!profile.OwnedHeroSkins.Contains(heroSkin)) profile.OwnedHeroSkins.Add(heroSkin);
        string hero = LocalShop.Default.HeroSkinClass(heroSkin);
        // An existing row is a choice, including an explicitly selected base portrait.
        if (hero != null && !profile.FavoriteHeroSkins.Any(row => row.Class == hero))
            profile.FavoriteHeroSkins.Add(new LocalHeroFavorite { Class = hero, HeroCardId = heroSkin });
    }

    private static bool AdoptAchievementHeroSkins(LocalProfileData profile)
    {
        bool changed = false;
        foreach (var group in profile.Achievements.Grants.Where(row => row.HeroSkinId != null)
            .Select(row => row.HeroSkinId).Distinct(StringComparer.Ordinal)
            .Where(profile.OwnedHeroSkins.Contains).GroupBy(LocalShop.Default.HeroSkinClass))
        {
            if (group.Key == null || group.Count() != 1 || profile.FavoriteHeroSkins.Any(row => row.Class == group.Key)) continue;
            profile.FavoriteHeroSkins.Add(new LocalHeroFavorite { Class = group.Key, HeroCardId = group.Single() });
            changed = true;
        }
        return changed;
    }
    // Native GetHeroPremium: the vanilla hero shows Golden whenever a Golden copy is owned; skins have no premium.
    public int HeroPremium(string baseHero, string heroCardId) => heroCardId == baseHero && OwnedCount(baseHero, 1) > 0 ? 1 : 0;
    // The native shop quotes every offer several times per open and per tile refresh. A quote is a pure function of the
    // committed ledger, the shop instance and process-constant tables (Core schedule, class-reward availability, offline
    // deckbuilding cards, rarities). `data` is only ever replaced (load and Commit), never mutated in place, so quotes are
    // memoized per (data, LocalShop.Default) identity. Errors are never memoized: they throw on every call.
    public int PurchasePrice(string productId, int quantity = 1) {
        lock (quoteLock) {
            var memo = Quotes();
            var key = new QuoteKey(productId, quantity);
            if (productId != null && memo.Prices.TryGetValue(key, out int cached)) return cached;
            int price = Price(memo.Shop, memo.Data, memo.Shop.ProductRef(productId), quantity,
                profileOptions.AllCosmetics, memo);
            memo.Prices[key] = price;
            return price;
        }
    }
    // Identity of the committed ledger: changes on every commit or reload. Shop refresh caches key on it.
    internal object LedgerIdentity => data;
    // The memoized MissingCopies behind the current deck quote, for the shop's own recipe of that ID. Read only.
    internal RecipeMissingCopies QuoteMissingCopies(string recipeId) {
        lock (quoteLock) { var memo = Quotes(); return memo.Missing(memo.Shop.RecipeRef(recipeId)); }
    }
    internal string DeckPurchaseBlocker(string recipeId) => LocalShop.DeckPurchaseBlocker(QuoteMissingCopies(recipeId));
    internal bool DeckPurchaseNeedsSlot(string recipeId) => MatchingRecipeDeck(data, LocalShop.Default.RecipeRef(recipeId)) == null;
    private readonly object quoteLock = new object();
    private ShopQuotes quotes;
    private ShopQuotes Quotes() {
        var current = data; var shop = LocalShop.Default;
        if (quotes == null || !ReferenceEquals(quotes.Data, current) || !ReferenceEquals(quotes.Shop, shop))
            quotes = new ShopQuotes(current, shop, profileOptions.AllCosmetics);
        return quotes;
    }
    private struct QuoteKey : IEquatable<QuoteKey> {
        private readonly string id; private readonly int quantity;
        internal QuoteKey(string id, int quantity) { this.id = id; this.quantity = quantity; }
        public bool Equals(QuoteKey other) => quantity == other.quantity && string.Equals(id, other.id, StringComparison.Ordinal);
        public override bool Equals(object other) => other is QuoteKey key && Equals(key);
        public override int GetHashCode() => (id == null ? 0 : StringComparer.Ordinal.GetHashCode(id)) * 31 + quantity;
    }
    private sealed class ShopQuotes {
        internal readonly LocalProfileData Data; internal readonly LocalShop Shop; internal readonly bool AllCosmetics;
        internal readonly Dictionary<QuoteKey, int> Prices = new Dictionary<QuoteKey, int>();
        private readonly Dictionary<string, RecipeMissingCopies> missing = new Dictionary<string, RecipeMissingCopies>(StringComparer.Ordinal);
        private Func<string, int> owned;
        internal ShopQuotes(LocalProfileData data, LocalShop shop, bool allCosmetics)
        { Data = data; Shop = shop; AllCosmetics = allCosmetics; }
        internal RecipeMissingCopies Missing(DeckRecipe recipe) {
            if (missing.TryGetValue(recipe.Id, out var found)) return found;
            if (owned == null) owned = RecipeLibrary.OwnedCount(OwnedAnyVersion(Data, AllCosmetics));
            return missing[recipe.Id] = Shop.Library.MissingCopies(recipe, owned);
        }
    }

    // quotes == null is the authoritative uncached path (Purchase). Reads product and recipe; never mutates them.
    private static int Price(LocalShop shop, LocalProfileData profile, LocalShopProduct product, int quantity,
        bool allCosmetics, ShopQuotes quotes = null, bool purchasing = false) {
        if (product.verify) throw new InvalidOperationException("pending native check");
        if (quantity < product.MinQuantity || quantity > product.MaxQuantity) throw new InvalidOperationException("Invalid purchase quantity.");
        if (product.Kind == "Recipe") shop.RecipeRef(product.RecipeId);
        if (product.Kind != "Deck") return checked(product.Price * quantity);
        var recipe = shop.RecipeRef(product.RecipeId);
        var missing = quotes != null ? quotes.Missing(recipe)
            : shop.Library.MissingCopies(recipe, RecipeLibrary.OwnedCount(OwnedAnyVersion(profile, allCosmetics)));
        string blocker = purchasing ? LocalShop.DeckPurchaseBlocker(missing) : null;
        if (blocker != null) throw new InvalidOperationException(blocker);
        int price = profile.OwnedRecipes.Contains(recipe.Id) ? 0 : shop.RecipePrice(recipe.Id);
        foreach (var card in missing.Cards.Where(card => !LocalShop.GrantOnly(card.Id)))
            price = checked(price + LocalShop.DeckGrant(card) * LocalShop.CopyPrice(card.Rarity));
        return price;
    }

    // Prices count every owned version; slot premiums are assigned at the shared profile commit boundary.
    internal IReadOnlyDictionary<string, int> OwnedAnyVersionCollection => OwnedAnyVersion(data, profileOptions.AllCosmetics);
    private static Dictionary<string, int> OwnedAnyVersion(LocalProfileData profile, bool allCosmetics) {
        var owned = OwnedCollection(profile);
        var premium = profile.PremiumCollection.Where(card => LocalClassRewards.IsAvailable(card.Id)).Select(Copy).ToList();
        if (allCosmetics)
        {
            var indexed = premium.ToDictionary(card => PremiumKey(card.Id, card.Premium), StringComparer.Ordinal);
            foreach (var floor in SupportedCheatPremium)
            {
                string key = PremiumKey(floor.Id, floor.Premium);
                if (!indexed.TryGetValue(key, out var row)) { row = Copy(floor); premium.Add(row); indexed.Add(key, row); }
                else row.Count = Math.Max(row.Count, floor.Count);
            }
        }
        foreach (var row in premium)
            owned[row.Id] = checked((owned.TryGetValue(row.Id, out int count) ? count : 0) + row.Count);
        return owned;
    }

    // Owner 2026-10-28: the unredeemed kit with the smallest cap that covers the price, else the one with the largest cap
    // (ties by id). Null when no kit is left.
    internal static LocalDeckKit ChooseDeckKit(IEnumerable<LocalDeckKit> kits, int price) {
        var open = kits.Where(k => !k.RedeemedPurchaseSequence.HasValue).OrderBy(k => k.Id, StringComparer.Ordinal).ToList();
        return open.Where(k => k.GoldCap >= price).OrderBy(k => k.GoldCap).FirstOrDefault()
            ?? open.OrderByDescending(k => k.GoldCap).FirstOrDefault();
    }
    // The kit a ready-made deck at this pre-kit price would use (a copy), or null.
    public LocalDeckKit DeckKitFor(int price) => Copy(ChooseDeckKit(data.DeckKits, price));
    public IReadOnlyList<LocalDeckKit> DeckKits => data.DeckKits.Select(Copy).ToArray();
    // Non-copying, per frame: the native kit badges (#57).
    public bool HasUnredeemedDeckKit => data.DeckKits.Any(k => !k.RedeemedPurchaseSequence.HasValue);

    // expectedPrice is the current price BEFORE the kit allowance, including on request replay.
    public LocalPurchaseReceipt Purchase(string productId, int quantity, int expectedPrice, string requestId, string kitId = null) {
        if (!LocalShop.ValidId(requestId)) throw new InvalidOperationException("A purchase request ID is required.");
        var existing = data.Purchases.SingleOrDefault(r => r.RequestId == requestId);
        if (existing != null) {
            if (existing.ProductId != productId || existing.Quantity != quantity || existing.PriceBeforeKit != expectedPrice || existing.KitId != kitId)
                throw new InvalidOperationException("Conflicting purchase request ID.");
            return Clone(existing);
        }
        var product = LocalShop.Default.Product(productId);
        int price = Price(LocalShop.Default, data, product, quantity, profileOptions.AllCosmetics, purchasing: true);
        if (expectedPrice != price) throw new InvalidOperationException("Shop price changed; refresh the purchase.");
        var kit = kitId == null ? null : data.DeckKits.SingleOrDefault(k => k.Id == kitId);
        if (kitId != null && (product.Kind != "Deck" || kit == null || kit.RedeemedPurchaseSequence.HasValue))
            throw new InvalidOperationException("Deck kit is unknown, redeemed, or not valid for this product.");
        int goldPaid = Math.Max(0, price - (kit?.GoldCap ?? 0));
        if (!profileOptions.InfiniteGold && data.GoldBalance < goldPaid) throw new InvalidOperationException("Not enough local gold.");
        if ((product.Kind == "Recipe" && data.OwnedRecipes.Contains(product.RecipeId))
            || (product.Kind == "Deck" && (price == 0 || HasDeckReceipt(product.RecipeId)))
            || (product.Kind == "CardBack" && OwnsCardBack(product.CardBack.Value))
            || (product.Kind == "Coin" && OwnsCoin(product.Coin.Value))
            || (product.Kind == "HeroSkin" && OwnsHeroSkin(product.HeroSkin))
            || (product.Kind == "HubTheme" && OwnsHubTheme(product.HubTheme))
            || (product.Kind == "Board" && OwnsBoard(product.Board.Value))
            || (product.Kind == "Pet" && OwnsPet(product.Pet)))
            throw new InvalidOperationException("Shop item already owned.");
        if (product.Kind == "Deck" && data.PackRevealPending) throw new InvalidOperationException("A native pack reveal is pending.");
        var next = Snapshot();
        var receipt = new LocalPurchaseReceipt { Sequence = checked(next.PurchaseSequence + 1), RequestId = requestId,
            CatalogVersion = LocalShop.Default.Version, ProductId = productId, Kind = product.Kind, Quantity = quantity, GoldPaid = goldPaid, PriceBeforeKit = price, KitId = kitId };
        if (!profileOptions.InfiniteGold) next.GoldBalance = checked(next.GoldBalance - goldPaid);
        if (kit != null) next.DeckKits.Single(k => k.Id == kitId).RedeemedPurchaseSequence = receipt.Sequence;
        switch (product.Kind) {
            case "Pack":
                AddPacks(next, product.PackType, quantity);
                receipt.Granted.Packs.Add(new LocalPackCount { PackType = product.PackType, Count = quantity }); break;
            case "Recipe":
                next.OwnedRecipes.Add(product.RecipeId); receipt.Granted.RecipeId = product.RecipeId; break;
            case "Deck": GrantDeck(next, product, receipt, profileOptions.AllCosmetics); break;
            case "CardBack": next.OwnedCardBacks.Add(product.CardBack.Value); receipt.Granted.CardBack = product.CardBack; break;
            case "Coin": next.OwnedCoins.Add(product.Coin.Value); receipt.Granted.Coin = product.Coin; break;
            case "HeroSkin": GrantHeroSkin(next, product.HeroSkin); receipt.Granted.HeroSkin = product.HeroSkin; break;
            // Owner 2026-10-25: buying a theme or board equips it in the same commit.
            case "HubTheme": next.OwnedHubThemes.Add(product.HubTheme); next.SelectedHubTheme = product.HubTheme; receipt.Granted.HubTheme = product.HubTheme; break;
            case "Board": next.OwnedBoards.Add(product.Board.Value); next.SelectedBoard = product.Board.Value; receipt.Granted.Board = product.Board; break;
            case "Pet": next.OwnedPets.Add(product.Pet); next.PetProgress.Add(new LocalPetProgress { Pet = product.Pet }); next.SelectedPet = product.Pet; receipt.Granted.Pet = product.Pet; break;
        }
        next.PurchaseSequence = receipt.Sequence;
        next.Purchases.Add(receipt);
        ReconcileCollectionAchievement(next);
        Commit(next);
        Backup("purchase");
        return Clone(receipt);
    }

    // What buying a ready-made deck grants (shared by Purchase and the Sandbox All decks creation option): the missing Normal
    // copies (locked Core copies join the Core grant ledger), the recipe and a matching or new personal deck.
    private static void GrantDeck(LocalProfileData next, LocalShopProduct product, LocalPurchaseReceipt receipt, bool allCosmetics) {
        var recipe = LocalShop.Default.Recipe(product.RecipeId);
        // Premium copies count as owned (any version); only the missing Normal copies are granted.
        var build = LocalShop.Default.Library.BuildDeck(recipe,
            RecipeLibrary.OwnedCount(OwnedAnyVersion(next, allCosmetics)));
        foreach (var row in build.Copies.Where(row => LocalShop.DeckGrant(row) > 0)) {
            if (row.Needed > Playset(row.Id)) throw new InvalidOperationException("Recipe exceeds purchased playset.");
            if (row.LockedCore > 0) {
                // Locked Core copies join the monotonic Core grant ledger, never the paid collection or Acquired;
                // a later class unlock only tops the grant up to the schedule's copies.
                var grant = next.CoreGrants.SingleOrDefault(c => c.Id == row.Id);
                int before = grant?.Count ?? 0, after = checked(before + row.Missing);
                if (after > LocalCoreProgression.ById[row.Id].Copies)
                    throw new InvalidOperationException("Recipe exceeds the Core unlock copies.");
                if (grant == null) { grant = new LocalCardCount { Id = row.Id }; next.CoreGrants.Add(grant); }
                receipt.Granted.Cards.Add(new LocalPurchaseCard { CardId = row.Id, CountBefore = before,
                    CountAfter = after, AcquiredBefore = before });
                grant.Count = after; continue;
            }
            var entry = GetCollectionCard(next, row.Id);
            receipt.Granted.Cards.Add(new LocalPurchaseCard { CardId = row.Id, CountBefore = entry.Count,
                CountAfter = checked(entry.Count + row.Missing), AcquiredBefore = entry.Acquired });
            entry.Acquired = Math.Min(Playset(entry.Id), checked(entry.Acquired + row.Missing)); entry.Count = checked(entry.Count + row.Missing);
        }
        if (!next.OwnedRecipes.Contains(recipe.Id)) next.OwnedRecipes.Add(recipe.Id);
        receipt.Granted.RecipeId = recipe.Id;
        var boards = build.Sideboards.Select(board => new LocalSideboard { Source = board.Key, Cards = board.Value.ToList() }).ToList();
        var saved = MatchingRecipeDeck(next, recipe);
        bool fresh = saved == null; // Only a new deck takes the best owned versions; a completed one keeps its explicit choices.
        if (fresh) {
            if (next.CustomDecks.Count >= MaximumCustomDecks) throw new InvalidOperationException(PersonalDeckLimitMessage);
            EnsureAllocatorAvailable(next.NextCustomDeckId);
            long deckId = next.NextCustomDeckId++;
            saved = new LocalCustomDeck { Id = deckId, SortOrder = deckId, Name = recipe.Name,
                Hero = build.Deck.Hero, SourceRecipeId = recipe.Id, Cards = build.CardIds.ToList(), Sideboards = boards };
            next.CustomDecks.Add(saved);
        }
        LocalCrafting.AssignOwnedPremiums(saved, DeckOwnedCount(next, allCosmetics), fresh);
        receipt.Granted.DeckId = saved.Id;
    }

    // Composition includes sideboard ownership/counts, never name, cosmetics or premium choices.
    private static LocalCustomDeck MatchingRecipeDeck(LocalProfileData profile, DeckRecipe recipe) =>
        MatchingRecipeDeck(profile, recipe.Id, recipe.Hero,
            recipe.Cards.SelectMany(card => Enumerable.Repeat(card.Id, card.Count)),
            recipe.Sideboards.Select(board => new LocalSideboard { Source = board.Owner,
                Cards = board.Cards.SelectMany(card => Enumerable.Repeat(card.Id, card.Count)).ToList() }));
    internal static LocalCustomDeck MatchingRecipeDeck(LocalProfileData profile, string recipeId, string hero,
        IEnumerable<string> cards, IEnumerable<LocalSideboard> boards)
    {
        string main = DeckCardSignature(cards), sideboards = DeckBoardSignature(boards);
        return profile.CustomDecks.Where(deck => deck.SourceRecipeId == recipeId && deck.Hero == hero
            && DeckCardSignature(deck.Cards) == main && DeckBoardSignature(deck.Sideboards) == sideboards)
            .OrderBy(deck => deck.SortOrder).ThenBy(deck => deck.Id).FirstOrDefault();
    }
    private static string DeckCardSignature(IEnumerable<string> cards) => string.Join(",", cards.OrderBy(id => id, StringComparer.Ordinal));
    private static string DeckBoardSignature(IEnumerable<LocalSideboard> boards) => string.Join(";", boards
        .OrderBy(board => board.Source, StringComparer.Ordinal).Select(board => board.Source + ":" + DeckCardSignature(board.Cards)));

    // Test fixture: replaces the whole favourite set; native toggles use SetCardBackFavorite.
    internal void SelectCardBack(int id) {
        if (!OwnsCardBack(id)) throw new InvalidOperationException("Card back is not owned.");
        var next = Snapshot(); next.FavoriteCardBacks = new List<int> { id }; Commit(next);
    }
    public void SetCardBackFavorite(int id, bool favorite) {
        if (!OwnsCardBack(id)) throw new InvalidOperationException("Card back is not owned.");
        if (data.FavoriteCardBacks.Contains(id) == favorite) return;
        var next = Snapshot();
        if (favorite) next.FavoriteCardBacks.Add(id); else next.FavoriteCardBacks.Remove(id);
        if (next.FavoriteCardBacks.Count == 0) next.FavoriteCardBacks.Add(0);
        if (next.FavoriteCardBacks.OrderBy(back => back).SequenceEqual(data.FavoriteCardBacks)) return;
        Commit(next);
    }
    public void SetCoinFavorite(int id, bool favorite) {
        if (!OwnsCoin(id)) throw new InvalidOperationException("Coin is not owned.");
        if (data.FavoriteCoins.Contains(id) == favorite) return;
        var next = Snapshot();
        if (favorite) next.FavoriteCoins.Add(id); else next.FavoriteCoins.Remove(id);
        if (next.FavoriteCoins.Count == 0) next.FavoriteCoins.Add(1);
        if (next.FavoriteCoins.OrderBy(coin => coin).SequenceEqual(data.FavoriteCoins)) return;
        Commit(next);
    }
    public int MatchCoin(Random random) {
        if (random == null) throw new ArgumentNullException(nameof(random));
        var coins = FavoriteCoins;
        return coins[random.Next(coins.Count)];
    }
    public int MatchCardBack(Random random) {
        if (random == null) throw new ArgumentNullException(nameof(random));
        var backs = FavoriteCardBacks;
        return backs[random.Next(backs.Count)];
    }
    // Equip an owned theme (applies from the next launch; "random" re-rolls each launch) or board (each match).
    public void SelectHubTheme(string key) {
        if (key == null || !(LocalHubCosmetics.KnownTheme(key) || key == LocalHubCosmetics.RandomTheme) || !OwnsHubTheme(key))
            throw new InvalidOperationException("Hub theme is not owned.");
        if (SelectedHubTheme == key) return;
        var next = Snapshot(); next.SelectedHubTheme = key; Commit(next);
    }
    public void SelectBoard(int id) {
        if (!OwnsBoard(id)) throw new InvalidOperationException("Board is not owned.");
        if (SelectedBoard == id) return;
        var next = Snapshot(); next.SelectedBoard = id; Commit(next);
    }
    public void SelectPet(string cardId) {
        if (!CanUsePetForm(cardId)) throw new InvalidOperationException("Pet form is not unlocked.");
        if (SelectedPet == cardId) return;
        var next = Snapshot(); next.SelectedPet = cardId; Commit(next);
    }
    public void SetFavoriteHeroSkin(string heroClass, string heroCardId) {
        if (!ValidFavorite(data, heroClass, heroCardId, profileOptions.AllCosmetics)) throw new InvalidOperationException("Hero skin is not owned for that class.");
        var next = Snapshot(); next.FavoriteHeroSkins.RemoveAll(f => f.Class == heroClass);
        next.FavoriteHeroSkins.Add(new LocalHeroFavorite { Class = heroClass, HeroCardId = heroCardId }); Commit(next);
    }
    private static bool ValidFavorite(LocalProfileData profile, string heroClass, string heroCardId, bool allCosmetics = false) =>
        LocalCoreProgression.Heroes.Contains(heroClass) && (heroClass == heroCardId || (profile.OwnedHeroSkins.Contains(heroCardId)
            || allCosmetics && LocalShop.SupportedCheatHeroSkins.Contains(heroCardId)) && LocalShop.Default.IsHeroSkinForClass(heroClass, heroCardId));

    // XmlSerializer construction reflects over the whole type graph (slow on Unity Mono); instances are thread-safe.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<System.Type, XmlSerializer> cloneSerializers =
        new System.Collections.Concurrent.ConcurrentDictionary<System.Type, XmlSerializer>();
    internal static T Clone<T>(T value) where T : class {
        if (value == null) return null;
        var serializer = cloneSerializers.GetOrAdd(typeof(T), type => new XmlSerializer(type));
        using (var stream = new MemoryStream()) { serializer.Serialize(stream, value); stream.Position = 0; return (T)serializer.Deserialize(stream); }
    }
    private static LocalPackCount Copy(LocalPackCount p) => new LocalPackCount { PackType = p.PackType, Count = p.Count };
    private static LocalPackProgress Copy(LocalPackProgress p) => new LocalPackProgress { PackType = p.PackType, Counters = p.Counters.Copy() };
    private static int Playset(string id) => LocalEconomy.PlayableCap(Cards.FromId(id) ?? throw new InvalidOperationException("Unknown ledger card."));
    // Historical receipts and counts are bounded by the largest playset, not by a card's current rarity.
    private const int MaximumPlayset = LocalEconomy.MaximumPlayset;
    private static readonly Lazy<Dictionary<string, string>> cardPackTypes = new Lazy<Dictionary<string, string>>(() =>
        FairPackRules.PackTypes.Where(type => type != FairPackRules.Standard)
            .SelectMany(type => FairPackRules.Eligible(type, FairPackRules.RuleVersion).Select(card => new { card.Id, Type = type }))
            .ToDictionary(card => card.Id, card => card.Type, StringComparer.Ordinal));
    private static readonly Lazy<Dictionary<string, HashSet<string>>> packCardIds = new Lazy<Dictionary<string, HashSet<string>>>(() =>
        FairPackRules.PackTypes.ToDictionary(type => type, type =>
            new HashSet<string>(FairPackRules.Eligible(type, FairPackRules.RuleVersion).Select(card => card.Id), StringComparer.Ordinal), StringComparer.Ordinal));
    internal static string CardPackType(string id) => id != null && cardPackTypes.Value.TryGetValue(id, out string type) ? type : null;
    private static bool NeedsExpandedDeckCatalog(IEnumerable<string> ids) {
        return ids.Any(id => KnownLedgerCard(id) && !LocalDecks.IsCustomCard(id));
    }
    internal static LocalDeck LedgerDeck(string name, string hero, IEnumerable<string> ids, IEnumerable<LocalSideboard> sideboards, bool complete) {
        var cards = ids?.ToArray() ?? throw new ArgumentNullException(nameof(ids));
        if (!NeedsExpandedDeckCatalog(cards)) return (complete ? LocalDecks.Custom(name, hero, cards) : LocalDecks.Draft(name, hero, cards)).WithSideboards(sideboards, complete);
        LocalShop.ValidateDeck(name, hero, cards, sideboards, complete, standardOnly: false);
        return new LocalDeck(name, hero, cards).WithSideboards(sideboards, complete);
    }
    // The one card universe of a complete personal deck: deck completeness, the start checks and the match itself
    // all use it, so a deck the builder accepts can start (LocalDecks.Custom alone misses pack cards such as CATA_485).
    internal static LocalDeck PlayableDeck(string name, string hero, IEnumerable<string> ids, IEnumerable<LocalSideboard> sideboards) {
        // The expanded ledger path accepts every stored hero; a match needs an implemented one, as Custom requires.
        if (!LocalDecks.PlayerHeroes.Contains(hero)) throw new InvalidOperationException("Unsupported local hero.");
        return LedgerDeck(name, hero, ids, sideboards, true);
    }
    private static readonly Lazy<EngineCard[]> ledgerCards = new Lazy<EngineCard[]>(() => LocalCardCatalog.Current
        .Concat(LocalShop.StandardCollectionCards)
#if ENGINE_SOURCE
        // Fabled companions are deck cards outside the collectible pack pool.
        .Concat(SabberStoneCore.CardSets.Client.StandardSets.CollectibleIds
            .SelectMany(SabberStoneCore.Actions.FabledRules.Bundle).Select(Cards.FromId)
            .Where(card => card != null && !LocalCardCatalog.IsCore(card.Id)))
#endif
        .GroupBy(c => c.Id).Select(g => g.First()).ToArray());
    private static IEnumerable<EngineCard> LedgerCards => ledgerCards.Value;
    // Private dictionaries/sets are built once and never mutated; meta/catalogue cannot reload in this process.
    private static readonly Lazy<Dictionary<string, EngineCard>> ledgerCardIndex = new Lazy<Dictionary<string, EngineCard>>(() =>
        ledgerCards.Value.ToDictionary(card => card.Id, StringComparer.Ordinal));
    private static Dictionary<string, EngineCard> LedgerCardIndex => ledgerCardIndex.Value;
    internal static bool KnownLedgerCard(string id) => id != null && (LedgerCardIndex.ContainsKey(id) || LocalCoreProgression.IsAvailable(id));
    // Premium receipts must keep loading after a meta change: any collectible definition is a valid sold card.
    private static bool IsCollectibleCard(string id) {
        try { return Cards.FromId(id)?.Collectible == true; } catch (KeyNotFoundException) { return false; }
    }
    private static int AcquiredCount(LocalProfileData profile, string id) => profile.Collection.SingleOrDefault(c => c.Id == id)?.Acquired ?? 0;
    private static LocalCardCount GetCollectionCard(LocalProfileData profile, string id, int premium = 0) {
        var collection = premium == 0 ? profile.Collection : profile.PremiumCollection;
        var row = collection.SingleOrDefault(c => c.Id == id && c.Premium == premium);
        if (row == null) { row = new LocalCardCount { Id = id, Premium = premium }; collection.Add(row); }
        return row;
    }
    private static void AddPacks(LocalProfileData profile, string type, int count) {
        var row = profile.UnopenedPacks.SingleOrDefault(p => p.PackType == type);
        if (row == null) { row = new LocalPackCount { PackType = type }; profile.UnopenedPacks.Add(row); }
        row.Count = checked(row.Count + count);
    }
    private static FairPackCounters CountersAfter(FairPackCounters before, IEnumerable<string> ids) {
        var after = before.Copy();
        after.PacksOpened = checked(after.PacksOpened + 1);
        bool legendary = ids.Any(id => Cards.FromId(id).Rarity == Rarity.LEGENDARY);
        bool epic = ids.Any(id => Cards.FromId(id).Rarity == Rarity.EPIC);
        after.PacksSinceLegendary = legendary ? 0 : checked(after.PacksSinceLegendary + 1);
        after.PacksSinceEpic = epic ? 0 : checked(after.PacksSinceEpic + 1);
        after.FirstLegendaryReceived |= legendary;
        return after;
    }
    private static void ValidateCounters(FairPackCounters c) {
        if (c == null || c.PacksOpened < 0 || c.PacksSinceLegendary < 0 || c.PacksSinceEpic < 0
            || c.PacksSinceLegendary > c.PacksOpened || c.PacksSinceEpic > c.PacksOpened
            || (!c.FirstLegendaryReceived && c.PacksSinceLegendary != c.PacksOpened)
            || (c.FirstLegendaryReceived && c.PacksSinceLegendary == c.PacksOpened))
            throw new InvalidOperationException("Invalid pack counters.");
    }
    private static bool SameCounters(FairPackCounters a, FairPackCounters b) => a != null && b != null
        && a.PacksOpened == b.PacksOpened && a.PacksSinceLegendary == b.PacksSinceLegendary
        && a.PacksSinceEpic == b.PacksSinceEpic && a.FirstLegendaryReceived == b.FirstLegendaryReceived;
    private static void ValidateFairReceipt(LocalPackResult pack) {
        if (!FairPackRules.PackTypes.Contains(pack.PackType) || (pack.PackRuleVersion < 1 || pack.PackRuleVersion > FairPackRules.RuleVersion) || pack.PoolVersion != 0
            || pack.Cards == null || pack.Cards.Count != 5) throw new InvalidOperationException("Invalid fair pack receipt.");
        ValidateCounters(pack.CountersBefore);
        var eligible = packCardIds.Value[pack.PackType];
        if (pack.Cards.Any(c => c == null || !eligible.Contains(c.Id) || c.DuplicateGold != 0 || c.AcquiredBefore < 0 || c.AcquiredBefore > MaximumPlayset
                || (c.Premium != 0 && (pack.PackRuleVersion == 1 || c.Premium != 1 && c.Premium != 3))
                || (c.Premium == 3 && (Cards.FromId(c.Id).Rarity != Rarity.LEGENDARY || !LocalCardPremium.HasPackSignature(c.Id))))
            || pack.Cards.GroupBy(c => c.Id).Any(g => g.Count() > MaximumPlayset)
            || pack.Cards.GroupBy(c => c.Key).Any(g => g.Count() > MaximumPlayset || g.Select(c => c.AcquiredBefore).Distinct().Count() != 1))
            throw new InvalidOperationException("Invalid fair pack card.");
    }
    private static void ValidateV10(LocalProfileData p, bool allCosmetics = false, bool allDecks = false) {
        if (p.LegacyUnopenedPacks != 0 || p.UnopenedPacks == null || p.UnopenedPacks.Any(row => row == null || !FairPackRules.IsKnown(row.PackType)
                || row.Count < 0 || row.Count > 1000000) || p.UnopenedPacks.Sum(row => (long)row.Count) > 1000000
            || p.UnopenedPacks.Select(row => row.PackType).Distinct().Count() != p.UnopenedPacks.Count
            || p.PackCounters == null || p.PackCounters.Count != FairPackRules.PackTypes.Count + 1
            || p.PackCounters.Any(row => row == null || !FairPackRules.IsKnown(row.PackType))
            || p.PackCounters.Select(row => row.PackType).Distinct().Count() != p.PackCounters.Count)
            throw new InvalidOperationException("Invalid typed pack ledger.");
        foreach (var progress in p.PackCounters) ValidateCounters(progress.Counters);
        if (p.PackCounters.Sum(row => (long)row.Counters.PacksOpened) > p.PackSequence)
            throw new InvalidOperationException("Pack counters exceed sequence.");
        var acquired = p.Collection.Concat(p.PremiumCollection).GroupBy(c => c.Id)
            .ToDictionary(group => group.Key, group => group.Sum(c => c.Acquired));
        var incompleteRarities = new Dictionary<string, HashSet<Rarity>>(StringComparer.Ordinal);
        foreach (var entry in p.Collection.Concat(p.PremiumCollection)) {
            // Rarity may have changed since these copies were acquired: accept either playset, never above two.
            if (entry.Acquired < Math.Min(entry.Count, 1) || entry.Acquired > MaximumPlayset || entry.Acquired < 0)
                throw new InvalidOperationException("Invalid lifetime acquired count.");
            // Retired premium grants stay owned history; only packs roll premium, so they never need extras checks.
            if (entry.Premium != 0 && !IsCollectibleCard(entry.Id)) continue;
            // Extras are copies beyond today's playset that lifetime acquisition does not explain (an epic pair
            // acquired before a legendary correction is not an extra).
            if (entry.Count - (entry.Premium == 0 ? 0 : ProgressionGrantedCount(p, entry.Id, entry.Premium))
                <= Math.Max(Playset(entry.Id), entry.Acquired)) continue;
            var card = Cards.FromId(entry.Id);
            // Legacy/Core cards never gained extras before V10 and cannot gain them now.
            string type = CardPackType(card.Id);
            // Premium copies of cards outside packs (Core, event grants) were never extras-checked; only packs roll premium.
            if (type == null && entry.Premium != 0) continue;
            if (type == null) throw new InvalidOperationException("Extras require complete acquisition of the set and rarity.");
            if (!incompleteRarities.TryGetValue(type, out var missing)) {
                missing = new HashSet<Rarity>(FairPackRules.Eligible(type, FairPackRules.RuleVersion)
                    // Extras (beyond two copies) keep today's full-playset rule: the ledger check needs it, and only a
                    // legendary later corrected to a lower rarity with one copy can trip it.
                    .Where(c => !acquired.TryGetValue(c.Id, out int count) || count < LocalEconomy.PlayableCap(c)).Select(c => c.Rarity));
                incompleteRarities.Add(type, missing);
            }
            if (missing.Contains(card.Rarity))
                throw new InvalidOperationException("Extras require complete acquisition of the set and rarity.");
        }
        if (p.LastPack != null) {
            if (p.PackSequence < 1 || !FairPackRules.IsKnown(p.LastPack.PackType)) throw new InvalidOperationException("Unknown receipt pack type.");
            ValidateCounters(p.LastPack.CountersBefore);
            if (p.LastPack.PackType == FairPackRules.Legacy && (p.LastPack.PackRuleVersion != 0
                || p.LastPack.Cards.Any(c => c.AcquiredBefore < 0 || c.AcquiredBefore > MaximumPlayset)))
                throw new InvalidOperationException("Invalid legacy acquisition receipt.");
            if (p.PackRevealPending && p.LastPack.PackType == FairPackRules.Legacy) {
                foreach (var group in p.LastPack.Cards.GroupBy(c => c.Id)) {
                    var current = p.Collection.SingleOrDefault(c => c.Id == group.Key);
                    int gained = group.Count(c => c.DuplicateGold == 0), before = group.First().AcquiredBefore;
                    if (current == null || current.Count < gained || group.Any(c => c.AcquiredBefore != before)
                        || before < Math.Min(current.Count - gained, 1)
                        || !LocalEconomy.HistoricAcquired(current.Acquired, before + gained, gained))
                        throw new InvalidOperationException("Invalid pending legacy acquisition delta.");
                }
            }
        }
        foreach (var match in p.Matches.Concat(p.Pending == null ? Enumerable.Empty<LocalMatchRecord>() : new[] { p.Pending })) {
            if (match.DustReward < 0 || match.Packs == null || match.Packs.Count != (match.PackReward ? 1 : 0)
                || match.Packs.Any(pack => pack == null || !FairPackRules.IsKnown(pack.PackType) || pack.Count != 1))
                throw new InvalidOperationException("Invalid typed match reward.");
        }
        if (p.DustBalance < 0 || p.CraftSequence < 0 || (p.CraftSequence == 0) != (p.LastCraft == null))
            throw new InvalidOperationException("Invalid crafting ledger.");
        if (p.LastCraft != null) {
            var craft = p.LastCraft;
            if ((craft.TokenRarity.HasValue && !Enum.IsDefined(typeof(LocalTokenRarity), craft.TokenRarity.Value))
                || (craft.Op == LocalCraftOp.TokenCraft && (!craft.TokenRarity.HasValue || craft.TokenDelta != -1 || craft.DustDelta != 0))
                || (craft.Op == LocalCraftOp.Undo ? (craft.TokenRarity.HasValue ? craft.TokenDelta != 1 || craft.DustDelta != 0 : craft.TokenDelta != 0)
                    : craft.Op != LocalCraftOp.TokenCraft && (craft.TokenRarity.HasValue || craft.TokenDelta != 0)))
                throw new InvalidOperationException("Invalid craft token receipt.");
            if (craft.Sequence != p.CraftSequence || !Enum.IsDefined(typeof(LocalCraftOp), craft.Op)
                || craft.Cards == null || craft.Cards.Count == 0 || craft.Cards.Any(c => c == null || c.CountDelta == 0
                    || c.CardId == null || !(KnownLedgerCard(c.CardId) || (c.Premium != 0 && (p.PremiumCollection.Any(row => row.Id == c.CardId) || IsCollectibleCard(c.CardId)))) || c.AcquiredBefore < 0 || c.AcquiredBefore > MaximumPlayset
                    || c.Premium < 0 || c.Premium > 3 || (c.Premium != 0 && craft.Op != LocalCraftOp.Disenchant && craft.Op != LocalCraftOp.Upgrade))
                || craft.Cards.Select(c => (c.CardId, c.Premium)).Distinct().Count() != craft.Cards.Count
                || (craft.Op == LocalCraftOp.Craft && (craft.DustDelta >= 0 || craft.Cards.Any(c => c.CountDelta < 0)))
                || ((craft.Op == LocalCraftOp.Disenchant || craft.Op == LocalCraftOp.MassDisenchant) && (craft.DustDelta <= 0 || craft.Cards.Any(c => c.CountDelta > 0)))
                || (craft.Op == LocalCraftOp.TokenCraft && (craft.Cards.Count != 1 || craft.Cards[0].CountDelta != 1))
                || (craft.Op == LocalCraftOp.Upgrade && (craft.Cards.Count != 2 || craft.DustDelta >= 0 || craft.Undoable
                    || craft.Cards[0].CardId != craft.Cards[1].CardId
                    || !craft.Cards.Any(c => c.Premium == 0 && c.CountDelta < 0)
                    || !craft.Cards.Any(c => c.Premium == 1 && c.CountDelta > 0)
                    || craft.Cards.Sum(c => (long)c.CountDelta) != 0))
                || ((craft.Op == LocalCraftOp.MassDisenchant || craft.Op == LocalCraftOp.Undo) && craft.Undoable))
                throw new InvalidOperationException("Invalid craft receipt.");
        }
        var ladder = p.LadderState;
        if (p.LadderRuleVersion < 0 || (ladder != null && (ladder.StarIndex < 0 || ladder.StarIndex > 150
            || ladder.BestStarIndex < ladder.StarIndex || ladder.BestStarIndex > 150 || ladder.Multiplier < 0 || ladder.Multiplier > 11
            || ladder.StreakWins < 0 || ladder.PlacementGames < 0 || (ladder.HiddenRating.HasValue && (double.IsNaN(ladder.HiddenRating.Value)
                || double.IsInfinity(ladder.HiddenRating.Value))) || ladder.FloorsRewarded == null
            || ladder.FloorsRewarded.Any(f => !LocalShop.ValidId(f)) || ladder.FloorsRewarded.Distinct().Count() != ladder.FloorsRewarded.Count)))
            throw new InvalidOperationException("Invalid ladder reservation.");
        ValidateReservations(p);
        ValidatePurchases(p, allCosmetics, allDecks);
    }
    private static IEnumerable<LocalTierReward> MilestoneRewards(LocalProfileData p) => p.Matches
        .Where(m => m.TierRewards != null).SelectMany(m => m.TierRewards)
        .Concat(p.MilestoneCatchUp?.TierRewards ?? Enumerable.Empty<LocalTierReward>());

    private static bool ValidRating(double? rating) => !rating.HasValue || (!double.IsNaN(rating.Value) && !double.IsInfinity(rating.Value));
    private static bool ValidIds(List<string> ids) => ids != null && ids.All(LocalShop.ValidId) && ids.Distinct().Count() == ids.Count;
    private static void ValidateTierRewards(List<LocalTierReward> rewards) {
        if (rewards == null || rewards.Any(r => r == null || !LocalShop.ValidId(r.MilestoneId)
            || (r.OpponentNpcId != null && !LocalShop.ValidId(r.OpponentNpcId))
            || (r.OpponentName != null && !ValidName(r.OpponentName, 128)) || r.Grants == null
            || r.Grants.Any(g => g == null || !Enum.IsDefined(typeof(LocalTierGrantKind), g.Kind) || !LocalShop.ValidId(g.Ref) || g.Count <= 0))
            || rewards.Select(r => r.MilestoneId).Distinct().Count() != rewards.Count)
            throw new InvalidOperationException("Invalid milestone reward receipt.");
    }
    private static void ValidateReservations(LocalProfileData p) {
        if (p.CardTokens == null || p.CardTokens.Common < 0 || p.CardTokens.Rare < 0 || p.CardTokens.Epic < 0 || p.CardTokens.Legendary < 0
            || p.DeckKits == null || p.DeckKits.Any(k => k == null || !LocalShop.ValidId(k.Id) || !LocalShop.ValidId(k.MilestoneId)
                || k.GoldCap < 0 || (k.RedeemedPurchaseSequence.HasValue && (k.RedeemedPurchaseSequence <= 0
                    || p.Purchases == null || !p.Purchases.Any(r => r != null && r.Sequence == k.RedeemedPurchaseSequence && r.KitId == k.Id))))
            || p.DeckKits.Select(k => k.Id).Distinct().Count() != p.DeckKits.Count
            || p.Trophies == null || p.Trophies.Any(t => t == null || !LocalShop.ValidId(t.Id) || !LocalShop.ValidId(t.MilestoneId)
                || !LocalShop.ValidId(t.MatchId) || !DateTimeOffset.TryParseExact(t.Utc, "o", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var utc) || utc.Offset != TimeSpan.Zero)
            || p.Trophies.Select(t => t.Id).Distinct().Count() != p.Trophies.Count)
            throw new InvalidOperationException("Invalid milestone reservation.");
        // Historical V1 receipts keep validating; V2 adds actual card grants. The kit may be redeemed.
        var welcomeKits = p.DeckKits.Where(k => k.Id == LocalWelcomeGift.KitId || k.MilestoneId == LocalWelcomeGift.KitMilestoneId).ToList();
        if (p.Welcome != null ? !LocalWelcomeGift.IsKnown(p.Welcome) || welcomeKits.Count != 1 || welcomeKits[0].Id != LocalWelcomeGift.KitId
                || welcomeKits[0].MilestoneId != LocalWelcomeGift.KitMilestoneId || welcomeKits[0].GoldCap != LocalWelcomeGift.KitGoldCap
            : welcomeKits.Count != 0)
            throw new InvalidOperationException("Invalid welcome gift receipt.");
        ValidateWelcomeCards(p.Welcome);
        if (p.LegendDeck != null) {
            var d = p.LegendDeck;
            if (d.Cards == null || d.Sideboards == null) throw new InvalidOperationException("Invalid Legend deck snapshot.");
            if (d.Legacy) {
                if (d.MatchId != null || d.DeckName != null || d.Hero != null || d.Cards.Count != 0 || d.Sideboards.Count != 0)
                    throw new InvalidOperationException("Legacy Legend deck cannot contain a snapshot.");
            } else {
                if (!LocalShop.ValidId(d.MatchId)) throw new InvalidOperationException("Invalid Legend match ID.");
                LedgerDeck(d.DeckName, d.Hero, d.Cards, d.Sideboards, true);
            }
        }
        if (p.MilestoneCatchUp != null) {
            if (!LocalShop.ValidId(p.MilestoneCatchUp.Id)) throw new InvalidOperationException("Invalid milestone catch-up ID.");
            ValidateTierRewards(p.MilestoneCatchUp.TierRewards);
        }
        int stars = 0;
        foreach (var m in p.Matches.Concat(p.Pending == null ? Enumerable.Empty<LocalMatchRecord>() : new[] { p.Pending })) {
            // #37 Casual: no ladder receipt and outside the star fold; its progression receipt is checked like a ranked one.
            if (CasualRules.IsCasual(m.Mode) && m.Casual != null) {
                if (m.Result == null || m.Ladder != null || (m.TierRewards != null && m.TierRewards.Count != 0)
                    || !m.ClassXpBefore.HasValue || !m.ClassXpAfter.HasValue || m.ClassXpBefore < 0 || m.ClassXpAfter < m.ClassXpBefore
                    || m.ProgressionRuleVersion < 1 || !ValidIds(m.UnlockedCardIds) || m.UnlockedCardIds.Any(id => !LocalCoreProgression.ById.ContainsKey(id)))
                    throw new InvalidOperationException("Invalid Casual progression receipt.");
                if (m.ProgressionRuleVersion == LocalCoreProgression.ScheduleVersion
                    && (long)m.ClassXpAfter - m.ClassXpBefore != (m.DeckHero == null ? 0 : LocalProfile.MatchClassXp(m)))
                    throw new InvalidOperationException("Invalid class XP receipt.");
                continue;
            }
            int after = CasualRules.IsCasual(m.Mode) ? stars : NextStars(stars, m.Result);
            if (m.Ladder == null) {
                if (m.ClassXpBefore.HasValue || m.ClassXpAfter.HasValue || m.UnlockedCardIds != null || m.ProgressionRuleVersion != 0 || m.TierRewards != null || m.DustReward != 0)
                    throw new InvalidOperationException("Partial historical match receipt.");
            } else {
                var r = m.Ladder;
                if (m.Result == null || r.StarsBefore < 0 || r.StarsBefore > 150 || r.StarsAfter < 0 || r.StarsAfter > 150
                    || r.StarsGained != r.StarsAfter - r.StarsBefore || r.MultiplierBefore < 1 || r.MultiplierBefore > 11
                    || r.MultiplierAfter < 1 || r.MultiplierAfter > 11 || r.StreakBefore < 0 || r.StreakAfter < 0
                    || !ValidRating(r.RatingBefore) || !ValidRating(r.RatingAfter) || !ValidIds(r.FloorsReached) || r.LadderRuleVersion < 1
                    || !m.ClassXpBefore.HasValue || !m.ClassXpAfter.HasValue || m.ClassXpBefore < 0 || m.ClassXpAfter < m.ClassXpBefore
                    || m.ProgressionRuleVersion < 1 || !ValidIds(m.UnlockedCardIds) || m.UnlockedCardIds.Any(id => !LocalCoreProgression.ById.ContainsKey(id)))
                    throw new InvalidOperationException("Invalid match progression receipt.");
                if (r.LadderRuleVersion == 1 && (r.StarsBefore != stars || r.StarsAfter != after || r.MultiplierBefore != 1 || r.MultiplierAfter != 1
                    || r.StreakBefore != 0 || r.StreakAfter != 0 || r.RatingBefore.HasValue || r.RatingAfter.HasValue
                    || r.FloorsReached.Count != 0 || r.LegendReached != (stars < 150 && after == 150)))
                    throw new InvalidOperationException("Invalid folded ladder receipt.");
                if (m.ProgressionRuleVersion == LocalCoreProgression.ScheduleVersion
                    && (long)m.ClassXpAfter - m.ClassXpBefore != (m.DeckHero == null ? 0 : LocalProfile.MatchClassXp(m)))
                    throw new InvalidOperationException("Invalid class XP receipt.");
                ValidateTierRewards(m.TierRewards);
            }
            stars = after;
        }
    }
    // Sandbox All decks receipts: zero-gold Deck receipts written by the creation commit (GrantAllDecks), first in the
    // ledger, only on saves created with the option. Every bought deck has a positive pre-kit price (Purchase refuses 0).
    internal const string SandboxDeckRequestPrefix = "sandbox:all-decks:";
    private static void ValidatePurchases(LocalProfileData p, bool allCosmetics, bool allDecks = false) {
        if (p.PurchaseSequence < 0 || p.Purchases == null || p.Purchases.Count > MaximumRecords || p.PurchaseSequence != p.Purchases.Count
            || p.OwnedRecipes == null || p.OwnedHeroSkins == null || p.OwnedCardBacks == null || p.FavoriteHeroSkins == null
            || p.OwnedCoins == null || !p.OwnedCoins.Contains(1) || p.FavoriteCoins == null || p.FavoriteCoins.Count == 0
            || p.OwnedCoins.Distinct().Count() != p.OwnedCoins.Count || p.FavoriteCoins.Distinct().Count() != p.FavoriteCoins.Count
            || p.FavoriteCoins.Any(id => !(p.OwnedCoins.Contains(id) || allCosmetics && LocalShop.SupportedCheatCoins.Contains(id)))
            || !p.OwnedCardBacks.Contains(0) || p.FavoriteCardBacks == null || p.FavoriteCardBacks.Count == 0
            || p.FavoriteCardBacks.Distinct().Count() != p.FavoriteCardBacks.Count
            || p.FavoriteCardBacks.Any(id => !(p.OwnedCardBacks.Contains(id)
                || allCosmetics && LocalShop.SupportedCheatCardBacks.Contains(id)))
            || p.OwnedRecipes.Distinct().Count() != p.OwnedRecipes.Count || p.OwnedHeroSkins.Distinct().Count() != p.OwnedHeroSkins.Count
            || p.OwnedCardBacks.Distinct().Count() != p.OwnedCardBacks.Count
            || p.FavoriteHeroSkins.Any(f => f == null || !ValidFavorite(p, f.Class, f.HeroCardId, allCosmetics))
            || p.FavoriteHeroSkins.Select(f => f.Class).Distinct().Count() != p.FavoriteHeroSkins.Count)
            throw new InvalidOperationException("Invalid shop ownership ledger.");
        p.FavoriteCardBacks.Sort();
        p.FavoriteCoins.Sort();
        ValidateHubCosmetics(p, allCosmetics);
        if (p.OwnedPets == null || p.OwnedPets.Distinct(StringComparer.Ordinal).Count() != p.OwnedPets.Count
            || p.OwnedPets.Any(pet => !LocalShop.Default.KnownPet(pet))
            || p.SelectedPet != null && LocalShop.Default.PetFamily(p.SelectedPet) == null
            || p.Version < 14 && (p.OwnedPets.Count != 0 || p.SelectedPet != null))
            throw new InvalidOperationException("Invalid pet ownership or selection.");
        ValidatePetProgress(p);
        if (p.SelectedPet != null) {
            string family = LocalShop.Default.PetFamily(p.SelectedPet);
            int xp = p.PetProgress.SingleOrDefault(row => row.Pet == family)?.Xp ?? 0;
            if (!(p.OwnedPets.Contains(family) && LocalShop.Default.PetUnlocked(p.SelectedPet, xp))
                && !(allCosmetics && LocalShop.SupportedCheatPetForms.Contains(p.SelectedPet)))
                throw new InvalidOperationException("Invalid pet ownership or selection.");
        }
        var acquired = p.Collection.ToDictionary(c => c.Id, c => c.Acquired, StringComparer.Ordinal);
        var recipes = new HashSet<string>(p.OwnedRecipes.Where(id => MilestoneRewards(p).SelectMany(r => r.Grants)
            .Any(g => g.Kind == LocalTierGrantKind.Recipe && g.Ref == id)));
        var backs = new HashSet<int>(MilestoneRewards(p).SelectMany(r => r.Grants)
            .Where(g => g.Kind == LocalTierGrantKind.CardBack && int.TryParse(g.Ref, out int id) && LocalShop.LadderExclusiveCardBacks.Values.Contains(id))
            .Select(g => int.Parse(g.Ref, System.Globalization.CultureInfo.InvariantCulture))) { 0 }; var skins = new HashSet<string>();
        var coins = new HashSet<int> { 1 };
        var decks = new HashSet<string>(); var requests = new HashSet<string>(); var deckIds = new HashSet<long>();
        var themes = new HashSet<string>(StringComparer.Ordinal); var boards = new HashSet<int>(); var pets = new HashSet<string>(StringComparer.Ordinal);
        int sandboxDecks = 0;
        for (int i = 0; i < p.Purchases.Count; i++) {
            var r = p.Purchases[i];
            if (r == null || r.Sequence != i + 1 || !LocalShop.ValidId(r.RequestId) || !requests.Add(r.RequestId)
                || r.CatalogVersion != LocalShop.Default.Version || r.GoldPaid < 0 || r.PriceBeforeKit < 0 || r.Granted == null)
                throw new InvalidOperationException("Invalid purchase receipt.");
            bool sandbox = r.RequestId.StartsWith(SandboxDeckRequestPrefix, StringComparison.Ordinal);
            if (sandbox ? !allDecks || r.Kind != "Deck" || i != sandboxDecks++ || r.RequestId != SandboxDeckRequestPrefix + r.ProductId
                    || r.PriceBeforeKit != 0 || r.GoldPaid != 0 || r.KitId != null
                : r.Kind == "Deck" && r.PriceBeforeKit <= 0)
                throw new InvalidOperationException("Invalid Sandbox deck receipt.");
            var product = LocalShop.Default.ReceiptProduct(r.ProductId); var g = r.Granted;
            if (r.Kind != product.Kind || r.Quantity < product.MinQuantity || r.Quantity > product.MaxQuantity
                || g.Packs == null || g.Cards == null || g.Packs.Count != (r.Kind == "Pack" ? 1 : 0)
                || (r.Kind != "Deck" && g.Cards.Count != 0) || g.RecipeId != product.RecipeId
                || g.CardBack != product.CardBack || g.Coin != product.Coin || g.HeroSkin != product.HeroSkin || g.DeckId.HasValue != (r.Kind == "Deck")
                || g.HubTheme != product.HubTheme || g.Board != product.Board || g.Pet != product.Pet
                || (r.Kind == "Coin" && r.PriceBeforeKit <= 0)
                || (r.Kind == "Pet" && p.Version < 14)
                || ((r.Kind == "HubTheme" || r.Kind == "Board") && p.Version < 12))
                throw new InvalidOperationException("Purchase grants do not match the product.");
            int paid = product.Price * r.Quantity;
            if (r.Quantity == 1 && LocalShop.Default.IsHistoricCosmeticPrice(r.Kind, r.PriceBeforeKit)) paid = r.PriceBeforeKit;
            // A pet is one-off and kit-free: the price paid at the time stands, so a later price change keeps receipts valid.
            if (r.Kind == "Pet" && r.Quantity == 1 && g.Pet != null && r.PriceBeforeKit > 0) paid = r.PriceBeforeKit;
            switch (r.Kind) {
                case "Pack":
                    if (g.Packs[0] == null || g.Packs[0].PackType != product.PackType || g.Packs[0].Count != r.Quantity)
                        throw new InvalidOperationException("Invalid pack purchase grant."); break;
                case "Recipe": if (!recipes.Add(g.RecipeId)) throw new InvalidOperationException("Repeated recipe purchase."); break;
                case "CardBack": if (!backs.Add(g.CardBack.Value)) throw new InvalidOperationException("Repeated card back purchase."); break;
                case "Coin": if (!coins.Add(g.Coin.Value)) throw new InvalidOperationException("Repeated coin purchase."); break;
                case "HeroSkin": if (!skins.Add(g.HeroSkin)) throw new InvalidOperationException("Repeated hero skin purchase."); break;
                case "HubTheme": if (!LocalHubCosmetics.PaidTheme(g.HubTheme) || !themes.Add(g.HubTheme)) throw new InvalidOperationException("Repeated or free theme purchase."); break;
                case "Board": if (!LocalHubCosmetics.PaidBoard(g.Board.Value) || !boards.Add(g.Board.Value)) throw new InvalidOperationException("Repeated or free board purchase."); break;
                case "Pet": if (!LocalShop.Default.KnownPet(g.Pet) || !pets.Add(g.Pet)) throw new InvalidOperationException("Repeated or free pet purchase."); break;
                case "Deck":
                    if (!decks.Add(g.RecipeId) || !NativeCustomId(g.DeckId.Value) || g.DeckId.Value >= p.NextCustomDeckId || !deckIds.Add(g.DeckId.Value)
                        || g.Cards.Any(c => c == null) || g.Cards.Select(c => c.CardId).Distinct().Count() != g.Cards.Count)
                        throw new InvalidOperationException("Invalid deck purchase grant.");
                    // A deck receipt records what was paid (PriceBeforeKit). Rarity, copy prices, recipe prices and Standard
                    // legality may change later, so the card rows are checked for shape and ledger consistency only.
                    foreach (var c in g.Cards) {
                        // Core rows were locked when bought: Core grants are monotonic, so the grant still covers them.
                        bool core = LocalCardCatalog.IsCore(c.CardId);
                        if (!(KnownLedgerCard(c.CardId) || IsCollectibleCard(c.CardId))
                            || c.CountBefore < 0 || c.CountBefore >= c.CountAfter
                            || c.CountAfter > MaximumPlayset || c.AcquiredBefore < c.CountBefore || c.AcquiredBefore > MaximumPlayset
                            || (core ? !LocalCoreProgression.IsAvailable(c.CardId) || c.AcquiredBefore != c.CountBefore
                                || c.CountAfter > LocalCoreProgression.ById[c.CardId].Copies
                                || (p.CoreGrants.SingleOrDefault(grant => grant.Id == c.CardId)?.Count ?? 0) < c.CountAfter
                                : (acquired.TryGetValue(c.CardId, out int count) ? count : 0) < c.CountAfter))
                            throw new InvalidOperationException("Invalid purchased card delta.");
                    }
                    paid = r.PriceBeforeKit;
                    recipes.Add(g.RecipeId); break;
            }
            var kit = r.KitId == null ? null : p.DeckKits.SingleOrDefault(k => k.Id == r.KitId);
            if (r.KitId != null && (r.Kind != "Deck" || kit == null || kit.RedeemedPurchaseSequence != r.Sequence))
                throw new InvalidOperationException("Invalid redeemed kit receipt.");
            if (r.PriceBeforeKit != paid || r.GoldPaid != Math.Max(0, paid - (kit?.GoldCap ?? 0))) throw new InvalidOperationException("Invalid purchase price receipt.");
        }
        skins.UnionWith(p.PortraitGrants.Where(row => row.Kind == "honored").Select(row => row.CardId));
        backs.UnionWith(p.Achievements.Grants.Where(row => row.CardBackId > 0).Select(row => row.CardBackId));
        skins.UnionWith(p.Achievements.Grants.Where(row => row.HeroSkinId != null).Select(row => row.HeroSkinId));
        if (!recipes.SetEquals(p.OwnedRecipes) || !backs.SetEquals(p.OwnedCardBacks) || !coins.SetEquals(p.OwnedCoins) || !skins.SetEquals(p.OwnedHeroSkins)
            || !themes.SetEquals(p.OwnedHubThemes) || !boards.SetEquals(p.OwnedBoards) || !pets.SetEquals(p.OwnedPets))
            throw new InvalidOperationException("Ownership differs from purchase receipts.");
    }
    // Pet XP comes only from committed match receipts, never native animations or favourite callbacks.
    private static void ApplyPetReceipt(LocalProfileData next, LocalMatchRecord match) {
        if (!match.PetXpBefore.HasValue) { match.PetSmall = match.PetMedium = match.PetLarge = null; return; }
        string family = LocalShop.Default.PetFamily(match.Pet);
        var progress = next.PetProgress.Single(row => row.Pet == family);
        if (progress.Xp != match.PetXpBefore.Value) throw new InvalidOperationException("Pet XP changed during the match.");
        match.PetRules = LocalShop.Default.PetRules(family, match.PetPacingPercent);
        match.PetPacingPercent = null; // frozen in the rule string's mult=; the pending-only field is not part of the receipt
        if (LocalShop.PetRulesUseCounters(match.PetRules)) {
            // Recovery and callers with no pet events record zero. The input is bounded by what one game can produce.
            match.PetSmall = match.PetSmall ?? 0; match.PetMedium = match.PetMedium ?? 0; match.PetLarge = match.PetLarge ?? 0;
            if (!PetCountsValid(match, match.PetRules)) throw new InvalidOperationException("Invalid pet XP counts.");
        } else match.PetSmall = match.PetMedium = match.PetLarge = null;
        match.PetXpAfter = LocalShop.PetXpAfter(match.PetRules, progress.Xp, match);
        progress.Xp = match.PetXpAfter.Value;
    }
    // Counters must be non-negative, within the event bound frozen in the receipt's own rule string (so a later like-table
    // edit never invalidates history), and only on the counters the rule maps likes and treats to.
    // The handoff from a match to settlement: presentation-derived counts can never cost gold, packs or ladder. Counts outside
    // what one game can produce (negative, or more events than the family's likes plus treats) are clamped, small counter
    // first; saved receipts are still validated strictly.
    internal static LocalPetXpCounts ClampPetXp(LocalPetXpCounts counts, string pet, out bool clamped) {
        clamped = false;
        string family = LocalShop.Default.PetFamily(pet);
        if (counts == null || family == null) return null;
        var policy = PetXpPolicy.Current;
        int room = policy.MaxTicks(LocalShop.Default.PetNativeId(family));
        // Counters the current policy maps nothing to are dropped first, else settlement would reject them.
        Func<PetCounter, bool> mapped = counter => !policy.Flat && (policy.Like == counter || policy.Treat == counter);
        int small = mapped(PetCounter.Small) ? Math.Min(Math.Max(0, counts.Small), room) : 0; room -= small;
        int medium = mapped(PetCounter.Medium) ? Math.Min(Math.Max(0, counts.Medium), room) : 0; room -= medium;
        int large = mapped(PetCounter.Large) ? Math.Min(Math.Max(0, counts.Large), room) : 0;
        clamped = small != counts.Small || medium != counts.Medium || large != counts.Large;
        return new LocalPetXpCounts { Small = small, Medium = medium, Large = large };
    }
    private static bool PetCountsValid(LocalMatchRecord match, string rules) {
        if (!(match.PetSmall >= 0 && match.PetMedium >= 0 && match.PetLarge >= 0)) return false;
        var rule = LocalShop.ParseRuleForValidation(rules);
        if ((long)match.PetSmall + match.PetMedium + match.PetLarge > rule.Events) return false;
        return (match.PetSmall == 0 || rule.Like == "small" || rule.Treat == "small")
            && (match.PetMedium == 0 || rule.Like == "medium" || rule.Treat == "medium")
            && (match.PetLarge == 0 || rule.Like == "large" || rule.Treat == "large");
    }
    private static void ValidatePetProgress(LocalProfileData p) {
        // XP above today's cap is legal history after a cap cut; levels clamp in PetLevel/PetLevelXp.
        if (p.PetProgress == null || p.PetProgress.Any(row => row == null || !LocalShop.Default.KnownPet(row.Pet) || row.Xp < 0)
            || p.PetProgress.Select(row => row.Pet).Distinct(StringComparer.Ordinal).Count() != p.PetProgress.Count
            || !new HashSet<string>(p.OwnedPets, StringComparer.Ordinal).SetEquals(p.PetProgress.Select(row => row.Pet)))
            throw new InvalidOperationException("Invalid pet progress ledger.");
        var earned = p.PetProgress.ToDictionary(row => row.Pet, row => 0, StringComparer.Ordinal);
        foreach (var match in p.Matches.Concat(p.Pending == null ? Enumerable.Empty<LocalMatchRecord>() : new[] { p.Pending })) {
            bool pending = ReferenceEquals(match, p.Pending);
            string family = LocalShop.Default.PetFamily(match.Pet);
            if (match.Pet != null && family == null || match.Pet == null && (match.PetXpBefore.HasValue || match.PetXpAfter.HasValue)
                || p.Version < 14 && (match.Pet != null || match.PetXpBefore.HasValue || match.PetXpAfter.HasValue)
                || !match.PetXpBefore.HasValue && match.PetXpAfter.HasValue
                || match.PetRules != null && !match.PetXpAfter.HasValue
                || (match.PetSmall.HasValue || match.PetMedium.HasValue || match.PetLarge.HasValue) && (!match.PetXpBefore.HasValue || ReferenceEquals(match, p.Pending))
                // The test pacing knob lives on a pending record with a pet recipient only, inside the allowed range, and never on a receipt.
                || match.PetPacingPercent.HasValue && (!ReferenceEquals(match, p.Pending) || !match.PetXpBefore.HasValue || p.Version < 15
                    || match.PetPacingPercent < PetPacingKnob.Min || match.PetPacingPercent > PetPacingKnob.Max))
                throw new InvalidOperationException("Invalid match pet snapshot.");
            if (!match.PetXpBefore.HasValue) continue;
            if (family == null || !earned.TryGetValue(family, out int before) || before != match.PetXpBefore.Value
                || pending && match.PetXpAfter.HasValue)
                throw new InvalidOperationException("Invalid match pet XP before.");
            if (pending) continue;
            // Event rules require the recorded counters (V15); flat rules record none. Counts are bounded by the event limit frozen in the rule string.
            bool counters = match.PetRules != null && LocalShop.PetRulesUseCounters(match.PetRules);
            if (counters ? p.Version < 15 || !match.PetSmall.HasValue || !match.PetMedium.HasValue || !match.PetLarge.HasValue || !PetCountsValid(match, match.PetRules)
                : match.PetSmall.HasValue || match.PetMedium.HasValue || match.PetLarge.HasValue)
                throw new InvalidOperationException("Invalid match pet XP counts.");
            // Recompute under the rules recorded at settlement, not today's catalogue.
            int after = LocalShop.PetXpAfter(match.PetRules ?? LocalShop.LegacyPetRules, before, match);
            if (match.PetXpAfter != after) throw new InvalidOperationException("Invalid match pet XP after.");
            earned[family] = after;
        }
        if (p.PetProgress.Any(row => earned[row.Pet] != row.Xp)) throw new InvalidOperationException("Pet XP differs from match receipts.");
    }
    // V12 theme/board ledger. Older profiles carry none of it. A selection must be owned (free ones always are).
    private static void ValidateHubCosmetics(LocalProfileData p, bool allCosmetics) {
        if (p.OwnedHubThemes == null || p.OwnedBoards == null
            || p.OwnedHubThemes.Distinct(StringComparer.Ordinal).Count() != p.OwnedHubThemes.Count || p.OwnedBoards.Distinct().Count() != p.OwnedBoards.Count
            || p.OwnedHubThemes.Any(key => !LocalHubCosmetics.PaidTheme(key)) || p.OwnedBoards.Any(id => !LocalHubCosmetics.PaidBoard(id)))
            throw new InvalidOperationException("Invalid theme or board ownership.");
        if (p.Version < 12) {
            // Older saves carry none of it; a default selection (a downgraded copy) is harmless and migration resets it.
            if (p.OwnedHubThemes.Count != 0 || p.OwnedBoards.Count != 0 || (p.SelectedHubTheme != null && p.SelectedHubTheme != LocalHubCosmetics.ClassicTheme)
                || p.SelectedBoard != 0)
                throw new InvalidOperationException("Theme and board ownership requires V12.");
            return;
        }
        string theme = p.SelectedHubTheme;
        if (theme == null || !(LocalHubCosmetics.FreeTheme(theme) || p.OwnedHubThemes.Contains(theme)
                || allCosmetics && LocalShop.SupportedCheatHubThemes.Contains(theme))
            || !(LocalHubCosmetics.FreeBoard(p.SelectedBoard) || p.OwnedBoards.Contains(p.SelectedBoard)
                || allCosmetics && LocalShop.SupportedCheatBoards.Contains(p.SelectedBoard)))
            throw new InvalidOperationException("Invalid theme or board selection.");
    }
}

internal enum PetCounter { Small = 0, Medium = 1, Large = 2 }

// Test-only pacing: a one-shot per-match pacing percent so a live test can reach a level-up in one match (docs/design/pet-xp.md).
// A file in a TEST LANE's reports directory (lanes 1-3 only: the plugin leaves Path null on lane 0 and on the Deck), two lines:
//   runId=<the current launch's run id>   (bound to this launch exactly like the enable-test-control flag)
//   percent=N                              (100..10000)
// It is honoured only while test control is live, consumed (deleted) whenever the plugin sees it, dropped when the run id is
// not this launch's, and any file left over from an earlier launch is removed at plugin start. The percent is then frozen into
// the next pending match and the receipt's mult=.
internal static class PetPacingKnob
{
    internal const int Min = 100, Max = 10000;
    internal static string Path;    // the lane's pet-xp-pacing file; null on lane 0 and on the Deck (knob off)
    internal static string RunId;   // this launch's run id
    // The knob exists on test lanes 1-3 only; lane 0 (the owner's client, test control live after every deploy) and the Deck get null.
    internal static string PathFor(int laneId, bool deck, string reports) => laneId >= 1 && laneId <= 3 && !deck && !string.IsNullOrEmpty(reports)
        ? reports + @"\pet-xp-pacing" : null;
    internal static int Parse(string text, string runId) {
        string[] rows = (text ?? "").Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        int percent;
        if (string.IsNullOrEmpty(runId) || rows.Length != 2 || rows[0] != "runId=" + runId || !rows[1].StartsWith("percent=", StringComparison.Ordinal)
            || !int.TryParse(rows[1].Substring(8), NumberStyles.None, CultureInfo.InvariantCulture, out percent) || percent < Min || percent > Max)
            throw new InvalidDataException("Expected runId=<this launch> and percent=" + Min + ".." + Max + ".");
        return percent;
    }
    // Plugin start: no knob file survives into a new launch (it carries the previous launch's run id at best).
    internal static void ClearStale(string path) {
        try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }
    // Returns the percent for the next match, or null. A file the plugin sees is deleted even when test control is off or the
    // run id is stale; if it cannot be deleted the knob is ignored so it can never apply twice.
    internal static int? Take(string path, bool testControlActive, string runId, Action<string> log = null) {
        if (string.IsNullOrEmpty(path)) return null;
        try {
            if (!File.Exists(path)) return null;
            string text = File.ReadAllText(path);
            File.Delete(path);
            if (!testControlActive) { log?.Invoke("pet-xp pacing knob dropped: test control is off"); return null; }
            return Parse(text, runId);
        }
        catch (Exception error) { try { log?.Invoke("pet-xp pacing knob ignored: " + error.Message); } catch { } return null; }
    }
}

// Every pet XP number lives here, so an owner ruling is a data change. Settled receipts freeze the knobs in their rule
// string (LocalShop.PetRules), so changing a preset never invalidates history.
//
// Owner ruling 2026-10-28: preset B (10 XP per like via the MEDIUM counter, each like once per game, plus 10 per treat feed,
// at most 2 feeds; conceded or under-4-turn games pay 0). The like lists of Krush, Rabbit'ath and Arfus are PROPOSED
// (docs/design/pet-xp.md). Pacing multiplier hook: backlog #52 (per-save grind presets) will set PacingPercent; 100 today.
internal sealed class PetXpPolicy
{
    // DBF PetXp row 1, shared by every enabled family (data/shop/client/pets-37.0.0.254477.json; checked by tests/test_shop_pets.py).
    internal const int SmallXp = 5, MediumXp = 10, LargeXp = 10;
    internal static int CounterXp(PetCounter counter) => counter == PetCounter.Small ? SmallXp : counter == PetCounter.Medium ? MediumXp : LargeXp;

    internal PetCounter Like, Treat;      // which counter each like / each treat feed increments
    // Counter ticks one like / one feed adds. The DBF counters stay 5/10/10 so the native in-match display (count x 10) matches
    // what settles; owner ruling 2026-10-28 "20 per like, 20 per treat" is therefore two MEDIUM ticks per event.
    internal int LikeTicks = 1, TreatTicks = 1;
    internal int GameCap;                 // 0 = none; otherwise the most counter XP one game can pay (before pacing)
    internal bool PayConceded, PayShort;  // do conceded / under-gate games pay
    internal int GateTurns;               // engine turns an eligible game needs; also the in-match display gate
    internal int PacingPercent;           // pacing multiplier hook (#52), whole percent
    internal bool Flat;                   // preset C: flat per-match award (rule 1, MatchXp 100), no counters, no in-match XP

    internal static readonly PetXpPolicy PresetA = new PetXpPolicy { Like = PetCounter.Small, Treat = PetCounter.Small, GateTurns = 4, PacingPercent = 100 };
    internal static readonly PetXpPolicy PresetB = new PetXpPolicy { Like = PetCounter.Medium, Treat = PetCounter.Medium, LikeTicks = 2, TreatTicks = 2, GateTurns = 4, PacingPercent = 100 };
    internal static readonly PetXpPolicy PresetC = new PetXpPolicy { Flat = true, GateTurns = 4, PacingPercent = 100 };
    // The one switch. Tests may replace it and must restore it.
    internal static PetXpPolicy Current { get; set; } = PresetB;

    internal bool UsesCounters => !Flat;
    // Ticks below this engine turn are banked silently and shown in one update when it is reached.
    internal int DisplayGate => PayShort ? 0 : GateTurns;
    internal string RuleString(int cap, int ticks, int? pacingPercent = null) => FormatTicks(Like.ToString().ToLowerInvariant(), Treat.ToString().ToLowerInvariant(),
        LikeTicks, TreatTicks, SmallXp, MediumXp, LargeXp, GameCap, PayConceded, PayShort, GateTurns, pacingPercent ?? PacingPercent, ticks, cap);
    internal static string FormatTicks(string like, string treat, int likeTicks, int treatTicks, int small, int medium, int large, int gameCap,
        bool payConceded, bool payShort, int turns, int percent, int ticks, int cap) => string.Format(CultureInfo.InvariantCulture,
        "rule=3;like={0};treat={1};liketicks={2};treatticks={3};small={4};medium={5};large={6};gamecap={7};concede={8};short={9};turns={10};mult={11};ticks={12};cap={13}",
        like, treat, likeTicks, treatTicks, small, medium, large, gameCap, payConceded ? 1 : 0, payShort ? 1 : 0, turns, percent, ticks, cap);
    // Rule 2 (one tick per event). Only used to parse and write the receipts settled before the 20-XP ruling.
    internal static string FormatEvents(string like, string treat, int small, int medium, int large, int gameCap, bool payConceded,
        bool payShort, int turns, int percent, int events, int cap) => string.Format(CultureInfo.InvariantCulture,
        "rule=2;like={0};treat={1};small={2};medium={3};large={4};gamecap={5};concede={6};short={7};turns={8};mult={9};events={10};cap={11}",
        like, treat, small, medium, large, gameCap, payConceded ? 1 : 0, payShort ? 1 : 0, turns, percent, events, cap);

    // Treat feeds per game (NetCacheFeatures.MaxPetTreatsAllowedPerGame; inferred from retail logs, medium confidence).
    internal const int MaxTreats = 2;

    // Likes by native pet family id (PetNativeId). Each like pays at most once per game. Ids are evaluated by NativePetLikes.
    // Official constructed lists (Blizzard patch notes) minus Battlegrounds rows and minus likes the engine cannot evaluate;
    // Krush, Rabbit'ath and Arfus are PROPOSED. See docs/design/pet-xp.md for the row-by-row table and dropped rows.
    private static readonly Dictionary<int, string[]> Table = new Dictionary<int, string[]> {
        [3] = new[] { "rush-minion", "beast-6plus", "beast-4to5", "nature-spell", "minion-attacks", "attack-kill", "board-full" },
        [4] = new[] { "shadow-spell", "old-god-summon", "discover", "discard", "legendary-minion", "dark-gift-card", "hero-hurt-own-turn", "cost-10-card" },
        [6] = new[] { "frost-spell", "undead-or-dk-minion", "corpse-gained", "deathrattle-summon", "three-died-turn", "freeze-enemy", "hero-power", "dk-spell" },
        [7] = new[] { "nature-spell", "heal-hero", "dragon-5plus", "spells-3-turn", "ysera-minion", "mana-10" },
        [8] = new[] { "untyped-minion", "untyped-spell", "summon-taunt", "board-full", "cards-4-turn", "legendary-minion", "pirate" },
        [9] = new[] { "summon-12-attack", "fire-spell", "herald", "cost-10-card", "dragon-5plus", "discard", "armor-gain" },
        [10] = new[] { "hero-hurt-own-turn", "friendly-died-own-turn", "enemy-hit-for-1", "discard", "dark-gift-card", "shadow-spell", "colossal", "legendary-minion" },
        [12] = new[] { "nature-spell", "heal-hero", "discover", "legendary-minion", "qonzu-minion", "mana-10", "beast-4plus" },
        [15] = new[] { "damage-enemy-hero", "damage-10plus", "board-full", "kelthuzad", "undead-3minus", "shadow-spell", "deathrattle-summon", "three-died-turn", "attack-kill" },
        [16] = new[] { "damage-enemy-hero", "damage-10plus", "board-full", "minions-3-turn", "deathrattle-summon", "friendly-died-own-turn", "shadow-spell", "cost-10-card", "old-god-summon" },
    };
    internal static IReadOnlyList<string> LikesFor(int nativeFamilyId) => Table.TryGetValue(nativeFamilyId, out var likes) ? likes : new string[0];
    internal static IEnumerable<string> AllLikeIds => Table.Values.SelectMany(likes => likes).Distinct(StringComparer.Ordinal);
    // The most events (likes plus treat feeds) one game of this family can record.
    internal static int MaxEvents(int nativeFamilyId) => LikesFor(nativeFamilyId).Count + MaxTreats;
    // The most counter ticks one game of this family can bank under this policy; the bound frozen in the rule string.
    internal int MaxTicks(int nativeFamilyId) => LikesFor(nativeFamilyId).Count * LikeTicks + MaxTreats * TreatTicks;
}
