using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SabberStoneCore.Enums;
using SabberStoneCore.Model.Entities;

// Diagnostics only. Never passed to the pilot, fair view or simulation, so it names hidden cards on both sides:
// a bug report must be reconstructable from the log (owner 2026-11-04, a Secret that killed a minion was unreadable).
public static class MatchSnapshot
{
    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
    public static string Side(Controller player, bool npc)
    {
        var hero = player.Hero;
        var weapon = hero.Weapon;
#if ENGINE_SOURCE
        IEnumerable<IPlayable> board = player.BoardZone.Slots;
#else
        IEnumerable<IPlayable> board = player.BoardZone;
#endif
        return (npc ? "npc" : "human") + " hero=" + hero.Card.Id + ":" + N(hero.Health) + "+" + N(hero.Armor)
            + " weapon=" + (weapon == null ? "-" : weapon.Card.Id + ":" + N(weapon.AttackDamage) + "/" + N(weapon.Durability))
            + " mana=" + N(player.UsedMana) + "/" + N(player.BaseMana) + " temp=" + N(player.TemporaryMana)
            + " overload=" + N(player.OverloadLocked) + "/" + N(player.OverloadOwed)
            + " hand=" + N(player.HandZone.Count) + "[" + string.Join(",", player.HandZone.Select(c => c.Card.Id)) + "]"
            + " deck=" + N(player.DeckZone.Count) + " secrets=" + N(player.SecretZone.Count)
            + "[" + string.Join(",", player.SecretZone.Select(c => c.Card.Id)) + "]"
            + " board=[" + string.Join(",", board.Select(Actor)) + "]";
    }
    private static string Actor(IPlayable card)
    {
#if ENGINE_SOURCE
        if (card is BoardObject location)
            return card.Card.Id + "#" + N(card.Id) + " durability=" + N(location.Durability)
                + " cooldown=" + N(card[GameTag.LOCATION_ACTION_COOLDOWN]) + (location.IsExhausted ? " exhausted" : " ready");
#endif
        var minion = card as Minion;
        if (minion == null) return card.Card.Id + "#" + N(card.Id);
        var flags = new List<string>();
        if (minion.HasTaunt) flags.Add("taunt");
        if (minion.HasCharge) flags.Add("charge");
        if (minion.IsRush) flags.Add("rush");
        if (minion.HasDivineShield) flags.Add("divine-shield");
        if (minion.IsFrozen) flags.Add("frozen");
        if (minion.HasStealth) flags.Add("stealth");
        if (minion.Poisonous) flags.Add("poisonous");
        if (!minion.IsSilenced && minion.Power?.Trigger?.Type == TriggerType.TURN_END
            && minion.Power.Trigger.SingleTask is SabberStoneCore.Tasks.SimpleTasks.DestroyTask)
            flags.Add("dies-at-end-of-turn");
        if (minion[GameTag.TAG_ONE_TURN_EFFECT] != 0 || minion.AppliedEnchantments?.Any(e => e.Power?.Enchant?.IsOneTurnEffect == true) == true)
            flags.Add("temporary");
        if (minion.IsExhausted) flags.Add("exhausted");
        flags.Add(minion.CanAttack ? "can-attack" : "cannot-attack");
        return minion.Card.Id + "#" + N(minion.Id) + " " + N(minion.AttackDamage) + "/" + N(minion.Health)
            + " " + string.Join("|", flags);
    }
    public static string Candidates(GoldenDecision decision) => decision == null ? ""
        : string.Join(",", decision.Candidates.OrderByDescending(p => p.Value).Take(5)
            .Select(p => p.Key + "=" + p.Value.ToString("0.###", CultureInfo.InvariantCulture)));
}
