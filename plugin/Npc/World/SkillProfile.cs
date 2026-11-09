using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

// WP2. Skill = how WELL an NPC plays. Same engine at every tier; low tiers inject human mistakes, never shallow search.
// data/npc/skills-v1.json; tiers must be monotone in measured harness rating (CalibrationSuite).
public enum MistakeKind
{
    LethalMiss,       // lethal line of >= MinLethalActions actions is not taken
    TradeTarget,      // attacks a reasonable but not best target with the same attacker
    IgnoreAoeRisk,    // plans this turn without the reply model (overextends into clears)
    GreedyMulligan,   // keeps an expensive card / a weak hand
    HoldTooLong,      // passes / hero-powers while a near-best play exists
    FaceBias,         // prefers face when a trade is clearly better
    Misorder          // plays a later plan action first (e.g. attacks before a buff)
}

public sealed class MistakeRule
{
    public MistakeKind Kind { get; }
    public double Chance { get; }         // per eligible decision (per turn for IgnoreAoeRisk, per match for GreedyMulligan)
    public double MaxValueLoss { get; }   // never pick a candidate worse than best - MaxValueLoss (evaluator units)
    public int MinLethalActions { get; }  // LethalMiss only: shorter lethals are always found
    public MistakeRule(MistakeKind kind, double chance, double maxValueLoss, int minLethalActions = 0)
    { Kind = kind; Chance = chance; MaxValueLoss = maxValueLoss; MinLethalActions = minLethalActions; }
}

public sealed class SkillProfile
{
    public string Id { get; }
    public int Tier { get; }                    // 1 = weakest ... 7 = legend elite
    public double RatingMin { get; }            // nominal ladder rating range this tier serves
    public double RatingMax { get; }
    public Budget Budget { get; }               // node budget (precision first); live clock from Budget.TurnSearchSeconds (45 s for shipped tiers), else 5 s/turn
    public double Temperature { get; }          // softmax over candidates' values; 0 = always best
    public int TopN { get; }                    // softmax only among the best N candidates
    public double ReplyBlendScale { get; }      // multiplies Personality.ReplyBlendBeta (risk awareness)
    public IReadOnlyList<MistakeRule> Mistakes { get; }
    // Softmax bound: the top-N noise never strays further than this below the best candidate (evaluator units).
    public double SoftmaxMaxValueLoss { get; }
    public SkillProfile(string id, int tier, double ratingMin, double ratingMax, Budget budget, double temperature, int topN,
        double replyBlendScale, IEnumerable<MistakeRule> mistakes, double softmaxMaxValueLoss = -1)
    { Id = id; Tier = tier; RatingMin = ratingMin; RatingMax = ratingMax; Budget = budget; Temperature = temperature;
      TopN = topN; ReplyBlendScale = replyBlendScale; Mistakes = GoldenData.Copy(mistakes);
      SoftmaxMaxValueLoss = softmaxMaxValueLoss >= 0 ? softmaxMaxValueLoss
          : Mistakes.Select(m => m.MaxValueLoss).DefaultIfEmpty(0).Max(); }
    // AI-leap research: apply the personality's evaluation style (PersonalityArchetype.Style) on this tier. Default off.
    public bool PersonalityStyle { get; internal set; }
    public double Chance(MistakeKind kind) => Mistakes.Where(m => m.Kind == kind).Select(m => m.Chance).DefaultIfEmpty(0).Max();
    public MistakeRule Rule(MistakeKind kind) => Mistakes.Where(m => m.Kind == kind).OrderByDescending(m => m.Chance).FirstOrDefault();
    // Elite: no mistakes and no noise, so SkilledPilot returns the search's own choice unchanged.
    public bool IsPerfect => Temperature <= 0 && Mistakes.All(m => m.Chance <= 0);
}

public sealed class SkillCatalog
{
    public const string ResourceName = "SoloStone.Npc.skills-v1.json";
    public int Version { get; }
    public IReadOnlyList<SkillProfile> Tiers { get; }
    public SkillCatalog(int version, IEnumerable<SkillProfile> tiers)
    { Version = version; Tiers = GoldenData.Copy((tiers ?? Enumerable.Empty<SkillProfile>()).OrderBy(t => t.Tier)); }

    public SkillProfile Find(string id) => Tiers.FirstOrDefault(t => t.Id == id);
    // Highest tier whose RatingMin <= rating; below every range = the weakest tier. Ranges are on the LadderRules
    // scale (Bronze 1000, Diamond 5 = 1540, Legend = 1600) and come from calibration-v1 tierElo bands.
    public SkillProfile ForRating(double rating)
    {
        if (Tiers.Count == 0) throw new InvalidOperationException("Empty skill catalog.");
        if (double.IsNaN(rating)) throw new ArgumentException("Rating is NaN.", nameof(rating));
        return Tiers.LastOrDefault(t => t.RatingMin <= rating) ?? Tiers[0];
    }

    public static SkillCatalog Parse(string json) => Load(NpcWorldJson.Parse(json));
    public static SkillCatalog Load(object root)
    {
        var top = NpcWorldJson.Object(root);
        var tiers = new List<SkillProfile>();
        foreach (object value in NpcWorldJson.List(top, "tiers"))
        {
            var row = NpcWorldJson.Object(value);
            var b = NpcWorldJson.Object(row["budget"]);
            var range = NpcWorldJson.List(row, "rating");
            if (range.Count != 2) throw new InvalidDataException("rating must be [min, max].");
            tiers.Add(new SkillProfile(NpcWorldJson.Text(row, "id"), NpcWorldJson.Integer(row, "tier"),
                Convert.ToDouble(range[0], System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToDouble(range[1], System.Globalization.CultureInfo.InvariantCulture),
                new Budget(NpcWorldJson.Integer(b, "turnNodes"), NpcWorldJson.Integer(b, "planNodes"),
                    NpcWorldJson.Integer(b, "lethalNodes"), NpcWorldJson.Integer(b, "replyK"),
                    NpcWorldJson.Integer(b, "replyL"), NpcWorldJson.Integer(b, "replyR"), NpcWorldJson.Integer(b, "ownBeamWidth", 6),
                    NpcWorldJson.Boolean(b, "dedupe", false), NpcWorldJson.Number(b, "turnSearchSeconds", 0),
                    NpcWorldJson.Number(b, "turnWallSeconds", 0), ParseBrain(b)),
                NpcWorldJson.Number(row, "temperature"), NpcWorldJson.Integer(row, "topN"),
                NpcWorldJson.Number(row, "replyBlendScale", 1),
                NpcWorldJson.List(row, "mistakes", false).Cast<object>().Select(m => {
                    var r = NpcWorldJson.Object(m);
                    return new MistakeRule(NpcDeckLibrary.ParseEnum<MistakeKind>(NpcWorldJson.Text(r, "kind")),
                        NpcWorldJson.Number(r, "chance"), NpcWorldJson.Number(r, "maxValueLoss", 0),
                        NpcWorldJson.Integer(r, "minLethalActions", 0));
                }), NpcWorldJson.Number(row, "softmaxMaxValueLoss", -1)) { PersonalityStyle = NpcWorldJson.Boolean(row, "personalityStyle", false) });
        }
        var catalog = new SkillCatalog(NpcWorldJson.Integer(top, "version"), tiers);
        catalog.Validate();
        return catalog;
    }

    // Optional `budget.brain` (AI-leap research knobs, BrainOptions); absent = the shipping golden brain.
    public static BrainOptions ParseBrain(IDictionary<string, object> budget)
    {
        object value;
        if (!budget.TryGetValue("brain", out value) || value == null) return null;
        try {
            var row = NpcWorldJson.Object(value);
            var unknown = row.Keys.Except(BrainKeys).ToArray();
            if (unknown.Length > 0) throw new InvalidDataException("unknown key " + string.Join(", ", unknown));
            var scales = new Dictionary<string, double>(StringComparer.Ordinal);
            object raw;
            if (row.TryGetValue("scales", out raw) && raw != null)
                foreach (var key in NpcWorldJson.Object(raw).Keys.ToArray()) scales[key] = NpcWorldJson.Number(NpcWorldJson.Object(raw), key);
            LearnedValue learned = null;
            if (row.TryGetValue("value", out raw) && raw != null) {
                var v = NpcWorldJson.Object(raw);
                learned = new LearnedValue(NpcWorldJson.Text(v, "id"), NpcWorldJson.List(v, "weights").Cast<object>()
                    .Select(w => Convert.ToDouble(w, System.Globalization.CultureInfo.InvariantCulture)), NpcWorldJson.Number(v, "handcrafted"));
            }
            string prior = NpcWorldJson.Text(row, "replyPrior", false) ?? "class";
            return new BrainOptions(scales, row.ContainsKey("clearRisk") ? NpcWorldJson.Number(row, "clearRisk") : (double?)null,
                NpcWorldJson.Boolean(row, "clearRiskByLife", false), NpcDeckLibrary.ParseEnum<ReplyPrior>(prior),
                NpcWorldJson.Number(row, "inferenceFloor", .15),
                NpcWorldJson.List(row, "excludedDecks", false).Cast<object>().Select(x => x as string ?? throw new InvalidDataException("excludedDecks: strings")), learned,
                NpcWorldJson.Number(row, "valueBlend", 1),
                row.ContainsKey("handFlat") ? NpcWorldJson.Number(row, "handFlat") : (double?)null, NpcWorldJson.Number(row, "handTail", 0),
                NpcWorldJson.Boolean(row, "sqrtLife", false), NpcWorldJson.Number(row, "clearBonus", 0), NpcWorldJson.Boolean(row, "prune", false),
                DeckScalesOf(row));
        }
        catch (Exception error) when (error is ArgumentException || error is InvalidDataException || error is InvalidCastException || error is FormatException)
        { throw new InvalidDataException("brain: " + error.Message); }
    }
    private static IDictionary<string, IDictionary<string, double>> DeckScalesOf(IDictionary<string, object> row)
    {
        var result = new Dictionary<string, IDictionary<string, double>>(StringComparer.Ordinal);
        object raw;
        if (!row.TryGetValue("deckScales", out raw) || raw == null) return result;
        var decks = NpcWorldJson.Object(raw);
        foreach (var deck in decks.Keys.ToArray()) {
            var scales = NpcWorldJson.Object(decks[deck]);
            result[deck] = scales.Keys.ToDictionary(k => k, k => NpcWorldJson.Number(scales, k), StringComparer.Ordinal);
        }
        return result;
    }
    private static readonly string[] BrainKeys = { "scales", "clearRisk", "clearRiskByLife", "replyPrior", "inferenceFloor",
        "excludedDecks", "value", "valueBlend", "handFlat", "handTail", "sqrtLife", "clearBonus", "prune", "deckScales" };

    // Load-time invariants: humans miss long lethals, never one- or two-action ones; chances are probabilities.
    public void Validate()
    {
        if (Tiers.Count == 0) throw new InvalidDataException("No skill tiers.");
        if (Tiers.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != Tiers.Count) throw new InvalidDataException("Duplicate skill id.");
        if (Tiers.Select(t => t.Tier).Distinct().Count() != Tiers.Count) throw new InvalidDataException("Duplicate skill tier.");
        for (int i = 0; i < Tiers.Count; i++)
        {
            var t = Tiers[i];
            if (t.RatingMax <= t.RatingMin) throw new InvalidDataException("Empty rating range: " + t.Id);
            if (i > 0 && Tiers[i - 1].RatingMax > t.RatingMin + 1e-9) throw new InvalidDataException("Overlapping rating ranges: " + t.Id);
            if (t.Temperature < 0 || t.TopN < 1 || t.ReplyBlendScale < 0 || t.SoftmaxMaxValueLoss < 0)
                throw new InvalidDataException("Invalid noise settings: " + t.Id);
            foreach (var m in t.Mistakes)
            {
                if (m.Chance < 0 || m.Chance > 1 || m.MaxValueLoss < 0) throw new InvalidDataException("Invalid mistake " + m.Kind + ": " + t.Id);
                if (m.Kind == MistakeKind.LethalMiss && m.MinLethalActions < MinMissableLethalActions)
                    throw new InvalidDataException("lethalMiss must keep lethals under " + MinMissableLethalActions + " actions: " + t.Id);
            }
        }
    }
    public const int MinMissableLethalActions = 3;
}
