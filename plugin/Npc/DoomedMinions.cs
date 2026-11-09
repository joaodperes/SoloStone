using System;
using System.Collections.Concurrent;
using System.Linq;
using SabberStoneCore.Model.Entities;

// doomed-minions: a minion that carries a delayed self-destroy (Nightmare, Corruption, Power Overwhelming, ...).
// Detection is structural, from card definitions and never from card ids: an attached enchantment whose printed trigger
// is an unconditional turn trigger (TURN_START / TURN_END) with a DestroyTask on the enchanted minion, or the minion's own
// unconditional printed TURN_END DestroyTask on itself. Silence removes attached enchantments, so a silenced minion is
// not doomed, and a silenced printed trigger does not count. A pure function of public state (a printed definition plus
// the attachments the fair view already shows), so it is fair for both sides.
internal static class DoomedMinions
{
#if ENGINE_SOURCE
    private static readonly ConcurrentDictionary<string, bool> definitions = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
    private static readonly Func<string, bool> describe = id => {
        try { return Dooms(SabberStoneCore.Model.Cards.FromId(id).Power?.Trigger, true); } catch (Exception) { return false; }
    };
    private static readonly System.Reflection.PropertyInfo targetType = typeof(SabberStoneCore.Tasks.SimpleTasks.DestroyTask)
        .GetProperty("TargetType", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
    // A printed trigger that unconditionally destroys, on a turn boundary, the enchanted minion (onTarget) or the minion
    // carrying the trigger itself (!onTarget). Conditional or compound tasks stay unknown, so they are not doomed.
    internal static bool Dooms(SabberStoneCore.Triggers.Trigger trigger, bool onTarget)
    {
        var multi = trigger as SabberStoneCore.Triggers.MultiTrigger;
        if (multi != null) return multi.Any(t => Dooms(t, onTarget));
        if (trigger == null || trigger.Type != SabberStoneCore.Enums.TriggerType.TURN_START && trigger.Type != SabberStoneCore.Enums.TriggerType.TURN_END
            || trigger.Condition != null || trigger.OwnerCondition != null) return false;
        var destroy = trigger.SingleTask as SabberStoneCore.Tasks.SimpleTasks.DestroyTask;
        if (destroy == null || targetType == null) return false;
        var kind = (SabberStoneCore.Tasks.SimpleTasks.EntityType)targetType.GetValue(destroy);
        // An enchantment is its own SOURCE, so SOURCE there would remove only the attachment; the enchanted minion is TARGET.
        return kind == (onTarget ? SabberStoneCore.Tasks.SimpleTasks.EntityType.TARGET : SabberStoneCore.Tasks.SimpleTasks.EntityType.SOURCE);
    }
    internal static bool EnchantmentDooms(string enchantmentCardId) => enchantmentCardId != null && definitions.GetOrAdd(enchantmentCardId, describe);
    // Caller guarantees a minion on a board.
    internal static bool Of(Minion minion)
    {
        var attached = minion.AppliedEnchantments;
        if (attached != null)
            for (int i = 0; i < attached.Count; i++)
                if (EnchantmentDooms(attached[i].Card.Id)) return true;
        return !minion.IsSilenced && Dooms(minion.Card.Power?.Trigger, false);
    }
#else
    internal static bool Of(Minion minion) => false;
#endif
}
