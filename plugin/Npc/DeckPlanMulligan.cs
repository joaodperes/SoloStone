using System;
using System.Collections.Generic;
using System.Linq;

public sealed class DeckPlanMulligan : MulliganPolicy
{
    private readonly DeckPlan plan;
    public DeckPlanMulligan(DeckPlan plan) { this.plan = plan ?? throw new ArgumentNullException(nameof(plan)); }

    public override IReadOnlyList<int> Keep(MulliganView view)
    {
        var matchup = plan.MatchupClasses.FirstOrDefault(m => m.Classes.Contains(view.OpponentClass))?.Matchup ?? Matchup.Midrange;
        var offered = view.Offered.Where(c => c.CardId != "GAME_005").OrderBy(c => c.EntityId).ToArray();
        var kept = new HashSet<int>(offered.Where(c => QuestValue.IsQuest(c.CardId))
            .GroupBy(c => c.CardId).Select(g => g.First().EntityId));
        var rules = plan.MulliganRules.Where(r => (r.Matchups & matchup) != 0).ToArray();
        foreach (var rule in rules.Where(r => r.Condition == MulliganCondition.Always)) Apply(rule);
        // Positive dependencies grow from unconditional keeps; discarded cards never enable each other.
        int previous;
        do
        {
            previous = kept.Count;
            foreach (var rule in rules.Where(r => r.Condition != MulliganCondition.Always)) Apply(rule);
        } while (kept.Count != previous);
        return Array.AsReadOnly(offered.Where(c => kept.Contains(c.EntityId)).Select(c => c.EntityId).ToArray());

        void Apply(MulliganRule rule)
        {
            foreach (var card in offered.Where(c => c.CardId == rule.CardId && !kept.Contains(c.EntityId)))
            {
                if (offered.Count(c => c.CardId == rule.CardId && kept.Contains(c.EntityId)) >= rule.MaxCopies) break;
                var others = offered.Where(c => kept.Contains(c.EntityId) && c.EntityId != card.EntityId).ToArray();
                bool dragon = others.Any(c => HasRole(c, CardRole.Dragon));
                bool curve = others.Count(c => c.Cost >= 1 && c.Cost <= 2) >= rule.MinimumEarlyPlays;
                bool keep;
                switch (rule.Condition)
                {
                    case MulliganCondition.Always: keep = true; break;
                    case MulliganCondition.KeptDragon: keep = dragon; break;
                    case MulliganCondition.EarlyCurve: keep = curve; break;
                    case MulliganCondition.EarlyCurveAndDragon: keep = curve && dragon; break;
                    case MulliganCondition.HasOneDrop: keep = others.Any(c => c.Cost == 1); break;
                    case MulliganCondition.HasAura: keep = others.Any(c => HasRole(c, CardRole.Aura)); break;
                    // The plan's cheap Development cards are minions; no engine card lookup crosses this boundary.
                    case MulliganCondition.CoinAndCheapMinions:
                        keep = view.Coin && others.Count(c => c.Cost >= 0 && c.Cost <= 1 && HasRole(c, CardRole.Development)) >= rule.MinimumEarlyPlays;
                        break;
                    default: throw new InvalidOperationException("Unknown mulligan condition: " + rule.Condition);
                }
                if (keep) kept.Add(card.EntityId);
            }
        }
    }

    private bool HasRole(MulliganCard card, CardRole role) =>
        plan.CardRoles.Any(r => r.CardId == card.CardId && (r.Roles & role) != 0);
}
