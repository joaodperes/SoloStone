using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MiniJSON;
using SoloStone.Roguelike;

// Static, bundled metadata; never downloads third-party decks or game assets.
// Parsing is read-only and deliberately independent of the local shop/collection ledger.
internal sealed class DungeonContent
{
    internal sealed class EventInfo
    {
        internal string Id, Title, Text;
        internal string[] Choices, Effects;
    }
    internal sealed class TreasureInfo
    {
        internal string Id, Title, Description;
        internal bool Implemented;
    }
    internal sealed class EncounterInfo
    {
        internal string Id, Title, DeckId, Hero, Archetype;
        internal int Tier, Act;
    }
    internal readonly List<Bucket> Buckets = new List<Bucket>();
    internal readonly List<EventInfo> Events = new List<EventInfo>();
    internal readonly List<TreasureInfo> Treasures = new List<TreasureInfo>();
    internal readonly List<EncounterInfo> Encounters = new List<EncounterInfo>();
    internal string[] ActNames;
    internal static DungeonContent Current { get { return current.Value; } }
    private static readonly Lazy<DungeonContent> current = new Lazy<DungeonContent>(Read);

    private static IDictionary<string, object> Obj(object value) =>
        value as IDictionary<string, object> ?? throw new InvalidDataException("Expected JSON object.");
    private static IList Arr(object value) => value as IList ?? throw new InvalidDataException("Expected JSON list.");
    private static string Str(IDictionary<string, object> d, string key) =>
        d.TryGetValue(key, out object v) && v is string s && s.Length > 0 ? s
        : throw new InvalidDataException("Missing or empty dungeon string: " + key);
    private static int Num(IDictionary<string, object> d, string key) =>
        d.TryGetValue(key, out object v) && v is long l && l >= int.MinValue && l <= int.MaxValue ? (int)l
        : throw new InvalidDataException("Missing dungeon integer: " + key);
    private static string[] Strings(object value) => Arr(value).Cast<object>()
        .Select(o => o as string ?? throw new InvalidDataException("Expected dungeon string.")).ToArray();
    private static string Optional(IDictionary<string, object> d, string key) =>
        d.TryGetValue(key, out object value) ? value as string : null;

    private static DungeonContent Read()
    {
        const string resource = "SoloStone.Roguelike.dungeon-content-v1.json";
        using (Stream stream = typeof(DungeonContent).Assembly.GetManifestResourceStream(resource))
        {
            if (stream == null) throw new InvalidDataException("Missing roguelike content: " + resource);
            string json;
            using (var reader = new StreamReader(stream)) json = reader.ReadToEnd();
            var root = Obj(Json.Deserialize(json));
            if (Num(root, "schemaVersion") != 1) throw new InvalidDataException("Unsupported dungeon content schema.");
            var library = new DungeonContent();
            library.ActNames = Strings(root["acts"]);
            if (library.ActNames.Length != DungeonRules.Acts) throw new InvalidDataException("Dungeon acts count mismatch.");
            foreach (object o in Arr(root["buckets"]))
            {
                var d = Obj(o);
                var bucket = new Bucket { Id = Str(d, "id"), Name = Str(d, "name"), Family = Str(d, "family"),
                    Cards = Strings(d["cards"]).ToList(), Weight = Num(d, "weight") };
                if (bucket.Cards.Count != 3 || bucket.Weight < 1 || !bucket.Family.StartsWith("HERO_", StringComparison.Ordinal))
                    throw new InvalidDataException("Invalid dungeon bucket " + bucket.Id);
                library.Buckets.Add(bucket);
            }
            foreach (object o in Arr(root["encounters"]))
            {
                var d = Obj(o);
                library.Encounters.Add(new EncounterInfo { Id = Str(d, "id"), Title = Str(d, "title"),
                    DeckId = Str(d, "deckId"), Hero = Str(d, "hero"), Archetype = Str(d, "archetype"),
                    Tier = Num(d, "tier"), Act = Num(d, "act") });
            }
            foreach (object o in Arr(root["events"]))
            {
                var d = Obj(o); var choices = Arr(d["choices"]).Cast<object>().Select(Obj).ToArray();
                if (choices.Length != 2) throw new InvalidDataException("Each event needs two choices.");
                library.Events.Add(new EventInfo { Id = Str(d, "id"), Title = Str(d, "title"),
                    Text = Str(d, "text"), Choices = choices.Select(c => Str(c, "title")).ToArray(),
                    Effects = choices.Select(c => Str(c, "effect")).ToArray() });
            }
            foreach (object o in Arr(root["treasures"]))
            {
                var d = Obj(o);
                library.Treasures.Add(new TreasureInfo { Id = Str(d, "id"), Title = Str(d, "title"),
                    Description = Str(d, "description"),
                    Implemented = d.TryGetValue("implemented", out object value) && value is bool b && b });
            }
            if (library.Buckets.Count < 50 || library.Events.Count < 8 || library.Encounters.Count < 20 ||
                library.Treasures.Count < 3 || library.Buckets.Select(b => b.Id).Distinct().Count() != library.Buckets.Count)
                throw new InvalidDataException("Dungeon content appears incomplete or duplicate.");
            return library;
        }
    }

    internal IReadOnlyList<Bucket> ForHero(string hero)
    {
        var eligible = Buckets.Where(b => b.Family == hero
            && b.Cards.All(id => {
                try { return LocalDecks.IsLegalDeckbuildingCard(id) && ClientBridge.ClientKnowsCard(id); }
                catch { return false; }
            })).ToArray();
        if (eligible.Length > 0) return eligible;
        // A changed client pool must not strand the player after winning an encounter.
        var starter = LocalDecks.All.FirstOrDefault(deck => deck.Hero == hero);
        if (starter == null) return new Bucket[0];
        var ids = starter.CardIds.Distinct(StringComparer.Ordinal).Where(ClientBridge.ClientKnowsCard).ToArray();
        var fallback = new List<Bucket>();
        for (int i = 0; i + 2 < ids.Length && fallback.Count < 5; i += 3)
            fallback.Add(new Bucket { Id = "starter/" + hero + "/" + i, Name = "Starter reserves",
                Family = hero, Weight = 1, Cards = ids.Skip(i).Take(3).ToList() });
        return fallback;
    }

    internal EventInfo EventFor(DungeonRun run)
    {
        var room = run.Rooms.Single(r => r.Id == run.ActiveRoom);
        return Events[new DungeonRandom(unchecked(run.Seed + room.Id * 3469)).Next(Events.Count)];
    }

    internal TreasureInfo TreasureFor(DungeonRun run)
    {
        var room = run.Rooms.Single(r => r.Id == run.ActiveRoom);
        var eligible = Treasures.Where(t => t.Implemented && !run.Treasures.Contains(t.Id)).ToArray();
        return eligible.Length == 0 ? Treasures.First(t => t.Implemented)
            : eligible[new DungeonRandom(unchecked(run.Seed + room.Id * 673)).Next(eligible.Length)];
    }

    internal LocalDeck EncounterFor(DungeonRun run)
    {
        var room = run.Rooms.Single(r => r.Id == run.ActiveRoom);
        // The current combat seam is known-good prebuilt NPC decks. A future encounter adapter
        // can bind Dungeon-specific hero powers without ever contacting the retail servers.
        var possible = LocalDecks.All.Concat(LocalDecks.NpcDecks).Where(d => {
            try { return d.Build() != null; } catch { return false; }
        }).ToArray();
        if (possible.Length == 0) throw new InvalidOperationException("No supported opponents are installed.");
        int act = room.Floor / DungeonRules.FloorsPerAct;
        var rng = new DungeonRandom(unchecked(run.Seed + room.Id * 8437 + act * 173));
        return possible[rng.Next(possible.Length)];
    }
}
