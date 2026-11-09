using EngineZone = SabberStoneCore.Enums.Zone;
using EngineState = SabberStoneCore.Enums.State;
using EngineUnknown = SabberStoneCore.Model.Entities.Unknown;
using EngineWeapon = SabberStoneCore.Model.Entities.Weapon;
using EngineMinion = SabberStoneCore.Model.Entities.Minion;
using EngineHero = SabberStoneCore.Model.Entities.Hero;
using EngineController = SabberStoneCore.Model.Entities.Controller;
using EngineGame = SabberStoneCore.Model.Game;
using EngineEntity = SabberStoneCore.Model.Entities.Entity;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using SabberStoneCore.Model.Entities;
using SabberStoneCore.Tasks.PlayerTasks;

public sealed class FairOptions
{
    public bool RemoveOpponentSecrets;
    public bool TrackOwnDeck;
    public bool MaterializeOwnDeckTriggers;
#if ENGINE_SOURCE
    internal OwnDeckLedger Observations;
#endif
    // Reply views only: the simulated replier's opponent is the bot, whose Secrets the bot itself placed and knows.
    internal bool KeepOpponentSecrets;
    // Reply views only, own-deck-value: keep the simulated deck orders, so a card the line added is not sorted to the top by its new id.
    internal bool KeepDeckOrder;
    // AiFixes "carried-cards" (golden factory): the owner's own stored payload that its own hand/board host displays by
    // identity stays in the fair copy. Default false keeps every stored actor private (pre-fix behaviour).
    public bool ShowOwnCarriedCards;
    // AiFixes "death-held" (golden factory): an opponent card whose rules publicly name the minions it holds stays known.
    public bool ShowPublicCaptures;
    // AiFixes "death-chip" (golden factory): the search-free fallbacks score with the pilot's own evaluator. Null keeps the legacy one-ply score.
    public IStateEvaluator FallbackEvaluator;
}

internal sealed class SimulationFailureException : Exception
{
    internal readonly BotAction Action;
    internal SimulationFailureException(BotAction action, Exception cause) : base("Speculative action failed.", cause) { Action = action; }
}

public interface IStateEvaluator
{
    double Evaluate(FairFacts facts);
}

// Snapshots contain values only, so an evaluator cannot walk back to a mutable engine game.
public sealed class FairCard
{
    public readonly int EntityId, Cost, Attack, Health, Damage, Armor, Durability, Position;
    public readonly string CardId;
    public readonly bool Unknown, Taunt, DivineShield, Frozen, Exhausted, GeneratedQuest;
    internal readonly bool HasAttachedEffects;
    // doomed-minions: carries a delayed self-destroy (see DoomedMinions); false off the board and for hidden cards.
    public readonly bool Doomed;
    public readonly CardType Type;
    public readonly IReadOnlyDictionary<GameTag, int> Tags;
    internal FairCard(IPlayable card)
    {
        Unknown = card is EngineUnknown; EntityId = Unknown ? 0 : card.Id; CardId = Unknown ? null : card.Card.Id;
#if ENGINE_SOURCE
        if (card.Card.IsQuest) {
            var origin = card;
            // Parallel quest components inherit their root's provenance; installation is not generation.
            for (int i = 0; i < 8 && origin[GameTag.DISPLAYED_CREATOR] != 0
                && card.Game.IdEntityDic.TryGetValue(origin[GameTag.DISPLAYED_CREATOR], out var parent)
                && !(parent is EngineUnknown) && QuestValue.IsQuest(parent.Card.Id); i++) origin = parent;
            GeneratedQuest = origin[GameTag.DISPLAYED_CREATOR] != 0
                || card.Game.HistoryFacts.Query.Discoveries().Any(f => f.EntityIdsKnown && f.SelectedEntityIds.Contains(origin.Id));
        }
#endif
        HasAttachedEffects = !Unknown && (card.AppliedEnchantments?.Count ?? 0) > 0;
        Type = card.Card.Type; Cost = card.Cost; Position = card.ZonePosition;
        var character = card as Character;
        Attack = character?.AttackDamage ?? (card as EngineWeapon)?.AttackDamage ?? 0;
        Health = character?.Health ?? 0; Armor = character?.Armor ?? 0; Damage = character?.Damage ?? 0;
        Durability = (card as EngineWeapon)?.Durability ?? 0;
#if ENGINE_SOURCE
        if (card is BoardObject location) Durability = location.Durability;
#endif
        Taunt = (card as EngineMinion)?.HasTaunt ?? false;
        DivineShield = (card as EngineMinion)?.HasDivineShield ?? false;
        Frozen = character?.IsFrozen ?? false; Exhausted = card[GameTag.EXHAUSTED] != 0;
        CantAttack = character?.CantAttack ?? false;
        CantAttackHeroes = character?.CantAttackHeroes ?? false;
        Stealth = character?.HasStealth ?? false; Immune = character?.IsImmune ?? false;
        AttacksThisTurn = character?.NumAttacksThisTurn ?? 0;
        AttackLimit = card[GameTag.MEGA_WINDFURY] > 0 ? 4 : character?.HasWindfury == true ? 2 : 1;
#if ENGINE_SOURCE
        Untouchable = (card as EngineMinion)?.Untouchable ?? false;
        RushOnly = (card as EngineMinion)?.AttackableByRush ?? false;
#endif
        var minion = card as EngineMinion;
        if (minion != null && card.Zone?.Type == EngineZone.PLAY) {
            Doomed = DoomedMinions.Of(minion);
            if (!minion.IsSilenced) Triggers = TriggerMeasures.For(card.Card.Id);
            if (minion.HasDeathrattle && !minion.IsSilenced && card.Power?.DeathrattleTask != null) {
                DeathValue = FairDeathValue.For(card.Card, card.Card);
                Death = DeathMeasures.For(card.Card, card.Card);
            }
            var effects = card.AppliedEnchantments;
            if (effects != null)
                for (int i = 0; i < effects.Count; i++)
                    if (effects[i].Power?.DeathrattleTask != null) {
                        DeathValue = DeathValue.Plus(FairDeathValue.For(effects[i].Card, card.Card));
                        var granted = DeathMeasures.For(effects[i].Card, card.Card);
                        Death = Death == null ? granted : Death.Plus(granted);
                    }
            if (card[GameTag.REBORN] != 0 && card[GameTag.HAS_BEEN_REBORN] == 0) {
                RebornValue = FairDeathValue.Body(card.Card, 1);
                if (card.Card.Power?.DeathrattleTask != null) {
                    RebornDeathValue = FairDeathValue.For(card.Card, card.Card);
                    RebornDeath = DeathMeasures.For(card.Card, card.Card);
                }
            }
        }
#if ENGINE_SOURCE
        // Captured minions a held card brings back; the hand's privacy mask removes these enchantments from hidden hands.
        else if (!Unknown && (card.AppliedEnchantments?.Count ?? 0) > 0 && DeathMeasures.Holds(card.Card.Id)) {
            var held = new List<string>();
            foreach (var effect in card.AppliedEnchantments)
                if (effect.CapturedCard != null && effect.CapturedCard.Type == CardType.MINION) held.Add(effect.CapturedCard.Id);
            if (held.Count != 0) Held = held.ToArray();
        }
#endif
        Tags = new FairTags(card.NativeTags, card.Card.IsQuest ? card[GameTag.QUEST_PROGRESS] : 0, card.Card.IsQuest ? card[GameTag.QUEST_PROGRESS_TOTAL] : 0, card.Card.IsQuest);
    }
    public readonly FairDeathValue DeathValue, RebornValue, RebornDeathValue;
    // death-value (AiFixes): measured data for the same public effects; null when there is none. Only that flag reads them.
    public readonly FairDeathMeasure Death, RebornDeath;
    // trigger-value (AiFixes): measured printed event triggers of a working board minion; null when there are none. Only that flag reads them.
    public readonly FairTriggerMeasure[] Triggers;
    public readonly IReadOnlyList<string> Held;
    public readonly bool CantAttack, CantAttackHeroes, Stealth, Immune, Untouchable, RushOnly;
    public readonly int AttacksThisTurn, AttackLimit;
}

// Value-only summaries of public card definitions; never execute tasks or inspect creator/payload identities.
public struct FairDeathValue
{
    public double Bodies, Attack, Health, Cost, Taunt, Shield, Cards, OpponentCards, Other;
    public bool Any => Bodies != 0 || Cards != 0 || OpponentCards != 0 || Other != 0;
    public FairDeathValue Plus(FairDeathValue b) => new FairDeathValue {
        Bodies = Bodies + b.Bodies, Attack = Attack + b.Attack, Health = Health + b.Health,
        Cost = Cost + b.Cost, Taunt = Taunt + b.Taunt, Shield = Shield + b.Shield,
        Cards = Cards + b.Cards, OpponentCards = OpponentCards + b.OpponentCards, Other = Other + b.Other };
    internal static FairDeathValue Body(SabberStoneCore.Model.Card c, int health = 0) => new FairDeathValue {
        Bodies = 1, Attack = c.ATK, Health = health > 0 ? health : c.Health, Cost = c.Cost,
        Taunt = c[GameTag.TAUNT] != 0 ? 1 : 0, Shield = c[GameTag.DIVINE_SHIELD] != 0 ? 1 : 0 };
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, FairDeathValue> definitions
        = new System.Collections.Concurrent.ConcurrentDictionary<string, FairDeathValue>(StringComparer.Ordinal);
    private static readonly Func<string, FairDeathValue> describe = Describe;
    // "Resummon this minion" enchantments bind the host at use; every other row is a pure function of the effect.
    internal static bool HostBound(string effectId) => definitions.GetOrAdd(effectId, describe).Bodies < 0;
    internal static FairDeathValue For(SabberStoneCore.Model.Card effect, SabberStoneCore.Model.Card host)
    {
        // Resummons use the printed body, not buffs, damage, Battlecry or granted Deathrattles.
        var value = definitions.GetOrAdd(effect.Id, describe);
        return value.Bodies < 0 ? Body(host) : value;
    }
    private static int Number(string value)
    {
        switch (value.ToLowerInvariant()) { case "a": case "an": case "one": return 1; case "two": return 2; case "three": return 3; }
        int n; return int.TryParse(value, out n) ? Math.Min(7, n) : 1;
    }
    private static FairDeathValue Describe(string id)
    {
        var card = SabberStoneCore.Model.Cards.FromId(id);
        string text = System.Text.RegularExpressions.Regex.Replace(card.Text ?? "", "<[^>]+>|\\[x\\]", "")
            .Replace("\\n", " ").Replace("&", " ").Replace("#", "").Replace('_', ' ');
        text = System.Text.RegularExpressions.Regex.Replace(text, "\\s+", " ").Trim();
        int start = text.IndexOf("Deathrattle:", StringComparison.OrdinalIgnoreCase);
        if (start >= 0) text = text.Substring(start + "Deathrattle:".Length).Trim();
        if (text.Equals("Resummon this minion.", StringComparison.OrdinalIgnoreCase))
            return new FairDeathValue { Bodies = -1 }; // Cached template; For binds the public host definition.
        if (text.StartsWith("Summon", StringComparison.OrdinalIgnoreCase) && text.IndexOf("opponent", StringComparison.OrdinalIgnoreCase) >= 0)
            return new FairDeathValue { Other = -3 };
        // Recognise only simple standalone promises. Complex/conditional script text keeps the bounded fallback.
        var match = System.Text.RegularExpressions.Regex.Match(text,
            @"^Summon (a|an|one|two|three|[1-7]) random ([1-9])-Cost minions?\.$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success) {
            int n = Number(match.Groups[1].Value), cost = Number(match.Groups[2].Value);
            // shortcut: cost-sized vanilla body prior; use pool moments if random-summon decisions need finer pricing.
            return new FairDeathValue { Bodies = n, Attack = n * cost, Health = n * cost, Cost = n * cost };
        }
        match = System.Text.RegularExpressions.Regex.Match(text,
            @"^Summon (a|an|one|two|three|[1-7]) ([0-9]+)/([0-9]+) [^.]+\.$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success) {
            int n = Number(match.Groups[1].Value);
            var summon = card.Power?.DeathrattleTask as SabberStoneCore.Tasks.SimpleTasks.SummonTask;
            if (summon?.Card != null) {
                var body = Body(summon.Card); var result = new FairDeathValue();
                for (int i = 0; i < n; i++) result = result.Plus(body);
                return result;
            }
            return new FairDeathValue { Bodies = n, Attack = n * double.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                Health = n * double.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture), Taunt = text.IndexOf("Taunt", StringComparison.OrdinalIgnoreCase) >= 0 ? n : 0 };
        }
        match = System.Text.RegularExpressions.Regex.Match(text, @"^Draw (a|one|two|three|[1-7]) cards?\.$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success) return new FairDeathValue { Cards = Number(match.Groups[1].Value) };
        match = System.Text.RegularExpressions.Regex.Match(text,
            @"^Your opponent draws (a|one|two|three|[1-7]) cards?\.$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success) return new FairDeathValue { OpponentCards = Number(match.Groups[1].Value) };
        // Known drawbacks must not get the generic positive retained-effect premium.
        if (id == "FP1_001" || id == "CORE_FP1_001") return new FairDeathValue { Other = -5 };
        if (id == "FP1_029" || id == "CORE_FP1_029") return new FairDeathValue { OpponentCards = 1 };
        // shortcut: opaque/conditional effects retain a modest prior, not a claim about a sampled outcome.
        return new FairDeathValue { Other = 3 };
    }
}

public sealed class FairPlayer
{
    public readonly int PlayerId, Mana, BaseMana, DeckCount;
    public readonly CardClass Class;
    public readonly PlayState PlayState;
    public readonly FairCard Hero, HeroPower, Weapon;
    public readonly IReadOnlyList<FairCard> Board, Hand, Locations, Auras, Quests, QuestEffects;
    // own-deck-value: deck cards this search added or improved. Simulated identities only: a masked real card stays out.
    public readonly IReadOnlyList<FairCard> DeckAdded, DeckUpgraded;
    public readonly IReadOnlyList<string> PendingQuestRewards;
    // Entity -> publicly completed quest ID; never the hidden reward definition.
    public readonly IReadOnlyDictionary<int, string> OpaqueQuestRewards;
    internal FairPlayer(EngineController player, bool own = false, IReadOnlyDictionary<int, string> opaqueRewards = null, HashSet<int> deckRoot = null)
    {
        List<FairCard> added = null, upgraded = null;
        if (deckRoot != null)
            foreach (var c in player.DeckZone) {
                if (c is EngineUnknown) continue;
                if (!deckRoot.Contains(c.Id)) (added ?? (added = new List<FairCard>())).Add(new FairCard(c));
                else if (c.AppliedEnchantments?.Count > 0) (upgraded ?? (upgraded = new List<FairCard>())).Add(new FairCard(c));
            }
        DeckAdded = added == null ? NoCards : Array.AsReadOnly(added.ToArray());
        DeckUpgraded = upgraded == null ? NoCards : Array.AsReadOnly(upgraded.ToArray());
        OpaqueQuestRewards = opaqueRewards == null || opaqueRewards.Count == 0 ? NoRewards : new ReadOnlyDictionary<int, string>(player.HandZone
            .Where(c => opaqueRewards.ContainsKey(c.Id)).ToDictionary(c => c.Id, c => opaqueRewards[c.Id]));
        PlayerId = player.PlayerId; Mana = player.RemainingMana; BaseMana = player.BaseMana;
        Class = player.BaseClass;
        OverloadOwed = player.OverloadOwed; OverloadLocked = player.OverloadLocked;
        // One pass over the secret zone, lists only on a hit: most players hold neither secrets nor quests.
        List<int> secretIds = null; List<FairCard> secretCards = null, quests = null;
        foreach (var c in player.SecretZone) {
            if (c.IsSecret) {
                (secretIds ?? (secretIds = new List<int>())).Add(c.Id);
                if (own) (secretCards ?? (secretCards = new List<FairCard>())).Add(new FairCard(c));
            }
            // Quests are revealed on installation, including the opponent's quests and parallel components.
            if (c.Card.IsQuest) (quests ?? (quests = new List<FairCard>())).Add(new FairCard(c));
        }
        SecretEntityIds = secretIds == null ? NoIds : Array.AsReadOnly(secretIds.ToArray());
        Secrets = secretCards == null ? NoCards : Array.AsReadOnly(secretCards.ToArray());
        Quests = quests == null ? NoCards : Array.AsReadOnly(quests.ToArray());
        List<FairCard> questEffects = null;
        var applied = player.AppliedEnchantments;
        if (applied != null)
            for (int i = 0; i < applied.Count; i++)
                if (QuestValue.IsPlayerEffect(applied[i].Card.Id)) (questEffects ?? (questEffects = new List<FairCard>())).Add(new FairCard(applied[i]));
        QuestEffects = questEffects == null ? NoCards : Array.AsReadOnly(questEffects.ToArray());
#if ENGINE_SOURCE
        // A full hand defers a publicly specified payout. Read only its public creator link, never queued card data.
        if (player.PendingQuestRewards.Count == 0) PendingQuestRewards = Array.Empty<string>();
        else {
            var publicQuests = player.SecretZone.Cast<IPlayable>().Concat(player.GraveyardZone)
                .Where(c => c.Card.IsQuest).ToDictionary(c => c.Id, c => QuestValue.For(c.Card.Id));
            PendingQuestRewards = Array.AsReadOnly(player.PendingQuestRewards.Select(r => {
                QuestValue.Recipe recipe; return publicQuests.TryGetValue(r.CreatorId, out recipe) ? recipe?.Reward : null;
            }).Where(id => id != null).ToArray());
        }
#else
        PendingQuestRewards = new string[0];
#endif
        DeckCount = player.DeckZone.Count; PlayState = player.PlayState;
        Hero = new FairCard(player.Hero); HeroPower = new FairCard(player.Hero.HeroPower);
        Weapon = player.Hero.Weapon == null ? null : new FairCard(player.Hero.Weapon);
        Board = Snapshot(player.BoardZone);
        Hand = Snapshot(player.HandZone);
#if ENGINE_SOURCE
        var locations = player.BoardZone.GetLocations();
        Locations = locations.Length == 0 ? NoCards : Array.AsReadOnly(locations.Select(c => new FairCard(c)).ToArray());
        var auras = player.SecretZone.Auras;
        Auras = auras.Count == 0 ? NoCards : Array.AsReadOnly(auras.Select(c => new FairCard(c)).ToArray());
#else
        Locations = new FairCard[0];
        Auras = new FairCard[0];
#endif
    }
    // Shared empties: immutable, and most snapshots have no secrets, quests, locations or auras.
    private static readonly IReadOnlyList<FairCard> NoCards = Array.AsReadOnly(new FairCard[0]);
    private static readonly IReadOnlyList<int> NoIds = Array.AsReadOnly(new int[0]);
    private static readonly IReadOnlyDictionary<int, string> NoRewards = new ReadOnlyDictionary<int, string>(new Dictionary<int, string>());
    private static IReadOnlyList<FairCard> Snapshot<T>(SabberStoneCore.Model.Zones.Zone<T> zone) where T : IPlayable
    {
        int count = zone.Count;
        if (count == 0) return NoCards;
        var cards = new FairCard[count];
        for (int i = 0; i < count; i++) cards[i] = new FairCard(zone[i]);
        return Array.AsReadOnly(cards);
    }
    public readonly int OverloadOwed, OverloadLocked;
    public readonly IReadOnlyList<FairCard> Secrets;
    public readonly IReadOnlyList<int> SecretEntityIds;
}

// Sorted arrays in place of SortedDictionary + ReadOnlyDictionary: same order and lookups, far fewer objects per snapshot card, no enum boxing on Mono.
internal sealed class FairTags : IReadOnlyDictionary<GameTag, int>
{
    private readonly int[] keys, values;
    public int Count { get; }
    internal FairTags(IEnumerable<KeyValuePair<GameTag, int>> native, int progress, int total, bool quest)
    {
        var k = new int[8]; var v = new int[8]; int n = 0;
        foreach (var pair in native) {
            if (n + 2 >= k.Length) { Array.Resize(ref k, k.Length * 2); Array.Resize(ref v, v.Length * 2); }
            k[n] = (int)pair.Key; v[n++] = pair.Value;
        }
        if (quest) {
            foreach (var pair in new[] { (GameTag.QUEST_PROGRESS, progress), (GameTag.QUEST_PROGRESS_TOTAL, total) }) {
                int at = Array.IndexOf(k, (int)pair.Item1, 0, n);
                if (at >= 0) v[at] = pair.Item2; else { k[n] = (int)pair.Item1; v[n++] = pair.Item2; }
            }
        }
        Array.Sort(k, v, 0, n);
        keys = k; values = v; Count = n;
    }
    public bool TryGetValue(GameTag key, out int value)
    {
        int at = Array.BinarySearch(keys, 0, Count, (int)key);
        value = at >= 0 ? values[at] : 0; return at >= 0;
    }
    public bool ContainsKey(GameTag key) => Array.BinarySearch(keys, 0, Count, (int)key) >= 0;
    public int this[GameTag key] => TryGetValue(key, out int value) ? value : throw new KeyNotFoundException();
    public IEnumerable<GameTag> Keys { get { for (int i = 0; i < Count; i++) yield return (GameTag)keys[i]; } }
    public IEnumerable<int> Values { get { for (int i = 0; i < Count; i++) yield return values[i]; } }
    public IEnumerator<KeyValuePair<GameTag, int>> GetEnumerator() { for (int i = 0; i < Count; i++) yield return new KeyValuePair<GameTag, int>((GameTag)keys[i], values[i]); }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
public sealed class FairFacts
{
    public readonly int Turn, CurrentPlayerId, DistinctFriendlyCharactersDamagedThisTurn, OpponentSecretCount;
    public readonly bool HealingDoesDamage;
    public readonly FairPlayer Own, Opponent;
    public readonly IReadOnlyDictionary<string, int> OwnDeckKnowledge;
    // Derived public bounds belong to this immutable snapshot, including the reversed reply perspective.
    internal long? OwnThreatBound, OpponentThreatBound;
    internal FairFacts(EngineGame game, EngineController owner, int opponentSecrets, IReadOnlyDictionary<string, int> ownDeck,
        IReadOnlyDictionary<int, string> opaqueRewards = null, Dictionary<int, HashSet<int>> deckRoots = null)
    {
        Turn = game.Turn; CurrentPlayerId = game.CurrentPlayer.PlayerId;
        HashSet<int> ownRoot = null, opponentRoot = null;
        if (deckRoots != null) { deckRoots.TryGetValue(owner.PlayerId, out ownRoot); deckRoots.TryGetValue(owner.Opponent.PlayerId, out opponentRoot); }
        Own = new FairPlayer(owner, true, opaqueRewards, ownRoot);
        Opponent = new FairPlayer(owner.Opponent, false, opaqueRewards, opponentRoot);
        OpponentSecretCount = opponentSecrets; OwnDeckKnowledge = ownDeck;
        HealingDoesDamage = owner.ControllerAuraEffects[GameTag.HEALING_DOES_DAMAGE] != 0;
#if ENGINE_SOURCE
        HealingDoesDamage |= owner.AppliedEnchantments?.Any(e => e.Card.Id == "CATA_301e") == true;
        HashSet<int> damaged = null;
        foreach (var f in game.HistoryFacts.Query.Damage(game.HistoryFacts.Query.ThisTurn))
            if (f.Amount > 0 && f.VictimControllerId == owner.Id) (damaged ?? (damaged = new HashSet<int>())).Add(f.VictimId);
        DistinctFriendlyCharactersDamagedThisTurn = damaged?.Count ?? 0;
#endif
    }
}

public sealed class BotAction : IEquatable<BotAction>
{
    public readonly PlayerTaskType Type;
    public readonly int Source, Target, ChooseOne, Position;
    private readonly string choices;
    private BotAction(PlayerTask task)
    {
        Type = task.PlayerTaskType;
        Source = task.Source?.Id ?? (Type == PlayerTaskType.HERO_POWER ? task.Controller.Hero.HeroPower.Id
            : Type == PlayerTaskType.HERO_ATTACK ? task.Controller.Hero.Id : 0);
#if ENGINE_SOURCE
        Target = task.ActionTarget?.Id ?? 0;
#else
        Target = task.Target?.Id ?? 0;
#endif
        ChooseOne = task.ChooseOne;
        Position = (task as PlayCardTask)?.ZonePosition ?? -1;
        choices = task is ChooseTask pick ? string.Join(",", pick.Choices) : "";
    }
    public static BotAction From(PlayerTask task) => new BotAction(task);
    public PlayerTask Resolve(IEnumerable<PlayerTask> legal) => legal.Single(t => Equals(From(t)));
    public bool Equals(BotAction other) => other != null && Type == other.Type && Source == other.Source
        && Target == other.Target && ChooseOne == other.ChooseOne && Position == other.Position && choices == other.choices;
    public override bool Equals(object obj) => Equals(obj as BotAction);
    public string WithoutPosition => $"{Type}:{Source}:{Target}:{ChooseOne}:{choices}";
    public override int GetHashCode() => ToString().GetHashCode();
    public override string ToString() => $"{Type}:{Source}:{Target}:{ChooseOne}:{Position}:{choices}";
}

// A fair view fails closed on hidden history. Debug engines wrap task failures in TaskException, so search
// boundaries must recognise the private-history cause anywhere in the chain (live 2026-10-26: EDR_251).
internal static class FairHistory
{
    internal static bool IsPrivate(Exception error)
    {
        for (var e = error; e != null; e = e.InnerException)
            if (e is NotSupportedException && e.Message.StartsWith("Private historical ", StringComparison.Ordinal)) return true;
        return false;
    }
}
// The planner gets this sealed view, never an authoritative EngineGame or a foreign PlayerTask.
public sealed class FairBotState
{
    // Only effects created by this player's known cards can add their identity to a fair search key.
    // carried-reuse: what the bot's own entity-reference hosts carry. A payload hidden from this view reads as its masked definition.
    internal string OwnCarriedKey()
    {
#if ENGINE_SOURCE
        var bot = Bot;
        var b = new System.Text.StringBuilder();
        foreach (var host in bot.HandZone.Cast<IPlayable>().Concat(bot.BoardZone.Slots)) {
            if (host == null || !SabberStoneCore.Actions.PayloadRules.ReferencesEntity(host.Card, GameTag.TAG_SCRIPT_DATA_NUM_1)) continue;
            if (game.IdEntityDic.TryGetValue(host[GameTag.TAG_SCRIPT_DATA_NUM_1], out var payload)) b.Append(host.Id).Append('=').Append(payload.Card.Id).Append(',');
        }
        return b.ToString();
#else
        return "";
#endif
    }
    internal string OwnVisibleEffectsKey()
    {
#if ENGINE_SOURCE
        var bot = Bot;
        var hosts = new IEntity[] { bot, bot.Hero, bot.Hero.HeroPower, bot.Hero.Weapon, bot.Opponent, bot.Opponent.Hero, bot.Opponent.Hero.HeroPower, bot.Opponent.Hero.Weapon }
            .Concat(bot.BoardZone.Slots.Cast<IEntity>()).Concat(bot.Opponent.BoardZone.Slots.Cast<IEntity>())
            .Concat(bot.HandZone.Cast<IEntity>()).Concat(bot.SecretZone.Cast<IEntity>())
            .Concat(bot.GraveyardZone.Cast<IEntity>()).Concat(bot.Opponent.GraveyardZone.Cast<IEntity>()).Where(e => e != null);
        var effects = new List<string>();
        foreach (var host in hosts.Where(e => !(e is EngineUnknown)))
        foreach (var effect in host.AppliedEnchantments ?? Enumerable.Empty<Enchantment>())
        {
            var creator = effect.Creator;
            if (creator == null || creator is EngineUnknown || creator.Controller.PlayerId != owner) continue;
            var zone = creator.Zone?.Type;
            if (zone != EngineZone.PLAY && zone != EngineZone.GRAVEYARD && zone != EngineZone.HAND) continue;
            effects.Add(host.Id + ":" + effect.Card.Id + ":" + creator.Id);
        }
        return string.Join(",", effects.OrderBy(x => x, StringComparer.Ordinal));
#else
        return "";
#endif
    }
    internal Action<string> FaultReporter;
    // Card.HistoryDisplay targets that render a stored card's identity in its host's text.
    private static readonly GameTag[] DisplayTags = { GameTag.TAG_SCRIPT_DATA_NUM_1, GameTag.TAG_SCRIPT_DATA_NUM_2, (GameTag)2921 }; // 2921 = TAG_SCRIPT_DATA_NUM_6 (absent from the vendor engine DLL)
    internal void ReportFault(SimulationFailureException error) =>
        FaultReporter?.Invoke("simulation-fault action=" + error.Action + " error=" + error.InnerException);
    private FairBotState InheritReporter(FairBotState state)
    {
        state.FaultReporter = FaultReporter;
        state.fallbackEvaluator = fallbackEvaluator;
        state.deckRoots = deckRoots;
        state.opaqueQuestRewards = opaqueQuestRewards.Where(p => state.game.IdEntityDic.TryGetValue(p.Key, out var card)
            && card.Zone?.Type == EngineZone.HAND).ToDictionary(p => p.Key, p => p.Value);
        return state;
    }
    private IReadOnlyDictionary<int, string> opaqueQuestRewards = new Dictionary<int, string>();
    // own-deck-value: each player's deck slots, by PlayerId, when the root view was built. Reply views keep them across the owner flip.
    private Dictionary<int, HashSet<int>> deckRoots;
    private IStateEvaluator fallbackEvaluator;
    // The search-free policies (BoundedFallbackJob, FastFinish) value a state here, so a clock cut plays by the same rules as the search.
    internal bool FallbackUsesEvaluator => fallbackEvaluator != null;
    internal double FallbackValue(bool zeroDoomed = false) => fallbackEvaluator != null ? fallbackEvaluator.Evaluate(Facts) : Evaluate(zeroDoomed);
    private readonly EngineGame game;
    internal EngineGame FingerprintGame => game;
    private readonly int owner;
    private readonly DeckStyle style;
    private readonly int opponentSecretCount;
    private readonly int[] opponentSecretSlots;
    private readonly IReadOnlyDictionary<string, int> ownDeckKnowledge;
    private readonly bool materializeOwnDeckTriggers;
    internal BotAction RewindKeep { get; }
    private EngineController Bot => game.Player1.PlayerId == owner ? game.Player1 : game.Player2;
    private FairBotState(EngineGame game, int owner, DeckStyle style, BotAction rewindKeep = null,
        int opponentSecretCount = 0, IReadOnlyDictionary<string, int> ownDeckKnowledge = null, bool materializeOwnDeckTriggers = false,
        int[] opponentSecretSlots = null)
    {
        this.game = game; this.owner = owner; this.style = style; RewindKeep = rewindKeep;
        this.opponentSecretCount = opponentSecretCount;
        this.opponentSecretSlots = opponentSecretSlots ?? Array.Empty<int>();
        this.materializeOwnDeckTriggers = materializeOwnDeckTriggers;
        this.ownDeckKnowledge = ownDeckKnowledge ?? new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(StringComparer.Ordinal));
    }
    public static FairBotState Create(EngineGame source, int owner, int artificialSeed, DeckStyle style, FairOptions options = null)
        => CreateCore(source, owner, artificialSeed, style, options, false);
    // Bound the optional public-hand reconstruction, never the ability to make a move.
    internal const int RootHistoryLimit = 8192;
    internal const int RootPacketHistoryLimit = 65536;
#if ENGINE_SOURCE
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<EngineGame, object> historyLimitLogged
        = new System.Runtime.CompilerServices.ConditionalWeakTable<EngineGame, object>();
    internal static Action<string> HistoryLimitLog = message => Console.Error.WriteLine(message);
#endif
    private static FairBotState CreateCore(EngineGame source, int owner, int artificialSeed, DeckStyle style, FairOptions options, bool exact)
    {
#if ENGINE_SOURCE
        bool reconstructHand = source.HistoryFacts.RecordCount <= RootHistoryLimit
            && (source.PowerHistory?.Full.Count ?? 0) <= RootPacketHistoryLimit;
        if (!exact && !reconstructHand) lock (historyLimitLogged) {
            if (!historyLimitLogged.TryGetValue(source, out _)) {
                historyLimitLogged.Add(source, new object());
                HistoryLimitLog?.Invoke("fair-hand-knowledge-limit facts=" + source.HistoryFacts.RecordCount
                    + " packets=" + (source.PowerHistory?.Full.Count ?? 0) + "; opponent hand reconstruction skipped");
            }
        }
#endif
        BotAction keep = null;
        var chooser = source.Player1.PlayerId == owner ? source.Player1 : source.Player2;
        if (chooser.PlayerId != owner) throw new ArgumentOutOfRangeException(nameof(owner));
        var known = new Dictionary<string, int>(StringComparer.Ordinal);
        var originals = new Dictionary<string, int>(StringComparer.Ordinal);
        var retained = new List<string>();
        // Controller.Opponent initializes a serialized cache; do not touch it on the authoritative game.
        var opponent = chooser == source.Player1 ? source.Player2 : source.Player1;
        int secretCount = opponent.SecretZone.Count(card => card.IsSecret);
#if ENGINE_SOURCE
        if (!exact && options != null && (options.TrackOwnDeck || options.MaterializeOwnDeckTriggers))
        {
            options.Observations = OwnDeckLedger.Observe(source, chooser, options.Observations);
            known = options.Observations.Sample(source, chooser, artificialSeed, retained, originals);
        }
        if (chooser.Choice?.ChoiceAction == ChoiceAction.REWIND)
            keep = BotAction.From(ChooseTask.Pick(chooser, source.Rewind.KeepEntityId));
        // Own Secrets are the bot's own knowledge: the golden path (TrackOwnDeck) keeps them live in the clone so they
        // trigger in simulated replies. Opponent Secrets are always removed and only counted. Legacy views still reject.
        if (keep == null && (chooser.SecretZone.Any(card => card.IsSecret) && options?.TrackOwnDeck != true
            || secretCount != 0 && options?.RemoveOpponentSecrets != true && options?.KeepOpponentSecrets != true))
            throw new NotSupportedException("Fair search does not model secrets.");
        // The chooser sees its own pending offers, including Dark Gift/Tracking picks that are still DECK entities.
        var offered = new HashSet<int>(chooser.Choice?.Choices ?? new List<int>());
        if (chooser.Choice?.SourceId > 0) offered.Add(chooser.Choice.SourceId);
        foreach (int id in offered.ToArray())
        {
            int bound = source.IdEntityDic[id][SabberStoneCore.Actions.DiscoverRealRules.BoundEntity];
            if (bound != 0) offered.Add(bound);
            int gift = source.IdEntityDic[id][GameTag.DARK_GIFT_ENTITY];
            if (gift != 0) offered.Add(gift);
        }
        var copy = exact ? source.CloneExact() : source.CloneForSimulation(artificialSeed);
        // The summary and simulated delivery must use the same public promise, never a queued private ID.
        foreach (var player in new[] { copy.Player1, copy.Player2 }) {
            var recipes = player.SecretZone.Cast<IPlayable>().Concat(player.GraveyardZone)
                .Where(c => c.Card.IsQuest).ToDictionary(c => c.Id, c => QuestValue.For(c.Card.Id));
            var pending = player.PendingQuestRewards.ToArray(); player.PendingQuestRewards.Clear();
            foreach (var reward in pending)
                if (recipes.TryGetValue(reward.CreatorId, out var recipe) && recipe != null)
                    player.PendingQuestRewards.Enqueue((recipe.Reward, reward.CreatorId));
        }
        var publicHand = exact || !reconstructHand ? null : PublicHandKnowledge.Read(source, opponent, copy.HistoryFacts.ProjectForFairView(copy, owner), options?.ShowPublicCaptures == true);
        // Retain the public quest as the prior's source, not the privately crafted reward identity.
        var opaqueRewards = publicHand?.Where(p => p.Value.OpaqueQuest != null).ToDictionary(p => p.Key, p => p.Value.OpaqueQuest)
            ?? new Dictionary<int, string>();
        foreach (int id in opaqueRewards.Keys) publicHand.Remove(id);
        // Promoted offers can leave duplicate Setaside references; clone creates a separate actor for each residence.
        foreach (var player in new[] { copy.Player1, copy.Player2 })
            foreach (var stale in player.SetasideZone.Where(c => !ReferenceEquals(c, copy.IdEntityDic[c.Id])).ToArray())
                player.SetasideZone.Remove(stale);
        copy.RedactSetupDecksForFairView();
        var copyChooser = copy.Player1.PlayerId == owner ? copy.Player1 : copy.Player2;
        // Rebuild remembered slots before history projection too: current hidden transforms/tags are not observations.
        if (publicHand != null)
            foreach (var pair in publicHand) {
                var actor = copy.IdEntityDic[pair.Key]; var player = actor.Controller;
                Mask(copy, player, actor);
                ReplaceUnknown(copy, player, copy.IdEntityDic[pair.Key], pair.Value.Id);
                copy.IdEntityDic[pair.Key][GameTag.COST] = pair.Value.Cost;
            }
        var choices = copyChooser.Choice?.Choices;
        // Proxy offers reveal their bound deck actors too; project their residence before restoring the real offer list.
        if (choices != null) copyChooser.Choice.Choices = offered.ToList();
        try { copy.ProjectHistoryForFairView(owner, publicHand?.Keys); }
        finally { if (choices != null) copyChooser.Choice.Choices = choices; }
#else
        var offered = new HashSet<int>();
        if (options != null && (options.TrackOwnDeck || options.MaterializeOwnDeckTriggers))
            throw new NotSupportedException("Own-deck knowledge requires the source engine history API.");
        if (chooser.SecretZone.Any(card => card.IsSecret) || secretCount != 0 && options?.RemoveOpponentSecrets != true)
            throw new NotSupportedException("Fair search does not model secrets.");
        int cloneIndex = source.NextCloneIndex;
        EngineGame copy;
        try { copy = source.Clone(logging:false, resetRandomSeed:true, history:false); }
        finally { source.NextCloneIndex = cloneIndex; }
#endif
        // Resolve dependents before replacing parents: some Choose One options have no privacy tag.
        var privateActors = new HashSet<int>(new[] { copy.Player1, copy.Player2 }.SelectMany(player =>
            player.DeckZone.Where(card => !offered.Contains(card.Id)
                && !(exact && options?.MaterializeOwnDeckTriggers == true && !(card is EngineUnknown)))
                .Concat(player.PlayerId == owner ? Enumerable.Empty<IPlayable>() : player.HandZone.Where(card => !offered.Contains(card.Id)))
                .Select(card => card.Id)));
#if ENGINE_SOURCE
        var observations = exact ? null : options?.Observations ?? OwnDeckLedger.Observe(source, chooser, null);
        var visibleOffboard = new HashSet<int>(copy.HistoryFacts.Query.Events<HistoryCardFact>()
            .Where(f => f.DefinitionKnown).Select(f => f.EntityId));
        foreach (var actor in copy.IdEntityDic.Values.Where(c => c.Controller.PlayerId == owner && observations?.Observed(c.Id) == true))
            visibleOffboard.Add(actor.Id);
        // A known definition does not identify the actor after a hidden deck departure.
        var publicAppearances = copy.HistoryFacts.Query.Events<HistoryCardFact>().Where(f => f.DefinitionKnown).ToLookup(f => f.EntityId);
        foreach (var residence in source.HistoryFacts.Query.Events<ResidenceChangedFact>().GroupBy(f => f.EntityId).Select(g => g.Last()))
            if (residence.Zone == EngineZone.DECK && !residence.Entered
                && !publicAppearances[residence.EntityId].Any(f => f.Sequence > residence.Sequence)) visibleOffboard.Remove(residence.EntityId);
        foreach (var actor in copyChooser.HandZone.Concat(copyChooser.BoardZone.Slots)
            .Concat(offered.Where(copy.IdEntityDic.ContainsKey).Select(id => copy.IdEntityDic[id])))
            foreach (var branch in actor.ChooseOnePlayables ?? Array.Empty<IPlayable>())
                if (branch != null) visibleOffboard.Add(branch.Id);
        // The chooser saw and kept its own Discover selection. A suspended recipe can still hold it offboard
        // (Qonzu's second choice); it stays known until it next enters or leaves a hand or deck.
        foreach (var discovery in source.HistoryFacts.Query.Discoveries().Where(d => d.ControllerId == chooser.Id && d.EntityIdsKnown))
            foreach (int id in discovery.SelectedEntityIds)
                if (copy.IdEntityDic.TryGetValue(id, out var kept) && kept.Controller.PlayerId == owner && kept.Zone?.Type == EngineZone.SETASIDE
                    && !source.HistoryFacts.Query.Events<ResidenceChangedFact>().Any(f => f.EntityId == id && f.Sequence > discovery.Sequence))
                {
                    visibleOffboard.Add(id);
                    foreach (var branch in kept.ChooseOnePlayables ?? Array.Empty<IPlayable>())
                        if (branch != null) visibleOffboard.Add(branch.Id);
                }
        // Nellie's chooser knows its selected crew; a later controller does not inherit that knowledge.
        var knownCrew = new HashSet<int>();
        var crew = copyChooser.SetasideZone.Where(c => c[SabberStoneCore.Actions.PayloadRules.Key] == 74894).ToArray();
        if (crew.Length != 0)
        {
            var crewSources = new HashSet<int>(copy.HistoryFacts.Query.Discoveries().Where(d => d.ControllerId == copyChooser.Id && d.EntityIdsKnown)
                .SelectMany(d => d.SelectedEntityIds).Where(copy.IdEntityDic.ContainsKey)
                .Select(id => copy.IdEntityDic[id][GameTag.DISPLAYED_CREATOR]));
            foreach (var card in crew)
                if (copy.IdEntityDic.TryGetValue(card[SabberStoneCore.Actions.PayloadRules.Owner], out var ship)
                    && ship.Card.Id == "TSC_660t" && crewSources.Contains(ship[GameTag.CREATOR])) knownCrew.Add(card.Id);
        }
        // carried-cards: a stored payload its owner's own hand/board card displays by identity (the card text names it, e.g.
        // a carved Treant's "Battlecry: Cast {0}" = TAG_SCRIPT_DATA_NUM_1 of the stored spell's dbf) is the owner's
        // knowledge. Live 2026-11-01 (tater96): masking it made the search play a carved Typhoon Treant as a plain 2/2.
        // The identity must match exactly, so any other stored actor, and every opponent payload, stays private.
        bool showCarried = !exact && options?.ShowOwnCarriedCards == true;
        var displayedPayloads = new HashSet<int>(copy.IdEntityDic.Values.Where(c => showCarried
            && SabberStoneCore.Actions.PayloadRules.IsStored(c) && c.Card.AssetId != 0
            && copy.IdEntityDic.TryGetValue(c[SabberStoneCore.Actions.PayloadRules.Owner], out var host)
            && host.Controller.PlayerId == owner && c.Controller.PlayerId == owner
            && (host.Zone?.Type == EngineZone.HAND || host.Zone?.Type == EngineZone.PLAY)
            && (DisplayTags.Any(tag => host[tag] == c.Card.AssetId)
                // Entity-reference hosts (Treant, Pupil, Hammer, Egg) display the stored entity's id, not its dbfId.
                || SabberStoneCore.Actions.PayloadRules.ReferencesEntity(host.Card, GameTag.TAG_SCRIPT_DATA_NUM_1)
                    && host[GameTag.TAG_SCRIPT_DATA_NUM_1] == c.Id))
            .Select(c => c.Id));
        foreach (var actor in copy.IdEntityDic.Values.Where(c => !(c is Enchantment)
            && (c.Zone?.Type == EngineZone.SETASIDE || c[GameTag.ZONE] == (int)EngineZone.REMOVEDFROMGAME)
            && !exact && !offered.Contains(c.Id)
            && !knownCrew.Contains(c.Id) && !displayedPayloads.Contains(c.Id)
            && (SabberStoneCore.Actions.PayloadRules.IsStored(c) || !visibleOffboard.Contains(c.Id)))) privateActors.Add(actor.Id);
        // Retain only the public queue membership, never original links or queue order.
        var retainedSlots = copyChooser.SetasideZone.Where(c => privateActors.Contains(c.Id)
            && copy.IdEntityDic.TryGetValue(c[SabberStoneCore.Actions.PayloadRules.Owner], out var host)
            && (host.Card.Id == "JAIL_719e2" || host.Card.Id == "JAIL_509e"))
            .OrderBy(c => c.Id).Select(c => new { c.Id, Host = c[SabberStoneCore.Actions.PayloadRules.Owner],
                Key = c[SabberStoneCore.Actions.PayloadRules.Key] }).ToArray();
#endif
        int privateCount;
        do
        {
            privateCount = privateActors.Count;
            foreach (var actor in copy.IdEntityDic.Values)
            {
                if (privateActors.Contains(actor.Id) && actor.ChooseOnePlayables != null)
                    foreach (var branch in actor.ChooseOnePlayables.Where(branch => branch != null)) privateActors.Add(branch.Id);
                if ((actor.Zone?.Type == EngineZone.SETASIDE || actor[GameTag.ZONE] == (int)EngineZone.REMOVEDFROMGAME)
                    && (privateActors.Contains(actor[GameTag.CREATOR]) || privateActors.Contains(actor[GameTag.PARENT_CARD])))
                    privateActors.Add(actor.Id);
            }
        } while (privateCount != privateActors.Count);
#if ENGINE_SOURCE
        // The engine's projector discovers hidden roots through deck membership. Extend that read-only
        // projection input to offboard roots too, without moving actors or recording artificial events.
        var offboard = copy.IdEntityDic.Values.Where(c => privateActors.Contains(c.Id)
            && (c.Zone?.Type == EngineZone.SETASIDE || c[GameTag.ZONE] == (int)EngineZone.REMOVEDFROMGAME)).ToArray();
        if (offboard.Length != 0)
        {
            var firstDeck = copy.Player1.DeckZone; var secondDeck = copy.Player2.DeckZone;
            try
            {
                copy.Player1.DeckZone = new PrivacyDeck(copy.Player1, firstDeck.Concat(offboard.Where(c => c.Controller == copy.Player1)));
                copy.Player2.DeckZone = new PrivacyDeck(copy.Player2, secondDeck.Concat(offboard.Where(c => c.Controller == copy.Player2)));
                if (choices != null) copyChooser.Choice.Choices = offered.ToList();
                copy.ProjectHistoryForFairView(owner, publicHand?.Keys);
            }
            finally
            {
                copy.Player1.DeckZone = firstDeck; copy.Player2.DeckZone = secondDeck;
                if (choices != null) copyChooser.Choice.Choices = choices;
            }
        }
#endif
#if ENGINE_SOURCE
        // Silence every actor about to be masked BEFORE any zone ChangeEntity: HandZone.ChangeEntity refreshes hand
        // auras (HandAdjacencyCostAura, e.g. Stickybomb Saboteur's Sabotage), and a still-live aura owned by a hidden
        // card would re-attach its presentation to a neighbour mid-mask (CATA_186te leak).
        foreach (var player in new[] { copy.Player1, copy.Player2 })
            foreach (var card in player.DeckZone.Where(c => !offered.Contains(c.Id)
                    && !(exact && options?.MaterializeOwnDeckTriggers == true && !(c is EngineUnknown)))
                .Concat(player.PlayerId == owner ? Enumerable.Empty<IPlayable>() : player.HandZone.Where(c => !offered.Contains(c.Id))).ToArray())
            {
                card.ActivatedTrigger?.Remove();
                card.OngoingEffect?.Remove();
            }
#endif
        foreach (var player in new[] { copy.Player1, copy.Player2 })
        {
            foreach (var card in player.DeckZone.ToArray())
                // Exact reply inputs are already masked; retain the parent's canonical from-deck actors.
                if (!offered.Contains(card.Id) && !(exact && options?.MaterializeOwnDeckTriggers == true && !(card is EngineUnknown)))
                    Mask(copy, player, card);
#if ENGINE_SOURCE
            if (!(exact && options?.KeepDeckOrder == true)) player.DeckZone.MaskOrderForFairView();
#else
            var deck = player.DeckZone.OrderBy(c => c.Id).ToArray();
            foreach (var card in player.DeckZone.ToArray()) player.DeckZone.Remove(card);
            foreach (var card in deck) player.DeckZone.Add(card);
#endif
            if (player.PlayerId != owner)
            {
                if (options?.RemoveOpponentSecrets == true)
                    foreach (var secret in player.SecretZone.Where(card => card.IsSecret).ToArray())
                    {
                        secret.ActivatedTrigger?.Remove();
                        secret.OngoingEffect?.Remove();
                        player.SecretZone.Remove(secret);
                        player.SetasideZone.Add(secret);
                        Mask(copy, player, secret);
                    }
#if ENGINE_SOURCE
                SabberStoneCore.Actions.TurnQueueRules.RedactOpening(player);
#endif
                foreach (var card in player.HandZone.ToArray()) if (!offered.Contains(card.Id)) Mask(copy, player, card);
#if ENGINE_SOURCE
                foreach (var card in copy.IdEntityDic.Values.Where(c => c.Controller == player && !(c is Enchantment)
                    && (c.Zone?.Type == EngineZone.SETASIDE || c[GameTag.ZONE] == (int)EngineZone.REMOVEDFROMGAME)
                    && (SabberStoneCore.Actions.CardStateBoundary.HasPrivateAttachments(c)
                        || c[SabberStoneCore.Actions.Generic.PrivateDecisionOfferTag] == player.PlayerId)).ToArray()) Mask(copy, player, card);
#endif
            }
            int count = player.DeckCards.Count;
            player.DeckCards.Clear();
            player.DeckCards.AddRange(Enumerable.Repeat(EngineUnknown.UnknownCard, count));
        }
        foreach (int id in privateActors.OrderBy(id => id))
            if (copy.IdEntityDic.TryGetValue(id, out var actor) && !(actor is EngineUnknown)
                && (actor.Zone?.Type == EngineZone.SETASIDE || actor[GameTag.ZONE] == (int)EngineZone.REMOVEDFROMGAME))
                Mask(copy, actor.Controller, actor);
#if ENGINE_SOURCE
        var projectedChooser = copy.Player1.PlayerId == owner ? copy.Player1 : copy.Player2;
        for (var next = projectedChooser.Choice?.NextChoice; next != null; next = next.NextChoice)
            foreach (int id in next.Choices.Where(id => !offered.Contains(id)))
                Mask(copy, copy.IdEntityDic[id].Controller, copy.IdEntityDic[id]);
        // The owner cannot know the designation either; both viewpoints use anonymous pair records.
        foreach (var entry in copy.IdEntityDic.Values.Where(e => !(e is EngineUnknown)
            && SabberStoneCore.Actions.PayloadRules.IsStored(e)
            && e[SabberStoneCore.Actions.PayloadRules.Key] == SabberStoneCore.Actions.NativeTailRules.IllusionKey).ToArray())
            Mask(copy, entry.Controller, entry);
        SabberStoneCore.Actions.NativeTailRules.RedactIllusions(copy);
        // Tome forms are hidden even from their owner; Reveal samples from the artificial RNG.
        foreach (var actor in copy.IdEntityDic.Values) actor.NativeTags.Remove(SabberStoneCore.Actions.TurnQueueRules.TomeSelection);
        // shortcut: fair search scores guesses as misses, model belief outcomes when search needs this accuracy.
        foreach (var actor in copy.IdEntityDic.Values) actor.NativeTags.Remove(SabberStoneCore.Actions.DiscoverRealRules.CorrectAnswer);
        SabberStoneCore.Actions.DiscoverRealRules.RedactSuspiciousOffers(copy);
        foreach (var actor in copy.IdEntityDic.Values.Where(e => e.Controller.PlayerId != owner))
            foreach (var tag in actor.NativeTags.Keys.Where(tag => SabberStoneCore.Actions.DiscoverRealRules.IsInternalTag(tag) || SabberStoneCore.Actions.StartOfGameRules.IsInternalTag(tag)).ToArray())
                actor.NativeTags.Remove(tag);
        RedactHiddenTargetEnchantments(copy);
#endif
#if !ENGINE_SOURCE
        copy.SetRandomSeed(artificialSeed);
#endif
#if ENGINE_SOURCE
        if (publicHand != null)
            foreach (var pair in publicHand) {
                var actor = copy.IdEntityDic[pair.Key];
                ReplaceUnknown(copy, actor.Controller, actor, pair.Value.Id);
                copy.IdEntityDic[pair.Key][GameTag.COST] = pair.Value.Cost;
            }
        var canonicalIds = exact ? null : CanonicalizePrivateActors(copy, retainedSlots.Select(s => s.Id));
        if (!exact)
        {
            if (choices != null) copyChooser.Choice.Choices = offered.ToList();
            try { copy.ProjectHistoryForFairView(owner, publicHand?.Keys); }
            finally { if (choices != null) copyChooser.Choice.Choices = choices; }
        }
        // Sampled own-deck slots carry the bot's belief of their origin; the real slot origin stays erased.
        var sampledOrigins = new Dictionary<int, bool>();
#endif
        // The ledger's slot origins apply whenever it ran; only MaterializeOwnDeckTriggers turns samples into cards.
        bool materialize = options?.MaterializeOwnDeckTriggers == true;
        if (!exact && options != null && (options.TrackOwnDeck || materialize))
        {
            var bot = copy.Player1.PlayerId == owner ? copy.Player1 : copy.Player2;
            // Assign a sampled multiset to canonical slots, never a live card's identity or deck position.
            var remaining = new SortedDictionary<string, int>(known, StringComparer.Ordinal);
            var remainingOriginals = new Dictionary<string, int>(originals, StringComparer.Ordinal);
            foreach (var visible in bot.DeckZone.Where(c => !(c is EngineUnknown)))
                if (remaining.ContainsKey(visible.Card.Id))
                {
                    remaining[visible.Card.Id]--;
#if ENGINE_SOURCE
                    int left;
                    if (copy.HistoryFacts.Query.KnownOrigin(visible.Id, bot.Id) == true
                        && remainingOriginals.TryGetValue(visible.Card.Id, out left) && left > 0) remainingOriginals[visible.Card.Id] = left - 1;
#endif
                }
            var samples = remaining.SelectMany(pair => Enumerable.Repeat(pair.Key, Math.Max(0, pair.Value))).ToList();
            // Equal definitions are interchangeable: the flags never change the sampled definition order.
            var flags = remaining.SelectMany(pair => {
                int count = Math.Max(0, pair.Value), original;
                remainingOriginals.TryGetValue(pair.Key, out original);
                original = Math.Max(0, Math.Min(count, original));
                return Enumerable.Repeat(true, original).Concat(Enumerable.Repeat(false, count - original));
            }).ToList();
            var slots = bot.DeckZone.OfType<EngineUnknown>().OrderBy(c => c.Id).ToArray();
            while (samples.Count < slots.Length) { samples.Add(null); flags.Add(false); }
            var random = new Random(artificialSeed);
            for (int i = samples.Count - 1; i > 0; i--) {
                int j = random.Next(i + 1); var card = samples[i]; samples[i] = samples[j]; samples[j] = card;
                bool flag = flags[i]; flags[i] = flags[j]; flags[j] = flag;
            }
            for (int i = 0; i < slots.Length; i++)
            {
                if (materialize && samples[i] != null) ReplaceUnknown(copy, bot, slots[i], samples[i]);
#if ENGINE_SOURCE
                // An unsampled slot is an unobserved addition, so it did not start in this deck.
                sampledOrigins[slots[i].Id] = samples[i] != null && flags[i];
#endif
            }
        }
#if ENGINE_SOURCE
        if (options?.MaterializeOwnDeckTriggers == true && !exact && retainedSlots.Length != 0)
        {
            retained.Sort(StringComparer.Ordinal);
            var random = new Random(artificialSeed);
            for (int i = retained.Count - 1; i > 0; i--) {
                int j = random.Next(i + 1); var card = retained[i]; retained[i] = retained[j]; retained[j] = card;
            }
            for (int i = 0; i < retainedSlots.Length; i++)
            {
                var slot = retainedSlots[i];
                int id = canonicalIds[slot.Id];
                if (i < retained.Count && retained[i] != null) ReplaceUnknown(copy, copyChooser, copy.IdEntityDic[id], retained[i]);
                var sample = copy.IdEntityDic[id];
                sample[SabberStoneCore.Actions.PayloadRules.Owner] = slot.Host;
                sample[SabberStoneCore.Actions.PayloadRules.Key] = slot.Key;
                sample[SabberStoneCore.Actions.PayloadRules.Stored] = 1;
            }
        }
#endif
        var secretSlots = options?.RemoveOpponentSecrets == true && secretCount > 0 ? new int[secretCount] : Array.Empty<int>();
#if ENGINE_SOURCE
        // Reserve from the public count after canonicalization, never the hidden actors' allocation history.
        for (int i = 0; i < secretSlots.Length; i++) {
            int id = copy.NextId;
            secretSlots[i] = id;
            copy.IdEntityDic[id] = new EngineUnknown(copyChooser.Opponent, new Dictionary<GameTag, int> {
                [GameTag.CONTROLLER] = copyChooser.Opponent.PlayerId, [GameTag.ZONE] = (int)EngineZone.REMOVEDFROMGAME }, id);
        }
        AssumeFairOrigins(exact ? source : null, copy, sampledOrigins);
        // Masking removed the holder's attachments; restore only the public minions, after every id is final.
        if (publicHand != null)
            foreach (var pair in publicHand.Where(p => p.Value.Captured != null)) {
                int held = canonicalIds != null && canonicalIds.TryGetValue(pair.Key, out int mapped) ? mapped : pair.Key;
                var holder = copy.IdEntityDic[held]; var controller = holder.Controller;
                foreach (string captured in pair.Value.Captured)
                    Enchantment.GetInstance(controller, controller.Hero, holder, Cards.FromId(pair.Value.CaptureEffect)).CapturedCard = Cards.FromId(captured);
            }
#endif
        var result = new FairBotState(copy, owner, style, keep, secretCount,
            new ReadOnlyDictionary<string, int>(new SortedDictionary<string, int>(known, StringComparer.Ordinal)),
            options?.MaterializeOwnDeckTriggers == true, secretSlots);
        result.fallbackEvaluator = options?.FallbackEvaluator;
        if (!exact && options?.TrackOwnDeck == true) {
            result.deckRoots = new[] { copy.Player1, copy.Player2 }.ToDictionary(p => p.PlayerId, p => new HashSet<int>(p.DeckZone.Select(c => c.Id)));
        }
#if ENGINE_SOURCE
        result.opaqueQuestRewards = opaqueRewards.ToDictionary(p => canonicalIds != null && canonicalIds.TryGetValue(p.Key, out int id) ? id : p.Key, p => p.Value);
#endif
        return result;
    }
#if ENGINE_SOURCE
    // Every erased origin gets a fair belief, so origin-reading cards resolve in search instead of failing closed:
    // the owner's sampled deck slots use the sample's origin; a reply view keeps its parent fair view's beliefs;
    // any other hidden actor (masked hand/deck card) is assumed to have started in its controller's deck.
    private static void AssumeFairOrigins(EngineGame parent, EngineGame copy, IReadOnlyDictionary<int, bool> sampled)
    {
        var query = copy.HistoryFacts.Query;
        var initial = new List<KeyValuePair<int, int>>();
        var foreign = new List<int>();
        foreach (var actor in copy.IdEntityDic.Values)
        {
            if (query.KnownOrigin(actor.Id, actor.Controller.Id) != null) continue;
            bool started;
            int controller = actor.Controller.Id;
            if (sampled.TryGetValue(actor.Id, out started)) { }
            else if (parent != null && parent.HistoryFacts.Query.KnownOrigin(actor.Id, parent.Player1.Id) is bool first
                && parent.HistoryFacts.Query.KnownOrigin(actor.Id, parent.Player2.Id) is bool second)
            {
                started = first || second;
                controller = first ? copy.Player1.Id : copy.Player2.Id;
            }
            else started = true;
            if (started) initial.Add(new KeyValuePair<int, int>(actor.Id, controller));
            else foreign.Add(actor.Id);
        }
        copy.HistoryFacts.AssumeOrigins(initial, foreign);
    }
    private static readonly FieldInfo entityCount = typeof(EntityList).GetField("_count", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo zoneEntities = typeof(SabberStoneCore.Model.Zones.Zone<IPlayable>).GetField("_entities", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly PropertyInfo enchantmentCreator = typeof(Enchantment).GetProperty(nameof(Enchantment.Creator));
    private static readonly PropertyInfo enchantmentTarget = typeof(Enchantment).GetProperty(nameof(Enchantment.Target));
    // A public aura can still refresh onto a neighbour while that neighbour is masked (the hidden card's id is reused),
    // leaving an enchantment whose Target is Unknown: its presence and counters describe a hidden card. Retire every
    // such enchantment the same way Mask retires attachments (structural Unknown, references redacted).
    private static readonly Dictionary<CardClass, string[]> secretPools = new Dictionary<CardClass, string[]>();
    private static string[] SecretPool(CardClass cardClass)
    {
        lock (secretPools)
        {
            string[] pool;
            if (secretPools.TryGetValue(cardClass, out pool)) return pool;
            var policy = OfflinePoolPolicy.Load();
            pool = SabberStoneCore.CardSets.Client.StandardSets.CollectibleIds.Select(Cards.FromId)
                .Where(card => card != null && card.Implemented && card.IsSecret && card.Class == cardClass && policy.AllowsCard(card))
                .Select(card => card.Id).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            secretPools.Add(cardClass, pool);
            return pool;
        }
    }
    private IReadOnlyList<string[]> secretWorlds;
    private bool secretWorldsComplete;
    private double secretWorldPriorMass;
    // A certificate covers every ordered assignment. Overflow still supplies bounded hypotheses to heuristic replay.
    internal IReadOnlyList<string[]> OpponentSecretWorlds()
    {
        var worlds = PlausibleOpponentSecretWorlds(out bool complete, out _);
        return complete ? worlds : null;
    }
    internal IReadOnlyList<string[]> PlausibleOpponentSecretWorlds(out bool complete, out double priorMass)
    {
        if (secretWorlds != null) { complete = secretWorldsComplete; priorMass = secretWorldPriorMass; return secretWorlds; }
        complete = true;
        priorMass = 1;
        if (opponentSecretCount == 0 || Bot.Opponent.SecretZone.Any(c => c.IsSecret)) return Array.Empty<string[]>();
        var prior = CardFrequencies.ForOrEmpty(Bot.Opponent.BaseClass).Where(f => Cards.FromId(f.CardId).IsSecret)
            .ToDictionary(f => f.CardId, f => f.Weight, StringComparer.Ordinal);
        var pool = new HashSet<string>(SecretPool(Bot.Opponent.BaseClass), StringComparer.Ordinal);
        pool.UnionWith(prior.Keys);
        bool broad = game.HistoryFacts.RecordCount > RootHistoryLimit;
        if (broad) complete = false; // The work cap must not turn a partial history into a certificate.
        else {
            var history = game.HistoryFacts.Query;
            var publicCards = history.Events<HistoryCardFact>().Where(f => f.DefinitionKnown).ToArray();
            pool.UnionWith(publicCards.Where(f => f.ControllerId == Bot.Opponent.Id && Cards.FromAssetId(f.DbfId).IsSecret)
                .Select(f => Cards.FromAssetId(f.DbfId).Id));
            var knownNonSecrets = new HashSet<int>(publicCards.Where(f => f.EntityId != 0
                && !Cards.FromAssetId(f.DbfId).IsSecret).Select(f => f.EntityId));
            long firstTurn = history.Events<TurnStartedFact>().Select(f => f.Sequence).DefaultIfEmpty(0).First();
            var hiddenPlays = history.Plays().Where(f => f.ControllerId == Bot.Opponent.Id && !f.DefinitionKnown).ToArray();
            long lastSecretPlay = hiddenPlays.Length == opponentSecretCount ? hiddenPlays.Max(f => f.Sequence) : long.MaxValue;
            // Coin and ordinary hero powers do not create off-class Secrets. Unexplained installation,
            // generation, transfer or shuffle widens to every implemented Secret, including historical sets.
            broad = firstTurn == 0
                || hiddenPlays.Length != opponentSecretCount
                || history.HandEntries().Any(f => f.Sequence > firstTurn && f.Sequence <= lastSecretPlay && f.ControllerId == Bot.Opponent.Id
                    && f.EntryKind == HistoryHandEntryKind.Generated && !knownNonSecrets.Contains(f.EntityId))
                || history.Events<ShuffleOperationFact>().Any(f => f.Sequence > firstTurn && f.Sequence <= lastSecretPlay && f.DestinationControllerId == Bot.Opponent.Id)
                || history.Events<CardTransferredFact>().Any(f => f.ControllerId == Bot.Opponent.Id)
                || publicCards.Any(f => Cards.FromAssetId(f.DbfId).Id == "TIME_860");
        }
        if (broad || pool.Count < opponentSecretCount) pool.UnionWith(AllImplementedSecrets.Value);
        // Priors order work only: even tiny-prior blockers remain in exhaustive certification.
        var ordered = pool.OrderByDescending(id => prior.TryGetValue(id, out double weight) ? weight : 0)
            .ThenBy(id => id, StringComparer.Ordinal).ToArray();
        // Include omitted assignments in the denominator without enumerating them (ordered, distinct weighted draws).
        var mass = new double[opponentSecretCount + 1]; mass[0] = 1;
        foreach (string id in ordered) {
            double weight = prior.TryGetValue(id, out double frequency) ? frequency : .001;
            for (int n = opponentSecretCount; n > 0; n--) mass[n] += mass[n - 1] * weight;
        }
        priorMass = mass[opponentSecretCount];
        for (int n = 2; n <= opponentSecretCount; n++) priorMass *= n;
        var worlds = new List<string[]>();
        var selected = new string[opponentSecretCount];
        bool Visit(int depth)
        {
            if (depth == selected.Length) {
                if (worlds.Count == 128) return false;
                worlds.Add((string[])selected.Clone()); return true;
            }
            foreach (string id in ordered) {
                bool used = false;
                for (int j = 0; j < depth; j++) if (selected[j] == id) used = true;
                if (used) continue;
                selected[depth] = id;
                if (!Visit(depth + 1)) return false;
            }
            return true;
        }
        complete &= Visit(0) && worlds.Count != 0;
        secretWorldsComplete = complete;
        secretWorldPriorMass = priorMass;
        return secretWorlds = worlds;
    }
    private static readonly Lazy<string[]> AllImplementedSecrets = new Lazy<string[]>(() => Cards.All
        .Where(c => c.Implemented && c.IsSecret).Select(c => c.Id).Distinct(StringComparer.Ordinal).ToArray());
    internal FairBotState WithOpponentSecrets(IReadOnlyList<string> secrets, int sample)
    {
        if (secrets.Count != opponentSecretSlots.Length) throw new ArgumentException("Sample must match the reserved Secret slots.", nameof(secrets));
        var state = WithArtificialSeed(sample);
        for (int i = 0; i < secrets.Count; i++) {
            var actor = EngineEntity.FromCard(state.Bot.Opponent, Cards.FromId(secrets[i]),
                zone: state.Bot.Opponent.SecretZone, id: opponentSecretSlots[i]);
            actor.Card.Power?.Trigger?.Activate(actor);
        }
        return state;
    }
    private static int StableHash(IEnumerable<string> ids, int seed)
    {
        unchecked
        {
            uint hash = 2166136261u ^ (uint)seed;
            foreach (string id in ids) { foreach (char c in id) hash = (hash ^ c) * 16777619u; hash = (hash ^ '|') * 16777619u; }
            return (int)hash;
        }
    }
    private static void SampleOwnSecrets(EngineGame game, int botId, int seed)
    {
        var bot = game.Player1.PlayerId == botId ? game.Player1 : game.Player2;
        var own = bot.SecretZone.Where(card => card.IsSecret).OrderBy(card => card.Id).ToArray();
        if (own.Length == 0) return;
        var pool = SecretPool(bot.BaseClass).ToList();
        var random = new Random(seed);
        foreach (var old in own)
        {
            old.ActivatedTrigger?.Remove(); old.OngoingEffect?.Remove();
            bot.SecretZone.Remove(old);
            var free = pool.Where(id => !bot.SecretZone.Any(c => c.Card.Id == id)).ToList();
            if (free.Count == 0) { bot.SetasideZone.Add(old); Mask(game, bot, old); continue; }
            var fresh = EngineEntity.FromCard(bot, Cards.FromId(free[random.Next(free.Count)]), zone: bot.SecretZone, id: old.Id);
            fresh.Card.Power?.Trigger?.Activate(fresh);
        }
    }
    private static void RedactHiddenTargetEnchantments(EngineGame game)
    {
        var orphans = game.IdEntityDic.Values.OfType<Enchantment>().Where(e => e.Target is EngineUnknown
            || e.Target is IPlayable target && game.IdEntityDic.TryGetValue(target.Id, out var live) && !ReferenceEquals(live, target))
            .ToArray();
        if (orphans.Length == 0) return;
        var replacements = new List<IPlayable>();
        foreach (var actor in orphans)
        {
            actor.ActivatedTrigger?.Remove(); actor.OngoingEffect?.Remove();
            (actor.Target as IPlayable)?.AppliedEnchantments?.Remove(actor);
            var redacted = new EngineUnknown(actor.Controller, new Dictionary<GameTag, int> {
                [GameTag.CONTROLLER] = actor.Controller.PlayerId, [GameTag.ZONE] = (int)EngineZone.REMOVEDFROMGAME
            }, actor.Id);
            game.IdEntityDic[actor.Id] = redacted;
            replacements.Add(redacted);
        }
        foreach (var actor in game.IdEntityDic.Values.OfType<Enchantment>())
            foreach (var replacement in replacements) actor.RedactReferencesForFairView(replacement);
    }
    private static readonly PropertyInfo effectFrames = typeof(SabberStoneCore.Model.Events.ResolutionScheduler).GetProperty("EffectFrames", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo[] assembleBodyFields = new[] { "_number", "_number1" }
        .Select(name => typeof(SabberStoneCore.Model.Effects.EffectFrame).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)).ToArray();
    private static Dictionary<int, int> CanonicalizePrivateActors(EngineGame game, IEnumerable<int> retained)
    {
        var hidden = game.IdEntityDic.Values.OfType<EngineUnknown>().ToArray();
        var privateIds = new HashSet<int>(hidden.Select(c => c.Id));
        var visible = game.IdEntityDic.Values.Where(c => !privateIds.Contains(c.Id)).ToArray();
        var kept = new HashSet<int>(retained);
        var assembleFrames = ((IEnumerable<SabberStoneCore.Model.Effects.EffectFrame>)effectFrames.GetValue(game.Resolution, null))
            .Where(frame => frame.RecipeKey.StartsWith("assemble/", StringComparison.Ordinal)).ToArray();
        kept.UnionWith(assembleFrames.SelectMany(frame => assembleBodyFields.Select(field => (int)field.GetValue(frame))).Where(id => id != 0));
        // Public parents may retain anonymous options after leaving play; their clone links must still resolve.
        kept.UnionWith(visible.SelectMany(c => c.ChooseOnePlayables ?? Array.Empty<IPlayable>())
            .Where(c => c != null).Select(c => c.Id));
        var queuedChoices = new List<Choice>();
        foreach (var player in new[] { game.Player1, game.Player2 })
            for (var choice = player.Choice?.NextChoice; choice != null; choice = choice.NextChoice)
            {
                queuedChoices.Add(choice);
                kept.UnionWith(choice.Choices);
            }
        // Keep only public memberships; private parents' option topology and retired attachments disappear.
        var slots = hidden.Where(c => c.Zone == c.Controller.HandZone || c.Zone == c.Controller.DeckZone
            || kept.Contains(c.Id) || SabberStoneCore.Actions.PayloadRules.IsStored(c)
            || c[SabberStoneCore.Actions.Generic.RetainedSupplier] != 0)
            .OrderBy(c => c.Controller.PlayerId).ThenBy(c => (int)c.Zone.Type).ThenBy(c => c.ZonePosition).ThenBy(c => c.Id).ToArray();
        kept.UnionWith(slots.Select(c => c.Id));
        foreach (var actor in visible.Cast<IEntity>().Concat(new IEntity[] { game, game.Player1, game.Player2 }))
            foreach (var tag in new[] { GameTag.CREATOR, GameTag.PARENT_CARD, GameTag.LAST_CARD_PLAYED, GameTag.LAST_CARD_DRAWN })
                if (privateIds.Contains(actor[tag])) actor.NativeTags.Remove(tag);
        foreach (var enchantment in visible.OfType<Enchantment>())
            if (privateIds.Contains(enchantment.Creator.Id)) enchantmentCreator.SetValue(enchantment, enchantment.Controller.Hero, null);
        foreach (var actor in hidden)
        {
            if (!kept.Contains(actor.Id) && actor.Controller.SetasideZone.Contains(actor)) actor.Controller.SetasideZone.Remove(actor);
            game.IdEntityDic.Remove(actor.Id);
        }
        // EntityList.Remove deliberately leaves its count unchanged; repair the clone's registry before reuse.
        entityCount.SetValue(game.IdEntityDic, visible.Length);
        int next = visible.Select(c => c.Id).DefaultIfEmpty(3).Max() + 1;
        var free = new Queue<int>(Enumerable.Range(4, next - 4).Where(id => !game.IdEntityDic.ContainsKey(id)));
        var ids = new Dictionary<int, int>();
        foreach (var old in slots)
        {
            var tags = new Dictionary<GameTag, int>(old.NativeTags);
            tags.Remove(GameTag.ENTITY_ID);
            var slot = new EngineUnknown(old.Controller, tags, free.Count == 0 ? next++ : free.Dequeue());
            ids.Add(old.Id, slot.Id);
            var members = (IPlayable[])zoneEntities.GetValue(old.Zone);
            int position = Array.IndexOf(members, old);
            if (position >= 0) members[position] = slot;
            slot.Zone = old.Zone;
            slot.ZonePosition = old.ZonePosition;
            game.IdEntityDic[slot.Id] = slot;
        }
        foreach (var choice in queuedChoices)
            choice.Choices = choice.Choices.Select(id => ids.TryGetValue(id, out int mapped) ? mapped : id).ToList();
        foreach (var frame in assembleFrames)
            foreach (var field in assembleBodyFields)
                if (ids.TryGetValue((int)field.GetValue(frame), out int mapped)) field.SetValue(frame, mapped);
        // Ido's retained-card tag points back to the spell's canonical slot.
        foreach (var actor in visible)
            if (ids.TryGetValue(actor[(GameTag)32120], out int mapped)) actor[(GameTag)32120] = mapped;
        foreach (var actor in visible.Where(c => c.ChooseOnePlayables != null))
            for (int i = 0; i < actor.ChooseOnePlayables.Length; i++)
                if (actor.ChooseOnePlayables[i] != null && ids.TryGetValue(actor.ChooseOnePlayables[i].Id, out int mapped))
                    actor.ChooseOnePlayables[i] = game.IdEntityDic[mapped];
        // Engine spans assume dense ids. Fill only gaps implied by public ids, never private allocation history.
        foreach (int id in free) game.IdEntityDic[id] = new EngineUnknown(game.Player1,
            new Dictionary<GameTag, int> { [GameTag.CONTROLLER] = game.Player1.PlayerId,
                [GameTag.ZONE] = (int)EngineZone.REMOVEDFROMGAME }, id);
        game.SetIndexer(next, game.NextOop);
        return ids;
    }
    private sealed class PrivacyDeck : SabberStoneCore.Model.Zones.DeckZone
    {
        internal PrivacyDeck(EngineController player, IEnumerable<IPlayable> roots) : base(player)
        { _entities = roots.ToArray(); _count = _entities.Length; }
    }
#endif
    private static void ReplaceUnknown(EngineGame game, EngineController player, IPlayable old, string cardId)
    {
        var card = Cards.FromId(cardId);
        if (card == null || card.Type == CardType.INVALID) throw new ArgumentException("Unknown sampled card: " + cardId);
        var replacement = EngineEntity.FromCard(player, card, new Dictionary<GameTag, int> {
            [GameTag.CONTROLLER] = player.PlayerId, [GameTag.ZONE] = (int)old.Zone.Type,
            [GameTag.ZONE_POSITION] = old.ZonePosition + 1 }, id: old.Id);
        if (replacement.ChooseOnePlayables != null)
        {
            // Fixed-id construction expects existing branches. Let the engine resolve fresh options, then bind them to the retained id.
            var fresh = EngineEntity.FromCard(player, card);
            replacement.ChooseOnePlayables = fresh.ChooseOnePlayables;
            foreach (var option in replacement.ChooseOnePlayables)
            {
                option[GameTag.CREATOR] = replacement.Id;
                option[GameTag.PARENT_CARD] = replacement.Id;
            }
            // EntityList.Remove leaves a counted hole; keep an inert placeholder for subsequent clones.
            game.IdEntityDic[fresh.Id] = new EngineUnknown(player, new Dictionary<GameTag, int> {
                [GameTag.CONTROLLER] = player.PlayerId, [GameTag.ZONE] = (int)EngineZone.REMOVEDFROMGAME }, fresh.Id);
        }
        if (old.Zone == player.HandZone) player.HandZone.ChangeEntity(old, replacement);
        else if (old.Zone == player.SetasideZone) player.SetasideZone.ChangeEntity(old, replacement);
        else
        {
            // Projected hidden cards have no residence identity; a real deck actor needs a fresh one before it can leave.
            int position = player.DeckZone.ToList().IndexOf(old);
            player.DeckZone.Remove(old);
            player.DeckZone.Add(replacement, position);
        }
        replacement.ZonePosition = old.ZonePosition;
    }
    private FairFacts facts;
    public FairFacts Facts => facts ?? (facts = new FairFacts(game, Bot, opponentSecretCount, ownDeckKnowledge, opaqueQuestRewards, deckRoots));
    public double Evaluate(IStateEvaluator evaluator) => evaluator == null ? Evaluate() : evaluator.Evaluate(Facts);
    // Each worker owns its lazy facts/options, game and RNG; callbacks drain on the pumping thread.
    // Fork on the thread that owns this state: tag arrays are shared copy-on-write, so a write during the fork would reach the fork.
    internal FairBotState ForkForSearch(Action<string> reporter)
    {
#if ENGINE_SOURCE
        var copy = new FairBotState(game.CloneExact(), owner, style, RewindKeep, opponentSecretCount,
            ownDeckKnowledge, materializeOwnDeckTriggers, opponentSecretSlots);
        InheritReporter(copy); copy.FaultReporter = reporter;
        return copy;
#else
        throw new NotSupportedException("Independent search workers require the source engine.");
#endif
    }
    // Sample indices derive distinct seeds from the fair simulation's artificial root seed, never live RNG.
    public FairBotState WithArtificialSeed(int sampleIndex)
    {
#if ENGINE_SOURCE
        var clone = game.CloneExact();
        clone.SetRandomSeed(unchecked((int)game.Seed + sampleIndex * (int)0x9E3779B9u));
        if (materializeOwnDeckTriggers) (clone.Player1.PlayerId == owner ? clone.Player1 : clone.Player2).DeckZone.Shuffle();
        return InheritReporter(new FairBotState(clone, owner, style, RewindKeep, opponentSecretCount, ownDeckKnowledge, materializeOwnDeckTriggers, opponentSecretSlots));
#else
        // The vendor clone retains the authoritative root Seed, so it cannot safely derive fair samples.
        throw new NotSupportedException("Artificial seed samples require the source engine.");
#endif
    }
    public FairBotState Determinize(IReadOnlyList<string> opponentHand, int seed)
    {
        if (opponentHand == null || opponentHand.Count != Bot.Opponent.HandZone.Count(c => c is EngineUnknown))
            throw new ArgumentException("Sample must match the masked opponent hand.", nameof(opponentHand));
#if ENGINE_SOURCE
        var clone = game.CloneExact();
#else
        var clone = game.Clone(logging:false, resetRandomSeed:false, history:false);
#endif
        clone.SetRandomSeed(seed);
        var enemy = (clone.Player1.PlayerId == owner ? clone.Player1 : clone.Player2).Opponent;
        var slots = enemy.HandZone.Where(c => c is EngineUnknown).ToArray();
        for (int i = 0; i < slots.Length; i++) ReplaceUnknown(clone, enemy, slots[i], opponentHand[i]);
        return InheritReporter(new FairBotState(clone, owner, style, RewindKeep, opponentSecretCount, ownDeckKnowledge, materializeOwnDeckTriggers, opponentSecretSlots));
    }
    internal CardClass ReplyClass => Bot.Opponent.BaseClass;
#if ENGINE_SOURCE
    internal TriggerEvidence OwnEvidence => TriggerEvidence.Of(game, Bot);
    internal TriggerEvidence OpponentEvidence => TriggerEvidence.Of(game, Bot.Opponent);
#else
    internal TriggerEvidence OwnEvidence => default(TriggerEvidence);
    internal TriggerEvidence OpponentEvidence => default(TriggerEvidence);
#endif
    internal IReadOnlyList<FairCard> ForReplyHand(FairFacts leaf)
    {
        var known = leaf.Own.Hand.Where(c => !c.Unknown).ToDictionary(c => c.EntityId);
        // Masked snapshots hide ids; join only inside the already-fair simulation.
        return Bot.Opponent.HandZone.Select(c => c is EngineUnknown && known.TryGetValue(c.Id, out var card)
            ? card : new FairCard(c)).ToArray();
    }
    internal bool HasOwnSecrets => Bot.SecretZone.Any(c => c.IsSecret);
    internal bool HasPendingChoice => Bot.Choice != null;
    internal bool IsTerminal => game.State == EngineState.COMPLETE;
    // Only inspect the already-redacted game. Unknown mechanisms/evaluators take the projection path.
    internal bool NeedsEndpointProjection(DeckPlanEvaluator evaluator, bool legacy = false)
    {
        if (HasPendingChoice || IsTerminal || game.CurrentPlayer.PlayerId != owner) return false;
#if ENGINE_SOURCE
        if (!legacy && (evaluator == null || evaluator.BoundaryChangesValue(Facts))) return true;
        if (!game.TaskQueue.IsEmpty || game.Resolution.HasSuspendedEffect || game.OneTurnEffects.Count != 0 || game.OneTurnEffectEnchantments.Count != 0
            || game.GhostlyCards.Count != 0 || game.SummonedMinions.Count != 0 || game.DeadMinions.Count != 0 || game.DeadBoardObjects.Count != 0
            || game.Auras.Count != 0 || SabberStoneCore.Actions.TurnQueueRules.Active(game)
            || SabberStoneCore.Actions.CostRules.HasTurnExpiry(game, game.Turn)) return true;
        // Draw/zone listeners can fire on the opponent's ordinary unknown draw, too.
        foreach (var channel in BoundaryChannels)
            if (game.TriggerManager.Events.GetSubscriptions(channel).Count != 0) return true;
        foreach (var player in new[] { Bot, Bot.Opponent }) {
            if (player.OverloadOwed != 0 || player.OverloadLocked != 0 || player.TemporaryMana != 0
                || player.NumTurnsLeft > 1 || player.TemporusFlag || player.SecretZone.Count != 0
                || player.SecretZone.Auras.Count != 0 || player.BoardZone.HasLocations
                || player.Hero.IsFrozen || player.Hero.ToBeDestroyed || player.Hero.Health <= 0
                || HasBoundaryState(player) || HasBoundaryState(player.Hero) || HasBoundaryState(player.Hero.HeroPower)
                || player.Hero.Weapon != null && (player.Hero.Weapon.ToBeDestroyed || player.Hero.Weapon.Durability <= 0
                    || HasBoundaryState(player.Hero.Weapon))) return true;
            foreach (var minion in player.BoardZone)
                if (minion.IsFrozen || minion.IsDormant || minion.ToBeDestroyed || minion.Health <= 0
                    || HasBoundaryState(minion)) return true;
            foreach (var card in player.HandZone)
                if (HasBoundaryState(card) || card.Card.HandProgress != null || card.Card.Shatter != null
                    || card.Power?.Aura != null) return true;
        }
        // No identity peek: masked opponent deck cards have no effects. Empty deck means fatigue.
        if (Bot.Opponent.DeckZone.Count == 0 || Bot.Opponent.DeckZone.Any(c => !(c is EngineUnknown))) return true;
        return false;
#else
        return true;
#endif
    }
#if ENGINE_SOURCE
    private static readonly string[] BoundaryChannels = {
        nameof(TriggerManager.EndTurnTrigger), nameof(TriggerManager.TurnStartTrigger),
        nameof(TriggerManager.DrawTrigger), nameof(TriggerManager.ZoneTrigger), nameof(TriggerManager.DiscardTrigger)
    };
    private static bool HasBoundaryState(IEntity entity) =>
        (entity.AppliedEnchantments?.Count ?? 0) != 0
        // Custom rule state lives in tags; unfamiliar state is never certified quiet.
        || entity.NativeTags.Any(t => t.Value != 0 && (int)t.Key >= 30000)
        || entity[SabberStoneCore.Actions.TemporaryRules.Temporary] != 0;
#endif
    internal double EndpointFailureValue(double value, DeckPlanEvaluator evaluator)
    {
        // Uncertainty policy: retain current value unless expiry is demonstrable. Unknown triggers,
        // Deathrattles and learned-value interactions are not estimated by this fallback.
        double credit = 0;
#if ENGINE_SOURCE
        foreach (var minion in Bot.BoardZone) {
            var card = Facts.Own.Board.First(c => c.EntityId == minion.Id);
            bool dies = minion.ActivatedTrigger?.UnconditionallyDestroysAtTurnEnd(minion) == true
                || minion.AppliedEnchantments?.Any(e => e.ActivatedTrigger?.UnconditionallyDestroysAtTurnEnd(minion) == true) == true;
            if (dies) {
                credit += evaluator == null ? Math.Max(0, BoardValue(minion)) : evaluator.FriendlyBoardCredit(card);
                continue;
            }
            long attack = 0;
            foreach (var entry in game.OneTurnEffects) {
                if (entry.entityId != minion.Id || entry.effect.Tag != GameTag.ATK) continue;
                if (entry.effect.Operator == SabberStoneCore.Enchants.EffectOperator.ADD) attack += entry.effect.Value;
                else if (entry.effect.Operator == SabberStoneCore.Enchants.EffectOperator.SUB) attack -= entry.effect.Value;
            }
            // Only reversible additive Attack is proven here; retain value for unfamiliar effect operators.
            int expiring = (int)Math.Min(minion.AttackDamage, Math.Max(0, attack));
            credit += evaluator == null ? expiring * 2.2
                - (minion.IsFrozen ? Math.Min(6, minion.AttackDamage) - Math.Min(6, minion.AttackDamage - expiring) : 0)
                : evaluator.FriendlyAttackCredit(card, expiring);
        }
#endif
        return Math.Max(-998000, value - credit);
    }
    internal BotAction ReplyEndTurnAction => BotAction.From(EndTurnTask.Any(Bot));
#if ENGINE_SOURCE
    private static readonly PropertyInfo historyRecorder = typeof(EngineGame).GetProperty("Facts", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo recordResidence = historyRecorder.PropertyType
        .GetMethod("Residence", BindingFlags.Instance | BindingFlags.NonPublic);
#endif
    internal FairBotState ForReplyEndTurn()
    {
        if (game.State == EngineState.COMPLETE || game.CurrentPlayer.PlayerId != owner) return this;
        // Unknown draws prohibit expansion, but ending the turn requires no private card identity.
        if (Bot.Choice != null) throw new InvalidOperationException("Cannot end a pending choice.");
        return Apply(BotAction.From(EndTurnTask.Any(Bot)));
    }
    public FairBotState ForReply(bool sampleSecrets = false, bool keepDeckOrder = false)
    {
        if (game.CurrentPlayer.PlayerId == owner || game.State == EngineState.COMPLETE)
            throw new InvalidOperationException("Reply view requires the opponent turn after END_TURN.");
#if ENGINE_SOURCE
        var clone = game.CloneExact();
#else
        var clone = game.Clone(logging:false, resetRandomSeed:false, history:false);
#endif
        var enemy = (clone.Player1.PlayerId == owner ? clone.Player1 : clone.Player2).Opponent;
        var samples = enemy.HandZone.ToArray();
        if (samples.Any(c => c is EngineUnknown)) throw new InvalidOperationException("Reply hand must be determinized first.");
        // These are already fair samples. Keep their residence: Remove/Add runs quest and reward mechanics.
#if ENGINE_SOURCE
        // Close anonymous history, then give the new viewer fresh belief residences. No zone callbacks run.
        foreach (var card in samples) recordResidence.Invoke(historyRecorder.GetValue(clone, null), new object[] { card, EngineZone.HAND, false });
#endif
        bool ownSecrets = materializeOwnDeckTriggers
            && (clone.Player1.PlayerId == owner ? clone.Player1 : clone.Player2).SecretZone.Any(card => card.IsSecret);
        // The replier's opponent is the bot. Opponent (human) Secrets were removed when the root view was built, so any
        // Secret left on the bot's side is the bot's own: keep it when the root kept own Secrets (golden path).
        var reply = CreateCore(clone, enemy.PlayerId, 0, style,
            new FairOptions { RemoveOpponentSecrets = !ownSecrets, KeepOpponentSecrets = ownSecrets,
                MaterializeOwnDeckTriggers = materializeOwnDeckTriggers, KeepDeckOrder = keepDeckOrder }, true);
        InheritReporter(reply);
#if ENGINE_SOURCE
        foreach (var card in reply.Bot.HandZone) recordResidence.Invoke(historyRecorder.GetValue(reply.game, null), new object[] { card, EngineZone.HAND, true });
        // Policy gets a class belief; the separate outcome replay retains the bot's actual known Secret.
        // Both paths start from the same determinized hand and artificial seed.
        if (ownSecrets && sampleSecrets) SampleOwnSecrets(reply.game, owner, StableHash(samples.Select(c => c.Card.Id), unchecked((int)game.Seed)));
#endif
        return reply;
    }
    private FairBotState WithGame(EngineGame clone)
    {
        if (ownDeckKnowledge.Count == 0) return InheritReporter(new FairBotState(clone, owner, style, null, opponentSecretCount, ownDeckKnowledge, materializeOwnDeckTriggers, opponentSecretSlots));
        var known = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in ownDeckKnowledge) known.Add(pair.Key, pair.Value);
        var next = clone.Player1.PlayerId == owner ? clone.Player1 : clone.Player2;
        foreach (var card in Bot.DeckZone.Where(c => !(c is EngineUnknown)))
            if (known.TryGetValue(card.Card.Id, out int count) && !next.DeckZone.Any(c => c.Id == card.Id))
            { if (count == 1) known.Remove(card.Card.Id); else known[card.Card.Id] = count - 1; }
        return InheritReporter(new FairBotState(clone, owner, style, null, opponentSecretCount,
            new ReadOnlyDictionary<string, int>(new SortedDictionary<string, int>(known, StringComparer.Ordinal)), materializeOwnDeckTriggers, opponentSecretSlots));
    }
    private static void Mask(EngineGame game, EngineController player, IPlayable old)
    {
#if ENGINE_SOURCE
        if (game.IdEntityDic[old.Id] is EngineUnknown) return;
        var children = game.IdEntityDic.Values.Where(actor => actor[GameTag.PARENT_CARD] == old.Id).ToArray();
#endif
        old.ActivatedTrigger?.Remove();
#if ENGINE_SOURCE
        old.OngoingEffect?.Remove();
        // Include retired actors: their cached references still reveal the old definition and counters.
        var attachments = new List<Enchantment>();
        var privateIds = new HashSet<int> { old.Id };
        int before;
        do
        {
            before = privateIds.Count;
            foreach (var actor in game.IdEntityDic.Values.OfType<Enchantment>())
                if (privateIds.Contains(actor.Target.Id) && privateIds.Add(actor.Id)) attachments.Add(actor);
        } while (privateIds.Count != before);
        // Keep the public schedule, but never the opponent's private ignition target.
        var fires = old.Zone == player.HandZone
            ? old.AppliedEnchantments?.Where(a => a.Card.Id == "CATA_EVENT_001e").ToArray() ?? Array.Empty<Enchantment>()
            : Array.Empty<Enchantment>();
        attachments.RemoveAll(a => fires.Contains(a));
        foreach (var actor in attachments)
        {
            if (actor.Target.AppliedEnchantments?.Contains(actor) == true) actor.Remove();
            else { actor.ActivatedTrigger?.Remove(); actor.OngoingEffect?.Remove(); }
        }
#endif
        var tags = new Dictionary<GameTag, int> { [GameTag.CONTROLLER] = player.PlayerId,
            [GameTag.ZONE] = old[GameTag.ZONE] == (int)EngineZone.REMOVEDFROMGAME
                ? (int)EngineZone.REMOVEDFROMGAME : (int)old.Zone.Type,
            [GameTag.ZONE_POSITION] = old.Zone == player.HandZone || old.Zone == player.DeckZone ? old.ZonePosition + 1 : 0 };
#if ENGINE_SOURCE
        // Retain only pair structure; RedactIllusions samples Value after all private identities are erased.
        if (SabberStoneCore.Actions.PayloadRules.IsStored(old)
            && old[SabberStoneCore.Actions.PayloadRules.Key] == SabberStoneCore.Actions.NativeTailRules.IllusionKey)
            foreach (var tag in new[] { SabberStoneCore.Actions.PayloadRules.Owner, SabberStoneCore.Actions.PayloadRules.Key,
                SabberStoneCore.Actions.PayloadRules.Policy, SabberStoneCore.Actions.PayloadRules.Link,
                SabberStoneCore.Actions.PayloadRules.Recipient, SabberStoneCore.Actions.PayloadRules.Stored })
                tags[tag] = old[tag];
        // Preserve anonymous return links and order, never the stored minion's identity or state.
        if (SabberStoneCore.Actions.PayloadRules.IsStored(old)
            && old[SabberStoneCore.Actions.PayloadRules.Key] == SabberStoneCore.Actions.HandTimelineRules.RuniKey)
            foreach (var tag in new[] { SabberStoneCore.Actions.PayloadRules.Owner, SabberStoneCore.Actions.PayloadRules.Key,
                SabberStoneCore.Actions.PayloadRules.Value, SabberStoneCore.Actions.PayloadRules.Stored })
                tags[tag] = old[tag];
#endif
        var hidden = new EngineUnknown(player, tags, old.Id);
#if ENGINE_SOURCE
        // Keep source-bound lifetime without retaining the hidden card's definition or effects.
        if (old[SabberStoneCore.Actions.Generic.RetainedSupplier] != 0)
            hidden[SabberStoneCore.Actions.Generic.RetainedSupplier] = old[SabberStoneCore.Actions.Generic.RetainedSupplier];
#endif
        if (old.Zone == player.DeckZone) player.DeckZone.ChangeEntity(old, hidden);
        else if (old.Zone == player.HandZone) player.HandZone.ChangeEntity(old, hidden);
        else if (player.SetasideZone.Contains(old)) player.SetasideZone.ChangeEntity(old, hidden);
        else hidden.Zone = old.Zone;
        game.IdEntityDic[old.Id] = hidden;
#if ENGINE_SOURCE
        if (fires.Length != 0)
        {
            if (player.AppliedEnchantments == null) player.AppliedEnchantments = new List<Enchantment>();
            foreach (var fire in fires)
            {
                old.AppliedEnchantments.Remove(fire);
                fire.RedactReferencesForFairView(hidden);
                enchantmentTarget.SetValue(fire, player, null);
                fire[GameTag.ATTACHED] = player.Id;
                player.AppliedEnchantments.Add(fire);
            }
        }
        var replacements = new List<IPlayable> { hidden };
        foreach (var actor in attachments)
        {
            // Keep structural identities for any surviving creator links, without retaining an attachment.
            var redacted = new EngineUnknown(actor.Controller, new Dictionary<GameTag, int> {
                [GameTag.CONTROLLER] = actor.Controller.PlayerId, [GameTag.ZONE] = (int)EngineZone.REMOVEDFROMGAME
            }, actor.Id);
            game.IdEntityDic[actor.Id] = redacted;
            replacements.Add(redacted);
        }
        foreach (var actor in game.IdEntityDic.Values.OfType<Enchantment>())
            foreach (var replacement in replacements) actor.RedactReferencesForFairView(replacement);
        // A hidden definition must not survive in its Choose One/Either Side descendants.
        foreach (var child in children) Mask(game, child.Controller, child);
        foreach (var actor in game.IdEntityDic.Values.Where(actor => actor.ChooseOnePlayables != null))
            for (int i = 0; i < actor.ChooseOnePlayables.Length; i++)
                actor.ChooseOnePlayables[i] = game.IdEntityDic[actor.ChooseOnePlayables[i].Id];
#endif
    }
    // shortcut: stop on an unknown draw/choice; replan when revealed. Only the eight curated decks are supported.
    public bool CanExpand => game.State != EngineState.COMPLETE && game.CurrentPlayer.PlayerId == owner
        && !Bot.HandZone.Any(c => c is EngineUnknown)
#if ENGINE_SOURCE
        && !(Bot.Choice?.Choices.Any(id => game.IdEntityDic[id] is EngineUnknown hidden && hidden.Zone != Bot.DeckZone) ?? false)
#endif
        ;
#if ENGINE_SOURCE
    internal bool HasLocationHand => Bot.HandZone.Any(card => card is BoardObject);
    internal bool IsLocation(int id) => game.IdEntityDic.TryGetValue(id, out var entity) && entity is BoardObject;
#endif
    public BotAction[] Actions => RewindKeep != null ? new[] { RewindKeep } : CanExpand ? Bot.Options().Select(BotAction.From).ToArray() : new BotAction[0];
    public BotAction[] OrderedActions => RewindKeep != null ? new[] { RewindKeep } : CanExpand ? Bot.Options().OrderByDescending(LocalBot.Score)
        .ThenBy(t => BotAction.From(t).ToString(), StringComparer.Ordinal).Select(BotAction.From).ToArray() : new BotAction[0];
    public FairBotState Apply(BotAction action)
    {
#if ENGINE_SOURCE
        // CloneForSimulation already retained the candidate and removed its transaction/offer.
        if (RewindKeep != null && RewindKeep.Equals(action)) return WithGame(game.CloneExact());
        // Hand-form upgrades patch their current ChangeEntity packet; capture this action's fair packets, not live history.
        var clone = game.CloneExact(history: true);
#else
        var clone = game.Clone(logging:false, resetRandomSeed:false, history:false);
#endif
        var player = clone.Player1.PlayerId == owner ? clone.Player1 : clone.Player2;
        var task = action.Resolve(player.Options());
        try {
#if ENGINE_SOURCE
        if (task.PlayerTaskType == PlayerTaskType.END_TURN)
            foreach (var fire in player.AppliedEnchantments?.Where(e => e.Card.Id == "CATA_EVENT_001e") ?? Enumerable.Empty<Enchantment>())
            {
                if (fire[SabberStoneCore.Actions.PayloadRules.Generation] == clone.Turn) continue;
                // Resolve no fuel or cancellation from private knowledge; replan after the public result.
                if (fire[GameTag.TAG_SCRIPT_DATA_NUM_2] <= 1)
                    throw new NotSupportedException("Private historical Phoenix ignition target is unavailable in a fair view.");
                fire[SabberStoneCore.Actions.PayloadRules.Generation] = clone.Turn;
                fire[GameTag.TAG_SCRIPT_DATA_NUM_2]--;
            }
        if (task is ChooseTask choice)
        {
            var decision = clone.GetPendingDecisions().Single(d => d.ControllerId == player.Id);
            if (!clone.TryAnswer(new SabberStoneCore.Model.Decisions.DecisionAnswer(decision.Id, decision.ControllerId, choice.Choices), out var error))
                throw new InvalidOperationException(error);
        }
        else
#endif
        if (!clone.Process(task)) throw new InvalidOperationException("Simulation action rejected.");
        }
        catch (Exception error) when (!(error is OutOfMemoryException) && !FairHistory.IsPrivate(error)) { throw new SimulationFailureException(action, error); }
#if ENGINE_SOURCE
        SabberStoneCore.Actions.DiscoverRealRules.RedactSuspiciousOffers(clone);
#endif
        var result = WithGame(clone);
#if ENGINE_SOURCE
        if (result.opaqueQuestRewards.Count != 0) {
            // A prior belongs to this hand residence, even if the actor returns or keeps its ID/definition.
            var retired = new HashSet<int>(clone.HistoryFacts.Query.Events<ResidenceChangedFact>()
                .Where(f => f.Sequence > game.HistoryFacts.RecordCount && f.Zone == EngineZone.HAND && !f.Entered)
                .Select(f => f.EntityId));
            foreach (var packet in clone.PowerHistory.Full) {
                if (packet is SabberStoneCore.Kettle.PowerHistoryChangeEntity changed) retired.Add(changed.Entity.Id);
                if (packet is SabberStoneCore.Kettle.PowerHistoryTagChange tag
                    && (tag.Tag == GameTag.CONTROLLER || tag.Tag == GameTag.ZONE && tag.Value != (int)EngineZone.HAND))
                    retired.Add(tag.EntityId);
            }
            result.opaqueQuestRewards = result.opaqueQuestRewards.Where(p => !retired.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        }
        clone.PowerHistory.Full.Clear();
        clone.PowerHistory.Last.Clear();
#endif
        return result;
    }
    public double Evaluate() => Evaluate(false);
    // zeroDoomed (AiFixes doomed-minions): the bot's own doomed minions earn no board credit. Enemy ones keep theirs here: this
    // legacy evaluator has no swing term, and zeroing them would make Nightmare on an enemy a free removal.
    internal double Evaluate(bool zeroDoomed)
    {
        var bot = Bot; var enemy = bot.Opponent;
        if (bot.PlayState == PlayState.WON) return 1000000;
        if (bot.PlayState == PlayState.LOST) return -1000000;
        if (bot.PlayState == PlayState.TIED) return -100;
        double hpWeight = style == DeckStyle.Control || style == DeckStyle.Frost ? 1.3 : 1.1;
        double score = hpWeight * Life(bot.Hero) - Life(enemy.Hero)
            + bot.BoardZone.Sum(m => zeroDoomed && DoomedMinions.Of(m) ? 0 : BoardValue(m)) - enemy.BoardZone.Sum(m => BoardValue(m))
            + bot.HandZone.Sum(c => c is EngineUnknown ? 5.5 : 2 + c.Cost * 1.8)
            + WeaponValue(bot.Hero.Weapon) - WeaponValue(enemy.Hero.Weapon);
#if ENGINE_SOURCE
        score += bot.BoardZone.GetLocations().Sum(location => location.Durability * 3.0)
            - enemy.BoardZone.GetLocations().Sum(location => location.Durability * 3.0);
#endif
        long incoming = enemy.BoardZone.Where(m => !m.IsFrozen).Sum(m => (long)m.AttackDamage);
        if (incoming >= (long)bot.Hero.Health + bot.Hero.Armor && !bot.BoardZone.Any(m => m.HasTaunt)) score -= 200;
        return score;
    }
    private static double Life(EngineHero hero) => (long)hero.Health + hero.Armor - Math.Max(0, 12L - hero.Health - hero.Armor) * 2;
    private static double WeaponValue(EngineWeapon weapon) => weapon == null ? 0 : (long)weapon.AttackDamage * weapon.Durability * 1.8;
    private static double BoardValue(EngineMinion minion) => 2 + minion.AttackDamage * 2.2 + minion.Health * 1.2
        + minion.Cost * .6 + (minion.HasTaunt ? 2 : 0) + (minion.HasDivineShield ? 4 : 0)
        - (minion.IsFrozen ? Math.Min(6, minion.AttackDamage) : 0);
    public string Describe() => string.Join("|", new[] { game.Player1, game.Player2 }.Select(p =>
        $"{p.PlayerId}:{p.Hero.Health}:{p.Hero.Armor}:{p.RemainingMana}:" +
#if ENGINE_SOURCE
        (p.BoardZone.HasLocations ? string.Join(",", p.BoardZone.Slots.Select(c => c is BoardObject location
            ? $"{c.Id}:{c.Card.Id}:location:{location.Durability}:{c[GameTag.EXHAUSTED]}:{c[GameTag.LOCATION_ACTION_COOLDOWN]}:{location.Location.UsesCompleted}:{location.Location.Stage}:{location.Location.ConditionLocked}"
            : $"{c.Id}:{c.Card.Id}:{((EngineMinion)c).AttackDamage}:{((EngineMinion)c).Health}")) :
#endif
        string.Join(",", p.BoardZone.Select(c => $"{c.Id}:{c.Card.Id}:{c.AttackDamage}:{c.Health}"))
#if ENGINE_SOURCE
        )
#endif
        + ":" +
        string.Join(",", p.HandZone.Select(c => $"{c.Id}:{(c is EngineUnknown ? "UNKNOWN" : c.Card.Id)}")) + ":" +
        string.Join(",", p.DeckZone.Select(c => $"{c.Id}:UNKNOWN"))));
}
