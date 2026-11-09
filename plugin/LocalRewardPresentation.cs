using System;
using System.Collections.Generic;
using System.Linq;

internal enum LocalRewardKind { Gold, Pack, Dust }
internal enum LocalRewardPhase { Idle, Summary, LoadingRewardBoxes, Revealing, DoneReady, Closing, Acknowledging, RouteChoice, NativeFallback, SaveError }
internal enum LocalRewardEscapeAction { Passthrough, Consume, NavigateBack }

internal sealed class LocalRewardItem
{
    public LocalRewardKind Kind { get; }
    public int Amount { get; }
    public string PackType { get; }   // Pack only: the saved receipt's pack set (null = native booster 1)

    public LocalRewardItem(LocalRewardKind kind, int amount, string packType = null)
    {
        Kind = kind;
        Amount = amount;
        PackType = packType;
    }
}

internal sealed class LocalRewardSummary
{
    public string Header { get; }
    public string Body { get; }
    public IReadOnlyList<LocalRewardItem> Items { get; }

    public LocalRewardSummary(string header, string body, IReadOnlyList<LocalRewardItem> items)
    {
        Header = header;
        Body = body;
        Items = items;
    }
}

internal sealed class LocalAchievementPresentation
{
    internal const int CaptionLimit = 64;
    internal int Count { get; }
    internal int Gold { get; }
    internal int Dust { get; }
    internal int StandardPacks { get; }
    internal string Caption { get; }
    internal string Details { get; }

    private LocalAchievementPresentation(int count, int gold, int dust, int standardPacks, string caption, string details)
    {
        Count = count; Gold = gold; Dust = dust; StandardPacks = standardPacks;
        Caption = caption; Details = details;
    }

    internal static LocalAchievementPresentation Build(IEnumerable<LocalAchievementGrantReceipt> saved)
    {
        var grants = (saved ?? Enumerable.Empty<LocalAchievementGrantReceipt>()).ToArray();
        if (grants.Length == 0) return new LocalAchievementPresentation(0, 0, 0, 0, "", "");
        var names = new List<string>();
        var rows = new List<string>();
        foreach (var grant in grants)
        {
            if (grant == null || LocalAchievementCatalog.Get(grant.AchievementId) == null)
                throw new ArgumentException("Saved achievement grant is unknown.", nameof(saved));
            string name = DisplayName(grant.AchievementId);
            names.Add(name);
            rows.Add(name + " — " + SavedRewardText(grant));
        }
        string suffix = grants.Length == 1 ? "" : " + " + (grants.Length - 1) + " more";
        int nameLimit = CaptionLimit - suffix.Length;
        string first = names[0].Length <= nameLimit ? names[0]
            : names[0].Substring(0, Math.Max(1, nameLimit - 1)) + "…";
        return new LocalAchievementPresentation(grants.Length, grants.Sum(row => row.Gold), grants.Sum(row => row.Dust),
            grants.Sum(row => row.StandardPacks), first + suffix, string.Join("\n", rows));
    }

    internal static string DisplayName(string achievementId) => LocalAchievementCatalog.Get(achievementId)?.Name
        ?? throw new ArgumentException("Unknown achievement id.", nameof(achievementId));

    internal static string SavedRewardText(LocalAchievementGrantReceipt grant)
    {
        if (grant == null) throw new ArgumentNullException(nameof(grant));
        var rewards = new List<string>();
        if (grant.Gold > 0) rewards.Add(grant.Gold + " gold");
        if (grant.Dust > 0) rewards.Add(grant.Dust + " dust");
        if (grant.StandardPacks > 0) rewards.Add(grant.StandardPacks + " Standard pack" + (grant.StandardPacks == 1 ? "" : "s"));
        if (grant.CardBackId > 0)
        {
            string id = "cardback-" + grant.CardBackId;
            rewards.Add("Card back: " + (AchievementExclusiveCosmetics.Find(id)?.Name ?? id));
        }
        if (grant.HeroSkinId != null)
        {
            string id = "heroskin-" + grant.HeroSkinId;
            rewards.Add("Hero skin: " + (AchievementExclusiveCosmetics.Find(id)?.Name ?? id));
        }
        if (grant.LinkedMilestoneId != null)
            rewards.Add("Linked milestone: " + LocalLadderMilestones.DisplayName(grant.LinkedMilestoneId));
        return rewards.Count == 0 ? "Feats points" : string.Join(", ", rewards);
    }
}

// The native widget can disappear for several reasons. Only its own Hide followed by destruction is completion.
internal sealed class LocalAchievementToastLifetime
{
    internal int Generation { get; private set; }
    internal bool Active { get; private set; }
    internal bool NormalHideObserved { get; private set; }
    private bool retiring;

    internal int Begin()
    {
        Generation++;
        Active = true;
        retiring = NormalHideObserved = false;
        return Generation;
    }

    internal bool ObserveNormalHide(int generation, bool owned)
    {
        if (!owned || !Active || retiring || generation != Generation) return false;
        NormalHideObserved = true;
        return true;
    }

    internal bool Destroyed(int generation, bool owned, bool visibleObserved)
    {
        if (!owned || !Active || retiring || generation != Generation) return false;
        bool completed = NormalHideObserved && visibleObserved;
        Active = false;
        Generation++;
        return completed;
    }

    internal void Retire()
    {
        retiring = true;
        Active = false;
        Generation++;
    }
}

internal static class LocalRewardPresentation
{
    private const int InlineAchievementDetailLimit = 4;
    internal static bool CanServiceSettlementRetry(bool readyContext, string screen,
        bool gameplayScene) => readyContext && screen == "Result" && gameplayScene;

    // gameplayPresentation: the reward boxes run in the owned end-game scene, so the receipt never routes through HUB.
    internal static bool ShouldRouteResultHome(bool readyContext, bool hasPendingReward,
        string screen, bool gameplayScene, bool resultsRoutingReleased = true, bool gameplayPresentation = false) =>
        readyContext && hasPendingReward && screen == "Result" && gameplayScene && resultsRoutingReleased && !gameplayPresentation;

    internal static bool CanPresentInScene(bool readyContext, bool hubScene) =>
        readyContext && hubScene;

    // Post-match rewards stay in the finished GAMEPLAY scene that still owns the native end-game screen.
    internal static bool CanPresentInGameplay(bool readyContext, string screen, bool gameplayScene, bool ownsPostMatch) =>
        readyContext && screen == "Result" && gameplayScene && ownsPostMatch;

    // Acknowledged and released, yet still in GAMEPLAY (a dropped navigation): leave for the deck picker.
    internal static bool ShouldRouteFinishedToPicker(bool readyContext, bool hasPendingReward, bool hasPendingMatch,
        string screen, bool gameplayScene) =>
        readyContext && !hasPendingReward && !hasPendingMatch && screen == "Menu" && gameplayScene;

    internal const string NoItemsPopupPrefix = "LOCAL_REWARD_NO_ITEMS_", DetailsPopupPrefix = "LOCAL_RESULTS_DETAILS_";
    internal const string DetailedResultsId = "results.detailed", DetailedCloseId = "results.detailed.close",
        RewardsContinueId = "rewards.continue", DetailedResultsLabel = "DETAILED RESULTS",
        // The compact card's cancel slot is narrower than CONTINUE once styled neutral: the short label fits unshrunk.
        DetailsCardLabel = "DETAILS",
        // #28: REMATCH beside Done and between CONTINUE and DETAILS on the no-reward card.
        RematchId = "results.rematch", RematchLabel = "REMATCH",
        // Owner 2026-10-28: the reward boxes' small secondary button reads "Details" beside "Rematch" (title case like Done);
        // the breakdown it opens keeps the DETAILED RESULTS header.
        BoxesDetailsLabel = "Details";
    // Done on the reward boxes, times the native size; Rematch and DETAILED RESULTS stay native (#28, owner 2026-10-28).
    internal const float DoneScale = 1.7f;
    // Before growing, Done drops this many native plate heights: its top edge stays where the native layout put it (under
    // the reward) plus a 0.3-height margin (live: grown in place it touched the reward).
    internal const float DoneDropHeights = (DoneScale - 1) * 0.5f + 0.3f;
    // Owner 2026-10-28: the secondary buttons (Rematch when offered, DETAILED RESULTS) share one row under the enlarged Done,
    // centred under it. The row sits this many native Done-plate heights below Done's centre (clears Done's enlarged half
    // and its own half with a small gap)...
    internal const float UnderDoneHeights = DoneScale * 0.5f + 0.9f;
    // ...and the column-th of count buttons is this many native Done-plate widths off Done's centre (a 0.1-width gap).
    internal static float UnderDoneColumn(int column, int count) => (column - (count - 1) * 0.5f) * 1.1f;
    // The card's grey DETAILS sits this many CONTINUE heights below the button row's centre (#28).
    internal const float CardDetailsGapHeights = 1.5f;

    // Semantic test-control ids for our own post-match AlertPopups; every other dialog keeps dialog.<button>.
    internal static string DialogButtonId(string popupId, string button)
    {
        // CONTINUE is the confirm (check) button on both cards; the red-X cancel read as "cancel" (live, 2026-10-25).
        if (popupId != null && popupId.StartsWith(NoItemsPopupPrefix, StringComparison.Ordinal))
        {
            if (button == "confirm") return RewardsContinueId;
            if (button == "cancel") return DetailedResultsId;
        }
        if (popupId != null && popupId.StartsWith(DetailsPopupPrefix, StringComparison.Ordinal))
        {
            if (button == "okay") return DetailedCloseId;
            if (button == "confirm") return RewardsContinueId;
        }
        return "dialog." + button;
    }

    // The compact no-items card shows only why nothing was earned; the full breakdown waits behind DETAILED RESULTS.
    internal static string NoItemsText(LocalRewardSummary summary)
    {
        string body = summary?.Body ?? "";
        int end = body.IndexOf('\n');
        return end < 0 ? body : body.Substring(0, end);
    }

    // The details popup owns input while open; in GAMEPLAY, Escape must not reach the native game menu.
    internal static LocalRewardEscapeAction PostMatchEscape(LocalRewardEscapeAction action, bool detailsOpen, bool inGameplay) =>
        detailsOpen || (action == LocalRewardEscapeAction.Passthrough && inGameplay) ? LocalRewardEscapeAction.Consume : action;

    internal static LocalRewardSummary Summary(LocalMatchRecord match, LocalProfile profile = null)
    {
        if (match == null || match.RewardFacts == null || string.IsNullOrEmpty(match.Result))
            throw new ArgumentException("A settled reward record is required.", nameof(match));
        string header = match.Result == "Won" ? "VICTORY" : match.Result == "Tied" ? "DRAW" : "DEFEAT";
        string progression = profile?.ProgressionSummary(match) ?? "";
        if (LocalEconomy.IsRolled(match.RewardVersion)) return CurrentSummary(match, header, progression);
        var items = new List<LocalRewardItem>();
        if (match.Gold > 0) items.Add(new LocalRewardItem(LocalRewardKind.Gold, match.Gold));
        if (match.PackReward) items.Add(new LocalRewardItem(LocalRewardKind.Pack, 1, match.Packs?.FirstOrDefault()?.PackType));
        if (match.DustReward > 0) items.Add(new LocalRewardItem(LocalRewardKind.Dust, match.DustReward));
        string savedItems = (match.PackReward ? "\n1 pack(s)" : "")
            + (match.DustReward > 0 ? "\nDust +" + match.DustReward : "");
        if (match.Gold == 0)
        {
            string reason = match.DustReward > 0 ? "No gold earned." : match.Conceded ? "No rewards: the encounter was conceded."
                : match.Result == "Abandoned" ? "No rewards: the encounter was interrupted."
                : match.Turns < 4 ? "No gold or packs: the encounter ended before four turns." : "No rewards earned.";
            return new LocalRewardSummary(header, reason + savedItems + progression, items);
        }
        var facts = new List<string> { "Completion +25" };
        if (match.Result == "Won") facts.Add("Victory +25");
        facts.Add(match.RewardFacts.DistinctCardsPlayed >= 6
            ? "6 different cards played +10" : Math.Min(6, match.RewardFacts.DistinctCardsPlayed) + "/6 different cards played");
        facts.Add(match.RewardFacts.EnemyMinionsDefeated >= 5
            ? "5 enemy minions defeated +10" : Math.Min(5, match.RewardFacts.EnemyMinionsDefeated) + "/5 enemy minions defeated");
        if (match.Result == "Won" && match.RewardFacts.MinimumHealth >= 1 && match.RewardFacts.MinimumHealth <= 10)
            facts.Add("Won after dropping to " + match.RewardFacts.MinimumHealth + " Health +20");
        else if (match.Result == "Won") facts.Add("Minimum observed Health: " + match.RewardFacts.MinimumHealth + " (bonus at 1-10)");
        return new LocalRewardSummary(header, string.Join("\n", facts) + "\n\nTotal: " + match.Gold + " gold"
            + savedItems + progression, items);
    }

    private static LocalRewardSummary CurrentSummary(LocalMatchRecord match, string header, string progression)
    {
        var achievements = LocalAchievementPresentation.Build(match.AchievementGrants);
        var items = new List<LocalRewardItem>();
        int gold = checked(match.Gold + match.RivalGold + achievements.Gold), dust = checked(match.DustReward + achievements.Dust);
        if (gold > 0) items.Add(new LocalRewardItem(LocalRewardKind.Gold, gold));
        int standardPacks = achievements.StandardPacks;
        if (match.PackReward)
        {
            string type = match.Packs?.FirstOrDefault()?.PackType;
            if (type == FairPackRules.Standard) standardPacks = checked(standardPacks + 1);
            else items.Add(new LocalRewardItem(LocalRewardKind.Pack, 1, type));
        }
        if (standardPacks > 0) items.Add(new LocalRewardItem(LocalRewardKind.Pack, standardPacks, FairPackRules.Standard));
        if (dust > 0) items.Add(new LocalRewardItem(LocalRewardKind.Dust, dust));

        var baseRewards = new List<string>();
        if (match.Gold > 0) baseRewards.Add(match.Gold + " gold");
        if (match.DustReward > 0) baseRewards.Add(match.DustReward + " dust");
        if (match.PackReward)
        {
            string type = match.Packs?.FirstOrDefault()?.PackType;
            baseRewards.Add("1 " + (string.IsNullOrEmpty(type) ? "" : type + " ") + "pack");
        }
        string reason = baseRewards.Count > 0 ? "Match reward: " + string.Join(", ", baseRewards)
                + (match.RewardVersion >= 5 && (match.Result == "Lost" || match.Result == "Tied")
                    ? "\n" + (match.Result == "Tied" ? "Draw" : "Loss") + " reward: 1/3 of a victory roll (then pace scaled)." : "")
            : achievements.Count > 0 ? "Achievement reward" + (achievements.Count == 1 ? "" : "s")
                + " saved. Open Details. No ordinary victory reward."
            : match.Conceded ? "No rewards: the encounter was conceded."
            : match.Result == "Abandoned" ? "No rewards: the encounter was interrupted."
            : match.Turns < 4 ? "No match rewards: the encounter ended before four turns."
            : match.RewardVersion == 4 && match.Result != "Won" ? "No match rewards: only victories roll a reward."
            : "No match rewards earned.";
        if (match.RivalGold > 0) reason += "\n" + RivalryText(match);
        string achievementText = achievements.Count == 0 ? "" : "\n\nAchievements:\n"
            + (achievements.Count <= InlineAchievementDetailLimit ? achievements.Details
                : achievements.Count + " achievement rewards saved.\n"
                    + "Open Feats from the home screen to inspect every condition and exact reward.");
        return new LocalRewardSummary(header, reason + achievementText + progression, items);
    }

    // Rivals-lite: the one-time gold for settling a rivalry; an NPC the world cannot name is "your rival".
    internal static string RivalryText(LocalMatchRecord match)
    {
        string name = null;
        try { name = match.NpcId == null ? null : LocalProfile.NpcDisplayName?.Invoke(match.NpcId); } catch (Exception) { }
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl)) name = "your rival";
        return "Rivalry settled with " + name + ": +" + match.RivalGold + " gold";
    }

    // Reward V3: only a win rolls gold, dust or both; the bonus lands on the gold when there is gold, else on the dust.
    private static LocalRewardSummary RolledSummary(LocalMatchRecord match, string header, string progression, List<LocalRewardItem> items)
    {
        if (match.Gold == 0 && match.DustReward == 0 && !match.PackReward)
        {
            string reason = match.Conceded ? "No rewards: the encounter was conceded."
                : match.Result == "Abandoned" ? "No rewards: the encounter was interrupted."
                : match.Turns < 4 ? "No rewards: the encounter ended before four turns."
                : match.Result == "Won" ? "No rewards earned." : "No gold or dust: only victories roll a reward.";
            return new LocalRewardSummary(header, reason + progression, items);
        }
        int bonus = match.BonusGold;
        int rolledGold = match.Gold - (match.Gold > 0 ? bonus : 0), rolledDust = match.DustReward - (match.Gold > 0 ? 0 : bonus);
        string Currency(int gold, int dust) => gold > 0 && dust > 0 ? gold + " gold + " + dust + " dust"
            : gold > 0 ? gold + " gold" : dust + " dust";
        var facts = new List<string> { "Victory roll: " + Currency(rolledGold, rolledDust) };
        facts.Add(match.RewardFacts.DistinctCardsPlayed >= 6
            ? "6 different cards played +5" : Math.Min(6, match.RewardFacts.DistinctCardsPlayed) + "/6 different cards played");
        facts.Add(match.RewardFacts.EnemyMinionsDefeated >= 5
            ? "5 enemy minions defeated +5" : Math.Min(5, match.RewardFacts.EnemyMinionsDefeated) + "/5 enemy minions defeated");
        if (match.RewardFacts.MinimumHealth >= 1 && match.RewardFacts.MinimumHealth <= 10)
            facts.Add("Won after dropping to " + match.RewardFacts.MinimumHealth + " Health +10");
        else facts.Add("Minimum observed Health: " + match.RewardFacts.MinimumHealth + " (bonus at 1-10)");
        if (bonus > 0) facts.Add("Bonus +" + bonus + (match.Gold > 0 ? " gold" : " dust"));
        string lines = "Total: " + Currency(match.Gold, match.DustReward) + (match.PackReward ? "\nGreat win: 1 pack" : "");
        return new LocalRewardSummary(header, string.Join("\n", facts) + "\n\n" + lines + progression, items);
    }

    internal static string TierRewardText(IReadOnlyList<LocalTierReward> rewards)
    {
        if (rewards == null || rewards.Count == 0) return "";
        var entries = new List<string>();
        foreach (var reward in rewards)
        {
            // Never a raw NPC/deck id: an opponent the world could not name (failed boot, renamed id) is "your opponent".
            string opponent = !string.IsNullOrEmpty(reward.OpponentName) ? reward.OpponentName
                : reward.OpponentNpcId != null ? "your opponent" : null;
            string milestone = LocalLadderMilestones.DisplayName(reward.MilestoneId);
            // Catch-up rewards have no match behind them, so no opponent to name.
            var lines = new List<string> { opponent == null ? milestone + " reached" : "You beat " + opponent + " to reach " + milestone };
            foreach (var grant in reward.Grants.Where(g => g.Kind != LocalTierGrantKind.Challengers))
            {
                if (grant.Kind == LocalTierGrantKind.Pack) lines.Add(grant.Count + " pack(s)");
                else if (grant.Kind == LocalTierGrantKind.Recipe)
                {
                    var recipe = LocalShop.Default.Recipes.FirstOrDefault(r => r.Id == grant.Ref);
                    lines.Add("Recipe: " + (recipe?.Name ?? grant.Ref));
                }
                else lines.Add(grant.Kind + ": " + grant.Ref + " x" + grant.Count);
            }
            var challengers = reward.Grants.Where(g => g.Kind == LocalTierGrantKind.Challengers).Select(g => g.Ref).ToArray();
            if (challengers.Length > 0) lines.Add("New challengers: " + string.Join(", ", challengers));
            entries.Add(string.Join("\n", lines));
        }
        return string.Join("\n\n", entries);
    }

    internal static bool CanInstantiate(string callbackMatch, int callbackGeneration, string currentMatch,
        int currentGeneration, LocalRewardPhase phase, string pendingMatch, bool terminalContext,
        bool displayExists, bool prefabAvailable) =>
        callbackMatch == currentMatch && callbackGeneration == currentGeneration
        && currentMatch == pendingMatch && phase == LocalRewardPhase.LoadingRewardBoxes
        && terminalContext && !displayExists && prefabAvailable;

    internal static bool CanAcknowledge(string ownerMatch, int ownerGeneration, string currentMatch,
        int currentGeneration, LocalRewardPhase phase, bool exactOwner, int openedMask, int itemCount) =>
        ownerMatch == currentMatch && ownerGeneration == currentGeneration && exactOwner
        && phase == LocalRewardPhase.Closing && itemCount > 0
        && openedMask == (1 << itemCount) - 1;

    internal static LocalRewardEscapeAction RewardEscape(bool exactOwner, LocalRewardPhase phase,
        int openedMask, int itemCount)
    {
        if (!exactOwner || (phase != LocalRewardPhase.Revealing
            && phase != LocalRewardPhase.DoneReady && phase != LocalRewardPhase.Closing))
            return LocalRewardEscapeAction.Passthrough;
        return phase == LocalRewardPhase.DoneReady && itemCount > 0
            && openedMask == (1 << itemCount) - 1
            ? LocalRewardEscapeAction.NavigateBack : LocalRewardEscapeAction.Consume;
    }

    internal static bool UnexpectedDestroy(bool exactOwner, LocalRewardPhase phase) => exactOwner
        && phase != LocalRewardPhase.Closing && phase != LocalRewardPhase.Acknowledging
        && phase != LocalRewardPhase.RouteChoice && phase != LocalRewardPhase.Idle;

    internal static bool SuppressArenaNarrative(bool hasTrackedOwner, bool isCurrentSingleton, LocalRewardPhase phase) =>
        hasTrackedOwner && isCurrentSingleton
        && (phase == LocalRewardPhase.Revealing || phase == LocalRewardPhase.DoneReady);

    internal static bool SuppressRankedIntro(bool isolated, bool sceneInitialized, bool tournament) =>
        isolated && sceneInitialized && tournament;
}

// Pure placement math for the native RewardBoxes prefab outside the HUB (no UnityEngine calls, unit-tested).
// The prefab is authored for the HUB box camera, which looks straight down world -Y with +Z as screen up. In the
// finished GAMEPLAY scene the layer-19 end-game camera sits elsewhere, so the HUB pose put the boxes ~38x too large and
// above the screen (lane-1 live, 2026-10-25). The whole display is moved rigidly: the prefab's (X, Y, Z) axes map to
// the camera's (right, -forward, up), the layout centre lands at the HUB's on-screen spot and the layout is scaled so
// box-to-Done spacing matches the HUB measurement.
internal struct LocalVec
{
    public double X, Y, Z;
    public LocalVec(double x, double y, double z) { X = x; Y = y; Z = z; }
    public static LocalVec operator +(LocalVec a, LocalVec b) => new LocalVec(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static LocalVec operator -(LocalVec a, LocalVec b) => new LocalVec(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static LocalVec operator *(LocalVec a, double s) => new LocalVec(a.X * s, a.Y * s, a.Z * s);
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public static double Dot(LocalVec a, LocalVec b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}

internal sealed class LocalRewardPlacement
{
    // HUB measurement (lane 1): box centre (666,240) and Done centre (666,324) in 1280x720 UI space.
    internal const double BoxToDoneScreenShare = 84.0 / 720.0, AnchorHeightShare = 1 - 282.0 / 720.0;
    public LocalVec Position;
    public double Qx, Qy, Qz, Qw;   // world rotation to pre-multiply onto the display's current rotation
    public double Scale;            // factor on the display's current local scale

    public LocalVec Rotate(LocalVec v)
    {
        // v' = q v q*, expanded.
        double tx = 2 * (Qy * v.Z - Qz * v.Y), ty = 2 * (Qz * v.X - Qx * v.Z), tz = 2 * (Qx * v.Y - Qy * v.X);
        return new LocalVec(v.X + Qw * tx + (Qy * tz - Qz * ty), v.Y + Qw * ty + (Qz * tx - Qx * tz), v.Z + Qw * tz + (Qx * ty - Qy * tx));
    }

    // Where a prefab point lands after placement (root at rootPosition before the move).
    public LocalVec Map(LocalVec point, LocalVec anchor, LocalVec target) => target + Rotate((point - anchor) * Scale);

    // targets: the layout's box target bones; done: the Done button; root: the display's world position.
    // depth: distance in front of the camera (> near plane); halfHeight: half the visible height at that depth
    // (perspective: depth * tan(fov / 2); orthographic: orthographicSize).
    internal static LocalRewardPlacement Fit(IReadOnlyList<LocalVec> targets, LocalVec done, LocalVec root,
        LocalVec cameraPosition, LocalVec right, LocalVec up, LocalVec forward, double depth, double halfHeight)
    {
        if (targets == null || targets.Count == 0) throw new ArgumentException("A reward layout is required.", nameof(targets));
        if (!(depth > 0) || !(halfHeight > 0) || double.IsInfinity(depth) || double.IsInfinity(halfHeight))
            throw new ArgumentException("Invalid camera framing.");
        LocalVec boxes = new LocalVec();
        foreach (LocalVec target in targets) boxes += target;
        boxes = boxes * (1.0 / targets.Count);
        double spacing = (done - boxes).Length;
        if (!(spacing > 1e-6)) throw new ArgumentException("Degenerate reward layout.");
        LocalVec anchor = (boxes + done) * 0.5;
        right = Unit(right); up = Unit(up); forward = Unit(forward);
        if (Math.Abs(LocalVec.Dot(right, up)) > 1e-3 || Math.Abs(LocalVec.Dot(right, forward)) > 1e-3 || Math.Abs(LocalVec.Dot(up, forward)) > 1e-3)
            throw new ArgumentException("Camera axes are not orthogonal.");
        var placement = new LocalRewardPlacement { Scale = BoxToDoneScreenShare * 2 * halfHeight / spacing };
        // Rotation matrix columns: prefab X -> right, prefab Y -> -forward, prefab Z -> up.
        LocalVec c0 = right, c1 = forward * -1, c2 = up;
        double m00 = c0.X, m10 = c0.Y, m20 = c0.Z, m01 = c1.X, m11 = c1.Y, m21 = c1.Z, m02 = c2.X, m12 = c2.Y, m22 = c2.Z;
        double trace = m00 + m11 + m22;
        if (trace > 0)
        {
            double s = Math.Sqrt(trace + 1) * 2;
            placement.Qw = s / 4; placement.Qx = (m21 - m12) / s; placement.Qy = (m02 - m20) / s; placement.Qz = (m10 - m01) / s;
        }
        else if (m00 > m11 && m00 > m22)
        {
            double s = Math.Sqrt(1 + m00 - m11 - m22) * 2;
            placement.Qw = (m21 - m12) / s; placement.Qx = s / 4; placement.Qy = (m01 + m10) / s; placement.Qz = (m02 + m20) / s;
        }
        else if (m11 > m22)
        {
            double s = Math.Sqrt(1 + m11 - m00 - m22) * 2;
            placement.Qw = (m02 - m20) / s; placement.Qx = (m01 + m10) / s; placement.Qy = s / 4; placement.Qz = (m12 + m21) / s;
        }
        else
        {
            double s = Math.Sqrt(1 + m22 - m00 - m11) * 2;
            placement.Qw = (m10 - m01) / s; placement.Qx = (m02 + m20) / s; placement.Qy = (m12 + m21) / s; placement.Qz = s / 4;
        }
        LocalVec spot = cameraPosition + forward * depth + up * ((AnchorHeightShare - 0.5) * 2 * halfHeight);
        placement.Position = placement.Map(root, anchor, spot);
        return placement;
    }

    // A native world-space drift magnitude (HUB units) for a display placed at `scale`; unplaced/invalid scale keeps it.
    internal static float WorldDrift(float magnitude, double scale) =>
        scale > 0 && !double.IsInfinity(scale) && !double.IsNaN(scale) ? (float)(magnitude * scale) : magnitude;

    private static LocalVec Unit(LocalVec v)
    {
        double length = v.Length;
        if (!(length > 1e-9)) throw new ArgumentException("Zero camera axis.");
        return v * (1 / length);
    }
}

// A viewport projection (Unity WorldToViewportPoint: x/y 0..1 from left/bottom, z = depth in front of the camera).
internal struct LocalViewportPoint
{
    public string Name;
    public double X, Y, Z;
    public bool Loose;   // box start bones: the fly-in may begin at the screen edge
    public LocalViewportPoint(string name, double x, double y, double z, bool loose = false) { Name = name; X = x; Y = y; Z = z; Loose = loose; }
}

// One PegUI pick at a sample point of a reward control. Unsupported: no mouse hit test (touch mode, input missing).
internal enum LocalHitProbe { Unsupported, Target, Covered, Nothing }

internal struct LocalCameraFacts
{
    public int CullingMask;
    public float Depth;
    public bool Enabled, RendersToTexture;
}

// The post-match reward presentation must never strand the player (review 2026-10-25): boxes and Done are proven on
// screen and reachable, otherwise the saved-rewards popup takes over (rewards are already saved). HUB Box buttons are
// gated only while the display or one of the presentation's own popups is actually up. Pure; NativeRewardUI feeds it.
internal static class LocalRewardSafety
{
    internal const double Inset = 0.02, LooseMargin = 0.25;
    internal const float BoxesTimeout = 12f, DoneTimeout = 8f, DoneClickableGrace = 2f;
    internal const string FallbackPopupPrefix = "LOCAL_REWARD_FALLBACK_", AckRetryPopupPrefix = "LOCAL_REWARD_ACK_RETRY_",
        SettleRetryPopupId = "LOCAL_REWARD_SETTLE_RETRY";

    // null when every point is in front of the near plane and inside the viewport: box targets, rewards and Done at least
    // Inset inside every edge; loose points (start bones) within LooseMargin outside. Otherwise the first failure.
    internal static string OffScreen(IEnumerable<LocalViewportPoint> points, double near)
    {
        foreach (LocalViewportPoint point in points)
        {
            double low = point.Loose ? -LooseMargin : Inset, high = 1 - low;
            if (!(point.Z > near)) return point.Name + " behind the near plane (z=" + F(point.Z) + ",near=" + F(near) + ")";
            if (!(point.X >= low && point.X <= high && point.Y >= low && point.Y <= high))
                return point.Name + " off screen (x=" + F(point.X) + ",y=" + F(point.Y) + ")";
        }
        return null;
    }

    // The camera that frames the end-game screen's layer: enabled, on screen, culling the layer; the most dedicated mask
    // wins (a board camera with an Everything mask loses to a layer-only UI camera), then the one drawn last. -1: none.
    internal static int PickCamera(IReadOnlyList<LocalCameraFacts> cameras, int layer)
    {
        int best = -1;
        for (int index = 0; index < cameras.Count; index++)
        {
            LocalCameraFacts camera = cameras[index];
            if (!camera.Enabled || camera.RendersToTexture || (camera.CullingMask & 1 << layer) == 0) continue;
            if (best < 0) { best = index; continue; }
            int bits = Bits(camera.CullingMask), bestBits = Bits(cameras[best].CullingMask);
            if (bits < bestBits || bits == bestBits && camera.Depth > cameras[best].Depth) best = index;
        }
        return best;
    }

    // A reward control is clickable only when it is live (active, enabled hit plate), on screen, and the input system's
    // own pick at one of its plates lands on it: a covering hitbox (live 2026-10-25: EndGameScreen "Click to continue")
    // makes it unclickable, so the watchdog falls back. Without a usable hit test the screen checks alone decide.
    internal static bool Clickable(bool live, string offscreen, IEnumerable<LocalHitProbe> probes)
    {
        if (!live || offscreen != null) return false;
        List<LocalHitProbe> picks = (probes ?? Enumerable.Empty<LocalHitProbe>()).Where(probe => probe != LocalHitProbe.Unsupported).ToList();
        return picks.Count == 0 || picks.Contains(LocalHitProbe.Target);
    }

    // Revealing: every box must have been clickable (or opened) within BoxesTimeout, and Done must show within DoneTimeout
    // of the last box opening. DoneReady: Done stays clickable on screen (not while DETAILED RESULTS owns input).
    // sinceAllOpened < 0: not every box opened yet. null = healthy.
    internal static string Watchdog(LocalRewardPhase phase, float sinceReveal, bool everyBoxReachable, float sinceAllOpened,
        float sinceDoneReady, bool doneClickable, bool detailsOpen)
    {
        if (phase == LocalRewardPhase.Revealing)
        {
            if (sinceAllOpened >= 0) return sinceAllOpened > DoneTimeout ? "Done not shown " + DoneTimeout + "s after the last box opened" : null;
            return !everyBoxReachable && sinceReveal > BoxesTimeout ? "boxes not clickable on screen " + BoxesTimeout + "s into the reveal" : null;
        }
        if (phase == LocalRewardPhase.DoneReady && !detailsOpen && !doneClickable && sinceDoneReady > DoneClickableGrace)
            return "Done not clickable on screen";
        return null;
    }

    internal static bool IsPresentationPopup(string id) => id != null
        && (id.StartsWith(LocalRewardPresentation.NoItemsPopupPrefix, StringComparison.Ordinal)
            || id.StartsWith(LocalRewardPresentation.DetailsPopupPrefix, StringComparison.Ordinal)
            || id.StartsWith(FallbackPopupPrefix, StringComparison.Ordinal)
            || id.StartsWith(AckRetryPopupPrefix, StringComparison.Ordinal) || id == SettleRetryPopupId);

    // HUB Box buttons (Play, Collection, Store, Packs) ignore presses only while the presentation is visibly up: never on
    // a phase that a dropped acknowledgement left behind.
    internal static bool BlocksHomeInput(bool displayUp, IEnumerable<string> dialogIds) =>
        displayUp || (dialogIds ?? Enumerable.Empty<string>()).Any(IsPresentationPopup);

    // Ready HUB with no pending receipt and nothing up: any non-Idle phase is stale and resets.
    internal static bool ResetToIdle(LocalRewardPhase phase, bool hubReady, bool pendingReceipt, bool presentationUp) =>
        phase != LocalRewardPhase.Idle && hubReady && !pendingReceipt && !presentationUp;

    // Historical receipts keep booster 1. Current V4 uses the exact saved pack type when it maps to a usable
    // native booster; anything else is booster 1 rather than the fallback popup.
    internal static int BoosterId(int rewardVersion, string packType, Func<string, int> nativeId, Func<int, bool> usable)
    {
        if (!LocalEconomy.IsRolled(rewardVersion) || string.IsNullOrEmpty(packType)) return 1;
        int id;
        try { id = nativeId(packType); }
        catch (Exception) { return 1; }
        if (id <= 0 || id == 1) return 1;
        try { return usable(id) ? id : 1; }
        catch (Exception) { return 1; }
    }

    internal static bool SameBooster(int expectedId, int expectedCount, int actualId, int actualCount) =>
        expectedCount > 0 && actualId == expectedId && actualCount == expectedCount;

    private static int Bits(int mask)
    {
        int count = 0;
        for (uint value = unchecked((uint)mask); value != 0; value &= value - 1) count++;
        return count;
    }

    private static string F(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
