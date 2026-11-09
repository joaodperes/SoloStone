using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

// WP1 (any-deck brain). data/npc/decks-v1.json: every admitted NPC deck, its tags and optional hand-written plan.
public enum DeckArchetype { Aggro, Tempo, Midrange, Control, Combo }

// League order matches LadderRules star indices: Bronze 0-29 ... Diamond 120-149, Legend 150.
public enum LadderBand { Bronze, Silver, Gold, Platinum, Diamond, Legend }

public sealed class NpcDeckCard
{
    public string Id { get; }
    public int Count { get; }
    public NpcDeckCard(string id, int count) { Id = id; Count = count; }
}

public sealed class NpcDeckSideboard
{
    public string Owner { get; }
    public IReadOnlyList<NpcDeckCard> Cards { get; }
    public NpcDeckSideboard(string owner, IEnumerable<NpcDeckCard> cards) { Owner = owner; Cards = GoldenData.Copy(cards); }
}

public sealed class NpcDeckTags
{
    public DeckArchetype Archetype { get; }
    public int PowerTier { get; }        // 1 = top meta ... 5 = meme
    public int PilotDifficulty { get; }  // 1 = easy to pilot ... 5 = hard (measured by admission pilotability)
    public int Annoyance { get; }        // 0-3: mill, OTK, removal spam, heavy burn
    public int Fun { get; }              // 1-5, owner/playtest judged
    public LadderBand MinBand { get; }
    public LadderBand MaxBand { get; }
    public NpcDeckTags(DeckArchetype archetype, int powerTier, int pilotDifficulty, int annoyance, int fun,
        LadderBand minBand, LadderBand maxBand)
    { Archetype = archetype; PowerTier = powerTier; PilotDifficulty = pilotDifficulty; Annoyance = annoyance; Fun = fun;
      MinBand = minBand; MaxBand = maxBand; }
    public bool Allows(LadderBand band) => band >= MinBand && band <= MaxBand;
}

public sealed class NpcDeckSource
{
    public string Site { get; }
    public string Url { get; }
    public string Accessed { get; }
    public string DeckCode { get; }
    public string Changes { get; }
    public NpcDeckSource(string site, string url, string accessed, string deckCode, string changes)
    { Site = site; Url = url; Accessed = accessed; DeckCode = deckCode; Changes = changes; }
}

public sealed class NpcDeckEntry
{
    public string Id { get; }               // library id, e.g. "npc-dragon-warrior"; doubles as LocalDeck id
    public int Version { get; }
    public string Name { get; }
    public string Class { get; }            // CardClass name, e.g. "WARRIOR"
    public string Hero { get; }             // base hero card id, e.g. "HERO_01"
    public string Origin { get; }           // "golden" | "recipe:<id>" | "candidate:<id>" | "batch-3:<id>" | "casual-curated"
    public string Admission { get; }        // "admitted" | "casual" | "rejected" (from admission-report-v1.json)
    public string PlanId { get; }           // optional hand-written overlay id (DeckPlans.ForId); null = AutoDeckPlan only
    public NpcDeckTags Tags { get; }
    public NpcDeckSource Source { get; }
    public IReadOnlyList<NpcDeckCard> Cards { get; }
    public IReadOnlyList<NpcDeckSideboard> Sideboards { get; }
    public NpcDeckEntry(string id, int version, string name, string cardClass, string hero, string origin, string admission,
        string planId, NpcDeckTags tags, NpcDeckSource source, IEnumerable<NpcDeckCard> cards, IEnumerable<NpcDeckSideboard> sideboards)
    { Id = id; Version = version; Name = name; Class = cardClass; Hero = hero; Origin = origin; Admission = admission;
      PlanId = planId; Tags = tags; Source = source; Cards = GoldenData.Copy(cards); Sideboards = GoldenData.Copy(sideboards); }
    public bool Admitted => Admission == "admitted";
    // Plan id the pilot resolves through DeckPlans.ForId: hand-written overlay if present, else the derived plan.
    public string EffectivePlanId => PlanId ?? AutoDeckPlan.PlanIdFor(Id);
    public IEnumerable<string> ExpandedCardIds() => Cards.SelectMany(card => Enumerable.Repeat(card.Id, card.Count));
}

public sealed class NpcDeckLibrary
{
    public const string ResourceName = "SoloStone.Npc.decks-v1.json";
    public int Schema { get; }
    public int LibraryVersion { get; }
    public string ActiveMeta { get; }
    public IReadOnlyList<NpcDeckEntry> Decks { get; }
    private readonly Dictionary<string, NpcDeckEntry> byId;

    public NpcDeckLibrary(int schema, int libraryVersion, string activeMeta, IEnumerable<NpcDeckEntry> decks)
    {
        Schema = schema; LibraryVersion = libraryVersion; ActiveMeta = activeMeta; Decks = GoldenData.Copy(decks);
        byId = new Dictionary<string, NpcDeckEntry>(StringComparer.Ordinal);
        foreach (var deck in Decks)
        {
            if (byId.ContainsKey(deck.Id)) throw new InvalidDataException("Duplicate NPC deck id: " + deck.Id);
            byId.Add(deck.Id, deck);
        }
    }

    public NpcDeckEntry Find(string id)
    {
        NpcDeckEntry deck;
        return id != null && byId.TryGetValue(id, out deck) ? deck : null;
    }

    public IEnumerable<NpcDeckEntry> Admitted => Decks.Where(deck => deck.Admitted);
    // NpcWorldHooks.Population body: the admitted lists as the public population for fair deck inference.
    // A deck whose class string does not parse is left out (inference only; never fails boot). Library lists only:
    // DeckInference.WithStarters adds the Core starter lists as inference candidates, never as NPC decks.
    public IReadOnlyList<PopulationDeck> Population()
    {
        var result = new List<PopulationDeck>();
        foreach (var deck in Admitted) {
            SabberStoneCore.Enums.CardClass heroClass;
            if (Enum.TryParse(deck.Class, true, out heroClass))
                result.Add(new PopulationDeck(deck.Id, heroClass, deck.Cards.SelectMany(card => Enumerable.Repeat(card.Id, card.Count))));
        }
        return GoldenData.Copy(result);
    }
    public IEnumerable<NpcDeckEntry> ForBand(LadderBand band) => Admitted.Where(deck => deck.Tags.Allows(band));

    // Builds the engine-facing deck. opponentName is the NPC display name (native name plate reads LocalDeck.Opponent).
    // Via LocalDeck's internal constructor + WithSideboards; callers check LocalDeck.Build() (DeckLibrarySuite does).
    public static LocalDeck ToLocalDeck(NpcDeckEntry entry, string opponentName)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        var deck = new LocalDeck(entry.Id, entry.Name, entry.Hero, string.IsNullOrEmpty(opponentName) ? entry.Name : opponentName,
            "NPC library deck " + entry.Id + " v" + entry.Version, StyleFor(entry.Tags.Archetype), new string[0],
            entry.ExpandedCardIds().ToArray(), entry.Source?.Url);
        if (entry.Sideboards.Count != 0)
            deck.WithSideboards(entry.Sideboards.Select(board => new LocalSideboard { Source = board.Owner,
                Cards = board.Cards.SelectMany(card => Enumerable.Repeat(card.Id, card.Count)).ToList() }), true);
        return deck;
    }

    // LocalBot/BotSearch style prior per archetype (search shape only; GoldenPilot reads the DeckPlan).
    public static DeckStyle StyleFor(DeckArchetype archetype)
    {
        switch (archetype)
        {
            case DeckArchetype.Aggro: case DeckArchetype.Tempo: return DeckStyle.Tempo;
            case DeckArchetype.Control: return DeckStyle.Control;
            case DeckArchetype.Combo: return DeckStyle.Combo;
            default: return DeckStyle.Value;
        }
    }

    // LocalDecks.ExternalDecks body: library decks by id, named by nameFor(deckId) when it answers. New ladder matches
    // use admitted decks only; the harness passes admittedOnly=false to measure pending decks for admission.
    // referenced(deckId) = an active or pending (saved, unsettled) encounter uses this deck: it is always served, even
    // if a later library version un-admitted or rejected it, so a resumed match never loses its opponent deck.
    public Func<string, LocalDeck> LocalDeckResolver(Func<string, string> nameFor = null, bool admittedOnly = true,
        Func<string, bool> referenced = null)
    {
        var cache = new Dictionary<string, LocalDeck>(StringComparer.Ordinal);
        return id => {
            var entry = Find(id);
            if (entry == null) return null;
            bool pinned = false;
            try { pinned = referenced?.Invoke(id) == true; } catch (Exception) { }
            if (!pinned && (admittedOnly && !entry.Admitted || entry.Admission == "rejected")) return null;
            string name = nameFor?.Invoke(id);
            if (name != null) return ToLocalDeck(entry, name);
            LocalDeck deck;
            lock (cache) { if (!cache.TryGetValue(id, out deck)) cache.Add(id, deck = ToLocalDeck(entry, entry.Name)); }
            return deck;
        };
    }

    // NpcWorldHooks.PlanForId body: "auto:<deckId>" derives (and overlays a hand-written plan for the same deck);
    // a deck's hand-written planId resolves statically in DeckPlans first and never reaches this method.
    private readonly Dictionary<string, DeckPlan> plans = new Dictionary<string, DeckPlan>(StringComparer.Ordinal);
    public DeckPlan PlanFor(string planId)
    {
        string deckId = AutoDeckPlan.DeckIdFor(planId);
        var entry = Find(deckId);
        if (entry == null) return null;
        lock (plans)
        {
            DeckPlan plan;
            if (plans.TryGetValue(planId, out plan)) return plan;
            var hand = DeckPlans.All.FirstOrDefault(p => p.DeckId == entry.Id);
            plan = AutoDeckPlan.Derive(entry);
            if (hand != null) plan = AutoDeckPlan.Overlay(plan, hand);
            plans.Add(planId, plan);
            return plan;
        }
    }

    public static readonly IReadOnlyList<string> Admissions = Array.AsReadOnly(new[] { "admitted", "casual", "rejected", "pending" });

    // Structural validation: shared deck-size rule, copy limits, tags in range,
    // no Black Empire cards (engine-aware checks — card existence, meta, Build() — live in tests/npc-world DeckLibrarySuite).
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Schema != 1) problems.Add("schema must be 1");
        if (string.IsNullOrEmpty(ActiveMeta)) problems.Add("activeMeta missing");
        foreach (var deck in Decks)
        {
            string at = deck.Id + ": ";
            if (string.IsNullOrEmpty(deck.Id) || !deck.Id.StartsWith("npc-", StringComparison.Ordinal)) problems.Add(at + "id must start with npc-");
            if (string.IsNullOrEmpty(deck.Name)) problems.Add(at + "name missing");
            if (string.IsNullOrEmpty(deck.Class) || string.IsNullOrEmpty(deck.Hero)) problems.Add(at + "class/hero missing");
            if (!Admissions.Contains(deck.Admission)) problems.Add(at + "unknown admission " + deck.Admission);
            if (deck.Origin == null || !(deck.Origin == "golden" || deck.Origin.StartsWith("recipe:", StringComparison.Ordinal)
                || deck.Origin.StartsWith("candidate:", StringComparison.Ordinal) || deck.Origin.StartsWith("batch-3:", StringComparison.Ordinal)
                || (deck.Origin == "casual-curated" && deck.Admission == "casual")))
                problems.Add(at + "bad origin " + deck.Origin);
            int total = deck.Cards.Sum(c => c.Count);
            int size = LocalDecks.DeckSize(deck.Cards.Select(c => c.Id));
            if (total != size) problems.Add(at + total + " cards, expected " + size);
            if (deck.Cards.Any(c => c.Count < 1 || c.Count > 2)) problems.Add(at + "card count outside 1..2");
            if (deck.Cards.GroupBy(c => c.Id).Any(g => g.Count() > 1)) problems.Add(at + "duplicate card rows");
            foreach (var id in deck.Cards.Select(c => c.Id).Concat(deck.Sideboards.SelectMany(b => new[] { b.Owner }.Concat(b.Cards.Select(c => c.Id)))))
                if (IsBlackEmpire(id)) problems.Add(at + "Black Empire card " + id);
            var t = deck.Tags;
            if (t.PowerTier < 1 || t.PowerTier > 5 || t.PilotDifficulty < 1 || t.PilotDifficulty > 5 || t.Annoyance < 0 || t.Annoyance > 3
                || t.Fun < 1 || t.Fun > 5 || t.MinBand > t.MaxBand) problems.Add(at + "tags out of range");
            if (deck.PlanId != null && AutoDeckPlan.IsAutoPlanId(deck.PlanId)) problems.Add(at + "planId must be a hand-written id; omit it for auto");
        }
        return problems;
    }

    public static bool IsBlackEmpire(string cardId)
    {
        if (cardId == null || !(cardId.StartsWith("BE_", StringComparison.Ordinal)
            || cardId.StartsWith("CORE_BE_", StringComparison.Ordinal))) return false;
#if ENGINE_SOURCE
        return !LocalCardCatalog.ActiveMetaExtraCards.Contains(cardId);
#else
        return !vendorExtraCards.Value.Contains(cardId);
#endif
    }

#if !ENGINE_SOURCE
    // Embed the same installed catalog the source engine reads, never the ahead-of-engine policy JSON.
    private static readonly Lazy<HashSet<string>> vendorExtraCards = new Lazy<HashSet<string>>(() => {
        using (var stream = typeof(NpcDeckLibrary).Assembly.GetManifestResourceStream("SoloStone.Npc.OfflinePoolCatalog.xml"))
        {
            if (stream == null) throw new InvalidDataException("Missing NPC admission catalog.");
            var root = System.Xml.Linq.XDocument.Load(stream).Root;
            var meta = root.Element("Metas").Elements("Meta").Single(row => (string)row.Attribute("id") == (string)root.Attribute("activeMeta"));
            return new HashSet<string>(meta.Elements("ExtraCard").Select(row => (string)row.Attribute("cardId")), StringComparer.Ordinal);
        }
    });
#endif

    public static NpcDeckLibrary Parse(string json) => Load(NpcWorldJson.Parse(json));

    public static NpcDeckLibrary Load(object root)
    {
        var top = NpcWorldJson.Object(root);
        var decks = new List<NpcDeckEntry>();
        foreach (object value in NpcWorldJson.List(top, "decks"))
        {
            var row = new Dictionary<string, object>(NpcWorldJson.Object(value));
            if (row.ContainsKey("pending"))
            {
                var pending = NpcWorldJson.Object(row["pending"]);
                var needs = NpcWorldJson.Strings(pending, "needs", true);
                if (needs.Count == 0 || needs.Any(string.IsNullOrWhiteSpace) || needs.Distinct(StringComparer.Ordinal).Count() != needs.Count
                    || string.IsNullOrWhiteSpace(NpcWorldJson.Text(pending, "branch"))
                    || needs.Any(id => !ParseCards(row).Any(card => card.Id == id)))
                    throw new InvalidDataException("Invalid NPC pending dependency.");
                if (needs.Any(IsBlackEmpire))
                {
                    var fallback = NpcWorldJson.Object(pending["fallback"]);
                    row["cards"] = fallback["cards"];
                    row["sideboards"] = fallback["sideboards"];
                    var fallbackSource = new Dictionary<string, object>(NpcWorldJson.Object(row["source"]));
                    fallbackSource["changes"] = NpcWorldJson.Text(fallback, "changes");
                    row["source"] = fallbackSource;
                }
            }
            var tags = NpcWorldJson.Object(row["tags"]);
            var bands = NpcWorldJson.Strings(tags, "bands", true);
            if (bands.Count != 2) throw new InvalidDataException("tags.bands must be [min, max].");
            var source = row.ContainsKey("source") && row["source"] != null ? NpcWorldJson.Object(row["source"]) : null;
            decks.Add(new NpcDeckEntry(NpcWorldJson.Text(row, "id"), NpcWorldJson.Integer(row, "version", 1),
                NpcWorldJson.Text(row, "name"), NpcWorldJson.Text(row, "class"), NpcWorldJson.Text(row, "hero"),
                NpcWorldJson.Text(row, "origin"), NpcWorldJson.Text(row, "admission"), NpcWorldJson.Text(row, "planId", false),
                new NpcDeckTags(ParseEnum<DeckArchetype>(NpcWorldJson.Text(tags, "archetype")),
                    NpcWorldJson.Integer(tags, "powerTier"), NpcWorldJson.Integer(tags, "pilotDifficulty"),
                    NpcWorldJson.Integer(tags, "annoyance"), NpcWorldJson.Integer(tags, "fun"),
                    ParseEnum<LadderBand>(bands[0]), ParseEnum<LadderBand>(bands[1])),
                source == null ? null : new NpcDeckSource(NpcWorldJson.Text(source, "site", false), NpcWorldJson.Text(source, "url", false),
                    NpcWorldJson.Text(source, "accessed", false), NpcWorldJson.Text(source, "deckCode", false),
                    NpcWorldJson.Text(source, "changes", false)),
                ParseCards(row),
                NpcWorldJson.List(row, "sideboards", false).Cast<object>().Select(board => {
                    var b = NpcWorldJson.Object(board);
                    return new NpcDeckSideboard(NpcWorldJson.Text(b, "owner"), ParseCards(b));
                })));
        }
        return new NpcDeckLibrary(NpcWorldJson.Integer(top, "schema"), NpcWorldJson.Integer(top, "libraryVersion"),
            NpcWorldJson.Text(top, "activeMeta"), decks);
    }

    // Same {id, count} shape as data/shop/recipes-v1.json and the feat/npc-decks candidate files.
    internal static List<NpcDeckCard> ParseCards(IDictionary<string, object> row) =>
        NpcWorldJson.List(row, "cards").Cast<object>().Select(value => {
            var card = NpcWorldJson.Object(value);
            return new NpcDeckCard(NpcWorldJson.Text(card, "id"), NpcWorldJson.Integer(card, "count"));
        }).ToList();

    internal static T ParseEnum<T>(string text) where T : struct
    {
        T value;
        if (text == null || !Enum.TryParse(text, true, out value) || !Enum.IsDefined(typeof(T), value))
            throw new InvalidDataException("Unknown " + typeof(T).Name + ": " + text);
        return value;
    }
}
