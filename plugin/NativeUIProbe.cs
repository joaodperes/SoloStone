using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using BepInEx.Logging;
using Blizzard.T5.Core.Utils;
using Blizzard.T5.Jobs;
using HarmonyLib;
using Hearthstone.Core;
using Hearthstone.Streaming;
using PegasusShared;
using UnityEngine;
using HiddenCards = Game.PackOpening.HiddenCards;
using EngineCardType = SabberStoneCore.Enums.CardType;
using EngineGameTag = SabberStoneCore.Enums.GameTag;

internal static partial class NativeUIProbe
{
    private const int LegacyTripleUpgradeMetadataTag = 1429;
    private enum NativeAuditScope { All, WeaponOnly, NonWeapon }
    private sealed class NativeAuditMapping
    {
        internal readonly string Name;
        internal readonly GAME_TAG? NativeTag;
        internal readonly EngineGameTag EngineTag;
        internal readonly NativeAuditScope Scope;
        internal readonly bool Fatal;
        internal NativeAuditMapping(string name, GAME_TAG? nativeTag, EngineGameTag engineTag,
            NativeAuditScope scope = NativeAuditScope.All, bool fatal = true)
        { Name = name; NativeTag = nativeTag; EngineTag = engineTag; Scope = scope; Fatal = fatal; }
    }
    private sealed class NativeMetadataMapping
    {
        internal readonly string Name;
        internal readonly GAME_TAG? NativeTag;
        internal readonly int EngineTag;
        internal NativeMetadataMapping(string name, GAME_TAG? nativeTag, int engineTag)
        { Name = name; NativeTag = nativeTag; EngineTag = engineTag; }
    }
#if SOLOSTONE_RELEASE
    private static string Reports = null;
#else
    private static string Reports = @"C:\hs-native-ui-"+@"prototype\reports";
#endif
    private static readonly string[] Heroes = LocalDecks.PlayerHeroes.ToArray();
    private static readonly Dictionary<Type, object> cache = new Dictionary<Type, object>();
    private static readonly Dictionary<long, LocalDeck> decks = new Dictionary<long, LocalDeck>();
    private static readonly Queue<Action> callbacks = new Queue<Action>();
    private static MethodInfo publisher;
    private static ManualLogSource log;
    private static string runId, phase = "WaitingBridge", failure = "", missing = "", baselineHash, selected = "", packStartHash = "", packCommitHash = "";
    private static string transactionFacts = "none", liveGrantProof = "none";
    private static string[] receipt;
    private static bool active, ready, attempted, replayArmed, replayRunning, massBoosterFed, doneAcknowledged, sceneWaiting, auditingOriginal, auditingEffective;
#if STANDARD_PROOF
    private static bool proofAuditPassed;
#endif
    private static int receiptSequence, receiptPacks;
    private static bool massHighlightsFinished, massSummaryDismissed, massPageReady, massRevealAllStarted;
    private static string[] massPageReceipt;
    private static float began, sceneBegan, replayBegan;
    private static PackOpening armedPack;
    private static PackOpeningDirector revealDirector;
    private static object boundButton;
    private static Box boundHubBox;
    private static SceneMgr.Mode target;
    private static long lastSelectedId;
    public static bool Active => active;
    internal static bool IsolatedLaunch => runId != null;
    public static bool HideMenu => true;
    private static LocalProfile Profile => LocalLadder.Profile;
    internal static LocalProfile RewardProfile => Profile;
    internal static object RewardAssetLoader => Service("AssetLoader");
    internal static bool RewardRetryContextReady
    {
        get
        {
            bool initialized = SceneMgr.IsInitialized();
            bool stable = active && ready && initialized && !sceneWaiting && !SceneMgr.Get().IsTransitioning();
            return LocalRewardPresentation.CanServiceSettlementRetry(stable, LocalLadder.Screen,
                initialized && SceneMgr.Get().GetMode() == SceneMgr.Mode.GAMEPLAY);
        }
    }
    internal static bool GameplayRewardContextReady
    {
        get
        {
            bool initialized = SceneMgr.IsInitialized();
            bool stable = active && ready && initialized && !sceneWaiting && !SceneMgr.Get().IsTransitioning();
            return LocalRewardPresentation.CanPresentInGameplay(stable, LocalLadder.Screen,
                initialized && SceneMgr.Get().GetMode() == SceneMgr.Mode.GAMEPLAY, ClientBridge.OwnsLocalPostMatch);
        }
    }
    internal static bool RewardContextReady
    {
        get
        {
            bool initialized = SceneMgr.IsInitialized();
            bool stable = active && ready && initialized && !sceneWaiting && !SceneMgr.Get().IsTransitioning()
                && LocalLadder.Screen != "Playing" && LocalLadder.Screen != "Starting";
            return LocalRewardPresentation.CanPresentInScene(stable,
                initialized && SceneMgr.Get().GetMode() == SceneMgr.Mode.HUB);
        }
    }
    internal static bool AchievementToastContextReady
    {
        get
        {
            return AchievementToastHostReady() && Profile.PendingRewardPresentation == null
                && NativeResultsUI.Receipt == null && !NativeResultsUI.BlocksEscape;
        }
    }
    // #57 welcome popup: the HUB is idle (no selector, results, rewards, catch-up, toast, Feats, shop, dialog or menu).
    private static bool WelcomeContextReady() => AchievementToastContextReady && NativeResultsUI.CanStart
        && !NativeAchievementToast.Active && !NativeProfileSelector.BlocksHome && AtHome();
    // #57 welcome scroll: opens after the popup's OK once the HUB scene is stable again.
    private static bool WelcomeScrollHubReady() => RewardContextReady && AtHome();
    internal static bool MatchAchievementToastContextReady(LocalProfile profile, string match) =>
        AchievementToastHostReady(true) && ReferenceEquals(Profile, profile) && !string.IsNullOrEmpty(match)
        && Profile.PendingRewardPresentation?.Id == match && NativeResultsUI.Receipt?.MatchId == match;

    private static bool AchievementToastHostReady(bool matchToast = false)
    {
        bool gameplay = matchToast && GameplayRewardContextReady;
        if ((!RewardContextReady && !gameplay) || Profile == null || Profile.Pending != null || Profile.PackRevealPending
            || replayArmed || replayRunning || armedPack != null || revealDirector != null
            || NativeRewardUI.Active || NativeAchievements.Active || (!gameplay && ClientBridge.OwnsLocalPostMatch)
            || NativeCrafting.PresentationBusy || NativeShop.PresentationBusy || NativeWelcomeUI.ScrollActive)
            return false;
        var bar = BnetBar.Get();
        var box = Box.Get();
        var shop = Shop.Get();
        return bar?.m_socialToastBone != null && bar.m_socialToastBone.activeInHierarchy
            && (gameplay || (box != null && box.IsInStateWithButtons()))
            && UniversalInputManager.Get()?.IsDialogActive() != true
            && DialogManager.Get()?.ShowingDialog() != true
            && PopupDisplayManager.Get()?.IsShowing != true
            && GameMenu.Get()?.GameMenuIsShown() != true
            && OptionsMenu.Get()?.IsShown() != true
            && !(shop != null && shop.IsOpen())
            && !UnityEngine.Object.FindObjectsByType<Notification>(FindObjectsSortMode.None)
                .Any(NotificationBlocksAchievementToast);
    }

    private static bool NotificationBlocksAchievementToast(Notification notification)
    {
        if (notification == null || !notification.gameObject.activeInHierarchy) return false;
        return notification.GetComponentsInChildren<Renderer>()
            .Any(renderer => renderer.enabled && renderer.gameObject.activeInHierarchy && renderer.isVisible);
    }
    public static void Initialize(Harmony harmony, ManualLogSource logger, string launchId, LocalProfile selectedProfile, string reports = null)
    {
        if (!ReferenceEquals(selectedProfile, LocalLadder.Profile))
            throw new InvalidOperationException("Native and ladder profiles differ");
#if STANDARD_PROOF
        CraftingLedgerRegistry.Current = null;
#else
        CraftingLedgerRegistry.Current = selectedProfile == null ? null : new LocalCraftingLedger(selectedProfile);
#endif
        Reports = reports ?? Reports;
        log = logger; runId = launchId;
        NativeShop.Initialize(harmony, logger);
        NativeHiddenEntries.Initialize(harmony, logger);
        NativeHelpUI.Initialize(harmony, logger);
        NativeRematchUI.Initialize(logger);
        NativeDeckPickerGuide.Initialize(logger);
        NativeGuideCards.Install(harmony, Reports);
        NativeDeckRecord.Initialize(logger);
        NativePackFlip.Initialize(logger);
        NativeAchievements.Initialize(harmony, logger);
        Hook(harmony, typeof(Options), "GetBoolImpl", nameof(OnboardingOption), new[] { typeof(Option), typeof(bool).MakeByRefType() });
        Hook(harmony, typeof(BnetBarMenuButton), "UpdateHighlight", nameof(HiddenMenuHighlight), Type.EmptyTypes);
        publisher = NativeUIProjection.ResolvePublisher(typeof(NetCache));
        InitializeCosmetics(harmony);
        InitializeDeckLimits(harmony);
        Hook(harmony, typeof(GameUtils), "GetAllCollectibleCardIds", nameof(Pool));
        Hook(harmony, typeof(GameUtils), "GetCardTagValue", nameof(PreviewDbf), new[] { typeof(string), typeof(GAME_TAG) });
        Hook(harmony, typeof(GameUtils), "GetCardTagValue", nameof(PreviewDbfId), new[] { typeof(int), typeof(GAME_TAG) });
        foreach (Type tagType in new[] { typeof(int), typeof(GAME_TAG) })
        {
            var getTag = typeof(EntityBase).GetMethods().Single(m => m.Name == "GetTag" && !m.IsGenericMethod
                && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == tagType);
            harmony.Patch(getTag, prefix: new HarmonyMethod(typeof(NativeUIProbe), tagType == typeof(int) ? nameof(PreviewTagInt) : nameof(PreviewTag)));
        }
        Hook(harmony, typeof(EntityBase), "GetReferencedTag", nameof(PreviewReferencedTag), new[] { typeof(GAME_TAG) });
        Hook(harmony, typeof(EntityDef), "GetReferencedTag", nameof(PreviewReferencedTagInt), new[] { typeof(int) });
        Hook(harmony, typeof(EntityBase), "HasReferencedTag", nameof(PreviewHasReferencedTag), new[] { typeof(GAME_TAG) });
        Hook(harmony, typeof(GameUtils), "IsCardCollectible", nameof(Collectible), new[] { typeof(string) });
#if STANDARD_PROOF
        Hook(harmony, typeof(GameUtils), "IsCardGameplayEventActive", nameof(ProofCardPlayable), new[] { typeof(string) });
#else
        Hook(harmony, typeof(GameUtils), "IsCardGameplayEventActive", nameof(CorePlayable), new[] { typeof(string) });
        Hook(harmony, typeof(GameUtils), "IsCardGameplayEventActive", nameof(CorePlayableDef), new[] { typeof(EntityDef) });
#endif
        Hook(harmony, typeof(CollectionManager), "AddTrialCardCounts", nameof(SkipActive));
        Hook(harmony, typeof(CollectionManager), "AreAllDeckContentsReady", nameof(DecksReady));
        Hook(harmony, typeof(CollectionDeck), "CountCardsByStatus", nameof(LoanerOwnership), new[] { typeof(FormatType?), typeof(bool) });
        Hook(harmony, typeof(CollectionDeck), "get_IsValidForRuleset", nameof(LoanerRuleset));
        Hook(harmony, typeof(NetCache), "NetCacheMakeBatchRequest", nameof(Batch));
        Hook(harmony, typeof(NetCache), "RequestNetCacheObject", nameof(RequestCache));
        Hook(harmony, typeof(NetCache), "NetCacheReload_Internal", nameof(ReloadCache));
        Hook(harmony, typeof(NetCache), "GetTestData", nameof(UsePublishedBoosters), new[] { typeof(Type) });
        Hook(harmony, typeof(Network), "RequestNetCacheObjectList", nameof(BlockTransport));
        Hook(harmony, typeof(Network), "RequestNetCacheObject", nameof(BlockTransport));
        Hook(harmony, typeof(GameUtils), "IsFakePackOpeningEnabled", nameof(FakePack));
        Hook(harmony, typeof(GameUtils), "GetFakePackCount", nameof(FakeCount));
        Hook(harmony, typeof(UnopenedPack), "CanOpenPack", nameof(OpenLocalPack), new[] { typeof(string).MakeByRefType() });
        try
        {
            harmony.Patch(Required(typeof(PackOpening), "OpenBooster", new[] { typeof(UnopenedPack), typeof(int) }),
                prefix: new HarmonyMethod(typeof(NativeUIProbe), nameof(BeforeOpen)),
                postfix: new HarmonyMethod(typeof(NativeUIProbe), nameof(OpenedPackType)),
                transpiler: new HarmonyMethod(typeof(NativeUIProbe), nameof(PackCoroutineCall)));
            Hook(harmony, typeof(PackOpeningDirector), "FinishPackOpen", nameof(BeforeDone));
            Hook(harmony, typeof(PackOpeningDirector), "FinishMassPackOpen", nameof(BeforeMassDone));
            Hook(harmony, typeof(PackOpeningDirector), "MassPackOpeningContinuePressed", nameof(BeforeMassContinue));
            harmony.Patch(MassPageMoveNext(),
                transpiler: new HarmonyMethod(typeof(NativeUIProbe), nameof(MassPageCalls)));
            Hook(harmony, typeof(PackOpeningDirector), "MassPackOpeningRevealAllPressed", nameof(BeforeMassRevealAll));
            Hook(harmony, typeof(PackOpeningDirector), "MassPackOpeningRevealRandomCard", nameof(BeforeMassRevealRandom));
            Hook(harmony, typeof(PackOpeningDirector), "HandleMassPackOpeningSummaryEvent", nameof(BeforeMassSummaryEvent));
            harmony.Patch(Required(typeof(PackOpening), "IsMassPackOpenable"), postfix: new HarmonyMethod(typeof(NativeUIProbe), nameof(MassOpenableOnly)));
            harmony.Patch(Required(typeof(PackOpening), "OnDirectorFinished"),
                prefix: new HarmonyMethod(typeof(NativeUIProbe), nameof(BeforeReplayFinished)),
                postfix: new HarmonyMethod(typeof(NativeUIProbe), nameof(ReplayFinished)));
            VerifyPackHooks(harmony);
        }
        catch (Exception error)
        {
            log.LogError("NATIVE_PACK ERROR critical hook startup check failed: " + error);
            throw;
        }
        Hook(harmony, typeof(PackOpening), "NavigateToBoxAfterDisconnect", nameof(PackDisconnect));
        Hook(harmony, typeof(Box), "OnOpenPacksButtonPressed", nameof(HomePacks), new[] { typeof(UIEvent) });
        // Play, Collection, Store and modes all route through OnButtonPressed: none may act under reward boxes.
        Hook(harmony, typeof(Box), "OnButtonPressed", nameof(HomeButtonGate), new[] { typeof(UIEvent) });
        Hook(harmony, typeof(Box), "OnButtonMouseOver", nameof(HomeModeHover), new[] { typeof(UIEvent) });
        Hook(harmony, typeof(Box), "OnButtonMouseOut", nameof(HomeModeOut), new[] { typeof(UIEvent) });
        Hook(harmony, typeof(BoxScrollButton), "TriggerOver", nameof(HomeScrollHover));
        Hook(harmony, typeof(BoxScrollButton), "TriggerOut", nameof(HomeScrollOut));
        harmony.Patch(Required(typeof(StoreButton), "OnButtonOver", new[] { typeof(UIEvent) }),
            postfix: new HarmonyMethod(typeof(NativeUIProbe), nameof(HomeStoreTooltip)));
        harmony.Patch(Required(typeof(Box), "UpdateUIEvents"),
            postfix: new HarmonyMethod(typeof(NativeUIProbe), nameof(RefreshHomeButtons)));
        Hook(harmony, typeof(Hub), "Start", nameof(LocalHubStart));
        harmony.Patch(Required(typeof(CollectionManagerDisplay), "Exit"),
            transpiler: new HarmonyMethod(typeof(NativeUIProbe), nameof(CollectionExitOnlineCheck)));
        Hook(harmony, typeof(GameSaveDataManager), "Request", nameof(Preferences), new[] { typeof(List<GameSaveKeyId>), typeof(GameSaveDataManager.OnRequestDataResponseDelegate) });
        Hook(harmony, typeof(GameSaveDataManager), "SaveSubkeys", nameof(SavePreferences));
        Hook(harmony, typeof(CollectionManager), "SendCreateDeck", nameof(CreateLocalDraft));
        InitializeCosmeticTrayBinding(harmony);
        Hook(harmony, typeof(CollectionManager), "OnDeckDeletedWhileOffline", nameof(DeleteLocalDraft));
        Hook(harmony, typeof(CollectionDeckTray), "DeleteEditingDeck", nameof(PrepareEditingDeckDelete), new[] { typeof(bool) });
        Hook(harmony, typeof(DeckTrayDeckListContent), "DeleteDeck", nameof(DeleteLocalDraftBeforeNative));
        Hook(harmony, typeof(CollectionDeckBoxVisual), "OnDeleteButtonConfirmationResponse", nameof(ConfirmListDeckDelete));
        Hook(harmony, typeof(DeckOptionsMenu), "OnDeleteButtonConfirmationResponse", nameof(ConfirmEditingDeckDelete));
        Hook(harmony, typeof(CollectionDeckTray), "OnBackOutOfDeckContentsImpl", nameof(PrepareDiscardDeck));
        Hook(harmony, typeof(CollectionDeck), "SendChanges", nameof(SaveLocalDraft));
        harmony.Patch(Required(typeof(DeckTrayReorderableContent), "StopDragToReorder"),
            prefix: new HarmonyMethod(typeof(NativeUIProbe), nameof(ReorderLocalDrafts)),
            finalizer: new HarmonyMethod(typeof(NativeUIProbe), nameof(FinishLocalReorder)));
        foreach (string name in new[] { "AddPendingDeckRename", "AddPendingDeckEdit" })
            Hook(harmony, typeof(CollectionManager), name, nameof(ReadOnly));
        Hook(harmony, typeof(CollectionManagerDisplay), "CreateDeckFromClipboard", nameof(BlockDeckImport));
        Hook(harmony, typeof(CollectionInputMgr), "PasteDeckInEditModeFromShareableDeck", nameof(BlockDeckImport));
        harmony.Patch(Required(typeof(DeckTrayDeckListContent), "ShouldDeckBoxesBeLocked"),
            prefix: new HarmonyMethod(typeof(NativeUIProbe), nameof(UnlockLocalDeckBoxSlots)));
        harmony.Patch(Required(typeof(CollectionDeckBoxVisual), "SetDeckID"),
            postfix: new HarmonyMethod(typeof(NativeUIProbe), nameof(LockLoanerBox)));
        harmony.Patch(Required(typeof(CollectionDeckBoxVisual), "OnOver"),
            prefix: new HarmonyMethod(typeof(NativeUIProbe), nameof(ShowLoanerTooltip)));
        Hook(harmony, typeof(CollectionDeckBoxVisual), "ShowDeleteButton", nameof(HideLoanerDeleteButton), new[] { typeof(bool) });
        harmony.Patch(Required(typeof(DeckTrayDeckListContent), "OnDeckBoxVisualOver", new[] { typeof(CollectionDeckBoxVisual) }),
            postfix: new HarmonyMethod(typeof(NativeUIProbe), nameof(ShowStarterDeleteButton)));
        Hook(harmony, typeof(DialogManager), "ShowPopup", nameof(StarterDeleteConfirmText), new[] { typeof(AlertPopup.PopupInfo) });
        Hook(harmony, typeof(DeckOptionsMenu), "ShowConvertButton", nameof(HidePersonalDeckConvert), Type.EmptyTypes);
        Hook(harmony, typeof(CollectionManager), "ShouldShowWildToStandardTutorial", nameof(HidePersonalDeckTutorial), new[] { typeof(bool) });
        Hook(harmony, typeof(DeckHelper), "HasChoicesToOffer", nameof(HidePersonalDeckHelperChoices), new[] { typeof(CollectionDeck) });
        Hook(harmony, typeof(DeckTrayCardListContent), "ShowDeckHelper", nameof(BlockPersonalDeckHelper), new[] { typeof(CollectionDeckSlot), typeof(bool) });
        harmony.Patch(Required(typeof(CollectionDeckTileActor), "UpdateAllComponents", new[] { typeof(bool) }),
            postfix: new HarmonyMethod(typeof(NativeUIProbe), nameof(RestorePersonalDeckGhost)));
        Hook(harmony, typeof(CollectionDeckTray), "CompleteMyDeckButtonPress", nameof(BlockPersonalDeckCompletion), Type.EmptyTypes);
        foreach (string name in new[] { "DeleteDeck", "RenameDeck" }) Hook(harmony, typeof(Network), name, nameof(ReadOnly));
        NativeCrafting.Initialize(harmony, logger, Publish);
        NativeCollectionStatus.Initialize(harmony, logger);
        foreach (string name in new[] { "DeleteDeck", "RenameDeck" }) Hook(harmony, typeof(OfflineDataCache), name, nameof(ReadOnly));
        NativeRewardUI.Initialize(harmony, logger);
        NativeResultsUI.Initialize(harmony, logger);
        NativeAchievementToast.Initialize(harmony, logger);
        NativeTierRewards.Initialize(logger);
        NativePetMoment.Initialize(harmony, logger);
        NativeWelcomeUI.Initialize(logger);
        NativeFormatLock.Initialize(harmony, logger);
        InitializeRecipePicker(harmony);
    }

    internal static void BindStartupProfile(LocalProfile selectedProfile)
    {
        if (selectedProfile == null || !ReferenceEquals(selectedProfile, LocalLadder.Profile))
            throw new InvalidOperationException("Native and ladder profiles differ");
        if (active || attempted || ready) throw new InvalidOperationException("Native save bootstrap already started.");
#if STANDARD_PROOF
        CraftingLedgerRegistry.Current = null;
#else
        CraftingLedgerRegistry.Current = new LocalCraftingLedger(selectedProfile);
#endif
    }

    internal static void ResetStartupProfile()
    {
        if (active || attempted || ready) return;
        CraftingLedgerRegistry.Current = null;
    }

    private static MethodInfo Required(Type type, string name, Type[] args = null) =>
        (args == null ? AccessTools.Method(type, name) : AccessTools.Method(type, name, args)) ?? throw new MissingMethodException(type.FullName, name);
    private static void Hook(Harmony harmony, Type type, string name, string prefix, Type[] args = null) =>
        harmony.Patch(Required(type, name, args), prefix: new HarmonyMethod(typeof(NativeUIProbe), prefix));
    // Offline bootstrap keeps BnetBar hidden, deferring its child Awake and highlight initialization.
    private static bool HiddenMenuHighlight(BnetBarMenuButton __instance) =>
        !IsolatedLaunch || __instance.gameObject.activeInHierarchy;
    private static object Field(object obj, string name) => obj == null ? null : AccessTools.Field(obj.GetType(), name)?.GetValue(obj);
    private static string Tsv(string value) => (value ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    private static object Service(string name)
    {
        if (name == "TrialCardManager") return Blizzard.T5.Services.ServiceManager.Get<TrialCardManager>();
        Type type = GameTypes.ByName(name);
        return type == null ? null : AccessTools.Method(type, "Get", Type.EmptyTypes)?.Invoke(null, null);
    }
    private static void Invoke(object obj, string name, params object[] args) => Required(obj.GetType(), name).Invoke(obj, args);
    private static string Hash()
    {
        return Profile.SavedHash();
    }
    // The 3-second status line re-hashes only after the saved profile changed; proofs (baseline/pack hashes) always use Hash().
    private static readonly SavedHashCache statusHash = new SavedHashCache();
    private static string StatusHash()
    {
        string identity = Profile.SavedFileIdentity(out DateTime written);
        return statusHash.Get(identity, written, DateTime.UtcNow, Hash);
    }
    // Deep scans (FindObjects, renderer visibility, deck rules) are cached between ticks; see StatusRoutine.DeepScanDue.
    private static string[] deepLines;
    private static float deepScanAt;
    private static string deepScanScene;
    private static void State(string value) { phase = value; log.LogInfo("NATIVE_UI " + value); WriteStatus(true); }
    private static void Fail(Exception error)
    {
        if (failure.Length != 0) return;
        failure = error.ToString(); ready = false; sceneWaiting = false;
        log.LogError("NATIVE_UI_BLOCKED " + failure); State("Blocked");
        ScreenCapture.CaptureScreenshot(Path.Combine(Reports, "native-ui-blocked.png"));
    }
    public static void Update(bool bridgeReady)
    {
        try
        {
            if (Profile == null || !bridgeReady || failure.Length != 0) return;
            int count = callbacks.Count;
            for (int i = 0; i < count; i++) callbacks.Dequeue()();
            if (!active) { active = true; began = Time.realtimeSinceStartup; State("WaitingServices"); }
            if (!attempted)
            {
                var required = new[] { "NetCache", "LoginManager", "Options", "Box", "AssetLoader", "EventTimingManager", "TrialCardManager", "CardBackManager", "CosmeticCoinManager", "GameSaveDataManager" };
                missing = string.Join(",", required.Where(name => Service(name) == null));
                if (missing.Length != 0)
                {
                    if (Time.realtimeSinceStartup - began > 30) throw new InvalidOperationException("Missing native services: " + missing);
                    return;
                }
                attempted = true;
                Bootstrap();
            }
            if (!ready) return;
            if (AtHome()) NativeHomeUI.UpdateJournal(Box.Get(), message => log.LogInfo(message), NativeHiddenEntries.Active);
            if (sceneWaiting && SplashScreen.Get() == null && !SceneMgr.Get().IsTransitioning() && SceneMgr.Get().GetMode() == target)
            {
                sceneWaiting = false; State("SceneLoaded:" + target);
            }
            if (sceneWaiting && Time.realtimeSinceStartup - sceneBegan > 60)
                throw new TimeoutException("Native scene did not finish: " + target);
            NativeResultsUI.Update();
            NativeAchievementToast.Update();
            NativeAchievements.Update();
            NativeTierRewards.Update();
            NativeWelcomeUI.Update(Profile, WelcomeContextReady, WelcomeScrollHubReady);
            NativeCollectionStatus.Update();
            NativeFormatLock.Update();
            NativeHelpUI.Update(AtHome());
            RoutePendingRewardHome();
            RouteFinishedMatchToPicker();
            BindPicker();
            NativeRematchUI.Update(pickerTray);
            NativeDeckPickerGuide.Update(pickerTray);
            NativeDeckRecord.Update(pickerTray);
            NativeRewardUI.Update();
            // Flip All only between the durable commit and the Done acknowledgement of that exact director.
            NativePackFlip.Update(replayRunning && !doneAcknowledged && receiptPacks == 1 ? revealDirector : null);
            RetireStaleDeleteNotice();
            if (replayArmed && !replayRunning && SceneMgr.Get().GetMode() == SceneMgr.Mode.PACKOPENING)
            {
                var pack = UnityEngine.Object.FindFirstObjectByType<PackOpening>();
                if (pack != null && pack != armedPack)
                {
                    var list = Field(pack, "m_fakeBoosterCards") as IList;
                    if (list == null) throw new InvalidOperationException("Pack fake-card list missing");
                    list.Clear();
                    if (Profile.PackRevealPending)
                    {
                        RememberPendingReceipt();
                        foreach (var card in Profile.PendingPacks.SelectMany(pending => pending.Cards)) pack.AddFakeBoosterCard(card.Id, (TAG_PREMIUM)card.Premium);
                    }
                    else { receipt = null; receiptSequence = 0; receiptPacks = 0; }
                    armedPack = pack;
                    State(receipt == null ? "PackArmed:ready for transaction" : "PackArmed:pending receipt ready");
                }
            }
            if (replayRunning && Time.realtimeSinceStartup - replayBegan > 30)
            {
                var director = Field(armedPack, "m_director") as PackOpeningDirector;
                // Mass highlights show five cards at a time; prove the batch was fed as well as the director surviving.
                if (director == null || (!director.IsMassPackOpening()
                    && UnityEngine.Object.FindObjectsByType<PackOpeningCard>(FindObjectsSortMode.None).Length < 5))
                    throw new TimeoutException("Replay director/five native card actors missing");
                if (receiptPacks >= 2 && !massBoosterFed) throw new TimeoutException("Native mass pack receipt was not fed within 30 seconds");
            }
        }
        catch (Exception error) { Fail(error); }
    }

    private static void RoutePendingRewardHome()
    {
        if (!SceneMgr.IsInitialized()) return;
        bool stable = active && ready && !sceneWaiting && !SceneMgr.Get().IsTransitioning();
        if (LocalRewardPresentation.ShouldRouteResultHome(stable,
            Profile?.PendingRewardPresentation != null, LocalLadder.Screen,
            SceneMgr.Get().GetMode() == SceneMgr.Mode.GAMEPLAY, NativeResultsUI.RoutingReleased,
            NativeResultsUI.GameplayPresentation))
            Navigate(SceneMgr.Mode.HUB);
    }

    private static void RouteFinishedMatchToPicker()
    {
        if (!SceneMgr.IsInitialized()) return;
        bool stable = active && ready && !sceneWaiting && !SceneMgr.Get().IsTransitioning() && !NativeRewardUI.Active;
        if (LocalRewardPresentation.ShouldRouteFinishedToPicker(stable, Profile?.PendingRewardPresentation != null,
            Profile?.Pending != null, LocalLadder.Screen, SceneMgr.Get().GetMode() == SceneMgr.Mode.GAMEPLAY))
            Navigate(SceneMgr.Mode.TOURNAMENT);
    }

    // The picker opens on the deck just played, also after a restart (lastSelectedId is session-only).
    internal static void RememberPlayedDeck(string deck)
    {
        long id = NativeUIProjection.PlayedDeckId(deck);
        if (id != 0 && decks.ContainsKey(id)) lastSelectedId = id;
    }

    private static void Bootstrap()
    {
        State("Projecting"); baselineHash = Hash();
        // Native Options reads these dimensions; Unity restores the display separately.
        // Test lanes skip it: the options file is shared with the owner's full-screen lane 0 and a windowed lane must not leave its size there.
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-hs-native-ui-lane") < 0)
        {
            Options.Get().SetInt(Option.GFX_WIDTH, Screen.width);
            Options.Get().SetInt(Option.GFX_HEIGHT, Screen.height);
        }
        // Native totals span every version; per-version rows go into the collection packet below.
        var own = NativeUIProjection.TotalOwned(Profile);
        decks.Clear();
        foreach (LocalCustomDeck custom in Profile.CustomDecks)
        {
            var localCustom = LocalProfile.LedgerDeck(custom.Name, custom.Hero, custom.Cards, custom.Sideboards, false);
            decks.Add(custom.Id, localCustom);
        }
        if (Profile.PackRevealPending) RememberPendingReceipt();
        // Bootstrap is the only full native deck-list rebuild. Freeze this loaner subset until
        // the next bootstrap; editor callbacks must not add/remove loaners behind native listeners.
        // Native New Deck may fill the reserved slot and then stays hidden until the next bootstrap.
        string testLoanerGroup = null;
#if ENGINE_SOURCE && (!SOLOSTONE_INSTALLER || SOLOSTONE_TEST_HOOKS)
        // A normal save never projects test loaners, whatever the selector file says (owner 2026-10-30).
        if (NativeUIProjection.AllowsTestLoaners(Profile))
            testLoanerGroup = NativeUIProjection.LoadTestLoanerGroup(
                NativeUIProjection.TestLoanerGroupPath, message => log.LogWarning(message));
        else if (System.IO.File.Exists(NativeUIProjection.TestLoanerGroupPath)
            && NativeUIProjection.LoadTestLoanerGroup(NativeUIProjection.TestLoanerGroupPath, message => { }) != "11")
            log.LogWarning("TEST_LOANER_GROUP ignored: test decks need a Sandbox save.");
#endif
        foreach (string key in NativeUIProjection.VisibleLoanerKeys(NativeUIProjection.ProjectedLoanerKeys(decks.Count, testLoanerGroup), Profile.HiddenLoaners))
            decks.Add(NativeUIProjection.DeckId(key), LocalDecks.Get(key));
#if !SOLOSTONE_RELEASE
        AuditCards();
#endif
        // Native packet translation supplies every denylist; local rules add no extra bans.
        var features = new NetCache.NetCacheFeatures {
            ConstructedCardDenylist = new List<int>(), SideboardCardDenylist = new List<int>(),
            StandardCardDenylist = new List<int>(), WildCardDenylist = new List<int>(),
            DuelsCardDenylist = new List<int>(), TavernBrawlCardDenylist = new List<int>(), ArenaCardDenylist = new List<int>(),
            VFXDenylist = new List<string>(), PetSkinsDenylist = new List<int>(), PetEventDenylist = new List<int>(),
            PetSkinsHSEnabled = true,
            // Retail inferred 2 (medium confidence, docs/design/pet-xp.md). NativeMatch answers packets 68/67 and enforces the cap.
            MaxPetTreatsAllowedPerGame = NativeMatch.MaxPetTreatsPerGame
        };
        // Standard ranked and Standard Casual are playable (GameUtils.IsFormatPlayable); Wild stays visible but disabled.
        features.Games.Tournament = true; features.Games.Casual = true; features.Games.RankedStandard = true; features.Games.RankedWild = false;
        features.Collection.Manager = true; features.Collection.Crafting = CraftingLedgerRegistry.Current != null;
        // Upgrades consume Normal copies through the same atomic crafting ledger.
        features.EnableUpgradeToGolden = CraftingLedgerRegistry.Current != null;
        features.Collection.DeckReordering = true;
        features.Collection.MultipleFavoriteCardBacks = true;
        features.Collection.CosmeticsRenderingEnabled = false; // Retail default: BG board/finisher render previews only; hero/card-back previews do not use it.
        // Retail mass pack opening (owner 2026-10-29): drag a pack off a stack of 5+ and hold it there to pick packs up in
        // chunks of 5, up to 40 (retail values); one batch commit per opening (BeforeOpen). Hold-Space continuous opening
        // stays off: keyboard-only, and it re-opens packs behind the armed-pack guard.
        features.MassPackOpeningEnabled = true; features.MassPackOpeningPackLimit = LocalProfile.MassPackLimit;
        features.MassPackOpeningGoldenPackLimit = LocalProfile.MassPackLimit; features.MassPackOpeningHooverChunkSize = LocalProfile.MassPackChunk;
        features.MassCatchupPackOpeningEnabled = false; features.ContinuousQuickOpenEnabled = false;
        Publish(features);
        Publish(new NetCache.NetCacheClientOptions()); Publish(new NetCache.NetCacheProfileNotices());
        Publish(new NetCache.NetCacheArcaneDustBalance { Balance = Profile.DustBalance }); Publish(new NetCache.NetCachePlayerRecords());
        Publish(new NetCache.NetCacheProfileProgress { TutorialComplete = true });
        Publish(ProfileHeroLevels());
        if (!CardBackManager.Get().GetCardBackIds().Contains(0)) throw new InvalidOperationException("Native default card back 0 unavailable");
        Publish(CosmeticCardBacks());
        Publish(CosmeticCoins()); Publish(CosmeticFavorites()); Publish(CosmeticPets());
        PublishBoosters();
        PublishMedal();
        var headers = new NetCache.NetCacheDecks();
        var contents = new List<PegasusUtil.DeckContents>();
        foreach (var pair in decks)
        {
            var deck = pair.Value;
            LocalCustomDeck custom = Profile.GetDraft(pair.Key);
            headers.Decks.Add(ExpectedHeader(pair.Key));
            var content = new PegasusUtil.DeckContents { DeckId = pair.Key, Success = true };
            // Saved drafts keep their version per copy; loaners and recipes are Normal.
            var entries = custom != null ? custom.CardEntries : deck.CardIds.Select(id => new LocalDeckCard { Id = id }).ToList();
            foreach (var row in NativeUIProjection.DeckRows(entries)) content.Cards.Add(new DeckCardData {
                Def = new PegasusShared.CardDef { Asset = GameUtils.TranslateCardIdToDbId(row.Id), Premium = row.Premium }, Qty = row.Qty });
            contents.Add(content);
        }
        Publish(headers);
        EnsurePreference(GameSaveKeyId.COLLECTION_MANAGER); EnsurePreference(GameSaveKeyId.PLAYER_FLAGS);
        SaveLocal(new GameSaveDataManager.SubkeySaveRequest(GameSaveKeyId.PLAYER_FLAGS, GameSaveKeySubkeyId.PLAYER_FLAGS_UNLOCKED_WILD, 1L));
        SaveLocal(new GameSaveDataManager.SubkeySaveRequest(GameSaveKeyId.PLAYER_FLAGS, GameSaveKeySubkeyId.PLAYER_FLAGS_HAS_SEEN_MASS_PACK_OPENING_TOOLTIP, Profile.SeenMassPackTooltip ? 1L : 0L));
        SaveLocal(new GameSaveDataManager.SubkeySaveRequest(GameSaveKeyId.PLAYER_FLAGS, GameSaveKeySubkeyId.PLAYER_FLAGS_HAS_COMPLETED_APPRENTICE, 1L));
        Options.SetFormatType(LocalDeckFormat);
        NativeUIProjection.CasualPlayMode = Profile.PrefersCasual;
        Options.SetInRankedPlayMode(!NativeUIProjection.CasualPlayMode);
        if (CollectionManager.Get() != null) throw new InvalidOperationException("CollectionManager was initialized before probe");
        CollectionManager.Init();
        var packet = CollectionPacket(NativeUIProjection.CollectionStacks(Profile), Profile.SeenCopies);
        Publish(CollectionManager.Get().OnInitialCollectionReceived(packet));
        if (!CollectionManager.Get().IsFullyLoaded()) Invoke(CollectionManager.Get(), "OnAchievesLoaded");
        if (!CollectionManager.Get().IsFullyLoaded()) throw new InvalidOperationException("Collection real ready callbacks did not complete");
        AddSupportedDisplayableSets(CollectionManager.Get(), own);
        CollectionManager.Get().OnInitialClientStateDeckContents(headers, contents);
        foreach (var pair in decks)
        {
            var native = CollectionManager.Get().GetDeck(pair.Key);
            if (native == null) throw new InvalidOperationException("Missing projected deck " + pair.Key);
            if (Profile.GetDraft(pair.Key) == null) native.TemplateSource = Assets.DeckTemplate.SourceType.NONE;
            ProjectCraftingRecipe(native, CollectionManager.Get().GetBaseDeck(pair.Key), CraftingRecipe(pair.Key));
            ValidateNative(native);
        }
        foreach (string id in NativeUIProjection.Supported)
        {
            int expected = own.TryGetValue(id, out int n) ? n : 0;
            if (CollectionManager.Get().GetTotalOwnedCount(id) != expected) throw new InvalidOperationException("Native ownership mismatch: " + id);
        }
        // Crafting synchronizes per version: totals alone would hide a Normal/premium swap.
        foreach (var stack in Profile.OwnedCards.Where(card => card.Premium > 0))
            if (CollectionManager.Get().GetOwnedCount(stack.Id, (TAG_PREMIUM)stack.Premium) != Profile.OwnedCount(stack.Id, stack.Premium))
                throw new InvalidOperationException("Native premium ownership mismatch: " + stack.Id + "/" + stack.Premium);
        Tournament.Init();
        if (Hash() != baselineHash) throw new InvalidOperationException("Profile changed during projection");
        ready = true; State("Ready:reward startup intentionally omitted; local deck editor enabled");
        Navigate(SceneMgr.Mode.HUB);
    }

    private static bool PreviewStatsContext
    {
        get
        {
            if (auditingEffective && !auditingOriginal) return true;
            if (!active || auditingOriginal || !SceneMgr.IsInitialized() || LocalLadder.Screen == "Starting" || LocalLadder.Screen == "Playing") return false;
            var mode = SceneMgr.Get().GetMode();
            return mode == SceneMgr.Mode.LOGIN || mode == SceneMgr.Mode.COLLECTIONMANAGER
                || mode == SceneMgr.Mode.PACKOPENING || mode == SceneMgr.Mode.TOURNAMENT;
        }
    }
    internal static bool PreviewDefinitionContext
    {
        get
        {
            if (auditingEffective && !auditingOriginal) return true;
            if (!active || auditingOriginal || !SceneMgr.IsInitialized()) return false;
            return NativeUIProjection.DefinitionContext(true, false, true, PreviewStatsContext, LocalLadder.Screen);
        }
    }
#if !STANDARD_PROOF
    private static bool CorePlayable(string __0, ref bool __result)
    {
        if (!NativeUIProjection.ForcesCorePlayability(active, __0)
            && !NativeUIProjection.ForcesTestPlayability(active, __0)) return true;
        __result = true; return false;
    }
    private static bool CorePlayableDef(EntityDef __0, ref bool __result) => CorePlayable(__0?.GetCardId(), ref __result);
#endif
#if STANDARD_PROOF
    private static bool ProofCardPlayable(string __0, ref bool __result)
    {
        // Offline event timing is unavailable; only the fully audited frozen pair may bypass it.
        if (!StandardProofNative.AllowNativePlayability(active, IsolatedLaunch, auditingOriginal,
            proofAuditPassed, failure.Length != 0, __0)) return true;
        __result = true; return false;
    }
#endif
    private static bool PreviewDbf(string __0, GAME_TAG __1, ref int __result)
    {
#if STANDARD_PROOF
        if (NativeUIProjection.UseOriginalProofDefinition(__0)) return true;
#endif
        if (!NativeUIProjection.TryGetDbfTag(PreviewStatsContext, __0, (int)__1, out int value)) return true;
        __result = value; return false;
    }
    private static bool PreviewDbfId(int __0, GAME_TAG __1, ref int __result)
    {
        if (!PreviewStatsContext) return true;
        return PreviewDbf(GameUtils.TranslateDbIdToCardId(__0), __1, ref __result);
    }
    private static bool PreviewReferencedTag(EntityBase __instance, GAME_TAG __0, ref int __result)
        => PreviewReferencedTagInt(__instance, (int)__0, ref __result);
    private static bool PreviewReferencedTagInt(EntityBase __instance, int __0, ref int __result)
    {
        if (!PreviewDefinitionContext || !(__instance is EntityDef definition) || !IsCanonicalDefinition(definition)
            || !NativeUIProjection.TryGetReferencedTag(true, definition.GetCardId(), __0, out int value)) return true;
        __result = value; return false;
    }
    private static bool PreviewHasReferencedTag(EntityBase __instance, GAME_TAG __0, ref bool __result)
    {
        int value = 0;
        if (PreviewReferencedTagInt(__instance, (int)__0, ref value)) return true;
        __result = value != 0; return false;
    }
    private static bool PreviewTag(EntityBase __instance, GAME_TAG __0, ref int __result) => PreviewTagInt(__instance, (int)__0, ref __result);
    private static bool PreviewTagInt(EntityBase __instance, int __0, ref int __result)
    {
#if STANDARD_PROOF
        if (__instance is EntityDef proofDefinition && NativeUIProjection.UseOriginalProofDefinition(proofDefinition.GetCardId())) return true;
#endif
        // Printed preview definitions only; live gameplay Entity tags always retain native behavior.
        if (!PreviewDefinitionContext || !(__instance is EntityDef definition) || !IsCanonicalDefinition(definition)
            || !NativeUIProjection.TryGetDefinitionTag(true, true, true, definition.GetCardId(), __0, out int value)) return true;
        __result = value; return false;
    }
    private static bool IsCanonicalDefinition(EntityDef definition)
    {
        string id = definition?.GetCardId();
        if (!NativeUIProjection.SupportsPreview(id)) return false;
        var loader = DefLoader.Get();
        return loader != null && loader.HasLoadedEntityDefs()
            && ReferenceEquals(definition, loader.GetEntityDef(id));
    }

#if SOLOSTONE_RELEASE
    internal static void AuditBeforeSave()
    {
        auditingEffective = true;
        try { AuditCards(); }
        finally { auditingEffective = false; }
    }
#endif
    private static void AuditCards()
    {
#if STANDARD_PROOF
        proofAuditPassed = false;
        var proofAudit = new List<string> { "#snapshot="+StandardProofNative.Snapshot+" runId="+runId+" matchId="+LocalLadder.MatchId+" utc="+DateTime.UtcNow.ToString("o") };
        auditingOriginal = true;
        try
        {
#if MODERN_MECHANICS
            string originalAssemblySha256;
            using (var stream = File.OpenRead(typeof(EntityDef).Assembly.Location))
            using (var hash = System.Security.Cryptography.SHA256.Create())
                originalAssemblySha256 = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
#endif
            StandardProofNative.AuditDefinitions(id => {
                int dbid = GameUtils.TranslateCardIdToDbId(id);
                if (dbid == 0 || GameUtils.TranslateDbIdToCardId(dbid) != id || GameDbf.Card.GetRecord(dbid) == null
                    || DefLoader.Get().GetEntityDef(id) == null)
                    throw new InvalidOperationException("Proof native definition identity missing: " + id);
                return dbid;
            }, id => (int)DefLoader.Get().GetEntityDef(id).GetCardSet(), dbid => {
                var manager = EventTimingManager.Get();
                return GameUtils.GetCardSetTimingsForCard(dbid).Select(timing => new StandardProofNative.CardSetTiming {
                    RowId=timing.ID, CardId=timing.CardId, CardSetId=timing.CardSetId, EventType=(int)timing.EventTimingEvent,
                    NativeSetExists=GameDbf.CardSet.GetRecord(timing.CardSetId) != null,
                    EventsReceived=manager == null ? (bool?)null : manager.HasReceivedEventTimingsFromServer,
                    EventKnown=manager == null ? (bool?)null : manager.AllKnownEvents.Contains(timing.EventTimingEvent),
                    EventActive=manager == null ? (bool?)null : manager.IsEventActive(timing.EventTimingEvent)
                }).ToArray();
            }, (id, tag) => DefLoader.Get().GetEntityDef(id).GetTag((GAME_TAG)tag),
                (id, tag) => DefLoader.Get().GetEntityDef(id).GetReferencedTag((GAME_TAG)tag), proofAudit.Add
#if MODERN_MECHANICS
                , originalAssemblySha256, id => {
                    var definition = DefLoader.Get().GetEntityDef(id);
                    if (!GameUtils.TryGetCardTagRecords(id,out var tags) || tags == null || tags.Any(tag => tag == null))
                        throw new InvalidOperationException("Unreadable original Dormant DBF diagnostics: "+id);
                    var raw = tags.Where(tag => tag.TagId == 1518).Select(tag => "cardId="+tag.CardId+",tagId="+tag.TagId+",value="+tag.TagValue
                        +",reference="+tag.IsReferenceTag+",powerKeyword="+tag.IsPowerKeywordTag).ToArray();
                    return "directPresent="+definition.GetTags().GetMap().ContainsKey(1518)
                        +";referenced1518="+definition.GetReferencedTag((GAME_TAG)1518)
                        +";dbf1518Rows="+(raw.Length == 0 ? "none" : string.Join("|",raw));
                }
#endif
                );
        }
        finally
        {
            auditingOriginal = false;
            File.WriteAllLines(Path.Combine(Reports, "standard-proof-original-definition-audit.tsv"), proofAudit);
        }
#endif
        File.WriteAllLines(Path.Combine(Reports, "card-catalog-policy.txt"), new[] {
            "catalog.count=" + LocalCardCatalog.Current.Count,
            "catalog.sha256=" + LocalCardCatalog.CurrentCatalogHash,
            "pool.version=" + LocalCardCatalog.CurrentPoolVersion,
            "pool.count=" + LocalCardCatalog.PackPool(LocalCardCatalog.CurrentPoolVersion).Count,
            "pool.sha256=" + LocalCardCatalog.CurrentPoolHash,
            "research.manifest.sha256=" + LocalCardCatalog.ResearchManifestHash,
            "supplemental.sha256=" + LocalCardCatalog.SupplementalGeneratedHash,
            "admitted=" + string.Join(",", LocalCardCatalog.Current.Where(card => LocalCardCatalog.LegacyV4.All(old => old.Id != card.Id)).Select(card => card.Id)),
            "quarantined=" + string.Join(";", LocalCardCatalog.QuarantinedIds.Select(id => id + "=" + LocalCardCatalog.QuarantineReasons[id]))
        });
        var audit = new List<string> { "id\tdbid\tcollectible\toriginalCost\toriginalAttack\toriginalHealth\toriginalDefCost\toriginalDefAttack\toriginalDefHealth\teffectiveCost\teffectiveAttack\teffectiveHealth\teffectiveDefCost\teffectiveDefAttack\teffectiveDefHealth\tengineCost\tengineAttack\tengineHealthRaw\tengineDurability\texpectedPrintedHealth\tengineName\tnativeRawName\tnativeEffectiveName" };
        var tagAudit = new List<string> { "id\tsemantic\tnativeTag\tengineTag\tscope\toriginalGameUtils\teffectiveGameUtils\toriginalEntityDef\teffectiveEntityDef\tengineDirect\tengineDbfExpected\tfatalDbfMismatch\tfatalDefinitionMismatch" };
        var referencedAudit = new List<string> { "id\tsemantic\tnativeTag\tengineTag\toriginalEntityDef\teffectiveEntityDef\tengineReferenced\tfatalMismatch" };
        var metadataAudit = new List<string> { "id\tsemantic\tnativeTag\tengineTagId\toriginalEntityDef\teffectiveEntityDef\tengine\tmismatch\tdecision" };
        var mismatches = new List<string>();
        var drift = new NativeCardDrift();
        Action<string, string, int, int> noteDrift = drift.Add;
        int skipped = 0;
        var poolIds = new HashSet<string>(LocalCardCatalog.ActivePoolIds(), StringComparer.Ordinal);
        bool auditCompleted = false;
        var auditedIds = NativeUIProjection.Supported.Concat(LocalDecks.TestStandardCardIds).Concat(LocalCardCatalog.SupplementalGeneratedIds)
            .Concat(LocalCardCatalog.LegacyGeneratedPublicIds).Concat(Heroes)
            .Distinct(StringComparer.Ordinal).OrderBy(id => id).ToArray();
        var directMappings = NativeAuditMappings();
        var referencedMappings = NativeReferencedMappings();
        var metadataMappings = NativeMetadataMappings();
        if (!directMappings.Select(mapping => mapping.Name).SequenceEqual(LocalCardCatalog.NativeAuditTags)
            || !referencedMappings.Select(mapping => mapping.Name).SequenceEqual(LocalCardCatalog.NativeAuditReferencedTags)
            || !metadataMappings.Select(mapping => mapping.Name).SequenceEqual(LocalCardCatalog.NativeAuditMetadataTags))
            throw new InvalidOperationException("Native card audit mapping policy drift.");
        try
        {
            foreach (string id in auditedIds)
            {
#if SOLOSTONE_RELEASE
                if (GameDbf.GetIndex().GetCardRecord(id) == null)
                {
                    if (poolIds.Contains(id)) mismatches.Add(id + ":missing");
                    else skipped++;
                    continue;
                }
#endif
                int dbid = GameUtils.TranslateCardIdToDbId(id);
                var definition = DefLoader.Get().GetEntityDef(id);
                if (dbid == 0 || GameUtils.TranslateDbIdToCardId(dbid) != id || GameDbf.Card.GetRecord(dbid) == null || definition == null)
                    throw new InvalidOperationException("Native DBID/EntityDef missing: " + id);
                var card = SabberStoneCore.Model.Cards.FromId(id);
                int[] original, originalDef;
                auditingOriginal = true;
                try
                {
                    original = new[] { GameUtils.GetCardTagValue(id, GAME_TAG.COST), GameUtils.GetCardTagValue(id, GAME_TAG.ATK), GameUtils.GetCardTagValue(id, GAME_TAG.HEALTH) };
                    originalDef = new[] { definition.GetCost(), definition.GetATK(), definition.GetHealth() };
                }
                finally { auditingOriginal = false; }
                int[] effective = { GameUtils.GetCardTagValue(id, GAME_TAG.COST), GameUtils.GetCardTagValue(id, GAME_TAG.ATK), GameUtils.GetCardTagValue(id, GAME_TAG.HEALTH) };
                int[] effectiveDef = { definition.GetCost(), definition.GetATK(), definition.GetHealth() };
                int durability = card[EngineGameTag.DURABILITY];
                int expectedHealth = card.Type == EngineCardType.WEAPON ? durability : card.Health;
                int[] expected = { card.Cost, card.ATK, expectedHealth };
                if (!Heroes.Contains(id))
                    for (int i = 0; i < expected.Length; i++) noteDrift(id, new[] { "COST", "ATK", "HEALTH" }[i], original[i], expected[i]);
                audit.Add(id + "\t" + dbid + "\t" + GameUtils.GetCardTagValue(id, GAME_TAG.COLLECTIBLE) + "\t"
                    + string.Join("\t", original.Concat(originalDef).Concat(effective).Concat(effectiveDef)) + "\t"
                    + string.Join("\t", card.Cost, card.ATK, card.Health, durability, expectedHealth) + "\t"
                    + Tsv(card.Name) + "\t" + Tsv(CardTextBuilder.GetRawCardName(definition))
                    + "\t" + Tsv(CardTextBuilder.GetDefaultCardName(definition)));
                if (!Heroes.Contains(id) && (!effective.SequenceEqual(expected) || !effectiveDef.SequenceEqual(expected))) mismatches.Add(id);
                foreach (var mapping in directMappings)
                {
                    if ((mapping.Scope == NativeAuditScope.WeaponOnly && card.Type != EngineCardType.WEAPON)
                        || (mapping.Scope == NativeAuditScope.NonWeapon && card.Type == EngineCardType.WEAPON)) continue;
                    int expectedDirect = card[mapping.EngineTag];
                    if (!mapping.NativeTag.HasValue)
                    {
                        tagAudit.Add(id + "\t" + mapping.Name + "\tunavailable\t" + mapping.EngineTag + "\t" + mapping.Scope
                            + "\tunavailable\tunavailable\tunavailable\tunavailable\t" + expectedDirect + "\t"
                            + expectedDirect + "\tFalse\tFalse");
                        continue;
                    }
                    GAME_TAG nativeTag = mapping.NativeTag.Value;
                    int originalTag, originalDefinitionTag;
                    auditingOriginal = true;
                    try
                    {
                        originalTag = GameUtils.GetCardTagValue(id, nativeTag);
                        originalDefinitionTag = definition.GetTag(nativeTag);
                    }
                    finally { auditingOriginal = false; }
                    int effectiveTag = GameUtils.GetCardTagValue(id, nativeTag);
                    int effectiveDefinitionTag = definition.GetTag(nativeTag);
                    int expectedDbf = expectedDirect;
                    if (!Heroes.Contains(id) && NativeUIProjection.TryGetDbfTag(true, id, (int)nativeTag, out int projectedDbf))
                        expectedDbf = projectedDbf;
                    if (!Heroes.Contains(id)) noteDrift(id, mapping.Name, originalDefinitionTag, expectedDirect);
                    bool dbfMismatch = effectiveTag != expectedDbf;
                    bool definitionMismatch = effectiveDefinitionTag != expectedDirect;
                    tagAudit.Add(id + "\t" + mapping.Name + "\t" + nativeTag + "\t" + mapping.EngineTag + "\t" + mapping.Scope
                        + "\t" + originalTag + "\t" + effectiveTag + "\t"
                        + originalDefinitionTag + "\t" + effectiveDefinitionTag + "\t" + expectedDirect + "\t" + expectedDbf
                        + "\t" + (mapping.Fatal && dbfMismatch) + "\t" + (mapping.Fatal && definitionMismatch));
                    if (!Heroes.Contains(id) && mapping.Fatal && (dbfMismatch || definitionMismatch))
                        mismatches.Add(id + ":" + mapping.Name);
                }
                foreach (var mapping in referencedMappings)
                {
                    int originalReferenced;
                    auditingOriginal = true;
                    try { originalReferenced = definition.GetReferencedTag(mapping.NativeTag.Value); }
                    finally { auditingOriginal = false; }
                    int effectiveReferenced = definition.GetReferencedTag(mapping.NativeTag.Value);
                    int expectedReferenced = card.RefTags.TryGetValue(mapping.EngineTag, out int referencedValue) ? referencedValue : 0;
                    if (!Heroes.Contains(id)) noteDrift(id, "referenced:" + mapping.Name, originalReferenced, expectedReferenced);
                    bool mismatch = effectiveReferenced != expectedReferenced;
                    referencedAudit.Add(id + "\t" + mapping.Name + "\t" + mapping.NativeTag.Value + "\t" + mapping.EngineTag
                        + "\t" + originalReferenced + "\t" + effectiveReferenced
                        + "\t" + expectedReferenced + "\t" + mismatch);
                    if (!Heroes.Contains(id) && mismatch) mismatches.Add(id + ":referenced:" + mapping.Name);
                }
                foreach (var mapping in metadataMappings)
                {
                    string originalMetadata = "unavailable", effectiveMetadata = "unavailable";
                    if (mapping.NativeTag.HasValue)
                    {
                        auditingOriginal = true;
                        try { originalMetadata = definition.GetTag(mapping.NativeTag.Value).ToString(); }
                        finally { auditingOriginal = false; }
                        effectiveMetadata = definition.GetTag(mapping.NativeTag.Value).ToString();
                    }
                    int expectedMetadata = card.Tags.FirstOrDefault(pair => (int)pair.Key == mapping.EngineTag).Value;
                    if (!Heroes.Contains(id) && mapping.NativeTag.HasValue && int.TryParse(originalMetadata, out int originalMetadataValue))
                        noteDrift(id, "metadata:" + mapping.Name, originalMetadataValue, expectedMetadata);
                    bool mismatch = mapping.NativeTag.HasValue && effectiveMetadata != expectedMetadata.ToString();
                    metadataAudit.Add(id + "\t" + mapping.Name + "\t" + (mapping.NativeTag.HasValue ? mapping.NativeTag.Value.ToString() : "unavailable")
                        + "\t" + mapping.EngineTag + "\t" + originalMetadata + "\t" + effectiveMetadata + "\t"
                        + expectedMetadata + "\t" + mismatch + "\t" + NativeMetadataDecision(mapping.Name));
                }
            }
            audit.Add("# skipped=" + skipped);
            auditCompleted = true;
        }
        finally
        {
            try
            {
                File.WriteAllLines(Path.Combine(Reports, "native-card-audit.tsv"), audit);
                File.WriteAllLines(Path.Combine(Reports, "native-card-tag-audit.tsv"), tagAudit);
                File.WriteAllLines(Path.Combine(Reports, "native-card-referenced-tag-audit.tsv"), referencedAudit);
                File.WriteAllLines(Path.Combine(Reports, "native-card-metadata-audit.tsv"), metadataAudit);
            }
            catch when (!auditCompleted || mismatches.Count != 0) { }
            drift.Write(Path.Combine(Reports, "native-card-drift.tsv"));
            log.LogInfo("NATIVE_CARD_DRIFT cards=" + drift.Cards + " fields=" + drift.Fields);
            log.LogInfo("NATIVE_CARD_AUDIT skipped=" + skipped);
        }
        if (mismatches.Count != 0) throw new NativeCardMismatchException("Effective native card tag mismatch: " + string.Join(",", mismatches.Take(20)));
#if STANDARD_PROOF
        proofAuditPassed = true;
#endif
    }

    private static NativeAuditMapping[] NativeAuditMappings() => new[] {
        new NativeAuditMapping("COLLECTIBLE", GAME_TAG.COLLECTIBLE, EngineGameTag.COLLECTIBLE),
        new NativeAuditMapping("CARD_SET", GAME_TAG.CARD_SET, EngineGameTag.CARD_SET, fatal: false),
        new NativeAuditMapping("CLASS", GAME_TAG.CLASS, EngineGameTag.CLASS),
        new NativeAuditMapping("CARDTYPE", GAME_TAG.CARDTYPE, EngineGameTag.CARDTYPE),
        new NativeAuditMapping("RARITY", GAME_TAG.RARITY, EngineGameTag.RARITY),
        new NativeAuditMapping("CARDRACE", GAME_TAG.CARDRACE, EngineGameTag.CARDRACE),
        new NativeAuditMapping("ELITE", GAME_TAG.ELITE, EngineGameTag.ELITE),
        new NativeAuditMapping("FACTION", GAME_TAG.FACTION, EngineGameTag.FACTION, fatal: false),
        new NativeAuditMapping("COST", GAME_TAG.COST, EngineGameTag.COST),
        new NativeAuditMapping("ATK", GAME_TAG.ATK, EngineGameTag.ATK),
        new NativeAuditMapping("HEALTH", GAME_TAG.HEALTH, EngineGameTag.HEALTH, NativeAuditScope.NonWeapon),
        new NativeAuditMapping("DAMAGE", GAME_TAG.DAMAGE, EngineGameTag.DAMAGE, NativeAuditScope.NonWeapon),
        new NativeAuditMapping("DURABILITY", GAME_TAG.HEALTH, EngineGameTag.DURABILITY, NativeAuditScope.WeaponOnly),
        new NativeAuditMapping("SPELLPOWER", GAME_TAG.SPELLPOWER, EngineGameTag.SPELLPOWER),
        new NativeAuditMapping("AFFECTED_BY_SPELL_POWER", GAME_TAG.AFFECTED_BY_SPELL_POWER, EngineGameTag.AFFECTED_BY_SPELL_POWER),
        new NativeAuditMapping("BATTLECRY", GAME_TAG.BATTLECRY, EngineGameTag.BATTLECRY),
        new NativeAuditMapping("DEATHRATTLE", GAME_TAG.DEATHRATTLE, EngineGameTag.DEATHRATTLE),
        new NativeAuditMapping("SECRET", GAME_TAG.SECRET, EngineGameTag.SECRET),
        new NativeAuditMapping("TAUNT", GAME_TAG.TAUNT, EngineGameTag.TAUNT),
        new NativeAuditMapping("CHARGE", GAME_TAG.CHARGE, EngineGameTag.CHARGE),
        new NativeAuditMapping("RUSH", GAME_TAG.RUSH, EngineGameTag.RUSH),
        new NativeAuditMapping("DIVINE_SHIELD", GAME_TAG.DIVINE_SHIELD, EngineGameTag.DIVINE_SHIELD),
        new NativeAuditMapping("STEALTH", GAME_TAG.STEALTH, EngineGameTag.STEALTH),
        new NativeAuditMapping("WINDFURY", GAME_TAG.WINDFURY, EngineGameTag.WINDFURY),
        new NativeAuditMapping("POISONOUS", GAME_TAG.POISONOUS, EngineGameTag.POISONOUS),
        new NativeAuditMapping("FREEZE", GAME_TAG.FREEZE, EngineGameTag.FREEZE),
        new NativeAuditMapping("CANT_ATTACK", GAME_TAG.CANT_ATTACK, EngineGameTag.CANT_ATTACK),
        new NativeAuditMapping("CANT_BE_TARGETED_BY_SPELLS", GAME_TAG.CANT_BE_TARGETED_BY_SPELLS, EngineGameTag.CANT_BE_TARGETED_BY_SPELLS),
        new NativeAuditMapping("CANT_BE_TARGETED_BY_HERO_POWERS", GAME_TAG.CANT_BE_TARGETED_BY_HERO_POWERS, EngineGameTag.CANT_BE_TARGETED_BY_HERO_POWERS),
        new NativeAuditMapping("AURA", GAME_TAG.AURA, EngineGameTag.AURA),
        new NativeAuditMapping("ADJACENT_BUFF", GAME_TAG.ADJACENT_BUFF, EngineGameTag.ADJACENT_BUFF),
        new NativeAuditMapping("ENRAGED", GAME_TAG.ENRAGED, EngineGameTag.ENRAGED),
        new NativeAuditMapping("SILENCE", GAME_TAG.SILENCE, EngineGameTag.SILENCE),
        new NativeAuditMapping("DISCARD_CARDS", null, EngineGameTag.DISCARD_CARDS, fatal: false),
        new NativeAuditMapping("COST_BLOOD", GAME_TAG.COST_BLOOD, (EngineGameTag)2196),
        new NativeAuditMapping("COST_FROST", GAME_TAG.COST_FROST, (EngineGameTag)2197),
        new NativeAuditMapping("COST_UNHOLY", GAME_TAG.COST_UNHOLY, (EngineGameTag)2198),
        new NativeAuditMapping("CORPSE_SPENDER", GAME_TAG.CORPSE_SPENDER, (EngineGameTag)4058)
    };

    private static NativeAuditMapping[] NativeReferencedMappings() => new[] {
        new NativeAuditMapping("CHARGE", GAME_TAG.CHARGE, EngineGameTag.CHARGE),
        new NativeAuditMapping("COUNTER", GAME_TAG.COUNTER, EngineGameTag.COUNTER),
        new NativeAuditMapping("DIVINE_SHIELD", GAME_TAG.DIVINE_SHIELD, EngineGameTag.DIVINE_SHIELD),
        new NativeAuditMapping("FREEZE", GAME_TAG.FREEZE, EngineGameTag.FREEZE),
        new NativeAuditMapping("SECRET", GAME_TAG.SECRET, EngineGameTag.SECRET),
        new NativeAuditMapping("SILENCE", GAME_TAG.SILENCE, EngineGameTag.SILENCE),
        new NativeAuditMapping("SPELLPOWER", GAME_TAG.SPELLPOWER, EngineGameTag.SPELLPOWER),
        new NativeAuditMapping("TAUNT", GAME_TAG.TAUNT, EngineGameTag.TAUNT),
        new NativeAuditMapping("WINDFURY", GAME_TAG.WINDFURY, EngineGameTag.WINDFURY)
    };

    private static NativeMetadataMapping[] NativeMetadataMappings() => new[] {
        new NativeMetadataMapping("CARD_SET", GAME_TAG.CARD_SET, (int)EngineGameTag.CARD_SET),
        new NativeMetadataMapping("FACTION", GAME_TAG.FACTION, (int)EngineGameTag.FACTION),
        new NativeMetadataMapping("AttackVisualType", null, (int)EngineGameTag.AttackVisualType),
        new NativeMetadataMapping("TRIGGER_VISUAL", GAME_TAG.TRIGGER_VISUAL, (int)EngineGameTag.TRIGGER_VISUAL),
        new NativeMetadataMapping("DevState", null, (int)EngineGameTag.DevState),
        new NativeMetadataMapping("TECH_LEVEL", GAME_TAG.TECH_LEVEL, (int)EngineGameTag.TECH_LEVEL),
        new NativeMetadataMapping("IS_BACON_POOL_MINION", GAME_TAG.IS_BACON_POOL_MINION, (int)EngineGameTag.IS_BACON_POOL_MINION),
        new NativeMetadataMapping("1429", GAME_TAG.BACON_TRIPLE_UPGRADE_MINION_ID, LegacyTripleUpgradeMetadataTag)
    };

    private static string NativeMetadataDecision(string tagName)
    {
        if (tagName == "CARD_SET") return "product pack policy is manifest-owned; record native Wild presentation for coordinator review";
        if (tagName == "FACTION") return "record only; faction does not drive the isolated engine match";
        if (tagName == "AttackVisualType") return "engine-only legacy metadata; native compiled enum has no semantic name; require actor/rendered proof";
        if (tagName == "TRIGGER_VISUAL") return "record only; require native actor/rendered proof; never patch globally";
        if (tagName == "DevState") return "engine-only development metadata; native compiled enum has no semantic name; not product eligibility";
        if (tagName == "1429") return "native BACON_TRIPLE_UPGRADE_MINION_ID versus raw engine tag 1429; record only";
        return "record only; Battlegrounds metadata is outside the isolated legacy match and pack policy";
    }

    private static void AddSupportedDisplayableSets(CollectionManager manager, Dictionary<string, int> owned)
    {
        var displayable = manager.GetDisplayableCardSets();
        var supportedIds = NativeUIProjection.Supported.Concat(Profile.OwnedCards.Where(card => card.Count > 0
            && NativeUIProjection.IsNativeCollectible(card.Id, Profile)).Select(card => card.Id)).Distinct().OrderBy(id => id).ToArray();
        var supportedSets = supportedIds.Select(id => DefLoader.Get().GetEntityDef(id).GetCardSet());
        var union = NativeUIProjection.DisplayableSetIds(displayable.Concat(supportedSets).Select(set => (int)set), Profile.OwnedCards)
            .Select(set => (TAG_CARD_SET)set).ToList();
        displayable.Clear();
        displayable.AddRange(union);
        var wild = new HashSet<TAG_CARD_SET>(GameUtils.GetAllWildPlayableSets());
        // Local decks are Standard (LocalDeckFormat): without server set-rotation timings no set is rotated offline, so
        // every displayable set is Standard. An owned card outside it would read as Wild-only, so stop at boot instead.
        var standard = new HashSet<TAG_CARD_SET>(GameUtils.GetStandardSets());
        var audit = new List<string> { "id\tsetInt\tsetName\tnativeFormat\trotated\tlegacy\tdisplayable\tallWildMembership\tstandardMembership" };
        var missingOwned = new List<string>();
        foreach (string id in supportedIds)
        {
            TAG_CARD_SET set = DefLoader.Get().GetEntityDef(id).GetCardSet();
            bool inDisplayable = displayable.Contains(set), inWild = wild.Contains(set), inStandard = standard.Contains(set);
            audit.Add(id + "\t" + (int)set + "\t" + set + "\t" + GameUtils.GetCardSetFormat(set) + "\t"
                + GameUtils.IsSetRotated(set) + "\t" + GameUtils.IsLegacySet(set) + "\t" + inDisplayable + "\t" + inWild + "\t" + inStandard);
            if (owned.TryGetValue(id, out int count) && count > 0 && !inStandard)
                missingOwned.Add(id + " set=" + set);
        }
        audit.Add("#displayableSets\t" + string.Join(",", displayable.Select(set => (int)set + ":" + set)));
        audit.Add("#allWildSets\t" + string.Join(",", wild.OrderBy(set => (int)set).Select(set => (int)set + ":" + set)));
        audit.Add("#standardSets\t" + string.Join(",", standard.OrderBy(set => (int)set).Select(set => (int)set + ":" + set)));
        File.WriteAllLines(Path.Combine(Reports, "native-card-set-audit.tsv"), audit);
        if (missingOwned.Count != 0)
            throw new InvalidOperationException("Owned supported cards absent from native Standard aggregate: " + string.Join(",", missingOwned));
    }

    internal static void PublishResultsMedal() => PublishMedal();

    private static NetCache.NetCacheHeroLevels ProfileHeroLevels()
    {
        var xp = Profile.ClassXp;
        return new NetCache.NetCacheHeroLevels { Levels = Heroes.Select(hero => new NetCache.HeroLevel {
            Class = (TAG_CLASS)(int)SabberStoneCore.Model.Cards.FromId(hero).Class,
            CurrentLevel = ResultsLevel(xp[hero])
        }).ToList() };
    }

    internal static bool PublishResultsHeroLevels(LocalResultsReceipt receipt, LocalResultsFlow results)
    {
        results.RetireScoop();
        Publish(ProfileHeroLevels());
        if (receipt?.HasXpBar != true) return false;
        var card = SabberStoneCore.Model.Cards.FromId(receipt.HeroClass);
        if (card == null) return false;
        var playedClass = (TAG_CLASS)(int)card.Class;
        if (!cache.TryGetValue(typeof(NetCache.NetCacheHeroLevels), out object current)) return false;
        var previous = (NetCache.NetCacheHeroLevels)current;
        var levels = CopyHeroLevels(previous);
        levels.Levels.RemoveAll(level => level.Class == playedClass);
        levels.Levels.Add(new NetCache.HeroLevel { Class = playedClass,
            PrevLevel = ResultsLevel(receipt.ClassXpBefore.Value), CurrentLevel = ResultsLevel(receipt.ClassXpAfter.Value) });
        results.PublishHeroLevels(() => Publish(levels), () => Publish(ProfileHeroLevels()));
        return true;
    }

    private static NetCache.NetCacheHeroLevels CopyHeroLevels(NetCache.NetCacheHeroLevels source) =>
        new NetCache.NetCacheHeroLevels { Levels = source.Levels.Select(level => new NetCache.HeroLevel {
            Class = level.Class, PrevLevel = CopyHeroLevel(level.PrevLevel), CurrentLevel = CopyHeroLevel(level.CurrentLevel)
        }).ToList() };

    private static NetCache.HeroLevel.LevelInfo CopyHeroLevel(NetCache.HeroLevel.LevelInfo source) => source == null ? null
        : new NetCache.HeroLevel.LevelInfo { Level = source.Level, MaxLevel = source.MaxLevel, XP = source.XP, MaxXP = source.MaxXP };

    private static NetCache.HeroLevel.LevelInfo ResultsLevel(int total)
    {
        var level = LocalResultsXp.Project(total);
        return new NetCache.HeroLevel.LevelInfo { Level = level.Level, MaxLevel = level.MaxLevel, XP = level.XP, MaxXP = level.MaxXP };
    }


    internal static LeagueDbfRecord ResolveMedalLeague(LadderPosition ladder, out LadderMedal projected)
    {
        // Projection of the ledger's LadderState; the native floor flags only drive visuals.
        var leagues = GameDbf.League.GetRecords().Where(r => r != null && r.ID > 0 && r.Ranks.Count > 0)
            .OrderByDescending(r => r.LeagueVersion).ThenBy(r => r.LeagueLevel).ToArray();
        if (leagues.Length == 0) throw new InvalidOperationException("No native league/rank configuration available");
        projected = null; LeagueDbfRecord league = null;
        foreach (var record in leagues)
        {
            int top = record.Ranks.Max(r => r.StarLevel);
            try { projected = LadderRules.Medal(ladder, record.ID, record.Ranks.Select(r => new LadderRankRow {
                StarLevel = r.StarLevel, Stars = r.Stars, Legend = r.StarLevel == top }), Profile.LadderArrivedByWin, Profile.GrindTier.Ladder); league = record; break; }
            catch (ArgumentException) { }
        }
        if (projected == null)
        {
            // No retail-shaped league (50 three-star ranks + Legend): keep the lowest-rank shell so the scene still loads.
            league = leagues[0];
            int level = league.Ranks.Min(r => r.StarLevel);
            projected = new LadderMedal { LeagueId = league.ID, StarLevel = level, BestStarLevel = level, StarsPerWin = 1 };
            log.LogError("NATIVE_MEDAL_FALLBACK no retail-shaped league; leagues=" + string.Join(",", leagues.Select(r => r.ID + ":" + r.Ranks.Count)));
        }
        return league;
    }

    private static void PublishMedal()
    {
        LadderPosition ladder = Profile.Ladder;
        var league = ResolveMedalLeague(ladder, out LadderMedal projected);
        var ranked = Profile.RankedHistory;
        var medal = ProjectMedal(projected, ladder.Rating, ranked.Count, ranked.Count(m => m.Result == "Won"));
        var translated = new MedalInfoTranslator(medal);
        if (!translated.IsDisplayable() || !translated.GetCurrentMedal(LocalDeckFormat).IsValid())
            throw new InvalidOperationException("Native medal translation invalid");
        var legendRow = league.Ranks.OrderByDescending(r => r.StarLevel).First();
        Publish(medal); log.LogInfo("NATIVE_MEDAL league=" + league.ID + " index=" + ladder.StarIndex + " level=" + projected.StarLevel
            + " stars=" + projected.Stars + " best=" + projected.BestStarLevel + " x" + projected.StarsPerWin + " streak=" + projected.Streak
            + " legendRow=" + legendRow.StarLevel + "/" + legendRow.Stars + "/" + legendRow.ShowIndividualRanking
            + " streakThreshold=" + league.Ranks.OrderBy(r => r.StarLevel).First().WinStreakThreshold);
    }

    // playerLegendRating: set only for an NPC opponent's medal while the player is in Legend; the NPC is then numbered with the
    // player counted beside it (NpcLegendField.OpponentLegendNumber), so the two never share a number.
    internal static NetCache.NetCacheMedalInfo ProjectMedal(LadderMedal numbers, double? rating, int seasonGames, int seasonWins,
        double? playerLegendRating = null)
    {
        int? legendRank = !numbers.IsLegend || !rating.HasValue ? null
            : playerLegendRating.HasValue ? NpcWorldRuntime.OpponentLegendNumber(rating.Value, seasonGames, playerLegendRating)
            : NpcWorldRuntime.LegendNumber(rating.Value, seasonGames);
        var medal = new NetCache.NetCacheMedalInfo();
        foreach (FormatType format in new[] { FormatType.FT_STANDARD, FormatType.FT_WILD })
        {
            var data = new PegasusUtil.MedalInfoData { FormatType = format, LeagueId = numbers.LeagueId,
                StarLevel = numbers.StarLevel, Stars = numbers.Stars, BestStarLevel = numbers.BestStarLevel,
                StarsPerWin = numbers.StarsPerWin, Streak = numbers.Streak, SeasonId = 0,
                SeasonGames = seasonGames, SeasonWins = seasonWins };
            // The native translator reads LegendRank only when its setter has marked the field present.
            if (legendRank.HasValue) data.LegendRank = legendRank.Value;
            medal.MedalData.Add(format, data);
        }
        return medal;
    }

    internal static bool CompleteGameplayMedal(LadderMedal numbers, NetCache.NetCacheMedalInfo packet, FormatType format)
    {
        return numbers != null && packet != null && (!numbers.IsLegend
            || packet.MedalData.Any(entry => entry.Key == format && entry.Value.HasLegendRank));
    }

    internal static bool OwnsGameplayRank(string screen, string activeMatchId, string pendingMatchId, string transportMatchId)
    {
        return (screen == "Starting" || screen == "Playing") && !string.IsNullOrEmpty(activeMatchId)
            && activeMatchId == pendingMatchId && activeMatchId == transportMatchId;
    }

    internal static LadderPosition ProjectOpponentLadder(double rating)
    {
        if (double.IsNaN(rating) || double.IsInfinity(rating)) throw new ArgumentException("Invalid opponent rating.");
        int index = LadderRules.IndexForRating(rating);
        return LadderRules.Validate(new LadderPosition {
            StarIndex = index, BestStarIndex = index, Multiplier = 1, PlacementGames = LadderRules.PlacementLength,
            Rating = rating, LegendReached = index == LadderRules.LegendIndex
        });
    }

    private static void Publish(object dto)
    {
        if (dto == null || !NativeUIProjection.AllowedCache(dto.GetType().Name)) throw new InvalidOperationException("Unknown native cache projection");
        cache[dto.GetType()] = dto;
        publisher.MakeGenericMethod(dto.GetType()).Invoke(NetCache.Get(), new[] { dto });
        log.LogInfo("NATIVE_CACHE " + dto.GetType().Name);
    }
    private static void PublishBoosters()
    {
        var stacks = NativeUIProjection.ProjectedPackStacks(
            Profile.PackInventory.Select(pack => (pack.PackType, pack.Count)).ToArray(),
            Profile.LastPack?.PackType, Profile.PendingPacks.Count, replayRunning);
        var boosters = new NetCache.NetCacheBoosters();
        foreach (var stack in stacks)
            boosters.BoosterStacks.Add(new NetCache.BoosterStack {
                Id = stack.boosterId, Count = stack.count, EverGrantedCount = stack.count });
        // Native UpdatePacks only visits present stacks; clear actors whose type just ran out.
        var actors = Field(armedPack, "m_unopenedPacks") as Blizzard.T5.Core.Map<int, UnopenedPack>;
        if (actors != null)
            foreach (int id in NativeUIProjection.BoosterIdsToZero(actors.Keys, stacks))
                if (actors[id] != null) actors[id].SetCount(0);
        Publish(boosters);
        if (AtHome()) NativeHomeUI.RefreshDrawer(Box.Get(), Profile.UnopenedPacks, message => log.LogInfo(message));
    }
    private static bool UsePublishedBoosters(Type __0, ref object __result)
    {
        if (!active || __0 != typeof(NetCache.NetCacheBoosters)) return true;
        // Fake opening otherwise replaces the ledger cache with a single Legacy stack.
        __result = null;
        return false;
    }
    // Native ribbon and drag share this predicate; offline OPEN_PACK_EVENT timings never arrive.
    private static bool OpenLocalPack(UnopenedPack __instance, ref string __0, ref bool __result) =>
        !NativeUIProjection.TryProjectPackOpenability(active, __instance.GetBoosterId(), ref __result, ref __0);
    private static bool Pool(ref List<string> __result)
    { if (!active) return true; __result = NativeUIProjection.NativeCollectiblePool(LocalCrafting.CraftableCardIds, Profile.OwnedCards); return false; }
    private static bool Collectible(string __0, ref bool __result)
    { if (!active) return true; __result = NativeUIProjection.IsNativeCollectible(__0, Profile); return false; }
    private static void LoanerOwnership(CollectionDeck __instance, ref bool __1)
    {
        if (active && !__1 && NativeUIProjection.IsExactLoaner(__instance.ID, __instance.HeroCardID, __instance.GetCardsWithCardID()))
            __1 = true;
    }
    private static bool LoanerRuleset(CollectionDeck __instance, ref bool __result)
    {
        if (!active || !__instance.NetworkContentsLoaded()
            || !NativeUIProjection.IsExactLoaner(__instance.ID, __instance.HeroCardID, __instance.GetCardsWithCardID())) return true;
        DeckRuleset ruleset = __instance.GetRuleset();
        __result = ruleset != null && ruleset.IsDeckValid(__instance, DeckRule.RuleType.PLAYER_OWNS_EACH_COPY);
        return false;
    }
    private static bool SkipActive() => !active;
    private static bool ReadOnly() { if (active) log.LogWarning("NATIVE_UI_READ_ONLY blocked native deck edit"); return !active; }
    private static bool BlockTransport() { if (active) log.LogInfo("NATIVE_CACHE transport suppressed"); return !active; }
    private static bool Batch(object __0)
    {
        if (!active) return true;
        foreach (object pair in (IEnumerable)Field(__0, "m_requests"))
        {
            var type = (Type)pair.GetType().GetProperty("Key").GetValue(pair, null);
            var request = pair.GetType().GetProperty("Value").GetValue(pair, null);
            if (!NativeUIProjection.AllowedCache(type.Name)) { Fail(new InvalidOperationException("Unknown cache batch: " + type.FullName + "\n" + Environment.StackTrace)); return false; }
            AccessTools.Field(request.GetType(), "m_reload").SetValue(request, false);
        }
        return true;
    }
    private static bool RequestCache(Type __0)
    {
        if (!active) return true;
        if (cache.TryGetValue(__0, out object dto)) Publish(dto);
        else if (__0 != typeof(NetCache.NetCacheCollection) || ready) Fail(new InvalidOperationException("Missing local cache: " + __0.FullName));
        return false;
    }
    private static bool ReloadCache(Type __1) => RequestCache(__1);
    private static bool DecksReady(ref bool __result)
    {
        if (!active) return true;
        __result = false;
        if (!ready) return false;
        try
        {
            if (CollectionManager.Get().GetDecks().Count != decks.Count) return false;
            foreach (long id in decks.Keys) ValidateNative(CollectionManager.Get().GetDeck(id));
            __result = true;
        }
        catch (Exception error) { Fail(error); }
        return false;
    }
    private static readonly Dictionary<long, string> unrestoredCraftingRecipes = new Dictionary<long, string>();

    internal static PegasusUtil.Collection CollectionPacket(IEnumerable<KeyValuePair<string, int>> counts)
    {
        return CollectionPacket(counts.Select(pair => new LocalCardCount { Id = pair.Key, Count = pair.Value }));
    }

    // One native stack per (card, version): native SetCounts overwrites duplicate keys, so sum them first.
    // NumSeen below Count is what native shows as NEW (card glow, class-tab counts); seen == null means all seen.
    internal static PegasusUtil.Collection CollectionPacket(IEnumerable<LocalCardCount> cards, Func<string, int, int> seen = null)
    {
        var packet = new PegasusUtil.Collection();
        foreach (var group in cards.Where(card => card.Count > 0).GroupBy(card => new { card.Id, card.Premium })
            .Select(group => new { group.Key, Count = group.Sum(card => card.Count) }))
            packet.Stacks.Add(new CardStack {
                CardDef = new PegasusShared.CardDef { Asset = GameUtils.TranslateCardIdToDbId(group.Key.Id), Premium = group.Key.Premium },
                Count = group.Count, NumSeen = seen == null ? group.Count : Math.Max(0, Math.Min(group.Count, seen(group.Key.Id, group.Key.Premium))),
                LatestInsertDate = new Date { Year = 2026, Month = 10, Day = 16, Hours = 0, Min = 0, Sec = 0 } });
        return packet;
    }

    private static LocalCustomDeck CraftingRecipe(long id)
    {
        var saved = Profile.GetDraft(id);
        if (saved != null) return saved;
        var loaner = decks[id];
        return new LocalCustomDeck { Id = id, Name = "Loaner: " + loaner.Name, Hero = loaner.Hero,
            Cards = loaner.CardIds.ToList(), Sideboards = loaner.Sideboards.Select(row =>
                new LocalSideboard { Source = row.Key, Cards = row.Value.ToList() }).ToList() };
    }

    // True only while native reconciles decks for a committed craft (SaveLocalDraft ignores those reconcile saves).
    private static bool craftingSynchronize;
    internal static void ApplyCraftingChanges(List<CollectionManager.CardModification> changes)
    {
        var manager = CollectionManager.Get();
        var edited = manager.GetEditedDeck();
        CollectionDeck snapshot = null;
        if (edited != null)
        {
            snapshot = new CollectionDeck();
            CopyCraftingDeck(snapshot, edited);
        }
        bool outerSynchronize = craftingSynchronize;
        try { craftingSynchronize = true; manager.OnCardsModified(changes); }
        finally
        {
            craftingSynchronize = outerSynchronize;
            try
            {
                if (snapshot != null)
                {
                    try { CopyCraftingDeck(edited, snapshot); }
                    catch
                    {
                        RememberReducedCraftingRecipe(edited, new LocalCustomDeck {
                            Id = snapshot.ID, Name = snapshot.Name, Hero = NativeBaseHero(snapshot),
                            Cards = snapshot.GetCardsWithCardID(), Sideboards = ReadSideboards(snapshot).ToList()
                        });
                        throw;
                    }
                    var recipe = new LocalCustomDeck { Id = edited.ID, Name = edited.Name, Hero = NativeBaseHero(edited),
                        CardEntries = DeckEntries(edited), Sideboards = ReadSideboards(edited).ToList() };
                    if (LocalCrafting.AssignOwnedPremiums(recipe, Profile.OwnedCount).Count > 0) {
                        ProjectMissingDraftCards(edited, recipe, true);
                        ProjectSideboards(edited, recipe.Sideboards);
                    }
                    RefreshCraftingOwnership(edited, changes.Select(change => change.cardID));
                    CollectionDeckTray.Get()?.GetCardsContent()?.UpdateCardList();
                    CollectionDeckTray.Get()?.GetDecksContent()?.RefreshMissingCardIndicators();
                }
            }
            finally { ReprojectCraftingDecks(changes.Select(change => change.cardID)); }
        }
    }

    private static void CopyCraftingDeck(CollectionDeck target, CollectionDeck source)
    {
        target.CopyFrom(source);
        // Native CopyFrom retains slot counts/premiums and sideboards, but drops Fabled tray counts.
        var originals = source.GetSlotsIncludingSideboards().ToArray();
        var copies = target.GetSlotsIncludingSideboards().ToArray();
        for (int i = 0; i < originals.Length; i++) copies[i].NumSubCards = originals[i].NumSubCards;
    }

    internal static void ReprojectCraftingDecks(IEnumerable<string> cardIds, bool personalOnly = false)
    {
        var changed = new HashSet<string>(cardIds, StringComparer.Ordinal);
        var errors = new List<Exception>();
        foreach (long id in decks.Keys)
        {
            var saved = Profile.GetDraft(id);
            if (saved == null && personalOnly) continue;
            saved = saved ?? CraftingRecipe(id);
            if (!saved.Cards.Any(changed.Contains) && !saved.Sideboards.Any(row =>
                changed.Contains(row.Source) || row.Cards.Any(changed.Contains))) continue;
            var native = CollectionManager.Get().GetDeck(id);
            if (native != null && (native.IsBeingEdited() || ReferenceEquals(native, CollectionManager.Get().GetEditedDeck()))) continue;
            try { ProjectCraftingRecipe(native, CollectionManager.Get().GetBaseDeck(id), saved); }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count > 0) throw new AggregateException("Crafting deck projection failed.", errors);
    }

    internal static void ProjectCraftingRecipe(CollectionDeck native, CollectionDeck baseline, LocalCustomDeck saved)
    {
        try
        {
            if (native == null || baseline == null) throw new InvalidOperationException("Missing projected deck " + saved.Id);
            ProjectMissingDraftCards(native, saved);
            ProjectSideboards(native, saved.Sideboards);
            if (baseline != native)
            {
                ProjectMissingDraftCards(baseline, saved);
                ProjectSideboards(baseline, saved.Sideboards);
            }
            unrestoredCraftingRecipes.Remove(saved.Id);
        }
        catch
        {
            RememberReducedCraftingRecipe(native, saved);
            throw;
        }
    }

    private static void RememberReducedCraftingRecipe(CollectionDeck native, LocalCustomDeck saved)
    {
        if (native == null || native.Name != saved.Name || NativeBaseHero(native) != saved.Hero) return;
        var cards = native.GetCardsWithCardID();
        var sides = ReadSideboards(native);
        // Record actual reconciliation damage only; unrelated editor changes remain ordinary saves.
        if (cards.GroupBy(id => id).Any(group => group.Count() > saved.Cards.Count(id => id == group.Key))
            || sides.Any(row => !saved.Sideboards.Any(original => row.Source == original.Source
                && row.Cards.GroupBy(id => id).All(group => group.Count() <= original.Cards.Count(id => id == group.Key))))) return;
        if (cards.Count != saved.Cards.Count || SideboardSignature(sides) != SideboardSignature(saved.Sideboards))
            unrestoredCraftingRecipes[saved.Id] = CraftingRecipeSignature(native);
    }

    private static string CraftingRecipeSignature(CollectionDeck native)
    {
        return string.Join(",", native.GetCardsWithCardID().OrderBy(id => id, StringComparer.Ordinal))
            + "|" + SideboardSignature(ReadSideboards(native));
    }

    private static LocalDeck ValidateNative(CollectionDeck native)
    {
        if (native == null || !decks.TryGetValue(native.ID, out LocalDeck local) || !native.NetworkContentsLoaded())
            throw new InvalidOperationException("Unmapped/unloaded native deck");
        var ids = native.GetCardsWithCardID().ToArray();
        LocalCustomDeck custom = Profile.GetDraft(native.ID);
        if (custom == null)
            NativeUIProjection.ValidateSelection(local.Id, NativeBaseHero(native), ids, new Dictionary<string, int>());
        else
        {
            if (SideboardSignature(custom.Sideboards) != SideboardSignature(ReadSideboards(native)))
                throw new InvalidOperationException("Native sideboard differs from saved selections.");
            NativeUIProjection.ValidateDraftProjection(native.ID, custom.Name, custom.Hero, custom.Cards.ToArray(),
                native.Name, NativeBaseHero(native), ids, NativeUIProjection.TotalOwned(Profile));
        }
        if (native.Name != (custom?.Name ?? "Loaner: " + local.Name) || NativeBaseHero(native) != local.Hero
            || !ids.OrderBy(id => id).SequenceEqual(local.CardIds.OrderBy(id => id)))
            throw new InvalidOperationException("Native selected contents differ from projection");
        return local;
    }
    private static DeckPickerTrayDisplay pickerTray;
    private static void BindPicker()
    {
        var tray = pickerTray = SceneMgr.Get().GetMode() == SceneMgr.Mode.TOURNAMENT
            ? UnityEngine.Object.FindFirstObjectByType<DeckPickerTrayDisplay>() : null;
        selected = "";
        var button = Field(tray, "m_playButton");
        if (button == null) boundButton = null;
        if (button != null && button != boundButton)
        {
            tray.OverridePlayButtonCallback(OnPlay); boundButton = button; log.LogInfo("NATIVE_PICKER Play bound to durable start");
        }
        var native = tray?.GetSelectedCollectionDeck();
        if (native != null)
        {
            ValidateNative(native);
            selected = native.ID + "|" + native.Name + "|" + native.HeroCardID + "|" + string.Join(",", native.GetCardsWithCardID().OrderBy(id => id));
        }
    }
    // The ledger key a match on this native deck settles under (what StartPicked passes to LocalLadder.StartSelected); null when unmapped.
    internal static string DeckKeyOf(CollectionDeck native)
    {
        if (native == null) return null;
        decks.TryGetValue(native.ID, out LocalDeck local);
        bool custom = Profile?.GetDraft(native.ID) != null;
        return custom || local != null ? LocalDeckRecord.KeyFor(custom, native.ID, local?.Id) : null;
    }
    internal static int DeckCount => decks.Count;
    private static void OnPlay(UIEvent e) => StartPicked(false);
    internal static bool PickerCanStart => ready && !sceneWaiting && failure.Length == 0 && NativeResultsUI.CanStart
        && LocalLadder.Screen != "Starting" && LocalLadder.Screen != "Playing"
        && !NativeDeckPickerGuide.Open;
    // Play and the #28 REMATCH button: the same validated start; a rematch keeps the last match's NPC and deck.
    internal static void StartPicked(bool rematch)
    {
        try
        {
            if (!PickerCanStart) return;
            var tray = UnityEngine.Object.FindFirstObjectByType<DeckPickerTrayDisplay>();
            var native = tray?.GetSelectedCollectionDeck();
            selected = "";
            var local = ValidateNative(native);
            selected = native.ID + "|" + native.Name + "|" + native.HeroCardID + "|" + string.Join(",", native.GetCardsWithCardID().OrderBy(id => id));
            LocalCustomDeck custom = Profile.GetDraft(native.ID);
            if (custom != null && !Profile.IsDeckComplete(custom.Id)) {
                DialogManager.Get()?.ShowPopup(new AlertPopup.PopupInfo {
                    m_headerText = "Incomplete deck", m_text = "Acquire missing cards and complete this deck before playing.",
                    m_responseDisplay = AlertPopup.ResponseDisplay.OK
                });
                return;
            }
            if (custom != null)
                NativeUIProjection.ValidateCustomSelection(native.ID, custom.Hero, custom.Cards.ToArray(),
                    NativeBaseHero(native), native.GetCardsWithCardID().ToArray(), NativeUIProjection.TotalOwned(Profile));
            lastSelectedId = native.ID;
            bool casual = NativeUIProjection.CasualPlayMode;
            log.LogInfo((rematch ? "NATIVE_PICKER_REMATCH " : "NATIVE_PICKER_START ") + selected + (casual ? " mode=Casual" : ""));
            LocalLadder.StartSelected(LocalDeckRecord.KeyFor(custom != null, native.ID, local.Id), local, custom != null,
                nativeMatchmaking: true, rematch: rematch, casual: casual);
        }
        catch (Exception error) { Fail(error); }
    }

    // Gated while a native presentation owns HUB input; stale receipt phases still do not block it.
    private static bool HomeButtonGate() => !NativeRewardUI.BlocksHome && !NativeAchievements.Active
        && !NativeProfileSelector.BlocksHome;
    private static bool HomePacks(Box __instance)
    {
        if (NativeRewardUI.BlocksHome || NativeAchievements.Active || NativeProfileSelector.BlocksHome) return false;
        if (!AtHome()) return true;
        __instance.FireButtonPressEvent(Box.ButtonType.OPEN_PACKS); return false;
    }
    private static bool AtHome() => active && ready && SceneMgr.IsInitialized() && SceneMgr.Get().GetMode() == SceneMgr.Mode.HUB;
    private static bool HomeModeHover(UIEvent __0) => !AtHome() || NativeHomeUI.ShowModeTooltip(Box.Get(), __0.GetElement());
    private static bool HomeModeOut(UIEvent __0) => !AtHome() || NativeHomeUI.HideModeTooltip(Box.Get(), __0.GetElement());
    private static void HomeStoreTooltip(StoreButton __instance) { if (AtHome()) NativeHomeUI.StoreTooltip(__instance); }
    // BoxScrollButton bypasses Box hover events when the retail mode feature is unavailable.
    private static bool HomeScrollHover(BoxScrollButton __instance) => !AtHome() || NativeHomeUI.ShowModeTooltip(Box.Get(), __instance);
    private static bool HomeScrollOut(BoxScrollButton __instance) => !AtHome() || NativeHomeUI.HideModeTooltip(Box.Get(), __instance);

    private static bool LocalHubStart()
    {
        if (!active) return true;
        var scene = SceneMgr.Get();
        var box = Box.Get();
        if (!ready || scene == null || box == null || CollectionManager.Get() == null || !CollectionManager.Get().IsFullyLoaded())
        {
            Fail(new InvalidOperationException("Local HUB dependencies were not ready"));
            return false;
        }
        if (boundHubBox != box)
        {
            if (boundHubBox != null) boundHubBox.RemoveButtonPressListener(OnLocalHubButton);
            box.AddButtonPressListener(OnLocalHubButton);
            boundHubBox = box;
        }
        var download = GameDownloadManagerProvider.Get();
        log.LogInfo("NATIVE_HUB_NOTIFY mode=" + scene.GetMode() + " prev=" + scene.GetPrevMode()
            + " transition=" + scene.IsTransitioning() + " boxState=" + Field(box, "m_state")
            + " boxBusy=" + box.IsBusy() + " loggedIn=" + Network.IsLoggedIn()
            + " downloadReady=" + (download != null && download.IsReadyToPlay));
        scene.NotifySceneLoaded();
        target = SceneMgr.Mode.HUB; sceneBegan = Time.realtimeSinceStartup; sceneWaiting = scene.IsTransitioning();
        if (!sceneWaiting) State("SceneLoaded:HUB");
        NativeHomeUI.BeginHub();
        ConfigureHomeButtons(box, true);
        return false;
    }
    private static void OnLocalHubButton(Box.ButtonType button, bool tutorialPreview, object userData)
    {
        if (!AtHome() || tutorialPreview) return;
        NativeHomeControl control = button == Box.ButtonType.TRADITIONAL ? NativeHomeControl.Traditional
            : button == Box.ButtonType.COLLECTION ? NativeHomeControl.Collection
            : button == Box.ButtonType.OPEN_PACKS ? NativeHomeControl.OpenPacks
            : button == Box.ButtonType.STORE ? NativeHomeControl.Store
            : NativeHomeControl.Unsupported;
        NativeMenuTarget route = NativeMenuFlow.HomeRoute(control);
        if (route == NativeMenuTarget.Picker) Navigate(SceneMgr.Mode.TOURNAMENT);
        else if (route == NativeMenuTarget.Collection)
        {
            CollectionManager.Get().NotifyOfBoxTransitionStart();
            Navigate(SceneMgr.Mode.COLLECTIONMANAGER);
        }
        else if (route == NativeMenuTarget.Pack) Navigate(SceneMgr.Mode.PACKOPENING);
        else if (route == NativeMenuTarget.LocalShop) { } // NativeShop raised this press after opening the local shop.
        else log.LogWarning("NATIVE_MENU unsupported home control blocked: " + button);
    }
    private static IEnumerable<CodeInstruction> CollectionExitOnlineCheck(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        MethodInfo original = Required(typeof(Network), "IsLoggedIn");
        MethodInfo replacement = Required(typeof(NativeUIProbe), nameof(CollectionExitLoggedIn));
        int replaced = 0;
        foreach (var instruction in code)
        {
            if (!instruction.Calls(original)) continue;
            instruction.opcode = System.Reflection.Emit.OpCodes.Call;
            instruction.operand = replacement;
            replaced++;
        }
        if (replaced != 1) throw new InvalidOperationException("Collection Exit login call-site changed: " + replaced);
        return code;
    }
    private static bool CollectionExitLoggedIn() => NativeMenuFlow.CollectionExitAllowed(
        Network.IsLoggedIn(), active, SceneMgr.Get().GetPrevMode() == SceneMgr.Mode.TOURNAMENT);
    private static bool PackDisconnect()
    {
        if (!active) return true;
        if (NativeMenuFlow.FatalPackDisconnectReturnsHome(replayRunning)) Navigate(SceneMgr.Mode.HUB);
        else log.LogWarning("NATIVE_MENU kept active pack reveal on disconnect callback");
        return false;
    }
    private static void RefreshHomeButtons(Box __instance) => ConfigureHomeButtons(__instance, false);
    private static void ConfigureHomeButtons(Box __instance, bool report)
    {
        if (!AtHome() || __instance == null) return;
        __instance.DisableAllButtons();
        __instance.SetBoxButtonInteractions(true, __instance.m_PlayButton, true);
        __instance.SetButtonEnabled(true, __instance.m_OpenPacksButton);
        __instance.SetButtonEnabled(true, __instance.m_CollectionButton);
        NativeHiddenEntries.Apply(__instance);
        NativeHomeUI.RefreshDrawer(__instance, Profile.UnopenedPacks, message => log.LogInfo(message));
        NativeShop.RefreshHomeStore();
        if (report) log.LogInfo("NATIVE_MENU home enabled=TRADITIONAL,OPEN_PACKS,COLLECTION; unsupported modes disabled with backlog tooltips; journal hidden");
    }

    public static void Navigate(SceneMgr.Mode mode)
    {
        try
        {
            if (!ready || sceneWaiting || replayRunning || LocalLadder.Screen == "Playing" || LocalLadder.Screen == "Starting") return;
            if (!NativeResultsUI.CanNavigate(mode == SceneMgr.Mode.HUB)) return;
            if (mode != SceneMgr.Mode.HUB && mode != SceneMgr.Mode.PACKOPENING && mode != SceneMgr.Mode.COLLECTIONMANAGER && mode != SceneMgr.Mode.TOURNAMENT)
                throw new ArgumentException("Unsupported native UI target");
            if (mode == SceneMgr.Mode.PACKOPENING)
            {
                if (Profile.PackRevealPending) RememberPendingReceipt();
                else { receipt = null; receiptSequence = 0; receiptPacks = 0; }
                packStartHash = Hash(); packCommitHash = ""; replayArmed = true; doneAcknowledged = false;
                armedPack = null; revealDirector = null; massBoosterFed = false;
                PublishBoosters();
            }
            else replayArmed = false;
            if (mode == SceneMgr.Mode.TOURNAMENT)
            {
                PublishMedal();
                if (lastSelectedId != 0) Options.Get().SetLong(Option.LAST_CUSTOM_DECK_CHOSEN, lastSelectedId);
            }
            target = mode; sceneWaiting = true; sceneBegan = Time.realtimeSinceStartup;
            State("Loading:" + mode);
            var transition = new JobDefinition("NativeUI.StageScene", StageScene(mode), Array.Empty<IJobDependency>());
            if (SplashScreen.Get() != null)
                Processor.QueueJob("NativeUI.HideSplash", SplashScreen.Get().Hide(transition), Array.Empty<IJobDependency>());
            else Processor.QueueJob(transition);
        }
        catch (Exception error) { Fail(error); }
    }
    private static IEnumerator<IAsyncJobResult> StageScene(SceneMgr.Mode mode)
    {
        try { SceneMgr.Get().SetNextMode(mode); }
        catch (Exception error) { Fail(error); }
        yield break;
    }
    private static bool FakePack(ref bool __result) { if (!active) return true; __result = replayArmed; return false; }
    private static bool FakeCount(ref int __result)
    {
        if (!active) return true;
        __result = NativeUIProjection.ProjectedPackCount(Profile.UnopenedPacks, Profile.PendingPacks.Count, replayRunning);
        return false;
    }
    private static bool BeforeOpen(PackOpening __instance, UnopenedPack __0, int __1, out int __state)
    {
        __state = 0;
        if (!active) return true;
        if (!NativeResultsUI.CanStart) return false;
        try
        {
            if (!ready || !replayArmed || replayRunning || __instance != armedPack || __1 < 1 || __1 > LocalProfile.MassPackLimit)
                throw new InvalidOperationException("Native pack transaction was not armed for 1-" + LocalProfile.MassPackLimit + " packs");
            bool resumed = Profile.PackRevealPending;
            if (__0 == null) throw new InvalidOperationException("Native opening pack missing");
            int boosterId = __0.GetBoosterId();
            string packType = NativeUIProjection.PackTypeForBooster(boosterId)
                ?? throw new InvalidOperationException("Unknown native booster ID: " + boosterId);
            int beforePacks = Profile.UnopenedPacks, beforeSequence = Profile.PackSequence, beforeGold = Profile.StoredGoldBalance;
            var beforeCollection = PackOwnership();
            // A pending reveal resumes whole (one pack or a mass batch), whatever the gesture picked up.
            if (!resumed && __1 > 1 && !NativeUIProjection.MassOpenable(boosterId, resumed))
                throw new InvalidOperationException("Mass opening is not available for pack type " + packType);
            var batch = Profile.BeginNativePacks(resumed ? Profile.LastPack.PackType : packType, resumed ? 1 : __1);
            LocalPackResult committed = batch[batch.Count - 1];
            receiptPacks = batch.Count;
            receipt = NativeUIProjection.PendingReceipt(batch);
            receiptSequence = Profile.PackSequence;
            var cards = (List<NetCache.BoosterCard>)Field(__instance, "m_fakeBoosterCards");
            cards.Clear();
            foreach (var card in batch.SelectMany(pending => pending.Cards)) __instance.AddFakeBoosterCard(card.Id, (TAG_PREMIUM)card.Premium);
            if (!NativeUIProjection.BatchReceipt(cards.Select(card => LocalPackCard.ReceiptKey(card.Def.Name, (int)card.Def.Premium)).ToArray(), receiptPacks).SequenceEqual(receipt))
                throw new InvalidOperationException("Committed native receipt was not installed exactly");
            var grants = NativeUIProjection.NewGrantCounts(batch.SelectMany(pack => pack.Cards));
            if (resumed)
            {
                if (Profile.UnopenedPacks != beforePacks || Profile.PackSequence != beforeSequence || Profile.StoredGoldBalance != beforeGold
                    || !PackOwnership().OrderBy(pair => pair.Key).SequenceEqual(beforeCollection.OrderBy(pair => pair.Key)))
                    throw new InvalidOperationException("Pending native receipt resume changed durable economy");
            }
            else
            {
                if (Profile.UnopenedPacks != beforePacks - batch.Count || Profile.PackSequence != beforeSequence + batch.Count
                    || Profile.StoredGoldBalance != checked(beforeGold + batch.Sum(pack => pack.Cards.Sum(card => card.DuplicateGold))) || !Profile.PackRevealPending)
                    throw new InvalidOperationException("Native pack transaction durable delta mismatch");
                NativeUIProjection.ValidatePackOwnership(beforeCollection, PackOwnership(), grants);
            }
            if (!Profile.PackRevealPending || receiptSequence != Profile.PackSequence
                || !NativeUIProjection.PendingReceipt(Profile.PendingPacks).SequenceEqual(receipt))
                throw new InvalidOperationException("Native pending receipt identity mismatch");
            revealDirector = Field(__instance, "m_director") as PackOpeningDirector
                ?? throw new InvalidOperationException("Native replay director missing before animation");
            // Fake OpenBooster skips retail's SetNumPacksOpened; establish the native mode before its coroutine starts.
            if (receiptPacks >= 2) revealDirector.SetNumPacksOpened(receiptPacks);
            massPageReceipt = null; massPageReady = false; massRevealAllStarted = false;
            replayRunning = true; massBoosterFed = false; massHighlightsFinished = false; massSummaryDismissed = false; doneAcknowledged = false; replayBegan = Time.realtimeSinceStartup;
            packCommitHash = Hash();
            transactionFacts = "mode=" + (resumed ? "resume" : "new") + ";opened=" + batch.Count + ";packs=" + beforePacks + "->" + Profile.UnopenedPacks
                + ";sequence=" + beforeSequence + "->" + Profile.PackSequence + ";storedGold=" + beforeGold + "->" + Profile.StoredGoldBalance
                + ";displayGold=" + Profile.Gold
                + ";pending=" + Profile.PackRevealPending + ";requestedBooster=" + boosterId + ";packType=" + committed.PackType;
            liveGrantProof = resumed ? "resume=bootstrap ownership already projected"
                : "newGrants=refreshing:" + string.Join(",", grants.OrderBy(pair => pair.Key).Select(pair => pair.Key + ":+" + pair.Value));
            if (!resumed) AddNativeGrants(grants);
            RefreshCosmetics();
            PublishBoosters();
            State(resumed ? "Revealing:resumed pending receipt" : batch.Count == 1 ? "Revealing:committed one pack" : "Revealing:committed " + batch.Count + " packs");
            __state = FairPackRules.NativeBoosterId(committed.PackType);
            return true;
        }
        catch (Exception error) { Fail(error); return false; }
    }
    private static void OpenedPackType(int __state, ref int ___m_lastOpenedBoosterId, ref int ___m_packOpeningId)
    {
        if (__state == 0) return;
        // Native fake opening hard-codes Legacy before its delayed director coroutine runs.
        ___m_lastOpenedBoosterId = __state;
        ___m_packOpeningId = __state;
    }
    private static Dictionary<string, int> PackOwnership() => Profile.StoredOwnedCards.ToDictionary(
        card => LocalPackCard.ReceiptKey(card.Id, card.Premium), card => card.Count, StringComparer.Ordinal);
    private static void AddNativeGrants(IReadOnlyDictionary<string, int> grants)
    {
        var manager = CollectionManager.Get() ?? throw new InvalidOperationException("Native collection unavailable during pack commit");
        var rewards = NativeUIProjection.PackGrantDeltas(Profile, grants,
            (id, premium) => manager.GetOwnedCount(id, (TAG_PREMIUM)premium))
            .Select(card => new CardRewardData(card.Id, (TAG_PREMIUM)card.Premium, card.Count)).ToList();
        if (rewards.Count != 0) manager.AddCardRewards(rewards, true);
        var verified = new List<string>();
        foreach (var pair in grants.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var card = LocalPackCard.FromKey(pair.Key);
            int expected = Math.Max(Profile.StoredOwnedCount(card.Id, card.Premium), Profile.OwnedCount(card.Id, card.Premium));
            int actual = manager.GetOwnedCount(card.Id, (TAG_PREMIUM)card.Premium);
            if (actual != expected) throw new InvalidOperationException("Native collection refresh mismatch: " + pair.Key);
            verified.Add(pair.Key + ":+" + pair.Value + "=" + actual);
        }
        ReprojectCraftingDecks(grants.Keys.Select(key => LocalPackCard.FromKey(key).Id), true);
        liveGrantProof = rewards.Count == 0 ? "newGrants=none;duplicatesOnly=true" : "newGrants=" + string.Join(",", verified);
    }
    // Premium copies granted at settlement/reconcile; per version, add-only.
    private static object premiumDeckLedger;
    internal static void AddNativePremiumGrants()
    {
        var manager = CollectionManager.Get() ?? throw new InvalidOperationException("Native collection unavailable for premium grants.");
        var deltas = NativeUIProjection.PremiumGrantDeltas(Profile.OwnedCards,
            (id, premium) => manager.GetOwnedCount(id, (TAG_PREMIUM)premium), Heroes.Contains);
        if (deltas.Count > 0) manager.AddCardRewards(deltas.Select(delta => new CardRewardData(delta.Id, (TAG_PREMIUM)delta.Premium, delta.Count)).ToList(), true);
        foreach (var delta in deltas)
            if (manager.GetOwnedCount(delta.Id, (TAG_PREMIUM)delta.Premium) < Profile.OwnedCount(delta.Id, delta.Premium))
                throw new InvalidOperationException("Native premium refresh mismatch: " + delta.Id + "/" + delta.Premium);
        // Refresh only newly projected versions, in quality order, including load-time catch-up.
        foreach (var delta in deltas.Where(delta => NativeUIProjection.ShouldRefreshPremiumDecks(Profile, delta.Id, delta.Premium))
            .OrderBy(delta => LocalCrafting.PremiumQuality(delta.Premium)))
            RefreshUpgradedDecks(delta.Id, delta.Premium, 0);
        // A zero-copy mastery grant changes saved slots even when native already owns every reward copy.
        if (!ReferenceEquals(premiumDeckLedger, Profile.LedgerIdentity)) {
            ReprojectCraftingDecks(Profile.ProgressionCards.Select(card => card.Id), true);
            premiumDeckLedger = Profile.LedgerIdentity;
        }
    }
    private static bool BeforeDone(PackOpeningDirector __instance)
    {
        if (!active) return true;
        try
        {
            bool directorMatches = ReferenceEquals(__instance, revealDirector);
            if (!replayRunning || !directorMatches || doneAcknowledged) return false;
            if (receiptPacks >= 2)
            {
                if (!replayRunning || !directorMatches || !massBoosterFed || !__instance.IsMassPackOpening())
                    throw new InvalidOperationException("Native mass highlights were not armed for this director");
                // Continue already proved the final page, then destroyed its actors. Never re-read that empty list.
                if (massHighlightsFinished)
                {
                    if (__instance.IsDoneButtonShown) __instance.HideCardsAndDoneButton();
                    return false;
                }
                // A visible Done is a highlight advance, never permission to clear a 5N receipt.
                if (!__instance.IsDoneButtonShown) return true;
                __instance.MassPackOpeningContinuePressed();
                return false;
            }
            if (!__instance.IsDoneButtonShown) return false;
            ValidateRevealedActors(__instance, true);
            Profile.CompleteNativePackReveal(receiptSequence, receipt);
            doneAcknowledged = true;
            State("RevealAcknowledged:pending receipt cleared");
            return true;
        }
        catch (Exception error) { Fail(error); return false; }
    }
    private static void ValidateRevealedActors(PackOpeningDirector director, bool advanceShown)
    {
        var hidden = Field(director, director.IsMassPackOpening() ? "m_massPackOpeningHiddenCards" : "m_hiddenCards");
        var cards = (Field(hidden, "m_cards") as IEnumerable)?.Cast<PackOpeningCard>().Where(card => card != null).ToArray();
        string[] actorIds = cards?.Select(card => LocalPackCard.ReceiptKey(card.GetActor()?.GetEntityDef()?.GetCardId(),
            (int)(card.GetActor()?.GetPremium() ?? TAG_PREMIUM.NORMAL))).ToArray();
        bool[] revealed = cards?.Select(card => card.IsRevealed()).ToArray();
        if (!NativeUIProjection.CanAcknowledgePackReveal(active, replayRunning, advanceShown,
            ReferenceEquals(director, revealDirector), receipt, actorIds, revealed, doneAcknowledged, director.IsMassPackOpening() ? massPageReceipt : null))
            throw new InvalidOperationException("Native advance did not prove the exact five revealed page actors with matching premiums and quantities");
    }
    // Capture the actual dequeued list at the attach boundary, before actors can diverge from it.
    private static void CaptureMassPage(object __instance, List<NetCache.BoosterCard> __0)
    {
        if (!active || !replayRunning || !ReferenceEquals(__instance, Field(revealDirector, "m_massPackOpeningHiddenCards"))) return;
        massPageReady = false;
        massRevealAllStarted = false;
        massPageReceipt = __0?.Select(card => LocalPackCard.ReceiptKey(card.Def.Name, (int)card.Def.Premium)).ToArray();
        if (!NativeUIProjection.ReceiptContainsPage(receipt, massPageReceipt))
            Fail(new InvalidOperationException("Native dequeued highlight page does not belong to the committed receipt"));
    }
    // Called inside MoveNext after native EnableReveal, after the yielded 0.5-second delay.
    private static void EnableMassPageReveal(HiddenCards hidden)
    {
        hidden.EnableReveal();
        if (active && replayRunning && ReferenceEquals(hidden, Field(revealDirector, "m_massPackOpeningHiddenCards"))
            && !massHighlightsFinished && massPageReceipt != null)
            massPageReady = true;
    }
    private static void AttachMassPage(Game.PackOpening.MassPackOpeningHiddenCards hidden, List<NetCache.BoosterCard> cards)
    {
        CaptureMassPage(hidden, cards);
        hidden.AttachMassPackOpeningHighlightCards(cards);
    }
    private static PackOpeningCard[] MassPageCards(PackOpeningDirector director)
    {
        var hidden = Field(director, "m_massPackOpeningHiddenCards");
        return (Field(hidden, "m_cards") as IEnumerable)?.Cast<PackOpeningCard>().Where(card => card != null).ToArray();
    }
    private static bool MassPageInputReady(PackOpeningDirector director)
    {
        return replayRunning && ReferenceEquals(director, revealDirector) && massBoosterFed
            && massPageReady && !massHighlightsFinished && !doneAcknowledged;
    }
    private static bool BeforeMassRevealAll(PackOpeningDirector __instance)
    {
        if (!active) return true;
        if (!MassPageInputReady(__instance) || massRevealAllStarted) return false;
        var cards = MassPageCards(__instance);
        if (cards == null || cards.Length != 5 || cards.Any(card => !card.IsReady()) || cards.All(card => card.IsRevealed())) return false;
        massRevealAllStarted = true;
        return true;
    }
    private static bool BeforeMassRevealRandom(PackOpeningDirector __instance)
    {
        return !active || (MassPageInputReady(__instance) && !massRevealAllStarted);
    }
    // Continue destroys the actors before checking the remaining queue. Prove the current group first.
    private static bool BeforeMassContinue(PackOpeningDirector __instance)
    {
        if (!active) return true;
        try
        {
            if (!replayRunning || receiptPacks < 2 || !massBoosterFed || massHighlightsFinished
                || !ReferenceEquals(__instance, revealDirector) || !__instance.IsMassPackOpening()) return false;
            if (!massPageReady) return false;
            var cards = MassPageCards(__instance);
            if (cards == null || cards.Length != 5 || cards.Any(card => !card.IsRevealed())) return false;
            ValidateRevealedActors(__instance, true);
            var remaining = Field(__instance, "m_highlights") as Queue<List<NetCache.BoosterCard>>
                ?? throw new InvalidOperationException("Native mass highlight queue missing");
            massPageReady = false;
            massPageReceipt = null;
            massHighlightsFinished = remaining.Count == 0;
            if (__instance.IsDoneButtonShown) __instance.HideCardsAndDoneButton();
            return true;
        }
        catch (Exception error) { Fail(error); return false; }
    }
    // Mouse/touch can drive the widget animation directly; PressDoneButton's bool only covers the keyboard path.
    private static bool BeforeMassSummaryEvent(PackOpeningDirector __instance, string __0)
    {
        if (!active || __0 != "HIDE_MPO_SUMMARY") return true;
        if (!replayRunning || !ReferenceEquals(__instance, revealDirector) || !massBoosterFed
            || !massHighlightsFinished || massSummaryDismissed || doneAcknowledged || !__instance.IsMassPackOpening()) return false;
        massSummaryDismissed = true;
        return true;
    }
    // The summary's hide event follows its Done animation; actor highlights alone never settle a batch.
    private static bool BeforeMassDone(PackOpeningDirector __instance)
    {
        if (!active) return true;
        try
        {
            bool directorMatches = ReferenceEquals(__instance, revealDirector);
            if (!replayRunning || !directorMatches || doneAcknowledged) return false;
            var summary = Field(__instance, "m_massPackOpeningSummary");
            string[] shown = (Field(summary, "m_cards") as List<NetCache.BoosterCard>)?.Select(card => LocalPackCard.ReceiptKey(card.Def.Name, (int)card.Def.Premium)).ToArray();
            int packs = Field(summary, "m_numPacksOpened") is int count ? count : 0;
            if (!NativeUIProjection.CanAcknowledgeMassReveal(active, replayRunning, __instance.IsMassPackOpening(), directorMatches,
                receipt, receiptPacks, shown, packs, doneAcknowledged, massBoosterFed && massHighlightsFinished && massSummaryDismissed))
                throw new InvalidOperationException("Native mass Done did not prove the completed batch summary");
            Profile.CompleteNativePackReveal(receiptSequence, receipt);
            doneAcknowledged = true;
            State("RevealAcknowledged:pending batch of " + packs + " cleared");
            return true;
        }
        catch (Exception error) { Fail(error); return false; }
    }
    // Replace the call in OpenBooster: Mono may inline the tiny native iterator factory before a prefix can run.
    private static IEnumerable<CodeInstruction> PackCoroutineCall(IEnumerable<CodeInstruction> instructions)
    {
        return ReplacePackCall(instructions, Required(typeof(PackOpening), "OnFakeBoosterOpened"), nameof(SelectPackCoroutine));
    }
    private static IEnumerator SelectPackCoroutine(PackOpening pack)
    {
        if (active && replayRunning && receiptPacks >= 2 && pack == armedPack)
        {
            log.LogInfo("NATIVE_PACK mass coroutine selected packs=" + receiptPacks);
            return MassBoosterOpened(pack, receiptPacks);
        }
        // Single packs and inactive probes retain the unpatched native fake coroutine.
        return (IEnumerator)Required(typeof(PackOpening), "OnFakeBoosterOpened").Invoke(pack, null);
    }
    private static MethodInfo MassPageMoveNext() =>
        AccessTools.EnumeratorMoveNext(Required(typeof(PackOpeningDirector), "NewHighlightsReadyImpl"))
            ?? throw new MissingMethodException("PackOpeningDirector.NewHighlightsReadyImpl.MoveNext");
    private static IEnumerable<CodeInstruction> MassPageCalls(IEnumerable<CodeInstruction> instructions)
    {
        var code = ReplacePackCall(instructions,
            Required(typeof(Game.PackOpening.MassPackOpeningHiddenCards), "AttachMassPackOpeningHighlightCards"), nameof(AttachMassPage));
        return ReplacePackCall(code, Required(typeof(HiddenCards), "EnableReveal"), nameof(EnableMassPageReveal));
    }
    private static IEnumerable<CodeInstruction> ReplacePackCall(IEnumerable<CodeInstruction> instructions, MethodInfo original, string replacement)
    {
        var code = instructions.ToList();
        int replaced = 0;
        foreach (var instruction in code)
        {
            if (!instruction.Calls(original)) continue;
            // Mutate in place to retain branch labels and exception boundaries.
            instruction.opcode = System.Reflection.Emit.OpCodes.Call;
            instruction.operand = Required(typeof(NativeUIProbe), replacement);
            replaced++;
        }
        if (replaced != 1) throw new InvalidOperationException("Native pack call-site changed: " + original.Name + " count=" + replaced);
        return code;
    }
    private static void VerifyPackHooks(Harmony harmony)
    {
        var open = Required(typeof(PackOpening), "OpenBooster", new[] { typeof(UnopenedPack), typeof(int) });
        var finished = Required(typeof(PackOpening), "OnDirectorFinished");
        var expected = new[] {
            (open, nameof(BeforeOpen), "prefix"), (open, nameof(OpenedPackType), "postfix"), (open, nameof(PackCoroutineCall), "transpiler"),
            (MassPageMoveNext(), nameof(MassPageCalls), "transpiler"),
            (Required(typeof(PackOpeningDirector), "FinishPackOpen"), nameof(BeforeDone), "prefix"),
            (Required(typeof(PackOpeningDirector), "FinishMassPackOpen"), nameof(BeforeMassDone), "prefix"),
            (Required(typeof(PackOpeningDirector), "MassPackOpeningContinuePressed"), nameof(BeforeMassContinue), "prefix"),
            (Required(typeof(PackOpeningDirector), "MassPackOpeningRevealAllPressed"), nameof(BeforeMassRevealAll), "prefix"),
            (Required(typeof(PackOpeningDirector), "MassPackOpeningRevealRandomCard"), nameof(BeforeMassRevealRandom), "prefix"),
            (Required(typeof(PackOpeningDirector), "HandleMassPackOpeningSummaryEvent"), nameof(BeforeMassSummaryEvent), "prefix"),
            (finished, nameof(BeforeReplayFinished), "prefix"), (finished, nameof(ReplayFinished), "postfix")
        };
        foreach (var hook in expected)
        {
            var info = Harmony.GetPatchInfo(hook.Item1);
            var patches = hook.Item3 == "prefix" ? info?.Prefixes : hook.Item3 == "postfix" ? info?.Postfixes : info?.Transpilers;
            if (patches == null || !patches.Any(patch => patch.owner == harmony.Id && patch.PatchMethod == Required(typeof(NativeUIProbe), hook.Item2)))
                throw new InvalidOperationException("Native pack hook did not bind: " + hook.Item1.Name + "/" + hook.Item2);
        }
        log.LogInfo("NATIVE_PACK hook check OK: OpenBooster coroutine call=1; mass MoveNext attach=1 enable=1; guards=" + expected.Length);
    }
    private static IEnumerator MassBoosterOpened(PackOpening pack, int packs)
    {
        float deadline = Time.realtimeSinceStartup + 15f;
        yield return new WaitForSeconds(0.5f);
        while (true)
        {
            bool loaded;
            try
            {
                if (pack == null || pack != armedPack || !replayRunning) yield break;
                var director = Field(pack, "m_director") as PackOpeningDirector;
                loaded = director != null && Field(director, "m_massPackOpeningHighlights") != null
                    && Field(director, "m_massPackOpeningSummary") != null;
            }
            catch (Exception error) { Fail(error); yield break; }
            if (Time.realtimeSinceStartup >= deadline) { Fail(new TimeoutException("Native mass pack opening widgets did not load within 15 seconds")); yield break; }
            if (loaded) break;
            yield return null;
        }
        FeedMassBooster(pack, packs);
    }
    private static void FeedMassBooster(PackOpening pack, int packs)
    {
        try
        {
            if (pack == null || pack != armedPack || !replayRunning) return;
            if (!active || !ready || !replayArmed || packs < 2 || packs != receiptPacks || massBoosterFed)
                throw new InvalidOperationException("Native mass pack feed was not armed for the committed batch");
            var director = Field(pack, "m_director") as PackOpeningDirector;
            if (director == null || !ReferenceEquals(director, revealDirector)) throw new InvalidOperationException("Mass director changed");
            if (Field(director, "m_massPackOpeningHighlights") == null || Field(director, "m_massPackOpeningSummary") == null)
                throw new InvalidOperationException("Native mass pack opening widgets missing before feed");
            var center = Field(pack, "m_centerPack") as GameObject;
            if (center != null) { UnityEngine.Object.Destroy(center); AccessTools.Field(typeof(PackOpening), "m_centerPack").SetValue(pack, null); }
            var fake = (List<NetCache.BoosterCard>)Field(pack, "m_fakeBoosterCards");
            var cards = fake.ToList();
            if (!NativeUIProjection.BatchReceipt(cards.Select(card => LocalPackCard.ReceiptKey(card.Def.Name, (int)card.Def.Premium)).ToArray(), packs).SequenceEqual(receipt))
                throw new InvalidOperationException("Mass receipt not installed exactly");
            fake.Clear();
            director.SetNumPacksOpened(packs);
            director.OnBoosterOpened(cards, false);
            if (!director.IsMassPackOpening()) throw new InvalidOperationException("Native mass flow did not start");
            director.Play((int)Field(pack, "m_lastOpenedBoosterId"), 1f, (int)Field(pack, "m_packOpeningId"));
            AccessTools.Field(typeof(PackOpening), "m_autoOpenPending").SetValue(pack, false);
            massBoosterFed = true;
            log.LogInfo("NATIVE_PACK mass reveal packs=" + packs + " cards=" + cards.Count);
        }
        catch (Exception error) { Fail(error); }
    }
    private static void MassOpenableOnly(UnopenedPack __0, ref bool __result)
    {
        if (active && __result && __0 != null && !NativeUIProjection.MassOpenable(__0.GetBoosterId(), Profile.PackRevealPending)) __result = false;
    }
    private static void RememberPendingReceipt()
    {
        var packs = Profile.PendingPacks;
        receipt = NativeUIProjection.PendingReceipt(packs);
        receiptPacks = packs.Count;
        receiptSequence = Profile.PackSequence;
    }
    private static bool BeforeReplayFinished(object __0, ref bool __state)
    {
        __state = false;
        var disposition = NativeUIProjection.PackCompletion(active, replayRunning,
            ReferenceEquals(__0, revealDirector), doneAcknowledged);
        if (disposition == PackCompletionDisposition.Passthrough) return true;
        if (disposition == PackCompletionDisposition.Ignore) return false;
        if (disposition == PackCompletionDisposition.Fail)
        {
            Fail(new InvalidOperationException("Native director finished without durable Done acknowledgement"));
            return false;
        }
        __state = true;
        return true;
    }
    private static void ReplayFinished(bool __state)
    {
        if (!__state) return;
        massPageReceipt = null; massPageReady = false; massRevealAllStarted = false;
        replayRunning = false; massBoosterFed = false; replayArmed = true; armedPack = null; revealDirector = null;
        PublishBoosters();
        State("RevealComplete:ready for next pack");
    }
    private static bool OnboardingOption(Option option, ref bool val, ref bool __result)
    {
        if (!NativeUIProjection.TryGetOnboardingOption(IsolatedLaunch, option.ToString(), out bool value)) return true;
        val = value;
        __result = true;
        return false;
    }
    private static void EnsurePreference(GameSaveKeyId key)
    {
        var manager = GameSaveDataManager.Get();
        if (!manager.IsDataReady(key)) manager.ApplyGameSaveDataUpdate(new GameSaveDataUpdate { Tuple = new List<GameSaveKey> { new GameSaveKey { Id = (long)key } } });
    }
    private static void SaveLocal(GameSaveDataManager.SubkeySaveRequest request) => Invoke(GameSaveDataManager.Get(), "SaveSubkeyToLocalCache", request);
    private static bool Preferences(List<GameSaveKeyId> __0, GameSaveDataManager.OnRequestDataResponseDelegate __1)
    {
        if (!active) return true;
        bool valid = __0 != null && __0.Count > 0 && __0.All(k => NativeUIProjection.AllowedPreference(k.ToString()));
        if (valid) foreach (var key in __0) EnsurePreference(key);
        else Fail(new InvalidOperationException("Unknown preference request: " + string.Join(",", __0 ?? new List<GameSaveKeyId>())));
        callbacks.Enqueue(() => __1?.Invoke(valid)); return false;
    }
    private static bool SavePreferences(List<GameSaveDataManager.SubkeySaveRequest> __0, GameSaveDataManager.OnSaveDataResponseDelegate __1, ref bool __result)
    {
        if (!active) return true;
        bool valid = __0 != null && __0.Count > 0 && __0.All(r => r != null
            && NativeUIProjection.AllowedPreferenceSave(r.Key.ToString(), r.Subkey.ToString(), r.Long_Values));
        try
        {
            if (valid) foreach (var request in __0)
            {
                EnsurePreference(request.Key);
                // The mass-opening tooltip's "seen" flag lives in this save; a failed write only means it shows again next start.
                if (request.Key == GameSaveKeyId.PLAYER_FLAGS)
                    try { Profile.MarkMassPackTooltipSeen(); }
                    catch (Exception error) { log.LogWarning("NATIVE_UI mass pack tooltip flag not saved: " + error.Message); }
                SaveLocal(request);
            }
            else log.LogWarning("NATIVE_UI denied non-UI preference save");
        }
        catch (Exception error) { valid = false; Fail(error); }
        __result = valid;
        callbacks.Enqueue(() => __1?.Invoke(valid)); return false;
    }

#if !SOLOSTONE_RELEASE
    private static string PendingCache()
    {
        var pending = new List<string>();
        var net = NetCache.Get();
        var batches = Field(net, "m_cacheRequests") as IEnumerable;
        if (batches == null) return "";
        foreach (object batch in batches)
        {
            var callback = Field(batch, "m_callback") as Delegate;
            foreach (object pair in (IEnumerable)Field(batch, "m_requests"))
            {
                var type = (Type)pair.GetType().GetProperty("Key").GetValue(pair, null);
                if (!cache.ContainsKey(type)) pending.Add((callback?.Method.Name ?? "none") + ":" + type.Name);
            }
        }
        return string.Join(",", pending.Distinct());
    }
#endif
#if !SOLOSTONE_RELEASE
    private static string[] CollectionActorStatus(CollectionManager manager)
    {
        var display = manager?.GetCollectibleDisplay();
        var groups = Field(display, "m_cardActors") as List<CollectionCardActors>;
        var actors = (groups ?? new List<CollectionCardActors>()).SelectMany(group =>
            Field(group, "m_cardActors") as List<Actor> ?? new List<Actor>()).Where(actor => actor != null).ToArray();
        var visible = actors.Where(actor => actor.gameObject.activeInHierarchy && actor.GetComponentsInChildren<Renderer>()
            .Any(renderer => renderer.enabled && renderer.gameObject.activeInHierarchy && renderer.isVisible)).ToArray();
        var cards = new List<string>();
        foreach (var actor in visible)
        {
            var definition = actor.GetEntityDef();
            string id = definition?.GetCardId() ?? "";
            int expected = Profile.TotalOwnedCount(id);
            cards.Add(id + ":nativeOwned=" + manager.GetTotalOwnedCount(id) + ":profileOwned=" + expected
                + ":cost=" + definition?.GetCost() + ":attack=" + definition?.GetATK() + ":healthOrDurability=" + definition?.GetHealth());
        }
        return new[] { "collectionPageActorCount=" + actors.Length, "collectionVisibleActorCount=" + visible.Length,
            "collectionPageActorIds=" + string.Join(",", actors.Select(actor => actor.GetEntityDef()?.GetCardId() ?? "")),
            "collectionVisibleCards=" + string.Join(";", cards) };
    }
#endif
#if !SOLOSTONE_RELEASE
    private static string[] DeckDiagnostics(CollectionManager manager)
    {
        if (manager == null) return new[] { "deckDiagnostics=collection-manager-unavailable" };
        FormatType format = Options.GetFormatType();
        DeckTrayDeckListContent content = CollectionDeckTray.Get()?.GetDecksContent();
        var sections = ((Field(content, "m_traySections") as IEnumerable)?.Cast<object>().OfType<TraySection>()
            ?? Enumerable.Empty<TraySection>()).ToArray();
        bool trayDoneEntering = Field(content, "m_doneEntering") is bool doneEntering && doneEntering;
        int unusedShown = sections.Count(section => section.m_deckBox != null
            && section.m_deckBox.GetDeckID() == -1 && section.IsDeckBoxShown());
        var boxes = UnityEngine.Object.FindObjectsByType<CollectionDeckBoxVisual>(FindObjectsSortMode.None)
            .Where(box => box != null).ToArray();
        var lines = new List<string> { "trayDoneEntering=" + trayDoneEntering + ",unusedShown=" + unusedShown,
            "nativeDeckReordering=" + NetCache.Get()?.GetNetObject<NetCache.NetCacheFeatures>()?.Collection.DeckReordering
                + ",canDragToReorder=" + content?.CanDragToReorderDecks + ",deleteNotice=" + deleteNoticeState,
            NativeCrafting.UpgradeEntryStatus(),
            "actualDeckBoxes=" + string.Join(";", boxes.Select(box => {
            CollectionDeck resolved = box.GetCollectionDeck();
            TraySection section = sections.FirstOrDefault(candidate => ReferenceEquals(candidate.m_deckBox, box));
            return "instance=" + box.GetInstanceID() + ",deckId=" + box.GetDeckID() + ",templateId=" + box.GetDeckTemplateId()
                + ",resolvedDeckId=" + (resolved == null ? "null" : resolved.ID.ToString())
                + ",active=" + box.gameObject.activeInHierarchy + ",enabled=" + box.IsDeckEnabled()
                + ",selectable=" + box.CanSelectDeck() + ",playable=" + box.IsDeckPlayable()
                + ",positionIndex=" + box.GetPositionIndex() + ",logicalLocked=" + box.IsLocked()
                + ",normalRootActive=" + (box.m_normalDeckVisuals != null && box.m_normalDeckVisuals.activeSelf)
                + ",lockedRootActive=" + (box.m_lockedDeckVisuals != null && box.m_lockedDeckVisuals.activeSelf)
                + ",highlightPresent=" + (Field(box, "m_highlightState") != null)
                + ",trayShown=" + (section != null && section.IsDeckBoxShown())
                + ",localPosition=" + box.transform.localPosition + ",localEuler=" + box.transform.localEulerAngles
                + ",localScale=" + box.transform.localScale;
        })) };
        foreach (long id in decks.Keys.OrderBy(id => id))
        {
            CollectionDeck deck = manager.GetDeck(id);
            if (deck == null) { lines.Add("deck=" + id + ":unavailable"); continue; }
            CollectionDeck.CardCountByStatus counts = deck.CountCardsByStatus(format, false);
            CollectionDeckSlot invalid = deck.FindInvalidSlot(format, true);
            string invalidDetail = "none";
            if (invalid != null)
            {
                EntityDef definition = invalid.GetEntityDef();
                TAG_CARD_SET set = definition.GetCardSet();
                invalidDetail = invalid.CardID + "|set=" + (int)set + ":" + set + "|format=" + GameUtils.GetCardSetFormat(set)
                    + "|rotated=" + GameUtils.IsSetRotated(set) + "|gameplayEvent=" + GameUtils.IsCardGameplayEventActive(definition)
                    + "|constructedDeny=" + GameUtils.IsBannedByConstructedDenylist(deck, invalid.CardID)
                    + "|wildDeny=" + GameUtils.IsBannedByWildDenylist(deck, invalid.CardID);
            }
            IList<DeckRuleViolation> violations = new List<DeckRuleViolation>();
            DeckRuleset ruleset = deck.GetRuleset(format);
            bool validIgnoringOwnership = ruleset != null && ruleset.IsDeckValid(deck, out violations, DeckRule.RuleType.PLAYER_OWNS_EACH_COPY);
            string violationDetail = ruleset == null ? "ruleset-null" : string.Join(",", violations.Select(violation =>
                violation.Rule.Type + "|" + violation.DisplayError));
            string common = "deck=" + deck.ID + ":name=" + deck.Name + ":templateSource=" + deck.TemplateSource
                + ":templateId=" + deck.DeckTemplateId + ":rulesetValid=" + deck.IsValidForRuleset
                + ":modeFormatValid=" + deck.IsValidForModeAndFormat(SceneMgr.Mode.TOURNAMENT, Options.GetInRankedPlayMode(), format)
                + ":rulesetValidIgnoringOwnership=" + validIgnoringOwnership
                + ":status=total=" + counts.Total + ",valid=" + counts.Valid + ",invalid=" + counts.Invalid
                + ",missing=" + counts.Missing + ",extra=" + counts.Extra + ":invalidSlot=" + invalidDetail
                + ":violationsIgnoringOwnership=" + violationDetail;
            var deckBoxes = boxes.Where(box => box.GetDeckID() == id || box.GetCollectionDeck()?.ID == id).ToArray();
            if (deckBoxes.Length == 0) lines.Add(common + ":actualBox=absent");
            else foreach (CollectionDeckBoxVisual box in deckBoxes)
                lines.Add(common + ":actualBox=" + box.GetInstanceID() + ":enabled=" + box.IsDeckEnabled()
                    + ":selectable=" + box.CanSelectDeck() + ":playable=" + box.IsDeckPlayable());
        }
        return lines.ToArray();
    }
#endif
    private static string ButtonStatus(PegUIElement button, HashSet<PegUIElement> pressDisabled) =>
        "present=" + (button != null) + "|isEnabled=" + (button != null && button.IsEnabled())
        + "|pressDisabled=" + (button != null && pressDisabled != null && pressDisabled.Contains(button));
#if !SOLOSTONE_RELEASE
    // In-memory counterpart of the live audit, using the same identity/tag mappings.
    // A client entity the host no longer lists as revealed is not a read failure. The host's reveal set describes everything it has
    // SENT; the client keeps the entity until the queued task that hides it runs (an enchantment on a minion that just died or
    // returned to hand is the usual case; a choice boundary deliberately audits while those tasks are still queued). So:
    //   - `drained` false: pending, recorded in `unrevealed` for the log and state, no difference, readiness unaffected;
    //   - `drained` true (no client queue blocker) AND `confirm(entityId)` true: a real disagreement, reported as a visible
    //     `unrevealed:ID:CARD` difference. `confirm` (TestControlRules.UnrevealedConfirmation) holds back the first sample of an entity:
    //     with no client blocker the host can still be one in-flight packet ahead (QueueDispatcher posts to the main thread first).
    //     Null `confirm` means no debounce. An unconfirmed drained entity is recorded as `pending`, like an undrained one.
    // Either way the audit goes on with the remaining entities, so one such entity never costs the whole view its diagnostics.
    internal static List<string> TestControlVisibleDiff(NativeMatch match, GameState front, List<string> presentationDifferences = null, bool drained = true, List<string> unrevealed = null, Func<int, bool> confirm = null)
    {
        var diff = new List<string>();
        foreach (Entity entity in front.GetEntityMap().Values)
        {
            string card = entity.GetCardId();
            int zone = entity.GetTag(GAME_TAG.ZONE);
            if (string.IsNullOrEmpty(card) || (zone != 1 && zone != 4 && !(zone == 3 && entity.GetControllerId() == 1))) continue;
            if (!NativeUIProjection.TryResolveVisibleEngineEntity(card, match.EngineReturnId(entity.GetEntityId()), id =>
                match.Game.IdEntityDic.ContainsKey(id) ? match.Game.IdEntityDic[id] : null,
                id => match.TransportCardId(entity.GetEntityId(), id), out SabberStoneCore.Model.Entities.IPlayable engine))
            {
                if (!match.Game.IdEntityDic.TryGetValue(match.EngineReturnId(entity.GetEntityId()), out engine)) continue;
            }
            string expectedCard = TestControlPresentationCardId(match, engine);
            if (expectedCard != card) { diff.Add("identity:" + entity.GetEntityId()); continue; }
            if (match.TransportCardId(engine.Id, engine.Card.Id) != card)
                presentationDifferences?.Add((engine.Id == match.Game.Player1.Hero.Id ? "playerSkin:" : "npcSkin:")
                    + entity.GetEntityId() + ":" + engine.Card.Id + ":" + card);
            IReadOnlyDictionary<int, int> expected;
            try { expected = match.SnapshotVisibleTransportTags(engine); }
            catch (TransportEntityNotVisibleException error)
            {
                bool real = (confirm?.Invoke(entity.GetEntityId()) ?? true) && drained;
                unrevealed?.Add((real ? "difference" : "pending") + ": client entity id=" + entity.GetEntityId() + " card=" + card
                    + " zone=" + zone + " controller=" + entity.GetControllerId() + " maps to engine id=" + engine.Id
                    + " card=" + engine.Card.Id + "; " + error.Message);
                if (real) diff.Add("unrevealed:" + entity.GetEntityId() + ":" + card);
                continue;
            }
            var actual = entity.GetTags().GetMap();
            foreach (var mapping in NativeAuditMappings().Where(row => row.Fatal && row.NativeTag.HasValue))
            {
                if ((mapping.Scope == NativeAuditScope.WeaponOnly && engine.Card.Type != EngineCardType.WEAPON)
                    || (mapping.Scope == NativeAuditScope.NonWeapon && engine.Card.Type == EngineCardType.WEAPON)) continue;
                int tag = (int)mapping.NativeTag.Value;
                bool hasActual = actual.TryGetValue(tag, out int value), hasExpected = expected.TryGetValue(tag, out int wanted);
                if (!NativeUIProjection.TestControlTransportTagMatches(tag, engine.Card.Type, hasActual, value, hasExpected, wanted))
                    diff.Add("tag:" + entity.GetEntityId() + ":" + tag + ":" + value + "!=" + wanted);
            }
            foreach (var tag in new[] { GAME_TAG.ZONE, GAME_TAG.ZONE_POSITION, GAME_TAG.CONTROLLER })
                if (entity.GetTag(tag) != (expected.TryGetValue((int)tag, out int location) ? location : 0)) diff.Add("location:" + entity.GetEntityId() + ":" + tag);
        }
        return diff;
    }
#endif

    // The audit expects exactly what the projection sends: scenario/profile/favourite skins and skin powers.
    private static string TestControlPresentationCardId(NativeMatch match, SabberStoneCore.Model.Entities.IPlayable engine) =>
        match.PresentationCardId(engine.Id, engine.Card.Id, engine.Controller.PlayerId);

#if !SOLOSTONE_RELEASE
    public static void WriteLiveCardAudit(NativeMatch match)
    {
        if (match?.Game == null) return;
        var front = GameState.Get();
        var processor = front?.GetPowerProcessor();
        int nativeTurn = front?.GetGameEntity()?.GetTag(GAME_TAG.TURN) ?? -1;
        if (!NativeUIProjection.LiveFrameIsStable(LocalLadder.Screen == "Playing", front != null,
            front?.IsBusy() ?? true, processor?.GetCurrentTaskList() != null,
            processor?.GetPowerQueue()?.Count ?? -1, nativeTurn, match.Game.Turn)) return;
        var loader = DefLoader.Get();
        if (loader == null || !loader.HasLoadedEntityDefs()) return;

        var rows = new List<string> {
            "entityId\tcardId\tcontroller\tzone\tdefinitionKind\tsemantic\tnativeTag\tengineTag\tnativeLive\tengineLive\tcanonicalBase\tentityDefinition\tliveMismatch"
            + "\tnativePresent\texpectedTransportPresent\texpectedTransport\ttransportSource\texpectedDefinition\tdefinitionApplicable\ttransportMismatch\tdefinitionMismatch"
        };
        int hiddenSkipped = 0, unsupportedSkipped = 0, identityRejected = 0, unrevealed = 0;
        int transportMismatches = 0, definitionMismatches = 0, fatalRows = 0;
        foreach (Entity entity in front.GetEntityMap().Values.OrderBy(value => value.GetEntityId()))
        {
            string cardId = entity.GetCardId();
            if (string.IsNullOrEmpty(cardId)) { hiddenSkipped++; continue; }
            if (!NativeUIProjection.TryResolveVisibleEngineEntity(cardId, match.EngineReturnId(entity.GetEntityId()), id =>
                match.Game.IdEntityDic.ContainsKey(id) ? match.Game.IdEntityDic[id] : null,
                id => match.TransportCardId(entity.GetEntityId(), id), out SabberStoneCore.Model.Entities.IPlayable engineEntity))
            {
                if (NativeUIProjection.SupportsPreview(cardId)) identityRejected++;
                else unsupportedSkipped++;
                continue;
            }
            EntityDef canonical = loader.GetEntityDef(cardId);
            EntityDef entityDefinition = entity.GetEntityDef();
            string definitionKind = ReferenceEquals(entityDefinition, canonical) ? "canonical" : "dynamic";
            Dictionary<int, int> nativeTags = entity.GetTags().GetMap();
            IReadOnlyDictionary<int, int> expectedTags;
            try { expectedTags = match.SnapshotVisibleTransportTags(engineEntity); }
            catch (TransportEntityNotVisibleException) { unrevealed++; fatalRows++; continue; }
            foreach (var mapping in NativeAuditMappings().Where(candidate => candidate.Fatal && candidate.NativeTag.HasValue)
#if STANDARD_PROOF
                .Concat(StandardProofNative.DefinitionIds.Contains(cardId) ? new[] {
                    new NativeAuditMapping("SPELL_SCHOOL", (GAME_TAG)1635, (EngineGameTag)1635),
                    new NativeAuditMapping("TRADEABLE", (GAME_TAG)1720, (EngineGameTag)1720),
                    new NativeAuditMapping("DECK_ACTION_COST", (GAME_TAG)1743, (EngineGameTag)1743),
                    new NativeAuditMapping("IS_USING_TRADE_OPTION", (GAME_TAG)2045, (EngineGameTag)2045)
#if MODERN_MECHANICS
                    , new NativeAuditMapping("KINDRED", (GAME_TAG)4028, (EngineGameTag)4028)
                    , new NativeAuditMapping("REBORN", (GAME_TAG)1085, (EngineGameTag)1085)
                    , new NativeAuditMapping("HAS_BEEN_REBORN", (GAME_TAG)1336, (EngineGameTag)1336)
                    , new NativeAuditMapping("DORMANT", (GAME_TAG)1518, (EngineGameTag)1518)
                    , new NativeAuditMapping("DORMANT_VISUAL", (GAME_TAG)1090, (EngineGameTag)1090)
                    , new NativeAuditMapping("DORMANT_AWAKENED_THIS_TURN", (GAME_TAG)4606, (EngineGameTag)4606)
                    , new NativeAuditMapping("UNTOUCHABLE", (GAME_TAG)448, (EngineGameTag)448)
                    , new NativeAuditMapping("TAG_SCRIPT_DATA_NUM_1", (GAME_TAG)2, (EngineGameTag)2)
#endif
                } : new NativeAuditMapping[0])
#if MODERN_MECHANICS
                .Concat(cardId == "EDR_469e" ? new[] {
                    new NativeAuditMapping("TRIGGER_VISUAL", (GAME_TAG)32, (EngineGameTag)32),
                    new NativeAuditMapping("ATTACHED", (GAME_TAG)40, (EngineGameTag)40),
                    new NativeAuditMapping("DORMANT_AWAKEN_CONDITION_ENCHANT", (GAME_TAG)1519, (EngineGameTag)1519)
                } : new NativeAuditMapping[0])
#endif
#endif
#if ENGINE_SOURCE
                .Concat(engineEntity is SabberStoneCore.Model.Entities.Minion dm && SabberStoneCore.Actions.DormantRules.HasState(dm) ? new[] {
                    new NativeAuditMapping("DORMANT", (GAME_TAG)1518, (EngineGameTag)1518),
                    new NativeAuditMapping("DORMANT_VISUAL", (GAME_TAG)1090, (EngineGameTag)1090),
                    new NativeAuditMapping("UNTOUCHABLE", (GAME_TAG)448, (EngineGameTag)448),
                    new NativeAuditMapping("DORMANT_AWAKENED_THIS_TURN", (GAME_TAG)4606, (EngineGameTag)4606)
                } : new NativeAuditMapping[0])
                .Concat(SabberStoneCore.Actions.TemporaryRules.IsTemporary(engineEntity) ? new[] {
                    new NativeAuditMapping("TEMPORARY", (GAME_TAG)3630, (EngineGameTag)3630)
                } : new NativeAuditMapping[0])
                .Concat(engineEntity is SabberStoneCore.Model.Entities.Enchantment helper && helper.Card.Id == "TIME_046e" ? new[] {
                    new NativeAuditMapping("ATTACHED", (GAME_TAG)40, (EngineGameTag)40),
                    new NativeAuditMapping("DORMANT_AWAKEN_CONDITION_ENCHANT", (GAME_TAG)1519, (EngineGameTag)1519),
                    new NativeAuditMapping("TAG_SCRIPT_DATA_NUM_1", (GAME_TAG)2, (EngineGameTag)2),
                    new NativeAuditMapping("SCORE_VALUE_1", (GAME_TAG)451, (EngineGameTag)451)
                } : new NativeAuditMapping[0])
#endif
#if ENGINE_SOURCE || STANDARD_PROOF
                .Concat(NativeMatch.HasPoweredUpHighlight(engineEntity) ? new[] {
                    new NativeAuditMapping("POWERED_UP", GAME_TAG.POWERED_UP, EngineGameTag.POWERED_UP)
                } : new NativeAuditMapping[0])
#endif
#if ENGINE_SOURCE
                // Printed Combo is transported for the client's Combo glow; audit it only where printed.
                .Concat(engineEntity.Card[EngineGameTag.COMBO] == 1 ? new[] {
                    new NativeAuditMapping("COMBO", GAME_TAG.COMBO, EngineGameTag.COMBO)
                } : new NativeAuditMapping[0])
#endif
            )
            {
                if ((mapping.Scope == NativeAuditScope.WeaponOnly && engineEntity.Card.Type != EngineCardType.WEAPON)
                    || (mapping.Scope == NativeAuditScope.NonWeapon && engineEntity.Card.Type == EngineCardType.WEAPON)) continue;
                int nativeTag = (int)mapping.NativeTag.Value;
                bool nativePresent = nativeTags.TryGetValue(nativeTag, out int nativeLive);
                bool expectedPresent = expectedTags.TryGetValue(nativeTag, out int expectedTransport);
                bool engineNativePresent = engineEntity.NativeTags.TryGetValue(mapping.EngineTag, out int engineNativeRaw);
                var engineZone = engineEntity.Zone?.Type ?? SabberStoneCore.Enums.Zone.INVALID;
                bool historySnapshot = engineEntity is SabberStoneCore.Model.Entities.Enchantment
                    || (engineZone != SabberStoneCore.Enums.Zone.HAND && engineZone != SabberStoneCore.Enums.Zone.PLAY);
                string transportSource = historySnapshot ? "historySnapshot"
                    : !expectedPresent ? "omitted"
                    : engineNativePresent && engineNativeRaw == expectedTransport ? "instance" : "flattened";
                int engineLive = engineEntity[mapping.EngineTag];
                int canonicalBase = canonical.GetTag(mapping.NativeTag.Value);
                int entityDefinitionValue = entityDefinition?.GetTag(mapping.NativeTag.Value) ?? 0;
                int expectedDefinition = 0;
                bool definitionApplicable = NativeUIProjection.TryGetAuditDefinitionTag(
                    definitionKind == "canonical", cardId, nativeTag, out expectedDefinition);
                bool transportMismatch = !NativeUIProjection.TransportTagMatches(
                    nativePresent, nativeLive, expectedPresent, expectedTransport);
#if STANDARD_PROOF
                if (!match.RequiredLiveTagMatches(engineEntity, nativeTag, nativePresent, nativeLive))
                {
                    transportMismatch = true;
                    transportSource += "|requiredLiveMismatch";
                }
#endif
                bool definitionMismatch = !NativeUIProjection.DefinitionTagMatches(
                    definitionApplicable, entityDefinitionValue, expectedDefinition);
                bool liveMismatch = transportMismatch || definitionMismatch;
                if (transportMismatch) transportMismatches++;
                if (definitionMismatch) definitionMismatches++;
                if (liveMismatch) fatalRows++;
                rows.Add(entity.GetEntityId() + "\t" + cardId + "\t"
                    + entity.GetControllerId() + "\t" + entity.GetZone() + "\t" + definitionKind + "\t"
                    + mapping.Name + "\t" + mapping.NativeTag.Value + "\t" + mapping.EngineTag + "\t"
                    + nativeLive + "\t" + engineLive + "\t" + canonicalBase + "\t" + entityDefinitionValue + "\t"
                    + liveMismatch + "\t" + nativePresent + "\t" + expectedPresent + "\t" + expectedTransport
                    + "\t" + transportSource + "\t" + (definitionApplicable ? expectedDefinition.ToString() : "n/a")
                    + "\t" + definitionApplicable + "\t" + transportMismatch + "\t" + definitionMismatch);
            }
        }
        var resources = new List<string> { "entityId\thero\tresource\tnative\tengine\tmismatch" };
        foreach (var player in new[] { match.Game.Player1, match.Game.Player2 })
        {
            Entity nativePlayer = front.GetEntityMap().Values.SingleOrDefault(entity => entity.GetEntityId() == player.Id);
            int earned = nativePlayer?.GetTag(GAME_TAG.CORPSES) ?? 0;
            int spent = nativePlayer?.GetTag(GAME_TAG.CORPSES_SPENT_THIS_GAME) ?? 0;
            int[] nativeValues = { earned, spent, Math.Max(0, earned - spent) };
            int engineEarned = player[(EngineGameTag)2186], engineSpent = player[(EngineGameTag)2639];
            int[] engineValues = { engineEarned, engineSpent, Math.Max(0, engineEarned - engineSpent) };
            string[] names = { "CORPSES", "CORPSES_SPENT_THIS_GAME", "CORPSES_AVAILABLE" };
            for (int i = 0; i < names.Length; i++)
            {
                bool mismatch = nativePlayer == null || nativeValues[i] != engineValues[i];
                if (mismatch) { transportMismatches++; fatalRows++; }
                resources.Add(player.Id + "\t" + player.Hero.Card.Id + "\t" + names[i] + "\t"
                    + nativeValues[i] + "\t" + engineValues[i] + "\t" + mismatch);
            }
        }
        File.WriteAllLines(Path.Combine(Reports, "native-player-resource-audit.tsv"), resources);
        rows.Add("#frame\tturn=" + match.Game.Turn + "\trows=" + (rows.Count - 1) + "\thiddenSkipped=" + hiddenSkipped
            + "\tunsupportedSkipped=" + unsupportedSkipped + "\tidentityRejected=" + identityRejected + "\tunrevealed=" + unrevealed
            + "\tmismatches=" + fatalRows + "\ttransportMismatches=" + transportMismatches
            + "\tdefinitionMismatches=" + definitionMismatches + "\tfatalRows=" + fatalRows);
        File.WriteAllLines(Path.Combine(Reports, "native-live-card-tag-audit.tsv"), rows);
    }
#endif
    public static void WriteStatus() => WriteStatus(false);
#if !SOLOSTONE_RELEASE
    private static string[] DeepStatusLines(CollectionManager manager)
    {
        var pack = UnityEngine.Object.FindFirstObjectByType<PackOpening>();
        var packCards = UnityEngine.Object.FindObjectsByType<PackOpeningCard>(FindObjectsSortMode.None);
        var packActorIds = packCards.Select(card => LocalPackCard.ReceiptKey(card.GetActor()?.GetEntityDef()?.GetCardId(),
            (int)(card.GetActor()?.GetPremium() ?? TAG_PREMIUM.NORMAL))).ToArray();
        var packDetails = packCards.Select(card => {
            var actor = card.GetActor();
            return "instance=" + card.GetInstanceID() + ":card=" + card.GetCardId() + ":definition=" + card.GetEntityDef()?.GetCardId()
                + ":actorInstance=" + actor?.GetInstanceID() + ":actor=" + actor?.GetEntityDef()?.GetCardId()
                + ":ready=" + card.IsReady() + ":revealed=" + card.IsRevealed()
                + ":visible=" + (actor != null && actor.gameObject.activeInHierarchy && actor.GetComponentsInChildren<Renderer>()
                    .Any(renderer => renderer.enabled && renderer.gameObject.activeInHierarchy && renderer.isVisible));
        });
        return new[] {
            string.Join("\n", CollectionActorStatus(manager)),
            "notifications=" + string.Join(",", UnityEngine.Object.FindObjectsByType<Notification>(FindObjectsSortMode.None)
                .Where(item => item.gameObject.activeInHierarchy).Select(item => item.GetType().Name + ":" + item.gameObject.name
                    + ":blocksAchievementToast=" + NotificationBlocksAchievementToast(item))),
            "collectionPageManager=" + (UnityEngine.Object.FindFirstObjectByType<CollectionManagerDisplay>()?.m_pageManager != null),
            string.Join("\n", DeckDiagnostics(manager)),
            "packReady=" + (pack != null && pack.IsReady()), "packDirector=" + (Field(pack, "m_director") != null),
            "nativePackActors=" + packCards.Length,
            "nativePackCardIds=" + string.Join(",", packCards.Select(card => card.GetCardId())),
            "nativePackActorIds=" + string.Join(",", packActorIds),
            "nativePackActorReceiptMatches=" + NativeUIProjection.ReceiptMatchesActors(
                revealDirector != null && revealDirector.IsMassPackOpening() ? massPageReceipt : receipt, packActorIds),
            "nativePackCards=" + string.Join(";", packDetails),
            "cardsPendingReveal=" + Field(Field(pack, "m_director"), "m_cardsPendingReveal")
        };
    }
#endif
    private static void WriteStatus(bool force)
    {
#if SOLOSTONE_RELEASE
        return; // lab state file; a public build writes none
#else
        var manager = CollectionManager.Get();
        var box = Box.Get();
        var pressDisabled = Field(box, "OnPressDisabledButtons") as HashSet<PegUIElement>;
        var journal = box?.GetSPJEntryButton();
        var popups = PopupDisplayManager.Get();
        var dialogs = DialogManager.Get();
        var personal = Profile.VisibleDeckSummaries();
        string scene = SceneMgr.IsInitialized() ? SceneMgr.Get().GetMode().ToString() : "none";
        if (StatusRoutine.DeepScanDue(scene, deepScanScene, force, Time.realtimeSinceStartup, deepScanAt, deepLines != null))
        {
            deepLines = DeepStatusLines(manager);
            deepScanAt = Time.realtimeSinceStartup; deepScanScene = scene;
        }
        File.WriteAllLines(Path.Combine(Reports, "native-ui-state.txt"), new[] {
            "splashActive=" + (SplashScreen.Get() != null && SplashScreen.Get().gameObject.activeInHierarchy),
            string.Join("\n", deepLines),
            "deepScanAgeSeconds=" + (Time.realtimeSinceStartup - deepScanAt).ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
            "runId=" + runId, "utc=" + DateTime.UtcNow.ToString("o"), "phase=" + phase, "ready=" + ready, "profileBound=True",
            "missing=" + missing, "elapsed=" + (Time.realtimeSinceStartup - began),
            "scene=" + scene,
            "transition=" + (SceneMgr.IsInitialized() && SceneMgr.Get().IsTransitioning()), "target=" + target,
            "popupShowing=" + (popups != null && popups.IsShowing),
            "dialogShowing=" + (dialogs != null && dialogs.ShowingDialog()),
            "dialogType=" + (Field(dialogs, "m_currentDialog")?.GetType().Name ?? "none"),
            "gameMenuShown=" + (GameMenu.Get() != null && GameMenu.Get().GameMenuIsShown()),
            "optionsMenuShown=" + (OptionsMenu.Get() != null && OptionsMenu.Get().IsShown()),
            "achievementToast=" + NativeAchievementToast.Diagnostics,
            "menuButtonActive=" + (BnetBar.Get()?.m_menuButton != null && BnetBar.Get().m_menuButton.gameObject.activeInHierarchy),
            "menuButtonInitialized=" + (Field(BnetBar.Get()?.m_menuButton, "m_highlightsByState") != null),
            "renderResolution=" + Screen.width + "x" + Screen.height,
            "fullscreen=" + Screen.fullScreen,
            "spectatorOpenJoin=" + Options.Get().GetBool(Option.SPECTATOR_OPEN_JOIN),
            "homeButton.Play=" + ButtonStatus(box?.m_PlayButton, pressDisabled),
            "homeButton.Packs=" + ButtonStatus(box?.m_OpenPacksButton, pressDisabled),
            "homeButton.Collection=" + ButtonStatus(box?.m_CollectionButton, pressDisabled),
            "homeButton.Arena=" + ButtonStatus(box?.m_ArenaButton, pressDisabled),
            "homeButton.Battlegrounds=" + ButtonStatus(box?.m_BattleGroundsButton, pressDisabled),
            "homeButton.Modes=" + ButtonStatus(box?.m_GameModesButton, pressDisabled),
            "homeButton.Store=" + ButtonStatus(box?.m_StoreButton, pressDisabled),
            "homeButton.Journal=present=" + (journal != null) + "|shown=" + (journal != null && journal.m_isButtonShown)
                + "|active=" + (journal != null && journal.gameObject.activeInHierarchy),
            "homeButton.LegacyJournal=" + ButtonStatus(box?.m_QuestLogButton, pressDisabled),
            "achieves=" + Field(manager, "m_achievesLoaded"), "netCache=" + Field(manager, "m_netCacheLoaded"), "collection=" + Field(manager, "m_collectionLoaded"),
            "collectionDisplay=" + (manager?.GetCollectibleDisplay() != null),
            "collectionDisplayReady=" + (manager?.GetCollectibleDisplay()?.IsReady() ?? false),
            "pendingCache=" + PendingCache(),
            "projectedDecks=" + string.Join(",", decks.Keys), "selected=" + selected, "playBound=" + (boundButton != null),
            "personalDeckIds=" + string.Join(",", personal.Select(deck => deck.Id)),
            "personalDeckNames=" + string.Join("|", personal.Select(deck => deck.Name)),
            "personalDeckCounts=" + string.Join(",", personal.Select(deck => deck.CardCount)),
            "personalDeckValid=" + string.Join(",", personal.Select(deck => deck.CardCount == deck.DeckSize)),
            "editedDraftId=" + editedDraftId, "lastDeckOperation=" + lastDeckOperation,
            "lastDeckError=" + (lastDeckError ?? "").Replace('\r', ' ').Replace('\n', ' '),
            "profilePacks=" + (active ? Profile.UnopenedPacks : 0),
            "profilePackSequence=" + (active ? Profile.PackSequence : 0),
            "profilePackPending=" + (active && Profile.PackRevealPending),
            "projectedPackCount=" + (active ? NativeUIProjection.ProjectedPackCount(Profile.UnopenedPacks, Profile.PendingPacks.Count, replayRunning) : 0),
            "receiptSequence=" + receiptSequence, "receipt=" + string.Join(",", receipt ?? new string[0]),
            "replayArmed=" + replayArmed, "replayRunning=" + replayRunning, "doneAcknowledged=" + doneAcknowledged,
            "packTransaction=" + transactionFacts, "liveGrantProof=" + liveGrantProof,
            "rewardPresentation=" + NativeRewardUI.Diagnostics,
            "resultsPresentation=" + NativeResultsUI.Diagnostics,
            "fixtureHash=" + baselineHash, "packStartHash=" + packStartHash, "packCommitHash=" + packCommitHash,
            "currentHash=" + (active ? StatusHash() : ""),
            "failure=" + failure.Replace('\r',' ').Replace('\n',' ')
        });
#endif
    }
}
