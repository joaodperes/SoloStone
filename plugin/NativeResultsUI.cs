using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;
using MiniJSON;
using UnityEngine;
using PegasusShared;
using Assets;
using Hearthstone.Progression;

internal static class NativeResultsUI
{
    private const string RankPrefab = "RankChangeTwoScoop_NEW.prefab:606c949d2ac1a8643a5ab70f4d8f67e6";
    private static readonly LocalResultsFlow flow = new LocalResultsFlow(clock: () => Time.realtimeSinceStartup, log: TierFailed);
    private static readonly LocalResultsScreenState screenState = new LocalResultsScreenState();
    private sealed class RankArrayOwner { internal string Match; internal int Generation; }
    // Asset callbacks retain managed owners after Unity destruction; weak reference identity keeps those callbacks scoped.
    private static readonly ConditionalWeakTable<RankedStarArray, RankArrayOwner> rankArrays = new ConditionalWeakTable<RankedStarArray, RankArrayOwner>();
    private static bool instantiatingRank;
    private static ManualLogSource log;
    internal static NativePremiumRewards PremiumRewards { get; private set; }
    private static EndGameScreen screen;
    private static string screenMatch, rankChange = "none", failure = "";
    private static RankChangeTwoScoop_NEW rank;
    private static Camera inputCamera;
    private static bool cameraAdded, rankRequested, rankShown, rankClickable, rankClosing, scoopRetired;
    private static float phaseBegan, rankCloseBegan;
    private static LocalResultsPhase observedPhase;
    private static readonly LocalTierRewardsOnly tierOnly = new LocalTierRewardsOnly(() => Time.realtimeSinceStartup, TierFailed);
    private static readonly Dictionary<string,int> forbidden = new Dictionary<string,int> {
        ["AchievementManager"] = 0, ["RewardTrackManager"] = 0, ["QuestManager"] = 0, ["NetCache"] = 0, ["TelemetryManager"] = 0, ["FixedRewardsMgr"] = 0 };
    private static readonly HashSet<string> forbiddenLogged = new HashSet<string>();

    internal static LocalResultsReceipt Receipt => flow.Receipt;
    internal static JsonList MasteryProgressState()
    {
        var profile = NativeUIProbe.RewardProfile;
        var history = profile?.History;
        var match = history?.FirstOrDefault(row => row.Id == Receipt?.MatchId);
        return TestControlProtocol.List(match == null ? Enumerable.Empty<object>()
            : LocalCardMastery.Next(history, match, profile.ProgressionCards, profile.GrindTier)
                .Select(row => (object)new JsonNode { { "cardId", row.CardId }, { "premium", row.Premium },
                    { "wins", row.Wins }, { "requiredWins", row.RequiredWins }, { "remainingWins", row.RequiredWins - row.Wins },
                    { "pace", profile.GrindTier.Id }, { "text", row.Wins + " / " + row.RequiredWins + " wins" } }));
    }

    internal static bool RoutingReleased => flow.Receipt?.MatchId == NativeUIProbe.RewardProfile?.PendingRewardPresentation?.Id && flow.RoutingReleased;
    // Boxes run in the finished GAMEPLAY scene; HUB routing remains only a safety net once that scene stops owning the match.
    internal static bool GameplayPresentation => (flow.BoxesInGameplay || flow.KeepsAchievementInGameplay) && ClientBridge.OwnsLocalPostMatch;
    internal static bool PresentsInGameplay => GameplayPresentation && NativeUIProbe.GameplayRewardContextReady
        && flow.Receipt?.MatchId == NativeUIProbe.RewardProfile?.PendingRewardPresentation?.Id;
    internal static bool CanAcknowledge(string match) => flow.Receipt?.MatchId == match && flow.CanAcknowledge;
    internal static bool BlocksEscape => tierOnly.BlocksEscape(flow.BlocksEscape && flow.Receipt?.MatchId == NativeUIProbe.RewardProfile?.PendingRewardPresentation?.Id
        && (ClientBridge.OwnsLocalPostMatch || NativeUIProbe.RewardContextReady));
    internal static bool CanStart => tierOnly.CanStart(NativeUIProbe.RewardProfile?.PendingRewardPresentation != null);
    internal static bool CanNavigate(bool home) => tierOnly.CanNavigate(NativeUIProbe.RewardProfile?.PendingRewardPresentation != null, home);
    internal static ILocalTierRewardStep TierRewardStep
    {
        get => flow.TierStep;
        set => flow.TierStep = value ?? throw new ArgumentNullException(nameof(value));
    }
    internal static ILocalAchievementStep AchievementStep
    {
        get => flow.AchievementStep;
        set => flow.AchievementStep = value ?? throw new ArgumentNullException(nameof(value));
    }
    internal static ILocalPetStep PetStep
    {
        get => flow.PetStep;
        set => flow.PetStep = value ?? throw new ArgumentNullException(nameof(value));
    }
    // Results own the finished scene (any phase before the acknowledged route); used to keep scrolls from deep-linking out.
    internal static bool ResultsPresenting => Active;
    internal static bool PetMomentActive => flow.Receipt != null && flow.Phase == LocalResultsPhase.PetMoment;
    internal static void PresentTierRewardsOnly(LocalResultsReceipt receipt, Action done) => tierOnly.Enqueue(receipt, done);
    internal static bool BoxesReady => flow.Phase == LocalResultsPhase.Boxes;
    internal static string Diagnostics => "phase=" + flow.Phase + ";match=" + flow.Receipt?.MatchId + ";generation=" + flow.Generation
        + ";rankChange=" + rankChange + ";xp=" + flow.Receipt?.ClassXpBefore + ":" + flow.Receipt?.ClassXpAfter
        + ";routeReleases=" + flow.RouteReleaseCount + ";pet=[" + NativePetMoment.Diagnostics + "];boxesInGameplay=" + flow.BoxesInGameplay + ";failure=" + failure + ";forbidden="
        + string.Join(",", forbidden.Select(p => p.Key + ":" + p.Value));
    internal static EndGameScreen ControllerOwner => screen;
    internal static string ControllerContext => flow.Receipt?.MatchId + ":" + flow.Generation + ":" + flow.Phase + ":" + rankClickRevision;
    internal static bool ControllerReady => flow.Phase == LocalResultsPhase.Celebration ? CelebrationClickable : flow.Phase == LocalResultsPhase.Rank && rankClickable;
    internal static bool OwnsScreen(EndGameScreen value) => Owned(value);
    internal static bool OwnsInput(PegUIElement element) => (screen != null && Owned(EndGameScreen.Get())
        && ReferenceEquals(element, screen.m_hitbox)) || (element != null && ReferenceEquals(element, NativeTierRewards.ClickTarget))
        || NativePetMoment.OwnsScrollInput(element) || PremiumRewards?.OwnsInput(element) == true;
    private static bool Owned(EndGameScreen value) => !ReferenceEquals(value, null) && ReferenceEquals(value, screen);
    private static bool Active => flow.Receipt != null && flow.Phase >= LocalResultsPhase.AwaitNativeScreen && flow.Phase <= LocalResultsPhase.Boxes;
    private static object Get(object value, string name) => AccessTools.Field(value.GetType(), name).GetValue(value);
    private static void Set(object value, string name, object item) => AccessTools.Field(value.GetType(), name).SetValue(value, item);
    private static void Call(object value, string name) => AccessTools.Method(value.GetType(), name, Type.EmptyTypes).Invoke(value, null);
    private static void Patch(Harmony harmony, Type type, string method, string hook, bool postfix = false)
    {
        MethodInfo target = AccessTools.DeclaredMethod(type, method) ?? throw new MissingMethodException(type.FullName, method);
        var handler = new HarmonyMethod(typeof(NativeResultsUI), hook);
        harmony.Patch(target, prefix: postfix ? null : handler, postfix: postfix ? handler : null);
    }

    internal static void Initialize(Harmony harmony, ManualLogSource logger)
    {
        log = logger;
        flow.PremiumStep = PremiumRewards = new NativePremiumRewards(logger);
        Patch(harmony, typeof(EndGameScreen), "Awake", nameof(BeforeAwake));
        Patch(harmony, typeof(EndGameScreen), "OnDestroy", nameof(BeforeDestroy));
        Patch(harmony, typeof(EndGameScreen), "ShouldMakeUtilRequests", nameof(FalseForOwned));
        Patch(harmony, typeof(EndGameScreen), "SendTelemetryIfTimeout", nameof(BeforeTelemetryTimeout));
        Patch(harmony, typeof(EndGameScreen), "ShowBmecRewards", nameof(FalseForOwned));
        Patch(harmony, typeof(EndGameScreen), "ShowJournalGameOnlyXpGains", nameof(SkipForOwned));
        Patch(harmony, typeof(VictoryScreen), "JustEarnedHeroReward", nameof(FalseForOwned));
        Patch(harmony, typeof(EndGameScreen), "ContinueEvents", nameof(BeforeContinue));
        Patch(harmony, typeof(EndGameTwoScoop), "Show", nameof(BeforeScoopShow));
        Patch(harmony, typeof(EndGameTwoScoop), "OnAllHidden", nameof(AfterHidden), true);
        Patch(harmony, typeof(RankChangeTwoScoop_NEW), "OnShown", nameof(AfterRankShown), true);
        Patch(harmony, typeof(RankChangeTwoScoop_NEW), "EnableClickToContinue", nameof(AfterRankClickable), true);
        Patch(harmony, typeof(RankChangeTwoScoop_NEW), "Hide", nameof(AfterRankHide), true);
        Patch(harmony, typeof(RankChangeTwoScoop_NEW), "OnClick", nameof(AfterRankClick), true);
        Patch(harmony, typeof(RankedStarArray), "Awake", nameof(BeforeRankArrayAwake));
        Patch(harmony, typeof(RankedStarArray), "OnStarLoaded", nameof(BeforeRankStarLoaded));
        Patch(harmony, typeof(RewardUtils), "GetNextHeroLevelRewardText", nameof(BeforeHeroRewardText));
        // Observe service calls without enabling any of the service-heavy native results paths.
        foreach (Type type in new[] { typeof(AchievementManager), typeof(RewardTrackManager), typeof(QuestManager), typeof(NetCache), typeof(FixedRewardsMgr) })
        foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsAbstract && !m.ContainsGenericParameters && m.GetMethodBody() != null
                && (type == typeof(NetCache) ? m.Name.Contains("Request") || m.Name == "RegisterScreenEndOfGame" || m.Name == "NetCacheReload_Internal"
                    : type == typeof(FixedRewardsMgr) ? LocalResultsFlow.CountFixedRewardsCall(m.Name)
                    : m.Name.StartsWith("Claim") || m.Name.StartsWith("Ack") || m.Name.StartsWith("Show") || m.Name.StartsWith("Request"))))
        {
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(NativeResultsUI), nameof(ObserveForbidden)));
            if (type == typeof(NetCache) && LocalResultsFlow.BlockNetCacheRequest(method.Name))
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(NativeResultsUI), nameof(BeforeCacheRequest)) { priority = Priority.First });
            // Only observes: this filter patches whatever exists, so a method retail removes or renames costs nothing. The blocked requests above are required V1 rows.
            else ClientChecks.NoteSelfAdapting(method);
        }
    }

    private static bool BeforeCacheRequest() => !Active;

    // Void observation still runs when another prefix suppresses the original request.
    private static void ObserveForbidden(MethodBase __originalMethod)
    {
        if (!Active) return;
        var frames = new System.Diagnostics.StackTrace().GetFrames() ?? Array.Empty<System.Diagnostics.StackFrame>();
        string service = __originalMethod.DeclaringType.Name, method = __originalMethod.Name;
        bool published = frames.Any(frame => frame.GetMethod()?.DeclaringType == typeof(NativeUIProbe)
            && frame.GetMethod().Name == "Publish");
        // Cache receipt/check bookkeeping runs synchronously inside the allowed projection publisher.
        if (service == "NetCache" && !LocalResultsFlow.CountNetCacheCall(method, published))
        {
            if (forbiddenLogged.Add("publish:" + method))
                log?.LogInfo("NATIVE_RESULTS_CACHE_PUBLISH match=" + flow.Receipt.MatchId + ";method=" + method + ";allowed projection bookkeeping");
            return;
        }
        forbidden[service]++;
        string entry = __originalMethod.ToString();
        if (forbiddenLogged.Add(service + "." + entry))
            log?.LogWarning("NATIVE_RESULTS_FORBIDDEN match=" + flow.Receipt.MatchId + ";method=" + service + "." + entry
                + ";stack=" + string.Join(" <- ", frames.Skip(1).Take(12).Select(frame => {
                    var caller = frame.GetMethod(); return caller?.DeclaringType?.Name + "." + caller?.Name;
                })));
    }

    private static bool BeforeHeroRewardText(int __1, ref string __3, ref string __4, ref bool __result)
    {
        if (!Active) return true;
        __3 = LocalResultsXp.NextCoreRewardText(flow.Receipt.HeroClass, __1);
        __4 = ""; __result = __3.Length > 0;
        return false;
    }

    private static void ObserveTerminal()
    {
        if (screenState.ObserveTerminal(ClientBridge.DiagnosticMatchId, Time.realtimeSinceStartup)) { ResetForbidden(); terminalAt = Time.realtimeSinceStartup; }
    }

    // Feature A: the friendly pet entity's win/lose spell runs before native Show; allow it more time before falling back.
    private static float terminalAt;
    private static bool PetEndgamePending()
    {
        try { return GameState.Get()?.GetFriendlySidePlayer()?.GetPetCard() != null; }
        catch { return false; }
    }
    private static float ShowLimit => LocalResultsScreenState.Limit(PetEndgamePending());

    private static void ResetForbidden()
    {
        foreach (string key in forbidden.Keys.ToArray()) forbidden[key] = 0;
        forbiddenLogged.Clear();
    }

    private static bool BeforeAwake(EndGameScreen __instance)
    {
        if (!ClientBridge.OwnsLocalPostMatch) return true;
        screen = __instance; screenMatch = ClientBridge.DiagnosticMatchId;
        ObserveTerminal();
        rankRequested = rankShown = rankClickable = rankClosing = scoopRetired = false;
        try
        {
            EnsureReceipt(false);
            Set(__instance, "s_instance", __instance);
            if (__instance.m_transformOverride != null) __instance.m_transformOverride.enabled = true;
            if (__instance.m_petTransformOverride != null) __instance.m_petTransformOverride.enabled = false;
            foreach (string ready in new[] { "m_netCacheReady", "m_achievesReady", "m_rewardsLoaded" }) Set(__instance, ready, true);
            foreach (string disabled in new[] { "m_shouldShowRankChange", "m_shouldShowRankedCardBackProgress", "m_shouldShowRewardXpGains" }) Set(__instance, disabled, false);
            __instance.m_hitbox.gameObject.SetActive(false);
            object continueText = Get(__instance, "m_continueText");
            AccessTools.Property(continueText.GetType(), "Text").SetValue(continueText, GameStrings.Get("GLOBAL_CLICK_TO_CONTINUE"), null);
            ((Component)continueText).gameObject.SetActive(false);
            ((Component)Get(__instance, "m_noGoldRewardText")).gameObject.SetActive(false);
            inputCamera = CameraUtils.FindFirstByLayerMask(1 << 19);
            // Balance only our registration; existing scene registrations remain scene-owned.
            cameraAdded = inputCamera != null && !((System.Collections.IList)Get(PegUI.Get(), "m_UICams")).Contains(inputCamera);
            if (cameraAdded) PegUI.Get().AddInputCamera(inputCamera);
            SetLayer(__instance.m_hitbox.gameObject);
            SetLayer(((Component)continueText).gameObject);
            Set(__instance, "m_screenEffectsHandle", new ScreenEffectsHandle(__instance));
        }
        catch (Exception error) { Failed(error); }
        return false;
    }

    private static bool FalseForOwned(EndGameScreen __instance, ref bool __result)
    { if (!Owned(__instance)) return true; __result = false; return false; }
    private static bool SkipForOwned(EndGameScreen __instance) => !Owned(__instance);
    private static bool BeforeTelemetryTimeout(EndGameScreen __instance, string __0, ref bool __result,
        ref float ___m_timeoutTimerStartTime, ref bool ___m_hasTimedOutAndLogged)
    {
        if (!Owned(__instance)) return true;
        __result = LocalResultsFlow.WaitForNativeReady(Time.time, ref ___m_timeoutTimerStartTime, ref ___m_hasTimedOutAndLogged, () => {
            forbidden["TelemetryManager"]++;
            log?.LogWarning("NATIVE_RESULTS_READY_TIMEOUT culprit=" + __0 + ";telemetry suppressed locally");
        });
        return false;
    }
    internal static bool AllowShow(EndGameScreen value)
    {
        if (!Owned(value)) return false;
        screenState.Show(screenMatch);
        try
        {
            log?.LogInfo("NATIVE_RESULTS_SHOW match=" + screenMatch + ";sinceTerminal=" + (Time.realtimeSinceStartup - terminalAt).ToString("0.0")
                + ";petCard=" + PetEndgamePending() + ";hasPetEndgame=" + (GameState.Get()?.HasPetEndgame() == true));
        }
        catch (Exception error) { log?.LogWarning("NATIVE_RESULTS_SHOW_DIAGNOSTIC " + error.Message); }
        EnsureReceipt(false);
        return screenState.CanShow(screenMatch) && flow.Receipt?.MatchId == screenMatch && flow.Phase == LocalResultsPhase.AwaitNativeScreen;
    }

    private static void EnsureReceipt(bool inHub)
    {
        LocalProfile profile = NativeUIProbe.RewardProfile;
        LocalMatchRecord match = profile?.PendingRewardPresentation;
        if (tierOnly.Active || match == null || flow.Receipt?.MatchId == match.Id) return;
        // A pending receipt without its owned gameplay scene is a restart: it waits for a settled HUB.
        if (!LocalResultsFlow.CanBeginPending(ClientBridge.OwnsLocalPostMatch, inHub)) return;
        LocalResultsReceipt receipt = LocalResultsReceipt.FromHistory(profile.History, match, profile.ProgressionCards, profile.GrindTier);
        // Settlement already moved the pet ledger. Publish it without the XP event now, so no later cache diff can
        // queue a second bar or scroll; the pet moment fires the one event itself from this receipt.
        // If this throws, the next refresh of any kind stays silent (PendingSilentPets) instead of re-firing the event.
        NativeUIProbe.PendingSilentPets = true;
        try { NativeUIProbe.RefreshPets(false); }
        catch (Exception error) { log?.LogWarning("NATIVE_RESULTS_PET_CACHE " + error.Message); }
        flow.Begin(receipt, inHub || !ClientBridge.OwnsLocalPostMatch);
        phaseBegan = Time.realtimeSinceStartup; observedPhase = flow.Phase;
        rankRequested = rankShown = rankClickable = rankClosing = scoopRetired = false; failure = ""; rankChange = "none";
        if (screenState.MatchId != match.Id)
            ResetForbidden();
        NativeUIProbe.PublishResultsMedal();
    }

    private static void BeforeScoopShow(EndGameTwoScoop __instance, ref bool showXPBar)
    {
        if (screen == null || __instance != screen.m_twoScoop) return;
        showXPBar = false;
        if (flow.Phase != LocalResultsPhase.AwaitNativeScreen) return;
        try { showXPBar = NativeUIProbe.PublishResultsHeroLevels(flow.Receipt, flow); }
        catch (Exception error) { log.LogWarning("NATIVE_RESULTS_XP " + error.Message); }
        flow.Complete(screenMatch, flow.Generation, LocalResultsPhase.AwaitNativeScreen);
    }

    private static bool BeforeContinue(EndGameScreen __instance, ref bool __result)
    {
        if (!Owned(__instance)) return true;
        __result = true;
        if (flow.Phase == LocalResultsPhase.PetMoment) { flow.PetContinueClicked(); return false; }
        if (flow.Phase != LocalResultsPhase.Celebration || !CelebrationClickable) return false;
        try
        {
            flow.Complete(screenMatch, flow.Generation, LocalResultsPhase.Celebration);
            if (__instance.m_twoScoop is VictoryTwoScoop victory) victory.StopFireworksAudio();
            Call(__instance, "HideTwoScoop");
        }
        catch (Exception error) { Failed(error); }
        return false;
    }
    private static bool CelebrationClickable => screen != null && !screen.IsPlayingBlockingAnim()
        && (int)Get(screen, "m_inputBlocker") == 0 && (bool)Get(screen, "m_haveShownTwoScoop") && !(bool)Get(screen, "m_showingScoreScreen");
    private static void AfterHidden(EndGameTwoScoop __instance)
    {
        if (screen == null || __instance != screen.m_twoScoop) return;
        try { flow.Complete(screenMatch, flow.Generation, LocalResultsPhase.HidingScoop); }
        catch (Exception error) { Failed(error); }
    }

    private static void StartRank()
    {
        rankRequested = true;
        string match = flow.Receipt.MatchId; int generation = flow.Generation;
        try
        {
            var r = flow.Receipt;
            var league = NativeUIProbe.ResolveMedalLeague(r.Position(false), out _);
            int top = league.Ranks.Max(row => row.StarLevel);
            var rows = league.Ranks.Select(row => new LadderRankRow {
                StarLevel = row.StarLevel, Stars = row.Stars, Legend = row.StarLevel == top }).ToArray();
            var before = NativeUIProbe.ProjectMedal(LadderRules.Medal(r.Position(false), league.ID, rows, r.ArrivedByWinBefore, r.Pace.Ladder),
                r.Ladder.RatingBefore, r.SeasonGamesBefore, r.SeasonWinsBefore);
            var after = NativeUIProbe.ProjectMedal(LadderRules.Medal(r.Position(true), league.ID, rows, r.ArrivedByWinAfter, r.Pace.Ladder),
                r.Ladder.RatingAfter, r.SeasonGamesAfter, r.SeasonWinsAfter);
            var translator = new MedalInfoTranslator(after, before);
            rankChange = translator.GetChangeType(NativeUIProbe.LocalDeckFormat).ToString();
            NativeRewardUI.LoadRaw(RankPrefab, raw => {
                if (!flow.IsCurrent(match, generation, LocalResultsPhase.Rank)) return;
                GameObject instance = null;
                try
                {
                    if (raw == null || screen == null) throw new InvalidOperationException("Rank prefab or end-game hitbox missing.");
                    instantiatingRank = true;
                    try { instance = UnityEngine.Object.Instantiate(raw); }
                    finally { instantiatingRank = false; }
                    foreach (var array in instance.GetComponentsInChildren<RankedStarArray>(true)) TrackRankArray(array);
                    rank = instance.GetComponent<RankChangeTwoScoop_NEW>() ?? throw new InvalidOperationException("Rank component missing.");
                    rank.Initialize(translator, NativeUIProbe.LocalDeckFormat, () => {
                        if (!flow.IsCurrent(match, generation, LocalResultsPhase.Rank)) return;
                        rankClickable = false; rank = null;
                        flow.Complete(match, generation, LocalResultsPhase.Rank);
                    });
                    rank.Show();
                }
                catch (Exception error) { if (instance != null) UnityEngine.Object.Destroy(instance); rank = null; Failed(error); }
            });
        }
        catch (Exception error) { Failed(error); }
    }
    private static void AfterRankShown(RankChangeTwoScoop_NEW __instance) { if (__instance == rank) rankShown = true; }
    private static void AfterRankClickable(RankChangeTwoScoop_NEW __instance) { if (__instance == rank) rankClickable = true; }
    private static void AfterRankHide(RankChangeTwoScoop_NEW __instance)
    { if (__instance == rank) { rankClickable = false; rankClosing = true; rankCloseBegan = Time.realtimeSinceStartup; } }
    private static void AfterRankClick(RankChangeTwoScoop_NEW __instance)
    {
        // A native cancel click can retain the listener; expose that accepted state change to the live driver.
        if (__instance == rank) rankClickRevision++;
    }
    private static int rankClickRevision;

    private static void BeforeRankArrayAwake(RankedStarArray __instance)
    {
        if (instantiatingRank && __instance.GetComponentInParent<RankChangeTwoScoop_NEW>() != null) TrackRankArray(__instance);
    }
    private static void TrackRankArray(RankedStarArray array) => rankArrays.GetValue(array,
        _ => new RankArrayOwner { Match = flow.Receipt.MatchId, Generation = flow.Generation });
    private static bool BeforeRankStarLoaded(RankedStarArray __instance, GameObject __1)
    {
        if (!rankArrays.TryGetValue(__instance, out var owner)) return true;
        return flow.AcceptRankChild(owner.Match, owner.Generation, __instance != null, () => {
            if (__1 != null) UnityEngine.Object.Destroy(__1);
        });
    }

    private static void SendFsmEvent(GameObject target, string name)
    {
        foreach (var fsm in target.GetComponents<MonoBehaviour>().Where(c => c != null && c.GetType().Name == "PlayMakerFSM"))
            AccessTools.Method(fsm.GetType(), "SendEvent", new[] { typeof(string) }).Invoke(fsm, new object[] { name });
    }
    private static void SetLayer(GameObject root)
    { foreach (Transform child in root.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = 19; }

    internal static void Update()
    {
        try
        {
            bool hub = NativeUIProbe.RewardContextReady;
            if (screen == null || screen.m_twoScoop == null || !ClientBridge.OwnsLocalPostMatch)
                flow.RetireScoop();
            if (ClientBridge.OwnsLocalPostMatch)
            {
                ObserveTerminal();
                if (screenState.CheckTimeout(Time.realtimeSinceStartup, ShowLimit)) Failed(new TimeoutException("Native end-game Show missing."));
            }
            EnsureReceipt(hub);
            tierOnly.Update(!hub || NativeUIProbe.RewardProfile?.PendingRewardPresentation != null
                || Active || NativeRewardUI.Active || ClientBridge.OwnsLocalPostMatch,
                NativeUIProbe.RewardProfile?.PackRevealPending == true, flow.TierStep, NativeRewardUI.ShowTierFallback);
            flow.Update();
            NativePetMoment.Update();
            PremiumRewards?.Update();
            SyncPetPrompt();
            if (flow.Receipt == null) return;
            if (flow.Phase == LocalResultsPhase.AwaitNativeScreen && screenState.MatchId == flow.Receipt.MatchId && screenState.Fallback)
                Failed(new TimeoutException("Native end-game screen retired before settlement saved."));
            if (observedPhase != flow.Phase) { observedPhase = flow.Phase; phaseBegan = Time.realtimeSinceStartup; }
            float elapsed = Time.realtimeSinceStartup - phaseBegan;
            if (flow.Phase == LocalResultsPhase.AwaitNativeScreen && flow.Receipt.MatchId == screenMatch
                && screenState.CanShow(screenMatch) && screen != null && !(bool)Get(screen,"m_shown")) screen.Show();
            if (flow.Phase == LocalResultsPhase.Rank && !rankRequested) StartRank();
            if (flow.Phase == LocalResultsPhase.RouteHome)
            {
                if (!scoopRetired && screen != null && screen.m_twoScoop != null)
                { scoopRetired = true; SendFsmEvent(screen.m_twoScoop.gameObject, "Death"); }
                // Retail pacing: rewards follow rank and class XP in the same scene; HUB only when that scene is gone.
                bool gameplay = !hub && NativeUIProbe.GameplayRewardContextReady;
                bool achievementHub = hub && (!flow.Receipt.HasAchievements
                    || NativeUIProbe.MatchAchievementToastContextReady(NativeUIProbe.RewardProfile, flow.Receipt.MatchId));
                if (achievementHub || gameplay) flow.EnterBoxes(flow.Receipt.MatchId, flow.Generation, gameplay);
            }
            // Timeouts cover stalled native work, never a normal wait for the player's click.
            if ((flow.Phase == LocalResultsPhase.AwaitNativeScreen && elapsed > ShowLimit)
                || (flow.Phase == LocalResultsPhase.Celebration && !CelebrationClickable && elapsed > 45f)
                || (flow.Phase == LocalResultsPhase.HidingScoop && elapsed > 10f)
                || (flow.Phase == LocalResultsPhase.Rank && ((!rankShown && elapsed > 45f)
                    || (rankShown && rank == null) || (rankClosing && Time.realtimeSinceStartup - rankCloseBegan > 10f)))) Failed(new TimeoutException("Native results phase " + flow.Phase));
        }
        catch (Exception error) { Failed(error); }
    }

    // While the pet bar plays the end-game hitbox is its only exit (native skip), so keep it live and show the native
    // "click to continue" text the results flow normally hides; scrolls bring their own controls.
    private static bool petPromptShown;
    private static void SyncPetPrompt()
    {
        bool want = screen != null && flow.Phase == LocalResultsPhase.PetMoment && NativePetMoment.BarStage && NativePetMoment.Skippable;
        if (want == petPromptShown) return;
        petPromptShown = want;
        try
        {
            if (screen == null) return;
            if (want && screen.m_hitbox != null) screen.m_hitbox.gameObject.SetActive(true);
            if (Get(screen, "m_continueText") is Component text && text != null) text.gameObject.SetActive(want);
        }
        catch (Exception error) { log?.LogWarning("NATIVE_RESULTS_PET_PROMPT " + error.Message); }
    }

    private static void TierFailed(Exception error)
    {
        failure = error.GetType().Name + ": " + error.Message;
        log?.LogWarning("NATIVE_TIER_REWARDS_FALLBACK " + failure);
    }

    private static void Failed(Exception error)
    {
        failure = error.GetType().Name + ": " + error.Message; log?.LogWarning("NATIVE_RESULTS_FALLBACK " + failure);
        // Expose settlement retry even when no receipt or native Show exists. Routing still requires a receipt and flow release.
        if (ClientBridge.OwnsLocalPostMatch)
        {
            screenState.Retire(ClientBridge.DiagnosticMatchId);
            LocalLadder.NativeFinished();
        }
        try
        {
            if (rank != null) UnityEngine.Object.Destroy(rank.gameObject);
            rank = null; rankClickable = false;
            if (screen != null)
            {
                screen.StopAllCoroutines();
                if (screen.m_twoScoop is VictoryTwoScoop victory) victory.StopFireworksAudio();
                screen.m_twoScoop?.gameObject.SetActive(false);
            }
        }
        catch (Exception cleanup) { log?.LogWarning("NATIVE_RESULTS_CLEANUP " + cleanup.Message); }
        finally
        {
            try
            {
                flow.RetireScoop();
                if (flow.Receipt != null) flow.Fail(flow.Receipt.MatchId, flow.Generation, flow.Phase);
            }
            catch (Exception cleanup) { log?.LogWarning("NATIVE_RESULTS_CLEANUP " + cleanup.Message); }
        }
    }

    private static bool BeforeDestroy(EndGameScreen __instance)
    {
        if (!Owned(__instance)) return true;
        try
        {
            flow.RetireScoop();
            if (__instance.m_twoScoop is VictoryTwoScoop victory) victory.StopFireworksAudio();
            (Get(__instance, "m_screenEffectsHandle") as ScreenEffectsHandle)?.StopEffect();
            EndGameScreen.OnTwoScoopsShown?.Invoke(false, __instance.m_twoScoop);
            if (cameraAdded && inputCamera != null && PegUI.Get() != null) PegUI.Get().RemoveInputCamera(inputCamera);
            if (EndGameScreen.Get() == __instance) Set(__instance, "s_instance", null);
            if (flow.Phase >= LocalResultsPhase.AwaitNativeScreen && flow.Phase < LocalResultsPhase.RouteHome) Failed(new InvalidOperationException("End-game screen destroyed early."));
        }
        catch (Exception error) { Failed(error); }
        screen = null; cameraAdded = false; inputCamera = null;
        return false;
    }

    internal static void BoxesCompleted()
    { if (flow.Receipt != null) flow.Complete(flow.Receipt.MatchId, flow.Generation, LocalResultsPhase.Boxes); }
    internal static void PresentNoItems(Func<Action, bool> showPopup, Action continued) => flow.PresentNoItems(showPopup, continued);
    internal static void Acknowledged()
    {
        if (flow.Receipt == null || !flow.Acknowledge(flow.Receipt.MatchId, flow.Generation)) return;
        flow.Complete(flow.Receipt.MatchId, flow.Generation, LocalResultsPhase.Acknowledged);
    }

    internal static void RouteCompleted(string match)
    { flow.Complete(match, flow.Generation, LocalResultsPhase.RouteChoice); }

    // Reuse the native hitbox. Test control reports native eligibility, never bypasses it.
    internal static void FilterTestUi(UiSnapshot snapshot)
    {
        // The ranked reward chest listens to the end-game hitbox (or its own catcher after a restart): NativeUiTree names it.
        UiElement tier = snapshot.Find(LocalResultsFlow.TierControlId);
        if (tier != null)
        {
            tier.Enabled &= NativeTierRewards.ClickReady;
            tier.Label = "Tier continue";
            tier.Tweening = false;
        }
        UiElement element = snapshot.Find("endgame.continue");
        if (element != null && screen != null && Owned(EndGameScreen.Get()))
        {
            bool celebration = flow.Phase == LocalResultsPhase.Celebration, pet = flow.Phase == LocalResultsPhase.PetMoment;
            element.Id = celebration ? "results.celebration" : pet ? "results.pet" : "results.rank";
            element.Enabled &= celebration ? CelebrationClickable : pet ? NativePetMoment.Skippable : flow.Phase == LocalResultsPhase.Rank && rankClickable;
            element.Label = celebration ? "Continue" : pet ? "Pet skip" : "Rank continue " + rankClickRevision;
            // Ignore perpetual decoration tweens; only native click gates determine input readiness here.
            element.Tweening = false;
        }
        else element = null;
        if (tier != null || element != null) UiRules.Finish(snapshot);
    }

}
