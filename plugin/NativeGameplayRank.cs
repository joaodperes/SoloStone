using System;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;

// Gameplay Players are offline, so retail gives them empty rank translators. Bind the frozen local match instead;
// native NameBanner still owns medal visuals and ShowOpponentRankInGame policy.
internal static class NativeGameplayRank
{
    private static ManualLogSource log;

    internal static void Initialize(Harmony harmony, ManualLogSource logger)
    {
        log = logger;
        var ranked = AccessTools.Method(typeof(GameUtils), nameof(GameUtils.IsGameTypeRanked), Type.EmptyTypes);
        var playerRank = AccessTools.Method(typeof(Player), nameof(Player.GetRank), Type.EmptyTypes);
        if (ranked == null || playerRank == null) throw new MissingMethodException("Required gameplay rank hook missing");
        harmony.Patch(ranked, postfix: new HarmonyMethod(typeof(NativeGameplayRank), nameof(ProjectRankedGame)));
        harmony.Patch(playerRank, postfix: new HarmonyMethod(typeof(NativeGameplayRank), nameof(ProjectPlayerRank)));
    }

    private static bool OwnsMatch(out LocalProfile profile, out LocalMatchRecord pending)
    {
        profile = LocalLadder.Profile;
        pending = profile?.Pending;
        // #37: a Casual match shows no rank in game (the native unranked GT_VS_AI presentation applies).
        if (pending != null && CasualRules.IsCasual(pending.Mode)) return false;
        return NativeUIProbe.OwnsGameplayRank(LocalLadder.Screen, LocalLadder.MatchId, pending?.Id,
            ClientBridge.DiagnosticMatchId);
    }

    private static void ProjectRankedGame(ref bool __result)
    {
        if (!__result && OwnsMatch(out _, out _)) __result = true;
    }

    private static void ProjectPlayerRank(Player __instance, ref MedalInfoTranslator __result)
    {
        if (__instance == null || !OwnsMatch(out LocalProfile profile, out LocalMatchRecord pending)) return;
        try
        {
            var ranked = profile.RankedHistory;
            LadderPosition position;
            double rating;
            int wins;
            bool arrivedByWin;
            double? playerLegendRating = null;
            if (__instance.GetSide() == Player.Side.FRIENDLY)
            {
                position = profile.Ladder;
                rating = position.Rating;
                wins = ranked.Count(match => match.Result == "Won");
                arrivedByWin = profile.LadderArrivedByWin;
            }
            else if (__instance.GetSide() == Player.Side.OPPOSING && pending.NpcRating.HasValue)
            {
                rating = pending.NpcRating.Value;
                position = NativeUIProbe.ProjectOpponentLadder(rating);
                wins = 0;
                arrivedByWin = false;
                if (profile.Ladder.LegendReached) playerLegendRating = profile.Ladder.Rating;
            }
            else return;

            var league = NativeUIProbe.ResolveMedalLeague(profile.Ladder, out _);
            int top = league.Ranks.Max(row => row.StarLevel);
            var medal = LadderRules.Medal(position, league.ID, league.Ranks.Select(row => new LadderRankRow {
                StarLevel = row.StarLevel, Stars = row.Stars, Legend = row.StarLevel == top
            }), arrivedByWin, GrindTiers.Find(pending.GrindTier).Ladder);   // the match's stamp, not a later pace change
            var packet = NativeUIProbe.ProjectMedal(medal, rating, ranked.Count, wins, playerLegendRating);
            if (!NativeUIProbe.CompleteGameplayMedal(medal, packet, NativeUIProbe.LocalDeckFormat)) return;
            var translated = new MedalInfoTranslator(packet);
            var current = translated.GetCurrentMedal(NativeUIProbe.LocalDeckFormat);
            if (!translated.IsDisplayable() || !current.IsValid()) return;
            __result = translated;
            log?.LogInfo("GAMEPLAY_MEDAL match=" + pending.Id + " side=" + __instance.GetSide()
                + " league=" + current.leagueId + " level=" + current.starLevel + " legend=" + current.legendIndex
                + " showOpponent=" + current.RankConfig.ShowOpponentRankInGame);
        }
        catch (Exception error)
        {
            log?.LogWarning("GAMEPLAY_MEDAL projection failed side=" + __instance.GetSide() + " " + error.Message);
        }
    }
}
