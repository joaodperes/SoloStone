using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

// WP3. Personality = HOW an NPC plays and behaves, never how well (that is SkillProfile).
// data/npc/personalities-v1.json; ~10 archetypes. Golden personalities are archetypes "pro", "veteran", "gambler".
public sealed class PersonalityArchetype
{
    public string Id { get; }
    public string Label { get; }
    public double PreTurnMinSeconds { get; }
    public double PreTurnMaxSeconds { get; }
    public double ActionMinSeconds { get; }
    public double ActionMaxSeconds { get; }
    public double ReplyBlendBeta { get; }      // 0 = expected-case optimist ... 1 = worst-quartile cautious
    public double ConcessionWillingness { get; }
    public int ConcedeFromTurn { get; }
    public double RiskBias { get; }            // -1 cautious ... +1 greedy; tie-break within the 3-point bound only
    public double AggressionBias { get; }      // -1 trade-happy ... +1 face; tie-break within the 3-point bound only
    public bool Warm { get; }                  // warm archetypes never mock (HumanMisplay mock rules rejected at load)
    public EmoteProfile Emotes { get; }
    // Generation weights per LadderBand (Bronze..Legend); 1 when the data omits them.
    public IReadOnlyList<double> BandWeights { get; }
    // Multiplier per DeckTaste (chaotic meme lords run meme decks, professors brew); 1 when absent.
    public IReadOnlyDictionary<DeckTaste, double> TasteAffinity { get; }
    public PersonalityArchetype(string id, string label, double preTurnMinSeconds, double preTurnMaxSeconds,
        double actionMinSeconds, double actionMaxSeconds, double replyBlendBeta, double concessionWillingness,
        int concedeFromTurn, double riskBias, double aggressionBias, bool warm, EmoteProfile emotes)
        : this(id, label, preTurnMinSeconds, preTurnMaxSeconds, actionMinSeconds, actionMaxSeconds, replyBlendBeta,
            concessionWillingness, concedeFromTurn, riskBias, aggressionBias, warm, emotes, null, null) { }
    public PersonalityArchetype(string id, string label, double preTurnMinSeconds, double preTurnMaxSeconds,
        double actionMinSeconds, double actionMaxSeconds, double replyBlendBeta, double concessionWillingness,
        int concedeFromTurn, double riskBias, double aggressionBias, bool warm, EmoteProfile emotes,
        IEnumerable<double> bandWeights, IDictionary<DeckTaste, double> tasteAffinity)
    { Id = id; Label = label; PreTurnMinSeconds = preTurnMinSeconds; PreTurnMaxSeconds = preTurnMaxSeconds;
      ActionMinSeconds = actionMinSeconds; ActionMaxSeconds = actionMaxSeconds; ReplyBlendBeta = replyBlendBeta;
      ConcessionWillingness = concessionWillingness; ConcedeFromTurn = concedeFromTurn; RiskBias = riskBias;
      AggressionBias = aggressionBias; Warm = warm; Emotes = emotes;
      // Warm archetypes never mock and never Threaten (enforced here, so hand-built archetypes obey it too).
      if (warm && emotes != null && emotes.Rules.Any(rule => rule.Mock || rule.Emote == "Threaten"))
          throw new InvalidDataException("Warm archetype may not mock or Threaten: " + id);
      var weights = (bandWeights ?? Enumerable.Repeat(1.0, BandCount)).ToArray();
      if (weights.Length != BandCount) throw new InvalidDataException("bandWeights needs " + BandCount + " values: " + id);
      BandWeights = Array.AsReadOnly(weights);
      TasteAffinity = new Dictionary<DeckTaste, double>(tasteAffinity ?? new Dictionary<DeckTaste, double>()); }

    // AI-leap research: optional evaluation style (BrainOptions scale multipliers, personalities JSON `style`). Applied only
    // on skill tiers with personalityStyle=true (NpcEncounterProfiles.Build); empty = no style.
    public IReadOnlyDictionary<string, double> Style { get; internal set; } = new Dictionary<string, double>();
    public const int BandCount = 6;
    public double BandWeight(LadderBand band) => BandWeights[(int)band];
    public double AffinityFor(DeckTaste taste) { double value; return TasteAffinity.TryGetValue(taste, out value) ? value : 1; }
    public bool CanMock => Emotes != null && Emotes.Rules.Any(rule => rule.Mock);

    // replyBlendScale comes from the SkillProfile (risk awareness grows with skill).
    public Personality ToPersonality(double replyBlendScale) => new Personality(PreTurnMinSeconds, PreTurnMaxSeconds,
        ActionMinSeconds, ActionMaxSeconds, Emotes, Math.Max(0, Math.Min(1, ReplyBlendBeta * replyBlendScale)),
        ConcessionWillingness, ConcedeFromTurn);
}

public sealed class PersonalityCatalog
{
    public const string ResourceName = "SoloStone.Npc.personalities-v1.json";
    public const int MaxMocksPerMatch = 2;
    // Emotes the bot may send: the native transport qualifies these six (NativeMatch.SendNpcPresentation; Sorry is not qualified).
    public static readonly IReadOnlyList<string> BotEmotes = Array.AsReadOnly(new[] { "Greetings", "WellPlayed", "Thanks", "Wow", "Oops", "Threaten" });
    // Emotes a human may send (ReplyToHumanEmote detail).
    public static readonly IReadOnlyList<string> HumanEmotes = Array.AsReadOnly(new[] { "Greetings", "WellPlayed", "Thanks", "Wow", "Oops", "Threaten", "Sorry" });
    public int Version { get; }
    public IReadOnlyList<PersonalityArchetype> Archetypes { get; }
    public PersonalityCatalog(int version, IEnumerable<PersonalityArchetype> archetypes)
    {
        Version = version; Archetypes = GoldenData.Copy(archetypes);
        var duplicate = Archetypes.GroupBy(a => a.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null) throw new InvalidDataException("Duplicate personality id: " + duplicate.Key);
    }
    public PersonalityArchetype Find(string id) => Archetypes.FirstOrDefault(a => a.Id == id);

    public static PersonalityCatalog Parse(string json) => Load(NpcWorldJson.Parse(json));
    public static PersonalityCatalog Load(object root)
    {
        var top = NpcWorldJson.Object(root);
        var archetypes = new List<PersonalityArchetype>();
        foreach (object value in NpcWorldJson.List(top, "archetypes"))
        {
            var row = NpcWorldJson.Object(value);
            string id = NpcWorldJson.Text(row, "id");
            var pacing = NpcWorldJson.Object(row["pacing"]);
            var emotes = NpcWorldJson.Object(row["emotes"]);
            bool warm = NpcWorldJson.Boolean(row, "warm");
            var rules = NpcWorldJson.List(emotes, "rules").Cast<object>().Select(r => {
                var e = NpcWorldJson.Object(r);
                bool mock = NpcWorldJson.Boolean(e, "mock");
                if (warm && mock) throw new InvalidDataException("Warm archetype may not mock: " + id);
                if (warm && NpcWorldJson.Text(e, "emote") == "Threaten") throw new InvalidDataException("Warm archetype may not Threaten: " + id);
                var trigger = NpcDeckLibrary.ParseEnum<NpcReaction>(NpcWorldJson.Text(e, "trigger"));
                string emote = NpcWorldJson.Text(e, "emote"), human = NpcWorldJson.Text(e, "humanEmote", false);
                if (!BotEmotes.Contains(emote)) throw new InvalidDataException("Unsupported bot emote " + emote + ": " + id);
                if ((trigger == NpcReaction.ReplyToHumanEmote) != (human != null) || human != null && !HumanEmotes.Contains(human))
                    throw new InvalidDataException("humanEmote belongs to ReplyToHumanEmote rules only: " + id);
                // Mocks answer public observations of the human's play only (fairness: public-only detection).
                if (mock && trigger != NpcReaction.HumanMisplay && trigger != NpcReaction.HumanBigTurn)
                    throw new InvalidDataException("Mock rules need a HumanMisplay/HumanBigTurn trigger: " + id);
                double chance = NpcWorldJson.Number(e, "chance");
                int maxUses = NpcWorldJson.Integer(e, "maxUses", 1);
                if (chance < 0 || chance > 1 || maxUses < 1) throw new InvalidDataException("Emote chance/maxUses out of range: " + id);
                return new EmoteRule(trigger, emote, chance, maxUses, human, mock);
            }).ToArray();
            double preMin = NpcWorldJson.Number(pacing, "preTurnMin"), preMax = NpcWorldJson.Number(pacing, "preTurnMax");
            double actMin = NpcWorldJson.Number(pacing, "actionMin"), actMax = NpcWorldJson.Number(pacing, "actionMax");
            double beta = NpcWorldJson.Number(row, "replyBlendBeta"), concede = NpcWorldJson.Number(row, "concessionWillingness");
            double risk = NpcWorldJson.Number(row, "riskBias", 0), aggression = NpcWorldJson.Number(row, "aggressionBias", 0);
            if (preMin < 0 || preMin > preMax || actMin < 0 || actMin > actMax || beta < 0 || beta > 1 || concede < 0 || concede > 1
                || Math.Abs(risk) > 1 || Math.Abs(aggression) > 1)
                throw new InvalidDataException("Personality numbers out of range: " + id);
            List<double> bands = null;
            if (row.ContainsKey("bandWeights") && row["bandWeights"] != null)
                bands = NpcWorldJson.List(row, "bandWeights").Cast<object>().Select(v => {
                    // Number per element: strings, booleans, nulls and non-finite values are InvalidDataException.
                    double w = NpcWorldJson.Number(new Dictionary<string, object> { { "bandWeights", v } }, "bandWeights");
                    if (w < 0) throw new InvalidDataException("bandWeights must be >= 0: " + id);
                    return w;
                }).ToList();
            var tastes = new Dictionary<DeckTaste, double>();
            if (row.ContainsKey("tasteAffinity") && row["tasteAffinity"] != null)
                foreach (var pair in NpcWorldJson.Object(row["tasteAffinity"]))
                {
                    double w = NpcWorldJson.Number(NpcWorldJson.Object(row["tasteAffinity"]), pair.Key);
                    if (w < 0) throw new InvalidDataException("tasteAffinity must be >= 0: " + id);
                    tastes[NpcDeckLibrary.ParseEnum<DeckTaste>(pair.Key)] = w;
                }
            var style = new Dictionary<string, double>(StringComparer.Ordinal);
            if (row.ContainsKey("style") && row["style"] != null) {
                var styleRow = NpcWorldJson.Object(row["style"]);
                foreach (var key in styleRow.Keys.ToArray()) {
                    if (!BrainOptions.ScaleNames.Contains(key)) throw new InvalidDataException("Unknown style scale " + key + ": " + id);
                    double v = NpcWorldJson.Number(styleRow, key);
                    if (!(v > 0)) throw new InvalidDataException("style scales must be > 0: " + id);
                    style[key] = v;
                }
            }
            archetypes.Add(new PersonalityArchetype(id, NpcWorldJson.Text(row, "label"), preMin, preMax, actMin, actMax,
                beta, concede, NpcWorldJson.Integer(row, "concedeFromTurn"), risk, aggression, warm,
                new EmoteProfile(NpcWorldJson.Integer(emotes, "budget"), NpcWorldJson.Number(emotes, "gapSeconds"), rules),
                bands, tastes) { Style = style });
        }
        return new PersonalityCatalog(NpcWorldJson.Integer(top, "version"), archetypes);
    }
}
