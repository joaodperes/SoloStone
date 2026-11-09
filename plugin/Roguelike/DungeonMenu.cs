using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using SoloStone.Roguelike;
using UnityEngine;

// Optional native overlay, not a replacement for the player's existing Hearthstone menus.
// F8 from Hub or deck picker opens it. All progress is in a separate XML file per profile.
// The classic ladder, packs, inventory and gold are never changed by dungeon actions.
internal static class DungeonMenu
{
    private static readonly XmlSerializer serializer = new XmlSerializer(typeof(DungeonRun));
    private static string profilePath, savePath, error;
    private static DungeonRun run;
    private static bool visible;
    private static Vector2 scroll;
    private static GUIStyle button, text, title, small, box;
    internal static bool Visible => visible;
    internal static bool Available => LocalLadder.Profile != null && SceneMgr.IsInitialized()
        && (SceneMgr.Get().GetMode() == SceneMgr.Mode.HUB || SceneMgr.Get().GetMode() == SceneMgr.Mode.TOURNAMENT)
        && !SceneMgr.Get().IsTransitioning() && LocalLadder.Screen != "Starting" && LocalLadder.Screen != "Playing";

    internal static void Tick()
    {
        if (LocalLadder.Profile == null) { visible = false; return; }
        string next = LocalLadder.Profile.DungeonProfilePath;
        if (next != profilePath)
        {
            visible = false;
            run = null; error = null; profilePath = next;
            savePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(next)), "dungeon-run.xml");
            try { Load(); } catch (Exception e) { run = null; error = "Run save could not be loaded: " + e.Message; }
        }
        if (Available && Input.GetKeyDown(KeyCode.F8)) visible = !visible;
        if (visible && !Available && LocalLadder.Screen != "Menu") visible = false;
    }

    internal static void Open()
    {
        if (!Available) return;
        visible = true;
    }

    private static void Load()
    {
        if (!File.Exists(savePath)) return;
        using (var stream = new FileStream(savePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length > 2 * 1024 * 1024) throw new InvalidDataException("Run save is too large.");
            run = (DungeonRun)serializer.Deserialize(stream);
        }
        DungeonRules.Validate(run);
        if (run.Step == RunStep.Battle && run.AttemptId != null)
        {
            // An interrupted debug match has been discarded by the host on restart.
            // Do not fabricate a win or deduct a life: the player may replay the room.
            run.AttemptId = null;
            Save();
        }
    }

    private static void Save()
    {
        if (run == null) throw new InvalidOperationException("No dungeon run.");
        DungeonRules.Validate(run);
        Directory.CreateDirectory(Path.GetDirectoryName(savePath));
        string staging = savePath + ".tmp";
        try
        {
            using (var stream = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                serializer.Serialize(stream, run);
                stream.Flush(true);
            }
            if (File.Exists(savePath)) File.Replace(staging, savePath, savePath + ".bak");
            else File.Move(staging, savePath);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    private static void NewExpedition()
    {
        try
        {
            if (run == null || (run.Step != RunStep.Won && run.Step != RunStep.Lost))
                throw new InvalidOperationException("Finish your current run first.");
            if (File.Exists(savePath))
                File.Move(savePath, savePath + ".finished-" + run.Id + ".xml");
            run = null;
            scroll = Vector2.zero;
            error = null;
        }
        catch (Exception e) { error = e.Message; }
    }

    private static DungeonRun Snapshot(DungeonRun source)
    {
        if (source == null) return null;
        using (var stream = new MemoryStream())
        {
            serializer.Serialize(stream, source);
            stream.Position = 0;
            return (DungeonRun)serializer.Deserialize(stream);
        }
    }

    private static void Mutate(Action action)
    {
        DungeonRun previous = Snapshot(run);
        try { action(); Save(); error = null; }
        catch (Exception e) { run = previous; error = e.Message; }
    }

    internal static void RegisterBattle(string matchId)
    {
        DungeonRules.BeginAttempt(run, matchId);
        try { Save(); }
        catch { run.AttemptId = null; throw; }
    }

    internal static void CancelBattle(string matchId)
    {
        if (run == null || run.AttemptId != matchId) return;
        run.AttemptId = null;
        try { Save(); } catch (Exception e) { error = "Could not reset a failed battle launch: " + e.Message; }
    }

    internal static void BattleSettled(string matchId, string result, bool conceded)
    {
        if (run == null || run.AttemptId != matchId) return;
        var previous = Snapshot(run);
        try
        {
            DungeonRules.BattleResult(run, matchId, result == "Won" && !conceded, DungeonContent.Current.ForHero(run.Hero));
            Save();
            visible = true;
            error = null;
        }
        catch (Exception e)
        {
            // Restore the entire attempt checkpoint: the normal profile settlement must
            // not drop its Pending while a dungeon receipt has failed to persist.
            run = previous;
            error = "Dungeon result was not saved: " + e.Message;
            Debug.LogError(error);
            throw;
        }
    }

    private static void Styles()
    {
        if (button != null) return;
        button = new GUIStyle(GUI.skin.button) { fontSize = 17, wordWrap = true, alignment = TextAnchor.MiddleCenter };
        text = new GUIStyle(GUI.skin.label) { fontSize = 18, wordWrap = true };
        text.normal.textColor = Color.white;
        small = new GUIStyle(text) { fontSize = 14 };
        title = new GUIStyle(text) { fontSize = 29, fontStyle = FontStyle.Bold };
        box = new GUIStyle(GUI.skin.box) { fontSize = 16 };
    }

    internal static void Draw()
    {
        if (!visible || !Available || Event.current == null) return;
        Styles();
        var oldMatrix = GUI.matrix;
        var oldColor = GUI.color;
        GUI.matrix = Matrix4x4.Scale(new Vector3(Screen.width / 1280f, Screen.height / 720f, 1));
        GUI.depth = -3000;
        GUI.color = new Color(0.055f, 0.075f, 0.105f, 0.99f);
        GUI.DrawTexture(new Rect(0, 0, 1280, 720), Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUI.Label(new Rect(42, 28, 1000, 48), "THE DEEP DELVE", title);
        GUI.Label(new Rect(44, 82, 1100, 36), "A branching, offline Kobolds-inspired dungeon run. F8 to close.", small);
        if (GUI.Button(new Rect(1122, 32, 116, 40), "Close", button)) visible = false;
        if (error != null) { GUI.color = new Color(1, 0.5f, 0.5f); GUI.Label(new Rect(45, 666, 1180, 34), error, small); GUI.color = Color.white; }

        if (run == null) StartScreen();
        else
        {
            GUI.Box(new Rect(875, 135, 360, 505), "", box);
            GUI.Label(new Rect(895, 156, 320, 32), "YOUR EXPEDITION", title);
            GUI.Label(new Rect(895, 205, 320, 160), "Hero: " + run.Hero + "\nLives: " + run.Lives
                + "   Gold: " + run.Gold + "\nCards: " + run.Deck.Count
                + "   Rerolls: " + run.Rerolls + "\nBattles won: " + run.BattlesWon
                + "\nTreasures: " + string.Join(", ", run.Treasures), small);
            if (run.Step == RunStep.Map) { Map(); Context("Choose an available room on the dungeon map."); }
            else { Map(); RoomActions(); }
            if (run.Step == RunStep.Won || run.Step == RunStep.Lost)
            {
                GUI.Label(new Rect(905, 490, 300, 60), run.Step == RunStep.Won ? "THE DEEP DELVE CLEARED!" : "YOUR EXPEDITION HAS ENDED", text);
                if (GUI.Button(new Rect(905, 560, 300, 50), "New expedition", button))
                    NewExpedition();
            }
        }
        GUI.matrix = oldMatrix; GUI.color = oldColor;
    }

    private static void StartScreen()
    {
        GUI.Label(new Rect(60, 150, 1150, 40), "Choose a hero and begin with a starter deck. No cards are consumed from your collection.", text);
        var starters = LocalDecks.All.Where(d => LocalDecks.PlayerHeroes.Contains(d.Hero))
            .GroupBy(d => d.Hero).Select(g => g.First()).Take(11).ToArray();
        for (int i = 0; i < starters.Length; i++)
        {
            int col = i % 3, row = i / 3; var starter = starters[i];
            if (GUI.Button(new Rect(80 + col * 380, 218 + row * 83, 345, 67),
                starter.Name + "\n" + starter.CardIds.Count + " starter cards", button))
                Mutate(() => run = DungeonRules.Create(Guid.NewGuid().GetHashCode() & int.MaxValue, starter.Hero, starter.CardIds));
        }
    }

    private static void Map()
    {
        var available = new HashSet<int>(DungeonRules.Available(run).Select(n => n.Id));
        GUI.Box(new Rect(42, 135, 814, 505), "", box);
        scroll = GUI.BeginScrollView(new Rect(60, 154, 782, 462), scroll, new Rect(0, 0, 732, 18 * 74 + 30));
        for (int floor = 0; floor < DungeonRules.Acts * DungeonRules.FloorsPerAct; floor++)
        {
            int f = floor;
            int rowTop = 16 + floor * 74;
            GUI.Label(new Rect(5, rowTop + 14, 75, 25), "F" + (floor + 1), small);
            foreach (var node in run.Rooms.Where(r => r.Floor == floor))
            {
                var room = node;
                bool open = available.Contains(room.Id);
                bool done = run.Cleared.Contains(room.Id);
                string name = done ? "✓ " + room.Kind : room.Id == run.ActiveRoom ? "▶ " + room.Kind : room.Kind.ToString();
                var bounds = new Rect(93 + room.Lane * 206, rowTop, 190, 48);
                GUI.enabled = open;
                if (GUI.Button(bounds, name + (room.Kind == RoomKind.Boss ? " ★" : ""), button) && open)
                {
                    Mutate(() => DungeonRules.Enter(run, room.Id));
                }
                GUI.enabled = true;
            }
        }
        GUI.EndScrollView();
    }

    private static void Context(string note) =>
        GUI.Label(new Rect(905, 396, 300, 96), note, small);

    private static void RoomActions()
    {
        var room = run.Rooms.SingleOrDefault(r => r.Id == run.ActiveRoom);
        if (room == null) return;
        GUI.Label(new Rect(895, 364, 315, 40), room.Kind + " • Act " + (room.Floor / 6 + 1), text);
        if (run.Step == RunStep.Battle)
        {
            Context("Face an offline NPC. Losing costs one life; a defeated encounter can be retried while lives remain.");
            if (GUI.Button(new Rect(905, 530, 300, 52), "Enter battle", button))
            {
                try
                {
                    var enemy = DungeonContent.Current.EncounterFor(run);
                    if (LocalLadder.StartDungeonBattle(run, enemy))
                        visible = false;
                }
                catch (Exception e) { error = "Battle could not start: " + e.Message; }
            }
        }
        else if (run.Step == RunStep.Draft)
        {
            GUI.Label(new Rect(895, 385, 320, 35), "CHOOSE A CARD BUCKET", text);
            for (int i = 0; i < run.DraftOptions.Count; i++)
            {
                int n = i; var bucket = run.DraftOptions[i];
                string body = bucket.Name + "\n" + string.Join(" · ", bucket.Cards.Select(CardName));
                if (GUI.Button(new Rect(895, 421 + i * 57, 326, 53), body, new GUIStyle(button) { fontSize = 12 }))
                    Mutate(() => DungeonRules.Pick(run, n));
            }
            if (run.Rerolls > 0 && GUI.Button(new Rect(905, 604, 300, 37), "Reroll offers", button))
                Mutate(() => DungeonRules.Reroll(run, DungeonContent.Current.ForHero(run.Hero)));
        }
        else if (run.Step == RunStep.Event)
        {
            var evt = DungeonContent.Current.EventFor(run);
            Context(evt.Title + "\n" + evt.Text);
            for (int i = 0; i < 2; i++)
            {
                int n = i;
                if (GUI.Button(new Rect(905, 530 + 56 * i, 300, 48), evt.Choices[i], button))
                    Mutate(() => DungeonRules.Event(run, n, evt.Effects[n]));
            }
        }
        else if (run.Step == RunStep.Rest || run.Step == RunStep.Merchant || run.Step == RunStep.Treasure)
        {
            var treasure = run.Step == RunStep.Treasure ? DungeonContent.Current.TreasureFor(run) : null;
            Context(run.Step == RunStep.Rest ? "Campfire: heal one life or gain a draft reroll."
                : run.Step == RunStep.Merchant ? "Merchant: spend 60 dungeon gold to heal one life, or move on."
                : treasure.Title + "\n" + treasure.Description);
            if (GUI.Button(new Rect(905, 530, 300, 49), run.Step == RunStep.Rest ? "Heal"
                : run.Step == RunStep.Merchant ? "Buy healing (60 gold)" : "Claim treasure", button))
                Mutate(() => {
                    if (run.Step == RunStep.Rest) DungeonRules.Rest(run, true);
                    else if (run.Step == RunStep.Merchant) DungeonRules.Merchant(run, true);
                    else DungeonRules.Treasure(run, treasure.Id);
                });
            if (run.Step != RunStep.Treasure && GUI.Button(new Rect(905, 588, 300, 48),
                run.Step == RunStep.Rest ? "Gain reroll" : "Leave", button))
                Mutate(() => { if (run.Step == RunStep.Rest) DungeonRules.Rest(run, false); else DungeonRules.Merchant(run, false); });
        }
    }

    private static string CardName(string id)
    {
        try { return SabberStoneCore.Model.Cards.FromId(id)?.Name ?? id; }
        catch { return id; }
    }
}
