using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

internal enum PackCompletionDisposition { Passthrough, Ignore, Fail, Complete }
internal enum NativeDeckSaveDisposition { Passthrough, Ignore, Save, Reject }

// Native UI boundary helpers shared with standalone checks.
internal static class NativeUIProjection
{
    internal static IEnumerable<string> CosmeticHeroes(LocalProfile profile) =>
        LocalDecks.PlayerHeroes.Concat(profile.OwnedHeroSkins).Distinct(StringComparer.Ordinal);

    internal static IEnumerable<string> CosmeticHeroPool => LocalDecks.PlayerHeroes.Concat(
        LocalShop.Default.HeroSkinIds);

    internal static string FavoriteHero(LocalProfile profile, string baseHero) => profile?.FavoriteHero(baseHero) ?? baseHero;

    // Native shows the golden hero when the vanilla hero is chosen and a golden copy is owned
    // (CollectionManager.GetHeroPremium); alternate skins have no premium version. Matches use the same helper.
    internal static int FavoritePremium(LocalProfile profile, string baseHero, string heroCardId) =>
        profile?.HeroPremium(baseHero, heroCardId) ?? 0;

    // Native total per card across versions. Profile checks that were Normal-only compare against this when
    // the question is "owned at all" and against OwnedCount(id, premium) when it is per version.
    internal static Dictionary<string, int> TotalOwned(LocalProfile profile) => profile.OwnedCards
        .GroupBy(card => card.Id, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Sum(card => card.Count), StringComparer.Ordinal);

    // Native collection rows: every owned (card, version) plus one Normal copy of each cosmetic hero.
    internal static IEnumerable<LocalCardCount> CollectionStacks(LocalProfile profile) => profile.OwnedCards
        .Concat(CosmeticHeroes(profile).Select(id => new LocalCardCount { Id = id, Count = 1 }));

    internal sealed class DeckRow
    {
        internal string Id; internal int Premium, Qty;
    }

    // One native deck-contents row per (card, version), in first-seen order.
    // Exact per-copy versions go to SavePremiumDraft; Normal-only decks keep the id path, which accepts missing (ghost) copies.
    internal static void SaveNativeDraft(LocalProfile profile, long id, string name, string hero,
        IReadOnlyList<LocalDeckCard> entries, IReadOnlyList<LocalSideboard> boards, long sortOrder, LocalDeckCosmetics cosmetics = null)
    {
        if (entries.Any(card => card.Premium != 0) || boards.Any(board => board.CardEntries.Any(card => card.Premium != 0)))
            profile.SavePremiumDraft(id, name, hero, entries, sortOrder, boards, cosmetics);
        else profile.SaveDraft(id, name, hero, entries.Select(card => card.Id).ToArray(), sortOrder, boards, cosmetics);
    }

    internal static List<DeckRow> DeckRows(IEnumerable<LocalDeckCard> entries)
    {
        var rows = new List<DeckRow>();
        foreach (var group in entries.GroupBy(entry => new { entry.Id, entry.Premium }))
            rows.Add(new DeckRow { Id = group.Key.Id, Premium = group.Key.Premium, Qty = group.Count() });
        return rows;
    }

    internal static void SelectCardBack(LocalProfile profile, int id, bool favorite)
    {
        if (!profile.OwnedCardBacks.Contains(id)) throw new InvalidOperationException("Card back is not owned.");
        profile.SetCardBackFavorite(id, favorite);
    }

    // Only the vanilla hero has a premium version, and only an owned one can be chosen. The profile stores the
    // hero card; FavoritePremium derives the version back from ownership, as native GetHeroPremium does.
    internal static void SelectHeroVersion(LocalProfile profile, string heroClass, string id, bool favorite, int premium)
    {
        if (premium != 0 && (premium != 1 || id != heroClass || profile.OwnedCount(id, 1) == 0))
            throw new InvalidOperationException("Hero version is not owned for this class.");
        SelectHeroSkin(profile, heroClass, id, favorite);
    }

    internal static void SelectHeroSkin(LocalProfile profile, string heroClass, string id, bool favorite)
    {
        if (!LocalDecks.PlayerHeroes.Contains(heroClass) || (id != heroClass
            && (!profile.OwnedHeroSkins.Contains(id) || !LocalShop.Default.IsHeroSkinForClass(heroClass, id))))
            throw new InvalidOperationException("Hero skin is not owned for this class.");
        if (favorite || FavoriteHero(profile, heroClass) == id)
            profile.SetFavoriteHeroSkin(heroClass, favorite ? id : heroClass);
    }

    internal static IEnumerable<int> DisplayableSetIds(IEnumerable<int> sets) => sets.Where(LocalCardCatalog.InActiveMetaSet).Distinct();
    // A window admission can belong to an otherwise inactive set (M.O.T.H.E.R. in BE).
    // Expose that set only for owned, admitted grants; card admission still governs its contents.
    internal static IEnumerable<int> DisplayableSetIds(IEnumerable<int> sets, IEnumerable<LocalCardCount> owned) =>
        DisplayableSetIds(sets).Concat(OwnedGrantOnlyIds(owned).Select(id => (int)SabberStoneCore.Model.Cards.FromId(id).Set)).Distinct();
    internal static IReadOnlyDictionary<string, int> CoreGrantCounts(IReadOnlyDictionary<string, int> owned,
        Func<string, int> nativeCount)
    {
        var grants = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var card in owned.Where(pair => LocalCoreProgression.ById.ContainsKey(pair.Key)))
        {
            int count = nativeCount(card.Key);
            if (count < 0 || count > card.Value) throw new InvalidOperationException("Native Core ownership exceeds profile.");
            if (count < card.Value) grants.Add(card.Key, card.Value - count);
        }
        return grants;
    }

    // Shop purchases grant Normal copies: compare the profile's Normal ledger with native's Normal stack only
    // (native's total also spans premium versions the shop never touches).
    internal static Dictionary<string, int> ShopGrantCounts(IReadOnlyDictionary<string, int> profileNormal,
        IEnumerable<string> cardIds, Func<string, int> nativeNormal)
    {
        var grants = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string id in cardIds.Distinct(StringComparer.Ordinal))
        {
            int delta = profileNormal[id] - nativeNormal(id);
            if (delta < 0) throw new InvalidOperationException("Native collection exceeds local shop ownership");
            if (delta > 0) grants.Add(id, delta);
        }
        return grants;
    }

    // Premium copies the profile owns beyond what native holds, per (card, version). Never negative: native copies
    // are not removed here. Heroes are excluded (RefreshCosmetics owns the golden hero).
    internal static List<(string Id, int Premium, int Count)> PremiumGrantDeltas(IEnumerable<LocalCardCount> owned,
        Func<string, int, int> nativeCount, Func<string, bool> excluded)
    {
        var deltas = new List<(string, int, int)>();
        foreach (var group in owned.Where(card => card.Premium > 0 && card.Count > 0 && !excluded(card.Id))
            .GroupBy(card => (card.Id, card.Premium)).OrderBy(group => group.Key.Id, StringComparer.Ordinal).ThenBy(group => group.Key.Premium))
        {
            int missing = group.Sum(card => card.Count) - nativeCount(group.Key.Id, group.Key.Premium);
            if (missing > 0) deltas.Add((group.Key.Id, group.Key.Premium, missing));
        }
        return deltas;
    }

    // Offline the client never receives Core's launch event timing, so it marks owned Core cards
    // unplayable. Only engine-supported Core unlocks bypass it; ownership rules still apply.
    internal static bool ForcesCorePlayability(bool active, string cardId) =>
        active && LocalCoreProgression.IsAvailable(cardId);

    // This bypasses unavailable native event timing; collection ownership is checked separately.
    private static readonly Lazy<HashSet<string>> testStandardIds = new Lazy<HashSet<string>>(() =>
        new HashSet<string>(LocalDecks.TestStandardCardIds, StringComparer.Ordinal));
    internal static bool ForcesTestPlayability(bool active, string cardId) =>
        active && cardId != null && (testStandardIds.Value.Contains(cardId) || LocalDecks.IsLegalDeckbuildingCard(cardId));

    internal static bool AcknowledgeAndRefreshCore(LocalProfile profile, string matchId, Action refresh, out Exception refreshError)
    {
        refreshError = null;
        bool acknowledged = profile.AcknowledgeRewardPresentation(matchId);
        try { refresh(); }
        catch (Exception error) { refreshError = error; }
        return acknowledged;
    }

    internal static int DeckCopyCount(IEnumerable<string> ids, string cardId) =>
        ids.Count(id => LocalCardCatalog.CopyGroup(id) == LocalCardCatalog.CopyGroup(cardId));

    // Retail caps the native deck tray at 27 decks (DeckTrayDeckListContent 27 slots + 2 spare sections, picker pages
    // are dynamic). SoloStone raises the tray to NativeDeckLimit (NativeDeckLimits transpiles the native literals).
    // CreateTraySections instantiates its TraySection prefab in a loop bounded only by the shifted section literal, and
    // DeckPickerTrayDisplay.GetDeckPageCount pages decks dynamically (9 per page), so the tray has no fixed ceiling of
    // its own: it holds every personal deck up to the personal cap plus every loaner, which never count against it.
    internal const int RetailDeckLimit = 27;
    internal const int MaximumPersonalDecks = LocalProfile.MaximumCustomDecks;
    // Most loaners a normal tray projects (the eleven Core starters; the vendor build pins eight).
    internal const int PlayerLoanerCapacity = 11;
    internal const int NativeDeckLimit = MaximumPersonalDecks + PlayerLoanerCapacity;
    // The native tray literals 25/26/27 (last/limit slots) and 29 (sections incl. 2 spare) shift by the same amount.
    internal static int TraySlots(int retail)
    {
        if (retail != 25 && retail != 26 && retail != RetailDeckLimit && retail != 29)
            throw new ArgumentOutOfRangeException(nameof(retail));
        return retail + NativeDeckLimit - RetailDeckLimit;
    }
    internal const long LegacyPersonalDeckId = 910000001L;
    internal const long FirstAllocatedPersonalDeckId = 910000010L;
    private static readonly string[] DeckKeys = { "custom", "mage", "warrior", "priest", "mage-frost", "warrior-control", "priest-combo", "paladin", "paladin-control"
#if ENGINE_SOURCE
        , LocalDecks.TestShowcaseId, LocalDecks.TestDecisionsId, LocalDecks.TestStandardBatch1Id,
        "test-standard-batch-2-warrior", "test-standard-batch-2-priest", "test-standard-batch-2-mage", "test-standard-batch-2-paladin",
        "test-standard-batch-2-druid", "test-standard-batch-2-rogue", "test-standard-batch-2-warlock",
        "test-standard-batch-2-hunter", "test-standard-batch-2-shaman", "test-standard-batch-2-demonhunter", LocalDecks.TestRushSilenceId,
        "test-standard-extra-warrior", "test-standard-extra-priest", "test-standard-extra-druid", "test-standard-extra-hunter", "test-standard-extra-demonhunter", "test-standard-extra-rogue",
        "test-deathknight-rainbow", "test-deathknight-frost", "test-deathknight-unholy", "test-deathknight-blood", "test-deathknight-buu",
        "test-standard-more-warrior", "test-standard-more-priest", "test-standard-more-mage", "test-standard-more-paladin",
        "test-standard-more-druid", "test-standard-more-rogue", "test-standard-more-warlock", "test-standard-more-hunter",
        "test-standard-more-shaman", "test-standard-more-demonhunter",
        "test-standard-b10-warrior", "test-standard-b10-priest", "test-standard-b10-mage", "test-standard-b10-paladin",
        "test-standard-b10-druid", "test-standard-b10-rogue", "test-standard-b10-warlock", "test-standard-b10-hunter",
        "test-standard-b10-shaman", "test-standard-b10-demonhunter", "test-standard-b10-deathknight",
        "test-standard-b10-deathknight-2",
        "test-standard-mecha-warrior", "test-standard-mecha-priest", "test-standard-mecha-mage", "test-standard-mecha-paladin",
        "test-standard-mecha-druid", "test-standard-mecha-rogue", "test-standard-mecha-warlock", "test-standard-mecha-hunter",
        "test-standard-mecha-shaman", "test-standard-mecha-demonhunter", "test-standard-mecha-deathknight", "test-standard-mecha-deathknight-2",
        "test-standard-rewind-rogue", "test-standard-rewind-mage", "test-standard-rewind-warlock", "test-standard-rewind-shaman", "test-standard-rewind-paladin",
        "test-standard-rewind-druid", "test-standard-rewind-warrior", "test-standard-rewind-priest", "test-standard-rewind-demonhunter", "test-standard-rewind-hunter",
        "test-keywords-cfa-paladin", "test-keywords-cfa-hunter",
        "test-keywords-cfa-warrior", "test-keywords-cfa-shaman", "test-keywords-cfa-priest", "test-keywords-cfa-mage",
        "test-standard-cardstate-mage", "test-standard-cardstate-shaman", "test-standard-cardstate-warlock", "test-standard-cardstate-warrior",
        "test-standard-cardstate-druid", "test-standard-cardstate-hunter", "test-standard-cardstate-paladin", "test-standard-cardstate-priest",
        "test-standard-b11-warrior", "test-standard-b11-priest", "test-standard-b11-mage", "test-standard-b11-paladin",
        "test-standard-b11-druid", "test-standard-b11-rogue", "test-standard-b11-warlock", "test-standard-b11-hunter",
        "test-standard-b11-shaman",
        "showcase-rogue-imbue", "showcase-death-knight-dark-gift", "showcase-demon-hunter-outcast", "showcase-druid-quest-tokens", "showcase-hunter-kindred-beasts", "showcase-mage-leylines", "showcase-paladin-pure-holy", "showcase-priest-imbue", "showcase-shaman-herald", "showcase-warlock-dragon-shred", "showcase-warrior-armor-herald", "showcase-rogue-prepare-combo",
        "player-rogue-imbue", "player-warlock-dragon-shred", "player-warrior-armor-herald",
        "player-core-mage", "player-core-paladin", "player-core-priest", "player-core-warrior", "player-core-druid", "player-core-hunter", "player-core-rogue", "player-core-shaman", "player-core-warlock", "player-core-demonhunter", "player-core-deathknight",
        "test-standard-azalina-priest"
#endif
#if STANDARD_PROOF
        , "proof-mage", "proof-warrior"
#endif
    };
    // Native GameUtils.GetAllCollectibleCardIds: CollectionManager.InitImpl registers a Normal and a Golden collectible
    // entry for each id. Cards outside it exist natively only once owned (from the collection packet) and only in the
    // owned version, so they never list as missing and CanUpgradeCardToGolden answers NotUpgradable.
    internal static List<string> NativeCollectiblePool(IEnumerable<string> craftable, IEnumerable<LocalCardCount> owned = null) => Supported.Concat(CosmeticHeroPool)
        .Concat(craftable ?? Enumerable.Empty<string>()).Concat(OwnedGrantOnlyIds(owned))
        .Distinct(StringComparer.Ordinal).OrderBy(id => id).ToList();
    private static bool AdmittedGrantOnly(string id) => LocalWelcomeGift.HistoricalCardIds.Contains(id)
        && LocalProfile.KnownLedgerCard(id) && LocalCardCatalog.InActiveMeta(SabberStoneCore.Model.Cards.FromId(id));
    private static IEnumerable<string> OwnedGrantOnlyIds(IEnumerable<LocalCardCount> owned) =>
        (owned ?? Enumerable.Empty<LocalCardCount>()).Where(card => card.Count > 0 && AdmittedGrantOnly(card.Id)).Select(card => card.Id);
    // Deck-slot collectibility: native EvaluateSlot checks it before ownership, so narrowing it turns missing cards
    // (blue ghosts) into invalid ones. Keep the historical predicate and only add owned admitted grants.
    internal static bool IsNativeCollectible(string id, LocalProfile profile) =>
        Supported.Contains(id) || LocalDecks.TestStandardCardIds.Contains(id) || CosmeticHeroPool.Contains(id)
        || (AdmittedGrantOnly(id) && profile.TotalOwnedCount(id) > 0);
    internal static readonly HashSet<string> Supported = new HashSet<string>(LocalEconomy.Catalog.Select(c => c.Id)
#if STANDARD_PROOF
        .Concat(ProofDecks.Get("proof-mage").CardIds).Concat(ProofDecks.Get("proof-warrior").CardIds)
#endif
        , StringComparer.Ordinal);
    private static readonly Dictionary<string, SabberStoneCore.Model.Card> PreviewCards = LocalDecks.CustomCards
        .Concat(LocalDecks.TestStandardCardIds.Select(SabberStoneCore.Model.Cards.FromId))
#if ENGINE_SOURCE
        .Concat(SabberStoneCore.CardSets.Client.StandardSets.CollectibleIds.Select(SabberStoneCore.Model.Cards.FromId)
            .Where(card => card.Type == SabberStoneCore.Enums.CardType.LOCATION
                || SabberStoneCore.Model.QuestDefinition.TryGet(card.AssetId, out _)))
#endif
#if STANDARD_PROOF
        .Concat(StandardProofNative.DefinitionIds.Select(SabberStoneCore.Model.Cards.FromId))
#endif
        .Concat(LocalCardCatalog.CardStateGeneratedIds.Select(SabberStoneCore.Model.Cards.FromId))
        .Concat(LocalCardCatalog.SupplementalGeneratedIds.Select(SabberStoneCore.Model.Cards.FromId).Where(card => card != null))
        .Concat(LocalCardCatalog.LegacyGeneratedPublicIds.Select(SabberStoneCore.Model.Cards.FromId).Where(card => card != null))
        .GroupBy(card => card.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    private static readonly HashSet<SabberStoneCore.Enums.GameTag> DefinitionTags = new HashSet<SabberStoneCore.Enums.GameTag> {
        SabberStoneCore.Enums.GameTag.COLLECTIBLE, SabberStoneCore.Enums.GameTag.CLASS,
        SabberStoneCore.Enums.GameTag.CARDTYPE, SabberStoneCore.Enums.GameTag.RARITY,
        SabberStoneCore.Enums.GameTag.CARDRACE, SabberStoneCore.Enums.GameTag.ELITE,
        SabberStoneCore.Enums.GameTag.COST, (SabberStoneCore.Enums.GameTag)2837, SabberStoneCore.Enums.GameTag.ATK,
        SabberStoneCore.Enums.GameTag.HEALTH, SabberStoneCore.Enums.GameTag.DAMAGE, SabberStoneCore.Enums.GameTag.SPELLPOWER,
        SabberStoneCore.Enums.GameTag.AFFECTED_BY_SPELL_POWER, SabberStoneCore.Enums.GameTag.BATTLECRY,
        SabberStoneCore.Enums.GameTag.DEATHRATTLE, SabberStoneCore.Enums.GameTag.SECRET,
        SabberStoneCore.Enums.GameTag.TAUNT, SabberStoneCore.Enums.GameTag.CHARGE,
        SabberStoneCore.Enums.GameTag.RUSH, SabberStoneCore.Enums.GameTag.DIVINE_SHIELD,
        SabberStoneCore.Enums.GameTag.STEALTH, SabberStoneCore.Enums.GameTag.WINDFURY,
        SabberStoneCore.Enums.GameTag.POISONOUS, SabberStoneCore.Enums.GameTag.FREEZE,
        SabberStoneCore.Enums.GameTag.CANT_ATTACK, SabberStoneCore.Enums.GameTag.CANT_BE_TARGETED_BY_SPELLS,
        SabberStoneCore.Enums.GameTag.CANT_BE_TARGETED_BY_HERO_POWERS, SabberStoneCore.Enums.GameTag.AURA,
        SabberStoneCore.Enums.GameTag.ADJACENT_BUFF, SabberStoneCore.Enums.GameTag.ENRAGED,
        SabberStoneCore.Enums.GameTag.SILENCE,
        (SabberStoneCore.Enums.GameTag)2196, (SabberStoneCore.Enums.GameTag)2197,
        (SabberStoneCore.Enums.GameTag)2198, (SabberStoneCore.Enums.GameTag)4058
#if ENGINE_SOURCE
        , SabberStoneCore.Enums.GameTag.LOCATION_ACTION_COST, SabberStoneCore.Enums.GameTag.LOCATION_ACTION_COOLDOWN
#endif
#if STANDARD_PROOF
        , (SabberStoneCore.Enums.GameTag)1635, (SabberStoneCore.Enums.GameTag)1720,
        (SabberStoneCore.Enums.GameTag)1743, (SabberStoneCore.Enums.GameTag)2045
#endif
    };
    private static readonly HashSet<SabberStoneCore.Enums.GameTag> FlattenedDbfTags = new HashSet<SabberStoneCore.Enums.GameTag> {
        SabberStoneCore.Enums.GameTag.CHARGE, SabberStoneCore.Enums.GameTag.COUNTER,
        SabberStoneCore.Enums.GameTag.DIVINE_SHIELD, SabberStoneCore.Enums.GameTag.FREEZE,
        SabberStoneCore.Enums.GameTag.SECRET, SabberStoneCore.Enums.GameTag.SILENCE,
        SabberStoneCore.Enums.GameTag.SPELLPOWER, SabberStoneCore.Enums.GameTag.TAUNT,
        SabberStoneCore.Enums.GameTag.WINDFURY
    };
#if STANDARD_PROOF
    internal static bool UseOriginalProofDefinition(string id) => StandardProofNative.CollectibleIds.Contains(id);
#endif
    internal static bool SupportsPreview(string id) => id != null && PreviewCards.ContainsKey(id);
    internal static bool TryGetDefinitionTag(bool definitionContext, bool definitionsLoaded,
        bool canonicalDefinition, string id, int nativeTag, out int value)
    {
        value = 0;
        if (!definitionContext || !definitionsLoaded || !canonicalDefinition
            || id == null || !PreviewCards.TryGetValue(id, out var card)) return false;
        var engineTag = (SabberStoneCore.Enums.GameTag)nativeTag;
#if ENGINE_SOURCE
        if ((nativeTag == 2352 || nativeTag == 2353) && card.Type != SabberStoneCore.Enums.CardType.LOCATION) return false;
#endif
        if (!DefinitionTags.Contains(engineTag) && !IsModernQuestDefinitionTag(card, engineTag)) return false;
        if (engineTag == SabberStoneCore.Enums.GameTag.HEALTH && card.Type == SabberStoneCore.Enums.CardType.WEAPON)
            engineTag = SabberStoneCore.Enums.GameTag.DURABILITY;
        value = card[engineTag];
        return true;
    }
    internal static bool TryGetDbfTag(bool uiContext, string id, int nativeTag, out int value)
    {
        value = 0;
        if (!uiContext || id == null || !PreviewCards.TryGetValue(id, out var card)) return false;
        var engineTag = (SabberStoneCore.Enums.GameTag)nativeTag;
#if ENGINE_SOURCE
        if ((nativeTag == 2352 || nativeTag == 2353) && card.Type != SabberStoneCore.Enums.CardType.LOCATION) return false;
#endif
        if (!DefinitionTags.Contains(engineTag) && !FlattenedDbfTags.Contains(engineTag)
            && !IsModernQuestDefinitionTag(card, engineTag)) return false;
        if (engineTag == SabberStoneCore.Enums.GameTag.HEALTH && card.Type == SabberStoneCore.Enums.CardType.WEAPON)
            engineTag = SabberStoneCore.Enums.GameTag.DURABILITY;
        value = card[engineTag];
        if (FlattenedDbfTags.Contains(engineTag) && card.RefTags.TryGetValue(engineTag, out int referenced))
            value = Math.Max(value, referenced);
        return true;
    }
    internal static bool TryGetReferencedTag(bool context, string id, int nativeTag, out int value)
    {
        value = 0;
        var tag = (SabberStoneCore.Enums.GameTag)nativeTag;
        if (!context || !FlattenedDbfTags.Contains(tag) || id == null || !PreviewCards.TryGetValue(id, out var card)) return false;
        card.RefTags.TryGetValue(tag, out value);
        return true;
    }
    private static bool IsModernQuestDefinitionTag(SabberStoneCore.Model.Card card, SabberStoneCore.Enums.GameTag tag)
    {
#if ENGINE_SOURCE
        if (!SabberStoneCore.Model.QuestDefinition.TryGet(card.AssetId, out var quest)) return false;
        return tag == SabberStoneCore.Enums.GameTag.QUEST
            || tag == SabberStoneCore.Enums.GameTag.QUEST_PROGRESS_TOTAL
            || tag == SabberStoneCore.Enums.GameTag.QUEST_REWARD_DATABASE_ID
            || (tag == SabberStoneCore.Enums.GameTag.REPEATABLE && quest.Repeatable);
#else
        return false;
#endif
    }
    internal static bool TryGetAuditDefinitionTag(bool canonicalDefinition, string id,
        int nativeTag, out int value) => TryGetDefinitionTag(
        true, true, canonicalDefinition, id, nativeTag, out value);
    internal static bool DefinitionContext(bool active, bool auditingOriginal, bool sceneInitialized,
        bool uiContext, string screen) => active && !auditingOriginal && sceneInitialized
        && (uiContext || screen == "Starting" || screen == "Playing");
    internal static bool LiveFrameIsStable(bool playing, bool hasFront, bool frontBusy,
        bool processingPower, int queuedPower, int nativeTurn, int engineTurn) => playing && hasFront
        && !frontBusy && !processingPower && queuedPower == 0 && nativeTurn == engineTurn;
    internal static bool TransportTagMatches(bool nativePresent, int nativeValue,
        bool expectedPresent, int expectedValue) => nativePresent == expectedPresent
        && (!expectedPresent || nativeValue == expectedValue);
    internal static bool TestControlTransportTagMatches(int tag, SabberStoneCore.Enums.CardType type,
        bool nativePresent, int nativeValue, bool expectedPresent, int expectedValue) =>
        TransportTagMatches(nativePresent, nativeValue, expectedPresent, expectedValue)
        // OnHideEntity writes printed ATK/HEALTH/COST and DAMAGE=0, including on enchantments.
        || (nativeValue == 0 && expectedValue == 0
            && type == SabberStoneCore.Enums.CardType.ENCHANTMENT
            && (tag == (int)SabberStoneCore.Enums.GameTag.DAMAGE
                || tag == (int)SabberStoneCore.Enums.GameTag.ATK
                || tag == (int)SabberStoneCore.Enums.GameTag.HEALTH
                || tag == (int)SabberStoneCore.Enums.GameTag.COST));
    internal static bool DefinitionTagMatches(bool canonicalDefinition, int attachedValue,
        int expectedValue) => !canonicalDefinition || attachedValue == expectedValue;
    internal static bool TryResolveVisibleEngineEntity(string nativeCardId, int entityId,
        Func<int, SabberStoneCore.Model.Entities.IPlayable> lookup, Func<string, string> transportCardId,
        out SabberStoneCore.Model.Entities.IPlayable engineEntity)
    {
        engineEntity = null;
        if (!SupportsPreview(nativeCardId)) return false;
        var candidate = lookup(entityId);
        var card = candidate?.Card;
        if (card == null || !string.Equals(transportCardId(card.Id), nativeCardId, StringComparison.Ordinal)) return false;
        engineEntity = candidate;
        return true;
    }
    internal static long DeckId(string key)
    {
#if STANDARD_PROOF
        if (key == "proof-mage") return 900000001L;
        if (key == "proof-warrior") return 900000002L;
#endif
#if ENGINE_SOURCE
        // Test loaners reserve 900000010..900000039, DK band 900000040..900000049 and seat band
        // 900000050..900000079 and keyword band 900000130..149, card-state band 900000150..158, batch-11 band 900000170..179; personal IDs start at 910000010. Mechanic-families seats: 900000029..39 and 900000047; Azalina 20-card loaner: 900000049.
        if (key == "test-standard-azalina-priest") return 900000049L;
        if (key == "test-keywords-cfa-paladin") return 900000130L;
        if (key == "test-keywords-cfa-hunter") return 900000131L;
        if (key == "test-keywords-cfa-warrior") return 900000132L;
        if (key == "test-keywords-cfa-shaman") return 900000133L;
        if (key == "test-keywords-cfa-priest") return 900000134L;
        if (key == "test-keywords-cfa-mage") return 900000135L;
        if (key == "test-deathknight-rainbow") return 900000040L;
        if (key == "test-deathknight-frost") return 900000041L;
        if (key == "test-deathknight-unholy") return 900000042L;
        if (key == "test-deathknight-blood") return 900000043L;
        if (key == "test-deathknight-buu") return 900000044L;
        if (key == "test-standard-cardstate-druid") return 900000150L;
        if (key == "test-standard-cardstate-priest") return 900000154L;
        if (key == "test-standard-cardstate-mage") return 900000152L;
        if (key == "test-standard-cardstate-hunter") return 900000151L;
        if (key == "test-standard-cardstate-paladin") return 900000153L;
        if (key == "test-standard-cardstate-shaman") return 900000155L;
        if (key == "test-standard-cardstate-warlock") return 900000156L;
        if (key == "test-standard-cardstate-warrior") return 900000158L;
        if (key == "showcase-rogue-imbue") return 900000200L;
        if (key == "showcase-death-knight-dark-gift") return 900000201L;
        if (key == "showcase-demon-hunter-outcast") return 900000202L;
        if (key == "showcase-druid-quest-tokens") return 900000203L;
        if (key == "showcase-hunter-kindred-beasts") return 900000204L;
        if (key == "showcase-mage-leylines") return 900000205L;
        if (key == "showcase-paladin-pure-holy") return 900000206L;
        if (key == "showcase-priest-imbue") return 900000207L;
        if (key == "showcase-shaman-herald") return 900000208L;
        if (key == "showcase-warlock-dragon-shred") return 900000209L;
        if (key == "showcase-warrior-armor-herald") return 900000210L;
        if (key == "showcase-rogue-prepare-combo") return 900000211L;
        if (key == "player-rogue-imbue") return 900000212L;
        if (key == "player-warlock-dragon-shred") return 900000213L;
        if (key == "player-warrior-armor-herald") return 900000214L;
        // Core starter loaners (#57) reserve 900000215..900000225.
        if (key == "player-core-mage") return 900000215L;
        if (key == "player-core-paladin") return 900000216L;
        if (key == "player-core-priest") return 900000217L;
        if (key == "player-core-warrior") return 900000218L;
        if (key == "player-core-druid") return 900000219L;
        if (key == "player-core-hunter") return 900000220L;
        if (key == "player-core-rogue") return 900000221L;
        if (key == "player-core-shaman") return 900000222L;
        if (key == "player-core-warlock") return 900000223L;
        if (key == "player-core-demonhunter") return 900000224L;
        if (key == "player-core-deathknight") return 900000225L;
        if (key == LocalDecks.TestShowcaseId) return 900000048L;
        if (key == "test-standard-more-warrior") return 900000050L;
        if (key == "test-standard-more-priest") return 900000051L;
        if (key == "test-standard-more-mage") return 900000052L;
        if (key == "test-standard-more-paladin") return 900000053L;
        if (key == "test-standard-more-druid") return 900000054L;
        if (key == "test-standard-more-rogue") return 900000055L;
        if (key == "test-standard-more-warlock") return 900000056L;
        if (key == "test-standard-more-hunter") return 900000057L;
        if (key == "test-standard-more-shaman") return 900000058L;
        if (key == "test-standard-more-demonhunter") return 900000059L;
        // Batch-11 legacy Core seats reserve 900000170..900000179; 179 remains unassigned.
        if (key == "test-standard-b11-warrior") return 900000170L;
        if (key == "test-standard-b11-priest") return 900000171L;
        if (key == "test-standard-b11-mage") return 900000172L;
        if (key == "test-standard-b11-paladin") return 900000173L;
        if (key == "test-standard-b11-druid") return 900000174L;
        if (key == "test-standard-b11-rogue") return 900000175L;
        if (key == "test-standard-b11-warlock") return 900000176L;
        if (key == "test-standard-b11-hunter") return 900000177L;
        if (key == "test-standard-b11-shaman") return 900000178L;
        if (key == "test-standard-b10-warrior") return 900000060L;
        if (key == "test-standard-b10-priest") return 900000061L;
        if (key == "test-standard-b10-mage") return 900000062L;
        if (key == "test-standard-b10-paladin") return 900000063L;
        if (key == "test-standard-b10-druid") return 900000064L;
        if (key == "test-standard-b10-rogue") return 900000065L;
        if (key == "test-standard-b10-warlock") return 900000066L;
        if (key == "test-standard-b10-hunter") return 900000067L;
        if (key == "test-standard-b10-shaman") return 900000068L;
        if (key == "test-standard-b10-demonhunter") return 900000069L;
        if (key == "test-standard-b10-deathknight") return 900000045L;
        if (key == "test-standard-b10-deathknight-2") return 900000046L;
        if (key == "test-standard-mecha-warrior") return 900000029L;
        if (key == "test-standard-mecha-priest") return 900000030L;
        if (key == "test-standard-mecha-mage") return 900000031L;
        if (key == "test-standard-mecha-paladin") return 900000032L;
        if (key == "test-standard-mecha-druid") return 900000033L;
        if (key == "test-standard-mecha-rogue") return 900000034L;
        if (key == "test-standard-mecha-warlock") return 900000035L;
        if (key == "test-standard-mecha-hunter") return 900000036L;
        if (key == "test-standard-mecha-shaman") return 900000037L;
        if (key == "test-standard-mecha-demonhunter") return 900000038L;
        if (key == "test-standard-mecha-deathknight") return 900000039L;
        if (key == "test-standard-mecha-deathknight-2") return 900000047L;
        if (key == "test-standard-rewind-rogue") return 900000070L;
        if (key == "test-standard-rewind-mage") return 900000071L;
        if (key == "test-standard-rewind-warlock") return 900000072L;
        if (key == "test-standard-rewind-shaman") return 900000073L;
        if (key == "test-standard-rewind-paladin") return 900000074L;
        if (key == "test-standard-rewind-druid") return 900000075L;
        if (key == "test-standard-rewind-warrior") return 900000076L;
        if (key == "test-standard-rewind-priest") return 900000077L;
        if (key == "test-standard-rewind-demonhunter") return 900000078L;
        if (key == "test-standard-rewind-hunter") return 900000079L;
        if (key == LocalDecks.TestDecisionsId) return 900000010L;
        if (key == LocalDecks.TestStandardBatch1Id) return 900000011L;
        if (key == "test-standard-batch-2-warrior") return 900000012L;
        if (key == "test-standard-batch-2-priest") return 900000013L;
        if (key == "test-standard-batch-2-mage") return 900000014L;
        if (key == "test-standard-batch-2-paladin") return 900000015L;
        if (key == "test-standard-batch-2-druid") return 900000016L;
        if (key == "test-standard-batch-2-rogue") return 900000017L;
        if (key == "test-standard-batch-2-warlock") return 900000018L;
        if (key == "test-standard-batch-2-hunter") return 900000019L;
        if (key == "test-standard-batch-2-shaman") return 900000020L;
        if (key == "test-standard-batch-2-demonhunter") return 900000021L;
        if (key == LocalDecks.TestRushSilenceId) return 900000022L;
        if (key == "test-standard-extra-warrior") return 900000023L;
        if (key == "test-standard-extra-priest") return 900000024L;
        if (key == "test-standard-extra-druid") return 900000025L;
        if (key == "test-standard-extra-hunter") return 900000026L;
        if (key == "test-standard-extra-demonhunter") return 900000027L;
        if (key == "test-standard-extra-rogue") return 900000028L;
#endif
        int index = Array.IndexOf(DeckKeys, key);
        if (index < 0) throw new ArgumentException("Unknown local deck: " + key);
        return 910000001L + index;
    }

    // Saved match deck ("custom:<native id>" or a loaner key) -> native deck id; 0 when it no longer maps.
    internal static long PlayedDeckId(string deck)
    {
        if (string.IsNullOrEmpty(deck)) return 0;
        const string custom = "custom:";
        if (deck.StartsWith(custom, StringComparison.Ordinal))
            return long.TryParse(deck.Substring(custom.Length), out long id) && id > 0 ? id : 0;
        try { return DeckId(deck); }
        catch (ArgumentException) { return 0; }
    }
    internal static bool IsPersonalId(long deckId) => deckId == LegacyPersonalDeckId
        || deckId >= FirstAllocatedPersonalDeckId;
    internal static bool IsLoanerId(long deckId) => DeckKeys.Skip(1).Any(key => DeckId(key) == deckId);
    internal static bool TryProjectDeckBoxLock(bool probeActive, bool collectionContext, long deckId,
        out bool locked)
    {
        locked = IsLoanerId(deckId);
        return probeActive && collectionContext;
    }
    internal static bool ShowLoanerTooltip(bool probeActive, bool collectionContext, long deckId, bool isLocked) =>
        isLocked && TryProjectDeckBoxLock(probeActive, collectionContext, deckId, out bool locked) && locked;
    internal static bool ShowDeckBoxDeleteButton(bool probeActive, bool collectionContext, long deckId,
        bool requested) => requested && !(probeActive && collectionContext && IsLoanerId(deckId) && HideableLoanerKey(deckId) == null);
    // Starters this save hid (LocalProfile.HiddenLoaners) never project; only Core starters can be hidden.
    internal static IReadOnlyList<string> VisibleLoanerKeys(IEnumerable<string> projected, IEnumerable<string> hiddenLoaners)
    {
        var hidden = new HashSet<string>(hiddenLoaners ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        return projected.Where(key => !hidden.Contains(key)).ToArray();
    }
    // Core starter loaners can be "deleted" from the tray: that hides them for the save (LocalProfile.HideLoaner).
    internal static string HideableLoanerKey(long deckId) =>
        LocalDecks.PlayerCoreStarterDecks.Select(deck => deck.Id).FirstOrDefault(key => DeckId(key) == deckId);
    internal static bool CanMutatePersonal(long deckId, IEnumerable<long> persistedIds) => IsPersonalId(deckId)
        && persistedIds != null && persistedIds.Contains(deckId);
    // Owner 2026-10-30: test/showcase loaners (and the legacy pinned decks only a harness selector shows) are for Sandbox
    // saves only; candidate (STANDARD_PROOF) builds are non-release harnesses. Core starter loaners stay for every save.
    internal static bool AllowsTestLoaners(LocalProfile profile) =>
#if STANDARD_PROOF
        true;
#else
        profile != null && profile.IsSandbox;
#endif
    internal const string TestLoanerRefusal = "Test decks are only available on Sandbox saves.";
    private static bool IsTestLoanerKey(string key) => key.StartsWith("test-", StringComparison.Ordinal)
        || key.StartsWith("showcase-", StringComparison.Ordinal);
    private static bool IsPlayerStarterKey(string key) => key.StartsWith("player-", StringComparison.Ordinal);
    private static IEnumerable<string> PinnedLoanerKeys => DeckKeys.Skip(1)
        .Where(key => !IsTestLoanerKey(key) && !IsPlayerStarterKey(key));

    // Player access is a loan, never a collection grant. Keep catalog IDs for saved history and harnesses; retired
    // loaners (LocalDecks.RetiredPlayerStarterDecks) keep their DeckKeys/DeckId entries but never project.
    internal static readonly IReadOnlyList<string> PlayerLoanerKeys = Array.AsReadOnly(
#if ENGINE_SOURCE
        LocalDecks.PlayerCoreStarterDecks.Select(deck => deck.Id).ToArray()
#else
        PinnedLoanerKeys.ToArray()
#endif
    );

#if SOLOSTONE_INSTALLER && !SOLOSTONE_TEST_HOOKS
    internal static string TestLoanerGroupPath = null;
#else
    internal static string TestLoanerGroupPath = @"C:\hs-native-ui-"+@"prototype\client\reports\test-loaner-group.txt";
#endif

    // Showcase loaners lead every group's page and belong to no group.
    internal static readonly IReadOnlyList<string> ShowcaseLoanerKeys = Array.AsReadOnly(
#if ENGINE_SOURCE
        new[] { LocalDecks.TestShowcaseId }
#else
        Array.Empty<string>()
#endif
    );
    // Numbered bootstrap groups; each projects one tray page of test loaners (showcases plus members at most
    // MaximumTestLoanerGroupSize, so every member stays visible for up to three personal decks). Aliases keep the
    // old selector values working.
    internal const int MaximumTestLoanerGroupSize = 15;
    internal static readonly IReadOnlyList<IReadOnlyList<string>> TestLoanerGroups = Array.AsReadOnly(new IReadOnlyList<string>[] {
#if ENGINE_SOURCE
        // 1 = default: original decision, batch-1, batch-2 class and silence loaners.
        Array.AsReadOnly(new[] { LocalDecks.TestDecisionsId, LocalDecks.TestStandardBatch1Id,
            "test-standard-batch-2-warrior", "test-standard-batch-2-priest", "test-standard-batch-2-mage", "test-standard-batch-2-paladin",
            "test-standard-batch-2-druid", "test-standard-batch-2-rogue", "test-standard-batch-2-warlock",
            "test-standard-batch-2-hunter", "test-standard-batch-2-shaman", "test-standard-batch-2-demonhunter", LocalDecks.TestRushSilenceId }),
        // 2 = extra: overflow recipes.
        Array.AsReadOnly(new[] { "test-standard-extra-warrior", "test-standard-extra-priest", "test-standard-extra-druid",
            "test-standard-extra-hunter", "test-standard-extra-demonhunter", "test-standard-extra-rogue", "test-standard-azalina-priest" }),
        // 3 = deathknight: original five Death Knight loaners.
        Array.AsReadOnly(new[] { "test-deathknight-rainbow", "test-deathknight-frost", "test-deathknight-unholy",
            "test-deathknight-blood", "test-deathknight-buu" }),
        // 4: seats for batch 9, Dark Gift, history-facts and cost-rules registrations.
        Array.AsReadOnly(new[] { "test-standard-more-warrior", "test-standard-more-priest", "test-standard-more-mage",
            "test-standard-more-paladin", "test-standard-more-druid", "test-standard-more-rogue", "test-standard-more-warlock",
            "test-standard-more-hunter", "test-standard-more-shaman", "test-standard-more-demonhunter" }),
        // 5: seats for Standard batch 10 (coordinator batch-10 bookkeeping), one per class plus a second Death Knight seat.
        Array.AsReadOnly(new[] { "test-standard-b10-warrior", "test-standard-b10-priest", "test-standard-b10-mage",
            "test-standard-b10-paladin", "test-standard-b10-druid", "test-standard-b10-rogue", "test-standard-b10-warlock",
            "test-standard-b10-hunter", "test-standard-b10-shaman", "test-standard-b10-demonhunter", "test-standard-b10-deathknight",
            "test-standard-b10-deathknight-2" }),
        // 6: seats for Standard mech families (coordinator mech-family bookkeeping), one per class plus a second DK seat.
        Array.AsReadOnly(new[] { "test-standard-mecha-warrior", "test-standard-mecha-priest", "test-standard-mecha-mage",
            "test-standard-mecha-paladin", "test-standard-mecha-druid", "test-standard-mecha-rogue", "test-standard-mecha-warlock",
            "test-standard-mecha-hunter", "test-standard-mecha-shaman", "test-standard-mecha-demonhunter", "test-standard-mecha-deathknight", "test-standard-mecha-deathknight-2" }),
        // 7: Rewind registrations and support cards.
        Array.AsReadOnly(new[] { "test-standard-rewind-rogue", "test-standard-rewind-mage", "test-standard-rewind-warlock", "test-standard-rewind-shaman", "test-standard-rewind-paladin", "test-standard-rewind-druid", "test-standard-rewind-warrior", "test-standard-rewind-priest", "test-standard-rewind-demonhunter", "test-standard-rewind-hunter" }),
        // 8: keyword primitive fixtures, reserved band 900000130..149.
        Array.AsReadOnly(new[] { "test-keywords-cfa-paladin", "test-keywords-cfa-hunter",
            "test-keywords-cfa-warrior", "test-keywords-cfa-shaman", "test-keywords-cfa-priest", "test-keywords-cfa-mage" }),
        // 9: card-state fixtures, reserved band 900000150..158.
        Array.AsReadOnly(new[] { "test-standard-cardstate-mage", "test-standard-cardstate-shaman", "test-standard-cardstate-warlock", "test-standard-cardstate-warrior", "test-standard-cardstate-druid", "test-standard-cardstate-hunter", "test-standard-cardstate-paladin", "test-standard-cardstate-priest" }),
        // 10: seats for Standard batch 11 (coordinator batch-11 bookkeeping), reserved band 900000170..179.
        Array.AsReadOnly(new[] { "test-standard-b11-warrior", "test-standard-b11-priest", "test-standard-b11-mage",
            "test-standard-b11-paladin", "test-standard-b11-druid", "test-standard-b11-rogue", "test-standard-b11-warlock",
            "test-standard-b11-hunter", "test-standard-b11-shaman" }),
        // 11 = showcase: curated player decks, in showcase-decks.md order.
        Array.AsReadOnly(new[] { "showcase-rogue-imbue", "showcase-death-knight-dark-gift", "showcase-demon-hunter-outcast", "showcase-druid-quest-tokens", "showcase-hunter-kindred-beasts", "showcase-mage-leylines", "showcase-paladin-pure-holy", "showcase-priest-imbue", "showcase-shaman-herald", "showcase-warlock-dragon-shred", "showcase-warrior-armor-herald", "showcase-rogue-prepare-combo" })
#else
        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()
#endif
    });

    // Canonical group number ("1".."N") for a selector value, or null when unknown.
    internal static string CanonicalTestLoanerGroup(string value)
    {
        string group = value?.Trim();
        if (string.IsNullOrEmpty(group)) return null;
        if (string.Equals(group, "default", StringComparison.OrdinalIgnoreCase)) return "1";
        if (string.Equals(group, "showcase", StringComparison.OrdinalIgnoreCase)) return "11";
        if (string.Equals(group, "extra", StringComparison.OrdinalIgnoreCase)) return "2";
        if (string.Equals(group, "deathknight", StringComparison.OrdinalIgnoreCase)) return "3";
        if (group.Length <= 2 && group[0] != '0' && group.All(c => c >= '0' && c <= '9')
            && int.TryParse(group, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            && number >= 1 && number <= TestLoanerGroups.Count && (number != 10 || TestLoanerGroups[9].Count > 0))
            return number.ToString(CultureInfo.InvariantCulture);
        return null;
    }

    internal static string LoadTestLoanerGroup(string path, Action<string> warning)
    {
        try
        {
            const int maximumBytes = 64;
            // No read-ahead: even a growing selector may only supply 64 bytes.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1))
            {
                if (stream.Length > maximumBytes) throw new InvalidDataException("Selector exceeds 64 bytes.");
                var bytes = new byte[maximumBytes];
                int count = 0, read;
                while (count < bytes.Length && (read = stream.Read(bytes, count, bytes.Length - count)) > 0)
                    count += read;
                if (stream.Length > maximumBytes) throw new InvalidDataException("Selector exceeds 64 bytes.");
                string group = CanonicalTestLoanerGroup(Encoding.UTF8.GetString(bytes, 0, count).TrimStart('\uFEFF'));
                if (group != null) return group;
                throw new InvalidDataException("Selector must be a group number 1.." + TestLoanerGroups.Count
                    + ", default, extra, deathknight or showcase.");
            }
        }
        catch (Exception error)
        {
            warning("TEST_LOANER_GROUP disabled: " + path + " (" + error.GetType().Name + ")");
            return null;
        }
    }

    internal static IReadOnlyList<string> ProjectedLoanerKeys(int visiblePersonalCount, string testLoanerGroup = null)
    {
        if (visiblePersonalCount < 0)
            throw new ArgumentOutOfRangeException(nameof(visiblePersonalCount));
        visiblePersonalCount = Math.Min(visiblePersonalCount, MaximumPersonalDecks);
#if ENGINE_SOURCE
        // Installers already write "showcase"; that selector is the normal player tray too.
        if (testLoanerGroup == null || (CanonicalTestLoanerGroup(testLoanerGroup) ?? "11") == "11")
            return PlayerLoanerKeys;
#endif
        string[] pinned = PinnedLoanerKeys.ToArray();
        // Test recipes include inactive metas; only an explicit harness selector exposes them.
        if (testLoanerGroup == null) return pinned;
        // Starters and proof decks are pinned. Reserve New Deck's slot below the personal cap.
        // Showcases, then the chosen group's members in group order, then every other test loaner in DeckKeys
        // order fill the remaining slots, dropping the last first. Unknown groups use group 11.
        // Harness loaners keep the retail 27-deck budget so a raised tray never floods with test decks.
        int testSlots = Math.Max(0, RetailDeckLimit - pinned.Length - visiblePersonalCount
            - (visiblePersonalCount < MaximumPersonalDecks ? 1 : 0));
        var tests = DeckKeys.Skip(1).Where(IsTestLoanerKey).ToArray();
        var members = ShowcaseLoanerKeys.Concat(TestLoanerGroups[
            int.Parse(CanonicalTestLoanerGroup(testLoanerGroup) ?? "11", CultureInfo.InvariantCulture) - 1]).ToArray();
        return pinned.Concat(members.Concat(tests.Where(key => !members.Contains(key))).Take(testSlots)).ToArray();
    }
    // Live tray capacity for any new personal deck (native create, shop deck purchase, test control): the personal cap,
    // plus the native tray ceiling for the session's bootstrap loaner subset and the new deck. With at most
    // PlayerLoanerCapacity loaners the cap is the only limit that can bind; the ceiling check is defence in depth.
    internal const string TrayFullMessage = "The deck tray is full. Delete a deck to make room.";
    internal static bool CanAddPersonalDeck(int personalCount, int sessionLoanerCount) =>
        personalCount >= 0 && sessionLoanerCount >= 0 && personalCount < MaximumPersonalDecks
        && personalCount + sessionLoanerCount + 1 <= NativeDeckLimit;
    // Null when a personal deck may be added; otherwise the player-facing refusal (personal cap first).
    internal static string PersonalDeckBlocker(int personalCount, int sessionLoanerCount) =>
        personalCount >= MaximumPersonalDecks ? LocalProfile.PersonalDeckLimitMessage
        : CanAddPersonalDeck(personalCount, sessionLoanerCount) ? null : TrayFullMessage;
    // Native New Deck: the shifted native slot gate (NativeDeckLimit) counts tray boxes, which undercounts the cap when
    // the session shows fewer than PlayerLoanerCapacity loaners (hidden starters, test-loaner trays). The live personal
    // gate must also pass, or New Deck would lead to a hero pick whose create is refused.
    internal static bool ShowNewDeckButton(bool nativeShows, int personalCount, int sessionLoanerCount) =>
        nativeShows && PersonalDeckBlocker(personalCount, sessionLoanerCount) == null;
    internal static bool CanCreateDraft(bool normalDeck, bool normalSource, bool supportedHero, bool standardFormat, int personalCount,
        int sessionLoanerCount) =>
        normalDeck && normalSource && supportedHero && standardFormat && CanAddPersonalDeck(personalCount, sessionLoanerCount);
    internal static bool SupportsLocalDeckSave(bool probeActive, bool collectionContext, bool supportedSource,
        bool packRewardReconcile) => probeActive && supportedSource && (collectionContext || packRewardReconcile);
    // craftingReconcile: native reconciles deck ownership inside a crafting synchronize. The craft is already committed
    // and the profile deck is authoritative (ReprojectCraftingDecks restores it), so a differing reconcile is ignored,
    // never thrown at the player (live 2026-10-30: native drops a whole Fabled bundle when its version leaves).
    internal static NativeDeckSaveDisposition DeckSaveAction(bool probeActive, bool personal, bool persisted,
        bool deletedPersonal, bool exactPersonal, bool loaner, bool stopReorder, bool ownershipReconcile,
        bool exactLoaner, bool craftingReconcile = false)
    {
        if (!probeActive) return NativeDeckSaveDisposition.Passthrough;
        if (personal && persisted) return exactPersonal || ownershipReconcile && craftingReconcile ? NativeDeckSaveDisposition.Ignore
            : ownershipReconcile ? NativeDeckSaveDisposition.Reject : NativeDeckSaveDisposition.Save;
        if (personal && deletedPersonal) return NativeDeckSaveDisposition.Ignore;
        return loaner && (stopReorder || ownershipReconcile) && exactLoaner
            ? NativeDeckSaveDisposition.Ignore : NativeDeckSaveDisposition.Reject;
    }
    internal static IReadOnlyDictionary<long, long> ExpectedNativeSortOrders(
        IEnumerable<KeyValuePair<long, long>> personalOrder, IEnumerable<string> projectedLoanerKeys = null)
    {
        if (personalOrder == null) throw new ArgumentNullException(nameof(personalOrder));
        var personals = personalOrder.ToArray();
        if (personals.Length > MaximumPersonalDecks || personals.Any(pair => !IsPersonalId(pair.Key) || pair.Value < 0)
            || personals.Select(pair => pair.Key).Distinct().Count() != personals.Length)
            throw new InvalidOperationException("Invalid personal deck order projection.");
        var ranks = personals.OrderBy(pair => pair.Value).ThenBy(pair => pair.Key)
            .Select((pair, rank) => new { pair.Key, Rank = (long)rank })
            .ToDictionary(pair => pair.Key, pair => pair.Rank);
        // The native adapter supplies its bootstrap subset for session-local create/delete/reorder.
        string[] loaners = (projectedLoanerKeys ?? ProjectedLoanerKeys(personals.Length)).ToArray();
        if (loaners.Any(key => !DeckKeys.Skip(1).Contains(key)) || loaners.Distinct().Count() != loaners.Length)
            throw new InvalidOperationException("Invalid loaner deck order projection.");
#if STANDARD_PROOF
        foreach (long id in ranks.Keys.ToArray()) ranks[id] += 2;
        ranks.Add(DeckId("proof-mage"), 0);
        ranks.Add(DeckId("proof-warrior"), 1);
        loaners = loaners.Where(key => !StandardProofNative.IsProof(key)).ToArray();
#endif
        // Keep the live-verified loaner band independent of the personal deck count.
        int loanerStart = MaximumPersonalDecks;
#if STANDARD_PROOF
        loanerStart += 2;
#endif
        // Starter loaners keep their PlayerLoanerKeys slot, so hiding one never re-ranks the others mid-session.
        bool starters = loaners.All(key => PlayerLoanerKeys.Contains(key));
        for (int i = 0; i < loaners.Length; i++)
            ranks.Add(DeckId(loaners[i]), loanerStart + (starters ? ((IList<string>)PlayerLoanerKeys).IndexOf(loaners[i]) : i));
        return ranks;
    }
    // Every profile/deck check that can refuse a local match. LocalLadder runs it, and builds the native match,
    // before LocalProfile.Begin commits Pending, so a refused start never leaves a Pending behind.
    // Returns the saved per-copy entries of a personal deck (presentation only), null for a loaner.
    internal static IReadOnlyList<LocalDeckCard> ValidateStart(LocalProfile profile, string selected, LocalDeck resolved, bool personal)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        if (resolved == null) throw new InvalidOperationException("Selected deck is missing.");
        if (!personal)
        {
            if (profile.IsLoanerHidden(selected)) throw new InvalidOperationException("That starter deck is hidden. Restore starter decks in Options.");
            if (!PlayerLoanerKeys.Contains(selected) && !AllowsTestLoaners(profile)) throw new InvalidOperationException(TestLoanerRefusal);
            ValidateSelection(selected, resolved.Hero, resolved.CardIds.ToArray(), new Dictionary<string, int>());
            ValidateMatchDeckBinding(selected, null);
            return null;
        }
        if (selected == null || !selected.StartsWith("custom:", StringComparison.Ordinal))
            throw new InvalidOperationException("Personal deck identity is missing.");
        if (!long.TryParse(selected.Substring("custom:".Length), NumberStyles.None, CultureInfo.InvariantCulture, out long nativeId))
            throw new InvalidOperationException("Custom deck identity is invalid.");
        LocalCustomDeck saved = profile.GetDraft(nativeId) ?? throw new InvalidOperationException("Custom deck no longer exists.");
        if (!profile.IsDeckComplete(nativeId))
            throw new InvalidOperationException("The saved deck is incomplete. Acquire missing cards before playing.");
        ValidateCustomStart(profile, nativeId, saved, resolved);
        resolved.WithSideboards(saved.Sideboards, true).Build();
        ValidateMatchDeckBinding(selected, resolved);
        return saved.CardEntries;
    }
    internal static void ValidateMatchDeckBinding(string key, LocalDeck personalSnapshot)
    {
        const string prefix = "custom:";
        if (key != null && key.StartsWith(prefix, StringComparison.Ordinal))
        {
            if (!long.TryParse(key.Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out long id)
                || !IsPersonalId(id) || personalSnapshot == null)
                throw new InvalidOperationException("Personal match requires an explicit saved deck ID and snapshot.");
            // Same universe as deck saving and completeness (LocalProfile.PlayableDeck), never the narrower Custom list.
            LocalProfile.PlayableDeck(personalSnapshot.Name, personalSnapshot.Hero, personalSnapshot.CardIds,
                personalSnapshot.Sideboards.Select(row => new LocalSideboard { Source = row.Key, Cards = row.Value.ToList() }));
            return;
        }
        if (personalSnapshot != null || key == "custom")
            throw new InvalidOperationException("Personal match snapshot does not match its deck key.");
        DeckId(key);
    }
    internal static bool MatchesDeleteConfirmation(Delegate callback, object owner, MethodInfo method,
        object callbackData, object userData) => callback != null && ReferenceEquals(callback.Target, owner)
        && callback.Method == method && ReferenceEquals(callbackData, userData);
    internal static bool OwnsDeleteResponse(bool active, bool currentCollection, bool confirm) =>
        active && currentCollection && confirm;
    internal static bool OwnsCreateRequest(bool active, bool currentCollection, bool allowed, bool heroOverridden) =>
        active && currentCollection && allowed && !heroOverridden;
    internal static bool OwnsPersonalDeckEditor(bool active, bool currentCollection, bool currentManager,
        bool currentTray, bool currentMainContext, bool exactDeck, bool exactInstance, bool persistedPersonal) =>
        active && currentCollection && currentManager && currentTray && currentMainContext
        && exactDeck && exactInstance && persistedPersonal;
    internal static bool TryCreateDraft(Func<LocalCustomDeck> create, Action<Exception> failed,
        out LocalCustomDeck saved)
    {
        try { saved = create(); return true; }
        catch (Exception error) { saved = null; failed(error); return false; }
    }
    internal static bool TryPrepareDelete(Action prepare, Action<Exception> failed)
    {
        try { prepare(); }
        catch (Exception error) { failed(error); return false; }
        return true;
    }
    // Messages end with their own sentence; drop the detail's final period so it never reads "..".
    private static string ErrorDetail(string detail) =>
        string.IsNullOrWhiteSpace(detail) ? "unknown error" : detail.Trim().TrimEnd('.');
    internal static string DeleteFailureMessage(long id, string detail) =>
        "Local deck " + id + " was not deleted: "
        + ErrorDetail(detail) + ". Resolve the error and retry.";
    internal static string DeckWriteFailureMessage(string operation, long id, string detail)
    {
        if (operation != "create" && operation != "save" && operation != "delete")
            throw new ArgumentException("Unknown deck operation.", nameof(operation));
        return "Local deck " + operation + " failed for " + id + ": "
            + ErrorDetail(detail) + ". Fix the deck and retry.";
    }
    internal static string ReorderRefreshFailureMessage(bool committed, string detail) =>
        (committed ? "Deck order was saved" : "Deck order is unchanged")
        + ", but the Collection display could not refresh: "
        + ErrorDetail(detail)
        + ". Reopen Collection to load the durable order.";
    internal static string DeleteRefreshFailureMessage(long id, string detail) =>
        "Local deck " + id + " was deleted, but the Collection display could not refresh: "
        + ErrorDetail(detail)
        + ". Reopen Collection to load the durable library.";
    internal static string CreateRefreshFailureMessage(long id, string detail) =>
        "Local deck " + id + " was saved, but Collection could not open it: "
        + ErrorDetail(detail)
        + ". Reopen Collection to load the durable deck.";
    internal static string SaveRefreshFailureMessage(long id, string detail) =>
        "Local deck " + id + " was saved, but the Collection display could not refresh: "
        + ErrorDetail(detail)
        + ". Reopen Collection to load the durable deck.";
    internal static bool IsExactLoaner(long deckId, string hero, IEnumerable<string> ids)
    {
        string key = DeckKeys.Skip(1).SingleOrDefault(candidate => DeckId(candidate) == deckId);
        if (key == null || ids == null) return false;
        var loaner = LocalDecks.Get(key);
        return hero == loaner.Hero && ids.OrderBy(id => id, StringComparer.Ordinal)
            .SequenceEqual(loaner.CardIds.OrderBy(id => id, StringComparer.Ordinal));
    }
    private static bool ValidPackCardKey(string key)
    {
        var card = LocalPackCard.FromKey(key);
        return (card.Premium == 0 || card.Premium == 1 || card.Premium == 3) && LocalProfile.KnownLedgerCard(card.Id);
    }
    internal static bool ShouldRefreshPremiumDecks(LocalProfile profile, string id, int premium) =>
        profile.ProgressionGrantedCount(id, premium) > 0;
    internal static string[] Receipt(string[] ids) => BatchReceipt(ids, 1);
    // A mass opening's receipt is its packs' cards in draw order: five per pack.
    internal static string[] BatchReceipt(string[] ids, int packs)
    {
        if (packs < 1 || packs > LocalProfile.MassPackLimit || ids == null || ids.Length != 5 * packs
            || ids.Any(id => !ValidPackCardKey(id)))
            throw new InvalidOperationException("Replay requires exactly five supported saved card IDs per pack.");
        return ids.ToArray();
    }
    internal static string[] PendingReceipt(IReadOnlyList<LocalPackResult> packs) =>
        packs == null || packs.Count == 0 ? null : BatchReceipt(packs.SelectMany(pack => pack.Cards.Select(card => card.Key)).ToArray(), packs.Count);
    // Native mass Done (summary dismissed): the director ran the mass flow for exactly this receipt.
    internal static bool CanAcknowledgeMassReveal(bool probeActive, bool revealRunning, bool massFlow, bool directorMatches,
        string[] savedIds, int savedPacks, string[] summaryIds, int summaryPacks, bool alreadyAcknowledged, bool highlightsFinished) =>
        probeActive && revealRunning && massFlow && directorMatches && !alreadyAcknowledged && highlightsFinished
        && savedPacks >= 2 && savedPacks <= LocalProfile.MassPackLimit
        && savedIds != null && savedIds.Length == 5 * savedPacks && summaryPacks == savedPacks
        && savedIds.All(ValidPackCardKey)
        && summaryIds != null && summaryIds.SequenceEqual(savedIds, StringComparer.Ordinal);
    // Retail hoovers a stack only for these: never Legacy (single opens) or a booster this build does not know.
    internal static bool MassOpenable(int boosterId, bool pendingReveal)
    {
        string type = PackTypeForBooster(boosterId);
        return !pendingReveal && FairPackRules.IsKnown(type) && type != FairPackRules.Legacy;
    }
    internal static LocalCardCount[] PackGrantDeltas(LocalProfile profile, IReadOnlyDictionary<string, int> grants, Func<string, int, int> nativeCount) =>
        grants.Select(pair => { var card = LocalPackCard.FromKey(pair.Key);
            return new LocalCardCount { Id = card.Id, Premium = card.Premium,
                Count = Math.Max(0, Math.Max(profile.StoredOwnedCount(card.Id, card.Premium), profile.OwnedCount(card.Id, card.Premium))
                    - nativeCount(card.Id, card.Premium)) }; })
            .Where(card => card.Count > 0).ToArray();
    // A dequeued native page must belong to the batch, including exact version quantities.
    internal static bool ReceiptContainsPage(string[] savedIds, string[] actorIds) =>
        savedIds != null && savedIds.Length >= 5 && savedIds.Length <= 5 * LocalProfile.MassPackLimit
        && savedIds.Length % 5 == 0 && savedIds.All(ValidPackCardKey)
        && actorIds != null && actorIds.Length == 5 && actorIds.All(id => id != null)
        && actorIds.GroupBy(id => id, StringComparer.Ordinal)
            .All(group => savedIds.Count(id => string.Equals(id, group.Key, StringComparison.Ordinal)) >= group.Count());
    // Actor proof always compares exactly five cards; a batch-wide subset is insufficient.
    internal static bool ReceiptMatchesActors(string[] savedIds, string[] actorIds) =>
        savedIds != null && savedIds.Length == 5 && ReceiptContainsPage(savedIds, actorIds);
    internal static int ProjectedPackCount(int unopenedPacks, int pendingPacks, bool revealRunning)
    {
        if (unopenedPacks < 0) throw new ArgumentOutOfRangeException(nameof(unopenedPacks));
        if (pendingPacks < 0) throw new ArgumentOutOfRangeException(nameof(pendingPacks));
        return checked(unopenedPacks + (!revealRunning ? pendingPacks : 0));
    }
    internal static IReadOnlyList<(int boosterId, int count)> ProjectedPackStacks(
        IReadOnlyList<(string type, int count)> inventory, string pendingType, int pendingPacks, bool revealRunning)
    {
        if (inventory == null) throw new ArgumentNullException(nameof(inventory));
        if (pendingPacks < 0) throw new ArgumentOutOfRangeException(nameof(pendingPacks));
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pack in inventory)
        {
            if (!FairPackRules.IsKnown(pack.type) || pack.count < 0)
                throw new ArgumentException("Invalid pack inventory", nameof(inventory));
            counts.Add(pack.type, pack.count);
        }
        if (pendingPacks > 0 && !FairPackRules.IsKnown(pendingType))
            throw new ArgumentException("Unknown pending pack type", nameof(pendingType));
        var stacks = new List<(int boosterId, int count)>();
        foreach (string type in new[] { FairPackRules.Legacy }.Concat(FairPackRules.PackTypes))
        {
            counts.TryGetValue(type, out int count);
            count = ProjectedPackCount(count, type == pendingType ? pendingPacks : 0, revealRunning);
            if (count > 0) stacks.Add((FairPackRules.NativeBoosterId(type), count));
        }
        return stacks;
    }
    internal static int[] BoosterIdsToZero(IEnumerable<int> actorBoosterIds,
        IReadOnlyList<(int boosterId, int count)> stacks) =>
        actorBoosterIds.Except(stacks.Select(stack => stack.boosterId)).ToArray();
    internal static string PackTypeForBooster(int boosterId) =>
        new[] { FairPackRules.Legacy }.Concat(FairPackRules.PackTypes)
            .FirstOrDefault(type => FairPackRules.NativeBoosterId(type) == boosterId);
    internal static bool TryProjectPackOpenability(bool probeActive, int boosterId,
        ref bool openable, ref string lockedReason)
    {
        if (!probeActive || !FairPackRules.IsKnown(PackTypeForBooster(boosterId))) return false;
        openable = true;
        lockedReason = string.Empty;
        return true;
    }
    internal static IReadOnlyDictionary<string, int> NewGrantCounts(IEnumerable<LocalPackCard> cards)
    {
        if (cards == null) throw new ArgumentNullException(nameof(cards));
        var items = cards.ToArray();
        if (items.Any(card => card == null || card.Id == null || !LocalProfile.KnownLedgerCard(card.Id)
            || (card.Premium != 0 && card.Premium != 1 && card.Premium != 3)
            || (card.DuplicateGold != 0 && (card.DuplicateGold != 10 || card.Premium != 0))))
            throw new InvalidOperationException("Invalid committed pack receipt.");
        return items.Where(card => card.DuplicateGold == 0).GroupBy(card => card.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    }
    // Stored ownership and grants use receipt keys so premium versions cannot substitute for each other.
    internal static void ValidatePackOwnership(IReadOnlyDictionary<string, int> before,
        IReadOnlyDictionary<string, int> after, IReadOnlyDictionary<string, int> grants)
    {
        foreach (string key in before.Keys.Union(after.Keys, StringComparer.Ordinal).Union(grants.Keys, StringComparer.Ordinal))
        {
            int oldCount = before.TryGetValue(key, out int old) ? old : 0;
            int added = grants.TryGetValue(key, out int count) ? count : 0;
            int newCount = after.TryGetValue(key, out int current) ? current : 0;
            if (newCount != oldCount + added)
                throw new InvalidOperationException("Native pack ownership delta mismatch: " + key);
        }
    }
    internal static bool CanAcknowledgePackReveal(bool probeActive, bool revealRunning, bool doneShown,
        bool directorMatches, string[] savedIds, string[] actorIds, bool[] revealed, bool alreadyAcknowledged, string[] pageIds = null) =>
        probeActive && revealRunning && doneShown && directorMatches && !alreadyAcknowledged
        && revealed != null && revealed.Length == 5
        && revealed.All(value => value) && ReceiptMatchesActors(pageIds ?? savedIds, actorIds)
        && (pageIds == null || ReceiptContainsPage(savedIds, pageIds));
    internal static PackCompletionDisposition PackCompletion(bool probeActive, bool revealRunning,
        bool directorMatches, bool doneAcknowledged)
    {
        if (!probeActive) return PackCompletionDisposition.Passthrough;
        if (!revealRunning || !directorMatches) return PackCompletionDisposition.Ignore;
        return doneAcknowledged ? PackCompletionDisposition.Complete : PackCompletionDisposition.Fail;
    }
    internal static void ValidateSelection(string key, string hero, string[] ids, Dictionary<string, int> owned)
    {
        DeckId(key);
        if (ids == null || ids.Length != LocalDecks.DeckSize(ids) || ids.Any(id => id == null || (key == "custom" && !LocalDecks.IsCustomCard(id))))
            throw new InvalidOperationException("Native selection must contain the required number of supported cards.");
        LocalDecks.ValidateFabled(ids);
        if (key == "custom")
        {
            LocalDecks.Custom("Native selection", hero, ids);
            var deckOwned = owned == null ? null : LocalDecks.WithDeckCompanions(owned, ids);
            if (deckOwned == null || ids.GroupBy(id => id).Any(g => !deckOwned.TryGetValue(g.Key, out int count) || count < g.Count()))
                throw new InvalidOperationException("Custom native deck exceeds actual ownership.");
        }
        else
        {
            var loaner = LocalDecks.Get(key);
            if (loaner.Hero != hero || !loaner.CardIds.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(ids.OrderBy(id => id, StringComparer.Ordinal)))
                throw new InvalidOperationException("Native loaner differs from immutable local deck.");
        }
    }
    // Start-time check of a personal deck: ownership here is the native total across versions
    // (per-version ownership is LocalProfile.IsDeckComplete's job, run before this).
    internal static void ValidateCustomStart(LocalProfile profile, long nativeId, LocalCustomDeck saved, LocalDeck resolved) =>
        ValidateCustomSelection(nativeId, saved.Hero, saved.Cards.ToArray(), resolved.Hero, resolved.CardIds.ToArray(), TotalOwned(profile));
    internal static void ValidateCustomSelection(long nativeId, string savedHero, string[] savedIds,
        string nativeHero, string[] nativeIds, Dictionary<string, int> owned)
    {
        if (!ValidateDraftProjection(nativeId, "Native deck", savedHero, savedIds,
            "Native deck", nativeHero, nativeIds, owned))
            throw new InvalidOperationException("The saved deck is incomplete. Acquire missing cards before playing.");
    }
    internal static bool ValidateDraftProjection(long nativeId, string savedName, string savedHero, string[] savedIds,
        string nativeName, string nativeHero, string[] nativeIds, IReadOnlyDictionary<string, int> owned)
    {
        if (!IsPersonalId(nativeId)) throw new InvalidOperationException("Native deck ID is not personal.");
        LocalProfile.LedgerDeck(savedName, savedHero, savedIds, null, false);
        LocalProfile.LedgerDeck(nativeName, nativeHero, nativeIds, null, false);
        if (savedName != nativeName || savedHero != nativeHero || !savedIds.OrderBy(id => id, StringComparer.Ordinal)
            .SequenceEqual(nativeIds.OrderBy(id => id, StringComparer.Ordinal)))
            throw new InvalidOperationException("Native draft differs from its persisted snapshot.");
        if (owned == null) return false;
        var deckOwned = LocalDecks.WithDeckCompanions(owned, nativeIds);
        return nativeIds.Length == LocalDecks.DeckSize(nativeIds) && LocalCardCatalog.WithinCopyLimits(nativeIds)
            && nativeIds.GroupBy(id => id).All(group => deckOwned.TryGetValue(group.Key, out int count) && count >= group.Count());
    }
    internal static MethodInfo ResolvePublisher(Type type)
    {
        var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic).Where(m =>
            m.Name == "OnNetCacheObjReceived" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1
            && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == m.GetGenericArguments()[0]).ToArray();
        if (methods.Length != 1) throw new MissingMethodException(type.FullName, "OnNetCacheObjReceived<T>(T)");
        return methods[0];
    }
    internal static bool AllowedCache(string name) => new[] {
        "NetCacheCollection", "NetCacheDecks", "NetCacheProfileNotices", "NetCacheArcaneDustBalance", "NetCacheClientOptions",
        "NetCacheFeatures", "NetCacheHeroLevels", "NetCacheBoosters", "NetCacheProfileProgress", "NetCachePlayerRecords",
        "NetCacheMedalInfo", "NetCacheCardBacks", "NetCacheCoins", "NetCacheFavoriteHeroes", "NetCachePets"
    }.Contains(name);
    internal static bool AllowedPreference(string name) => name == "COLLECTION_MANAGER" || name == "PLAYER_FLAGS";
    internal static bool AllowedPreferenceSave(string key, string subkey, long[] values) => key == "COLLECTION_MANAGER"
        || key == "PLAYER_FLAGS" && subkey == "PLAYER_FLAGS_HAS_SEEN_MASS_PACK_OPENING_TOOLTIP"
            && values != null && values.Length == 1 && values[0] == 1;
    // Format picker modes, as the popup's own event and object names spell them (WILD_BUTTON_CLICKED, ...).
    internal static readonly string[] FormatPickerModes = { "STANDARD", "WILD", "CASUAL", "CLASSIC", "TWIST" };
    // Which mode a picker button selects: a single mode named by its own send-event actions wins; otherwise the nearest
    // object name (button first, then ancestors) naming exactly one mode; null when unnamed or ambiguous.
    internal static string FormatPickerMode(IEnumerable<string> eventNames, IEnumerable<string> namesNearestFirst)
    {
        string Single(string text)
        {
            string upper = (text ?? "").ToUpperInvariant();
            var hits = FormatPickerModes.Where(mode => upper.Contains(mode)).ToArray();
            return hits.Length == 1 ? hits[0] : null;
        }
        var fromEvents = (eventNames ?? Enumerable.Empty<string>()).Select(Single).Where(mode => mode != null).Distinct().ToArray();
        if (fromEvents.Length != 0) return fromEvents.Length == 1 ? fromEvents[0] : null;
        foreach (string name in namesNearestFirst ?? Enumerable.Empty<string>())
        {
            string mode = Single(name);
            if (mode != null) return mode;
        }
        return null;
    }
    // SoloStone plays Standard ranked and Standard Casual (#37): every other picker mode stays visible but disabled.
    internal static bool IsDisabledFormatMode(string mode) => mode != null && mode != "STANDARD" && mode != "CASUAL";
    // The native picker's Standard/Casual choice (NativeFormatLock.StandardMode keeps it; boot restores the save's
    // LocalProfile.PrefersCasual). IN_RANKED_PLAY_MODE reads its inverse, so the client renders Casual natively.
    internal static bool CasualPlayMode;
    // True between the picker switch's prefix and postfix (NativeFormatLock.StandardMode/StandardModeDone).
    internal static bool PickerSwitchActive;
    // The value Options.SetInRankedPlayMode stores: a picker switch adopts the request (once); anything else keeps the mode.
    internal static bool FollowRankedPlayMode(bool requested)
    {
        if (!PickerSwitchActive) return !CasualPlayMode;
        PickerSwitchActive = false;
        CasualPlayMode = !requested;
        return requested;
    }
    // Disabled picker button tint (live 2026-10-25: desaturate alone was invisible on the picker art). The material's own
    // colour property at DisabledFormatBrightness of its shared value, alpha kept: _Color first, else _TintColor; null when
    // the shader has neither. Always computed from the shared material, so re-applying never compounds.
    internal const float DisabledFormatBrightness = 0.45f;
    internal static (string Property, float R, float G, float B, float A)? DisabledFormatTint(
        (float R, float G, float B, float A)? color, (float R, float G, float B, float A)? tint)
    {
        string property = color.HasValue ? "_Color" : tint.HasValue ? "_TintColor" : null;
        if (property == null) return null;
        var source = color ?? tint.Value;
        return (property, source.R * DisabledFormatBrightness, source.G * DisabledFormatBrightness,
            source.B * DisabledFormatBrightness, source.A);
    }
    internal static bool TryGetOnboardingOption(bool isolated, string option, out bool value)
    {
        value = false;
        if (!isolated) return false;
        // Empty offline ClientOptions must not make every launch a new-player visit.
        switch (option)
        {
            case "HAS_SEEN_COLLECTIONMANAGER":
            case "HAS_SEEN_COLLECTIONMANAGER_AFTER_PRACTICE":
            case "HAS_STARTED_A_DECK":
            case "HAS_SEEN_PACK_OPENING":
            case "HAS_OPENED_BOOSTER":
            case "HAS_SEEN_SET_FILTER_TUTORIAL":
            case "HAS_SEEN_MULLIGAN":
            case "HAS_SEEN_THE_COIN":
            case "HAS_SEEN_STEALTH_TAUNTER":
            case "IN_RANKED_PLAY_MODE":
                value = !CasualPlayMode;
                return true;
            default:
                return false;
        }
    }
}

// Delete notices wait for their own confirmation's hide, never another dialog's event.
internal sealed class NativeDeckDeleteNotice
{
    private readonly object manager, dialog;
    private bool hidden, consumed;
    internal string Header { get; }
    internal string Message { get; }
    internal NativeDeckDeleteNotice(object manager, object dialog, string header, string message)
    { this.manager = manager; this.dialog = dialog; Header = header; Message = message; }
    internal void MarkHidden(object owner) { if (ReferenceEquals(dialog, owner)) hidden = true; }
    internal bool TryTake(object owner, bool localContext)
    {
        if (consumed || !hidden || !ReferenceEquals(manager, owner)) return false;
        consumed = true;
        return localContext;
    }
}
