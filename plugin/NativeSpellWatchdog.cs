using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using MiniJSON;
using UnityEngine;

// Native completion callbacks release presentation ownership; never discard queued engine history.
internal static partial class NativeSpellWatchdog
{
    private static readonly FieldInfo Controllers = AccessTools.Field(typeof(GameState), "m_serverBlockingSpellControllers");
    private static readonly MethodInfo Finish = AccessTools.Method(typeof(AttackSpellController), "FinishEverything", Type.EmptyTypes);
    private static readonly MethodInfo TweenCount = AccessTools.Method(GameTypes.ByName("iTween"), "Count", new[] { typeof(GameObject) });
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AttackSpellController, object> staticAttacks =
        new System.Runtime.CompilerServices.ConditionalWeakTable<AttackSpellController, object>();
    private static readonly NativeStallClock clock = new NativeStallClock();
    private static double nextSample;
    private static bool applicationPaused, applicationFocused = true;
    // How often the client lost focus this launch: unchanged across a real mouse press means it held focus throughout.
    internal static int FocusLosses { get; private set; }
    private static bool warned;
    private static string armedMatch;
    private static SpellController injected;
    internal static JsonNode LastRecovery { get; private set; }

    internal static void Initialize(Harmony harmony)
    {
        // Let the native attack load, target and launch its spell before withholding completion.
        harmony.Patch(AccessTools.Method(typeof(AttackSpellController), "LaunchAttack", Type.EmptyTypes),
            postfix: new HarmonyMethod(typeof(NativeSpellWatchdog), nameof(AfterAttackLaunched)));
        harmony.Patch(AccessTools.Method(typeof(AttackSpellController), "OnHeroMoveBackFinished", Type.EmptyTypes),
            prefix: new HarmonyMethod(typeof(NativeSpellWatchdog), nameof(BeforeAttackCompletion)));
    }
    internal static string Lifecycle => applicationPaused ? "paused" : applicationFocused ? "active" : "unfocused";

    internal static void ApplicationPause(bool paused)
    {
        applicationPaused = paused;
        InvalidateObservations();
    }

    internal static void ApplicationFocus(bool focused)
    {
        if (applicationFocused && !focused) FocusLosses++;
        applicationFocused = focused;
        InvalidateObservations();
    }

    private static void InvalidateObservations()
    {
        clock.Invalidate(); generalClock.Invalidate(); nextSample = 0;
    }

    internal static void Arm(string matchId)
    {
        if (!NativeTestControl.DiagnosticsEnabled || !ClientBridge.OwnsLiveMatch || string.IsNullOrEmpty(matchId))
            throw new InvalidOperationException("Spell stall injection requires an active isolated test match");
        if (armedMatch != null || injected != null) throw new InvalidOperationException("Spell stall already armed/injected");
        armedMatch = matchId;
    }
    private static void AfterAttackLaunched(AttackSpellController __instance)
    {
        // The registered callback proves LaunchAttack took the stationary branch, even after it removes itself.
        var listeners = BlockingDiagnostics.Get(ControllerSpell(__instance), "m_stateFinishedListeners") as IEnumerable;
        if (listeners != null && listeners.Cast<object>().Any(listener =>
            BlockingDiagnostics.Get(listener, "m_callback") is Delegate callback && ReferenceEquals(callback.Target, __instance)
            && callback.Method.Name == "OnStaticHeroSourceAttackStateFinished"))
            staticAttacks.GetValue(__instance, _ => new object());
        if (armedMatch == null || ControllerSpell(__instance) == null || __instance.GetPowerTaskList() == null) return;
        bool same = armedMatch == ClientBridge.DiagnosticMatchId;
        armedMatch = null;
        if (!same || !NativeTestControl.DiagnosticsEnabled || !ClientBridge.OwnsLiveMatch) return;
        injected = __instance;
        var detail = Detail(__instance);
        detail["event"] = "STALL_INJECTED";
        detail["matchId"] = ClientBridge.DiagnosticMatchId;
        ClientBridge.RecordFx(detail);
    }

    private static bool BeforeAttackCompletion(AttackSpellController __instance) => !ReferenceEquals(injected, __instance);

    // No assignability test: a subclass can introduce cleanup or reusable ownership we have not audited.
    internal static bool CanRecover(SpellController controller) => controller?.GetType() == typeof(AttackSpellController)
        && staticAttacks.TryGetValue((AttackSpellController)controller, out _)
        && controller.GetSource()?.GetEntity()?.IsHero() == true
        && controller.GetSource().GetEntity().GetTag((GAME_TAG)3211) != 0
        && CardIdle(controller.GetSource()) && CardIdle(controller.GetTarget());

    private static bool CardIdle(Card card) => card != null && TweenCount != null
        && (int)TweenCount.Invoke(null, new object[] { card.gameObject }) == 0;

    internal static object ControllerSpell(object controller) => controller?.GetType() == typeof(AttackSpellController)
        ? BlockingDiagnostics.Get(controller, "m_sourceAttackSpell") : null;

    internal static JsonNode Detail(SpellController controller)
    {
        var task = controller.GetPowerTaskList();
        var spell = ControllerSpell(controller);
        var fsm = BlockingDiagnostics.Get(spell, "m_fsm");
        return new JsonNode {
            { "taskList", task?.GetId() }, { "blockType", task?.GetBlockType().ToString() },
            { "controller", controller.GetType().Name }, { "sourceCardId", controller.GetSource()?.GetEntity()?.GetCardId() },
            { "sourceEntity", controller.GetSource()?.GetEntity()?.GetEntityId() },
            { "attackType", BlockingDiagnostics.Get(controller, "m_attackType")?.ToString() },
            { "staticAttackTag", controller.GetSource()?.GetEntity()?.GetTag((GAME_TAG)3211) },
            { "weaponCardId", controller.GetSource()?.GetEntity()?.GetController()?.GetWeaponCard()?.GetEntity()?.GetCardId() },
            { "spellPrefab", NativeFxTelemetry.Prefab(spell) ?? BlockingDiagnostics.Get(spell, "name") as string },
            { "spellState", BlockingDiagnostics.Get(spell, "m_activeStateType")?.ToString() },
            { "fsmState", BlockingDiagnostics.Get(fsm, "ActiveStateName") as string },
            { "pendingTasks", BlockingDiagnostics.Get(task, "m_pendingTasks") },
            { "injected", ReferenceEquals(controller, injected) }
        };
    }

    internal static void Update(bool ownsMatch, string matchId, ManualLogSource log)
    {
        try
        {
            double now = Time.realtimeSinceStartup;
            if (!ownsMatch)
            {
                armedMatch = null; injected = null; nextSample = 0; generalClock = new NativeStallClock();
                clock.Observe(false, null, null, null, null, now); return;
            }
            if (applicationPaused || !applicationFocused) { InvalidateObservations(); return; }
            if (now < nextSample && now >= nextSample - 1) return;
            nextSample = now + .5;
            var front = GameState.Get();
            try { UpdateGeneral(front, matchId, now, log); }
            catch (Exception error)
            {
                generalClock = new NativeStallClock();
                if (!generalWarned) { generalWarned = true; log?.LogWarning("GENERAL_STALL_WATCHDOG_FAILED " + error); }
            }
            var processor = front?.GetPowerProcessor();
            var task = processor?.GetCurrentTaskList();
            var controllers = Controllers?.GetValue(front) as IEnumerable;
            var controller = controllers?.Cast<SpellController>().FirstOrDefault(c => c != null && ReferenceEquals(c.GetPowerTaskList(), task));
            bool eligible = Finish != null && controller != null && task != null
                && SceneMgr.Get()?.IsTransitioning() == false && Time.timeScale >= 1
                && !front.IsMulliganBusy() && !front.IsMulliganManagerActive()
                && InputManager.Get()?.GetHeldCard() == null && ChoiceCardMgr.Get()?.HasChoices() == false;
            var detail = controller == null ? null : Detail(controller);
            string progress = detail == null ? null : detail["pendingTasks"] + ":" + NativeFxTelemetry.TaskProgress
                + ":" + detail["spellState"] + ":" + detail["fsmState"];
            if (!clock.Observe(eligible, matchId, controller, task, progress, now)) return;
            detail["matchId"] = matchId; detail["elapsedSeconds"] = clock.Elapsed(now);
            detail["clientGameOver"] = front.IsGameOver();
            detail["queueCount"] = processor.GetPowerQueue()?.Count ?? 0;
            detail["blockingContext"] = BlockingDiagnostics.Read(front);
            detail["utc"] = DateTime.UtcNow.ToString("o");
            // Reset/Rewind, swaps, subspells and unknown controllers retain all native cleanup and callbacks.
            if (!CanRecover(controller) || front.IsGameOver())
            {
                detail["status"] = "observe-only";
                detail["reason"] = front.IsGameOver() ? "game-over"
                    : controller.GetType() == typeof(AttackSpellController) ? "moving-attack" : "unaudited-controller";
                Record("STALL_DETECTED", detail, log);
                return;
            }
            LastRecovery = detail;
            try
            {
                Recover(controller);
                detail["status"] = ReferenceEquals(processor.GetCurrentTaskList(), task) ? "released-awaiting-tasks" : "recovered";
            }
            catch (Exception error) { detail["status"] = "failed"; detail["error"] = (error.InnerException ?? error).ToString(); }
            finally
            {
                if (ReferenceEquals(injected, controller)) injected = null;
                Record("STALL_RECOVERY", detail, log);
            }
        }
        catch (Exception error)
        {
            clock.Observe(false, null, null, null, null, 0);
            if (!warned) { warned = true; log?.LogWarning("STALL_WATCHDOG_FAILED " + error); }
        }
    }

    private static void Record(string name, JsonNode detail, ManualLogSource log)
    {
        log?.LogWarning(name + " " + Json.Serialize(detail));
        var journal = new JsonNode { ["event"] = name };
        foreach (var pair in detail) journal[pair.Key] = pair.Value;
        ClientBridge.RecordFx(journal);
    }

    internal static void Recover(SpellController controller)
    {
        if (!CanRecover(controller)) throw new InvalidOperationException("Unaudited controller is observe-only");
        var spell = ControllerSpell(controller) as Spell;
        // No card-owned tween may be in flight. Retire spell callbacks before native cleanup detaches ownership.
        controller.StopAllCoroutines();
        if (!ReferenceEquals(spell, null))
            foreach (string field in new[] { "m_finishedListeners", "m_stateFinishedListeners", "m_stateStartedListeners", "m_spellEventListeners", "m_spellReleasedListeners" })
            {
                var listeners = BlockingDiagnostics.Get(spell, field) as IList;
                if (listeners == null) throw new MissingFieldException(typeof(Spell).Name, field);
                for (int i = listeners.Count - 1; i >= 0; i--)
                    if (BlockingDiagnostics.Get(listeners[i], "m_callback") is Delegate callback && ReferenceEquals(callback.Target, controller))
                        listeners.RemoveAt(i);
            }
        var source = controller.GetSource();
        source?.EnableAttacking(false);
        source?.SetDoNotSort(false);
        if (!ReferenceEquals(spell, null)) AccessTools.Method(typeof(Spell), "ReleaseSpell", Type.EmptyTypes).Invoke(spell, null);
        // Includes immunity visual cleanup before OnFinishedTaskList detaches source/targets, then OnFinished.
        Finish.Invoke(controller, null);
    }
}
