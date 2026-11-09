using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SabberStoneCore.Kettle;
using SabberStoneCore.Model.Entities;
using SabberStoneCore.Tasks;
using SabberStoneCore.Tasks.SimpleTasks;
using EngineGame = SabberStoneCore.Model.Game;

internal static class CopyTransportBridge
{
    internal sealed class CopyTaskState
    {
        internal NativeMatch Match;
        internal EngineGame Game;
        internal List<IPowerHistoryEntry> Entries;
        internal Controller Controller;
        internal HashSet<int> BoardIds;
        internal int DestinationId;
        internal string CopiedCardId;
        internal bool Summon;
        internal bool CapturesInternalCopy;
    }

    [ThreadStatic] private static NativeMatch activeMatch;
    [ThreadStatic] private static EngineGame activeGame;

    internal static void Enter(NativeMatch match, EngineGame game)
    {
        if (match == null) throw new ArgumentNullException(nameof(match));
        if (game == null) throw new ArgumentNullException(nameof(game));
        if (activeMatch != null || activeGame != null)
            throw new InvalidOperationException("Copy transport scope is already active.");
        activeMatch = match;
        activeGame = game;
    }

    internal static void Exit()
    {
        activeMatch = null;
        activeGame = null;
    }

    internal static NativeMatch ActiveMatch(EngineGame game) =>
        activeMatch != null && ReferenceEquals(activeGame, game) ? activeMatch : null;

    internal static MethodInfo RequiredProcess(Type taskType)
    {
        if (taskType != typeof(SummonCopyTask) && taskType != typeof(TransformCopyTask))
            throw new ArgumentException("Unsupported copy-task type.", nameof(taskType));
        Type[] signature = {
            typeof(EngineGame).MakeByRefType(), typeof(Controller).MakeByRefType(),
            typeof(IEntity).MakeByRefType(), typeof(IPlayable).MakeByRefType(),
            typeof(SabberStoneCore.Model.TaskStack).MakeByRefType()
        };
        MethodInfo process = taskType.GetMethod("Process", BindingFlags.Instance | BindingFlags.Public,
            null, signature, null);
        if (process == null || process.ReturnType != typeof(TaskState))
            throw new MissingMethodException(taskType.FullName, "Process(pinned five-byref signature)");
        return process;
    }

    internal static void BeforeSummonCopy(ref EngineGame game, ref Controller controller,
        ref IEntity source, ref IPlayable target, out CopyTaskState __state)
    {
        __state = Begin(game, controller, source, true);
    }

    internal static void AfterSummonCopy(TaskState __result, CopyTaskState __state)
    {
        Complete(__result, __state);
    }

    internal static void BeforeTransformCopy(ref EngineGame game, ref Controller controller,
        ref IEntity source, ref IPlayable target, out CopyTaskState __state)
    {
        __state = Begin(game, controller, source, false);
    }

    internal static void AfterTransformCopy(TaskState __result, CopyTaskState __state)
    {
        Complete(__result, __state);
    }

    private static CopyTaskState Begin(EngineGame game, Controller controller, IEntity source, bool summon)
    {
        if (activeMatch == null || !ReferenceEquals(activeGame, game) || !game.History
            || controller == null || !(source is IPlayable sourcePlayable))
            return null;
        string expectedCard = summon ? "EX1_294" : "EX1_564";
        if (!string.Equals(sourcePlayable.Card?.Id, expectedCard, StringComparison.Ordinal))
            return null;
        IPlayable copiedSource = null;
        if (summon)
        {
            PropertyInfo currentEvent = typeof(EngineGame).GetProperty("CurrentEventData",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object eventData = currentEvent?.GetValue(game, null);
            copiedSource = eventData?.GetType().GetProperty("EventSource",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(eventData, null) as IPlayable;
        }
        bool capturesInternalCopy = !summon
            || copiedSource is Minion && copiedSource.Zone?.Type == SabberStoneCore.Enums.Zone.PLAY;
        if (capturesInternalCopy && game.PowerHistory?.Last == null)
            throw new InvalidOperationException("Copy task has no active power-history batch.");
        return new CopyTaskState {
            Match = activeMatch,
            Game = game,
            Entries = capturesInternalCopy ? game.PowerHistory.Last : null,
            Controller = controller,
            BoardIds = summon && capturesInternalCopy
                ? new HashSet<int>(controller.BoardZone.Select(minion => minion.Id)) : null,
            DestinationId = summon ? 0 : source.Id,
            CopiedCardId = copiedSource?.Card?.Id,
            Summon = summon,
            CapturesInternalCopy = capturesInternalCopy
        };
    }

    private static void Complete(TaskState result, CopyTaskState state)
    {
        if (state == null || result != TaskState.COMPLETE || !state.CapturesInternalCopy)
            return;
        if (!ReferenceEquals(activeMatch, state.Match) || !ReferenceEquals(activeGame, state.Game))
            throw new InvalidOperationException("Copy task completed outside its native-match scope.");
        if (!ReferenceEquals(state.Game.PowerHistory.Last, state.Entries))
            throw new InvalidOperationException("Copy task changed its power-history batch.");

        IPlayable destination;
        if (state.Summon)
        {
            IPlayable[] created = state.Controller.BoardZone.Where(entity =>
                !state.BoardIds.Contains(entity.Id)
                && string.Equals(entity.Card?.Id, state.CopiedCardId, StringComparison.Ordinal)).Cast<IPlayable>().ToArray();
            if (created.Length != 1)
                throw new InvalidOperationException("SummonCopyTask completion did not produce one unambiguous board entity.");
            destination = created[0];
        }
        else if (!state.Game.IdEntityDic.TryGetValue(state.DestinationId, out destination)
            || destination.Zone?.Type != SabberStoneCore.Enums.Zone.PLAY || !(destination is Minion))
        {
            throw new InvalidOperationException("TransformCopyTask completion destination is unavailable.");
        }

        state.Match.RecordCopyCompletion(state.Entries, state.Entries.Count, destination);
    }
}
