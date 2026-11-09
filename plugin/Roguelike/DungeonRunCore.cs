using System;
using System.Collections.Generic;
using System.Linq;

namespace SoloStone.Roguelike
{
    // No client, Unity, filesystem or clock dependencies. The exact same seed gives the same map.
    // Keep this file usable from the launcher test runner without a Hearthstone installation.
    public enum RoomKind { Battle, Elite, Boss, Event, Rest, Merchant, Treasure }
    public enum RunStep { Map, Battle, Event, Rest, Merchant, Treasure, Draft, Won, Lost }

    public sealed class Room
    {
        public int Id;
        public int Floor;
        public int Lane;
        public RoomKind Kind;
        public string Encounter;
        public List<int> Exits = new List<int>();
    }

    public sealed class Bucket
    {
        public string Id;
        public string Name;
        public string Family;
        public int Weight = 1;
        public List<string> Cards = new List<string>();
    }

    // Public fields and parameterless constructors are intentional: XML serializers can persist runs
    // without touching SoloStone's profile.xml, ladder receipt or currency ledger.
    public sealed class DungeonRun
    {
        public int Schema = 1;
        public string Id;
        public int Seed;
        public string Hero;
        public List<string> Deck = new List<string>();
        public List<string> Treasures = new List<string>();
        public List<Room> Rooms = new List<Room>();
        public List<int> Cleared = new List<int>();
        public int CompletedRoom = -1;
        public int ActiveRoom = -1;
        public string AttemptId;
        public string LastSettledAttempt;
        public RunStep Step = RunStep.Map;
        public int Lives = 3;
        public int Gold;
        public int Rerolls = 1;
        public int BattlesWon;
        public int EventsVisited;
        public List<Bucket> DraftOptions = new List<Bucket>();
        public int RewardSequence;
    }

    // A small stable PRNG instead of System.Random: deterministic across Mono/.NET versions,
    // and generated worlds never depend on effects, pack draws or any live game RNG.
    public sealed class DungeonRandom
    {
        private uint state;
        public DungeonRandom(int seed) { state = (uint)seed; if (state == 0) state = 0x9e3779b9; }
        public uint Next() { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return state; }
        public int Next(int exclusive) {
            if (exclusive < 1) throw new ArgumentOutOfRangeException(nameof(exclusive));
            return (int)(Next() % (uint)exclusive);
        }
    }

    public static class DungeonRules
    {
        public const int Acts = 3, FloorsPerAct = 6, MaxDeck = 70, MaxLives = 5;
        private static readonly RoomKind[][] Pattern = {
            new[] { RoomKind.Battle, RoomKind.Battle, RoomKind.Battle },
            new[] { RoomKind.Battle, RoomKind.Event, RoomKind.Battle },
            new[] { RoomKind.Elite, RoomKind.Merchant, RoomKind.Event },
            new[] { RoomKind.Rest, RoomKind.Battle, RoomKind.Treasure },
            new[] { RoomKind.Elite, RoomKind.Event, RoomKind.Merchant }
        };
        private static readonly string[][] Bosses = {
            new[] { "The Candlekeeper", "Buried Guardian", "Grimstone Keeper" },
            new[] { "Fungal Overmind", "The Hoardmaster", "Mirror Sentinel" },
            new[] { "The Forgotten King", "Heart of the Mountain", "The Last Kobold" }
        };

        public static DungeonRun Create(int seed, string hero, IEnumerable<string> starterDeck)
        {
            var cards = (starterDeck ?? Enumerable.Empty<string>()).ToList();
            if (string.IsNullOrWhiteSpace(hero) || cards.Count < 10 || cards.Count > MaxDeck
                || cards.Any(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException("A Dungeon Run requires a hero and 10-70 valid starting cards.");
            var run = new DungeonRun { Id = Guid.NewGuid().ToString("N"), Seed = seed, Hero = hero, Deck = cards };
            var rng = new DungeonRandom(seed);
            int id = 0;
            for (int floor = 0; floor < Acts * FloorsPerAct; floor++)
            {
                int inAct = floor % FloorsPerAct, act = floor / FloorsPerAct;
                if (inAct == 5)
                {
                    run.Rooms.Add(new Room { Id = id++, Floor = floor, Lane = 1, Kind = RoomKind.Boss,
                        Encounter = Bosses[act][rng.Next(Bosses[act].Length)] });
                }
                else
                {
                    var kinds = Pattern[inAct].ToArray();
                    // Fixed combat at the start of each act; rotate other lane assignments for variety.
                    if (inAct != 0)
                        for (int j = kinds.Length - 1; j > 0; j--)
                        {
                            int k = rng.Next(j + 1);
                            var temp = kinds[j]; kinds[j] = kinds[k]; kinds[k] = temp;
                        }
                    for (int lane = 0; lane < 3; lane++)
                        run.Rooms.Add(new Room { Id = id++, Floor = floor, Lane = lane, Kind = kinds[lane],
                            Encounter = kinds[lane].ToString() + " " + (act + 1) + "." + (inAct + 1) });
                }
            }
            foreach (Room room in run.Rooms)
            {
                var next = run.Rooms.Where(n => n.Floor == room.Floor + 1).ToArray();
                if (next.Length == 0) continue;
                if (room.Kind == RoomKind.Boss) room.Exits.AddRange(next.Select(n => n.Id));
                else if (next.Length == 1) room.Exits.Add(next[0].Id);
                else
                {
                    room.Exits.Add(next.Single(n => n.Lane == room.Lane).Id);
                    int otherLane = room.Lane == 0 ? 1 : room.Lane == 2 ? 1 : rng.Next(2) == 0 ? 0 : 2;
                    room.Exits.Add(next.Single(n => n.Lane == otherLane).Id);
                }
            }
            Validate(run);
            return run;
        }

        public static IReadOnlyList<Room> Available(DungeonRun run)
        {
            if (run.Step != RunStep.Map) return new Room[0];
            if (run.CompletedRoom == -1) return run.Rooms.Where(room => room.Floor == 0).ToArray();
            Room previous = run.Rooms.Single(room => room.Id == run.CompletedRoom);
            return run.Rooms.Where(room => previous.Exits.Contains(room.Id)).ToArray();
        }

        public static void Enter(DungeonRun run, int roomId)
        {
            if (!Available(run).Any(room => room.Id == roomId))
                throw new InvalidOperationException("That dungeon room is not reachable.");
            var room = run.Rooms.Single(n => n.Id == roomId);
            run.ActiveRoom = room.Id;
            run.Step = room.Kind == RoomKind.Battle || room.Kind == RoomKind.Elite || room.Kind == RoomKind.Boss
                ? RunStep.Battle : room.Kind == RoomKind.Rest ? RunStep.Rest
                : room.Kind == RoomKind.Event ? RunStep.Event
                : room.Kind == RoomKind.Merchant ? RunStep.Merchant : RunStep.Treasure;
        }

        public static void BeginAttempt(DungeonRun run, string matchId)
        {
            if (run.Step != RunStep.Battle || run.AttemptId != null || string.IsNullOrWhiteSpace(matchId))
                throw new InvalidOperationException("No battle is awaiting a match.");
            run.AttemptId = matchId;
        }

        // Return false for a replayed completion receipt (e.g. native result UI re-firing).
        public static bool BattleResult(DungeonRun run, string matchId, bool won,
            IReadOnlyList<Bucket> catalog)
        {
            if (run.LastSettledAttempt == matchId) return false;
            if (run.Step != RunStep.Battle || run.AttemptId != matchId || string.IsNullOrWhiteSpace(matchId))
                throw new InvalidOperationException("Battle completion does not match the active attempt.");
            run.LastSettledAttempt = matchId;
            run.AttemptId = null;
            if (!won)
            {
                run.Lives--;
                if (run.Lives <= 0) run.Step = RunStep.Lost;
                return true; // same room can be retried while lives remain
            }
            run.BattlesWon++;
            var room = run.Rooms.Single(n => n.Id == run.ActiveRoom);
            run.Gold += room.Kind == RoomKind.Boss ? 90 : room.Kind == RoomKind.Elite ? 55 : 25;
            if (room.Kind == RoomKind.Boss && room.Floor == Acts * FloorsPerAct - 1)
            {
                Complete(run);
                run.Step = RunStep.Won;
                return true;
            }
            var options = RewardOptions(run, catalog);
            run.DraftOptions = options.ToList();
            if (room.Kind == RoomKind.Boss) { run.Lives = Math.Min(MaxLives, run.Lives + 1); run.Rerolls++; }
            run.Step = run.DraftOptions.Count == 0 ? RunStep.Map : RunStep.Draft;
            if (run.Step == RunStep.Map) Complete(run);
            return true;
        }

        public static IReadOnlyList<Bucket> RewardOptions(DungeonRun run, IReadOnlyList<Bucket> catalog)
        {
            var eligible = (catalog ?? new Bucket[0]).Where(b => b != null && b.Weight > 0 && b.Cards != null
                && b.Cards.Count == 3 && b.Cards.All(s => !string.IsNullOrWhiteSpace(s))
                && b.Cards.Count + run.Deck.Count <= MaxDeck).ToList();
            var rng = new DungeonRandom(unchecked(run.Seed + run.ActiveRoom * 1009 + run.RewardSequence++ * 7919));
            var result = new List<Bucket>();
            while (eligible.Count > 0 && result.Count < 3)
            {
                int total = eligible.Sum(b => b.Weight);
                int selected = rng.Next(total), index = 0;
                for (; index < eligible.Count - 1; index++) { selected -= eligible[index].Weight; if (selected < 0) break; }
                result.Add(eligible[index]);
                eligible.RemoveAt(index);
            }
            return result;
        }

        public static void Reroll(DungeonRun run, IReadOnlyList<Bucket> catalog)
        {
            if (run.Step != RunStep.Draft || run.Rerolls <= 0)
                throw new InvalidOperationException("No reroll is available.");
            run.Rerolls--;
            run.DraftOptions = RewardOptions(run, catalog).ToList();
        }

        public static void Pick(DungeonRun run, int index)
        {
            if (run.Step != RunStep.Draft || index < 0 || index >= run.DraftOptions.Count)
                throw new InvalidOperationException("No such card bucket is offered.");
            run.Deck.AddRange(run.DraftOptions[index].Cards);
            run.DraftOptions.Clear();
            Complete(run);
        }

        // The events are intentionally out-of-combat for v1: they require no invented client tags.
        public static void Event(DungeonRun run, int choice)
        {
            if (run.Step != RunStep.Event || choice < 0 || choice > 1)
                throw new InvalidOperationException("No such event choice.");
            if (choice == 0) { run.Gold += 40; }
            else { run.Lives = Math.Max(1, run.Lives - 1); run.Rerolls += 2; }
            run.EventsVisited++;
            Complete(run);
        }

        public static void Rest(DungeonRun run, bool heal)
        {
            if (run.Step != RunStep.Rest) throw new InvalidOperationException("Not at a campfire.");
            if (heal) run.Lives = Math.Min(MaxLives, run.Lives + 1);
            else run.Rerolls++;
            Complete(run);
        }

        public static void Merchant(DungeonRun run, bool buy)
        {
            if (run.Step != RunStep.Merchant) throw new InvalidOperationException("Not at a merchant.");
            if (buy)
            {
                if (run.Gold < 60) throw new InvalidOperationException("You need 60 dungeon gold.");
                run.Gold -= 60; run.Lives = Math.Min(MaxLives, run.Lives + 1);
            }
            Complete(run);
        }

        public static void Treasure(DungeonRun run, string treasureId)
        {
            if (run.Step != RunStep.Treasure || string.IsNullOrWhiteSpace(treasureId))
                throw new InvalidOperationException("No treasure is available.");
            // A unique treasure cannot be farmed by revisiting nodes.
            if (!run.Treasures.Contains(treasureId)) run.Treasures.Add(treasureId);
            if (treasureId == "spare-candle") run.Lives = Math.Min(MaxLives, run.Lives + 1);
            if (treasureId == "lucky-pick") run.Rerolls += 2;
            if (treasureId == "coin-purse") run.Gold += 75;
            Complete(run);
        }

        private static void Complete(DungeonRun run)
        {
            if (run.ActiveRoom < 0 || run.Cleared.Contains(run.ActiveRoom))
                throw new InvalidOperationException("Room already cleared or not selected.");
            run.Cleared.Add(run.ActiveRoom);
            run.CompletedRoom = run.ActiveRoom;
            run.ActiveRoom = -1;
            run.DraftOptions.Clear();
            run.Step = RunStep.Map;
        }

        public static void Validate(DungeonRun run)
        {
            if (run == null || run.Schema != 1 || run.Id == null || run.Hero == null || run.Rooms == null
                || run.Deck == null || run.Deck.Count < 10 || run.Deck.Count > MaxDeck || run.Lives < 0 || run.Lives > MaxLives
                || run.Rooms.Select(room => room.Id).Distinct().Count() != run.Rooms.Count
                || run.Rooms.Count != Acts * (5 * 3 + 1))
                throw new InvalidOperationException("Invalid dungeon run state.");
            foreach (Room room in run.Rooms)
            {
                if (room.Floor < 0 || room.Floor >= Acts * FloorsPerAct
                    || room.Exits == null || room.Exits.Any(id => !run.Rooms.Any(n => n.Id == id && n.Floor == room.Floor + 1)))
                    throw new InvalidOperationException("Invalid dungeon map edge.");
                if (room.Floor != Acts * FloorsPerAct - 1 && room.Exits.Count == 0)
                    throw new InvalidOperationException("Dead end on dungeon map.");
            }
            if (run.Cleared.Distinct().Count() != run.Cleared.Count ||
                run.Cleared.Any(id => !run.Rooms.Any(n => n.Id == id)))
                throw new InvalidOperationException("Invalid cleared room ledger.");
            if (run.Step == RunStep.Battle && !run.Rooms.Any(room => room.Id == run.ActiveRoom
                && (room.Kind == RoomKind.Battle || room.Kind == RoomKind.Elite || room.Kind == RoomKind.Boss)))
                throw new InvalidOperationException("Invalid battle checkpoint.");
            if (run.Step == RunStep.Draft && (run.DraftOptions == null || run.DraftOptions.Count == 0))
                throw new InvalidOperationException("Invalid draft checkpoint.");
        }
    }
}
