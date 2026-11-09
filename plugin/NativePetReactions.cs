using System.Collections.Generic;
using System.Linq;
using SabberStoneCore.Enums;
using EngineZone = SabberStoneCore.Enums.Zone;
using SabberStoneCore.Kettle;
using SabberStoneCore.Model;
using NativeHistory = PegasusGame.PowerHistoryData;

// Presentation only. Read immutable history, never the already-resolved engine graph.
// Mapping and timing follow retail Power.log evidence (docs/pet-reactions.md); unproven events stay unprojected.
internal sealed class NativePetReactions
{
    internal const int EventTag = 4038;
    // A hand/board play; an automatic EffectPlay (Fyrakk's casts, autoplay) is presentation, never a card play.
    internal static bool IsCardPlay(PowerHistoryBlockStart start) => start.BlockType == BlockType.PLAY
#if ENGINE_SOURCE
        && !(start is PowerHistoryEffectPlayStart)
#endif
        ;
    // Mirrors pinned Assembly-CSharp PetEventType (37.0.0.254477) so engine-only builds need no client alias.
    internal enum PetEvent
    {
        OPPONENT_DAMAGED_5ORLESS = 1,
        OPPONENT_DAMAGED_6PLUS = 2,
        FRIENDLY_DAMAGED_5ORLESS = 3,
        FRIENDLY_DAMAGED_6PLUS = 4,
        OPPONENT_MINIONPLAYED_MANAVALUE_3ORLESS = 5,
        OPPONENT_MINIONPLAYED_MANAVALUE_4TO6 = 6,
        OPPONENT_MINIONPLAYED_MANAVALUE_7PLUS = 7,
        FRIENDLY_MINIONPLAYED_MANAVALUE_3ORLESS = 8,
        FRIENDLY_MINIONPLAYED_MANAVALUE_4TO6 = 9,
        FRIENDLY_MINIONPLAYED_MANAVALUE_7PLUS = 10,
        OPPONENT_TURN_START = 11,
        FRIENDLY_TURN_START = 12,
        OPPONENT_SPELLCAST_MANAVALUE_3ORLESS = 13,
        OPPONENENT_SPELLCAST_MANAVALUE_4PLUS = 14,
        FRIENDLY_SPELLCAST_MANAVALUE_3ORLESS = 15,
        FRIENDLY_SPELLCAST_MANAVALUE_4PLUS = 16,
        OPPONENT_MINIONDESTROYED_MANAVALUE_3ORLESS = 17,
        OPPONENT_MINIONDESTROYED_MANAVALUE_4PLUS = 18,
        FRIENDLY_MINIONDESTROYED_MANAVALUE_3ORLESS = 19,
        FRIENDLY_MINIONDESTROYED_MANAVALUE_4PLUS = 20,
        OPPONENT_WEAPONEQUIPPED = 21,
        FRIENDLY_WEAPONEQUIPPED = 22,
        OPPONENT_ARMORGAINED = 23,
        FRIENDLY_ARMORGAINED = 24,
        OPPONENT_HEALTHRESTORED_5ORLESS = 25,
        OPPONENT_HEALTHRESTORED_6PLUS = 26,
        FRIENDLY_HEALTHRESTORED_5ORLESS = 27,
        FRIENDLY_HEALTHRESTORED_6PLUS = 28,
        OPPONENT_HEROPOWER = 29,
        FRIENDLY_HEROPOWER = 30,
        OPPONENT_LOCATIONUSED = 33,
        FRIENDLY_LOCATIONUSED = 34,
        OPPONENT_CARDDISCOVERED = 35,
        FRIENDLY_CARDDISCOVERED = 36,
        OPPONENT_CARDDISCARDED = 37,
        FRIENDLY_CARDDISCARDED = 38,
        OPPONENT_SECRETTRIGGERED = 39,
        FRIENDLY_SECRETTRIGGERED = 40,
    }
    private readonly Dictionary<int, Dictionary<GameTag, int>> entities = new Dictionary<int, Dictionary<GameTag, int>>();
    private readonly Dictionary<int, int> players = new Dictionary<int, int>();
    private readonly Stack<Frame> blocks = new Stack<Frame>();

    private sealed class Frame
    {
        internal PowerHistoryBlockStart Start;
        internal int PaidCost, Owner;
        internal PetEvent Opposing, Friendly;
        internal void Defer(int owner, PetEvent opposing, PetEvent friendly) { Owner = owner; Opposing = opposing; Friendly = friendly; }
    }
    private readonly HashSet<int> triggeredSecrets = new HashSet<int>();

    private int gameId;
    private bool seeding;
    private int CurrentPlayer() => players.Keys.FirstOrDefault(id => Tag(id, GameTag.CURRENT_PLAYER) == 1) is int id && id != 0 ? Owner(id) : 0;
    private int Tag(int id, GameTag tag) => entities.TryGetValue(id, out var tags) && tags.TryGetValue(tag, out int value) ? value : 0;
    private int Owner(int id) => players.TryGetValue(id, out int player) ? player : Tag(id, GameTag.CONTROLLER);
    private void Snapshot(int id, IDictionary<GameTag, int> tags, string cardId = null)
    {
        entities[id] = new Dictionary<GameTag, int>(tags);
        // Printed SECRET is absent from ordinary full/show snapshots; the frozen identity supplies it.
        if (!string.IsNullOrEmpty(cardId) && !tags.ContainsKey(GameTag.SECRET))
        {
            try { entities[id][GameTag.SECRET] = Cards.FromId(cardId)[GameTag.SECRET]; }
            catch (KeyNotFoundException) { } // Synthetic transport identities need no secret reaction.
        }
    }

    internal Dictionary<int, List<NativeHistory>> Project(List<IPowerHistoryEntry> entries, int human, int pet)
    {
        var result = new Dictionary<int, List<NativeHistory>>();
        for (int index = 0; index < entries.Count; index++)
        {
            void Emit(int owner, PetEvent opposing, PetEvent friendly)
            {
                if (owner == 0) return;
                if (!result.TryGetValue(index, out var changes)) result[index] = changes = new List<NativeHistory>();
                // Card.OnTagChanged ignores zero. Clear after every event so identical consecutive events still fire.
                foreach (int value in new[] { (int)(owner == human ? friendly : opposing), 0 })
                    changes.Add(new NativeHistory { TagChange = new PegasusGame.PowerHistoryTagChange {
                        Entity = pet, Tag = EventTag, Value = value } });
            }
            // Retail: every minion entering play (played or summoned), tiered by paid cost when played from hand.
            void MinionEntered(int id)
            {
                int cost = blocks.FirstOrDefault(block => IsCardPlay(block.Start) && block.Start.Source == id)?.PaidCost ?? -1;
                if (cost < 0) cost = Tag(id, GameTag.COST);
                Emit(Owner(id), cost <= 3 ? PetEvent.OPPONENT_MINIONPLAYED_MANAVALUE_3ORLESS : cost <= 6 ? PetEvent.OPPONENT_MINIONPLAYED_MANAVALUE_4TO6 : PetEvent.OPPONENT_MINIONPLAYED_MANAVALUE_7PLUS,
                    cost <= 3 ? PetEvent.FRIENDLY_MINIONPLAYED_MANAVALUE_3ORLESS : cost <= 6 ? PetEvent.FRIENDLY_MINIONPLAYED_MANAVALUE_4TO6 : PetEvent.FRIENDLY_MINIONPLAYED_MANAVALUE_7PLUS);
            }
            var entry = entries[index];
#if ENGINE_SOURCE
            if (entry is PowerHistoryResetGame reset) entry = reset.CreateGame;
#endif
            if (!(entry is PowerHistoryFullEntity)) seeding = entry is PowerHistoryCreateGame;
            if (entry is PowerHistoryCreateGame create)
            {
                entities.Clear(); players.Clear(); blocks.Clear(); triggeredSecrets.Clear();
                gameId = create.Game.Id;
                Snapshot(create.Game.Id, create.Game.Tags);
                foreach (var player in create.Players) {
                    players[player.PowerEntity.Id] = player.PlayerId;
                    Snapshot(player.PowerEntity.Id, player.PowerEntity.Tags);
                }
            }
            else if (entry is PowerHistoryFullEntity full)
            {
                // Created straight onto the board = summoned; game-start and rewind snapshots only seed state.
                bool summoned = !seeding && !entities.ContainsKey(full.Entity.Id)
                    && full.Entity.Tags.TryGetValue(GameTag.ZONE, out int zone) && zone == (int)EngineZone.PLAY;
                Snapshot(full.Entity.Id, full.Entity.Tags, full.Entity.Name);
                if (summoned && Tag(full.Entity.Id, GameTag.CARDTYPE) == (int)CardType.MINION) MinionEntered(full.Entity.Id);
                continue;
            }
            else if (entry is PowerHistoryShowEntity show)
            {
                // A reveal may carry the move into play; only a known non-play residence counts as an entry.
                bool entering = entities.TryGetValue(show.Entity.Id, out var known) && known.TryGetValue(GameTag.ZONE, out int zone)
                    && zone != (int)EngineZone.PLAY && show.Entity.Tags.TryGetValue(GameTag.ZONE, out int shown) && shown == (int)EngineZone.PLAY;
                Snapshot(show.Entity.Id, show.Entity.Tags, show.Entity.Name);
                if (entering && Tag(show.Entity.Id, GameTag.CARDTYPE) == (int)CardType.MINION) MinionEntered(show.Entity.Id);
            }
            else if (entry is PowerHistoryChangeEntity change)
            {
                if (!entities.TryGetValue(change.Entity.Id, out var tags)) entities[change.Entity.Id] = tags = new Dictionary<GameTag, int>();
                foreach (var tag in change.Entity.Tags) tags[tag.Key] = tag.Value;
                if (!string.IsNullOrEmpty(change.CardId))
                {
                    try { tags[GameTag.SECRET] = Cards.FromId(change.CardId)[GameTag.SECRET]; }
                    catch (KeyNotFoundException) { tags[GameTag.SECRET] = 0; }
                }
            }
            else if (entry is PowerHistoryBlockStart start)
            {
                var frame = new Frame { Start = start, PaidCost = -1 };
                blocks.Push(frame);
                if (IsCardPlay(start))
                {
                    int source = start.Source, owner = Owner(source), type = Tag(source, GameTag.CARDTYPE), cost = Tag(source, GameTag.COST);
                    // Payment annotation survives consumed one-use discounts; zero is a real paid cost.
                    for (int ahead = index + 1; ahead < entries.Count; ahead++) {
                        if (entries[ahead] is PowerHistoryBlockStart || entries[ahead] is PowerHistoryBlockEnd) break;
                        if (entries[ahead] is PowerHistoryTagChange paidTag && paidTag.EntityId == source
                            && paidTag.Tag == GameTag.TAG_LAST_KNOWN_COST_IN_HAND) { cost = paidTag.Value; break; }
                        if (entries[ahead] is PowerHistoryShowEntity frozen && frozen.Entity.Id == source) {
                            if (frozen.Entity.Tags.TryGetValue(GameTag.TAG_LAST_KNOWN_COST_IN_HAND, out int paid)) cost = paid;
                            else if (frozen.Entity.Tags.TryGetValue(GameTag.COST, out int effective)) cost = effective;
                            break;
                        }
                    }
                    frame.PaidCost = cost;
                    // Retail emits these after the effect resolves, at the tail of the PLAY block (Power.log evidence).
                    // Minions react when they enter play, so a PLAY block alone never emits one.
                    if (type == (int)CardType.SPELL)
                        frame.Defer(owner, cost <= 3 ? PetEvent.OPPONENT_SPELLCAST_MANAVALUE_3ORLESS : PetEvent.OPPONENENT_SPELLCAST_MANAVALUE_4PLUS,
                            cost <= 3 ? PetEvent.FRIENDLY_SPELLCAST_MANAVALUE_3ORLESS : PetEvent.FRIENDLY_SPELLCAST_MANAVALUE_4PLUS);
                    else if (type == (int)CardType.HERO_POWER)
                        frame.Defer(owner, PetEvent.OPPONENT_HEROPOWER, PetEvent.FRIENDLY_HEROPOWER);
                    else if (type == 39 && Tag(source, GameTag.ZONE) == (int)EngineZone.PLAY)
                        frame.Defer(owner, PetEvent.OPPONENT_LOCATIONUSED, PetEvent.FRIENDLY_LOCATIONUSED);
                }
                else if (start.BlockType == BlockType.TRIGGER && Tag(start.Source, GameTag.SECRET) == 1
                    && Tag(start.Source, GameTag.ZONE) == (int)EngineZone.SECRET && triggeredSecrets.Add(start.Source))
                    Emit(Owner(start.Source), PetEvent.OPPONENT_SECRETTRIGGERED, PetEvent.FRIENDLY_SECRETTRIGGERED);
            }
            else if (entry is PowerHistoryBlockEnd)
            {
                if (blocks.Count == 0) continue;
                var ended = blocks.Pop();
                // Retail fires right after the effect's own POWER block, before later after-cast triggers and deaths
                // inside the same PLAY block. A PLAY without that POWER block falls back to its own end.
                var play = ended.Start.BlockType == BlockType.POWER && blocks.Count > 0 ? blocks.Peek() : null;
                if (play != null && IsCardPlay(play.Start) && play.Owner != 0 && play.Start.Source == ended.Start.Source)
                { Emit(play.Owner, play.Opposing, play.Friendly); play.Owner = 0; }
                else if (ended.Owner != 0) Emit(ended.Owner, ended.Opposing, ended.Friendly);
            }
            else if (entry is PowerHistoryMetaData meta && meta.Data > 0 && meta.Info != null)
            {
                foreach (int target in meta.Info)
                {
                    // These native events describe damage/healing to a player; minion loss has its own event.
                    if (Tag(target, GameTag.CARDTYPE) != (int)CardType.HERO) continue;
                    if (meta.Type == MetaDataType.DAMAGE)
                        Emit(Owner(target), meta.Data <= 5 ? PetEvent.OPPONENT_DAMAGED_5ORLESS : PetEvent.OPPONENT_DAMAGED_6PLUS,
                            meta.Data <= 5 ? PetEvent.FRIENDLY_DAMAGED_5ORLESS : PetEvent.FRIENDLY_DAMAGED_6PLUS);
                    else if (meta.Type == MetaDataType.HEALING)
                        Emit(Owner(target), meta.Data <= 5 ? PetEvent.OPPONENT_HEALTHRESTORED_5ORLESS : PetEvent.OPPONENT_HEALTHRESTORED_6PLUS,
                            meta.Data <= 5 ? PetEvent.FRIENDLY_HEALTHRESTORED_5ORLESS : PetEvent.FRIENDLY_HEALTHRESTORED_6PLUS);
                }
            }
            else if (entry is PowerHistoryTagChange tag)
            {
                int id = tag.EntityId, before = Tag(id, tag.Tag), owner = Owner(id), type = Tag(id, GameTag.CARDTYPE);
                bool enteredPlay = false;
#if ENGINE_SOURCE
                // Completion pulses may repeat; opening/refreshing a choice never produces one.
                if (tag.Tag == SabberStoneCore.Actions.DiscoverRealRules.CompletionEvent && tag.Value == 1 && players.ContainsKey(id))
                    Emit(owner, PetEvent.OPPONENT_CARDDISCOVERED, PetEvent.FRIENDLY_CARDDISCOVERED);
#endif
                // This explicit resolved marker is emitted even when the same card is discarded again.
                if (tag.Tag == GameTag.LAST_CARD_DISCARDED && tag.Value > 0 && players.ContainsKey(id))
                    Emit(owner, PetEvent.OPPONENT_CARDDISCARDED, PetEvent.FRIENDLY_CARDDISCARDED);
                if (tag.Value != before)
                {
                    // Retail fires turn start at MAIN_START_TRIGGERS, before the turn's draw.
                    if (tag.Tag == GameTag.STEP && tag.Value == (int)Step.MAIN_START_TRIGGERS && id == gameId)
                        Emit(CurrentPlayer(), PetEvent.OPPONENT_TURN_START, PetEvent.FRIENDLY_TURN_START);
                    else if (tag.Tag == GameTag.ARMOR && tag.Value > before && type == (int)CardType.HERO)
                        Emit(owner, PetEvent.OPPONENT_ARMORGAINED, PetEvent.FRIENDLY_ARMORGAINED);
                    else if (tag.Tag == GameTag.ZONE)
                    {
                        if (tag.Value != (int)EngineZone.SECRET) triggeredSecrets.Remove(id);
                        if (tag.Value == (int)EngineZone.PLAY && type == (int)CardType.WEAPON)
                            Emit(owner, PetEvent.OPPONENT_WEAPONEQUIPPED, PetEvent.FRIENDLY_WEAPONEQUIPPED);
                        // Retail sends nothing for placing a location (LOCATIONSUMMONED unused); activation is LOCATIONUSED.
                        else if (tag.Value == (int)EngineZone.PLAY && type == (int)CardType.MINION)
                            enteredPlay = true;
                        else if (before == (int)EngineZone.PLAY && tag.Value == (int)EngineZone.GRAVEYARD && type == (int)CardType.MINION
                            && blocks.Any(block => block.Start.BlockType == BlockType.DEATHS))
                            Emit(owner, Tag(id, GameTag.COST) <= 3 ? PetEvent.OPPONENT_MINIONDESTROYED_MANAVALUE_3ORLESS : PetEvent.OPPONENT_MINIONDESTROYED_MANAVALUE_4PLUS,
                                Tag(id, GameTag.COST) <= 3 ? PetEvent.FRIENDLY_MINIONDESTROYED_MANAVALUE_3ORLESS : PetEvent.FRIENDLY_MINIONDESTROYED_MANAVALUE_4PLUS);
                    }
                }
                if (!entities.TryGetValue(id, out var tags)) entities[id] = tags = new Dictionary<GameTag, int>();
                tags[tag.Tag] = tag.Value;
                if (enteredPlay) MinionEntered(id);
            }
        }
        return result;
    }
}
