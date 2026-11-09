using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using EngineCard = SabberStoneCore.Model.Card;

public static class LocalCardCatalog
{
    public const int CurrentPoolVersion = 2;
    public const string LegacyCatalogHash = "8ab20c076391d6604f4c10216035f2b1fd2670f4cfdd0024f2cb766648dfac82";
    public const string CurrentCatalogHash = "580afabfbfd1a29424af55c8b4f0974785710a9b184f634aac81a887a673e870";
    public const string LegacyPoolHash = "86153fd3ef37378239324d7523851af458457bbb01f42b2f31301dcce5192111";
    public const string CurrentPoolHash = "5c0f1b3a3c15a7aadf27338e8b14963a416ee1274524e1edc69ba383622d7950";
    public const string LegacyV4StarterHash = "4742616f7d3cc7d2cba7eeaf757130680e5b4c030c5fc1bcf0b46c9904fc599a";
    public const string SupplementalGeneratedHash = "d289f6a1cfb8a7bb2c885e052c62976e2953e5ae301134e4160c5de31c175e90";
    public const string ResearchManifestHash = "c21ae847be98695888a8de72877a98dd9aaa0491072a2fc41e226d83d5df34dd";

    private const string LegacyV4IdManifest =
        "CS1_112 CS1_129 CS1_130 CS2_004 CS2_022 CS2_023 CS2_024 CS2_026 CS2_027 CS2_028 CS2_029 CS2_031 CS2_032 CS2_033 CS2_087 CS2_088 CS2_089 CS2_091 CS2_092 CS2_093 CS2_094 CS2_097 CS2_106 CS2_108 CS2_112 CS2_114 CS2_117 CS2_179 CS2_181 CS2_182 CS2_200 CS2_203 CS2_222 CS2_234 CS2_235 CS2_236 DS1_055 EX1_005 EX1_007 EX1_012 EX1_015 EX1_016 EX1_019 EX1_066 EX1_091 EX1_096 EX1_110 EX1_249 EX1_279 EX1_284 EX1_298 EX1_335 EX1_354 EX1_355 EX1_360 EX1_362 EX1_366 EX1_371 EX1_382 EX1_391 EX1_400 EX1_402 EX1_407 EX1_410 EX1_411 EX1_414 EX1_556 EX1_561 EX1_591 EX1_603 EX1_606 EX1_608 EX1_619 EX1_621 EX1_622 NEW1_011 NEW1_012 NEW1_020 NEW1_021";

    private const string CurrentIdManifest =
        "CS1_042 CS1_069 CS1_112 CS1_113 CS1_129 CS1_130 CS2_003 CS2_004 CS2_022 CS2_023 CS2_024 CS2_025 CS2_026 CS2_027 CS2_028 CS2_029 CS2_031 CS2_032 CS2_033 CS2_087 CS2_088 CS2_089 CS2_091 CS2_092 CS2_093 CS2_094 CS2_097 CS2_103 CS2_104 CS2_105 CS2_106 CS2_108 CS2_112 CS2_114 CS2_117 CS2_118 CS2_119 CS2_120 CS2_121 CS2_122 CS2_124 CS2_125 CS2_127 CS2_131 CS2_141 CS2_142 CS2_146 CS2_147 CS2_150 CS2_151 CS2_155 CS2_161 CS2_162 CS2_168 CS2_169 CS2_171 CS2_172 CS2_173 CS2_179 CS2_181 CS2_182 CS2_186 CS2_187 CS2_188 CS2_189 CS2_196 CS2_197 CS2_200 CS2_201 CS2_203 CS2_213 CS2_221 CS2_222 CS2_226 CS2_227 CS2_231 CS2_234 CS2_235 CS2_236 DS1_055 EX1_001 EX1_002 EX1_004 EX1_005 EX1_006 EX1_007 EX1_008 EX1_009 EX1_010 EX1_011 EX1_012 EX1_014 EX1_015 EX1_016 EX1_017 EX1_019 EX1_020 EX1_021 EX1_023 EX1_025 EX1_028 EX1_029 EX1_032 EX1_033 EX1_043 EX1_044 EX1_045 EX1_046 EX1_048 EX1_049 EX1_055 EX1_057 EX1_058 EX1_059 EX1_066 EX1_067 EX1_076 EX1_080 EX1_082 EX1_083 EX1_084 EX1_085 EX1_089 EX1_091 EX1_093 EX1_095 EX1_096 EX1_097 EX1_100 EX1_102 EX1_103 EX1_105 EX1_110 EX1_116 EX1_130 EX1_132 EX1_136 EX1_162 EX1_170 EX1_179 EX1_184 EX1_186 EX1_187 EX1_190 EX1_192 EX1_249 EX1_274 EX1_275 EX1_277 EX1_279 EX1_283 EX1_284 EX1_287 EX1_289 EX1_294 EX1_298 EX1_332 EX1_334 EX1_335 EX1_339 EX1_341 EX1_345 EX1_350 EX1_354 EX1_355 EX1_360 EX1_362 EX1_363 EX1_365 EX1_366 EX1_371 EX1_379 EX1_382 EX1_383 EX1_384 EX1_390 EX1_391 EX1_392 EX1_393 EX1_396 EX1_398 EX1_399 EX1_400 EX1_402 EX1_405 EX1_407 EX1_408 EX1_409 EX1_410 EX1_411 EX1_412 EX1_414 EX1_506 EX1_507 EX1_508 EX1_509 EX1_556 EX1_557 EX1_558 EX1_559 EX1_560 EX1_561 EX1_562 EX1_563 EX1_564 EX1_572 EX1_582 EX1_583 EX1_584 EX1_586 EX1_590 EX1_591 EX1_593 EX1_594 EX1_595 EX1_597 EX1_603 EX1_604 EX1_606 EX1_607 EX1_608 EX1_612 EX1_616 EX1_619 EX1_621 EX1_622 EX1_623 EX1_624 EX1_625 EX1_626 NEW1_011 NEW1_012 NEW1_017 NEW1_018 NEW1_019 NEW1_020 NEW1_021 NEW1_022 NEW1_023 NEW1_024 NEW1_025 NEW1_026 NEW1_027 NEW1_029 NEW1_030 NEW1_036 NEW1_037 NEW1_038 NEW1_040 NEW1_041 tt_004 tt_010";

    private const string NewBasicIdManifest =
        "CS1_042 CS1_113 CS2_003 CS2_025 CS2_103 CS2_105 CS2_118 CS2_119 CS2_120 CS2_121 CS2_122 CS2_124 CS2_125 CS2_127 CS2_131 CS2_141 CS2_142 CS2_147 CS2_150 CS2_155 CS2_162 CS2_168 CS2_171 CS2_172 CS2_173 CS2_186 CS2_187 CS2_189 CS2_196 CS2_197 CS2_201 CS2_213 CS2_226 EX1_011 EX1_025 EX1_084 EX1_192 EX1_277 EX1_399 EX1_506 EX1_508 EX1_582 EX1_593";

    private const string LegacyPoolIdManifest =
        "CS1_129 CS2_028 CS2_031 CS2_117 CS2_181 CS2_203 EX1_005 EX1_007 EX1_012 EX1_016 EX1_091 EX1_096 EX1_110 EX1_249 EX1_279 EX1_284 EX1_298 EX1_335 EX1_354 EX1_355 EX1_362 EX1_366 EX1_382 EX1_391 EX1_402 EX1_407 EX1_410 EX1_411 EX1_414 EX1_556 EX1_561 EX1_591 EX1_603 EX1_608 EX1_619 EX1_621 NEW1_012 NEW1_020 NEW1_021";

    private const string LegacyV4StarterManifest =
        "CS1_112:2 CS1_130:2 CS2_004:2 CS2_022:2 CS2_023:2 CS2_024:2 CS2_026:2 CS2_027:2 CS2_029:2 CS2_032:2 CS2_033:2 CS2_087:2 CS2_088:2 CS2_089:2 CS2_091:2 CS2_092:2 CS2_093:2 CS2_094:2 CS2_097:2 CS2_106:2 CS2_108:2 CS2_112:2 CS2_114:2 CS2_179:2 CS2_181:2 CS2_182:2 CS2_200:2 CS2_222:2 CS2_234:2 CS2_235:2 CS2_236:2 DS1_055:2 EX1_007:2 EX1_015:2 EX1_019:2 EX1_066:2 EX1_091:2 EX1_284:2 EX1_335:2 EX1_360:2 EX1_371:2 EX1_400:2 EX1_402:2 EX1_556:2 EX1_603:2 EX1_606:2 EX1_608:2 EX1_622:2 NEW1_011:2 NEW1_012:2";

    private const string CurrentPoolIdManifest =
        "CS1_069 CS1_129 CS2_028 CS2_031 CS2_104 CS2_117 CS2_146 CS2_151 CS2_161 CS2_169 CS2_181 CS2_188 CS2_203 CS2_221 CS2_227 CS2_231 EX1_001 EX1_002 EX1_004 EX1_005 EX1_006 EX1_007 EX1_008 EX1_009 EX1_010 EX1_012 EX1_014 EX1_016 EX1_017 EX1_020 EX1_021 EX1_023 EX1_028 EX1_029 EX1_032 EX1_033 EX1_043 EX1_044 EX1_045 EX1_046 EX1_048 EX1_049 EX1_055 EX1_057 EX1_058 EX1_059 EX1_067 EX1_076 EX1_080 EX1_082 EX1_083 EX1_085 EX1_089 EX1_091 EX1_093 EX1_095 EX1_096 EX1_097 EX1_100 EX1_102 EX1_103 EX1_105 EX1_110 EX1_116 EX1_130 EX1_132 EX1_136 EX1_162 EX1_170 EX1_179 EX1_184 EX1_186 EX1_187 EX1_190 EX1_249 EX1_274 EX1_275 EX1_279 EX1_283 EX1_284 EX1_287 EX1_289 EX1_294 EX1_298 EX1_332 EX1_334 EX1_335 EX1_339 EX1_341 EX1_345 EX1_350 EX1_354 EX1_355 EX1_362 EX1_363 EX1_365 EX1_366 EX1_379 EX1_382 EX1_383 EX1_384 EX1_390 EX1_391 EX1_392 EX1_393 EX1_396 EX1_398 EX1_402 EX1_405 EX1_407 EX1_408 EX1_409 EX1_410 EX1_411 EX1_412 EX1_414 EX1_507 EX1_509 EX1_556 EX1_557 EX1_558 EX1_559 EX1_560 EX1_561 EX1_562 EX1_563 EX1_564 EX1_572 EX1_583 EX1_584 EX1_586 EX1_590 EX1_591 EX1_594 EX1_595 EX1_597 EX1_603 EX1_604 EX1_607 EX1_608 EX1_612 EX1_616 EX1_619 EX1_621 EX1_623 EX1_624 EX1_625 EX1_626 NEW1_012 NEW1_017 NEW1_018 NEW1_019 NEW1_020 NEW1_021 NEW1_022 NEW1_023 NEW1_024 NEW1_025 NEW1_026 NEW1_027 NEW1_029 NEW1_030 NEW1_036 NEW1_037 NEW1_038 NEW1_040 NEW1_041 tt_004 tt_010";

    private const string SupplementalIdManifest =
        "CS2_103e2 CS2_104e CS2_105e CS2_122e CS2_152 CS2_188o CS2_221e CS2_226e CS2_boar DREAM_01 DREAM_02 DREAM_03 DREAM_04 DREAM_05 DREAM_05e EX1_001e EX1_004e EX1_009e EX1_014t EX1_014te EX1_025t EX1_043e EX1_044e EX1_046e EX1_055o EX1_059e EX1_080o EX1_084e EX1_093e EX1_103e EX1_116t EX1_130a EX1_162o EX1_187e EX1_274e EX1_334e EX1_363e EX1_379e EX1_383t EX1_390e EX1_393e EX1_398t EX1_399e EX1_409e EX1_409t EX1_412e EX1_506a EX1_507e EX1_508o EX1_509e EX1_584e EX1_590e EX1_598 EX1_604o EX1_607e EX1_612o EX1_623e EX1_625t EX1_625t2 EX1_tk28 EX1_tk29 ICC_830p NEW1_017e NEW1_018e NEW1_024o NEW1_026t NEW1_027e NEW1_029t NEW1_036e NEW1_036e2 NEW1_037e NEW1_038o NEW1_040t tt_004o tt_010a";

    private const string LegacyGeneratedPublicIdManifest =
        "CS2_tk1 CS2_mirror CS2_101t EX1_110t skele21";

    public static readonly IReadOnlyList<string> NewBasicIds = Ids(NewBasicIdManifest);
    public static readonly IReadOnlyDictionary<string, int> LegacyV4Starter = Counts(LegacyV4StarterManifest);
    public static readonly IReadOnlyList<string> SupplementalGeneratedIds = Ids(SupplementalIdManifest);
    public static readonly IReadOnlyList<string> CardStateGeneratedIds = Ids(
#if ENGINE_SOURCE
        "TIME_046e TLC_450e GBL_999e CATA_489t CATA_489t2 JAIL_801t CATA_134t CATA_134t2 CATA_134t3 CATA_134e CATA_306t1 CATA_306t2 CATA_306e CATA_479t CATA_479t2 CATA_479t3 CATA_479e CATA_820t CATA_820t2 CATA_820e TIME_058e TIME_063e1 TIME_063e2 EDR_461t EDR_843a EDR_843b EDR_843t1 FIR_918t FIR_918e1 JAIL_803t TLC_253e TLC_253e1 TLC_253e2 JAIL_850e JAIL_850e2 JAIL_997e1 JAIL_997e3 EDR_416t EDR_416te EDR_979e EDR_979e2"
#else
        ""
#endif
    );
    public static readonly IReadOnlyList<string> LegacyGeneratedPublicIds = Ids(LegacyGeneratedPublicIdManifest);
    public static readonly IReadOnlyDictionary<string, string> QuarantineReasons = new ReadOnlyDictionary<string, string>(
        new Dictionary<string, string>(StringComparer.Ordinal) {
            { "EX1_180", "Unbounded random Mage spell generation outside audited catalog." },
            { "EX1_188", "Unbounded random Beast generation outside audited catalog." },
            { "EX1_189", "Unbounded random Legendary generation outside audited catalog." },
            { "EX1_577", "Generated Finkle Einhorn uses the native Pip Quickwit identity and assets." },
            { "EX1_614", "Illidan and Flame of Azzinoth use native Xavius and Xavian Satyr identities and assets." }
        });
    public static readonly IReadOnlyList<string> QuarantinedIds = Array.AsReadOnly(new[] { "EX1_180", "EX1_188", "EX1_189", "EX1_577", "EX1_614" });
    public static readonly IReadOnlyList<string> NativeAuditTags = Array.AsReadOnly(new[] {
        "COLLECTIBLE", "CARD_SET", "CLASS", "CARDTYPE", "RARITY", "CARDRACE", "ELITE", "FACTION",
        "COST", "ATK", "HEALTH", "DAMAGE", "DURABILITY", "SPELLPOWER", "AFFECTED_BY_SPELL_POWER", "BATTLECRY",
        "DEATHRATTLE", "SECRET", "TAUNT", "CHARGE", "RUSH", "DIVINE_SHIELD", "STEALTH", "WINDFURY", "POISONOUS",
        "FREEZE", "CANT_ATTACK", "CANT_BE_TARGETED_BY_SPELLS", "CANT_BE_TARGETED_BY_HERO_POWERS", "AURA",
        "ADJACENT_BUFF", "ENRAGED", "SILENCE", "DISCARD_CARDS", "COST_BLOOD", "COST_FROST", "COST_UNHOLY", "CORPSE_SPENDER"
    });
    public static readonly IReadOnlyList<string> NativeAuditReferencedTags = Array.AsReadOnly(new[] {
        "CHARGE", "COUNTER", "DIVINE_SHIELD", "FREEZE", "SECRET", "SILENCE", "SPELLPOWER", "TAUNT", "WINDFURY"
    });
    public static readonly IReadOnlyList<string> NativeAuditMetadataTags = Array.AsReadOnly(new[] {
        "CARD_SET", "FACTION", "AttackVisualType", "TRIGGER_VISUAL", "DevState", "TECH_LEVEL", "IS_BACON_POOL_MINION", "1429"
    });
    public static readonly IReadOnlyList<EngineCard> LegacyV4 = Resolve(LegacyV4IdManifest);
    public static readonly IReadOnlyList<EngineCard> Current = Resolve(CurrentIdManifest);
    public static readonly IReadOnlyList<EngineCard> Supported = Array.AsReadOnly(Current.Concat(LocalCoreProgression.AvailableCards).ToArray());
    internal static bool InActiveMeta(EngineCard card)
    {
#if ENGINE_SOURCE
        return OfflinePoolPolicy.Load().AllowsCard(card);
#else
        return card != null;
#endif
    }
    // What V2 requires of a client: every collectible card a player or NPC can have in the active meta, and the pack pool.
    internal static IReadOnlyList<string> ActivePoolIds() => Array.AsReadOnly(Cards.All
        .Where(card => card.Implemented && card.Collectible && InActiveMeta(card)).Select(card => card.Id)
        .Concat(PackPool(CurrentPoolVersion).Select(card => card.Id))
        .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray());
    internal static bool InActiveMetaSet(int setId)
    {
#if ENGINE_SOURCE
        return OfflinePoolPolicy.Load().AllowsSet(setId);
#else
        return true;
#endif
    }
#if ENGINE_SOURCE
    public static IReadOnlyList<string> PackEligibleSets => OfflinePoolPolicy.Load().PackEligibleSets;
    public static IReadOnlyList<string> ActiveMetaExtraCards => OfflinePoolPolicy.Load().ExtraCards;
    public static string ActiveMetaNewestSet => OfflinePoolPolicy.Load().NewestSet;
    public static bool InActiveMetaPack(EngineCard card) => OfflinePoolPolicy.Load().IsPackEligible(card);
#endif
    public static readonly IReadOnlyList<string> NewClassicIds = Array.AsReadOnly(Current
        .Where(card => card.Rarity != Rarity.FREE && LegacyV4.All(old => old.Id != card.Id))
        .Select(card => card.Id).ToArray());
    private static readonly IReadOnlyList<EngineCard> LegacyPool = Resolve(LegacyPoolIdManifest);
    private static readonly IReadOnlyList<EngineCard> CurrentPool = Resolve(CurrentPoolIdManifest);

    static LocalCardCatalog()
    {
        if (LegacyV4.Count != 79 || LegacyV4Starter.Count != 50 || Current.Count != 252 || NewBasicIds.Count != 43 || NewClassicIds.Count != 130
            || LegacyPool.Count != 39 || CurrentPool.Count != 169 || SupplementalGeneratedIds.Count != 75
            || LegacyGeneratedPublicIds.Count != 5)
            throw new InvalidOperationException("Local card manifest count mismatch.");
        if (Current.Select(card => card.Id).Distinct(StringComparer.Ordinal).Count() != Current.Count
            || Current.Any(card => !card.Collectible || !card.Implemented || card.Type == CardType.HERO
                || (card.Class != CardClass.MAGE && card.Class != CardClass.PALADIN && card.Class != CardClass.PRIEST
                    && card.Class != CardClass.WARRIOR && card.Class != CardClass.NEUTRAL)))
            throw new InvalidOperationException("Local card manifest contains an ineligible card.");
        if (LegacyV4Starter.Any(entry => entry.Value < 1 || entry.Value > 2 || LegacyV4.All(card => card.Id != entry.Key)))
            throw new InvalidOperationException("Legacy V4 starter manifest mismatch.");
        if (NewBasicIds.Any(id => {
            EngineCard card = Current.Single(candidate => candidate.Id == id);
            return card.Set != CardSet.CORE || card.Rarity != Rarity.FREE;
        }) || CurrentPool.Any(card => card.Rarity == Rarity.FREE) || QuarantinedIds.Any(id => Current.Any(card => card.Id == id)))
            throw new InvalidOperationException("Local card policy manifest mismatch.");
        if (LegacyGeneratedPublicIds.Any(id => {
            EngineCard card = Cards.FromId(id);
            return card == null || card.Id != id || !card.Implemented || card.Collectible || card.Type != CardType.MINION
                || Current.Any(current => current.Id == id) || SupplementalGeneratedIds.Contains(id);
        })) throw new InvalidOperationException("Legacy generated public audit manifest mismatch.");
    }

    // Client tags 1413 (deck-rule copy identity), then 858 (counterpart DBF ID).
    internal static string CopyGroup(string id) => LocalCoreProgression.ById.TryGetValue(id, out var card) ? card.CopyGroup : id;
    internal static bool IsCore(string id) => id != null && (id.StartsWith("CORE_", StringComparison.OrdinalIgnoreCase)
        || LocalCoreProgression.ById.ContainsKey(id));
    internal static bool WithinCopyLimits(IEnumerable<string> ids) => ids.GroupBy(CopyGroup, StringComparer.Ordinal)
        .All(group => group.Count() <= group.Min(id => LocalCoreProgression.ById.TryGetValue(id, out var core)
            ? core.Copies : (Cards.FromId(id).Rarity == Rarity.LEGENDARY ? 1 : 2)));

    public static IReadOnlyList<EngineCard> PackPool(int version)
    {
        if (version == 1) return LegacyPool;
        if (version == CurrentPoolVersion) return CurrentPool;
        throw new ArgumentOutOfRangeException(nameof(version), "Unknown local pack pool version.");
    }

    private static IReadOnlyList<string> Ids(string manifest) => Array.AsReadOnly(manifest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));

    private static IReadOnlyDictionary<string, int> Counts(string manifest)
    {
        var result = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (string item in Ids(manifest))
        {
            int separator = item.LastIndexOf(':');
            if (separator <= 0 || !int.TryParse(item.Substring(separator + 1), out int count)
                || result.ContainsKey(item.Substring(0, separator)))
                throw new InvalidOperationException("Invalid count manifest entry: " + item);
            result.Add(item.Substring(0, separator), count);
        }
        return new ReadOnlyDictionary<string, int>(result);
    }

    private static IReadOnlyList<EngineCard> Resolve(string manifest)
    {
        var cards = Ids(manifest).Select(id => Cards.FromId(id)
            ?? throw new InvalidOperationException("Unknown local card " + id)).ToList();
        if (!cards.Select(card => card.Id).SequenceEqual(cards.Select(card => card.Id).OrderBy(id => id, StringComparer.Ordinal)))
            throw new InvalidOperationException("Local card manifest is not ordinally ordered.");
        return new ReadOnlyCollection<EngineCard>(cards);
    }
}

internal sealed class CoreUnlock
{
    internal readonly string Id, Name, Hero, CopyGroup;
    internal readonly int Threshold, Copies;
    internal CoreUnlock(string id, string name, string hero, int threshold, int copies, string copyGroup = null)
    { Id = id; Name = name; Hero = hero; Threshold = threshold; Copies = copies;
        CopyGroup = copyGroup ?? id; }
}

internal static class LocalCoreProgression
{
    // BEGIN GENERATED CORE UNLOCKS
    internal const int NeutralBaseCombinedLevel = 4;
    internal const int ScheduleVersion = 8;
    internal static readonly string[] Heroes = { "HERO_08", "HERO_04", "HERO_09", "HERO_01", "HERO_06", "HERO_05", "HERO_03", "HERO_02", "HERO_07", "HERO_10", "HERO_11" };
    internal static readonly int[] LevelThresholds = { 0, 40, 90, 160, 250, 360, 490, 640, 810, 1000, 1300, 1610, 1930, 2250, 2580, 2920, 3260, 3610, 3970, 4340, 4720, 5100, 5490, 5890, 6290, 6700, 7120, 7550, 7990, 8430, 8880, 9340, 9800, 10270, 10750, 11240, 11740, 12240, 12750, 13270, 13790, 14320, 14860, 15410, 15970, 16530, 17100, 17680, 18260, 18850, 19450, 20060, 20680, 21300, 21930, 22570, 23210, 23860, 24520, 25190 };
    internal const int WinXp = 100, LossXp = 60, TieXp = 60, ConcededXp = 0, AbandonedXp = 0;
    internal static readonly CoreUnlock[] Unlocks = {
        new CoreUnlock("CORE_DRG_107", "Violet Spellwing", "HERO_08", 1, 2, "DRG_107"),
        new CoreUnlock("CORE_SW_108", "First Flame", "HERO_08", 1, 2, "SW_108"),
        new CoreUnlock("CORE_CS2_024", "Frostbolt", "HERO_08", 2, 2, "CS2_024"),
        new CoreUnlock("CORE_BAR_541", "Runed Orb", "HERO_08", 2, 2, "BAR_541"),
        new CoreUnlock("CORE_CS2_023", "Arcane Intellect", "HERO_08", 3, 2, "CS2_023"),
        new CoreUnlock("CORE_EX1_289", "Ice Barrier", "HERO_08", 3, 2, "EX1_289"),
        new CoreUnlock("CORE_BAR_812", "Oasis Ally", "HERO_08", 4, 2, "BAR_812"),
        new CoreUnlock("CORE_EDR_001", "Babbling Bookcase", "HERO_08", 4, 2, "EDR_001"),
        new CoreUnlock("CORE_EX1_287", "Counterspell", "HERO_08", 5, 2, "EX1_287"),
        new CoreUnlock("CORE_LOOT_101", "Explosive Runes", "HERO_08", 6, 2, "LOOT_101"),
        new CoreUnlock("CORE_CS2_029", "Fireball", "HERO_08", 6, 2, "CS2_029"),
        new CoreUnlock("CORE_CS2_028", "Blizzard", "HERO_08", 7, 2, "CS2_028"),
        new CoreUnlock("CORE_BT_072", "Deep Freeze", "HERO_08", 7, 2, "BT_072"),
        new CoreUnlock("CORE_BOT_256", "Astromancer", "HERO_08", 8, 2, "BOT_256"),
        new CoreUnlock("CORE_CS2_032", "Flamestrike", "HERO_08", 8, 2, "CS2_032"),
        new CoreUnlock("CORE_DAL_575", "Khadgar", "HERO_08", 9, 1, "DAL_575"),
        new CoreUnlock("CORE_EX1_559", "Archmage Antonidas", "HERO_08", 10, 1, "EX1_559"),
        new CoreUnlock("CORE_ICC_038", "Righteous Protector", "HERO_04", 1, 2, "ICC_038"),
        new CoreUnlock("CORE_EX1_362", "Argent Protector", "HERO_04", 1, 2, "EX1_362"),
        new CoreUnlock("CORE_TRL_307", "Flash of Light", "HERO_04", 2, 2, "TRL_307"),
        new CoreUnlock("CORE_CFM_753", "Grimestreet Outfitter", "HERO_04", 2, 2, "CFM_753"),
        new CoreUnlock("CORE_BT_292", "Hand of A'dal", "HERO_04", 3, 2, "BT_292"),
        new CoreUnlock("TTN_851", "Resistance Aura", "HERO_04", 3, 2, "TTN_851"),
        new CoreUnlock("CORE_ONY_022", "Battle Vicar", "HERO_04", 4, 2, "ONY_022"),
        new CoreUnlock("CORE_EX1_619", "Equality", "HERO_04", 4, 2, "EX1_619"),
        new CoreUnlock("CORE_CS2_093", "Consecration", "HERO_04", 5, 2, "CS2_093"),
        new CoreUnlock("CORE_CS2_094", "Hammer of Wrath", "HERO_04", 5, 2, "CS2_094"),
        new CoreUnlock("CORE_KAR_077", "Silvermoon Portal", "HERO_04", 6, 2, "KAR_077"),
        new CoreUnlock("CORE_GVG_061", "Muster for Battle", "HERO_04", 6, 2, "GVG_061"),
        new CoreUnlock("CORE_GVG_059", "Coghammer", "HERO_04", 7, 2, "GVG_059"),
        new CoreUnlock("CORE_KAR_057", "Ivory Knight", "HERO_04", 7, 2, "KAR_057"),
        new CoreUnlock("CORE_BAR_878", "Veteran Warmedic", "HERO_04", 8, 2, "BAR_878"),
        new CoreUnlock("CORE_UNG_952", "Spikeridged Steed", "HERO_04", 8, 2, "UNG_952"),
        new CoreUnlock("CORE_TSC_076", "Immortalized in Stone", "HERO_04", 9, 2, "TSC_076"),
        new CoreUnlock("CORE_SW_047", "Highlord Fordragon", "HERO_04", 9, 1, "SW_047"),
        new CoreUnlock("CORE_EX1_383", "Tirion Fordring", "HERO_04", 10, 1, "EX1_383"),
        new CoreUnlock("CORE_RLK_814", "Crystalsmith Cultist", "HERO_09", 1, 2, "RLK_814"),
        new CoreUnlock("CORE_AT_055", "Flash Heal", "HERO_09", 1, 2, "AT_055"),
        new CoreUnlock("CORE_CS1_130", "Holy Smite", "HERO_09", 2, 2, "CS1_130"),
        new CoreUnlock("CORE_CS2_004", "Power Word: Shield", "HERO_09", 2, 2, "CS2_004"),
        new CoreUnlock("CORE_EX1_193", "Psychic Conjurer", "HERO_09", 3, 2, "EX1_193"),
        new CoreUnlock("CORE_ICC_210", "Shadow Ascendant", "HERO_09", 3, 2, "ICC_210"),
        new CoreUnlock("CORE_BAR_311", "Devouring Plague", "HERO_09", 4, 2, "BAR_311"),
        new CoreUnlock("CORE_CS1_112", "Holy Nova", "HERO_09", 4, 2, "CS1_112"),
        new CoreUnlock("CORE_SW_442", "Void Shard", "HERO_09", 5, 2, "SW_442"),
        new CoreUnlock("CORE_CFM_604", "Greater Healing Potion", "HERO_09", 6, 2, "CFM_604"),
        new CoreUnlock("CORE_EX1_197", "Shadow Word: Ruin", "HERO_09", 6, 2, "EX1_197"),
        new CoreUnlock("CORE_BAR_313", "Priest of An'she", "HERO_09", 7, 2, "BAR_313"),
        new CoreUnlock("CORE_SCH_512", "Initiation", "HERO_09", 7, 2, "SCH_512"),
        new CoreUnlock("CORE_BAR_310", "Lightshower Elemental", "HERO_09", 8, 2, "BAR_310"),
        new CoreUnlock("CORE_ICC_214", "Obsidian Statue", "HERO_09", 8, 2, "ICC_214"),
        new CoreUnlock("CORE_CATA_002", "Calia Menethil", "HERO_09", 9, 1, "CORE_CATA_002"),
        new CoreUnlock("CORE_EX1_198", "Natalie Seline", "HERO_09", 10, 1, "EX1_198"),
        new CoreUnlock("CORE_CS2_108", "Execute", "HERO_01", 1, 2, "CS2_108"),
        new CoreUnlock("CORE_WON_350", "I Know a Guy", "HERO_01", 1, 2, "WON_350"),
        new CoreUnlock("CORE_DRG_024", "Sky Raider", "HERO_01", 2, 2, "DRG_024"),
        new CoreUnlock("CORE_EX1_391", "Slam", "HERO_01", 2, 2, "EX1_391"),
        new CoreUnlock("CORE_REV_990", "Sanguine Depths", "HERO_01", 3, 2, "REV_990"),
        new CoreUnlock("CORE_AT_064", "Bash", "HERO_01", 3, 2, "AT_064"),
        new CoreUnlock("CORE_EX1_606", "Shield Block", "HERO_01", 4, 2, "EX1_606"),
        new CoreUnlock("CORE_LOOT_044", "Bladed Gauntlet", "HERO_01", 4, 2, "LOOT_044"),
        new CoreUnlock("CORE_NX2_028", "Hookfist-3000", "HERO_01", 5, 2, "NX2_028"),
        new CoreUnlock("CORE_OG_149", "Ravaging Ghoul", "HERO_01", 6, 2, "OG_149"),
        new CoreUnlock("CORE_EX1_604", "Frothing Berserker", "HERO_01", 6, 2, "EX1_604"),
        new CoreUnlock("CORE_BT_120", "Warmaul Challenger", "HERO_01", 7, 2, "BT_120"),
        new CoreUnlock("CORE_OG_218", "Bloodhoof Brave", "HERO_01", 7, 2, "OG_218"),
        new CoreUnlock("CORE_WW_329", "Detonation Juggernaut", "HERO_01", 8, 2, "WW_329"),
        new CoreUnlock("CORE_WON_337", "Ironforge Portal", "HERO_01", 8, 2, "WON_337"),
        new CoreUnlock("CORE_BT_781", "Bulwark of Azzinoth", "HERO_01", 9, 1, "BT_781"),
        new CoreUnlock("CORE_EX1_414", "Grommash Hellscream", "HERO_01", 10, 1, "EX1_414"),
        new CoreUnlock("CORE_EX1_169", "Innervate", "HERO_06", 1, 2, "EX1_169"),
        new CoreUnlock("CORE_AT_037", "Living Roots", "HERO_06", 1, 2, "AT_037"),
        new CoreUnlock("Core_LOE_115", "Raven Idol", "HERO_06", 2, 2, "LOE_115"),
        new CoreUnlock("CORE_SW_439", "Vibrant Squirrel", "HERO_06", 2, 2, "SW_439"),
        new CoreUnlock("CORE_EX1_160", "Power of the Wild", "HERO_06", 3, 2, "EX1_160"),
        new CoreUnlock("CORE_TRL_240", "Savage Striker", "HERO_06", 3, 2, "TRL_240"),
        new CoreUnlock("CORE_EX1_154", "Wrath", "HERO_06", 4, 2, "EX1_154"),
        new CoreUnlock("CORE_CS2_009", "Mark of the Wild", "HERO_06", 4, 2, "CS2_009"),
        new CoreUnlock("CORE_ULD_133", "Crystal Merchant", "HERO_06", 5, 2, "ULD_133"),
        new CoreUnlock("CORE_OG_047", "Feral Rage", "HERO_06", 6, 2, "OG_047"),
        new CoreUnlock("CORE_LOOT_309", "Oaken Summons", "HERO_06", 6, 2, "LOOT_309"),
        new CoreUnlock("CORE_TSC_650", "Flipper Friends", "HERO_06", 7, 2, "TSC_650"),
        new CoreUnlock("CORE_ONY_018", "Boomkin", "HERO_06", 7, 2, "ONY_018"),
        new CoreUnlock("CORE_SW_429", "Best in Shell", "HERO_06", 8, 2, "SW_429"),
        new CoreUnlock("CORE_RLK_657", "Underking", "HERO_06", 8, 2, "RLK_657"),
        new CoreUnlock("CORE_OG_044", "Fandral Staghelm", "HERO_06", 9, 1, "OG_044"),
        new CoreUnlock("CORE_CATA_006", "Ulfar", "HERO_06", 10, 1, "CORE_CATA_006"),
        new CoreUnlock("CORE_UNG_912", "Jeweled Macaw", "HERO_05", 1, 2, "UNG_912"),
        new CoreUnlock("CORE_BAR_801", "Wound Prey", "HERO_05", 1, 2, "BAR_801"),
        new CoreUnlock("CORE_DS1_185", "Arcane Shot", "HERO_05", 2, 2, "DS1_185"),
        new CoreUnlock("CORE_DS1_184", "Tracking", "HERO_05", 2, 2, "DS1_184"),
        new CoreUnlock("CORE_EX1_610", "Explosive Trap", "HERO_05", 3, 2, "EX1_610"),
        new CoreUnlock("CORE_EX1_611", "Freezing Trap", "HERO_05", 3, 2, "EX1_611"),
        new CoreUnlock("CORE_TRL_111", "Headhunter's Hatchet", "HERO_05", 4, 2, "TRL_111"),
        new CoreUnlock("CORE_ULD_152", "Pressure Plate", "HERO_05", 4, 2, "ULD_152"),
        new CoreUnlock("CORE_BRM_013", "Quick Shot", "HERO_05", 5, 2, "BRM_013"),
        new CoreUnlock("CORE_GIL_577", "Rat Trap", "HERO_05", 6, 2, "GIL_577"),
        new CoreUnlock("CORE_NEW1_031", "Animal Companion", "HERO_05", 6, 2, "NEW1_031"),
        new CoreUnlock("CORE_AT_062", "Ball of Spiders", "HERO_05", 7, 2, "AT_062"),
        new CoreUnlock("CORE_BT_201", "Augmented Porcupine", "HERO_05", 7, 2, "BT_201"),
        new CoreUnlock("CORE_AV_337", "Mountain Bear", "HERO_05", 8, 2, "AV_337"),
        new CoreUnlock("CORE_OG_211", "Call of the Wild", "HERO_05", 8, 2, "OG_211"),
        new CoreUnlock("CORE_DRG_256", "Dragonbane", "HERO_05", 9, 1, "DRG_256"),
        new CoreUnlock("CORE_TRL_900", "Halazzi, the Lynx", "HERO_05", 10, 1, "TRL_900"),
        new CoreUnlock("CORE_CS2_072", "Backstab", "HERO_03", 1, 2, "CS2_072"),
        new CoreUnlock("CORE_EX1_145", "Preparation", "HERO_03", 1, 2, "EX1_145"),
        new CoreUnlock("CORE_CS2_074", "Deadly Poison", "HERO_03", 2, 2, "CS2_074"),
        new CoreUnlock("CORE_BT_701", "Spymistress", "HERO_03", 2, 2, "BT_701"),
        new CoreUnlock("CORE_KAR_069", "Swashburglar", "HERO_03", 3, 2, "KAR_069"),
        new CoreUnlock("CORE_EX1_131", "Defias Ringleader", "HERO_03", 3, 2, "EX1_131"),
        new CoreUnlock("CORE_EX1_129", "Fan of Knives", "HERO_03", 4, 2, "EX1_129"),
        new CoreUnlock("CORE_DMF_511", "Foxy Fraud", "HERO_03", 4, 2, "DMF_511"),
        new CoreUnlock("CORE_TID_931", "Jackpot!", "HERO_03", 5, 2, "TID_931"),
        new CoreUnlock("CORE_EX1_278", "Shiv", "HERO_03", 6, 2, "EX1_278"),
        new CoreUnlock("CORE_EX1_134", "SI:7 Agent", "HERO_03", 6, 2, "EX1_134"),
        new CoreUnlock("CORE_CS2_076", "Assassinate", "HERO_03", 7, 2, "CS2_076"),
        new CoreUnlock("CORE_ULD_280", "Sahket Sapper", "HERO_03", 7, 2, "ULD_280"),
        new CoreUnlock("CORE_DAL_720", "Waggle Pick", "HERO_03", 8, 2, "DAL_720"),
        new CoreUnlock("CORE_BOT_576", "Crazed Chemist", "HERO_03", 8, 2, "BOT_576"),
        new CoreUnlock("CORE_RLK_567", "Shadow of Demise", "HERO_03", 9, 1, "RLK_567"),
        new CoreUnlock("CORE_CFM_781", "Shaku, the Collector", "HERO_03", 10, 1, "CFM_781"),
        new CoreUnlock("CORE_GIL_531", "Witch's Apprentice", "HERO_02", 1, 2, "GIL_531"),
        new CoreUnlock("CORE_EX1_238", "Lightning Bolt", "HERO_02", 1, 2, "EX1_238"),
        new CoreUnlock("CS3_007", "Novice Zapper", "HERO_02", 2, 2, "CS3_007"),
        new CoreUnlock("CORE_WC_042", "Wailing Vapor", "HERO_02", 2, 2, "WC_042"),
        new CoreUnlock("CORE_GIL_836", "Blazing Invocation", "HERO_02", 3, 2, "GIL_836"),
        new CoreUnlock("CORE_BOT_451", "Voltaic Burst", "HERO_02", 3, 2, "BOT_451"),
        new CoreUnlock("CORE_AT_052", "Totem Golem", "HERO_02", 4, 2, "AT_052"),
        new CoreUnlock("CORE_LOOT_373", "Healing Rain", "HERO_02", 4, 2, "LOOT_373"),
        new CoreUnlock("CORE_EX1_246", "Hex", "HERO_02", 5, 2, "EX1_246"),
        new CoreUnlock("CORE_EX1_259", "Lightning Storm", "HERO_02", 6, 2, "EX1_259"),
        new CoreUnlock("CORE_CS2_053", "Far Sight", "HERO_02", 6, 2, "CS2_053"),
        new CoreUnlock("CORE_EX1_250", "Earth Elemental", "HERO_02", 7, 2, "EX1_250"),
        new CoreUnlock("CORE_OG_031", "Hammer of Twilight", "HERO_02", 7, 2, "OG_031"),
        new CoreUnlock("CORE_CS2_042", "Fire Elemental", "HERO_02", 8, 2, "CS2_042"),
        new CoreUnlock("CORE_AV_107", "Glaciate", "HERO_02", 8, 2, "AV_107"),
        new CoreUnlock("CORE_CATA_004", "Rehgar Earthfury", "HERO_02", 9, 1, "CORE_CATA_004"),
        new CoreUnlock("CORE_TRL_345", "Krag'wa, the Frog", "HERO_02", 10, 1, "TRL_345"),
        new CoreUnlock("CORE_YOD_026", "Fiendish Servant", "HERO_07", 1, 2, "YOD_026"),
        new CoreUnlock("CORE_EX1_319", "Flame Imp", "HERO_07", 1, 2, "EX1_319"),
        new CoreUnlock("CORE_EX1_302", "Mortal Coil", "HERO_07", 2, 2, "EX1_302"),
        new CoreUnlock("CORE_BOT_222", "Spirit Bomb", "HERO_07", 2, 2, "BOT_222"),
        new CoreUnlock("CORE_WON_096", "Dark Peddler", "HERO_07", 3, 2, "WON_096"),
        new CoreUnlock("CORE_ICC_055", "Drain Soul", "HERO_07", 3, 2, "ICC_055"),
        new CoreUnlock("CORE_LOOT_013", "Vulgar Homunculus", "HERO_07", 4, 2, "LOOT_013"),
        new CoreUnlock("CORE_ICC_407", "Gnomeferatu", "HERO_07", 4, 2, "ICC_407"),
        new CoreUnlock("CORE_CS2_062", "Hellfire", "HERO_07", 5, 2, "CS2_062"),
        new CoreUnlock("CORE_SW_088", "Demonic Assault", "HERO_07", 6, 2, "SW_088"),
        new CoreUnlock("CORE_EX1_309", "Siphon Soul", "HERO_07", 6, 2, "EX1_309"),
        new CoreUnlock("CORE_EX1_310", "Doomguard", "HERO_07", 7, 2, "EX1_310"),
        new CoreUnlock("CORE_ULD_165", "Riftcleaver", "HERO_07", 7, 2, "ULD_165"),
        new CoreUnlock("CORE_EX1_312", "Twisting Nether", "HERO_07", 8, 2, "EX1_312"),
        new CoreUnlock("CORE_LOOT_368", "Voidlord", "HERO_07", 8, 2, "LOOT_368"),
        new CoreUnlock("CORE_SCH_181", "Archwitch Willow", "HERO_07", 9, 1, "SCH_181"),
        new CoreUnlock("CORE_EX1_323", "Lord Jaraxxus", "HERO_07", 10, 1, "EX1_323"),
        new CoreUnlock("CORE_BT_351", "Battlefiend", "HERO_10", 1, 2, "BT_351"),
        new CoreUnlock("CORE_BT_480", "Crimson Sigil Runner", "HERO_10", 1, 2, "BT_480"),
        new CoreUnlock("CORE_YOP_001", "Illidari Studies", "HERO_10", 2, 2, "YOP_001"),
        new CoreUnlock("CORE_BT_035", "Chaos Strike", "HERO_10", 2, 2, "BT_035"),
        new CoreUnlock("CORE_BT_321", "Netherwalker", "HERO_10", 3, 2, "BT_321"),
        new CoreUnlock("CORE_BT_491", "Spectral Sight", "HERO_10", 3, 2, "BT_491"),
        new CoreUnlock("CORE_BT_921", "Aldrachi Warblades", "HERO_10", 4, 2, "BT_921"),
        new CoreUnlock("CORE_WC_701", "Felrattler", "HERO_10", 4, 2, "WC_701"),
        new CoreUnlock("CORE_BT_801", "Eye Beam", "HERO_10", 5, 2, "BT_801"),
        new CoreUnlock("CORE_TTN_843", "Eredar Deceptor", "HERO_10", 6, 2, "TTN_843"),
        new CoreUnlock("CORE_BT_416", "Raging Felscreamer", "HERO_10", 6, 2, "BT_416"),
        new CoreUnlock("CORE_BT_510", "Wrathspike Brute", "HERO_10", 7, 2, "BT_510"),
        new CoreUnlock("CORE_BT_493", "Priestess of Fury", "HERO_10", 7, 2, "BT_493"),
        new CoreUnlock("CORE_TTN_866", "Mythical Terror", "HERO_10", 8, 2, "TTN_866"),
        new CoreUnlock("CS3_020", "Illidari Inquisitor", "HERO_10", 8, 2, "CS3_020"),
        new CoreUnlock("CORE_BT_187", "Kayn Sunfury", "HERO_10", 9, 1, "BT_187"),
        new CoreUnlock("CORE_CATA_001", "Tichondrius", "HERO_10", 10, 1, "CORE_CATA_001"),
        new CoreUnlock("RLK_503", "Body Bagger", "HERO_11", 1, 2, "RLK_503"),
        new CoreUnlock("RLK_958", "Skeletal Sidekick", "HERO_11", 1, 2, "RLK_958"),
        new CoreUnlock("RLK_061", "Battlefield Necromancer", "HERO_11", 1, 2, "RLK_061"),
        new CoreUnlock("CORE_CATA_009", "Death's Advance", "HERO_11", 1, 2, "CORE_CATA_009"),
        new CoreUnlock("RLK_025", "Frost Strike", "HERO_11", 2, 2, "RLK_025"),
        new CoreUnlock("RLK_511", "Harbinger of Winter", "HERO_11", 2, 2, "RLK_511"),
        new CoreUnlock("CORE_RLK_116", "Necrotic Mortician", "HERO_11", 2, 2, "RLK_116"),
        new CoreUnlock("CORE_RLK_712", "Blood Tap", "HERO_11", 2, 2, "RLK_712"),
        new CoreUnlock("CORE_RLK_083", "Deathchiller", "HERO_11", 3, 2, "RLK_083"),
        new CoreUnlock("CORE_RLK_066", "Hematurge", "HERO_11", 3, 2, "RLK_066"),
        new CoreUnlock("CORE_EDR_002", "Poison Breath", "HERO_11", 3, 2, "CORE_EDR_002"),
        new CoreUnlock("CORE_RLK_121", "Acolyte of Death", "HERO_11", 4, 2, "RLK_121"),
        new CoreUnlock("CORE_RLK_087", "Asphyxiate", "HERO_11", 4, 2, "RLK_087"),
        new CoreUnlock("RLK_708", "Chillfallen Baron", "HERO_11", 4, 2, "RLK_708"),
        new CoreUnlock("RLK_048", "Anti-Magic Shell", "HERO_11", 4, 2, "RLK_048"),
        new CoreUnlock("CORE_ETC_523", "Death Metal Knight", "HERO_11", 5, 2, "ETC_523"),
        new CoreUnlock("CORE_WW_374", "Corpse Farm", "HERO_11", 5, 2, "WW_374"),
        new CoreUnlock("RLK_024", "Death Strike", "HERO_11", 5, 2, "RLK_024"),
        new CoreUnlock("CORE_RLK_745", "Malignant Horror", "HERO_11", 5, 2, "RLK_745"),
        new CoreUnlock("CORE_RLK_062", "Nerubian Swarmguard", "HERO_11", 6, 2, "RLK_062"),
        new CoreUnlock("RLK_709", "Remorseless Winter", "HERO_11", 6, 2, "RLK_709"),
        new CoreUnlock("CORE_CATA_007", "Consumption", "HERO_11", 6, 2, "CORE_CATA_007"),
        new CoreUnlock("CORE_RLK_118", "Tomb Guardians", "HERO_11", 7, 2, "RLK_118"),
        new CoreUnlock("RLK_707", "Grave Strength", "HERO_11", 7, 2, "RLK_707"),
        new CoreUnlock("RLK_060", "Army of the Dead", "HERO_11", 7, 2, "RLK_060"),
        new CoreUnlock("RLK_067", "Corrupted Ashbringer", "HERO_11", 7, 2, "RLK_067"),
        new CoreUnlock("RLK_720", "Gnome Muncher", "HERO_11", 8, 2, "RLK_720"),
        new CoreUnlock("CORE_RLK_505", "Marrow Manipulator", "HERO_11", 8, 2, "RLK_505"),
        new CoreUnlock("CORE_RLK_063", "Frostwyrm's Fury", "HERO_11", 8, 2, "RLK_063"),
        new CoreUnlock("CORE_RLK_506", "Boneguard Commander", "HERO_11", 8, 2, "RLK_506"),
        new CoreUnlock("CORE_EDR_003", "Falric", "HERO_11", 9, 1, "CORE_EDR_003"),
        new CoreUnlock("RLK_223", "Thassarian", "HERO_11", 9, 1, "RLK_223"),
        new CoreUnlock("CORE_RLK_086", "Frostmourne", "HERO_11", 9, 1, "RLK_086"),
        new CoreUnlock("CORE_RLK_706", "Alexandros Mograine", "HERO_11", 10, 1, "RLK_706"),
        new CoreUnlock("CORE_CS2_188", "Abusive Sergeant", "", 1, 2, "CS2_188"),
        new CoreUnlock("CORE_ULD_191", "Beaming Sidekick", "", 1, 2, "ULD_191"),
        new CoreUnlock("CORE_CS2_189", "Elven Archer", "", 1, 2, "CS2_189"),
        new CoreUnlock("CORE_UNG_809", "Fire Fly", "", 2, 2, "UNG_809"),
        new CoreUnlock("CORE_UNG_205", "Glacial Shard", "", 2, 2, "UNG_205"),
        new CoreUnlock("CORE_ULD_723", "Murmy", "", 3, 2, "ULD_723"),
        new CoreUnlock("CORE_GIL_558", "Swamp Leech", "", 3, 2, "GIL_558"),
        new CoreUnlock("CORE_EX1_011", "Voodoo Doctor", "", 4, 2, "EX1_011"),
        new CoreUnlock("CORE_EX1_010", "Worgen Infiltrator", "", 4, 2, "EX1_010"),
        new CoreUnlock("CORE_EX1_509", "Murloc Tidecaller", "", 5, 2, "EX1_509"),
        new CoreUnlock("CORE_WON_351", "Small-Time Buccaneer", "", 5, 2, "WON_351"),
        new CoreUnlock("CORE_GVG_085", "Annoy-o-Tron", "", 6, 2, "GVG_085"),
        new CoreUnlock("CORE_NEW1_018", "Bloodsail Raider", "", 6, 2, "NEW1_018"),
        new CoreUnlock("CORE_EX1_162", "Dire Wolf Alpha", "", 7, 2, "EX1_162"),
        new CoreUnlock("CORE_NEW1_023", "Faerie Dragon", "", 7, 2, "NEW1_023"),
        new CoreUnlock("CORE_BT_156", "Imprisoned Vilefiend", "", 8, 2, "BT_156"),
        new CoreUnlock("CORE_ULD_271", "Injured Tol'vir", "", 8, 2, "ULD_271"),
        new CoreUnlock("CORE_EX1_096", "Loot Hoarder", "", 9, 2, "EX1_096"),
        new CoreUnlock("CORE_EX1_082", "Mad Bomber", "", 9, 2, "EX1_082"),
        new CoreUnlock("CORE_REV_308", "Maze Guide", "", 10, 2, "REV_308"),
        new CoreUnlock("CORE_GVG_103", "Micro Machine", "", 10, 2, "GVG_103"),
        new CoreUnlock("CORE_EX1_506", "Murloc Tidehunter", "", 10, 2, "EX1_506"),
        new CoreUnlock("CORE_KAR_062", "Netherspite Historian", "", 11, 2, "KAR_062"),
        new CoreUnlock("CORE_LOOT_413", "Plated Beetle", "", 11, 2, "LOOT_413"),
        new CoreUnlock("CORE_DMF_067", "Prize Vendor", "", 12, 2, "DMF_067"),
        new CoreUnlock("CS3_038", "Redgill Razorjaw", "", 12, 2, "CS3_038"),
        new CoreUnlock("CORE_EX1_059", "Crazed Alchemist", "", 13, 2, "EX1_059"),
        new CoreUnlock("CORE_SCH_713", "Cult Neophyte", "", 13, 2, "SCH_713"),
        new CoreUnlock("CS3_022", "Fogsail Freebooter", "", 14, 2, "CS3_022"),
        new CoreUnlock("CORE_EX1_058", "Sunfury Protector", "", 14, 2, "EX1_058"),
        new CoreUnlock("CORE_NEW1_020", "Wild Pyromancer", "", 15, 2, "NEW1_020"),
        new CoreUnlock("CORE_CFM_790", "Dirty Rat", "", 15, 2, "CFM_790"),
        new CoreUnlock("CORE_NEW1_021", "Doomsayer", "", 16, 2, "NEW1_021"),
        new CoreUnlock("CORE_EX1_007", "Acolyte of Pain", "", 16, 2, "EX1_007"),
        new CoreUnlock("CORE_LOE_039", "Gorillabot A-3", "", 17, 2, "LOE_039"),
        new CoreUnlock("CORE_GIL_534", "Hench-Clan Thug", "", 17, 2, "GIL_534"),
        new CoreUnlock("CORE_WON_141", "Menagerie Mug", "", 18, 2, "WON_141"),
        new CoreUnlock("CORE_CS2_122", "Raid Leader", "", 18, 2, "CS2_122"),
        new CoreUnlock("CORE_SW_072", "Rustrot Viper", "", 19, 2, "SW_072"),
        new CoreUnlock("CORE_UNG_928", "Tar Creeper", "", 19, 2, "UNG_928"),
        new CoreUnlock("CORE_EX1_103", "Coldlight Seer", "", 20, 2, "EX1_103"),
        new CoreUnlock("Core_UNG_072", "Stonehill Defender", "", 20, 2, "UNG_072"),
        new CoreUnlock("CORE_DRG_403", "Blowtorch Saboteur", "", 20, 2, "DRG_403"),
        new CoreUnlock("CORE_REV_023", "Demolition Renovator", "", 21, 2, "REV_023"),
        new CoreUnlock("CORE_EX1_507", "Murloc Warleader", "", 21, 2, "EX1_507"),
        new CoreUnlock("CORE_EDR_004_2026", "Raptor Herald", "", 22, 2, "CORE_EDR_004"),
        new CoreUnlock("CORE_NEW1_027", "Southsea Captain", "", 22, 2, "NEW1_027"),
        new CoreUnlock("CORE_NEW1_022", "Dread Corsair", "", 23, 2, "NEW1_022"),
        new CoreUnlock("CORE_UNG_084", "Fire Plume Phoenix", "", 23, 2, "UNG_084"),
        new CoreUnlock("CORE_ETC_111", "Merch Seller", "", 24, 2, "ETC_111"),
        new CoreUnlock("CORE_SW_066", "Royal Librarian", "", 24, 2, "SW_066"),
        new CoreUnlock("CORE_CS2_179", "Sen'jin Shieldmasta", "", 25, 2, "CS2_179"),
        new CoreUnlock("CORE_GIL_622", "Lifedrinker", "", 25, 2, "GIL_622"),
        new CoreUnlock("CORE_EX1_043", "Twilight Drake", "", 26, 2, "EX1_043"),
        new CoreUnlock("CORE_EX1_005", "Big Game Hunter", "", 26, 2, "EX1_005"),
        new CoreUnlock("CORE_SCH_605", "Lake Thresher", "", 27, 2, "SCH_605"),
        new CoreUnlock("CORE_EX1_028", "Stranglethorn Tiger", "", 27, 2, "EX1_028"),
        new CoreUnlock("CORE_REV_946", "Steamcleaner", "", 28, 2, "REV_946"),
        new CoreUnlock("CORE_GIL_623", "Witchwood Grizzly", "", 28, 2, "GIL_623"),
        new CoreUnlock("Core_CS2_200", "Boulderfist Ogre", "", 29, 2, "CS2_200"),
        new CoreUnlock("CORE_DRG_079", "Evasive Wyrm", "", 29, 2, "DRG_079"),
        new CoreUnlock("CORE_CS2_222", "Stormwind Champion", "", 30, 2, "CS2_222"),
        new CoreUnlock("CORE_SW_068", "Mo'arg Forgefiend", "", 30, 2, "SW_068"),
        new CoreUnlock("CORE_UNG_848", "Primordial Drake", "", 30, 2, "UNG_848"),
        new CoreUnlock("CORE_LOOT_137", "Sleepy Dragon", "", 31, 2, "LOOT_137"),
        new CoreUnlock("CORE_YOP_034", "Runaway Blackwing", "", 31, 2, "YOP_034"),
        new CoreUnlock("CORE_EX1_012", "Bloodmage Thalnos", "", 32, 1, "EX1_012"),
        new CoreUnlock("CORE_EX1_189", "Brightwing", "", 32, 1, "EX1_189"),
        new CoreUnlock("CORE_EX1_100", "Lorewalker Cho", "", 33, 1, "EX1_100"),
        new CoreUnlock("CORE_EX1_014", "King Mukla", "", 33, 1, "EX1_014"),
        new CoreUnlock("CORE_EX1_002", "The Black Knight", "", 34, 1, "EX1_002"),
        new CoreUnlock("CORE_CFM_344", "Finja, the Flying Star", "", 34, 1, "CFM_344"),
        new CoreUnlock("CS3_025", "Overlord Runthak", "", 35, 1, "CS3_025"),
        new CoreUnlock("CS3_024", "Taelan Fordring", "", 35, 1, "CS3_024"),
        new CoreUnlock("CORE_KAR_061", "The Curator", "", 36, 1, "KAR_061"),
        new CoreUnlock("CORE_EX1_110", "Cairne Bloodhoof", "", 36, 1, "EX1_110"),
        new CoreUnlock("CORE_AT_123", "Chillmaw", "", 37, 1, "AT_123"),
        new CoreUnlock("CORE_SCH_717", "Keymaster Alabaster", "", 37, 1, "SCH_717"),
        new CoreUnlock("CS3_035", "Nozdormu the Eternal", "", 38, 1, "CS3_035"),
        new CoreUnlock("CORE_ULD_178", "Siamat", "", 38, 1, "ULD_178"),
        new CoreUnlock("CORE_GVG_114", "Sneed's Old Shredder", "", 39, 1, "GVG_114"),
        new CoreUnlock("CORE_WON_145", "Avatar of Hearthstone", "", 39, 1, "WON_145"),
        new CoreUnlock("CORE_CFM_670", "Mayor Noggenfogger", "", 40, 1, "CFM_670"),
    };
    // END GENERATED CORE UNLOCKS

    internal static readonly IReadOnlyDictionary<string, CoreUnlock> ById =
        new ReadOnlyDictionary<string, CoreUnlock>(Unlocks.ToDictionary(card => card.Id, StringComparer.Ordinal));
    internal static readonly IReadOnlyList<EngineCard> AvailableCards = Array.AsReadOnly(Cards.All
        .Where(card => ById.ContainsKey(card.Id) && card.Implemented && card.Collectible)
#if ENGINE_SOURCE
        .Where(OfflinePoolPolicy.Load().AllowsCard)
#endif
        .OrderBy(card => card.Id, StringComparer.Ordinal).ToArray());
    private static readonly HashSet<string> AvailableIds = new HashSet<string>(AvailableCards.Select(card => card.Id), StringComparer.Ordinal);

    internal static bool IsAvailable(string id) => id != null && AvailableIds.Contains(id);
    internal static int Level(int xp) => 1 + LevelThresholds.Skip(1).Count(threshold => xp >= threshold);
    internal static int Xp(string result, bool conceded) => conceded ? ConcededXp
        : result == "Won" ? WinXp : result == "Lost" ? LossXp : result == "Tied" ? TieXp : AbandonedXp;
    internal static string ClassName(string hero) => hero == "HERO_08" ? "Mage" : hero == "HERO_04" ? "Paladin"
        : hero == "HERO_09" ? "Priest" : hero == "HERO_01" ? "Warrior"
        : hero == "HERO_06" ? "Druid" : hero == "HERO_05" ? "Hunter" : hero == "HERO_03" ? "Rogue"
        : hero == "HERO_02" ? "Shaman" : hero == "HERO_07" ? "Warlock" : hero == "HERO_11" ? "Death Knight" : hero == "HERO_10" ? "Demon Hunter" : "Unknown";

    internal static Dictionary<string, int> Owned(IReadOnlyDictionary<string, int> xp, bool availableOnly = true)
    {
        int combined = NeutralBaseCombinedLevel + Heroes.Sum(hero => Level(xp.TryGetValue(hero, out int amount) ? amount : 0) - 1);
        return Unlocks.Where(card => (!availableOnly || AvailableIds.Contains(card.Id))
            && (card.Hero.Length == 0 ? combined : Level(xp.TryGetValue(card.Hero, out int amount) ? amount : 0)) >= card.Threshold)
            .ToDictionary(card => card.Id, card => card.Copies, StringComparer.Ordinal);
    }
}
