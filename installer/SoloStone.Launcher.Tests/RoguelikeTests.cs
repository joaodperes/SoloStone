using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using SoloStone.Roguelike;

static partial class Program
{
    static void RoguelikeTests(string temp)
    {
        var starter = Enumerable.Repeat("CS2_024", 30).ToArray();
        var one = DungeonRules.Create(314159, "HERO_08", starter);
        var two = DungeonRules.Create(314159, "HERO_08", starter);
        Check(one.Rooms.Count == 48, "dungeon: three acts of sixteen rooms");
        Check(one.Rooms.Count(r => r.Kind == RoomKind.Boss) == 3, "dungeon: one boss per act");
        Check(one.Rooms.Select(r => r.Kind).SequenceEqual(two.Rooms.Select(r => r.Kind))
            && one.Rooms.SelectMany(r => r.Exits).SequenceEqual(two.Rooms.SelectMany(r => r.Exits)),
            "dungeon: deterministic map for a seed");
        foreach (var node in one.Rooms)
        {
            if (node.Floor == 17) continue;
            var visited = new HashSet<int> { node.Id };
            var queue = new Queue<int>(); queue.Enqueue(node.Id);
            while (queue.Count > 0)
            {
                var current = one.Rooms.Single(r => r.Id == queue.Dequeue());
                foreach (int next in current.Exits)
                    if (visited.Add(next)) queue.Enqueue(next);
            }
            Check(visited.Any(id => one.Rooms.Single(r => r.Id == id).Floor == 17),
                "dungeon: no isolated room " + node.Id);
        }

        var start = DungeonRules.Available(one);
        Check(start.Count == 3 && start.All(r => r.Kind == RoomKind.Battle), "dungeon: three opening lanes");
        Refuses(() => DungeonRules.Enter(one, one.Rooms.Single(r => r.Floor == 5).Id),
            "dungeon: cannot skip to boss");
        DungeonRules.Enter(one, start[1].Id);
        Check(one.Step == RunStep.Battle, "dungeon: battle offered");
        var buckets = new List<Bucket> {
            new Bucket { Id = "a", Name = "A", Weight = 1, Cards = new List<string> { "A", "B", "C" } },
            new Bucket { Id = "b", Name = "B", Weight = 2, Cards = new List<string> { "D", "E", "F" } },
            new Bucket { Id = "c", Name = "C", Weight = 3, Cards = new List<string> { "G", "H", "I" } },
            new Bucket { Id = "d", Name = "D", Weight = 1, Cards = new List<string> { "J", "K", "L" } }
        };
        DungeonRules.BeginAttempt(one, "match1");
        Refuses(() => DungeonRules.BeginAttempt(one, "match2"), "dungeon: one match at a time");
        Refuses(() => DungeonRules.BattleResult(one, "wrong", true, buckets),
            "dungeon: reject other match result");
        Check(DungeonRules.BattleResult(one, "match1", true, buckets),
            "dungeon: first battle settlement succeeds");
        Check(!DungeonRules.BattleResult(one, "match1", true, buckets),
            "dungeon: duplicate result is idempotent");
        Check(one.Step == RunStep.Draft && one.DraftOptions.Count == 3,
            "dungeon: three distinct bucket rewards");
        Check(one.DraftOptions.Select(b => b.Id).Distinct().Count() == 3,
            "dungeon: no duplicate bucket choices");
        int firstRoom = one.ActiveRoom;
        int oldDeck = one.Deck.Count;
        DungeonRules.Pick(one, 0);
        Check(one.Step == RunStep.Map && one.Deck.Count == oldDeck + 3
            && one.CompletedRoom == firstRoom && one.Cleared.Contains(firstRoom),
            "dungeon: draft persists and advances the map");
        Check(DungeonRules.Available(one).Count == 2, "dungeon: branching successors");
        Refuses(() => DungeonRules.Enter(one, start[0].Id), "dungeon: cannot revisit an uncleared sibling");

        var loss = DungeonRules.Create(7, "HERO_01", starter);
        DungeonRules.Enter(loss, DungeonRules.Available(loss)[0].Id);
        for (int n = 0; n < 3; n++)
        {
            string match = "lost-" + n;
            DungeonRules.BeginAttempt(loss, match);
            DungeonRules.BattleResult(loss, match, false, buckets);
        }
        Check(loss.Lives == 0 && loss.Step == RunStep.Lost, "dungeon: three losses end the run");

        var eventRun = DungeonRules.Create(8, "HERO_04", starter);
        eventRun.Step = RunStep.Event; eventRun.ActiveRoom = eventRun.Rooms[0].Id;
        DungeonRules.Event(eventRun, 0, "gold:75,reroll:1");
        Check(eventRun.Gold == 75 && eventRun.Rerolls == 2 && eventRun.Step == RunStep.Map,
            "dungeon: event effect commits once");
        var unsafeRun = DungeonRules.Create(9, "HERO_04", starter);
        unsafeRun.Step = RunStep.Event; unsafeRun.ActiveRoom = unsafeRun.Rooms[0].Id;
        Refuses(() => DungeonRules.Event(unsafeRun, 0, "gold:10,bad:1"),
            "dungeon: unknown event effect refused before write");
        Check(unsafeRun.Gold == 0 && unsafeRun.Step == RunStep.Event,
            "dungeon: failed event choice leaves state unchanged");

        var treasure = DungeonRules.Create(11, "HERO_08", starter);
        treasure.Step = RunStep.Treasure; treasure.ActiveRoom = treasure.Rooms[0].Id;
        DungeonRules.Treasure(treasure, "king-key");
        Check(treasure.Lives == 5 && treasure.Rerolls == 2 && treasure.Treasures.Count == 1,
            "dungeon: treasure applies once");

        var file = Path.Combine(temp, "dungeon-roundtrip.xml");
        var serializer = new XmlSerializer(typeof(DungeonRun));
        using (var stream = File.Create(file)) serializer.Serialize(stream, one);
        DungeonRun restored;
        using (var stream = File.OpenRead(file)) restored = (DungeonRun)serializer.Deserialize(stream);
        DungeonRules.Validate(restored);
        Check(restored.Deck.SequenceEqual(one.Deck) && restored.CompletedRoom == one.CompletedRoom
            && restored.Rooms.SelectMany(r => r.Exits).SequenceEqual(one.Rooms.SelectMany(r => r.Exits)),
            "dungeon: XML checkpoint roundtrip");

        Check(DungeonRules.Create(6, "HERO_08", starter).Deck.Count == 30,
            "dungeon: independent starter state");
    }
}
