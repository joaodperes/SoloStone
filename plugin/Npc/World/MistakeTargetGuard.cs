using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using EngineCard = SabberStoneCore.Model.Card;
using SabberStoneCore.Tasks.PlayerTasks;
using SabberStoneCore.Tasks.SimpleTasks;

// A constraint on injected mistakes, never on the search. Only public actors and static card definitions are read.
internal sealed class MistakeTargetGuard
{
    [Flags] internal enum Effect { Unknown = 0, Harmful = 1, Beneficial = 2 }
    private readonly HashSet<int> friendly, enemy;
    private readonly Dictionary<int, string> sources;
    private readonly bool healingDoesDamage;
    private static readonly Regex Markup = new Regex(@"<[^>]*>|\[x\]|[$#@_]", RegexOptions.Compiled);
    private static readonly Regex Harm = new Regex(@"\bdeals?\b[^.;]*\bdamage\b|\b(destroy|transform|silence|freeze|frozen)\b|\breturn\b.*\bhand\b|\btake control\b|-[0-9]+", RegexOptions.Compiled);
    private static readonly Regex NegativeBuff = new Regex(@"\b(?:give|grant|gets?|gain)\b[^.;]*-[0-9][^.;]*", RegexOptions.Compiled);
    private static readonly Regex SelfBenefit = new Regex(@"\b(?:restore|heal)\b[^.;]*\byour hero\b[^.;]*|\b(?:give|grant)\s+your\b[^.;]*|\bgain\b[^.;]*", RegexOptions.Compiled);
    private static readonly Regex Benefit = new Regex(@"\b(restore|heal|give|grant|gain)\b|\bgets?\b.*(\+[0-9]|taunt|rush|charge|windfury|divine shield|lifesteal|stealth|immune)|\+[0-9]+", RegexOptions.Compiled);

    internal MistakeTargetGuard(FairFacts facts)
    {
        friendly = new HashSet<int>(facts.Own.Board.Select(c => c.EntityId)) { facts.Own.Hero.EntityId };
        enemy = new HashSet<int>(facts.Opponent.Board.Select(c => c.EntityId)) { facts.Opponent.Hero.EntityId };
        sources = facts.Own.Hand.Concat(facts.Own.Board).Concat(facts.Own.Locations)
            .Concat(new[] { facts.Own.Hero, facts.Own.HeroPower }).GroupBy(c => c.EntityId)
            .ToDictionary(g => g.Key, g => g.First().CardId);
        healingDoesDamage = facts.HealingDoesDamage;
    }

    internal bool Allows(BotAction best, BotAction alternative)
    {
        Effect forbidden = friendly.Contains(alternative.Target) ? Effect.Harmful
            : enemy.Contains(alternative.Target) ? Effect.Beneficial : Effect.Unknown;
        if (forbidden == Effect.Unknown || (For(alternative) & forbidden) == 0) return true;
        // Search-owned synergy remains legal; sharing a target with a heal does not license damaging it.
        return best.Target == alternative.Target && (For(best) & forbidden) != 0;
    }

    private Effect For(BotAction action)
    {
        if (action.Type == PlayerTaskType.MINION_ATTACK || action.Type == PlayerTaskType.HERO_ATTACK) return Effect.Harmful;
        string id;
        return sources.TryGetValue(action.Source, out id) ? Classify(Cards.FromId(id), healingDoesDamage) : Effect.Unknown;
    }

    internal static Effect Classify(EngineCard card, bool healingDoesDamage = false)
    {
        if (card == null) return Effect.Unknown;
        string text = Markup.Replace(card.Text ?? "", " ").Replace('\n', ' ').ToLowerInvariant();
        var task = card.Power?.PowerTask;
        bool harm = Harm.IsMatch(text) || task is DamageTask || task is DestroyTask || task is TransformTask || task is SilenceTask;
        string targetBenefit = NegativeBuff.Replace(SelfBenefit.Replace(text, ""), "");
        bool targetHealTask = task is HealTask healing && healing.Type == EntityType.TARGET;
        bool heal = targetHealTask || Regex.IsMatch(targetBenefit, @"\b(restore|heal)\b");
        bool benefit = Benefit.IsMatch(targetBenefit) || targetHealTask;
        if (heal && healingDoesDamage) { harm = true; benefit = Benefit.IsMatch(Regex.Replace(targetBenefit, @"\b(restore|heal)\b", "")); }
        // Mixed/opaque effects keep every known polarity. Unknown effects otherwise remain eligible.
        return (harm ? Effect.Harmful : Effect.Unknown) | (benefit ? Effect.Beneficial : Effect.Unknown);
    }
}
