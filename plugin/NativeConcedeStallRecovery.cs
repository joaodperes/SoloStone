using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using MiniJSON;
using UnityEngine;

internal static class NativeConcedeStallRecovery
{
    // Native early-concede is behind CanProcessPowerQueue, which rejects a stuck current task list.
    private static readonly FieldInfo BusyTaskList = AccessTools.Field(typeof(PowerProcessor), "m_busyTaskList");
    private static readonly FieldInfo CurrentTaskList = AccessTools.Field(typeof(PowerProcessor), "m_currentTaskList");
    private static readonly MethodInfo CanProcessQueue = AccessTools.Method(typeof(GameState), "CanProcessPowerQueue", Type.EmptyTypes);
    private static readonly MethodInfo EndCurrentTaskList = AccessTools.Method(typeof(PowerProcessor), "EndCurrentTaskList", Type.EmptyTypes);
    private static readonly StallDecision decision = new StallDecision();
    internal static JsonNode LastRecovery { get; private set; }

    internal sealed class StallDecision
    {
        private string match, attemptedMatch;
        private object processor, busy, current, head;
        private int unfinished;
        private bool canProcess;
        private double since;
        internal double Elapsed(double now) => now - since;

        internal bool Observe(bool terminal, string matchId, object nextProcessor, object nextBusy,
            object nextCurrent, object nextHead, int nextUnfinished, bool nextCanProcess, double now, bool conceded = true)
        {
            if (!terminal || string.IsNullOrEmpty(matchId) || nextProcessor == null
                || nextBusy == null && nextCurrent == null && nextHead == null
                || double.IsNaN(now) || double.IsInfinity(now))
            { processor = null; return false; }
            if (match != matchId || !ReferenceEquals(processor, nextProcessor) || !ReferenceEquals(busy, nextBusy)
                || !ReferenceEquals(current, nextCurrent) || !ReferenceEquals(head, nextHead)
                || unfinished != nextUnfinished || canProcess != nextCanProcess || now < since)
            {
                match = matchId; processor = nextProcessor; busy = nextBusy; current = nextCurrent; head = nextHead;
                unfinished = nextUnfinished; canProcess = nextCanProcess; since = now;
                return false;
            }
            // A concede is waiting on the player; a natural game end may still be playing one long lethal animation.
            if (now - since < (conceded ? 5 : 15) || attemptedMatch == matchId) return false;
            attemptedMatch = matchId; // Claim before native callbacks: a failure or reentrant frame cannot replay game over.
            return true;
        }
    }

    private static int Unfinished(PowerTaskList list)
    {
        if (list == null) return 0;
        int count = list.IsComplete() ? 0 : 1; // Includes zone changes after all tasks have completed.
        foreach (var task in list.GetTaskList()) if (!task.IsCompleted()) count++;
        return count;
    }

    private static string Describe(PowerTaskList list)
    {
        if (list == null) return "none";
        return list.GetId() + ":" + list.GetBlockStart()?.BlockType + ":" + list.GetSourceEntity(false)?.GetCardId()
            + ":unfinished=" + Unfinished(list);
    }

    private static void Complete(PowerTaskList list, JsonNode record)
    {
        if (list == null) return;
        // The native DoEarlyConcedeTasks loop, isolating each task so a broken old effect cannot hide terminal tags.
        foreach (var task in list.GetTaskList())
            try { task.DoEarlyConcedeTask(); }
            catch (Exception error) { record["error"] = error.Message; }
    }

    internal static void Update(bool ownsMatch, bool engineComplete, bool humanConceded, string matchId, ManualLogSource log)
    {
        try
        {
            var front = ownsMatch ? GameState.Get() : null;
            var processor = front?.GetPowerProcessor();
            if (BusyTaskList == null || CurrentTaskList == null || CanProcessQueue == null || EndCurrentTaskList == null || front == null || processor == null
                || !engineComplete || front.IsGameOver())
            { decision.Observe(false, null, null, null, null, null, 0, false, 0); return; }
            var queue = processor.GetPowerQueue();
            var current = processor.GetCurrentTaskList();
            var busy = BusyTaskList.GetValue(processor) as PowerTaskList;
            var head = queue == null || queue.Count == 0 ? null : queue.Peek();
            bool canProcess = (bool)CanProcessQueue.Invoke(front, null);
            double now = Time.realtimeSinceStartup;
            if (!decision.Observe(true, matchId, processor, busy, current, head,
                Unfinished(current) + Unfinished(busy) + Unfinished(head), canProcess, now, humanConceded)) return;

            var record = new JsonNode { { "matchId", matchId }, { "reason", humanConceded ? "human-concede" : "engine-complete" },
                { "busyTaskList", Describe(busy) }, { "current", Describe(current) }, { "head", Describe(head) },
                { "canProcessPowerQueue", canProcess }, { "elapsedSeconds", decision.Elapsed(now) },
                { "queueCount", queue?.Count ?? 0 }, { "utc", DateTime.UtcNow.ToString("o") } };
            LastRecovery = record;
            try
            {
                // Pinned PowerTask.DoEarlyConcedeTask skips completed tasks and applies actual
                // queued tags. PLAYSTATE calls GameState.OnGameOver, including the ordinary EndGameScreen.
                // Keep the queue; clearing it would discard the authoritative game-over tags.
                Complete(current, record);
                if (current != null)
                {
                    try { EndCurrentTaskList.Invoke(processor, null); }
                    catch (Exception error) { record["error"] = (error.InnerException ?? error).Message; }
                    finally { CurrentTaskList.SetValue(processor, null); }
                }
                BusyTaskList.SetValue(processor, null); // This is a reference to Peek(), not a second task list.
                processor.ForceStopHistoryBlocking();
                while (queue != null && queue.Count > 0) Complete(queue.Dequeue(), record);
                front.SetBusy(false);
                record["status"] = front.IsGameOver() ? "recovered" : "no-game-over";
            }
            catch (Exception error) { record["status"] = "failed"; record["error"] = (error.InnerException ?? error).Message; }
            log?.LogWarning("CONCEDE_STALL_RECOVERY " + Json.Serialize(record));
        }
        catch (Exception) { /* Read failures never authorize recovery or break the frame. */ }
    }
}
