using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SabberStoneCore.Model;
using EngineCard = SabberStoneCore.Model.Card;

public enum DeckStyle { Tempo, Value, Control, Frost, Combo }

public sealed class LocalSideboard : LocalDeckCardContainer
{
    public string Source;
}

public sealed class LocalDeck
{
    public readonly string Id, Name, Hero, Opponent, Strategy, Source;
    public readonly DeckStyle Style;
    private readonly string[] cards;
    private Dictionary<string, string[]> sideboards = new Dictionary<string, string[]>();
    // Separate, temporary dungeon decks can grow beyond the 30-card constructed format.
    // They never enter the collection ledger or the native deck editor.
    internal bool DungeonDeck { get; private set; }
    internal static LocalDeck ForDungeon(string hero, IEnumerable<string> ids)
    {
        var deck = new LocalDeck("Dungeon expedition", hero, (ids ?? Enumerable.Empty<string>()).ToArray());
        deck.DungeonDeck = true;
        return deck;
    }
    public IReadOnlyDictionary<string, string[]> Sideboards => sideboards.ToDictionary(row => row.Key, row => row.Value.ToArray());
    internal LocalDeck WithSideboards(IEnumerable<LocalSideboard> selections, bool complete = false, bool allowInactive = false)
    {
        var rows = (selections ?? Enumerable.Empty<LocalSideboard>()).ToArray();
        LocalDecks.ValidateNativeSave(cards, rows, complete);
        var next = rows.ToDictionary(row => row.Source, row => row.Cards.ToArray());
        if (Id == "custom" && !allowInactive && next.SelectMany(row => row.Value).Any(id => !LocalCardCatalog.InActiveMeta(Cards.FromId(id))))
            throw new InvalidOperationException("Sideboard card is outside the active meta.");
        sideboards = next;
        return this;
    }
    public List<string> CardIds => cards.ToList();
    internal LocalDeck(string id, string name, string hero, string opponent, string strategy, DeckStyle style,
        string[] pairs, string[] singles = null, string source = null)
    {
        Id = id; Name = name; Hero = hero; Opponent = opponent; Strategy = strategy; Style = style; Source = source;
        cards = pairs.SelectMany(card => new[] { card, card }).Concat(singles ?? new string[0]).ToArray();
        LocalDecks.ValidateFabled(cards);
    }
    internal LocalDeck(string name, string hero, string[] cardIds)
    {
        Id = "custom"; Name = name; Hero = hero; Opponent = "Custom deck"; Strategy = "Custom deck";
        Style = DeckStyle.Value; Source = null; cards = cardIds.ToArray();
        LocalDecks.ValidateFabled(cards);
    }
    public List<EngineCard> Build()
    {
        var result = cards.Select(Cards.FromId).ToList();
        if (result.Any(c => c == null) || (DungeonDeck
            ? result.Count < 10 || result.Count > 70
            : result.Count != LocalDecks.DeckSize(cards)))
            throw new InvalidOperationException("Invalid local deck " + Id);
#if ENGINE_SOURCE
        if (!DungeonDeck)
            SabberStoneCore.Actions.DeckbuildingRules.Validate(cards, sideboards, SabberStoneCore.Enums.FormatType.FT_WILD);
#endif
        return result;
    }
}

public static class LocalDecks
{
    static LocalDecks()
    {
#if ENGINE_SOURCE
        foreach (var deck in TestLoaners)
            deck.WithSideboards(deck.CardIds.Where(SabberStoneCore.Actions.DeckbuildingRules.HasSideboard).Distinct()
                .Select(source => new LocalSideboard { Source = source, Cards =
                    SabberStoneCore.CardSets.Client.StandardSets.CollectibleIds.Select(Cards.FromId)
                        .Where(card => SabberStoneCore.Actions.DeckbuildingRules.Eligible(source, card, SabberStoneCore.Enums.FormatType.FT_STANDARD)
                            && !deck.CardIds.Contains(card.Id)).OrderBy(card => card.Id, StringComparer.Ordinal)
                        .Take(SabberStoneCore.Actions.DeckbuildingRules.SlotCount(source)).Select(card => card.Id).ToList() }), true);
#endif
    }

    // The vendor engine has no definitions for these cards; source-engine proofs check this against tag 997.
    internal static readonly IReadOnlyDictionary<string, int> VendorDeckSizes = new Dictionary<string, int>(StringComparer.Ordinal) {
        ["JAIL_430"] = 20, ["TIME_005"] = 40
    };

    public static int DeckSize(IEnumerable<string> ids)
    {
#if ENGINE_SOURCE
        return SabberStoneCore.Actions.FabledRules.DeckSize(ids);
#else
        int size = ids.Where(VendorDeckSizes.ContainsKey).Select(id => VendorDeckSizes[id]).FirstOrDefault();
        return size > 0 ? size : 30;
#endif
    }

    // Incomplete drafts may exceed a smaller deck-size rule (retail keeps such decks as "too many cards"),
    // so a 30-card list that gains Azalina stays loadable and editable. Complete decks use DeckSize exactly.
    internal static int DraftLimit(IEnumerable<string> ids) => Math.Max(DeckSize(ids), 30);
    // Catalogue and active meta are fixed for this process. A runtime reload must replace all these indexes together.
    // Fabled bundles and sideboard owners are deckbuilding identities only; none is owned virtually (owner 2026-10-30).
    // Primaries and sideboard owners are ordinary collectible cards owned through the ledger; companions are owned
    // through their primary in the same deck (WithDeckCompanions).
    private static readonly Lazy<IReadOnlyList<EngineCard>> knownBundleAndSideboardCards = new Lazy<IReadOnlyList<EngineCard>>(() =>
    {
#if ENGINE_SOURCE
        return Array.AsReadOnly(SabberStoneCore.CardSets.Client.StandardSets.CollectibleIds
                .Where(id => SabberStoneCore.Actions.FabledRules.IsFabled(id) || SabberStoneCore.Actions.DeckbuildingRules.HasSideboard(id))
                .SelectMany(id => new[] { id }.Concat(SabberStoneCore.Actions.FabledRules.Bundle(id)))
                .Select(Cards.FromId)
                .Where(card => card != null && card.Implemented).ToArray());
#else
        return Array.AsReadOnly(Array.Empty<EngineCard>());
#endif
    });
    internal static IEnumerable<EngineCard> KnownBundleAndSideboardCards => knownBundleAndSideboardCards.Value;
    private static readonly Lazy<IReadOnlyList<EngineCard>> knownDeckbuildingCards = new Lazy<IReadOnlyList<EngineCard>>(() => {
#if ENGINE_SOURCE
        var extras = new HashSet<string>(OfflinePoolPolicy.Load().Metas.SelectMany(meta => meta.ExtraCards), StringComparer.Ordinal);
        return Array.AsReadOnly(KnownBundleAndSideboardCards.Concat(Cards.All.Where(card => card.Collectible && card.Implemented
            && extras.Contains(card.Id))).ToArray());
#else
        return knownBundleAndSideboardCards.Value;
#endif
    });
    internal static IEnumerable<EngineCard> KnownDeckbuildingCards => knownDeckbuildingCards.Value;
    private static readonly Lazy<IReadOnlyList<EngineCard>> legalDeckbuildingCards = new Lazy<IReadOnlyList<EngineCard>>(() =>
        Array.AsReadOnly(KnownDeckbuildingCards.Where(LocalCardCatalog.InActiveMeta).ToArray()));
    private static readonly Lazy<IReadOnlyList<EngineCard>> customCards = new Lazy<IReadOnlyList<EngineCard>>(() =>
        Array.AsReadOnly(LocalCardCatalog.Supported.Concat(LegalDeckbuildingCards).Distinct().ToArray()));
    public static IEnumerable<EngineCard> LegalDeckbuildingCards => legalDeckbuildingCards.Value;
    public static IEnumerable<EngineCard> CustomCards => customCards.Value;
    private static readonly Lazy<HashSet<string>> customCardIds = new Lazy<HashSet<string>>(() =>
        new HashSet<string>(CustomCards.Select(card => card.Id), StringComparer.Ordinal));
    private static readonly Lazy<HashSet<string>> storedCardIds = new Lazy<HashSet<string>>(() =>
        new HashSet<string>(LocalCardCatalog.Current.Concat(KnownDeckbuildingCards).Select(card => card.Id), StringComparer.Ordinal));
    private static readonly Lazy<HashSet<string>> legalCardIds = new Lazy<HashSet<string>>(() =>
        new HashSet<string>(LegalDeckbuildingCards.Select(card => card.Id), StringComparer.Ordinal));
    internal static bool IsCustomCard(string id) => customCardIds.Value.Contains(id);
    internal static bool IsLegalDeckbuildingCard(string id) => legalCardIds.Value.Contains(id);

    public static readonly IReadOnlyList<string> PlayerHeroes = Array.AsReadOnly(
        LocalCoreProgression.Heroes.Where(IsImplementedPlayerHero).ToArray());

    private static bool IsImplementedPlayerHero(string id)
    {
        try
        {
            var hero = Cards.FromId(id);
            return hero != null && hero.Implemented && hero.Type == SabberStoneCore.Enums.CardType.HERO;
        }
        catch (KeyNotFoundException) { return false; }
    }

    public static readonly IReadOnlyList<LocalDeck> All = new[] {
        new LocalDeck("mage", "Mage: spell tempo", "HERO_08", "Ember, the Spellweaver", "Develop minions; freeze threats; finish with burn.", DeckStyle.Tempo, new[] {
            "NEW1_012", "EX1_608", "CS2_024", "CS2_029", "CS2_033", "CS2_023", "CS2_022", "CS2_032",
            "EX1_015", "EX1_556", "EX1_019", "CS2_182", "CS2_179", "CS2_200", "EX1_284" }),
        new LocalDeck("warrior", "Warrior: weapons and value", "HERO_01", "Bram, the Ironkeeper", "Trade with weapons; build armor; win the board.", DeckStyle.Value, new[] {
            "CS2_106", "CS2_114", "CS2_108", "EX1_606", "EX1_400", "NEW1_011", "CS2_112", "EX1_402",
            "EX1_603", "EX1_007", "EX1_556", "CS2_179", "CS2_182", "CS2_200", "CS2_222" }),
        new LocalDeck("priest", "Priest: healing control", "HERO_09", "Mira, the Patient", "Heal your board; answer big threats; outlast opponents.", DeckStyle.Control, new[] {
            "CS2_235", "CS2_004", "CS1_130", "CS2_234", "EX1_622", "CS1_112", "EX1_091", "EX1_335",
            "CS2_181", "DS1_055", "EX1_066", "EX1_019", "CS2_179", "CS2_182", "CS2_200" }),
        new LocalDeck("mage-frost", "Mage: frost and burst", "HERO_08", "Neris, the Winter Sage",
            "Freeze dangerous boards; use Doomsayer; prepare Alexstrasza and spell burst.", DeckStyle.Frost,
            new[] { "CS2_031", "CS2_027", "CS2_024", "CS2_023", "CS2_026", "CS2_033", "CS2_179",
                "CS2_029", "CS2_028", "NEW1_021", "EX1_096", "EX1_007", "EX1_284" },
            new[] { "CS2_032", "EX1_279", "EX1_012", "EX1_561" },
            "https://www.icy-veins.com/hearthstone/classic-freeze-mage-deck"),
        new LocalDeck("warrior-control", "Warrior: armor control", "HERO_01", "Garron, the Last Bastion",
            "Armor and efficient removal; finish with heavy threats or Grommash.", DeckStyle.Control,
            new[] { "CS2_108", "EX1_410", "EX1_400", "EX1_402", "EX1_603", "CS2_106", "EX1_606",
                "NEW1_011", "EX1_407", "EX1_007" },
            new[] { "EX1_391", "EX1_411", "EX1_414", "CS2_203", "EX1_005", "EX1_110", "EX1_016",
                "EX1_249", "EX1_298", "EX1_561" },
            "https://www.icy-veins.com/hearthstone/classic-control-warrior-deck"),
        new LocalDeck("priest-combo", "Priest: Inner Fire combo", "HERO_09", "Selene, the Spiritbinder",
            "Keep a healthy minion alive; combine Divine Spirit with Inner Fire for a decisive attack.", DeckStyle.Combo,
            new[] { "CS2_235", "CS2_004", "CS1_129", "CS2_236", "EX1_621", "EX1_591", "CS2_181",
                "EX1_335", "CS2_234", "EX1_622", "NEW1_020", "CS2_117", "EX1_284", "CS2_179", "EX1_007" },
            source: "https://hearthstone-decks.net/"),
        new LocalDeck("paladin", "Paladin: Recruit value", "HERO_04", "Aldric, the Just",
            "Build Recruits; strengthen the board; trade efficiently.", DeckStyle.Value,
            new[] { "CS2_087", "CS2_088", "CS2_089", "CS2_091", "CS2_092", "CS2_093", "CS2_094", "CS2_097",
                "EX1_360", "EX1_371", "CS2_179", "CS2_182", "CS2_200", "CS2_222", "EX1_019" }),
        new LocalDeck("paladin-control", "Paladin: board control", "HERO_04", "Seraph, the Arbiter",
            "Control attack and health; build Recruits; win with durable threats.", DeckStyle.Control,
            new[] { "CS2_089", "CS2_093", "CS2_094", "CS2_097", "EX1_360", "EX1_371", "EX1_382", "EX1_619",
                "CS2_179", "CS2_200", "EX1_366" },
            new[] { "CS2_087", "CS2_088", "CS2_092", "EX1_362", "EX1_354", "EX1_355", "EX1_005", "EX1_561" })
#if STANDARD_PROOF
        , StandardProofNative.Loaner("proof-mage"), StandardProofNative.Loaner("proof-warrior")
#endif
    };
#if ENGINE_SOURCE
    // TEST LOANER, not a starter archetype: draws Discover (Netherspite Historian with dragons) and
    // Tradeable Core cards so the endpoint driver reaches those decision paths on the real client.
    public const string TestDecisionsId = "test-decisions", TestCenariusId = "test-cenarius";
    public const string TestStandardBatch1Id = "test-standard-batch-1", TestStandardBatch1OpponentId = "test-standard-batch-1-opp";
    // Frozen batch-1+2 opponents: player qualification recipes evolve independently.
    private static readonly string[] DruidOpponentCards = new[] { "END_035", "TIME_434", "TLC_427", "TLC_468", "TLC_621", "TLC_831", "EDR_495" }
        .SelectMany(id => new[] { id, id }).Concat(new[] { "BE_850t", "BE_856", "BE_859", "DINO_130", "DINO_421", "DINO_432", "EDR_272", "TIME_703",
                "TLC_233", "TLC_234", "TLC_237", "DINO_410", "FIR_958", "BE_994", "CATA_111", "CATA_720" }).ToArray();
    private static readonly string[] RogueOpponentCards = new[] { "TLC_468", "TLC_621", "TLC_831", "EDR_495", "BE_994", "CATA_111", "END_035", "TIME_434",
                "TLC_427" }
        .SelectMany(id => new[] { id, id }).Concat(new[] { "BE_725", "CAP_001", "CATA_201", "DINO_408", "EDR_523", "JAIL_720", "TIME_710", "FIR_958",
                "CATA_720", "DINO_410", "BE_193", "CATA_612" }).ToArray();
    private static readonly string[] WarlockOpponentCards = new[] { "TLC_831", "EDR_495", "BE_994", "CATA_111", "END_035", "TIME_434", "TLC_427", "TLC_468",
                "TLC_621" }
        .SelectMany(id => new[] { id, id }).Concat(new[] { "EDR_485", "JAIL_399", "JAIL_513", "TIME_025", "TIME_028", "TIME_029", "FIR_958", "CATA_720",
                "DINO_410", "BE_193", "CATA_612", "DINO_411" }).ToArray();
    private static readonly string[] HunterOpponentCards = new[] { "TLC_427", "TLC_468", "TLC_621", "TLC_831", "EDR_495" }
        .SelectMany(id => new[] { id, id }).Concat(new[] { "TIME_434", "DINO_403", "BE_550", "BE_732", "CATA_558", "EDR_014", "EDR_261", "EDR_481", "JAIL_882", "MEND_302",
                "TIME_601", "TIME_603", "TIME_605", "TLC_826", "DINO_410", "FIR_958", "BE_994", "CATA_111",
                "CATA_720", "END_035" }).ToArray();
    public const string TestRushSilenceId = "test-rush-silence", TestShowcaseId = "test-showcase-deathknight";
    public static readonly IReadOnlyList<LocalDeck> TestLoaners = new[] {
        new LocalDeck(TestDecisionsId, "TEST: decision paths", "HERO_08", "Test opponent: Cenarius",
            "Test loaner for live qualification of Discover, Trade and prompts. Not a starter deck.", DeckStyle.Value,
            new[] { "CORE_KAR_062", "CORE_EX1_043", "CORE_LOOT_137", "CORE_UNG_848", "CORE_EX1_005", "CORE_SW_066",
                "CS2_179", "CS2_024", "CS2_029", "CS2_023", "EX1_015", "CS2_182", "CS2_033", "CS2_026" },
            new[] { "CORE_EX1_002", "CORE_AT_123" }),
        // Bitter End waits for native dual-class picker qualification; do not broaden class rules here.
        new LocalDeck(TestStandardBatch1Id, "TEST: Standard batch 1", "HERO_01", "Test opponent: Standard batch 1",
            "Test loaner for live qualification of Standard batch 1. Not a starter deck.", DeckStyle.Value,
            new[] { "JAIL_456", "TLC_606", "EDR_457", "TIME_056", "BE_193" },
            new[] { "EDR_468", "TIME_871", "BE_560", "BE_572", "JAIL_376",
                "EDR_486", "EDR_598", "CATA_612", "EDR_492", "TIME_059", "TIME_051", "TIME_720",
                "TIME_062", "DINO_411", "JAIL_007", "TLC_249", "EDR_110", "EDR_571", "FIR_929", "EDR_861" }),
        // Explicit legal recipes: class cards first, then distributed neutrals; overflow keeps 30-card limits.
        new LocalDeck("test-standard-batch-2-warrior", "TEST: Standard Warrior", "HERO_01", "Test opponent: Standard batch 2 Druid",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CAP_107", "CAP_102", "CAP_103", "CAP_106", "CATA_580", "CATA_582", "CATA_585", "CATA_610",
                "DINO_400", "EDR_459", "EDR_471", "EDR_531", "END_021", "JAIL_029", "JAIL_307", "JAIL_311",
                "JAIL_386", "JAIL_387", "TIME_715", "TIME_716", "TIME_750", "TIME_870", "TIME_873", "TLC_478",
                "TLC_600", "TLC_601", "TLC_620", "TLC_622", "TLC_623", "CORE_NX2_028" }),
        new LocalDeck("test-standard-batch-2-priest", "TEST: Standard Priest", "HERO_09", "Test opponent: Standard batch 2 Rogue",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "BE_228", "BE_790", "CAP_801", "CAP_803", "CAP_804", "CAP_805", "CATA_302", "CATA_303",
                "CATA_304", "CATA_305", "CATA_308", "DINO_428", "EDR_476", "FIR_777", "JAIL_913", "JAIL_941",
                "JAIL_942", "TIME_037", "TIME_427", "TIME_429", "TIME_431", "TIME_447", "TLC_811", "TLC_818",
                "TLC_820", "TLC_835", "BE_220", "CORE_BAR_310", "CORE_BAR_311", "CORE_EX1_193" }),
        new LocalDeck("test-standard-batch-2-mage", "TEST: Standard Mage", "HERO_08", "Test opponent: Standard batch 2 Warlock",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "BE_258", "BE_270", "CATA_485", "DINO_429", "FIR_910", "JAIL_312", "JAIL_379", "TIME_006",
                "TIME_855", "TIME_856", "TIME_858", "TLC_220", "BE_251", "CATA_484", "CORE_BAR_812", "CORE_BT_072",
                "CORE_DRG_107", "CORE_SW_108", "JAIL_125", "BE_008", "BE_207", "BE_255", "BE_701", "BE_733",
                "BE_861", "BE_993", "BE_994", "BE_998", "CATA_111", "CATA_208" }),
        new LocalDeck("test-standard-batch-2-paladin", "TEST: Standard Paladin", "HERO_04", "Test opponent: Standard batch 2 Hunter",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "BE_152", "BE_153", "BE_154", "CATA_473", "CATA_475", "CATA_478", "EDR_252", "EDR_253",
                "FIR_941", "FIR_961", "JAIL_035", "JAIL_118", "JAIL_326", "JAIL_329", "JAIL_377", "MEND_800",
                "MEND_803", "MEND_804", "TIME_015", "TLC_441", "CORE_BAR_878", "CORE_BT_292", "CORE_SW_047", "CORE_TSC_076",
                "JAIL_328", "TIME_044", "TLC_426", "EDR_257", "CATA_476", "END_010" }),
        new LocalDeck("test-standard-batch-2-druid", "TEST: Standard Druid", "HERO_06", "Test opponent: Standard Warrior",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "JAIL_202", "BE_850t", "BE_855", "BE_856", "BE_859", "CATA_133", "CATA_135", "CATA_138",
                "DINO_130", "DINO_421", "DINO_432", "EDR_272", "FIR_906", "JAIL_436", "JAIL_866", "JAIL_872",
                "TIME_023", "TIME_702", "TIME_703", "TLC_231", "TLC_232", "TLC_233", "TLC_234", "TLC_237",
                "TLC_257", "BE_854", "CORE_RLK_657", "CORE_SW_429", "CORE_SW_439", "CORE_ULD_133" }),
        new LocalDeck("test-standard-batch-2-rogue", "TEST: Standard Rogue", "HERO_03", "Test opponent: Standard Priest",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "BE_071", "BE_541", "BE_721", "BE_723", "BE_725", "CAP_000", "CAP_001", "CAP_003",
                "CAP_005", "CAP_006", "CATA_200", "CATA_201", "CATA_203", "DINO_408", "EDR_523", "EDR_527",
                "FIR_919", "JAIL_503", "JAIL_720", "JAIL_909", "TIME_710", "TIME_713", "TLC_518", "CORE_BT_701",
                "CORE_DMF_511", "CORE_ULD_280", "END_002", "END_031", "END_032", "EDR_525" }),
        new LocalDeck("test-standard-batch-2-warlock", "TEST: Standard Warlock", "HERO_07", "Test opponent: Standard Mage",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "EDR_485", "BE_729", "CAP_400", "CAP_404", "CAP_406", "CATA_490", "CATA_494", "CATA_498",
                "CATA_725", "DINO_131", "DINO_132", "FIR_954", "FIR_955", "JAIL_399", "JAIL_510", "JAIL_513",
                "JAIL_514", "JAIL_906", "TIME_025", "TIME_026", "TIME_027", "TIME_028", "TIME_029", "TIME_031",
                "TLC_463", "CORE_EX1_312", "CORE_SCH_181", "CORE_SW_088", "CORE_ULD_165", "EDR_490" }),
        new LocalDeck("test-standard-batch-2-hunter", "TEST: Standard Hunter", "HERO_05", "Test opponent: Standard Paladin",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CATA_558", "BE_550", "BE_730", "BE_732", "CATA_551", "CATA_552", "CATA_560", "EDR_014",
                "EDR_261", "EDR_481", "FIR_909", "FIR_953", "JAIL_395", "JAIL_881", "JAIL_882", "MEND_302",
                "MEND_305", "TIME_601", "TIME_603", "TIME_605", "TLC_366", "TLC_823", "TLC_826", "TLC_827",
                "TLC_828", "CORE_AV_337", "CORE_BAR_801", "CORE_BT_201", "CORE_DRG_256", "DINO_403" }),
        new LocalDeck("test-standard-batch-2-shaman", "TEST: Standard Shaman", "HERO_02", "Test opponent: Standard batch 2 Druid",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "TIME_218", "BE_171", "BE_303", "BE_349", "BE_842", "CATA_565", "CATA_724", "DINO_406",
                "DINO_413", "EDR_230", "EDR_232", "EDR_238", "EDR_477", "END_030", "FIR_778", "FIR_923",
                "JAIL_802", "TIME_212", "TIME_213", "TIME_214", "TIME_215", "TIME_216", "TLC_221", "TLC_222",
                "TLC_224", "TLC_225", "CORE_AT_052", "CORE_EX1_250", "CORE_WC_042", "CS3_007" }),
        new LocalDeck("test-standard-batch-2-demonhunter", "TEST: Standard Demon Hunter", "HERO_10", "Test opponent: Standard batch 2 Rogue",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CATA_528", "BE_564", "BE_873", "BE_877", "CATA_525", "CATA_526", "CATA_530", "DINO_138",
                "EDR_842", "EDR_890", "EDR_891", "EDR_892", "FIR_904", "JAIL_890", "TIME_449", "TLC_630",
                "TLC_833", "TLC_840", "TLC_901", "TLC_902", "TLC_903", "CORE_BT_351", "CORE_BT_416", "CORE_BT_493",
                "CORE_BT_510", "CORE_BT_801", "CORE_CATA_001", "CORE_TTN_843", "CORE_WC_701", "TLC_631" }),
        new LocalDeck(TestRushSilenceId, "TEST: Rush/Charge silence", "HERO_09", "Test opponent: Standard batch 1",
            "Test loaner for live qualification of Rush/Charge silence. Not a starter deck.", DeckStyle.Value,
            new[] { "EX1_332", "CS2_173", "CS2_124", "TIME_051", "EDR_492", "DINO_419", "CS2_172",
                "BE_228", "CS2_004", "EX1_015", "CS2_235", "EX1_096", "CS2_171", "CS2_120", "CS2_182" }),
        new LocalDeck("test-standard-extra-warrior", "TEST: Standard Warrior extra", "HERO_01", "Test opponent: Standard batch 2 Druid",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CORE_REV_990", "CORE_WW_329", "END_023", "CORE_DRG_403", "CORE_NEW1_023", "CORE_REV_023", "CORE_SCH_713", "CORE_SW_068",
                "CORE_SW_072", "CORE_ULD_191", "CORE_ULD_271", "CORE_WON_351", "CORE_YOP_034", "CS3_025", "CS3_038", "CORE_DMF_067",
                "EDR_470", "CORE_DRG_079", "EDR_530", "EDR_572", "EDR_889", "EDR_942", "EDR_971", "CATA_210",
                "FIR_940", "EDR_570", "JAIL_030", "JAIL_204", "JAIL_453", "JAIL_718" }),
        new LocalDeck("test-standard-extra-priest", "TEST: Standard Priest extra", "HERO_09", "Test opponent: Standard batch 2 Rogue",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CORE_EX1_197", "CORE_EX1_198", "CORE_RLK_814", "CORE_SCH_512", "JAIL_883", "JAIL_974", "TIME_040", "TIME_050",
                "TIME_053", "TIME_054", "TIME_060", "TIME_061", "TIME_100", "TIME_428", "EDR_472", "TLC_102",
                "TLC_110", "TLC_243", "TLC_244", "TLC_247", "TLC_248", "TLC_255", "TLC_256", "TLC_427",
                "TLC_429", "TLC_468", "END_025", "END_026", "END_028", "TLC_621" }),
        new LocalDeck("test-standard-extra-druid", "TEST: Standard Druid extra", "HERO_06", "Test opponent: Standard Warrior",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "EDR_848", "TLC_239", "TLC_829", "TLC_831", "TLC_888", "TLC_987", "BE_169", "BE_802",
                "TIME_705", "EDR_847", "CATA_612", "CATA_720", "DINO_410", "DINO_411", "DINO_419", "EDR_110",
                "EDR_486", "EDR_492", "EDR_495", "EDR_571", "EDR_598", "EDR_861", "END_035", "FIR_929",
                "FIR_958", "JAIL_007", "JAIL_456", "TIME_051", "TIME_056", "TIME_059" }),
        new LocalDeck("test-standard-extra-hunter", "TEST: Standard Hunter extra", "HERO_05", "Test opponent: Standard Paladin",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CORE_ULD_152", "JAIL_877", "END_014", "END_017", "CATA_554", "CATA_111", "CATA_612", "CATA_720",
                "DINO_410", "DINO_411", "DINO_419", "EDR_110", "EDR_486", "EDR_492", "EDR_495", "EDR_571",
                "EDR_598", "EDR_861", "END_035", "FIR_929", "FIR_958", "JAIL_007", "JAIL_456", "TIME_051",
                "TIME_056", "TIME_059", "TIME_062", "TIME_434", "TIME_720", "TLC_249" }),
        new LocalDeck("test-standard-extra-demonhunter", "TEST: Standard Demon Hunter extra", "HERO_10", "Test opponent: Standard batch 2 Rogue",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "END_004", "END_007", "END_008", "CATA_721", "CATA_723", "CATA_999", "TLC_480", "TLC_603",
                "TLC_605", "CORE_BT_480", "CORE_BT_491", "JAIL_892", "CATA_111", "CATA_612", "CATA_720", "DINO_410",
                "DINO_411", "DINO_419", "EDR_110", "EDR_486", "EDR_492", "EDR_495", "EDR_571", "EDR_598",
                "EDR_861", "END_035", "CATA_697", "BE_036", "JAIL_206", "JAIL_456" }),
        new LocalDeck("test-standard-extra-rogue", "TEST: Standard Rogue extra", "HERO_03", "Test opponent: Standard Priest",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "TIME_770", "BE_169", "BE_193", "BE_994", "CATA_111", "CATA_612", "CATA_720", "DINO_410",
                "DINO_411", "DINO_419", "EDR_110", "EDR_486", "EDR_492", "EDR_495", "EDR_571", "EDR_598",
                "EDR_861", "END_035", "FIR_929", "FIR_958", "JAIL_007", "JAIL_456", "TIME_051", "TIME_056",
                "TIME_059", "TIME_062", "TIME_434", "TIME_720", "TLC_249", "TLC_468" }),
        new LocalDeck("test-deathknight-rainbow", "TEST: Death Knight BFU", "HERO_11", "Test opponent: Standard batch 2",
            "Death Knight test loaner. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CORE_EDR_002", "CORE_RLK_062", "CORE_RLK_121", "EDR_816", "JAIL_440", "RLK_708", "RLK_958", "TLC_443",
                "BE_284", "BE_325", "CATA_467", "CATA_469", "EDR_812", "JAIL_441", "JAIL_445", "JAIL_454",
                "TLC_439", "CORE_RLK_066", "CORE_RLK_712", "RLK_503", "RLK_024", "RLK_025", "RLK_223", "RLK_511",
                "RLK_709", "RLK_720", "TIME_613", "BE_749", "TLC_432", "BE_780" }),
        DeathKnightLoaner("frost", "FFF", new[] { "TIME_611", "TLC_401", "CORE_CATA_009", "CORE_RLK_505",
            "CORE_RLK_063", "CORE_RLK_083" }),
        DeathKnightLoaner("unholy", "UUU", new[] { "JAIL_450", "RLK_707", "EDR_815", "DINO_417" }),
        DeathKnightLoaner("blood", "BBB", new[] { "DINO_415", "RLK_067", "BE_285", "CORE_RLK_706" }),
        // Seats for every registration that had no player loaner (batch 9, Dark Gift, history facts, cost rules).
        // Pending cards come first (pairs unless legendary), then registered neutral filler; recipes are explicit.
        new LocalDeck("test-deathknight-buu", "TEST: Death Knight BUU", "HERO_11", "Test opponent: Standard batch 2 Rogue",
            "Death Knight test loaner. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CATA_471", "FIR_900", "FIR_901", "TIME_612", "TIME_614", "TIME_615", "JAIL_205", "TIME_047",
                "TIME_048", "CS3_024", "CATA_471", "FIR_900", "FIR_901", "TIME_612", "TIME_614", "TIME_615",
                "JAIL_205", "TIME_047", "TIME_048", "CATA_465", "CORE_RLK_118", "RLK_061", "TIME_610", "FIR_951",
                "CORE_EDR_003", "CATA_470", "EDR_818", "TLC_433", "CATA_612", "CATA_612" }),
        new LocalDeck("test-standard-more-warrior", "TEST: Standard Warrior more", "HERO_01", "Test opponent: Standard batch 2 Druid",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "BE_042", "BE_559", "BE_600", "CAP_105", "CORE_WON_337", "DINO_433", "EDR_456", "FIR_939",
                "FIR_956", "TIME_714", "TIME_872", "END_019", "BE_042", "BE_559", "BE_600", "CAP_105",
                "CORE_WON_337", "DINO_433", "EDR_456", "FIR_939", "FIR_956", "TIME_872", "END_019", "CATA_612",
                "CATA_612", "EDR_492", "EDR_492", "EDR_598", "EDR_598", "TIME_056" }),
        new LocalDeck("test-standard-more-priest", "TEST: Standard Priest more", "HERO_09", "Test opponent: Standard batch 2 Rogue",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "DINO_431", "EDR_460", "EDR_462", "EDR_463", "EDR_895", "FIR_916", "JAIL_448", "JAIL_912",
                "TLC_814", "TLC_819", "CATA_897", "DINO_431", "EDR_460", "EDR_462", "EDR_463", "FIR_916",
                "JAIL_912", "TLC_814", "TLC_819", "CATA_897", "CATA_612", "CATA_612", "EDR_492", "EDR_492",
                "EDR_598", "EDR_598", "TIME_056", "TIME_056", "TIME_059", "TIME_059" }),
        new LocalDeck("test-standard-more-mage", "TEST: Standard Mage more", "HERO_08", "Test opponent: Standard batch 2 Warlock",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "BE_201", "BE_252", "CATA_452", "CATA_483", "DINO_409", "EDR_430", "EDR_940", "EDR_941",
                "FIR_911", "FIR_913", "JAIL_313", "TLC_334", "TLC_364", "TLC_365", "BE_201", "BE_252",
                "CATA_452", "CATA_483", "DINO_409", "EDR_940", "EDR_941", "FIR_911", "FIR_913", "JAIL_313",
                "TLC_334", "TLC_364", "TLC_365", "CATA_612", "CATA_612", "EDR_492" }),
        new LocalDeck("test-standard-more-paladin", "TEST: Standard Paladin more", "HERO_04", "Test opponent: Standard batch 2 Hunter",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CATA_474", "EDR_251", "FIR_914", "MEND_805", "TLC_430", "TLC_442", "TLC_477", "END_013",
                "CAP_407", "JAIL_407", "CORE_REV_308", "CATA_474", "EDR_251", "FIR_914", "MEND_805", "TLC_430",
                "TLC_442", "TLC_477", "END_013", "CAP_407", "CORE_REV_308", "CATA_612", "CATA_612", "EDR_492",
                "EDR_492", "EDR_598", "EDR_598", "TIME_056", "TIME_056", "TIME_059" }),
        new LocalDeck("test-standard-more-druid", "TEST: Standard Druid more", "HERO_06", "Test opponent: Standard batch 2 Warlock",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "BE_208", "CATA_132", "CATA_136", "EDR_060", "EDR_271", "END_009", "JAIL_201", "MEND_041",
                "MEND_042", "MEND_045", "EDR_873", "BE_208", "CATA_132", "CATA_136", "EDR_060", "EDR_271",
                "END_009", "JAIL_201", "MEND_041", "MEND_042", "MEND_045", "EDR_873", "CATA_612", "CATA_612",
                "EDR_492", "EDR_492", "EDR_598", "EDR_598", "TIME_056", "TIME_056" }),
        new LocalDeck("test-standard-more-rogue", "TEST: Standard Rogue more", "HERO_03", "Test opponent: Standard batch 2 Hunter",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CORE_TID_931", "EDR_522", "EDR_528", "EDR_540", "FIR_922", "JAIL_225", "JAIL_470", "JAIL_706",
                "TLC_517", "TLC_520", "CATA_186", "CORE_TID_931", "EDR_522", "EDR_528", "EDR_540", "FIR_922",
                "JAIL_225", "JAIL_470", "JAIL_706", "TLC_517", "TLC_520", "CATA_186", "CATA_612", "CATA_612",
                "EDR_492", "EDR_492", "EDR_598", "EDR_598", "TIME_056", "TIME_056" }),
        new LocalDeck("test-standard-more-warlock", "TEST: Standard Warlock more", "HERO_07", "Test opponent: Standard batch 2 Druid",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "BE_728", "CATA_499", "CORE_WON_096", "EDR_482", "EDR_483", "EDR_487", "EDR_488", "EDR_494",
                "EDR_654", "FIR_924", "TLC_467", "TLC_479", "CATA_499", "CORE_WON_096", "EDR_482", "EDR_483",
                "EDR_488", "EDR_494", "EDR_654", "FIR_924", "TLC_467", "TLC_479", "CATA_612", "CATA_612",
                "EDR_492", "EDR_492", "EDR_598", "EDR_598", "TIME_056", "TIME_056" }),
        new LocalDeck("test-standard-more-hunter", "TEST: Standard Hunter more", "HERO_05", "Test opponent: Standard batch 2 Rogue",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CATA_557", "DINO_434", "EDR_853", "FIR_960", "JAIL_380", "JAIL_876", "JAIL_878", "JAIL_879",
                "MEND_301", "TIME_606", "TLC_822", "EDR_978", "CATA_557", "DINO_434", "FIR_960", "JAIL_380",
                "JAIL_876", "JAIL_878", "JAIL_879", "MEND_301", "TIME_606", "TLC_822", "EDR_978", "CATA_612",
                "CATA_612", "EDR_492", "EDR_492", "EDR_598", "EDR_598", "TIME_056" }),
        new LocalDeck("test-standard-more-shaman", "TEST: Standard Shaman more", "HERO_02", "Test opponent: Standard batch 2 Warlock",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "BE_870", "CATA_568", "CATA_569", "CORE_AV_107", "DINO_412", "JAIL_806", "END_029", "CATA_180",
                "EDR_999", "JAIL_460", "BE_870", "CATA_568", "CATA_569", "CORE_AV_107", "DINO_412", "JAIL_806",
                "END_029", "CATA_180", "EDR_999", "JAIL_460", "CATA_612", "CATA_612", "EDR_492", "EDR_492",
                "EDR_598", "EDR_598", "TIME_056", "TIME_056", "TIME_059", "TIME_059" }),
        new LocalDeck("test-standard-more-demonhunter", "TEST: Standard Demon Hunter more", "HERO_10", "Test opponent: Standard batch 2 Hunter",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CATA_529", "EDR_882", "FIR_902", "EDR_102", "EDR_105", "EDR_856", "END_033", "CATA_556",
                "CATA_529", "EDR_882", "FIR_902", "EDR_102", "EDR_105", "END_033", "CATA_556", "CATA_612",
                "CATA_612", "EDR_492", "EDR_492", "EDR_598", "EDR_598", "TIME_056", "TIME_056", "TIME_059",
                "TIME_059", "TIME_720", "TIME_720", "EDR_571", "EDR_571", "EDR_861" }),
        // Group 5: batch-10 seats, one per class plus a second Death Knight seat for rune-conflicting DK cards.
        // coordinator batch-10 bookkeeping owns these recipe literals: new cards first (one copy each), then a prefix of the
        // filler pairs; each seat replaces the last filler copy.
        new LocalDeck("test-standard-b10-warrior", "TEST: Standard Warrior batch 10", "HERO_01", "Test opponent: Standard batch 2 Druid",
            "Test loaner for Standard batch 10 live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "JAIL_447", "CATA_160", "CATA_584", "CORE_WON_350", "CORE_DRG_024", "CORE_EX1_606", "CORE_EX1_604", "CORE_EX1_391",
                "CORE_CS2_108", "CORE_AT_064", "CATA_612", "CATA_612", "EDR_492", "EDR_492", "EDR_598", "EDR_598",
                "TIME_056", "TIME_056", "TIME_059", "TIME_059", "TIME_720", "TIME_720", "EDR_571", "EDR_571",
                "EDR_861", "EDR_861", "BE_193", "BE_193", "EDR_486", "EDR_486" }),
        new LocalDeck("test-standard-b10-priest", "TEST: Standard Priest batch 10", "HERO_09", "Test opponent: Standard batch 2 Rogue",
            "Test loaner for Standard batch 10 live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "JAIL_462", "TIME_102", "CORE_GVG_085", "CORE_EX1_011", "CORE_LOOT_413", "DINO_426", "CORE_SW_442", "CATA_612",
                "CATA_612", "EDR_492", "EDR_492", "EDR_598", "EDR_598", "TIME_056", "TIME_056", "TIME_059",
                "TIME_059", "TIME_720", "TIME_720", "EDR_571", "EDR_571", "EDR_861", "EDR_861", "BE_193",
                "BE_193", "EDR_486", "EDR_486", "JAIL_007", "JAIL_007", "TIME_051" }),
        new LocalDeck("test-standard-b10-mage", "TEST: Standard Mage batch 10", "HERO_08", "Test opponent: Standard batch 2 Warlock",
            "Test loaner for Standard batch 10 live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "JAIL_998", "EDR_872", "CORE_EDR_001", "CORE_BAR_541", "CORE_EX1_289", "CORE_EX1_287", "CORE_CS2_032", "CORE_CS2_029",
                "CORE_CS2_028", "CORE_CS2_024", "TLC_462", "TLC_226", "CATA_612", "CATA_612", "EDR_492", "EDR_492",
                "EDR_598", "EDR_598", "TIME_056", "TIME_056", "TIME_059", "TIME_059", "TIME_720", "TIME_720",
                "EDR_571", "EDR_571", "EDR_861", "EDR_861", "BE_193", "BE_193" }),
        new LocalDeck("test-standard-b10-paladin", "TEST: Standard Paladin batch 10", "HERO_04", "Test opponent: Standard batch 2 Hunter",
            "Test loaner for Standard batch 10 live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "JAIL_516", "TIME_043", "CORE_ONY_022", "CORE_EX1_007", "CORE_GVG_114", "CORE_UNG_952", "DINO_404", "DINO_424",
                "BE_183", "CATA_612", "CATA_612", "EDR_492", "EDR_492", "EDR_598", "EDR_598", "TIME_056",
                "TIME_056", "TIME_059", "TIME_059", "TIME_720", "TIME_720", "EDR_571", "EDR_571", "EDR_861",
                "EDR_861", "BE_193", "BE_193", "EDR_486", "EDR_486", "JAIL_007" }),
        new LocalDeck("test-standard-b10-druid", "TEST: Standard Druid batch 10", "HERO_06", "Test opponent: Standard batch 2 Warlock",
            "Test loaner for Standard batch 10 live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "TIME_052", "MEND_043", "EDR_273", "FIR_908", "TLC_109", "EDR_209", "CATA_612", "CATA_612",
                "EDR_492", "EDR_492", "EDR_598", "EDR_598", "TIME_056", "TIME_056", "TIME_059", "TIME_059",
                "TIME_720", "TIME_720", "EDR_571", "EDR_571", "EDR_861", "EDR_861", "BE_193", "BE_193",
                "EDR_486", "EDR_486", "JAIL_007", "JAIL_007", "TIME_051", "TIME_051" }),
        new LocalDeck("test-standard-b10-rogue", "TEST: Standard Rogue batch 10", "HERO_03", "Test opponent: Standard batch 2 Hunter",
            "Test loaner for Standard batch 10 live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CORE_ULD_178", "TIME_712", "CORE_EX1_110", "CORE_CS2_222", "TLC_514", "CATA_612", "CATA_612", "EDR_492",
                "EDR_492", "EDR_598", "EDR_598", "TIME_056", "TIME_056", "TIME_059", "TIME_059", "TIME_720",
                "TIME_720", "EDR_571", "EDR_571", "EDR_861", "EDR_861", "BE_193", "BE_193", "EDR_486",
                "EDR_486", "JAIL_007", "JAIL_007", "TIME_051", "TIME_051", "EDR_110" }),
        new LocalDeck("test-standard-b10-warlock", "TEST: Standard Warlock batch 10", "HERO_07", "Test opponent: Standard batch 2 Druid",
            "Test loaner for Standard batch 10 live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "END_016", "END_020", "CORE_LOOT_368", "CORE_CS2_189", "DINO_435", "CATA_612", "CATA_612", "EDR_492",
                "EDR_492", "EDR_598", "EDR_598", "TIME_056", "TIME_056", "TIME_059", "TIME_059", "TIME_720",
                "TIME_720", "EDR_571", "EDR_571", "EDR_861", "EDR_861", "BE_193", "BE_193", "EDR_486",
                "EDR_486", "JAIL_007", "JAIL_007", "TIME_051", "TIME_051", "EDR_110" }),
        new LocalDeck("test-standard-b10-hunter", "TEST: Standard Hunter batch 10", "HERO_05", "Test opponent: Standard batch 2 Rogue",
            "Test loaner for Standard batch 10 live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "JAIL_880", "END_015", "BE_552", "BE_554", "TLC_825", "CATA_612", "CATA_612", "EDR_492",
                "EDR_492", "EDR_598", "EDR_598", "TIME_056", "TIME_056", "TIME_059", "TIME_059", "TIME_720",
                "TIME_720", "EDR_571", "EDR_571", "EDR_861", "EDR_861", "BE_193", "BE_193", "EDR_486",
                "EDR_486", "JAIL_007", "JAIL_007", "TIME_051", "TIME_051", "EDR_110" }),
        new LocalDeck("test-standard-b10-shaman", "TEST: Standard Shaman batch 10", "HERO_02", "Test opponent: Standard batch 2 Warlock",
            "Test loaner for Standard batch 10 live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "JAIL_457", "CORE_OG_031", "CORE_EX1_096", "CORE_CS2_179", "JAIL_805", "CATA_612", "CATA_612", "EDR_492",
                "EDR_492", "EDR_598", "EDR_598", "TIME_056", "TIME_056", "TIME_059", "TIME_059", "TIME_720",
                "TIME_720", "EDR_571", "EDR_571", "EDR_861", "EDR_861", "BE_193", "BE_193", "EDR_486",
                "EDR_486", "JAIL_007", "JAIL_007", "TIME_051", "TIME_051", "EDR_110" }),
        new LocalDeck("test-standard-b10-demonhunter", "TEST: Standard Demon Hunter batch 10", "HERO_10", "Test opponent: Standard batch 2 Hunter",
            "Test loaner for Standard batch 10 live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "TIME_448", "BE_264", "BE_265", "BE_565", "FIR_952", "CORE_YOP_001", "CORE_BT_321", "TLC_900",
                "EDR_820", "EDR_840", "EDR_841", "CORE_BT_921", "BE_230", "BE_566", "CATA_151", "CORE_BT_035",
                "CORE_BT_187", "DINO_136", "DINO_137", "EDR_421", "JAIL_721", "TIME_022", "END_006", "TIME_021",
                "TIME_446", "JAIL_730", "JAIL_733", "CATA_527", "JAIL_EVENT_101", "CATA_612" }),
        new LocalDeck("test-standard-b10-deathknight", "TEST: Standard Death Knight batch 10", "HERO_11", "Test opponent: Standard batch 2 Rogue",
            "Test loaner for Standard batch 10 live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CORE_RLK_745", "BE_283", "BE_287", "CATA_190h", "END_005", "JAIL_442", "TIME_619", "TIME_619t",
                "TIME_619t2", "CATA_612", "CATA_612", "EDR_492", "EDR_492", "EDR_598", "EDR_598", "TIME_056",
                "TIME_056", "TIME_059", "TIME_059", "TIME_720", "TIME_720", "EDR_571", "EDR_571", "EDR_861",
                "EDR_861", "BE_193", "BE_193", "EDR_486", "EDR_486", "JAIL_007" }),
        new LocalDeck("test-standard-b10-deathknight-2", "TEST: Standard Death Knight batch 10 #2", "HERO_11", "Test opponent: Standard batch 2 Rogue",
            "Test loaner for Standard batch 10 live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "TLC_434", "TLC_435", "CATA_612", "CATA_612", "EDR_492", "EDR_492", "EDR_598", "EDR_598",
                "TIME_056", "TIME_056", "TIME_059", "TIME_059", "TIME_720", "TIME_720", "EDR_571", "EDR_571",
                "EDR_861", "EDR_861", "BE_193", "BE_193", "EDR_486", "EDR_486", "JAIL_007", "JAIL_007",
                "TIME_051", "TIME_051", "EDR_110", "EDR_110", "TLC_249", "TLC_249" }),
        // Group 6: mech-A mechanic-families seats, one per class plus a second DK seat.
        // coordinator mech-family bookkeeping owns these recipe literals: new cards first (one copy each), then a prefix of the
        // filler pairs; each seat replaces the last filler copy.
        new LocalDeck("test-standard-mecha-warrior", "TEST: Standard Warrior mech families", "HERO_01", "Test opponent: Standard batch 2 Druid",
            "Test loaner for mechanic-families live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CATA_722", "BE_561", "BE_999", "CAP_104", "CATA_586", "CORE_BT_781", "EDR_455", "TLC_107",
                "TLC_624", "TIME_850", "TIME_850t", "TIME_850t1", "CORE_EX1_414", "CORE_OG_218", "END_022", "BE_740",
                "CAP_101", "CATA_615", "CORE_SCH_717", "DINO_401", "TLC_602", "BE_017", "CORE_WON_145", "JAIL_421",
                "JAIL_852", "TLC_100", "YOG_514", "JAIL_EVENT_100", "CATA_612", "CATA_612" }),
        new LocalDeck("test-standard-mecha-priest", "TEST: Standard Priest mech families", "HERO_09", "Test opponent: Standard batch 2 Rogue",
            "Test loaner for mechanic-families live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "TLC_815", "TLC_816", "EDR_449", "EDR_970", "CAP_806", "CORE_BAR_313", "FIR_921", "TLC_252",
                "TIME_890", "TIME_890t", "TIME_890t2", "EDR_469", "END_027", "TIME_055", "BE_044", "BE_045",
                "CAP_802", "CORE_CFM_670", "EDR_000", "JAIL_398", "TLC_817", "BE_327", "CAP_800", "CORE_EX1_197",
                "JAIL_433", "TIME_432", "TIME_436", "TIME_EVENT_300", "BE_EVENT_102", "CATA_612" }),
        new LocalDeck("test-standard-mecha-mage", "TEST: Standard Mage mech families", "HERO_08", "Test opponent: Standard batch 2 Warlock",
            "Test loaner for mechanic-families live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "MEND_500", "MEND_501", "MEND_502", "MEND_503", "MEND_504", "MEND_505", "MEND_506", "EDR_519",
                "EDR_871", "BE_150", "CATA_979", "EDR_520", "EDR_874", "JAIL_735", "TIME_859", "CATA_487",
                "CORE_BOT_256", "CORE_CS2_023", "EDR_804", "END_024", "BE_250", "BE_259", "CORE_DAL_575", "JAIL_319",
                "JAIL_887", "TIME_852", "TIME_852t1", "TIME_852t3", "TLC_483", "CATA_612" }),
        new LocalDeck("test-standard-mecha-paladin", "TEST: Standard Paladin mech families", "HERO_04", "Test opponent: Standard batch 2 Hunter",
            "Test loaner for mechanic-families live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "BE_146", "BE_997", "CATA_480", "CORE_BT_156", "Core_UNG_072", "EDR_256", "EDR_259", "END_011",
                "JAIL_327", "MEND_802", "TIME_017", "TIME_019", "TIME_700", "TTN_851", "TIME_009", "TIME_009t1",
                "TIME_009t2", "TIME_016", "BE_229", "CATA_621", "EDR_258", "EDR_264", "BE_004", "CATA_472",
                "DINO_405", "JAIL_330", "MEND_801", "TIME_706", "TIME_EVENT_998", "CATA_612" }),
        new LocalDeck("test-standard-mecha-druid", "TEST: Standard Druid mech families", "HERO_06", "Test opponent: Standard batch 2 Warlock",
            "Test loaner for mechanic-families live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "TLC_236", "EDR_800", "CATA_130", "CATA_131", "CORE_AT_037", "CORE_EX1_154", "CORE_EX1_160", "CORE_OG_047",
                "CORE_ONY_018", "CORE_TSC_650", "Core_LOE_115", "CATA_140", "CORE_OG_044", "EDR_270", "JAIL_200", "TIME_704",
                "TIME_707", "BE_851", "EDR_260", "EDR_845", "MEND_044", "TIME_730", "BE_852", "FIR_907",
                "JAIL_860", "JAIL_875", "TIME_211", "TIME_211t1", "TIME_211t2", "CATA_612" }),
        new LocalDeck("test-standard-mecha-rogue", "TEST: Standard Rogue mech families", "HERO_03", "Test opponent: Standard batch 2 Hunter",
            "Test loaner for mechanic-families live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CATA_158", "CATA_785", "TLC_519", "END_000", "END_001", "CATA_202", "CATA_786", "EDR_781",
                "JAIL_474", "TLC_521", "CATA_613", "EDR_844", "JAIL_459", "TIME_036", "TIME_039", "TIME_711",
                "BE_702", "BE_703", "CAP_002", "CATA_215", "CORE_RLK_567", "DINO_427", "FIR_920", "TLC_513",
                "JAIL_500", "TIME_875", "TIME_875t", "TIME_875t1", "CATA_EVENT_402", "CATA_612" }),
        new LocalDeck("test-standard-mecha-warlock", "TEST: Standard Warlock mech families", "HERO_07", "Test opponent: Standard batch 2 Druid",
            "Test loaner for mechanic-families live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "TLC_447", "BE_093", "CATA_493", "CORE_SCH_605", "DINO_402", "EDR_860", "FIR_959", "JAIL_515",
                "TLC_451", "CATA_492", "DINO_430", "EDR_846", "TIME_030", "TIME_103", "BE_087", "CAP_402",
                "CATA_185", "CORE_ETC_111", "CORE_YOD_026", "EDR_489", "TLC_446", "BE_085", "BE_726", "CATA_496",
                "EDR_491", "JAIL_509", "CATA_EVENT_000", "TIME_EVENT_301", "CATA_612", "CATA_612" }),
        new LocalDeck("test-standard-mecha-hunter", "TEST: Standard Hunter mech families", "HERO_05", "Test opponent: Standard batch 2 Rogue",
            "Test loaner for mechanic-families live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "EDR_226", "EDR_227", "EDR_852", "CATA_553", "EDR_001", "EDR_480", "TIME_045", "TIME_600",
                "TIME_810", "BE_588", "EDR_262", "EDR_263", "TIME_049", "TLC_106", "BE_151", "BE_553",
                "CATA_614", "CORE_REV_946", "EDR_453", "MEND_100", "TLC_824", "BE_731", "CATA_550", "JAIL_461",
                "MEND_304", "TIME_620", "CATA_EVENT_110", "TIME_EVENT_997", "CATA_612", "CATA_612" }),
        new LocalDeck("test-standard-mecha-shaman", "TEST: Standard Shaman mech families", "HERO_02", "Test opponent: Standard batch 2 Warlock",
            "Test loaner for mechanic-families live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CATA_561", "EDR_231", "EDR_518", "CATA_563", "CATA_564", "END_034", "TIME_217", "TLC_101",
                "TLC_223", "TLC_228", "BE_302", "CORE_LOOT_373", "EDR_233", "TIME_013", "TLC_482", "BE_114",
                "CATA_567", "CORE_CATA_004", "EDR_234", "JAIL_987", "BE_009", "BE_209", "CS3_022", "EDR_529",
                "JAIL_452", "JAIL_800", "TLC_464", "JAIL_EVENT_102", "CATA_612", "CATA_612" }),
        new LocalDeck("test-standard-mecha-demonhunter", "TEST: Standard Demon Hunter mech families", "HERO_10", "Test opponent: Standard batch 2 Hunter",
            "Test loaner for mechanic-families live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "BE_734", "CATA_533", "CATA_699", "CORE_WON_141", "EDR_254", "TLC_242", "TLC_245", "TLC_246",
                "TLC_254", "TLC_454", "BE_014", "BE_771", "BE_204", "BE_996", "CATA_206", "CATA_213",
                "CORE_TTN_866", "CS3_020", "EDR_493", "JAIL_719", "JAIL_734", "TIME_442", "TIME_443", "TLC_841",
                "TIME_444", "BE_262", "JAIL_732", "JAIL_891", "TLC_633", "CATA_612" }),
        new LocalDeck("test-standard-mecha-deathknight", "TEST: Standard Death Knight mech families", "HERO_11", "Test opponent: Standard batch 2 Rogue",
            "Test loaner for mechanic-families live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CATA_156", "CATA_780", "END_003", "CORE_RLK_087", "DINO_416", "EDR_810", "EDR_811", "EDR_814",
                "EDR_817", "JAIL_451", "TIME_616", "CORE_RLK_506", "RLK_060", "TLC_810", "CORE_CATA_007", "CORE_ETC_523",
                "CORE_RLK_116", "JAIL_444", "RLK_048", "BE_781", "BE_298", "BE_571", "CATA_155", "CORE_RLK_086",
                "CORE_WW_374", "EDR_813", "EDR_819", "JAIL_443", "JAIL_446", "TLC_436" }),
        new LocalDeck("test-standard-mecha-deathknight-2", "TEST: Standard Death Knight mech families #2", "HERO_11", "Test opponent: Standard batch 2 Rogue",
            "Test loaner for mechanic-families live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "TLC_440", "CATA_464", "TIME_617", "CATA_161", "CATA_497", "TIME_618", "TLC_EVENT_402", "BE_EVENT_100",
                "CATA_612", "EDR_492", "EDR_492", "EDR_598", "EDR_598", "TIME_056", "TIME_056", "TIME_059",
                "TIME_059", "TIME_720", "TIME_720", "EDR_571", "EDR_571", "EDR_861", "EDR_861", "BE_193",
                "BE_193", "EDR_486", "EDR_486", "JAIL_007", "JAIL_007", "TIME_051" }),
        // Group 7: Rewind seats: explicit immutable recipes; frozen batch-2 opponents.
        new LocalDeck("test-standard-rewind-rogue", "TEST: Standard Rogue Rewind", "HERO_03", "Test opponent: Standard batch 2 Hunter",
            "Test loaner for Rewind live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "TIME_001", "TIME_001", "TIME_002", "TIME_002", "CORE_TID_931", "EDR_522", "EDR_528", "EDR_540",
                "FIR_922", "JAIL_225", "JAIL_470", "JAIL_706", "TLC_517", "TLC_520", "CATA_186", "CORE_TID_931",
                "EDR_522", "EDR_528", "EDR_540", "FIR_922", "JAIL_225", "JAIL_470", "JAIL_706", "TLC_517",
                "TLC_520", "CATA_186", "CATA_612", "CATA_612", "EDR_492", "EDR_492" }),
        new LocalDeck("test-standard-rewind-mage", "TEST: Standard Mage Rewind", "HERO_08", "Test opponent: Standard batch 2 Warlock",
            "Test loaner for Rewind live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "TIME_000", "TIME_000", "TIME_003", "TIME_003", "BE_201", "BE_252", "CATA_452", "CATA_483",
                "DINO_409", "EDR_430", "EDR_940", "EDR_941", "FIR_911", "FIR_913", "JAIL_313", "TLC_334",
                "TLC_364", "TLC_365", "BE_201", "BE_252", "CATA_452", "CATA_483", "DINO_409", "EDR_940",
                "EDR_941", "FIR_911", "FIR_913", "JAIL_313", "TLC_334", "TLC_364" }),
        new LocalDeck("test-standard-rewind-warlock", "TEST: Standard Warlock Rewind", "HERO_07", "Test opponent: Standard batch 2 Druid",
            "Test loaner for Rewind live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "TIME_008", "TIME_008", "TIME_004", "TIME_004", "BE_728", "CATA_499", "CORE_WON_096", "EDR_482",
                "EDR_483", "EDR_487", "EDR_488", "EDR_494", "EDR_654", "FIR_924", "TLC_467", "TLC_479",
                "CATA_499", "CORE_WON_096", "EDR_482", "EDR_483", "EDR_488", "EDR_494", "EDR_654", "FIR_924",
                "TLC_467", "TLC_479", "CATA_612", "CATA_612", "EDR_492", "EDR_492" }),
        new LocalDeck("test-standard-rewind-shaman", "TEST: Standard Shaman Rewind", "HERO_02", "Test opponent: Standard batch 2 Warlock",
            "Test loaner for Rewind live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "TIME_014", "TIME_014", "TIME_035", "TIME_035", "BE_870", "CATA_568", "CATA_569", "CORE_AV_107",
                "DINO_412", "JAIL_806", "END_029", "CATA_180", "EDR_999", "JAIL_460", "BE_870", "CATA_568",
                "CATA_569", "CORE_AV_107", "DINO_412", "JAIL_806", "END_029", "CATA_180", "EDR_999", "JAIL_460",
                "CATA_612", "CATA_612", "EDR_492", "EDR_492", "EDR_598", "EDR_598" }),
        new LocalDeck("test-standard-rewind-paladin", "TEST: Standard Paladin Rewind", "HERO_04", "Test opponent: Standard batch 2 Hunter",
            "Test loaner for Rewind live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "TIME_018", "TIME_018", "TIME_038", "CATA_474", "EDR_251", "FIR_914", "MEND_805", "TLC_430",
                "TLC_442", "TLC_477", "END_013", "CAP_407", "JAIL_407", "CORE_REV_308", "CATA_474", "EDR_251",
                "FIR_914", "MEND_805", "TLC_430", "TLC_442", "TLC_477", "END_013", "CAP_407", "CORE_REV_308",
                "CATA_612", "CATA_612", "EDR_492", "EDR_492", "EDR_598", "EDR_598" }),
        new LocalDeck("test-standard-rewind-druid", "TEST: Standard Druid Rewind", "HERO_06", "Test opponent: Standard batch 2 Warlock",
            "Test loaner for Rewind live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "TIME_033", "TIME_033", "END_036", "BE_208", "CATA_132", "CATA_136", "EDR_060", "EDR_271",
                "END_009", "JAIL_201", "MEND_041", "MEND_042", "MEND_045", "EDR_873", "BE_208", "CATA_132",
                "CATA_136", "EDR_060", "EDR_271", "END_009", "JAIL_201", "MEND_041", "MEND_042", "MEND_045",
                "EDR_873", "CATA_612", "CATA_612", "EDR_492", "EDR_492", "EDR_598" }),
        new LocalDeck("test-standard-rewind-warrior", "TEST: Standard Warrior Rewind", "HERO_01", "Test opponent: Standard batch 2 Druid",
            "Test loaner for Rewind live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "TIME_034", "TIME_034", "CORE_EDR_004", "CORE_EDR_004", "BE_042", "BE_559", "BE_600", "CAP_105",
                "CORE_WON_337", "DINO_433", "EDR_456", "FIR_939", "FIR_956", "TIME_714", "TIME_872", "END_019",
                "BE_042", "BE_559", "BE_600", "CAP_105", "CORE_WON_337", "DINO_433", "EDR_456", "FIR_939",
                "FIR_956", "TIME_872", "END_019", "CATA_612", "CATA_612", "EDR_492" }),
        new LocalDeck("test-standard-rewind-priest", "TEST: Standard Priest Rewind", "HERO_09", "Test opponent: Standard batch 2 Rogue",
            "Test loaner for Rewind live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "TIME_433", "TIME_433", "CORE_EDR_004_2026", "CORE_EDR_004_2026", "DINO_431", "EDR_460", "EDR_462", "EDR_463",
                "EDR_895", "FIR_916", "JAIL_448", "JAIL_912", "TLC_814", "TLC_819", "CATA_897", "DINO_431",
                "EDR_460", "EDR_462", "EDR_463", "FIR_916", "JAIL_912", "TLC_814", "TLC_819", "CATA_897",
                "CATA_612", "CATA_612", "EDR_492", "EDR_492", "EDR_598", "EDR_598" }),
        new LocalDeck("test-standard-rewind-demonhunter", "TEST: Standard Demon Hunter Rewind", "HERO_10", "Test opponent: Standard batch 2 Hunter",
            "Test loaner for Rewind live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "TIME_441", "TIME_441", "CATA_529", "EDR_882", "FIR_902", "EDR_102", "EDR_105", "EDR_856",
                "END_033", "CATA_556", "CATA_529", "EDR_882", "FIR_902", "EDR_102", "EDR_105", "END_033",
                "CATA_556", "TIME_020", "TIME_020t1", "TIME_020t2", "CATA_612", "CATA_612", "EDR_492", "EDR_492",
                "EDR_598", "EDR_598", "TIME_056", "TIME_056", "TIME_059", "TIME_059" }),
        new LocalDeck("test-standard-rewind-hunter", "TEST: Standard Hunter Rewind", "HERO_05", "Test opponent: Standard batch 2 Rogue",
            "Test loaner for Rewind live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "TIME_602", "TIME_602", "CATA_557", "DINO_434", "EDR_853", "FIR_960", "JAIL_380", "JAIL_876",
                "JAIL_878", "JAIL_879", "MEND_301", "TIME_606", "TLC_822", "EDR_978", "CATA_557", "DINO_434",
                "FIR_960", "JAIL_380", "JAIL_876", "JAIL_878", "JAIL_879", "MEND_301", "TIME_606", "TLC_822",
                "EDR_978", "CATA_612", "CATA_612", "EDR_492", "EDR_492", "EDR_598" }),
        // Group 8: Colossal and Fabled keyword fixtures.
        new LocalDeck("test-keywords-cfa-paladin", "TEST: Keywords CFA Paladin", "HERO_04", "Test opponent: Standard batch 2 Hunter",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CATA_432", "BE_153", "BE_154", "CATA_473", "CATA_475", "CATA_478", "EDR_252", "EDR_253",
                "FIR_941", "FIR_961", "JAIL_035", "JAIL_118", "JAIL_326", "JAIL_329", "JAIL_377", "MEND_800",
                "MEND_803", "MEND_804", "TIME_015", "TLC_441", "CORE_BAR_878", "CORE_BT_292", "CORE_SW_047", "CORE_TSC_076",
                "JAIL_328", "TIME_044", "TLC_426", "EDR_257", "CATA_476", "END_010" }),
        new LocalDeck("test-keywords-cfa-hunter", "TEST: Keywords CFA Hunter", "HERO_05", "Test opponent: Standard batch 2 Hunter",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "TIME_609", "TIME_609t1", "TIME_609t2", "CORE_ULD_152", "JAIL_877", "END_014", "END_017", "CATA_554",
                "CATA_111", "CATA_612", "CATA_720", "DINO_410", "DINO_411", "DINO_419", "EDR_110", "EDR_486",
                "EDR_492", "EDR_495", "EDR_571", "EDR_598", "EDR_861", "END_035", "FIR_929", "FIR_958",
                "JAIL_007", "JAIL_456", "TIME_051", "TIME_056", "TIME_059", "TIME_062" }),
        new LocalDeck("test-keywords-cfa-warrior", "TEST: Keywords CFA Warrior", "HERO_01", "Test opponent: Standard batch 2 Hunter",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CATA_150", "BE_042", "BE_559", "BE_600", "CAP_105", "CORE_WON_337", "DINO_433", "EDR_456",
                "FIR_939", "FIR_956", "TIME_714", "TIME_872", "END_019", "BE_042", "BE_559", "BE_600",
                "CAP_105", "CORE_WON_337", "DINO_433", "EDR_456", "FIR_939", "FIR_956", "TIME_872", "END_019",
                "CATA_612", "CATA_612", "EDR_492", "EDR_492", "EDR_598", "EDR_598" }),
        new LocalDeck("test-keywords-cfa-shaman", "TEST: Keywords CFA Shaman", "HERO_02", "Test opponent: Standard batch 2 Hunter",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CATA_153", "BE_870", "CATA_568", "CATA_569", "CORE_AV_107", "DINO_412", "JAIL_806", "END_029",
                "CATA_180", "EDR_999", "JAIL_460", "BE_870", "CATA_568", "CATA_569", "CORE_AV_107", "DINO_412",
                "JAIL_806", "END_029", "CATA_180", "EDR_999", "JAIL_460", "CATA_612", "CATA_612", "EDR_492",
                "EDR_492", "EDR_598", "EDR_598", "TIME_056", "TIME_056", "TIME_059" }),
        new LocalDeck("test-keywords-cfa-priest", "TEST: Keywords CFA Priest", "HERO_09", "Test opponent: Standard batch 2 Hunter",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CATA_300", "DINO_431", "EDR_460", "EDR_462", "EDR_463", "EDR_895", "FIR_916", "JAIL_448",
                "JAIL_912", "TLC_814", "TLC_819", "CATA_897", "DINO_431", "EDR_460", "EDR_462", "EDR_463",
                "FIR_916", "JAIL_912", "TLC_814", "TLC_819", "CATA_897", "CATA_612", "CATA_612", "EDR_492",
                "EDR_492", "EDR_598", "EDR_598", "TIME_056", "TIME_056", "TIME_059" }),
        new LocalDeck("test-keywords-cfa-mage", "TEST: Keywords CFA Mage", "HERO_08", "Test opponent: Standard batch 2 Hunter",
            "Test loaner for all registered Standard live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CATA_488", "BE_201", "BE_252", "CATA_452", "CATA_483", "DINO_409", "EDR_430", "EDR_940",
                "EDR_941", "FIR_911", "FIR_913", "JAIL_313", "TLC_334", "TLC_364", "TLC_365", "BE_201",
                "BE_252", "CATA_452", "CATA_483", "DINO_409", "EDR_940", "EDR_941", "FIR_911", "FIR_913",
                "JAIL_313", "TLC_334", "TLC_364", "TLC_365", "CATA_612", "CATA_612" }),
        // Group 9: card-state loaners.
        new LocalDeck("test-standard-cardstate-warrior", "TEST: Card state Warrior", "HERO_01", "Test opponent: Standard batch 2 Druid",
            "Card state test loaner. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] { "TIME_046", "TIME_046", "TIME_058", "TIME_058", "TIME_063", "CATA_612", "TIME_101", "TIME_101", "JAIL_850", "CATA_612", "EDR_979", "EDR_979", "TIME_062", "TIME_062", "TLC_253", "TLC_253", "TIME_434", "TIME_434", "TLC_249", "TLC_249", "TLC_468", "TLC_468", "TLC_621", "TLC_621", "TLC_831", "TLC_831", "EDR_495", "EDR_495", "BE_994", "BE_994" }),
        new LocalDeck("test-standard-cardstate-warlock", "TEST: Card state Warlock", "HERO_07", "Test opponent: Standard batch 2 Rogue",
            "Card state test loaner. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] { "TLC_450", "TLC_450", "TLC_449", "TLC_449", "TLC_469", "TLC_469", "JAIL_997", "JAIL_997", "TIME_056", "TIME_056", "TIME_059", "TIME_059", "TIME_062", "TIME_062", "TIME_720", "TIME_720", "TIME_434", "TIME_434", "TLC_249", "TLC_249", "TLC_468", "TLC_468", "TLC_621", "TLC_621", "TLC_831", "TLC_831", "EDR_495", "EDR_495", "BE_994", "BE_994" }),
        new LocalDeck("test-standard-cardstate-druid", "TEST: Card state druid", "HERO_06", "Test opponent: Standard batch 2 Warlock",
            "Card state test loaner. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] { "EDR_843", "EDR_843", "CATA_134", "CATA_134", "EDR_492", "EDR_492", "EDR_598", "EDR_598", "TIME_056", "TIME_056", "TIME_059", "TIME_059", "TIME_062", "TIME_062", "TIME_434", "TIME_434", "TIME_720", "TIME_720", "TLC_249", "TLC_249", "TLC_468", "TLC_468", "TLC_621", "TLC_621", "TLC_831", "TLC_831", "EDR_495", "EDR_495", "BE_994", "BE_994" }),
        new LocalDeck("test-standard-cardstate-priest", "TEST: Card state priest", "HERO_09", "Test opponent: Standard batch 2 Warlock",
            "Card state test loaner. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] { "EDR_461", "EDR_461", "FIR_918", "FIR_918", "EDR_460", "EDR_460", "CATA_306", "CATA_306", "EDR_492", "EDR_492", "EDR_598", "EDR_598", "TIME_056", "TIME_056", "TIME_059", "TIME_059", "TIME_062", "TIME_062", "TIME_434", "TIME_434", "TIME_720", "TIME_720", "TLC_249", "TLC_249", "TLC_468", "TLC_468", "TLC_621", "TLC_621", "TLC_831", "TLC_831" }),
        CardStateLoaner("mage", "HERO_08", "CATA_489"),
        new LocalDeck("test-standard-cardstate-hunter", "TEST: Card state hunter", "HERO_05", "Test opponent: Standard batch 2 Warlock",
            "Card-state primitive test loaner. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] { "CATA_820", "CATA_820", "EDR_416", "EDR_416", "EDR_492", "EDR_492", "EDR_598", "EDR_598", "TIME_056", "TIME_056", "TIME_059", "TIME_059", "TIME_062", "TIME_062", "TIME_434", "TIME_434", "TIME_720", "TIME_720", "TLC_249", "TLC_249", "TLC_468", "TLC_468", "TLC_621", "TLC_621", "TLC_831", "TLC_831", "EDR_495", "EDR_495", "BE_994", "BE_994" }),
        CardStateLoaner("paladin", "HERO_04", "CATA_479"),
        CardStateLoaner("shaman", "HERO_02", "JAIL_801"),
        // Group 10: batch-11 seats for the nine legacy classes.
        // coordinator batch-11 bookkeeping owns these literals: new cards first, then a prefix of the filler pairs;
        // each seat replaces the last filler copy.
        new LocalDeck("test-standard-b11-warrior", "TEST: Standard Warrior batch 11", "HERO_01", "Test opponent: Standard batch 2 Druid",
            "Test loaner for Standard batch 11 live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CORE_CFM_344", "CORE_CS2_122", "CORE_EX1_012", "CORE_EX1_059", "CORE_EX1_506", "CORE_GIL_534", "CORE_GIL_623", "CORE_LOOT_044",
                "CORE_NEW1_018", "CORE_NEW1_022", "CORE_OG_149", "BE_557", "BE_558", "CATA_581", "EDR_465", "CORE_BT_120",
                "JAIL_435", "CATA_591", "EDR_454", "FIR_928", "JAIL_384", "TLC_632", "BE_055", "CS3_035",
                "JAIL_455", "TIME_024", "TLC_250", "CATA_EVENT_002", "TIME_EVENT_999", "CATA_612" }),
        new LocalDeck("test-standard-b11-priest", "TEST: Standard Priest batch 11", "HERO_09", "Test opponent: Standard batch 2 Rogue",
            "Test loaner for Standard batch 11 live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CORE_AT_055", "CORE_CFM_604", "CORE_CS1_112", "CORE_CS1_130", "CORE_CS2_004", "CORE_GIL_558", "CORE_GVG_103", "CORE_ICC_210",
                "CORE_ICC_214", "CORE_NEW1_027", "CORE_UNG_205", "BE_227", "JAIL_940", "BE_012", "BE_196", "BE_221",
                "BE_222", "BE_223", "CATA_216", "CATA_301", "CORE_CATA_002", "EDR_464", "TLC_821", "CATA_307",
                "JAIL_432", "JAIL_434", "TIME_435", "CATA_EVENT_401", "TLC_EVENT_400", "CATA_612" }),
        new LocalDeck("test-standard-b11-mage", "TEST: Standard Mage batch 11", "HERO_08", "Test opponent: Standard batch 2 Warlock",
            "Test loaner for Standard batch 11 live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CORE_CFM_790", "CORE_CS2_188", "CORE_EX1_014", "CORE_EX1_082", "CORE_EX1_507", "CORE_EX1_559", "CORE_KAR_061", "CORE_LOOT_101",
                "CORE_NEW1_020", "CORE_ULD_723", "CORE_UNG_809", "CATA_978", "EDR_517", "JAIL_122", "TLC_461", "BE_256",
                "JAIL_321", "TIME_860", "CATA_458", "JAIL_123", "TIME_857", "BE_257", "JAIL_315", "DINO_414",
                "JAIL_502", "TIME_064", "TIME_861", "TLC_452", "TLC_460", "BE_EVENT_101" }),
        new LocalDeck("test-standard-b11-paladin", "TEST: Standard Paladin batch 11", "HERO_04", "Test opponent: Standard batch 2 Hunter",
            "Test loaner for Standard batch 11 live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CORE_CFM_753", "CORE_CS2_093", "CORE_CS2_094", "CORE_EX1_100", "CORE_EX1_362", "CORE_EX1_383", "CORE_EX1_619", "CORE_GVG_059",
                "CORE_GVG_061", "CORE_ICC_038", "CORE_KAR_057", "CORE_KAR_077", "CORE_TRL_307", "EDR_255", "TLC_428", "TLC_438",
                "BE_000", "BE_142", "JAIL_101", "TIME_057", "TLC_240", "TLC_444", "EDR_451", "BE_143",
                "CATA_477", "END_012", "JAIL_397", "MEND_900", "TLC_241", "CATA_612" }),
        new LocalDeck("test-standard-b11-druid", "TEST: Standard Druid batch 11", "HERO_06", "Test opponent: Standard batch 2 Warlock",
            "Test loaner for Standard batch 11 live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CORE_CS2_009", "CORE_EX1_010", "CORE_EX1_028", "CORE_EX1_103", "CORE_EX1_169", "CORE_GIL_622", "CORE_LOE_039", "CORE_LOOT_309",
                "CORE_NEW1_021", "CORE_TRL_240", "CORE_UNG_928", "JAIL_507", "TLC_235", "TLC_465", "BE_801", "CATA_209",
                "CATA_616", "CORE_CATA_006", "JAIL_851", "MEND_046", "TIME_701", "TLC_230", "BE_784", "CATA_139",
                "JAIL_703", "JAIL_861", "MEND_040", "CATA_EVENT_400", "CATA_612", "CATA_612" }),
        new LocalDeck("test-standard-b11-rogue", "TEST: Standard Rogue batch 11", "HERO_03", "Test opponent: Standard batch 2 Hunter",
            "Test loaner for Standard batch 11 live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CORE_BOT_576", "CORE_CFM_781", "CORE_CS2_072", "CORE_CS2_074", "CORE_CS2_076", "CORE_DAL_720", "CORE_EX1_129", "CORE_EX1_131",
                "CORE_EX1_134", "CORE_EX1_145", "CORE_EX1_278", "CORE_KAR_069", "EDR_524", "TLC_516", "JAIL_501", "BE_720",
                "BE_722", "BE_724", "CATA_154", "CATA_481", "EDR_521", "DINO_407", "EDR_526", "TIME_876",
                "CAP_004", "JAIL_504", "TLC_251", "TLC_515", "TLC_522", "CATA_612" }),
        new LocalDeck("test-standard-b11-warlock", "TEST: Standard Warlock batch 11", "HERO_07", "Test opponent: Standard batch 2 Druid",
            "Test loaner for Standard batch 11 live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CORE_BOT_222", "CORE_CS2_062", "CORE_EX1_058", "CORE_EX1_162", "CORE_EX1_302", "CORE_EX1_309", "CORE_EX1_310", "CORE_EX1_319",
                "CORE_EX1_323", "CORE_ICC_055", "CORE_ICC_407", "CORE_LOOT_013", "BE_086", "BE_094", "CAP_401", "JAIL_511",
                "CATA_491", "CAP_403", "JAIL_303", "EDR_484", "TIME_032", "BE_005", "BE_090", "CAP_405",
                "CATA_726", "END_018", "TIME_005", "TIME_005t1", "TIME_005t2", "TIME_005t3", "TIME_005t4", "TIME_005t5",
                "TIME_005t6", "TIME_005t7", "TIME_005t8", "TIME_005t9", "TLC_466", "CATA_EVENT_001", "CATA_612", "CATA_612" }),
        new LocalDeck("test-standard-b11-hunter", "TEST: Standard Hunter batch 11", "HERO_05", "Test opponent: Standard batch 2 Rogue",
            "Test loaner for Standard batch 11 live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CORE_AT_062", "CORE_BRM_013", "CORE_DS1_184", "CORE_DS1_185", "CORE_EX1_509", "CORE_EX1_610", "CORE_EX1_611", "CORE_FP1_011",
                "CORE_GIL_577", "CORE_NEW1_031", "CORE_OG_211", "CORE_TRL_111", "CORE_TRL_900", "CORE_UNG_912", "BE_900", "DINO_422",
                "BE_555", "CATA_566", "JAIL_986", "MEND_300", "MEND_303", "MEND_307", "TLC_836", "EDR_780",
                "JAIL_831", "TIME_042", "TLC_830", "EDR_950", "CATA_612", "CATA_612" }),
        new LocalDeck("test-standard-b11-shaman", "TEST: Standard Shaman batch 11", "HERO_02", "Test opponent: Standard batch 2 Warlock",
            "Test loaner for Standard batch 11 live qualification. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] {
                "CORE_BOT_451", "CORE_CS2_042", "CORE_CS2_053", "CORE_EX1_189", "CORE_EX1_238", "CORE_EX1_246", "CORE_EX1_259", "CORE_GIL_531",
                "CORE_GIL_836", "CORE_UNG_084", "Core_CS2_200", "CATA_570", "CORE_TRL_345", "TLC_227", "BE_778", "BE_337",
                "EDR_849", "END_037", "TIME_041", "EDR_888", "TLC_229", "BE_013", "CATA_898", "EDR_031",
                "FIR_927", "JAIL_458", "TIME_209", "TIME_209t", "TIME_209t2", "CATA_612" }),
        // Curated showcase, not a coverage seat: Blood/Unholy Corpses, Dark Gift, Health-cost spells and
        // history payoffs (hero Health changed this turn, turns taken) on a real curve. Runes 1,0,2.
        new LocalDeck(TestShowcaseId, "SHOWCASE: Death Knight", "HERO_11", "Test opponent: Standard batch 2 Hunter",
            "Showcase loaner: spend Corpses, pay Health or heal with Lifesteal to arm Liferender, "
            + "and hold a Dark Gift for Frostburn Matriarch. Not a starter deck.", DeckStyle.Value,
            new[] { "RLK_503", "CORE_RLK_066", "JAIL_441", "EDR_105", "TIME_612", "FIR_900", "TIME_614",
                "FIR_901", "TIME_048" },
            new[] { "EDR_812", "EDR_815", "EDR_856", "EDR_102", "RLK_024", "FIR_958", "CATA_471", "CS3_024",
                "JAIL_454", "RLK_720", "JAIL_407", "TIME_615" }),
        new LocalDeck("showcase-rogue-imbue", "Imbue Rogue", "HERO_03", "Showcase opponents",
            "Imbue early; replay Battlecries; finish with discounted threats and Stealth.", DeckStyle.Value,
            new[] { "EDR_528", "CORE_BT_701", "EDR_852", "END_000", "CORE_DMF_511", "JAIL_225", "EDR_523", "TIME_001", "END_001", "JAIL_470", "TIME_770", "EDR_800", "TIME_710" },
            new[] { "FIR_919", "END_036", "EDR_856", "TIME_712" }),
        new LocalDeck("showcase-death-knight-dark-gift", "Dark Gift DK", "HERO_11", "Showcase opponents",
            "Spend Corpses; heal with Lifesteal; grow Dark Gift threats behind sticky boards.", DeckStyle.Value,
            new[] { "RLK_503", "EDR_105", "JAIL_441", "CORE_RLK_066", "TIME_612", "RLK_708", "FIR_900", "TIME_614", "RLK_024", "CORE_RLK_062", "FIR_901" },
            new[] { "EDR_812", "EDR_815", "EDR_856", "FIR_958", "JAIL_454", "CATA_471", "RLK_720", "TIME_615" }),
        new LocalDeck("showcase-demon-hunter-outcast", "Outcast DH", "HERO_10", "Showcase opponents",
            "Use Outcast draw and Rush trades; finish with hero attacks and weapons.", DeckStyle.Tempo,
            new[] { "CORE_BT_351", "CORE_BT_480", "CORE_YOP_001", "JAIL_892", "BE_877", "TLC_902", "END_007", "CORE_BT_491", "BE_264", "CORE_BT_801", "CORE_WC_701", "TLC_833", "EDR_842", "FIR_952" },
            new[] { "CATA_528", "END_004" }),
        new LocalDeck("showcase-druid-quest-tokens", "Token Druid", "HERO_06", "Showcase opponents",
            "Fill the board across turns; complete the Quest; buff tokens for lethal.", DeckStyle.Tempo,
            new[] { "CATA_210", "CORE_SW_439", "DINO_130", "CATA_135", "TLC_232", "EDR_843", "TLC_233", "FIR_906", "TLC_237", "EDR_492", "DINO_432", "END_009", "CATA_134" },
            new[] { "TLC_239", "CATA_138", "CATA_133", "EDR_209" }),
        new LocalDeck("showcase-hunter-kindred-beasts", "Kindred Hunter", "HERO_05", "Showcase opponents",
            "Chain Beasts and handbuffs; refill with Network; finish with Devilsaur Mask.", DeckStyle.Value,
            new[] { "BE_193", "TIME_601", "TLC_823", "JAIL_878", "MEND_305", "JAIL_877", "CORE_BT_201", "END_013", "TLC_827", "EDR_492", "TLC_825", "TLC_828", "TIME_605", "TLC_366" },
            new[] { "TLC_110", "DINO_403" }),
        new LocalDeck("showcase-mage-leylines", "Leyline Mage", "HERO_08", "Showcase opponents",
            "Upgrade Leylines; answer boards with spells; turn Rewind and Discover into value.", DeckStyle.Control,
            new[] { "CORE_SW_108", "CATA_485", "CATA_484", "MEND_504", "MEND_506", "CORE_BAR_541", "BE_251", "MEND_501", "TLC_365", "CATA_489", "MEND_500", "MEND_503", "CORE_CS2_028" },
            new[] { "TIME_003", "EDR_856", "MEND_502", "MEND_505" }),
        new LocalDeck("showcase-paladin-pure-holy", "Holy Paladin", "HERO_04", "Showcase opponents",
            "Build Divine Shield boards; use Holy spells and Colossal threats to finish.", DeckStyle.Value,
            new[] { "CORE_ONY_022", "CORE_BT_292", "TIME_015", "JAIL_377", "TIME_044", "JAIL_328", "BE_152", "JAIL_035", "CATA_479", "CATA_474", "EDR_253", "CATA_478", "CATA_475" },
            new[] { "CATA_473", "CORE_SW_047", "CATA_432", "JAIL_118" }),
        new LocalDeck("showcase-priest-imbue", "Imbue Priest", "HERO_09", "Showcase opponents",
            "Imbue for resources; spend Temporary cards immediately; stabilize with Lifesteal.", DeckStyle.Control,
            new[] { "CATA_302", "EDR_852", "EDR_449", "CATA_303", "FIR_777", "CORE_BAR_311", "END_025", "EDR_970", "TIME_427", "EDR_800", "TLC_816", "CATA_308" },
            new[] { "TIME_433", "TLC_815", "EDR_856", "EDR_476", "CATA_300", "EDR_895" }),
        new LocalDeck("showcase-shaman-herald", "Herald Shaman", "HERO_02", "Showcase opponents",
            "Herald into Colossal pressure; chain Elementals and Rewind for value.", DeckStyle.Value,
            new[] { "CORE_WC_042", "TLC_225", "JAIL_802", "CATA_561", "CATA_565", "BE_842", "BE_870", "DINO_406", "CATA_569", "DINO_413", "BE_349", "CATA_722", "TLC_221" },
            new[] { "END_030", "TIME_014", "CATA_153", "CATA_568" }),
        new LocalDeck("showcase-warlock-dragon-shred", "Shred Warlock", "HERO_07", "Showcase opponents",
            "Build cheap boards; turn Shreds and Dragon Battlecries into pressure and reach.", DeckStyle.Tempo,
            new[] { "CATA_556", "TIME_026", "END_020", "EDR_485", "CORE_WON_096", "CORE_NEW1_023", "EDR_889", "TIME_027", "TIME_025", "CATA_111", "FIR_955", "TIME_028", "TIME_031", "TIME_029", "END_033" },
            new string[0]),
        new LocalDeck("showcase-warrior-armor-herald", "Armor Warrior", "HERO_01", "Showcase opponents",
            "Bank Armor; Herald into Colossal threats; finish behind resilient Taunts.", DeckStyle.Control,
            new[] { "CORE_AT_064", "CORE_EX1_606", "BE_559", "BE_572", "BE_600", "DINO_400", "CATA_580", "TLC_620", "CATA_160", "TLC_601", "CATA_722", "BE_560", "CORE_SW_068" },
            new[] { "FIR_939", "EDR_979", "TIME_714", "CATA_150" }),
        new LocalDeck("showcase-rogue-prepare-combo", "Prepare Rogue", "HERO_03", "Showcase opponents",
            "Prepare and Coins enable Combo turns; build hand stats and finish with big threats.", DeckStyle.Combo,
            new[] { "BE_071", "CATA_200", "CORE_BT_701", "CORE_DMF_511", "JAIL_720", "CATA_785", "JAIL_503", "JAIL_909", "TIME_770", "TIME_710", "BE_723", "END_032", "BE_721" },
            new[] { "CATA_203", "BE_725", "TIME_713", "JAIL_407" }),
        new LocalDeck("test-standard-azalina-priest", "TEST: Standard Azalina Priest", "HERO_09", "Test opponent: Standard batch 2 Rogue",
            "Test loaner for twenty-card Azalina deck qualification. Not a starter deck.", DeckStyle.Value,
            new[] { "CORE_EX1_197", "EDR_449", "EDR_476", "EDR_970", "TIME_432", "JAIL_912", "JAIL_432", "JAIL_433" },
            new[] { "TIME_890", "TIME_890t", "TIME_890t2", "JAIL_430" })
    }.Select(deck => deck.CardIds.Any(WaitingForCatalog)
        ? new LocalDeck(deck.Id, deck.Name, deck.Hero, deck.Opponent, deck.Strategy, deck.Style, new string[0],
            deck.CardIds.Select(id => WaitingForCatalog(id) ? "CATA_612" : id).ToArray(), deck.Source) : deck).ToArray();
    // Keep stable loaner identities and other seats while early-admission cards await the installed catalog.
    private static bool WaitingForCatalog(string id) => (id == "BE_036" || id.StartsWith("BE_EVENT_", StringComparison.Ordinal))
        && !LocalCardCatalog.InActiveMeta(Cards.FromId(id));
    // Fixed opponents of the later seat loaners and the showcase: always one of the four frozen batch-2 opponents.
    private static readonly IReadOnlyDictionary<string, string> SeatOpponents = new Dictionary<string, string>(StringComparer.Ordinal) {
        ["test-standard-b11-warrior"] = "test-standard-batch-2-opp-druid",
        ["test-standard-b11-priest"] = "test-standard-batch-2-opp-rogue",
        ["test-standard-b11-mage"] = "test-standard-batch-2-opp-warlock",
        ["test-standard-b11-paladin"] = "test-standard-batch-2-opp-hunter",
        ["test-standard-b11-druid"] = "test-standard-batch-2-opp-warlock",
        ["test-standard-b11-rogue"] = "test-standard-batch-2-opp-hunter",
        ["test-standard-b11-warlock"] = "test-standard-batch-2-opp-druid",
        ["test-standard-b11-hunter"] = "test-standard-batch-2-opp-rogue",
        ["test-standard-b11-shaman"] = "test-standard-batch-2-opp-warlock",
        ["test-keywords-cfa-paladin"] = "test-standard-batch-2-opp-hunter",
        ["test-keywords-cfa-hunter"] = "test-standard-batch-2-opp-hunter",
        ["test-keywords-cfa-warrior"] = "test-standard-batch-2-opp-hunter",
        ["test-keywords-cfa-shaman"] = "test-standard-batch-2-opp-hunter",
        ["test-keywords-cfa-priest"] = "test-standard-batch-2-opp-hunter",
        ["test-keywords-cfa-mage"] = "test-standard-batch-2-opp-hunter",
        ["test-standard-cardstate-druid"] = "test-standard-batch-2-opp-warlock",
        ["test-standard-cardstate-priest"] = "test-standard-batch-2-opp-warlock",
        ["test-standard-cardstate-mage"] = "test-standard-batch-2-opp-warlock",
        ["test-standard-cardstate-hunter"] = "test-standard-batch-2-opp-warlock",
        ["test-standard-cardstate-paladin"] = "test-standard-batch-2-opp-warlock",
        ["test-standard-cardstate-shaman"] = "test-standard-batch-2-opp-warlock",
        ["test-deathknight-buu"] = "test-standard-batch-2-opp-rogue",
        ["test-standard-cardstate-warrior"] = "test-standard-batch-2-opp-druid",
        ["test-standard-cardstate-warlock"] = "test-standard-batch-2-opp-rogue",
        ["test-standard-more-warrior"] = "test-standard-batch-2-opp-druid",
        ["test-standard-more-priest"] = "test-standard-batch-2-opp-rogue",
        ["test-standard-more-mage"] = "test-standard-batch-2-opp-warlock",
        ["test-standard-more-paladin"] = "test-standard-batch-2-opp-hunter",
        ["test-standard-more-druid"] = "test-standard-batch-2-opp-warlock",
        ["test-standard-more-rogue"] = "test-standard-batch-2-opp-hunter",
        ["test-standard-more-warlock"] = "test-standard-batch-2-opp-druid",
        ["test-standard-more-hunter"] = "test-standard-batch-2-opp-rogue",
        ["test-standard-more-shaman"] = "test-standard-batch-2-opp-warlock",
        ["test-standard-more-demonhunter"] = "test-standard-batch-2-opp-hunter",
        ["test-standard-b10-warrior"] = "test-standard-batch-2-opp-druid",
        ["test-standard-b10-priest"] = "test-standard-batch-2-opp-rogue",
        ["test-standard-b10-mage"] = "test-standard-batch-2-opp-warlock",
        ["test-standard-b10-paladin"] = "test-standard-batch-2-opp-hunter",
        ["test-standard-b10-druid"] = "test-standard-batch-2-opp-warlock",
        ["test-standard-b10-rogue"] = "test-standard-batch-2-opp-hunter",
        ["test-standard-b10-warlock"] = "test-standard-batch-2-opp-druid",
        ["test-standard-b10-hunter"] = "test-standard-batch-2-opp-rogue",
        ["test-standard-b10-shaman"] = "test-standard-batch-2-opp-warlock",
        ["test-standard-b10-demonhunter"] = "test-standard-batch-2-opp-hunter",
        ["test-standard-b10-deathknight"] = "test-standard-batch-2-opp-rogue",
        ["test-standard-b10-deathknight-2"] = "test-standard-batch-2-opp-rogue",
        ["test-standard-mecha-warrior"] = "test-standard-batch-2-opp-druid",
        ["test-standard-mecha-priest"] = "test-standard-batch-2-opp-rogue",
        ["test-standard-azalina-priest"] = "test-standard-batch-2-opp-rogue",
        ["test-standard-mecha-mage"] = "test-standard-batch-2-opp-warlock",
        ["test-standard-mecha-paladin"] = "test-standard-batch-2-opp-hunter",
        ["test-standard-mecha-druid"] = "test-standard-batch-2-opp-warlock",
        ["test-standard-mecha-rogue"] = "test-standard-batch-2-opp-hunter",
        ["test-standard-mecha-warlock"] = "test-standard-batch-2-opp-druid",
        ["test-standard-mecha-hunter"] = "test-standard-batch-2-opp-rogue",
        ["test-standard-mecha-shaman"] = "test-standard-batch-2-opp-warlock",
        ["test-standard-mecha-demonhunter"] = "test-standard-batch-2-opp-hunter",
        ["test-standard-mecha-deathknight"] = "test-standard-batch-2-opp-rogue",
        ["test-standard-mecha-deathknight-2"] = "test-standard-batch-2-opp-rogue",
        ["test-standard-rewind-rogue"] = "test-standard-batch-2-opp-hunter",
        ["test-standard-rewind-mage"] = "test-standard-batch-2-opp-warlock",
        ["test-standard-rewind-warlock"] = "test-standard-batch-2-opp-druid",
        ["test-standard-rewind-shaman"] = "test-standard-batch-2-opp-warlock",
        ["test-standard-rewind-paladin"] = "test-standard-batch-2-opp-hunter",
        ["test-standard-rewind-druid"] = "test-standard-batch-2-opp-warlock",
        ["test-standard-rewind-warrior"] = "test-standard-batch-2-opp-druid",
        ["test-standard-rewind-priest"] = "test-standard-batch-2-opp-rogue",
        ["test-standard-rewind-demonhunter"] = "test-standard-batch-2-opp-hunter",
        ["test-standard-rewind-hunter"] = "test-standard-batch-2-opp-rogue",
        [TestShowcaseId] = "test-standard-batch-2-opp-hunter",
    };
    private static LocalDeck CardStateLoaner(string cls, string hero, string fixture)
    {
        string[] filler = "CATA_612 EDR_492 EDR_598 TIME_056 TIME_059 TIME_062 TIME_434 TIME_720 TLC_249 TLC_468 TLC_621 TLC_831 EDR_495 BE_994".Split(' ');
        if (cls == "shaman") { filler[0] = "DINO_406"; filler[1] = "BE_842"; filler[2] = "JAIL_803"; }
        return new LocalDeck("test-standard-cardstate-" + cls, "TEST: Card state " + cls, hero,
            "Test opponent: Standard batch 2 Warlock", "Card-state primitive test loaner. Not a starter deck.", DeckStyle.Value,
            new string[0], new[] { fixture, fixture }.Concat(filler.SelectMany(id => new[] { id, id })).ToArray());
    }

    private static LocalDeck DeathKnightLoaner(string kind, string runes, string[] package)
    {
        string[] common = { "CORE_EDR_002", "CORE_RLK_062", "CORE_RLK_121", "EDR_816",
            "JAIL_440", "RLK_708", "RLK_958", "TLC_443" };
        string[] filler = { "CS2_179", "CS2_182", "CS2_200", "CS2_222", "CS2_120", "CS2_171", "CS2_172",
            "CS2_173", "EX1_015", "EX1_019", "EX1_096", "EX1_066", "EX1_007", "EX1_556", "EX1_284" };
        return new LocalDeck("test-deathknight-" + kind, "TEST: Death Knight " + runes, "HERO_11",
            "Test opponent: Standard batch 2", "Death Knight test loaner. Not a starter deck.", DeckStyle.Value,
            new string[0], common.Concat(package).Concat(filler.Concat(filler).Take(30 - common.Length - package.Length)).ToArray());
    }
    // Fixed opponent of the test loaner only: ramps into Forest Lord Cenarius, whose three suspended
    // Choose Thrice prompts the bot answers. Six copies of a legendary keep it test-only.
    public static readonly IReadOnlyList<LocalDeck> TestOpponents = new[] {
        new LocalDeck(TestCenariusId, "TEST: Cenarius prompts", "HERO_06", "Test: Cenarius prompts",
            "Test opponent for live qualification of suspended multi-step choices.", DeckStyle.Control,
            new[] { "EX1_169", "CS2_013", "CS2_179", "CS2_121", "EX1_405", "CS2_125", "CS2_187", "CS2_162",
                "CS2_182", "CS2_200", "CS2_232", "EX1_396" },
            Enumerable.Repeat("EDR_209", 6).ToArray()),
        new LocalDeck(TestStandardBatch1OpponentId, "TEST: Standard batch 1 opponent", "HERO_05", "Test: Standard batch 1",
            "Fixed Hunter test opponent for Standard batch 1 live qualification.", DeckStyle.Value,
            new[] { "TIME_601", "TIME_603", "BE_193", "JAIL_456", "TIME_056", "CATA_612", "TLC_249", "EDR_861" },
            new[] { "TIME_605", "EDR_481", "EDR_486", "EDR_598", "EDR_492", "TIME_059", "TIME_051", "TIME_720",
                "TIME_062", "DINO_411", "JAIL_007", "EDR_110", "EDR_571", "FIR_929" }),
        new LocalDeck("test-standard-batch-2-opp-druid", "TEST: Standard batch 2 Druid opponent", "HERO_06", "Test: Standard batch 2",
            "Fixed Druid test opponent for Standard batch 2 live qualification.", DeckStyle.Value,
            new string[0], DruidOpponentCards),
        new LocalDeck("test-standard-batch-2-opp-rogue", "TEST: Standard batch 2 Rogue opponent", "HERO_03", "Test: Standard batch 2",
            "Fixed Rogue test opponent for Standard batch 2 live qualification.", DeckStyle.Value,
            new string[0], RogueOpponentCards),
        new LocalDeck("test-standard-batch-2-opp-warlock", "TEST: Standard batch 2 Warlock opponent", "HERO_07", "Test: Standard batch 2",
            "Fixed Warlock test opponent for Standard batch 2 live qualification.", DeckStyle.Value,
            new string[0], WarlockOpponentCards),
        new LocalDeck("test-standard-batch-2-opp-hunter", "TEST: Standard batch 2 Hunter opponent", "HERO_05", "Test: Standard batch 2",
            "Fixed Hunter test opponent for Standard batch 2 live qualification.", DeckStyle.Value,
            new string[0], HunterOpponentCards),
        new LocalDeck("showcase-ai-hunter-face", "Face Hunter", "HERO_05", "Kestrel, the Quickshot",
            "Develop cheap minions; spend the hand; keep Charge and hero-power damage flowing.", DeckStyle.Tempo,
            new[] { "CATA_556", "BE_255", "TIME_606", "DINO_434", "CATA_558", "TLC_249", "TIME_601", "FIR_909", "BE_730", "JAIL_878", "JAIL_881", "END_013", "CATA_560", "MEND_302", "TIME_047" },
            new string[0]),
        new LocalDeck("showcase-ai-paladin-end-turn", "End of Turn Paladin", "HERO_04", "Solenne, the Steadfast",
            "Build durable Divine Shield boards; grow end-of-turn pressure; refill after removal.", DeckStyle.Value,
            new[] { "CORE_ONY_022", "TIME_015", "JAIL_377", "JAIL_328", "BE_152", "JAIL_035", "EDR_257", "CATA_474", "EDR_253", "CATA_478", "JAIL_516", "CATA_475", "CORE_TSC_076", "FIR_941" },
            new[] { "CATA_473", "CATA_432" }),
        new LocalDeck("showcase-ai-warrior-armor", "Armor Control Warrior", "HERO_01", "Brakka, the Iron Warden",
            "Bank Armor behind Taunts; develop Herald threats; finish with Charge and Deathrattles.", DeckStyle.Control,
            new[] { "CORE_LOOT_413", "CORE_EX1_606", "BE_572", "DINO_400", "CATA_580", "TLC_620", "TLC_606", "CORE_WW_329", "CATA_160", "EDR_979", "CATA_722", "BE_560", "CORE_SW_068" },
            new[] { "CORE_EX1_110", "TIME_714", "CATA_150", "EDR_471" })
    };
#else
    public static readonly IReadOnlyList<LocalDeck> TestLoaners = new LocalDeck[0];
    public static readonly IReadOnlyList<LocalDeck> TestOpponents = new LocalDeck[0];
#endif
    // Source-engine player defaults are separate from historical showcase/test and AI population catalogs.
    // Core starter loans (2026-10-28 #57): one Core-only deck per class, the normal player tray. Loans never grant cards;
    // class levels unlock the same Core cards for real. Order is the tray order (NativeUIProjection.PlayerLoanerKeys).
    public static readonly IReadOnlyList<LocalDeck> PlayerCoreStarterDecks = Array.AsReadOnly(
#if ENGINE_SOURCE
        new[] {
            new LocalDeck("player-core-mage", "Frost and Fire Mage", "HERO_08", "Showcase opponents",
                "Trade early with cheap minions and burn; Deep Freeze and Flamestrike hold the board; Antonidas refuels Fireballs.", DeckStyle.Frost,
                new[] { "CORE_DRG_107", "CORE_SW_108", "CORE_UNG_809", "CORE_CS2_024", "CORE_NEW1_023", "CORE_CS2_023", "CORE_EDR_001", "CORE_UNG_928", "CORE_BAR_541", "CORE_CS2_179", "CORE_CS2_029", "CORE_UNG_084", "CORE_DRG_079", "CORE_CS2_032" },
                new[] { "CORE_BT_072", "CORE_EX1_559" }),
            new LocalDeck("player-core-paladin", "Silver Hand Paladin", "HERO_04", "Showcase opponents",
                "Flood the board with shielded recruits; buff and protect them; Stormwind Champion and Tirion close the game.", DeckStyle.Value,
                new[] { "CORE_ICC_038", "CORE_EX1_362", "CORE_CFM_753", "CORE_BT_292", "CORE_GVG_085", "CORE_CS2_093", "CORE_CS2_094", "CORE_GVG_061", "CORE_GVG_059", "CORE_CS2_122", "CORE_KAR_057", "CORE_CS2_179", "CORE_CS2_222" },
                new[] { "CORE_UNG_952", "CS3_024", "CORE_SW_047", "CORE_EX1_383" }),
            new LocalDeck("player-core-priest", "Holy Light Priest", "HERO_09", "Showcase opponents",
                "Trade with sturdy minions and keep them healed; clear wide boards with Holy Nova; win late with big Taunts.", DeckStyle.Control,
                new[] { "CORE_CS1_130", "CORE_CS2_004", "CORE_ULD_271", "CORE_ICC_210", "CORE_UNG_928", "CORE_CS1_112", "CORE_BAR_311", "CORE_UNG_084", "CORE_GIL_622", "CORE_SW_442", "CORE_BAR_313", "CORE_BAR_310", "CORE_DRG_079" },
                new[] { "CORE_SCH_512", "CORE_EX1_110", "CORE_EX1_198", "CORE_ICC_214" }),
            new LocalDeck("player-core-warrior", "Battle Scar Warrior", "HERO_01", "Showcase opponents",
                "Hold the board behind Taunts and Armor; Execute and Warmaul remove threats; legends like Cairne and Grommash close.", DeckStyle.Control,
                new[] { "CORE_CS2_108", "CORE_EX1_391", "CORE_AT_064", "CORE_EX1_606", "CORE_UNG_928", "CORE_BT_120", "CORE_OG_218", "CORE_WW_329", "CORE_CS2_179", "CORE_UNG_084", "CORE_DRG_079", "CORE_SW_068" },
                new[] { "CORE_WON_350", "CS3_024", "CS3_025", "CORE_EX1_110", "CORE_ULD_178", "CORE_EX1_414" }),
            new LocalDeck("player-core-druid", "Grove Druid", "HERO_06", "Showcase opponents",
                "Ramp with Innervate, pick both Choose One halves with Fandral, and grow into Turtles, Orcas and Underking.", DeckStyle.Value,
                new[] { "CORE_EX1_169", "CORE_AT_037", "CORE_SW_439", "CORE_EX1_154", "CORE_EX1_160", "CORE_CS2_009", "CORE_UNG_928", "CORE_LOOT_309", "CORE_CS2_179", "CORE_UNG_084", "CORE_ONY_018", "CORE_TSC_650", "CORE_SW_429", "CORE_RLK_657" },
                new[] { "CORE_OG_044", "CORE_CATA_006" }),
            new LocalDeck("player-core-hunter", "Beast Pack Hunter", "HERO_05", "Showcase opponents",
                "Curve out with Beasts and keep the hero power firing; Dragonbane and Call of the Wild finish the job.", DeckStyle.Tempo,
                new[] { "CORE_UNG_912", "CORE_BAR_801", "CORE_DS1_185", "CORE_TRL_111", "CORE_EX1_162", "CORE_BRM_013", "CORE_NEW1_031", "CORE_BT_201", "CORE_SW_072", "CORE_GIL_622", "CORE_UNG_084", "CORE_EX1_028", "CORE_AV_337" },
                new[] { "CORE_EX1_014", "CORE_DRG_256", "CORE_TRL_900", "CORE_OG_211" }),
            new LocalDeck("player-core-rogue", "Cutthroat Rogue", "HERO_03", "Showcase opponents",
                "Swing your dagger with Pirates; chain cheap cards into Combo payoffs; Assassinate the biggest threat.", DeckStyle.Tempo,
                new[] { "CORE_CS2_072", "CORE_CS2_074", "CORE_BT_701", "CORE_WON_351", "CORE_DMF_511", "CORE_EX1_131", "CORE_EX1_129", "CORE_NEW1_018", "CS3_022", "CORE_EX1_134", "CORE_CS2_076", "CORE_DAL_720", "CORE_ULD_280", "CORE_BOT_576" },
                new[] { "CORE_EX1_145", "CORE_CFM_781" }),
            new LocalDeck("player-core-shaman", "Stormcaller Shaman", "HERO_02", "Showcase opponents",
                "Grow Elementals, bolt early threats and Hex the big ones; Fire and Earth Elementals take over the mid-game.", DeckStyle.Value,
                new[] { "CORE_EX1_238", "CS3_007", "CORE_WC_042", "CORE_UNG_809", "CORE_CS2_179", "CORE_BOT_451", "CORE_AT_052", "CORE_EX1_246", "CORE_EX1_259", "CORE_UNG_928", "CORE_UNG_084", "CORE_EX1_250", "CORE_OG_031", "CORE_CS2_042" },
                new[] { "CORE_CATA_004", "CORE_TRL_345" }),
            new LocalDeck("player-core-warlock", "Fel Pact Warlock", "HERO_07", "Showcase opponents",
                "Spend Health for a fast Demon board; Raid Leader pushes damage; Voidlord, Willow and Jaraxxus hold the late game.", DeckStyle.Tempo,
                new[] { "CORE_EX1_319", "CORE_YOD_026", "CORE_EX1_302", "CORE_LOOT_013", "CORE_WON_096", "CORE_BT_156", "CORE_ICC_055", "CORE_EX1_162", "CORE_CS2_122", "CORE_SW_088", "CORE_CS2_179", "CORE_ULD_165", "CORE_LOOT_368" },
                new[] { "CORE_CS2_062", "CORE_EX1_309", "CORE_SCH_181", "CORE_EX1_323" }),
            new LocalDeck("player-core-demonhunter", "Fel Hunt Demon Hunter", "HERO_10", "Showcase opponents",
                "Attack every turn; drain with Lifesteal blades and Lifedrinker; cheapen Demons with Felscreamer; finish with Kayn's Charge.", DeckStyle.Tempo,
                new[] { "CORE_BT_351", "CORE_BT_480", "CORE_BT_035", "CORE_BT_321", "CORE_BT_801", "CORE_BT_921", "CORE_WC_701", "CORE_BT_156", "CORE_BT_416", "CORE_GIL_622", "CORE_UNG_084", "CORE_BT_510", "CORE_BT_493", "CS3_020" },
                new[] { "CORE_BT_187", "CORE_DRG_079" }),
            new LocalDeck("player-core-deathknight", "Risen Army Death Knight", "HERO_11", "Showcase opponents",
                "Collect Corpses from every trade; raise Taunt walls and Rush Ghouls; drain with Death Strike and Frostmourne.", DeckStyle.Value,
                new[] { "RLK_503", "RLK_061", "CORE_RLK_116", "CORE_RLK_066", "CORE_RLK_121", "RLK_708", "RLK_048", "CORE_RLK_118", "CORE_RLK_062", "CORE_RLK_745", "RLK_024", "RLK_060", "RLK_720" },
                new[] { "CORE_EDR_003", "CORE_RLK_087", "CORE_RLK_086", "CORE_RLK_506" })
        }
#else
        new LocalDeck[0]
#endif
    );
    // Retired meta loaners: out of every normal tray, still resolvable (Get, native id, display name) for saved
    // history and pending records. Never reuse their ids, native ids or display names.
    public static readonly IReadOnlyList<LocalDeck> RetiredPlayerStarterDecks = Array.AsReadOnly(
#if ENGINE_SOURCE
        new[] {
            new LocalDeck("player-rogue-imbue", "Imbue Rogue", "HERO_03", "Showcase opponents",
                "Imbue early; draw with Petal Picker; replay Battlecries and finish with discounted Wild Gods.", DeckStyle.Value,
                new[] { "CORE_EX1_145", "EDR_528", "EDR_852", "END_000", "EDR_523", "FIR_920", "JAIL_225", "END_001", "FIR_921", "TIME_001", "EDR_800", "EDR_860", "TIME_770" },
                new[] { "END_036", "EDR_856", "TIME_EVENT_999", "EDR_888" }),
            new LocalDeck("player-warlock-dragon-shred", "Shred Warlock", "HERO_07", "Showcase opponents",
                "Build cheap Dragon boards; shuffle Shreds; finish with Lifesteal threats and copied Rush Dragons.", DeckStyle.Tempo,
                new[] { "TLC_451", "CATA_556", "TIME_026", "CORE_EX1_319", "EDR_485", "CORE_KAR_062", "EDR_889", "TIME_027", "TIME_025", "CATA_111", "END_018", "TIME_028", "TIME_045", "TIME_029", "END_033" },
                new string[0]),
            new LocalDeck("player-warrior-armor-herald", "Armor Warrior", "HERO_01", "Showcase opponents",
                "Bank Armor; control the board; Herald into Colossal pressure and upgraded Deathwing.", DeckStyle.Control,
                new[] { "JAIL_386", "CORE_AT_064", "CORE_EX1_606", "TIME_873", "DINO_400", "CATA_580", "TLC_620", "CATA_160", "TLC_601", "TLC_622", "CATA_722", "TLC_600", "CORE_SW_068" },
                new[] { "EDR_979", "TIME_714", "CATA_150", "CATA_190h" })
        }
#else
        new LocalDeck[0]
#endif
    );
    // Every player default that must resolve: Core starters, then retired loaners.
    public static readonly IReadOnlyList<LocalDeck> PlayerStarterDecks = Array.AsReadOnly(
        PlayerCoreStarterDecks.Concat(RetiredPlayerStarterDecks).ToArray());
#if ENGINE_SOURCE
    // Unknown saved cards have no sideboard metadata and remain hidden by profile projection.
    private static readonly Lazy<Dictionary<string, EngineCard>> nativeSaveCardIndex = new Lazy<Dictionary<string, EngineCard>>(() =>
        Cards.All.ToDictionary(card => card.Id, StringComparer.Ordinal));
#endif

    // Source-only opponents; never concatenate this list into a player or economy catalog.
    public static readonly IReadOnlyList<LocalDeck> NpcDecks = Array.AsReadOnly(
#if ENGINE_SOURCE
        new[] {
            new LocalDeck("npc-dragon-warrior", "Dragon Warrior", "HERO_01", "Dragonyyyy",
                "Contest the board, hold a Dragon, and turn damaged characters into Warptooth pressure.", DeckStyle.Tempo,
                new[] { "EDR_456", "CATA_556", "CATA_585", "CAP_107", "EDR_457", "EDR_889", "FIR_939", "CATA_582",
                    "CAP_105", "CATA_584", "EDR_492", "TIME_034", "END_033", "TLC_600" },
                new[] { "JAIL_421", "JAIL_384" },
                "https://hearthstone-decks.net/ (2026-10-20)"),
            new LocalDeck("npc-pure-paladin", "Pure Aura Paladin", "HERO_04", "FatherUther",
                "Build shielded boards, time Auras and preserve a refill after removal.", DeckStyle.Value,
                new[] { "CORE_ICC_038", "END_011", "CATA_472", "MEND_802", "JAIL_035", "JAIL_327", "TIME_019",
                    "CATA_474", "CATA_479", "TIME_700", "JAIL_516", "CATA_475" },
                new[] { "TIME_009t1", "EDR_258", "TIME_009t2", "CATA_473", "JAIL_397", "TIME_009" },
                "https://hearthstone-decks.net/pure-paladin-243-legend-unknown-score-14-5/ (2026-10-11)")
                .WithSideboards(new[] { new LocalSideboard { Source = "JAIL_397", Cards = new List<string> { "JAIL_328" } } }, true),
            new LocalDeck("npc-zee-shaman", "Zee Shaman", "HERO_02", "ZeeCashh",
                "Win early board control, sequence every fifth minion's doubled Battlecry, then Herald finishers.", DeckStyle.Tempo,
                new[] { "CORE_GIL_531", "CORE_ULD_191", "CORE_UNG_809", "CORE_WC_042", "CATA_556", "DINO_411",
                    "CATA_565", "JAIL_802", "JAIL_457", "TLC_482", "CATA_722" },
                new[] { "CORE_UNG_205", "JAIL_850", "EDR_856", "JAIL_458", "CATA_497", "JAIL_800", "CATA_153", "CATA_190h" },
                "https://hearthstone-decks.net/zee-shaman-37-legend-unknown-score-10-6/ (2026-10-09)")
        }
#else
        new LocalDeck[0]
#endif
    );
    // UI-only definitions: never feed the economy, collection, packs, or custom deck catalog.
    internal static readonly IReadOnlyList<string> TestStandardCardIds = Array.AsReadOnly(
#if ENGINE_SOURCE
        TestLoaners.Concat(TestOpponents).Concat(PlayerStarterDecks).SelectMany(deck => deck.CardIds).Distinct(StringComparer.Ordinal)
            .Where(SabberStoneCore.CardSets.Client.StandardSets.CollectibleIds.Contains).OrderBy(id => id, StringComparer.Ordinal).ToArray()
#else
        new string[0]
#endif
    );
    // Showcase players rotate curated AI decks; other test loaners retain fixed opponents.
    public static LocalDeck OpponentFor(string selected, IEnumerable<string> history)
    {
#if ENGINE_SOURCE
        if (PlayerStarterDecks.Any(deck => deck.Id == selected)
            || TestLoaners.Any(deck => deck.Id == selected && deck.Id.StartsWith("showcase-", StringComparison.Ordinal)))
        {
            var opponents = TestOpponents.Where(deck => deck.Id.StartsWith("showcase-ai-", StringComparison.Ordinal)).ToArray();
            string last = history.LastOrDefault(id => opponents.Any(deck => deck.Id == id));
            int index = Array.FindIndex(opponents, deck => deck.Id == last);
            return opponents[(index + 1) % opponents.Length];
        }
        if (selected != null && selected.StartsWith("test-standard-extra-", StringComparison.Ordinal))
            return OpponentFor(selected.Replace("test-standard-extra-", "test-standard-batch-2-"), history);
        if (selected == "test-deathknight-rainbow") return Get("test-standard-batch-2-opp-druid");
        if (selected == "test-deathknight-frost") return Get("test-standard-batch-2-opp-warlock");
        if (selected == "test-deathknight-unholy") return Get("test-standard-batch-2-opp-hunter");
        if (selected == "test-deathknight-blood") return Get("test-standard-batch-2-opp-druid");
        if (selected != null && SeatOpponents.TryGetValue(selected, out string seatOpponent)) return Get(seatOpponent);
        if (selected == TestRushSilenceId) return Get(TestStandardBatch1OpponentId);
        if (selected == TestDecisionsId) return Get(TestCenariusId);
        if (selected == TestStandardBatch1Id) return Get(TestStandardBatch1OpponentId);
        if (selected == "test-standard-batch-2-warrior") return Get("test-standard-batch-2-opp-druid");
        if (selected == "test-standard-batch-2-priest") return Get("test-standard-batch-2-opp-rogue");
        if (selected == "test-standard-batch-2-mage") return Get("test-standard-batch-2-opp-warlock");
        if (selected == "test-standard-batch-2-paladin") return Get("test-standard-batch-2-opp-hunter");
        if (selected == "test-standard-batch-2-druid") return Get("test-standard-batch-2-warrior");
        if (selected == "test-standard-batch-2-rogue") return Get("test-standard-batch-2-priest");
        if (selected == "test-standard-batch-2-warlock") return Get("test-standard-batch-2-mage");
        if (selected == "test-standard-batch-2-hunter") return Get("test-standard-batch-2-paladin");
        if (selected == "test-standard-batch-2-shaman") return Get("test-standard-batch-2-opp-druid");
        if (selected == "test-standard-batch-2-demonhunter") return Get("test-standard-batch-2-opp-rogue");
#endif
        return NextOpponent(history);
    }
    public static LocalDeck NextOpponent(IEnumerable<string> history)
    {
        // Only starter archetypes rotate: a test opponent, or one this build lacks, must not break the next start.
        var played = history.Where(id => All.Any(deck => deck.Id == id)).ToList();
        var recent = new HashSet<string>(played.Skip(Math.Max(0, played.Count - 2)));
        string lastHero = played.Count == 0 ? "" : Get(played.Last()).Hero;
        return All.Where(d => !recent.Contains(d.Id)
#if STANDARD_PROOF
            && !StandardProofNative.IsProof(d.Id)
#endif
        ).OrderBy(d => played.Count(id => id == d.Id))
            .ThenBy(d => d.Hero == lastHero ? 1 : 0).ThenBy(d => d.Id, StringComparer.Ordinal).First();
    }
    // NPC World seam: library decks the static lists lack (null = today's behaviour). Lives here because
    // CardsTests links LocalDecks without plugin/Npc.
    public static Func<string, LocalDeck> ExternalDecks;
    // A throwing hook is logged and treated as "no such deck" (the same InvalidOperationException as today).
    public static Action<string> ExternalDecksLog;
    public static LocalDeck Get(string id) => All.Concat(TestLoaners).Concat(TestOpponents).Concat(PlayerStarterDecks).Concat(NpcDecks).SingleOrDefault(d => d.Id == id)
        ?? External(id) ?? throw new InvalidOperationException("Unknown local deck: " + id);
    // Stable identity wins; catalogue order retains ownership of ambiguous legacy display-name aliases.
    internal static LocalDeck Resolve(IEnumerable<LocalDeck> decks, string key)
    {
        if (key == null) return null;
        var catalog = decks as IReadOnlyList<LocalDeck> ?? decks.ToArray();
        return catalog.FirstOrDefault(deck => deck.Id == key) ?? catalog.FirstOrDefault(deck => deck.Name == key);
    }
    private static LocalDeck External(string id)
    {
        var hook = ExternalDecks;
        if (hook == null) return null;
        try { return hook(id); }
        catch (Exception error)
        {
            try { ExternalDecksLog?.Invoke("LocalDecks.ExternalDecks(" + id + ") failed: " + error.GetType().Name + ": " + error.Message); }
            catch (Exception) { }
            return null;
        }
    }

    public static LocalDeck Custom(string name, string hero, IEnumerable<string> ids) => CreateCustom(name, hero, ids,
        PlayerHeroes, customCardIds.Value, true);

    // Drafts are also UI snapshots: keep old mixed-printing decks editable. Saves and play enforce group limits.
    public static LocalDeck Draft(string name, string hero, IEnumerable<string> ids) => CreateCustom(name, hero, ids,
        PlayerHeroes, customCardIds.Value, false, true);

    // Saved heroes and Core cards survive engine downgrades; unavailable drafts stay out of the UI.
    internal static bool ValidateStoredDraft(string name, string hero, IEnumerable<string> ids)
    {
        string[] cards = ids?.ToArray() ?? throw new ArgumentNullException(nameof(ids));
        if (cards.Length > DraftLimit(cards)) throw new InvalidOperationException("Local draft exceeds its deck-size limit.");
        CreateCustom(name, hero, cards.Where(id => !LocalCardCatalog.IsCore(id)),
            LocalCoreProgression.Heroes, storedCardIds.Value, false, true);
        if (PlayerHeroes.Contains(hero))
            ValidateRunes(cards.Where(id => !LocalCardCatalog.IsCore(id) || LocalCoreProgression.IsAvailable(id))
                .Select(Cards.FromId), HeroClass(hero));
        // Current Core metadata may change. Legality must never become a profile-load exception.
        return PlayerHeroes.Contains(hero) && cards.Where(id => !LocalCardCatalog.IsCore(id)).All(id => LocalCardCatalog.InActiveMeta(Cards.FromId(id)))
            && cards.Where(LocalCoreProgression.ById.ContainsKey).All(id => {
            CoreUnlock card = LocalCoreProgression.ById[id];
            return LocalCoreProgression.IsAvailable(id) ? IsAllowedInDeck(Cards.FromId(id), HeroClass(hero))
                : card.Hero.Length == 0 || card.Hero == hero;
        }) && cards.All(id => !LocalCardCatalog.IsCore(id) || LocalCoreProgression.ById.ContainsKey(id))
            && LocalCardCatalog.WithinCopyLimits(cards);
    }

    internal static LocalDeck LegacyV2Custom(string name, string hero, IEnumerable<string> ids) => CreateCustom(name, hero, ids,
        new[] { "HERO_08", "HERO_01", "HERO_09" }, All.Where(deck => new[] {
            "mage", "warrior", "priest", "mage-frost", "warrior-control", "priest-combo"
        }.Contains(deck.Id)), true);

    private static LocalDeck CreateCustom(string name, string hero, IEnumerable<string> ids,
        IEnumerable<string> heroes, IEnumerable<LocalDeck> knownDecks, bool requireComplete) =>
        CreateCustom(name, hero, ids, heroes, knownDecks.SelectMany(deck => deck.CardIds), requireComplete);

    private static LocalDeck CreateCustom(string name, string hero, IEnumerable<string> ids,
        IEnumerable<string> heroes, IEnumerable<EngineCard> knownCards, bool requireComplete) =>
        CreateCustom(name, hero, ids, heroes, knownCards.Select(card => card.Id), requireComplete);

    private static LocalDeck CreateCustom(string name, string hero, IEnumerable<string> ids,
        IEnumerable<string> heroes, IEnumerable<string> knownIds, bool requireComplete, bool allowStoredCopyOverflow = false)
    {
        if (!ValidDeckName(name))
            throw new ArgumentException("Deck name must contain 1 to 32 visible characters.");
        if (ids == null) throw new ArgumentNullException(nameof(ids));
        var cardIds = ids.ToArray();
        if (cardIds.Length > DraftLimit(cardIds) || (requireComplete && cardIds.Length != DeckSize(cardIds)))
            throw new InvalidOperationException(requireComplete ? "Local deck must contain " + DeckSize(cardIds) + " cards." : "Local draft exceeds its deck-size limit.");
        if (!heroes.Contains(hero))
            throw new InvalidOperationException("Unsupported local hero.");
        ValidateFabled(cardIds);
        var known = knownIds as HashSet<string> ?? new HashSet<string>(knownIds, StringComparer.Ordinal);
        var cards = cardIds.Select(id => known.Contains(id) ? Cards.FromId(id) : null).ToArray();
        if (cards.Any(card => card == null)) throw new InvalidOperationException("Unknown local card.");
        var selectedClass = HeroClass(hero);
        if (cards.Any(card => !IsAllowedInDeck(card, selectedClass)))
            throw new InvalidOperationException("Card does not belong to the selected class.");
        ValidateRunes(cards, selectedClass);
        if (cards.GroupBy(card => card.Id).Any(group => group.Count() > (group.First().Rarity == SabberStoneCore.Enums.Rarity.LEGENDARY ? 1 : 2)))
            throw new InvalidOperationException("Deck exceeds the playable copy limit.");
        if (!allowStoredCopyOverflow && !LocalCardCatalog.WithinCopyLimits(cardIds))
            throw new InvalidOperationException("Deck exceeds the playable copy limit across printings.");
        return new LocalDeck(name, hero, cardIds);
    }

    // The vendor enum lacks Demon Hunter; stored heroes need no loaded definition.
    internal static SabberStoneCore.Enums.CardClass HeroClass(string hero) => hero == "HERO_10"
        ? (SabberStoneCore.Enums.CardClass)14
        : (SabberStoneCore.Enums.CardClass)Enum.Parse(typeof(SabberStoneCore.Enums.CardClass),
            LocalCoreProgression.ClassName(hero).Replace(" ", "").ToUpperInvariant());

    internal static bool IsAllowedInDeck(EngineCard card, SabberStoneCore.Enums.CardClass heroClass)
    {
#if ENGINE_SOURCE
        return SabberStoneCore.Actions.CardClassRules.IsAllowedInDeck(card, heroClass);
#else
        if (card == null) return false;
        int mask = card[(SabberStoneCore.Enums.GameTag)476];
        int value = (int)heroClass;
        if (mask != 0) return value > 0 && value <= 32 && (mask & (1 << (value - 1))) != 0;
        switch ((SabberStoneCore.Enums.MultiClassGroup)card.MultiClassGroup)
        {
            case SabberStoneCore.Enums.MultiClassGroup.GRIMY_GOONS:
                return heroClass == SabberStoneCore.Enums.CardClass.HUNTER || heroClass == SabberStoneCore.Enums.CardClass.PALADIN || heroClass == SabberStoneCore.Enums.CardClass.WARRIOR;
            case SabberStoneCore.Enums.MultiClassGroup.JADE_LOTUS:
                return heroClass == SabberStoneCore.Enums.CardClass.DRUID || heroClass == SabberStoneCore.Enums.CardClass.ROGUE || heroClass == SabberStoneCore.Enums.CardClass.SHAMAN;
            case SabberStoneCore.Enums.MultiClassGroup.KABAL:
                return heroClass == SabberStoneCore.Enums.CardClass.MAGE || heroClass == SabberStoneCore.Enums.CardClass.PRIEST || heroClass == SabberStoneCore.Enums.CardClass.WARLOCK;
            default: return card.Class == heroClass || card.Class == SabberStoneCore.Enums.CardClass.NEUTRAL;
        }
#endif
    }

    // Shared by recipe validation and native editor save; incomplete ownership never rejects a draft.
    internal static void ValidateNativeSave(IEnumerable<string> main, IEnumerable<LocalSideboard> sideboards, bool complete = false)
    {
        var ids = main.ToArray();
        var rows = (sideboards ?? Enumerable.Empty<LocalSideboard>()).ToArray();
        if (rows.Any(row => row == null || row.Source == null || row.Cards == null)
            || rows.Select(row => row.Source).Distinct(StringComparer.Ordinal).Count() != rows.Length)
            throw new InvalidOperationException("Invalid native sideboard records.");
        ValidateFabled(ids);
        int maximum = complete ? DeckSize(ids) : DraftLimit(ids);
        if (ids.Length > maximum || complete && ids.Length != maximum)
            throw new InvalidOperationException("Local draft must fit its " + maximum + "-card limit.");
#if ENGINE_SOURCE
        foreach (string id in ids.Distinct(StringComparer.Ordinal))
            // MAX_SIDEBOARD_CARDS (2931) is pinned to client 37.0.0.254477.
            if (nativeSaveCardIndex.Value.TryGetValue(id, out var card) && card[(SabberStoneCore.Enums.GameTag)2931] > 0
                && !SabberStoneCore.Actions.DeckbuildingRules.HasSideboard(id))
                throw new InvalidOperationException("Unsupported native sideboard owner: " + id);
        SabberStoneCore.Actions.DeckbuildingRules.Validate(ids, rows.ToDictionary(row => row.Source, row => row.Cards.ToArray()),
            SabberStoneCore.Enums.FormatType.FT_WILD, complete);
#else
        if (rows.Length != 0) throw new InvalidOperationException("Sideboards require the source engine.");
#endif
    }

    // A Fabled primary's companions (empty for any other card). Native gives each companion its primary's version
    // (CollectionDeck.AddFabledBundledCard) and never puts companions in the collection.
    internal static IReadOnlyList<string> FabledBundle(string id)
    {
#if ENGINE_SOURCE
        return SabberStoneCore.Actions.FabledRules.Bundle(id);
#else
        return Array.Empty<string>();
#endif
    }

    // Engine deckbuilding legality of one sideboard selection (owner modelled by DeckbuildingRules, active meta). Native
    // uses it for an owner whose server-delivered sideboard subset is empty offline (King of the Underbelly, 19499).
    internal static bool SideboardEligible(string owner, string cardId)
    {
#if ENGINE_SOURCE
        if (!SabberStoneCore.Actions.DeckbuildingRules.HasSideboard(owner) || cardId == null) return false;
        EngineCard card;
        try { card = Cards.FromId(cardId); } catch (KeyNotFoundException) { return false; }
        // Eligible rejects most cards in a few field reads; the meta check scans policies, so it runs last.
        return card != null && SabberStoneCore.Actions.DeckbuildingRules.Eligible(owner, card, SabberStoneCore.Enums.FormatType.FT_WILD)
            && LocalCardCatalog.InActiveMeta(card);
#else
        return false;
#endif
    }

    // Use the engine rule only where native has no subset data: a modelled owner whose native subset is empty.
    internal static bool UsesLocalSideboardSubset(string owner, int subsetId, int nativeSubsetCount) =>
#if ENGINE_SOURCE
        subsetId != 0 && nativeSubsetCount == 0 && SabberStoneCore.Actions.DeckbuildingRules.HasSideboard(owner);
#else
        false;
#endif

    internal static bool IsFabledCompanion(string id)
    {
#if ENGINE_SOURCE
        return SabberStoneCore.Actions.FabledRules.IsCompanion(id);
#else
        return false;
#endif
    }

    // Retail (CollectionDeck.AddFabledBundledCard): a companion never reaches the collection; each copy is owned through
    // an owned copy of its primary in the same deck. Returns owned counts plus those deck-local companion credits.
    internal static Dictionary<string, int> WithDeckCompanions(IReadOnlyDictionary<string, int> owned, IEnumerable<string> deckIds)
    {
        var result = owned.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var primary in deckIds.GroupBy(id => id, StringComparer.Ordinal))
        {
            int credit = Math.Min(primary.Count(), owned.TryGetValue(primary.Key, out int copies) ? copies : 0);
            if (credit <= 0) continue;
            foreach (string companion in FabledBundle(primary.Key))
                result[companion] = checked((result.TryGetValue(companion, out int current) ? current : 0) + credit);
        }
        return result;
    }

    internal static void ValidateFabled(IEnumerable<string> ids)
    {
#if ENGINE_SOURCE
        string error = SabberStoneCore.Actions.FabledRules.DeckErrors(ids).FirstOrDefault();
        if (error != null) throw new InvalidOperationException(error);
#endif
    }

    // Blood, Frost, Unholy maxima; copies never add requirements.
    internal static int[] RuneCounts(IEnumerable<EngineCard> cards)
    {
#if ENGINE_SOURCE
        var runes = SabberStoneCore.Actions.RuneRules.FromCards(cards);
        return new[] { runes.Blood, runes.Frost, runes.Unholy };
#else
        var maxima = new int[3];
        foreach (var card in cards)
        {
            if (card == null) throw new ArgumentException("Unknown deck card.");
            for (int i = 0; i < maxima.Length; i++) maxima[i] = Math.Max(maxima[i], card[(SabberStoneCore.Enums.GameTag)(2196 + i)]);
        }
        return maxima;
#endif
    }

    internal static void ValidateRunes(IEnumerable<EngineCard> cards, SabberStoneCore.Enums.CardClass heroClass)
    {
        if (heroClass == SabberStoneCore.Enums.CardClass.DEATHKNIGHT && RuneCounts(cards).Sum() > 3)
            throw new InvalidOperationException("Death Knight decks may require at most three runes.");
    }

    private static bool ValidDeckName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        try
        {
            if (StringInfo.ParseCombiningCharacters(name).Length > 32) return false;
            bool hasRenderableBase = false;
            for (int index = 0; index < name.Length; index += char.IsSurrogatePair(name, index) ? 2 : 1)
            {
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(name, index);
                if (category == UnicodeCategory.Control || category == UnicodeCategory.LineSeparator
                    || category == UnicodeCategory.ParagraphSeparator) return false;
                if (category != UnicodeCategory.Format && category != UnicodeCategory.NonSpacingMark
                    && category != UnicodeCategory.SpacingCombiningMark && category != UnicodeCategory.EnclosingMark
                    && category != UnicodeCategory.SpaceSeparator && category != UnicodeCategory.Surrogate
                    && category != UnicodeCategory.OtherNotAssigned) hasRenderableBase = true;
            }
            return hasRenderableBase;
        }
        catch (ArgumentException) { return false; }
    }
}
