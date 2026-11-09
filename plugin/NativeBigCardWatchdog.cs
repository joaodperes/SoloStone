using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using MiniJSON;
using UnityEngine;

internal static class NativeBigCardWatchdog
{
    // Native uses <=4s plus spell time. Keep 6s margin, also respecting explicit longer displays.
    private const double TimeoutSeconds = 10, DisplayMarginSeconds = 6;
    private static readonly string[][] ReflectionBindings =
    {
        new[] { "Method", "HistoryManager", "RunFinishedCallbackAndDestroyBigCard", "", "", "" },
        new[] { "Method", "HistoryManager", "CutoffBigCardTransformEffects", "", "", "" },
        new[] { "Field", "HistoryManager", "m_pendingBigCardEntry", "", "", "" },
        new[] { "Field", "HistoryManager/BigCardEntry", "m_finishedCallback", "", "", "" },
        new[] { "Field", "HistoryManager", "m_showingBigCard", "", "", "" }
    };
    private static readonly Dictionary<string, MethodInfo> bindings = ResolveBindings();
    private static readonly Dictionary<string, FieldInfo> admissionBindings = ResolveAdmissionBindings();
    private static HistoryManager observedHistory;
    private static HistoryCard observedCard;
    private static object observedAdmission;
    private static string observedMatch;
    private static double since, normalSeconds;
    private static bool attempted;
    private static FieldInfo counterCardBinding;
    private static MethodInfo counterCallbackBinding, counterRemoveBinding, counterSpellBinding;
    private sealed class CounterDisplay
    {
        internal HistoryManager History;
        internal HistoryCard Card;
        internal Spell Spell;
        internal Delegate Callback;
    }
    private static CounterDisplay counterDisplay, observedCounter;
    internal static JsonNode LastRelease { get; private set; }

    internal static void Initialize(Harmony harmony)
    {
        try
        {
            var start = AccessTools.Method(typeof(HistoryManager), "PlayBigCardCounteredEffects", Type.EmptyTypes);
            counterCardBinding = AccessTools.Field(typeof(HistoryManager), "m_currentBigCard");
            foreach (var method in typeof(Spell).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var args = method.GetParameters();
                if (method.Name == "RemoveStateFinishedCallback" && method.ReturnType == typeof(bool) && args.Length == 2
                    && typeof(Delegate).IsAssignableFrom(args[0].ParameterType) && args[1].ParameterType == typeof(object))
                    counterRemoveBinding = method;
            }
            // Spell enums and callback delegate live in Blizzard.T5.Game.dll, outside the compile-time reference subset.
            var callbackType = counterRemoveBinding?.GetParameters()[0].ParameterType;
            var callbackArgs = callbackType?.GetMethod("Invoke")?.GetParameters();
            if (callbackArgs == null || callbackArgs.Length != 3 || callbackArgs[0].ParameterType != typeof(Spell)
                || !callbackArgs[1].ParameterType.IsEnum || callbackArgs[2].ParameterType != typeof(object)) return;
            // The compiler numbers this lambda by its place in the class, so a retail patch can renumber it. Match the one lambda of that method with this shape.
            counterCallbackBinding = typeof(HistoryManager).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(method => method.Name.StartsWith("<PlayBigCardCounteredEffects>b__", StringComparison.Ordinal)
                    && method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(Spell), callbackArgs[1].ParameterType, typeof(object) }))
                .SingleOrDefault();
            counterSpellBinding = AccessTools.Method(typeof(Actor), "GetSpell");
            if (counterSpellBinding == null || counterSpellBinding.IsStatic || counterSpellBinding.ReturnType != typeof(Spell)
                || counterSpellBinding.GetParameters().Length != 1 || !counterSpellBinding.GetParameters()[0].ParameterType.IsEnum) return;
            if (counterRemoveBinding == null || start == null || start.IsStatic || start.ReturnType != typeof(void)
                || counterCallbackBinding == null || counterCallbackBinding.IsStatic || counterCallbackBinding.ReturnType != typeof(void)
                || counterCardBinding == null || counterCardBinding.IsStatic || counterCardBinding.IsInitOnly
                || counterCardBinding.FieldType != typeof(HistoryCard)) return;
            harmony.Patch(start, prefix: new HarmonyMethod(typeof(NativeBigCardWatchdog), nameof(BeforeCounterEffects)));
        }
        catch (Exception) { counterCardBinding = null; counterCallbackBinding = null; }
    }

    private static void BeforeCounterEffects(HistoryManager __instance)
    {
        try
        {
            counterDisplay = null;
            var card = __instance.GetCurrentBigCard();
            var actor = card?.m_mainCardActor;
            if (actor == null || counterSpellBinding == null) return;
            // Pinned 0x06005942 IL_0013-0019 requests SpellType value 5.
            var spell = counterSpellBinding.Invoke(actor,
                new[] { Enum.ToObject(counterSpellBinding.GetParameters()[0].ParameterType, 5) }) as Spell;
            if (ReferenceEquals(spell, null) || counterCardBinding == null || counterCallbackBinding == null || counterRemoveBinding == null) return;
            // 0x06005942 IL_002f-0049 registers this callback with the card, then drops the current-card field.
            counterDisplay = new CounterDisplay { History = __instance, Card = card, Spell = spell,
                Callback = Delegate.CreateDelegate(
                    counterRemoveBinding.GetParameters()[0].ParameterType, __instance, counterCallbackBinding) };
        }
        catch (Exception) { counterDisplay = null; }
    }

    private static bool ChoicesWaitingBehindHistory(ChoiceCardMgr choices, GameState front)
    {
        bool found = false;
        foreach (int playerId in new[] { front.GetFriendlyPlayerId(), front.GetOpposingPlayerId() })
        {
            var state = choices.GetChoiceStateForPlayer(playerId);
            if (state == null) continue;
            found = true;
            // 0x060052DC IL_0090-0102 registers before IL_0250-0266 waits for the preceding task.
            if (!state.m_waitingToStart || state.m_hasBeenRevealed || state.m_preTaskList == null
                || !front.GetPowerProcessor().HasTaskList(state.m_preTaskList)) return false;
        }
        return found;
    }

    private static Dictionary<string, MethodInfo> ResolveBindings()
    {
        try
        {
            var result = new Dictionary<string, MethodInfo>();
            foreach (var row in ReflectionBindings)
            {
                if (row[0] != "Method") continue;
                var method = AccessTools.Method(typeof(HistoryManager), row[2], Type.EmptyTypes);
                if (method == null || method.IsStatic || method.ReturnType != typeof(void)) return null;
                result.Add(row[2], method);
            }
            return result;
        }
        catch (Exception) { return null; }
    }

    private static Dictionary<string, FieldInfo> ResolveAdmissionBindings()
    {
        try
        {
            var result = new Dictionary<string, FieldInfo>();
            foreach (var row in ReflectionBindings)
            {
                if (row[0] != "Field") continue;
                var type = row[1] == "HistoryManager" ? typeof(HistoryManager)
                    : typeof(HistoryManager).GetNestedType("BigCardEntry", BindingFlags.Public | BindingFlags.NonPublic);
                if (type == null) return null;
                var field = AccessTools.Field(type, row[2]);
                if (field == null || field.IsStatic || field.IsInitOnly) return null;
                result.Add(row[2], field);
            }
            if (result["m_pendingBigCardEntry"].FieldType != result["m_finishedCallback"].DeclaringType
                || result["m_finishedCallback"].FieldType != typeof(HistoryManager.BigCardFinishedCallback)
                || result["m_showingBigCard"].FieldType != typeof(bool)) return null;
            return result;
        }
        catch (Exception) { return null; }
    }

    internal static bool ShouldRelease(double elapsed, int queueCount, bool currentTask, bool pendingChoice, double normalDisplaySeconds = 4)
    {
        return !double.IsNaN(elapsed) && !double.IsInfinity(elapsed)
            && !double.IsNaN(normalDisplaySeconds) && !double.IsInfinity(normalDisplaySeconds)
            && elapsed > Math.Max(TimeoutSeconds, normalDisplaySeconds + DisplayMarginSeconds)
            && queueCount > 0 && !currentTask && !pendingChoice;
    }

    internal static void Update(bool ownsMatch, bool engineChoicePending, string matchId, ManualLogSource log)
    {
        try
        {
            var history = ownsMatch ? HistoryManager.Get() : null;
            var card = history?.GetCurrentBigCard();
            if (history == null || !history.IsShowingBigCard() || !ReferenceEquals(counterDisplay?.History, history))
                counterDisplay = null;
            var counter = card == null && counterDisplay?.Card != null ? counterDisplay : null;
            var front = ownsMatch ? GameState.Get() : null;
            var processor = front?.GetPowerProcessor();
            var input = ownsMatch ? InputManager.Get() : null;
            var choices = ownsMatch ? ChoiceCardMgr.Get() : null;
            var scene = ownsMatch ? SceneMgr.Get() : null;
            // Missing native state/bindings cannot authorize mutation; paused/slow displays start a fresh clock.
            if (bindings == null || !bindings.ContainsKey("RunFinishedCallbackAndDestroyBigCard")
                || !bindings.ContainsKey("CutoffBigCardTransformEffects") || history == null
                || !history.IsShowingBigCard() || front?.GetGameEntity() == null || processor == null || input == null
                || choices == null || scene == null || scene.IsTransitioning() || Time.timeScale < 1f)
            { Reset(); return; }

            object admission = null;
            HistoryManager.BigCardFinishedCallback admissionFinished = null;
            if (card == null && counter == null)
            {
                if (admissionBindings == null || !admissionBindings.ContainsKey("m_pendingBigCardEntry")
                    || !admissionBindings.ContainsKey("m_finishedCallback") || !admissionBindings.ContainsKey("m_showingBigCard"))
                { Reset(); return; }
                admission = admissionBindings["m_pendingBigCardEntry"].GetValue(history);
                if (admission != null)
                    admissionFinished = admissionBindings["m_finishedCallback"].GetValue(admission) as HistoryManager.BigCardFinishedCallback;
                // Pending entry exists only during admission, before the started callback and prefab load.
                if (admissionFinished == null) { Reset(); return; }
            }
            card = card ?? counter?.Card;
            double now = Time.realtimeSinceStartup;
            if (!ReferenceEquals(history, observedHistory) || !ReferenceEquals(card, observedCard)
                || !ReferenceEquals(admission, observedAdmission) || !ReferenceEquals(counter, observedCounter)
                || observedMatch != matchId || now < since)
            {
                observedHistory = history; observedCard = card; observedAdmission = admission; observedMatch = matchId;
                observedCounter = counter;
                since = now; normalSeconds = 4; attempted = false;
            }
            normalSeconds = Math.Max(normalSeconds, Math.Max((card == null ? 0 : card.GetDisplayTimeMS()) / 1000d,
                4 + Math.Max(0, front.GetGameEntity().GetAdditionalTimeToWaitForSpells())));
            var mode = front.GetResponseMode();
            bool waitingChoice = ChoicesWaitingBehindHistory(choices, front);
            bool pendingChoice = !waitingChoice && (engineChoicePending || front.MustWaitForChoices() || choices.HasChoices()
                    || mode == GameState.ResponseMode.CHOICE)
                || front.IsMulliganBusy() || front.IsMulliganManagerActive() || front.IsInSubOptionMode()
                || mode == GameState.ResponseMode.OPTION_TARGET
                || mode == GameState.ResponseMode.OPTION_REVERSE_TARGET || mode == GameState.ResponseMode.SUB_OPTION
                || front.GetFriendlyEntityChoices()?.ChoiceType == CHOICE_TYPE.TARGET
                || input.GetHeldCard() != null || input.GetBattlecrySourceCard() != null
                || choices.GetSubOptionParentCard() != null;
            // Native can begin/resume the display after an answer; time spent choosing is never a stalled display.
            if (pendingChoice) { since = now; return; }
            int queueCount = processor.GetPowerQueue()?.Count ?? -1;
            double elapsed = now - since;
            // CanProcessPowerQueue includes IsHistoryBlocking: requiring it would disable the safety net itself.
            if (attempted || front.IsBusy()
                || !ShouldRelease(elapsed, queueCount, processor.GetCurrentTaskList() != null, pendingChoice, normalSeconds)) return;

            var entity = (card == null ? null : card.GetEntity()) ?? history.GetPendingBigCardEntity();
            string cardId = entity?.GetCardId() ?? "unknown";
            int entityId = entity?.GetEntityId() ?? 0;
            attempted = true;
            if (card == null)
            {
                // 0x06005933 IL_008f-009b starts by name; 0x06005997 IL_003d-0061 waits with the entry intact.
                // 0x06005941 IL_0000-000e cannot finish a null card. Use the native skip callback (0x06005933
                // IL_0011-0017), then its normal completion's showing-state clear (0x06005941 IL_001a-001c).
                history.StopCoroutine("WaitForCardLoadedAndCreateBigCard");
                admissionFinished();
                admissionBindings["m_pendingBigCardEntry"].SetValue(history, null);
                admissionBindings["m_showingBigCard"].SetValue(history, false);
            }
            else
            {
                if (counter != null)
                {
                    // Cancel the exact listener before restoring its card for native terminal cleanup. HistoryCard's
                    // 0x060058BC IL_0000-0023 makes the already-run power-processor callback idempotent.
                    if (!(bool)counterRemoveBinding.Invoke(counter.Spell, new object[] { counter.Callback, counter.Card })) return;
                    counterCardBinding.SetValue(history, counter.Card);
                    counterDisplay = null;
                }
                // Cancel old named timers so a late coroutine cannot finish the next card. Use the timer's terminal
                // completion, not DestroyBigCard's transform/counter animations, which can themselves be the stalled effect.
                history.StopCoroutine("WaitThenShowBigCard");
                history.StopCoroutine("WaitThenDestroyBigCard");
                try
                {
                    bindings[card.HasBigCardPostTransformedEntity() ? "CutoffBigCardTransformEffects" : "RunFinishedCallbackAndDestroyBigCard"]
                        .Invoke(history, null);
                }
                catch (Exception) { if (history.IsShowingBigCard()) return; } // Cleanup may fail after the native release.
            }
            if (history.IsShowingBigCard()) return;
            LastRelease = new JsonNode { { "matchId", matchId }, { "entityId", entityId }, { "cardId", cardId },
                { "elapsedSeconds", elapsed }, { "queueCount", queueCount }, { "utc", DateTime.UtcNow.ToString("o") } };
            log?.LogWarning("BIGCARD_WATCHDOG released " + entityId + " " + cardId + " after "
                + elapsed.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "s queue=" + queueCount);
        }
        catch (Exception) { /* A failed probe/completion must never break the frame or retry the same occurrence. */ }
    }

    private static void Reset()
    {
        observedHistory = null; observedCard = null; observedAdmission = null; observedMatch = null;
        observedCounter = null;
        since = normalSeconds = 0; attempted = false;
    }
}
