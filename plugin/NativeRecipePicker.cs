using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Hearthstone.Core;
using PegasusShared;

internal static partial class NativeUIProbe
{
    private static NativeRecipeTemplates recipeTemplates;
    // Also guards direct/stale callbacks if navigation removal or a later refresh fails.
    private static readonly HashSet<DeckTemplatePicker> committedRecipePickers = new HashSet<DeckTemplatePicker>();
    private static NativeRecipeTemplates RecipeTemplates => recipeTemplates
        ?? (recipeTemplates = new NativeRecipeTemplates(NativeRecipeLibrary.Current.Recipes));

    private static void InitializeRecipePicker(Harmony harmony)
    {
        Hook(harmony, typeof(CollectionManager), "GetNonStarterTemplateDecks", nameof(LocalRecipeTemplates),
            new[] { typeof(FormatType), typeof(TAG_CLASS) });
        harmony.Patch(Required(typeof(DeckTemplatePicker), "SetDeckFormatAndClass"),
            postfix: new HarmonyMethod(typeof(NativeUIProbe), nameof(DecorateLocalRecipes)));
        Hook(harmony, typeof(DeckTemplatePicker), "ChooseRecipeAndFillInCards", nameof(ChooseLocalRecipe));
        Hook(harmony, typeof(DeckTemplatePicker), "OnNavigateBack", nameof(GuardSavedRecipeBack));
        harmony.Patch(Required(typeof(CollectionDeck), "FillFromTemplateDeck"),
            prefix: new HarmonyMethod(typeof(NativeUIProbe), nameof(ResetRecipePreview)),
            finalizer: new HarmonyMethod(typeof(NativeUIProbe), nameof(FinishRecipePreview)));
        harmony.Patch(Required(typeof(CollectionManagerDisplay), "ShowDeck"),
            postfix: new HarmonyMethod(typeof(NativeUIProbe), nameof(AttachRecipeGuide)));
    }

    private static bool LocalRecipeTemplates(FormatType __0, TAG_CLASS __1, ref List<CollectionManager.TemplateDeck> __result)
    {
        if (!active) return true;
        __result = NativeRecipeSafety.OrDefault(() => RecipeTemplates.ForClass(Profile.OwnedRecipes, __0, __1),
            new List<CollectionManager.TemplateDeck>(), LogRecipeFailure);
        return false;
    }

    private static void DecorateLocalRecipes(DeckTemplatePicker __instance, FormatType __0, TAG_CLASS __1)
    {
        if (!active) return;
        NativeRecipeSafety.OrDefault(() => {
            var draft = Profile.GetDraft(CollectionManager.Get()?.GetEditedDeck()?.ID ?? 0);
            if (draft != null && draft.SourceRecipeId == null && draft.Cards.Count == 0 && draft.Sideboards.Count == 0)
                committedRecipePickers.Remove(__instance);
            var templates = RecipeTemplates.ForClass(Profile.OwnedRecipes, __0, __1);
            var buttons = (List<DeckTemplatePickerButton>)Field(__instance, "m_pickerButtons");
            var visible = NativeRecipeTemplates.ButtonVisibility(templates.Count, buttons.Count);
            for (int index = 0; index < buttons.Count; index++)
            {
                var button = buttons[index];
                button.gameObject.SetActive(visible[index]);
                button.SetIsCoreDeck(false);
                if (!visible[index])
                {
                    // ShowPacks visits unused slots too; its default FlyIn reactivates uninitialized packs.
                    button.GetComponent<AnimatedLowPolyPack>().Init(0, button.transform.localPosition,
                        button.transform.localPosition + __instance.m_offscreenPackOffset,
                        ignoreFullscreenEffects: false, changeActivation: false);
                    continue;
                }
                var template = templates[index];
                button.SetCardCountText(button.GetOwnedCardCount(), template.m_cardIds.Sum(row => row.Value));
                var recipe = RecipeTemplates.Recipe(template);
                // No synthetic DBF record: use hero art and decorate runes directly.
                button.SetDeckArtByCardId(GameUtils.TranslateCardIdToDbId(recipe.Hero), __instance.m_deckArtMaterial, null);
                bool deathKnight = template.m_class == TAG_CLASS.DEATHKNIGHT;
                button.m_runes.SetActive(deathKnight);
                if (deathKnight)
                {
                    var decorate = Required(typeof(DeckTemplatePickerButton), "SetRuneMaterials");
                    decorate.Invoke(button, new object[] { button.m_rune1, button.m_slot1, template.m_rune1, false });
                    decorate.Invoke(button, new object[] { button.m_rune2, button.m_slot2, template.m_rune2, false });
                    decorate.Invoke(button, new object[] { button.m_rune3, button.m_slot3, template.m_rune3, false });
                }
            }
            return true;
        }, false, error => { LogRecipeFailure(error); RecoverRecipePicker(__instance, false); });
    }

    internal static void LogRecipeFailure(Exception error)
    {
        try { log?.LogError("NATIVE_RECIPE " + error); } catch { /* Logging must not escape Harmony. */ }
    }

    private static void RecoverRecipePicker(DeckTemplatePicker picker, bool committed)
    {
        NativeRecipeSafety.OrDefault(() => {
            if (committed)
            {
                picker.StopAllCoroutines();
                picker.m_chooseButton?.Disable();
                picker.m_root?.SetActive(false);
                picker.m_phoneTray?.gameObject.SetActive(false);
            }
            else
            {
                AccessTools.Field(typeof(DeckTemplatePicker), "m_currentSelectedDeck").SetValue(picker, Field(picker, "m_customDeck"));
                foreach (var button in (List<DeckTemplatePickerButton>)Field(picker, "m_pickerButtons"))
                    button.gameObject.SetActive(false);
                Invoke(picker, "SelectCustomDeckButton", true);
            }
            return true;
        }, false, LogRecipeFailure);
    }

    private static bool GuardSavedRecipeBack(DeckTemplatePicker __instance, ref bool __result)
    {
        if (!active || !committedRecipePickers.Contains(__instance)) return true;
        __result = true;
        return false;
    }

    private static bool ResetRecipePreview(CollectionDeck __instance, CollectionManager.TemplateDeck __0)
    {
        if (!active) return true;
        return NativeRecipeSafety.OrDefault(() => {
            if (recipeTemplates?.Recipe(__0) != null) NativeRecipeSafety.ResetPreview(__instance);
            return true;
        }, false, RecoverFailedRecipePreview);
    }

    private static void RecoverFailedRecipePreview(Exception error)
    {
        LogRecipeFailure(error);
        NativeRecipeSafety.OrDefault(() => {
            var display = CollectionManager.Get()?.GetCollectibleDisplay() as CollectionManagerDisplay;
            if (display != null)
            {
                var picker = CurrentRecipePicker(display);
                RecoverRecipePicker(picker, committedRecipePickers.Contains(picker));
            }
            return true;
        }, false, LogRecipeFailure);
    }

    private static Exception FinishRecipePreview(Exception __exception, CollectionManager.TemplateDeck __0)
    {
        if (__exception == null || !active) return __exception;
        return NativeRecipeSafety.OrDefault(() => {
            if (recipeTemplates?.Recipe(__0) == null) return __exception;
            RecoverFailedRecipePreview(__exception);
            return (Exception)null;
        }, null, LogRecipeFailure);
    }

    private static bool ChooseLocalRecipe(DeckTemplatePicker __instance)
    {
        if (!active) return true;
        if (committedRecipePickers.Contains(__instance)) return false;
        bool committed = false;
        long id = 0;
        string stage = "prepare";
        try
        {
            var selectedTemplate = Field(__instance, "m_currentSelectedDeck") as CollectionManager.TemplateDeck;
            if (ReferenceEquals(selectedTemplate, Field(__instance, "m_customDeck"))) return true;
            if (!CurrentCollectionScene()) throw new InvalidOperationException("Recipe picker is outside Collection.");
            var manager = CollectionManager.Get();
            var display = manager?.GetCollectibleDisplay() as CollectionManagerDisplay;
            if (display == null || !ReferenceEquals(CurrentRecipePicker(display), __instance)
                || display.GetViewMode() != CollectionUtils.ViewMode.DECK_TEMPLATE)
                throw new InvalidOperationException("Recipe picker is no longer current.");
            var recipe = RecipeTemplates.Recipe(selectedTemplate);
            var tray = CollectionDeckTray.Get() ?? throw new InvalidOperationException("Recipe deck tray unavailable.");
            if (tray.GetCardsContent() == null) throw new InvalidOperationException("Recipe card list unavailable.");
            var working = manager.GetEditedDeck() ?? throw new InvalidOperationException("Recipe draft unavailable.");
            id = working.ID;
            var prior = manager.GetBaseDeck(id) ?? throw new InvalidOperationException("Recipe base deck unavailable.");
            var header = DeckHeaders().Decks.Single(row => row.ID == id);
            var prepared = NativeRecipeTemplates.PrepareChoice(NativeRecipeLibrary.Current, recipe,
                Profile.GetDraft(id), Profile.OwnedRecipes, cardId => manager.GetTotalOwnedCount(cardId), Profile.OwnedCount);
            // Commit before filling native slots or firing callbacks; failed writes leave native draft untouched.
            stage = "commit";
            var saved = Profile.SaveDeckFromRecipe(recipe.Id, prepared, id);
            committed = true;
            committedRecipePickers.Add(__instance);
            Navigation.RemoveHandler(__instance.OnNavigateBack);
            decks[id] = LocalProfile.LedgerDeck(saved.Name, saved.Hero, saved.Cards, saved.Sideboards, false);
            stage = "fill-native";
            // CommitFakeDeckChanges uses CopyContents/AddCardsFrom, which rejects unowned sideboard cards.
            // Fill the real working deck with allowInvalid, preserving both main and sideboard ghosts.
            working.FillFromTemplateDeck(selectedTemplate);
            // Template filling hands out native's best owned versions; project the saved (fresh best-version) ones, sideboards too.
            RestoreSavedPremiums(working, saved.CardEntries);
            ProjectSideboards(working, saved.Sideboards);
            working.SourceType = DeckSourceType.DECK_SOURCE_TYPE_NORMAL;
            stage = "project-header-and-base";
            ProjectSavedDraft(saved, working, prior, header, ExpectedRanks()[id]);
            stage = "assert-working";
            NativeRecipeSafety.AssertContents(saved, working.Name, NativeBaseHero(working), working.GetCardsWithCardID(), ReadSideboards(working));
            stage = "assert-base";
            NativeRecipeSafety.AssertContents(saved, prior.Name, NativeBaseHero(prior), prior.GetCardsWithCardID(), ReadSideboards(prior));
            stage = "notify-content";
            working.OnContentChangesComplete();
            working.OnNameChangeComplete();
            manager.SetShowDeckTemplatePageForClass(selectedTemplate.m_class, true);
            stage = "open-editor";
            Invoke(__instance, "FireOnTemplateDeckChosenEvent");
            stage = "editor-runes-and-filters";
            tray.InitializeRuneIndicatorVisual(working);
            if (UniversalInputManager.UsePhoneUI) __instance.StartCoroutine(__instance.EnterDeckPhone());
            (display.GetPageManager() as CollectionPageManager)?.UpdateFiltersForDeck(working, working.GetClasses(), skipPageTurn: false);
            if (working.HasClass(TAG_CLASS.DEATHKNIGHT))
                TutorialDeathKnightDeckBuilding.ShowTutorial(UIVoiceLinesManager.TriggerType.STARTED_EDITING_DEATH_KNIGHT_DECK);
            editedDraftId = id;
            lastDeckOperation = "recipe:" + recipe.Id + ":" + id;
            lastDeckError = "";
            log?.LogInfo("NATIVE_RECIPE chosen recipe=" + recipe.Id + " deck=" + id);
        }
        catch (Exception error)
        {
            LogRecipeFailure(new InvalidOperationException("fallback deck=" + id + " committed=" + committed + " stage=" + stage, error));
            RecoverRecipePicker(__instance, committed);
            NativeRecipeSafety.OrDefault(() => {
                NativeRecipeGuide.ShowError(committed ? "RECIPE SAVED" : "RECIPE NOT SAVED", committed
                    ? "Your deck is saved locally. Restart the game to reload it."
                    : "The draft is unchanged. Choose Custom or reopen the picker.\n\n" + error.GetBaseException().Message);
                return true;
            }, false, LogRecipeFailure);
        }
        return false;
    }

    private static void AttachRecipeGuide(CollectionManagerDisplay __instance)
    {
        if (!active) return;
        NativeRecipeSafety.OrDefault(() => {
            if (CurrentCollectionScene()) NativeRecipeGuide.Attach(__instance);
            return true;
        }, false, LogRecipeFailure);
    }

    private static DeckTemplatePicker CurrentRecipePicker(CollectionManagerDisplay display) => UniversalInputManager.UsePhoneUI
        ? display.GetPhoneDeckTemplateTray() : (display.GetPageManager() as CollectionPageManager)?.GetDeckTemplatePicker();

    internal static DeckRecipe CurrentRecipeGuide(CollectionManagerDisplay display) => NativeRecipeSafety.OrDefault(
        () => FindCurrentRecipeGuide(display), null, LogRecipeFailure);

    private static DeckRecipe FindCurrentRecipeGuide(CollectionManagerDisplay display)
    {
        if (!active || !CurrentCollectionScene() || !ReferenceEquals(CollectionManager.Get()?.GetCollectibleDisplay(), display)) return null;
        if (display.GetViewMode() == CollectionUtils.ViewMode.DECK_TEMPLATE)
        {
            var picker = CurrentRecipePicker(display);
            var recipe = RecipeTemplates.Recipe(Field(picker, "m_currentSelectedDeck") as CollectionManager.TemplateDeck);
            return recipe != null && Profile.OwnedRecipes.Contains(recipe.Id) ? recipe : null;
        }
        if (!CollectionManager.Get().IsInEditMode() || display.GetViewMode() != CollectionUtils.ViewMode.CARDS) return null;
        var deck = CollectionManager.Get().GetEditedDeck();
        return deck == null ? null : NativeRecipeGuideText.ForSavedDeck(NativeRecipeLibrary.Current.Recipes, Profile.GetDraft(deck.ID));
    }
}
