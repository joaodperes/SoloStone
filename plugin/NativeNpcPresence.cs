using System;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

// Native presence of the local (NPC) opponent, live 2026-10-25:
// - Emotes. GameEntity.NotifyOfMulliganInitialized loads the EnemyEmoteHandler (the native squelch: right-click the
//   opponent hero) when !GameMgr.IsAI() || matchmade, and CAN_SQUELCH_OPPONENT. GameMgr.IsAI() is
//   GameUtils.IsAIMission(missionId): a staged local match has mission 0 (no Scenario record), so IsAI() is false and
//   the native code itself requests the handler, exactly as for ranked. Round 2 logged no npc-presence line because the
//   loader returned silently on that branch. Now every mulligan logs the guard values and what was done; if the native
//   path did not request it (e.g. a future AI mission id) the loader requests it; and if the handler still does not exist
//   later (first opponent emote, a squelch command) one more request is made. While no handler exists, the receive check
//   validates the emote type itself, as the retail path does once the handler exists.
// - Name plate. Player.UpdateName gives an AI opponent of a non-matchmade game its hero's name; a staged local match
//   shows the opponent's name (NPC DisplayName) instead, plus the rivals-lite head-to-head record (NativeMatch.OpponentPlateName). The match intro's hero labels stay hero names, as in ranked.
// Every hook is optional: a missing member logs one warning and leaves the native behaviour unchanged.
internal static class NativeNpcPresence
{
    private const string EnemyEmoteHandlerPrefab = "EnemyEmoteHandler.prefab:6ace3edd8826cad4aaa0d0e0eb085012";
    private const float RetryAfterSeconds = 10f;
    private const int MaxRequests = 2;
    private static ManualLogSource log;
    private static Func<NativeMatch> activeMatch;
    private static readonly FieldInfo PlayerName = AccessTools.Field(typeof(Player), "m_name");
    private static readonly FieldInfo EnemyEmote1 = AccessTools.Field(typeof(EnemyEmoteHandler), "m_emote1");
    private static readonly MethodInfo DoSquelchClick = AccessTools.Method(typeof(EnemyEmoteHandler), "DoSquelchClick");
    private static readonly FieldInfo AvailableEmotes = AccessTools.Field(typeof(EmoteHandler), "m_availableEmotes");
    private static readonly FieldInfo CurrentEmoteType = AccessTools.Field(typeof(EmoteOption), "m_currentEmoteType");
    // Per game entity (one match): requests made (native or ours), when, and whether presence was reported.
    private static GameEntity tracked;
    private static bool nativeRequested, reported;
    private static int requests;
    private static float requestedAt = -1;
    private static string lastRequest = "none";

    internal static void Install(Harmony harmony, ManualLogSource logger, Func<NativeMatch> active)
    {
        log = logger; activeMatch = active;
        TryPatch(harmony, AccessTools.Method(typeof(GameEntity), "NotifyOfMulliganInitialized"), nameof(LoadEnemyEmoteHandler), postfix: true);
        TryPatch(harmony, AccessTools.Method(typeof(RemoteActionHandler), "CanReceiveEnemyEmote"), nameof(CanReceiveEnemyEmote), postfix: false);
        TryPatch(harmony, AccessTools.Method(typeof(Player), "UpdateName"), nameof(UpdateOpponentName), postfix: true);
        if (PlayerName == null) log?.LogWarning("npc-presence: Player.m_name missing; native opponent name unavailable");
        if (EnemyEmote1 == null || DoSquelchClick == null) log?.LogWarning("npc-presence: EnemyEmoteHandler squelch members missing; test-control squelch unavailable");
        if (AvailableEmotes == null || CurrentEmoteType == null) log?.LogWarning("npc-presence: EmoteHandler option members missing; test-control emote unavailable");
        log?.LogInfo("npc-presence: hooks installed");
    }

    private static void TryPatch(Harmony harmony, MethodInfo original, string hook, bool postfix)
    {
        try
        {
            if (original == null) throw new MissingMethodException(hook);
            var method = new HarmonyMethod(typeof(NativeNpcPresence), hook);
            if (postfix) harmony.Patch(original, postfix: method); else harmony.Patch(original, prefix: method);
        }
        catch (Exception error) { log?.LogWarning("npc-presence: hook " + hook + " unavailable: " + error.Message); }
    }

    private static bool Local => activeMatch?.Invoke()?.Game != null;

    // Squelch state of the NPC (native right-click on the opponent hero); false while no handler exists.
    internal static bool NpcSquelched()
    {
        var handler = EnemyEmoteHandler.Get();
        var state = GameState.Get();
        return handler != null && state != null && handler.IsSquelched(state.GetOpposingPlayerId());
    }

    private static void Track(GameEntity entity)
    {
        if (ReferenceEquals(entity, tracked)) return;
        tracked = entity; nativeRequested = reported = false; requests = 0; requestedAt = -1; lastRequest = "none";
    }

    // Mirrors the native condition in GameEntity.NotifyOfMulliganInitialized (decompiled Assembly-CSharp).
    private static void LoadEnemyEmoteHandler(GameEntity __instance)
    {
        try
        {
            var manager = GameMgr.Get();
            if (manager == null) { log?.LogInfo("npc-presence: mulligan skip=no-game-mgr"); return; }
            bool local = Local, handler = EnemyEmoteHandler.Get() != null;
            bool tutorial = manager.IsTraditionalTutorial(), battlegrounds = manager.IsBattlegroundsMatchOrTutorial();
            bool ai = manager.IsAI(), matchmade = GameUtils.IsMatchmadeGameType(manager.GetGameType());
            bool canSquelch = __instance.GetGameOptions().GetBooleanOption(GameEntityOption.CAN_SQUELCH_OPPONENT);
            bool native = !tutorial && (!ai || matchmade) && canSquelch;
            string action;
            if (!local) action = "skip=not-local";
            else
            {
                Track(__instance);
                if (native) { nativeRequested = true; requestedAt = Time.realtimeSinceStartup; lastRequest = "native"; }
                if (handler) action = "skip=handler-exists";
                else if (tutorial || battlegrounds) action = "skip=tutorial-or-battlegrounds";
                else if (!canSquelch) action = "skip=can-squelch-false";
                else if (native) action = "native-request";
                else action = Request(__instance, "mulligan");
            }
            log?.LogInfo("npc-presence: mulligan local=" + local + " handler=" + handler + " ai=" + ai + " gameType=" + manager.GetGameType()
                + " mission=" + manager.GetMissionId() + " matchmade=" + matchmade + " canSquelch=" + canSquelch + " " + action);
        }
        catch (Exception error) { log?.LogWarning("npc-presence: enemy emote handler check failed: " + error.GetType().Name + ": " + error.Message); }
    }

    // Requests the handler again when none exists in a local match: never before the native request had time to load,
    // at most MaxRequests per match. Returns the outcome for logs and test-control replies.
    internal static string EnsureEnemyEmoteHandler(string reason)
    {
        try
        {
            if (!Local) return "not-local";
            if (EnemyEmoteHandler.Get() != null) return "present";
            var entity = GameState.Get()?.GetGameEntity();
            if (entity == null) return "no-game-entity";
            Track(entity);
            if (requestedAt >= 0 && Time.realtimeSinceStartup - requestedAt < RetryAfterSeconds) return "loading:" + lastRequest;
            if (requests >= MaxRequests) return "gave-up";
            string outcome = Request(entity, reason);
            log?.LogInfo("npc-presence: enemy emote handler missing after " + lastRequest + " request; " + outcome);
            return outcome;
        }
        catch (Exception error)
        {
            log?.LogWarning("npc-presence: enemy emote handler ensure failed: " + error.GetType().Name + ": " + error.Message);
            return "error";
        }
    }

    private static string Request(GameEntity entity, string reason)
    {
        try
        {
            requests++; requestedAt = Time.realtimeSinceStartup; lastRequest = reason;
            object loader = typeof(AssetLoader).GetMethod("Get", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null)
                ?? throw new InvalidOperationException("AssetLoader unavailable");
            MethodInfo instantiate = loader.GetType().GetMethods().Single(m => m.Name == "InstantiatePrefab" && m.GetParameters().Length == 4
                && m.GetParameters()[2].ParameterType == typeof(object));
            ParameterInfo[] parameters = instantiate.GetParameters();
            MethodInfo conversion = parameters[0].ParameterType.GetMethod("op_Implicit", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(string) }, null) ?? throw new MissingMethodException(parameters[0].ParameterType.FullName, "op_Implicit");
            object reference = conversion.Invoke(null, new object[] { EnemyEmoteHandlerPrefab });
            // The native placement callback (opposing hero zone), bound to this game entity.
            MethodInfo placed = AccessTools.Method(typeof(GameEntity), "EnemyEmoteHandlerDoneLoadingCallback")
                ?? throw new MissingMethodException(typeof(GameEntity).FullName, "EnemyEmoteHandlerDoneLoadingCallback");
            Delegate callback = Delegate.CreateDelegate(parameters[1].ParameterType, entity, placed);
            object options = Enum.ToObject(parameters[3].ParameterType, 2);
            object accepted = instantiate.Invoke(loader, new[] { reference, callback, null, options });
            return "requested=" + accepted + " reason=" + reason;
        }
        catch (Exception error)
        {
            log?.LogWarning("npc-presence: enemy emote handler not loaded: " + error.GetType().Name + ": " + error.Message);
            return "request-failed";
        }
    }

    // Before the handler has loaded (or when it could not be), a local match accepts a valid opponent emote type.
    private static bool CanReceiveEnemyEmote(EmoteType __0, ref bool __result)
    {
        try
        {
            if (!Local) return true;
            bool present = EnemyEmoteHandler.Get() != null;
            if (!reported)
            {
                reported = true;
                log?.LogInfo("npc-presence: first opponent emote " + __0 + " enemyEmoteHandler=" + present
                    + (present ? "" : " " + EnsureEnemyEmoteHandler("first-emote")));
            }
            if (present) return true;
            var emotes = EmoteHandler.Get();
            __result = emotes != null && emotes.IsValidEmoteTypeForOpponent(__0);
            return false;
        }
        catch (Exception) { return true; }
    }

    private static void UpdateOpponentName(Player __instance)
    {
        try
        {
            var match = activeMatch?.Invoke();
            // Rivals-lite: the plate carries the head-to-head record (OpponentPlateName); the engine name stays the NPC name.
            string name = match?.Game != null ? match.OpponentPlateName ?? match.OpponentName : null;
            if (string.IsNullOrEmpty(name) || PlayerName == null || __instance.GetSide() != Player.Side.OPPOSING) return;
            PlayerName.SetValue(__instance, name);
        }
        catch (Exception) { }
    }

    // Test control: set the native squelch of the opponent through the handler's own click path (toggle only when the
    // state differs). Throws with the reason when the native squelch is unavailable.
    internal static bool SetOpponentSquelch(bool squelched)
    {
        if (!Local) throw new InvalidOperationException("No local NPC match");
        var handler = EnemyEmoteHandler.Get();
        var state = GameState.Get();
        if (handler == null)
            throw new InvalidOperationException("EnemyEmoteHandler not loaded (" + EnsureEnemyEmoteHandler("squelch-command") + ")");
        if (state == null) throw new InvalidOperationException("No game state");
        if (EnemyEmote1 == null || DoSquelchClick == null) throw new InvalidOperationException("Native squelch members missing");
        bool before = handler.IsSquelched(state.GetOpposingPlayerId());
        if (before != squelched)
        {
            object emote = EnemyEmote1.GetValue(handler) ?? throw new InvalidOperationException("Native squelch option missing");
            DoSquelchClick.Invoke(handler, new[] { emote });
        }
        bool after = handler.IsSquelched(state.GetOpposingPlayerId());
        log?.LogInfo("npc-presence: squelch requested=" + squelched + " before=" + before + " after=" + after);
        if (after != squelched) throw new InvalidOperationException("Native squelch did not change");
        return before != after;
    }

#if !SOLOSTONE_RELEASE
    // Test control: the human plays one emote through the native emote option (speech bubble + Network.SendEmote).
    internal static void PlayHumanEmote(EmoteType type)
    {
        if (!Local) throw new InvalidOperationException("No local NPC match");
        var emotes = EmoteHandler.Get() ?? throw new InvalidOperationException("EmoteHandler not loaded");
        if (AvailableEmotes == null || CurrentEmoteType == null) throw new InvalidOperationException("Native emote members missing");
        if (emotes.EmoteSpamBlocked()) throw new InvalidOperationException("Emote spam-blocked");
        var options = AvailableEmotes.GetValue(emotes) as System.Collections.IEnumerable
            ?? throw new InvalidOperationException("No available emotes");
        var option = options.OfType<EmoteOption>().FirstOrDefault(o => o != null && (EmoteType)CurrentEmoteType.GetValue(o) == type)
            ?? throw new InvalidOperationException("Emote not available: " + type);
        option.DoClick();
        log?.LogInfo("npc-presence: human emote " + type);
    }
#endif

    internal static void PlayNpcEmote(EmoteType type)
    {
        var match = activeMatch?.Invoke();
        if (match?.Game == null || match.Game.State == SabberStoneCore.Enums.State.COMPLETE)
            throw new InvalidOperationException("No active local NPC match");
        string name;
        switch (type)
        {
            case EmoteType.GREETINGS: name = "Greetings"; break;
            case EmoteType.WELL_PLAYED: name = "WellPlayed"; break;
            case EmoteType.THANKS: name = "Thanks"; break;
            case EmoteType.WOW: name = "Wow"; break;
            case EmoteType.OOPS: name = "Oops"; break;
            case EmoteType.THREATEN: name = "Threaten"; break;
            default: throw new ArgumentException("Unsupported NPC emote");
        }
        // The same UserUI dispatch as the NPC scheduler, preserving native squelch/settings and spell selection.
        if (!match.SendNpcPresentation(new NpcPresentationEvent(match.Game.Player2.PlayerId, name)))
            throw new InvalidOperationException("NPC emote suppressed or native dispatch unavailable");
        log?.LogInfo("npc-presence: test NPC emote " + type);
    }

    internal static string Status => "enemyEmoteHandler=" + (EnemyEmoteHandler.Get() != null) + " nativeRequested=" + nativeRequested
        + " requests=" + requests + " last=" + lastRequest;
}
