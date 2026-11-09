using System;
using System.Collections.Generic;
using System.Linq;

// Retail ranked rules on a linear star index, fed by a hidden rating.
// Spec: docs/superpowers/specs/2026-10-24-ladder-rules-design.md. Pure: no profile, Unity or native types.
public enum LadderOutcome { Win, Loss, Tie }

public sealed class LadderPosition
{
    public int StarIndex, BestStarIndex, Multiplier, StreakWins, PlacementGames;
    public double Rating;
    public bool LegendReached;
    public LadderPosition Copy() => (LadderPosition)MemberwiseClone();
}

public sealed class LadderStep
{
    public LadderPosition Before, After;
    public LadderOutcome Outcome;
    public double OpponentRating;
    public bool Placement, StreakBonus, StoppedAtDiamondFive, LegendReachedNow;
    public int StarsGained;
    public int[] FloorsReached;
}

// Grind tier ladder knobs (docs/design/2026-10-31-grind-tiers.md). Standard = the retail-shaped rules unchanged.
// Stars per win below Diamond 5 scale the bonus part of the multiplier; Diamond 5-1 wins give TopStarsPerWin;
// in Legend the settled hidden-rating step is SettledK * LegendKScale (symmetric: pace, not a skill handicap).
public sealed class LadderPace
{
    public static readonly LadderPace Standard = new LadderPace(1, 1, 1);
    public double BonusStarScale { get; }
    public int TopStarsPerWin { get; }
    public double LegendKScale { get; }
    public LadderPace(double bonusStarScale, int topStarsPerWin, double legendKScale)
    {
        if (double.IsNaN(bonusStarScale) || bonusStarScale < 0 || bonusStarScale > 4 || topStarsPerWin < 1 || topStarsPerWin > 3
            || double.IsNaN(legendKScale) || legendKScale <= 0 || legendKScale > 4)
            throw new ArgumentException("Invalid ladder pace.");
        BonusStarScale = bonusStarScale; TopStarsPerWin = topStarsPerWin; LegendKScale = legendKScale;
    }
    // Stars per win before a streak doubles them: x1 stays x1, the bonus above it scales, never above the retail x11.
    public int Stars(int multiplier) => Math.Min(LadderRules.MaximumMultiplier,
        1 + (int)Math.Round((multiplier - 1) * BonusStarScale, MidpointRounding.AwayFromZero));
}

public sealed class LadderRankRow
{
    public int StarLevel, Stars;
    public bool Legend;
}

public sealed class LadderMedal
{
    public int LeagueId, StarLevel, Stars, BestStarLevel, StarsPerWin, Streak;
    public bool IsLegend;
}

public static class LadderRules
{
    // 1 = the V10 folded one-star receipts (LocalProfile.NextStars); 2 = these rules.
    public const int RuleVersion = 2;
    public const int LegendIndex = 150, DiamondFiveIndex = 135, SilverTenIndex = 30, FloorSpacing = 15;
    public const int PlacementLength = 10, MaximumMultiplier = 11;
    // Rating scale: rating typical of star index i is BronzeRating + i * RatingPerStar (Legend 1600).
    // Owner decision 2 / #52 tune these; the climb simulation (tests/ladder-rules) reports their effect.
    public const double BronzeRating = 1000, RatingPerStar = 4, VeryHighRating = 1800;
    public const double StartRating = 1400, PlacementK = 160, SettledK = 24;

    public static LadderOutcome Outcome(string result) =>
        result == "Won" ? LadderOutcome.Win : result == "Lost" || result == "Abandoned" ? LadderOutcome.Loss : LadderOutcome.Tie;

    public static bool IsFloor(int index) => index > 0 && index <= DiamondFiveIndex && index % FloorSpacing == 0;

    // Bronze never loses stars; elsewhere the last rank 10 / rank 5 at or below the index holds.
    public static int FloorBelow(int index) => index < SilverTenIndex ? index : index / FloorSpacing * FloorSpacing;

    // Floors reached this season, from Bronze 5 to Diamond 5: a x10 start decays to x1 exactly at Diamond 5.
    public static int FloorsReached(int bestIndex) => Math.Min(bestIndex, DiamondFiveIndex) / FloorSpacing;

    public static double RatingForIndex(int index) => BronzeRating + Clamp(index, 0, LegendIndex) * RatingPerStar;

    public static int IndexForRating(double rating) =>
        Clamp((int)Math.Floor((rating - BronzeRating) / RatingPerStar), 0, LegendIndex);

    // Retail's season-start minimums by rank, read from the rating's typical rank.
    public static int StartMultiplier(double rating)
    {
        if (rating >= VeryHighRating) return MaximumMultiplier;
        int index = IndexForRating(rating);
        return index >= LegendIndex ? 10 : Math.Max(2, index / FloorSpacing);
    }

    public static int Multiplier(int startMultiplier, int bestIndex, int index) =>
        index >= DiamondFiveIndex ? 1 : Math.Max(1, startMultiplier - FloorsReached(bestIndex));

    public static double Expected(double rating, double opponent) => 1 / (1 + Math.Pow(10, (opponent - rating) / 400));

    public static LadderPosition Fresh() => new LadderPosition {
        Rating = StartRating, Multiplier = Multiplier(StartMultiplier(StartRating), 0, 0)
    };

    // A V10 reservation (rule version 0) keeps its star index unchanged; the rest is derived from the settled
    // history: rating from the index and recent win rate, placement complete (spec: migration), streak from trailing wins.
    public static LadderPosition Seed(int starIndex, int bestStarIndex, IReadOnlyList<LadderOutcome> history)
    {
        if (starIndex < 0 || starIndex > LegendIndex || bestStarIndex < starIndex || bestStarIndex > LegendIndex || history == null)
            throw new ArgumentException("Invalid ladder reservation.");
        if (history.Count == 0 && starIndex == 0) return Fresh();
        var recent = history.Where(outcome => outcome != LadderOutcome.Tie).Reverse().Take(20).ToArray();
        int wins = recent.Count(outcome => outcome == LadderOutcome.Win), losses = recent.Length - wins;
        double form = Math.Max(-100, Math.Min(100, 400 * Math.Log10((wins + 1.0) / (losses + 1.0))));
        double rating = Math.Round(RatingForIndex(starIndex) + form, 3);
        int streak = 0;
        foreach (var outcome in history.Reverse())
            if (outcome == LadderOutcome.Win) streak++;
            else if (outcome == LadderOutcome.Loss) break;
        return Validate(new LadderPosition {
            StarIndex = starIndex, BestStarIndex = bestStarIndex, Rating = rating,
            PlacementGames = PlacementLength, StreakWins = streak,
            Multiplier = Multiplier(StartMultiplier(rating), bestStarIndex, starIndex),
            LegendReached = starIndex == LegendIndex
        });
    }

    public static LadderPosition Validate(LadderPosition p)
    {
        if (p == null || p.StarIndex < 0 || p.StarIndex > LegendIndex || p.BestStarIndex < p.StarIndex || p.BestStarIndex > LegendIndex
            || p.Multiplier < 1 || p.Multiplier > MaximumMultiplier || p.StreakWins < 0
            || p.PlacementGames < 0 || p.PlacementGames > PlacementLength
            || double.IsNaN(p.Rating) || double.IsInfinity(p.Rating)
            || p.LegendReached != (p.StarIndex == LegendIndex) || (p.BestStarIndex == LegendIndex && !p.LegendReached)
            || (p.StarIndex >= DiamondFiveIndex && p.Multiplier != 1)
            || p.Multiplier > Math.Max(1, MaximumMultiplier - FloorsReached(p.BestStarIndex))
            // No "index >= floor(best)" check: legacy folds (old floors) can leave a best above a floor the index is below.
            || (p.StarIndex < SilverTenIndex && p.BestStarIndex != p.StarIndex))
            throw new ArgumentException("Invalid ladder position.");
        return p;
    }

    public static LadderStep Apply(LadderPosition before, LadderOutcome outcome, double opponentRating, LadderPace pace = null)
    {
        Validate(before);
        pace = pace ?? LadderPace.Standard;
        if (double.IsNaN(opponentRating) || double.IsInfinity(opponentRating)) throw new ArgumentException("Invalid opponent rating.");
        var after = before.Copy();
        var step = new LadderStep { Before = before.Copy(), Outcome = outcome, OpponentRating = opponentRating,
            Placement = before.PlacementGames < PlacementLength, FloorsReached = new int[0] };
        // Ties change nothing: stars, streak, rating, placement count and multiplier stay (retail counts non-drawn games).
        if (outcome == LadderOutcome.Tie) { step.After = after; return step; }
        double score = outcome == LadderOutcome.Win ? 1 : 0;
        double k = step.Placement ? PlacementK : before.LegendReached ? SettledK * pace.LegendKScale : SettledK;
        after.Rating = Math.Round(before.Rating + k * (score - Expected(before.Rating, opponentRating)), 3);
        if (step.Placement) after.PlacementGames++;

        if (outcome == LadderOutcome.Win)
        {
            after.StreakWins = before.StreakWins + 1;
            if (!before.LegendReached)
            {
                bool belowDiamondFive = before.StarIndex < DiamondFiveIndex;
                step.StreakBonus = belowDiamondFive && before.StreakWins >= 2;
                int stars = belowDiamondFive ? pace.Stars(before.Multiplier) * (step.StreakBonus ? 2 : 1) : pace.TopStarsPerWin;
                int cap = belowDiamondFive ? DiamondFiveIndex : LegendIndex;
                step.StoppedAtDiamondFive = belowDiamondFive && before.StarIndex + stars > cap;
                after.StarIndex = Math.Min(cap, before.StarIndex + stars);
            }
        }
        else
        {
            after.StreakWins = 0;
            if (!before.LegendReached) after.StarIndex = Math.Max(FloorBelow(before.StarIndex), before.StarIndex - 1);
        }

        after.BestStarIndex = Math.Max(before.BestStarIndex, after.StarIndex);
        after.LegendReached = after.StarIndex == LegendIndex;
        // Stars use the multiplier before this game; floors crossed by it decay the next win (SoloStone choice).
        after.Multiplier = step.Placement
            ? Multiplier(StartMultiplier(after.Rating), after.BestStarIndex, after.StarIndex)
            : after.StarIndex >= DiamondFiveIndex ? 1
            : Math.Max(1, before.Multiplier - (FloorsReached(after.BestStarIndex) - FloorsReached(before.BestStarIndex)));
        step.StarsGained = after.StarIndex - before.StarIndex;
        step.FloorsReached = Enumerable.Range(before.BestStarIndex + 1, after.BestStarIndex - before.BestStarIndex).Where(IsFloor).ToArray();
        step.LegendReachedNow = after.LegendReached && !before.LegendReached;
        step.After = Validate(after);
        return step;
    }

    public static string RankName(int index)
    {
        if (index >= LegendIndex) return "Legend";
        return new[] { "Bronze", "Silver", "Gold", "Platinum", "Diamond" }[index / 30] + " " + (10 - index % 30 / 3);
    }

    // Native medal: star level from the pinned client's LeagueRankDbfRecord rows (50 three-star ranks, then Legend).
    // The client (MedalInfoTranslator) shows 0-3 stars per rank: a win at 3/3 promotes to 1/3, a loss at 0/3 drops to 2/3,
    // so "rank N, 0/3" and "rank below N, 3/3" are one position. arrivedByWin picks retail's form for it: 3/3 after a win.
    public static LadderMedal Medal(LadderPosition position, int leagueId, IEnumerable<LadderRankRow> ranks, bool arrivedByWin = false,
        LadderPace pace = null)
    {
        Validate(position);
        pace = pace ?? LadderPace.Standard;
        var rows = (ranks ?? throw new ArgumentNullException(nameof(ranks))).OrderBy(row => row?.StarLevel ?? int.MinValue).ToArray();
        if (leagueId <= 0 || rows.Length != 51 || rows.Any(row => row == null) || rows.Select(row => row.StarLevel).Distinct().Count() != 51
            || rows.Take(50).Any(row => row.Legend || row.Stars != 3) || !rows[50].Legend)
            throw new ArgumentException("Expected 50 three-star ranks followed by Legend.");
        bool belowDiamondFive = position.StarIndex < DiamondFiveIndex;
        bool full = arrivedByWin && !position.LegendReached && position.StarIndex > 0 && position.StarIndex % 3 == 0;
        int level = full ? position.StarIndex / 3 - 1 : position.StarIndex / 3;
        int best = position.BestStarIndex == position.StarIndex ? level : position.BestStarIndex / 3;
        return new LadderMedal {
            LeagueId = leagueId, StarLevel = rows[level].StarLevel,
            Stars = position.LegendReached ? 0 : full ? 3 : position.StarIndex % 3,
            BestStarLevel = rows[best].StarLevel,
            StarsPerWin = belowDiamondFive ? pace.Stars(position.Multiplier) : pace.TopStarsPerWin, Streak = belowDiamondFive ? position.StreakWins : 0,
            IsLegend = position.LegendReached
        };
    }

    private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
}

// #37 Standard Casual (owner 2026-10-28): a hidden matchmaking rating, never shown and never read by the ladder. Seeded from
// the ranked hidden rating at the first Casual game, then moved by Elo on Casual results only. Ties change nothing, as on
// the ladder; an abandoned Casual match is a loss.
public static class CasualRules
{
    public const string Mode = "Casual";
    public const double K = 32, MinRating = 600, MaxRating = 2400;
    public static bool IsCasual(string mode) => mode == Mode;
    public static bool ValidRating(double rating) => !double.IsNaN(rating) && rating >= MinRating && rating <= MaxRating;
    public static double Seed(double rankedRating) => Clamp(Math.Round(rankedRating, 3));
    public static double Apply(double before, string result, double opponentRating)
    {
        if (!ValidRating(before) || double.IsNaN(opponentRating) || double.IsInfinity(opponentRating))
            throw new ArgumentException("Invalid Casual rating.");
        if (result == "Tied") return before;
        double score = result == "Won" ? 1 : 0;
        return Clamp(Math.Round(before + K * (score - LadderRules.Expected(before, opponentRating)), 3));
    }
    private static double Clamp(double rating) => Math.Max(MinRating, Math.Min(MaxRating, rating));
}
