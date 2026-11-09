#if STANDARD_PROOF
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using SabberStoneCore.Enums;
using MiniJSON;
using SabberStoneCore.Model;

// Exact research pair; never part of ownership, starter grants or pack pools.
internal static class StandardProofNative
{
    internal static readonly string[] CollectibleIds = {
        "CORE_EX1_011", "CORE_CS2_189", "CORE_EX1_096", "CORE_GVG_085", "CORE_LOOT_413",
        "CORE_EX1_007", "CORE_CS2_179", "CORE_SW_066", "Core_CS2_200", "CORE_CS2_222",
        "CORE_CS2_024", "CORE_CS2_023", "CORE_CS2_029", "CORE_CS2_028", "CORE_CS2_032",
        "CORE_CS2_108", "CORE_EX1_391", "CORE_AT_064", "CORE_EX1_606", "CORE_EX1_604"
#if MODERN_MECHANICS
        , "TIME_045", "TLC_101", "TLC_226", "DINO_435", "CORE_BT_156", "EDR_469"
#endif
    };
    internal static bool AllowNativePlayability(bool active, bool isolatedLaunch, bool auditingOriginal,
        bool auditPassed, bool failed, string id) => active && isolatedLaunch && !auditingOriginal
        && auditPassed && !failed && CollectibleIds.Contains(id, StringComparer.Ordinal);
    internal static readonly string[] DefinitionIds = CollectibleIds.Concat(new[] {
        "HERO_08", "HERO_01", "HERO_08bp", "HERO_01bp", "GAME_005", "CS2_222o", "EX1_604o"
#if MODERN_MECHANICS
        , "TLC_101e", "EDR_469e"
#endif
    }).ToArray();
    internal static readonly int[] RequiredLiveTags = { 1635, 1720, 1743
#if MODERN_MECHANICS
        , 1085, 1336, 1518, 1090, 4606, 448, 2, 4028
#endif
    };
#if MODERN_MECHANICS
    private static readonly int[] HelperLiveTags = { 32, 40, 1519 };
#endif
    internal static bool RequiresLiveTag(string id, int tag) => DefinitionIds.Contains(id, StringComparer.Ordinal)
        && (RequiredLiveTags.Contains(tag)
#if MODERN_MECHANICS
            || (id == "EDR_469e" && HelperLiveTags.Contains(tag))
#endif
        );
    internal static Dictionary<int, int> RequiredLiveSnapshot(string id, IDictionary<GameTag, int> snapshot)
    {
        if (!DefinitionIds.Contains(id, StringComparer.Ordinal)) return null;
        var definition = Cards.FromId(EngineId(id));
        // Frozen closure has no aura modifiers for these tags; preserve historical instance overrides, including zero.
        IEnumerable<int> tags = RequiredLiveTags;
#if MODERN_MECHANICS
        if (id == "EDR_469e") tags = tags.Concat(HelperLiveTags);
#endif
        return tags.ToDictionary(tag => tag, tag => snapshot != null && snapshot.TryGetValue((GameTag)tag, out int value)
            ? value : definition[(GameTag)tag]);
    }
    internal static string EngineId(string id) => id == "HERO_08bp" ? "CS2_034" : id == "HERO_01bp" ? "CS2_102" : id;
#if MODERN_MECHANICS
    internal const string Snapshot = "standard-2026-10-17-modern-mechanics";
#else
    internal const string Snapshot = "standard-2026-10-17-core-tradeable";
#endif
    internal static bool IsProof(string id) => id == "proof-mage" || id == "proof-warrior";
    internal static string Opponent(string id) => id == "proof-mage" ? "proof-warrior"
        : id == "proof-warrior" ? "proof-mage" : throw new ArgumentException("Not a proof deck.");
    internal static LocalDeck Loaner(string id)
    {
        var deck = ProofDecks.Get(id);
        return new LocalDeck(deck.Id, deck.Name, deck.Hero, ProofDecks.Get(Opponent(id)).Name,
#if MODERN_MECHANICS
            "Exact Reborn, Kindred and Dormant research proof",
#else
            "Exact Core research proof",
#endif
            DeckStyle.Value, new string[0], deck.CardIds);
    }
    internal sealed class TradeRecord
    {
        public int action { get; set; }
        public int player { get; set; }
        public int turn { get; set; }
        public int source { get; set; }
        public int sourceZoneBefore { get; set; }
        public int sourceZoneAfter { get; set; }
        public string inputNonce { get; set; }
        public string card { get; set; }
        public int optionsId { get; set; }
        public int optionIndex { get; set; }
        public int keyword { get; set; } = 1720;
        public int target { get; set; }
        public int sub { get; set; } = -1;
        public int position { get; set; }
        public int manaBefore { get; set; }
        public int manaAfter { get; set; }
        public int handBefore { get; set; }
        public int handAfter { get; set; }
        public int deckBefore { get; set; }
        public int deckAfter { get; set; }
        public int? humanReplacementEntity { get; set; }
        public string humanReplacementCard { get; set; }
        public string[] engineHistory { get; set; }
        public string[] nativeHistory { get; set; }
    }
    internal static string InputNonce(string path,string run,string match,int options,int index)
    {
        try
        {
            string text;
            using(var file = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
            using(var reader = new StreamReader(file)) text=reader.ReadToEnd();
            var input=MiniJSON.Json.Deserialize(text) as JsonNode;
            return input != null && Text(input,"ExpectedRunId")==run && Number(input,"ExpectedPid")==System.Diagnostics.Process.GetCurrentProcess().Id
                && Text(input,"ExpectedSnapshotId")==Snapshot && Text(input,"ExpectedMatchId")==match
                && Number(input,"ExpectedOptionsId")==options && Number(input,"ExpectedOptionIndex")==index ? Text(input,"Nonce")??"" : "";
        }
        catch(IOException) { return ""; }
        catch(InvalidOperationException) { return ""; }
        catch(UnauthorizedAccessException) { return ""; }
    }
    internal static string Envelope(string run,string match,string record) =>
        "{\"snapshot\":"+Json(Snapshot)+",\"runId\":"+Json(run)+",\"matchId\":"+Json(match)
        +",\"utc\":"+Json(DateTime.UtcNow.ToString("o"))+",\"record\":"+record+"}";
    private static JsonList History(string[] rows)
    {
        var list = new JsonList();
        foreach(string row in rows) list.Add(row);
        return list;
    }
    internal static string Json(object value)
    {
        if (value is TradeRecord trade)
        {
            var node = new JsonNode {
                {"action",trade.action}, {"player",trade.player}, {"turn",trade.turn}, {"source",trade.source},
                {"sourceZoneBefore",trade.sourceZoneBefore}, {"sourceZoneAfter",trade.sourceZoneAfter}, {"inputNonce",trade.inputNonce},
                {"card",trade.card}, {"optionsId",trade.optionsId}, {"optionIndex",trade.optionIndex}, {"keyword",trade.keyword},
                {"target",trade.target}, {"sub",trade.sub}, {"position",trade.position},
                {"manaBefore",trade.manaBefore}, {"manaAfter",trade.manaAfter}, {"handBefore",trade.handBefore}, {"handAfter",trade.handAfter},
                {"deckBefore",trade.deckBefore}, {"deckAfter",trade.deckAfter},
                {"engineHistory",History(trade.engineHistory)}, {"nativeHistory",History(trade.nativeHistory)}
            };
            if (trade.humanReplacementEntity.HasValue) {
                node.Add("humanReplacementEntity",trade.humanReplacementEntity.Value);
                node.Add("humanReplacementCard",trade.humanReplacementCard);
            }
            value=node;
        }
        return MiniJSON.Json.Serialize(value);
    }
    private static string Text(JsonNode row,string field)
    {
        if (!row.TryGetValue(field,out object value) || value==null) return null;
        return value as string ?? throw new InvalidOperationException("Proof string field invalid: "+field);
    }
    private static int? Number(JsonNode row,string field)
    {
        if (!row.TryGetValue(field,out object value) || value==null) return null;
        if (value is long integer && integer>=int.MinValue && integer<=int.MaxValue) return (int)integer;
        if (value is int direct) return direct;
        throw new InvalidOperationException("Proof integer field invalid: "+field);
    }
    private static string[] Strings(JsonNode row,string field)
    {
        if (!row.TryGetValue(field,out object value) || value==null) return new string[0];
        if (!(value is JsonList list) || list.Any(item=>!(item is string))) throw new InvalidOperationException("Proof string list invalid: "+field);
        return list.Cast<string>().ToArray();
    }
    private sealed class Definition
    {
        public string id { get; set; }
        public int dbfId { get; set; }
        public string cardClass { get; set; }
        public string set { get; set; }
        public string type { get; set; }
        public string rarity { get; set; }
        public string race { get; set; }
        public string spellSchool { get; set; }
        public int? cost { get; set; }
        public int? attack { get; set; }
        public int? health { get; set; }
        public int? heroPowerDbfId { get; set; }
        public bool collectible { get; set; }
        public string[] mechanics { get; set; }
        public string[] referencedTags { get; set; }
    }
    private static readonly Dictionary<string,int> Keywords = new Dictionary<string,int>(StringComparer.Ordinal) {
        {"TRIGGER_VISUAL",32}, {"TAUNT",190}, {"DIVINE_SHIELD",194}, {"FREEZE",208},
        {"DEATHRATTLE",217}, {"BATTLECRY",218}, {"SILENCE",339}, {"AURA",362}, {"TRADEABLE",1720}
#if MODERN_MECHANICS
        , {"ENRAGED",212}, {"POISONOUS",363}, {"RUSH",791}, {"REBORN",1085}
#endif
    };
    private static Dictionary<int,int> Tags(Definition row)
    {
        var tags = new Dictionary<int,int> {
            {45,row.health??0}, {47,row.attack??0}, {48,row.cost??0},
            {199,row.cardClass=="MAGE"?4:row.cardClass=="WARRIOR"?10:row.cardClass=="NEUTRAL"?12:throw new InvalidOperationException("Unknown proof class")},
            {183,row.set=="CORE"?1637:row.set=="HERO_SKINS"?17:row.set=="LEGACY"?1635:row.set=="EXPERT1"?3:
#if MODERN_MECHANICS
                row.set=="EMERALD_DREAM"?1946:row.set=="THE_LOST_CITY"?1952:row.set=="TIME_TRAVEL"?1957:
#endif
                throw new InvalidOperationException("Unknown proof set")},
            {202,row.type switch { "HERO"=>3, "MINION"=>4, "SPELL"=>5, "ENCHANTMENT"=>6, "HERO_POWER"=>10, _=>throw new InvalidOperationException("Unknown proof type") }},
            {203,row.rarity switch { null=>0, "COMMON"=>1, "FREE"=>2, "RARE"=>3, "EPIC"=>4, "LEGENDARY"=>5, _=>throw new InvalidOperationException("Unknown proof rarity") }},
            {200,row.race==null?0:row.race=="MECHANICAL"?17:row.race=="BEAST"?20:
#if MODERN_MECHANICS
                row.race=="DEMON"?15:row.race=="ELEMENTAL"?18:row.race=="DRAGON"?24:row.race=="ALL"?26:
#endif
                throw new InvalidOperationException("Unknown proof tribe")},
            {1635,row.spellSchool switch { null=>0, "ARCANE"=>1, "FIRE"=>2, "FROST"=>3, _=>throw new InvalidOperationException("Unknown proof school") }},
            {321,row.collectible?1:0}, {380,row.heroPowerDbfId??0}, {1743,(row.mechanics??new string[0]).Contains("TRADEABLE")?1:0}
        };
        foreach (var keyword in Keywords) tags[keyword.Value] = (row.mechanics??new string[0]).Contains(keyword.Key)?1:0;
        return tags;
    }
#if MODERN_MECHANICS
    // Code pin only; external DBF data still requires the complete original-definition audit.
    internal const string OriginalDormantClientSha256 = "786376bddad544709639d4b3ab60ce8799d4b1c6c7d7fc4c225ce79fe07228c6";
    private static bool UsesDormantOriginalContract(string id, int tag) => tag == 1518 && (id == "CORE_BT_156" || id == "EDR_469");
    private static Dictionary<string,Dictionary<int,int>> ParseNativeTagSupplement(string text)
    {
        var root = MiniJSON.Json.Deserialize(text) as JsonNode;
        if (root == null || Number(root,"schemaVersion") != 1 || Text(root,"snapshot") != Snapshot
            || !root.TryGetValue("source",out object sourceValue) || !(sourceValue is JsonNode source)
            || Number(source,"build") != 253932
            || Text(source,"url") != "https://raw.githubusercontent.com/marssuren/fireplace/33724afb7a77f09e00077ebb284ab95578642013/fireplace/cards/253932/CardDefs.xml"
            || Text(source,"commit") != "33724afb7a77f09e00077ebb284ab95578642013"
            || Text(source,"blobSha1") != "03d1ba8881764dede07ead3532eff091863aabb4"
            || Text(source,"sha256") != "0faba475d5a0e29caf246390783d0e1e07c7788b689d3248aa7ca8cea3943022")
            throw new InvalidOperationException("Modern native supplement provenance mismatch.");
        var expected = new Dictionary<string,Tuple<int,int,string>>(StringComparer.Ordinal) {
            {"TLC_226",Tuple.Create(117535,4028,"0ab38611abea0787e5403e0c15152a97e5932d5cbfaa2acffafb1e021110dd76")},
            {"DINO_435",Tuple.Create(119695,4028,"93d7dde912218d7665f52ad6daa78f9d599f2b4440371cdea171754f6d17081a")},
            {"CORE_BT_156",Tuple.Create(127593,1518,"038898e70b1ecfb9d2afff4e4ecf697430e6d42f9da8c8c6909ef92a562a8dc4")},
            {"EDR_469",Tuple.Create(114344,1518,"cb91e6ab8b88cb17de55adebcacfeb33c3df586e9b6a0553fca7f1dd2cfb2303")},
            {"EDR_469e",Tuple.Create(114345,1519,"967af67e74e7b382d8235f669f1ac5ec46c45305f783655901ea22d833c5976c")}
        };
        if (!root.TryGetValue("definitions",out object definitions) || !(definitions is JsonList rows) || rows.Count != expected.Count)
            throw new InvalidOperationException("Modern native supplement closure mismatch.");
        var result = new Dictionary<string,Dictionary<int,int>>(StringComparer.Ordinal);
        foreach (object value in rows)
        {
            var row = value as JsonNode ?? throw new InvalidOperationException("Modern native supplement row invalid.");
            string id = Text(row,"id");
            if (id == null || !expected.TryGetValue(id,out var exact) || result.ContainsKey(id)
                || Number(row,"dbfId") != exact.Item1 || Text(row,"sourceEntitySha256") != exact.Item3
                || !row.TryGetValue("directTags",out object direct) || !(direct is JsonNode tags)
                || tags.Count != 1 || Number(tags,exact.Item2.ToString(System.Globalization.CultureInfo.InvariantCulture)) != 1)
                throw new InvalidOperationException("Modern native supplement identity/tag mismatch.");
            string xml = Text(row,"sourceEntityXml");
            string digest;
            using (var hash = System.Security.Cryptography.SHA256.Create())
                digest = xml == null ? "" : BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(xml))).Replace("-","").ToLowerInvariant();
            if (digest != exact.Item3) throw new InvalidOperationException("Modern native supplement source extract mismatch.");
            result.Add(id,new Dictionary<int,int> { {exact.Item2,1} });
        }
        return result;
    }
#endif
    internal sealed class CardSetTiming
    {
        internal int RowId, CardId, CardSetId, EventType;
        internal bool NativeSetExists;
        internal bool? EventsReceived, EventKnown, EventActive;
    }
    internal static void AuditDefinitions(Func<string,int> nativeDbid, Func<string,int> nativeCardSet, Func<int,CardSetTiming[]> nativeCardSetTimings, Func<string,int,int> nativeTag,
        Func<string,int,int> nativeReferencedTag, Action<string> record
#if MODERN_MECHANICS
        , string nativeAssemblySha256, Func<string,string> nativeDormantDiagnostic
#endif
        )
    {
        Definition[] rows;
        using (var input = typeof(StandardProofNative).Assembly.GetManifestResourceStream("StandardProofDefinitions.json"))
        {
            if (input == null) throw new InvalidOperationException("Proof definition resource missing.");
            string text;
            using(var reader = new StreamReader(input)) text=reader.ReadToEnd();
            if (!(MiniJSON.Json.Deserialize(text) is JsonList list)) throw new InvalidOperationException("Proof definition JSON invalid.");
            rows=list.Select(value => {
                var row=value as JsonNode ?? throw new InvalidOperationException("Proof definition row invalid.");
                return new Definition {
                    id=Text(row,"id"), dbfId=Number(row,"dbfId")??0, cardClass=Text(row,"cardClass"), set=Text(row,"set"),
                    type=Text(row,"type"), rarity=Text(row,"rarity"), race=Text(row,"race"), spellSchool=Text(row,"spellSchool"),
                    cost=Number(row,"cost"), attack=Number(row,"attack"), health=Number(row,"health"), heroPowerDbfId=Number(row,"heroPowerDbfId"),
                    collectible=row.TryGetValue("collectible",out object collectible) && collectible is bool flag && flag,
                    mechanics=Strings(row,"mechanics"), referencedTags=Strings(row,"referencedTags")
                };
            }).ToArray();
        }
        if (rows.Length != DefinitionIds.Length || !rows.Select(row => row.id).OrderBy(id => id,StringComparer.Ordinal)
            .SequenceEqual(DefinitionIds.OrderBy(id => id,StringComparer.Ordinal)))
            throw new InvalidOperationException("Proof definition closure differs from explicit selected IDs.");
#if MODERN_MECHANICS
        if (nativeAssemblySha256 != OriginalDormantClientSha256 || nativeDormantDiagnostic == null)
            throw new InvalidOperationException("Modern original Dormant contract requires pinned native code and readable diagnostics.");
        Dictionary<string,Dictionary<int,int>> supplement;
        using (var input = typeof(StandardProofNative).Assembly.GetManifestResourceStream("ModernMechanicsNativeTags.json"))
        {
            if (input == null) throw new InvalidOperationException("Modern native supplement resource missing.");
            using (var reader = new StreamReader(input)) supplement = ParseNativeTagSupplement(reader.ReadToEnd());
        }
#endif
        var failures = new List<string>();
        record("id\tengineId\tsemantic\ttag\tmanifest\toriginalNative\tengine\tdecision");
        foreach (var row in rows)
        {
            string engineId = EngineId(row.id);
            var card = Cards.FromId(engineId) ?? throw new InvalidOperationException("Missing proof engine definition: " + engineId);
            int actualDbid = nativeDbid(row.id);
            bool dbidMismatch=actualDbid != row.dbfId || card.AssetId != row.dbfId;
            if (dbidMismatch) failures.Add(row.id+":DBID");
            record(row.id+"\t"+engineId+"\tDBID\t0\t"+row.dbfId+"\t"+actualDbid+"\t"+card.AssetId+"\t"+(dbidMismatch?"FATAL":"exact"));
            var expectedTags = Tags(row);
#if MODERN_MECHANICS
            if (supplement.TryGetValue(row.id,out var extraTags))
                foreach (var tag in extraTags) expectedTags.Add(tag.Key,tag.Value);
#endif
            foreach (var tag in expectedTags)
            {
                int original = tag.Key == 183 ? nativeCardSet(row.id) : nativeTag(row.id,tag.Key), engine = card[(GameTag)tag.Key];
                bool legacySet = tag.Key == 183 && engine == 2 && new[]{"HERO_08","HERO_01","GAME_005","CS2_222o","HERO_08bp","HERO_01bp"}.Contains(row.id);
                bool nativeMismatch = original != tag.Value;
                int engineExpected = tag.Value;
                string decision = legacySet ? "declared legacy helper set2" : "exact";
#if MODERN_MECHANICS
                if (UsesDormantOriginalContract(row.id,tag.Key))
                {
                    // The frozen research supplement predates Sprite's reviewed Starts Dormant printing correction.
#if ENGINE_SOURCE
                    if (row.id == "EDR_469") engineExpected = 0;
#endif
                    int nativeReferenced = nativeReferencedTag(row.id,tag.Key);
                    nativeMismatch = tag.Value != 1 || original != 0 || nativeReferenced != 1;
#if ENGINE_SOURCE
                    if (row.id == "EDR_469" && card[(GameTag)1652] != 1) nativeMismatch = true;
#endif
                    decision = "installed-original-dormant-direct-reference;sourceExpected=1;nativeDirectExpected=0;nativeReferencedExpected=1;nativeReferenced="+nativeReferenced+";engineExpected="+engineExpected+";clientSha256="+OriginalDormantClientSha256;
                    string diagnostic = nativeDormantDiagnostic(row.id);
                    if (string.IsNullOrWhiteSpace(diagnostic)) throw new InvalidOperationException("Missing original Dormant diagnostics: "+row.id);
                    record(row.id+"\t"+engineId+"\toriginal-dormant-diagnostics\t1518\t"+tag.Value+"\t"+original+"\t"+engine+"\tnativeReferenced1518="+nativeReferenced+";"+diagnostic);
                }
#endif
                if (tag.Key == 183 && original == 0)
                {
                    var timings = dbidMismatch ? new CardSetTiming[0] : nativeCardSetTimings(actualDbid) ?? new CardSetTiming[0];
                    bool staticMatch = timings.Any(timing => timing != null && timing.CardSetId == tag.Value)
                        && timings.All(timing => timing != null && timing.CardId == actualDbid && timing.CardSetId > 0 && timing.NativeSetExists
                            && (timing.CardSetId == tag.Value
                                || (timing.CardSetId == 1810 && tag.Value == 1637 && CollectibleIds.Contains(row.id))
                                || (timing.CardSetId == 2 && new[]{"HERO_08bp","HERO_01bp","GAME_005","CS2_222o"}.Contains(row.id))));
                    nativeMismatch = !staticMatch;
                    foreach (var timing in timings)
                        record(row.id+"\t"+engineId+"\tcard-set-timing\t183\t"+tag.Value+"\t"+original+"\t"+engine+"\t"+
                            (timing == null ? "null timing row" : "rowId="+timing.RowId+";cardId="+timing.CardId+";setId="+timing.CardSetId
                                +";nativeSetExists="+timing.NativeSetExists+";eventType="+timing.EventType+";eventsReceived="+(timing.EventsReceived?.ToString()??"unknown")
                                +";known="+(timing.EventKnown?.ToString()??"unknown")+";active="+(timing.EventActive?.ToString()??"unknown")));
                    if (staticMatch) decision = (timings.Any(timing => timing.CardSetId != tag.Value)
                        ? "static-offline expected static membership with declared historical alternative" : "static-offline exact set")
                        +(legacySet ? "; declared legacy helper set2" : "");
                }
                bool mismatch = nativeMismatch || (engine != engineExpected && !legacySet);
                if (mismatch) { decision = "FATAL"; failures.Add(row.id+":"+tag.Key); }
                record(row.id+"\t"+engineId+"\t"+(tag.Key == 183 ? "card-set" :
#if MODERN_MECHANICS
                    extraTags != null && extraTags.ContainsKey(tag.Key) ? "source-supplement" :
#endif
                    "direct")+"\t"+tag.Key+"\t"+tag.Value+"\t"+original+"\t"+engine+"\t"+decision);
            }
            foreach (var keyword in Keywords)
            {
                int expected = (row.referencedTags??new string[0]).Contains(keyword.Key)?1:0;
                int original = nativeReferencedTag(row.id,keyword.Value);
                int engine = card.RefTags.TryGetValue((GameTag)keyword.Value,out int value)?value:0;
                bool mismatch = original != expected || engine != expected;
                if (mismatch) failures.Add(row.id+":referenced:"+keyword.Key);
                record(row.id+"\t"+engineId+"\treferenced\t"+keyword.Value+"\t"+expected+"\t"+original+"\t"+engine+"\t"+(mismatch?"FATAL":"exact"));
            }
        }
        if (failures.Count != 0) throw new InvalidOperationException("Proof original definition mismatch: " + string.Join(",",failures));
    }
}
#endif
