using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

internal enum LocalTierRewardTileKind { Pack, CardBack, RandomCard }
internal sealed class LocalTierRewardTile
{
    internal LocalTierRewardTileKind Kind;
    internal int Id, Count;
    internal string Rarity;
}
internal sealed class LocalTierRewardPlan
{
    // Header is the full sentence for popups; Title/Subtitle split it for the native ribbon without repeating the name.
    internal string MilestoneId, Header, Title, Subtitle;
    internal List<LocalTierRewardTile> Tiles = new List<LocalTierRewardTile>();
    internal List<string> Captions = new List<string>();
    internal static IReadOnlyList<LocalTierRewardPlan> Build(IReadOnlyList<LocalTierReward> rewards,
        Func<string, string> recipeName, Func<int, bool> isRealCardBack, Func<string, string> challengerName = null)
    {
        if (rewards == null) throw new ArgumentNullException(nameof(rewards));
        if (isRealCardBack == null) throw new ArgumentNullException(nameof(isRealCardBack));
        var plans = new List<LocalTierRewardPlan>();
        foreach (var reward in rewards)
        {
            string name = LocalLadderMilestones.DisplayName(reward.MilestoneId);
            string beat = string.IsNullOrEmpty(reward.OpponentName) ? null
                : (reward.MilestoneId == "legend:1" ? "You dethroned " : "You beat ") + reward.OpponentName;
            var plan = new LocalTierRewardPlan { MilestoneId = reward.MilestoneId, Title = name, Subtitle = beat,
                Header = beat == null ? name + " reached" : beat + " to reach " + name };
            if (reward.MilestoneId == "floor:diamond-5")
            {
                const string wall = "From here, nobody goes easy on you.";
                plan.Header += "\n" + wall;
                plan.Subtitle = beat == null ? wall : beat + "\n" + wall;
            }
            var challengers = new List<string>();
            foreach (var grant in reward.Grants)
            {
                if (grant.Count <= 0) throw new ArgumentException("Milestone grant count must be positive.", nameof(rewards));
                switch (grant.Kind)
                {
                    case LocalTierGrantKind.Pack:
                        plan.Tiles.Add(new LocalTierRewardTile { Kind = LocalTierRewardTileKind.Pack,
                            Id = FairPackRules.NativeBoosterId(grant.Ref), Count = grant.Count });
                        break;
                    case LocalTierGrantKind.Token:
                        if (!new[] { "COMMON", "RARE", "EPIC", "LEGENDARY" }.Contains(grant.Ref))
                            throw new ArgumentException("Unknown milestone token rarity.", nameof(rewards));
                        plan.Tiles.Add(new LocalTierRewardTile { Kind = LocalTierRewardTileKind.RandomCard,
                            Rarity = grant.Ref, Count = grant.Count });
                        plan.Captions.Add("Craft any " + char.ToUpperInvariant(grant.Ref[0]) + grant.Ref.Substring(1).ToLowerInvariant()
                            + (grant.Count == 1 ? "" : " (x" + grant.Count + ")"));
                        break;
                    case LocalTierGrantKind.CardBack:
                        if (!int.TryParse(grant.Ref, NumberStyles.None, CultureInfo.InvariantCulture, out int back))
                            throw new ArgumentException("Invalid milestone card back id.", nameof(rewards));
                        if (isRealCardBack(back))
                            plan.Tiles.Add(new LocalTierRewardTile { Kind = LocalTierRewardTileKind.CardBack, Id = back, Count = grant.Count });
                        else plan.Captions.Add("New card back: " + (LocalShop.LadderExclusiveCardBacks.FirstOrDefault(pair => pair.Value == back).Key ?? name));
                        break;
                    case LocalTierGrantKind.DeckKit:
                        plan.Captions.Add("Deck kit: claim a ready-made deck in the Shop"); break;
                    case LocalTierGrantKind.Trophy:
                        plan.Captions.Add("Trophy: " + (grant.Ref == "trophy:legend-1" ? "Legend #1" : "Legend")); break;
                    case LocalTierGrantKind.LegendDeck:
                        plan.Captions.Add(grant.Ref == "Legacy" ? "Legend deck: legacy claim" : "Legend deck saved"); break;
                    case LocalTierGrantKind.Recipe:
                        plan.Captions.Add("Recipe: " + (recipeName?.Invoke(grant.Ref) ?? grant.Ref)); break;
                    case LocalTierGrantKind.Challengers:
                        challengers.Add(challengerName?.Invoke(grant.Ref) ?? grant.Ref); break;
                    default: throw new ArgumentException("Unknown milestone grant kind.", nameof(rewards));
                }
            }
            if (challengers.Count > 0) plan.Captions.Add("New challengers: " + string.Join(", ", challengers));
            plans.Add(plan);
        }
        return plans;
    }
}
