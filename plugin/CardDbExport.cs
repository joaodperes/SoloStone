#if !SOLOSTONE_RELEASE
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MiniJSON;

// All client reads and file publication happen on the endpoint's Unity main thread.
internal static class CardDbExport
{
    internal static string Root = @"C:\hs-native-ui-"+@"prototype\reports\control";
    private static readonly FieldInfo ReferencedTags = typeof(EntityDef).GetField("m_referencedTags", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo TagLists = typeof(EntityBase).GetField("m_tagLists", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo ReferenceTagLists = typeof(EntityDef).GetField("m_referenceTagLists", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo ListValues = typeof(TagListMap).GetField("m_values", BindingFlags.Instance | BindingFlags.NonPublic);

    internal static JsonNode Export()
    {
        NativeTestControl.AssertMainThread();
        var loader = DefLoader.Get();
        if (loader == null || !loader.HasLoadedEntityDefs())
            throw new InvalidOperationException("Entity definitions have not loaded");
        // DbfLocValue.GetString(Locale) ignores its argument after StripUnusedLocales.
        // Require actual enUS rather than silently export another locale as English.
        var localization = Type.GetType("Localization, Blizzard.T5.Localization", true);
        var locale = localization.GetMethod("GetActualLocale", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
        if (locale == null || locale.Invoke(null, null)?.ToString() != "enUS")
            throw new InvalidOperationException("Card database export requires the client locale enUS");

        // BaseContextData.ProgramVersion contains all four build components; Network.ProductVersion omits the build.
        string version = TelemetryManager.ProgramVersion;
        if (version == null || !Regex.IsMatch(version, @"\A[0-9]+\.[0-9]+\.[0-9]+\.[1-9][0-9]*\z"))
            throw new InvalidOperationException("Exact client build version unavailable");
        var definitions = loader.GetAllEntityDefs();
        if (definitions == null || definitions.Count == 0)
            throw new InvalidOperationException("No loaded entity definitions");
        // Diagnostic only: missing server timings make IsSetRotated return false.
        bool collectionAvailable = CollectionManager.Get() != null;
        var sets = collectionAvailable ? GameUtils.GetStandardSets() : GameDbf.CardSet.GetRecords()
            .Select(record => (TAG_CARD_SET)record.ID).Where(GameUtils.IsStandardCardSet).ToArray();
        var standardSets = TestControlProtocol.List(sets.Distinct().OrderBy(set => (int)set).Select(set => (object)Set(set)));
        string path = Path.Combine(Root, "carddb-" + version + ".json");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        OfflineGuard.RejectReparsePoints(path);
        OfflineGuard.RejectReparsePoints(temporary);
        Directory.CreateDirectory(Root);
        try
        {
            using (var writer = new StreamWriter(temporary, false, TestControlProtocol.Utf8, 64 * 1024))
            {
                var header = new JsonNode {
                    { "clientVersion", version }, { "clientVersionSource", "TelemetryManager.ProgramVersion" },
                    { "exportUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
                    { "count", definitions.Count }, { "locale", "enUS" },
                    { "clientApiStandard", new JsonNode {
                        { "status", "unreliable offline" }, { "sets", standardSets },
                        { "source", collectionAvailable ? "GameUtils.GetStandardSets" : "GameDbf.CardSet + GameUtils.IsStandardCardSet" } } },
                    { "formatLegalitySource", "clientApiStandard (unreliable offline); compute from rotationInputs, never from API fallback" },
                    { "rotationInputs", RotationInputs(definitions.Values) }
                };
                string prefix = Json.Serialize(header);
                writer.Write(prefix.Substring(0, prefix.Length - 1));
                writer.Write(",\"cards\":[");
                bool first = true;
                foreach (var pair in definitions)
                {
                    if (pair.Value == null) throw new InvalidOperationException("Null loaded EntityDef: " + pair.Key);
                    if (!first) writer.Write(',');
                    writer.Write(Json.Serialize(Card(pair.Value)));
                    first = false;
                }
                writer.Write("]}");
                writer.Flush();
                writer.BaseStream.Flush();
            }
            // A failed export leaves the previous complete database intact.
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
            string hash;
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            return new JsonNode { { "path", path }, { "clientVersion", version }, { "count", definitions.Count },
                { "sha256", hash }, { "bytes", new FileInfo(path).Length } };
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static JsonNode Set(TAG_CARD_SET set) => new JsonNode { { "name", set.ToString() }, { "id", (int)set } };

    private static JsonNode RotationInputs(IEnumerable<EntityDef> definitions)
    {
        if (GameDbf.CardSet == null || GameDbf.CardSetTiming == null || GameDbf.SetRotationEvent == null ||
            GameDbf.DeckRuleset == null || GameDbf.DeckRulesetRule == null || GameDbf.DeckRulesetRuleSubset == null)
            throw new InvalidOperationException("Rotation DBF tables have not loaded");
        var eventIds = new HashSet<EventTimingType> { EventTimingType.SPECIAL_EVENT_ALWAYS, EventTimingType.SPECIAL_EVENT_NEVER };
        int Event(EventTimingType value) { eventIds.Add(value); return (int)value; }
        var cardSets = TestControlProtocol.List(GameDbf.CardSet.GetRecords().Select(record => (object)new JsonNode {
            { "id", record.ID }, { "name", ((TAG_CARD_SET)record.ID).ToString() },
            { "isCollectible", record.IsCollectible }, { "isCoreCardSet", record.IsCoreCardSet },
            { "isExpansionCardSet", record.GetVar("IS_EXPANSION_CARD_SET") },
            { "isFeaturedCardSet", record.IsFeaturedCardSet }, { "releaseOrder", record.ReleaseOrder },
            { "standardEvent", Event(record.StandardEvent) }, { "contentLaunchEvent", Event(record.ContentLaunchEvent) },
            { "legacyCardSetEvent", Event(record.LegacyCardSetEvent) }, { "setFilterEvent", Event(record.SetFilterEvent) },
            { "craftableWhenWild", record.CraftableWhenWild }
        }));
        // EntityBase.GetCardSet picks the FIRST active mapping. Preserve DBF order.
        var cardSetTimings = TestControlProtocol.List(GameDbf.CardSetTiming.GetRecords().Select(record => (object)new JsonNode {
            { "id", record.ID }, { "cardId", record.CardId }, { "cardSetId", record.CardSetId },
            { "eventTimingEvent", Event(record.EventTimingEvent) }
        }));
        var rotationEvents = TestControlProtocol.List(GameDbf.SetRotationEvent.GetRecords().Select(record => (object)new JsonNode {
            { "id", record.ID }, { "eventTiming", Event(record.EventTiming) }
        }));
        foreach (var definition in definitions)
        {
            var record = definition == null ? null : GameDbf.GetIndex().GetCardRecord(definition.GetCardId());
            if (record != null) Event(record.GameplayEvent);
        }
        var manager = EventTimingManager.Get();
        if (manager != null) eventIds.UnionWith(manager.AllKnownEvents);
        var names = new Dictionary<int, string>();
        var eventMap = DbfShared.GetEventMap();
        if (eventMap != null)
            for (int i = 0; i < eventMap.Keys.Count; i++)
            {
                names[eventMap.Values[i]] = eventMap.Keys[i];
                eventIds.Add((EventTimingType)eventMap.Values[i]);
            }
        var eventTimings = TestControlProtocol.List(eventIds.OrderBy(value => (int)value).Select(value => {
            DateTime? start = null, end = null;
            bool available = manager != null && manager.GetEventRangeUtc(value, out start, out end);
            string name;
            if (!names.TryGetValue((int)value, out name)) name = manager == null ? value.ToString() : manager.GetName(value);
            return (object)new JsonNode {
                { "id", (int)value }, { "name", name }, { "rangeAvailable", available },
                { "startUtc", start?.ToString("O", CultureInfo.InvariantCulture) },
                { "endUtc", end?.ToString("O", CultureInfo.InvariantCulture) },
                { "source", value == EventTimingType.SPECIAL_EVENT_ALWAYS || value == EventTimingType.SPECIAL_EVENT_NEVER
                    ? "EventTimingManager.InitializeHardcodedEvents" : "EventTimingManager.InitEventTimingsFromServer (not static DBF dates)" },
                { "forcedActive", manager != null && manager.IsEventForcedActive(value) },
                { "forcedInactive", manager != null && manager.IsEventForcedInactive(value) }
            };
        }));
        return new JsonNode {
            { "schemaVersion", 1 }, { "source", "Loaded GameDbf assets + EventTimingManager snapshot" },
            { "eventTimingManagerAvailable", manager != null },
            { "hasReceivedEventTimingsFromServer", manager != null && manager.HasReceivedEventTimingsFromServer },
            { "devTimeOffsetSeconds", manager == null ? 0L : manager.DevTimeOffsetSeconds },
            { "cardSets", cardSets }, { "cardSetTimings", cardSetTimings }, { "setRotationEvents", rotationEvents },
            { "eventTimings", eventTimings }, { "standardRulesetId", 2 },
            { "standardRulesetSource", "DeckRuleset.GetReverseDeckRulesetLookup: FT_STANDARD = 2" },
            { "deckRulesets", TestControlProtocol.List(GameDbf.DeckRuleset.GetRecords().Select(record => (object)new JsonNode {
                { "id", record.ID }, { "assetFlags", Convert.ToInt32(record.GetVar("ASSET_FLAGS"), CultureInfo.InvariantCulture) }
            })) },
            { "deckRulesetRules", TestControlProtocol.List(GameDbf.DeckRulesetRule.GetRecords().Select(record => (object)new JsonNode {
                { "id", record.ID }, { "deckRulesetId", record.DeckRulesetId },
                { "appliesToSubsetId", record.AppliesToSubsetId }, { "appliesToIsNot", record.AppliesToIsNot },
                { "ruleType", record.RuleType.ToString() }, { "ruleTypeId", (int)record.RuleType }, { "ruleIsNot", record.RuleIsNot },
                { "minValue", record.MinValue }, { "maxValue", record.MaxValue },
                { "tag", record.Tag }, { "tagMinValue", record.TagMinValue }, { "tagMaxValue", record.TagMaxValue },
                { "stringValue", record.StringValue }, { "showInvalidCards", record.ShowInvalidCards }
            })) },
            { "deckRulesetRuleSubsets", TestControlProtocol.List(GameDbf.DeckRulesetRuleSubset.GetRecords().Select(record => (object)new JsonNode {
                { "id", record.ID }, { "deckRulesetRuleId", record.DeckRulesetRuleId }, { "subsetId", record.SubsetId }
            })) }
        };
    }

    private static JsonNode Tags(TagMap map)
    {
        var result = new JsonNode();
        foreach (var tag in map.GetMap()) result.Add(tag.Key.ToString(CultureInfo.InvariantCulture), tag.Value);
        return result;
    }

    private static JsonNode Lists(TagListMap map)
    {
        var result = new JsonNode();
        foreach (var tag in (Dictionary<int, List<int>>)ListValues.GetValue(map))
            result.Add(tag.Key.ToString(CultureInfo.InvariantCulture), TestControlProtocol.List(tag.Value.Select(value => (object)value)));
        return result;
    }

    private static TAG_CARD_SET ResolveCardSet(EntityDef definition)
    {
        // GetCardSet can select timed memberships using unavailable server dates.
        return (TAG_CARD_SET)definition.GetTag(GAME_TAG.CARD_SET);
    }

    private static JsonList CardSetTimings(CardDbfRecord record)
    {
        if (record == null) return TestControlProtocol.List(Enumerable.Empty<object>());
        return TestControlProtocol.List(GameUtils.GetCardSetTimingsForCard(record.ID).Select(mapping => (object)new JsonNode {
            { "id", mapping.ID }, { "cardId", mapping.CardId },
            { "cardSet", Set((TAG_CARD_SET)mapping.CardSetId) },
            { "eventTimingEvent", (int)mapping.EventTimingEvent }
        }));
    }

    private static JsonNode Card(EntityDef original)
    {
        // Preview Harmony patches only apply to canonical definitions. Clone preserves
        // raw tags, avoids preview projection, and keeps derived race caches off live defs.
        var definition = original.Clone();
        string id = definition.GetCardId();
        var record = GameDbf.GetIndex().GetCardRecord(id);
        var classes = new List<TAG_CLASS>();
        definition.GetClasses(classes);
        var set = ResolveCardSet(definition);
        var row = new JsonNode {
            { "cardId", id }, { "dbfId", record == null ? 0 : record.ID },
            { "name", record?.Name?.GetString() ?? "" }, { "text", record?.TextInHand?.GetString() ?? "" },
            { "cardType", definition.GetCardType().ToString() }, { "cardClass", definition.GetClass().ToString() },
            { "cardClasses", TestControlProtocol.List(classes.Select(value => (object)value.ToString())) },
            { "cardSet", Set(set) }, { "cardSetTimings", CardSetTimings(record) }, { "rarity", definition.GetRarity().ToString() },
            { "rawCardSetTag", definition.GetTag(GAME_TAG.CARD_SET) },
            { "gameplayEvent", record == null ? (int)EventTimingType.UNKNOWN : (int)record.GameplayEvent },
            { "cost", definition.GetCost() }, { "atk", definition.GetATK() }, { "health", definition.GetHealth() },
            { "collectible", definition.GetTag(GAME_TAG.COLLECTIBLE) != 0 },
            { "races", TestControlProtocol.List(definition.GetRaces().Select(value => (object)value.ToString())) },
            { "spellSchool", definition.GetSpellSchool().ToString() },
            { "tags", Tags(original.GetTags()) },
            { "referencedTags", Tags((TagMap)ReferencedTags.GetValue(original)) },
            { "tagLists", Lists((TagListMap)TagLists.GetValue(original)) },
            { "referencedTagLists", Lists((TagListMap)ReferenceTagLists.GetValue(original)) },
            { "stringTags", new JsonNode() },
            { "clientApiStandard", GameUtils.IsStandardCardSet(set) }, { "clientApiWildOnly", GameUtils.IsWildCardSet(set) }
        };
        if (definition.GetCardType() == TAG_CARDTYPE.WEAPON)
            row.Add("durability", definition.GetHealth());
        if (definition.GetCardType() == TAG_CARDTYPE.HERO || original.GetTags().GetMap().ContainsKey((int)GAME_TAG.ARMOR))
            row.Add("armor", definition.GetArmor());
        return row;
    }
}
#endif
