using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;

// Game Menu rows (owner 2026-10-28): no Achievements row (Feats opens from the HUB drawer) and
// the save row reads "Change Profile" (NativeProfileSelector also creates it with that label). Done here, after the
// owning modules have added their rows; it also collapses doubled dividers.
internal static class NativeGameMenuTidy
{
    internal const string AchievementsRow = "SoloStoneAchievementsButton", ProfileRow = "SoloStoneSaveSelection", ProfileLabel = "Change Profile";
    private static ManualLogSource log;

    internal static void Initialize(Harmony harmony, ManualLogSource logger)
    {
        log = logger;
        // Last: after the Save selection, Achievements and hidden-entries postfixes have all finished.
        harmony.Patch(AccessTools.Method(typeof(GameMenu), "GetButtons") ?? throw new MissingMethodException("GameMenu", "GetButtons"),
            postfix: new HarmonyMethod(typeof(NativeGameMenuTidy), nameof(Tidy)) { priority = Priority.Last });
    }

    private static void Tidy(ref List<UIBButton> __result)
    {
        try
        {
            if (__result == null) return;
            HandheldPolicy.TidyRows(__result, button => button.name, AchievementsRow, ProfileRow, button => button.SetText(ProfileLabel));
        }
        catch (Exception error) { log?.LogWarning("GAME_MENU tidy: " + error.Message); }
    }
}
