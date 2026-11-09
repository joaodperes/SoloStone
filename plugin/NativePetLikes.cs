using System;
using System.Collections.Generic;
using System.Linq;
using SabberStoneCore.Enums;
using EngineZone = SabberStoneCore.Enums.Zone;
using SabberStoneCore.Kettle;
using SabberStoneCore.Model;
using NativeHistory = PegasusGame.PowerHistoryData;

// Pet XP "likes" (docs/design/pet-xp.md). Retail pets earn XP when their owner does things the pet likes; each like fires at most
// once per game and bumps a counter tag on the pet entity, which the pinned client turns into its own XP effect. This evaluator
// reads the same immutable engine power-history stream as NativePetReactions, from the pet owner's perspective, and never touches
// the engine, the profile or any ownership. The policy (which counter, the display gate) is PetXpPolicy; the per-family like
// lists are PetXpPolicy.LikesFor. Settlement reads the banked totals (Small/Medium/Large), not what was shown.
internal sealed class NativePetLikes
{
    internal const int SmallTag = 4076, MediumTag = 4077, LargeTag = 4078;

    private readonly PetXpPolicy policy;
    private readonly HashSet<string> likes;
    private readonly HashSet<string> fired = new HashSet<string>(StringComparer.Ordinal);
    private readonly int[] banked = new int[3], shown = new int[3];
    private bool gatePassed;

    // Same graph tracking as NativePetReactions (kept apart so each stays a pure function of the stream).
    private readonly Dictionary<int, Dictionary<GameTag, int>> entities = new Dictionary<int, Dictionary<GameTag, int>>();
    private readonly Dictionary<int, int> players = new Dictionary<int, int>();
    private readonly Dictionary<int, string> cardIds = new Dictionary<int, string>();
    private readonly Stack<Frame> blocks = new Stack<Frame>();
    private int gameId, turn, cardsThisTurn, spellsThisTurn, minionsThisTurn, diedThisTurn;
    private bool seeding;

    private sealed class Frame { internal PowerHistoryBlockStart Start; internal bool AwaitingCard, Activation; internal int Cost; }

    // Test seam: runs at the start of every history entry, so a test can prove a fault never breaks the history stream.
    internal static Action FaultHook;

    internal NativePetLikes(int nativeFamilyId, PetXpPolicy policy)
    {
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
        likes = new HashSet<string>(PetXpPolicy.LikesFor(nativeFamilyId), StringComparer.Ordinal);
        gatePassed = policy.DisplayGate <= 0;
    }

    internal int Small => banked[0];
    internal int Medium => banked[1];
    internal int Large => banked[2];
    internal bool GatePassed => gatePassed;
    internal IReadOnlyCollection<string> Fired => fired;
    // The counters the client has been shown (the reinjected pet entity must carry exactly these).
    internal IEnumerable<KeyValuePair<int, int>> ShownTags()
    {
        for (int counter = 0; counter < 3; counter++)
            if (shown[counter] > 0) yield return new KeyValuePair<int, int>(TagOf(counter), shown[counter]);
    }
    private static int TagOf(int counter) => counter == 0 ? SmallTag : counter == 1 ? MediumTag : LargeTag;

    // A fed treat: TreatTicks ticks (two under preset B), banked or shown by the same display gate as a like. Returns the tag changes to send now.
    internal List<NativeHistory> Treat(int pet)
    {
        var output = new List<NativeHistory>();
        Tick((int)policy.Treat, policy.TreatTicks, output, pet);
        return output;
    }

    // One event adds `ticks` counter ticks (preset B: two MEDIUM ticks = 20 XP) and shows them as ONE absolute counter update.
    private void Tick(int counter, int ticks, List<NativeHistory> output, int pet)
    {
        banked[counter] += ticks;
        if (!gatePassed) return;
        Show(counter, output, pet);
    }

    private void Show(int counter, List<NativeHistory> output, int pet)
    {
        if (shown[counter] == banked[counter]) return;
        shown[counter] = banked[counter];
        output.Add(new NativeHistory { TagChange = new PegasusGame.PowerHistoryTagChange { Entity = pet, Tag = TagOf(counter), Value = shown[counter] } });
    }

    private int Tag(int id, GameTag tag) => entities.TryGetValue(id, out var tags) && tags.TryGetValue(tag, out int value) ? value : 0;
    private int Owner(int id) => players.TryGetValue(id, out int player) ? player : Tag(id, GameTag.CONTROLLER);
    private int CurrentPlayer() => players.Keys.FirstOrDefault(id => Tag(id, GameTag.CURRENT_PLAYER) == 1) is int id && id != 0 ? Owner(id) : 0;

    // Printed facts about a card, read once per card id from the engine's card data.
    private sealed class Info
    {
        internal string Name = "";
        internal CardType Type;
        internal CardClass Class;
        internal int Cost, Atk, School;
        internal bool Elite, Typeless, Dragon, Beast, Pirate, Undead, Rush, Taunt, Deathrattle, OldGod, Herald, Colossal;
    }
    private static readonly Dictionary<string, Info> InfoCache = new Dictionary<string, Info>(StringComparer.Ordinal);
    private static readonly Info Unknown = new Info();
    // The four Old Gods as engine cards: the BE ones carry the pool keyword 4754; the retail ones (and the Wild event copy) by id,
    // never by name prefix ("C'Thun's Chosen" and "N'Zoth's First Mate" are not Old Gods).
    private static readonly HashSet<string> OldGodIds = new HashSet<string>(StringComparer.Ordinal) {
        "OG_279", "OG_280", "OG_133", "WE1_035", "OG_042", "OG_134" };
    private static Info InfoOf(string cardId)
    {
        if (string.IsNullOrEmpty(cardId)) return Unknown;
        lock (InfoCache)
        {
            if (InfoCache.TryGetValue(cardId, out var known)) return known;
            Info info = Unknown;
            try
            {
                var card = Cards.FromId(cardId);
                if (card != null)
                {
                    string name = card.Name ?? "";
                    info = new Info {
                        Name = name, Type = card.Type, Class = card.Class, Elite = card[GameTag.ELITE] == 1, Cost = card.Cost, Atk = card.ATK,
                        School = card[(GameTag)1635], Rush = card.Rush, Taunt = card.Taunt, Deathrattle = card.Deathrattle,
                        Herald = card[(GameTag)4309] > 0,
                        // BE Old Gods carry the pool keyword 4754; retail ones are recognised by name.
                        OldGod = card.Type == CardType.MINION && (card[(GameTag)4754] > 0 || OldGodIds.Contains(cardId))
                    };
#if ENGINE_SOURCE
                    info.Typeless = card.Races.Count == 0;
                    info.Dragon = card.IsRace(Race.DRAGON); info.Beast = card.IsRace(Race.BEAST); info.Pirate = card.IsRace(Race.PIRATE);
                    info.Undead = card.IsRace(Race.UNDEAD);
                    info.Colossal = card.ColossalAppendageIds?.Count > 0;
#else
                    int race = card[GameTag.CARDRACE];
                    info.Typeless = race == 0;
                    info.Dragon = race == (int)Race.DRAGON || race == (int)Race.ALL; info.Beast = race == (int)Race.BEAST || race == (int)Race.ALL;
                    info.Pirate = race == (int)Race.PIRATE || race == (int)Race.ALL; info.Undead = race == (int)Race.UNDEAD || race == (int)Race.ALL;
#endif
                }
            }
            catch (KeyNotFoundException) { }
            InfoCache[cardId] = info;
            return info;
        }
    }

    private void Snapshot(int id, IDictionary<GameTag, int> tags, string cardId = null)
    {
        entities[id] = new Dictionary<GameTag, int>(tags);
        if (!string.IsNullOrEmpty(cardId)) cardIds[id] = cardId;
    }

    private int MyBoardMinions(int human) => entities.Count(pair => pair.Value.TryGetValue(GameTag.ZONE, out int zone) && zone == (int)EngineZone.PLAY
        && pair.Value.TryGetValue(GameTag.CARDTYPE, out int type) && type == (int)CardType.MINION
        && pair.Value.TryGetValue(GameTag.CONTROLLER, out int controller) && controller == human);

    // Tag changes to send, by the index of the history entry that caused them (the same shape as NativePetReactions.Project).
    // `human` is the pet owner's player id. Likes outside the family's list never fire.
    internal Dictionary<int, List<NativeHistory>> Project(List<IPowerHistoryEntry> entries, int human, int pet)
    {
        var result = new Dictionary<int, List<NativeHistory>>();
        for (int index = 0; index < entries.Count; index++)
        {
            FaultHook?.Invoke();
            var output = new List<NativeHistory>();
            void Like(string id)
            {
                if (likes.Contains(id) && fired.Add(id)) Tick((int)policy.Like, policy.LikeTicks, output, pet);
            }
            void SetTurn(int value)
            {
                if (value != turn) { turn = value; cardsThisTurn = spellsThisTurn = minionsThisTurn = diedThisTurn = 0; }
                if (!gatePassed && turn >= policy.DisplayGate)
                {
                    // Release everything banked before the gate as one update per counter.
                    gatePassed = true;
                    for (int counter = 0; counter < 3; counter++) Show(counter, output, pet);
                }
            }
            void MinionEntered(int id)
            {
                if (Owner(id) != human) return;
                var info = InfoOf(cardIds.TryGetValue(id, out string card) ? card : null);
                if (Tag(id, GameTag.TAUNT) == 1 || info.Taunt && !entities[id].ContainsKey(GameTag.TAUNT)) Like("summon-taunt");
                if ((entities[id].ContainsKey(GameTag.ATK) ? Tag(id, GameTag.ATK) : info.Atk) >= 12) Like("summon-12-attack");
                if (Tag(id, GameTag.DEATHRATTLE) == 1 || info.Deathrattle && !entities[id].ContainsKey(GameTag.DEATHRATTLE)) Like("deathrattle-summon");
                if (info.OldGod) Like("old-god-summon");
                if (MyBoardMinions(human) >= 7) Like("board-full");
            }
            void PlayLikes(int source, int cost, bool activation)
            {
                var info = InfoOf(cardIds.TryGetValue(source, out string card) ? card : null);
                int type = Tag(source, GameTag.CARDTYPE) != 0 ? Tag(source, GameTag.CARDTYPE) : (int)info.Type;
                if (type == (int)CardType.HERO_POWER) Like("hero-power");
                else if (activation)
                {
                    // A location or Rift activation opens a PLAY block for a board object, not a card from hand: no like.
                }
                else
                {
                    if (++cardsThisTurn >= 4) Like("cards-4-turn");
                    if (cost >= 10) Like("cost-10-card");
                    if (Tag(source, (GameTag)3880) > 0) Like("dark-gift-card");
                    if (info.Herald) Like("herald");
                    if (type == (int)CardType.SPELL)
                    {
                        if (++spellsThisTurn >= 3) Like("spells-3-turn");
                        if (info.School == 4) Like("nature-spell");
                        else if (info.School == 2) Like("fire-spell");
                        else if (info.School == 3) Like("frost-spell");
                        else if (info.School == 6) Like("shadow-spell");
                        if (info.School == 0) Like("untyped-spell");
                        if (info.Class == CardClass.DEATHKNIGHT) Like("dk-spell");
                    }
                    else if (type == (int)CardType.MINION)
                    {
                        if (++minionsThisTurn >= 3) Like("minions-3-turn");
                        if (info.Typeless) Like("untyped-minion");
                        if (entities[source].TryGetValue(GameTag.ELITE, out int elite) ? elite == 1 : info.Elite) Like("legendary-minion");
                        if (info.Pirate) Like("pirate");
                        if (info.Dragon && cost >= 5) Like("dragon-5plus");
                        if (info.Beast && cost >= 4) Like("beast-4plus");
                        if (info.Beast && cost >= 6) Like("beast-6plus");
                        if (info.Beast && cost >= 4 && cost <= 5) Like("beast-4to5");
                        if (info.Rush || Tag(source, GameTag.RUSH) == 1) Like("rush-minion");
                        if (info.Undead && cost <= 3) Like("undead-3minus");
                        if (info.Undead || info.Class == CardClass.DEATHKNIGHT) Like("undead-or-dk-minion");
                        if (info.Colossal) Like("colossal");
                        if (info.Name.StartsWith("Ysera", StringComparison.Ordinal)) Like("ysera-minion");
                        if (info.Name.StartsWith("Q'onzu", StringComparison.Ordinal)) Like("qonzu-minion");
                        if (info.Name.StartsWith("Kel'Thuzad", StringComparison.Ordinal)) Like("kelthuzad");
                    }
                }
            }
            var entry = entries[index];
#if ENGINE_SOURCE
            if (entry is PowerHistoryResetGame reset) entry = reset.CreateGame;
#endif
            if (!(entry is PowerHistoryFullEntity)) seeding = entry is PowerHistoryCreateGame;
            if (entry is PowerHistoryCreateGame create)
            {
                // A rewind reset rebuilds the graph but never un-fires a like or un-banks XP.
                entities.Clear(); players.Clear(); cardIds.Clear(); blocks.Clear();
                // Per-turn tallies restart with the rebuilt graph: a rewind replays plays of the same turn, and an undercount is
                // the safe side (fired likes and banked XP are never taken back).
                cardsThisTurn = spellsThisTurn = minionsThisTurn = diedThisTurn = 0;
                gameId = create.Game.Id;
                Snapshot(create.Game.Id, create.Game.Tags);
                foreach (var player in create.Players)
                {
                    players[player.PowerEntity.Id] = player.PlayerId;
                    Snapshot(player.PowerEntity.Id, player.PowerEntity.Tags);
                }
                if (create.Game.Tags.TryGetValue(GameTag.TURN, out int startTurn)) SetTurn(startTurn);
            }
            else if (entry is PowerHistoryFullEntity full)
            {
                bool summoned = !seeding && !entities.ContainsKey(full.Entity.Id)
                    && full.Entity.Tags.TryGetValue(GameTag.ZONE, out int zone) && zone == (int)EngineZone.PLAY;
                Snapshot(full.Entity.Id, full.Entity.Tags, full.Entity.Name);
                if (summoned && Tag(full.Entity.Id, GameTag.CARDTYPE) == (int)CardType.MINION) MinionEntered(full.Entity.Id);
            }
            else if (entry is PowerHistoryShowEntity show)
            {
                bool entering = entities.TryGetValue(show.Entity.Id, out var known) && known.TryGetValue(GameTag.ZONE, out int zone)
                    && zone != (int)EngineZone.PLAY && show.Entity.Tags.TryGetValue(GameTag.ZONE, out int shownZone) && shownZone == (int)EngineZone.PLAY;
                Snapshot(show.Entity.Id, show.Entity.Tags, show.Entity.Name);
                var awaiting = blocks.FirstOrDefault(block => block.AwaitingCard && NativePetReactions.IsCardPlay(block.Start) && block.Start.Source == show.Entity.Id);
                if (awaiting != null && !string.IsNullOrEmpty(show.Entity.Name)) { awaiting.AwaitingCard = false; PlayLikes(show.Entity.Id, awaiting.Cost, awaiting.Activation); }
                if (entering && Tag(show.Entity.Id, GameTag.CARDTYPE) == (int)CardType.MINION) MinionEntered(show.Entity.Id);
            }
            else if (entry is PowerHistoryChangeEntity change)
            {
                if (!entities.TryGetValue(change.Entity.Id, out var tags)) entities[change.Entity.Id] = tags = new Dictionary<GameTag, int>();
                foreach (var tag in change.Entity.Tags) tags[tag.Key] = tag.Value;
                if (!string.IsNullOrEmpty(change.CardId)) cardIds[change.Entity.Id] = change.CardId;
            }
            else if (entry is PowerHistoryBlockStart start)
            {
                blocks.Push(new Frame { Start = start });
                int source = start.Source;
                if (NativePetReactions.IsCardPlay(start) && Owner(source) == human)
                {
                    int cost = PaidCost(entries, index, source, Tag(source, GameTag.COST));
                    // Cards drawn face-down are revealed by a SHOW_ENTITY inside the PLAY block: wait for the identity.
                    // Judged before any reveal: a board object (location, Rift minion) is already in play when its block opens.
                    bool activation = Tag(source, GameTag.ZONE) == (int)EngineZone.PLAY;
                    if (cardIds.ContainsKey(source)) PlayLikes(source, cost, activation);
                    else { blocks.Peek().AwaitingCard = true; blocks.Peek().Cost = cost; blocks.Peek().Activation = activation; }
                }
                else if (start.BlockType == BlockType.ATTACK && Owner(source) == human && Tag(source, GameTag.CARDTYPE) == (int)CardType.MINION)
                    Like("minion-attacks");
            }
            else if (entry is PowerHistoryBlockEnd)
            {
                if (blocks.Count > 0) blocks.Pop();
            }
            else if (entry is PowerHistoryMetaData meta && meta.Data > 0 && meta.Info != null)
            {
                bool myTurn = CurrentPlayer() == human;
                foreach (int target in meta.Info)
                {
                    int type = Tag(target, GameTag.CARDTYPE), owner = Owner(target);
                    bool hero = type == (int)CardType.HERO, character = hero || type == (int)CardType.MINION;
                    if (meta.Type == MetaDataType.HEALING && hero && owner == human) Like("heal-hero");
                    else if (meta.Type == MetaDataType.DAMAGE && character)
                    {
                        if (hero && owner == human && myTurn) Like("hero-hurt-own-turn");
                        else if (owner != human && owner != 0 && myTurn)
                        {
                            if (hero) Like("damage-enemy-hero");
                            if (meta.Data == 1) Like("enemy-hit-for-1");
                            if (meta.Data >= 10) Like("damage-10plus");
                        }
                    }
                }
            }
            else if (entry is PowerHistoryTagChange tag)
            {
                int id = tag.EntityId, before = Tag(id, tag.Tag), owner = Owner(id), type = Tag(id, GameTag.CARDTYPE);
                bool enteredPlay = false;
#if ENGINE_SOURCE
                if (tag.Tag == SabberStoneCore.Actions.DiscoverRealRules.CompletionEvent && tag.Value == 1 && players.ContainsKey(id) && owner == human)
                    Like("discover");
#endif
                if (tag.Tag == GameTag.LAST_CARD_DISCARDED && tag.Value > 0 && players.ContainsKey(id) && owner == human) Like("discard");
                if (tag.Value != before)
                {
                    if (tag.Tag == GameTag.TURN && id == gameId) { entities[id][GameTag.TURN] = tag.Value; SetTurn(tag.Value); }
                    else if (tag.Tag == GameTag.RESOURCES && players.ContainsKey(id) && owner == human && tag.Value >= 10) Like("mana-10");
                    else if (tag.Tag == (GameTag)2186 && players.ContainsKey(id) && owner == human && tag.Value > before) Like("corpse-gained");
                    else if (tag.Tag == GameTag.ARMOR && tag.Value > before && type == (int)CardType.HERO && owner == human) Like("armor-gain");
                    else if (tag.Tag == GameTag.FROZEN && tag.Value == 1 && before == 0 && owner != human && owner != 0
                        && (type == (int)CardType.MINION || type == (int)CardType.HERO)) Like("freeze-enemy");
                    else if (tag.Tag == GameTag.TO_BE_DESTROYED && tag.Value == 1 && type == (int)CardType.MINION && owner != human && owner != 0
                        && blocks.Any(block => block.Start.BlockType == BlockType.ATTACK && Owner(block.Start.Source) == human))
                        Like("attack-kill");
                    else if (tag.Tag == GameTag.ZONE)
                    {
                        if (tag.Value == (int)EngineZone.PLAY && type == (int)CardType.MINION) enteredPlay = true;
                        else if (before == (int)EngineZone.PLAY && tag.Value == (int)EngineZone.GRAVEYARD && type == (int)CardType.MINION
                            && owner == human && blocks.Any(block => block.Start.BlockType == BlockType.DEATHS))
                        {
                            if (++diedThisTurn >= 3) Like("three-died-turn");
                            if (CurrentPlayer() == human) Like("friendly-died-own-turn");
                        }
                    }
                }
                if (!entities.TryGetValue(id, out var tags)) entities[id] = tags = new Dictionary<GameTag, int>();
                tags[tag.Tag] = tag.Value;
                if (enteredPlay) MinionEntered(id);
            }
            if (output.Count > 0) result[index] = output;
        }
        return result;
    }

    // The cost actually paid for a hand play: the payment annotation survives consumed discounts; zero is a real cost.
    private static int PaidCost(List<IPowerHistoryEntry> entries, int index, int source, int fallback)
    {
        for (int ahead = index + 1; ahead < entries.Count; ahead++)
        {
            if (entries[ahead] is PowerHistoryBlockStart || entries[ahead] is PowerHistoryBlockEnd) break;
            if (entries[ahead] is PowerHistoryTagChange paid && paid.EntityId == source && paid.Tag == GameTag.TAG_LAST_KNOWN_COST_IN_HAND) return paid.Value;
            if (entries[ahead] is PowerHistoryShowEntity frozen && frozen.Entity.Id == source)
            {
                if (frozen.Entity.Tags.TryGetValue(GameTag.TAG_LAST_KNOWN_COST_IN_HAND, out int cost)) return cost;
                if (frozen.Entity.Tags.TryGetValue(GameTag.COST, out int effective)) return effective;
                break;
            }
        }
        return fallback;
    }
}
