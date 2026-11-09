using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

public enum PlaytestKnob { Npc, On, Off }

// Test-only knobs are read once per match, never from the player's durable profile.
public sealed class PlaytestQueue
{
    // NPC World seam S10. Set by the world runtime when it is installed (null = golden catalog only, as before).
    // Resolves a world NPC id (npc.n.* notables, npc.g.* generated) to the profile that NPC plays with in a playtest.
    public static Func<string, NpcProfile> WorldProfileResolver { get; set; }
    private static NpcProfile ResolveWorld(string id)
    {
        if (id == null || !(id.StartsWith("npc.n.", StringComparison.Ordinal) || id.StartsWith("npc.g.", StringComparison.Ordinal)))
            return null;
        try
        {
            var profile = WorldProfileResolver?.Invoke(id);
            return profile != null && profile.Id == id ? profile : null;
        }
        catch (Exception) { return null; }
    }
#if SOLOSTONE_INSTALLER && !SOLOSTONE_TEST_HOOKS
    public static string KnobPath = null;
#else
    public static string KnobPath = @"C:\hs-native-ui-"+@"prototype\reports\playtest-opponent";
#endif
    public NpcProfile Profile { get; private set; }
    // Each presentation knob is Npc (default: the NPC's own world presentation, exactly as the matchmaker shows it),
    // On, or Off. Off/On/explicit card back ids keep their original meaning for tests.
    public PlaytestKnob EmotesMode { get; private set; } = PlaytestKnob.Npc;
    public PlaytestKnob HoverMode { get; private set; } = PlaytestKnob.Npc;
    public PlaytestKnob SkinMode { get; private set; } = PlaytestKnob.Npc;
    public PlaytestKnob CardBackMode { get; private set; } = PlaytestKnob.Npc;
    // True unless explicitly off (npc = what the world presentation decides, resolved in Presentation()).
    public bool Emotes => EmotesMode != PlaytestKnob.Off;
    public bool Hover => HoverMode != PlaytestKnob.Off;
    public bool Skin => SkinMode != PlaytestKnob.Off;
    // The explicit card back id (CardBackMode == On); null for npc/off.
    public int? CardBack { get; private set; }
    public int? TurnNodes { get; private set; }
    public int? Seed { get; private set; }
    public int SelectSeed(int generated) => Seed ?? generated;
    public bool NativeStartingTaunt { get; private set; } = true;
    // skinKnown / cardBackKnown / skinPowerKnown are the client's own lookups (as in the ladder); null accepts any id.
    public NpcPresentationSettings Presentation(Func<string, bool> skinKnown = null, Func<int, bool> cardBackKnown = null,
        Func<string, bool> skinPowerKnown = null)
    {
        var natural = NpcPresentationSettings.LadderDefaults(Profile, skinKnown, cardBackKnown, skinPowerKnown);
        return new NpcPresentationSettings {
            Emotes = Resolve(EmotesMode, natural.Emotes), Hover = Resolve(HoverMode, natural.Hover),
            Skin = Resolve(SkinMode, natural.Skin),
            CardBack = CardBackMode == PlaytestKnob.On || CardBackMode == PlaytestKnob.Npc && natural.CardBack,
            CardBackOverride = CardBackMode == PlaytestKnob.On ? CardBack : null,
            NativeStartingTaunt = NativeStartingTaunt };
    }
    private static bool Resolve(PlaytestKnob mode, bool natural) => mode == PlaytestKnob.On || mode == PlaytestKnob.Npc && natural;
    private static string Name(PlaytestKnob mode) => mode == PlaytestKnob.On ? "on" : mode == PlaytestKnob.Off ? "off" : "npc";
    public LocalDeck Opponent(bool proof) => proof ? null : LocalDecks.Get(Profile.DeckId);
    public Dictionary<string, object> Knobs => new Dictionary<string, object> {
        { "npc", Profile.Id }, { "emotes", Name(EmotesMode) }, { "hover", Name(HoverMode) },
        { "skin", Name(SkinMode) }, { "cardback", CardBack.HasValue ? (object)CardBack.Value : Name(CardBackMode) },
        { "turnNodes", TurnNodes ?? Profile.Budget.TurnNodes }, { "seed", Seed }, { "nativeStartingTaunt", NativeStartingTaunt ? "on" : "off" } };
    public static PlaytestQueue Read(string path = null, Action<string> log = null)
    {
#if SOLOSTONE_INSTALLER && !SOLOSTONE_TEST_HOOKS
#pragma warning disable CS0162
        return null; // An installer release takes no opponent from a file.
#endif
        path = path ?? KnobPath;
        try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : null; }
        catch (Exception error) { log?.Invoke("playtest-queue ignored: " + error.Message); return null; }
    }
    public static PlaytestQueue Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string row in (text ?? "").Split('\n'))
        {
            string line = row.Trim();
            if (line.Length == 0) continue;
            int split = line.IndexOf('=');
            if (split <= 0 || split == line.Length - 1) throw new ArgumentException("Expected key=value.");
            string key = line.Substring(0, split), value = line.Substring(split + 1);
            if (!new[] { "npc", "emotes", "hover", "skin", "cardback", "turnNodes", "nativeStartingTaunt", "seed" }.Contains(key)
                || values.ContainsKey(key)) throw new ArgumentException("Unknown or duplicate knob: " + key);
            values.Add(key, value);
        }
        string id;
        var profile = values.TryGetValue("npc", out id) ? NpcCatalog.All.SingleOrDefault(p => p.Id == id) ?? ResolveWorld(id) : null;
        if (profile == null) throw new ArgumentException("Unknown or missing NPC.");
        int? cardBack;
        return new PlaytestQueue { Profile = profile, EmotesMode = Mode(values, "emotes"), HoverMode = Mode(values, "hover"),
            SkinMode = Mode(values, "skin"), CardBackMode = CardBackKnob(values, out cardBack), CardBack = cardBack,
            TurnNodes = Positive(values, "turnNodes", false), Seed = Positive(values, "seed", false),
            NativeStartingTaunt = !values.ContainsKey("nativeStartingTaunt") || Toggle(values, "nativeStartingTaunt") };
    }
    private static bool Toggle(Dictionary<string, string> values, string key)
    {
        string value;
        if (!values.TryGetValue(key, out value) || value == "off") return false;
        if (value == "on") return true;
        throw new ArgumentException("Expected on/off: " + key);
    }
    private static PlaytestKnob Mode(Dictionary<string, string> values, string key)
    {
        string value;
        if (!values.TryGetValue(key, out value) || value == "npc") return PlaytestKnob.Npc;
        if (value == "on") return PlaytestKnob.On;
        if (value == "off") return PlaytestKnob.Off;
        throw new ArgumentException("Expected on/off/npc: " + key);
    }
    private static PlaytestKnob CardBackKnob(Dictionary<string, string> values, out int? id)
    {
        id = null;
        string value;
        if (!values.TryGetValue("cardback", out value) || value == "npc") return PlaytestKnob.Npc;
        if (value == "off") return PlaytestKnob.Off;
        id = Positive(values, "cardback", false);
        return PlaytestKnob.On;
    }
    private static int? Positive(Dictionary<string, string> values, string key, bool allowOff)
    {
        string value; int number;
        if (!values.TryGetValue(key, out value) || allowOff && value == "off") return null;
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0) return number;
        throw new ArgumentException("Expected positive 32-bit integer: " + key);
    }
}
