using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

public sealed partial class LocalProfile
{
    // Effective ladder position. Rule version 0 is the untouched V10 reservation: its star index is kept and
    // the rest is seeded from settled history; the first settlement persists the seeded state.
    public LadderPosition Ladder => EffectiveLadder(data);
    // Direction of the last ladder move that changed stars, for the native medal's 0/3-vs-3/3 form (LadderRules.Medal).
    public bool LadderArrivedByWin => data.Matches.Select(match => match.Ladder)
        .LastOrDefault(r => r != null && r.LadderRuleVersion >= LadderRules.RuleVersion && r.StarsGained != 0)?.StarsGained > 0;
    public LocalLadderReceipt LadderReceipt(string matchId) => Copy(data.Matches.SingleOrDefault(match => match.Id == matchId)?.Ladder);

    private static LadderPosition LadderOf(LocalProfileData profile)
    {
        var state = profile.LadderState;
        if (profile.LadderRuleVersion == LadderRules.RuleVersion)
            return LadderRules.Validate(new LadderPosition {
                StarIndex = state.StarIndex, BestStarIndex = state.BestStarIndex, Multiplier = state.Multiplier,
                StreakWins = state.StreakWins, PlacementGames = state.PlacementGames, Rating = state.HiddenRating.Value,
                LegendReached = state.LegendReached
            });
        return LegacySeed(RankedMatches(profile));
    }

    // #37: the ladder reads ranked matches only. A Casual record never carries a ladder receipt and never moves stars,
    // hidden rating, calibration, Legend number or the NPC field's drift epoch.
    internal static List<LocalMatchRecord> RankedMatches(LocalProfileData profile) =>
        profile.Matches.Where(match => !CasualRules.IsCasual(match.Mode)).ToList();
    internal int RankedMatchCount => data.Matches.Count(match => !CasualRules.IsCasual(match.Mode));
    public IReadOnlyList<LocalMatchRecord> RankedHistory => RankedMatches(data).Select(Copy).ToArray();
    public IReadOnlyList<LocalMatchRecord> CasualHistory => data.Matches.Where(match => CasualRules.IsCasual(match.Mode)).Select(Copy).ToArray();
    // Hidden Casual matchmaking rating: the stored one, else the seed the first Casual settlement will store.
    public double CasualRating => data.CasualRating ?? CasualRules.Seed(EffectiveLadder(data).Rating);

    public bool PrefersCasual => CasualRules.IsCasual(data.PlayMode);
    // Remembers the native picker's mode for the next launch; a no-op when unchanged (no commit per picker open).
    public void SetPreferredPlayMode(bool casual)
    {
        if (casual == PrefersCasual) return;
        var next = Snapshot();
        next.PlayMode = casual ? CasualRules.Mode : null;
        Commit(next);
    }

    private LocalCasualReceipt SettleCasual(LocalProfileData next, string result, double? opponentRating)
    {
        double before = next.CasualRating ?? CasualRules.Seed(EffectiveLadder(next).Rating);
        double opponent = opponentRating ?? before;
        double after = CasualRules.Apply(before, result, opponent);
        next.CasualRating = after;
        return new LocalCasualReceipt { RatingBefore = before, RatingAfter = after, OpponentRating = opponent };
    }

    // Casual receipts chain: each step starts where the previous one ended and follows CasualRules; the stored rating is
    // the last step's. The first step's seed is not replayed (it read the ladder at that time), only range-checked: accepted
    // (review 2026-10-28), the hidden Casual rating only picks opponents and pays nothing.
    private static void ValidateCasual(LocalProfileData profile)
    {
        if (profile.CasualRating.HasValue && !CasualRules.ValidRating(profile.CasualRating.Value))
            throw new InvalidOperationException("Invalid Casual rating.");
        if (profile.PlayMode != null && !CasualRules.IsCasual(profile.PlayMode)) throw new InvalidOperationException("Unknown play mode.");
        double? last = null;
        foreach (var match in profile.Matches.Concat(profile.Pending == null ? Enumerable.Empty<LocalMatchRecord>() : new[] { profile.Pending }))
        {
            if (match.Mode != null && !CasualRules.IsCasual(match.Mode)) throw new InvalidOperationException("Unknown match mode.");
            bool settled = match != profile.Pending;
            if (!CasualRules.IsCasual(match.Mode) || !settled) {
                if (match.Casual != null) throw new InvalidOperationException("Casual receipt on a ranked or pending match.");
                continue;
            }
            var r = match.Casual;
            if (r == null || match.Ladder != null || (match.TierRewards != null && match.TierRewards.Count != 0)
                || !CasualRules.ValidRating(r.RatingBefore) || !CasualRules.ValidRating(r.RatingAfter)
                || double.IsNaN(r.OpponentRating) || double.IsInfinity(r.OpponentRating)
                || (last.HasValue && Math.Abs(last.Value - r.RatingBefore) > RatingTolerance)
                || Math.Abs(CasualRules.Apply(r.RatingBefore, match.Result, r.OpponentRating) - r.RatingAfter) > RatingTolerance)
                throw new InvalidOperationException("Invalid Casual receipt.");
            last = r.RatingAfter;
        }
        if (last.HasValue != profile.CasualRating.HasValue || (last.HasValue && Math.Abs(last.Value - profile.CasualRating.Value) > RatingTolerance))
            throw new InvalidOperationException("Casual rating does not match the last Casual receipt.");
    }

    private static LadderPosition CheatLegendOne(double rating) => LadderRules.Validate(new LadderPosition {
        StarIndex = LadderRules.LegendIndex, BestStarIndex = LadderRules.LegendIndex, Multiplier = 1,
        PlacementGames = LadderRules.PlacementLength, Rating = rating, LegendReached = true
    });

    internal static double LegendOneCreationRating()
    {
        try { return NpcLegendTopRating?.Invoke(0) ?? 1900d; }
        catch (Exception) { return 1900d; }
    }

    private LadderPosition EffectiveLadder(LocalProfileData profile) => profileOptions.StartLegendOne
        && profile.LadderRuleVersion == 0 && !profile.Matches.Any(match => !CasualRules.IsCasual(match.Mode)) ? CheatLegendOne(profileOptions.LegendRating) : LadderOf(profile);

    // The reservation's position, derived from history alone so the first ladder receipt can be checked against it:
    // star index = the legacy fold; best = the fold at V10 migration (matches without receipts), then its running
    // maximum over the shop's folded rule-1 receipts, exactly as MigrateV9 and ApplyMatchReceipt store them.
    private static LadderPosition LegacySeed(IEnumerable<LocalMatchRecord> matches)
    {
        int stars = 0, best = 0;
        var history = new List<LadderOutcome>();
        foreach (var match in matches)
        {
            stars = NextStars(stars, match.Result);
            best = match.Ladder == null ? stars : Math.Max(best, stars);
            history.Add(LadderRules.Outcome(match.Result));
        }
        return LadderRules.Seed(stars, best, history);
    }

    // Applies one settled match to the snapshot's ladder state; call before the match joins the snapshot's history.
    private LocalLadderReceipt SettleLadder(LocalProfileData next, LocalMatchRecord match, string result, double? opponentRating)
    {
        var before = EffectiveLadder(next);
        var step = LadderRules.Apply(before, LadderRules.Outcome(result), opponentRating ?? before.Rating, GrindTiers.Find(match.GrindTier).Ladder);
        var after = step.After;
        var state = next.LadderState ?? new LocalLadderState();
        state.StarIndex = after.StarIndex; state.BestStarIndex = after.BestStarIndex; state.Multiplier = after.Multiplier;
        state.StreakWins = after.StreakWins; state.PlacementGames = after.PlacementGames; state.HiddenRating = after.Rating;
        state.LegendReached = after.LegendReached;
        next.LadderState = state;
        next.LadderRuleVersion = LadderRules.RuleVersion;
        return new LocalLadderReceipt {
            LadderRuleVersion = LadderRules.RuleVersion, Outcome = step.Outcome.ToString(),
            StarsBefore = before.StarIndex, StarsAfter = after.StarIndex, BestBefore = before.BestStarIndex, BestAfter = after.BestStarIndex,
            StarsGained = step.StarsGained, MultiplierBefore = before.Multiplier, MultiplierAfter = after.Multiplier,
            StreakBefore = before.StreakWins, StreakAfter = after.StreakWins,
            PlacementBefore = before.PlacementGames, PlacementAfter = after.PlacementGames,
            RatingBefore = before.Rating, RatingAfter = after.Rating, OpponentRating = step.OpponentRating,
            StreakBonus = step.StreakBonus, StoppedAtDiamondFive = step.StoppedAtDiamondFive, LegendReached = step.LegendReachedNow,
            FloorsReached = FloorIds(step.FloorsReached)
        };
    }

    private static int LegacyPrefix(LocalProfileData profile) => RankedMatches(profile)
        .TakeWhile(match => match.Ladder == null || match.Ladder.LadderRuleVersion < LadderRules.RuleVersion).Count();

    private static bool TryCalibrateLadder(LocalProfileData profile)
    {
        if (profile.LadderCalibration != null || profile.Pending != null) return false;
        int legacy = LegacyPrefix(profile);
        if (legacy == 0 || LegacySeed(RankedMatches(profile).Take(legacy)).PlacementGames != LadderRules.PlacementLength) return false;
        var position = LadderOf(profile);
        position.Rating = LadderRules.StartRating;
        position.PlacementGames = 0;
        var state = profile.LadderState ?? new LocalLadderState();
        state.StarIndex = position.StarIndex; state.BestStarIndex = position.BestStarIndex; state.Multiplier = position.Multiplier;
        state.StreakWins = position.StreakWins; state.PlacementGames = position.PlacementGames; state.HiddenRating = position.Rating;
        state.LegendReached = position.LegendReached;
        profile.LadderState = state;
        profile.LadderRuleVersion = LadderRules.RuleVersion;
        profile.LadderCalibration = new LocalLadderCalibration { Version = 1, MatchCount = RankedMatches(profile).Count };
        return true;
    }

    // Replay the frozen old seed/receipts, with one derived difficulty transition at the persisted boundary.
    private static void ValidateLadder(LocalProfileData profile, double legendRating = 0)
    {
        try { ValidateLadderCore(profile, legendRating); }
        catch (ArgumentException error) { throw new InvalidOperationException("Invalid ladder state: " + error.Message); }
    }

    private static void ValidateLadderCore(LocalProfileData profile, double legendRating)
    {
        if (profile.Pending?.Ladder != null) throw new InvalidOperationException("Pending match with a ladder receipt.");
        ValidateCasual(profile);
        var matches = RankedMatches(profile);
        var marker = profile.LadderCalibration;
        int legacy = LegacyPrefix(profile);
        var expected = legendRating > 0 && legacy == 0 ? CheatLegendOne(legendRating) : LegacySeed(matches.Take(legacy));
        if (marker != null && (marker.Version != 1 || marker.MatchCount < legacy || marker.MatchCount > matches.Count
            || legacy == 0 || expected.PlacementGames != LadderRules.PlacementLength))
            throw new InvalidOperationException("Invalid ladder calibration.");
        // Profile rule version 0 = V10 reservation (receipts, if any, are the folded legacy rule 1).
        if (profile.LadderRuleVersion != 0 && profile.LadderRuleVersion != LadderRules.RuleVersion)
            throw new InvalidOperationException("Unknown ladder rule version.");
        if (profile.LadderRuleVersion == 0)
        {
            if (marker != null || legacy != matches.Count)
                throw new InvalidOperationException("Ladder receipt/calibration without ladder state.");
            LadderOf(profile);
            return;
        }
        if (profile.LadderState == null || profile.LadderState.HiddenRating == null)
            throw new InvalidOperationException("Missing ladder state.");
        var position = LadderOf(profile);
        if (matches.Count == 0 && marker == null && Same(position, LadderRules.Fresh())) return;
        for (int i = legacy; i <= matches.Count; i++)
        {
            if (marker != null && marker.MatchCount == i) {
                expected.Rating = LadderRules.StartRating;
                expected.PlacementGames = 0;
            }
            if (i == matches.Count) break;
            var match = matches[i];
            var r = match.Ladder;
            if (r == null || r.LadderRuleVersion != LadderRules.RuleVersion || r.Outcome != LadderRules.Outcome(match.Result).ToString()
                || r.FloorsReached == null || r.StarsGained != r.StarsAfter - r.StarsBefore
                || r.RatingBefore == null || r.RatingAfter == null || r.OpponentRating == null
                || double.IsNaN(r.OpponentRating.Value) || double.IsInfinity(r.OpponentRating.Value)
                || !Same(expected, ReceiptPosition(r, before: true), i == legacy && marker?.MatchCount != i ? RatingTolerance : 0))
                throw new InvalidOperationException("Invalid ladder receipt.");
            var replay = LadderRules.Apply(ReceiptPosition(r, before: true), LadderRules.Outcome(match.Result), r.OpponentRating.Value, GrindTiers.Find(match.GrindTier).Ladder);
            if (!Same(replay.After, ReceiptPosition(r, before: false), RatingTolerance) || replay.StarsGained != r.StarsGained || replay.StreakBonus != r.StreakBonus
                || replay.StoppedAtDiamondFive != r.StoppedAtDiamondFive || replay.LegendReachedNow != r.LegendReached
                || !FloorIds(replay.FloorsReached).SequenceEqual(r.FloorsReached))
                throw new InvalidOperationException("Ladder receipt does not follow the rules.");
            expected = ReceiptPosition(r, before: false);
        }
        if ((legacy == matches.Count && marker == null) || !Same(position, expected))
            throw new InvalidOperationException("Ladder state does not match the last receipt/calibration.");
    }

    private static LadderPosition ReceiptPosition(LocalLadderReceipt r, bool before) => LadderRules.Validate(new LadderPosition {
        StarIndex = before ? r.StarsBefore : r.StarsAfter, BestStarIndex = before ? r.BestBefore : r.BestAfter,
        Multiplier = before ? r.MultiplierBefore : r.MultiplierAfter, StreakWins = before ? r.StreakBefore : r.StreakAfter,
        PlacementGames = before ? r.PlacementBefore : r.PlacementAfter, Rating = (before ? r.RatingBefore : r.RatingAfter).Value,
        LegendReached = (before ? r.StarsBefore : r.StarsAfter) == LadderRules.LegendIndex
    });

    // Field copy: History copies every receipt, and an XmlSerializer round trip per match is slow on Mono.
    private static LocalLadderReceipt Copy(LocalLadderReceipt r) => r == null ? null : new LocalLadderReceipt {
        StarsBefore = r.StarsBefore, StarsAfter = r.StarsAfter, MultiplierBefore = r.MultiplierBefore, MultiplierAfter = r.MultiplierAfter,
        StreakBefore = r.StreakBefore, StreakAfter = r.StreakAfter, RatingBefore = r.RatingBefore, RatingAfter = r.RatingAfter,
        StarsGained = r.StarsGained, FloorsReached = r.FloorsReached?.ToList(), LegendReached = r.LegendReached, LadderRuleVersion = r.LadderRuleVersion,
        Outcome = r.Outcome, BestBefore = r.BestBefore, BestAfter = r.BestAfter, PlacementBefore = r.PlacementBefore, PlacementAfter = r.PlacementAfter,
        OpponentRating = r.OpponentRating, StreakBonus = r.StreakBonus, StoppedAtDiamondFive = r.StoppedAtDiamondFive
    };

    private static List<string> FloorIds(IEnumerable<int> floors) => floors.Select(floor => floor.ToString(CultureInfo.InvariantCulture)).ToList();

    // Replays recompute Elo with Math.Pow; allow a last-digit rounding flip across runtimes, never a rule difference.
    private const double RatingTolerance = 0.002;
    private static bool Same(LadderPosition a, LadderPosition b, double tolerance = 0) =>
        a.StarIndex == b.StarIndex && a.BestStarIndex == b.BestStarIndex && a.Multiplier == b.Multiplier && a.StreakWins == b.StreakWins
        && a.PlacementGames == b.PlacementGames && Math.Abs(a.Rating - b.Rating) <= tolerance && a.LegendReached == b.LegendReached;

}
