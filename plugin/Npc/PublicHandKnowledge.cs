#if ENGINE_SOURCE
using System;
using EngineZone = SabberStoneCore.Enums.Zone;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using SabberStoneCore.Model.Entities;
using SabberStoneCore.Kettle;
using SabberStoneCore.Tasks.SimpleTasks;

// Reconstruct only public observations, never the definition/cost/enchantments of a face-down hand actor.
internal static class PublicHandKnowledge
{
    internal sealed class Card
    {
        internal string Id;
        internal int Cost;
        internal string OpaqueQuest;
        // Publicly known minions this card holds, and the enchantment that holds them (death-held).
        internal string[] Captured;
        internal string CaptureEffect;
    }
    private static readonly FieldInfo generatedCard = typeof(AddCardTo).GetField("_card", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly Regex discount = new Regex(@"costs?\s*\((\d+)\)\s*(less|more)", RegexOptions.IgnoreCase);
    internal static Dictionary<int, Card> Read(SabberStoneCore.Model.Game game, Controller enemy, HistoryFacts publicHistory, bool captures = false)
    {
        var known = new Dictionary<int, Card>();
        var appearances = new Dictionary<int, string>();
        // Quest components and completed quests are public even when they were installed rather than played.
        var quests = enemy.SecretZone.Cast<IPlayable>().Concat(enemy.GraveyardZone)
            .Where(c => c.Card.IsQuest).ToDictionary(c => c.Id, c => c.Card.Id);
        var publicCards = publicHistory.Query.Events<HistoryCardFact>().Where(f => f.DefinitionKnown)
            .ToDictionary(f => f.Sequence);
        foreach (var fact in game.HistoryFacts.Query.Events<HistoryFact>()) {
            if (fact is ResidenceChangedFact residence && residence.Entered
                && (residence.Zone == EngineZone.DECK || residence.Zone == EngineZone.SECRET)) {
                known.Remove(residence.EntityId); appearances.Remove(residence.EntityId);
            }
            if (fact is HistoryCardFact actor && publicCards.TryGetValue(fact.Sequence, out var visible)) {
                var definition = Cards.FromAssetId(visible.DbfId);
                // Casting/playing a Secret is face down. Summons and non-Secret plays are public.
                if (!definition.IsSecret && (visible is CardPlayedFact || visible is MinionSummonedFact || visible is MinionDiedFact))
                    appearances[actor.EntityId] = definition.Id;
            }
            if (fact is MinionFormChangedFact form) appearances[form.NewEntityId] = Cards.FromAssetId(form.NewDefinition.DbfId).Id;
            if (!(fact is HandEnteredFact hand) || hand.ControllerId != enemy.Id) continue;
            known.Remove(hand.EntityId);
            string id = null, opaqueQuest = null;
            if (hand.EntryKind == HistoryHandEntryKind.Generated && quests.TryGetValue(hand.SourceId, out string quest)) {
                var recipe = QuestValue.For(quest);
                if (recipe != null && recipe.Reward != "TLC_426e") {
                    id = recipe.Reward;
                    if (id == "ICC_828t") opaqueQuest = quest;
                }
            }
            if (hand.FromZone == EngineZone.PLAY || hand.FromZone == EngineZone.GRAVEYARD)
                appearances.TryGetValue(hand.EntityId, out id);
            if (id == null && hand.EntryKind == HistoryHandEntryKind.Generated
                && appearances.TryGetValue(hand.SourceId, out string creator)) {
                // A fixed public generator (Mukla, etc.) reveals the product by its rules, unlike Discover/random draws.
                // shortcut: custom generator recipes need explicit public observations before their products become known.
                var task = Cards.FromId(creator).Power?.PowerTask as AddCardTo;
                if (task != null) id = ((SabberStoneCore.Model.Card)generatedCard.GetValue(task)).Id;
            }
            if (id == null) continue;
            int cost = Cards.FromId(id).Cost;
            if (hand.FromZone == EngineZone.PLAY && appearances.TryGetValue(hand.SourceId, out string source)) {
                var match = discount.Match((Cards.FromId(source).Text ?? "").Replace('_', ' '));
                if (match.Success) cost = Math.Max(0, cost + int.Parse(match.Groups[1].Value)
                    * (match.Groups[2].Value.Equals("less", StringComparison.OrdinalIgnoreCase) ? -1 : 1));
            }
            known[hand.EntityId] = new Card { Id = id, Cost = cost, OpaqueQuest = opaqueQuest };
        }
        // Incremental, branch-local observations survive cold forks without rescanning packet history.
        foreach (var actor in enemy.HandZone)
            if (game.PowerHistory != null && game.PowerHistory.TryGetPublicHandReveal(actor.Id, enemy.PlayerId, out string id, out int cost))
                known[actor.Id] = new Card { Id = id, Cost = cost };
        foreach (var reward in QuestRewards(game, enemy)) known[reward.Key] = reward.Value;
        var handIds = new HashSet<int>(enemy.HandZone.Select(c => c.Id));
        foreach (int id in known.Keys.Where(id => !handIds.Contains(id)).ToArray()) known.Remove(id);
        foreach (var card in enemy.HandZone)
            if (card[GameTag.REVEALED] != 0) known[card.Id] = new Card { Id = card.Card.Id, Cost = card[GameTag.COST] };
        if (captures)
            foreach (var card in enemy.HandZone) {
                // A holder's rules name its product (Slime 'em: "Each player gets a 3-Cost spell that resummons theirs"), so it
                // is public once its generator was seen played and every captured minion was seen. Anything less stays hidden.
                if (!DeathMeasures.Holds(card.Card.Id)) continue;
                var held = card.AppliedEnchantments?.Where(e => e.CapturedCard != null && e.CapturedCard.Type == CardType.MINION).ToArray();
                if (held == null || held.Length == 0) continue;
                if (!publicCards.Values.Any(f => f is CardPlayedFact && f.EntityId == held[0].Creator?.Id)) continue;
                if (!held.All(e => publicCards.Values.Any(f => f.DbfId == e.CapturedCard.AssetId))) continue;
                known[card.Id] = new Card { Id = card.Card.Id, Cost = card[GameTag.COST],
                    Captured = held.Select(e => e.CapturedCard.Id).ToArray(), CaptureEffect = held[0].Card.Id };
            }
        return known;
    }

    private static Dictionary<int, Card> QuestRewards(SabberStoneCore.Model.Game game, Controller enemy)
    {
        var quests = enemy.SecretZone.Cast<IPlayable>().Concat(enemy.GraveyardZone).Where(c => c.Card.IsQuest)
            .ToDictionary(c => c.Id, c => new { Recipe = QuestValue.For(c.Card.Id), Total = c[GameTag.QUEST_PROGRESS_TOTAL] });
        var result = new Dictionary<int, Card>();
        if (quests.Count == 0) return result;
        var completed = new HashSet<int>(); var pending = new Queue<(string Reward, string Quest)>();
        foreach (var packet in game.PowerHistory?.Full ?? Enumerable.Empty<IPowerHistoryEntry>()) {
            if (packet is PowerHistoryChangeEntity changed) result.Remove(changed.Entity.Id);
            if (packet is PowerHistoryTagChange tag) {
                if (tag.Tag == GameTag.QUEST_PROGRESS && quests.TryGetValue(tag.EntityId, out var quest)
                    && quest.Recipe != null && quest.Total > 0 && tag.Value >= quest.Total
                    && quest.Recipe.Reward != "TLC_426e"
                    && completed.Add(tag.EntityId)) pending.Enqueue((quest.Recipe.Reward, quest.Recipe.Id));
                if (tag.Tag == GameTag.CONTROLLER || tag.Tag == GameTag.ZONE && tag.Value != (int)EngineZone.HAND) result.Remove(tag.EntityId);
            }
            // Completion publicly promises a fixed card. The engine delivers queued quest rewards before
            // later hand-entry effects. Read only public identity/controller/zone, never the hidden Name or tags.
            if (!(packet is PowerHistoryFullEntity full) || pending.Count == 0
                || !full.Entity.Tags.TryGetValue(GameTag.CONTROLLER, out int controller) || controller != enemy.PlayerId
                || !full.Entity.Tags.TryGetValue(GameTag.ZONE, out int zone) || zone != (int)EngineZone.HAND) continue;
            var promise = pending.Dequeue(); string id = promise.Reward;
            result[full.Entity.Id] = new Card { Id = id, Cost = Cards.FromId(id).Cost, OpaqueQuest = id == "ICC_828t" ? promise.Quest : null };
            // The public halves automatically combine when both are held; no Discover selection is inspected.
            string other = id == "TLC_817t3" ? "TLC_817t4" : id == "TLC_817t4" ? "TLC_817t3" : null;
            if (other != null && result.Values.Any(c => c.Id == other)) pending.Enqueue(("TLC_817t5", promise.Quest));
        }
        return result;
    }
}
#endif
