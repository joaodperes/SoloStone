using System;
using System.Collections.Generic;
using System.Linq;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using EngineCard = SabberStoneCore.Model.Card;

// Public recipes for the frozen release pool. Progress itself always comes from the engine, never text guesses.
internal static class QuestValue
{
    internal sealed class Recipe
    {
        internal readonly string Id, Reward;
        internal readonly double Rate;
        internal readonly Func<EngineCard, bool> Supports;
        internal Recipe(string id, string reward, double rate, Func<EngineCard, bool> supports = null)
        { Id = id; Reward = reward; Rate = rate; Supports = supports; }
    }
    private static bool Text(EngineCard c, string text) => (c.Text ?? "").IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
    private static bool HasMinionType(EngineCard c)
    {
#if ENGINE_SOURCE
        return c.Races.Count > 0;
#else
        return c[GameTag.CARDRACE] != 0;
#endif
    }
    internal static readonly IReadOnlyList<Recipe> Recipes = Array.AsReadOnly(new[] {
        new Recipe("TLC_602", "TLC_602t", 1),
        new Recipe("TLC_817t", "TLC_817t3", .8, c => (int)c[(GameTag)1635] == 5),
        new Recipe("TLC_817t2", "TLC_817t4", .8, c => (int)c[(GameTag)1635] == 6),
        new Recipe("TLC_513", "TLC_513t", 1, c => Text(c, "shuffle")),
        new Recipe("TLC_631", "TLC_631t", 2, c => Text(c, "2 damage") || c.Type == CardType.MINION && c.ATK == 2),
        new Recipe("TLC_830", "TLC_830t", .7, c => c.IsRace(Race.BEAST)),
        new Recipe("TLC_EVENT_400", "ICC_828t", 1.2, c => c.IsRace(Race.BEAST) || c.IsRace(Race.UNDEAD)),
        new Recipe("TLC_460", "TLC_460t", 1.5, c => Text(c, "Discover")),
        new Recipe("TLC_426", "TLC_426e", 1.5, c => c.IsRace(Race.MURLOC) || Text(c, "Murloc")),
        new Recipe("TLC_433", "TLC_433t", 3, c => Text(c, "Corpse")),
        new Recipe("TLC_229", "TLC_229t14", 1, c => c.Type == CardType.MINION && HasMinionType(c)),
        new Recipe("TLC_239", "TLC_239t", .5, c => Text(c, "Summon")),
        new Recipe("TLC_446", "TLC_446t", 1.2, c => Text(c, "Temporary")),
        new Recipe("END_017", "END_017t", .3, c => Text(c, "draw") || c.Cost <= 2),
        // Legacy engine quests lie outside the release starting lists; Latorvius can grant their rewards.
        new Recipe("UNG_116", "UNG_116t", 1), new Recipe("UNG_028", "UNG_028t", 1),
        new Recipe("UNG_067", "UNG_067t1", .8), new Recipe("UNG_920", "UNG_920t1", 1.5),
        new Recipe("UNG_934", "UNG_934t1", 1), new Recipe("UNG_940", "UNG_940t8", 1),
        new Recipe("UNG_942", "UNG_942t", 1.5), new Recipe("UNG_829", "UNG_829t1", 1),
        new Recipe("UNG_954", "UNG_954t1", 1)
    });
    private static readonly Dictionary<string, Recipe> byId = Recipes.ToDictionary(r => r.Id, StringComparer.Ordinal);
    private static readonly HashSet<string> rewards = new HashSet<string>(Recipes.Select(r => r.Reward)
        .Concat(new[] { "TLC_817t5" }), StringComparer.Ordinal);
    internal static Recipe For(string id) { Recipe recipe; return id != null && byId.TryGetValue(id, out recipe) ? recipe : null; }
    internal static bool IsQuest(string id) => id != null && (Cards.FromId(id).IsQuest || Cards.FromId(id)[(GameTag)1192] != 0);
    internal static bool IsReward(string id) => id != null && rewards.Contains(id);
    internal static bool IsPlayerEffect(string id) => id == "TLC_426e" || id == "TLC_631e"
        || id == "TLC_229t14e" || id == "TLC_513e2" || id == "UNG_067t1e";
}

public sealed partial class DeckPlanEvaluator
{
    private readonly Dictionary<string, double> questRates = new Dictionary<string, double>(StringComparer.Ordinal);
    private readonly Dictionary<string, double> generatedQuestDensity = new Dictionary<string, double>(StringComparer.Ordinal);
    private double GeneratedSupport(QuestValue.Recipe recipe, CardClass heroClass)
    {
        string key = recipe.Id + ":" + heroClass;
        if (generatedQuestDensity.TryGetValue(key, out double density)) return density;
        var prior = CardFrequencies.ForOrEmpty(heroClass);
        double total = prior.Sum(c => c.Weight);
        density = total == 0 ? 0 : prior.Where(c => recipe.Supports(Cards.FromId(c.CardId))).Sum(c => c.Weight) / total;
        generatedQuestDensity.Add(key, density); return density;
    }
    internal double QuestRate(QuestValue.Recipe recipe, bool own)
    {
        // An opponent's public quest implies its condition prior, never its actual list. Own plans may refine it.
        string root = recipe.Id == "TLC_817t" || recipe.Id == "TLC_817t2" ? "TLC_817" : recipe.Id;
        if (!own || recipe.Supports == null || !Plan.CardRoles.Any(r => r.CardId == root)) return recipe.Rate;
        double rate;
        if (questRates.TryGetValue(recipe.Id, out rate)) return rate;
        var cards = Plan.CardRoles.Select(r => Cards.FromId(r.CardId)).Where(c => !c.IsQuest).ToArray();
        double density = cards.Length == 0 ? .4 : cards.Count(recipe.Supports) / (double)cards.Length;
        // A bounded deck-density prior, not a prediction of draws or exact turns to completion.
        rate = recipe.Rate * Math.Max(.5, Math.Min(1.5, density / .4));
        questRates.Add(recipe.Id, rate); return rate;
    }

    internal double QuestAssets(FairPlayer player, IReadOnlyList<FairCard> hand, bool own)
    {
        double value = 0;
        foreach (var quest in player.Quests) {
            var recipe = QuestValue.For(quest.CardId);
            int total = Math.Max(1, Tag(quest, GameTag.QUEST_PROGRESS_TOTAL));
            int progress = Math.Max(0, Math.Min(total, Tag(quest, GameTag.QUEST_PROGRESS)));
            int remaining = total - progress;
            double rate = recipe == null ? 1 : QuestRate(recipe, own);
            double feasibility = 1;
            if (recipe?.Supports != null) {
                // A generated quest does not imply a supporting deck. Use public class frequencies instead.
                bool generatedOpponent = !own && quest.GeneratedQuest;
                double density = generatedOpponent ? GeneratedSupport(recipe, player.Class) : .4;
                if (generatedOpponent) rate *= density / .4;
                double support = hand.Count(c => !c.Unknown && recipe.Supports(Cards.FromId(c.CardId)))
                    + density * (player.DeckCount + hand.Count(c => c.Unknown));
                // Exact-damage attackers and full-board survival can progress repeatedly without new cards.
                if (quest.CardId == "TLC_631" && player.Board.Any(c => c.Attack == 2)
                    || quest.CardId == "TLC_239" && player.Board.Count == 7) support = Math.Max(support, remaining);
                feasibility = Math.Min(1, support / Math.Max(1, remaining));
            }
            double turns = remaining / Math.Max(.01, rate);
            long life = (long)player.Hero.Health + player.Hero.Armor, fatigue = Tag(player.Hero, GameTag.FATIGUE);
            int safeDraws = 0;
            while (life > fatigue + 1 && safeDraws < 30) { life -= ++fatigue; safeDraws++; }
            double horizon = player.DeckCount + safeDraws + 1;
            feasibility *= Math.Pow(Math.Min(1, horizon / Math.Max(1, turns)), 4);
            double reward = .8 * (recipe == null ? 12 : RewardPotential(recipe.Reward, player));
            // Installation begins earning progress immediately. Remaining work discounts the same reward
            // that replaces this term on completion; there is no separate action/progress bonus to double count.
            double fraction = progress / (double)total;
            double installed = Math.Min(.6 * reward, 12 / (1 + .04 * turns));
            value += feasibility * (reward * fraction + installed * (1 - fraction));
        }
        foreach (var card in hand.Where(c => !c.Unknown && QuestValue.IsReward(c.CardId))) {
            double held = .8 * RewardPotential(card.CardId, player)
                / (1 + .13 * Math.Max(0, card.Cost - player.BaseMana));
            // Normal Hand owns the generic friendly card value. Opponent ordinary hand is not scored.
            value += own ? Math.Max(0, held - Hand(new[] { card })) : held;
        }
        foreach (string reward in player.PendingQuestRewards) value += .8 * RewardPotential(reward, player);
        foreach (var reward in player.OpaqueQuestRewards) {
            var sampled = hand.FirstOrDefault(c => c.EntityId == reward.Key);
            value += Math.Max(0, .8 * RewardPotential(QuestValue.For(reward.Value).Reward, player)
                - (own && sampled != null ? Hand(new[] { sampled }) : 0));
        }
        foreach (var effect in player.QuestEffects) {
            int stacks = Math.Max(1, Tag(effect, GameTag.TAG_SCRIPT_DATA_NUM_1));
            // Future triggers only: already-buffed bodies are owned by Board, and drawn cards by Hand.
            if (effect.CardId == "TLC_426e") value += 12 * stacks;
            if (effect.CardId == "TLC_631e") value += 12 * stacks;
            if (effect.CardId == "TLC_229t14e") value += 7;
            if (effect.CardId == "TLC_513e2") value += 6;
            if (effect.CardId == "UNG_067t1e") value += 16;
        }
        if (player.HeroPower.CardId == "TLC_513hp") value += 14;
        if (player.Weapon?.CardId == "TLC_460t") value += 3.0 * player.Weapon.Durability;
        if (player.Weapon?.CardId == "TLC_239t") value += 4.0 * player.Weapon.Durability;
        value += player.Board.Count(c => c.CardId == "TLC_446t1" && Tag(c, GameTag.SILENCED) == 0) * 24;
        return value;
    }

    internal double RewardPotential(string id, FairPlayer player)
    {
        var card = Cards.FromId(id);
        double body = 2 + card.ATK * W.BoardAttack * k.BoardAttack + card.Health * W.BoardHealth * k.BoardHealth
            + .6 * card.Cost + (card.Taunt ? 2 : 0) + (card.DivineShield ? W.DivineShield : 0);
        if (card[GameTag.REBORN] != 0) body += .65 * (2 + card.ATK * W.BoardAttack + W.BoardHealth + .6 * card.Cost);
        if (card.Power?.DeathrattleTask != null) body += 3;
        switch (id) {
            case "TLC_426e": return 12;
            case "TLC_602t": return body + 11; // Two generated cards, not the seven hidden future draws.
            case "TLC_817t3": case "TLC_817t5": return 2 * body;
            case "TLC_631t": return body + 12;
            case "TLC_229t14": return body + 14;
            case "TLC_830t": return body + 16.5;
            case "END_017t": return body + Math.Min(4, player.DeckCount) * 5.5;
            case "TLC_513t": return 2 * (2 + 3 * W.BoardAttack + 3 * W.BoardHealth) + 5 * W.OwnLife + 20;
            case "TLC_460t": return 24;
            case "TLC_239t": return 24;
            case "TLC_446t": return 24;
            case "ICC_828t": return 18; // Choice-dependent Zombeast: bounded prior, never read hidden components.
            case "UNG_067t1": return 20;
            case "UNG_028t": return 18;
            case "UNG_829t1": return 20;
            case "UNG_934t1": return 18;
            default: return card.Type == CardType.MINION ? body : 12;
        }
    }
}
