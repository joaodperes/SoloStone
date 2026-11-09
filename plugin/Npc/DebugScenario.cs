using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using EngineCards = SabberStoneCore.Model.Cards;
#if ENGINE_SOURCE
using EngineGame = SabberStoneCore.Model.Game;
using EngineController = SabberStoneCore.Model.Entities.Controller;
using EngineEntity = SabberStoneCore.Model.Entities.Entity;
#endif

// One-shot debug scenario (docs/agent-workflows/steam-deck.md, `deck.py scenario`): stages the NEXT match so a live run can
// show a specific situation (the NPC drawing Illusion first, a human with Fyrakk on turn 1, ...). The file is key=value lines:
//   npcDeckTop=ID,ID   humanDeckTop=ID,...   (new hidden deck entities, drawn in the listed order)
//   humanOpeningHand=ID,... npcOpeningHand=ID,... (replace opening slots before mulligan offers; Coin excluded)
//   npcHand=ID,...     humanHand=ID,...      (new cards added to the hand before turn 1)
//   npcBoard=ID,...    humanBoard=ID,...     (up to seven minions/locations before turn 1)
//   npcMana=N          humanMana=N           (mana crystals on that player's FIRST turn, full)
//   runId=<this launch's run id>   (REQUIRED by Read: the file is bound to one launch, like enable-test-control)
// ENGINE_SOURCE builds only (the Deck and lane builds); other builds ignore the file. A file is honoured only while test
// control is live and its runId is this launch's; anything else is rejected (renamed .rejected-<utc>), and any file left over
// from an earlier launch is deleted at plugin start (ClearStale). The match is flagged on its pending
// record (LocalMatchRecord.DebugScenario) so it never settles: see LocalProfile.Settle. Applied after both mulligans, before
// main-begin. OpeningHand overrides apply before offers; "first draw" remains exact.
public sealed class DebugScenario
{
#if SOLOSTONE_RELEASE
    public static string Path = null;
#else
    public static string Path = @"C:\hs-native-ui-"+@"prototype\reports\debug-scenario";
#endif
    public const int MaxDeckTop = 10, MaxHand = 5, MaxBoard = 7, MaxMana = 10;
    // This launch's run id (set by the plugin at start); Read requires the file's runId to equal it.
    public static string RunId;
    public sealed class Outcome
    {
        public string Status { get; internal set; }
        public string Detail { get; internal set; }
        public string RunId { get; internal set; }
    }
    public static Outcome LastOutcome { get; private set; }
    internal static void RecordOutcome(string status, string detail, string runId) =>
        LastOutcome = new Outcome { Status = status, Detail = detail, RunId = runId };
    private static readonly string[] Keys = { "npcDeckTop", "humanDeckTop", "humanOpeningHand", "npcOpeningHand", "npcHand", "humanHand", "npcBoard", "humanBoard", "npcMana", "humanMana", "humanSkin", "npcSkin", "heroAttack", "heroPower", "heroPowerUpgrade", "attackSecret", "attackArmor", "attackWeapon", "attackLethal", "humanSeat", "overdraw", "runId" };

    public IReadOnlyList<string> NpcDeckTop { get; private set; } = new string[0];
    public IReadOnlyList<string> HumanDeckTop { get; private set; } = new string[0];
    public IReadOnlyList<string> NpcHand { get; private set; } = new string[0];
    public IReadOnlyList<string> HumanHand { get; private set; } = new string[0];
    public IReadOnlyList<string> HumanOpeningHand { get; private set; } = new string[0];
    public IReadOnlyList<string> NpcOpeningHand { get; private set; } = new string[0];
    public string Overdraw { get; private set; }
    public string HumanSkin { get; private set; }
    public string NpcSkin { get; private set; }
    public string HeroAttack { get; private set; }
    public string HeroPower { get; private set; }
    public string HeroPowerUpgrade { get; private set; }
    public string AttackSecret { get; private set; }
    public string AttackWeapon { get; private set; }
    public int AttackArmor { get; private set; }
    public bool AttackLethal { get; private set; }
    public IReadOnlyList<string> NpcBoard { get; private set; } = new string[0];
    public IReadOnlyList<string> HumanBoard { get; private set; } = new string[0];
    public int? NpcMana { get; private set; }
    public int? HumanMana { get; private set; }
    public int? FirstPlayer { get; private set; }
    private static int? Seat(Dictionary<string, string> values)
    {
        if (!values.TryGetValue("humanSeat", out string seat)) return null;
        if (seat == "first") return 1;
        if (seat == "second") return 2;
        throw new ArgumentException("humanSeat must be first or second.");
    }
    // The launch the file names (not part of Canonical: a record's replay does not need it).
    public string BoundRunId { get; private set; }
    // The file this scenario was read from (null for Parse); Consume retires it.
    public string SourcePath { get; private set; }

    // known: extra id check, e.g. the native client's own card definitions. Throws ArgumentException on the first problem;
    // nothing is partially accepted.
    public static DebugScenario Parse(string text, Func<string, bool> known = null)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string row in (text ?? "").Split('\n'))
        {
            string line = row.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int split = line.IndexOf('=');
            if (split <= 0 || split == line.Length - 1) throw new ArgumentException("Expected key=value, got: " + line);
            string key = line.Substring(0, split).Trim(), value = line.Substring(split + 1).Trim();
            if (Array.IndexOf(Keys, key) < 0) throw new ArgumentException("Unknown key: " + key + " (known: " + string.Join(", ", Keys) + ")");
            if (values.ContainsKey(key)) throw new ArgumentException("Duplicate key: " + key);
            values.Add(key, value);
        }
        if (values.Count == 0) throw new ArgumentException("Empty scenario.");
        string boundRun = null;
        if (values.TryGetValue("runId", out string run))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(run, "^[0-9A-Fa-f-]{8,64}$")) throw new ArgumentException("runId is not a run id.");
            boundRun = run;
            values.Remove("runId");
            if (values.Count == 0) throw new ArgumentException("Empty scenario.");
        }
        return new DebugScenario {
            BoundRunId = boundRun, FirstPlayer = Seat(values),
            Overdraw = Choice(values, "overdraw", "human", "npc", "human-godfrey", "npc-godfrey"),
            HumanSkin = Skin(values, "humanSkin", known), NpcSkin = Skin(values, "npcSkin", known),
            HeroAttack = Choice(values, "heroAttack", "human", "npc"),
            HeroPower = Choice(values, "heroPower", "human", "npc"),
            HeroPowerUpgrade = Choice(values, "heroPowerUpgrade", "human", "npc", "both"),
            AttackSecret = Choice(values, "attackSecret", "none", "CORE_EX1_289", "CORE_EX1_287", "EX1_610", "EX1_594"),
            AttackWeapon = Choice(values, "attackWeapon", "none", "TLC_478", "CS2_106"),
            AttackArmor = Number(values, "attackArmor", 0, 30), AttackLethal = Number(values, "attackLethal", 0, 1) == 1,
            NpcDeckTop = Cards(values, "npcDeckTop", MaxDeckTop, known), HumanDeckTop = Cards(values, "humanDeckTop", MaxDeckTop, known),
            HumanOpeningHand = Cards(values, "humanOpeningHand", 4, known), NpcOpeningHand = Cards(values, "npcOpeningHand", 4, known),
            NpcHand = Cards(values, "npcHand", MaxHand, known), HumanHand = Cards(values, "humanHand", MaxHand, known),
            NpcBoard = Cards(values, "npcBoard", MaxBoard, known), HumanBoard = Cards(values, "humanBoard", MaxBoard, known),
            NpcMana = Mana(values, "npcMana"), HumanMana = Mana(values, "humanMana") };
    }

    private static string Skin(Dictionary<string, string> values, string key, Func<string, bool> known)
    {
        if (!values.TryGetValue(key, out string id)) return null;
        if (!HeroSkinData.All.ContainsKey(id) || known != null && !known(id)) throw new ArgumentException(key + ": unknown hero skin " + id);
        return id;
    }
    private static string Choice(Dictionary<string, string> values, string key, params string[] allowed)
    {
        if (!values.TryGetValue(key, out string value)) return null;
        if (!allowed.Contains(value)) throw new ArgumentException(key + ": expected " + string.Join(", ", allowed));
        return value;
    }
    private static int Number(Dictionary<string, string> values, string key, int min, int max)
    {
        if (!values.TryGetValue(key, out string text)) return 0;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < min || n > max)
            throw new ArgumentException(key + ": out of range");
        return n;
    }

    private static IReadOnlyList<string> Cards(Dictionary<string, string> values, string key, int maximum, Func<string, bool> known)
    {
        if (!values.TryGetValue(key, out string text)) return new string[0];
        var ids = text.Split(',').Select(id => id.Trim()).ToArray();
        if (ids.Length > maximum) throw new ArgumentException(key + " takes at most " + maximum + " cards.");
        foreach (string id in ids)
        {
            if (id.Length == 0 || id.Length > 64 || id.Any(c => !(char.IsLetterOrDigit(c) || c == '_' || c == '-')))
                throw new ArgumentException(key + " has an invalid card id: '" + id + "'");
            SabberStoneCore.Model.Card card;
            try { card = EngineCards.FromId(id); }
            catch (KeyNotFoundException) { card = null; }
            if (card == null) throw new ArgumentException(key + ": unknown card id " + id);
            if (key.EndsWith("OpeningHand", StringComparison.Ordinal) && id == "GAME_005")
                throw new ArgumentException(key + ": the starting Coin cannot replace an opening card.");
            string type = card.Type.ToString();
            // Hero cards (Jaraxxus, Deathwing...) are played from hand; base hero portraits (cost 0) are not.
            bool heroCard = type == "HERO" && card.Collectible && card.Cost > 0;
            if (type != "MINION" && type != "SPELL" && type != "WEAPON" && type != "LOCATION" && !heroCard)
                throw new ArgumentException(key + ": " + id + " is a " + type + ", not a playable card.");
            if (key.EndsWith("Board", StringComparison.Ordinal) && type != "MINION" && type != "LOCATION")
                throw new ArgumentException(key + ": " + id + " is not a board minion or location.");
            if (known != null && !known(id)) throw new ArgumentException(key + ": " + id + " has no native client definition.");
        }
        return ids;
    }

    private static int? Mana(Dictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out string text)) return null;
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int mana) && mana >= 1 && mana <= MaxMana) return mana;
        throw new ArgumentException(key + " must be 1.." + MaxMana + ".");
    }

    // Stable text: the same scenario always prints, records and parses back identically (Replay re-parses it).
    public string Canonical()
    {
        var lines = new List<string>();
        void Add(string key, IReadOnlyList<string> ids) { if (ids.Count > 0) lines.Add(key + "=" + string.Join(",", ids)); }
        Add("npcDeckTop", NpcDeckTop); Add("humanDeckTop", HumanDeckTop); Add("npcHand", NpcHand); Add("humanHand", HumanHand);
        Add("humanOpeningHand", HumanOpeningHand); Add("npcOpeningHand", NpcOpeningHand);
        Add("npcBoard", NpcBoard); Add("humanBoard", HumanBoard);
        if (NpcMana.HasValue) lines.Add("npcMana=" + NpcMana.Value.ToString(CultureInfo.InvariantCulture));
        if (HumanMana.HasValue) lines.Add("humanMana=" + HumanMana.Value.ToString(CultureInfo.InvariantCulture));
        if (FirstPlayer.HasValue) lines.Add("humanSeat=" + (FirstPlayer == 1 ? "first" : "second"));
        if (Overdraw != null) lines.Add("overdraw=" + Overdraw);
        if (HumanSkin != null) lines.Add("humanSkin=" + HumanSkin);
        if (NpcSkin != null) lines.Add("npcSkin=" + NpcSkin);
        if (HeroAttack != null) lines.Add("heroAttack=" + HeroAttack);
        if (HeroPower != null) lines.Add("heroPower=" + HeroPower);
        if (HeroPowerUpgrade != null) lines.Add("heroPowerUpgrade=" + HeroPowerUpgrade);
        if (AttackSecret != null) lines.Add("attackSecret=" + AttackSecret);
        if (AttackWeapon != null) lines.Add("attackWeapon=" + AttackWeapon);
        if (AttackArmor != 0) lines.Add("attackArmor=" + AttackArmor);
        if (AttackLethal) lines.Add("attackLethal=1");
        return string.Join("\n", lines);
    }
    public string Describe() => Canonical().Replace("\n", " ");

    // Builds that cannot stage it (no engine source) never read the file, so a stray file there stays inert.
    // runId: this launch's run id; testControlLive: the test-control endpoint is up. A file for another launch (or none), or
    // read while test control is off, is rejected whole and retired. Rejected content bound to this live launch throws:
    // callers must refuse before BeginLaunch, never fall through into an ordinary match.
    public static DebugScenario Read(string path, Action<string> log, Func<string, bool> known, string runId, bool testControlLive)
    {
#if ENGINE_SOURCE
        path = path ?? Path;
        // A new stage must never report a previous match's applied scenario in this same launch.
        RecordOutcome("absent", "No scenario file for this match", runId);
        bool bound = false;
        string status = "unreadable";
        try
        {
            if (!File.Exists(path)) return null;
            var text = new System.Text.StringBuilder();
            using (var reader = File.OpenText(path))
            {
                string row;
                while ((row = reader.ReadLine()) != null)
                {
                    text.AppendLine(row);
                    int split = row.IndexOf('=');
                    // Establish intent before parsing: malformed content must not turn a test into a real match.
                    if (testControlLive && !string.IsNullOrEmpty(runId) && split > 0
                        && row.Substring(0, split).Trim() == "runId"
                        && string.Equals(row.Substring(split + 1).Trim(), runId, StringComparison.OrdinalIgnoreCase))
                        bound = true;
                }
            }
            status = "rejected";
            var scenario = Parse(text.ToString(), known);
            if (!testControlLive) throw new InvalidOperationException("test control is not live (deck.py control on first)");
            if (string.IsNullOrEmpty(runId)) throw new InvalidOperationException("this launch has no run id");
            if (scenario.BoundRunId == null) throw new InvalidOperationException("the file has no runId line (not bound to a launch)");
            if (!string.Equals(scenario.BoundRunId, runId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("the file's runId belongs to another launch");
            scenario.SourcePath = path;
            RecordOutcome("armed", scenario.Describe(), runId);
            log?.Invoke("DEBUG_SCENARIO armed " + scenario.Describe());
            return scenario;
        }
        catch (Exception error)
        {
            RecordOutcome(status, error.Message, runId);
            log?.Invoke("DEBUG_SCENARIO " + status + ", nothing applied: " + error.Message);
            Retire(path, "rejected", log);
            if (bound) throw new InvalidOperationException("Debug scenario refused: " + error.Message, error);
            return null;
        }
#else
        return null;
#endif
    }

    // Plugin start: no scenario file survives into a new launch (it can only name an earlier run id at best).
    public static void ClearStale(string path)
    {
        try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }

    // Renames the file away so it can never apply to a second match. Throws when it cannot (the caller refuses the start).
    public void Consume(Action<string> log = null)
    {
        if (SourcePath == null) return;
        string target = RetiredName(SourcePath, "applied");
        File.Move(SourcePath, target);
        log?.Invoke("DEBUG_SCENARIO consumed -> " + System.IO.Path.GetFileName(target));
        SourcePath = null;
    }

    private static string RetiredName(string path, string kind) =>
        path + "." + kind + "-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);

    private static void Retire(string path, string kind, Action<string> log)
    {
        try { File.Move(path, RetiredName(path, kind)); }
        catch (Exception error)
        {
            try { File.Delete(path); }
            catch (Exception) { log?.Invoke("DEBUG_SCENARIO file could not be retired: " + error.Message); }
        }
    }

#if ENGINE_SOURCE
    // Everything Apply needs, checked without touching the game: room for the new deck/hand cards (a hand card also leaves
    // space for that player's next draw, so it is not burned). Returns null when it fits, else the reason.
    public string Validate(EngineGame game)
    {
        string Fit(EngineController player, string name, int top, int hand)
        {
            if (top > 0 && player.DeckZone.FreeSpace < top) return name + " deck has no room for " + top + " more cards.";
            if (hand > 0 && player.HandZone.FreeSpace < hand + 1) return name + " hand has no room for " + hand + " more cards plus its draw.";
            return null;
        }
        foreach (string id in NpcDeckTop.Concat(HumanDeckTop).Concat(NpcHand).Concat(HumanHand).Concat(HumanOpeningHand).Concat(NpcOpeningHand).Concat(NpcBoard).Concat(HumanBoard))
            if (EngineCards.FromId(id) == null) return "unknown card " + id;
        if (HumanOpeningHand.Count > game.Player1.HandZone.Count(c => c.Card.Id != "GAME_005") ||
            NpcOpeningHand.Count > game.Player2.HandZone.Count(c => c.Card.Id != "GAME_005")) return "Opening hand has too few replaceable slots.";
        if (HumanSkin != null && HeroSkinData.All[HumanSkin].BaseHero != game.Player1.Hero.Card.Id) return "humanSkin class mismatch";
        if (NpcSkin != null && HeroSkinData.All[NpcSkin].BaseHero != game.Player2.Hero.Card.Id) return "npcSkin class mismatch";
        if (game.Player2.BoardZone.FreeSpace < NpcBoard.Count || game.Player1.BoardZone.FreeSpace < HumanBoard.Count) return "Board has no room for scenario bodies.";
        return Fit(game.Player2, "NPC", NpcDeckTop.Count, NpcHand.Count) ?? Fit(game.Player1, "Human", HumanDeckTop.Count, HumanHand.Count);
    }

    // After the normal deal, before either offer is sent/answered. Transform real dealt entities, never the Coin.
    public void ApplyOpening(EngineGame game)
    {
        if (HumanOpeningHand.Count == 0 && NpcOpeningHand.Count == 0) return;
        string problem = Validate(game);
        if (problem != null) throw new InvalidOperationException(problem);
        void Replace(EngineController player, IReadOnlyList<string> ids)
        {
            var slots = player.HandZone.Where(c => c.Card.Id != "GAME_005").Take(ids.Count).ToArray();
            for (int i = 0; i < ids.Count; i++)
                SabberStoneCore.Actions.Generic.ChangeEntityBlock(player, slots[i], EngineCards.FromId(ids[i]), false);
        }
        Replace(game.Player1, HumanOpeningHand); Replace(game.Player2, NpcOpeningHand);
    }

    // Game is paused at BEGIN_MULLIGAN with both mulligans DONE; main-begin has not run. Returns the log summary.
    // Deterministic given the game, so Replay reproduces it exactly (it calls this at the same point).
    public string Apply(EngineGame game)
    {
        string problem = Validate(game);
        if (problem != null) throw new InvalidOperationException(problem);
        var applied = new List<string>();
        // Last in the zone = next drawn, so the listed order is added in reverse.
        void Top(EngineController player, IReadOnlyList<string> ids, string name)
        {
            if (ids.Count == 0) return;
            for (int i = ids.Count - 1; i >= 0; i--) EngineEntity.FromCard(player, EngineCards.FromId(ids[i]), zone: player.DeckZone);
            applied.Add(name + "=" + string.Join(",", ids) + " (deck " + player.DeckZone.Count + ")");
        }
        void Hand(EngineController player, IReadOnlyList<string> ids, string name)
        {
            if (ids.Count == 0) return;
            // Retail finds the human's opening Coin by its final hand position during the mulligan animation.
            var coin = player == game.Player1 && game.FirstPlayer != player
                ? player.HandZone.FirstOrDefault(card => card.Card.Id == "GAME_005") : null;
            foreach (string id in ids) {
                var card = EngineEntity.FromCard(player, EngineCards.FromId(id), zone: player.HandZone);
                if (coin != null) player.HandZone.Swap(coin, card);
            }
            applied.Add(name + "=" + string.Join(",", ids) + " (hand " + player.HandZone.Count + ")");
        }
        void Board(EngineController player, IReadOnlyList<string> ids, string name)
        {
            foreach (string id in ids)
            {
                var body = EngineEntity.FromCard(player, EngineCards.FromId(id));
                if (body is SabberStoneCore.Model.Entities.Minion minion)
                    SabberStoneCore.Actions.Generic.SummonBlock(game, minion, -1, null);
                else player.BoardZone.Add((SabberStoneCore.Model.Entities.BoardObject)body);
            }
            if (ids.Count > 0) applied.Add(name + "=" + string.Join(",", ids));
        }
        void Mana(EngineController player, int? mana, string name)
        {
            if (!mana.HasValue) return;
            // The first turn's own +1 crystal brings it to the asked amount, with all of it available.
            player.BaseMana = mana.Value - 1;
            applied.Add(name + "=" + mana.Value);
        }
        Top(game.Player2, NpcDeckTop, "npcDeckTop"); Top(game.Player1, HumanDeckTop, "humanDeckTop");
        Hand(game.Player2, NpcHand, "npcHand"); Hand(game.Player1, HumanHand, "humanHand");
        if (Overdraw != null)
        {
            var player = Overdraw.StartsWith("npc", StringComparison.Ordinal) ? game.Player2 : game.Player1;
            while (!player.HandZone.IsFull)
                EngineEntity.FromCard(player, EngineCards.FromId("CS2_182"), zone: player.HandZone);
            if (Overdraw.EndsWith("-godfrey", StringComparison.Ordinal))
            {
                if (player.AppliedEnchantments?.Any(e => e.Card.Id == "JAIL_509e") != true)
                    SabberStoneCore.Actions.QuestRules.AddPlayerEffect(player, player.Hero, "JAIL_509e");
                player.Hero[(SabberStoneCore.Enums.GameTag)4717] = 1;
            }
            applied.Add("overdraw=" + Overdraw);
        }
        Board(game.Player2, NpcBoard, "npcBoard"); Board(game.Player1, HumanBoard, "humanBoard");
        Mana(game.Player2, NpcMana, "npcMana"); Mana(game.Player1, HumanMana, "humanMana");
        foreach (var player in new[] { game.Player1, game.Player2 })
        {
            string seat = player == game.Player1 ? "human" : "npc";
            if (HeroPower == seat && !(seat == "human" ? HumanMana : NpcMana).HasValue) player.BaseMana = 9;
            if (HeroPowerUpgrade == seat || HeroPowerUpgrade == "both")
            {
                int upgrade = player.Hero.HeroPower.Card[(SabberStoneCore.Enums.GameTag)1086];
                if (upgrade != 0)
                    new SabberStoneCore.Tasks.SimpleTasks.ReplaceHeroPower(EngineCards.FromAssetId(upgrade))
                        .Process(game, player, player.Hero, null);
                applied.Add("heroPowerUpgrade=" + seat + ":" + player.Hero.HeroPower.Card.Id);
            }
        }
        if (HeroPower != null) applied.Add("heroPower=" + HeroPower);
        if (HeroAttack != null)
        {
            var attacker = HeroAttack == "human" ? game.Player1 : game.Player2;
            var defender = attacker.Opponent;
            if (AttackWeapon != null && AttackWeapon != "none")
            {
                var weapon = (SabberStoneCore.Model.Entities.Weapon)EngineEntity.FromCard(attacker, EngineCards.FromId(AttackWeapon));
                attacker.Hero.AddWeapon(weapon);
                weapon.Card.Power?.Trigger?.Activate(weapon);
                weapon.Card.Power?.Aura?.Activate(weapon);
            }
            else attacker.Hero[SabberStoneCore.Enums.GameTag.ATK] = 3;
            defender.Hero.Armor = AttackArmor;
            if (AttackLethal) defender.Hero.Damage = defender.Hero.BaseHealth - 1;
            if (AttackSecret != null && AttackSecret != "none")
            {
                var secret = EngineEntity.FromCard(defender, EngineCards.FromId(AttackSecret), zone: defender.SecretZone);
                secret.Card.Power?.Trigger?.Activate(secret);
            }
            applied.Add("heroAttack=" + HeroAttack + "; weapon=" + (AttackWeapon ?? "none")
                + "; secret=" + (AttackSecret ?? "none") + "; armor=" + AttackArmor + "; lethal=" + AttackLethal);
        }
        if (HumanSkin != null) applied.Add("humanSkin=" + HumanSkin);
        if (NpcSkin != null) applied.Add("npcSkin=" + NpcSkin);
        return string.Join("; ", applied);
    }
#endif
}
