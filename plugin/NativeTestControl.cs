#if !SOLOSTONE_RELEASE
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using BepInEx.Logging;
using HarmonyLib;
using MiniJSON;
using Networking;
using UnityEngine;
using SabberStoneCore.Tasks.PlayerTasks;

internal sealed partial class NativeTestControl
{
    private string Reports => launch.Reports;
#if !SOLOSTONE_INSTALLER || SOLOSTONE_TEST_HOOKS
    private string FlagPath => Reports + @"\enable-test-control";
#endif
    private string CaptureRoot => Reports + @"\control";
    private const int TradeKeyword = 1720;
    private static NativeTestControl current;
    private readonly OfflineLaunchContext launch;
    private readonly ManualLogSource log;
    private readonly int mainThread;
    private readonly ControlLedger ledger = new ControlLedger();
    private readonly List<PendingWait> waits = new List<PendingWait>();
    private readonly List<PendingCapture> captures = new List<PendingCapture>();
    private readonly List<PendingUi> uiReads = new List<PendingUi>(), uiWaits = new List<PendingUi>();
    private readonly UiTracker uiTracker = new UiTracker();
    private UiSnapshot uiSample;
    private int uiSampleFrame = -1;
    private PendingPress press;
    private ControlTransport transport;
    private string token, fault, faultMatchId, lastRevision, lastSettledAction;
    private ControlOperation active;
    private ControlConnection owner;
    private ControlConnection controller;
    private ControlEnvelope pending;
    private float nextFlagCheck;
    private int sampledFrame = -1;
    private string expectedSend, terminalSeenAction;
    private int expectedOptions, expectedIndex, expectedTarget, expectedSub, expectedPosition;
    private int[] expectedHeld;
    private bool inSubmission, observingSend, engineAccepted;
    private long sends, packets, mutations;
    private int stableFrames, choiceBoundaryFrames, mulliganFrames, choiceReadyFrames, endTurnFrames;
    private string choiceReadyIdentity, submittedChoiceRevision, endTurnIdentity;
    private string mulliganIdentity, choiceEngineRevision;
    private readonly BoardActorFaultTracker boardFaults = new BoardActorFaultTracker();
    private long choiceSends, choiceMutations;
    // Send/mutation counters when a native drag began: proof, at its end, that the drag changed nothing and may be rejected.
    private long dragSends, dragMutations;
    private string dragEngineRevision;
    // A send right after a rejected drag is a deferred send the drag did not account for: it must freeze, not slip through unattributed.
    private float orphanDragUntil;
    private bool mulliganReleasePending;
    private int mulliganToggleFrame;
    private DateTime retryAfter;
    // Fixed, read-only private API adapters. Fail closed when this pinned client changes.
    private static readonly MethodInfo BlockingPower = TurnInput.BlockingPower;
    private static readonly FieldInfo DelayedTasks = TurnInput.DelayedTasks;
    private static readonly FieldInfo QueuedChoices = AccessTools.Field(typeof(GameState), "m_queuedChoices");
    // Power-queue stall readout (live 2026-10-30: queue stuck at 10 with no current task after an Imbued hero power).
    private static readonly FieldInfo BusyTaskList = AccessTools.Field(typeof(PowerProcessor), "m_busyTaskList");
    private static readonly MethodInfo CanProcessQueue = AccessTools.Method(typeof(GameState), "CanProcessPowerQueue", Type.EmptyTypes);
    private static readonly MethodInfo WaitForChoices = AccessTools.Method(typeof(GameState), "MustWaitForChoices", Type.EmptyTypes);
    private static readonly MethodInfo MulliganRelease = AccessTools.Method(typeof(MulliganManager), "OnMulliganButtonReleased", new[] { typeof(UIEvent) });

    private static readonly FieldInfo MulliganWaiting = AccessTools.Field(typeof(MulliganManager), "m_waitingForUserInput");
    private static readonly MethodInfo TweenCount = AccessTools.Method(GameTypes.ByName("iTween"), "Count", new[] { typeof(GameObject) });

    internal sealed class View
    {
        internal readonly JsonNode State = new JsonNode();
        internal readonly List<string> Blockers = new List<string>(), ActionBlockers = new List<string>(), Differences = new List<string>();
        internal string MatchId;
        internal int OptionsId = -1, ChoiceId = -1, ClientChoiceId = -1;
        internal int QueuedChoiceCount = -1;
        internal bool Gameplay, Terminal, ClientGameOver, CanMulligan, MulliganReady, ClientMulliganPending, ChoiceBoundary, ChoiceReady, CanChoose, ClientChoicePending;
        internal bool CanAct, EndTurnReady;
        internal string ChoiceRevision, EndTurnBlocker = "noGameplay";
        internal string EngineRevision;
        internal string Revision;
    }
    private sealed class PendingWait
    {
        internal ControlEnvelope Envelope;
        internal DateTime Deadline;
    }
    private sealed class PendingUi
    {
        internal ControlEnvelope Envelope;
        internal DateTime Deadline;
    }
    private sealed class PendingPress
    {
        internal ControlOperation Operation;
        internal ControlConnection Owner;
        internal DateTime Deadline;
        internal UiElement Target;
        internal int PressFrame = -1;
        internal int RevealAttempts, RevealFrame = -1, RevealPendingFrame = -1;
        internal bool Right;
        // "press" (left or right click), "ui-scroll", "ui-hover", "ui-unhover" or "ui-drag".
        internal string Gesture = "press";
        internal UiElement DragTarget;
        internal int TargetIndex = -1, DragStage, StageFrame = -1, ReachedFrames;
    }
    private sealed class PendingCapture
    {
        internal ControlOperation Operation;
        internal ControlConnection Connection;
        internal string Path;
        internal long Length;
        internal int Stable, Frame;
    }
    private NativeTestControl(OfflineLaunchContext launch, ManualLogSource log)
    { this.launch = launch; this.log = log; mainThread = Thread.CurrentThread.ManagedThreadId; }
    internal static void Initialize(OfflineLaunchContext launch, ManualLogSource log) { current = new NativeTestControl(launch, log); }
    internal static void AssertMainThread()
    {
        if (current != null && current.mainThread != Thread.CurrentThread.ManagedThreadId)
            throw new InvalidOperationException("Test control game access outside Unity thread");
    }
    internal static void Update()
    {
        if (current == null) return;
        if (current.transport != null) SamplePress();
        current.Tick();
    }
    internal static bool DiagnosticsEnabled => current?.transport != null;
    internal static void Shutdown() { current?.Stop("Plugin shutdown"); current = null; }
    internal static string SemanticNonce => current?.active != null && (current.inSubmission || current.observingSend) ? "semantic:" + current.active.Request.Id : null;
    internal static string AttributeTrace(string record)
    {
        if (SemanticNonce == null) return record;
        var row = Json.Deserialize(record) as JsonNode;
        if (row == null) throw new InvalidOperationException("Invalid proof trace");
        row.Add("inputKind", "semantic"); row.Add("requestId", current.active.Request.Id);
        row.Add("runId", current.launch.RunId); row.Add("matchId", current.active.Request.Text("matchId"));
        return Json.Serialize(row);
    }
#if !SOLOSTONE_INSTALLER || SOLOSTONE_TEST_HOOKS
    private bool Enabled(out string readToken)
    {
        readToken = null;
        try
        {
            if (!File.Exists(FlagPath)) return false;
            OfflineGuard.RejectReparsePoints(FlagPath);
            using (var file = new FileStream(FlagPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete | FileShare.Write))
            {
                if (file.Length > 512) return false;
                using (var reader = new StreamReader(file, TestControlProtocol.Utf8, false))
                    return TestControlProtocol.Flag(reader.ReadToEnd(), launch.RunId, out readToken);
            }
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException || error is ArgumentException) { return false; }
    }
#else
    // An installer release has no test control: no flag file, no listener.
    private bool Enabled(out string readToken) { readToken = null; return false; }
#endif
    private void Stop(string reason)
    {
        AssertMainThread();
        try { NativeGameplayDrag.Cancel(); }
        catch (Exception error) { log.LogWarning("TEST_CONTROL drag cleanup: " + error.GetBaseException().Message); }
        transport?.Dispose(); transport = null; token = null;
        nextFlagCheck = Time.realtimeSinceStartup + 1f;
        waits.Clear(); ResetSampling(); observingSend = false; controller = null; pending = null;
        foreach (var capture in captures) Reject(capture.Operation, reason);
        captures.Clear();
        uiReads.Clear(); uiWaits.Clear(); uiTracker.Reset(); uiSampleFrame = -1;
        if (press != null)
        {
            // Mouse down without mouse up leaves native state unknown.
            press.Operation.Phase = press.PressFrame >= 0 ? "fault" : "rejected";
            press.Operation.Detail["error"] = reason + "; reconcile action-status before retry";
            if (press.Operation.Phase == "fault") LatchFault(reason);
            if (press.Gesture == "ui-drag") NativeUiGestures.EndDrag();
            press = null;
        }
        if (active != null)
        {
            active.Phase = active.Phase == "fault" || active.Sends != 0 || active.Detail.ContainsKey("nativeInvocationStarted") ? "fault" : "rejected";
            active.Detail["error"] = reason + "; reconcile action-status before retry";
            if (active.Phase == "fault") LatchFault(reason);
            active = null; owner = null; inSubmission = false;
        }
    }
    // The last mouse press as this client saw it. A real drag is evidence only if focus held from the frame the button went down to the
    // frame it came up; the input tool's own window can take focus for a moment before that without touching the press.
    // `HeldEntity` is the first card the client held while that press was down (-1: none seen): a hand fan overlaps, and the card under
    // the pointer can differ from the one aimed at; the press tool compares it with the entity it asked for instead of guessing.
    private static readonly TestControlRules.PressRecord pressRecord = new TestControlRules.PressRecord();
    private static void SamplePress()
    {
        // Test control only: a release build never connects (Enabled is false), so this never reads the input manager there. A fault here must not reach Update.
        try
        {
            var input = InputManager.Get();
            pressRecord.Sample(input?.LeftMouseButtonDown == true, input?.GetHeldCard()?.GetEntity()?.GetEntityId() ?? -1, Time.frameCount,
                NativeSpellWatchdog.Lifecycle == "active", NativeSpellWatchdog.FocusLosses);
        }
        catch (Exception) { }
    }
    private void Tick()
    {
        float now = Time.realtimeSinceStartup;
        if (transport == null && now < nextFlagCheck) return;
        AssertMainThread();
        string readToken = token;
        if (now >= nextFlagCheck)
        {
            nextFlagCheck = now + (transport == null ? 1f : .5f);
            if (!Enabled(out readToken))
            {
                if (transport != null) Stop("Control flag disabled");
                nextFlagCheck = now + 1f;
                return;
            }
            if (transport != null && token != readToken) Stop("Control token changed");
        }
        if (transport == null)
        {
            if (DateTime.UtcNow < retryAfter) return;
            try
            {
                OfflineGuard.RejectReparsePoints(CaptureRoot);
                Directory.CreateDirectory(CaptureRoot);
                transport = new ControlTransport(launch.RunId, readToken, launch.ControlPort); token = readToken;
                nextFlagCheck = now + .5f;
                log.LogInfo("TEST_CONTROL_LISTENING loopback:" + launch.ControlPort + " run=" + launch.RunId);
            }
            catch (Exception error)
            {
                retryAfter = DateTime.UtcNow.AddSeconds(3);
                log.LogWarning("TEST_CONTROL_UNAVAILABLE " + error.GetType().Name); return;
            }
        }
        try
        {
            if (NativeGameplayDrag.Running)
            {
                if (active == null || active.Finished) NativeGameplayDrag.Cancel();
                else
                {
                    inSubmission = true;
                    try
                    {
                        bool released = NativeGameplayDrag.Tick(active.Detail);
                        if (active.Sends == 1)
                        {
                            active.Detail[active.Request.Command == "choice-click" ? "clickAccepted" : "dragAccepted"] = true;
                        }
                        if (released)
                        {
                            string cleanup = null;
                            try { NativeGameplayDrag.Cancel(); }
                            catch (Exception error) { cleanup = error.GetBaseException().Message; active.Detail["cleanupError"] = cleanup; }
                            if (cleanup != null) Freeze("Native drag cleanup failed: " + cleanup);
                            else if (active.Sends == 0)
                            {
                                // Cleanup restored the input state and no send happened: the match is unchanged, so only this action fails.
                                if (DragUnsent()) RejectUnsentDrag("Native drag released without a send (a click on a hand card only picks it up; "
                                    + "play it with --position or --target); nothing changed, retry");
                                else Freeze("Native drag released without expected send; inspect targeting before retry");
                            }
                        }
                    }
                    catch (Exception error)
                    {
                        string cleanup = null;
                        try { NativeGameplayDrag.Cancel(); }
                        catch (Exception failure) { cleanup = failure.GetBaseException().Message; active.Detail["cleanupError"] = cleanup; }
                        if (cleanup == null && DragUnsent()) RejectUnsentDrag("Native drag failed before any send: " + error.GetBaseException().Message);
                        else Freeze("Native drag failed: " + error.GetBaseException().Message);
                    }
                    finally { inSubmission = false; }
                }
            }
            TickCaptures();
            waits.RemoveAll(wait => !wait.Envelope.Connection.Open);
            if (press != null || uiReads.Count != 0 || uiWaits.Count != 0) TickUi();
            View view = null;
            // Submitted work outlives its controller; only reply delivery depends on the connection.
            if (active?.Finished == false || faultMatchId != null || waits.Count != 0 || mulliganFrames > 0 || choiceReadyFrames > 0)
            {
                view = SampleView();
                FinishActive(view);
                Derive(view);
                TickWaits(view);
            }
            bool mutated = false;
            for (int count = 0; count < TestControlProtocol.MaxQueue && transport != null; count++)
            {
                ControlEnvelope envelope = pending;
                pending = null;
                if (envelope == null && !transport.Requests.Take(out envelope)) break;
                if (!envelope.Connection.Open) continue;
                controller = envelope.Connection;
                if (envelope.Request.Mutation && mutated)
                {
                    // Keep ordering bounded without performing a second mutation in this frame.
                    pending = envelope;
                    break;
                }
                if (NeedsView(envelope.Request) && view == null)
                {
                    bool starting = sampledFrame < 0 || (sampledFrame != Time.frameCount && sampledFrame != Time.frameCount - 1);
                    view = SampleView();
                    // Keep the request queued for two consecutive samples after an idle sampling gap.
                    if (starting) { pending = envelope; break; }
                }
                if ((envelope.Request.Command == "mulligan-confirm" || (envelope.Request.Command == "choose" || envelope.Request.Command == "choice-click")) && ledger.Find(envelope.Request.Id) == null
                    && fault == null && active == null && !((envelope.Request.Command == "choose" || envelope.Request.Command == "choice-click") ? view.CanChoose : view.CanMulligan)
                    && envelope.Request.Text("matchId") == view.MatchId && envelope.Request.Integer("choiceId") == view.ChoiceId
                    && (DateTime.UtcNow - envelope.Request.ReceivedUtc).TotalSeconds <= 10)
                { pending = envelope; break; }
                Handle(envelope, view);
                if (envelope.Request.Mutation) { mutated = true; view = null; }
            }
            if (sampledFrame != Time.frameCount) ResetSampling();
        }
        catch (Exception error) { Freeze("Control tick failed: " + error.GetType().Name + ": " + error.Message); }
    }
    private static bool NeedsView(ControlRequest request) => (request.Mutation && request.Command != "shop-purchase" && request.Command != "open-packs"
        && !(request.Command == "press" && UiRules.IsMatchEscape(request.Text("element")))) || request.Command == "state"
        || request.Command == "options" || request.Command == "choices" || request.Command == "wait-until-idle";
    private void ResetSampling()
    {
        stableFrames = 0; choiceBoundaryFrames = 0; mulliganFrames = 0; choiceReadyFrames = 0; endTurnFrames = 0;
        choiceReadyIdentity = null; mulliganIdentity = null; endTurnIdentity = null; lastRevision = null; sampledFrame = -1;
    }
    private View SampleView()
    {
        View view = ReadSafeView();
        ClearEndedFault(view);
        if (sampledFrame != Time.frameCount)
        {
            if (sampledFrame != Time.frameCount - 1) ResetSampling();
            string revision = view.Revision + ":" + sends + ":" + packets;
            stableFrames = view.Blockers.Count == 0 && view.Differences.Count == 0
                ? (revision == lastRevision ? stableFrames + 1 : 1) : 0;
            choiceBoundaryFrames = view.ChoiceBoundary && view.Differences.Count == 0
                ? (revision == lastRevision ? choiceBoundaryFrames + 1 : 1) : 0;
            string identity = view.MatchId + ":" + view.ChoiceId;
            mulliganFrames = view.MulliganReady ? (identity == mulliganIdentity ? mulliganFrames + 1 : 1) : 0;
            mulliganIdentity = identity;
            string choiceIdentity = identity + ":" + view.ChoiceRevision + ":" + revision;
            choiceReadyFrames = view.ChoiceReady && view.Differences.Count == 0 ? (choiceIdentity == choiceReadyIdentity ? choiceReadyFrames + 1 : 1) : 0;
            choiceReadyIdentity = choiceIdentity;
            // Button settling stays out of the revision: its animation must not reset other actions' quiet frames.
            string turnIdentity = view.MatchId + ":" + view.OptionsId;
            endTurnFrames = view.EndTurnBlocker == null ? (turnIdentity == endTurnIdentity ? endTurnFrames + 1 : 1) : 0;
            endTurnIdentity = turnIdentity;
            lastRevision = revision; sampledFrame = Time.frameCount;
        }
        Derive(view);
        view.CanMulligan = view.MulliganReady && mulliganFrames >= TestControlProtocol.MulliganReadyFrames && active == null && fault == null;
        view.State["canConfirmMulligan"] = view.CanMulligan;
        view.State["mulliganReadyFrames"] = mulliganFrames;
        view.CanChoose = view.ChoiceReady && choiceReadyFrames >= TestControlProtocol.ChoiceReadyFrames && active == null && fault == null;
        view.State["canChoose"] = view.CanChoose;
        view.State["choiceReadyFrames"] = choiceReadyFrames;
        return view;
    }
    // Option-mode readiness from this frame's counters; recomputed when settlement clears the active mutation mid-frame.
    private void Derive(View view)
    {
        view.CanAct = stableFrames >= 2 && active == null && fault == null && view.ActionBlockers.Count == 0 && view.Differences.Count == 0;
        string endTurn = view.EndTurnBlocker ?? (endTurnFrames < TestControlProtocol.EndTurnReadyFrames ? "endTurnSettling" : null);
        view.EndTurnReady = view.CanAct && endTurn == null;
        view.State["endTurnReady"] = view.EndTurnReady; view.State["endTurnBlocker"] = endTurn;
        view.State["endTurnReadyFrames"] = endTurnFrames;
    }
    // Once per entity and verdict per match, with the queue state that decides what the line means.
    private readonly HashSet<string> unrevealedLogged = new HashSet<string>();
    private readonly TestControlRules.UnrevealedConfirmation unrevealedSeen = new TestControlRules.UnrevealedConfirmation();
    private string unrevealedMatch;
    private void LogUnrevealed(View view, List<string> unrevealed, int turn)
    {
        if (unrevealedMatch != view.MatchId) { unrevealedLogged.Clear(); unrevealedMatch = view.MatchId; }
        foreach (string detail in unrevealed)
            if (unrevealedLogged.Add(detail))
                log.LogWarning("TEST_CONTROL_UNREVEALED_ENTITY match=" + view.MatchId + " frame=" + Time.frameCount + " turn=" + turn + " packets=" + packets + " choiceBoundary=" + view.ChoiceBoundary
                    + " blockers=[" + string.Join(",", view.Blockers) + "] " + detail);
    }
    private string lastReadError;
    private View ReadSafeView()
    {
        try { return ClientBridge.ReadTestControlView(); }
        catch (Exception error)
        {
            string readError = "Snapshot failed: " + error.GetType().Name + ": " + error.Message;
            var view = new View { MatchId = LocalLadder.MatchId, Revision = "snapshot-fault" };
            view.Blockers.Add(readError); view.ActionBlockers.Add(readError);
            view.State["readError"] = readError;
            view.State["readErrorTrace"] = error.ToString();
            // Once per distinct failure: the message alone does not say where a snapshot read broke.
            if (error.ToString() != lastReadError) { lastReadError = error.ToString(); log.LogWarning("TEST_CONTROL_SNAPSHOT_FAULT " + lastReadError); }
            return view;
        }
    }
    private JsonNode Reply(ControlRequest request, string phase, object data = null, string error = null)
    {
        var reply = new JsonNode {
            { "v", 1 }, { "runId", launch.RunId }, { "snapshotId", launch.SnapshotId },
            { "matchId", LocalLadder.MatchId }, { "frame", Time.frameCount }, { "id", request.Id }, { "phase", phase }
        };
        if (data != null) reply.Add("data", Clone(data));
        if (error != null) reply.Add("error", error);
        return reply;
    }
    private void OperationReply(ControlConnection connection, ControlRequest request, ControlOperation operation)
    {
        var data = new JsonNode {
            { "actionId", operation.Request.Id }, { "command", operation.Request.Command }, { "sends", operation.Sends },
            { "detail", operation.Detail }, { "frozen", fault != null }, { "fault", fault }
        };
        connection?.Reply(Reply(request, operation.Phase, data));
    }
    private static JsonNode NpcCardBacks()
    {
        var records = GameDbf.CardBack?.GetRecords();
        if (records == null) throw new InvalidOperationException("CardBackDbf unavailable");
        return new JsonNode { { "cardbacks", TestControlProtocol.List(records.OrderBy(record => record.ID)
            .Select(record => (object)new JsonNode { { "id", record.ID }, { "name", record.Name.GetString() } })) } };
    }

    private void Handle(ControlEnvelope envelope, View view)
    {
        var request = envelope.Request; var connection = envelope.Connection;
        if (!connection.Open) return;
        try
        {
            if (NeedsView(request) && view?.State.ContainsKey("readError") == true) throw new InvalidOperationException((string)view.State["readError"]);
            if (request.Command == "hero-skin-spells")
            { connection.Reply(Reply(request, "settled", NativeHeroSkinAudit.Read(request.Text("cardId")))); return; }
            if (!request.Mutation)
            {
                switch (request.Command)
                {
                    case "hello": connection.Reply(Reply(request, "settled", new JsonNode { { "protocol", "test-control/1" }, { "inputKind", "semantic" } })); return;
                    case "crafting-state":
                        AssertMainThread();
                        connection.Reply(Reply(request, "settled", TestControlCrafting.State(LocalLadder.Profile,
                            request.Has("cardIds") ? request.CardIds() : new string[0]))); return;
                    case "shop-catalog":
                        AssertMainThread();
                        connection.Reply(Reply(request, "settled", TestControlShop.Catalog(LocalLadder.Profile,
                            NativeUIProbe.ShopContextReady && ControlBusy().Count == 0))); return;
                    case "cosmetic-check":
                        connection.Reply(Reply(request, "settled", NativeCosmeticCheck.Start(
                            request.Has("productIds") ? request.ProductIds() : new string[0]))); return;
                    case "cosmetic-check-status":
                        connection.Reply(Reply(request, "settled", NativeCosmeticCheck.Status(request.Text("runId")))); return;
                    case "cosmetics-state":
                        connection.Reply(Reply(request, "settled", NativeCosmeticsState.Read(LocalLadder.Profile))); return;
                    case "save-catalog": AssertMainThread(); connection.Reply(Reply(request, "settled", NativeProfileSelector.CatalogState())); return;
                    case "npc-queue": AssertMainThread(); connection.Reply(Reply(request, "settled", NpcQueue(request.Has("mode") ? request.Text("mode") : "ranked", request.Integer("count")))); return;
                    case "buttons": AssertMainThread(); connection.Reply(Reply(request, "settled", NativeButtonKit.State(request.Has("dump") && request.Integer("dump") == 1))); return;
                    case "achievements-state":
                        AssertMainThread(); connection.Reply(Reply(request, "settled", NativeAchievements.State())); return;
                    case "pet-database": connection.Reply(Reply(request, "settled", PetDatabaseExport.Export())); return;
                    case "pet-log": connection.Reply(Reply(request, "settled", PetReactionTelemetry.Read(request.Has("after") ? request.Integer("after") : 0))); return;
                    case "export-coins": connection.Reply(Reply(request, "settled", CosmeticCoinExport.Export())); return;
                    case "export-cardbacks": connection.Reply(Reply(request, "settled", CardBackExport.Export())); return;
                    case "npc-cardbacks": connection.Reply(Reply(request, "settled", NpcCardBacks())); return;
                    case "export-card-db": connection.Reply(Reply(request, "settled", CardDbExport.Export())); return;
                    case "fx-scene": AssertMainThread(); connection.Reply(Reply(request, "settled", FireFxScene.Read(request.Has("scene") ? request.Integer("scene") : -1, request.Has("root") ? request.Integer("root") : -1, request.Has("name") ? request.Text("name") : null, request.Has("cursor") ? request.Integer("cursor") : 0))); return;
                    case "fx-status": connection.Reply(Reply(request, "settled", NativeFxTelemetry.Status(
                        request.Has("name") ? request.Text("name") : null, request.Has("source") ? request.Integer("source") : 0, request.Integer("seconds")))); return;
                    case "fx": connection.Reply(Reply(request, "settled", NativeFxTelemetry.Read(
                        request.Has("after") ? request.Integer("after") : 0, request.Has("limit") ? request.Integer("limit") : 100))); return;
                    case "screenshot": HandleCapture(envelope); return;
                    case "locate": connection.Reply(Reply(request, "settled", Locate(request))); return;
                    case "card-text": AssertMainThread(); connection.Reply(Reply(request, "settled", CardText(request))); return;
                    case "entity": connection.Reply(Reply(request, "settled", ClientBridge.TestControlEntities(request.Text("matchId"), request.EntityIds("entityIds")))); return;
                    case "ui": case "wait-ui": QueueUi(envelope); return;
                    case "state":
                        view.State["grindTier"] = LocalLadder.Profile?.GrindTier.Id;
                        view.State["playMode"] = LocalLadder.Profile == null ? null : LocalLadder.Profile.PrefersCasual ? "casual" : "ranked";
                        view.State["fpsCounter"] = NativeFpsCounter.State();
                        view.State["brandLabel"] = NativeBrandLabel.State();
                        view.State["deckInference"] = NativeDeckInferenceOption.State();
                        view.State["deckGuide"] = NativeDeckPickerGuide.State();
                        view.State["deckGuideCards"] = NativeGuideCards.State();
                        view.State["deckRecord"] = NativeDeckRecord.State();
                        var settledMatch = LocalLadder.Profile?.LastSettledMatch;
                        view.State["settledMatch"] = settledMatch == null ? null : new JsonNode {
                            { "id", settledMatch.Id }, { "result", settledMatch.Result },
                            { "gold", settledMatch.Gold }, { "dust", settledMatch.DustReward },
                            { "rewardVersion", settledMatch.RewardVersion }, { "grindTier", settledMatch.GrindTier },
                            { "npcId", settledMatch.NpcId }, { "npcDeckId", settledMatch.NpcDeckId }
                        };
                        view.State["idle"] = stableFrames >= 2 && active == null && fault == null;
                        view.State["canAct"] = view.CanAct; view.State["frozen"] = fault != null; view.State["fault"] = fault;
                        view.State["sends"] = sends; view.State["injectedPackets"] = packets; view.State["engineMutations"] = mutations;
                        connection.Reply(Reply(request, "settled", view.State)); return;
                    case "options": RequireMatch(request, view); connection.Reply(Reply(request, "settled", Options(view))); return;
                    case "choices": RequireMatch(request, view); connection.Reply(Reply(request, "settled", Choices(view))); return;
                    case "action-status":
                        var operation = ledger.Find(request.Text("actionId"));
                        if (operation == null) throw new InvalidOperationException("Unknown action ID in this run");
                        OperationReply(connection, request, operation); return;
                    case "wait-until-idle":
                        RequireMatch(request, view);
                        var after = ledger.Find(request.Text("afterActionId"));
                        if (after == null || (after.Sends != 1 && after.Phase != "settled") || after.Request.Command == "screenshot"
                            || after.Request.Text("matchId") != view.MatchId) throw new InvalidOperationException("afterActionId has not submitted in this match");
                        if (waits.Count == TestControlProtocol.MaxQueue) throw new InvalidOperationException("Wait queue full");
                        waits.Add(new PendingWait { Envelope = envelope, Deadline = DateTime.UtcNow.AddMilliseconds(request.Integer("timeoutMs")) });
                        connection.Reply(Reply(request, "accepted")); return;
                }
                throw new InvalidOperationException("Unsupported command");
            }
            if (request.Command == "press" || request.Command == "ui-hover" || request.Command == "ui-unhover" || request.Command == "ui-escape" || request.Command == "ui-drag" || request.Command == "ui-scroll") { HandlePress(envelope); return; }
            if (request.Command == "shop-purchase") { HandleShopPurchase(envelope); return; }
            if (request.Command == "open-packs") { HandleOpenPacks(envelope); return; }
            if (request.Command == "squelch" || request.Command == "unsquelch" || request.Command == "emote" || request.Command == "npc-emote" || request.Command == "npc-concede"
                || request.Command == "human-defeat" || request.Command == "premium-unlock-prepare" || request.Command == "achievement-prime" || request.Command == "turn-timer-debug" || request.Command == "spell-stall-arm")
            { HandlePresence(envelope, view); return; }
            // Lookup before busy/stale checks: a retry must return prior status even after the board changed.
            var prior = ledger.Find(request.Id);
            if (prior != null)
            {
                ledger.Accept(request, out bool _);
                if (ReferenceEquals(prior, active) && !prior.Finished) owner = connection;
                OperationReply(connection, request, prior); return;
            }
            ControlOperation next = ledger.Accept(request, out bool added);
            if (!added) throw new InvalidOperationException("Unexpected ledger duplicate");
            if (request.Command == "entity-drag") next.Detail["dragAccepted"] = false;
            if (request.Command == "choice-click") next.Detail["clickable"] = false;
            if (FaultBlocks(request, view)) Reject(next, "Mutations frozen: " + fault);
            else if (active != null && !active.Finished) Reject(next, "Another mutation unresolved");
            else if (press != null) Reject(next, "UI press unresolved: " + press.Operation.Request.Id);
            else if ((DateTime.UtcNow - request.ReceivedUtc).TotalSeconds > 10) Reject(next, "Expired before execution");
            else if (!Enabled(out string enabledToken) || token != enabledToken) { Reject(next, "Control flag changed"); Stop("Control flag changed"); }
            else
            {
                bool terminalNoOp = request.Command == "concede" && view.Terminal;
                try
                {
                    if (terminalNoOp) RequireMatch(request, view);
                    else Validate(request, view);
                    if (request.Command == "choice-click") next.Detail["clickable"] = true;
                }
                catch (Exception error) { Reject(next, error.Message); OperationReply(connection, request, next); return; }
                active = next; owner = connection; engineAccepted = false;
                if (request.Command == "mulligan-confirm" || (request.Command == "choose" || request.Command == "choice-click"))
                {
                    choiceEngineRevision = view.EngineRevision; choiceSends = sends; choiceMutations = mutations;
                    submittedChoiceRevision = view.ChoiceRevision;
                }
                active.Detail["inputKind"] = request.Command == "recover-choice" ? "diagnostic-recovery" : "semantic"; active.Detail["runId"] = launch.RunId;
                active.Detail["matchId"] = view.MatchId; active.Detail["optionsId"] = view.OptionsId;
                active.Detail["queueMs"] = (DateTime.UtcNow - request.ReceivedUtc).TotalMilliseconds;
                if (terminalNoOp)
                {
                    active.Detail["terminalNoOp"] = true; active.SubmittedUtc = DateTime.UtcNow;
                    OperationReply(owner, request, active); return;
                }
                OperationReply(owner, request, active);
                DateTime began = DateTime.UtcNow;
                try
                {
                    inSubmission = true;
                    active.Detail["nativeInvocationStarted"] = true;
                    dragSends = sends; dragMutations = mutations; dragEngineRevision = view.EngineRevision;
                    Execute(request);
                    next.Detail["submissionMs"] = (DateTime.UtcNow - began).TotalMilliseconds;
                    if (request.Command != "mulligan-confirm" && request.Command != "choose" && request.Command != "choice-click" && request.Command != "entity-drag" && next.Sends == 0)
                        throw new InvalidOperationException("Native handler returned without expected send");
                }
                catch (Exception error)
                {
                    // A drag refused before it pressed anything (no actor, covered board slot, blocked input) changed nothing.
                    if (request.Command == "entity-drag" && DragUnsent()) RejectUnsentDrag("Native drag refused before any send: " + error.GetBaseException().Message);
                    else Freeze("Submission failed: " + error.GetType().Name + ": " + error.Message);
                }
                finally { inSubmission = false; }
                return;
            }
            OperationReply(connection, request, next);
        }
        catch (Exception error) { connection.Reply(Reply(request, "rejected", error: error.Message)); }
    }
    private void HandleShopPurchase(ControlEnvelope envelope)
    {
        AssertMainThread();
        var request = envelope.Request;
        var operation = ledger.Accept(request, out bool added);
        if (!added) { OperationReply(envelope.Connection, request, operation); return; }
        if (fault != null) Reject(operation, "Mutations frozen: " + fault);
        else if (ControlBusy().Count != 0) Reject(operation, "Another mutation unresolved");
        else if ((DateTime.UtcNow - request.ReceivedUtc).TotalSeconds > 10) Reject(operation, "Expired before execution");
        else if (!Enabled(out string enabledToken) || token != enabledToken)
        { Reject(operation, "Control flag changed"); Stop("Control flag changed"); }
        else
        {
            try
            {
                var profile = LocalLadder.Profile;
                var receipt = TestControlShop.Purchase(profile, request.Id, request.Text("productId"),
                    request.Has("quantity") ? request.Integer("quantity") : 1,
                    request.Has("kitId") ? request.Text("kitId") : null, NativeUIProbe.ShopContextReady);
                operation.Detail["receipt"] = TestControlShop.Receipt(receipt, profile.Gold);
                operation.Detail["inputKind"] = "local-ledger";
                // The durable transaction succeeded even if its native projection needs recovery.
                operation.Phase = "settled";
                try { NativeUIProbe.RefreshShopPurchase(receipt); }
                catch (Exception error) { operation.Detail["projectionError"] = error.GetBaseException().Message; }
            }
            catch (Exception error) { Reject(operation, error.Message); }
        }
        OperationReply(envelope.Connection, request, operation);
    }
    // Squelch/unsquelch the NPC opponent through the native EnemyEmoteHandler, or play one human emote through the
    // native emote option. Settles at once: no game packet, no engine mutation; a retry with the same ID replays the record.
    // npc-concede / human-defeat (debug instant win / plain loss) share the gate but do mutate the engine (counted in
    // engineMutations): the engine ends the game at once, then the normal terminal flow settles the match.
    private void HandlePresence(ControlEnvelope envelope, View view)
    {
        AssertMainThread();
        var request = envelope.Request;
        var operation = ledger.Accept(request, out bool added);
        if (!added) { OperationReply(envelope.Connection, request, operation); return; }
        if (FaultBlocks(request, view)) Reject(operation, "Mutations frozen: " + fault);
        else if (ControlBusy().Any(blocker => blocker != "fault")) Reject(operation, "Another mutation unresolved");
        else if ((DateTime.UtcNow - request.ReceivedUtc).TotalSeconds > 10) Reject(operation, "Expired before execution");
        else if (!Enabled(out string enabledToken) || token != enabledToken)
        { Reject(operation, "Control flag changed"); Stop("Control flag changed"); }
        else
        {
            try
            {
                RequireMatch(request, view);
                if (GameState.Get() == null || SceneMgr.Get()?.GetMode() != SceneMgr.Mode.GAMEPLAY || LocalLadder.Screen != "Playing")
                    throw new InvalidOperationException("No active isolated native match");
                operation.Detail["inputKind"] = "semantic-presence"; operation.Detail["runId"] = launch.RunId;
                operation.Detail["matchId"] = view.MatchId;
                if (request.Command == "spell-stall-arm")
                {
                    NativeSpellWatchdog.Arm(view.MatchId);
                    operation.Detail["armed"] = true;
                }
                else if (request.Command == "turn-timer-debug")
                {
                    ClientBridge.ShortenPlayerTurnForTest(request.Integer("seconds"));
                    mutations++;
                    operation.Detail["seconds"] = request.Integer("seconds");
                }
                else if (request.Command == "achievement-prime")
                {
                    if (!view.CanAct) throw new InvalidOperationException("Wait for an actionable human turn before priming an achievement.");
                    ClientBridge.PrimeAchievementForTest(request.Text("achievementId"));
                    mutations++;
                    operation.Detail["achievementPrimed"] = request.Text("achievementId");
                }
                else if (request.Command == "premium-unlock-prepare")
                {
                    var reward = LocalLadder.Profile.PreparePremiumUnlockForTest(view.MatchId);
                    operation.Detail["cardId"] = reward.CardId;
                    operation.Detail["premium"] = reward.Premium;
                    operation.Detail["quantity"] = reward.Copies;
                    operation.Detail["level"] = reward.Level;
                }
                else if (request.Command == "npc-concede")
                {
                    ClientBridge.ConcedeNpcForTest();
                    mutations++;
                    operation.Detail["npcConceded"] = true;
                }
                else if (request.Command == "human-defeat")
                {
                    ClientBridge.LoseForTest();
                    mutations++;
                    operation.Detail["humanDefeated"] = true;
                }
                else if (request.Command == "emote" || request.Command == "npc-emote")
                {
                    var type = (EmoteType)Enum.Parse(typeof(EmoteType), request.Text("emote"));
                    if (request.Command == "npc-emote") NativeNpcPresence.PlayNpcEmote(type);
                    else NativeNpcPresence.PlayHumanEmote(type);
                    operation.Detail["emote"] = type.ToString();
                }
                else
                {
                    operation.Detail["changed"] = NativeNpcPresence.SetOpponentSquelch(request.Command == "squelch");
                }
                operation.Detail["squelched"] = NativeNpcPresence.NpcSquelched();
                operation.Detail["presence"] = NativeNpcPresence.Status;
                operation.Phase = "settled";
            }
            catch (Exception error) { Reject(operation, error.GetBaseException().Message); operation.Detail["presence"] = NativeNpcPresence.Status; }
        }
        OperationReply(envelope.Connection, request, operation);
    }
    // #66 live test: what the real queue would draw next on this save (QueueForecast), read-only. picks[0] is the real next match.
    private static JsonNode NpcQueue(string mode, int count)
    {
        var profile = LocalLadder.Profile; var world = NpcWorldRuntime.World;
        var ranked = NpcWorldRuntime.Matchmaker as LadderMatchmaker; var casual = NpcWorldRuntime.Casual;
        if (profile == null || world == null || (mode == "casual" ? casual == null : ranked == null)) return new JsonNode { { "available", false }, { "mode", mode } };
        var decks = world.Catalogs.Decks;
        IReadOnlyList<MatchPick> picks;
        if (mode == "casual")
        {
            var history = profile.CasualHistory;
            picks = QueueForecast.Casual(casual, new CasualRequest(profile.NpcProfileSeed, history.Count, profile.RankedMatchCount, profile.CasualRating,
                history.Skip(Math.Max(0, history.Count - casual.Rules.HistoryDepth)).Select(match => LocalRematch.Past(match, decks))), count);
        }
        else picks = QueueForecast.Ranked(ranked, LocalRematch.Request(profile, profile.RankedHistory, decks, ranked.Rules.HistoryDepth, NpcWorldRuntime.Legend), count);
        return new JsonNode {
            { "available", true }, { "mode", mode }, { "roster", world.All.Count(npc => npc.Notable) },
            { "regularShare", Math.Round(100.0 * picks.Count(pick => pick.Encounter.Npc.Notable) / picks.Count, 1) },
            { "regularsMet", picks.Where(pick => pick.Encounter.Npc.Notable).Select(pick => pick.Encounter.Npc.Id).Distinct().Count() },
            { "picks", TestControlProtocol.List(picks.Select(pick => (object)new JsonNode {
                { "npcId", pick.Encounter.Npc.Id }, { "name", pick.Encounter.Npc.DisplayName }, { "regular", pick.Encounter.Npc.Notable }, { "regularDraw", pick.RegularDraw },
                { "deck", pick.Encounter.Deck.Id }, { "rating", Math.Round(pick.Encounter.Rating, 1) }, { "relaxLevel", pick.RelaxLevel } })) }
        };
    }

    private static void RequireMatch(ControlRequest request, View view)
    { TestControlProtocol.RequireIdentity(request.Text("matchId"), view.MatchId, 0, 0); }
    private static void Reject(ControlOperation operation, string reason) { operation.Phase = "rejected"; operation.Detail["error"] = reason; }
    private void Validate(ControlRequest request, View view)
    {
        AssertMainThread();
        RequireMatch(request, view);
        var front = GameState.Get();
        if (front == null || SceneMgr.Get()?.GetMode() != SceneMgr.Mode.GAMEPLAY || LocalLadder.Screen != "Playing")
            throw new InvalidOperationException("No active isolated native match");
        expectedSend = null;
        if (request.Command == "concede")
        {
            if (view.Terminal || front.WasConcedeRequested()) throw new InvalidOperationException("Already complete/concede requested");
            expectedSend = "concede"; return;
        }
        if (request.Command == "mulligan-confirm")
        {
            TestControlProtocol.RequireIdentity(request.Text("matchId"), view.MatchId, request.Integer("choiceId"), view.ChoiceId);
            if (!view.CanMulligan || front.GetFriendlyEntityChoices()?.ID != view.ChoiceId || front.GetFriendlyEntityChoices()?.ChoiceType != CHOICE_TYPE.MULLIGAN
                || MulliganRelease == null)
                throw new InvalidOperationException("Mulligan not ready");
            var cards = MulliganManager.Get().GetStartingCards();
            if (cards == null || cards.Any(card => card == null || card.GetEntity() == null || card.GetActor() == null))
                throw new InvalidOperationException("Mulligan cards not ready");
            var choices = front.GetFriendlyEntityChoices();
            int[] replace = request.EntityIds("replaceEntityIds");
            expectedHeld = TestControlProtocol.MulliganHeld(cards.Select(card => card.GetEntity().GetEntityId()).ToArray(),
                choices.Entities.ToArray(), front.GetChosenEntities().Select(entity => entity.GetEntityId()).ToArray(), replace);
            if (expectedHeld.Length < choices.CountMin || expectedHeld.Length > choices.CountMax
                || replace.Any(id => choices.UnchoosableEntities?.Contains(id) == true))
                throw new InvalidOperationException("Invalid mulligan selection");
            expectedSend = "choices"; expectedOptions = view.ChoiceId; return;
        }
        if (request.Command == "choose" || request.Command == "choice-click") { ValidateChoice(request, view, front); return; }
        if (request.Command == "recover-choice") { ValidateRecovery(request, view); return; }
        TestControlProtocol.RequireIdentity(request.Text("matchId"), view.MatchId, request.Integer("optionsId"), view.OptionsId);
        if (view.ActionBlockers.Count != 0 || view.Differences.Count != 0 || stableFrames < 2) throw new InvalidOperationException("Not ready: " + string.Join(",", view.ActionBlockers));
        var options = front.GetOptionsPacket();
        if (options == null || options.ID != view.OptionsId) throw new InvalidOperationException("Client options stale");
        int index = request.Command == "entity-drag"
            ? options.List.FindIndex(row => row.Type == Network.Options.Option.OptionType.POWER
                && row.Main?.ID == request.Integer("entityId") && (int)row.Main.PowerKeyword == 0)
            : request.Command == "end-turn" ? options.List.FindIndex(row => row.Type == Network.Options.Option.OptionType.END_TURN) : request.Integer("index");
        int target = request.Command == "act" || (request.Command == "entity-drag" && request.Has("target")) ? request.Integer("target") : 0;
        int sub = request.Command == "act" ? request.Integer("sub") : -1;
        int position = request.Command == "act" || (request.Command == "entity-drag" && request.Has("position")) ? request.Integer("position") : 0;
        // Only a minion or a location is placed in a board slot. Every other card (spell, secret, quest, weapon, hero card) is dropped
        // on the board without one, so `entity-drag CARD --position N` only picks where the native drag lets go; the option itself is
        // validated (and expected back from the client) without a position, as NativeMatch.ValidateOption demands.
        if (request.Command == "entity-drag" && position != 0)
            position = TestControlRules.OptionPosition((int)(front.GetEntity(request.Integer("entityId"))?.GetCardType() ?? 0), position);
        ValidateClientOption(options, index, target, sub);
        var option = options.List[index];
        if (request.Command == "act" && option.Type != Network.Options.Option.OptionType.POWER)
            throw new InvalidOperationException("Use end-turn command");
        if (request.Command == "end-turn")
        {
            string blocker = EndTurnBlocker(front, EndTurnButton.Get());
            if (blocker == "endTurnSelectionResidue") throw new InvalidOperationException("End-turn selection residue");
            if (blocker != null || !view.EndTurnReady) throw new InvalidOperationException("End-turn button blocked: " + (blocker ?? view.State["endTurnBlocker"] ?? "endTurnSettling"));
        }
        PlayerTask task = ClientBridge.ValidateTestControlOption(options.ID, index, target, sub, position);
        // shortcut: advanced semantic classes need separate Windows qualification before enabling.
        // Trade (untargeted, keyword 1720) is qualified; choose-one and targeted battlecries are not.
#if STANDARD_PROOF || ENGINE_SOURCE
        bool trade = (int)option.Main.PowerKeyword == TradeKeyword && target == 0 && sub == -1 && position == 0
            && task.PlayerTaskType == PlayerTaskType.TRADE_CARD;
#else
        const bool trade = false;
#endif
        if (sub != -1 || ((int)option.Main.PowerKeyword != 0 && !trade)
            || (target != 0 && task is PlayCardTask play && play.Source is SabberStoneCore.Model.Entities.Minion))
            throw new InvalidOperationException("Unqualified semantic action class (choose-one/targeted battlecry)");
        expectedSend = "option"; expectedOptions = options.ID; expectedIndex = index;
        expectedTarget = target; expectedSub = sub; expectedPosition = position;
    }
    private static void ValidateClientOption(Network.Options options, int index, int target, int sub)
    {
        if (index < 0 || index >= options.List.Count) throw new ArgumentException("Invalid option index");
        var row = options.List[index];
        // END_TURN intentionally has no wire main option; the client leaves its default error INVALID.
        if (row.Type == Network.Options.Option.OptionType.END_TURN)
        {
            if (target != 0 || sub != -1 || (row.Subs?.Count ?? 0) != 0) throw new ArgumentException("Invalid end-turn tuple");
            return;
        }
        if (row.Type != Network.Options.Option.OptionType.POWER) throw new ArgumentException("Unsupported client option type");
        if (row.Main == null || row.Main.PlayErrorInfo?.IsValid() != true) throw new ArgumentException("Client option error");
        int count = row.Subs?.Count ?? 0;
        if (sub < -1 || sub >= count || (count > 0 && sub == -1)) throw new ArgumentException("Invalid sub-option");
        var selected = sub < 0 ? row.Main : row.Subs[sub];
        if (selected.PlayErrorInfo?.IsValid() != true || (target == 0 ? (selected.Targets?.Count ?? 0) != 0 : !selected.IsValidTarget(target)))
            throw new ArgumentException("Client target/sub-option error");
    }
    private void Execute(ControlRequest request)
    {
        AssertMainThread();
        if (!Enabled(out string enabledToken) || enabledToken != token) throw new InvalidOperationException("Flag changed before submission");
        if (owner?.Open != true) throw new InvalidOperationException("Controller disconnected before execution");
        var front = GameState.Get();
        switch (request.Command)
        {
            case "choice-click":
            case "entity-drag": NativeGameplayDrag.Begin(request, active.Detail); break;
            case "act":
                front.SetSelectedOption(expectedIndex); front.SetSelectedSubOption(expectedSub);
                front.SetSelectedOptionTarget(expectedTarget); front.SetSelectedOptionPosition(expectedPosition);
                front.SendOption(); break;
            case "end-turn": InputManager.Get().DoEndTurnButton(); break;
            case "mulligan-confirm": ConfirmMulligan(front); break;
            case "choose": ExecuteChoice(front); break;
            case "recover-choice": ClientBridge.RecoverTestControlChoice(request.Text("matchId"), request.Integer("choiceId"), request.EntityIds("entityIds")); break;
            case "concede": NativeUiGestures.CloseMenusForConcede(); front.Concede(); break;
        }
    }
    private void ValidateChoice(ControlRequest request, View view, GameState front)
    {
        TestControlProtocol.RequireIdentity(request.Text("matchId"), view.MatchId, request.Integer("choiceId"), view.ChoiceId);
        var choices = front.GetFriendlyEntityChoices();
        int[] selected = request.EntityIds("entityIds");
        if (choices == null || choices.ID != view.ChoiceId) throw new InvalidOperationException("Client choice stale");
        if ((choices.ChoiceType != CHOICE_TYPE.GENERAL && choices.ChoiceType != CHOICE_TYPE.TARGET)
            || choices.CountMin != 1 || choices.CountMax != 1 || selected.Length != 1)
            throw new InvalidOperationException("Only GENERAL/TARGET single-pick choices supported");
        if (!choices.Entities.Contains(selected[0]) || choices.UnchoosableEntities?.Contains(selected[0]) == true)
            throw new InvalidOperationException("Invalid choice membership");
        if (!view.CanChoose || !ChoiceInputReady(front) || front.GetChosenEntities().Count != 0)
            throw new InvalidOperationException("Choice not ready");
        if (request.Command == "choice-click") NativeGameplayDrag.ChoicePoint(selected[0]);
        expectedSend = "choices"; expectedOptions = choices.ID; expectedHeld = selected;
    }
    private void ValidateRecovery(ControlRequest request, View view)
    {
        TestControlProtocol.RequireIdentity(request.Text("matchId"), view.MatchId, request.Integer("choiceId"), view.ChoiceId);
        expectedHeld = ClientBridge.ValidateTestControlRecovery(request.Text("matchId"), request.Integer("choiceId"), request.EntityIds("entityIds"));
        expectedSend = "choices"; expectedOptions = request.Integer("choiceId");
    }
    internal static Dictionary<string, object> ClientEntityDiagnostic(int id)
    {
        AssertMainThread();
        var entity = GameState.Get()?.GetEntity(id); var card = entity?.GetCard();
        return new Dictionary<string, object> {
            { "present", entity != null }, { "cardId", entity?.GetCardId() },
            { "hasActor", card != null && card.GetActor() != null }, { "liveActor", HasLiveActor(card) },
            { "herald", HeraldDiagnostic(entity) }
        };
    }
    // Herald cards only: both tag 4534 values the client holds and the Soldier key the held card text names (null when not a Herald card).
    private static Dictionary<string, object> HeraldDiagnostic(Entity entity)
    {
        if (entity == null || entity.GetCard() == null) return null;
        int own = entity.GetTag((GAME_TAG)4534), controller = entity.GetController()?.GetTag((GAME_TAG)4534) ?? 0;
        if (own == 0 && controller == 0 && entity.GetTag((GAME_TAG)4309) == 0) return null;
        return new Dictionary<string, object> { { "cardTag4534", own }, { "controllerTag4534", controller },
            { "namedKey", NativeHeraldText.Shown(own, controller) },
            { "named", GameStrings.Get(NativeHeraldText.Shown(own, controller)) },
            // The keyword tooltip. tooltipKey/tooltip are what the tooltip prints NOW: the card's Soldier only if the transpiler is applied,
            // else the stock (controller) one, so a missed patch fails the live check. Calls/lastClass show the patched read really ran.
            { "tooltipPatchApplied", ClientBridge.HeraldTooltipApplied }, { "tooltipPatchStatus", ClientBridge.HeraldTooltipStatus },
            { "tooltipCalls", ClientBridge.HeraldTooltipCalls }, { "tooltipLastClass", ClientBridge.HeraldTooltipLastClass },
            { "tooltipKey", NativeHeraldText.TooltipKey(ClientBridge.HeraldTooltipApplied ? NativeHeraldText.TooltipClass(own, controller) : controller) },
            { "tooltip", GameStrings.Format(NativeHeraldText.TooltipKey(ClientBridge.HeraldTooltipApplied ? NativeHeraldText.TooltipClass(own, controller) : controller), new object[0]) } };
    }
    private static bool HasLiveActor(Card card)
    {
        // Unity equality also rejects destroyed objects; null-conditional access does not.
        if (card == null || !card.gameObject.activeInHierarchy) return false;
        var actor = card.GetActor();
        return actor != null && actor.gameObject.activeInHierarchy;
    }
    private static bool ChoiceInputReady(GameState front)
    {
        var choices = front?.GetFriendlyEntityChoices(); var manager = ChoiceCardMgr.Get(); var input = InputManager.Get();
        var processor = front?.GetPowerProcessor();
        if (choices == null || (choices.ChoiceType != CHOICE_TYPE.GENERAL && choices.ChoiceType != CHOICE_TYPE.TARGET)
            || choices.CountMin != 1 || choices.CountMax != 1
            || front.GetResponseMode() != GameState.ResponseMode.CHOICE || front.IsResponsePacketBlocked()
            || front.GetChosenEntities().Count != 0 || manager == null
            || front.GetBooleanGameOption(GameEntityOption.MULLIGAN_IS_CHOOSE_ONE)
            || input == null || !input.IsInputEnabled() || !input.PermitDecisionMakingInput()
            || processor == null || processor.GetCurrentTaskList() != null) return false;
        // TARGET selects existing actors; ChoiceCardMgr creates offer UI only for GENERAL.
        if (choices.ChoiceType == CHOICE_TYPE.GENERAL && (manager.GetFriendlyChoiceState() == null
            || manager.IsFriendlyWaitingToStartChoices() || manager.IsFriendlyShopChoice() || manager.IsFriendlyMagicItemDiscover()
            || processor.HasTaskList(manager.GetFriendlyPreChoiceTaskList()))) return false;
        var source = front.GetEntity(choices.Source);
        if (source != null && source.HasTag(GAME_TAG.BACON_IS_MAGIC_ITEM_DISCOVER)) return false;
        return choices.Entities.Count != 0 && choices.Entities.All(id => {
            var entity = front.GetEntity(id); var card = entity?.GetCard(); var actor = card?.GetActor();
            return (entity?.GetTag(GAME_TAG.ZONE) != 3 || front.CanTargetCardsInHand()) && HasLiveActor(card)
                && actor.IsColliderEnabled() && TweenIdle(card.gameObject) && TweenIdle(actor.gameObject)
                && (choices.UnchoosableEntities?.Contains(id) == true || front.IsEntityInputEnabled(entity));
        });
    }
    private void ExecuteChoice(GameState front)
    {
        if (!ChoiceInputReady(front) || front.GetFriendlyEntityChoices()?.ID != expectedOptions)
            throw new InvalidOperationException("Choice changed before submission");
        // DoNetworkChoice toggles and auto-sends single picks; never call SendChoices again.
        InputManager.Get().DoNetworkResponse(front.GetEntity(expectedHeld[0]));
    }
    // The native End Turn button animates at turn start and refuses input until it settles; act/choose stay allowed.
    private static string EndTurnBlocker(GameState front, EndTurnButton end)
    {
        if (front?.GetOptionsPacket()?.List?.Any(row => row.Type == Network.Options.Option.OptionType.END_TURN) != true) return "endTurnOptionMissing";
        if (front.GetSelectedOptionTarget() != 0 || front.GetSelectedSubOption() != -1 || front.GetSelectedOptionPosition() != 0) return "endTurnSelectionResidue";
        if (end == null || end.IsDisabled || end.IsInputBlocked()) return "endTurnButtonBlocked";
        return null;
    }
    private static bool TweenIdle(GameObject target)
    { return TweenCount != null && (int)TweenCount.Invoke(null, new object[] { target }) == 0; }
    private static bool MulliganCardsReady(GameState front, MulliganManager manager)
    {
        var starting = manager?.GetStartingCards();
        return starting != null && starting.Count != 0
            && starting.All(card => card != null && card.GetEntity() != null && card.GetActor() != null
                && card.gameObject.activeInHierarchy && card.GetActor().gameObject.activeInHierarchy
                && card.GetActor().IsColliderEnabled() && TweenIdle(card.gameObject) && TweenIdle(card.GetActor().gameObject))
            && TestControlProtocol.MulliganInitialized(starting.Select(card => card.GetEntity().GetEntityId()).ToArray(),
                front.GetFriendlyEntityChoices().Entities.ToArray(), front.GetChosenEntities().Select(entity => entity.GetEntityId()).ToArray());
    }
    private static bool MulliganInputReady(GameState front, MulliganManager manager)
    {
        var input = InputManager.Get(); var button = manager?.GetMulliganButton();
        return front != null && manager != null && input != null && input.IsInputEnabled() && input.PermitDecisionMakingInput()
            && front.IsInChoiceMode() && front.GetFriendlyEntityChoices()?.ChoiceType == CHOICE_TYPE.MULLIGAN
            && front.IsMulliganManagerActive() && !front.IsMulliganBusy() && !front.IsBusy()
            && manager.IsMulliganActive() && !manager.IsMulliganIntroActive() && !manager.IsMulliganIntroRunning() && !manager.IsCustomIntroActive()
            && MulliganWaiting != null && (bool)MulliganWaiting.GetValue(manager)
            && !front.GetBooleanGameOption(GameEntityOption.MULLIGAN_IS_CHOOSE_ONE)
            && !front.GetBooleanGameOption(GameEntityOption.MULLIGAN_HAS_HERO_LOBBY)
            && button != null && button.activeInHierarchy && button.GetComponent<Collider>()?.enabled == true
            && button.GetComponent<PegUIElement>()?.IsEnabled() == true && TweenIdle(button)
            && UniversalInputManager.Get()?.IsDialogActive() != true;
    }
    private void ConfirmMulligan(GameState front)
    {
        AssertMainThread();
        var manager = MulliganManager.Get();
        var chosen = new HashSet<int>(front.GetChosenEntities().Select(entity => entity.GetEntityId()));
        var toggles = manager.GetStartingCards().Where(card => chosen.Contains(card.GetEntity().GetEntityId()) != expectedHeld.Contains(card.GetEntity().GetEntityId())).ToArray();
        foreach (var card in toggles)
            manager.ToggleHoldState(card);
        if (!new HashSet<int>(expectedHeld).SetEquals(front.GetChosenEntities().Select(entity => entity.GetEntityId())))
            throw new InvalidOperationException("Mulligan hold toggles did not apply");
        // Let native hold visuals/state finish this frame before the single release attempt.
        mulliganToggleFrame = Time.frameCount;
        mulliganReleasePending = true;
    }
    private void ReleaseMulligan()
    {
        AssertMainThread();
        if (!mulliganReleasePending || Time.frameCount <= mulliganToggleFrame) return;
        mulliganReleasePending = false;
        var front = GameState.Get(); var manager = MulliganManager.Get();
        if (!MulliganInputReady(front, manager) || front.GetFriendlyEntityChoices()?.ID != expectedOptions) return;
        if (!new HashSet<int>(expectedHeld).SetEquals(front.GetChosenEntities().Select(entity => entity.GetEntityId())))
            throw new InvalidOperationException("Mulligan hold set changed before release");
        MulliganRelease.Invoke(manager, new object[] { null });
    }
    internal static void PacketInjected() { if (current?.transport != null) { AssertMainThread(); current.packets++; } }
    internal static void ObserveChoices(int id, List<int> picks)
    {
        if (current?.active != null && current.expectedSend == "choices"
            && (picks == null || picks.Distinct().Count() != picks.Count || !new HashSet<int>(current.expectedHeld).SetEquals(picks)))
        { current.Freeze("Unexpected choice selection"); throw new InvalidOperationException("Unexpected choice selection"); }
        ObserveSend("choices", id);
    }
    internal static void ObserveSend(string kind, int id = 0, int index = 0, int target = 0, int sub = -1, int position = 0)
    {
        var self = current;
        if (self?.transport == null) return;
        AssertMainThread();
        if (self.fault != null && kind != "concede") throw new InvalidOperationException("Mutations frozen: " + self.fault);
        self.sends++; self.stableFrames = 0;
        if (self.active == null && kind != "concede" && Time.realtimeSinceStartup < self.orphanDragUntil)
        {
            self.Freeze("Native send after a rejected drag; the drag was not unsent, reconcile before retry");
            throw new InvalidOperationException("Native send after a rejected drag");
        }
        if (self.active == null || kind == "concede" && (self.active.Finished || self.active.Request.Command != "concede")) return;
        if (self.expectedSend != kind || self.active.Sends != 0
            || (kind == "choices" && id != self.expectedOptions)
            || (kind == "option" && (id != self.expectedOptions || index != self.expectedIndex || target != self.expectedTarget
                || sub != self.expectedSub || position != self.expectedPosition)))
        {
            self.Freeze("Unexpected or duplicate native send"); throw new InvalidOperationException("Unexpected or duplicate native send");
        }
        self.observingSend = true;
        self.active.Sends = 1;
        self.active.SubmittedUtc = DateTime.UtcNow; self.active.Phase = "submitted";
        self.active.Detail["observedSend"] = kind;
        self.active.Detail["submissionDelayMs"] = (DateTime.UtcNow - self.active.AcceptedUtc).TotalMilliseconds;
        self.OperationReply(self.owner, self.active.Request, self.active);
        self.log.LogInfo("TEST_CONTROL_SUBMITTED " + Json.Serialize(new JsonNode {
            { "inputKind", self.active.Request.Command == "recover-choice" ? "diagnostic-recovery" : "semantic" }, { "requestId", self.active.Request.Id }, { "runId", self.launch.RunId },
            { "matchId", self.active.Request.Text("matchId") }, { "snapshotId", self.launch.SnapshotId },
            { "optionsId", kind == "option" ? id : -1 }, { "choiceId", kind == "choices" ? id : -1 }, { "kind", kind }
        }));
    }
    internal static void EngineAccepted()
    {
        if (current?.transport == null) return;
        AssertMainThread(); current.mutations++;
        bool attributed = current.observingSend;
        current.observingSend = false;
        if (attributed && current.active != null) { current.engineAccepted = true; current.active.Detail["engineAccepted"] = true; }
    }
    internal static void SendFailed(Exception error) { if (current != null) { current.observingSend = false; if (current.active != null) current.Freeze("Engine send failed: " + error.Message); } }
    internal static void SendRejected(TurnExpiredException error)
    {
        var self = current;
        if (self?.transport == null) return;
        AssertMainThread();
        self.observingSend = false;
        if (self.active == null) return;
        Reject(self.active, error.Message);
        self.active.Detail["result"] = "rejected-turn-expired";
        self.OperationReply(self.owner, self.active.Request, self.active);
        self.active = null; self.owner = null; self.expectedSend = null; self.expectedHeld = null;
        self.mulliganReleasePending = false;
        self.ResetSampling();
    }
    private void LatchFault(string reason)
    {
        if (fault == null) faultMatchId = active?.Request.Text("matchId")
            ?? (LocalLadder.Screen == "Playing" ? LocalLadder.MatchId : null);
        fault = reason;
    }
    private bool FaultBlocks(ControlRequest request, View view)
    {
        return fault != null && !(faultMatchId != null && view.MatchId == faultMatchId
            && request.Text("matchId") == faultMatchId && !view.Terminal
            && (request.Command == "concede" || request.Command == "human-defeat"));
    }
    private static bool MatchEnded(View view, string matchId)
    {
        var profile = LocalLadder.Profile;
        if (string.IsNullOrEmpty(matchId) || profile == null || profile.PendingMatchId == matchId
            || view.State.ContainsKey("readError")) return false;
        // Debug matches return directly to the picker; native unload can also discard the terminal snapshot.
        return (view.MatchId == matchId && view.Terminal && (view.ClientGameOver || LocalLadder.Screen == "Result"))
            || !view.Gameplay;
    }
    private void ClearEndedFault(View view)
    {
        if (fault == null || faultMatchId == null || !MatchEnded(view, faultMatchId)) return;
        log.LogInfo("TEST_CONTROL_FAULT_CLEARED " + fault + " match=" + faultMatchId);
        fault = null; faultMatchId = null;
        if (active?.Finished == true) { active = null; owner = null; expectedSend = null; }
    }
    // A native drag that sent nothing, changed no engine state and left no residual input has no unknown outcome:
    // freezing every later mutation for it (until the match ends) only strands the run. It fails like a choice that never sent.
    // Only a gesture-driven entity-drag qualifies (choice-click keeps its own engine/choice-revision proof). Everything that could
    // mean "something happened" counts against it: a send or engine change, a cleanup the driver ran (even swallowed) that failed,
    // anything the client input still holds, a changed engine revision (timer-driven moves included).
    private bool DragUnsent()
    {
        if (active == null || active.Request.Command != "entity-drag" || active.Sends != 0 || sends != dragSends || mutations != dragMutations
            || engineAccepted || observingSend || NativeGameplayDrag.Running || active.Detail.ContainsKey("cleanupError")
            || NativeGameplayDrag.CleanupFailure != null || NativeGameplayDrag.InputResidue() != null) return false;
        string revision = ReadSafeView().EngineRevision;
        return revision != null && revision == dragEngineRevision;
    }
    private void RejectUnsentDrag(string reason)
    {
        Reject(active, reason);
        active.Detail["result"] = "rejected-no-send";
        OperationReply(owner, active.Request, active);
        active = null; owner = null; expectedSend = null; expectedHeld = null;
        orphanDragUntil = Time.realtimeSinceStartup + 10f;
        ClearDragSelection();
        ResetSampling();
    }
    // The press may have stored a target or board position in the client's selected option; a stale one must not ride on the next send.
    private void ClearDragSelection()
    {
        try { var front = GameState.Get(); front?.SetSelectedOptionTarget(0); front?.SetSelectedOptionPosition(0); }
        catch (Exception error) { log.LogWarning("TEST_CONTROL drag selection reset: " + error.GetBaseException().Message); }
    }
    private void Freeze(string reason)
    {
        AssertMainThread(); LatchFault(reason);
        if (active != null)
        {
            active.Phase = "fault"; active.Detail["error"] = reason;
            try
            {
                View view = ReadSafeView();
                active.Detail["blockers"] = Strings(view.Blockers); active.Detail["differences"] = Strings(view.Differences);
                foreach (string key in new[] { "blockingCauses", "blockingSpells", "blockingSpellControllers" })
                    active.Detail[key] = view.State.ContainsKey(key) ? view.State[key] : null;
                string path = Path.Combine(CaptureRoot, "fault-" + Guid.NewGuid().ToString("N") + ".png");
                OfflineGuard.RejectReparsePoints(path); ScreenCapture.CaptureScreenshot(path);
                active.Detail["diagnosticScreenshot"] = Path.GetFileName(path);
            }
            catch (Exception) { /* The fault still latches if diagnostics cannot be captured. */ }
            OperationReply(owner, active.Request, active);
        }
        log.LogError("TEST_CONTROL_FAULT " + reason);
        // Retain unresolved identity to prevent late deferred sends being mistaken for another operation.
    }
    private void FinishActive(View view)
    {
        if (active == null || active.Finished) return;
        if (view.Terminal && view.Gameplay && view.MatchId == active.Request.Text("matchId")) terminalSeenAction = active.Request.Id;
        bool recovery = active.Request.Command == "recover-choice";
        bool choose = (active.Request.Command == "choose" || active.Request.Command == "choice-click") || recovery;
        if ((active.Request.Command == "mulligan-confirm" || choose) && active.Sends == 0)
        {
            if ((DateTime.UtcNow - active.AcceptedUtc).TotalSeconds >= TestControlProtocol.MulliganSendSeconds)
            {
                mulliganReleasePending = false;
                bool unchanged = sends == choiceSends && mutations == choiceMutations && !engineAccepted
                    && view.MatchId == active.Request.Text("matchId") && view.ChoiceId == expectedOptions
                    && (choose ? view.ClientChoicePending && view.ChoiceRevision == submittedChoiceRevision : view.ClientMulliganPending)
                    && view.ClientChoiceId == expectedOptions && view.EngineRevision != null && view.EngineRevision == choiceEngineRevision;
                if (!unchanged) { Freeze("Choice send deadline; state changed, reconcile before retry"); return; }
                Reject(active, "Native choice produced no choices send within 15 seconds");
                active.Detail["result"] = "rejected-no-send";
                OperationReply(owner, active.Request, active);
                active = null; owner = null; expectedSend = null; expectedHeld = null;
                ResetSampling();
                return;
            }
            if (!choose) ReleaseMulligan();
        }
        if (active.Sends == 1 && !active.Detail.ContainsKey("packetDrainMs")
            && !view.Blockers.Any(blocker => blocker == "dispatcherPackets" || blocker == "currentPowerTask" || blocker == "powerQueue" || blocker == "powerTaskLists"))
            active.Detail["packetDrainMs"] = (DateTime.UtcNow - active.SubmittedUtc).TotalMilliseconds;
        bool complete = !NativeGameplayDrag.Running && ((active.Sends == 1 && engineAccepted) || active.Detail.ContainsKey("terminalNoOp")) && (stableFrames >= 2 || choiceBoundaryFrames >= 2)
            && (view.OptionsId >= 0 || view.ChoiceId >= 0 || view.Terminal)
            && (!choose || view.ChoiceId != expectedOptions)
            && (!recovery || view.QueuedChoiceCount == 0 && view.Differences.Count == 0);
        // Concede is the stall escape: game over or a settled native exit proves the outcome despite blocked presentation.
        if (active.Request.Command == "concede")
        {
            complete = ((active.Sends == 1 && engineAccepted) || active.Detail.ContainsKey("terminalNoOp"))
                && MatchEnded(view, active.Request.Text("matchId"));
            if (complete && !view.Gameplay) active.Detail["terminalBoundary"] = "nativeMatchExit";
        }
        // A match-ending action (concede, also at an open prompt, or lethal) leaves GAMEPLAY for the local result
        // before two quiet frames exist. With the single send engine-accepted, the engine COMPLETE and the ladder on
        // its Result screen (native game finished), the outcome is known; only the final frame is not visibly audited.
        bool exitable = !complete && active.Request.Command != "concede" && active.Sends == 1 && engineAccepted
            && (!choose || view.ChoiceId != expectedOptions)
            && (!recovery || view.QueuedChoiceCount == 0 && view.Differences.Count == 0);
        bool resultExit = exitable && view.Terminal && !view.Gameplay
            && LocalLadder.Screen == "Result" && view.MatchId == active.Request.Text("matchId");
        // A debug scenario match never shows a Result screen: LocalLadder.FinishDebugScenario settles it away and returns to the
        // picker ("Menu") as soon as the native game over arrives or 20 s after engine terminal, whichever is first. If the client
        // presentation lags the engine by more than that (a long NPC turn), the scene unloads mid-animation, and the unload can
        // discard the terminal snapshot, so engine terminal is latched while it is still visible. Live evidence 2026-11-04: the
        // action otherwise sat to the 120 s deadline and froze with "Settlement deadline" although the match was long over.
        bool scenarioExit = exitable && !resultExit && !view.Gameplay && terminalSeenAction == active.Request.Id
            && LocalLadder.Screen == "Menu" && MatchEnded(view, active.Request.Text("matchId"));
        bool terminalExit = resultExit || scenarioExit;
        if (terminalExit) active.Detail["terminalBoundary"] = resultExit ? "resultScreen" : "scenarioExit";
        if (complete || terminalExit)
        {
            active.Phase = "settled";
            active.Detail["settlementMs"] = (DateTime.UtcNow - active.SubmittedUtc).TotalMilliseconds;
            active.Detail["blockers"] = Strings(view.Blockers); active.Detail["differences"] = Strings(view.Differences);
            if (recovery) active.Detail["queuedChoiceCount"] = view.QueuedChoiceCount;
            lastSettledAction = active.Request.Id;
            OperationReply(owner, active.Request, active); active = null; owner = null; expectedSend = null;
        }
        else if ((DateTime.UtcNow - active.SubmittedUtc).TotalSeconds > 120 && (active.Sends != 0 || active.Detail.ContainsKey("terminalNoOp")))
            Freeze("Settlement deadline; outcome unknown, reconcile before retry");
    }
    private void QueueUi(ControlEnvelope envelope)
    {
        var request = envelope.Request;
        var queue = request.Command == "ui" ? uiReads : uiWaits;
        if (queue.Count == TestControlProtocol.MaxQueue) throw new InvalidOperationException("UI queue full");
        // A read replies once the tracker has enough contiguous samples to judge stability.
        queue.Add(new PendingUi { Envelope = envelope, Deadline = DateTime.UtcNow.AddMilliseconds(request.Command == "ui" ? 1000 : request.Integer("timeoutMs")) });
        if (request.Command == "wait-ui") envelope.Connection.Reply(Reply(request, "accepted"));
    }
    private void HandlePress(ControlEnvelope envelope)
    {
        var request = envelope.Request; var connection = envelope.Connection;
        // Same ID returns the recorded outcome; a press never executes twice.
        var prior = ledger.Find(request.Id);
        if (prior != null)
        {
            ledger.Accept(request, out bool _);
            if (press != null && ReferenceEquals(press.Operation, prior)) press.Owner = connection;
            OperationReply(connection, request, prior); return;
        }
        var next = ledger.Accept(request, out bool added);
        if (!added) throw new InvalidOperationException("Unexpected ledger duplicate");
        bool escape = request.Command == "press" && UiRules.IsMatchEscape(request.Text("element"));
        if (fault != null && !escape) Reject(next, "Mutations frozen: " + fault);
        else if (active != null && !active.Finished && !escape) Reject(next, "Match action unresolved: " + active.Request.Id);
        else if (press != null) Reject(next, "Another press unresolved: " + press.Operation.Request.Id);
        else if ((DateTime.UtcNow - request.ReceivedUtc).TotalSeconds > 10) Reject(next, "Expired before execution");
        else if (!Enabled(out string enabledToken) || token != enabledToken) { Reject(next, "Control flag changed"); Stop("Control flag changed"); }
        else if (request.Command == "press" && UiRules.ButtonBlocker(request.Has("button") ? request.Text("button") : null, request.Text("element")) is string buttonBlocker) Reject(next, buttonBlocker);
        else
        {
            int waitMs = request.Has("timeoutMs") ? request.Integer("timeoutMs") : UiRules.DefaultPressWaitMs;
            bool right = request.Has("button") && request.Text("button") == "right";
            press = new PendingPress { Operation = next, Owner = connection, Deadline = DateTime.UtcNow.AddMilliseconds(waitMs), Right = right, Gesture = request.Command };
            next.Detail["inputKind"] = "semantic-ui"; next.Detail["runId"] = launch.RunId; next.Detail["gesture"] = request.Command;
            if (request.Has("element")) next.Detail["element"] = request.Text("element");
            if (request.Has("target")) next.Detail["target"] = request.Text("target");
            if (request.Command == "press") next.Detail["button"] = right ? "right" : "left";
            next.Detail["queueMs"] = (DateTime.UtcNow - request.ReceivedUtc).TotalMilliseconds;
        }
        OperationReply(connection, request, next);
    }
    private UiSnapshot SampleUi()
    {
        if (uiSampleFrame == Time.frameCount) return uiSample;
        UiSnapshot snapshot;
        var began = System.Diagnostics.Stopwatch.StartNew();
        try { snapshot = NativeUiTree.Read(); NativeResultsUI.FilterTestUi(snapshot); }
        catch (Exception error)
        {
            snapshot = new UiSnapshot { Scene = "unknown", Screen = LocalLadder.Screen };
            snapshot.Busy.Add("readError:" + error.GetType().Name + ": " + error.Message);
            UiRules.Finish(snapshot);
        }
        // Tree read plus hit tests runs every frame while UI work is pending; keep its cost visible.
        snapshot.ReadMs = began.Elapsed.TotalMilliseconds;
        uiTracker.Observe(snapshot, Time.frameCount);
        uiSample = snapshot; uiSampleFrame = Time.frameCount;
        return snapshot;
    }
    // Endpoint-level work that makes the UI not idle: unresolved match action, latched fault, press in flight.
    private List<string> ControlBusy()
    {
        var busy = new List<string>();
        if (active != null && !active.Finished) busy.Add("matchAction:" + active.Request.Id);
        if (fault != null) busy.Add("fault");
        if (press != null) busy.Add("press:" + press.Operation.Request.Id);
        return busy;
    }
    private void TickUi()
    {
        UiSnapshot snapshot = SampleUi();
        List<string> busy = ControlBusy();
        for (int i = uiReads.Count - 1; i >= 0; i--)
        {
            var read = uiReads[i]; var request = read.Envelope.Request;
            if (!read.Envelope.Connection.Open) { uiReads.RemoveAt(i); continue; }
            if (snapshot.Samples < UiRules.StableFrames && DateTime.UtcNow < read.Deadline) continue;
            read.Envelope.Connection.Reply(Reply(request, "settled", UiData(snapshot, busy, request.Has("all") && request.Integer("all") == 1)));
            uiReads.RemoveAt(i);
        }
        for (int i = uiWaits.Count - 1; i >= 0; i--)
        {
            var wait = uiWaits[i]; var request = wait.Envelope.Request;
            if (!wait.Envelope.Connection.Open) { uiWaits.RemoveAt(i); continue; }
            string blocker = UiWaitBlocker(snapshot, busy, request);
            bool expired = DateTime.UtcNow >= wait.Deadline;
            if (blocker != null && !expired) continue;
            JsonNode data = UiData(snapshot, busy, false);
            data["blocker"] = blocker; data["elapsedMs"] = (DateTime.UtcNow - request.ReceivedUtc).TotalMilliseconds;
            wait.Envelope.Connection.Reply(Reply(request, blocker == null ? "settled" : "timeout", data));
            uiWaits.RemoveAt(i);
        }
        if (press != null) TickPress(snapshot);
    }
    // Read only: which native PowerProcessor.ProcessPowerQueue precondition holds the queue.
    private static JsonNode PowerStall(GameState front, PowerProcessor processor)
    {
        JsonNode TaskList(PowerTaskList list)
        {
            if (list == null) return null;
            var source = list.GetSourceEntity(false);
            var block = list.GetBlockStart();
            return new JsonNode { { "id", list.GetId() }, { "source", source?.GetCardId() }, { "sourceEntity", source?.GetEntityId() ?? 0 },
                { "blockType", block?.BlockType.ToString() }, { "tasks", list.GetTaskList()?.Count ?? 0 }, { "complete", list.IsComplete() } };
        }
        try
        {
            var queue = processor.GetPowerQueue();
            var history = HistoryManager.Get();
            return new JsonNode {
                { "busyTaskList", TaskList(BusyTaskList?.GetValue(processor) as PowerTaskList) },
                { "historyBlocking", TaskList(processor.GetHistoryBlockingTaskList()) },
                { "current", TaskList(processor.GetCurrentTaskList()) },
                { "head", queue == null || queue.Count == 0 ? null : TaskList(queue.Peek()) },
                { "bigCard", history != null && history.IsShowingBigCard() },
                { "gameBusy", front.IsBusy() },
                { "mustWaitForChoices", WaitForChoices == null ? null : (object)(bool)WaitForChoices.Invoke(front, null) },
                { "canProcessPowerQueue", CanProcessQueue == null ? null : (object)(bool)CanProcessQueue.Invoke(front, null) } };
        }
        catch (Exception error) { return new JsonNode { { "error", (error.InnerException ?? error).Message } }; }
    }
    private static JsonNode UiData(UiSnapshot snapshot, List<string> busy, bool all)
    {
        JsonNode data = snapshot.ToJson(busy);
        data["reportIssue"] = NativeReportUI.State();
        data["achievementToast"] = NativeAchievementToast.State();
        data["matchmaking"] = NativeMatchmaking.State(); // Finding Opponent popup: Cancel plate parts, disabled/face-down state.
        data["resultsPresentation"] = NativeResultsUI.Diagnostics;
        data["masteryProgress"] = NativeResultsUI.MasteryProgressState();
        data["premiumRewardPresentation"] = NativeResultsUI.PremiumRewards?.State;
        data["rewardPresentation"] = NativeRewardUI.Diagnostics;
        if (all) data["shopSubTabs"] = NativeShop.SubTabDiagnostics(); // Which object draws each sub-tab plate.
        if (all) data["shopRecipePrices"] = NativeShopTilePrices.Diagnostics(); // Deck tiles' recipe price plates.
        if (all) data["shopScroll"] = NativeShop.ScrollDiagnostics(); // Browser scroll height inputs.
        if (!all) data["elements"] = TestControlProtocol.List(snapshot.Elements.Where(element => element.OnScreen || element.Pack != null).Select(element => (object)element.ToJson()));
        return data;
    }
    // Screen and element conditions combine; with neither, the whole UI must be idle.
    private static string UiWaitBlocker(UiSnapshot snapshot, List<string> busy, ControlRequest request)
    {
        bool escape = request.Has("element") && UiRules.IsMatchEscape(request.Text("element"));
        var all = snapshot.Busy.Concat(escape ? busy.Where(item => item != "fault" && !item.StartsWith("matchAction:", StringComparison.Ordinal)) : busy).ToList();
        if (all.Count != 0) return "Busy: " + string.Join(",", all);
        if (request.Has("screen") && !snapshot.OnScreen(request.Text("screen"))) return "Screen is " + snapshot.Scene + "/" + snapshot.Screen;
        if (request.Has("element"))
        {
            string id = request.Text("element");
            return UiRules.PermanentBlocker(snapshot, id, null) ?? UiRules.TransientBlocker(snapshot, id);
        }
        if (request.Has("screen")) return null;
        return snapshot.Idle ? null : "Not idle: moving " + string.Join(",", snapshot.Moving.Take(8));
    }
    private void TickPress(UiSnapshot snapshot)
    {
        var operation = press.Operation; var request = operation.Request;
        if (press.Gesture == "ui-hover" || press.Gesture == "ui-unhover") { TickHover(snapshot); return; }
        if (press.Gesture == "ui-escape") { TickEscape(snapshot); return; }
        if (press.Gesture == "ui-drag") { TickDrag(snapshot); return; }
        if (press.Gesture == "ui-scroll") { TickScroll(snapshot); return; }
        if (operation.Detail.ContainsKey("premiumDismissalsBefore") && operation.Detail.ContainsKey("releaseFrame"))
        {
            var premium = NativeResultsUI.PremiumRewards;
            bool dismissed = premium != null && premium.DismissalCount > (int)operation.Detail["premiumDismissalsBefore"];
            operation.Detail["dismissed"] = dismissed;
            if (dismissed && premium.DismissalSettled)
            {
                operation.Detail["premiumRewardPresentation"] = premium.State;
                operation.Detail["resultsPresentation"] = NativeResultsUI.Diagnostics;
                operation.Detail["ui"] = snapshot.ToJson();
                operation.Phase = "settled";
                operation.Detail["settlementMs"] = (DateTime.UtcNow - operation.SubmittedUtc).TotalMilliseconds;
                OperationReply(press.Owner, request, operation); press = null;
            }
            else if (DateTime.UtcNow >= press.Deadline) FailPress("Premium release did not reach the next reward state before deadline");
            return;
        }
        if (press.PressFrame >= 0)
        {
            // Release on a later frame, like a real mouse-up. PegUI re-hit-tests on mouse-up: if the element no longer
            // receives the click, it only gets release-all(false) and the click does not complete.
            if (Time.frameCount <= press.PressFrame) return;
            UiElement now = snapshot.Elements.FirstOrDefault(element => element.Instance == press.Target.Instance);
            string interrupted = now == null ? "element gone after press"
                : now.Id != press.Target.Id || now.Type != press.Target.Type || now.DeckId != press.Target.DeckId ? "element identity changed after press"
                : now.Denied != null ? "element denied after press: " + now.Denied
                : !now.OnScreen ? "element off screen after press"
                : now.ScrollClipped ? "element clipped by scroll area after press"
                : !now.Enabled ? "element disabled after press"
                : now.PressDisabled ? "element press-disabled by its owner after press"
                : !now.Hit ? "element covered after press" + (now.HitBy == null ? "" : " by " + now.HitBy) : null;
            try
            {
                // PegUI sends nothing on right mouse-up; the opened preview covering the tile is the click's effect.
                if (press.Right) { if (NativeUiTree.Alive(press.Target)) NativeUiTree.RightRelease(press.Target); }
                else if (interrupted == null) NativeReportUI.TestRelease(() => NativeUiTree.Release(press.Target));
                else if (NativeUiTree.Alive(press.Target)) NativeUiTree.Cancel(press.Target);
            }
            catch (Exception error) { FailPress("Release failed: " + error.GetType().Name + ": " + error.Message); return; }
            bool premiumDismiss = operation.Detail.ContainsKey("premiumDismissalsBefore");
            operation.Phase = premiumDismiss ? "submitted" : "settled"; operation.Detail["releaseFrame"] = Time.frameCount;
            operation.Detail["released"] = press.Right || interrupted == null;
            if (interrupted != null) operation.Detail[press.Right ? "afterPress" : "interrupted"] = interrupted;
            if (premiumDismiss) return; // Observe a fresh snapshot after release; input delivery alone is not dismissal.
            operation.Detail["settlementMs"] = (DateTime.UtcNow - operation.SubmittedUtc).TotalMilliseconds;
            OperationReply(press.Owner, request, operation); press = null;
            return;
        }
        string id = request.Text("element");
        string blocker = UiRules.PermanentBlocker(snapshot, id, request.Has("snapshot") ? request.Text("snapshot") : null);
        if (blocker != null) { RejectPress(blocker); return; }
        blocker = UiRules.TransientBlocker(snapshot, id);
        // A local shop product below the fold: scroll the shop's own browser to it (scroll position only), then the
        // usual actionability checks decide once it is laid out, visible, hit and stable.
        UiElement wanted = snapshot.Find(id);
        // Only a tile that is not fully visible, and never under a dialog: a visible tile that is not hit is covered.
        // A reveal waits for the browser's layout (no attempt spent), and a retry waits until the tile stopped moving.
        if (wanted != null && wanted.Denied == null && snapshot.Busy.Count == 0 && snapshot.Dialogs.Count == 0
            && NativeShopReveal.ShouldReveal(id, wanted.OnScreen, wanted.X, wanted.Y, wanted.W, wanted.H, wanted.StableFrames,
                press.RevealAttempts, Time.frameCount, press.RevealFrame)
            && wanted.Handle is PegUIElement tile)
        {
            // The native layout wait is bounded: after LayoutWaitFrames pending frames a still, laid-out tile is centred.
            bool waitOver = NativeShopReveal.LayoutWaitOver(press.RevealPendingFrame, Time.frameCount, wanted.StableFrames, wanted.W, wanted.H);
            var outcome = NativeShop.RevealLocalProduct(tile, id, waitOver);
            if (outcome == NativeShopReveal.Outcome.Started)
            {
                press.RevealAttempts++; press.RevealFrame = Time.frameCount;
                operation.Detail["revealed"] = (long)press.RevealAttempts;
                if (press.RevealPendingFrame >= 0) operation.Detail["revealLayoutWaitFrames"] = (long)(Time.frameCount - press.RevealPendingFrame);
                blocker = "Element revealed: scrolling";
            }
            else if (outcome == NativeShopReveal.Outcome.Pending)
            {
                if (press.RevealPendingFrame < 0) press.RevealPendingFrame = Time.frameCount;
                operation.Detail["revealPending"] = true;
                operation.Detail["revealLayout"] = NativeShop.RevealLayoutState();
                blocker = "Element reveal waiting: shop layout pending";
            }
            else if (outcome == NativeShopReveal.Outcome.InView) operation.Detail["revealInView"] = true;
        }
        if (wanted != null && wanted.Denied == null && snapshot.Busy.Count == 0 && snapshot.Dialogs.Count == 0
            && NativeFeatsReveal.ShouldReveal(id, wanted.OnScreen && !wanted.ScrollClipped, wanted.StableFrames,
                press.RevealAttempts, Time.frameCount, press.RevealFrame)
            && wanted.Handle is PegUIElement subcategory && NativeAchievements.RevealSubcategory(subcategory, id))
        {
            press.RevealAttempts++; press.RevealFrame = Time.frameCount;
            operation.Detail["revealed"] = (long)press.RevealAttempts;
            blocker = "Element revealed: scrolling";
        }
        if (blocker != null)
        {
            operation.Detail["lastBlocker"] = blocker;
            if (DateTime.UtcNow >= press.Deadline) RejectPress("Not actionable before deadline: " + blocker);
            return;
        }
        if (!Enabled(out string enabledToken) || enabledToken != token) { RejectPress("Control flag changed"); Stop("Control flag changed"); return; }
        UiElement target = snapshot.Find(id);
        // The tile id was canonical at acceptance; the live element must also take PegUI's RIGHTCLICK event.
        if (press.Right && !NativeUiTree.RightClickable(target)) { RejectPress("Element does not accept right-click"); return; }
        press.Target = target; press.PressFrame = Time.frameCount;
        if (!press.Right && (id == NativePremiumRewards.DoneId || id.StartsWith(NativePremiumRewards.DoneId + ".", StringComparison.Ordinal)))
        {
            operation.Detail["premiumDismissalsBefore"] = NativeResultsUI.PremiumRewards?.DismissalCount ?? 0;
            operation.Detail["dismissed"] = false;
        }
        operation.Phase = "submitted"; operation.SubmittedUtc = DateTime.UtcNow;
        operation.Detail["generation"] = snapshot.Generation; operation.Detail["type"] = target.Type; operation.Detail["label"] = target.Label ?? "";
        operation.Detail["rect"] = target.ToJson()["rect"]; operation.Detail["pressFrame"] = Time.frameCount;
        operation.Detail["waitMs"] = (DateTime.UtcNow - operation.AcceptedUtc).TotalMilliseconds;
        operation.Detail.Remove("lastBlocker");
        operation.Detail["nativeInvocationStarted"] = true;
        try { if (press.Right) NativeUiTree.RightClick(target); else NativeUiTree.Press(target); }
        catch (Exception error) { FailPress("Press failed: " + error.GetType().Name + ": " + error.Message); return; }
        log.LogInfo("TEST_CONTROL_PRESS " + Json.Serialize(new JsonNode {
            { "inputKind", "semantic-ui" }, { "requestId", request.Id }, { "runId", launch.RunId }, { "element", id }, { "generation", snapshot.Generation },
            { "button", press.Right ? "right" : "left" }
        }));
        OperationReply(press.Owner, request, operation);
    }
    // Scrolling only: no hover, press or release. Reuse the mutation ledger and wait for native motion to finish.
    private void TickScroll(UiSnapshot snapshot)
    {
        var operation = press.Operation; var request = operation.Request;
        string id = request.Text("element");
        string blocker = UiRules.PermanentBlocker(snapshot, id, press.PressFrame < 0 && request.Has("snapshot") ? request.Text("snapshot") : null);
        if (blocker != null) { RejectPress(blocker); return; }
        var target = snapshot.Find(id);
        if (press.PressFrame >= 0 && (target == null || target.Instance != press.Target.Instance || target.Type != press.Target.Type))
        { RejectPress("Scroll target identity changed; inspect UI before retry"); return; }
        blocker = snapshot.Busy.Count != 0 ? "Busy: " + string.Join(",", snapshot.Busy)
            : snapshot.Dialogs.Count != 0 ? "Dialog covers Collection tray"
            : target == null ? "Element not listed" : null;
        if (blocker == null && press.PressFrame < 0)
        {
            if (!Enabled(out string enabledToken) || enabledToken != token) { RejectPress("Control flag changed"); Stop("Control flag changed"); return; }
            press.Target = target;
            try { blocker = NativeCollectionTestUi.Scroll(target); }
            catch (Exception error) { FailPress("Scroll failed: " + error.GetType().Name + ": " + error.Message); return; }
            if (blocker != null) { RejectPress(blocker); return; }
            press.PressFrame = Time.frameCount;
            operation.Phase = "submitted"; operation.SubmittedUtc = DateTime.UtcNow;
            operation.Detail["scrollFrame"] = Time.frameCount;
            OperationReply(press.Owner, request, operation); return;
        }
        if (blocker == null) blocker = UiRules.TransientBlocker(snapshot, id);
        if (blocker == null && !NativeCollectionTestUi.Scrolling(target))
        {
            if (++press.ReachedFrames >= UiRules.StableFrames)
            { operation.Detail["scrolled"] = true; operation.Detail.Remove("lastBlocker"); SettlePress(operation); return; }
        }
        else press.ReachedFrames = 0;
        operation.Detail["lastBlocker"] = blocker ?? "Native scroll settling";
        if (DateTime.UtcNow >= press.Deadline)
        {
            if (press.PressFrame < 0) RejectPress("Scroll not started: " + operation.Detail["lastBlocker"]);
            else RejectPress("Scroll did not settle: " + operation.Detail["lastBlocker"]);
        }
    }
    // The Escape key's native handler with its plugin prefixes, on the main thread; a live match has semantic commands instead.
    private void TickEscape(UiSnapshot snapshot)
    {
        var operation = press.Operation;
        if (!Enabled(out string enabledToken) || enabledToken != token) { RejectPress("Control flag changed"); Stop("Control flag changed"); return; }
        if (snapshot.MatchLive) { RejectPress("Live match: use semantic match commands"); return; }
        try { operation.Detail["handled"] = NativeUiGestures.Escape(out string route); operation.Detail["route"] = route; }
        catch (Exception error) { FailPress("Escape failed: " + error.GetType().Name + ": " + error.Message); return; }
        SettlePress(operation);
    }
    // A roll-over is pointer state only: one actionable listed element, applied and settled in one frame.
    private void TickHover(UiSnapshot snapshot)
    {
        var operation = press.Operation; var request = operation.Request;
        if (!Enabled(out string enabledToken) || enabledToken != token) { RejectPress("Control flag changed"); Stop("Control flag changed"); return; }
        if (press.Gesture == "ui-unhover")
        {
            string name = NativeUiGestures.HoveredName;
            try { operation.Detail["rolledOut"] = NativeUiGestures.Unhover(); }
            catch (Exception error) { FailPress("Unhover failed: " + error.GetType().Name + ": " + error.Message); return; }
            if (name != null) operation.Detail["was"] = name;
            SettlePress(operation); return;
        }
        string id = request.Text("element");
        string blocker = UiRules.PermanentBlocker(snapshot, id, request.Has("snapshot") ? request.Text("snapshot") : null);
        if (blocker != null) { RejectPress(blocker); return; }
        blocker = UiRules.TransientBlocker(snapshot, id);
        if (blocker != null)
        {
            operation.Detail["lastBlocker"] = blocker;
            if (DateTime.UtcNow >= press.Deadline) RejectPress("Not actionable before deadline: " + blocker);
            return;
        }
        UiElement target = snapshot.Find(id);
        if (!NativeUiGestures.Hoverable(target)) { RejectPress("Element does not accept roll-over"); return; }
        operation.Phase = "submitted"; operation.SubmittedUtc = DateTime.UtcNow; operation.Detail.Remove("lastBlocker");
        operation.Detail["generation"] = snapshot.Generation; operation.Detail["type"] = target.Type; operation.Detail["rect"] = target.ToJson()["rect"];
        operation.Detail["nativeInvocationStarted"] = true;
        try { NativeUiGestures.Hover(target); }
        catch (Exception error) { FailPress("Hover failed: " + error.GetType().Name + ": " + error.Message); return; }
        log.LogInfo("TEST_CONTROL_HOVER " + Json.Serialize(new JsonNode { { "requestId", request.Id }, { "runId", launch.RunId }, { "element", id } }));
        SettlePress(operation);
    }

    // Tray reorder in frames: wait until both boxes are actionable; press; hold (native drag starts); let the native
    // tray move the box to the target slot; release once it sat there two frames, or at the deadline wherever it is.
    private void TickDrag(UiSnapshot snapshot)
    {
        var operation = press.Operation; var request = operation.Request;
        string id = request.Text("element"), to = request.Text("target");
        if (press.DragStage == 0)
        {
            string blocker = UiRules.PermanentBlocker(snapshot, id, request.Has("snapshot") ? request.Text("snapshot") : null)
                ?? UiRules.PermanentBlocker(snapshot, to, null);
            bool coinDrag = UiRules.IsCoinDrag(id, to);
            if (blocker != null) { if (coinDrag) operation.Detail["result"] = "blocked"; RejectPress(blocker); return; }
            blocker = UiRules.TransientBlocker(snapshot, id) ?? UiRules.TransientBlocker(snapshot, to);
            UiElement source = snapshot.Find(id), target = snapshot.Find(to);
            if (blocker == null) blocker = NativeUiGestures.DragBlocker(source, target, out press.TargetIndex);
            if (blocker != null)
            {
                operation.Detail["lastBlocker"] = blocker;
                if (coinDrag) operation.Detail["result"] = "blocked";
                if (DateTime.UtcNow >= press.Deadline) RejectPress("Not actionable before deadline: " + blocker);
                return;
            }
            if (!Enabled(out string enabledToken) || enabledToken != token) { RejectPress("Control flag changed"); Stop("Control flag changed"); return; }
            if (coinDrag)
            {
                operation.Detail.Remove("result"); operation.Detail.Remove("lastBlocker");
                operation.SubmittedUtc = DateTime.UtcNow; operation.Detail["nativeInvocationStarted"] = true;
                try
                {
                    bool moved = NativeUiGestures.CoinDrag(source, target);
                    operation.Detail["moved"] = moved;
                    if (!moved) { operation.Detail["result"] = "blocked"; RejectPress("Native coin drag did not start or release"); return; }
                    operation.Detail["result"] = "dragged"; SettlePress(operation);
                }
                catch (Exception error) { FailPress("Coin drag failed: " + error.GetType().Name + ": " + error.Message); }
                return;
            }
            press.Target = source; press.DragTarget = target; press.PressFrame = press.StageFrame = Time.frameCount; press.DragStage = 1;
            operation.Phase = "submitted"; operation.SubmittedUtc = DateTime.UtcNow; operation.Detail.Remove("lastBlocker");
            operation.Detail["generation"] = snapshot.Generation; operation.Detail["fromIndex"] = (long)NativeUiGestures.Slot(source);
            operation.Detail["targetIndex"] = (long)press.TargetIndex; operation.Detail["nativeInvocationStarted"] = true;
            // The drag holds the tray for several frames; give it its own bounded window from here.
            press.Deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(3000, request.Has("timeoutMs") ? request.Integer("timeoutMs") : 0));
            try { NativeUiGestures.DragPress(source); }
            catch (Exception error) { FailPress("Drag press failed: " + error.GetType().Name + ": " + error.Message); return; }
            log.LogInfo("TEST_CONTROL_DRAG " + Json.Serialize(new JsonNode { { "requestId", request.Id }, { "runId", launch.RunId }, { "element", id }, { "target", to } }));
            OperationReply(press.Owner, request, operation);
            return;
        }
        if (Time.frameCount <= press.StageFrame) return;
        if (press.DragStage == 1)
        {
            bool started;
            try { started = NativeUiGestures.DragHold(press.Target, press.TargetIndex); }
            catch (Exception error) { NativeUiGestures.EndDrag(); FailPress("Drag hold failed: " + error.GetType().Name + ": " + error.Message); return; }
            operation.Detail["dragStarted"] = started;
            if (!started) { operation.Detail["moved"] = false; RejectPress("Native drag did not start"); return; }
            press.DragStage = 2; press.StageFrame = Time.frameCount;
            return;
        }
        int slot;
        try { slot = NativeUiGestures.DragSlot(press.Target); }
        catch (Exception error) { NativeUiGestures.EndDrag(); FailPress("Drag read failed: " + error.GetType().Name + ": " + error.Message); return; }
        bool nativeStopped = slot < 0;
        press.ReachedFrames = slot == press.TargetIndex ? press.ReachedFrames + 1 : 0;
        if (!nativeStopped && press.ReachedFrames < 2 && DateTime.UtcNow < press.Deadline) return;
        try { NativeUiGestures.DragRelease(press.Target); }
        catch (Exception error) { FailPress("Drag release failed: " + error.GetType().Name + ": " + error.Message); return; }
        operation.Detail["releaseFrame"] = Time.frameCount; operation.Detail["finalIndex"] = (long)slot;
        operation.Detail["reached"] = slot == press.TargetIndex;
        if (nativeStopped) operation.Detail["nativeStopped"] = true;
        SettlePress(operation);
    }
    private void SettlePress(ControlOperation operation)
    {
        operation.Phase = "settled";
        operation.Detail["settlementMs"] = (DateTime.UtcNow - (operation.SubmittedUtc == default ? operation.AcceptedUtc : operation.SubmittedUtc)).TotalMilliseconds;
        OperationReply(press.Owner, operation.Request, operation); press = null;
    }
    private void RejectPress(string reason)
    {
        var request = press.Operation.Request;
        Reject(press.Operation, reason);
        // Report controls flag blocked-ness beside the unchanged reason (hslive's RETRYABLE prefixes); ui-unhover has no element.
        if (request.Has("element") && NativeReportText.IsControl(request.Text("element"))) press.Operation.Detail["blocked"] = true;
        OperationReply(press.Owner, press.Operation.Request, press.Operation);
        press = null;
    }
    // A native handler threw mid-click: the UI state is unknown, so latch the fault like a failed match send.
    private void FailPress(string reason)
    {
        LatchFault(reason);
        press.Operation.Phase = "fault"; press.Operation.Detail["error"] = reason;
        OperationReply(press.Owner, press.Operation.Request, press.Operation);
        log.LogError("TEST_CONTROL_FAULT " + reason);
        press = null;
    }
    // Read-only hover aim: the plugin never moves the cursor; Input-Client.ps1 does, so native hover code runs unchanged.
    private static JsonNode Locate(ControlRequest request)
    {
        TestControlProtocol.RequireIdentity(request.Text("matchId"), LocalLadder.MatchId, 0, 0);
        var front = GameState.Get(); var scene = SceneMgr.Get();
        if (front == null || scene == null || scene.GetMode() != SceneMgr.Mode.GAMEPLAY) throw new InvalidOperationException("No native gameplay scene");
        int id = request.Integer("entityId", 1);
        var entity = front.GetEntity(id);
        if (entity == null) throw new InvalidOperationException("Unknown entity");
        // Hidden cards stay hidden: only the board and the friendly hand are aimable.
        int zone = entity.GetTag(GAME_TAG.ZONE);
        if (zone != 1 && !(zone == 3 && entity.GetControllerId() == front.GetFriendlySidePlayer()?.GetPlayerId()))
            throw new InvalidOperationException("Entity not in play or friendly hand");
        var card = entity.GetCard(); var actor = card?.GetActor();
        if (card == null || actor == null || !card.gameObject.activeInHierarchy || !actor.gameObject.activeInHierarchy)
            throw new InvalidOperationException("Entity has no visible actor");
        if (!TweenIdle(card.gameObject) || !TweenIdle(actor.gameObject)) throw new InvalidOperationException("Entity moving");
        var result = new JsonNode { { "entityId", id }, { "cardId", entity.GetCardId() }, { "zone", zone } };
        if (zone == 3)
        {
            // The client picks a hand card through its stand-in collider (InputManager.HandleLeftMouseDown), which stays where the layout put it
            // while the hovered card itself is lifted and enlarged: aim at the stand-in, never at the actor, and say what the cursor is on now.
            var hand = front.GetFriendlySidePlayer().GetHandZone();
            var standIn = hand?.GetStandIn(card);
            if (standIn == null || standIn.standInCollider == null || !standIn.standInCollider.enabled) throw new InvalidOperationException("Hand card has no interactive stand-in");
            var aims = NativePlaytest.AimPoints(standIn.standInCollider, 3);
            if (aims.Count == 0) throw new InvalidOperationException("Hand card's stand-in is covered by its neighbours everywhere");
            var candidates = new List<object>();
            foreach (var aim in aims)
                candidates.Add(new JsonNode { { "point", TestControlProtocol.List(new object[] { (long)aim[0], (long)aim[1] }) }, { "clearance", (long)aim[2] } });
            var hovered = hand.GetMousedOverCard()?.GetEntity();
            result["point"] = TestControlProtocol.List(new object[] { (long)aims[0][0], (long)aims[0][1] });
            result["aim"] = "standIn"; result["candidates"] = TestControlProtocol.List(candidates);
            result["hoveredEntityId"] = hovered == null ? -1 : hovered.GetEntityId();
            result["handSize"] = hand.GetCardCount(); result["zonePosition"] = entity.GetTag(GAME_TAG.ZONE_POSITION);
        }
        else
        {
            Vector2 point = NativePlaytest.Point(actor.GetCollider());
            result["point"] = TestControlProtocol.List(new object[] { (long)Math.Min(1279, Math.Round(point.x)), (long)Math.Min(719, Math.Round(point.y)) });
            result["aim"] = "actor";
        }
        result["screen"] = TestControlProtocol.List(new object[] { (long)Screen.width, (long)Screen.height });
        return result;
    }
    // Read-only: the text the client really renders for one visible card (friendly hand or board), end to end. A host tag
    // that "looks right" is not proof: Treants read "Cast another card." with correct tag values because the client's
    // builder resolves tag 2 as an ENTITY it must already know. Reports the builder, the entity tag 2 resolves to, the
    // string the builder produces, the string in the card's own text mesh and, while the cursor is over the card, the
    // big card's (hover with `hslive hover`, then read this).
    private static JsonNode CardText(ControlRequest request)
    {
        TestControlProtocol.RequireIdentity(request.Text("matchId"), LocalLadder.MatchId, 0, 0);
        var front = GameState.Get(); var scene = SceneMgr.Get();
        if (front == null || scene == null || scene.GetMode() != SceneMgr.Mode.GAMEPLAY) throw new InvalidOperationException("No native gameplay scene");
        int id = request.Integer("entityId", 1);
        var entity = front.GetEntity(id);
        if (entity == null) throw new InvalidOperationException("Unknown entity");
        int zone = entity.GetTag(GAME_TAG.ZONE);
        // Hidden cards stay hidden: only the board and the friendly hand can be read.
        if (zone != 1 && !(zone == 3 && entity.GetControllerId() == front.GetFriendlySidePlayer()?.GetPlayerId()))
            throw new InvalidOperationException("Entity not in play or friendly hand");
        int reference = entity.GetTag(GAME_TAG.TAG_SCRIPT_DATA_NUM_1);
        var referenced = reference == 0 ? null : front.GetEntity(reference);
        bool referencedNamed = referenced != null && referenced.HasValidDisplayName();
        var card = entity.GetCard(); var actor = card?.GetActor();
        var big = BigCard.Get(); bool bigShown = big != null && big.GetCard() != null && big.GetCard().GetEntity() == entity;
        return new JsonNode {
            { "entityId", id }, { "cardId", entity.GetCardId() }, { "zone", zone }, { "name", entity.GetName() },
            { "builder", entity.GetCardTextBuilder()?.GetType().Name },
            { "tag2", reference }, { "tag3", entity.GetTag(GAME_TAG.TAG_SCRIPT_DATA_NUM_2) },
            { "reference", new JsonNode {
                { "known", referenced != null }, { "cardId", referenced?.GetCardId() },
                { "zone", referenced?.GetTag(GAME_TAG.ZONE) ?? 0 }, { "named", referencedNamed },
                { "name", referencedNamed ? referenced.GetName() : null } } },
            { "builtText", entity.GetCardTextInHand() },
            { "actorText", PowersText(actor) },
            { "bigCard", new JsonNode { { "shown", bigShown }, { "text", bigShown ? PowersText(big.GetBigCardActor()) : null } } },
            { "enchantments", Enchantments(front, entity) }
        };
    }
    // The enchantments the client lists on a minion or hero (the tooltip panels): Ascended, Devoured Soul, Pack Mentality, Assembled
    // build their text from an entity reference too, so each reports the builder, what tag 2 resolves to and the string it builds.
    private static JsonList Enchantments(GameState front, Entity host)
    {
        var rows = new List<object>();
        var displayed = host.GetDisplayedEnchantments(false);
        if (displayed != null) foreach (var enchantment in displayed)
        {
            int reference = enchantment.GetTag(GAME_TAG.TAG_SCRIPT_DATA_NUM_1);
            var referenced = reference == 0 ? null : front.GetEntity(reference);
            bool named = referenced != null && referenced.HasValidDisplayName();
            rows.Add(new JsonNode {
                { "entityId", enchantment.GetEntityId() }, { "cardId", enchantment.GetCardId() },
                { "builder", enchantment.GetCardTextBuilder()?.GetType().Name },
                { "tag2", reference },
                { "reference", new JsonNode { { "known", referenced != null }, { "cardId", referenced?.GetCardId() },
                    { "named", named }, { "name", named ? referenced.GetName() : null } } },
                { "builtText", enchantment.GetCardTextInHand() }
            });
        }
        return TestControlProtocol.List(rows);
    }
    // UberText lives in an assembly the plugin does not reference: read the card's powers-text mesh by reflection.
    private static string PowersText(Actor actor)
    {
        var mesh = actor?.GetType().GetMethod("GetPowersTextObject", Type.EmptyTypes)?.Invoke(actor, null);
        return mesh?.GetType().GetProperty("Text")?.GetValue(mesh, null) as string;
    }

    private void HandleCapture(ControlEnvelope envelope)
    {
        var request = envelope.Request;
        var operation = ledger.Accept(request, out bool added);
        if (!added)
        {
            var prior = captures.Find(capture => capture.Operation == operation);
            if (prior != null) prior.Connection = envelope.Connection;
            OperationReply(envelope.Connection, request, operation); return;
        }
        if (captures.Count >= TestControlProtocol.MaxQueue) Reject(operation, "Capture queue full");
        else
        {
            try
            {
                string path = Path.Combine(CaptureRoot, "capture-" + Guid.NewGuid().ToString("N") + ".png");
                OfflineGuard.RejectReparsePoints(path);
                operation.Detail["captureId"] = Path.GetFileNameWithoutExtension(path);
                operation.Detail["captureRequestFrame"] = Time.frameCount;
                operation.Detail["afterSettledActionId"] = lastSettledAction;
                OperationReply(envelope.Connection, request, operation);
                ScreenCapture.CaptureScreenshot(path);
                operation.Phase = "submitted"; operation.SubmittedUtc = DateTime.UtcNow;
                captures.Add(new PendingCapture { Operation = operation, Connection = envelope.Connection, Path = path, Frame = Time.frameCount });
            }
            catch (Exception error) { Reject(operation, "Capture failed: " + error.Message); }
        }
        OperationReply(envelope.Connection, request, operation);
    }
    private void TickCaptures()
    {
        for (int i = captures.Count - 1; i >= 0; i--)
        {
            var capture = captures[i]; var operation = capture.Operation;
            try
            {
                if (CaptureReady(capture)) operation.Phase = "settled";
                else if ((DateTime.UtcNow - operation.SubmittedUtc).TotalSeconds > 15) Reject(operation, "Capture deadline; PNG incomplete");
            }
            catch (Exception error) { Reject(operation, "Capture failed: " + error.Message); }
            if (!operation.Finished) continue;
            operation.Detail["captureMs"] = (DateTime.UtcNow - operation.SubmittedUtc).TotalMilliseconds;
            OperationReply(capture.Connection, operation.Request, operation);
            captures.RemoveAt(i);
        }
    }
    private bool CaptureReady(PendingCapture capture)
    {
        try
        {
            if (Time.frameCount <= capture.Frame) return false;
            OfflineGuard.RejectReparsePoints(capture.Path);
            using (var file = new FileStream(capture.Path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                byte[] signature = new byte[8], tail = new byte[12];
                if (file.Length < 45 || file.Read(signature, 0, 8) != 8 || !signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) { capture.Stable = 0; return false; }
                file.Seek(-12, SeekOrigin.End);
                if (file.Read(tail, 0, 12) != 12 || !tail.SequenceEqual(new byte[] { 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 })) { capture.Stable = 0; return false; }
                capture.Stable = file.Length == capture.Length ? capture.Stable + 1 : 1; capture.Length = file.Length;
            }
            if (capture.Stable < 2) return false;
            capture.Operation.Detail["file"] = Path.GetFileName(capture.Path); capture.Operation.Detail["bytes"] = capture.Length; return true;
        }
        catch (IOException) { capture.Stable = 0; return false; }
    }
    private void TickWaits(View view)
    {
        for (int i = waits.Count - 1; i >= 0; i--)
        {
            var wait = waits[i]; var request = wait.Envelope.Request;
            bool match = request.Text("matchId") == view.MatchId;
            bool idle = match && stableFrames >= 2 && fault == null && active == null;
            bool expired = DateTime.UtcNow >= wait.Deadline;
            if (!wait.Envelope.Connection.Open) { waits.RemoveAt(i); continue; }
            if (!idle && !expired && match) continue;
            var blockers = new List<string>(view.Blockers);
            blockers.AddRange(view.Differences.Select(diff => "mismatch:" + diff));
            if (active != null) blockers.Add("actionUnresolved:" + active.Request.Id);
            if (fault != null) blockers.Add("fault:" + fault);
            if (!match) blockers.Add("staleMatchId");
            if (stableFrames < 2) blockers.Add("twoStableFrames");
            wait.Envelope.Connection.Reply(Reply(request, idle ? "settled" : "timeout", new JsonNode {
                { "idle", idle }, { "blockers", Strings(blockers) },
                { "elapsedMs", (DateTime.UtcNow - request.ReceivedUtc).TotalMilliseconds }
            }));
            waits.RemoveAt(i);
        }
    }
    private static object Clone(object value)
    {
        if (value is JsonNode node)
        {
            var copy = new JsonNode();
            foreach (string key in node.Keys) copy.Add(key, Clone(node[key]));
            return copy;
        }
        if (value is JsonList list) return TestControlProtocol.List(list.Select(Clone));
        // MiniJSON serializes only JsonNode/JsonList: a BCL dictionary or array (the `entity` diagnostics) printed as its type name.
        if (value is System.Collections.IDictionary dictionary)
        {
            var converted = new JsonNode();
            foreach (System.Collections.DictionaryEntry pair in dictionary) converted.Add((string)pair.Key, Clone(pair.Value));
            return converted;
        }
        if (value is System.Collections.IEnumerable sequence && !(value is string)) return TestControlProtocol.List(sequence.Cast<object>().Select(Clone));
        return value;
    }
    private static JsonList Strings(IEnumerable<string> values) => TestControlProtocol.List(values.Cast<object>());

    // The readiness rule gameplay timing depends on lives in TurnInput (it stays in public builds); test control only forwards.
    internal static bool TurnInputReady(NativeMatch match, bool forTimer = false) => TurnInput.Ready(match, forTimer);

    internal static View ReadView(NativeMatch match, string matchId)
    {
        AssertMainThread();
        var view = new View { MatchId = matchId, OptionsId = match?.HumanOptionsId ?? -1, ChoiceId = match?.HumanChoiceId ?? -1 };
        view.State["turnTimer"] = NativeTurnTimerOption.State(match);
        var front = GameState.Get(); var scene = SceneMgr.Get(); var input = InputManager.Get(); var zone = ZoneMgr.Get();
        view.QueuedChoiceCount = front != null && QueuedChoices?.GetValue(front) is ICollection queued ? queued.Count : -1;
        view.State["queuedChoiceCount"] = view.QueuedChoiceCount;
        var processor = front?.GetPowerProcessor();
        var dispatcher = Network.Get()?.QueueDispatcher as QueueDispatcher;
        bool gameplay = scene != null && scene.GetMode() == SceneMgr.Mode.GAMEPLAY;
        view.Gameplay = gameplay;
        void Block(bool condition, string name) { if (condition) view.Blockers.Add(name); }
        Block(scene == null || scene.IsTransitioning(), "sceneTransition");
        Block(UniversalInputManager.Get()?.IsDialogActive() == true, "dialog");
        if (gameplay)
        {
            Block(front == null, "gameStateMissing"); Block(processor == null, "powerProcessorMissing"); Block(dispatcher == null, "dispatcherMissing");
            Block(front?.IsBusy() == true, "gameBusy"); Block(front?.IsMulliganBusy() == true, "mulliganBusy");
            Block(dispatcher?.HasGamePackets() == true, "dispatcherPackets");
            Block(processor?.GetCurrentTaskList() != null, "currentPowerTask");
            Block((processor?.GetPowerQueue()?.Count ?? 0) != 0, "powerQueue");
            Block(processor?.HasTaskLists() == true, "powerTaskLists"); Block(processor?.IsHistoryBlocking() == true, "historyBlocking");
            Block(BlockingPower == null || DelayedTasks == null, "unsupportedPrivateIdleProbe");
            if (BlockingPower != null && front != null) Block((bool)BlockingPower.Invoke(front, null), "blockingPowerProcessor");
            if (DelayedTasks != null && processor != null)
            {
                var delayed = DelayedTasks.GetValue(processor) as ICollection;
                Block(delayed == null || delayed.Count != 0, "delayedRealTimeTasks");
                view.State["delayedRealTimeTaskCount"] = delayed?.Count ?? -1;
            }
            Block(zone == null, "zoneManagerMissing");
            Block(zone?.HasPendingLocalChange() == true, "pendingLocalChange"); Block(zone?.HasActiveLocalChange() == true, "activeLocalChange");
            // Native history survives completed animations until matched or the next local turn.
            Block(zone?.HasUnresolvedLocalChange() == true && input?.IsInputEnabled() != true, "unresolvedLocalChange");
            Block(input == null, "inputManagerMissing"); Block(input?.GetHeldCard() != null, "heldCard");
            Block(input?.GetBattlecrySourceCard() != null, "battlecryCard"); Block(front?.IsInSubOptionMode() == true, "subOptionMode");
            Block(ChoiceCardMgr.Get()?.GetSubOptionParentCard() != null, "subOptionUI");
        }
        view.State["achievementToast"] = NativeAchievementToast.State();
        view.State["scene"] = scene?.GetMode().ToString(); view.State["screen"] = LocalLadder.Screen;
        view.ClientGameOver = front?.IsGameOver() == true;
        view.State["responseMode"] = front?.GetResponseMode().ToString(); view.State["concedeRequested"] = front?.WasConcedeRequested() ?? false; view.State["humanOptionsId"] = view.OptionsId;
        view.State["clientOptionsId"] = front?.GetOptionsPacket()?.ID ?? -1; view.State["choiceId"] = view.ChoiceId;
        view.State["clientChoiceId"] = front?.GetFriendlyEntityChoices()?.ID ?? -1;
        view.State["inputEnabled"] = input?.IsInputEnabled() ?? false;
        view.State["currentPowerTask"] = processor?.GetCurrentTaskList() != null;
        view.State["powerQueueCount"] = processor?.GetPowerQueue()?.Count ?? 0;
        if (gameplay && front != null && processor != null) view.State["powerStall"] = PowerStall(front, processor);
        view.State["dispatcherPackets"] = dispatcher?.HasGamePackets() ?? false;
        bool needsChoice = front?.GetFriendlyEntityChoices() != null && front.GetFriendlyEntityChoices().ChoiceType != CHOICE_TYPE.MULLIGAN;
        view.State["needsChoice"] = needsChoice;
        Block(needsChoice, "needsChoice");
        // ZoneChangeList.ProcessChanges waits for choices; Active can span an answerable offer.
        view.ChoiceBoundary = needsChoice && front.GetFriendlyEntityChoices().ID == view.ChoiceId
            && ChoiceCardMgr.Get() != null && (front.GetFriendlyEntityChoices().ChoiceType == CHOICE_TYPE.TARGET
                || !ChoiceCardMgr.Get().IsFriendlyWaitingToStartChoices())
            && processor != null && processor.GetCurrentTaskList() == null
            && !view.Blockers.Any(blocker => blocker != "needsChoice" && blocker != "powerQueue" && blocker != "powerTaskLists"
                && blocker != "historyBlocking" && blocker != "blockingPowerProcessor" && blocker != "activeLocalChange");
        var manager = MulliganManager.Get();
        view.ClientChoiceId = front?.GetFriendlyEntityChoices()?.ID ?? -1;
        view.ClientChoicePending = front?.GetResponseMode() == GameState.ResponseMode.CHOICE && needsChoice;
        var choicePacket = front?.GetFriendlyEntityChoices();
        view.ChoiceRevision = choicePacket == null ? null : TestControlProtocol.Hash(Json.Serialize(new JsonNode {
            { "id", choicePacket.ID }, { "type", (int)choicePacket.ChoiceType }, { "min", choicePacket.CountMin }, { "max", choicePacket.CountMax },
            { "source", choicePacket.Source }, { "playerId", choicePacket.PlayerId },
            { "entities", TestControlProtocol.List(choicePacket.Entities.Cast<object>()) },
            { "unchoosable", TestControlProtocol.List((choicePacket.UnchoosableEntities ?? new List<int>()).Cast<object>()) },
            { "selected", TestControlProtocol.List(front.GetChosenEntities().Select(entity => (object)entity.GetEntityId())) }
        }));
        view.ChoiceReady = gameplay && view.ChoiceBoundary && ChoiceInputReady(front);
        view.State["choiceInputReady"] = view.ChoiceReady;
        view.ClientMulliganPending = front?.IsInChoiceMode() == true && front.GetFriendlyEntityChoices()?.ChoiceType == CHOICE_TYPE.MULLIGAN;
        view.MulliganReady = gameplay && MulliganInputReady(front, manager) && view.ChoiceId >= 0 && view.ClientChoiceId == view.ChoiceId
            && MulliganCardsReady(front, manager);
        view.State["mulliganInputReady"] = view.MulliganReady;
        if (gameplay) view.EndTurnBlocker = EndTurnBlocker(front, EndTurnButton.Get());
        view.ActionBlockers.AddRange(view.Blockers);
        if (!gameplay || front == null || input == null || !input.IsInputEnabled() || !input.PermitDecisionMakingInput()) view.ActionBlockers.Add("decisionInputBlocked");
        if (front == null || front.GetResponseMode() != GameState.ResponseMode.OPTION || front.IsResponsePacketBlocked()) view.ActionBlockers.Add("cleanOptionModeRequired");
        if (view.OptionsId < 0 || front?.GetOptionsPacket()?.ID != view.OptionsId) view.ActionBlockers.Add("optionsNotSynchronized");
        if (!gameplay && current?.active != null && !current.active.Detail.ContainsKey("terminalNoOp")) Block(true, "noClientBoundary");
        if (match?.Game != null)
        {
            var game = match.Game;
            view.Terminal = game.State == SabberStoneCore.Enums.State.COMPLETE;
            view.State["opponentPlate"] = match.OpponentPlateName;
            view.State["engine"] = new JsonNode {
                { "state", game.State.ToString() }, { "step", game.Step.ToString() }, { "turn", game.Turn },
                { "currentPlayerId", game.CurrentPlayer?.PlayerId ?? 0 }, { "firstPlayer", match.FirstPlayer }, { "seed", match.Seed },
                { "botNodes", match.BotNodes }, { "botMilliseconds", match.BotMilliseconds }, { "botFallbacks", match.BotFallbacks },
                { "human", EnginePlayer(match, game.Player1, true) }, { "opponent", EnginePlayer(match, game.Player2, false) }
            };
            var coin = game.Player1.HandZone.FirstOrDefault(card => card.Id == match.CosmeticCoinEntityId);
            var nativeCoin = coin == null ? null : front?.GetEntityMap()?.Values.FirstOrDefault(entity => entity.GetEntityId() == coin.Id);
            view.State["coin"] = coin == null ? null : new JsonNode {
                { "entityId", coin.Id }, { "engineCardId", coin.Card.Id }, { "nativeCardId", nativeCoin?.GetCardId() } };
            view.EngineRevision = TestControlProtocol.Hash(Json.Serialize(view.State["engine"]));
            if (gameplay && front?.GetGameEntity() != null)
            {
                var gameEntity = front.GetGameEntity();
                var human = front.GetFriendlySidePlayer(); var opponent = front.GetOpposingSidePlayer();
                view.State["client"] = new JsonNode {
                    { "state", gameEntity.GetTag(GAME_TAG.STATE) }, { "step", gameEntity.GetTag(GAME_TAG.STEP) }, { "turn", gameEntity.GetTag(GAME_TAG.TURN) },
                    { "human", ClientPlayer(front, human) }, { "opponent", ClientPlayer(front, opponent) },
                    { "boardActors", BoardActors(front) },
                    { "playActors", PlayActorVisibility(front) },
                    { "entities", VisibleEntities(front) }
                };
                void Compare(string name, int actual, int wanted) { if (actual != wanted) view.Differences.Add(name + ":" + actual + "!=" + wanted); }
                Compare("state", gameEntity.GetTag(GAME_TAG.STATE), (int)game.State);
                Compare("step", gameEntity.GetTag(GAME_TAG.STEP), (int)game.Step); Compare("turn", gameEntity.GetTag(GAME_TAG.TURN), game.Turn);
                if (human == null || opponent == null) view.Differences.Add("playersMissing");
                else
                {
                    Compare("humanCurrentPlayer", human.GetTag(GAME_TAG.CURRENT_PLAYER), game.CurrentPlayer == game.Player1 ? 1 : 0);
                    Compare("opponentCurrentPlayer", opponent.GetTag(GAME_TAG.CURRENT_PLAYER), game.CurrentPlayer == game.Player2 ? 1 : 0);
                    ComparePlayer(match, front, human, game.Player1, view.Differences); ComparePlayer(match, front, opponent, game.Player2, view.Differences);
                    if (view.Blockers.Count == 0 || view.ChoiceBoundary)
                    {
                        CompareBoardActors(match, human, game.Player1, view.Differences);
                        CompareBoardActors(match, opponent, game.Player2, view.Differences);
                    }
                }
                // Audit only after queues drain; transient packet application is expected divergence.
                var presentationDifferences = new List<string>();
                var unrevealed = new List<string>();
                // No client blocker does not mean the client is current: a host packet is posted to the main thread first, so for about
                // a frame the host is ahead with every blocker empty. The verdict needs the entity unrevealed on two samples with no
                // packet injected between (UnrevealedConfirmation); the first sample is pending.
                current?.unrevealedSeen.Begin(view.MatchId, Time.frameCount, current.packets);
                TestControlRules.PlanUnrevealedAudit(view.Blockers, view.ChoiceBoundary, out bool auditView, out bool auditDrained);
                if (auditView)
                    view.Differences.AddRange(NativeUIProbe.TestControlVisibleDiff(match, front, presentationDifferences, auditDrained, unrevealed,
                        current == null ? (Func<int, bool>)null : current.unrevealedSeen.Confirm));
                view.State["presentationDifferences"] = Strings(presentationDifferences);
                view.State["unrevealedEntities"] = Strings(unrevealed);
                current?.LogUnrevealed(view, unrevealed, game.Turn);
                // A drained view with a pending (not yet confirmed) entity is not settled: no readiness on the one frame the host may be ahead.
                if (TestControlRules.HoldsBackForPending(auditView, auditDrained, unrevealed))
                {
                    TestControlRules.HoldBack(view.Blockers, view.ActionBlockers, ref view.ChoiceReady, ref view.ChoiceBoundary);
                    view.State["choiceInputReady"] = false;
                }
                if (view.Terminal) { if (!front.IsGameOver()) view.Differences.Add("clientNotTerminal"); }
                else if (view.ChoiceId >= 0)
                {
                    if (front.GetFriendlyEntityChoices()?.ID != view.ChoiceId) view.Differences.Add("choiceIdentity");
                }
                else if (front.GetOptionsPacket()?.ID != view.OptionsId) view.Differences.Add("optionsIdentity");
            }
            else if (gameplay) view.Differences.Add("clientGameMissing");
        }
        view.State["terminal"] = view.Terminal; view.State["canAct"] = view.ActionBlockers.Count == 0 && view.Differences.Count == 0;
        view.State["canConfirmMulligan"] = view.CanMulligan; view.State["canChoose"] = view.CanChoose;
        view.State["blockers"] = Strings(view.Blockers); view.State["actionBlockers"] = Strings(view.ActionBlockers);
        view.State["differences"] = Strings(view.Differences);
        if (current != null)
        {
            bool drained = view.Blockers.Count == 0;
            var step = current.boardFaults.Observe(view.MatchId, drained, view.Terminal || !gameplay,
                drained ? string.Join("|", view.Differences.Where(d => d.Contains(":boardActor:"))) : null, Time.frameCount, Time.realtimeSinceStartup);
            if (step != null)
                current.log.LogInfo("BOARD_ACTOR_INVARIANT match=" + step.Match + " frame=" + Time.frameCount
                    + " status=" + step.Status + " episodeFrames=" + step.Frames
                    + " episodeSeconds=" + step.Seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " differences=" + step.Faults
                    + " actors=" + Json.Serialize(front == null ? null : BoardActors(front)));
        }
        view.Revision = TestControlProtocol.Hash(Json.Serialize(view.State));
        // Diagnostic strings must not influence stability/readiness or action settlement.
        foreach (var pair in BlockingDiagnostics.Read(front)) view.State[pair.Key] = pair.Value;
        if (current != null) view.State["boardActorInvariant"] = current.boardFaults.Summary(Time.frameCount, Time.realtimeSinceStartup);
        view.State["bigCardWatchdogLastRelease"] = NativeBigCardWatchdog.LastRelease;
        view.State["spellWatchdogLastRecovery"] = NativeSpellWatchdog.LastRecovery;
        view.State["spellWatchdogLifecycle"] = NativeSpellWatchdog.Lifecycle; // paused/unfocused disables stall timing
        view.State["focusLosses"] = NativeSpellWatchdog.FocusLosses;
        view.State["lastPress"] = new JsonNode { { "number", pressRecord.Number }, { "downFrame", pressRecord.DownFrame }, { "upFrame", pressRecord.UpFrame },
            { "downFocused", pressRecord.DownFocused }, { "upFocused", pressRecord.UpFocused }, { "downFocusLosses", pressRecord.DownLosses }, { "upFocusLosses", pressRecord.UpLosses },
            { "heldEntityId", pressRecord.HeldEntity } };
        view.State["concedeStallRecovery"] = NativeConcedeStallRecovery.LastRecovery;
        var scenarioOutcome = DebugScenario.LastOutcome;
        view.State["debugScenario"] = scenarioOutcome == null ? null : new JsonNode {
            { "status", scenarioOutcome.Status }, { "detail", scenarioOutcome.Detail }, { "runId", scenarioOutcome.RunId } };
        view.State["unresolvedLocalChange"] = zone?.HasUnresolvedLocalChange() ?? false;
        return view;
    }
    private static JsonNode EnginePlayer(NativeMatch match, SabberStoneCore.Model.Entities.Controller player, bool human)
    {
        var result = new JsonNode {
            { "id", player.PlayerId }, { "heroId", player.Hero.Id }, { "health", player.Hero.Health }, { "armor", player.Hero.Armor },
            { "mana", player.RemainingMana }, { "handCount", player.HandZone.Count }, { "boardCount",
#if ENGINE_SOURCE
                player.BoardZone.SlotCount
#else
                player.BoardZone.Count
#endif
            },
            { "deckCount", player.DeckZone.Count }, { "playState", player.PlayState.ToString() },
            { "board", TestControlProtocol.List(
#if ENGINE_SOURCE
                player.BoardZone.Slots
#else
                player.BoardZone
#endif
                .Select(card => (object)new JsonNode {
                { "id", match.WireReturnId(card.Id) }, { "cardId", match.PresentationCardId(card.Id, card.Card.Id, player.PlayerId) }, { "position", card.ZonePosition + 1 }
            })) }
        };
        if (human) result.Add("hand", TestControlProtocol.List(player.HandZone.Select(card => (object)new JsonNode {
            { "id", match.WireReturnId(card.Id) }, { "cardId", match.PresentationCardId(card.Id, card.Card.Id, player.PlayerId) }
        })));
        return result;
    }
    private static JsonNode ClientPlayer(GameState front, Player player)
    {
        if (player == null) return null;
        var hero = player.GetHero();
        int Count(int zone) => front.GetEntityMap().Values.Count(entity => entity.GetControllerId() == player.GetPlayerId() && entity.GetTag(GAME_TAG.ZONE) == zone);
        return new JsonNode {
            { "id", player.GetPlayerId() }, { "heroId", hero?.GetEntityId() ?? 0 },
            { "health", hero == null ? 0 : hero.GetTag(GAME_TAG.HEALTH) - hero.GetTag(GAME_TAG.DAMAGE) },
            { "armor", hero?.GetTag(GAME_TAG.ARMOR) ?? 0 }, { "mana", player.GetNumAvailableResources() },
            { "handCount", Count(3) }, { "deckCount", Count(2) }, { "playState", player.GetTag(GAME_TAG.PLAYSTATE) }
        };
    }
    private static JsonList VisibleEntities(GameState front)
    {
        return TestControlProtocol.List(front.GetEntityMap().Values.OrderBy(entity => entity.GetEntityId())
            .Where(entity => !string.IsNullOrEmpty(entity.GetCardId()) && (entity.GetTag(GAME_TAG.ZONE) == 1 || entity.GetTag(GAME_TAG.ZONE) == 4
                || (entity.GetTag(GAME_TAG.ZONE) == 3 && entity.GetControllerId() == 1)))
            .Select(entity => {
                var tags = new JsonNode();
                foreach (var tag in entity.GetTags().GetMap()) tags.Add(tag.Key.ToString(), tag.Value);
                return (object)new JsonNode { { "id", entity.GetEntityId() }, { "cardId", entity.GetCardId() }, { "tags", tags } };
            }));
    }
    private static void ComparePlayer(NativeMatch match, GameState front, Player native, SabberStoneCore.Model.Entities.Controller engine, List<string> diff)
    {
        void Compare(string name, int actual, int wanted) { if (actual != wanted) diff.Add("player" + engine.PlayerId + ":" + name + ":" + actual + "!=" + wanted); }
        var hero = native.GetHero();
        Compare("heroId", hero?.GetEntityId() ?? 0, engine.Hero.Id);
        Compare("heroHealth", hero == null ? 0 : hero.GetTag(GAME_TAG.HEALTH) - hero.GetTag(GAME_TAG.DAMAGE), engine.Hero.Health);
        Compare("heroArmor", hero?.GetTag(GAME_TAG.ARMOR) ?? 0, engine.Hero.Armor);
        Compare("mana", native.GetNumAvailableResources(), engine.RemainingMana);
        Compare("playState", native.GetTag(GAME_TAG.PLAYSTATE), (int)engine.PlayState);
        int Count(int zone) => front.GetEntityMap().Values.Count(entity => entity.GetControllerId() == engine.PlayerId && entity.GetTag(GAME_TAG.ZONE) == zone);
        Compare("handCount", Count(3), engine.HandZone.Count); Compare("deckCount", Count(2), engine.DeckZone.Count);
        int[] board = native.GetBattlefieldZone()?.GetCards()?.Select(card => card.GetEntity().GetEntityId()).ToArray() ?? new int[0];
        if (!board.SequenceEqual(
#if ENGINE_SOURCE
            engine.BoardZone.Slots
#else
            engine.BoardZone
#endif
            .Select(card => match.WireReturnId(card.Id)))) diff.Add("player" + engine.PlayerId + ":boardOrder");
        if (engine.PlayerId == 1)
        {
            var cards = native.GetHandZone()?.GetCards()?.Where(card => card != null && card.GetEntity() != null).ToArray() ?? new Card[0];
            int[] hand = cards.Select(card => card.GetEntity().GetEntityId()).OrderBy(id => id).ToArray();
            foreach (var expected in engine.HandZone)
            {
                var card = cards.FirstOrDefault(value => value.GetEntity().GetEntityId() == match.WireReturnId(expected.Id));
                diff.AddRange(TestControlProtocol.HandDifferences(engine.PlayerId, match.WireReturnId(expected.Id), match.PresentationCardId(expected.Id, expected.Card.Id, engine.PlayerId),
                    card != null, card == null ? null : card.GetEntity().GetCardId(), HasLiveActor(card)));
            }
            if (!hand.SequenceEqual(engine.HandZone.Select(card => match.WireReturnId(card.Id)).OrderBy(id => id))) diff.Add("humanHandIds");
        }
    }
    private static TestControlProtocol.BoardActorState BoardActor(Card card)
    {
        var actor = card == null ? null : card.GetActor();
        var entity = actor == null ? null : actor.GetEntity();
        return new TestControlProtocol.BoardActorState {
            Present = card != null, Live = HasLiveActor(card),
            Shown = card != null && card.IsShown() && actor != null && actor.IsShown(),
            // Board actors carry no EntityDef in the pinned client; their entity names the shown card.
            CardId = entity?.GetCardId() ?? actor?.GetEntityDef()?.GetCardId(),
            Position = card == null ? 0 : card.GetZonePosition(),
            Attack = entity?.GetTag(GAME_TAG.ATK) ?? 0,
            Health = (entity?.GetTag(GAME_TAG.HEALTH) ?? 0) - (entity?.GetTag(GAME_TAG.DAMAGE) ?? 0)
        };
    }

    private static JsonList BoardActors(GameState front)
    {
        var rows = new JsonList();
        foreach (var player in new[] { front.GetFriendlySidePlayer(), front.GetOpposingSidePlayer() })
            foreach (var card in player?.GetBattlefieldZone()?.GetCards() ?? new List<Card>())
            {
                var actor = BoardActor(card);
                rows.Add(new JsonNode { { "id", card?.GetEntity()?.GetEntityId() ?? 0 }, { "player", player.GetPlayerId() },
                    { "cardId", actor.CardId }, { "position", actor.Position }, { "attack", actor.Attack }, { "health", actor.Health },
                    { "live", actor.Live }, { "shown", actor.Shown }, { "hidden", !actor.Shown },
                    { "cardShown", card != null && card.IsShown() }, { "actorShown", card?.GetActor()?.IsShown() ?? false },
                    { "colliderEnabled", card?.GetActor()?.IsColliderEnabled() ?? false } });
            }
        return rows;
    }

    private static JsonList PlayActorVisibility(GameState front)
    {
        var rows = new JsonList();
        var players = new[] { front.GetFriendlySidePlayer(), front.GetOpposingSidePlayer() };
        foreach (var entity in front.GetEntityMap().Values.OrderBy(value => value.GetEntityId()))
        {
            if (entity.GetTag(GAME_TAG.ZONE) != (int)TAG_ZONE.PLAY
                || !players.Any(player => player != null && player.GetPlayerId() == entity.GetControllerId())
                || entity.GetCardType() is TAG_CARDTYPE.GAME or TAG_CARDTYPE.PLAYER or TAG_CARDTYPE.ENCHANTMENT) continue;
            var card = entity.GetCard();
            var actor = card == null ? null : card.GetActor();
            bool cardShown = card != null && card.IsShown(), actorShown = actor != null && actor.IsShown();
            rows.Add(new JsonNode { { "id", entity.GetEntityId() }, { "player", entity.GetControllerId() },
                { "cardPresent", card != null }, { "actorPresent", actor != null },
                { "cardShown", cardShown }, { "actorShown", actorShown }, { "hidden", !cardShown || !actorShown },
                { "live", HasLiveActor(card) }, { "colliderEnabled", actor != null && actor.IsColliderEnabled() } });
        }
        return rows;
    }

    private static void CompareBoardActors(NativeMatch match, Player native, SabberStoneCore.Model.Entities.Controller engine, List<string> diff)
    {
        var cards = native.GetBattlefieldZone()?.GetCards() ?? new List<Card>();
        foreach (var expected in
#if ENGINE_SOURCE
            engine.BoardZone.Slots
#else
            engine.BoardZone
#endif
        )
        {
            int id = match.WireReturnId(expected.Id);
            var card = cards.FirstOrDefault(value => value != null && value.GetEntity()?.GetEntityId() == id);
            var character = expected as SabberStoneCore.Model.Entities.Character;
            diff.AddRange(TestControlProtocol.BoardDifferences(engine.PlayerId, id,
                new TestControlProtocol.BoardActorState {
                    CardId = NativeMatch.TransportCardId(expected.Card.Id), Position = expected.ZonePosition + 1,
                    Attack = character?.AttackDamage ?? 0,
                    Health = character?.Health ?? expected[SabberStoneCore.Enums.GameTag.HEALTH] - expected[SabberStoneCore.Enums.GameTag.DAMAGE]
                }, BoardActor(card)));
        }
    }

    private static JsonNode Choices(View view)
    {
        AssertMainThread();
        var front = GameState.Get(); var choices = front?.GetFriendlyEntityChoices();
        return new JsonNode {
            { "choiceId", choices?.ID ?? -1 }, { "type", choices?.ChoiceType.ToString() }, { "countMin", choices?.CountMin ?? 0 },
            { "countMax", choices?.CountMax ?? 0 }, { "source", choices?.Source ?? 0 }, { "playerId", choices?.PlayerId ?? 0 },
            { "entities", TestControlProtocol.List((choices?.Entities ?? new List<int>()).Cast<object>()) },
            { "unchoosableEntities", TestControlProtocol.List((choices?.UnchoosableEntities ?? new List<int>()).Cast<object>()) },
            { "selected", TestControlProtocol.List((front?.GetChosenEntities() ?? new List<Entity>()).Select(entity => (object)entity.GetEntityId())) },
            { "handTargets", TestControlProtocol.List((choices?.Entities ?? new List<int>()).Select(id => {
                var row = new JsonNode { { "entityId", id }, { "clickable", false } };
                try { var point = NativeGameplayDrag.ChoicePoint(id); row["clickable"] = true;
                    row["point"] = TestControlProtocol.List(new object[] { point.x, point.y }); }
                catch (Exception error) { row["blocked"] = error.Message; }
                return (object)row;
            })) },
            { "canTargetCardsInHand", front?.CanTargetCardsInHand() == true },
            { "canChoose", view.CanChoose }, { "canConfirmMulligan", view.CanMulligan },
            { "chooseSupported", (choices?.ChoiceType == CHOICE_TYPE.GENERAL || choices?.ChoiceType == CHOICE_TYPE.TARGET)
                && choices.CountMin == 1 && choices.CountMax == 1
                && !front.GetBooleanGameOption(GameEntityOption.MULLIGAN_IS_CHOOSE_ONE)
                && ChoiceCardMgr.Get() != null && (choices.ChoiceType == CHOICE_TYPE.TARGET
                    || (!ChoiceCardMgr.Get().IsFriendlyShopChoice() && !ChoiceCardMgr.Get().IsFriendlyMagicItemDiscover()))
                && front.GetEntity(choices.Source)?.HasTag(GAME_TAG.BACON_IS_MAGIC_ITEM_DISCOVER) != true }
        };
    }
    private static JsonNode Options(View view)
    {
        AssertMainThread();
        var packet = GameState.Get()?.GetOptionsPacket(); var rows = new JsonList();
        if (packet != null)
            for (int index = 0; index < packet.List.Count; index++)
            {
                var row = packet.List[index];
                JsonNode Sub(Network.Options.Option.SubOption sub, int subIndex)
                {
                    var targets = new JsonList(); var legal = new JsonList();
                    foreach (var target in sub?.Targets ?? new List<Network.Options.Option.TargetOption>())
                        targets.Add(new JsonNode { { "id", target.ID }, { "error", target.PlayErrorInfo?.PlayError.ToString() }, { "errorParam", target.PlayErrorInfo?.PlayErrorParam } });
                    foreach (int target in sub?.Targets?.Where(target => target.PlayErrorInfo?.IsValid() == true).Select(target => target.ID) ?? Enumerable.Empty<int>()) AddPositions(target);
                    if ((sub?.Targets?.Count ?? 0) == 0) AddPositions(0);
                    void AddPositions(int target)
                    {
                        var positions = new JsonList();
                        int keyword = (int)(sub?.PowerKeyword ?? 0);
                        bool enabled = subIndex == -1 && (keyword == 0 || keyword == TradeKeyword);
                        for (int position = 0; position <= 7; position++)
                            try
                            {
                                ValidateClientOption(packet, index, target, subIndex);
                                PlayerTask task = ClientBridge.ValidateTestControlOption(packet.ID, index, target, subIndex, position);
                                if (target != 0 && task is PlayCardTask play && play.Source is SabberStoneCore.Model.Entities.Minion) enabled = false;
                                positions.Add(position);
                            }
                            catch (Exception error) when (error is InvalidOperationException || error is ArgumentException) { }
                        if (positions.Count > 0) legal.Add(new JsonNode { { "target", target }, { "positions", positions }, { "semanticEnabled", enabled } });
                    }
                    return new JsonNode { { "sub", subIndex }, { "source", sub?.ID ?? 0 }, { "keyword", (int)(sub?.PowerKeyword ?? 0) },
                        { "error", sub?.PlayErrorInfo?.PlayError.ToString() }, { "errorParam", sub?.PlayErrorInfo?.PlayErrorParam },
                        { "targets", targets }, { "permitted", legal }, { "positionProvenance", "NativeMatch.ValidateOption" } };
                }
                rows.Add(new JsonNode { { "index", index }, { "type", row.Type.ToString() }, { "main", Sub(row.Main, -1) },
                    { "subs", TestControlProtocol.List((row.Subs ?? new List<Network.Options.Option.SubOption>()).Select((sub, subIndex) => (object)Sub(sub, subIndex))) } });
            }
        return new JsonNode { { "optionsId", packet?.ID ?? -1 }, { "humanOptionsId", view.OptionsId },
            { "ready", view.CanAct }, { "endTurnReady", view.EndTurnReady }, { "endTurnBlocker", view.State["endTurnBlocker"] },
            { "endTurnReadyFrames", view.State["endTurnReadyFrames"] },
            { "blockers", Strings(view.ActionBlockers.Concat(view.Differences)) }, { "rows", rows } };
    }
}

internal static partial class NativeUIProbe
{
    internal static bool ShopContextReady => RewardContextReady && !replayRunning
        && LocalLadder.Screen == "Menu" && SceneMgr.Get().GetMode() == SceneMgr.Mode.HUB;
}

// No Unity/client types: tests/shop compiles this handler from the same source.
internal static class TestControlShop
{
    // The theme this launch shows (plugin: HubTheme.LaunchTheme); unset in offline test hosts.
    internal static Func<string> LaunchHubTheme;
    internal static LocalPurchaseReceipt Purchase(LocalProfile profile, string actionId, string productId,
        int quantity, string kitId, bool atMenu)
    {
        if (profile == null) throw new InvalidOperationException("Local profile unavailable.");
        var prior = profile.Purchases.SingleOrDefault(receipt => receipt.RequestId == actionId);
        // A deck's current price changes after its cards/recipe are granted; replay its original quote.
        if (prior != null) return profile.Purchase(productId, quantity, prior.PriceBeforeKit, actionId, kitId);
        var product = LocalShop.Default.Product(productId);
        int price = profile.PurchasePrice(productId, quantity);
        string blocker = Blocker(profile, product, price, atMenu, kitId != null);
        if (blocker != null) throw new InvalidOperationException(blocker);
        return profile.Purchase(productId, quantity, price, actionId, kitId);
    }

    internal static JsonNode Catalog(LocalProfile profile, bool atMenu)
    {
        if (profile == null) throw new InvalidOperationException("Local profile unavailable.");
        var products = new JsonList(); var pets = new JsonList();
        foreach (string pet in profile.OwnedPets) pets.Add(pet);
        foreach (var product in LocalShop.Default.Products)
        {
            int? price = null; string reason;
            try { price = profile.PurchasePrice(product.Id); reason = Blocker(profile, product, price.Value, atMenu, false); }
            catch (InvalidOperationException error) { reason = error.Message; }
            products.Add(new JsonNode {
                { "id", product.Id }, { "kind", product.Kind }, { "price", price },
                { "owned", Owned(profile, product, price) }, { "purchasable", reason == null }, { "reason", reason },
                { "equipped", Equipped(profile, product) }
            });
        }
        return new JsonNode { { "version", LocalShop.Default.Version }, { "gold", profile.Gold }, { "products", products },
            { "selectedHubTheme", profile.SelectedHubTheme }, { "selectedBoard", profile.SelectedBoard }, { "selectedPet", profile.SelectedPet },
            { "ownedPets", pets },
            { "launchHubTheme", LaunchHubTheme?.Invoke() } };
    }

    private static bool Owned(LocalProfile profile, LocalShopProduct product, int? price)
    {
        switch (product.Kind)
        {
            case "Pack": return profile.PackInventory.Any(pack => pack.PackType == product.PackType && pack.Count > 0);
            case "Recipe": return profile.OwnedRecipes.Contains(product.RecipeId);
            case "Deck": return price == 0 && profile.DeckPurchaseBlocker(product.RecipeId) == null || profile.Purchases.Any(receipt => receipt.Kind == "Deck" && receipt.Granted.RecipeId == product.RecipeId);
            case "CardBack": return profile.OwnedCardBacks.Contains(product.CardBack.Value);
            case "Coin": return profile.OwnsCoin(product.Coin.Value);
            case "HeroSkin": return profile.OwnedHeroSkins.Contains(product.HeroSkin);
            case "HubTheme": return profile.OwnsHubTheme(product.HubTheme);
            case "Board": return profile.OwnsBoard(product.Board.Value);
            case "Pet": return profile.OwnsPet(product.Pet);
            default: return false;
        }
    }
    private static bool? Equipped(LocalProfile profile, LocalShopProduct product) =>
        product.Kind == "HubTheme" ? profile.SelectedHubTheme == product.HubTheme
        : product.Kind == "Board" ? profile.SelectedBoard == product.Board.Value
        : product.Kind == "Pet" ? (product.Pet == null ? profile.EffectiveSelectedPet == null : LocalShop.Default.PetFamily(profile.EffectiveSelectedPet) == product.Pet) : (bool?)null;

    private static string Blocker(LocalProfile profile, LocalShopProduct product, int price, bool atMenu, bool withKit)
    {
        if (product.Kind == "Deck") {
            string grantOnly = profile.DeckPurchaseBlocker(product.RecipeId);
            if (grantOnly != null) return grantOnly;
        }
        if (!atMenu || profile.Pending != null || profile.PendingRewardPresentation != null) return "Shop purchases require idle HUB/menu with no pending match or rewards.";
        if (profile.PackRevealPending) return "A native pack reveal is pending.";
        if (product.Kind != "Pack" && Owned(profile, product, price)) return "Shop item already owned.";
        if (NativeShopPurchase.DeckCapacityBlocker(profile, product) is string trayFull) return trayFull;
        // Kit validation and the discounted affordability check belong to the ledger.
        if (!withKit && profile.Gold < price) return "Not enough local gold.";
        return null;
    }

    internal static JsonNode Receipt(LocalPurchaseReceipt receipt, int goldAfter)
    {
        var packs = new JsonList(); var cards = new JsonList();
        foreach (var pack in receipt.Granted.Packs)
            packs.Add(new JsonNode { { "packType", pack.PackType }, { "count", pack.Count } });
        foreach (var card in receipt.Granted.Cards)
            cards.Add(new JsonNode { { "cardId", card.CardId }, { "countBefore", card.CountBefore },
                { "countAfter", card.CountAfter }, { "count", card.CountAfter - card.CountBefore } });
        return new JsonNode {
            { "sequence", receipt.Sequence }, { "requestId", receipt.RequestId }, { "productId", receipt.ProductId },
            { "kind", receipt.Kind }, { "quantity", receipt.Quantity }, { "goldPaid", receipt.GoldPaid },
            { "priceBeforeKit", receipt.PriceBeforeKit }, { "kitId", receipt.KitId }, { "goldAfter", goldAfter },
            { "granted", new JsonNode { { "packs", packs }, { "cards", cards }, { "recipeId", receipt.Granted.RecipeId },
                { "deckId", receipt.Granted.DeckId }, { "cardBack", receipt.Granted.CardBack }, { "coin", receipt.Granted.Coin }, { "heroSkin", receipt.Granted.HeroSkin },
                { "hubTheme", receipt.Granted.HubTheme }, { "board", receipt.Granted.Board }, { "pet", receipt.Granted.Pet } } }
        };
    }
}

// Snapshot only: no transaction, projection refresh, or persistence calls.
internal static class TestControlCrafting
{
    internal static JsonNode State(LocalProfile profile, string[] requestedIds)
    {
        if (profile == null) throw new InvalidOperationException("Local profile unavailable.");
        // The receipt is deliberately not part of the public crafting mutation API.
        var saved = (LocalProfileData)AccessTools.Field(typeof(LocalProfile), "data").GetValue(profile);
        var ledger = CraftingLedgerRegistry.Current;
        var net = NetCache.Get();
        var collection = CollectionManager.Get();
        var acquired = profile.Acquired;
        bool craftingMode = CraftingManager.GetIsInCraftingMode();
        var crafting = CraftingManager.IsInitialized ? CraftingManager.Get() : null;
        string shownCardId = craftingMode && crafting != null && crafting.GetShownCardInfo(out var shownCard, out _)
            ? shownCard.GetCardId() : null;
        var owned = ledger?.OwnedCounts();
        var ids = requestedIds.Length != 0 ? requestedIds : (owned ?? profile.Collection)
            .Where(pair => pair.Value > 0 && LocalCrafting.TryGetPrice(pair.Key, out _, out _))
            .Select(pair => pair.Key).OrderBy(id => id, StringComparer.Ordinal).Take(50).ToArray();
        var cards = new JsonList();
        foreach (string id in ids)
        {
            bool craftable = LocalCrafting.TryGetPrice(id, out int cost, out int value);
            NetCache.CardValue nativePrice = null;
            // Without the local ledger hook, retail price lookup may depend on unloaded client data.
            if (ledger != null && NativeUIProbe.Active)
                nativePrice = CraftingManager.GetCardValue(id, TAG_PREMIUM.NORMAL, out _);
            cards.Add(new JsonNode {
                { "cardId", id }, { "ledgerCount", owned == null ? (int?)null : owned.TryGetValue(id, out int ownedCount) ? ownedCount : 0 },
                { "acquired", acquired.TryGetValue(id, out int count) ? count : 0 },
                { "nativeNormalCount", collection?.GetOwnedCount(id, TAG_PREMIUM.NORMAL) },
                { "craftable", craftable }, { "craftCost", cost }, { "disenchantValue", value },
                { "nativeBuy", nativePrice?.GetBuyValue() }, { "nativeSell", nativePrice?.GetSellValue() }
            });
        }
        var receipt = saved.LastCraft;
        return new JsonNode {
            { "ledgerActive", ledger != null }, { "dust", ledger?.DustBalance },
            { "craftingMode", craftingMode }, { "shownCardId", shownCardId },
            { "nativeDust", net?.GetArcaneDustBalance() },
            { "nativeDustRaw", net?.GetNetObject<NetCache.NetCacheArcaneDustBalance>()?.Balance },
            { "craftSequence", saved.CraftSequence },
            { "lastCraft", receipt == null ? null : new JsonNode {
                { "sequence", receipt.Sequence }, { "op", receipt.Op.ToString() }, { "dustDelta", receipt.DustDelta },
                { "cards", TestControlProtocol.List(receipt.Cards.Select(card => (object)new JsonNode {
                    { "cardId", card.CardId }, { "countDelta", card.CountDelta }, { "acquiredBefore", card.AcquiredBefore }, { "premium", card.Premium }
                })) }
            } },
            { "packRevealPending", profile.PackRevealPending }, { "cards", cards },
            { "deckOwnership", TestControlProtocol.List(profile.CustomDecks.Select(deck => {
                int slots = deck.CardEntries.Count + deck.Sideboards.Sum(board => board.CardEntries.Count);
                int missing = profile.MissingDeckSlotCount(deck.Id);
                var native = CollectionManager.Get()?.GetDeck(deck.Id);
                return (object)new JsonNode { { "id", deck.Id }, { "name", deck.Name }, { "slots", slots },
                    { "owned", slots - missing }, { "missing", missing },
                    { "nativeMissing", native?.GetSlotsIncludingSideboards().Where(slot => !slot.Owned).Sum(slot => slot.Count) } };
            })) },
            { "incompleteDecks", TestControlProtocol.List(profile.CustomDecks.Where(deck => !profile.IsDeckComplete(deck.Id))
                .Select(deck => (object)deck.Name)) }
        };
    }
}
#endif
