using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;
using MiniJSON;
using UnityEngine;

internal static class NativeFxTelemetry
{
    private sealed class EffectBinding
    {
        internal int Id;
        internal WeakReference Task, Source;
        internal string ControllerType;
        internal string Prefab;
        internal int SubSpellId;
    }
    private static readonly ConditionalWeakTable<CardEffect, EffectBinding> effects = new ConditionalWeakTable<CardEffect, EffectBinding>();
    private static readonly ConditionalWeakTable<object, EffectBinding> spells = new ConditionalWeakTable<object, EffectBinding>();
    private static readonly ConditionalWeakTable<object, EffectBinding> subSpells = new ConditionalWeakTable<object, EffectBinding>();
    private static readonly HashSet<MethodBase> observationWarnings = new HashSet<MethodBase>();
    private static readonly List<string> installed = new List<string>(), failed = new List<string>();
    private static FieldInfo effectPath, effectSpell, effectOwner, pending, active, unresolved, powerSpell, spellTask, spellState, spellTargets;
    private static FxTelemetryLog records;
    private static readonly Dictionary<int, WeakReference> observedSpells = new Dictionary<int, WeakReference>();
    private static readonly Dictionary<int, Dictionary<string, object>> spellIdentities = new Dictionary<int, Dictionary<string, object>>();
    private static readonly Queue<int> trackedSpellIds = new Queue<int>();
    private static bool trackingTruncated;
    internal static void CloseDisk() => records?.Dispose();
    private static Func<bool> configured;
    private static ManualLogSource log;
    private static Harmony patcher;
    private static bool hooksAttempted;
    private static string runId;
    private static int effectSequence;
    private static int subSpellSequence;
    private static FieldInfo subSpellStart, subSpellInstance;
    private static PropertyInfo subSpellGuid, subSpellSource;
    private static readonly BlockedTelemetryGate blockedGate = new BlockedTelemetryGate();
    private static MethodInfo blockingPower;
    private static long taskProgress;
    internal static long TaskProgress => taskProgress;
    internal static string Prefab(object spell) => spell != null && spells.TryGetValue(spell, out var binding) ? binding.Prefab : null;
    private static float nextBlockedSample;
    private static bool blockedWarning;

    internal static void Initialize(Harmony harmony, ManualLogSource logger, string run, Func<bool> enabled, string reports)
    {
        log = logger; runId = run; configured = enabled; patcher = harmony;
        records = new FxTelemetryLog(reports + @"\fx-telemetry.tsv");
        InstallHooks();
    }

    private static void InstallHooks()
    {
        if (hooksAttempted) return; // Always-on match journals also need gameplay FX, without diagnostic opt-in.
        hooksAttempted = true;
        var harmony = patcher;
        try
        {
            effectPath = Field(typeof(CardEffect), "m_spellPath");
            effectSpell = Field(typeof(CardEffect), "m_spell");
            effectOwner = Field(typeof(CardEffect), "m_owner");
            powerSpell = Field(typeof(PowerSpellController), "m_powerSpell");
            spellTask = Field(typeof(Spell), "m_taskList");
            spellState = Field(typeof(Spell), "m_activeStateType");
            spellTargets = Field(typeof(Spell), "m_targets");
            pending = Field(typeof(ZoneMgr), "m_pendingLocalChangeLists");
            active = Field(typeof(ZoneMgr), "m_activeLocalChangeLists");
            unresolved = Field(typeof(ZoneMgr), "m_localChangeListHistory");
            blockingPower = AccessTools.DeclaredMethod(typeof(GameState), "IsBlockingPowerProcessor", Type.EmptyTypes);
            Hook(harmony, typeof(PowerProcessor), "OnWillProcessTaskList", typeof(void), typeof(PowerTaskList));
            Hook(harmony, typeof(PowerTaskList), "OnTaskCompleted", typeof(void));
            Hook(harmony, typeof(PowerProcessor), "DoTaskListWithSpellController", typeof(bool), typeof(GameState), typeof(PowerTaskList), typeof(Entity));
            Hook(harmony, typeof(SpellController), "AttachPowerTaskList", typeof(bool), typeof(PowerTaskList));
            Hook(harmony, typeof(PowerSpellController), "GetOrCreateEffect", typeof(CardEffect), typeof(Card), typeof(PowerTaskList));
            Hook(harmony, typeof(PowerSpellController), "InitPowerSpell", typeof(bool), typeof(CardEffect), typeof(Card));
            Hook(harmony, typeof(PowerSpellController), "ActivatePowerSpell", typeof(bool));
            Hook(harmony, typeof(TriggerSpellController), "InitEffect", typeof(CardEffect), typeof(Card));
            Hook(harmony, typeof(TriggerSpellController), "InitTriggerSpell", typeof(void), typeof(CardEffect), typeof(Card));
            Hook(harmony, typeof(TriggerSpellController), "ActivateTriggerSpell", typeof(bool), typeof(int));
            // TriggerKeyword=TRIGGER_VISUAL (32) and friends: the actor bauble spell runs before the CardDef trigger effect.
            Hook(harmony, typeof(TriggerSpellController), "ActivateActorTriggerSpell", typeof(bool), typeof(int));
            Hook(harmony, typeof(CardEffect), "GetSpell", typeof(Spell), typeof(bool));
            Hook(harmony, typeof(SpellManager), "CreateSpell", typeof(Spell), typeof(string));
            Hook(harmony, typeof(SpellManager), "GetSpell", typeof(Spell), typeof(string));
            var subInstanceType = typeof(SubSpellController).GetNestedType("SubSpellInstance", BindingFlags.Public | BindingFlags.NonPublic);
            subSpellStart = Field(subInstanceType, "SubSpellStartTask");
            subSpellInstance = Field(subInstanceType, "SpellInstance");
            subSpellGuid = subSpellStart.FieldType.GetProperty("SpellPrefabGUID");
            subSpellSource = subSpellStart.FieldType.GetProperty("SourceEntityID");
            var referenceType = AccessTools.DeclaredMethod(typeof(SubSpellController), "OnSubSpellLoadAttempted").GetParameters()[0].ParameterType;
            Hook(harmony, typeof(SubSpellController), "GetSubSpellInstanceForTasklist", subInstanceType, typeof(PowerTaskList));
            Hook(harmony, typeof(SubSpellController), "OnSubSpellLoadAttempted", typeof(void), referenceType, typeof(GameObject), typeof(object));
            Hook(harmony, typeof(SubSpellController), "AttachTasklistToSubSpell", typeof(bool), typeof(PowerTaskList), subInstanceType);
            Hook(harmony, typeof(SubSpellController), "OnSubSpellFinished", typeof(void), typeof(Spell), typeof(object));
            Hook(harmony, typeof(Spell), "AttachPowerTaskList", typeof(bool), typeof(PowerTaskList));
            Hook(harmony, typeof(Spell), "AddSinglePowerTarget", typeof(bool));
            Hook(harmony, typeof(Spell), "AddMultiplePowerTargets", typeof(bool));
            // SpellStateType lives in Blizzard.T5.Game.dll, absent from the local reference subset.
            var stateType = AccessTools.DeclaredMethod(typeof(Spell), "ActivateState").GetParameters()[0].ParameterType;
            Hook(harmony, typeof(Spell), "ActivateState", typeof(void), stateType);
            Hook(harmony, typeof(Spell), "ChangeStateImpl", typeof(void), stateType);
            foreach (string stage in new[] { "OnProcessTaskList", "LaunchAttack", "OnHeroMoveBackFinished", "OnFinishedTaskList" })
                Hook(harmony, typeof(AttackSpellController), stage, typeof(void));
            foreach (string stage in new[] { "OnSourceAttackStateStarted", "OnHeroSourceAttackStateFinished", "OnStaticHeroSourceAttackStateFinished" })
                Hook(harmony, typeof(AttackSpellController), stage, typeof(void), typeof(Spell), stateType, typeof(object));
            Hook(harmony, typeof(SubSpellController), "OnSubSpellStateFinished", typeof(void), typeof(Spell), stateType, typeof(object));
            Hook(harmony, typeof(InputManager), "DropHeldSpellLikeCard", typeof(void), typeof(Card), typeof(Entity), typeof(bool).MakeByRefType());
        }
        catch (Exception error) { failed.Add(error.Message); Warn("FX_TELEMETRY_INIT_DISABLED " + error); }
    }

    private static FieldInfo Field(Type type, string name) => AccessTools.Field(type, name) ?? throw new MissingFieldException(type.FullName, name);
    private static void Hook(Harmony harmony, Type type, string name, Type result, params Type[] args)
    {
        string signature = type.Name + "." + name + "(" + string.Join(",", args.Select(arg => arg.Name)) + ")";
        try
        {
            var method = AccessTools.DeclaredMethod(type, name, args);
            if (method == null || method.ReturnType != result) throw new MissingMethodException(signature);
            // Use the enum's int storage so the disabled diagnostic hook does not box its argument.
            if (type == typeof(Spell) && name == "ChangeStateImpl" && Enum.GetUnderlyingType(args[0]) != typeof(int))
                throw new InvalidOperationException("Spell state storage changed");
            string finalizer = type == typeof(Spell) && name == "ChangeStateImpl" ? nameof(StateFinalizer)
                : type == typeof(InputManager) && name == "DropHeldSpellLikeCard" ? nameof(DropFinalizer)
                : (result == typeof(void) ? "VoidFinalizer" : result == typeof(bool) ? "BoolFinalizer" : "ObjectFinalizer") + args.Length;
            harmony.Patch(method, finalizer: new HarmonyMethod(typeof(NativeFxTelemetry), finalizer));
            installed.Add(signature);
        }
        catch (Exception error) { failed.Add(signature); Warn("FX_TELEMETRY_HOOK_DISABLED " + signature + " " + error); }
    }

    private static bool DetailedEnabled => (configured?.Invoke() ?? false) || NativeTestControl.DiagnosticsEnabled;
    private static void StateFinalizer(MethodBase __originalMethod, object __instance, int __0, Exception __exception)
    {
        if (!DetailedEnabled) return;
        Observe(__originalMethod, __instance, new[] { Enum.ToObject(spellState.FieldType, __0) }, null, __exception);
    }

    // HarmonyX 2.9.0 writes its magic argument array back to native ref parameters; use by-value arguments only.
    private static void VoidFinalizer0(MethodBase __originalMethod, object __instance, Exception __exception)
    { Observe(__originalMethod, __instance, Array.Empty<object>(), null, __exception); }
    private static void VoidFinalizer1(MethodBase __originalMethod, object __instance, object __0, Exception __exception)
    { Observe(__originalMethod, __instance, new[] { __0 }, null, __exception); }
    private static void VoidFinalizer2(MethodBase __originalMethod, object __instance, object __0, object __1, Exception __exception)
    { Observe(__originalMethod, __instance, new[] { __0, __1 }, null, __exception); }
    private static void VoidFinalizer3(MethodBase __originalMethod, object __instance, object __0, object __1, object __2, Exception __exception)
    { Observe(__originalMethod, __instance, new[] { __0, __1, __2 }, null, __exception); }
    private static void BoolFinalizer0(MethodBase __originalMethod, object __instance, bool __result, Exception __exception)
    { Observe(__originalMethod, __instance, Array.Empty<object>(), __result, __exception); }
    private static void BoolFinalizer1(MethodBase __originalMethod, object __instance, object __0, bool __result, Exception __exception)
    { Observe(__originalMethod, __instance, new[] { __0 }, __result, __exception); }
    private static void BoolFinalizer2(MethodBase __originalMethod, object __instance, object __0, object __1, bool __result, Exception __exception)
    { Observe(__originalMethod, __instance, new[] { __0, __1 }, __result, __exception); }
    private static void BoolFinalizer3(MethodBase __originalMethod, object __instance, object __0, object __1, object __2, bool __result, Exception __exception)
    { Observe(__originalMethod, __instance, new[] { __0, __1, __2 }, __result, __exception); }
    private static void ObjectFinalizer1(MethodBase __originalMethod, object __instance, object __0, object __result, Exception __exception)
    { Observe(__originalMethod, __instance, new[] { __0 }, __result, __exception); }
    private static void ObjectFinalizer2(MethodBase __originalMethod, object __instance, object __0, object __1, object __result, Exception __exception)
    { Observe(__originalMethod, __instance, new[] { __0, __1 }, __result, __exception); }
    // cancelDrop is cancellation, not success. Omit the ref output entirely rather than risk native write-back.
    private static void DropFinalizer(MethodBase __originalMethod, object __instance, object __0, object __1, Exception __exception)
    { Observe(__originalMethod, __instance, new[] { __0, __1 }, null, __exception); }

    private static void Observe(MethodBase method, object instance, object[] args, object result, Exception exception)
    {
        try
        {
            if (records == null) return;
            if (instance is UnityEngine.Object native && native == null) return;
            bool flag = (configured?.Invoke() ?? false) || ClientBridge.MatchJournalEnabled, control = NativeTestControl.DiagnosticsEnabled;
            if (!flag && !control) return;
            bool inMatch = SceneMgr.Get()?.GetMode() == SceneMgr.Mode.GAMEPLAY && GameState.Get()?.GetGameEntity() != null;
            if (!FxTelemetryLog.Enabled(flag, control, inMatch)) return;
            PowerTaskList task = args.OfType<PowerTaskList>().FirstOrDefault();
            // Failed attachments can restore m_taskList to null. Arguments and saved bindings still prove gameplay.
            bool idlePreview = exception == null && !(result is bool successResult && !successResult) && task == null;
            if (idlePreview && method.DeclaringType == typeof(SpellManager) && result != null
                && (!spells.TryGetValue(result, out var loadedBinding)
                    || (loadedBinding.Task?.Target == null && loadedBinding.SubSpellId == 0))
                && records.FilterHoverName(args[0] as string)) return;
            if (idlePreview && instance is Spell hover && spellTask.GetValue(hover) == null
                && (!spells.TryGetValue(hover, out var hoverBinding)
                    || (hoverBinding.Task?.Target == null && hoverBinding.SubSpellId == 0))
                && records.FilterHoverName(((UnityEngine.Object)(object)hover).name)) return;
            if (method.DeclaringType == typeof(PowerTaskList) && method.Name == "OnTaskCompleted")
            { taskProgress++; return; }
            if (method.DeclaringType == typeof(PowerProcessor) && method.Name == "OnWillProcessTaskList") taskProgress++;
            Entity source = args.OfType<Entity>().FirstOrDefault() ?? args.OfType<Card>().FirstOrDefault()?.GetEntity();
            string controllerType = (instance as SpellController)?.GetType().Name
                ?? (typeof(SpellController).IsAssignableFrom(method.DeclaringType) ? method.DeclaringType.Name : null);
            var controller = instance as SpellController;
            if (task == null && controller != null && !(controller is SubSpellController)) task = controller.GetPowerTaskList();
            CardEffect effect = args.OfType<CardEffect>().FirstOrDefault();
            object spell = null;
            string prefab = null;
            int? requested = null;
            int? subSpellId = null;
            bool? spellObserved = null, effectObserved = null;
            if (((MethodInfo)method).ReturnType == typeof(CardEffect))
            {
                effect = result as CardEffect;
                effectObserved = effect != null;
            }
            if (instance is CardEffect cardEffect) effect = cardEffect;
            if (effect != null)
            {
                var binding = effects.GetValue(effect, _ => new EffectBinding { Id = ++effectSequence });
                if (task != null) { binding.Task = new WeakReference(task); binding.Source = new WeakReference(source); binding.ControllerType = controllerType; }
                else { task = binding.Task?.Target as PowerTaskList; source = binding.Source?.Target as Entity; controllerType = binding.ControllerType; }
                if (source == null) source = ((Card)effectOwner.GetValue(effect))?.GetEntity();
                spell = effectSpell.GetValue(effect);
                if (spell != null) { spells.Remove(spell); spells.Add(spell, binding); }
                prefab = effectPath.GetValue(effect) as string;
                binding.Prefab = prefab;
                effectObserved = true; spellObserved = spell != null;
            }
            if (method.DeclaringType == typeof(SpellManager))
            {
                spell = result; prefab = (string)args[0]; spellObserved = spell != null;
                if (spell != null) spells.GetValue(spell, _ => new EffectBinding()).Prefab = prefab;
            }
            if (method.DeclaringType == typeof(SubSpellController))
            {
                object nativeInstance = method.Name == "GetSubSpellInstanceForTasklist" ? result
                    : method.Name == "OnSubSpellLoadAttempted" ? args[2]
                    : method.Name == "AttachTasklistToSubSpell" ? args[1] : null;
                if (nativeInstance != null)
                {
                    var binding = subSpells.GetValue(nativeInstance, _ => new EffectBinding {
                        SubSpellId = ++subSpellSequence, ControllerType = nameof(SubSpellController) });
                    var start = subSpellStart.GetValue(nativeInstance);
                    prefab = binding.Prefab = (string)subSpellGuid.GetValue(start, null);
                    int sourceId = (int)subSpellSource.GetValue(start, null);
                    source = sourceId == 0 ? null : GameState.Get()?.GetEntity(sourceId);
                    if (task != null) binding.Task = new WeakReference(task);
                    else task = binding.Task?.Target as PowerTaskList;
                    binding.Source = new WeakReference(source);
                    subSpellId = binding.SubSpellId;
                    spell = subSpellInstance.GetValue(nativeInstance);
                    spellObserved = spell != null;
                    if (spell != null) { spells.Remove(spell); spells.Add(spell, binding); }
                }
                else { spell = args.OfType<Spell>().FirstOrDefault(); spellObserved = spell != null; }
            }
            if (method.DeclaringType == typeof(Spell))
            {
                spell = instance;
                if (task == null) task = spellTask.GetValue(spell) as PowerTaskList;
                spellObserved = true;
                if (method.Name == "ActivateState" || method.Name == "ChangeStateImpl") requested = Convert.ToInt32(args[0]);
            }
            if (instance is AttackSpellController)
            {
                spell = BlockingDiagnostics.Get(instance, "m_sourceAttackSpell");
                spellObserved = spell != null;
                source = source ?? controller.GetSource()?.GetEntity();
            }
            if (instance is PowerSpellController && effect == null)
            { spell = powerSpell.GetValue(instance); spellObserved = spell != null; }
            if (spell != null && spells.TryGetValue(spell, out var spellBinding))
            {
                if (spellBinding.SubSpellId != 0) subSpellId = spellBinding.SubSpellId;
                prefab = prefab ?? spellBinding.Prefab;
                controllerType = controllerType ?? spellBinding.ControllerType;
                task = task ?? spellBinding.Task?.Target as PowerTaskList;
                source = source ?? spellBinding.Source?.Target as Entity;
            }
            if (spell != null && (UnityEngine.Object)spell == null) { spell = null; spellObserved = false; }
            bool? outcome = result is bool success ? (bool?)success : null;
            Write(task, source, controllerType, method.DeclaringType.Name + "." + method.Name, effect, effectObserved,
                spell, spellObserved, prefab, requested, exception == null ? outcome : null, exception, flag, control, subSpellId, exception == null && method.DeclaringType == typeof(Spell) && method.Name == "ChangeStateImpl" ? args[0]?.ToString() : null);
        }
        catch (Exception error)
        {
            // A transient destroyed object or incomplete task must not retire a process-wide hook.
            if (observationWarnings.Add(method)) Warn("FX_TELEMETRY_OBSERVATION_FAILED " + method + " " + error);
        }
    }

    private static void Write(PowerTaskList task, Entity source, string controllerType, string stage,
        CardEffect effect, bool? effectPresent, object spell, bool? spellPresent, string prefab, int? requested,
        bool? result, Exception error, bool flag, bool control, int? subSpellId = null, string reachedState = null)
    {
        var block = task?.GetBlockStart();
        // Multi-source triggers need a row per entity, including an ID whose entity no longer exists.
        var ids = source != null ? new[] { source.GetEntityId() } : block?.Entities?.ToArray() ?? new int[0];
        if (ids.Length == 0) ids = new[] { 0 };
        foreach (int id in ids)
        {
            var entity = source ?? (id == 0 ? null : GameState.Get()?.GetEntity(id));
            int index = block?.Entities?.IndexOf(id) ?? -1;
            string effectCardId = index >= 0 && index < (block?.EffectCardId?.Count ?? 0) ? block.EffectCardId[index] : null;
            if (effectCardId == "") effectCardId = entity?.GetCardId();
            int? owner = entity?.GetControllerId();
            var friendly = GameState.Get()?.GetFriendlySidePlayer();
            var opponent = GameState.Get()?.GetOpposingSidePlayer();
            var zone = ZoneMgr.Get();
            var row = new FxTelemetryRecord {
                RunId = runId, MatchId = ClientBridge.DiagnosticMatchId, Event = stage, Frame = Time.frameCount,
                Realtime = Time.realtimeSinceStartup, TaskList = task?.GetId(), BlockType = task?.GetBlockType().ToString(),
                SourceEntity = id == 0 ? (int?)null : id, CardId = entity?.GetCardId(), EffectCardId = effectCardId,
                EffectIndex = block?.EffectIndex, SubOption = block?.SubOption, TriggerKeyword = block?.TriggerKeyword, Controller = owner,
                Side = owner == null ? null : owner == friendly?.GetPlayerId() ? "friendly" : owner == opponent?.GetPlayerId() ? "opponent" : "other",
                ControllerType = controllerType, EffectPresent = effectPresent, SpellPresent = spellPresent,
                SubSpellId = subSpellId,
                EffectId = effect == null ? (int?)null : effects.GetValue(effect, _ => new EffectBinding { Id = ++effectSequence }).Id,
                Prefab = prefab, SpellId = spell == null ? (int?)null : ((UnityEngine.Object)spell).GetInstanceID(),
                SpellName = spell == null ? null : ((UnityEngine.Object)spell).name,
                State = spell == null ? (int?)null : Convert.ToInt32(spellState.GetValue(spell)), RequestedState = requested,
                StateName = spell == null ? null : spellState.GetValue(spell)?.ToString(), ReachedStateName = reachedState,
                Targets = spell == null ? (int?)null : ((ICollection)spellTargets.GetValue(spell))?.Count, Result = result,
                LocalPending = zone == null || id == 0 ? (bool?)null : Contains(pending.GetValue(zone), id),
                LocalActive = zone == null || id == 0 ? (bool?)null : Contains(active.GetValue(zone), id),
                LocalUnresolved = zone == null || id == 0 ? (bool?)null : Contains(unresolved.GetValue(zone), id),
                Error = error == null ? null : error.GetType().Name + ": " + error.Message
            };
            if (spell != null && DetailedEnabled) TrackSpell(spell, row);
            Record(row, flag, control, true);
        }
    }

    private static void TrackSpell(object spell, FxTelemetryRecord row)
    {
        int id = row.SpellId.Value;
        if (!observedSpells.TryGetValue(id, out var reference))
        {
            // Rotate a bounded slice on admission, including at capacity; never scan all 4,096 per miss.
            for (int i = 0, count = Math.Min(16, trackedSpellIds.Count); i < count; i++)
            {
                int key = trackedSpellIds.Dequeue();
                if (!observedSpells.TryGetValue(key, out var candidate)) continue;
                if (!(candidate.Target is UnityEngine.Object live) || live == null)
                { observedSpells.Remove(key); spellIdentities.Remove(key); }
                else trackedSpellIds.Enqueue(key);
            }
            if (observedSpells.Count >= 4096) { trackingTruncated = true; return; }
            observedSpells[id] = reference = new WeakReference(spell);
            trackedSpellIds.Enqueue(id);
        }
        else if (!ReferenceEquals(reference.Target, spell)) reference.Target = spell;
        if (!spellIdentities.TryGetValue(id, out var identity))
            spellIdentities[id] = identity = new Dictionary<string, object>(5);
        // Update just identity fields, without capturing/serializing another full telemetry record.
        if (row.MatchId != null) identity["matchId"] = FxTelemetryRecord.Bound(row.MatchId, 256);
        identity["spellId"] = id;
        if (row.SpellName != null) identity["spellName"] = FxTelemetryRecord.Bound(row.SpellName, 256);
        if (row.Prefab != null) identity["prefab"] = FxTelemetryRecord.Bound(row.Prefab, 256);
        if (row.SourceEntity.HasValue) identity["sourceEntity"] = row.SourceEntity.Value;
    }

    private static void Record(FxTelemetryRecord row, bool flag, bool control, bool inMatch)
    {
        // Journal batches are the disk sink. A full/failed legacy TSV must never disable a native hook.
        records.Record(flag, control, inMatch, row, writeDisk: false, journal: ClientBridge.RecordFx, trackLifecycle: DetailedEnabled);
    }

    private static bool Contains(object lists, int entityId)
    {
        if (!(lists is IEnumerable sequence)) return false;
        foreach (object item in sequence)
            if (item is ZoneChangeList list && list.GetChanges()?.Any(change => change?.GetEntity()?.GetEntityId() == entityId) == true) return true;
        return false;
    }

    internal static void Tick()
    {
        try
        {
            if (records == null) return;
            bool flag = (configured?.Invoke() ?? false) || ClientBridge.MatchJournalEnabled, control = NativeTestControl.DiagnosticsEnabled;
            bool enabled = flag || control;
            float now = Time.realtimeSinceStartup;
            if (!enabled)
            { blockedGate.Poll(now, false, false, false, null, null, null); return; }
            InstallHooks();
            if (now < nextBlockedSample) return;
            nextBlockedSample = now + 1;
            ClientBridge.FlushMatchJournal();
            var front = GameState.Get();
            bool inMatch = SceneMgr.Get()?.GetMode() == SceneMgr.Mode.GAMEPLAY && front?.GetGameEntity() != null;
            bool blocked = inMatch && blockingPower?.ReturnType == typeof(bool) && (bool)blockingPower.Invoke(front, null);
            var processor = inMatch ? front.GetPowerProcessor() : null;
            var task = processor?.GetCurrentTaskList();
            // Receipts/completions catch progress between samples; current task/pending/queue catch transitions too.
            string progress = taskProgress + ":" + task?.GetId() + ":" + BlockingDiagnostics.Get(task, "m_pendingTasks")
                + ":" + processor?.GetPowerQueue()?.Count;
            var detail = blocked ? BlockingDiagnostics.Read(front) : null;
            if (!blockedGate.Poll(now, enabled, inMatch, blocked, ClientBridge.DiagnosticMatchId, progress,
                detail?["blockingCauses"] as string)) return;
            Record(new FxTelemetryRecord {
                RunId = runId, MatchId = ClientBridge.DiagnosticMatchId, Event = "BLOCKED", Frame = Time.frameCount,
                Realtime = now, TaskList = task?.GetId(), BlockType = task?.GetBlockType().ToString(),
                BlockingCauses = detail["blockingCauses"] as string, BlockingSpells = detail["blockingSpells"] as string,
                BlockingSpellControllers = detail["blockingSpellControllers"] as string
            }, flag, control, inMatch);
        }
        catch (Exception error)
        {
            blockedGate.Poll(0, false, false, false, null, null, null);
            if (!blockedWarning) { blockedWarning = true; Warn("FX_TELEMETRY_BLOCKED_SAMPLE_FAILED " + error); }
        }
    }

    private static void Warn(string message) { try { log?.LogWarning(message); } catch { } }
    internal static JsonNode Status(string name, int source, int seconds)
    {
        if (!DetailedEnabled) return new JsonNode { { "trackingEnabled", false }, { "trackingComplete", false } };
        var live = new Dictionary<int, string>();
        bool complete = !trackingTruncated;
        foreach (var pair in observedSpells.ToArray())
        {
            try
            {
                var spell = pair.Value.Target as UnityEngine.Object;
                if (spell == null) continue;
                live[pair.Key] = spellState.GetValue(spell)?.ToString() ?? "unavailable";
                if (live[pair.Key] == "unavailable") complete = false;
            }
            catch { live[pair.Key] = "unavailable"; complete = false; }
        }
        var data = records?.Status(Time.realtimeSinceStartup, seconds, name, source, ClientBridge.DiagnosticMatchId, live, spellIdentities.Values) ?? new JsonNode();
        data["trackingEnabled"] = true;
        data["trackingComplete"] = complete;
        data["observationWarnings"] = TestControlProtocol.List(observationWarnings.Select(method => (object)method.ToString()));
        return data;
    }

    internal static JsonNode Read(int after, int limit)
    {
        var data = records?.Read(after, limit) ?? new JsonNode();
        data["configured"] = configured != null && configured();
        data["testControl"] = NativeTestControl.DiagnosticsEnabled;
        data["matchJournal"] = ClientBridge.MatchJournalEnabled;
        data["installedHooks"] = TestControlProtocol.List(installed.Cast<object>());
        data["failedHooks"] = TestControlProtocol.List(failed.Cast<object>());
        data["disabledHooks"] = new JsonList();
        data["observationWarnings"] = TestControlProtocol.List(observationWarnings.Select(method => (object)(method.DeclaringType.Name + "." + method)));
        return data;
    }
}
