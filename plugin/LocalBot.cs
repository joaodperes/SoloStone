using System;
using System.Collections.Generic;
using System.Linq;
using SabberStoneCore.Enums;
using SabberStoneCore.Model.Entities;
using SabberStoneCore.Tasks.PlayerTasks;

public static class LocalBot
{
    // Public-state fallback and branch ordering; bounded search evaluates the resulting positions.
    public static PlayerTask Choose(List<PlayerTask> tasks)
    {
        if (tasks == null || tasks.Count == 0) throw new InvalidOperationException("Bot has no legal action.");
#if ENGINE_SOURCE
        var keep = RewindKeep(tasks);
        if (keep != null) return keep;
#endif
        return tasks.OrderByDescending(Score).First();
    }

#if ENGINE_SOURCE
    internal static ChooseTask RewindKeep(List<PlayerTask> tasks) => tasks?.OfType<ChooseTask>().FirstOrDefault(task =>
        task.Controller.Choice?.ChoiceAction == SabberStoneCore.Model.ChoiceAction.REWIND && task.Choices.Count == 1 &&
        task.Controller.Game.IdEntityDic[task.Choices[0]].Card.Id == "TIME_000ta");
#endif

    public static double Score(PlayerTask task)
    {
#if ENGINE_SOURCE
        if (task is ChooseTask choice && task.Controller.Choice?.ChoiceAction == SabberStoneCore.Model.ChoiceAction.REWIND)
            return task.Controller.Game.IdEntityDic[choice.Choices.Single()].Card.Id == "TIME_000ta" ? double.MaxValue : double.MinValue;
#endif
        var bot = task.Controller;
        var target = task.Target as Character;
        bool enemy = target != null && target.Controller != bot;
        string id = task.PlayerTaskType == PlayerTaskType.HERO_POWER
            ? bot.Hero.HeroPower.Card.Id : task.Source?.Card.Id ?? "";
        if (task.PlayerTaskType == PlayerTaskType.END_TURN) return -10000;
        if (task.PlayerTaskType == PlayerTaskType.MINION_ATTACK || task.PlayerTaskType == PlayerTaskType.HERO_ATTACK)
        {
            var source = task.Source as Character ?? bot.Hero;
            if (target == null) return -1000;
            if (target is Hero)
                return source.AttackDamage >= (long)target.Health + target.Armor ? 100000 : 12.0 + source.AttackDamage * 2.0;
            bool kill = source.AttackDamage >= target.Health && !(target is Minion shieldTarget && shieldTarget.HasDivineShield);
            bool survive = (long)source.Health + source.Armor > target.AttackDamage || (source is Minion shieldSource && shieldSource.HasDivineShield);
            return (kill ? 24.0 + (long)target.Card.Cost * 3 : 1) + (survive ? 12 : -(long)source.Card.Cost * 3)
                + Math.Min(source.AttackDamage, target.Health) - (source is Hero ? (long)target.AttackDamage * 2.0 : 0);
        }
        if (task.PlayerTaskType == PlayerTaskType.HERO_POWER)
        {
            if (id == "CS2_101") return bot.BoardZone.Count < 7 ? 18 : -11000;
            if (id == "CS1h_001" && bot.ControllerAuraEffects[GameTag.HEALING_DOES_DAMAGE] == 1) return DamageScore(target, enemy, 2);
            if (id == "CS1h_001") return !enemy && target != null ? target.Damage > 0 ? Math.Min(2, target.Damage) * 7 : -11000 : -11000;
            if (id == "CS2_102") return 2;
            return DamageScore(target, enemy, 1) - 8;
        }
#if ENGINE_SOURCE
        if (task is ActivateOldGodTask) return 30;
        if (task.PlayerTaskType == PlayerTaskType.ACTIVATE_LOCATION)
        {
            if (id == "TLC_446t1") return 45.0 - task.ActionTarget.Cost;
            var location = (BoardObject)task.Source;
            if (id == "CORE_REV_990" && target is Minion minion)
            {
                if (enemy) return minion.Health == 1 && !minion.HasDivineShield ? 40.0 + (long)minion.Cost * 4 : -11000;
                return minion.Health > 1 && minion.CanAttack ? 28.0 + minion.AttackDamage : -11000;
            }
            if (id == "JAIL_877") return !bot.BoardZone.IsFull || location.Durability == 1 ? 30 : -11000;
            if (id == "BE_251") return 25.0 + Math.Min(2, bot.UsedMana) * 5;
            if (id == "TIME_044" || id == "TIME_044t1" || id == "TIME_044t2")
                return !enemy && target is Minion buffTarget ? 28.0 + (buffTarget.CanAttack ? buffTarget.AttackDamage : 0) : -11000;
            return -11000;
        }
        if (task.PlayerTaskType == PlayerTaskType.PLAY_CARD && id == "CORE_REV_023" &&
            task.ActionTarget is BoardObject victim && victim.Controller != bot)
            return 32.0 + (long)victim.Durability * 8;
        if (task.PlayerTaskType == PlayerTaskType.PLAY_CARD && task.Source is BoardObject boardObject)
            return 16.0 + (long)boardObject.Durability * 4;
#endif
        if (task.PlayerTaskType != PlayerTaskType.PLAY_CARD) return -1000;
        if (id == "CS2_089") return HealScore(bot, target, enemy, 6);
        if (id == "EX1_354") return HealScore(bot, target, enemy, 8, 18);
        if (id == "CS2_094") return enemy ? DamageScore(target, true, 3) + 12 : -11000;
        if (id == "CS2_087") return !enemy && target is Minion ? 24.0 + target.AttackDamage : -11000;
        if (id == "CS2_092") return !enemy && target is Minion ? 38.0 + target.AttackDamage : -11000;
        if (id == "EX1_355") return !enemy && target is Minion && target.AttackDamage > 0
            ? 20.0 + (long)target.AttackDamage * 5 : -11000;
        if (id == "EX1_371") return !enemy && target is Minion shield && !shield.HasDivineShield
            ? 28.0 + shield.AttackDamage : -11000;
        if (id == "EX1_362")
            return target == null ? 22.0 + (long)task.Source.Cost * 3
                : !enemy && target is Minion protectorTarget && !protectorTarget.HasDivineShield
                    ? 34.0 + protectorTarget.AttackDamage : -11000;
        if (id == "EX1_360") return enemy && target is Minion humbleTarget && humbleTarget.AttackDamage > 1
            ? 20.0 + (long)humbleTarget.AttackDamage * 4 : -11000;
        if (id == "EX1_382")
            return target == null ? 22.0 + (long)task.Source.Cost * 3
                : enemy && target is Minion aldorTarget && aldorTarget.AttackDamage > 1
                    ? 30.0 + (long)aldorTarget.AttackDamage * 4 : -11000;
        if (id == "EX1_619") return EqualityScore(bot);
        if (id == "CS2_093")
        {
            double hero = DamageScore(bot.Opponent.Hero, true, 2);
            return hero >= 100000 ? hero : bot.Opponent.BoardZone.Count == 0 ? -11000 : AreaScore(bot, 2) + hero - 5;
        }
        if (id == "CS2_024") return DamageScore(target, enemy, 3);
        if (id == "CS2_029") return DamageScore(target, enemy, 6);
        if (id == "CS1_130") return DamageScore(target, enemy, 2);
        if (id == "CS2_004") return !enemy && target is Minion ? 32.0 + target.AttackDamage : -1000;
        if (id == "CS2_022" || id == "CS2_108" || id == "CS2_234" || id == "EX1_622")
            return enemy && target is Minion ? 15.0 + (long)target.Card.Cost * 5 + target.AttackDamage : -1000;
        if (id == "CS2_032") return AreaScore(bot, 4) - 10;
        if (id == "CS1_112") return AreaScore(bot, 2) + bot.BoardZone.Sum(m => Math.Min(2, m.Damage) * 3) - 5;
        if (id == "CS2_114") return AreaScore(bot, 2) / Math.Max(1, bot.Opponent.BoardZone.Count) * Math.Min(2, bot.Opponent.BoardZone.Count);
        if (id == "EX1_400") return AreaScore(bot, 1) - bot.BoardZone.Sum(m => m.Health == 1 ? 20.0 + (long)m.Card.Cost * 2 : 1);
        if (id == "EX1_019") return !enemy && target is Minion ? 35.0 + target.AttackDamage : -1000;
        if (id == "EX1_603") return enemy ? DamageScore(target, true, 1) + 15 : target is Minion && target.Health > 1 ? 34 : -1000;
        if (task.Source is Minion) return 22.0 + (long)task.Source.Cost * 3;
        if (task.Source is Weapon) return bot.Hero.Weapon == null ? 40.0 + task.Source.Cost : -20;
        return 18.0 + task.Source.Cost;
    }

    private static double DamageScore(Character target, bool enemy, int damage)
    {
        if (!enemy || target == null) return -1000;
        if (target is Hero) return damage >= (long)target.Health + target.Armor ? 100000 : damage * 2;
        return !(target is Minion shieldTarget && shieldTarget.HasDivineShield) && damage >= target.Health ? 30.0 + (long)target.Card.Cost * 4 : Math.Min(damage, target.Health);
    }

    private static double HealScore(Controller bot, Character target, bool enemy, int healing, int drawValue = 0)
    {
        if (bot.ControllerAuraEffects[GameTag.HEALING_DOES_DAMAGE] == 1)
            return enemy ? DamageScore(target, true, healing) + drawValue : -11000;
        if (enemy || target == null) return -11000;
        int restored = Math.Min(healing, target.Damage);
        return restored > 0 || drawValue > 0 ? restored * 7 + drawValue : -11000;
    }

    private static double EqualityScore(Controller bot)
    {
        long gained = bot.Opponent.BoardZone.Sum(m => Math.Max(0L, (long)m.Health - 1))
            - bot.BoardZone.Sum(m => Math.Max(0L, (long)m.Health - 1));
        return gained > 0 ? 20.0 + gained * 3 : -11000;
    }

    private static double AreaScore(Controller bot, int damage) => bot.Opponent.BoardZone.Sum(m =>
        !m.HasDivineShield && m.Health <= damage ? 22.0 + (long)m.Card.Cost * 3 : Math.Min(damage, m.Health) * 2);
}
