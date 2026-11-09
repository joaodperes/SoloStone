using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hearthstone.UI;
using MiniJSON;
using UnityEngine;

// Reads the live PegUI tree for the test-control endpoint and presses elements along PegUI's own mouse path.
// Hit testing reuses PegUI.FindHitElement with the cached input position pointed at the element, so dialog
// priority, full-screen effects and render passes decide occlusion exactly as they do for a real click.
internal static class NativeUiTree
{
#if !SOLOSTONE_RELEASE
    // Fixed private API adapters for the pinned client. Missing adapters fail closed (busy / unlabelled).
    private static readonly FieldInfo MousePosition = (FieldInfo)NativeShop.UiBinding("UniversalInputManager", "m_mousePosition");
    private static readonly FieldInfo PressDisabledButtons = (FieldInfo)NativeShop.UiBinding("Box", "OnPressDisabledButtons");
    private static readonly FieldInfo PickerPlay = (FieldInfo)NativeShop.UiBinding("AbsDeckPickerTrayDisplay", "m_playButton");
    private static readonly FieldInfo PickerLeft = (FieldInfo)NativeShop.UiBinding("DeckPickerTrayDisplay", "m_leftArrow");
    private static readonly FieldInfo PickerRight = (FieldInfo)NativeShop.UiBinding("DeckPickerTrayDisplay", "m_rightArrow");
    private static readonly FieldInfo PickerFormat = (FieldInfo)NativeShop.UiBinding("DeckPickerTrayDisplay", "m_switchFormatButton");
    private static readonly FieldInfo DoneShown = (FieldInfo)NativeShop.UiBinding("RewardBoxesDisplay", "m_doneButtonFinishedShown");
    // Per-read memo of same-name sibling suffixes: exact for this frame, and each parent's children are scanned once.
    private static readonly Dictionary<Transform, Dictionary<Transform, string>> Siblings = new Dictionary<Transform, Dictionary<Transform, string>>();
    private static readonly FieldInfo CurrentDialog = (FieldInfo)NativeShop.UiBinding("DialogManager", "m_currentDialog");
    private static readonly MethodInfo TweenCount = (MethodInfo)NativeShop.UiBinding("iTween", "Count");
    private static readonly MethodInfo UberTextText = (MethodInfo)NativeShop.UiBinding("UberText", "get_Text");
    private static readonly FieldInfo GameMenuConcede = (FieldInfo)NativeShop.UiBinding("GameMenu", "m_concedeButton");
    private static readonly FieldInfo GameMenuOptions = (FieldInfo)NativeShop.UiBinding("GameMenu", "m_optionsButton");
    private static readonly FieldInfo HeroPreviewState = (FieldInfo)NativeShop.UiBinding("BaseHeroSkinInfoManager", "m_desiredVisibilityState");
    private static readonly FieldInfo HelperReplaceCard = (FieldInfo)NativeShop.UiBinding("DeckHelper", "m_replaceCardActor");
    private static readonly FieldInfo HelperChoices = (FieldInfo)NativeShop.UiBinding("DeckHelper", "m_choiceActors");

    private static readonly FieldInfo ClickableStates = (FieldInfo)NativeShop.UiBinding("Hearthstone.UI.Clickable", "m_stateCollection");

    private static string CoinTrayControlId(PegUIElement element)
    {
        var clickable = element.GetComponent<Clickable>();
        if (clickable == null || ClickableStates?.GetValue(clickable) is not WidgetBehaviorStateCollection states || states.States == null) return null;
        foreach (WidgetBehaviorState state in states.States)
            foreach (StateAction action in state?.Actions ?? new List<StateAction>())
                if (action != null && (action.Type == StateAction.ActionType.SendEvent || action.Type == StateAction.ActionType.SendEventUpward
                    || action.Type == StateAction.ActionType.SendEventDownward))
                {
                    string id = CoinTrayEventId(action.GetString(0));
                    if (id != null) return id;
                }
        return null;
    }

    internal static string CoinTrayEventId(string eventName) => eventName == "CODE_TRAY_CLICKED" ? "collection.deck.coin"
        : eventName == "CODE_CHECKBOX_TOGGLED" ? "collection.deck.coin.favorites" : null;

    internal static UiSnapshot Read()
    {
        NativeTestControl.AssertMainThread();
        var snapshot = new UiSnapshot();
        var scene = SceneMgr.Get();
        snapshot.Scene = scene?.GetMode().ToString();
        Siblings.Clear();
        snapshot.Screen = LocalLadder.Screen;
        snapshot.MatchLive = scene != null && scene.GetMode() == SceneMgr.Mode.GAMEPLAY && GameState.Get()?.IsGameOver() != true;
        var peg = PegUI.Get(); var input = UniversalInputManager.Get(); var splash = SplashScreen.Get();
        void Busy(bool condition, string name) { if (condition) snapshot.Busy.Add(name); }
        Busy(scene == null, "sceneMgrMissing"); Busy(scene != null && scene.IsTransitioning(), "sceneTransition");
        Busy(LoadingScreen.Get() != null && LoadingScreen.Get().IsTransitioning(), "loadingScreen");
        Busy(splash != null && splash.gameObject.activeInHierarchy, "splash");
        Busy(peg == null || input == null, "inputMissing");
        Busy(MousePosition == null, "unsupportedHitTest");
        Busy(input != null && input.IsTouchMode(), "touchMode");
        Busy(NativeAchievements.SubcategoryTransition, "featsTransition");

        var collection = CollectionDisplay(scene);
        var names = new Dictionary<PegUIElement, string>();
        var contexts = new Dictionary<PegUIElement, string>();
        void Name(PegUIElement element, string id) { if (element != null && !names.ContainsKey(element)) names[element] = id; }
        var box = Box.Get();
        HashSet<PegUIElement> pressDisabled = null;
        if (box != null)
        {
            Name(box.m_PlayButton, "home.play"); Name(box.m_OpenPacksButton, "home.packs"); Name(box.m_CollectionButton, "home.collection");
            if (NativeShop.HomeStoreReady) Name(box.m_StoreButton, "home.store");
            Name(NativeHelpUI.HomeButton, NativeHelpText.HomeId);
            Name(NativeAchievements.HomeButton, NativeAchievements.HomeId);
            // Hidden controls have no test-control identity, including diagnostic rows.
            pressDisabled = PressDisabledButtons?.GetValue(box) as HashSet<PegUIElement>;
        }
        foreach (var tray in UnityEngine.Object.FindObjectsByType<AbsDeckPickerTrayDisplay>(FindObjectsSortMode.None))
        {
            Name(PickerPlay?.GetValue(tray) as PegUIElement, NativeCollectionTestUi.PlayId(collection, tray));
            Name(tray.m_backButton, "picker.back"); Name(tray.m_collectionButton, "picker.collection");
            Name(NativeRematchUI.Button, NativeRematchUI.Id);
            Name(NativeDeckPickerGuide.Button, NativeDeckPickerGuide.Id);
            if (tray is DeckPickerTrayDisplay pages)
            {
                Name(PickerLeft?.GetValue(pages) as PegUIElement, "picker.prev"); Name(PickerRight?.GetValue(pages) as PegUIElement, "picker.next");
                Name(PickerFormat?.GetValue(pages) as PegUIElement, "picker.format");
            }
        }
        // Format picker popup buttons: only Standard is pressable; the rest report Enabled=false (NativeFormatLock).
        foreach (var button in NativeFormatLock.PickerButtons)
            if (button.Mode != null) Name(button.Element, "picker.format." + button.Mode.ToLowerInvariant());
        foreach (var control in NativeProfileSelector.Controls) Name(control.Key, control.Value);
        foreach (var control in NativeReportUI.Controls) Name(control.Key, control.Value);
        // #57: a Core starter's delete button (it hides the starter) and Options' Restore Starter Decks plank. Personal
        // decks' delete buttons keep their path ids: deleting a personal deck is not a reviewed test-control action.
        foreach (var deckBox in UnityEngine.Object.FindObjectsByType<CollectionDeckBoxVisual>(FindObjectsSortMode.None))
            if (NativeUIProjection.HideableLoanerKey(deckBox.GetDeckID()) != null)
                Name(deckBox.m_deleteButton, NativeStarterText.DeleteButtonId(deckBox.GetDeckID()));
        Name(NativeStarterDecks.OptionsButton, NativeStarterText.OptionsId);
        Name(NativeOptionsPace.OptionsButton, NativeOptionsPaceText.Id);
        Name(NativeTurnTimerOption.Checkbox, NativeTurnTimerOption.Id);
        Name(NativeFpsCounter.Checkbox, NativeFpsCounterText.Id);
        Name(NativeDeckInferenceOption.Checkbox, NativeDeckInferenceText.Id);
        Name(NativeTurnTimerOption.Dismiss, "options.dismiss");
        Name(NativePackFlip.Button, PackFlipText.ButtonId);
        // The hub gear (allowed only by native identity, UiRules.GameMenuControlAllowed) and the Game Menu's Options row.
        Name(BnetBar.Get()?.m_menuButton, UiRules.GameMenuId);
        var gameMenu = GameMenu.Get();
        if (gameMenu != null) Name(GameMenuOptions?.GetValue(gameMenu) as PegUIElement, UiRules.GameMenuOptionsId);
        if (gameMenu != null)
            Name(GameMenuConcede?.GetValue(gameMenu) as PegUIElement, UiRules.GameMenuConcedeId);
        var dialogs = DialogManager.Get();
        var dialog = dialogs == null ? null : CurrentDialog?.GetValue(dialogs) as DialogBase;
        if (dialog != null)
        {
            var node = new JsonNode { { "type", dialog.GetType().Name } };
            if (dialog is AlertPopup alert)
            {
                node.Add("id", alert.GetInfo()?.m_id); node.Add("header", alert.GetInfo()?.m_headerText);
                node.Add("text", alert.GetInfo()?.m_text);
                // Post-match popups carry semantic ids (rewards.continue, results.detailed); others stay dialog.<button>.
                string popupId = alert.GetInfo()?.m_id;
                Name(alert.m_okayButton, NativeHelpText.DialogButtonId(popupId, "okay") ?? NativeWelcomeText.DialogButtonId(popupId, "okay")
                    ?? LocalRewardPresentation.DialogButtonId(popupId, "okay"));
                Name(alert.m_confirmButton, NativeHelpText.DialogButtonId(popupId, "confirm") ?? NativeStarterText.DialogButtonId(popupId, "confirm")
                    ?? NativeSaveDeleteText.DialogButtonId(popupId, "confirm") ?? NativeDeckGuideText.DialogButtonId(popupId, "confirm")
                    ?? LocalRewardPresentation.DialogButtonId(popupId, "confirm"));
                Name(alert.m_cancelButton, NativeHelpText.DialogButtonId(popupId, "cancel") ?? NativeStarterText.DialogButtonId(popupId, "cancel")
                    ?? NativeSaveDeleteText.DialogButtonId(popupId, "cancel") ?? NativeDeckGuideText.DialogButtonId(popupId, "cancel")
                    ?? LocalRewardPresentation.DialogButtonId(popupId, "cancel"));
                // The save-delete body carries the player's save name: the denylist reads its header only.
                string context = alert.GetInfo()?.m_headerText + (popupId == NativeSaveDeleteText.PopupId ? "" : " " + alert.GetInfo()?.m_text);
                foreach (PegUIElement button in alert.GetComponentsInChildren<PegUIElement>(true)) contexts[button] = context;
            }
            else if (dialog is CardListPopup cardList) NativeGuideCards.Describe(cardList, node, (element, id) => Name(element, id), contexts);
            snapshot.Dialogs.Add(node);
        }
        // Clickable creates DontSave PegUIElements: global enumeration misses ProductButton and its proxies.
        // Name and walk local roots before collecting hitboxes, so reviewed collider-less buttons get fallback bounds.
        var shopControls = new List<PegUIElement>();
        var shopProxies = new List<PegUIElementProxy>();
        var packDetails = NativePackTestUi.Name(snapshot, Name, shopControls, shopProxies);
        if (NativeShop.Active)
        {
            var shop = Shop.Get();
            var page = shop?.ProductPageController?.CurrentProductPage;
            var roots = new Component[] { shop, page, page?.GetComponentInParent<ProductPageContainer>(), dialog }
                .Concat(UnityEngine.Object.FindObjectsByType<StorePurchaseAuth>(FindObjectsSortMode.None))
                .Concat(UnityEngine.Object.FindObjectsByType<StoreQuantityPrompt>(FindObjectsSortMode.None));
            foreach (var root in roots.Where(root => root != null).Distinct())
            {
                shopControls.AddRange(root.GetComponentsInChildren<PegUIElement>(true));
                shopProxies.AddRange(root.GetComponentsInChildren<PegUIElementProxy>(true));
            }
            foreach (var control in shopControls.Distinct())
            {
                if (NativeShop.IsLocalShopElement(control, out string id)) Name(control, id);
            }
        }
        // Feats (native Journal widget) also spawns DontSave Clickable controls: walk its root like the shop.
        if (NativeAchievements.Active)
        {
            foreach (var control in NativeAchievements.Controls)
            {
                shopControls.Add(control);
                string id = NativeAchievements.ControlId(control);
                if (id != null) Name(control, id);
            }
            shopProxies.AddRange(NativeAchievements.Proxies);
        }
        // #57 welcome reward scroll: its dismiss is a Widget Clickable (DontSave PegUIElement); walk it like Feats.
        foreach (var control in NativeWelcomeUI.ScrollControls) { shopControls.Add(control); Name(control, NativeWelcomeText.ScrollDoneId); }
        shopProxies.AddRange(NativeWelcomeUI.ScrollProxies);
        if (NativeResultsUI.PremiumRewards != null)
        {
            foreach (var control in NativeResultsUI.PremiumRewards.Controls) { shopControls.Add(control); Name(control, NativeResultsUI.PremiumRewards.ControlId(control)); }
            shopProxies.AddRange(NativeResultsUI.PremiumRewards.Proxies);
            snapshot.PremiumRewards = NativeResultsUI.PremiumRewards.ItemState();
        }
        if (collection != null)
            foreach (var coin in UnityEngine.Object.FindObjectsByType<Hearthstone.Progression.CoinPreview>(FindObjectsSortMode.None))
            {
                shopControls.AddRange(coin.GetComponentsInChildren<PegUIElement>(true));
                shopProxies.AddRange(coin.GetComponentsInChildren<PegUIElementProxy>(true));
            }
        if (collection != null)
            foreach (var tray in UnityEngine.Object.FindObjectsByType<DeckTrayCosmeticCoinContent>(FindObjectsSortMode.None))
            {
                foreach (var control in tray.GetComponentsInChildren<PegUIElement>(true))
                {
                    shopControls.Add(control);
                    string id = CoinTrayControlId(control);
                    if (id != null) Name(control, id);
                }
                shopProxies.AddRange(tray.GetComponentsInChildren<PegUIElementProxy>(true));
            }
        CollectionUtils.ViewMode? collectionMode = collection?.GetViewMode();
        if (collection != null) { NameCollection(collection, Name); NativeCollectionTestUi.Name(collection, Name); }
        if (collection != null) NameCrafting(Name);
        // Before the end-game hitbox: while the tier chest is shown it owns that hitbox's clicks.
        Name(NativeTierRewards.ClickTarget, LocalResultsFlow.TierControlId);
        var end = EndGameScreen.Get();
        if (end != null) Name(end.m_hitbox, "endgame.continue");
        var rewards = RewardBoxesDisplay.Get();
        if (rewards != null) Name(rewards.m_DoneButton, "rewards.done");
        Name(NativeRewardUI.DetailedButton, LocalRewardPresentation.DetailedResultsId);
        foreach (var scrollControl in NativePetMoment.ScrollControls()) Name(scrollControl.Key, scrollControl.Value);
        Name(NativeRewardUI.RematchButton, LocalRewardPresentation.RematchId);
        // Done keeps an enabled collider while hidden behind unopened chests; it only works once shown (fails closed).
        bool doneShown = rewards != null && DoneShown != null && DoneShown.GetValue(rewards) is bool shown && shown;

        var proxies = new Dictionary<PegUIElement, List<Collider>>();
        foreach (var proxy in UnityEngine.Object.FindObjectsByType<PegUIElementProxy>(FindObjectsSortMode.None).Concat(shopProxies).Distinct())
        {
            if (proxy.Owner == null || !proxy.gameObject.activeInHierarchy) continue;
            if (!proxies.TryGetValue(proxy.Owner, out List<Collider> list)) proxies[proxy.Owner] = list = new List<Collider>();
            list.AddRange(proxy.GetComponents<Collider>().Where(collider => collider.enabled));
        }
        Vector3 mouse = MousePosition != null && input != null ? (Vector3)MousePosition.GetValue(input) : Vector3.zero;
        bool hitTest = MousePosition != null && input != null && peg != null && !input.IsTouchMode();
        var cameras = new Dictionary<int, Camera>();
        bool rewardBlocks = NativeRewardUI.BlocksHome;
        bool achievementBlocks = NativeAchievements.Active;
        bool selectorBlocks = NativeProfileSelector.BlocksHome;
        var labelClock = new System.Diagnostics.Stopwatch(); var hitClock = new System.Diagnostics.Stopwatch();
        var enumerateClock = System.Diagnostics.Stopwatch.StartNew(); var tweenClock = new System.Diagnostics.Stopwatch();
        // FindObjectsByType does not return every live control (live: the picker's next-page arrow was active, enabled
        // and had a collider, yet was never returned), so reviewed controls are also walked directly.
        var all = UnityEngine.Object.FindObjectsByType<PegUIElement>(FindObjectsSortMode.None).Concat(shopControls).Concat(names.Keys).Distinct().ToList();
        enumerateClock.Stop();
        var visited = new HashSet<PegUIElement>();
        try
        {
            foreach (var element in all)
            {
                if (element == null || !element.gameObject.activeInHierarchy && !packDetails.ContainsKey(element)) continue;
                visited.Add(element);
                var colliders = element.GetComponents<Collider>().Where(collider => collider.enabled).ToList();
                if (proxies.TryGetValue(element, out List<Collider> extra)) colliders.AddRange(extra);
                var bounds = colliders.Select(collider => collider.bounds).ToList();
                bool named = names.ContainsKey(element);
                // Named buttons without colliders (deck picker page arrows) are hit through PegUI's render-pass hit
                // detection or a child hitbox: frame them by child colliders, else renderers. FindHitElement still decides hit.
                string diag = null;
                if (bounds.Count == 0 && named)
                {
                    var own = element.GetComponents<Collider>();
                    diag = "fallback colliders=" + own.Length + " enabled=" + own.Count(collider => collider.enabled);
                }
                if (bounds.Count == 0 && named)
                    bounds.AddRange(element.GetComponentsInChildren<Collider>(false)
                        .Where(collider => collider.enabled && collider.GetComponentInParent<PegUIElement>() == element).Select(collider => collider.bounds));
                if (bounds.Count == 0 && named)
                    bounds.AddRange(element.GetComponentsInChildren<Renderer>(packDetails.ContainsKey(element))
                        .Where(renderer => renderer.enabled || packDetails.ContainsKey(element)).Select(renderer => renderer.bounds));
                if (bounds.Count == 0 && !named) continue;
                Camera camera = ProjectionCamera(element.gameObject, cameras);
                if (camera == null) continue;
                var item = new UiElement {
                    Type = element.GetType().Name, Instance = element.GetInstanceID(), Handle = element,
                    // Some owners disable PRESS/TAP/RELEASE individually and keep m_enabled; a click then does nothing.
                    Enabled = element.IsEnabled() && (element.IsEnabled(UIEventType.PRESS) || element.IsEnabled(UIEventType.TAP) || element.IsEnabled(UIEventType.RELEASE)),
                    PressDisabled = pressDisabled != null && pressDisabled.Contains(element),
                    Path = PathOf(element.transform), Diag = diag
                };
                if (names.TryGetValue(element, out string id)) item.Id = id;
                if (packDetails.TryGetValue(element, out JsonNode detail))
                {
                    item.Pack = detail;
                    item.Enabled &= detail["shown"] is bool packShown && packShown;
                    if (detail.ContainsKey("clickReady")) item.Enabled &= detail["clickReady"] is bool clickReady && clickReady;
                    if (detail.ContainsKey("revealed")) item.Enabled &= detail["ready"] is bool cardReady && cardReady
                        && detail["inputEnabled"] is bool cardInput && cardInput
                        && (detail["revealed"] is bool revealed && revealed || detail["revealEnabled"] is bool revealEnabled && revealEnabled);
                }
                if (NativeProfileSelector.IsSelected(element)) item.Selected = true;
                if (NativeProfileSelector.OwnsInput(element)) item.Selectable = true;
                if (item.Id == "report.include-save") { item.Selectable = true; item.Selected = NativeReportUI.IncludeSave; }
                if (item.Id == NativeFpsCounterText.Id) { item.Selectable = true; item.Selected = NativeFpsCounter.Checked; }
                if (item.Id == NativeDeckInferenceText.Id) { item.Selectable = true; item.Selected = NativeDeckInferenceOption.Checked; }
                if (contexts.TryGetValue(element, out string owner)) item.Context = owner;
                if (element is CollectionDeckBoxVisual deckBox)
                {
                    item.DeckId = deckBox.GetDeckID(); item.Selected = deckBox.IsSelected();
                    var boxDeck = deckBox.GetCollectionDeck();
                    item.DeckName = boxDeck?.Name; item.DeckClass = boxDeck?.GetClasses()?.FirstOrDefault().ToString();
                    if (item.Id == null && item.DeckId > 0) item.Id = "deck:" + item.DeckId;
                }
                if (item.Id == null && collectionMode != null) item.Id = CollectionTileId(element, collectionMode.Value);
                if (item.Id == null && element is RewardPackage) item.Id = "rewards.package";
                if (item.Id == "rewards.done" && !doneShown) item.Enabled = false;
                Rect pixels = default;
                if (bounds.Count == 0) item.HitBy = "noHitbox";  // listed off screen so a missing hitbox is visible
                else Project(item, bounds, camera, out pixels, element.GetComponentInParent<PopupRoot>()?.PrimaryRenderPass);
                if (NativeFeatsReveal.IsSubcategory(item.Id))
                    item.ScrollClipped = NativeAchievements.SubcategoryClipped(element, item.Id, bounds);
                item.ScrollClipped |= NativeCollectionTestUi.Clipped(item, bounds);
                if (item.Pack != null && !(item.Pack["shown"] is bool visible && visible)) item.OnScreen = false;
                if (item.OnScreen) { labelClock.Start(); item.Label = Label(element); labelClock.Stop(); }
                if (item.Id != null && item.Id.StartsWith(NativePremiumRewards.DoneId, StringComparison.Ordinal)) item.Label = "Continue premium rewards";
                // Off-screen elements never block idle, so only on-screen ones pay for the tween scan.
                if (item.OnScreen) { tweenClock.Start(); item.Tweening = TweenCount != null && ((int)TweenCount.Invoke(null, new object[] { element.gameObject }) != 0
                    || NativeFeatsReveal.IsSubcategory(item.Id) && NativeAchievements.SubcategoryScrolling(element, item.Id,
                        target => (int)TweenCount.Invoke(null, new object[] { target }) != 0)); tweenClock.Stop(); }
                if (item.OnScreen) item.Tweening |= NativeCollectionTestUi.Scrolling(item);
                if (item.OnScreen && hitTest) { hitClock.Start(); HitTest(item, element, pixels, input, peg, names); hitClock.Stop(); }
                // Under an owned presentation a raycast can still reach controls that ignore the click: report the truth.
                if (item.Hit && rewardBlocks && !NativeRewardUI.OwnsInput(element) && !NativeResultsUI.OwnsInput(element))
                { item.Hit = false; item.HitBy = "rewardPresentation"; }
                else if (item.Hit && achievementBlocks && !NativeAchievements.OwnsInput(element))
                { item.Hit = false; item.HitBy = "feats"; }
                else if (item.Hit && selectorBlocks && NativeProfileSelector.InputBlock(element, dialog) is string covered)
                { item.Hit = false; item.HitBy = covered; }
                snapshot.Elements.Add(item);
            }
        }
        finally { if (MousePosition != null && input != null) MousePosition.SetValue(input, mouse); }
        foreach (var element in names.Keys.Where(element => element != null && !visited.Contains(element)))
            snapshot.Elements.Add(new UiElement {
                // Own namespace: a diagnostic row must never take (or #n-shift) the id of a real, pressable control.
                Id = "diag:" + (names.TryGetValue(element, out string missing) ? missing : element.GetInstanceID().ToString()), Type = element.GetType().Name,
                Instance = element.GetInstanceID(), Handle = element, Path = PathOf(element.transform),
                Diag = "notEnumerated activeSelf=" + element.gameObject.activeSelf + " activeInHierarchy=" + element.gameObject.activeInHierarchy
                    + " componentEnabled=" + element.enabled + " colliders=" + element.GetComponents<Collider>().Length
            });
        snapshot.EnumerateMs = enumerateClock.Elapsed.TotalMilliseconds; snapshot.TweenMs = tweenClock.Elapsed.TotalMilliseconds;
        snapshot.LabelMs = labelClock.Elapsed.TotalMilliseconds; snapshot.HitMs = hitClock.Elapsed.TotalMilliseconds;
        var finishClock = System.Diagnostics.Stopwatch.StartNew();
        UiRules.Finish(snapshot);
        snapshot.FinishMs = finishClock.Elapsed.TotalMilliseconds;
        return snapshot;
    }

    // Native Collection only (not deck pickers or brawl editors): the current page's view mode decides what a tile is.
    private static CollectionManagerDisplay CollectionDisplay(SceneMgr scene) =>
        scene == null || scene.GetMode() != SceneMgr.Mode.COLLECTIONMANAGER ? null
            : CollectionManager.Get()?.GetCollectibleDisplay() as CollectionManagerDisplay;

    // Tiles are named from their live native owner on every read, so a pooled visual reused for another back or skin
    // gets a new id (and a press in flight sees "identity changed"). Hidden or other-mode visuals keep path ids.
    private static string CollectionTileId(PegUIElement element, CollectionUtils.ViewMode mode)
    {
        if (element is CollectionCardVisual visual)
        {
            if (!visual.IsShown() || visual.GetVisualType() != mode) return null;
            if (mode == CollectionUtils.ViewMode.CARD_BACKS)
            {
                var back = visual.GetActor()?.GetComponent<CollectionCardBack>();
                return back == null ? null : UiRules.CollectionCardBackId(back.GetCardBackId());
            }
            if (mode == CollectionUtils.ViewMode.COINS)
                return UiRules.CollectionCoinId(CosmeticCoinManager.Get().GetCoinIdFromCoinCard(visual.CardId));
            return mode == CollectionUtils.ViewMode.HERO_SKINS ? UiRules.CollectionHeroSkinId(visual.CardId) : null;
        }
        // Hero Skins landing page: one class button per class (label "Paladin 2/8"), owned by the collection picker.
        if (element is HeroPickerButton hero && mode == CollectionUtils.ViewMode.HERO_PICKER
            && hero.GetComponentInParent<CollectionHeroPickerButtons>() != null)
            return UiRules.CollectionHeroClassId(hero.m_heroClass.ToString());
        return null;
    }

    // Card Backs / Hero Skins book tabs, then an open card back / hero skin preview: the favourite button its owner wires
    // to HandleFavoriteToggle / SetFavoriteHero (both then CancelPreview), and the off-click catcher that only cancels.
    // Buy buttons stay unnamed (denylisted).
    private static void NameCollection(CollectionManagerDisplay display, Action<PegUIElement, string> name)
    {
        void Active(PegUIElement element, string id) { if (element != null && element.gameObject.activeInHierarchy) name(element, id); }
        if (display.GetPageManager() is CollectionPageManager pages)
        {
            Active(pages.m_cardBacksTab, "collection.tab.cardbacks"); Active(pages.m_heroSkinsTab, "collection.tab.heroskins");
            Active(pages.m_coinsTab, "collection.tab.coins");
            Active(pages.m_petsTab, "collection.tab.pets");
        }
        foreach (var coin in UnityEngine.Object.FindObjectsByType<Hearthstone.Progression.CoinPreview>(FindObjectsSortMode.None))
        {
            Active(coin.m_favoriteButton, UiRules.CollectionFavorite);
            Active(coin.m_clickable?.GetComponent<PegUIElement>(), UiRules.CollectionPreviewClose);
        }
        var backs = CardBackInfoManager.Get();
        if (backs != null && backs.IsPreviewing)
        {
            Active(backs.m_favoriteButton, UiRules.CollectionFavorite); Active(backs.m_offClicker, UiRules.CollectionPreviewClose);
        }
        var heroes = HeroSkinInfoManager.Get();
        if (heroes != null && heroes.IsShowingPreview && HeroPreviewState != null)
        {
            // Same choice as BaseHeroSkinInfoManager.UpdateFavoriteButton: vanilla frame or the new/custom frame.
            bool vanilla = HeroPreviewState.GetValue(heroes) as string == "SHOW_VANILLA_HERO";
            Active(vanilla ? heroes.m_vanillaHeroFavoriteButton : heroes.m_newHeroFavoriteButton, UiRules.CollectionFavorite);
            Active(heroes.m_offClicker, UiRules.CollectionPreviewClose);
        }
    }

    private static void NameCrafting(Action<PegUIElement, string> name)
    {
        void Active(PegUIElement element, string id) { if (element != null && element.gameObject.activeInHierarchy && id != null) name(element, id); }
        // IsActive/IsInitialized first: the native Get methods otherwise instantiate prefabs during a read.
        if (DeckHelper.IsActive())
        {
            var helper = DeckHelper.Get();
            var missing = HelperReplaceCard?.GetValue(helper) as Actor;
            // UpdateChoices wires RELEASE on the ghost object, not the actor's regular collider.
            Active(missing?.m_ghostCardGameObject?.GetComponent<PegUIElement>(), UiRules.DeckHelperNotOwned);
            if (HelperChoices?.GetValue(helper) is List<Actor> choices)
                foreach (var actor in choices.Where(actor => actor != null))
                    Active(actor.GetCollider()?.GetComponent<PegUIElement>(), UiRules.DeckHelperChoiceId(actor.GetEntityDef()?.GetCardId()));
            Active(helper.m_replaceDoneButton, UiRules.DeckHelperClose);
            Active(helper.m_suggestDoneButton, UiRules.DeckHelperClose);
        }
        if (CraftingManager.GetIsInCraftingMode() && CraftingManager.IsInitialized)
        {
            var crafting = CraftingManager.Get();
            Active(crafting.m_craftingUI?.m_buttonCreate, UiRules.CraftingCreate);
            Active(crafting.m_craftingUI?.m_buttonDisenchant, UiRules.CraftingDisenchant);
            Active(crafting.m_offClickCatcher?.GetComponent<PegUIElement>(), UiRules.CraftingClose);
        }
    }
#endif

    internal static Camera ProjectionCamera(GameObject gameObject, Dictionary<int, Camera> cameras)
    {
        var popup = gameObject.GetComponentInParent<PopupRoot>();
        if (popup != null) return popup.IsEnabled ? popup.PopupCamera : null;
        // Match CameraUtils.FindProjectionCameraForObject for overlay ownership, not the shared layer mask.
        // Collider/renderer world bounds already include the BoxCalibrator and every ancestor transform.
        var overlay = OverlayUI.Get();
        if (overlay != null && overlay.HasObject(gameObject)) return overlay.m_UICamera;
        int layer = gameObject.layer;
        if (!cameras.TryGetValue(layer, out Camera camera))
        {
            camera = CameraUtils.FindFirstByLayerMask(1 << layer);
            if (camera == null) camera = Camera.main;
            cameras[layer] = camera;
        }
        return camera;
    }
#if !SOLOSTONE_RELEASE

    private static void Project(UiElement item, List<Bounds> boxes, Camera camera, out Rect pixels, CameraOverridePass renderPass = null)
    {
        float left = float.MaxValue, right = float.MinValue, bottom = float.MaxValue, top = float.MinValue;
        bool behind = false;
        foreach (Bounds bounds in boxes)
        {
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 world = new Vector3((corner & 1) == 0 ? bounds.min.x : bounds.max.x,
                    (corner & 2) == 0 ? bounds.min.y : bounds.max.y, (corner & 4) == 0 ? bounds.min.z : bounds.max.z);
                Vector3 point = ProjectPoint(world, camera, renderPass);
                if (point.z < 0) behind = true;
                left = Math.Min(left, point.x); right = Math.Max(right, point.x);
                bottom = Math.Min(bottom, point.y); top = Math.Max(top, point.y);
            }
        }
        float width = UnityEngine.Screen.width, height = UnityEngine.Screen.height;
        pixels = Rect.MinMaxRect(Math.Max(0, left), Math.Max(0, bottom), Math.Min(width, right), Math.Min(height, top));
        item.OnScreen = !behind && pixels.width >= 1 && pixels.height >= 1;
        item.X = left * 1280f / width; item.Y = (height - top) * 720f / height;
        item.W = (right - left) * 1280f / width; item.H = (top - bottom) * 720f / height;
    }

    internal static Vector3 ProjectPoint(Vector3 world, Camera camera, CameraOverridePass renderPass)
    {
        if (ReferenceEquals(renderPass, null) || (renderPass.toOverride & (CameraOverridePass.OverrideFlags.ProjectionMatrix
            | CameraOverridePass.OverrideFlags.ViewMatrix)) == 0) return camera.WorldToScreenPoint(world);
        // PopupRoot renders perspective pages with per-pass matrices; the shared UI camera stays orthographic.
        var view = (renderPass.toOverride & CameraOverridePass.OverrideFlags.ViewMatrix) != 0
            ? renderPass.viewMatrixOverride : camera.worldToCameraMatrix;
        var projection = (renderPass.toOverride & CameraOverridePass.OverrideFlags.ProjectionMatrix) != 0
            ? renderPass.projectionOverride : camera.projectionMatrix;
        Vector4 eye = view * new Vector4(world.x, world.y, world.z, 1);
        Vector4 clip = projection * eye;
        if (clip.w <= 0) return new Vector3(0, 0, -1);
        Rect viewport = camera.pixelRect;
        return new Vector3(viewport.x + (clip.x / clip.w + 1) * .5f * viewport.width,
            viewport.y + (clip.y / clip.w + 1) * .5f * viewport.height, -eye.z);
    }

    // Playwright's "receives events": the element must be what a click at one of these points would hit.
    private static void HitTest(UiElement item, PegUIElement element, Rect pixels, UniversalInputManager input, PegUI peg, Dictionary<PegUIElement, string> names)
    {
        float width = UnityEngine.Screen.width, height = UnityEngine.Screen.height;
        int count = UiRules.HitSampleCount(pixels.width / width, pixels.height / height);
        for (int i = 0; i < count; i++)
        {
            float[] share = UiRules.HitSamples[i];
            var point = new Vector2(pixels.xMin + pixels.width * share[0], pixels.yMax - pixels.height * share[1]);
            MousePosition.SetValue(input, new Vector3(point.x, point.y, 0));
            PegUIElement hit = peg.FindHitElement();
            if (hit == element)
            {
                item.Hit = true;
                if (i != 0) { item.HitX = point.x * 1280f / width; item.HitY = (height - point.y) * 720f / height; }
                return;
            }
            if (i == 0) item.HitBy = hit == null ? "nothing" : names.TryGetValue(hit, out string name) ? name : hit.GetType().Name + ":" + hit.gameObject.name;
        }
    }

    private static string Label(PegUIElement element)
    {
        if (UberTextText == null) return null;
        foreach (Component text in element.GetComponentsInChildren(UberTextText.DeclaringType, false))
        {
            string value = UberTextText.Invoke(text, null) as string;
            if (!string.IsNullOrWhiteSpace(value)) return string.Join(" ", value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        }
        return null;
    }

    private static string PathOf(Transform transform)
    {
        var parts = new List<string>();
        for (; transform != null; transform = transform.parent)
        {
            Transform parent = transform.parent;
            parts.Add(parent == null ? transform.name : SiblingNames(parent)[transform]);
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    // Child names with "[i]" when several children share a name, in sibling order.
    private static Dictionary<Transform, string> SiblingNames(Transform parent)
    {
        if (Siblings.TryGetValue(parent, out var names)) return names;
        var children = new List<KeyValuePair<Transform, string>>(parent.childCount);
        for (int i = 0; i < parent.childCount; i++) { Transform child = parent.GetChild(i); children.Add(new KeyValuePair<Transform, string>(child, child.name)); }
        names = new Dictionary<Transform, string>(children.Count);
        foreach (var group in children.GroupBy(child => child.Value, StringComparer.Ordinal))
        {
            int index = 0, count = group.Count();
            foreach (var child in group) names[child.Key] = count > 1 ? child.Value + "[" + index++ + "]" : child.Value;
        }
        return Siblings[parent] = names;
    }

    internal static bool Alive(UiElement item) => item.Handle is PegUIElement element && element != null && element.gameObject.activeInHierarchy;

    // Mouse-down frame: PegUI hovers then presses the element under the cursor.
    // Use the same hit-tested point as Read(), restoring the real pointer before returning.
    internal static T AtPoint<T>(UiElement item, Func<T> action)
    {
        var input = UniversalInputManager.Get();
        Vector3 old = (Vector3)MousePosition.GetValue(input);
        float x = item.HitX >= 0 ? item.HitX : item.X + item.W / 2;
        float y = item.HitY >= 0 ? item.HitY : item.Y + item.H / 2;
        try
        {
            MousePosition.SetValue(input, new Vector3(x * UnityEngine.Screen.width / 1280f,
                (720f - y) * UnityEngine.Screen.height / 720f, 0));
            return action();
        }
        finally { MousePosition.SetValue(input, old); }
    }

    internal static void Press(UiElement item)
    {
        NativeTestControl.AssertMainThread();
        var element = (PegUIElement)item.Handle;
        element.TriggerOver(); element.TriggerPress();
    }

    // Mouse-up away from the element (it was covered or disabled after mouse-down): PegUI only sends release-all(false).
    internal static void Cancel(UiElement item)
    {
        NativeTestControl.AssertMainThread();
        var element = (PegUIElement)item.Handle;
        element.TriggerReleaseAll(false); element.TriggerOut();
    }

    // Right mouse-down: PegUI calls TriggerRightClick on the hovered element; nothing is pressed or released.
    internal static bool RightClickable(UiElement item) =>
        item?.Handle is PegUIElement element && element != null && element.IsEnabled() && element.IsEnabled(UIEventType.RIGHTCLICK);

    internal static void RightClick(UiElement item)
    {
        NativeTestControl.AssertMainThread();
        var element = (PegUIElement)item.Handle;
        element.TriggerOver(); element.TriggerRightClick();
    }

    // Later frame: the cursor leaves the tile (the opened preview's off-clicker now covers it).
    internal static void RightRelease(UiElement item)
    {
        NativeTestControl.AssertMainThread();
        ((PegUIElement)item.Handle).TriggerOut();
    }

    // Mouse-up frame: tap (short click), release, release-all over the element, then the cursor leaves it.
    internal static void Release(UiElement item)
    {
        NativeTestControl.AssertMainThread();
        var element = (PegUIElement)item.Handle;
        element.TriggerTap(); element.TriggerRelease(); element.TriggerReleaseAll(true); element.TriggerOut();
    }
#endif
}
