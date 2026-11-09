#if ENGINE_SOURCE
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SabberStoneCore.Actions;
using SabberStoneCore.Enums;
using SabberStoneCore.Kettle;
using SabberStoneCore.Model;
using SabberStoneCore.Model.Entities;
using EngineGame = SabberStoneCore.Model.Game;
using ZoneKind = SabberStoneCore.Enums.Zone;
using EngineCard = SabberStoneCore.Model.Card;

// Match event log (owner rule 2026-10-31: live logs must reconstruct board, hands and options).
// One concise line per match event, both seats, decoded from the stream the engine already produces for the client:
// the PowerHistory entries (blocks with source/target, entity creations, zone and tag changes) plus the engine's
// HistoryFacts ledger for classification (cast kind, burn, discard, Discover) and the RNG draw count stamped on each entry.
// It is a pure observer of the REAL game: it reads, never writes (no RNG, entity id, tag or history change), runs from
// NativeMatch.SendHistory only (search clones never reach it), and any fault disables it instead of reaching gameplay.
// Lines name hidden cards on purpose: logs are local and never feed the NPC's fair view.
internal sealed class MatchEventLog
{
    internal const int MaxLinesPerTurn = 600;
    internal const int MaxErrors = 3;
    private readonly string match;
    private readonly Action<string> sink;
    private int sequence, turn, linesThisTurn, droppedThisTurn, errors, pendingDraws;
    private bool disabled, setup = true;
    private IPowerHistoryEntry lastSeen;
    private long lastFact;
    private int gameEntity;
    private readonly Dictionary<int, int> playerEntitySeat = new Dictionary<int, int>();
    private readonly Dictionary<int, Ent> ents = new Dictionary<int, Ent>();
    private readonly Dictionary<long, int> zoneCounts = new Dictionary<long, int>();
    private readonly List<Frame> frames = new List<Frame>();
    private readonly List<int> created = new List<int>();
    private Ent pendingStat; private string pendingStatBy;
    private int pendingStatFromAtk, pendingStatFromHp;
    private readonly HashSet<int> touched = new HashSet<int>();
    private readonly Dictionary<int, int> heraldProgress = new Dictionary<int, int>();
    private readonly HashSet<long> announcedOps = new HashSet<long>();
    private IList<IPowerHistoryEntry> batch; private int batchIndex;
    private bool inReset; private int resetEntities;
    // Test hook: throws from inside entry decoding to prove a fault disables the log and never reaches the match.
    internal Action<IPowerHistoryEntry> TestFault;
    private readonly string humanSeat, npcSeat;
    private static readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal);

    private sealed class Ent
    {
        internal int Id, Controller, Zone, Type, Atk, Health, Damage, Armor;
        internal string Card;
        internal bool Fresh, Logged, Script1Known, Guessed;
        internal int Script1;
    }
    private sealed class Frame { internal PowerHistoryBlockStart Start; internal bool Auto; internal bool Silent; internal bool Reset; }

    private static readonly GameTag[] Keywords = { GameTag.TAUNT, GameTag.DIVINE_SHIELD, GameTag.STEALTH, GameTag.FROZEN,
        GameTag.SILENCED, GameTag.WINDFURY, GameTag.CHARGE, GameTag.RUSH, GameTag.POISONOUS, GameTag.LIFESTEAL,
        GameTag.REBORN, GameTag.IMMUNE, GameTag.DORMANT };

    private readonly int maxLinesPerTurn;
    internal MatchEventLog(string matchId, Action<string> sink, string humanSeat = "1", string npcSeat = "2", int maxLinesPerTurn = MaxLinesPerTurn)
    {
        this.maxLinesPerTurn = maxLinesPerTurn;
        match = string.IsNullOrEmpty(matchId) ? "-" : matchId.Length > 8 ? matchId.Substring(0, 8) : matchId;
        this.sink = sink; this.humanSeat = humanSeat; this.npcSeat = npcSeat;
    }

    internal int Lines => sequence;
    internal bool Disabled => disabled;

    // Called with every history batch the real game sends. Entries already seen (same list re-sent) are skipped.
    internal void Observe(EngineGame game, IList<IPowerHistoryEntry> entries)
    {
        if (disabled || sink == null || game == null || entries == null || entries.Count == 0) return;
        var newest = entries[entries.Count - 1];
        try
        {
            int start = 0;
            if (lastSeen != null)
                for (int i = entries.Count - 1; i >= 0; i--)
                    if (ReferenceEquals(entries[i], lastSeen)) { start = i + 1; break; }
            if (start >= entries.Count) return;
            gameEntity = game.Id;
            bool rewound = false;
            for (int i = start; i < entries.Count && !rewound; i++) rewound = entries[i] is PowerHistoryResetGame;
            var facts = ReadFacts(game, rewound);
            created.Clear(); touched.Clear();
            batch = entries;
            for (batchIndex = start; batchIndex < entries.Count; batchIndex++)
            {
                TestFault?.Invoke(entries[batchIndex]);
                Handle(game, entries[batchIndex], facts);
            }
            FlushStat();
            AfterBatch(game, facts);
            EndBatch(game);
        }
        catch (Exception error)
        {
            // A fault mid-batch leaves open blocks and half-counted zones behind: forget all tracked state so the next batch
            // (if the log stays enabled) rebuilds it from the live game instead of building on a desynced shadow.
            Recover();
            errors++;
            Fault("EVENT_LOG_ERROR " + error.GetType().Name + ": " + Clean(error.Message));
            if (errors >= MaxErrors) { disabled = true; Fault("EVENT_LOG_DISABLED after " + errors + " errors"); }
        }
        finally
        {
            // Never decode the same entries twice, even after a fault.
            lastSeen = newest; batch = null;
        }
    }

    private void Recover()
    {
        frames.Clear(); created.Clear(); touched.Clear(); ents.Clear(); zoneCounts.Clear(); heraldProgress.Clear();
        pendingStat = null; inReset = false; pendingDraws = 0;
    }

    private void Fault(string text) { try { sink("m=" + match + " t=" + turn + " " + text); } catch (Exception) { } }

    // ---- classification from the engine's HistoryFacts ledger ----
    private sealed class Facts
    {
        internal bool WholeLedger;
        internal readonly HashSet<int> Burned = new HashSet<int>(), Discarded = new HashSet<int>();
        internal readonly Dictionary<int, List<HistoryCastKind>> Casts = new Dictionary<int, List<HistoryCastKind>>();
        internal readonly Dictionary<int, int> CastCursor = new Dictionary<int, int>();
        internal readonly Dictionary<int, HandEnteredFact> HandEntries = new Dictionary<int, HandEnteredFact>();
        internal readonly List<DiscoverCompletedFact> Discoveries = new List<DiscoverCompletedFact>();
    }
    // A Rewind restores an older ledger and regrows it inside the same batch, so sequence numbers alone cannot tell old from new.
    // Such a batch reads the whole (valid) ledger: entity-keyed burn/discard/entry facts stay correct, casts use the latest kind.
    private Facts ReadFacts(EngineGame game, bool rewound)
    {
        var facts = new Facts();
        long count = game.HistoryFacts.RecordCount;
        if (count < lastFact) rewound = true;
        facts.WholeLedger = rewound;
        long from = rewound ? 0 : lastFact;
        // Constant-time exit when the ledger grew nothing since the previous batch (most batches).
        if (!rewound && count <= lastFact) return facts;
        long newest = 0;
        if (rewound) announcedOps.Clear();
        foreach (var fact in game.HistoryFacts.Records)
        {
            if (fact.Sequence <= from) continue;
            newest = Math.Max(newest, fact.Sequence);
            switch (fact)
            {
                case CardBurnedFact burn: facts.Burned.Add(burn.CardId); break;
                case CardDiscardedFact discard: facts.Discarded.Add(discard.EntityId); break;
                case SpellCastFact cast when cast.Outcome == HistoryCastOutcome.Started:
                    if (!facts.Casts.TryGetValue(cast.EntityId, out var list)) facts.Casts[cast.EntityId] = list = new List<HistoryCastKind>();
                    list.Add(cast.CasterKind); break;
                case HandEnteredFact hand: facts.HandEntries[hand.EntityId] = hand; break;
                case DiscoverCompletedFact discover:
                    // After a Rewind the ledger is the restored baseline: those picks are old history (already announced or
                    // abandoned), so they are only remembered. Operation ids are restored with the ledger, so a replayed Discover reuses
                    // an abandoned attempt's id; that is why the set is rebuilt here instead of keeping the abandoned ids.
                    if (announcedOps.Add(discover.OperationId) && !rewound) facts.Discoveries.Add(discover);
                    break;
            }
        }
        lastFact = Math.Max(newest, count);
        return facts;
    }

    // ---- entry decoding ----
    private void Handle(EngineGame game, IPowerHistoryEntry entry, Facts facts)
    {
        // RNG draws are stamped on whichever entry followed them (often a silent tag change), so they accumulate until the
        // next logged event, which reports them as rng=N ("draws consumed since the previous logged event").
        pendingDraws += game.PowerHistory.RandomDrawsBefore(entry);
        if (setup || inReset) pendingDraws = 0;
        if (!(entry is PowerHistoryTagChange change && IsStat(change))) FlushStat();
        AnnouncePicks(game, entry, facts);
        switch (entry)
        {
            case PowerHistoryCreateGame create:
                gameEntity = create.Game.Id;
                foreach (var player in create.Players) playerEntitySeat[player.PowerEntity.Id] = player.PlayerId;
                break;
            case PowerHistoryResetGame reset:
                if (reset.CreateGame != null)
                    foreach (var player in reset.CreateGame.Players) playerEntitySeat[player.PowerEntity.Id] = player.PlayerId;
                break;
            case PowerHistoryFullEntity full: OnFull(game, full, facts); break;
            case PowerHistoryShowEntity show: OnShow(game, show, facts); break;
            case PowerHistoryChangeEntity changeEntity: OnTransform(game, changeEntity); break;
            case PowerHistoryTagChange tag: if (!inReset) OnTag(game, tag, facts); break;
            case PowerHistoryMetaData meta: OnMeta(game, meta); break;
            case PowerHistoryBlockStart block: OnBlockStart(game, block, facts); break;
            case PowerHistoryBlockEnd _: OnBlockEnd(game); break;
            case PowerHistoryShuffleDeck shuffle: if (!inReset) Emit(shuffle.PlayerId, "SHUFFLE_DECK", ""); break;
            case PowerHistoryRetainedOverdraw overdraw:
                Emit(overdraw.Player, "BURN", "reason=deck-overdraw card=" + Ref(game, overdraw.Entity.Id, overdraw.Entity.Name)); break;
        }
    }

    private void OnBlockEnd(EngineGame game)
    {
        if (frames.Count == 0) return;
        var frame = frames[frames.Count - 1];
        frames.RemoveAt(frames.Count - 1);
        if (!frame.Reset) return;
        inReset = false;
        Emit(0, "REWIND", "cause=" + Ref(game, frame.Start.Source) + " entities=" + resetEntities + " hand1=" + Zoned(1, (int)ZoneKind.HAND) + " deck1=" + Zoned(1, (int)ZoneKind.DECK)
            + " hand2=" + Zoned(2, (int)ZoneKind.HAND) + " deck2=" + Zoned(2, (int)ZoneKind.DECK) + " state-restored");
    }

    // A Discover pick is announced right before the first entry that involves one of its cards, so it reads in order with by=.
    private void AnnouncePicks(EngineGame game, IPowerHistoryEntry entry, Facts facts)
    {
        if (facts.Discoveries.Count == 0 || setup) return;
        int id = entry is PowerHistoryFullEntity f ? f.Entity.Id : entry is PowerHistoryShowEntity sh ? sh.Entity.Id
            : entry is PowerHistoryTagChange t ? t.EntityId : entry is PowerHistoryChangeEntity c ? c.Entity.Id : 0;
        if (id == 0) return;
        for (int i = facts.Discoveries.Count - 1; i >= 0; i--)
        {
            var pick = facts.Discoveries[i];
            if (!pick.EntityIdsKnown || !pick.SelectedEntityIds.Contains(id)) continue;
            facts.Discoveries.RemoveAt(i);
            EmitPick(game, pick, "");
        }
    }
    private void EmitPick(EngineGame game, DiscoverCompletedFact pick, string note)
    {
        string chosen = pick.DefinitionsKnown ? string.Join(", ", pick.SelectedDefinitions.Select(d => DefinitionRef(d))) : "?";
        Emit(SeatOf(pick.ControllerId), "DISCOVER_PICK", "op=" + pick.OperationId + " picked=[" + chosen + "]" + (pick.KeepAll ? " keep-all" : "") + note + By(game));
    }

    private static bool IsStat(PowerHistoryTagChange change) => change.Tag == GameTag.ATK || change.Tag == GameTag.HEALTH;

    private Ent Know(EngineGame game, int id, string cardId = null)
    {
        if (ents.TryGetValue(id, out var known))
        {
            if (!string.IsNullOrEmpty(cardId)) known.Card = cardId;
            return known;
        }
        var ent = new Ent { Id = id, Card = cardId };
        if (game.IdEntityDic.TryGetValue(id, out var live))
        {
            ent.Card = string.IsNullOrEmpty(cardId) ? live.Card?.Id : cardId;
            ent.Controller = live.Controller?.PlayerId ?? 0;
            ent.Type = (int)live.Card.Type;
            // Unknown history: the live value is the best "before"; the first change then reads as no-op.
            ent.Zone = live[GameTag.ZONE];
            TakeLiveStats(ent, live);
            ent.Guessed = true;   // first seen mid-stream (log attached late or recovered from a fault): "before" values are the engine's "after"
        }
        ents[id] = ent;
        return ent;
    }

    // Stats as the client shows them (current attack, health = max health minus damage), straight from the engine entity.
    private static void TakeLiveStats(Ent ent, IPlayable live)
    {
        // PeekStats, never the getters: AttackDamage/BaseHealth initialize cached fields on first read, and a log must not change state.
        if (live is Character character)
        {
            character.PeekStats(out int atk, out int maxHealth, out int damage);
            ent.Atk = atk; ent.Health = maxHealth; ent.Damage = damage; ent.Armor = character.Armor;
        }
    }

    // True when the batch still holds a later entry that changes this entity's stats, so the live value is not the value "now".
    // A reveal is not one: OnShow takes no stats from it, and a copied enchantment's own reveal must not hide the copy's real stats.
    private bool ChangedLater(int id)
    {
        if (batch == null) return false;
        for (int i = batchIndex + 1; i < batch.Count; i++)
        {
            switch (batch[i])
            {
                case PowerHistoryTagChange t when t.EntityId == id && (t.Tag == GameTag.ATK || t.Tag == GameTag.HEALTH || t.Tag == GameTag.DAMAGE || t.Tag == GameTag.ARMOR): return true;
                case PowerHistoryChangeEntity c when c.Entity.Id == id: return true;
            }
        }
        return false;
    }

    private static int Tag(IDictionary<GameTag, int> tags, GameTag tag) => tags.TryGetValue(tag, out int value) ? value : 0;

    private void Count(int controller, int zone, int delta)
    {
        long key = controller * 16L + zone;
        zoneCounts.TryGetValue(key, out int now);
        zoneCounts[key] = Math.Max(0, now + delta);
    }
    private int Zoned(int controller, int zone) { zoneCounts.TryGetValue(controller * 16L + zone, out int now); return now; }

    private void OnFull(EngineGame game, PowerHistoryFullEntity full, Facts facts)
    {
        var e = full.Entity;
        var tags = e.Tags;
        IPlayable live = null;
        if (game.IdEntityDic.TryGetValue(e.Id, out var found)) live = found;
        string card = string.IsNullOrEmpty(e.Name) && live != null ? live.Card?.Id : e.Name;
        var ent = new Ent { Id = e.Id, Card = card, Controller = Tag(tags, GameTag.CONTROLLER), Zone = Tag(tags, GameTag.ZONE),
            Type = Tag(tags, GameTag.CARDTYPE), Atk = Tag(tags, GameTag.ATK), Health = Tag(tags, GameTag.HEALTH),
            Damage = Tag(tags, GameTag.DAMAGE), Armor = Tag(tags, GameTag.ARMOR) };
        if (tags.TryGetValue(GameTag.TAG_SCRIPT_DATA_NUM_1, out int script1)) { ent.Script1 = script1; ent.Script1Known = true; }
        ents[e.Id] = ent;
        touched.Add(e.Id);
        if (ent.Type == 0 && game.IdEntityDic.TryGetValue(e.Id, out var typed)) ent.Type = (int)typed.Card.Type;
        // The engine's FullEntity health tag is the current health for a fresh actor; the log tracks max health + damage.
        if (ent.Damage != 0 && ent.Type == (int)CardType.MINION) ent.Health += ent.Damage;
        if (setup || inReset) { Count(ent.Controller, ent.Zone, 1); if (inReset) resetEntities++; return; }
        if (ent.Type == (int)CardType.ENCHANTMENT)
        {
            int target = Tag(tags, GameTag.ATTACHED), creator = Tag(tags, GameTag.CREATOR);
            if (target == 0 && game.IdEntityDic.TryGetValue(e.Id, out var attached) && attached is Enchantment live2) target = live2.Target?.Id ?? 0;
            if (creator == 0 && game.IdEntityDic.TryGetValue(e.Id, out var made) && made is Enchantment live3) creator = live3.Creator?.Id ?? 0;
            Emit(ent.Controller, "ENCHANT", "target=" + Ref(game, target) + " ench=" + Ref(game, ent.Id, ent.Card)
                + (creator != 0 ? " creator=" + Ref(game, creator) : "") + By(game));
            return;
        }
        if (ent.Type == (int)CardType.INVALID && (ent.Card ?? "").Length == 0) return;
        Count(ent.Controller, ent.Zone, 1);
        // Stored payloads (Bashana's carved spells, bound spells) say what they hold the moment they exist, in stream order.
        if (live != null && live[PayloadRules.Stored] != 0)
        {
            int owner = live[PayloadRules.Owner];
            Emit(live.Controller?.PlayerId ?? ent.Controller, "PAYLOAD", "owner=" + (owner != 0 ? Ref(game, owner) : "-") + " slot=" + live[PayloadRules.Key]
                + " card=" + Ref(game, e.Id, live.Card?.Id) + " " + CardKind(live) + " cost=" + live.Card.Cost + By(game));
        }
        // Offboard entities (stored payloads, Discover offers, unplaced tokens) are logged when placed.
        if (ent.Zone == (int)ZoneKind.INVALID || ent.Zone == (int)ZoneKind.SETASIDE) { ent.Fresh = true; return; }
        // The snapshot holds printed stats. A copy's enchantments (Specter Specialist on a buffed minion) raise them with no stat
        // tag change, so the stream never says so: read the engine, as a card entering the board does, unless the batch still says.
        if (live != null && !ChangedLater(ent.Id)) TakeLiveStats(ent, live);
        EmitCreate(game, ent, Tag(tags, GameTag.CREATOR), Tag(tags, GameTag.DISPLAYED_CREATOR), (ZoneKind)ent.Zone, facts);
    }

    private void EmitCreate(EngineGame game, Ent ent, int creator, int displayed, ZoneKind zone, Facts facts)
    {
        ent.Fresh = false; ent.Logged = true;
        string how = "";
        if (facts.HandEntries.TryGetValue(ent.Id, out var hand)) how = " entry=" + hand.EntryKind;
        int by = creator != 0 ? creator : displayed;
        string stats = ent.Type == (int)CardType.MINION || ent.Type == (int)CardType.WEAPON ? " stats=" + ent.Atk + "/" + (ent.Health - ent.Damage) : "";
        Emit(ent.Controller, zone == ZoneKind.DECK ? "SHUFFLE_INTO_DECK" : "CREATE", "card=" + Ref(game, ent.Id, ent.Card) + " new=yes zone=" + zone + stats
            + (by != 0 ? " creator=" + Ref(game, by) : "") + how + HeraldNote(game, ent) + By(game));
    }

    // Herald count at the moment the Soldier exists: tracked from the controller's progress tag, never read from the final state.
    private string HeraldNote(EngineGame game, Ent ent)
    {
        try
        {
            if (!game.IdEntityDic.TryGetValue(ent.Id, out var live) || !HeraldRules.IsSoldier(live.Card)) return "";
            var controller = live.Controller;
            int count;
            if (!heraldProgress.TryGetValue(controller.Id, out count))
            {
                // First sight in this batch: the progress change that follows the summon gives the count before it.
                count = -1;
                for (int i = batchIndex + 1; batch != null && i < batch.Count && count < 0; i++)
                    if (batch[i] is PowerHistoryTagChange t && t.EntityId == controller.Id && t.Tag == HeraldRules.Progress) count = t.Value - 1;
                if (count < 0) count = HeraldRules.Count(controller);
            }
            int magnitude = ent.Script1Known ? ent.Script1 : live[GameTag.TAG_SCRIPT_DATA_NUM_1];
            return " herald=soldier count=" + count + " tier=" + Math.Min(2, count / 2) + " magnitude=" + magnitude;
        }
        catch (Exception) { return ""; }
    }

    private void OnShow(EngineGame game, PowerHistoryShowEntity show, Facts facts)
    {
        var e = show.Entity;
        var ent = Know(game, e.Id, e.Name);
        touched.Add(e.Id);
        // A reveal can change the card type (hidden hand card); stats are tracked from tag changes and the end-of-batch resync.
        if (e.Tags.TryGetValue(GameTag.CARDTYPE, out int type) && type != 0) ent.Type = type;
        if (e.Tags.TryGetValue(GameTag.TAG_SCRIPT_DATA_NUM_1, out int script1)) { ent.Script1 = script1; ent.Script1Known = true; }
        if (e.Tags.ContainsKey(GameTag.ZONE) && !inReset) MoveZone(game, ent, Tag(e.Tags, GameTag.ZONE), facts, null);
    }

    private void OnTransform(EngineGame game, PowerHistoryChangeEntity change)
    {
        var ent = Know(game, change.Entity.Id);
        touched.Add(ent.Id);
        string before = Ref(game, ent.Id, ent.Card);
        int atk0 = ent.Atk, hp0 = ent.Health - ent.Damage;
        ent.Card = change.CardId;
        EngineCard printed = null;
        try { printed = Cards.FromId(change.CardId); } catch (Exception) { }
        if (printed != null) ent.Type = (int)printed.Type;
        // The engine may keep or clear damage and enchantments on a transform (removeEnchantments), and the stream does not say
        // which. When nothing later in this batch touches the entity, the live entity is exactly the state right now.
        if (!ChangedLater(ent.Id) && game.IdEntityDic.TryGetValue(ent.Id, out var live)) TakeLiveStats(ent, live);
        else if (printed != null) { ent.Atk = Tag(change.Entity.Tags, GameTag.ATK); ent.Health = Tag(change.Entity.Tags, GameTag.HEALTH); ent.Damage = 0;
            if (ent.Atk == 0 && ent.Health == 0) { ent.Atk = printed.ATK; ent.Health = printed.Health; } }
        string stats = ent.Type == (int)CardType.MINION || ent.Type == (int)CardType.WEAPON
            ? " stats=" + atk0 + "/" + hp0 + "->" + ent.Atk + "/" + (ent.Health - ent.Damage) : "";
        if (!setup && !inReset)
            Emit(ent.Controller, "TRANSFORM", "entity=#" + ent.Id + " from=" + before + " to=" + Ref(game, ent.Id, change.CardId) + stats + By(game));
    }

    private void OnTag(EngineGame game, PowerHistoryTagChange change, Facts facts)
    {
        if (change.EntityId == gameEntity)
        {
            if (change.Tag == GameTag.TURN)
            {
                if (droppedThisTurn > 0)
                {
                    int dropped = droppedThisTurn; droppedThisTurn = 0;
                    EmitRaw("m=" + match + " t=" + turn + " n=" + (++sequence) + " s=- EVENTS_TRUNCATED dropped=" + dropped + " previous-turn");
                }
                turn = change.Value; linesThisTurn = 0; setup = false;
                Emit(0, "TURN", "turn=" + change.Value);
            }
            return;
        }
        if (playerEntitySeat.TryGetValue(change.EntityId, out int seat))
        {
            if (change.Tag == HeraldRules.Progress) heraldProgress[change.EntityId] = change.Value;
            if (change.Tag == GameTag.PLAYSTATE && change.Value != 0 && change.Value != (int)PlayState.PLAYING)
                Emit(seat, "PLAYSTATE", "state=" + (PlayState)change.Value);
            else if (change.Tag == GameTag.CURRENT_PLAYER && change.Value == 1) { setup = false; Emit(seat, "ACTIVE", "turn=" + turn); }
            return;
        }
        var ent = Know(game, change.EntityId);
        touched.Add(ent.Id);
        switch (change.Tag)
        {
            case GameTag.TAG_SCRIPT_DATA_NUM_1: ent.Script1 = change.Value; ent.Script1Known = true; break;
            case GameTag.ZONE: MoveZone(game, ent, change.Value, facts, change); break;
            case GameTag.CONTROLLER:
                if (ent.Controller != change.Value)
                {
                    if (!setup) Emit(change.Value, "CONTROL", "entity=" + Ref(game, ent.Id, ent.Card) + " from=" + ent.Controller + " to=" + change.Value + By(game, ent.Id));
                    Count(ent.Controller, ent.Zone, -1); ent.Controller = change.Value; Count(ent.Controller, ent.Zone, 1);
                }
                break;
            case GameTag.DAMAGE:
                {
                    int old = ent.Damage; ent.Damage = change.Value;
                    if (ent.Guessed)
                    {
                        // Unknown "before" (the log lost its state): state only what is true.
                        ent.Guessed = false;
                        if (!setup) Emit(ent.Controller, "DAMAGE", "target=" + Ref(game, ent.Id, ent.Card) + " amount=? hp=?->" + (ent.Health - change.Value) + " before-unknown" + By(game, ent.Id));
                        break;
                    }
                    if (setup || old == change.Value) break;
                    int hpBefore = ent.Health - old, hpAfter = ent.Health - change.Value;
                    Emit(ent.Controller, change.Value > old ? "DAMAGE" : "HEAL", "target=" + Ref(game, ent.Id, ent.Card) + " amount=" + Math.Abs(change.Value - old)
                        + " hp=" + hpBefore + "->" + hpAfter + By(game, ent.Id));
                    break;
                }
            case GameTag.ARMOR:
                {
                    int old = ent.Armor; ent.Armor = change.Value;
                    if (!setup && old != change.Value)
                        Emit(ent.Controller, "ARMOR", "target=" + Ref(game, ent.Id, ent.Card) + " " + old + "->" + change.Value + By(game, ent.Id));
                    break;
                }
            case GameTag.ATK:
            case GameTag.HEALTH:
                {
                    if (setup) { if (change.Tag == GameTag.ATK) ent.Atk = change.Value; else ent.Health = change.Value; break; }
                    if (ent.Guessed) { ent.Guessed = false; if (change.Tag == GameTag.ATK) ent.Atk = change.Value; else ent.Health = change.Value; break; }
                    if (pendingStat != ent)
                    {
                        FlushStat();
                        pendingStat = ent; pendingStatFromAtk = ent.Atk; pendingStatFromHp = ent.Health - ent.Damage; pendingStatBy = By(game, ent.Id);
                    }
                    if (change.Tag == GameTag.ATK) ent.Atk = change.Value; else ent.Health = change.Value;
                    break;
                }
            default:
                if (!setup && ent.Zone == (int)ZoneKind.PLAY && Array.IndexOf(Keywords, change.Tag) >= 0)
                    Emit(ent.Controller, "KEYWORD", "target=" + Ref(game, ent.Id, ent.Card) + " " + change.Tag + "=" + change.Value + By(game, ent.Id));
                break;
        }
    }

    private void FlushStat()
    {
        if (pendingStat == null) return;
        var ent = pendingStat; pendingStat = null;
        int hp = ent.Health - ent.Damage;
        if (ent.Atk == pendingStatFromAtk && hp == pendingStatFromHp) return;
        Emit(ent.Controller, "STATS", "target=" + RefNoGame(ent) + " stats=" + pendingStatFromAtk + "/" + pendingStatFromHp + "->" + ent.Atk + "/" + hp + pendingStatBy);
    }

    private void OnMeta(EngineGame game, PowerHistoryMetaData meta)
    {
        if (meta.Type != MetaDataType.TARGET || meta.Info == null || frames.Count == 0 || setup) return;
        var top = frames[frames.Count - 1];
        int draws = pendingDraws;
        bool same = meta.Info.Count == 1 && meta.Info[0] == top.Start.Target;
        if (same && draws == 0) return;
        Emit(SeatOf(top.Start.Source), "TARGETS", "src=" + Ref(game, top.Start.Source) + " targets=[" + string.Join(", ", meta.Info.Take(12).Select(id => Ref(game, id)))
            + (meta.Info.Count > 12 ? ", +" + (meta.Info.Count - 12) + " more" : "") + "] random=" + (draws > 0 ? "yes" : "no"));
    }

    private void OnBlockStart(EngineGame game, PowerHistoryBlockStart block, Facts facts)
    {
        var frame = new Frame { Start = block, Auto = block is PowerHistoryEffectPlayStart };
        if (block.BlockType == BlockType.GAME_RESET)
        {
            // Rewind: everything before is abandoned and the restored state follows as full entities; resync silently.
            frames.Clear(); ents.Clear(); zoneCounts.Clear(); heraldProgress.Clear(); created.Clear();
            pendingStat = null; inReset = true; resetEntities = 0; pendingDraws = 0;
            frame.Silent = true; frame.Reset = true; frames.Add(frame);
            return;
        }
        string by = By(game, block.Source);
        int draws = pendingDraws;
        var src = block.Source != 0 ? Know(game, block.Source) : null;
        int seat = src != null ? src.Controller : SeatOf(block.Source);
        string card = src != null ? Ref(game, src.Id, src.Card) : "-";
        string target = block.Target != 0 ? " target=" + Ref(game, block.Target) : "";
        bool spell = src != null && src.Type == (int)CardType.SPELL;
        switch (block.BlockType)
        {
            case BlockType.PLAY:
                if (frame.Auto)
                {
                    // An automatic play picks among the legal targets with the engine's RNG (it draws even for one candidate), so
                    // the engine states how many there were: random only when more than one.
                    var note = game.PowerHistory.AutoChoiceOf(block) ?? (LegalTargets: -1, Mode: 0, ModeCardId: (string)null, ModeRandom: false);
                    string pick = note.LegalTargets == -2 ? (block.Target != 0 ? " pick=rule random=no" : "")
                        : block.Target != 0 || note.LegalTargets >= 0
                        ? " pick=auto legal=" + (note.LegalTargets >= 0 ? note.LegalTargets.ToString(CultureInfo.InvariantCulture) : "?")
                            + " random=" + (note.LegalTargets < 0 ? "unknown" : note.LegalTargets > 1 ? "yes" : "no") : "";
                    string mode = "";
                    if (note.Mode > 0)
                    {
                        int options = 0;
                        try { options = game.IdEntityDic[src.Id].ChooseOnePlayables?.Length ?? 0; } catch (Exception) { }
                        mode = " mode=" + note.Mode + "/" + options + " option=" + (note.ModeCardId != null ? note.ModeCardId + "(" + Clean(NameOf(note.ModeCardId)) + ")" : "?") + " mode-random=" + (note.ModeRandom ? "yes" : "no");
                    }
                    Emit(seat, spell ? "CAST" : "AUTOPLAY", (spell ? "kind=" + CastKind(facts, src.Id, HistoryCastKind.Effect) : "kind=" + TypeName(src))
                        + " card=" + card + target + pick + mode + by);
                }
                else if (spell)
                    Emit(seat, "CAST", "kind=" + CastKind(facts, src.Id, HistoryCastKind.Player) + " card=" + card + target + " pick=chosen" + by);
                else
                    Emit(seat, src != null && src.Type == (int)CardType.HERO_POWER ? "HEROPOWER" : "PLAY", "kind=" + TypeName(src) + " card=" + card + target + by);
                break;
            case BlockType.POWER:
                if (spell && string.IsNullOrEmpty(block.EffectCardId)) frame.Silent = true;
                else Emit(seat, src != null && src.Type == (int)CardType.MINION ? "BATTLECRY" : "EFFECT", "card=" + card + target
                    + (string.IsNullOrEmpty(block.EffectCardId) ? "" : " effect=" + block.EffectCardId) + by);
                break;
            case BlockType.TRIGGER:
                if (block.TriggerKeyword.HasValue)
                    Emit(seat, "TRIGGER", "kind=" + (GameTag)block.TriggerKeyword.Value + " card=" + card + target + by);
                else if (IsColossal(game, src))
                    Emit(seat, "COLOSSAL", "body=" + card + " appendages=" + AppendageList(game, src));
                else if ((block.EffectIndex ?? block.Index) >= 0 && src != null)
                    Emit(seat, src.Zone == (int)ZoneKind.SECRET ? "SECRET" : "TRIGGER", "card=" + card + target
                        + (string.IsNullOrEmpty(block.EffectCardId) ? "" : " effect=" + block.EffectCardId) + by);
                else frame.Silent = true;
                break;
            case BlockType.ATTACK: Emit(seat, "ATTACK", "attacker=" + card + target); break;
            case BlockType.FATIGUE: Emit(SeatOf(block.Source), "FATIGUE", ""); break;
            case BlockType.DEATHS: case BlockType.REVEAL_CARD: case BlockType.GAME_RESET: frame.Silent = true; break;
            default: Emit(seat, "BLOCK", "type=" + block.BlockType + " card=" + card + target + by); break;
        }
        frames.Add(frame);
    }

    private static string CastKind(Facts facts, int entity, HistoryCastKind fallback)
    {
        if (facts.Casts.TryGetValue(entity, out var list) && list.Count > 0)
        {
            if (facts.WholeLedger) return list[list.Count - 1].ToString().ToLowerInvariant();
            facts.CastCursor.TryGetValue(entity, out int at);
            facts.CastCursor[entity] = at + 1;
            return list[Math.Min(at, list.Count - 1)].ToString().ToLowerInvariant();
        }
        return fallback.ToString().ToLowerInvariant();
    }

    private static bool IsColossal(EngineGame game, Ent src)
    {
        try { return src != null && game.IdEntityDic.TryGetValue(src.Id, out var live) && live.Card.ColossalAppendageIds != null; }
        catch (Exception) { return false; }
    }
    private string AppendageList(EngineGame game, Ent src)
    {
        try { return "[" + string.Join(", ", game.IdEntityDic[src.Id].Card.ColossalAppendageIds) + "]"; }
        catch (Exception) { return "[]"; }
    }

    private static string TypeName(Ent ent) => ent == null ? "?" : ((CardType)ent.Type).ToString().ToLowerInvariant();

    // ---- zone transitions ----
    private void MoveZone(EngineGame game, Ent ent, int to, Facts facts, PowerHistoryTagChange change)
    {
        int from = ent.Zone;
        if (from == to) return;
        Count(ent.Controller, from, -1); Count(ent.Controller, to, 1);
        ent.Zone = to;
        touched.Add(ent.Id);
        // Leaving the board resets a minion (damage, enchantments) without any tag change: take its stats from the engine, or from
        // the printed card when the batch goes on to change it again.
        if (from == (int)ZoneKind.PLAY && to != (int)ZoneKind.PLAY && ent.Type == (int)CardType.MINION)
        {
            if (!ChangedLater(ent.Id) && game.IdEntityDic.TryGetValue(ent.Id, out var left)) TakeLiveStats(ent, left);
            else
            {
                ent.Damage = 0;
                try { var printed = Cards.FromId(ent.Card); ent.Atk = printed.ATK; ent.Health = printed.Health; } catch (Exception) { }
            }
        }
        // Entering the board: the hand/deck snapshot of a hidden card carries no stats, so take them from the engine unless the batch
        // goes on to change them (then the printed card is the best "before").
        if (to == (int)ZoneKind.PLAY && from != (int)ZoneKind.PLAY && (ent.Type == (int)CardType.MINION || ent.Type == (int)CardType.WEAPON || ent.Type == (int)CardType.HERO))
        {
            if (!ChangedLater(ent.Id) && game.IdEntityDic.TryGetValue(ent.Id, out var entering)) TakeLiveStats(ent, entering);
            else if (ent.Atk == 0 && ent.Health == 0)
                try { var printed = Cards.FromId(ent.Card); ent.Atk = printed.ATK; ent.Health = printed.Health; } catch (Exception) { }
            ent.Guessed = false;
        }
        if (setup || inReset) return;
        var toZone = (ZoneKind)to; var fromZone = (ZoneKind)from;
        if (ent.Type == (int)CardType.ENCHANTMENT || ent.Type == (int)CardType.INVALID && ent.Card == null) return;
        // A spell copy cast from nowhere ends in the graveyard: the CAST line already named it, so only a burn is news.
        if (ent.Fresh && toZone == ZoneKind.GRAVEYARD && !facts.Burned.Contains(ent.Id)) { ent.Fresh = false; ent.Logged = true; return; }
        if (ent.Fresh && (toZone == ZoneKind.HAND || toZone == ZoneKind.DECK || toZone == ZoneKind.PLAY || toZone == ZoneKind.SECRET || toZone == ZoneKind.GRAVEYARD))
        {
            int creator = game.IdEntityDic.TryGetValue(ent.Id, out var live) ? live[GameTag.CREATOR] : 0;
            int displayed = live != null ? live[GameTag.DISPLAYED_CREATOR] : 0;
            EmitCreate(game, ent, creator, displayed, toZone, facts);
            if (toZone != ZoneKind.GRAVEYARD) return;
        }
        string card = Ref(game, ent.Id, ent.Card);
        // A card being played passes through zones on its way to the board; that is the play, not a return to hand or a discard.
        bool playing = frames.Count > 0 && frames.Any(f => f.Start.BlockType == BlockType.PLAY && f.Start.Source == ent.Id);
        switch (toZone)
        {
            case ZoneKind.HAND:
                if (fromZone == ZoneKind.DECK)
                {
                    string kind = facts.HandEntries.TryGetValue(ent.Id, out var entry) ? " entry=" + entry.EntryKind : "";
                    Emit(ent.Controller, "DRAW", "card=" + card + " hand=" + Zoned(ent.Controller, (int)ZoneKind.HAND) + " deck=" + Zoned(ent.Controller, (int)ZoneKind.DECK) + kind + By(game, ent.Id));
                }
                else if ((fromZone == ZoneKind.PLAY || fromZone == ZoneKind.GRAVEYARD || fromZone == ZoneKind.SECRET) && !playing)
                    Emit(ent.Controller, "RETURN_TO_HAND", "card=" + card + " from=" + fromZone + By(game, ent.Id));
                else if (ent.Logged || !ent.Fresh) { }
                break;
            case ZoneKind.DECK:
                if (fromZone != ZoneKind.INVALID && fromZone != ZoneKind.SETASIDE || !ent.Logged)
                    Emit(ent.Controller, "SHUFFLE_INTO_DECK", "card=" + card + " from=" + fromZone + " deck=" + Zoned(ent.Controller, (int)ZoneKind.DECK) + By(game, ent.Id));
                break;
            case ZoneKind.PLAY:
                if (ent.Type == (int)CardType.MINION && !playing)
                    Emit(ent.Controller, "SUMMON", "card=" + card + " from=" + fromZone + " stats=" + ent.Atk + "/" + (ent.Health - ent.Damage) + HeraldNote(game, ent) + By(game, ent.Id));
                else if (ent.Type == (int)CardType.WEAPON && !playing)
                    Emit(ent.Controller, "EQUIP", "card=" + card + By(game, ent.Id));
                break;
            case ZoneKind.SECRET:
                Emit(ent.Controller, "SECRET_SET", "card=" + card + By(game, ent.Id)); break;
            case ZoneKind.GRAVEYARD:
                if (fromZone == ZoneKind.PLAY)
                {
                    // A resolved spell leaves PLAY for the graveyard as a matter of course; its CAST line already told the story.
                    if (ent.Type != (int)CardType.SPELL)
                        Emit(ent.Controller, ent.Type == (int)CardType.MINION ? "DEATH" : ent.Type == (int)CardType.WEAPON ? "DESTROY" : "REMOVED",
                            "card=" + card + By(game, ent.Id));
                }
                else if (facts.Burned.Contains(ent.Id))
                    Emit(ent.Controller, "BURN", "reason=" + (fromZone == ZoneKind.DECK ? "hand-full-draw" : "hand-full") + " card=" + card + " from=" + fromZone
                        + " hand=" + Zoned(ent.Controller, (int)ZoneKind.HAND) + By(game, ent.Id));
                else if (fromZone == ZoneKind.DECK) Emit(ent.Controller, "MILL", "card=" + card + By(game, ent.Id));
                else if (fromZone == ZoneKind.HAND && !playing)
                    Emit(ent.Controller, facts.Discarded.Contains(ent.Id) ? "DISCARD" : "HAND_TO_GRAVEYARD", "card=" + card + By(game, ent.Id));
                break;
            case ZoneKind.REMOVEDFROMGAME:
                Emit(ent.Controller, "REMOVED_FROM_GAME", "card=" + card + " from=" + fromZone + By(game, ent.Id)); break;
        }
    }

    // ---- end of batch: unplaced Discover picks, then truth checks against the engine ----
    private void AfterBatch(EngineGame game, Facts facts)
    {
        foreach (var pick in facts.Discoveries) EmitPick(game, pick, " late=yes");
        facts.Discoveries.Clear();
    }

    // The tracker rebuilds what the stream implies; the engine is the truth. Counts always resync; a visible stat mismatch is
    // corrected AND reported, so a tracking gap shows up in the log (and in the tests) instead of silently lying later.
    private void EndBatch(EngineGame game)
    {
        foreach (var player in new[] { game.Player1, game.Player2 })
        {
            if (player == null) continue;
            zoneCounts[player.PlayerId * 16L + (int)ZoneKind.HAND] = player.HandZone.Count;
            zoneCounts[player.PlayerId * 16L + (int)ZoneKind.DECK] = player.DeckZone.Count;
        }
        foreach (int id in touched)
        {
            if (!ents.TryGetValue(id, out var ent) || !game.IdEntityDic.TryGetValue(id, out var live)) continue;
            string card = live.Card?.Id;
            if (live[GameTag.ZONE] == (int)ZoneKind.PLAY && live is Character character && ent.Zone == (int)ZoneKind.PLAY)
            {
                character.PeekStats(out int atk, out int maxHealth, out int damage);
                int hp = maxHealth - damage;
                // A hero's attack is its weapon's: the client derives it, the stream carries no tag change for it.
                bool atkDiffers = ent.Atk != atk && !(live is Hero);
                if (live is Hero) ent.Atk = atk;
                if (atkDiffers || ent.Health - ent.Damage != hp)
                {
                    Emit(ent.Controller, "RESYNC", "target=" + Ref(game, id, card) + " tracked=" + ent.Atk + "/" + (ent.Health - ent.Damage) + " engine=" + atk + "/" + hp);
                    TakeLiveStats(ent, live);
                }
            }
            if (card != null && ent.Card != card) ent.Card = card;
            ent.Type = (int)live.Card.Type;
            int controller = live.Controller?.PlayerId ?? ent.Controller;
            ent.Controller = controller;
        }
    }

    /// <summary>A choice offer opening (Mulligan, Discover, Rewind...): the offers in the timeline, not only in choice-open.</summary>
    internal void ChoiceOpen(EngineGame game, int seat, string choiceType, int sourceId, IEnumerable<int> offers)
    {
        if (disabled || sink == null) return;
        try
        {
            Emit(seat, "CHOICE_OPEN", "type=" + choiceType + " source=" + (sourceId != 0 ? Ref(game, sourceId) : "-")
                + " offers=[" + string.Join(", ", offers.Select(id => Ref(game, id))) + "]");
        }
        catch (Exception error) { Fault("EVENT_LOG_ERROR " + error.GetType().Name + ": " + Clean(error.Message)); }
    }

    private static string DefinitionRef(HistoryDefinition definition)
    {
        try { var card = definition.Materialize(); return card.Id + "(" + Clean(card.Name) + ")"; }
        catch (Exception) { return "?"; }
    }
    private static string CardKind(IEntity entity) => "type=" + entity.Card.Type.ToString().ToLowerInvariant();

    // ---- line building ----
    private string By(EngineGame game, int self = 0)
    {
        for (int i = frames.Count - 1; i >= 0; i--)
        {
            var start = frames[i].Start;
            if (start.Source == self || start.Source == 0 || start.Source == gameEntity || playerEntitySeat.ContainsKey(start.Source)) continue;
            return " by=" + Ref(game, start.Source);
        }
        return "";
    }

    private int SeatOf(int entityId)
    {
        if (playerEntitySeat.TryGetValue(entityId, out int seat)) return seat;
        return ents.TryGetValue(entityId, out var ent) ? ent.Controller : 0;
    }

    private string Ref(EngineGame game, int id, string cardId = null)
    {
        if (id == 0) return "-";
        if (playerEntitySeat.TryGetValue(id, out int playerSeat)) return "PLAYER" + playerSeat;
        Ent ent; ents.TryGetValue(id, out ent);
        if (string.IsNullOrEmpty(cardId)) cardId = ent?.Card;
        string name = null;
        if (game.IdEntityDic.TryGetValue(id, out var live))
        {
            if (string.IsNullOrEmpty(cardId)) cardId = live.Card?.Id;
            if (live.Card != null && live.Card.Id == cardId) name = live.Card.Name;
        }
        if (string.IsNullOrEmpty(cardId)) return "#" + id;
        return cardId + "#" + id + "(" + Clean(name ?? NameOf(cardId)) + ")";
    }
    private string RefNoGame(Ent ent) => (ent.Card ?? "?") + "#" + ent.Id + "(" + Clean(ent.Card == null ? "" : NameOf(ent.Card)) + ")";

    private static string NameOf(string cardId)
    {
        if (Names.TryGetValue(cardId, out string name)) return name;
        try { name = Cards.FromId(cardId)?.Name ?? ""; } catch (Exception) { name = ""; }
        Names[cardId] = name; return name;
    }
    private static string Clean(string text) => (text ?? "").Replace("\r", " ").Replace("\n", " ");

    private void Emit(int seat, string kind, string detail) => EmitCore(seat, kind, detail);
    private void EmitRaw(string text) { try { sink(text); } catch (Exception) { } }

    private void EmitCore(int seat, string kind, string detail)
    {
        if (++linesThisTurn > maxLinesPerTurn)
        {
            // Explicit marker the first time a turn overflows; the final count follows when the next turn starts.
            if (droppedThisTurn++ == 0)
                EmitRaw("m=" + match + " t=" + turn + " n=" + (++sequence) + " s=- EVENTS_TRUNCATED cap=" + maxLinesPerTurn + " per-turn; later events this turn are not logged");
            return;
        }
        var line = new StringBuilder(160);
        line.Append("m=").Append(match).Append(" t=").Append(turn.ToString(CultureInfo.InvariantCulture))
            .Append(" n=").Append((++sequence).ToString(CultureInfo.InvariantCulture))
            .Append(" s=").Append(seat == 0 ? "-" : seat.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(kind);
        if (!string.IsNullOrEmpty(detail)) line.Append(' ').Append(detail.TrimStart());
        if (pendingDraws > 0) line.Append(" rng=").Append(pendingDraws.ToString(CultureInfo.InvariantCulture));
        pendingDraws = 0;
        sink(line.ToString());
    }
}
#endif
