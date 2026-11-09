using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SabberStoneCore.Auras;
using SabberStoneCore.Enchants;
using SabberStoneCore.Enums;
using SabberStoneCore.Kettle;
using SabberStoneCore.Model.Entities;
using EngineGame = SabberStoneCore.Model.Game;
using EngineZone = SabberStoneCore.Enums.Zone;

internal static class AuraTransportBridge
{
    internal sealed class RemovalState
    {
        internal NativeMatch Match;
        internal EngineGame Game;
        internal List<IPowerHistoryEntry> Entries;
        internal List<(IPlayable Entity, GameTag[] Tags)> Affected;
    }

    private static readonly FieldInfo EffectsField = typeof(Aura).GetField("Effects",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(Aura).FullName, "Effects");

    internal static MethodInfo RequiredRemoveInternal(System.Type auraType)
    {
        if (auraType != typeof(Aura) && auraType != typeof(SwitchingAura))
            throw new ArgumentException("Unsupported aura type.", nameof(auraType));
        MethodInfo method = auraType.GetMethod("RemoveInternal",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
            null, System.Type.EmptyTypes, null);
        if (method == null || method.ReturnType != typeof(void))
            throw new MissingMethodException(auraType.FullName, "RemoveInternal()");
        _ = EffectsField;
        return method;
    }

    internal static void BeforeRemove(Aura __instance, out RemovalState __state)
    {
        __state = null;
        if (__instance == null || !__instance.Game.History) return;
        NativeMatch match = CopyTransportBridge.ActiveMatch(__instance.Game);
        List<IPowerHistoryEntry> entries = __instance.Game.PowerHistory?.Last;
        if (match == null || entries == null
            || !(__instance is SwitchingAura) && !__instance.Game.Auras.Contains(__instance)
            || __instance.Type == AuraType.CONTROLLER
            || __instance.Type == AuraType.OPPONENT
            || __instance.Type == AuraType.CONTROLLERS)
            return;
        GameTag[] tags = ((IEffect[])EffectsField.GetValue(__instance))
            ?.Select(effect => effect.Tag).Distinct().ToArray() ?? Array.Empty<GameTag>();
        if (tags.Length == 0) return;
        var affected = __instance.AppliedEntities.Where(entity => IsPublicDestination(entity))
            .Select(entity => (entity, tags)).ToList();
        if (affected.Count == 0) return;
        __state = new RemovalState {
            Match = match,
            Game = __instance.Game,
            Entries = entries,
            Affected = affected
        };
    }

    internal static void AfterRemove(RemovalState __state)
    {
        if (__state == null) return;
        if (!ReferenceEquals(CopyTransportBridge.ActiveMatch(__state.Game), __state.Match)
            || !ReferenceEquals(__state.Game.PowerHistory.Last, __state.Entries))
            throw new InvalidOperationException("Aura removal completed outside its native history scope.");
        foreach (var affected in __state.Affected)
        {
            IPlayable entity = affected.Entity;
            if (!ReferenceEquals(entity.Game, __state.Game)
                || !__state.Game.IdEntityDic.TryGetValue(entity.Id, out IPlayable current)
                || !ReferenceEquals(current, entity))
                throw new InvalidOperationException("Aura removal destination changed identity.");
            var tags = affected.Tags.ToDictionary(tag => tag, tag => entity[tag]);
            __state.Match.RecordTransportCorrection(__state.Entries, __state.Entries.Count,
                entity.Id, tags, true);
        }
    }

    private static bool IsPublicDestination(IPlayable entity)
    {
        EngineZone zone = entity?.Zone?.Type ?? EngineZone.INVALID;
        return zone == EngineZone.PLAY
            || zone == EngineZone.HAND && entity.Controller?.PlayerId == 1;
    }
}
