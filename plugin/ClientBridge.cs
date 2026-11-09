using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using BepInEx.Logging;
using Blizzard.T5.Jobs;
using Blizzard.T5.Core.Utils;
using HarmonyLib;
using Hearthstone.Core;
using Networking;
using PegasusShared;
using UnityEngine;
using SabberStoneCore.Tasks.SimpleTasks;
using SabberStoneCore.Auras;

internal static class ClientBridge
{
    private static readonly string[] RequiredPaladinDefinitions = {
        "HERO_04", "HERO_04bp", "CS2_101t",
        "CS2_087", "CS2_088", "CS2_089", "CS2_091", "CS2_092", "CS2_093", "CS2_094", "CS2_097",
        "EX1_360", "EX1_371", "EX1_354", "EX1_355", "EX1_362", "EX1_366", "EX1_382", "EX1_619",
        "CS2_087e", "CS2_092e", "EX1_360e", "EX1_355e", "EX1_366e", "EX1_382e", "EX1_619e"
    };
    private static ManualLogSource log;
    private static OfflineLaunchContext launch;
    private static bool boardsLogged;
    private static bool boardLoadRetried;
    private static bool staged, relaunchAbandoned;
    private static bool requested;
    private static bool localConnected;
    private static float stageTime;
    private static NativeMatch match;
    private static string runId, matchId;
    private static PlaytestRecord playtestRecord;
    internal static bool OwnsLiveMatch => staged && requested && localConnected && match?.Game != null
        && SceneMgr.IsInitialized() && SceneMgr.Get()?.GetMode() == SceneMgr.Mode.GAMEPLAY;
    internal static string DiagnosticMatchId => matchId;
    internal static string DiagnosticProfilePath { get; private set; }
    internal static bool MatchJournalEnabled => playtestRecord?.Enabled == true;
    internal static void RecordFx(Dictionary<string, object> row) => playtestRecord?.OnFx(row);
    internal static void FlushMatchJournal() => playtestRecord?.Flush();
    internal static void CloseMatchJournal() => playtestRecord?.Dispose();
    // Test control only: see NativeMatch.ConcedeNpcForTest.
    internal static void ConcedeNpcForTest() => (match ?? throw new InvalidOperationException("No local match.")).ConcedeNpcForTest();
    internal static void PrimeAchievementForTest(string id)
    {
        var profile = LocalLadder.Profile;
        if (profile?.Pending?.AchievementContext == null || profile.Pending.DebugScenario
            || profile.Achievements.Unlocks.Any(row => row.Id == id))
            throw new InvalidOperationException("Achievement fixture requires an eligible match and a locked achievement.");
        (match ?? throw new InvalidOperationException("No local match.")).PrimeAchievementForTest(id);
    }
    internal static void ShortenPlayerTurnForTest(int seconds) => (match ?? throw new InvalidOperationException("No local match.")).ShortenPlayerTurnForTest(seconds);
    internal static void LoseForTest() => (match ?? throw new InvalidOperationException("No local match.")).LoseForTest();
    internal static string DiagnosticRunId => runId;
    internal static bool OwnsLocalPostMatch => staged && match?.Game?.State == SabberStoneCore.Enums.State.COMPLETE
        && matchId == LocalLadder.MatchId && SceneMgr.IsInitialized() && SceneMgr.Get().GetMode() == SceneMgr.Mode.GAMEPLAY;
    private static bool ready, npcGameplaySeen, npcFailed;
    private static readonly System.Reflection.FieldInfo NpcDelayedTasks = AccessTools.Field(typeof(PowerProcessor), "m_delayedRealTimeTasks");
    public static Action StartMatch;
    public static Action<int, int, int, int, int> Option;
    public static Action<int, List<int>> Choices;
    public static Action Concede;

    private static void RefreshHandChoiceTargeting(GameState __instance, ref bool __result)
    {
        var choice = __instance.GetFriendlyEntityChoices();
        if (!OwnsLiveMatch || choice == null || choice.ID != match.HumanChoiceId
            || choice.ChoiceType != CHOICE_TYPE.TARGET || !__instance.IsInChoiceMode()) return;
        // EnterChoiceMode can precede draw/zone tasks; HasHandTarget reads animated Card.GetZone().
        __result = __instance.GetEntity(choice.Source)?.HasHandTarget() == true;
    }

    private static string reports;

    public static void Initialize(Harmony harmony, ManualLogSource logger, OfflineLaunchContext launch)
    {
        ClientBridge.launch = launch;
        reports = launch.Reports;
        log = logger;
        runId = launch.RunId;
        harmony.Patch(AccessTools.Method(typeof(GameState), nameof(GameState.CanTargetCardsInHand)),
            postfix: new HarmonyMethod(typeof(ClientBridge), nameof(RefreshHandChoiceTargeting)));
        NativeBigCardWatchdog.Initialize(harmony);
        NativeSpellWatchdog.Initialize(harmony);
        ProfileStartupStatus.ClearBound(reports);
        NativeMatchmaking.Initialize(harmony, logger);
        StartupTiming.Mark("bridge-matchmaking");
        NativeUIProbe.Initialize(harmony, logger, launch.RunId, null, launch.Reports);
        StartupTiming.Mark("bridge-ui-probe");
        NativeGameplayRank.Initialize(harmony, logger);
        StartupTiming.Mark("bridge-gameplay-rank");
        NativeProfileSelector.Initialize(harmony, logger, launch);
        StartupTiming.Mark("bridge-profile-selector");
        // Install before a save is chosen so Start Legend #1 can freeze the current field's top rating at creation.
        // A failed world still falls back to the deterministic legacy cap and never blocks a save from opening.
        try { NpcWorldBoot.TryInstall(message => log.LogInfo(message)); }
        catch (Exception error)
        {
            log.LogError("npc-world boot crashed; using legacy opponents: " + error);
            try { NpcWorldRuntime.Uninstall(); } catch (Exception) { }
        }
        StartupTiming.Mark("bridge-npc-world");
#if STANDARD_PROOF
        OpenSaves();
        StartupTiming.Mark("bridge-save-bind");
#endif
        PatchCopyTask(harmony, typeof(SummonCopyTask),
            nameof(CopyTransportBridge.BeforeSummonCopy), nameof(CopyTransportBridge.AfterSummonCopy));
        PatchCopyTask(harmony, typeof(TransformCopyTask),
            nameof(CopyTransportBridge.BeforeTransformCopy), nameof(CopyTransportBridge.AfterTransformCopy));
        PatchAuraRemoval(harmony, typeof(Aura));
        PatchAuraRemoval(harmony, typeof(SwitchingAura));
        Patch(harmony, typeof(QueueDispatcher), "IsConnectedToGameServer", nameof(Connected));
        Patch(harmony, typeof(QueueDispatcher), "SendGamePacket", nameof(DropSend), new[] { typeof(int), typeof(IProtoBuf), typeof(Action) });
        Patch(harmony, typeof(Network), "GetGameState", nameof(GetGameState));
        harmony.Patch(AccessTools.Method(typeof(MulliganManager), "IsCoinCard", new[] { typeof(Card) })
            ?? throw new MissingMethodException("MulliganManager", "IsCoinCard"),
            postfix: new HarmonyMethod(typeof(ClientBridge), nameof(RecognizeStartingCoin)));
        Patch(harmony, typeof(Network), "SendOption", nameof(SendOption));
        Patch(harmony, typeof(Network), "SendEmote", nameof(SendEmote));
        Patch(harmony, typeof(Network), "SendUserUI", nameof(SkipLocal), new[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(int) });
        Patch(harmony, typeof(Network), "SendUserUI", nameof(SkipLocal), new[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(bool) });
        Patch(harmony, typeof(Network), "SendChoices", nameof(SendChoices));
        Patch(harmony, typeof(Network), "Concede", nameof(SendConcede));
        Patch(harmony, typeof(GameMenu), "ConcedeButtonPressed", nameof(MenuConcede));
        Patch(harmony, typeof(Network), "DisconnectFromGameServer", nameof(Disconnect));
        Patch(harmony, typeof(CardTextBuilder), "GetDefaultCardName", nameof(LocalCardName));
        Patch(harmony, typeof(CardTextBuilder), "GetRawCardTextInHand", nameof(LocalRawCardText), new[] { typeof(string) });
        // Pinned builder names the Soldier from the CONTROLLER's tag 4534 only; a cross-class card in hand needs its own (NativeHeraldText).
        Patch(harmony, typeof(HeraldCardTextBuilder), "GetHeraldColossalName", nameof(HeraldColossalName), new[] { typeof(Entity) });
        // The Herald keyword tooltip reads the CONTROLLER's tag 4534 inside SetupKeywordPanelIfNecessary (a second reader, found with Cecil);
        // the transpiler swaps that read for HeraldTooltipTag. A failed patch only leaves the stock (controller) tooltip.
        try
        {
            HeraldTooltipApplied = false;
            harmony.Patch(AccessTools.Method(typeof(TooltipPanelManager), "SetupKeywordPanelIfNecessary")
                ?? throw new MissingMethodException("TooltipPanelManager", "SetupKeywordPanelIfNecessary"),
                transpiler: new HarmonyMethod(typeof(ClientBridge), nameof(HeraldTooltipTranspiler)));
            if (HeraldTooltipApplied) log.LogInfo("herald tooltip hook applied");
            else log.LogWarning("herald tooltip hook NOT applied: " + HeraldTooltipStatus + " (the stock tooltip names the controller's Soldier)");
        }
        catch (Exception error) { HeraldTooltipApplied = false; HeraldTooltipStatus = "patch failed: " + error.Message; log.LogWarning("herald tooltip hook unavailable: " + error); }
        Patch(harmony, typeof(DialogManager), "ShowExistingAccountPopup", nameof(SkipAccountPrompt));
        Patch(harmony, typeof(EndGameScreen), "UpdateRewards", nameof(SkipLocalRewards), Type.EmptyTypes);
        Patch(harmony, typeof(EndGameScreen), "Show", nameof(ShowLocalResult), Type.EmptyTypes);
        Patch(harmony, typeof(ThinkEmoteManager), "Update", nameof(AllowIdleThinkEmotes), Type.EmptyTypes);
        Patch(harmony, typeof(Gameplay), "OnBoardLoaded", nameof(RetryBoardLoad));
        StartupTiming.Mark("bridge-transport-patches");
        NativeMatch.NativeCoinCard = id =>
        {
            try { return GameDbf.CosmeticCoin?.GetRecord(id)?.CardRecord?.NoteMiniGuid; }
            catch (Exception) { return null; }
        };
        Patch(harmony, typeof(Card), "OnActorChanged", nameof(RepairNullToPlayActor), new[] { typeof(Actor) });
        NativeNpcPresence.Install(harmony, log, () => staged ? match : null);
        StartupTiming.Mark("bridge-npc-presence");
        try
        {
            var instantiate = typeof(PrefabInstanceLoadTracker).GetMethods().Single(method =>
                method.Name == "InstantiatePrefab" && method.GetParameters().Length == 5);
            harmony.Patch(instantiate, postfix: new HarmonyMethod(typeof(ClientBridge), nameof(BoardLoadRejected)));
        }
        catch (Exception error) { log.LogWarning("boards: synchronous rejection hook unavailable " + error); }
    }

    // Save hold released (V1 and V2 passed), or a candidate build's fixed snapshot. The native bootstrap that waits for a bound save starts from here; an
    // auto-open now binds where a pick binds, after every hook is in and the card records are loaded.
    internal static void OpenSaves()
    {
        NativeProfileSelector.SavesResolved();
        // How the startup save was chosen, always logged (also what `hslive startup-save` reports): a picker with its reason, or the auto-open.
        if (launch.Startup != null)
        {
            foreach (string line in launch.Startup.LogLines) log.LogInfo(line);
            log.LogInfo(launch.Startup.LogLine());
        }
        if (launch.ShowProfileSelector) return;
        string autoTarget = launch.AutoOpenTargetId;
        try { BindStartupPath(launch.ProfilePath, launch.RelaunchTargetId ?? autoTarget); }
        catch (Exception error) when (launch.RelaunchTargetId != null)
        {
            // Picker launches only: keep the client alive and let the owner choose another save.
            log.LogError("SAVE_RELAUNCH_FAILED " + launch.RelaunchTargetId + ": " + error);
            relaunchAbandoned = true;
            NativeProfileSelector.RecoverStartup(error.Message);
        }
        catch (Exception error) when (autoTarget != null)
        {
            // The last-opened save did not open: nothing was created, repaired or deleted; the ordinary picker takes over.
            log.LogError("SAVE_AUTO_OPEN_FAILED " + autoTarget + ": " + error);
            string notice = launch.Startup.FailOpen(error);
            log.LogInfo(launch.Startup.LogLine());
            NativeProfileSelector.RecoverAutoOpen(notice, launch.Startup.NoticeHeader);
        }
    }

    // Pinned Card.OnActorChanged misses null -> PLAY when the hidden old actor differs from the loaded actor.
    // Retire that actor as its fallback would, then let the native fresh-actor branch own spawn FX/readiness.
    private static void RepairNullToPlayActor(Card __instance, ref Actor oldActor, Zone ___m_prevZone, Zone ___m_zone)
    {
        if (!staged || match == null || ___m_prevZone != null || !(___m_zone is ZonePlay)
            || __instance.IsShown() || oldActor == null || __instance.GetActor() == null
            || oldActor == __instance.GetActor()) return;
        var entity = __instance.GetEntity();
        if (entity == null || string.IsNullOrEmpty(entity.GetCardId())
            || entity.GetCardType() != TAG_CARDTYPE.MINION && entity.GetCardType() != TAG_CARDTYPE.LOCATION) return;
        var front = GameState.Get();
        if (front == null || front.IsGameCreating() || front.IsGameReseting()) return;
        __instance.ShowCard();
        oldActor.Destroy();
        oldActor = null;
    }

    internal static void BindStartupProfile(string id) => BindStartupPath(
        ProfileCatalog.ProfilePathForId(launch.RootProfilePath, id), id);

    private static void BindStartupPath(string path, string id)
    {
        if (LocalLadder.Profile != null) throw new InvalidOperationException("A save is already bound for this process.");
        // The unfinished-start marker is down BEFORE anything opens the save: an auto-open's was written by Decide before its checks, any
        // other bind writes it here. A crash, hang or native fault anywhere in the open leaves it, so the next launch shows the picker; a
        // failure that is caught here removes it again. A marker that cannot be written never refuses an open (a pick or a new save must
        // always work); it only means that one open has no unfinished-start protection.
        if (id != null)
        {
            startupMarkerArmed = ProfileCatalog.Startup.BeginOpen(launch.RootProfilePath, id, runId, launch.AutoOpenTargetId == id, out string warning);
            if (warning != null) log.LogWarning("STARTUP_MARKER could not be written (" + warning + "); opening " + id + " without unfinished-start protection");
        }
        try { BindStartupOpened(path, id); }
        catch
        {
            if (startupMarkerArmed)
            {
                startupMarkerArmed = false;
                try { ProfileCatalog.Startup.Confirm(launch.RootProfilePath, runId); }
                catch (Exception error) { log.LogWarning("STARTUP_MARKER clear after a failed open failed: " + error.Message); }
            }
            throw;
        }
    }

    private static void BindStartupOpened(string path, string id)
    {
        LocalProfile profile = SelectedProfile.OpenStartup(launch, path, launch.AutoOpenTargetId != null);
        try
        {
            if (profile.WelcomeSkipped != null) log.LogWarning("SAVE_WELCOME_SKIPPED missing profile.xml but " + profile.WelcomeSkipped + ": " + path);
            LocalLadder.Initialize(profile);
            DiagnosticProfilePath = path;
            NativeUIProbe.BindStartupProfile(profile);
            if (id != null)
            {
                if (launch.RelaunchTargetId != null && !relaunchAbandoned)
                    ProfileCatalog.CompleteRelaunch(launch.RootProfilePath, launch.RunId,
                        launch.RelaunchPreviousRunId, launch.RelaunchToken, id);
                // The auto-opened save is already the recorded one: no catalog rewrite for nothing.
                else if (launch.AutoOpenTargetId == null || ProfileCatalog.SelectedId(launch.RootProfilePath) != id)
                    ProfileCatalog.CommitSelection(launch.RootProfilePath, id);
            }
            ProfileStartupStatus.WriteBound(reports, runId, id ?? "fixed", path);
            log.LogInfo("SAVE_BOUND " + path);
        }
        catch
        {
            NativeUIProbe.ResetStartupProfile();
            LocalLadder.ResetStartup(profile);
            profile.Dispose();
            throw;
        }
    }

    private static bool startupMarkerArmed;
    private static readonly StartupHomeSettle HomeSettle = new StartupHomeSettle();

    // Home with nothing happening: the HUB scene, no transition, no reward/toast/welcome/selector presentation, no dialog, no pending
    // reward or pack reveal. Arriving is not enough: a pending reward or the welcome scroll can still take a save down right after.
    private static bool HomeIdle()
    {
        if (LocalLadder.Profile == null || !SceneMgr.IsInitialized()) return false;
        var scene = SceneMgr.Get();
        var input = UniversalInputManager.Get();
        return scene.GetMode() == SceneMgr.Mode.HUB && !scene.IsTransitionNowOrPending() && input != null && !input.IsDialogActive()
            && LocalLadder.Screen == "Menu" && !NativeRewardUI.BlocksHome && !NativeAchievements.Active && !NativeProfileSelector.BlocksHome
            && LocalLadder.Profile.PendingRewardPresentation == null && !LocalLadder.Profile.PackRevealPending;
    }

    // The save counts as working only after Home has stayed idle for StartupHomeSettle.HoldSeconds. A crash before that (opening,
    // loading, a pending reward, the welcome scroll) leaves the marker and the next launch shows the picker. A crash after it is
    // recovered in game: Change Profile from Home.
    private static bool homeReached;

    private static bool confirmFaultLogged;

    private static void ConfirmStartupAtHome()
    {
        if (!startupMarkerArmed) return;
        try
        {
            var scene = SceneMgr.IsInitialized() ? SceneMgr.Get() : null;
            var mode = scene?.GetMode();
            bool transitioning = scene == null || scene.IsTransitionNowOrPending();
            if (!homeReached && mode == SceneMgr.Mode.HUB && !transitioning) homeReached = true;
            bool leftHome = homeReached && !transitioning && (mode == SceneMgr.Mode.TOURNAMENT || mode == SceneMgr.Mode.GAMEPLAY
                || mode == SceneMgr.Mode.COLLECTIONMANAGER || mode == SceneMgr.Mode.PACKOPENING);
            if (!HomeSettle.Tick(Time.realtimeSinceStartup, HomeIdle(), leftHome)) return;
            ClearStartupMarker(leftHome ? "the player left Home" : "Home idle for " + StartupHomeSettle.HoldSeconds + " s");
        }
        catch (Exception error)
        {
            // A throwing presentation getter must not take the rest of ClientBridge.Update down with it; the marker simply stays.
            if (!confirmFaultLogged) { confirmFaultLogged = true; log.LogWarning("STARTUP_MARKER home check failed (the marker stays, the next launch shows the picker): " + error); }
        }
    }

    // A quit that the engine runs normally (Quit from the menu, Change Profile, a refused isolation check) is not a crash: once Home was
    // reached the marker goes, so closing the game a few seconds after it opens never costs the player their auto-open. A crash, kill or
    // hang never gets here, which is the point.
    internal static void ConfirmStartupOnQuit()
    {
        if (startupMarkerArmed && homeReached) ClearStartupMarker("normal quit after Home");
    }

    private static void ClearStartupMarker(string why)
    {
        startupMarkerArmed = false;
        try { ProfileCatalog.Startup.Confirm(launch.RootProfilePath, runId); log.LogInfo("STARTUP_MARKER confirmed: " + why); }
        catch (Exception error) { log.LogWarning("STARTUP_MARKER confirm failed (the next launch shows the picker): " + error.Message); }
    }

    private static void Patch(Harmony harmony, Type type, string method, string prefix, Type[] args = null)
    {
        var original = args == null ? AccessTools.Method(type, method) : AccessTools.Method(type, method, args);
        if (original == null) throw new MissingMethodException(type.FullName, method);
        harmony.Patch(original, prefix: new HarmonyMethod(typeof(ClientBridge), prefix));
    }

    private static void PatchCopyTask(Harmony harmony, Type taskType, string prefix, string postfix)
    {
        harmony.Patch(CopyTransportBridge.RequiredProcess(taskType),
            prefix: new HarmonyMethod(typeof(CopyTransportBridge), prefix),
            postfix: new HarmonyMethod(typeof(CopyTransportBridge), postfix));
    }

    private static void PatchAuraRemoval(Harmony harmony, Type auraType)
    {
        harmony.Patch(AuraTransportBridge.RequiredRemoveInternal(auraType),
            prefix: new HarmonyMethod(typeof(AuraTransportBridge), nameof(AuraTransportBridge.BeforeRemove)),
            postfix: new HarmonyMethod(typeof(AuraTransportBridge), nameof(AuraTransportBridge.AfterRemove)));
    }

    public static void Update()
    {
        ConfirmStartupAtHome();
        NativeMatchmaking.Update();
        bool gameplay = SceneMgr.IsInitialized() && SceneMgr.Get()?.GetMode() == SceneMgr.Mode.GAMEPLAY;
        NativeBigCardWatchdog.Update(staged && requested && localConnected && gameplay && match?.Game != null,
            match?.HumanChoiceId >= 0 || match?.OpposingChoiceId >= 0, matchId, log);
        if (requested && gameplay) npcGameplaySeen = true;
        if (match?.Game != null && requested && (!localConnected || npcGameplaySeen && !gameplay))
        {
            match.StopNpc();
            playtestRecord?.Dispose();
        }
        if (!npcFailed)
        {
            try { match?.TickTurnTimer(); match?.TickNpc(); }
            catch (Exception error)
            {
                npcFailed = true;
                match?.StopNpc();
                log.LogError("LOCAL_NPC_FAILED: " + error);
                NativeTestControl.SendFailed(error);
            }
        }
        NativeSpellWatchdog.Update(OwnsLiveMatch, matchId, log);
#if ENGINE_SOURCE
        if (OwnsLiveMatch) match?.TickChoiceWatchdog();
#endif
        NativeConcedeStallRecovery.Update(staged && requested && localConnected && gameplay && match?.Game != null,
            match?.Game?.State == SabberStoneCore.Enums.State.COMPLETE, match?.HumanConceded ?? false, matchId, log);
        LocalLadder.Observe(match, message => log.LogError(message));
        NativeUIProbe.Update(ready);
        NativePlaytest.Update(match, runId);
        if (staged && !requested && Time.realtimeSinceStartup - stageTime > 90)
        {
            log.LogError("LOCAL_STAGE_TIMEOUT: native gameplay never requested match state");
            LocalLadder.CancelUnstartedStage(message => log.LogError(message));
            Application.Quit(44);
            return;
        }
        if (ready || LocalLadder.Profile == null || !GameDbf.IsLoaded || !SceneMgr.IsInitialized() || SceneMgr.Get().LoadingScreen == null
            || SceneMgr.Get().GetMode() != SceneMgr.Mode.LOGIN || SceneMgr.Get().IsTransitioning()
            || DefLoader.Get() == null || !DefLoader.Get().HasLoadedEntityDefs() || GameMgr.Get() == null || Network.Get() == null) return;
        ready = true;
        LocalLadder.Ready();
    }

    // An unstarted native match plus the setup it was validated for. Built before LocalProfile.Begin commits Pending.
    internal sealed class PreparedLocalMatch
    {
        internal readonly string Deck, Opponent;
        internal readonly int Seed;
        internal readonly LocalDeck Player;
        internal readonly NativeMatch Match;
        internal PreparedLocalMatch(string deck, string opponent, int seed, LocalDeck player, NativeMatch match)
        { Deck = deck; Opponent = opponent; Seed = seed; Player = player; Match = match; }
    }

    private static long? PersonalDeckId(string deck) => deck != null && deck.StartsWith("custom:", StringComparison.Ordinal)
        && long.TryParse(deck.Substring("custom:".Length), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out long id) ? id : (long?)null;

    // Every native-side check that can refuse a match: run before Begin, so a refusal never leaves a Pending behind.
    internal static PreparedLocalMatch PrepareLocalMatch(string deck, string opponent, int seed, int stars, LocalDeck player = null,
        IReadOnlyList<LocalDeckCard> premiumEntries = null, bool dungeon = false)
    {
        EnsureCanStart();
        if (dungeon)
        {
            // Explicit isolated entry point, never a global bypass of collection ownership.
            if (player == null || !player.DungeonDeck || LocalDecks.Get(deck).Hero != player.Hero ||
                !LocalDecks.PlayerHeroes.Contains(player.Hero))
                throw new InvalidOperationException("Dungeon deck/hero mismatch.");
        }
        else NativeUIProjection.ValidateMatchDeckBinding(deck, player);
        var prepared = player == null
            ? new NativeMatch(Send, deck, opponent, seed, stars)
            : new NativeMatch(Send, player, opponent, seed, stars, premiumEntries) { CosmeticDeckId = PersonalDeckId(deck) };
        var defs = DefLoader.Get().GetAllEntityDefs();
        log.LogInfo("LOCAL_HERO_DEFS Fireblast=" + defs.ContainsKey("HERO_08bp"));
        foreach (string id in RequiredPaladinDefinitions)
            if (!defs.ContainsKey(id)) throw new InvalidOperationException("Required Paladin definition missing: " + id);
        var decks = LocalDecks.All.Concat(LocalDecks.TestLoaners).Concat(LocalDecks.TestOpponents)
            .Concat(LocalDecks.PlayerStarterDecks).Concat(LocalDecks.NpcDecks);
        foreach (var checkedDeck in player == null ? decks : decks.Concat(new[] { player }))
        {
            foreach (var card in checkedDeck.Build())
                if (!defs.ContainsKey(card.Id)) throw new InvalidOperationException("Current card definition missing: " + card.Id);
            if (!defs.ContainsKey(checkedDeck.Hero + "bp")) throw new InvalidOperationException("Current hero power definition missing: " + checkedDeck.Hero);
        }
        return new PreparedLocalMatch(deck, opponent, seed, player, prepared);
    }

    // The native client has an entity definition for this card (debug scenario ids must be playable by the pinned client).
    internal static bool ClientKnowsCard(string cardId)
    {
        try { return DefLoader.Get()?.GetAllEntityDefs()?.ContainsKey(cardId) == true; }
        catch (Exception) { return false; }
    }

    private static void EnsureCanStart()
    {
        if (!NativeResultsUI.CanStart) throw new InvalidOperationException("Reward presentation is still active.");
        if (!ready || (match?.Game != null && match.Game.State != SabberStoneCore.Enums.State.COMPLETE))
            throw new InvalidOperationException("Native match is not ready.");
    }

    // Runs after Begin. Any throw leaves no staged match, so LocalProfile.BeginLaunch clears the unstarted Pending.
    public static void StartLocalMatch(LocalMatchRecord record, PreparedLocalMatch prepared, PlaytestQueue playtest = null,
        bool nativeMatchmaking = false, DebugScenario scenario = null)
    {
        if (record == null || prepared == null || record.Deck != prepared.Deck || record.Opponent != prepared.Opponent
            || record.Seed != prepared.Seed)
            throw new InvalidOperationException("Prepared match differs from its pending record.");
        EnsureCanStart();
        LocalDeck player = prepared.Player;
        match?.StopNpc();
        playtestRecord?.Dispose();
        playtestRecord = null;
        match = prepared.Match;
        matchId = record.Id;
        npcFailed = false;
        try
        {
            match.MatchId = record.Id;
            match.PlayerTurnTimerEnabled = NativeTurnTimerOption.Enabled;
            match.StartingPlayer = record.FirstPlayer;
#if ENGINE_SOURCE
            if (scenario != null)
            {
                if (!record.DebugScenario) throw new InvalidOperationException("Debug scenario without its pending flag.");
                match.DebugScenario = scenario;
                match.DebugScenarioLog = line => log.LogInfo(line);
            }
#endif
            // The NPC's own cosmetic pet (PROPOSED, docs/design/npc-pets.md): wire-only, never recorded or settled.
            try { match.OpponentPetCard = NpcPetSeats.For(record, LocalShop.Default, LocalLadder.Profile?.Ladder.StarIndex ?? 0)?.CardId; }
            catch (Exception error) { log.LogWarning("NPC pet unavailable: " + error.Message); }
            // NPC deck finishes follow the rank the opponent's medal shows (NativeGameplayRank reads the same rating);
            // Casual has no medal, so the player's ladder rank (NpcPremiums.SeatStarIndex).
            if (!string.IsNullOrEmpty(record.NpcId) && record.NpcRating is double npcRating)
                match.OpponentPremiumSeat = new NpcPremiumSeat(record.NpcId, record.NpcDeckId ?? record.Opponent,
                    NpcPremiums.SeatStarIndex(record.Mode, npcRating, LocalLadder.Profile?.Ladder.StarIndex ?? 0),
                    NpcWorldRuntime.World?.Find(record.NpcId)?.PersonalityId);
            if (playtest != null)
            {
                // Knobs left at npc show the NPC's own world presentation, with the client's own cosmetic lookups.
                string playtestHeroClass = NativeCardClass(LocalDecks.Get(record.Opponent).Hero);
                match.PresentationSettings = playtest.Presentation(
                    id => NativeCardKnown(id) && NpcPresentationSettings.IsHeroSkin(NativeCardType(id), NativeCardClass(id), playtestHeroClass),
                    NativeCardBackKnown, id => NativeCardKnown(id) && NpcPresentationSettings.IsHeroPower(NativeCardType(id)));
                match.NpcTurnNodes = playtest.TurnNodes;
            }
            // NPC World seam S8: the playtest knob overrides; otherwise a world encounter shows its qualified ladder defaults.
            else if (NpcWorldRuntime.Active is NpcEncounter encounter && encounter.Deck.Id == record.Opponent)
            {
                string baseHeroClass = NativeCardClass(encounter.Deck.Hero);
                match.PresentationSettings = NpcPresentationSettings.LadderDefaults(encounter.Profile,
                    id => NativeCardKnown(id) && NpcPresentationSettings.IsHeroSkin(NativeCardType(id), NativeCardClass(id), baseHeroClass),
                    NativeCardBackKnown, id => NativeCardKnown(id) && NpcPresentationSettings.IsHeroPower(NativeCardType(id)));
            }
            match.NativeNpcIdle = NpcClientIdle;
            match.NativeNpcTurnSynchronized = NpcTimerSynchronized;
            match.NativeNpcBusyReason = NpcClientBusyReason;
#if ENGINE_SOURCE
            match.NativeOpposingChoiceRevealed = OpposingChoiceRevealed;
            match.NativeOpposingChoiceReceived = id => GameState.Get()?.GetOpponentEntityChoices()?.ID == id;
            match.NativeHumanChoicePresentation = HumanChoicePresentation;
#endif
            match.NativeNpcSquelched = NativeNpcPresence.NpcSquelched;
            match.NativeNpcUserUI = SendNpcUserUI;
            // START taunt playback is not reliably exposed by client state; qualification may override the default.
            match.NativeNpcStartingTaunt = () => match.PresentationSettings.NativeStartingTaunt;
            match.NativeHumanInputReady = () => NativeTestControl.TurnInputReady(match);
            match.NativeHumanTimerReady = () => NativeTestControl.TurnInputReady(match, forTimer: true);
            match.TimerTrace = line => log.LogInfo("PLAYER_TURN_TIMER " + line);
            match.NativePresentationInputReady = NpcPresentationInputReady;
            match.BotTrace = line => log.LogInfo("LOCAL_AI " + line);
            match.ChoiceTrace = line => log.LogInfo("LOCAL_CHOICE_OPEN " + line);
            match.EventTrace = line => log.LogInfo("MATCH_EVENT " + line);
#if !SOLOSTONE_RELEASE
            match.NativeEntityDiagnostic = NativeTestControl.ClientEntityDiagnostic;
#endif
            // Every live match, including legacy/casual opponents without a catalog profile. Diagnostics are fail-open.
            try
            {
                log.LogInfo("NPC_REPLY_PRIOR match=" + record.Id + " prior=" + NativeDeckInferenceOption.ThisMatch + " nextMatch=" + NativeDeckInferenceOption.Enabled);
                var npc = NpcCatalog.ForDeck(record.Opponent);
                var humanDeck = player ?? LocalDecks.Get(record.Deck);
                var encounterFacts = EncounterFacts(record.Opponent);
                playtestRecord = PlaytestRecord.Open(PlaytestRecord.RecordDirectory, record.Id, () =>
                    PlaytestRecord.Header(record.Id, npc, humanDeck, LocalDecks.Get(record.Opponent), record.Seed,
                        PlaytestRecord.EngineSourceIdentity,
#if ENGINE_SOURCE
                        match.Game.OfflinePools.Hash,
#else
                        null,
#endif
                        null, playtest, match.PresentationSettings, match.NpcTurnNodes, encounterFacts,
#if ENGINE_SOURCE
                        match.DebugScenario?.Canonical(), match.EngineStartOpponentHero, match.FirstPlayer),
#else
                        null, match.EngineStartOpponentHero, match.FirstPlayer),
#endif
                    message => log.LogWarning(message), typeof(ClientBridge).Assembly);
                match.Recorder = playtestRecord;
                if (string.IsNullOrEmpty(PlaytestRecord.EngineSourceIdentity))
                    log.LogWarning("PLAYTEST engine source build stamp missing; this record cannot be replayed");
            }
            catch (Exception error) { try { log.LogWarning("match-journal unavailable: " + error.Message); } catch { } }
            StartMatch = match.Start;
#if STANDARD_PROOF
            match.TradeTrace = recordJson => File.AppendAllText(reports + @"\trade-proof.jsonl",
                StandardProofNative.Envelope(runId,matchId,NativeTestControl.AttributeTrace(recordJson))+"\n");
            Option = (id,index,target,sub,position) => {
                match.TraceInputNonce=NativeTestControl.SemanticNonce ?? StandardProofNative.InputNonce(reports + @"\input.json",runId,matchId,id,index);
                match.SendOption(id,index,target,sub,position);
            };
#else
            Option = match.SendOption;
#endif
            Choices = match.SendChoices;
            Concede = match.Concede;
            requested = false;
            npcGameplaySeen = false;
            boardLoadRetried = false;
            staged = true;
            localConnected = true;
            stageTime = Time.realtimeSinceStartup;
            // Last refusal point before the stage jobs queue: the file is retired here so it can never apply to a second match.
            scenario?.Consume(message => log.LogInfo(message));
            var manager = GameMgr.Get();
            Set(manager, "m_gameType", GameType.GT_VS_AI);
            Set(manager, "m_nextGameType", GameType.GT_VS_AI);
            Set(manager, "m_nextFormatType", NativeUIProbe.LocalDeckFormat);
            Set(manager, "m_nextMissionId", 0);
            if (nativeMatchmaking) NativeMatchmaking.Begin();
            var transition = new JobDefinition("Offline.StageGame", StageGame(), Array.Empty<IJobDependency>());
            if (SplashScreen.Get() != null)
                Processor.QueueJob("Offline.HideSplash", SplashScreen.Get().Hide(transition), Array.Empty<IJobDependency>());
            else Processor.QueueJob(transition);
        }
        catch (Exception error)
        {
            // Nothing was queued: drop the half-staged match so the stage timeout never fires, and let the caller
            // clear the unstarted Pending instead of quitting into an abandoned (lost) match.
            log.LogError("LOCAL_STAGE_FAILED " + error);
            match?.StopNpc();
            playtestRecord?.Dispose();
            playtestRecord = null;
            match = null;
            matchId = null;
            staged = false;
            localConnected = false;
            StartMatch = null; Option = null; Choices = null; Concede = null;
            throw new InvalidOperationException("Native match could not be staged: " + error.Message, error);
        }
    }

    private static Dictionary<string, object> EncounterFacts(string opponentDeck)
    {
        try
        {
            var active = NpcWorldRuntime.Active;
            if (active == null || active.Deck == null || active.Deck.Id != opponentDeck) return null;
            return new Dictionary<string, object> {
                { "npcId", active.Npc?.Id }, { "deckId", active.Deck.Id }, { "skill", active.Skill?.Id },
                { "skillTier", active.Skill?.Tier }, { "personality", active.Personality?.Id },
                { "rating", active.Rating }, { "reason", active.Reason }, { "replyPrior", NativeDeckInferenceOption.ThisMatch } };
        }
        catch (Exception) { return null; }
    }

    private static bool NpcClientIdle() => NpcClientBusyReason() == null;

    private static bool NpcTimerSynchronized(int turn)
    {
        var front = GameState.Get();
        var processor = front?.GetPowerProcessor();
        return front?.GetGameEntity()?.GetTag(GAME_TAG.TURN) == turn
            && !front.IsFriendlySidePlayerTurn() && NpcClientIdle()
            && processor != null && processor.GetPowerQueue()?.Count == 0
            && !processor.HasTaskLists() && !processor.IsHistoryBlocking();
    }

    // ChoiceCardMgr has dealt the opponent's choice cards (card backs) for this EntityChoices id. Until then the NPC
    // briefly holds its pick (bounded by NativeMatch.OpposingChoiceDwell), so the backs are seen before EntitiesChosen.
    private static bool OpposingChoiceRevealed(int choiceId)
    {
        var opposing = GameState.Get()?.GetOpposingSidePlayer();
        var state = opposing == null ? null : ChoiceCardMgr.Get()?.GetChoiceStateForPlayer(opposing.GetPlayerId());
        return state != null && state.m_choiceID == choiceId && (state.m_hasBeenRevealed || state.m_hasBeenConcealed);
    }

    // Null when the native board has settled: no packets waiting, power queue drained, no history block, no local zone
    // change, no delayed real-time task. Otherwise the first busy condition, for logs. Response-packet readiness requires
    // local input/options, which stay blocked during an NPC turn.
    // The NPC's own played-card preview is not busy (live 2026-10-25 round 2: 16 of 29 idle timeouts). Natively the
    // preview is a fixed-time display (HistoryManager.WaitThenDestroyBigCard: 3 s minion, 4 s spell/hero power) that holds
    // the power queue, and a retail opponent's next play arrives meanwhile and queues behind it. Only that hold is
    // exempt: a running task list (the play's own effects) and the human's big cards still count as busy.
    private static string NpcClientBusyReason()
    {
        var front = GameState.Get();
        var processor = front?.GetPowerProcessor();
        var dispatcher = Network.Get()?.QueueDispatcher as QueueDispatcher;
        if (front == null) return "no-game-state";
        if (front.IsBusy()) return "game-busy";
        if (front.IsMulliganBusy()) return "mulligan";
        if (dispatcher == null) return "no-dispatcher";
        if (dispatcher.HasGamePackets()) return "packets";
        if (processor == null) return "no-processor";
        if (processor.GetCurrentTaskList() != null) return "task-list";
        var history = HistoryManager.Get();
        bool bigCard = history?.IsShowingBigCard() == true;
        if (processor.GetPowerQueue()?.Count != 0 || processor.HasTaskLists())
            return !bigCard ? "power-queue" : NpcOwnBigCard(history) ? null : "big-card";
        if (processor.IsHistoryBlocking()) return bigCard && NpcOwnBigCard(history) ? null : "history-block";
        var zones = ZoneMgr.Get();
        if (zones == null || zones.HasPendingLocalChange() || zones.HasActiveLocalChange()) return "zone-change";
        return NpcDelayedTasks?.GetValue(processor) is System.Collections.ICollection tasks && tasks.Count == 0 ? null : "delayed-tasks";
    }

    // The preview on screen (or loading) is a card the NPC (opposing side) controls.
    private static bool NpcOwnBigCard(HistoryManager history)
    {
        try
        {
            var entity = history.GetCurrentBigCard()?.GetEntity() ?? history.GetPendingBigCardEntity();
            return entity != null && entity.IsControlledByOpposingSidePlayer();
        }
        catch (Exception) { return false; }
    }

#if !SOLOSTONE_RELEASE
    public static void WriteStatus()
    {
        if (LocalLadder.Profile == null)
        {
            string scene = SceneMgr.IsInitialized() ? SceneMgr.Get().GetMode().ToString() : "none";
            bool transitioning = SceneMgr.IsInitialized() && SceneMgr.Get().IsTransitioning();
            ProfileStartupStatus.WriteWaiting(reports, runId, scene, transitioning, NativeProfileSelector.OwnsPicker, SaveHold.Held);
            return;
        }
        LocalLadder.WriteStatus(reports);
        NativeUIProbe.WriteStatus();
        if (match?.Game == null) return;
        NativeUIProbe.WriteLiveCardAudit(match);
        var game = match.Game;
        var front = GameState.Get();
        var entity = front?.GetGameEntity();
        var human = front?.GetFriendlySidePlayer();
        var bot = front?.GetOpposingSidePlayer();
        File.WriteAllLines(reports + @"\match-state.txt", new[] {
            "runId=" + runId, "matchId=" + matchId, "utc=" + DateTime.UtcNow.ToString("o"),
            "engine=" + game.State, "step=" + game.Step, "turn=" + game.Turn,
            "engineStartHero=" + match.EngineStartHero, "engineStartCount=" + match.EngineStartCount,
            "engineStartCards=" + match.EngineStartCards,
#if STANDARD_PROOF
            "snapshot=" + StandardProofNative.Snapshot, "seed=" + match.Seed, "humanOptionsId=" + match.HumanOptionsId,
#endif
            "playtestDebug=" + NativePlaytest.DebugStatus,
#if ENGINE_SOURCE
            "debugScenario=" + (match.DebugScenario == null ? "" : match.DebugScenarioApplied ?? "armed " + match.DebugScenario.Describe()),
#endif
            "botNodes=" + match.BotNodes, "botMilliseconds=" + match.BotMilliseconds, "botFallbacks=" + match.BotFallbacks,
            "humanHp=" + game.Player1.Hero.Health, "botHp=" + game.Player2.Hero.Health,
            "humanResult=" + game.Player1.PlayState, "botResult=" + game.Player2.PlayState,
            "frontState=" + (entity?.GetTag(GAME_TAG.STATE) ?? 0),
            "frontStep=" + (entity?.GetTag(GAME_TAG.STEP) ?? 0),
            "frontTurn=" + (entity?.GetTag(GAME_TAG.TURN) ?? 0),
            "frontHumanResult=" + (human?.GetTag(GAME_TAG.PLAYSTATE) ?? 0),
            "frontBotResult=" + (bot?.GetTag(GAME_TAG.PLAYSTATE) ?? 0),
            "frontHumanDamage=" + (human?.GetHero()?.GetTag(GAME_TAG.DAMAGE) ?? 0),
            "frontMana=" + (human?.GetNumAvailableResources() ?? 0),
            "frontGameOver=" + (front?.IsGameOver() ?? false),
            "frontHand=" + string.Join(",", human?.GetHandZone()?.GetCards()?.Select(card => card.GetEntity().GetEntityId() + ":" + card.GetEntity().GetCardId() + ":actor=" + (card.GetActor() != null)) ?? Array.Empty<string>())
        });
    }
#endif

    private static void RecognizeStartingCoin(Card __0, ref bool __result)
    {
        var entity = __0?.GetEntity();
        if (NativeUIProbe.IsolatedLaunch && staged && entity != null
            && match?.IsMappedStartingCoin(entity.GetEntityId(), entity.GetCardId()) == true) __result = true;
    }

#if !SOLOSTONE_RELEASE
// Test control only (compiled out of a public build): reads, recovery and option validation for the endpoint.
    internal static NativeTestControl.View ReadTestControlView() => NativeTestControl.ReadView(match, matchId);
    internal static Dictionary<string, object> TestControlEntities(string requestedMatch, int[] ids)
    {
        NativeTestControl.AssertMainThread();
        RequireDiagnosticMatch(requestedMatch);
        return new Dictionary<string, object> { { "diagnosticsOnly", true }, { "entities", ids.Select(id => match.DiagnosticEntity(id)).ToArray() } };
    }
    private static void RequireDiagnosticMatch(string requestedMatch)
    {
        if (!NativeTestControl.DiagnosticsEnabled || !OwnsLiveMatch || requestedMatch != matchId)
            throw new InvalidOperationException("No matching isolated diagnostics match");
    }
    internal static int[] ValidateTestControlRecovery(string requestedMatch, int id, int[] picks)
    {
        NativeTestControl.AssertMainThread(); RequireDiagnosticMatch(requestedMatch);
        if (GameState.Get() == null || GameState.Get().GetFriendlyEntityChoices() != null)
            throw new InvalidOperationException("Client registered a choice; use normal choose input");
#if ENGINE_SOURCE
        return match.ValidateRecoveryChoice(id, picks);
#else
        throw new InvalidOperationException("Recovery requires source engine");
#endif
    }
    internal static void RecoverTestControlChoice(string requestedMatch, int id, int[] picks)
    {
        ValidateTestControlRecovery(requestedMatch, id, picks);
#if ENGINE_SOURCE
        var recoveringMatch = match;
        recoveringMatch.RecoverUnregisteredChoice(id, picks, entityId => GameState.Get()?.GetEntity(entityId) != null,
            choiceId => GameState.Get()?.GetFriendlyEntityChoices()?.ID == choiceId,
            clientPicks => {
                RequireDiagnosticMatch(requestedMatch);
                if (!ReferenceEquals(match, recoveringMatch)) throw new InvalidOperationException("Recovery match changed");
                NativeTestControl.ObserveChoices(id, clientPicks);
                recoveringMatch.SendChoices(id, clientPicks);
                NativeTestControl.EngineAccepted();
            }, NativeTestControl.SendFailed);
#endif
    }
    internal static SabberStoneCore.Tasks.PlayerTasks.PlayerTask ValidateTestControlOption(int id, int index, int target, int sub, int position)
    {
        NativeTestControl.AssertMainThread();
        if (match == null) throw new InvalidOperationException("No isolated match");
        return match.ValidateOption(id, index, target, sub, position);
    }
#endif

    private static bool LocalCardName(EntityDef __0, ref string __result)
    {
        if (LocalNativeText.TryGetNameOverride(NativeUIProbe.PreviewDefinitionContext,
            __0?.GetCardId(), raw => TextUtils.TransformCardText(raw), out string name))
        {
            __result = name;
            return false;
        }
        if (!staged || CollectionManager.Get() != null) return true;
        // shortcut: offline classic game has no collection manager or Battlegrounds cosmetic lookup.
        __result = TextUtils.TransformCardText(CardTextBuilder.GetRawCardName(__0));
        return false;
    }

    // Replaces `GameState.Get().GetPlayer(id).GetTag(4534)` (ldc.i4 4534; callvirt GetTag, exactly once) with `HeraldTooltipTag(player, local 1)`,
    // local 1 being the tooltip's main EntityBase. Pattern absent or ambiguous: the original instructions, untouched.
    // True only when the transpiler replaced the read; reported by `hslive entity` (client.herald.tooltipPatchApplied) so a live check can fail.
    internal static bool HeraldTooltipApplied;
    internal static string HeraldTooltipStatus = "not installed";
    // Runtime evidence the patched read ran: calls and the class the last tooltip switched on.
    internal static int HeraldTooltipCalls, HeraldTooltipLastClass;

    private static IEnumerable<HarmonyLib.CodeInstruction> HeraldTooltipTranspiler(IEnumerable<HarmonyLib.CodeInstruction> instructions)
    {
        var list = instructions.ToList();
        try
        {
            var matches = new List<int>();
            for (int i = 0; i + 1 < list.Count; i++)
                if (list[i].opcode == System.Reflection.Emit.OpCodes.Ldc_I4 && list[i].operand is int value && value == 4534
                    && list[i + 1].opcode == System.Reflection.Emit.OpCodes.Callvirt
                    && list[i + 1].operand is System.Reflection.MethodInfo method && method.Name == "GetTag") matches.Add(i);
            var helper = typeof(ClientBridge).GetMethod(nameof(HeraldTooltipTag), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            if (matches.Count != 1 || helper == null)
            {
                HeraldTooltipStatus = "read pattern matched " + matches.Count + " times (expected 1)" + (helper == null ? ", helper missing" : "");
                return list;
            }
            list[matches[0]].opcode = System.Reflection.Emit.OpCodes.Ldloc_1; list[matches[0]].operand = null;
            list[matches[0] + 1].opcode = System.Reflection.Emit.OpCodes.Call; list[matches[0] + 1].operand = helper;
            HeraldTooltipApplied = true; HeraldTooltipStatus = "applied";
        }
        catch (Exception error) { HeraldTooltipStatus = "transpiler failed: " + error.Message; return instructions; }
        return list;
    }

    private static int HeraldTooltipTag(Player player, EntityBase entity)
    {
        int controller = 0;
        try { controller = player == null ? 0 : player.GetTag((GAME_TAG)4534); } catch (Exception) { }
        int shown = controller;
        try { shown = NativeHeraldText.TooltipClass(entity == null ? 0 : entity.GetTag((GAME_TAG)4534), controller); }
        catch (Exception) { }
        HeraldTooltipCalls++; HeraldTooltipLastClass = shown;
        return shown;
    }

    // Must never throw into the client's text build: any doubt runs the original (controller tag).
    private static bool HeraldColossalName(Entity __0, ref string __result)
    {
        try
        {
            string key = NativeHeraldText.Key(__0 == null ? 0 : __0.GetTag((GAME_TAG)4534));
            if (key == null) return true;
            string text = GameStrings.Get(key);
            if (string.IsNullOrEmpty(text)) return true;
            __result = text;
            return false;
        }
        catch (Exception) { return true; }
    }

    private static bool LocalRawCardText(string __0, ref string __result)
    {
        if (!LocalNativeText.TryGetOverride(staged || NativeUIProbe.Active, __0, TextUtils.GetDefaultTextVariant, out string text))
            return true;
        __result = text;
        return false;
    }

    private static bool SkipLocal() { return !staged; }
    private static bool SkipAccountPrompt() { return false; }
    private static bool AllowIdleThinkEmotes() { return OfflineProbe.IdleThinkEmotes.Value; }
    private static bool SkipLocalRewards(EndGameScreen __instance) => !NativeResultsUI.OwnsScreen(__instance);
    private static bool ShowLocalResult(EndGameScreen __instance)
    {
        if (!OwnsLocalPostMatch && !NativeResultsUI.OwnsScreen(__instance)) return true;
        if (match?.Game?.State != SabberStoneCore.Enums.State.COMPLETE) return false;
        LocalLadder.NativeFinished();
        return NativeResultsUI.AllowShow(__instance);
    }

    internal static void ChoiceApplicationPause(bool paused)
    {
#if ENGINE_SOURCE
        NativeMatch.ChoiceApplicationPause(paused);
#endif
    }
    internal static void ChoiceApplicationFocus(bool focused)
    {
#if ENGINE_SOURCE
        NativeMatch.ChoiceApplicationFocus(focused);
#endif
    }

#if ENGINE_SOURCE
    private static NativeMatch.ChoicePresentation HumanChoicePresentation()
    {
        var front = GameState.Get(); var processor = front?.GetPowerProcessor();
        var choices = front?.GetFriendlyEntityChoices();
        if (front == null || processor == null || choices?.ID != match?.HumanChoiceId
            || SceneMgr.Get()?.IsTransitioning() != false || Time.timeScale < 1f
            || front.GetResponseMode() != GameState.ResponseMode.CHOICE || front.IsResponsePacketBlocked()
            || front.IsBusy() || processor.GetCurrentTaskList() != null || processor.HasTaskLists()
            || processor.GetPowerQueue()?.Count != 0 || processor.IsHistoryBlocking()
            || InputManager.Get()?.GetHeldCard() != null || UniversalInputManager.Get()?.IsDialogActive() == true)
            return null;
        var manager = ChoiceCardMgr.Get();
        var state = manager?.GetFriendlyChoiceState();
        return new NativeMatch.ChoicePresentation {
            ChoiceId = choices.ID, Type = (SabberStoneCore.Enums.ChoiceType)(int)choices.ChoiceType,
            CountMin = choices.CountMin,
            Settled = true, SourceExists = choices.Source <= 0 || front.GetEntity(choices.Source) != null,
            CanTargetCardsInHand = front.CanTargetCardsInHand(),
            StateChoiceId = state?.m_choiceID ?? -1, StateRevealed = state?.m_hasBeenRevealed == true,
            ToggleButtonExists = manager != null && manager.GetToggleButton() != null,
            ChoicesShown = !(BlockingDiagnostics.Get(manager, "m_friendlyChoicesShown") is bool shown) || shown,
            // These paths use their own controls instead of the default card picker (Default = 0).
            DisplayType = state == null ? 0 : (int)state.m_choiceDisplayType,
            ShopChoice = state?.m_isShopChoice == true, MagicItemDiscover = state?.m_isMagicItemDiscover == true,
            Offers = choices.Entities.Select(id => {
                var entity = front.GetEntity(id); var card = entity?.GetCard();
                var actor = card == null ? null : card.GetActor();
                return new NativeMatch.OfferPresentation {
                    CardId = entity?.GetCardId(),
                    Loading = entity?.IsBusy() == true || entity?.IsLoadingAssets() == true || card?.IsActorLoading() == true,
                    InHand = card != null && card.GetZone()?.m_ServerTag == TAG_ZONE.HAND,
                    InputBlocked = entity == null || !front.IsEntityInputEnabled(entity),
                    CardExists = card != null, HasCardDef = card != null && card.HasCardDef,
                    CardActive = card != null && card.gameObject.activeInHierarchy,
                    ActorExists = actor != null, ActorActive = actor != null && actor.gameObject.activeInHierarchy,
                    ActorShown = actor != null && actor.IsShown(), ColliderEnabled = actor != null && actor.IsColliderEnabled()
                };
            }).ToArray()
        };
    }
#endif

    public static void ConcedeLocal()
    {
        if (match?.Game == null || match.Game.State == SabberStoneCore.Enums.State.COMPLETE) return;
        LocalLadder.MarkConcede();
        Concede?.Invoke();
    }

    private static IEnumerator<IAsyncJobResult> StageGame()
    {
        int board = 1;
        int forced = 0, selected = 0;
        try
        {
            if (!GameDbf.IsLoaded || GameDbf.Board == null)
                throw new InvalidOperationException("BOARD table unavailable");
            var records = GameDbf.Board.GetRecords().ToArray();
            // These asset types live outside the plugin's referenced assemblies.
            var available = typeof(AssetLoader).GetMethod("IsRuntimeAssetVariantAvailable");
            var parameters = available.GetParameters();
            var options = Enum.ToObject(parameters[1].ParameterType, 0);
            var loader = typeof(AssetLoader).GetMethod("Get").Invoke(null, null);
            var present = records.Where(record => !string.IsNullOrEmpty(record.Prefab)
                && (bool)available.Invoke(loader, new[] {
                    Activator.CreateInstance(parameters[0].ParameterType, record.Prefab), options
                })).Select(record => record.ID).ToArray();
            if (!boardsLogged)
            {
                boardsLogged = true;
                log.LogInfo("boards: count=" + records.Length);
                foreach (var record in records.OrderBy(record => record.ID))
                    log.LogInfo("boards: id=" + record.ID + " NoteDesc=" + record.NoteDesc + " Prefab=" + record.Prefab
                        + " available=" + (present.Contains(record.ID) ? "true" : "false"));
            }
            OfflineProbe.RefreshForceBoard();
            forced = Math.Max(0, OfflineProbe.ForceBoardId.Value);
            if (forced > 0 && !present.Contains(forced))
                log.LogWarning("boards: forced id=" + forced + " unavailable, ignoring");
            // The board is the player's cosmetic (owner 2026-10-25), NPC matches too.
            var profile = LocalLadder.Profile;
            selected = profile?.SelectedBoard ?? 0;
            board = BoardChoice.ForPlayer(match.Seed, present, forced, selected, profile?.OwnedBoards ?? new int[0]);
        }
        catch (Exception error) { log.LogWarning("boards: fallback=1 " + error); }
        log.LogInfo("board: id=" + board + " seed=" + match.Seed + " forced=" + forced + " selected=" + selected);
        Send(16, new PegasusGame.GameSetup {
            Board = board, BoardLayout = 0, MaxSecretZoneSizePerPlayer = 5,
            MaxSecretsPerPlayer = 5, MaxQuestsPerPlayer = 3, MaxFriendlyMinionsPerPlayer = 7,
            DisconnectWhenStuckSeconds = 120
        });
        log.LogInfo("LOCAL_GAME_SETUP_QUEUED");
        yield break;
    }

    private static void BoardLoadRejected(bool __result, object __1, Delegate __2, object __3)
    {
        if (__result) return;
        if (!staged || __2 == null || __2.Method.Name != "OnBoardLoaded" || !(__2.Target is Gameplay)) return;
        try
        {
            log.LogWarning("boards: load rejected synchronously, invoking OnBoardLoaded(null)");
            __2.DynamicInvoke(__1, null, __3);
        }
        catch (Exception error) { log.LogWarning("boards: rejected callback failed " + error); }
    }

    private static bool RetryBoardLoad(Gameplay __instance, GameObject __1)
    {
        if (!staged || __1 != null || boardLoadRetried) return true;
        try
        {
            var setup = GameMgr.Get().GetGameSetup();
            if (setup.Board == 1) return true;
            var tracker = AccessTools.Field(typeof(Gameplay), "m_prefabInstanceLoaderTracker").GetValue(__instance);
            var context = AccessTools.Field(typeof(Gameplay), "m_prefabContext").GetValue(__instance);
            var instantiate = typeof(PrefabInstanceLoadTracker).GetMethods().Single(method =>
                method.Name == "InstantiatePrefab" && method.GetParameters().Length == 5);
            var parameters = instantiate.GetParameters();
            var assetRef = Activator.CreateInstance(parameters[1].ParameterType, GameDbf.Board.GetRecord(1).Prefab);
            var callback = Delegate.CreateDelegate(parameters[2].ParameterType, __instance,
                AccessTools.Method(typeof(Gameplay), "OnBoardLoaded", new[] { parameters[1].ParameterType, typeof(GameObject), typeof(object) }));
            log.LogWarning("boards: load failed id=" + setup.Board + ", falling back to 1");
            boardLoadRetried = true;
            setup.Board = 1;
            instantiate.Invoke(tracker, new[] { context, assetRef, callback, null, Enum.ToObject(parameters[4].ParameterType, 0) });
            return false;
        }
        catch (Exception error) { log.LogWarning("boards: retry failed " + error); return true; }
    }

    private static void Set(object instance, string field, object value)
    {
        var member = AccessTools.Field(instance.GetType(), field);
        if (member == null) throw new MissingFieldException(instance.GetType().FullName, field);
        member.SetValue(instance, value);
    }

    private static bool SendNpcUserUI(PegasusGame.UserUI packet)
    {
        var handler = RemoteActionHandler.Get();
        var state = GameState.Get();
        if (handler == null || state == null || state.GetOpposingSidePlayer() == null) return false;
        // Preserve the retail receiving/squelch path; no alternate bubbles or direct actor calls.
        var native = new Network.UserUI { playerId = packet.PlayerId };
        if (packet.HasMouseInfo)
            native.mouseInfo = new Network.UserUI.MouseInfo { OverCardID = packet.MouseInfo.OverCard,
                HeldCardID = 0, ArrowOriginID = 0, X = 0, Y = 0 };
        else if (packet.HasEmote)
            native.emoteInfo = new Network.UserUI.EmoteInfo { Emote = packet.Emote };
        else return false;
        handler.HandleAction(native);
        return true;
    }

    public static void Send(int id, IProtoBuf body)
    {
        if (body is PegasusGame.PowerHistory history)
        {
            var defs = DefLoader.Get().GetAllEntityDefs();
            foreach (var entry in history.List)
            {
                var entity = entry.HasFullEntity ? entry.FullEntity : entry.HasShowEntity ? entry.ShowEntity : entry.HasChangeEntity ? entry.ChangeEntity : null;
                NativeMatch.ValidateHistoryEntityIdentity(entity);
                if (entity != null && !string.IsNullOrEmpty(entity.Name) && !defs.ContainsKey(entity.Name))
                    throw new InvalidOperationException("Native client missing card definition " + entity.Name);
            }
        }
        var dispatcher = Network.Get().QueueDispatcher as QueueDispatcher;
        if (dispatcher == null) throw new InvalidOperationException("Unexpected native dispatcher");
        dispatcher.OnGamePacketReceived(new PegasusPacket(id, 0, body), null);
        NativeTestControl.PacketInjected();
        log.LogInfo("LOCAL_PACKET " + id + " " + body.GetType().Name);
    }

    private static bool Connected(ref bool __result)
    {
        if (!localConnected) return true;
        __result = true;
        return false;
    }

    private static bool Disconnect(Network.DisconnectReason __0)
    {
        if (!staged) return true;
        localConnected = false;
        match?.StopNpc();
        log.LogWarning("LOCAL_DISCONNECT " + __0);
        return false;
    }

    private static bool DropSend(int __0, IProtoBuf __1, Action __2)
    {
        if (!staged) return true;
        // 68 GeneratePetTreat and 67 FeedPet are answered with pet tag changes inside the running match; others are unused.
        if ((__0 == 67 || __0 == 68) && match != null)
        {
            bool accepted = false;
            try { accepted = match.ReceiveNativePetTreat(__0); }
            catch (Exception error) { log.LogWarning("LOCAL_PET_TREAT_FAILED " + error.Message); }
            log.LogInfo("LOCAL_PET_TREAT " + __0 + (accepted ? " accepted" : " ignored"));
            __2?.Invoke();
            return false;
        }
        log.LogInfo("LOCAL_UNUSED_SEND " + __0 + " " + (__1 == null ? "null" : __1.GetType().Name));
        __2?.Invoke();
        return false;
    }

    private static bool GetGameState()
    {
        if (!staged) return true;
        if (!requested)
        {
            requested = true;
            log.LogInfo("LOCAL_GAME_STATE_REQUESTED");
            try { StartMatch(); }
            catch (Exception error)
            {
                log.LogError("LOCAL_MATCH_START_FAILED " + error);
                LocalLadder.CancelUnstartedStage(message => log.LogError(message));
                Application.Quit(45);
                return false;
            }
            LocalLadder.EngineStarted(message => log.LogError(message), match.FirstPlayer);
            LocalLadder.NativeStarted();
        }
        return false;
    }

    // The client's own record lookups decide whether an NPC cosmetic can be shown; anything else falls back.
    private static bool NativeCardKnown(string cardId)
    {
        try { return GameDbf.GetIndex().GetCardRecord(cardId) != null; } catch (Exception) { return false; }
    }
    private static string NativeCardType(string cardId)
    {
        try { return DefLoader.Get().GetEntityDef(cardId)?.GetCardType().ToString(); } catch (Exception) { return null; }
    }
    private static string NativeCardClass(string cardId)
    {
        try { return DefLoader.Get().GetEntityDef(cardId)?.GetClass().ToString(); } catch (Exception) { return null; }
    }
    private static bool NativeCardBackKnown(int id)
    {
        try { return GameDbf.CardBack != null && GameDbf.CardBack.GetRecord(id) != null; } catch (Exception) { return false; }
    }
    private static bool NpcPresentationInputReady()
    {
        var front = GameState.Get(); var input = InputManager.Get();
        return front != null && input != null && input.GetHeldCard() == null && input.GetBattlecrySourceCard() == null
            && !front.IsInSubOptionMode() && ChoiceCardMgr.Get()?.GetSubOptionParentCard() == null
            && front.GetFriendlyEntityChoices() == null
            && (front.GetResponseMode() == GameState.ResponseMode.NONE || front.GetResponseMode() == GameState.ResponseMode.OPTION);
    }

    private static bool SendEmote(EmoteType __0)
    {
        if (!staged) return true;
        match?.ReceiveNativeEmote((int)__0);
        return false;
    }

    private static bool SendOption(int __0, int __1, int __2, int __3, int __4)
    {
        if (!staged) return true;
        log.LogInfo($"LOCAL_OPTION id={__0} index={__1} target={__2} sub={__3} pos={__4}");
        try
        {
            NativeTestControl.ObserveSend("option", __0, __1, __2, __3, __4);
            Option?.Invoke(__0, __1, __2, __3, __4);
            NativeTestControl.EngineAccepted();
        }
        catch (TurnExpiredException error) { NativeTestControl.SendRejected(error); }
        catch (Exception error) { NativeTestControl.SendFailed(error); throw; }
        return false;
    }

    private static bool SendChoices(int __0, List<int> __1)
    {
        if (!staged) return true;
        log.LogInfo("LOCAL_CHOICES id=" + __0 + " picks=" + (__1 == null ? "null" : string.Join(",", __1)));
        try
        {
            NativeTestControl.ObserveChoices(__0, __1);
            Choices?.Invoke(__0, __1);
            NativeTestControl.EngineAccepted();
        }
        catch (TurnExpiredException error) { NativeTestControl.SendRejected(error); }
        catch (Exception error) { NativeTestControl.SendFailed(error); throw; }
        return false;
    }

    private static bool MenuConcede(GameMenu __instance)
    {
        if (!OwnsLiveMatch) return true;
        var record = new MiniJSON.JsonNode { { "matchId", matchId }, { "runId", runId }, { "event", "CONCEDE_PRESS" },
            { "origin", "native-menu" }, { "utc", DateTime.UtcNow.ToString("o") } };
        log.LogInfo("CONCEDE_PRESS " + MiniJSON.Json.Serialize(record));
        RecordFx(record);
        try
        {
            // Same GameState -> Network -> local settlement path as semantic concede. Retail progression popups
            // have no offline meaning and may never surface while the presentation queue is blocked.
            var front = GameState.Get();
            if (!front.WasConcedeRequested()) front.Concede();
            else ConcedeLocal(); // Retry an earlier failed send; terminal matches are idempotent.
            __instance.GameMenuHide();
            record["event"] = "CONCEDE_OUTCOME";
            record["status"] = match?.Game?.State == SabberStoneCore.Enums.State.COMPLETE ? "engine-complete" : "pending";
        }
        catch (Exception error) { record["event"] = "CONCEDE_OUTCOME"; record["status"] = "failed"; record["error"] = error.ToString(); }
        log.LogInfo("CONCEDE_OUTCOME " + MiniJSON.Json.Serialize(record));
        RecordFx(record);
        return false;
    }

    private static bool SendConcede()
    {
        if (!staged) return true;
        try
        {
            NativeTestControl.ObserveSend("concede");
            ConcedeLocal();
            NativeTestControl.EngineAccepted();
        }
        catch (Exception error) { NativeTestControl.SendFailed(error); throw; }
        return false;
    }
}
