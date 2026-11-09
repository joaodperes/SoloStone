using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using MiniJSON;
using UnityEngine;

internal static partial class NativeSpellWatchdog
{
    private static readonly FieldInfo BlockingSpells = AccessTools.Field(typeof(GameState), "m_serverBlockingSpells");
    private static readonly MethodInfo ResumeTasks = AccessTools.Method(typeof(PowerProcessor), "DoCurrentTaskList", Type.EmptyTypes);
    private static NativeStallClock generalClock = new NativeStallClock();
    private static bool generalWarned;

    // Current/queued history is already authoritative engine work. No input wait is timed, and changing
    // task completion, FSM state, card movement, animation or timeline position restarts the deadline.
    private static void UpdateGeneral(GameState front, string matchId, double now, ManualLogSource log)
    {
        var processor = front?.GetPowerProcessor();
        var task = processor?.GetCurrentTaskList();
        if (task == null && processor?.GetPowerQueue()?.Count > 0) task = processor.GetPowerQueue().Peek();
        bool eligible = task != null && !front.IsGameOver() && SceneMgr.Get()?.IsTransitioning() == false
            && Time.timeScale >= 1 && !front.IsMulliganBusy() && !front.IsMulliganManagerActive()
            && InputManager.Get()?.GetHeldCard() == null && ChoiceCardMgr.Get()?.HasChoices() == false;
        if (!eligible) { generalClock.Observe(false, null, null, null, null, now); return; }
        var controllers = ((Controllers.GetValue(front) as IEnumerable)?.Cast<SpellController>()
            ?? Enumerable.Empty<SpellController>()).Where(c => c != null).ToArray();
        // Retain the audited 30-second attack recovery; general fallback gets twice its grace.
        var spells = ((BlockingSpells.GetValue(front) as IEnumerable)?.Cast<Spell>()
            ?? Enumerable.Empty<Spell>()).Where(s => ((Component)(object)s) != null).ToList();
        var cards = new HashSet<Card>();
        foreach (var controller in controllers)
        {
            foreach (var card in controller.GetSources().Concat(controller.GetTargets())) if (card != null) cards.Add(card);
            if (ControllerSpell(controller) is Spell attack && ((Component)(object)attack) != null && !spells.Contains(attack)) spells.Add(attack);
        }
        var changes = BlockingDiagnostics.Get(task, "m_zoneChangeList") as ZoneChangeList;
        if (changes != null)
            foreach (var change in changes.GetChanges())
                if (change.GetEntity()?.GetCard() is Card card) cards.Add(card);
        foreach (var card in cards)
            foreach (var spell in card.GetComponentsInChildren<Component>(true).OfType<Spell>())
                if ((bool)AccessTools.Method(typeof(Spell), "IsActive", Type.EmptyTypes).Invoke(spell, null)
                    && !string.Equals(BlockingDiagnostics.Get(spell, "m_activeStateType")?.ToString(), "Idle", StringComparison.OrdinalIgnoreCase)
                    && !spells.Contains(spell)) spells.Add(spell);
        string progress = string.Join(";", task.GetTaskList().Select(t => t.IsCompleted() ? "1" : "0"))
            + ":" + BlockingDiagnostics.Get(task, "m_pendingTasks") + ":" + task.IsComplete() + ":" + NativeFxTelemetry.TaskProgress;
        foreach (var spell in spells)
            progress += "|" + ((Component)(object)spell).GetInstanceID() + ":" + BlockingDiagnostics.Get(spell, "m_activeStateType")
                + ":" + BlockingDiagnostics.Get(BlockingDiagnostics.Get(spell, "m_fsm"), "ActiveStateName")
                + AnimationProgress(spell);
        foreach (var card in cards)
            progress += "|" + card.GetEntity()?.GetEntityId() + ":" + card.transform.position.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)
                + ":" + card.transform.rotation.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + ":" + card.IsActorReady() + ":" + card.IsActorLoading();
        if (!generalClock.Observe(true, matchId, processor, task, progress, now, 60)) return;
        var header = task.GetBlockStart();
        var detail = new JsonNode {
            { "matchId", matchId }, { "taskList", task.GetId() }, { "blockType", task.GetBlockType().ToString() },
            { "sourceEntity", task.GetSourceEntity(false)?.GetEntityId() },
            { "sourceCardId", task.GetSourceEntity(false)?.GetCardId() },
            { "effectCardIds", header?.EffectCardId?.ToArray() }, { "effectIndex", header?.EffectIndex },
            { "sourceEntities", header?.Entities?.ToArray() }, { "subOption", header?.SubOption },
            { "triggerKeyword", header?.TriggerKeyword }, { "elapsedSeconds", generalClock.Elapsed(now) },
            { "queueCount", processor.GetPowerQueue()?.Count ?? 0 },
            { "spells", spells.Select(s => (object)new JsonNode {
                { "id", ((Component)(object)s).GetInstanceID() }, { "prefab", NativeFxTelemetry.Prefab(s) ?? ((Component)(object)s).name },
                { "sourceEntity", (AccessTools.Method(typeof(Spell), "GetSourceCard", Type.EmptyTypes).Invoke(s, null) as Card)?.GetEntity()?.GetEntityId() },
                { "sourceCardId", (AccessTools.Method(typeof(Spell), "GetSourceCard", Type.EmptyTypes).Invoke(s, null) as Card)?.GetEntity()?.GetCardId() },
                { "state", BlockingDiagnostics.Get(s, "m_activeStateType")?.ToString() },
                { "fsmState", BlockingDiagnostics.Get(BlockingDiagnostics.Get(s, "m_fsm"), "ActiveStateName") }
            }).ToArray() }, { "blockingContext", BlockingDiagnostics.Read(front) }, { "utc", DateTime.UtcNow.ToString("o") }
        };
        // Controller coroutines can own input/audio/reset cleanup. Even spell callbacks may bypass
        // that tail, so unaudited controllers keep all presentation ownership until native completion.
        var unaudited = controllers.Where(c => !CanRecover(c)).ToArray();
        if (unaudited.Length != 0)
        {
            detail["status"] = "observe-only";
            detail["reason"] = "unaudited-controller";
            detail["controllers"] = unaudited.Select(c => c.GetType().Name).ToArray();
            Record("STALL_DETECTED", detail, log);
            return;
        }
        LastRecovery = detail;
        try
        {
            // ForceDeactivate runs native state/finished listeners and zone unblock. ReleaseSpell alone
            // clears listeners too early; ForceKill alone misses card-owned burn and hand-full spells.
            bool retiredActiveSpell = spells.Any(s => (bool)AccessTools.Method(typeof(Spell), "IsActive", Type.EmptyTypes).Invoke(s, null));
            foreach (var spell in spells.ToArray())
                if (((Component)(object)spell) != null) AccessTools.Method(typeof(Spell), "ForceDeactivate", Type.EmptyTypes).Invoke(spell, null);
            foreach (var controller in controllers)
                if (!retiredActiveSpell && controller != null && controller.GetPowerTaskList() != null
                    && ReferenceEquals(controller.GetPowerTaskList(), processor.GetCurrentTaskList()))
                    Recover(controller);
            bool waitingOnSpell = spells.Any(s => ((Component)(object)s) != null
                && (bool)AccessTools.Method(typeof(Spell), "IsActive", Type.EmptyTypes).Invoke(s, null));
            foreach (var card in cards)
            {
                if (card == null || card.IsActorLoading() || waitingOnSpell) continue;
                // Retired presentation cannot finish a stale opponent draw. The pending native zone
                // list still owns the real move, even if a later HIDE already changed its logical zone.
                AccessTools.Field(typeof(Card), "m_beingDrawnByOpponent")?.SetValue(card, false);
                front.ClearCardBeingDrawn(card);
                card.SetDoNotSort(false);
            }
            // Reissue a lost layout callback through the native zone; never fabricate completion.
            if (BlockingDiagnostics.Get(changes, "m_dirtyZones") is IEnumerable dirty)
                foreach (var zone in dirty.Cast<Zone>().ToArray()) zone.UpdateLayout();
            // Do not end a zone list or mark tasks completed: its coroutine owns actual moves/layout/callbacks.
            if (!retiredActiveSpell && ReferenceEquals(processor.GetCurrentTaskList(), task) && changes == null
                && controllers.Length == 0)
                ResumeTasks.Invoke(processor, null);
            processor.ForceStopHistoryBlocking();
            front.SetBusy(false);
            detail["status"] = ReferenceEquals(processor.GetCurrentTaskList(), task) ? "released-awaiting-tasks" : "recovered";
        }
        catch (Exception error) { detail["status"] = "failed"; detail["error"] = (error.InnerException ?? error).ToString(); }
        finally
        {
            Record("STALL_RECOVERY", detail, log);
            // Retry only after another full no-progress interval, including a failed native callback.
            generalClock = new NativeStallClock();
        }
    }

    private static string AnimationProgress(Spell spell)
    {
        string result = "";
        foreach (var component in ((Component)(object)spell).GetComponentsInChildren<Component>(true))
        {
            if (component == null) continue;
            if (component.GetType().Name == "Animator" && Equals(BlockingDiagnostics.Get(component, "isActiveAndEnabled"), true))
            {
                int layers = Convert.ToInt32(BlockingDiagnostics.Get(component, "layerCount"));
                var state = AccessTools.Method(component.GetType(), "GetCurrentAnimatorStateInfo", new[] { typeof(int) });
                for (int layer = 0; layer < layers; layer++)
                    result += ":" + BlockingDiagnostics.Get(state.Invoke(component, new object[] { layer }), "normalizedTime");
            }
            // Timeline can remain in one FSM state for a long authored animation.
            if (component.GetType().FullName == "UnityEngine.Playables.PlayableDirector")
                result += ":" + BlockingDiagnostics.Get(component, "time");
        }
        return result;
    }
}
