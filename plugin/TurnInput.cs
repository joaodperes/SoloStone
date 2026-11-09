using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Networking;

// Whether the client is ready for the human's next input. Real turn timing reads this (ClientBridge.NativeHumanInputReady and
// NativeHumanTimerReady), so it ships in every build; the test-control endpoint (NativeTestControl) only forwards to it.
internal static class TurnInput
{
    // Fixed, read-only private API adapters. Fail closed when this pinned client changes.
    internal static readonly MethodInfo BlockingPower = AccessTools.Method(typeof(GameState), "IsBlockingPowerProcessor", Type.EmptyTypes);
    internal static readonly FieldInfo DelayedTasks = AccessTools.Field(typeof(PowerProcessor), "m_delayedRealTimeTasks");

    internal static bool Ready(NativeMatch match, bool forTimer = false)
    {
        var front = GameState.Get(); var scene = SceneMgr.Get(); var input = InputManager.Get();
        var processor = front?.GetPowerProcessor(); var zone = ZoneMgr.Get();
        var dispatcher = Network.Get()?.QueueDispatcher as QueueDispatcher;
        if (match?.Game == null || scene == null || scene.GetMode() != SceneMgr.Mode.GAMEPLAY || scene.IsTransitioning()
            || front == null
            || front.GetGameEntity()?.GetTag(GAME_TAG.TURN) != match.Game.Turn
            || front.GetGameEntity()?.GetTag(GAME_TAG.STEP) != (int)SabberStoneCore.Enums.Step.MAIN_ACTION
            || front.GetFriendlySidePlayer()?.GetTag(GAME_TAG.CURRENT_PLAYER) != (match.Game.CurrentPlayer == match.Game.Player1 ? 1 : 0)) return false;
        // Matching playable packets latch the clock; held targeting/sub-options and their local queues cannot delay it.
        if (forTimer)
            return !front.IsResponsePacketBlocked() && (match.HumanOptionsId >= 0 && front.GetOptionsPacket()?.ID == match.HumanOptionsId
                || match.HumanChoiceId >= 0 && front.GetFriendlyEntityChoices()?.ID == match.HumanChoiceId
                    && front.GetFriendlyEntityChoices().ChoiceType != CHOICE_TYPE.MULLIGAN);
        if (front.IsBusy() || front.IsMulliganBusy() || front.IsMulliganManagerActive()
            || input == null || !input.IsInputEnabled() || !input.PermitDecisionMakingInput() || front.IsResponsePacketBlocked()
            || dispatcher == null || dispatcher.HasGamePackets() || processor == null || processor.GetCurrentTaskList() != null
            || DelayedTasks == null || !(DelayedTasks.GetValue(processor) is ICollection delayed) || delayed.Count != 0
            || zone == null || zone.HasPendingLocalChange()) return false;
        if (match.HumanChoiceId >= 0)
        {
            // An actionable choice is a queue barrier: its continuation cannot drain before the answer.
            var choices = front.GetFriendlyEntityChoices(); var manager = ChoiceCardMgr.Get();
            return choices?.ID == match.HumanChoiceId && choices.ChoiceType != CHOICE_TYPE.MULLIGAN
                && front.GetResponseMode() == GameState.ResponseMode.CHOICE && manager != null
                && (choices.ChoiceType == CHOICE_TYPE.TARGET || !manager.IsFriendlyWaitingToStartChoices())
                && !processor.HasTaskList(manager.GetFriendlyPreChoiceTaskList());
        }
        return match.HumanOptionsId >= 0 && front.GetOptionsPacket()?.ID == match.HumanOptionsId
            && front.GetResponseMode() == GameState.ResponseMode.OPTION && !zone.HasActiveLocalChange()
            && processor.GetPowerQueue()?.Count == 0 && !processor.HasTaskLists() && !processor.IsHistoryBlocking()
            && BlockingPower != null && !(bool)BlockingPower.Invoke(front, null);
    }
}
