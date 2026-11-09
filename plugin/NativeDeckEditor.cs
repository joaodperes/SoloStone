using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Blizzard.T5.Core.Utils;
using Blizzard.T5.Jobs;
using HarmonyLib;
using Hearthstone.Core;
using PegasusShared;

internal static partial class NativeUIProbe
{
    internal static void InitializeDeckCopyRules(Harmony harmony)
    {
        // No contraband ownership prefix: retail CanCardBeAddedAsOwned owns sideboard cards only through the collection.
        InitializeSideboardSubset(harmony);
        Hook(harmony, typeof(DeckRule_CountCopiesOfEachCard), "GetCopiesInDeck", nameof(LocalDeckCopyCount),
            new[] { typeof(string), typeof(CollectionDeck), typeof(bool) });
    }

    // Use the same counterpart groups for the native editor and its invalid-deck UI.
    private static bool LocalDeckCopyCount(string __0, CollectionDeck __1, ref int __result)
    {
        if (!active || __1 == null || __1.HasSideboardCards || !NativeUIProjection.IsPersonalId(__1.ID)
            || Profile.GetDraft(__1.ID) == null || !LocalDecks.CustomCards.Any(card => card.Id == __0)) return true;
        __result = NativeUIProjection.DeckCopyCount(__1.GetCardsWithCardID(), __0);
        return false;
    }

    internal static void RefreshCoreProgression()
    {
        var manager = CollectionManager.Get() ?? throw new InvalidOperationException("Native collection unavailable for Core unlocks.");
        var grants = NativeUIProjection.CoreGrantCounts(Profile.Collection, id => manager.GetOwnedCount(id, TAG_PREMIUM.NORMAL));
        AddNativeGrants(grants);
        AddNativePremiumGrants();
        RefreshCosmetics();
    }

    private static long editedDraftId;
    private static string lastDeckOperation = "none";
    private static string lastDeckError = "";
    private static readonly HashSet<long> preparedDeletes = new HashSet<long>();
    private static readonly HashSet<long> deletedDrafts = new HashSet<long>();

    private sealed class NativeRankTarget
    {
        internal CollectionDeck Working;
        internal CollectionDeck BaseDeck;
        internal NetCache.DeckHeader Header;
        internal long Rank;
    }

    private static bool CreateLocalDraft(CollectionManager __instance, DeckType deckType, string name,
        string heroCardID, DeckSourceType deckSourceType, string pastedDeckHashString, bool isHeroOverridden)
    {
        if (!active) return true;
        long candidateId = Profile.NextCustomDeckId;
        bool allowed = NativeUIProjection.CanCreateDraft(deckType == DeckType.NORMAL_DECK,
            deckSourceType == DeckSourceType.DECK_SOURCE_TYPE_NORMAL && pastedDeckHashString == null,
            Heroes.Contains(heroCardID), Options.GetFormatType() == LocalDeckFormat,
            Profile.CustomDecks.Count, SessionLoanerKeys().Count());
        if (!NativeUIProjection.OwnsCreateRequest(active, CurrentCollectionScene(), allowed, isHeroOverridden))
            throw new InvalidOperationException("Unsupported local deck creation request.");

        if (!NativeUIProjection.TryCreateDraft(() => Profile.CreateDraft(name, heroCardID),
            error => ReportDeckError("create", candidateId, error), out LocalCustomDeck saved)) return false;
        try
        {
            decks.Add(saved.Id, LocalProfile.LedgerDeck(saved.Name, saved.Hero, saved.Cards, saved.Sideboards, false));
            var header = ExpectedHeader(saved.Id);
            editedDraftId = saved.Id;
            lastDeckOperation = "create:" + saved.Id;
            lastDeckError = "";
            MethodInfo created = Required(typeof(CollectionManager), "OnDeckCreated",
                new[] { typeof(NetCache.DeckHeader), typeof(int?) });
            Processor.ScheduleCallback(0.5f, false, _ => created.Invoke(__instance, new object[] { header, null }));
        }
        catch (Exception error)
        {
            ReportCreateRefreshError(saved.Id, error);
        }
        try { WriteStatus(); }
        catch (Exception statusError) { log.LogError("NATIVE_DECK create status write failed: " + statusError); }
        return false;
    }

    // Live 2026-10-25: on a newly created deck the card-back and hero-skin presses wrote nowhere: the trays bind their
    // m_currentDeck only when their content animates in, and the creation session (deferred OnDeckCreated -> ShowDeck ->
    // StartEditingDeck) can leave it unbound (null: the press returns early) or on another deck object (the press never
    // reaches the deck Done saves). Every tray write path first binds the tray to the deck being edited, exactly as its
    // own PreAnimateContentEntrance does; existing-deck sessions are already bound and are left untouched.
    // The tray types are only touched here (not in a static initializer): loading them needs client-only assemblies.
    private static FieldInfo cardBackTrayDeck, heroSkinTrayDeck, petTrayDeck, coinTrayDeck;

    private static void InitializeCosmeticTrayBinding(Harmony harmony)
    {
        var writes = new[] {
            (typeof(DeckTrayCardBackContent), "AnimateInCardBack"), (typeof(DeckTrayCardBackContent), "UpdateCardBack"),
            (typeof(DeckTrayCardBackContent), "ToggleFavoritesOnly"), (typeof(DeckTrayCardBackContent), "RemoveCardBack"),
            (typeof(DeckTrayCardBackContent), "GrabCardBack"),
            (typeof(DeckTrayHeroSkinContent), "AnimateInHeroSkin"), (typeof(DeckTrayHeroSkinContent), "UpdateHeroSkin"),
            (typeof(DeckTrayHeroSkinContent), "ToggleFavoritesOnly"), (typeof(DeckTrayHeroSkinContent), "RemoveHeroSkin"),
            (typeof(DeckTrayHeroSkinContent), "GrabHeroSkin"),
            (typeof(DeckTrayCosmeticCoinContent), "AnimateInCosmeticCoin"), (typeof(DeckTrayCosmeticCoinContent), "UpdateCosmeticCoin"),
            (typeof(DeckTrayCosmeticCoinContent), "ToggleFavoritesOnly"), (typeof(DeckTrayCosmeticCoinContent), "RemoveCosmeticCoin"),
            (typeof(DeckTrayCosmeticCoinContent), "GrabCosmeticCoin"),
            (typeof(DeckTrayPetContent), "UpdatePet"), (typeof(DeckTrayPetContent), "UpdatePetVariant"),
            (typeof(DeckTrayPetContent), "RemovePet"), (typeof(DeckTrayPetContent), "GrabPet"),
            (typeof(DeckTrayPetContent), "SetRandomPetUseFavorite")
        };
        cardBackTrayDeck = AccessTools.Field(typeof(DeckTrayCardBackContent), "m_currentDeck")
            ?? throw new MissingFieldException("DeckTrayCardBackContent", "m_currentDeck");
        heroSkinTrayDeck = AccessTools.Field(typeof(DeckTrayHeroSkinContent), "m_currentDeck")
            ?? throw new MissingFieldException("DeckTrayHeroSkinContent", "m_currentDeck");
        coinTrayDeck = AccessTools.Field(typeof(DeckTrayCosmeticCoinContent), "m_currentDeck")
            ?? throw new MissingFieldException("DeckTrayCosmeticCoinContent", "m_currentDeck");
        petTrayDeck = AccessTools.Field(typeof(DeckTrayPetContent), "m_currentDeck")
            ?? throw new MissingFieldException("DeckTrayPetContent", "m_currentDeck");
        foreach (var (type, method) in writes)
            harmony.Patch(Required(type, method, null), prefix: new HarmonyMethod(typeof(NativeUIProbe), nameof(BindCosmeticTray)));
    }

    // Prefix only: never throws into the tray and never skips the native write.
    private static void BindCosmeticTray(object __instance)
    {
        try
        {
            if (!active || !CurrentCollectionScene()) return;
            FieldInfo field = __instance is DeckTrayCardBackContent ? cardBackTrayDeck
                : __instance is DeckTrayHeroSkinContent ? heroSkinTrayDeck
                : __instance is DeckTrayPetContent ? petTrayDeck
                : __instance is DeckTrayCosmeticCoinContent ? coinTrayDeck : null;
            var manager = CollectionManager.Get();
            if (field == null || manager == null || !manager.IsInEditMode()) return;
            CollectionDeck edited = manager.GetEditedDeck();
            if (BindTrayDeck(() => field.GetValue(__instance) as CollectionDeck, deck => field.SetValue(__instance, deck),
                edited, edited != null && Profile?.GetDraft(edited.ID) != null))
                log.LogInfo("NATIVE_DECK bound " + __instance.GetType().Name + " to edited deck " + edited.ID);
        }
        catch (Exception error)
        {
            try { log.LogError("NATIVE_DECK cosmetic tray binding failed: " + error); } catch { /* never escape Harmony */ }
        }
    }

    private static bool SaveLocalDraft(CollectionDeck __instance, CollectionDeck.ChangeSource __0)
    {
        if (active && __instance is SideboardDeck sideboard) __instance = sideboard.MainDeck;
        bool supportedSource = __0 == CollectionDeck.ChangeSource.MarkDeckAsSeen
            || __0 == CollectionDeck.ChangeSource.PocoSetDeckName
            || __0 == CollectionDeck.ChangeSource.OnScenePreUnload
            || __0 == CollectionDeck.ChangeSource.SaveCurrentDeck
            || __0 == CollectionDeck.ChangeSource.StopDragToReorder
            || __0 == CollectionDeck.ChangeSource.ReconcileCardOwnership;
        bool ownershipReconcile = __0 == CollectionDeck.ChangeSource.ReconcileCardOwnership;
        bool packRewardReconcile = ownershipReconcile && PackOpeningContext();
        if (!NativeUIProjection.SupportsLocalDeckSave(active,
            supportedSource && LocalCollectionContext(), supportedSource, packRewardReconcile)) return true;
        long id = __instance.ID;
        bool committed = false;
        try
        {
            LocalCustomDeck persisted = Profile.GetDraft(id);
            bool personal = NativeUIProjection.IsPersonalId(id);
            bool loaner = IsLoanerId(id);
            bool exact = persisted != null && ExactProjectedDeck(__instance, id)
                && ExactProjectedDeck(CollectionManager.Get().GetBaseDeck(id), id) && ExactProjectedHeader(id);
            bool exactLoaner = loaner && ExactProjectedDeck(__instance, id)
                && ExactProjectedDeck(CollectionManager.Get().GetBaseDeck(id), id) && ExactProjectedHeader(id);
            NativeDeckSaveDisposition disposition = NativeUIProjection.DeckSaveAction(active, personal,
                persisted != null, deletedDrafts.Contains(id), exact, loaner,
                __0 == CollectionDeck.ChangeSource.StopDragToReorder, ownershipReconcile, exactLoaner, craftingSynchronize);
            if (disposition == NativeDeckSaveDisposition.Ignore && ownershipReconcile && craftingSynchronize && !exact && personal)
                log.LogInfo("NATIVE_DECK crafting reconcile ignored for " + id + " (profile deck is authoritative): "
                    + ExactProjectedDeckProblem(__instance, id));
            if (disposition == NativeDeckSaveDisposition.Passthrough) return true;
            if (disposition == NativeDeckSaveDisposition.Ignore)
            {
                if (personal && persisted == null) log.LogInfo("NATIVE_DECK ignored trailing save for deleted draft " + id);
                return false;
            }
            if (disposition == NativeDeckSaveDisposition.Reject && ownershipReconcile)
                log.LogWarning("NATIVE_DECK ownership reconcile differs for " + id + ": edited=" + (ExactProjectedDeckProblem(__instance, id) ?? "exact")
                    + "; base=" + (ExactProjectedDeckProblem(CollectionManager.Get().GetBaseDeck(id), id) ?? "exact")
                    + "; header=" + ExactProjectedHeader(id));
            if (disposition == NativeDeckSaveDisposition.Reject)
                throw new InvalidOperationException((ownershipReconcile
                    ? "Reward reconciliation changed an authoritative local deck: " + id
                    : loaner ? "Loaner decks are immutable: " + id : "Unknown local deck: " + id));
            CollectionDeck prior = CollectionManager.Get().GetBaseDeck(id);
            if (!ExactProjectedDeck(prior, id) || !ExactProjectedHeader(id))
                throw new InvalidOperationException("Unsupported local deck variant or sideboard.");
            if (IsUnrestoredCraftingRecipe(__instance, persisted))
                throw new InvalidOperationException("Crafting reconciliation is incomplete. Reopen the collection before saving this deck.");
            CommitLocalDraft(Profile, __instance, prior);
            committed = true;
            unrestoredCraftingRecipes.Remove(id);

            LocalCustomDeck saved = Profile.GetDraft(id);
            decks[id] = LocalProfile.LedgerDeck(saved.Name, saved.Hero, saved.Cards, saved.Sideboards, false);
            CollectionDeck baseDeck = CollectionManager.Get().GetBaseDeck(id)
                ?? throw new InvalidOperationException("Native base deck is missing: " + id);
            NetCache.DeckHeader header = DeckHeaders().Decks.Single(deck => deck.ID == id);
            ProjectSavedDraft(saved, __instance, baseDeck, header, ExpectedRanks()[id]);
            __instance.OnContentChangesComplete();
            __instance.OnNameChangeComplete();
            editedDraftId = id;
            lastDeckOperation = "save:" + id + ":cards=" + saved.Cards.Count;
            lastDeckError = "";
        }
        catch (Exception error)
        {
            if (committed)
            {
                ReportSaveRefreshError(id, error);
                return false;
            }
            ReportDeckError("save", id, error);
            throw;
        }
        try { WriteStatus(); }
        catch (Exception statusError) { log.LogError("NATIVE_DECK save status write failed: " + statusError); }
        return false;
    }

    internal static bool IsUnrestoredCraftingRecipe(CollectionDeck native, LocalCustomDeck saved)
    {
        return saved != null && native.Name == saved.Name && NativeBaseHero(native) == saved.Hero
            && unrestoredCraftingRecipes.TryGetValue(saved.Id, out string reduced)
            && CraftingRecipeSignature(native) == reduced;
    }

    private static bool ConfirmListDeckDelete(CollectionDeckBoxVisual __instance, AlertPopup.Response response, object userData)
    {
        if (!NativeUIProjection.OwnsDeleteResponse(active, CurrentCollectionScene(),
            response == AlertPopup.Response.CONFIRM)) return true;
        AlertPopup confirmation = OwnedDeleteConfirmation(__instance, userData);
        if (confirmation == null) return true;
        var sections = Field(CollectionDeckTray.Get()?.GetDecksContent(), "m_traySections") as System.Collections.IEnumerable;
        bool currentBox = sections != null && sections.Cast<object>().OfType<TraySection>()
            .Any(section => ReferenceEquals(section.m_deckBox, __instance));
        return TryPrepareLocalDelete(__instance.GetDeckID(), confirmation, currentBox);
    }

    private static bool ConfirmEditingDeckDelete(DeckOptionsMenu __instance, AlertPopup.Response response, object userData)
    {
        if (!NativeUIProjection.OwnsDeleteResponse(active, CurrentCollectionScene(),
            response == AlertPopup.Response.CONFIRM)) return true;
        AlertPopup confirmation = OwnedDeleteConfirmation(__instance, userData);
        if (confirmation == null) return true;
        CollectionDeck edited = CollectionManager.Get()?.GetEditedDeck();
        return TryPrepareLocalDelete(edited?.ID ?? 0, confirmation,
            edited != null && ReferenceEquals(Field(__instance, "m_deck"), edited));
    }

    private static AlertPopup OwnedDeleteConfirmation(object owner, object userData)
    {
        var popup = Field(DialogManager.Get(), "m_currentDialog") as AlertPopup;
        AlertPopup.PopupInfo info = popup?.GetInfo();
        var callback = info?.m_responseCallback;
        return info != null && info.m_responseDisplay == AlertPopup.ResponseDisplay.CONFIRM_CANCEL
            && NativeUIProjection.MatchesDeleteConfirmation(callback, owner,
                Required(owner.GetType(), "OnDeleteButtonConfirmationResponse",
                    new[] { typeof(AlertPopup.Response), typeof(object) }), info.m_responseUserData, userData) ? popup : null;
    }

    private static bool PrepareDiscardDeck(CollectionDeckTray __instance, bool deleteDeck, ref bool __result)
    {
        if (!active || !CurrentCollectionScene() || !deleteDeck
            || !ReferenceEquals(__instance, CollectionDeckTray.Get())) return true;
        // Preserve native early exits before any discard side effects or local commit.
        if (!__instance.IsShowingDeckContents() || (__instance.GetCurrentContentType() != DeckTray.DeckContentTypes.INVALID
            && __instance.GetCurrentContent() != null && !__instance.GetCurrentContent().IsModeActive())) return true;
        CollectionDeck edited = CollectionManager.Get()?.GetEditedDeck();
        if (TryPrepareLocalDelete(edited?.ID ?? 0, null, edited != null)) return true;
        __result = false;
        return false;
    }

    private static bool PrepareEditingDeckDelete(CollectionDeckTray __instance)
    {
        if (!active || !CurrentCollectionScene() || !ReferenceEquals(__instance, CollectionDeckTray.Get())) return true;
        CollectionDeck edited = CollectionManager.Get()?.GetEditedDeck();
        return TryPrepareLocalDelete(edited?.ID ?? 0, null, edited != null);
    }

    private static bool DeleteLocalDraftBeforeNative(DeckTrayDeckListContent __instance, long __0)
    {
        if (!active || !CurrentCollectionScene()
            || !ReferenceEquals(__instance, CollectionDeckTray.Get()?.GetDecksContent())) return true;
        return TryPrepareLocalDelete(__0, null, true);
    }

    private static bool TryPrepareLocalDelete(long deckId, AlertPopup confirmation, bool validOwner) =>
        NativeUIProjection.TryPrepareDelete(() => {
            if (!validOwner) throw new InvalidOperationException("The delete target changed. Reopen Collection and retry.");
            if (preparedDeletes.Contains(deckId)) return;
            PrepareLocalDelete(deckId, confirmation);
        }, error => ReportDeleteFailure(deckId, error, confirmation));

    private static void PrepareLocalDelete(long deckId, AlertPopup confirmation)
    {
        // #57: "deleting" a Core starter loaner hides it for this save; personal decks keep their own path below.
        string starter = NativeUIProjection.HideableLoanerKey(deckId);
        if (starter != null && decks.ContainsKey(deckId)) { HideStarterLoaner(deckId, starter, confirmation); return; }
        bool committed = false;
        try
        {
            if (!NativeUIProjection.IsPersonalId(deckId))
                throw new InvalidOperationException("Loaner decks cannot be deleted: " + deckId);
            if (!NativeUIProjection.CanMutatePersonal(deckId, Profile.CustomDecks.Select(deck => deck.Id))
                || CollectionManager.Get()?.GetDeck(deckId) == null || !ExactProjectedHeader(deckId))
                throw new InvalidOperationException("The local deck no longer matches this delete request: " + deckId);
            IReadOnlyDictionary<long, long> deleteRanks = NativeUIProjection.ExpectedNativeSortOrders(
                Profile.CustomDecks.Where(deck => deck.Id != deckId).Select(deck =>
                    new KeyValuePair<long, long>(deck.Id, deck.SortOrder)), SessionLoanerKeys());
            NativeRankTarget[] deleteTargets = ResolveExpectedNativeRankTargets(deleteRanks);
            Profile.DeleteDraft(deckId);
            committed = true;
            decks.Remove(deckId);
            deletedDrafts.Add(deckId);
            preparedDeletes.Add(deckId);
            editedDraftId = deckId;
            try
            {
                ApplyNativeRanks(deleteTargets);
                // Native callback still reads the box ID, then owns its deletion animation.
                lastDeckOperation = "delete:" + deckId;
                lastDeckError = "";
            }
            catch (Exception displayError) { ReportDeleteRefreshError(deckId, displayError, confirmation); }
            try { WriteStatus(); }
            catch (Exception statusError) { log.LogError("NATIVE_DECK delete status write failed: " + statusError); }
        }
        catch (Exception error)
        {
            if (committed)
            {
                decks.Remove(deckId);
                deletedDrafts.Add(deckId);
                preparedDeletes.Add(deckId);
                editedDraftId = deckId;
                ReportDeleteRefreshError(deckId, error, confirmation);
                return;
            }
            throw;
        }
    }

    // The starter leaves this session's tray through the native delete (the box animation and CollectionManager removal);
    // the hidden list makes every later bootstrap skip it. Starter ranks are stable slots, so nothing re-ranks.
    private static void HideStarterLoaner(long deckId, string key, AlertPopup confirmation)
    {
        if (CollectionManager.Get()?.GetDeck(deckId) == null)
            throw new InvalidOperationException("The starter deck no longer matches this request: " + deckId);
        Profile.HideLoaner(key);
        decks.Remove(deckId);
        preparedDeletes.Add(deckId);
        lastDeckOperation = "hide-starter:" + key;
        lastDeckError = "";
        log.LogInfo("NATIVE_DECK starter hidden " + key);
        try { WriteStatus(); }
        catch (Exception statusError) { log.LogError("NATIVE_DECK hide status write failed: " + statusError); }
    }

    // Native hover shows a box's delete button only when it is unlocked; starter loaners stay locked (no card edits),
    // so their delete button is shown here under the same conditions the native hover uses.
    private static void ShowStarterDeleteButton(DeckTrayDeckListContent __instance, CollectionDeckBoxVisual __0)
    {
        try
        {
            if (!active || !CurrentCollectionScene() || __0 == null || !__0.IsLocked()
                || NativeUIProjection.HideableLoanerKey(__0.GetDeckID()) == null || !decks.ContainsKey(__0.GetDeckID())
                || UniversalInputManager.Get()?.IsTouchMode() == true || !__instance.IsModeTryingOrActive()
                || AccessTools.Property(typeof(DeckTrayReorderableContent), "DraggingDeckBox")?.GetValue(__instance, null) != null
                || (bool)Required(typeof(DeckTrayDeckListContent), "IsEditingCards").Invoke(__instance, null)) return;
            __0.ShowDeleteButton(true);
        }
        catch (Exception error) { log.LogWarning("NATIVE_DECK starter delete button: " + error.Message); }
    }

    // The native delete confirm (CollectionDeckBoxVisual.OnDeleteButtonPressed) says "delete"; for a starter it hides.
    private static void StarterDeleteConfirmText(AlertPopup.PopupInfo __0)
    {
        try
        {
            if (!active || __0?.m_responseCallback?.Target is not CollectionDeckBoxVisual box
                || __0.m_responseCallback.Method.Name != "OnDeleteButtonConfirmationResponse"
                || NativeUIProjection.HideableLoanerKey(box.GetDeckID()) == null) return;
            __0.m_headerText = NativeStarterText.HideHeader;
            __0.m_text = NativeStarterText.HideBody;
        }
        catch (Exception error) { log.LogWarning("NATIVE_DECK starter confirm text: " + error.Message); }
    }

    private static bool DeleteLocalDraft(long deckId)
    {
        if (!active) return true;
        if (preparedDeletes.Remove(deckId)) return true;
        if (!CurrentCollectionScene()) return true;
        if (Profile.GetDraft(deckId) == null) return false;
        var error = new InvalidOperationException("Personal deck delete was not committed before native mutation: " + deckId);
        ReportDeleteFailure(deckId, error, null);
        return false;
    }

    private static bool BlockDeckImport()
    {
        if (active) log.LogWarning("NATIVE_DECK blocked clipboard import");
        return !active;
    }

    private static bool UnlockLocalDeckBoxSlots(ref bool __result)
    {
        if (!NativeUIProjection.TryProjectDeckBoxLock(active, CurrentCollectionScene(), -1L, out bool locked))
            return true;
        __result = locked;
        return false;
    }

    private static void LockLoanerBox(CollectionDeckBoxVisual __instance, ref bool ___m_isLocked)
    {
        if (__instance != null && NativeUIProjection.TryProjectDeckBoxLock(active, CurrentCollectionScene(),
            __instance.GetDeckID(), out bool locked)) ___m_isLocked = locked;
    }

    private static void ShowLoanerTooltip(CollectionDeckBoxVisual __instance, bool ___m_isLocked)
    {
        if (__instance != null && NativeUIProjection.ShowLoanerTooltip(active, CurrentCollectionScene(),
            __instance.GetDeckID(), ___m_isLocked))
            __instance.m_tooltipZone?.ShowTooltip("Loaner Deck", "Ready to play. Cards cannot be edited. Customize its look using your favorite hero skin, card back and pet in Collection.", 4f);
    }

    private static void HideLoanerDeleteButton(CollectionDeckBoxVisual __instance, ref bool __0)
    {
        if (__instance != null)
            __0 = NativeUIProjection.ShowDeckBoxDeleteButton(active, CurrentCollectionScene(),
                __instance.GetDeckID(), __0);
    }

    private static bool HidePersonalDeckConvert(DeckOptionsMenu __instance, ref bool __result)
    {
        if (!OwnsPersonalDeckEditor(Field(__instance, "m_deck") as CollectionDeck, __instance)) return true;
        __result = false;
        return false;
    }

    private static bool HidePersonalDeckTutorial(CollectionManager __instance, ref bool __result)
    {
        if (!OwnsPersonalDeckEditor(__instance?.GetEditedDeck(), __instance)) return true;
        __result = false;
        return false;
    }

    private static bool HidePersonalDeckHelperChoices(CollectionDeck __0, ref bool __result)
    {
        if (!OwnsPersonalDeckEditor(__0, null)) return true;
        __result = false;
        return false;
    }

    private static bool BlockPersonalDeckHelper(DeckTrayCardListContent __instance, CollectionDeckSlot __0, bool __1)
    {
        var edited = CollectionManager.Get()?.GetEditedDeck();
        if (!OwnsPersonalDeckEditor(edited, __instance)) return true;
        try
        {
            // DeckMaker reads the projected collection; Done still commits through SaveLocalDraft.
            var target = LocalDeckSuggestionSlot(edited, __0);
            DeckHelper.Get()?.Show(target, __1 || !ReferenceEquals(target, __0),
                chosen => CollectionDeckTray.Get()?.OnCardManuallyAddedByUser_CheckSuggestions(chosen));
        }
        catch (Exception error)
        {
            LogRecipeFailure(error);
            NativeRecipeSafety.OrDefault(() => {
                DeckHelper.Get()?.Hide();
                NativeRecipeGuide.ShowError("CARD SUGGESTIONS UNAVAILABLE", "Your saved deck is unchanged. Reopen the deck to retry.");
                return true;
            }, false, LogRecipeFailure);
        }
        return false;
    }

    private static void RestorePersonalDeckGhost(CollectionDeckTileActor __instance) =>
        NativeRecipeSafety.OrDefault(() => {
            if (!active || !CurrentCollectionScene()) return false;
            var edited = CollectionManager.Get()?.GetEditedDeck();
            if (__instance != null && OwnsPersonalDeckEditor(edited, null)
                && IsProjectedDeckSlot(edited, __instance.GetSlot()) && __instance.GetPortraitMaterial() != null)
                // Show/OnEnable can replace the portrait material after SetUpActor applied its ghost effect.
                __instance.UpdateGhostTileEffect();
            return true;
        }, false, LogRecipeFailure);

    private static bool BlockPersonalDeckCompletion(CollectionDeckTray __instance) =>
        !OwnsPersonalDeckEditor(CollectionManager.Get()?.GetEditedDeck(), __instance);

    private static bool OwnsPersonalDeckEditor(CollectionDeck candidate, object instance)
    {
        // Replace Cards and ghost styling cover all persisted personal decks; crafting can make any deck incomplete.
        CollectionManager manager = CollectionManager.Get();
        CollectionDeckTray tray = CollectionDeckTray.Get();
        CollectionDeck edited = manager?.GetEditedDeck();
        CollectibleDisplay display = manager?.GetCollectibleDisplay();
        bool currentCollection = CurrentCollectionScene();
        bool exactInstance = instance == null
            || instance is CollectionManager && ReferenceEquals(instance, manager)
            || instance is CollectionDeckTray && ReferenceEquals(instance, tray)
            || instance is DeckTrayCardListContent && ReferenceEquals(instance, tray?.GetCardsContent())
            || instance is DeckOptionsMenu && ReferenceEquals(instance,
                Field(tray?.GetDecksContent(), "m_deckOptionsMenu"));
        bool currentMainContext = manager != null && tray != null && edited != null && display != null
            && manager.IsInEditMode() && tray.IsShowingDeckContents() && !edited.IsBrawlDeck
            && display.GetViewMode() != CollectionUtils.ViewMode.DECK_TEMPLATE
            && ReferenceEquals(tray.GetCurrentDeckContext(), edited);
        bool persisted = active && currentCollection && edited != null && Profile.GetDraft(edited.ID) != null;
        return NativeUIProjection.OwnsPersonalDeckEditor(active, currentCollection, manager != null,
            tray != null, currentMainContext, ReferenceEquals(candidate, edited), exactInstance, persisted);
    }

    private static bool IsLoanerId(long id) => NativeUIProjection.IsLoanerId(id);

    private static bool CurrentCollectionScene() => SceneMgr.IsInitialized() && SceneMgr.Get() != null
        && SceneMgr.Get().GetMode() == SceneMgr.Mode.COLLECTIONMANAGER;

    private static NetCache.NetCacheDecks DeckHeaders()
    {
        if (!cache.TryGetValue(typeof(NetCache.NetCacheDecks), out object value))
            throw new InvalidOperationException("Native deck header cache is unavailable.");
        return (NetCache.NetCacheDecks)value;
    }

    private static IEnumerable<string> SessionLoanerKeys() => decks
        .Where(pair => NativeUIProjection.IsLoanerId(pair.Key)).Select(pair => pair.Value.Id);
    // Shop/test-control seam: null when the live tray (this session's personal decks + bootstrap loaners + one new
    // deck) has room for another personal deck, else the refusal text.
    internal static string PersonalDeckBlocker() =>
        NativeUIProjection.PersonalDeckBlocker(Profile.CustomDeckCount, SessionLoanerKeys().Count()); // non-copying: shop availability runs it per product

    private static IReadOnlyDictionary<long, long> ExpectedRanks() =>
        NativeUIProjection.ExpectedNativeSortOrders(Profile.CustomDecks.Select(deck =>
            new KeyValuePair<long, long>(deck.Id, deck.SortOrder)), SessionLoanerKeys());

    private static NetCache.DeckHeader ExpectedHeader(long id)
    {
        LocalCustomDeck saved = Profile.GetDraft(id);
        LocalDeck deck = saved == null ? decks[id] : LocalProfile.LedgerDeck(saved.Name, saved.Hero, saved.Cards, saved.Sideboards, false);
        return ExpectedHeader(id, deck, saved == null ? "Loaner: " + deck.Name : saved.Name, ExpectedRanks()[id],
            saved == null ? null : Profile.DeckCosmetics(id));
    }

    private static bool ExactProjectedHeader(long id)
    {
        NetCache.DeckHeader actual = DeckHeaders().Decks.SingleOrDefault(deck => deck.ID == id);
        if (actual == null) return false;
        NetCache.DeckHeader expected = ExpectedHeader(id);
        return actual.ID == expected.ID && actual.Name == expected.Name && actual.CardBack == expected.CardBack
            && actual.CosmeticCoin == expected.CosmeticCoin && actual.RandomCoinUseFavorite == expected.RandomCoinUseFavorite
            && actual.Hero == expected.Hero && actual.UIHeroOverride == expected.UIHeroOverride
            && actual.UIHeroOverridePremium == expected.UIHeroOverridePremium && actual.HeroPower == expected.HeroPower
            && actual.Type == expected.Type && actual.HeroOverridden == expected.HeroOverridden
            && actual.RandomHeroUseFavorite == expected.RandomHeroUseFavorite && actual.SeasonId == expected.SeasonId
            && actual.BrawlLibraryItemId == expected.BrawlLibraryItemId && actual.NeedsName == expected.NeedsName
            && actual.SortOrder == expected.SortOrder && actual.Rune1 == expected.Rune1 && actual.Rune2 == expected.Rune2
            && actual.Rune3 == expected.Rune3 && actual.FormatType == expected.FormatType && actual.Locked == expected.Locked
            && actual.SourceType == expected.SourceType && actual.Pet == expected.Pet && actual.PetVariant == expected.PetVariant
            && actual.RandomPetUseFavorite == expected.RandomPetUseFavorite;
    }

    private static bool ExactProjectedDeck(CollectionDeck deck, long id) => ExactProjectedDeckProblem(deck, id) == null;

    // Null when the native deck is exactly the projection of the saved (or loaner) deck; otherwise the first difference,
    // for logs (live 2026-10-30: a Fabled craft's ownership reconcile was refused without saying why).
    internal static string ExactProjectedDeckProblem(CollectionDeck deck, long id)
    {
        if (deck == null) return "missing deck";
        if (!SupportedNativeEnvelope(deck)) return "unsupported envelope";
        LocalCustomDeck saved = Profile.GetDraft(id);
        LocalDeck local = saved == null ? (decks.TryGetValue(id, out LocalDeck loaner) ? loaner : null)
            : LocalProfile.LedgerDeck(saved.Name, saved.Hero, saved.Cards, saved.Sideboards, false);
        if (local == null) return "unknown deck";
        NetCache.DeckHeader expected = ExpectedHeader(id);
        bool header = deck.ID == id && deck.Name == expected.Name && deck.HeroCardID == expected.Hero
            && deck.CardBackID == expected.CardBack && deck.HeroOverridden == expected.HeroOverridden
            && deck.CosmeticCoinID == expected.CosmeticCoin && deck.RandomCoinUseFavorite == expected.RandomCoinUseFavorite
            && deck.RandomHeroUseFavorite == expected.RandomHeroUseFavorite && deck.SeasonId == expected.SeasonId
            && deck.BrawlLibraryItemId == expected.BrawlLibraryItemId && deck.NeedsName == expected.NeedsName
            && deck.SortOrder == expected.SortOrder && deck.UIHeroOverridePremium == expected.UIHeroOverridePremium
            && deck.PetID == expected.Pet && deck.PetVariantID == expected.PetVariant
            && deck.RandomPetUseFavorite == expected.RandomPetUseFavorite && deck.Locked == expected.Locked
            && deck.GetRuneOrder().SequenceEqual(new[] { expected.Rune1, expected.Rune2, expected.Rune3 });
        if (!header) return "header differs";
        if (saved != null && SideboardSignature(saved.Sideboards) != SideboardSignature(ReadSideboards(deck)))
            return "sideboards " + SideboardSignature(ReadSideboards(deck)) + " != " + SideboardSignature(saved.Sideboards);
        var nativeCards = deck.GetCardsWithCardID().GroupBy(card => card, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
        var localCards = local.CardIds.GroupBy(card => card, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
        var diff = nativeCards.Keys.Union(localCards.Keys).OrderBy(card => card, StringComparer.Ordinal)
            .Where(card => (nativeCards.TryGetValue(card, out int a) ? a : 0) != (localCards.TryGetValue(card, out int b) ? b : 0))
            .Select(card => card + " native=" + (nativeCards.TryGetValue(card, out int a) ? a : 0) + " saved=" + (localCards.TryGetValue(card, out int b) ? b : 0))
            .ToArray();
        return diff.Length == 0 ? null : "cards " + string.Join(", ", diff);
    }

    private static bool SameNativeEnvelopeExceptSort(CollectionDeck first, CollectionDeck second)
    {
        if (first == null || second == null) return false;
        long sort = first.SortOrder;
        try
        {
            first.SortOrder = second.SortOrder;
            return SameNativeEnvelope(first, second);
        }
        finally { first.SortOrder = sort; }
    }

    private static bool SameNativeEnvelope(CollectionDeck first, CollectionDeck second) =>
        first.ID == second.ID && first.Type == second.Type && first.Name == second.Name
        && first.HeroCardID == second.HeroCardID && first.HeroOverridden == second.HeroOverridden
        && first.CosmeticCoinID == second.CosmeticCoinID && first.RandomCoinUseFavorite == second.RandomCoinUseFavorite
        && first.CardBackID == second.CardBackID && first.NeedsName == second.NeedsName
        && first.SeasonId == second.SeasonId && first.BrawlLibraryItemId == second.BrawlLibraryItemId
        && first.FormatType == second.FormatType && first.SortOrder == second.SortOrder
        && first.SourceType == second.SourceType && first.UIHeroOverrideCardID == second.UIHeroOverrideCardID
        && first.UIHeroOverridePremium == second.UIHeroOverridePremium && first.PetID == second.PetID
        && first.PetVariantID == second.PetVariantID && first.RandomPetUseFavorite == second.RandomPetUseFavorite
        && first.RandomHeroUseFavorite == second.RandomHeroUseFavorite && first.Locked == second.Locked
        && first.GetRuneOrder().SequenceEqual(second.GetRuneOrder())
        && first.GetCardsWithCardID().OrderBy(card => card, StringComparer.Ordinal)
            .SequenceEqual(second.GetCardsWithCardID().OrderBy(card => card, StringComparer.Ordinal))
        && SameSideboards(first, second)
        && first.GetSlots().All(slot => slot != null) && second.GetSlots().All(slot => slot != null);

    private static bool ReorderLocalDrafts(DeckTrayReorderableContent __instance, out bool __state)
    {
        __state = OwnsCollectionReorder(__instance);
        if (!__state) return true;
        var list = (DeckTrayDeckListContent)__instance;
        bool runOriginal = false;
        bool committed = false;
        bool synchronizing = false;
        try
        {
            IReadOnlyDictionary<long, long> ranks = ExpectedRanks();
            List<CollectionDeck> working = CollectionManager.Get().GetDecks(DeckType.NORMAL_DECK);
            if (!working.Select(deck => deck.ID).OrderBy(id => id).SequenceEqual(ranks.Keys.OrderBy(id => id)))
                throw new InvalidOperationException("Native deck set does not match the local library.");
            foreach (CollectionDeck deck in working)
            {
                CollectionDeck baseDeck = CollectionManager.Get().GetBaseDeck(deck.ID);
                if (!ExactProjectedDeck(baseDeck, deck.ID) || !ExactProjectedHeader(deck.ID)
                    || !SupportedNativeEnvelope(deck) || !SameNativeEnvelopeExceptSort(deck, baseDeck))
                    throw new InvalidOperationException("Native deck changed outside the local reorder transaction: " + deck.ID);
            }
            if (working.All(deck => deck.SortOrder == ranks[deck.ID]))
            {
                runOriginal = true;
                return true;
            }

            long[] orderedIds = working.Where(deck => Profile.GetDraft(deck.ID) != null).Select(deck => deck.ID).ToArray();
            long[] persistedIds = Profile.CustomDecks.OrderBy(deck => deck.SortOrder).ThenBy(deck => deck.Id)
                .Select(deck => deck.Id).ToArray();
            if (!orderedIds.SequenceEqual(persistedIds))
            {
                Profile.ReorderDrafts(orderedIds);
                committed = true;
            }
            synchronizing = true;
            ApplyExpectedNativeRanks(list);
            lastDeckOperation = orderedIds.SequenceEqual(persistedIds) ? "reorder-noop" : "reorder:" + string.Join(",", orderedIds);
            lastDeckError = "";
            WriteStatus();
            return false;
        }
        catch (Exception error)
        {
            if (synchronizing)
            {
                string recovery = RecoverReorderDisplay(list, committed);
                string detail = Unwrap(error).Message + recovery;
                ReportReorderRefreshError(committed, detail);
            }
            else
            {
                long heldId = (__instance.DraggingDeckBox as CollectionDeckBoxVisual)?.GetDeckID() ?? 0;
                string recovery = RestoreAndRefresh(list);
                ReportDeckError("save", heldId, RecoveryError(error, recovery));
            }
            return false;
        }
        finally
        {
            if (!runOriginal && __instance.DraggingDeckBox != null)
                try { CleanupLocalReorder(__instance); }
                catch (Exception cleanupError) { log.LogError("NATIVE_DECK reorder cleanup failed: " + cleanupError); }
        }
    }

    private static Exception FinishLocalReorder(DeckTrayReorderableContent __instance, Exception __exception, bool __state)
    {
        if (__exception == null || !__state) return __exception;
        string recovery = RestoreAndRefresh((DeckTrayDeckListContent)__instance);
        if (recovery.Length != 0) log.LogError("NATIVE_DECK reorder finalizer recovery failed:" + recovery);
        try { CleanupLocalReorder(__instance); }
        catch (Exception cleanupError) { log.LogError("NATIVE_DECK reorder cleanup failed: " + cleanupError); }
        return __exception;
    }

    private static bool LocalCollectionContext()
    {
        if (!active || !SceneMgr.IsInitialized() || CollectionDeckTray.Get() == null) return false;
        SceneMgr scenes = SceneMgr.Get();
        return scenes != null && (scenes.GetMode() == SceneMgr.Mode.COLLECTIONMANAGER
            || scenes.GetPrevMode() == SceneMgr.Mode.COLLECTIONMANAGER);
    }

    private static bool PackOpeningContext() => active && SceneMgr.IsInitialized()
        && SceneMgr.Get() != null && SceneMgr.Get().GetMode() == SceneMgr.Mode.PACKOPENING;

    private static bool OwnsCollectionReorder(DeckTrayReorderableContent content) =>
        LocalCollectionContext() && content is DeckTrayDeckListContent
        && ReferenceEquals(CollectionDeckTray.Get().GetDecksContent(), content);

    private static void ApplyExpectedNativeRanks(DeckTrayDeckListContent content)
    {
        SynchronizeExpectedNativeRanks();
        RefreshDeckTray(content);
    }

    private static void SynchronizeExpectedNativeRanks()
    {
        ApplyNativeRanks(ResolveExpectedNativeRankTargets(ExpectedRanks()));
    }

    private static NativeRankTarget[] ResolveExpectedNativeRankTargets(IReadOnlyDictionary<long, long> ranks)
    {
        CollectionManager manager = CollectionManager.Get()
            ?? throw new InvalidOperationException("Native collection manager is unavailable.");
        NetCache.NetCacheDecks headers = DeckHeaders();
        return ranks.Select(pair => new NativeRankTarget {
            Working = manager.GetDeck(pair.Key) ?? throw new InvalidOperationException("Native working deck is missing: " + pair.Key),
            BaseDeck = manager.GetBaseDeck(pair.Key) ?? throw new InvalidOperationException("Native base deck is missing: " + pair.Key),
            Header = headers.Decks.SingleOrDefault(deck => deck.ID == pair.Key)
                ?? throw new InvalidOperationException("Native deck header is missing: " + pair.Key),
            Rank = pair.Value
        }).ToArray();
    }

    private static void ApplyNativeRanks(IEnumerable<NativeRankTarget> targets)
    {
        foreach (var target in targets)
        {
            target.Working.SortOrder = target.Rank;
            target.BaseDeck.SortOrder = target.Rank;
            target.Header.SortOrder = target.Rank;
        }
    }

    private static void RestoreWorkingDecks(DeckTrayDeckListContent content)
    {
        CollectionManager manager = CollectionManager.Get();
        foreach (CollectionDeck working in manager.GetDecks(DeckType.NORMAL_DECK))
        {
            CollectionDeck baseDeck = manager.GetBaseDeck(working.ID);
            if (baseDeck != null) working.CopyFrom(baseDeck);
        }
    }

    private static void RefreshDeckTray(DeckTrayDeckListContent content)
    {
        if (content == null) return;
        Required(typeof(DeckTrayDeckListContent), "UpdateAllTrays", new[] { typeof(bool), typeof(bool) })
            .Invoke(content, new object[] { true, true });
        Required(typeof(DeckTrayDeckListContent), "RefreshTraySectionPositions", new[] { typeof(bool) })
            .Invoke(content, new object[] { false });
    }

    private static string RestoreAndRefresh(DeckTrayDeckListContent content)
    {
        var failures = new List<string>();
        try { RestoreWorkingDecks(content); }
        catch (Exception error) { failures.Add(" restore failed: " + Unwrap(error).Message); }
        try { RefreshDeckTray(content); }
        catch (Exception error) { failures.Add(" refresh failed: " + Unwrap(error).Message); }
        return string.Join(";", failures);
    }

    private static string RecoverReorderDisplay(DeckTrayDeckListContent content, bool committed)
    {
        var failures = new List<string>();
        bool synchronized = false;
        try { SynchronizeExpectedNativeRanks(); synchronized = true; }
        catch (Exception error) { failures.Add(" rank recovery failed: " + Unwrap(error).Message); }
        if (synchronized || !committed)
            try { RestoreWorkingDecks(content); }
            catch (Exception error) { failures.Add(" restore failed: " + Unwrap(error).Message); }
        try { RefreshDeckTray(content); }
        catch (Exception error) { failures.Add(" retry refresh failed: " + Unwrap(error).Message); }
        return string.Join(";", failures);
    }

    private static void CleanupLocalReorder(DeckTrayReorderableContent content)
    {
        IDraggableCollectionVisual held = content.DraggingDeckBox;
        try { held?.OnStopDragToReorder(); }
        catch (Exception error) { log.LogError("NATIVE_DECK held-box cleanup failed: " + error); }
        try { AccessTools.Field(typeof(DeckTrayReorderableContent), "m_draggingDeckBox").SetValue(content, null); }
        catch (Exception error) { log.LogError("NATIVE_DECK drag-pointer cleanup failed: " + error); }
        try { content.m_scrollbar?.Pause(false); }
        catch (Exception error) { log.LogError("NATIVE_DECK scrollbar cleanup failed: " + error); }
        try { content.m_scrollbar?.PauseUpdateScrollHeight(false); }
        catch (Exception error) { log.LogError("NATIVE_DECK scrollbar-height cleanup failed: " + error); }
    }

    private static Exception Unwrap(Exception error) => error is TargetInvocationException && error.InnerException != null
        ? error.InnerException : error;

    private static Exception RecoveryError(Exception error, string recovery) => recovery.Length == 0 ? error
        : new InvalidOperationException(Unwrap(error).Message + ". Display recovery also failed:" + recovery, error);

    private static void ReportReorderRefreshError(bool committed, string detail)
    {
        lastDeckError = NativeUIProjection.ReorderRefreshFailureMessage(committed, detail);
        lastDeckOperation = committed ? "reorder-saved-refresh-failed" : "reorder-unchanged-refresh-failed";
        log.LogError("NATIVE_DECK " + lastDeckError);
        try
        {
            DialogManager.Get()?.ShowPopup(new AlertPopup.PopupInfo {
                m_headerText = committed ? "Deck order saved" : "Deck order unchanged",
                m_text = lastDeckError,
                m_showAlertIcon = true,
                m_responseDisplay = AlertPopup.ResponseDisplay.OK
            });
        }
        catch (Exception popupError) { log.LogError("NATIVE_DECK popup failed: " + popupError); }
    }

    private static DialogManager deleteNoticeManager;
    private static Action deleteNoticeHidden;
    private static NativeDeckDeleteNotice deleteNotice;
    private static string deleteNoticeState = "none";
    private static int deleteNoticeGeneration;

    private static void ReportDeleteFailure(long id, Exception error, AlertPopup confirmation)
    {
        Exception cause = Unwrap(error);
        lastDeckError = NativeUIProjection.DeleteFailureMessage(id, cause.Message);
        lastDeckOperation = "delete-failed:" + id;
        log.LogError("NATIVE_DECK " + lastDeckError + "\n" + cause);
        ShowDeleteNotice(confirmation, "Deck not deleted", lastDeckError);
        try { WriteStatus(); }
        catch (Exception statusError) { log.LogError("NATIVE_DECK delete status write failed: " + statusError); }
    }

    private static void ReportDeleteRefreshError(long id, Exception error, AlertPopup confirmation)
    {
        Exception cause = Unwrap(error);
        lastDeckError = NativeUIProjection.DeleteRefreshFailureMessage(id, cause.Message);
        lastDeckOperation = "delete-saved-refresh-failed:" + id;
        log.LogError("NATIVE_DECK " + lastDeckError + "\n" + cause);
        ShowDeleteNotice(confirmation, "Deck deleted", lastDeckError);
    }

    private static void ReportCreateRefreshError(long id, Exception error)
    {
        Exception cause = Unwrap(error);
        lastDeckError = NativeUIProjection.CreateRefreshFailureMessage(id, cause.Message);
        lastDeckOperation = "create-saved-refresh-failed:" + id;
        log.LogError("NATIVE_DECK " + lastDeckError + "\n" + cause);
        try
        {
            DialogManager.Get()?.ShowPopup(new AlertPopup.PopupInfo {
                m_headerText = "Deck saved",
                m_text = lastDeckError,
                m_showAlertIcon = true,
                m_responseDisplay = AlertPopup.ResponseDisplay.OK
            });
        }
        catch (Exception popupError) { log.LogError("NATIVE_DECK popup failed: " + popupError); }
    }

    private static void ReportSaveRefreshError(long id, Exception error)
    {
        Exception cause = Unwrap(error);
        lastDeckError = NativeUIProjection.SaveRefreshFailureMessage(id, cause.Message);
        lastDeckOperation = "save-saved-refresh-failed:" + id;
        log.LogError("NATIVE_DECK " + lastDeckError + "\n" + cause);
        try
        {
            DialogManager.Get()?.ShowPopup(new AlertPopup.PopupInfo {
                m_headerText = "Deck saved",
                m_text = lastDeckError,
                m_showAlertIcon = true,
                m_responseDisplay = AlertPopup.ResponseDisplay.OK
            });
        }
        catch (Exception popupError) { log.LogError("NATIVE_DECK popup failed: " + popupError); }
    }

    private static void RetireStaleDeleteNotice()
    {
        if (deleteNotice == null || (active && CurrentCollectionScene()
            && ReferenceEquals(deleteNoticeManager, DialogManager.Get()))) return;
        ClearDeleteNotice();
        deleteNoticeState = "retired";
    }

    private static void ClearDeleteNotice()
    {
        deleteNoticeGeneration++;
        if (deleteNoticeManager != null && deleteNoticeHidden != null)
            deleteNoticeManager.OnDialogHidden -= deleteNoticeHidden;
        deleteNoticeHidden = null;
        deleteNoticeManager = null;
        deleteNotice = null;
    }

    private static void ShowDeleteNotice(AlertPopup confirmation, string header, string message)
    {
        try
        {
            ClearDeleteNotice();
            DialogManager manager = DialogManager.Get();
            if (manager == null) { deleteNoticeState = "manager-unavailable"; return; }
            if (confirmation == null) { DisplayDeleteNotice(manager, header, message); return; }
            var pending = new NativeDeckDeleteNotice(manager, confirmation, header, message);
            deleteNotice = pending;
            deleteNoticeManager = manager;
            deleteNoticeState = "waiting-for-owned-confirmation";
            confirmation.AddHideListener((hidden, data) => pending.MarkHidden(hidden));
            deleteNoticeHidden = () => {
                if (!ReferenceEquals(deleteNotice, pending)) return;
                bool current = active && CurrentCollectionScene() && ReferenceEquals(DialogManager.Get(), manager);
                bool show = pending.TryTake(manager, current);
                // Only the exact popup's hide listener can release this one-shot handler.
                if (!show && current) return;
                ClearDeleteNotice();
                if (show) DisplayDeleteNotice(manager, pending.Header, pending.Message);
                else deleteNoticeState = "retired";
            };
            manager.OnDialogHidden += deleteNoticeHidden;
        }
        catch (Exception popupError) { deleteNoticeState = "failed"; log.LogError("NATIVE_DECK popup failed: " + popupError); }
    }

    private static void DisplayDeleteNotice(DialogManager manager, string header, string message)
    {
        try
        {
            int generation = deleteNoticeGeneration;
            deleteNoticeState = "requested";
            manager.ShowPopup(new AlertPopup.PopupInfo {
                m_headerText = header, m_text = message, m_showAlertIcon = true,
                m_responseDisplay = AlertPopup.ResponseDisplay.OK
            }, (dialog, data) => {
                if (generation != deleteNoticeGeneration || !active || !CurrentCollectionScene()
                    || !ReferenceEquals(manager, DialogManager.Get())) return false;
                deleteNoticeState = "processed";
                return true;
            });
        }
        catch (Exception popupError) { deleteNoticeState = "failed"; log.LogError("NATIVE_DECK popup failed: " + popupError); }
    }

    private static void ReportDeckError(string operation, long id, Exception error)
    {
        Exception cause = Unwrap(error);
        lastDeckError = NativeUIProjection.DeckWriteFailureMessage(operation, id, cause.Message);
        lastDeckOperation = operation + "-failed:" + id;
        log.LogError("NATIVE_DECK " + lastDeckError + "\n" + cause);
        try
        {
            DialogManager.Get()?.ShowPopup(new AlertPopup.PopupInfo {
                m_headerText = "Local deck not saved",
                m_text = lastDeckError,
                m_showAlertIcon = true,
                m_responseDisplay = AlertPopup.ResponseDisplay.OK
            });
        }
        catch (Exception popupError) { log.LogError("NATIVE_DECK popup failed: " + popupError); }
    }
}
