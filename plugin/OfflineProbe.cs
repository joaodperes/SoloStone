using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

#if SOLOSTONE_RELEASE
[BepInPlugin("solostone.plugin", "SoloStone", "0.1.0")]
#else
[BepInPlugin("local.hs.native.ui.probe", "Hearthstone Native UI Probe", "0.0.1")]
#endif
public sealed class OfflineProbe : BaseUnityPlugin
{
#if SOLOSTONE_RELEASE
    private const string HarmonyId = "solostone.plugin.patches";
    // Set from the launch context before first use: a public build has no default location.
    private string Root, Reports;
#else
    private const string HarmonyId = "local.hs.offline.probe";
    private string Root = @"C:\hs-native-ui-"+@"prototype";
    private string Reports = @"C:\hs-native-ui-"+@"prototype\reports";
#endif
    private static BepInEx.Logging.ManualLogSource diagnosticLog;
    internal static ConfigEntry<bool> IdleThinkEmotes;
#if SOLOSTONE_RELEASE
    // No ForceBoardId in a public build (I2c): nothing is bound in the player's config and the board is always the player's own.
    internal sealed class NoBoardOverride { internal int Value => 0; }
    internal static readonly NoBoardOverride ForceBoardId = new NoBoardOverride();
    internal static void RefreshForceBoard() { }
#else
    internal static ConfigEntry<int> ForceBoardId;
    private static DateTime forceBoardStamp;
    // Board verification aid: a test lane edits ForceBoardId between matches without a restart.
    internal static void RefreshForceBoard()
    {
        try
        {
            var file = ForceBoardId?.ConfigFile;
            if (file == null) return;
            var stamp = System.IO.File.GetLastWriteTimeUtc(file.ConfigFilePath);
            if (stamp == forceBoardStamp) return;
            forceBoardStamp = stamp;
            file.Reload();
        }
        catch (Exception) { }
    }
#endif
    private float nextStatus, nextScreenshotPoll;
    private double previousUpdateEnd;
    private OfflineLaunchContext launchContext;

    private void Awake()
    {
        StartupTiming.Begin(message => Logger.LogInfo(message), Time.realtimeSinceStartup);
        try { InitializeProbe(); StartupTiming.End(); }
        catch (Exception error) { Fatal(error); }
    }

    private void Fatal(Exception error)
    {
        enabled = false;
#if SOLOSTONE_INSTALLER
        if (error is InstallerRefusal refusal)
        {
            Logger.LogFatal("Installer launch refused: " + refusal.Message);
            Application.Quit(refusal.ExitCode);
            return;
        }
#endif
        Logger.LogFatal("Offline probe initialization failed: " + error);
#if SOLOSTONE_INSTALLER
        Application.Quit(InstallerRuntime.ExitCodeFor(error));
#else
        Application.Quit(42);
#endif
    }

    // Its own method: touching NativeUIProjection runs the engine card DB and plugin catalog static initializers. Inline
    // in InitializeProbe, Mono ran them while JIT-compiling that method, before the isolation guard and unmeasured.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
#if SOLOSTONE_INSTALLER && !SOLOSTONE_TEST_HOOKS
    private static void ConfigureLoanerGroupPath(string root) { }
#else
    private static void ConfigureLoanerGroupPath(string root) =>
        NativeUIProjection.TestLoanerGroupPath = root + @"\client\reports\test-loaner-group.txt";
#endif

    private void InitializeProbe()
    {
#if !STANDARD_PROOF
        SaveHold.Engage();
#endif
        string identity = WindowsIdentity.GetCurrent().Name;
        string executable = Process.GetCurrentProcess().MainModule.FileName;
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] args = Environment.GetCommandLineArgs();
        bool wine = RunningUnderWine();
        int lane = OfflineGuard.LaunchLane(args);
        bool installed = false, installerBuild = false;
#if SOLOSTONE_INSTALLER
        installerBuild = true;
        installed = InstallerRuntime.Resolve(executable, localData, args, false, wine) != null;
#endif
        bool deck = OfflineGuard.DeckLaunch(args, wine);
        // OfflineGuard.Judge decides and explains. Elevation, not group membership, for a public build (see TokenElevation); the checks run lazily and
        // never on a Deck launch, as before.
#if SOLOSTONE_RELEASE
        const bool releaseBuild = true;
        Func<bool> labIsolated = () => false, inAdminGroup = () => false;
#else
        const bool releaseBuild = false;
        Func<bool> labIsolated = () => deck ? OfflineGuard.IsDeckIsolated(executable, localData, lane)
            : OfflineGuard.IsIsolated(identity, Environment.MachineName, executable, localData, lane);
        Func<bool> inAdminGroup = () => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
#endif
        OfflineGuard.GuardVerdict verdict = OfflineGuard.Judge(releaseBuild, deck, installed, labIsolated, TokenElevation.IsElevated, inAdminGroup);
        if (verdict.Rejected)
        {
            Logger.LogFatal("Isolation guard rejected process; exiting before hooks (wine=" + wine
                + " executable=" + executable + " localData=" + localData + " identity=" + identity + "): "
                + OfflineGuard.RejectionReasons(args, releaseBuild, installerBuild, deck, verdict)
                + " | args=" + OfflineGuard.RedactedArguments(args));
            Application.Quit(40);
            return;
        }
#if STANDARD_PROOF
        const bool candidate = true;
#else
        const bool candidate = false;
#endif
#if SOLOSTONE_INSTALLER
        OfflineLaunchContext launch = InstallerRuntime.Prepare(() => OfflineGuard.ResolveHeldLaunch(identity, Environment.MachineName,
            executable, localData, args, candidate, wine));
#elif STANDARD_PROOF
        OfflineLaunchContext launch = OfflineGuard.ResolveLaunch(identity, Environment.MachineName,
            executable, localData, args, candidate, wine);
#else
        OfflineLaunchContext launch = OfflineGuard.ResolveHeldLaunch(identity, Environment.MachineName,
            executable, localData, args, candidate, wine);
#endif
        launchContext = launch;
        StartupTiming.Mark("isolation-guard");
        Root = launch.Root;
        Reports = launch.Reports;
#if !STANDARD_PROOF
        // V1, on any version, before the first hook. A client that lost a target stops here: no patch applied, no save touched.
        string bindingFailure = ClientChecks.CheckBindings(Reports, launch.RunId, Logger);
        StartupTiming.Mark("client-bindings");
        if (bindingFailure != null)
        {
            enabled = false;
            Application.Quit(ClientChecks.ExitBlocked);
            return;
        }
#endif
        NativePlaytest.Root = Reports + @"\";
        ConfigureLoanerGroupPath(Root);
        StartupTiming.Mark("card-catalogs");
#if !SOLOSTONE_INSTALLER || SOLOSTONE_TEST_HOOKS
        PlaytestQueue.KnobPath = Reports + @"\playtest-opponent";
#endif
        DebugScenario.Path = Reports + @"\debug-scenario";
        DebugScenario.RunId = launch.RunId;
        DebugScenario.ClearStale(DebugScenario.Path);   // a scenario file never survives into a new launch
        // Test lanes 1-3 only: lane 0 (the owner's client, test control is live after every deploy) and the Deck never arm the knob.
        PetPacingKnob.ClearStale(Reports + @"\pet-xp-pacing");
        // Any -hs-native-ui-host selector (deck) disables it too, whether or not the launch validated as a Deck launch.
        PetPacingKnob.Path = PetPacingKnob.PathFor(launch.LaneId, launch.Deck || Array.IndexOf(args, "-hs-native-ui-host") >= 0, Reports);
        PetPacingKnob.RunId = launch.RunId;
        PlaytestRecord.RecordDirectory = Reports + @"\playtest";
#if !SOLOSTONE_RELEASE
        CardDbExport.Root = Reports + @"\control";
        CosmeticCoinExport.Root = Reports;
#endif
        string runId = launch.RunId;
        Logger.LogInfo("ISOLATED identity=" + identity + " localData=" + localData + (launch.Deck ? " host=deck" : ""));
        diagnosticLog = Logger;
        LocalProfile.DeckRepairLog = message => Logger.LogInfo(message);
#if !STANDARD_PROOF
        LocalProfileBackups.Log = message => diagnosticLog.LogWarning(message);
#endif
        var harmony = new Harmony(HarmonyId);
        var loginIntent = AccessTools.Method(typeof(Network), "SetShouldBeConnectedToAurora");
        var tokenFetch = AccessTools.Method(typeof(Hearthstone.Login.DesktopLoginTokenFetcher), "GetTokenIfAvailable");
        var tokenRefresh = AccessTools.Method(typeof(Hearthstone.Login.OfflineAuthTokenCache), "RefreshTokenCacheIfNeeded");
        var loginStart = AccessTools.Method(typeof(Hearthstone.Login.LoginService), "StartLogin");
        var loginCheck = AccessTools.Method(typeof(Network), "ShouldBeConnectedToAurora");
        var launchOptions = AccessTools.Method(typeof(Blizzard.GameService.SDK.Client.Integration.BattleNet), "GetLaunchOption", new[] { typeof(string), typeof(bool) });
        if (loginIntent == null || tokenFetch == null || tokenRefresh == null || loginStart == null || loginCheck == null || launchOptions == null)
            throw new MissingMethodException("Required offline authentication guard hook missing");
        harmony.Patch(loginIntent,
            prefix: new HarmonyMethod(typeof(OfflineProbe), nameof(DisableLoginIntent)));
        harmony.Patch(tokenFetch,
            prefix: new HarmonyMethod(typeof(OfflineProbe), nameof(NoToken)));
        harmony.Patch(tokenRefresh,
            prefix: new HarmonyMethod(typeof(OfflineProbe), nameof(Skip)));
        harmony.Patch(loginStart,
            prefix: new HarmonyMethod(typeof(OfflineProbe), nameof(Skip)));
        harmony.Patch(loginCheck,
            prefix: new HarmonyMethod(typeof(OfflineProbe), nameof(Disconnected)));
        harmony.Patch(launchOptions,
            prefix: new HarmonyMethod(typeof(OfflineProbe), nameof(NoToken)));
        harmony.Patch(AccessTools.Method(GameTypes.ByName("Blizzard.T5.Core.Utils.GeneralUtils"), "ExitApplication", Type.EmptyTypes),
            prefix: new HarmonyMethod(typeof(OfflineProbe), nameof(TraceExit)));
        harmony.Patch(AccessTools.Method(typeof(Hearthstone.HearthstoneApplication), "Exit", Type.EmptyTypes),
            prefix: new HarmonyMethod(typeof(OfflineProbe), nameof(TraceExit)));
        Application.wantsToQuit += () => { TraceExit(); try { ClientBridge.ConfirmStartupOnQuit(); } catch (Exception) { } return true; };
        StartupTiming.Mark("login-guard-patches");
        Network.SetShouldBeConnectedToAurora(false);
        foreach (string address in new[] { "1.1.1.1", "192.168.1.1" })
        {
            using (var socket = new TcpClient())
            {
                var attempt = socket.BeginConnect(address, 443, null, null);
                if (attempt.AsyncWaitHandle.WaitOne(1500))
                {
                    try { socket.EndConnect(attempt); } catch (SocketException) { }
                }
                if (socket.Connected)
                {
                    Logger.LogFatal("NETWORK CANARY CONNECTED " + address);
                    Application.Quit(41);
                    return;
                }
            }
        }
        Logger.LogInfo("NETWORK_CANARIES_BLOCKED; login token fetching disabled");
        StartupTiming.Mark("network-canaries");
        // Register before native parsing so the account-only checkbox reloads as a local bool.
        if (AccessTools.Field(typeof(LocalOptions), "m_path").GetValue(LocalOptions.Get()) != null)
            throw new InvalidOperationException("Native options initialized before local settings registration");
        OptionDataTables.s_typeMap[Option.SPECTATOR_OPEN_JOIN] = typeof(bool);
        Options.Get().GetClientOptions()[Option.SPECTATOR_OPEN_JOIN] = "spectatoropenjoin";
        Logger.LogInfo("LOCAL_SETTINGS_REGISTERED before native options load");
        IdleThinkEmotes = Config.Bind("Gameplay", "IdleThinkEmotes", false,
            "Native idle 'thinking' voice lines are off by default: SoloStone is play-at-your-own-pace. Enable to restore native behaviour.");
#if !SOLOSTONE_RELEASE
        ForceBoardId = Config.Bind("Gameplay", "ForceBoardId", 0,
            "0 = the player's board (shop Themes > Boards); any other BOARD id forces that board (verification aid; ignores the allowlist and ownership, must exist in the client's BOARD table). Re-read at each match start when this file changed.");
        try { forceBoardStamp = System.IO.File.GetLastWriteTimeUtc(Config.ConfigFilePath); } catch (Exception) { }
#endif
        StartupTiming.Mark("settings-seed-config");
        GoldenPilot.ConfigureLethalHost(launch.Deck);
        Logger.LogInfo("NPC_LETHAL_MODE host=" + (launch.Deck ? "deck" : "windows")
            + " workers=" + (GoldenPilot.ParallelLethalEnabled ? 2 : 1));
        // Lanes 1-3 are windowed test clients: keep them out of the owner's way and out of the shared display options.
        if (launch.LaneId != 0) LaneWindow.Install(harmony, Logger);
        StartupTiming.Mark("lethal-lane-window");
        ClientBridge.Initialize(harmony, Logger, launch);
        LaneMute.Initialize(harmony, Logger, launch);
        NativeHelpUI.Lane = launch.LaneId;
        StartupTiming.Mark("lane-mute");
        HubTheme.Initialize(harmony, Logger, Config);
        StartupTiming.Mark("hub-theme");
        DeckExperience.Initialize(harmony, Logger, Config, launch);
        StartupTiming.Mark("deck-experience");
        NativePowerSource.Initialize(harmony, Logger, launch.Deck);
        NativeStarterDecks.Initialize(harmony, Logger);
        NativeTurnTimerOption.Initialize(harmony, Logger, Config);
        NativeFpsCounter.Initialize(harmony, Logger, Config);
        NativeDeckInferenceOption.Initialize(harmony, Logger, Config);
        NativeReportUI.Initialize(harmony, Logger, launch);
#if !SOLOSTONE_RELEASE
        NativeUiGestures.Initialize(new Harmony(ClientBindings.LabOwner), Logger);
#endif
        NativeGameMenuTidy.Initialize(harmony, Logger);
        StartupTiming.Mark("power-source-menu-tidy");
        NativeTestControl.Initialize(launch, Logger);
        StartupTiming.Mark("test-control");
#if SOLOSTONE_RELEASE
        // The debug box and the VFX ring telemetry are dev knobs: a public build leaves them out of the player's config (I2c).
        NativeFxTelemetry.Initialize(harmony, Logger, runId, () => false, Reports);
#else
        var matchDebugOverlay = Config.Bind("Diagnostics", "MatchDebugOverlay", false,
            "Show the plugin's in-match debug box (rank, gold, opponent, Concede shortcut). Off for players; tooling does not need it.");
        LocalLadder.ShowMatchDebugOverlay = () => matchDebugOverlay.Value;
        var fxTelemetry = Config.Bind("Diagnostics", "FxTelemetry", false,
            "Enable passive spell VFX ring telemetry during matches even if the always-on match journal is unavailable. Test-control mode also enables this.");
        NativeFxTelemetry.Initialize(harmony, Logger, runId, () => fxTelemetry.Value, Reports);
#endif
        StartupTiming.Mark("fx-telemetry");
#if STANDARD_PROOF
        string pluginHash = HashFile(typeof(OfflineProbe).Assembly.Location);
        string engineHash = HashFile(typeof(SabberStoneCore.Model.Game).Assembly.Location);
        string marker = Path.Combine(Reports, "standard-plugin-loaded.txt");
        File.WriteAllLines(marker + ".tmp", new[] {
            runId, identity, launch.SnapshotId, launch.ProfilePath, pluginHash, engineHash, DateTime.UtcNow.ToString("o") });
        File.Move(marker + ".tmp", marker);
#else
#if !SOLOSTONE_INSTALLER
        File.WriteAllText(Path.Combine(Reports, "plugin-loaded.txt"), runId + "\n" + identity + "\n" + DateTime.UtcNow.ToString("o"));
#endif
        ClientChecks.ArmCardCheck(Time.realtimeSinceStartup);
#endif
    }

#if !STANDARD_PROOF
    // V2, then the save hold's release. Runs every frame until the client's card records are in. A client with no card pool cards never gets past here.
    private void TickSaveHold()
    {
        try
        {
            ClientChecks.Step step = ClientChecks.Tick(Time.realtimeSinceStartup);
            if (step == ClientChecks.Step.Waiting) return;
            if (step != ClientChecks.Step.Passed)
            {
                enabled = false;
                Application.Quit(step == ClientChecks.Step.Stuck ? ClientChecks.ExitStuck : ClientChecks.ExitBlocked);
                return;
            }
#if SOLOSTONE_RELEASE
            try { NativeUIProbe.AuditBeforeSave(); }
            catch (NativeCardMismatchException error)
            {
                Logger.LogError("NATIVE_UI_BLOCKED " + error);
                try { ClientChecks.Refuse("native card audit", error.Message); }
                catch (Exception reportError) { Logger.LogError("CLIENT_CHECK refusal report failed: " + reportError); }
                enabled = false;
                Application.Quit(ClientChecks.ExitBlocked);
                return;
            }
#endif
            ClientChecks.ReleaseCardCheck();
            StartupTiming.Milestone("client-checks-passed");
            ClientChecks.ReportCoverage();
            SaveHold.Release();
            OfflineLaunchContext launch = launchContext;
#if SOLOSTONE_INSTALLER
            InstallerRuntime.Prepare(launch.ResolveSaves);
            InstallerRuntime.Prepare(() => OfflineGuard.SeedProfileIfMissing(launch, Path.Combine(Root, "fixture.xml")));
#else
            launch.ResolveSaves();
            OfflineGuard.SeedProfileIfMissing(launch, Path.Combine(Root, "fixture.xml"));
#endif
            ClientBridge.OpenSaves();
            StartupTiming.Milestone("save-hold-released");
#if SOLOSTONE_INSTALLER
            // The launcher's health check: this release runs on this client, checks included. It waits for this file.
            string marker = launch.RunId + "\n" + WindowsIdentity.GetCurrent().Name + "\n" + DateTime.UtcNow.ToString("o");
            InstallerRuntime.Prepare(() => File.WriteAllText(Path.Combine(Reports, "plugin-loaded.txt"), marker));
#endif
        }
        catch (Exception error) { Fatal(error); }
    }
#endif

#if STANDARD_PROOF
    private static string HashFile(string path)
    {
        return ReportExport.HashFile(path).ToUpperInvariant();
    }
#endif

    [DllImport("kernel32", CharSet = CharSet.Ansi)] private static extern IntPtr GetModuleHandleA(string name);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);

    // Wine's ntdll exports wine_get_version; Windows' never does.
    private static bool RunningUnderWine()
    {
        IntPtr ntdll = GetModuleHandleA("ntdll.dll");
        return ntdll != IntPtr.Zero && GetProcAddress(ntdll, "wine_get_version") != IntPtr.Zero;
    }

    private static void DisableLoginIntent(ref bool __0) { __0 = false; }
    private static bool NoToken(ref string __result) { __result = null; return false; }
    private static bool Skip() { return false; }
    private static bool Disconnected(ref bool __result) { __result = false; return false; }
    private static void TraceExit() { diagnosticLog.LogWarning("EXIT_REQUEST " + Environment.StackTrace); }

    private void Update()
    {
#if !STANDARD_PROOF
        TickSaveHold();
        if (!enabled) return;
#endif
        NativeReportUI.Update();
        if (startupMilestones) TraceStartupMilestones();
        double stage = DeckExperience.TraceStage("outside-update", previousUpdateEnd);
        LaneWindow.Tick();
        stage = DeckExperience.TraceStage("lane-window", stage);
        NativeProfileSelector.Update();
        stage = DeckExperience.TraceStage("profile-selector", stage);
        ClientBridge.Update();
        stage = DeckExperience.TraceStage("client-bridge", stage);
        NativeTestControl.Update();
        stage = DeckExperience.TraceStage("test-control", stage);
        NativeFxTelemetry.Tick();
        stage = DeckExperience.TraceStage("fx-telemetry", stage);
        LaneMute.Tick();
        stage = DeckExperience.TraceStage("lane-mute", stage);
        HubTheme.Tick(Time.realtimeSinceStartup);
        stage = DeckExperience.TraceStage("hub-theme", stage);
        DeckExperience.Tick();
        NativeStarterDecks.Tick();
        NativeOptionsPace.Tick();
        NativeFpsCounter.Tick();
        NativeDeckInferenceOption.Tick();
        stage = DeckExperience.TraceStage("deck-experience", stage);
        // client.png is captured on demand: a tool drops screenshot-request.txt and waits for a fresh client.png.
        // A periodic full-resolution PNG encode on the main thread was the routine's main hitch.
#if !SOLOSTONE_INSTALLER || SOLOSTONE_TEST_HOOKS
        if (StatusRoutine.Due(Time.realtimeSinceStartup, ref nextScreenshotPoll, StatusRoutine.ScreenshotPollSeconds)
            && StatusRoutine.ConsumeScreenshotRequest(Reports))
            ScreenCapture.CaptureScreenshot(Path.Combine(Reports, StatusRoutine.ScreenshotFile));
#endif
        stage = DeckExperience.TraceStage("screenshot-poll", stage);
#if !SOLOSTONE_RELEASE
        // The lab's state files (ladder-state, native-ui-state, match-state) feed test tooling only; a public build writes none.
        if (StatusRoutine.Due(Time.realtimeSinceStartup, ref nextStatus, 3))
        {
            ClientBridge.WriteStatus();
            Logger.LogInfo("STATUS seconds=" + Time.realtimeSinceStartup + " scene=" + (SceneMgr.Get() == null ? "none" : SceneMgr.Get().GetMode().ToString()));
        }
#endif
        previousUpdateEnd = DeckExperience.TraceStage("status", stage);
    }

    private bool startupMilestones = true;
    private void TraceStartupMilestones()
    {
        StartupTiming.Milestone("first-update");
        if (SceneMgr.IsInitialized() && SceneMgr.Get().GetMode() == SceneMgr.Mode.HUB && !SceneMgr.Get().IsTransitioning())
        {
            StartupTiming.Milestone("hub");
            startupMilestones = false;
        }
    }

    private void OnGUI() { LocalLadder.Draw(); NativeBrandLabel.Draw(); NativeFpsCounter.Draw(); }
    private void OnApplicationPause(bool paused)
    {
        NativeSpellWatchdog.ApplicationPause(paused);
        ClientBridge.ChoiceApplicationPause(paused);
        if (paused) NativeCollectionStatus.FlushPendingSeen(true, true);
    }
    private void OnApplicationFocus(bool focused)
    {
        NativeSpellWatchdog.ApplicationFocus(focused);
        ClientBridge.ChoiceApplicationFocus(focused);
    }
    private void OnApplicationQuit() { ClientBridge.CloseMatchJournal(); NativeFxTelemetry.CloseDisk(); NativeCollectionStatus.FlushPendingSeen(true, true); }
    private void OnDestroy() { DeckExperience.Shutdown(); NativeCollectionStatus.FlushPendingSeen(true, true); NativeTestControl.Shutdown(); LocalLadder.Shutdown(); }
}

// Test lanes (1-3) share the Windows account, its Unity PlayerPrefs and the native local options file with the owner's lane 0.
// They start windowed (Run-Client.ps1 flags), must never write window geometry into those shared stores, never take the
// foreground from the owner's game, and keep running while unfocused. Nothing here may throw out of Awake/Update.
internal static class LaneWindow
{
    private const uint HWND_BOTTOM = 1, SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10;
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    private static readonly EnumWindowsProc Visit = VisitWindow;
    private static bool active;
    private static uint ownProcess;
    private static IntPtr foreground;
    private static float stopAt, nextTick;

    internal static void Install(Harmony harmony, BepInEx.Logging.ManualLogSource log)
    {
        try
        {
            Application.runInBackground = true;
            // GraphicsManager would persist this lane's window size/position (shared native options file) and re-apply a saved window position.
            PatchSkip(harmony, log, "Shutdown", nameof(KeepSharedWindowOptions));
            PatchSkip(harmony, log, "InitializeScreen", nameof(KeepSharedWindowOptions));
            ownProcess = (uint)Process.GetCurrentProcess().Id;
            active = true;
            stopAt = Time.realtimeSinceStartup + 25f;
            PushBehind();
            log.LogInfo("LANE_WINDOW background windowed lane; runInBackground set");
        }
        catch (Exception error) { log.LogWarning("LANE_WINDOW setup failed: " + error.Message); }
    }

    private static void PatchSkip(Harmony harmony, BepInEx.Logging.ManualLogSource log, string target, string patch)
    {
        var original = AccessTools.Method(typeof(GraphicsManager), target, Type.EmptyTypes);
        if (original == null) { log.LogWarning("LANE_WINDOW GraphicsManager." + target + " missing; shared window options not protected"); return; }
        harmony.Patch(original, prefix: new HarmonyMethod(typeof(LaneWindow), patch));
    }

    // Harmony prefix: false skips the original, so the lane neither writes nor applies shared window options.
    private static bool KeepSharedWindowOptions() { return false; }

    // Startup only: if the OS activated the new lane window, send it to the bottom without activating anything.
    internal static void Tick()
    {
        if (!active) return;
        try
        {
            float now = Time.realtimeSinceStartup;
            if (now > stopAt) { active = false; return; }
            if (now < nextTick) return;
            nextTick = now + 0.5f;
            PushBehind();
        }
        catch (Exception) { active = false; }
    }

    private static void PushBehind()
    {
        foreground = GetForegroundWindow();
        EnumWindows(Visit, IntPtr.Zero);
    }

    private static bool VisitWindow(IntPtr window, IntPtr state)
    {
        uint process;
        GetWindowThreadProcessId(window, out process);
        if (process == ownProcess && IsWindowVisible(window) && GetWindow(window, 4) == IntPtr.Zero
            && window == foreground)
        {
            SetWindowPos(window, (IntPtr)HWND_BOTTOM, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
        }
        return true;
    }
}
