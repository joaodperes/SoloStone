using System;
using System.Linq;

// Player-facing acquisition copy for the closed achievement-exclusive cosmetic registry.
internal static class LocalAchievementCosmeticCopy
{
    internal static string ForHeroSkin(string heroSkinId, string retailFlavor)
    {
        if (!AchievementExclusiveCosmetics.ContainsHeroSkin(heroSkinId)) return null;
        return Build(retailFlavor, LocalAchievementCatalog.All.Single(row => row.Reward.HeroSkinId == heroSkinId));
    }

    internal static string ForCardBack(int cardBackId, string retailDescription)
    {
        if (!AchievementExclusiveCosmetics.ContainsCardBack(cardBackId)) return null;
        string flavor = (retailDescription ?? "").Replace("\r\n", "\n");
        int acquisition = flavor.IndexOf("\n\n", StringComparison.Ordinal);
        if (acquisition >= 0) flavor = flavor.Substring(0, acquisition);
        return Build(flavor, LocalAchievementCatalog.All.Single(row => row.Reward.CardBackId == cardBackId));
    }

    private static string Build(string flavor, LocalAchievementDefinition achievement)
    {
        string acquisition = "Earned through " + achievement.Name;
        char last = acquisition[acquisition.Length - 1];
        if (last != '.' && last != '!' && last != '?' && last != '…') acquisition += ".";
        string prefix = (flavor ?? "").Trim();
        return (prefix.Length == 0 ? "" : prefix + "\n\n")
            + acquisition + "\nOpen Feats to view the condition.";
    }
}
