#if ENGINE_SOURCE
using EngineGame = SabberStoneCore.Model.Game;
using EngineController = SabberStoneCore.Model.Entities.Controller;
using EngineZone = SabberStoneCore.Enums.Zone;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using SabberStoneCore.Enums;
using SabberStoneCore.Kettle;
using SabberStoneCore.Model;
using SabberStoneCore.Model.Entities;

// Observation memory contains definitions seen by this player, never a hidden-zone snapshot.
internal sealed class OwnDeckLedger
{
    // CloneExact shares immutable fact objects but intentionally drops presentation history.
    private static readonly ConditionalWeakTable<HistoryFact, Dictionary<int, OwnDeckLedger>> observations
        = new ConditionalWeakTable<HistoryFact, Dictionary<int, OwnDeckLedger>>();
    private readonly HistoryFact first, last;
    private readonly int owner;
    private readonly Dictionary<int, string> definitions;
    private readonly Dictionary<long, string> moveDefinitions;
    private readonly HashSet<long> neutralTransforms;
    internal bool Observed(int id) => definitions.ContainsKey(id);
    private OwnDeckLedger(HistoryFact first, HistoryFact last, int owner, Dictionary<int, string> definitions,
        Dictionary<long, string> moveDefinitions, HashSet<long> neutralTransforms)
    { this.first = first; this.last = last; this.owner = owner; this.definitions = definitions; this.moveDefinitions = moveDefinitions; this.neutralTransforms = neutralTransforms; }

    internal static OwnDeckLedger Observe(EngineGame game, EngineController player, OwnDeckLedger previous)
    {
        var facts = game.HistoryFacts.Query.Events<HistoryFact>().ToArray();
        var first = facts.FirstOrDefault();
        var last = facts.LastOrDefault();
        Dictionary<int, OwnDeckLedger> remembered = last == null ? null : observations.GetValue(last, _ => new Dictionary<int, OwnDeckLedger>());
        OwnDeckLedger inherited = null;
        if (remembered != null) lock (remembered) remembered.TryGetValue(player.Id, out inherited);
        if (previous == null || !ReferenceEquals(previous.first, first) || previous.owner != player.Id
            || !facts.Contains(previous.last)) previous = inherited;
        bool same = previous != null && previous.owner == player.Id && ReferenceEquals(previous.first, first);
        var definitions = same ? new Dictionary<int, string>(previous.definitions) : new Dictionary<int, string>();
        if (inherited != null)
            foreach (var pair in inherited.definitions)
                definitions[pair.Key] = pair.Value;
        var frozen = same ? new Dictionary<long, string>(previous.moveDefinitions) : new Dictionary<long, string>();
        if (inherited != null) foreach (var pair in inherited.moveDefinitions) if (!frozen.ContainsKey(pair.Key)) frozen.Add(pair.Key, pair.Value);
        var transforms = same ? new HashSet<long>(previous.neutralTransforms) : new HashSet<long>();
        if (inherited != null) transforms.UnionWith(inherited.neutralTransforms);
        var plays = facts.OfType<CardPlayedFact>().Where(f => f.ControllerId == player.Id).ToLookup(f => f.EntityId);
        var playCounts = new Dictionary<int, int>();
        CardPlayedFact currentPlay = null;
        var deckMoves = facts.OfType<ResidenceChangedFact>().Where(f => f.ControllerId == player.Id && f.Zone == EngineZone.DECK).ToLookup(f => f.EntityId);
        var hand = facts.OfType<HandEnteredFact>().Where(f => f.ControllerId == player.Id).ToLookup(f => f.EntityId);
        void Remember(int id, string definition)
        {
            if (string.IsNullOrEmpty(definition)) return;
            definitions[id] = definition;
        }
        // SHOW precedes HAND for generated cards. Read the frozen definition only once admission makes it visible.
        var snapshots = new Dictionary<int, PowerHistoryEntity>();
        var visibleSnapshots = new HashSet<int>();
        var zones = new Dictionary<int, int>();
        var arrivals = new Dictionary<int, int>();
        var deckArrivals = new Dictionary<int, int>();
        var revealedArrivals = new Dictionary<int, long>();
        var tentativeArrivals = new HashSet<long>();
        void ObserveDeck(int id, bool entered, bool visible)
        {
            int index = deckArrivals.TryGetValue(id, out int count) ? count : 0;
            if (!entered) index = Math.Max(0, index - 1);
            var arrival = deckMoves[id].Where(f => f.Entered).Skip(index).FirstOrDefault();
            if (arrival != null && visible && snapshots.TryGetValue(id, out var snapshot) && !string.IsNullOrEmpty(snapshot.Name))
            {
                if (!entered) revealedArrivals[id] = arrival.Sequence;
                if (!frozen.ContainsKey(arrival.Sequence))
                {
                    frozen.Add(arrival.Sequence, snapshot.Name);
                    if (!entered) tentativeArrivals.Add(arrival.Sequence);
                }
                Remember(id, snapshot.Name);
            }
            if (entered) { deckArrivals[id] = index + 1; visibleSnapshots.Remove(id); }
        }
        void ObserveHand(int id, bool entered)
        {
            if (!snapshots.TryGetValue(id, out var snapshot)) return;
            int index = arrivals.TryGetValue(id, out int count) ? count : 0;
            if (!entered) index = Math.Max(0, index - 1);
            var arrival = hand[id].Skip(index).FirstOrDefault();
            if (arrival == null) return;
            Remember(id, snapshot.Name);
            visibleSnapshots.Add(id);
            if (entered)
            {
                var departure = deckMoves[id].LastOrDefault(f => !f.Entered && f.Sequence < arrival.Sequence);
                if (departure != null && !string.IsNullOrEmpty(snapshot.Name) && !frozen.ContainsKey(departure.Sequence))
                    frozen.Add(departure.Sequence, snapshot.Name);
            }
            if (entered) arrivals[id] = index + 1;
        }
        foreach (var entry in game.PowerHistory?.Full ?? Enumerable.Empty<IPowerHistoryEntry>())
        {
            if (entry is PowerHistoryBlockStart block)
            {
                // An automatic play (Fyrakk's casts, autoplay) is not one of the deck's card plays.
                if (block.BlockType == BlockType.PLAY && !(block is PowerHistoryEffectPlayStart))
                {
                    int index = playCounts.TryGetValue(block.Source, out int count) ? count : 0;
                    currentPlay = plays[block.Source].Skip(index).FirstOrDefault();
                    playCounts[block.Source] = index + 1;
                }
                // Observe activation, not CHANGE_ENTITY targets/definitions or an unsuccessful play.
                if (block.BlockType == BlockType.POWER && currentPlay != null && block.Source == currentPlay.EntityId
                    && currentPlay.DefinitionKnown && currentPlay.DbfId == 116495) transforms.Add(currentPlay.Sequence);
            }
            var snapshot = (entry as PowerHistoryShowEntity)?.Entity ?? (entry as PowerHistoryFullEntity)?.Entity
                ?? (entry as PowerHistoryChangeEntity)?.Entity;
            if (entry is PowerHistoryChangeEntity transformed && snapshots.TryGetValue(transformed.Entity.Id, out var priorSnapshot))
            {
                snapshot = new PowerHistoryEntity { Id = transformed.Entity.Id, Name = transformed.CardId,
                    Tags = new Dictionary<GameTag, int>(priorSnapshot.Tags) };
                foreach (var tag in transformed.Entity.Tags) snapshot.Tags[tag.Key] = tag.Value;
                if (zones.TryGetValue(snapshot.Id, out int currentZone)) snapshot.Tags[GameTag.ZONE] = currentZone;
            }
            if (snapshot != null)
            {
                if (!snapshot.Tags.TryGetValue(GameTag.CONTROLLER, out int controller) || controller != player.PlayerId)
                { snapshots.Remove(snapshot.Id); zones.Remove(snapshot.Id); visibleSnapshots.Remove(snapshot.Id); continue; }
                snapshots[snapshot.Id] = snapshot;
                if (snapshot.Tags.TryGetValue(GameTag.ZONE, out int zone))
                {
                    if (zone == (int)EngineZone.HAND) ObserveHand(snapshot.Id, !zones.TryGetValue(snapshot.Id, out int old) || old != zone);
                    if (zone == (int)EngineZone.DECK)
                        ObserveDeck(snapshot.Id, !zones.TryGetValue(snapshot.Id, out int prior) || prior != zone,
                            entry is PowerHistoryShowEntity && (!snapshot.Tags.TryGetValue(SabberStoneCore.Actions.Generic.PrivateDecisionOfferTag, out int privateOwner) || privateOwner == 0)
                            && (!snapshot.Tags.TryGetValue(SabberStoneCore.Actions.PayloadRules.Stored, out int stored) || stored == 0));
                    zones[snapshot.Id] = zone;
                }
            }
            else if (entry is PowerHistoryTagChange change)
            {
                if (change.Tag == GameTag.CONTROLLER && change.Value != player.PlayerId)
                { snapshots.Remove(change.EntityId); zones.Remove(change.EntityId); visibleSnapshots.Remove(change.EntityId); }
                if (change.Tag == GameTag.ZONE && snapshots.ContainsKey(change.EntityId))
                {
                    if (zones.TryGetValue(change.EntityId, out int origin) && origin == (int)EngineZone.DECK
                        && (change.Value == (int)EngineZone.GRAVEYARD || change.Value == (int)EngineZone.REMOVEDFROMGAME))
                    {
                        var departure = deckMoves[change.EntityId].Where(f => !f.Entered)
                            .Skip(Math.Max(0, (deckArrivals.TryGetValue(change.EntityId, out int count) ? count : 0) - 1)).FirstOrDefault();
                        // The public SHOW immediately before this exit describes the burn, not its old insertion.
                        if (departure != null && revealedArrivals.TryGetValue(change.EntityId, out long arrival))
                        {
                            if (tentativeArrivals.Remove(arrival)) frozen.Remove(arrival);
                            if (!frozen.ContainsKey(departure.Sequence)) frozen.Add(departure.Sequence, snapshots[change.EntityId].Name);
                        }
                    }
                    if (change.Value == (int)EngineZone.HAND && (!zones.TryGetValue(change.EntityId, out int old) || old != change.Value))
                        ObserveHand(change.EntityId, true);
                    if (change.Value == (int)EngineZone.DECK && (!zones.TryGetValue(change.EntityId, out int prior) || prior != change.Value))
                        ObserveDeck(change.EntityId, true, visibleSnapshots.Contains(change.EntityId));
                    zones[change.EntityId] = change.Value;
                }
            }
        }
        foreach (var fact in facts.OfType<HistoryCardFact>().Where(f => f.ControllerId == player.Id && f.DefinitionKnown))
            Remember(fact.EntityId, Cards.FromAssetId(fact.DbfId).Id);
        // Snapshot-only callers can recover visible cards; absent observations stay unknown.
        foreach (var actor in player.HandZone.Concat(player.BoardZone.Slots).Concat(player.GraveyardZone))
            Remember(actor.Id, actor.Card.Id);
        foreach (int id in player.Choice?.Choices ?? new List<int>())
        {
            var offer = game.IdEntityDic[id];
            Remember(id, offer.Card.Id);
            int bound = offer[SabberStoneCore.Actions.DiscoverRealRules.BoundEntity];
            if (bound != 0) Remember(bound, offer.Card.Id);
        }
        // Offers can be shuffled without a HAND packet. Freeze the last observation before that move,
        // so a later draw/transform cannot rewrite the insertion either.
        if (same && previous.last != null)
        {
            var residences = facts.OfType<ResidenceChangedFact>().ToLookup(f => f.EntityId);
            foreach (var move in deckMoves.SelectMany(group => group).Where(f => f.Entered && f.Sequence > previous.last.Sequence))
            {
                var origin = residences[move.EntityId].LastOrDefault(f => f.Sequence < move.Sequence);
                if (!frozen.ContainsKey(move.Sequence) && (origin == null || origin.ControllerId == player.Id)
                    && previous.definitions.TryGetValue(move.EntityId, out string definition)) frozen.Add(move.Sequence, definition);
            }
        }
        var result = new OwnDeckLedger(first, last, player.Id, definitions, frozen, transforms);
        if (remembered != null) lock (remembered) remembered[player.Id] = result;
        return result;
    }

    // originals (optional) receives, per sampled definition, how many sampled copies are bag slots that started
    // in this deck: decklist slots, and observed returns of the owner's own original cards. Flags ride along with
    // definitions, so the sampled multiset and its RNG draws are unchanged.
    internal Dictionary<string, int> Sample(EngineGame game, EngineController player, int seed, List<string> retained = null,
        Dictionary<string, int> originals = null)
    {
        var bag = player.DeckCards.Select(c => new Slot(c.Id, true)).ToList();
        var facts = game.HistoryFacts.Query.Events<HistoryFact>().ToArray();
        var moves = facts.OfType<ResidenceChangedFact>().Where(f => f.ControllerId == player.Id && f.Zone == EngineZone.DECK).ToArray();
        var residences = facts.OfType<ResidenceChangedFact>().ToLookup(f => f.EntityId);
        var publicCards = facts.OfType<HistoryCardFact>().Where(f => f.ControllerId == player.Id && f.DefinitionKnown).ToLookup(f => f.EntityId);
        var publicGraveyard = new HashSet<int>(player.GraveyardZone.Select(c => c.Id));
        // Hogger's declared copies are known from the decklist, including originals already drawn at game start.
        var copies = player.DeckCards.Where(c => c.Id == "JAIL_384").SelectMany(_ =>
            player.DeckCards.Where(c => c.Id != "JAIL_384" && c[GameTag.ELITE] == 1)).Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var initial = new HashSet<long>(moves.Where(f => f.Entered && f.Turn == 0).Take(player.DeckCards.Count).Select(f => f.Sequence));
        var additions = moves.Where(f => f.Entered && !initial.Contains(f.Sequence)).ToArray();
        long openingEnd = facts.OfType<TurnStartedFact>().Select(f => f.Sequence).DefaultIfEmpty(long.MaxValue).First();
        var startCopies = new HashSet<long>(additions.Where(f => f.Sequence < openingEnd).Reverse().Take(copies.Length).Select(f => f.Sequence));
        int copy = 0;
        var random = new Random(seed);
        const string druidSlot = "?druid";
        IReadOnlyList<SabberStoneCore.Model.Card> druidPool = null;
        string SampleDruid()
        {
            druidPool = druidPool ?? OfflineCardPools.Candidates(game, player.BaseClass,
                new OfflinePoolFilter(classMode: PoolClassMode.Exact, exactClass: CardClass.DRUID), 116495);
            return druidPool.Count == 0 ? null : druidPool[random.Next(druidPool.Count)].Id;
        }
        // The owner sees whether its own observed cards were created; unobserved cards never read an origin.
        bool Original(int entity) => game.HistoryFacts.Query.KnownOrigin(entity, player.Id) == true;
        bool Remove(string definition, bool original)
        {
            int index = bag.FindIndex(s => s.Definition == definition && s.Original == original);
            if (index < 0) index = bag.FindIndex(s => s.Definition == definition);
            if (index < 0) return false;
            bag.RemoveAt(index); return true;
        }
        Slot RemoveUnknown()
        {
            if (bag.Count == 0) return new Slot(null, false);
            bag.Sort(Slot.Order);
            int index = random.Next(bag.Count);
            var slot = bag[index]; bag.RemoveAt(index);
            return slot.Definition == druidSlot ? new Slot(SampleDruid(), slot.Original) : slot;
        }
        var transforms = facts.OfType<CardPlayedFact>().Where(f => neutralTransforms.Contains(f.Sequence));
        foreach (var operation in moves.Where(f => !initial.Contains(f.Sequence)).Cast<HistoryFact>().Concat(transforms).OrderBy(f => f.Sequence))
        {
            if (operation is CardPlayedFact)
            {
                // Envoy's public battlecry changes neutral slots, but never reveals their replacement identities.
                for (int i = 0; i < bag.Count; i++)
                    if (bag[i].Definition != null && bag[i].Definition != druidSlot && Cards.FromId(bag[i].Definition).Class == CardClass.NEUTRAL)
                        bag[i] = new Slot(druidSlot, bag[i].Original);
                continue;
            }
            var move = (ResidenceChangedFact)operation;
            if (move.Entered)
            {
                if (startCopies.Contains(move.Sequence)) bag.Add(new Slot(copies[copy++], Original(move.EntityId)));
                else
                {
                    var previousResidence = residences[move.EntityId].LastOrDefault(f => f.Sequence < move.Sequence);
                    var previousAppearance = publicCards[move.EntityId].LastOrDefault(f => f.Sequence < move.Sequence);
                    bool hiddenSource = previousResidence != null && previousResidence.ControllerId != player.Id
                        && (previousAppearance == null || previousAppearance.Sequence < previousResidence.Sequence);
                    string definition = moveDefinitions.TryGetValue(move.Sequence, out string addition) ? addition
                        : !hiddenSource && previousAppearance != null ? Cards.FromAssetId(previousAppearance.DbfId).Id : null;
                    bag.Add(new Slot(definition, definition != null && Original(move.EntityId)));
                }
                continue;
            }
            var destination = residences[move.EntityId].FirstOrDefault(f => f.Sequence > move.Sequence);
            var appearance = publicCards[move.EntityId].FirstOrDefault(f => f.Sequence > move.Sequence);
            // A later return from an opponent's private zone cannot identify an earlier secret removal.
            bool observed = destination != null && destination.Entered && destination.Zone == EngineZone.HAND && destination.ControllerId == player.Id
                || appearance != null && (destination == null || appearance.Sequence < destination.Sequence)
                || destination == null && publicGraveyard.Contains(move.EntityId);
            string departure = moveDefinitions.TryGetValue(move.Sequence, out string frozenDeparture) ? frozenDeparture
                : appearance != null && (destination == null || appearance.Sequence < destination.Sequence) ? Cards.FromAssetId(appearance.DbfId).Id : null;
            bool departed = departure != null && Original(move.EntityId);
            if ((observed || moveDefinitions.ContainsKey(move.Sequence)) && departure != null
                && (Remove(departure, departed) || bag.Any(s => s.Definition == druidSlot)
                    && OfflineCardPools.Candidates(game, player.BaseClass,
                        new OfflinePoolFilter(classMode: PoolClassMode.Exact, exactClass: CardClass.DRUID), 116495).Any(c => c.Id == departure)
                    && Remove(druidSlot, departed) || Remove(null, departed))) continue;
            var removed = RemoveUnknown();
            if (retained != null && game.IdEntityDic.TryGetValue(move.EntityId, out var actor)
                && actor.Zone == player.SetasideZone && SabberStoneCore.Actions.PayloadRules.IsStored(actor)) retained.Add(removed.Definition);
        }
        // Counts remain observable even when a restored snapshot has no definition witness.
        while (bag.Count > player.DeckZone.Count) RemoveUnknown();
        var sampled = bag.Select(s => s.Definition == druidSlot ? new Slot(SampleDruid(), s.Original) : s).Where(s => s.Definition != null).ToArray();
        if (originals != null)
            foreach (var group in sampled.Where(s => s.Original).GroupBy(s => s.Definition, StringComparer.Ordinal))
                originals[group.Key] = group.Count();
        return sampled.GroupBy(s => s.Definition, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
    }
    private readonly struct Slot
    {
        internal readonly string Definition; internal readonly bool Original;
        internal Slot(string definition, bool original) { Definition = definition; Original = original; }
        // Definitions keep the former ordinal order; the origin flag only orders equal definitions.
        internal static readonly Comparison<Slot> Order = (a, b) => {
            int order = StringComparer.Ordinal.Compare(a.Definition, b.Definition);
            return order != 0 ? order : a.Original.CompareTo(b.Original);
        };
    }
}
#endif
