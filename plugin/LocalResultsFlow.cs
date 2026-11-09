using System;
using System.Collections.Generic;
using System.Linq;

// Presentation data only. No settlement or acknowledgement lives here.
internal sealed class LocalResultsReceipt
{
    public string MatchId, Result, HeroClass;
    public bool Conceded, PackReward;
    // #37 Standard Casual: no rank scoop or tier step (Ladder stays null); the rank line reads "Casual".
    public bool Casual;
    public int Turns, Gold, Dust;
    public int SeasonGamesBefore, SeasonGamesAfter, SeasonWinsBefore, SeasonWinsAfter;
    public int? ClassXpBefore, ClassXpAfter;
    // Pet family card id and its settled XP ledger move for this match; absent for no pet / unowned pet / old receipts.
    public string Pet;
    public int? PetXpBefore, PetXpAfter;
    public LocalLadderReceipt Ladder;
    // The grind tier the match was played under (its stamp), for star texts and medals; never the current choice.
    public GrindTier Pace = GrindTiers.Standard;
    public IReadOnlyList<LocalTierReward> TierRewards = Array.Empty<LocalTierReward>();
    public IReadOnlyList<string> UnlockedCardIds = Array.Empty<string>();
    // Copied from the saved match receipt; historical receipts carry neither list.
    public IReadOnlyList<LocalClassRewardGrant> ClassRewards = Array.Empty<LocalClassRewardGrant>();
    public IReadOnlyList<LocalMasteryGrant> MasteryGrants = Array.Empty<LocalMasteryGrant>();
    // Saved positive-copy grants only: zero-copy mastery milestones and class dust are not new cards.
    internal LocalCardCount[] PremiumUnlocks => ClassRewards.Select(row => new LocalCardCount { Id = row.CardId, Premium = row.Premium, Count = row.Copies })
        .Concat(MasteryGrants.Select(row => new LocalCardCount { Id = row.CardId, Premium = row.Premium, Count = row.Copies }))
        .Where(row => row.Count > 0 && row.Premium >= 1 && row.Premium <= 3 && !string.IsNullOrEmpty(row.Id))
        .GroupBy(row => new { row.Id, row.Premium }).Select(group => new LocalCardCount {
            Id = group.Key.Id, Premium = group.Key.Premium, Count = group.Sum(row => row.Count) }).ToArray();
    // The retail scroll has layouts for one through four items.
    internal LocalCardCount[][] PremiumUnlockPages => PremiumUnlocks.Select((item, index) => new { item, index })
        .GroupBy(row => row.index / 4).Select(group => group.Select(row => row.item).ToArray()).ToArray();
    public IReadOnlyList<LocalMasteryProgress> MasteryProgress = Array.Empty<LocalMasteryProgress>();
    public IReadOnlyList<LocalPortraitGrant> Portraits = Array.Empty<LocalPortraitGrant>();
    public IReadOnlyList<LocalAchievementGrantReceipt> AchievementGrants = Array.Empty<LocalAchievementGrantReceipt>();
    public bool HasAchievements => AchievementGrants.Count > 0;
    public bool HasItems => Gold > 0 || PackReward || Dust > 0
        || AchievementGrants.Any(row => row.Gold > 0 || row.Dust > 0 || row.StandardPacks > 0);
    public bool HasRank => Ladder != null;
    // A zero-gain pet (cap, concede, short match) has nothing to present.
    public bool HasPetXp => Pet != null && PetXpBefore.HasValue && PetXpAfter.HasValue && PetXpAfter.Value > PetXpBefore.Value;
    public bool HasXpBar => LocalCoreProgression.Heroes.Contains(HeroClass) && ClassXpBefore.HasValue && ClassXpAfter.HasValue && ClassXpAfter > ClassXpBefore;
    // Direction of the last star move before this match; picks the native 0/3-vs-3/3 form (LadderRules.Medal).
    public bool ArrivedByWinBefore;
    public bool ArrivedByWinAfter => Ladder != null && (Ladder.StarsGained > 0 || (Ladder.StarsGained == 0 && ArrivedByWinBefore));
    public int StarsBefore => Ladder?.StarsBefore ?? 0;
    public int StarsAfter => Ladder?.StarsAfter ?? 0;
    public string RankText
    {
        get
        {
            if (Casual) return "Casual · no rank change";
            if (!HasRank) return "";
            string before = DisplayRank(StarsBefore, ArrivedByWinBefore), after = DisplayRank(StarsAfter, ArrivedByWinAfter);
            string text = "Rank: " + before + (before != after ? " → " + after : "");
            if (StarsAfter < LadderRules.LegendIndex && Ladder.StarsGained != 0)
                text += " (" + (Ladder.StarsGained > 0 ? "+" : "") + Ladder.StarsGained
                    + (Math.Abs(Ladder.StarsGained) == 1 ? " star)" : " stars)");
            else if (Ladder.StarsGained == 0 && Result == "Lost") text += " (no stars lost)";
            int bonus = Ladder.StarsBefore < LadderRules.DiamondFiveIndex ? Pace.Ladder.Stars(Ladder.MultiplierBefore) : 1;
            return text + (bonus > 1 ? " · bonus stars x" + bonus : "")
                + (Ladder.StreakBonus ? " · win streak" : "");
        }
    }

    // A boundary index reached by a win shows as the rank below at 3/3, matching the native medal.
    internal static string DisplayRank(int index, bool arrivedByWin) =>
        LadderRules.RankName(arrivedByWin && index > 0 && index < LadderRules.LegendIndex && index % 3 == 0 ? index - 1 : index);

    internal LadderPosition Position(bool after)
    {
        if (!HasRank) throw new InvalidOperationException("No saved ladder receipt.");
        return new LadderPosition {
            StarIndex = after ? Ladder.StarsAfter : Ladder.StarsBefore,
            BestStarIndex = after ? Ladder.BestAfter : Ladder.BestBefore,
            Multiplier = after ? Ladder.MultiplierAfter : Ladder.MultiplierBefore,
            StreakWins = after ? Ladder.StreakAfter : Ladder.StreakBefore,
            PlacementGames = after ? Ladder.PlacementAfter : Ladder.PlacementBefore,
            Rating = (after ? Ladder.RatingAfter : Ladder.RatingBefore) ?? 0,
            LegendReached = (after ? Ladder.StarsAfter : Ladder.StarsBefore) == LadderRules.LegendIndex
        };
    }

    internal string ProgressionText
    {
        get
        {
            string text = "";
            if (HeroClass != null && ClassXpBefore.HasValue && ClassXpAfter.HasValue && ClassXpAfter > ClassXpBefore)
            {
                text = LocalCoreProgression.ClassName(HeroClass) + " class progress +" + (ClassXpAfter.Value - ClassXpBefore.Value) + " XP";
                int before = LocalResultsXp.Project(ClassXpBefore.Value).Level, after = LocalResultsXp.Project(ClassXpAfter.Value).Level;
                if (after > before) text += "\nLevel up: " + before + " to " + after;
            }
            if (UnlockedCardIds.Count > 0) text += "\nCore unlocked: " + string.Join(", ", UnlockedCardIds.Select(id =>
                LocalCoreProgression.ById.TryGetValue(id, out var card) ? card.Name + " x" + card.Copies : id));
            if (ClassRewards.Count > 0) text += "\nClass rewards: " + string.Join(", ", ClassRewards.OrderBy(row => row.Level)
                .Select(row => row.Dust > 0 ? row.Dust + " dust" : PremiumName(row.Premium) + " " + RewardCardName(row) + (row.Copies > 1 ? " x" + row.Copies : "")));
            if (MasteryGrants.Any(row => row.Copies > 0)) text += "\nCard Mastery: " + string.Join(", ", MasteryGrants.Where(row => row.Copies > 0)
                .Select(row => PremiumName(row.Premium) + " " + CardName(row.CardId) + " x" + row.Copies));
            if (MasteryProgress.Count > 0) text += "\nNext: " + string.Join(" · ", MasteryProgress
                .Select(row => PremiumName(row.Premium) + " " + CardName(row.CardId) + " " + row.Wins + " / " + row.RequiredWins + " wins"));
            if (Portraits.Count > 0) text += "\nPortraits unlocked: " + string.Join(", ", Portraits.Select(PortraitText));
            return text;
        }
    }

    // Native TAG_PREMIUM: 0 Normal, 1 Golden, 2 Diamond, 3 Signature.
    internal static string PremiumName(int premium) => premium >= 1 && premium <= 3
        ? new[] { "Golden", "Diamond", "Signature" }[premium - 1] : "";

    private static string CardName(string id) => id == null ? "" : SabberStoneCore.Model.Cards.FromId(id)?.Name ?? id;

    internal static string RewardCardName(LocalClassRewardGrant grant)
    {
        string name = LocalClassRewards.Rewards.FirstOrDefault(row => row.Hero == grant.Hero && row.CardId == grant.CardId)?.Name;
        return string.IsNullOrEmpty(name) ? CardName(grant.CardId) : name;
    }

    internal static string PortraitText(LocalPortraitGrant grant) =>
        grant.Kind == "golden" ? "Golden " + CardName(grant.CardId) : "Honored " + CardName(grant.CardId);

    internal string SummaryText(LocalRewardSummary rewards) => string.Join("\n\n", new[] {
        rewards.Header, RankText, rewards.Body, ProgressionText, LocalRewardPresentation.TierRewardText(TierRewards)
    }.Where(text => !string.IsNullOrEmpty(text)));

    internal static LocalResultsReceipt FromHistory(IReadOnlyList<LocalMatchRecord> history, LocalMatchRecord match, IEnumerable<LocalCardCount> owned = null, GrindTier masteryPace = null)
    {
        if (history == null || match == null || string.IsNullOrEmpty(match.Id)) throw new ArgumentException("Settled match required.");
        int index = -1;
        for (int i = 0; i < history.Count; i++)
            if (history[i].Id == match.Id) { if (index != -1) throw new ArgumentException("Duplicate match id."); index = i; }
        if (index < 0) throw new ArgumentException("Match is not in saved history.");
        match = history[index];
        bool casual = CasualRules.IsCasual(match.Mode);
        // Season counts are ranked games only: Casual neither counts nor shows a season line.
        var ranked = history.Take(index).Where(m => !CasualRules.IsCasual(m.Mode)).ToList();
        int wins = ranked.Count(m => m.Result == "Won");
        bool arrivedByWin = history.Take(index).Select(m => m.Ladder)
            .LastOrDefault(r => r != null && r.LadderRuleVersion >= LadderRules.RuleVersion && r.StarsGained != 0)?.StarsGained > 0;
        LocalClassRewardGrant CopyGrant(LocalClassRewardGrant row) => new LocalClassRewardGrant { Hero = row.Hero, Level = row.Level,
            CardId = row.CardId, Premium = row.Premium, Copies = row.Copies, ScheduleVersion = row.ScheduleVersion,
            ScheduledCardId = row.ScheduledCardId, Dust = row.Dust, GrantedUtc = row.GrantedUtc };
        return new LocalResultsReceipt { MatchId = match.Id, Result = match.Result, Conceded = match.Conceded, Casual = casual,
            Pace = GrindTiers.Find(match.GrindTier),
            Turns = match.Turns, Gold = checked(match.Gold + match.RivalGold), PackReward = match.PackReward, Dust = match.DustReward,
            // Rule version 1 = shop-era one-star folds: shown like legacy (no rank scoop).
            Ladder = match.Ladder == null || match.Ladder.LadderRuleVersion < 2 ? null : LocalProfile.Clone(match.Ladder),
            SeasonGamesBefore = ranked.Count, SeasonGamesAfter = ranked.Count + (casual ? 0 : 1), ArrivedByWinBefore = arrivedByWin,
            SeasonWinsBefore = wins, SeasonWinsAfter = wins + (!casual && match.Result == "Won" ? 1 : 0),
            HeroClass = match.DeckHero, ClassXpBefore = match.ClassXpBefore, ClassXpAfter = match.ClassXpAfter,
            Pet = match.Pet, PetXpBefore = match.PetXpBefore, PetXpAfter = match.PetXpAfter,
            UnlockedCardIds = match.UnlockedCardIds?.ToArray() ?? Array.Empty<string>(),
            MasteryGrants = match.MasteryGrants?.Select(LocalCardMastery.Clone).ToArray() ?? Array.Empty<LocalMasteryGrant>(),
            MasteryProgress = LocalCardMastery.Next(history.Take(index + 1), match, owned, masteryPace),
            ClassRewards = match.ClassRewardGrants?.Select(CopyGrant).ToArray() ?? Array.Empty<LocalClassRewardGrant>(),
            Portraits = match.PortraitGrants?.Select(row => new LocalPortraitGrant { Hero = row.Hero, Kind = row.Kind, CardId = row.CardId, GrantedUtc = row.GrantedUtc }).ToArray()
                ?? Array.Empty<LocalPortraitGrant>(),
            AchievementGrants = match.AchievementGrants?.Select(row => new LocalAchievementGrantReceipt {
                AchievementId = row.AchievementId, CatalogVersion = row.CatalogVersion, Gold = row.Gold, Dust = row.Dust,
                StandardPacks = row.StandardPacks, CardBackId = row.CardBackId, HeroSkinId = row.HeroSkinId,
                LinkedMilestoneId = row.LinkedMilestoneId
            }).ToArray() ?? Array.Empty<LocalAchievementGrantReceipt>(),
            TierRewards = match.TierRewards?.Select(LocalProfile.Clone).ToArray() ?? Array.Empty<LocalTierReward>() };
    }
}

internal sealed class LocalResultsXp
{
    public int Level, MaxLevel = 60, XP, MaxXP;
    internal static string NextCoreRewardText(string hero, int level)
    {
        int next = LocalCoreProgression.Unlocks.Where(card => card.Hero == hero && card.Threshold > level)
            .Select(card => card.Threshold).DefaultIfEmpty(0).Min();
        return next > 0 ? "Next Core card unlock at level " + next : "";
    }
    internal static LocalResultsXp Project(int total)
    {
        if (total < 0) throw new ArgumentOutOfRangeException(nameof(total));
        int level = LocalCoreProgression.Level(total);
        return level == 60 ? new LocalResultsXp { Level = 60, XP = 1, MaxXP = 1 }
            : new LocalResultsXp { Level = level, XP = total - LocalCoreProgression.LevelThresholds[level - 1],
                MaxXP = LocalCoreProgression.LevelThresholds[level] - LocalCoreProgression.LevelThresholds[level - 1] };
    }
}

// Arguments of the native NetCache.PetsXpLevelChanged event: 1-based levels, XP is the remainder inside the level.
internal struct LocalPetXpEvent
{
    public int PetId, PreviousLevel, NewLevel, PreviousXp, NewXp;
    internal static bool TryFrom(LocalResultsReceipt receipt, out LocalPetXpEvent result)
    {
        result = default(LocalPetXpEvent);
        if (receipt == null || !receipt.HasPetXp) return false;
        string family = LocalShop.Default.PetFamily(receipt.Pet);
        if (family == null) return false;
        int before = receipt.PetXpBefore.Value, after = receipt.PetXpAfter.Value;
        result = new LocalPetXpEvent { PetId = LocalShop.Default.PetNativeId(family),
            PreviousLevel = LocalShop.Default.PetLevel(family, before), NewLevel = LocalShop.Default.PetLevel(family, after),
            PreviousXp = LocalShop.Default.PetLevelXp(family, before), NewXp = LocalShop.Default.PetLevelXp(family, after) };
        return true;
    }
}

internal enum LocalResultsPhase { Idle, AwaitNativeScreen, Celebration, HidingScoop, Rank, PetMoment, Achievement, TierReward, PremiumReward, RouteHome, Boxes, Acknowledged, RouteChoice }
// AwaitingPlayer: true while the native display waits for a click; the attempt timeout then guards only loads and stalls.
// Retire: the attempt moved on (timeout, flow failure, new receipt); the presenter must drop its UI and never show it again.
internal interface ILocalTierRewardStep { void Present(LocalResultsReceipt receipt, Action done); bool AwaitingPlayer { get; } void Retire(); }
internal sealed class LocalTierRewardStub : ILocalTierRewardStep
{
    public void Present(LocalResultsReceipt receipt, Action done) => done();
    public bool AwaitingPlayer => false;
    public void Retire() { }
}

internal interface ILocalAchievementStep
{
    void Present(LocalResultsReceipt receipt, Action done, Action failed);
    void Retire();
}
internal sealed class LocalAchievementStepStub : ILocalAchievementStep
{
    public void Present(LocalResultsReceipt receipt, Action done, Action failed) => done();
    public void Retire() { }
}

// Retail end-of-game pet XP bar and level-up scroll(s), driven from the settled receipt after rank.
// AwaitingPlayer: a level-up scroll waits for the player; the stall guard then only counts bar/load time.
// ContinueClicked: the end-game hitbox click while the bar plays (native skip).
internal interface ILocalPetStep
{
    void Present(LocalResultsReceipt receipt, Action done, Action failed);
    void Retire();
    void ContinueClicked();
    bool AwaitingPlayer { get; }
}
internal sealed class LocalPetStepStub : ILocalPetStep
{
    public void Present(LocalResultsReceipt receipt, Action done, Action failed) => done();
    public void Retire() { }
    public void ContinueClicked() { }
    public bool AwaitingPlayer => false;
}

// Both tier entry points retire presenter callbacks before entering their fallback.
internal sealed class LocalTierRewardAttempt
{
    private readonly Func<float> clock;
    private readonly Action<Exception> log;
    private Action completed, fallback;
    private ILocalTierRewardStep presenter;
    private float began;
    private int generation;
    internal LocalTierRewardAttempt(Func<float> clock, Action<Exception> log)
    {
        this.clock = clock ?? (() => (float)((double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency));
        this.log = log;
    }
    internal void Retire()
    {
        var retired = presenter;
        generation++; completed = fallback = null; presenter = null;
        // A presenter retrying after a scene change would otherwise show a chest for an attempt that already moved on.
        try { retired?.Retire(); }
        catch (Exception error) { log?.Invoke(error); }
    }
    internal void Start(ILocalTierRewardStep tier, LocalResultsReceipt receipt, Action done, Action failed)
    {
        Retire();
        int token = generation;
        began = clock(); completed = done; fallback = failed; presenter = tier;
        try
        {
            tier.Present(receipt, () => {
                if (token != generation || completed == null) return;
                var callback = completed;
                presenter = null;   // finished by itself: nothing to retire
                Retire(); callback();
            });
        }
        catch (Exception error)
        {
            if (token != generation) throw;
            Fail(error);
        }
    }
    internal void Update()
    {
        if (completed == null) return;
        if (Awaiting()) { began = clock(); return; }
        if (clock() - began >= 120f)
            Fail(new TimeoutException("Tier reward presentation timed out."));
    }
    private bool Awaiting()
    {
        try { return presenter?.AwaitingPlayer == true; }
        catch (Exception error) { log?.Invoke(error); return false; }
    }
    internal bool Fail(Exception error)
    {
        if (completed == null) return false;
        var callback = fallback;
        Retire(); log?.Invoke(error); callback();
        return true;
    }
}

// Screen readiness starts at terminal gameplay, including when settlement has not saved a receipt yet.
internal sealed class LocalResultsScreenState
{
    // Native Show waits for the hero blow-up spells and, with a pet equipped, its win/lose cutscene (up to a few seconds
    // plus the early-exit wait), so a pet match allows a longer no-Show window before the local fallback retires the screen.
    internal const float ShowLimit = 45f, PetShowLimit = 90f;
    internal static float Limit(bool petEndgame) => petEndgame ? PetShowLimit : ShowLimit;
    internal string MatchId { get; private set; }
    internal bool Fallback { get; private set; }
    private bool shown;
    private float began;
    internal bool ObserveTerminal(string match, float now)
    {
        if (MatchId == match) return false;
        MatchId = match; began = now; shown = Fallback = false;
        return true;
    }
    internal void Show(string match) { if (MatchId == match) shown = true; }
    internal bool CanShow(string match) => MatchId == match && shown && !Fallback;
    internal void Retire(string match) { if (MatchId == match) Fallback = true; }
    internal bool CheckTimeout(float now, float limit = ShowLimit)
    {
        if (MatchId == null || shown || Fallback || now - began <= limit) return false;
        Fallback = true; return true;
    }
}

internal sealed class LocalResultsFlow
{
    private readonly LocalTierRewardAttempt tierAttempt;
    internal ILocalTierRewardStep TierStep { get; set; }
    internal ILocalTierRewardStep PremiumStep { get; set; }
    internal ILocalAchievementStep AchievementStep { get; set; }
    internal ILocalPetStep PetStep { get; set; }
    internal const float PetStallSeconds = 120f;
    private readonly Func<float> clock;
    // Match ids whose pet moment was already presented (or deliberately skipped) in this process: a replay of the same
    // receipt never fires the XP event again. Restarts resume in HUB, which skips the moment entirely.
    private readonly HashSet<string> petPresented = new HashSet<string>(StringComparer.Ordinal);
    private float petBegan;
    private bool resumeInHub;
    private bool achievementComplete;
    private bool achievementContinuationPending;
    private bool boxesComplete;
    private bool noItemsPopup;
    private int noItemsAttempt;
    private Action restoreHeroLevels;
    public LocalResultsReceipt Receipt { get; private set; }
    public LocalResultsPhase Phase { get; private set; }
    public int Generation { get; private set; }
    public int RouteReleaseCount { get; private set; }
    // Set when the celebration hands over to boxes inside the finished GAMEPLAY scene instead of routing through HUB.
    public bool BoxesInGameplay { get; private set; }
    // Toast completion can release routing late in the frame, before EnterBoxes runs again.
    internal bool KeepsAchievementInGameplay => !resumeInHub && Receipt?.HasAchievements == true
        && Phase >= LocalResultsPhase.Achievement && Phase <= LocalResultsPhase.RouteHome;
    internal bool BlocksEscape => achievementContinuationPending
        || (Phase >= LocalResultsPhase.AwaitNativeScreen && Phase <= LocalResultsPhase.PremiumReward);
    internal bool CanAcknowledge => Phase == LocalResultsPhase.Boxes && boxesComplete;
    public bool RoutingReleased => RouteReleaseCount == 1;
    internal LocalResultsFlow(ILocalTierRewardStep tier = null, Func<float> clock = null, Action<Exception> log = null,
        ILocalAchievementStep achievement = null, ILocalPetStep pet = null, ILocalTierRewardStep premium = null)
    {
        TierStep = tier ?? new LocalTierRewardStub(); AchievementStep = achievement ?? new LocalAchievementStepStub();
        PetStep = pet ?? new LocalPetStepStub();
        PremiumStep = premium ?? new LocalTierRewardStub();
        this.clock = clock ?? (() => (float)((double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency));
        tierAttempt = new LocalTierRewardAttempt(clock, log);
    }
    internal void Update()
    {
        tierAttempt.Update();
        if (Phase != LocalResultsPhase.PetMoment) return;
        bool awaiting;
        try { awaiting = PetStep.AwaitingPlayer; } catch { awaiting = false; }
        if (awaiting) petBegan = clock();
        else if (clock() - petBegan >= PetStallSeconds) Fail(Receipt.MatchId, Generation, LocalResultsPhase.PetMoment);
    }
    // Hitbox click during the pet bar; ignored in every other phase.
    internal void PetContinueClicked()
    {
        if (Phase != LocalResultsPhase.PetMoment) return;
        try { PetStep.ContinueClicked(); } catch { Fail(Receipt.MatchId, Generation, LocalResultsPhase.PetMoment); }
    }
    internal void PublishHeroLevels(Action publish, Action restore)
    {
        RetireScoop();
        // Publish can update the cache before a native listener throws.
        restoreHeroLevels = restore;
        try { publish(); }
        catch { RetireScoop(); throw; }
    }
    internal void RetireScoop()
    {
        if (restoreHeroLevels == null) return;
        restoreHeroLevels();
        restoreHeroLevels = null;
    }
    internal int Begin(LocalResultsReceipt receipt, bool inHub)
    {
        RetireScoop();
        Receipt = receipt ?? throw new ArgumentNullException(nameof(receipt));
        tierAttempt.Retire(); AchievementStep.Retire(); PetStep.Retire();
        Generation++; boxesComplete = noItemsPopup = BoxesInGameplay = false; RouteReleaseCount = 0;
        resumeInHub = inHub; achievementComplete = achievementContinuationPending = false;
        Phase = LocalResultsPhase.AwaitNativeScreen;
        if (inHub)
        {
            if (receipt.HasAchievements) Phase = LocalResultsPhase.RouteHome;
            else StartTier(receipt.MatchId, Generation);
        }
        return Generation;
    }
    internal bool IsCurrent(string match, int generation, LocalResultsPhase phase) =>
        Receipt?.MatchId == match && generation == Generation && Phase == phase;
    internal void PresentNoItems(Func<Action, bool> showPopup, Action continued)
    {
        if (Receipt == null || Receipt.HasItems || Phase != LocalResultsPhase.Boxes || boxesComplete || noItemsPopup) return;
        string match = Receipt.MatchId; int generation = Generation, attempt = ++noItemsAttempt;
        noItemsPopup = showPopup(() => {
            if (!noItemsPopup || attempt != noItemsAttempt || !IsCurrent(match, generation, LocalResultsPhase.Boxes) || boxesComplete) return;
            noItemsPopup = false; boxesComplete = true;
            continued();
        });
    }
    // Test-control id of the ranked reward chest's click control (end-game hitbox, or the display's catcher after a restart).
    internal const string TierControlId = "results.tier";
    // A pending receipt starts with its owned gameplay scene, or after a restart only in a settled HUB: begun while
    // LOGIN->HUB is still loading, the scene change destroyed the tier chest and left HUB covered (live 2026-10-28).
    internal static bool CanBeginPending(bool ownsGameplay, bool settledHub) => ownsGameplay || settledHub;
    internal static bool BlockNetCacheRequest(string method) => method == "RegisterScreenEndOfGame"
        || method == "RequestNetCacheObject" || method == "NetCacheReload_Internal";
    internal static bool CountNetCacheCall(string method, bool pluginPublish) => !pluginPublish
        || (method != "UpdateRequestNeedState" && method != "NetCacheCheckRequest");
    // Exclude menu polls and pinned read-only getters; GetEarnedMetaActionReward mutates state.
    internal static bool CountFixedRewardsCall(string method) => !(method == "Get" || method == "GetDependencies"
        || method == "GetNumberOfRewardsToShow" || method == "GetNextHeroLevelReward" || method == "GetNextTotalLevelReward"
        || method == "CanCraftCard" || method.StartsWith("Is", StringComparison.Ordinal)
        || method.StartsWith("Has", StringComparison.Ordinal) || method.StartsWith("get_", StringComparison.Ordinal));
    internal static bool WaitForNativeReady(float now, ref float began, ref bool timedOut, Action timeout)
    {
        if (timedOut) return false;
        if (began == 0) began = now;
        if (now - began < 5f) return true;
        timedOut = true; timeout(); return false;
    }
    internal bool AcceptRankChild(string match, int generation, bool ownerAlive, Action destroyInstance)
    {
        if (ownerAlive && IsCurrent(match, generation, LocalResultsPhase.Rank)) return true;
        destroyInstance(); return false;
    }
    internal bool Complete(string match, int generation, LocalResultsPhase phase)
    {
        if (!IsCurrent(match, generation, phase)) return false;
        switch (phase)
        {
            case LocalResultsPhase.AwaitNativeScreen: Phase = LocalResultsPhase.Celebration; break;
            case LocalResultsPhase.Celebration: Phase = LocalResultsPhase.HidingScoop; break;
            case LocalResultsPhase.HidingScoop:
                RetireScoop();
                if (Receipt.HasRank) Phase = LocalResultsPhase.Rank; else AfterRank(match, generation);
                break;
            case LocalResultsPhase.Rank: AfterRank(match, generation); break;
            case LocalResultsPhase.PetMoment: AfterPet(match, generation); break;
            case LocalResultsPhase.TierReward:
                StartPremium(match, generation);
                break;
            case LocalResultsPhase.PremiumReward:
                Phase = resumeInHub ? LocalResultsPhase.Boxes : LocalResultsPhase.RouteHome;
                if (RouteReleaseCount == 0) RouteReleaseCount++;
                break;
            case LocalResultsPhase.RouteHome: Phase = LocalResultsPhase.Boxes; break;
            case LocalResultsPhase.Boxes: boxesComplete = true; break;
            case LocalResultsPhase.Acknowledged: Phase = LocalResultsPhase.RouteChoice; break;
            case LocalResultsPhase.RouteChoice:
                RetireScoop();
                tierAttempt.Retire(); AchievementStep.Retire(); PetStep.Retire(); Generation++; Receipt = null; Phase = LocalResultsPhase.Idle;
                boxesComplete = noItemsPopup = BoxesInGameplay = false; RouteReleaseCount = 0; break;
            default: return false;
        }
        return true;
    }
    // RouteHome ends either in HUB (restart-style) or, normally, in the same GAMEPLAY scene right after rank and XP.
    internal bool EnterBoxes(string match, int generation, bool inGameplay)
    {
        if (!IsCurrent(match, generation, LocalResultsPhase.RouteHome)) return false;
        if (achievementContinuationPending)
        {
            achievementContinuationPending = false; resumeInHub = !inGameplay;
            StartTier(match, generation);
            return true;
        }
        if (Receipt.HasAchievements && !achievementComplete)
        {
            resumeInHub = !inGameplay;
            StartAchievement(match, generation);
            return true;
        }
        BoxesInGameplay = inGameplay;
        return Complete(match, generation, LocalResultsPhase.RouteHome);
    }
    // The moment belongs to the finished GAMEPLAY scene: never in HUB resumes and never after a failed native screen.
    private void AfterRank(string match, int generation, bool allowPet = true)
    {
        if (allowPet && !resumeInHub && Receipt.HasPetXp && petPresented.Add(match)) { StartPet(match, generation); return; }
        AfterPet(match, generation);
    }
    private void StartPet(string match, int generation)
    {
        Phase = LocalResultsPhase.PetMoment; petBegan = clock();
        Action finished = () => {
            if (!IsCurrent(match, generation, LocalResultsPhase.PetMoment)) return;
            AfterPet(match, generation);
        };
        try { PetStep.Present(Receipt, finished, finished); }
        catch { PetStep.Retire(); finished(); }
    }
    private void AfterPet(string match, int generation) => StartAchievementOrTier(match, generation);
    private void StartAchievementOrTier(string match, int generation)
    {
        if (Receipt.HasAchievements && !achievementComplete) StartAchievement(match, generation);
        else StartTier(match, generation);
    }
    private void StartAchievement(string match, int generation)
    {
        Phase = LocalResultsPhase.Achievement;
        Action finished = () => {
            if (!IsCurrent(match, generation, LocalResultsPhase.Achievement)) return;
            achievementComplete = achievementContinuationPending = true;
            Phase = LocalResultsPhase.RouteHome;
            if (RouteReleaseCount == 0) RouteReleaseCount++;
        };
        try { AchievementStep.Present(Receipt, finished, finished); }
        catch { AchievementStep.Retire(); finished(); }
    }
    private void StartTier(string match, int generation)
    {
        Phase = LocalResultsPhase.TierReward;
        Action next = () => Complete(match, generation, LocalResultsPhase.TierReward);
        // The final REWARDS SAVED summary includes tier grants even when their animation fails.
        tierAttempt.Start(TierStep, Receipt, next, next);
    }
    private void StartPremium(string match, int generation)
    {
        Phase = LocalResultsPhase.PremiumReward;
        Action next = () => Complete(match, generation, LocalResultsPhase.PremiumReward);
        if (Receipt.PremiumUnlocks.Length == 0) { next(); return; }
        tierAttempt.Start(PremiumStep, Receipt, next, next);
    }
    internal bool Fail(string match, int generation, LocalResultsPhase phase)
    {
        if (!IsCurrent(match, generation, phase)) return false;
        if (phase >= LocalResultsPhase.AwaitNativeScreen && phase <= LocalResultsPhase.Rank)
        { RetireScoop(); AfterRank(match, generation, false); }
        else if (phase == LocalResultsPhase.PetMoment) { PetStep.Retire(); AfterPet(match, generation); }
        else if (phase == LocalResultsPhase.Achievement)
        {
            AchievementStep.Retire(); achievementComplete = achievementContinuationPending = true;
            Phase = LocalResultsPhase.RouteHome;
            if (RouteReleaseCount == 0) RouteReleaseCount++;
        }
        else if (phase == LocalResultsPhase.TierReward || phase == LocalResultsPhase.PremiumReward)
            return tierAttempt.Fail(new InvalidOperationException("Reward presentation failed."));
        else return false;
        return true;
    }
    internal bool Acknowledge(string match, int generation)
    {
        if (!IsCurrent(match, generation, LocalResultsPhase.Boxes) || !boxesComplete) return false;
        Phase = LocalResultsPhase.Acknowledged; return true;
    }
}

// Catch-up presentations queue independently; they never replace the pending match flow.
internal sealed class LocalTierRewardsOnly
{
    private sealed class Request { internal LocalResultsReceipt Receipt; internal Action Done; }
    private readonly Queue<Request> waiting = new Queue<Request>();
    private Request active;
    private readonly LocalTierRewardAttempt tierAttempt;
    private readonly Action<Exception> log;
    private int generation, popupAttempt;
    private bool fallback, popup;
    internal LocalTierRewardsOnly(Func<float> clock = null, Action<Exception> log = null)
    { tierAttempt = new LocalTierRewardAttempt(clock, log); this.log = log; }
    internal bool Active => active != null;
    internal bool BlocksEscape(bool matchBlocks) => Active || matchBlocks;
    internal bool CanStart(bool matchPending) => !Active && !matchPending;
    internal bool CanNavigate(bool matchPending, bool home) => !Active && (!matchPending || home);
    internal void Enqueue(LocalResultsReceipt receipt, Action done)
    {
        if (receipt == null || string.IsNullOrEmpty(receipt.MatchId)) throw new ArgumentException("Tier reward receipt id required.");
        if (done == null) throw new ArgumentNullException(nameof(done));
        waiting.Enqueue(new Request { Receipt = receipt, Done = done });
    }
    internal void Update(bool matchPendingOrActive, bool packRevealActive, ILocalTierRewardStep tier,
        Func<LocalResultsReceipt, Action, bool> showFallback = null)
    {
        if (!Active)
        {
            if (waiting.Count == 0 || matchPendingOrActive || packRevealActive) return;
            var request = active = waiting.Dequeue();
            int token = ++generation;
            fallback = popup = false;
            tierAttempt.Start(tier, request.Receipt, () => Finish(request, token), () => fallback = true);
        }
        tierAttempt.Update();
        if (!Active || !fallback || popup || showFallback == null) return;
        var expected = active;
        int expectedGeneration = generation, attempt = ++popupAttempt;
        popup = true;
        bool shown = false;
        try
        {
            shown = showFallback(expected.Receipt, () => {
                if (!popup || attempt != popupAttempt || expectedGeneration != generation || active != expected) return;
                Finish(expected, expectedGeneration);
            });
        }
        catch (Exception error) { log?.Invoke(error); }
        finally
        {
            if (!shown && expectedGeneration == generation && active == expected) popup = false;
        }
    }
    private void Finish(Request request, int token)
    {
        if (token != generation || active != request) return;
        generation++; active = null; fallback = popup = false;
        request.Done();
    }
}
