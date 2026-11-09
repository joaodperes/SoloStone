using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PegasusShared;

internal static partial class NativeUIProbe
{
    // SoloStone presents Standard everywhere: every local deck header, the picker, ladder medal and results screens.
    // Offline the client has no set-rotation timings, so it classes every set Standard; the engine (always FT_WILD
    // internally) and LocalDecks/LocalCardCatalog keep enforcing the real meta pool. Not persisted: profiles store no format.
    internal const FormatType LocalDeckFormat = FormatType.FT_STANDARD;

    // Live 2026-10-30: offline the client has no server-delivered King of the Underbelly subset (ALLOWED_SIDEBOARD_SUBSET
    // 19499 is empty), so DeckRule_SideboardSubset refused every contraband pick ("invalid cards in your sideboard",
    // empty contraband pool). Where native has no subset data for an owner the engine models, the engine's deckbuilding
    // rule supplies membership. Ownership stays native (CanCardBeAddedAsOwned is untouched).
    internal static void InitializeSideboardSubset(Harmony harmony)
    {
        foreach (var hook in new[] { ("IsInAllowedSubset", nameof(LocalSideboardSubsetMember)),
            (nameof(DeckRule_SideboardSubset.IsDeckValid), nameof(LocalSideboardSubsetDeckValid)) })
            harmony.Patch(AccessTools.Method(typeof(DeckRule_SideboardSubset), hook.Item1)
                ?? throw new MissingMethodException(nameof(DeckRule_SideboardSubset), hook.Item1),
                prefix: new HarmonyMethod(typeof(NativeUIProbe), hook.Item2));
    }

    private static bool LocalSubsetOwner(SideboardDeck side, out string owner, out bool ignoresClassRule)
    {
        owner = null; ignoresClassRule = false;
        if (!active || side == null) return false;
        var definition = DefLoader.Get()?.GetEntityDef(side.OwnerCardDbId);
        if (definition == null) return false;
        int subset = definition.GetTag(GAME_TAG.ALLOWED_SIDEBOARD_SUBSET);
        owner = definition.GetCardId();
        if (!LocalDecks.UsesLocalSideboardSubset(owner, subset, subset == 0 ? 0 : GameDbf.GetIndex().GetSubsetById(subset).Count))
            return false;
        ignoresClassRule = definition.HasTag(GAME_TAG.SIDEBOARD_IGNORES_CLASS_RULE);
        return true;
    }

    private static bool LocalSideboardSubsetMember(string cardId, SideboardDeck sideboardDeck, ref bool ignoresClassRule, ref bool __result)
    {
        try
        {
            if (!LocalSubsetOwner(sideboardDeck, out string owner, out bool ignores)) return true;
            __result = LocalDecks.SideboardEligible(owner, cardId);
            ignoresClassRule = __result && ignores;
            return false;
        }
        catch (Exception error) { UnityEngine.Debug.LogError("NATIVE_DECK sideboard subset failed: " + error); return true; }
    }

    private static bool LocalSideboardSubsetDeckValid(CollectionDeck deck, List<DeckRule.RuleType> ignoreRules,
        ref RuleInvalidReason reason, ref bool __result)
    {
        try
        {
            if (!(deck is SideboardDeck side) || !LocalSubsetOwner(side, out string owner, out bool ignores)) return true;
            if (ignores)
            {
                ignoreRules.Add(DeckRule.RuleType.IS_CLASS_CARD_OR_NEUTRAL);
                ignoreRules.Add(DeckRule.RuleType.IS_DUAL_CLASS_CARD_OR_NEUTRAL);
            }
            int outside = deck.GetCardsWithCardID().Count(id => !LocalDecks.SideboardEligible(owner, id));
            reason = outside > 0 ? new RuleInvalidReason(GameStrings.Format("GLUE_COLLECTION_DECK_RULE_OUTSIDE_SIDEBOARD_SUBSET", outside), outside) : null;
            __result = outside == 0;
            return false;
        }
        catch (Exception error) { UnityEngine.Debug.LogError("NATIVE_DECK sideboard subset validation failed: " + error); return true; }
    }

    // The native hook and headless checks share the authoritative save and projection path.
    internal static LocalCustomDeck CommitLocalDraft(LocalProfile profile, CollectionDeck working, CollectionDeck prior)
    {
        if (!SupportedNativeEnvelope(working) || !SameFixedPersonalEnvelope(working, prior))
            throw new InvalidOperationException("Unsupported local deck variant or sideboard.");
        var entries = DeckEntries(working);
        string[] ids = entries.Select(card => card.Id).ToArray();
        string hero = NativeBaseHero(working);
        ValidateNativeDeckCards(ids);
        if (!NativeDeckRunes.Compatible(hero, ids, working.GetRuneOrder()))
            throw new InvalidOperationException("Native runes do not cover the deck cards.");
        var persisted = profile.GetDraft(working.ID)
            ?? throw new InvalidOperationException("Unknown local deck: " + working.ID);
        if (hero != persisted.Hero) throw new InvalidOperationException("The deck hero class changed.");
        var boards = ReadSideboards(working);
        // The deck tray's card back and hero skin are per-deck cosmetics, saved with the cards. Cards untouched since
        // the projected baseline (rename, card back, hero skin, pet) keep the saved cards and versions exactly: the
        // projection may show other versions (best owned copy, Normal ghost) and must never rewrite them. A saved version
        // that is no longer owned (disenchanted) is repaired instead: the full save writes what the projection shows.
        if (prior != null && profile.SavedVersionsOwned(working.ID) && SameDeckEntries(entries, DeckEntries(prior)) && SameSideboardEntries(boards, ReadSideboards(prior))
            && SameDeckIds(entries.Select(card => card.Id), persisted.Cards)
            && SideboardSignature(boards) == SideboardSignature(persisted.Sideboards))
            profile.SaveDraftCosmetics(working.ID, working.Name, NativeDeckCosmetics(profile, working));
        else NativeUIProjection.SaveNativeDraft(profile, working.ID, working.Name, hero, entries,
            boards, persisted.SortOrder, NativeDeckCosmetics(profile, working));
        return profile.GetDraft(working.ID);
    }

    private static string EntryKey(LocalDeckCard card) => card.Premium + ":" + card.Id;
    internal static bool SameDeckIds(IEnumerable<string> first, IEnumerable<string> second) =>
        first.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(second.OrderBy(id => id, StringComparer.Ordinal));
    internal static bool SameDeckEntries(IEnumerable<LocalDeckCard> first, IEnumerable<LocalDeckCard> second) =>
        SameDeckIds(first.Select(EntryKey), second.Select(EntryKey));
    private static bool SameSideboardEntries(LocalSideboard[] first, LocalSideboard[] second) =>
        first.Length == second.Length && first.Zip(second, (a, b) => a.Source == b.Source && SameDeckEntries(a.CardEntries, b.CardEntries)).All(same => same);

    // Per-copy versions in native slot order (Diamond, Signature, Golden, Normal within a slot).
    internal static System.Collections.Generic.List<LocalDeckCard> DeckEntries(CollectionDeck deck)
    {
        var entries = new System.Collections.Generic.List<LocalDeckCard>();
        // Read the slots directly (what GetCardsWithPremiumStatus flattens) to skip a lossy card id -> dbid -> card id round trip.
        foreach (var slot in deck.GetSlots())
            foreach (var premium in new[] { TAG_PREMIUM.DIAMOND, TAG_PREMIUM.SIGNATURE, TAG_PREMIUM.GOLDEN, TAG_PREMIUM.NORMAL })
                for (int copy = slot.GetCount(premium); copy > 0; copy--)
                    entries.Add(new LocalDeckCard { Id = slot.CardID, Premium = (int)premium });
        return entries;
    }

    internal static void ValidateNativeDeckCards(string[] ids)
    {
        LocalDecks.ValidateNativeSave(ids, Array.Empty<LocalSideboard>());
    }

    internal static void ProjectSavedDraft(LocalCustomDeck saved, CollectionDeck working, CollectionDeck prior,
        NetCache.DeckHeader header, long rank)
    {
        var expected = ExpectedHeader(saved.Id, LocalProfile.LedgerDeck(saved.Name, saved.Hero, saved.Cards, saved.Sideboards, false),
            saved.Name, rank, saved.Cosmetics);
        working.SetRuneOrder(NativeDeckRunes.Order(saved.Hero, saved.Cards));
        working.SortOrder = rank;
        // A removed skin leaves the skin id behind with HeroOverridden false; project the saved cosmetics exactly.
        working.HeroCardID = expected.Hero; working.HeroOverridden = expected.HeroOverridden;
        working.CosmeticCoinID = expected.CosmeticCoin; working.RandomCoinUseFavorite = expected.RandomCoinUseFavorite;
        working.CardBackID = expected.CardBack; working.RandomHeroUseFavorite = expected.RandomHeroUseFavorite;
        working.PetID = expected.Pet; working.PetVariantID = expected.PetVariant; working.RandomPetUseFavorite = expected.RandomPetUseFavorite;
        prior.CopyFrom(working);
        CopyHeader(expected, header);
    }

    internal static NetCache.DeckHeader ExpectedHeader(long id, LocalDeck deck, string name, long rank, LocalDeckCosmetics cosmetics = null)
    {
        RuneType[] runes = NativeDeckRunes.Order(deck.Hero, deck.CardIds);
        int? random = cosmetics != null && cosmetics.RandomCardBack ? RandomCardBackId() : null;
        return new NetCache.DeckHeader {
            ID = id, Name = name, Hero = cosmetics?.HeroSkin ?? deck.Hero, HeroPower = deck.Hero + "bp",
            HeroOverridden = cosmetics?.HeroSkin != null, RandomHeroUseFavorite = cosmetics?.RandomHeroUseFavorite ?? false,
            CosmeticCoin = cosmetics?.Coin is int coin && !string.IsNullOrEmpty(NativeMatch.NativeCoinCard(coin)) ? cosmetics.Coin : null, RandomCoinUseFavorite = cosmetics?.RandomCoinUseFavorite ?? true,
            Type = DeckType.NORMAL_DECK, CardBack = random ?? cosmetics?.CardBack, FormatType = LocalDeckFormat, SortOrder = rank,
            SourceType = DeckSourceType.DECK_SOURCE_TYPE_NORMAL, Rune1 = runes[0], Rune2 = runes[1], Rune3 = runes[2],
            Pet = cosmetics?.PetFamily == null ? (int?)null : LocalShop.Default.PetNativeId(cosmetics.PetFamily),
            PetVariant = cosmetics?.PetForm == null ? (int?)null : NativeMatch.NativePetVariant(cosmetics.PetForm),
            RandomPetUseFavorite = cosmetics?.RandomPetUseFavorite ?? false
        };
    }

    // Native "random card back" marker (a CardBack DBF record). Unknown before the DBF loads: treat nothing as random.
    internal static Func<int?> RandomCardBackSource = () =>
    {
        try { int id = CardBackManager.Get()?.TheRandomCardBackID ?? 0; return id > 0 ? id : (int?)null; }
        catch (Exception) { return null; }
    };
    private static int? RandomCardBackId() => RandomCardBackSource?.Invoke();

    internal static LocalDeckCosmetics NativeDeckCosmetics(LocalProfile profile, CollectionDeck deck)
    {
        int? random = RandomCardBackId();
        bool randomBack = random.HasValue && deck.CardBackID == random;
        string family = deck.PetID.HasValue ? profile.OwnedPets.SingleOrDefault(pet => LocalShop.Default.PetNativeId(pet) == deck.PetID.Value) : null;
        if (deck.PetID.HasValue && family == null || deck.PetVariantID.HasValue && family == null)
            throw new InvalidOperationException("Pet is not owned.");
        string form = deck.PetVariantID.HasValue ? LocalShop.Default.FormsForPet(family)
            .Where(profile.CanUsePetForm).SingleOrDefault(card => NativeMatch.NativePetVariant(card) == deck.PetVariantID.Value) : null;
        if (deck.PetVariantID.HasValue && form == null) throw new InvalidOperationException("Pet form is not unlocked.");
        // An unavailable saved coin projects as Random; unrelated native edits must not erase that selection.
        int? coin = deck.CosmeticCoinID;
        var saved = profile.DeckCosmetics(deck.ID);
        if (!coin.HasValue && saved?.Coin is int stored && string.IsNullOrEmpty(NativeMatch.NativeCoinCard(stored))) coin = stored;
        return new LocalDeckCosmetics {
            Coin = coin, RandomCoinUseFavorite = deck.RandomCoinUseFavorite,
            CardBack = randomBack ? null : deck.CardBackID, RandomCardBack = randomBack,
            HeroSkin = deck.HeroOverridden ? deck.HeroCardID : null, RandomHeroUseFavorite = deck.RandomHeroUseFavorite,
            PetFamily = family, PetForm = form, RandomPetUseFavorite = deck.RandomPetUseFavorite };
    }

    // Card-back / hero-skin tray binding (NativeDeckEditor.BindCosmeticTray), shared with the headless checks: a tray
    // holding no deck or another deck object is bound to the edited personal deck before it writes.
    internal static bool BindTrayDeck(Func<CollectionDeck> current, Action<CollectionDeck> bind, CollectionDeck edited, bool persisted)
    {
        if (edited == null || !persisted || !NativeUIProjection.IsPersonalId(edited.ID) || ReferenceEquals(current(), edited)) return false;
        bind(edited);
        return true;
    }

    // A deck's class hero: the deck tray puts a hero skin's card id in HeroCardID.
    internal static string NativeBaseHero(CollectionDeck deck)
    {
        string hero = deck?.HeroCardID;
        if (hero == null || LocalDecks.PlayerHeroes.Contains(hero)) return hero;
        return LocalShop.Default.HeroSkinClass(hero) ?? hero;
    }

    internal static void ProjectMissingDraftCards(CollectionDeck native, LocalCustomDeck saved, bool force = false)
    {
        // Keep the loader's ownership split when all copies survived; template filling restores discarded ghosts.
        if (!force && SameDeckEntries(DeckEntries(native), saved.CardEntries)) return;
        var template = new CollectionManager.TemplateDeck { m_title = saved.Name };
        RuneType[] runes = NativeDeckRunes.Order(saved.Hero, saved.Cards);
        template.m_rune1 = runes[0]; template.m_rune2 = runes[1]; template.m_rune3 = runes[2];
        foreach (var group in saved.Cards.GroupBy(id => id)) template.m_cardIds.Add(group.Key, group.Count());
        native.FillFromTemplateDeck(template);
        RestoreSavedPremiums(native, saved.CardEntries);
    }

    private static void RefreshCraftingOwnership(CollectionDeck edited, IEnumerable<string> cardIds)
    {
        var manager = CollectionManager.Get();
        var changed = new HashSet<string>(cardIds, StringComparer.Ordinal);
        var slots = new[] { edited }.Concat(edited.GetAllSideboards().Values)
            .SelectMany(deck => deck.GetSlots().Select(slot => new { Deck = deck, Slot = slot }));
        foreach (var group in slots.Where(row => changed.Contains(row.Slot.CardID)).GroupBy(row => row.Slot.CardID))
        {
            var available = Enum.GetValues(typeof(TAG_PREMIUM)).Cast<TAG_PREMIUM>().ToDictionary(premium => premium,
                premium => manager.GetOwnedCount(group.Key, premium) + manager.GetTrialCardCount(group.Key, premium));
            foreach (var row in group)
            {
                var owned = available.ToDictionary(pair => pair.Key, pair => Math.Min(row.Slot.GetCount(pair.Key), Math.Max(pair.Value, 0)));
                foreach (var pair in owned) available[pair.Key] -= pair.Value;
                int ownedCount = owned.Values.Sum();
                row.Slot.Owned = ownedCount > 0;
                if (ownedCount == 0 || ownedCount == row.Slot.Count) continue;
                // Native reconciliation also removes sideboards and changes premiums; only split its display slots here.
                CollectionDeckSlot missing = null;
                foreach (var pair in owned)
                {
                    int count = row.Slot.GetCount(pair.Key) - pair.Value;
                    if (count > 0)
                    {
                        if (missing == null) missing = row.Deck.InsertSlotWithCard(group.Key, pair.Key, false, count)
                            ?? throw new InvalidOperationException("Cannot split crafting ownership slot: " + group.Key);
                        else missing.SetCount(count, pair.Key);
                    }
                    row.Slot.SetCount(pair.Value, pair.Key);
                }
            }
        }
        foreach (var slot in edited.GetSlots())
        {
            var primary = edited.FindPrimaryFabledCardSlot(slot.GetEntityDef());
            if (primary != null && changed.Contains(primary.CardID)) slot.Owned = primary.Owned;
        }
    }

    internal static void RefreshUpgradedDecks(string cardId, int premium = 1, int? lower = null)
    {
        var manager = CollectionManager.Get();
        foreach (var saved in Profile.CustomDecks)
        {
            if (!saved.Cards.Contains(cardId) && !saved.Sideboards.Any(board => board.Cards.Contains(cardId))) continue;
            foreach (var native in new[] { manager.GetDeck(saved.Id), manager.GetBaseDeck(saved.Id) }.Where(deck => deck != null).Distinct())
            {
                var recipe = saved;
                if (native.IsBeingEdited() || ReferenceEquals(native, manager.GetEditedDeck()))
                {
                    // Keep unsaved edits; reconcile only their version demand against the committed ownership.
                    recipe = new LocalCustomDeck { Name = native.Name, Hero = NativeBaseHero(native),
                        CardEntries = DeckEntries(native), Sideboards = ReadSideboards(native).ToList() };
                    LocalCrafting.ReconcileUpgradeDeck(recipe, cardId, lower ?? Profile.OwnedCount(cardId), Profile.OwnedCount(cardId, premium), premium);
                    LocalCrafting.AssignOwnedPremiums(recipe, Profile.OwnedCount);
                }
                native.ClearSideboards();
                ProjectMissingDraftCards(native, recipe, true);
                ProjectSideboards(native, recipe.Sideboards);
                RefreshCraftingOwnership(native, new[] { cardId });
            }
        }
        CollectionDeckTray.Get()?.GetCardsContent()?.UpdateCardList();
        CollectionDeckTray.Get()?.GetDecksContent()?.RefreshMissingCardIndicators();
    }

    // Template filling hands out the best owned version of every card; put the saved versions back where owned.
    // Missing copies stay Normal ghosts (native's own fallback), the draft itself is never edited.
    private static void RestoreSavedPremiums(CollectionDeck native, System.Collections.Generic.IEnumerable<LocalDeckCard> entries)
    {
        var manager = CollectionManager.Get();
        RestoreDraftVersions(native, entries, (id, premium) => manager.GetOwnedCount(id, (TAG_PREMIUM)premium)
            + manager.GetTrialCardCount(id, (TAG_PREMIUM)premium));
    }

    internal static void RestoreDraftVersions(CollectionDeck native, System.Collections.Generic.IEnumerable<LocalDeckCard> entries,
        Func<string, int, int> nativeOwned)
    {
        foreach (var group in entries.GroupBy(card => card.Id))
        {
            // Companions are never in the collection; they follow their primary below.
            if (IsFabledCompanionSlot(native, group.Key)) continue;
            var slots = native.GetSlots().Where(slot => slot.CardID == group.Key).ToList();
            var owned = slots.Where(slot => slot.Owned).ToList();
            if (owned.Count != 1 || slots.Sum(slot => slot.Count) != group.Count()) continue;
            int capacity = owned[0].Count;
            var wanted = new int[4]; var available = new int[4]; var assigned = new int[4];
            foreach (var card in group) wanted[card.Premium]++;
            for (int premium = 0; premium < 4; premium++)
                available[premium] = Math.Max(nativeOwned(group.Key, premium), 0);
            for (int premium = 3; premium >= 0; premium--)
            {
                assigned[premium] = Math.Min(Math.Min(wanted[premium], available[premium]), capacity);
                capacity -= assigned[premium];
            }
            // Owned slots must stay filled: spend leftover capacity on unspent versions, Normal first.
            for (int premium = 0; premium < 4 && capacity > 0; premium++)
            {
                int extra = Math.Min(available[premium] - assigned[premium], capacity);
                assigned[premium] += extra; capacity -= extra;
            }
            if (capacity != 0) continue;
            // Raise before lowering so the slot never reads empty.
            for (int premium = 0; premium < 4; premium++)
                if (assigned[premium] > 0) owned[0].SetCount(assigned[premium], (TAG_PREMIUM)premium);
            for (int premium = 0; premium < 4; premium++)
                if (assigned[premium] == 0 && owned[0].GetCount((TAG_PREMIUM)premium) != 0) owned[0].SetCount(0, (TAG_PREMIUM)premium);
        }
        AlignFabledCompanions(native);
    }

    private static bool IsFabledCompanionSlot(CollectionDeck native, string id)
    {
        return native.GetSlots().Any(slot => LocalDecks.FabledBundle(slot.CardID).Contains(id));
    }

    // Native gives a Fabled companion its primary's version when the bundle is added (AddFabledBundledCard). Template
    // filling adds the primary in its best owned version (Golden on an all-cosmetics save), so once the primary's saved
    // version is restored its companions must follow, or the deck shows (and saves) Golden companions of a Normal primary.
    internal static void AlignFabledCompanions(CollectionDeck native)
    {
        foreach (var primary in native.GetSlots().ToArray())
        {
            var bundle = LocalDecks.FabledBundle(primary.CardID);
            var versions = Enumerable.Range(0, 4).Where(premium => primary.GetCount((TAG_PREMIUM)premium) > 0).ToArray();
            if (bundle.Count == 0 || versions.Length != 1) continue;
            var version = (TAG_PREMIUM)versions[0];
            foreach (var companion in native.GetSlots().Where(slot => bundle.Contains(slot.CardID)))
            {
                int count = companion.Count;
                if (count == 0 || companion.GetCount(version) == count) continue;
                // Raise before lowering so the slot never reads empty.
                companion.SetCount(count, version);
                for (int premium = 0; premium < 4; premium++)
                    if ((TAG_PREMIUM)premium != version && companion.GetCount((TAG_PREMIUM)premium) != 0)
                        companion.SetCount(0, (TAG_PREMIUM)premium);
            }
        }
    }

    internal static bool IsProjectedDeckSlot(CollectionDeck deck, CollectionDeckSlot slot) => deck != null && slot != null
        && (deck.GetSlots().Contains(slot) || deck.GetAllSideboards().Values.Any(side => side.GetSlots().Contains(slot)));

    internal static CollectionDeckSlot LocalDeckSuggestionSlot(CollectionDeck deck, CollectionDeckSlot slot)
    {
        // Match ShowDeckHelper's bundle redirect without its retail login prerequisite.
        var definition = slot?.GetEntityDef();
        return definition != null && definition.HasTag(GAME_TAG.IS_FABLED_BUNDLE_CARD)
            ? deck.FindPrimaryFabledCardSlot(definition) ?? slot : slot;
    }

    private static bool SupportedNativeEnvelope(CollectionDeck deck) => deck != null
        && deck.Type == DeckType.NORMAL_DECK && deck.FormatType == LocalDeckFormat
        && deck.SourceType == DeckSourceType.DECK_SOURCE_TYPE_NORMAL
        && (!deck.HeroOverridden || LocalDecks.PlayerHeroes.Contains(NativeBaseHero(deck)))
        && deck.CreatedFromShareableDeck == null && SupportedSideboards(deck)
        && string.IsNullOrEmpty(deck.UIHeroOverrideCardID)
        && deck.GetSlots().All(slot => slot != null);

    private static bool SameFixedPersonalEnvelope(CollectionDeck first, CollectionDeck second) =>
        // Card back, hero skin and the random-hero toggle are per-deck cosmetics the player may change. Pet fields
        // are validated separately as per-deck cosmetics.
        first != null && second != null && first.ID == second.ID && first.Type == second.Type
        && NativeBaseHero(first) == NativeBaseHero(second)
        && first.NeedsName == second.NeedsName && first.SeasonId == second.SeasonId
        && first.BrawlLibraryItemId == second.BrawlLibraryItemId && first.FormatType == second.FormatType
        && first.SourceType == second.SourceType && first.UIHeroOverrideCardID == second.UIHeroOverrideCardID
        && first.UIHeroOverridePremium == second.UIHeroOverridePremium && first.Locked == second.Locked
        && (first.GetRuneOrder().SequenceEqual(second.GetRuneOrder())
            || NativeBaseHero(first) == "HERO_11" && NativeBaseHero(second) == "HERO_11"
                && NativeDeckRunes.Compatible("HERO_11", first.GetCardsWithCardID(), first.GetRuneOrder()))
        && first.CreatedFromShareableDeck == null && second.CreatedFromShareableDeck == null
        && SupportedSideboards(first) && SupportedSideboards(second);

    internal static LocalSideboard[] ReadSideboards(CollectionDeck deck)
    {
        var rows = new System.Collections.Generic.List<LocalSideboard>();
        foreach (var row in deck.GetAllSideboards().OrderBy(row => row.Key, StringComparer.Ordinal))
            rows.Add(new LocalSideboard { Source = row.Key, CardEntries = DeckEntries(row.Value) });
        return rows.ToArray();
    }

    internal static void ProjectSideboards(CollectionDeck deck, System.Collections.Generic.IEnumerable<LocalSideboard> rows)
    {
        deck.ClearSideboards();
        foreach (var row in rows)
        {
            var sideboard = deck.GetOrCreateSideboard(row.Source, TAG_PREMIUM.NORMAL);
            foreach (var card in row.CardEntries)
            {
                // Native add sets Owned and splits blue ghosts exactly as it does in the main deck;
                // a version that is no longer owned falls back to a Normal ghost there.
                if (!sideboard.AddCard(card.Id, (TAG_PREMIUM)card.Premium, allowInvalid: true))
                    throw new InvalidOperationException("Native sideboard rejected saved card: " + row.Source + "/" + card.Id);
            }
            sideboard.InitCardCount();
        }
    }

    private static bool SupportedSideboards(CollectionDeck deck)
    {
        if (deck.GetAllSideboards().Any(row => row.Value == null || row.Value.GetSlots().Any(slot => slot == null))) return false;
        try { LocalDecks.ValidateNativeSave(deck.GetCardsWithCardID(), ReadSideboards(deck)); return true; }
        catch (InvalidOperationException) { return false; }
        catch (ArgumentException) { return false; }
    }

    // Ids only: native may swap an unowned version for a Normal ghost on either side of a comparison.
    internal static bool SameSideboards(CollectionDeck first, CollectionDeck second) => SideboardSignature(ReadSideboards(first)) == SideboardSignature(ReadSideboards(second));
    internal static string SideboardSignature(System.Collections.Generic.IEnumerable<LocalSideboard> rows) => string.Join(";", rows
        .OrderBy(row => row.Source, StringComparer.Ordinal).Select(row => row.Source + ":" + string.Join(",", row.Cards.OrderBy(id => id, StringComparer.Ordinal))));

    private static void CopyHeader(NetCache.DeckHeader source, NetCache.DeckHeader target)
    {
        target.ID = source.ID; target.Name = source.Name; target.CardBack = source.CardBack;
        target.CosmeticCoin = source.CosmeticCoin; target.RandomCoinUseFavorite = source.RandomCoinUseFavorite;
        target.Hero = source.Hero; target.UIHeroOverride = source.UIHeroOverride;
        target.UIHeroOverridePremium = source.UIHeroOverridePremium; target.HeroPower = source.HeroPower;
        target.Type = source.Type; target.HeroOverridden = source.HeroOverridden;
        target.RandomHeroUseFavorite = source.RandomHeroUseFavorite; target.SeasonId = source.SeasonId;
        target.BrawlLibraryItemId = source.BrawlLibraryItemId; target.NeedsName = source.NeedsName;
        target.SortOrder = source.SortOrder; target.Rune1 = source.Rune1; target.Rune2 = source.Rune2;
        target.Rune3 = source.Rune3; target.FormatType = source.FormatType; target.Locked = source.Locked;
        target.SourceType = source.SourceType; target.Pet = source.Pet; target.PetVariant = source.PetVariant;
        target.RandomPetUseFavorite = source.RandomPetUseFavorite;
    }

}
