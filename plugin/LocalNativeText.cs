using System;
using System.Collections.Generic;
using System.Linq;
using SabberStoneCore.Model;
using EngineCard = SabberStoneCore.Model.Card;

internal static class LocalNativeText
{
    // CardTextBuilder.IsAssembledCard and TextUtils.ParseTextForCraftableSplit read component dbfIds.
    internal const int FirstCompositeHalfTag = 4572;
    internal const int SecondCompositeHalfTag = 4573;
    private const string ObsoleteSpellDamageSuffix = " @spelldmg";
    private static readonly string[] AdditionalEngineIds = {
        "GAME_005",
        "NEW1_012o", "EX1_019e", "EX1_603e", "CS2_222o", "CS2_004e", "EX1_561e",
        "EX1_411e", "EX1_414e", "CS1_129e", "CS2_236e", "CS2_087e", "CS2_092e",
        "EX1_360e", "EX1_382e", "EX1_619e", "EX1_366e", "EX1_355e"
    };
    private static readonly Dictionary<string, string> TextByNativeId = BuildTextByNativeId();

    internal static IReadOnlyCollection<string> SupportedIds => TextByNativeId.Keys;

    internal static bool TryGetNameOverride(bool localContext, string cardId, Func<string, string> transform,
        out string name)
    {
        name = null;
        if (!localContext || cardId != "EX1_084e" || transform == null) return false;
        name = transform(Cards.FromId(cardId).Name);
        return true;
    }

    internal static bool TryGetOverride(bool staged, string cardId, Func<string, string> defaultVariant,
        out string text)
    {
        text = null;
        if (!staged || string.IsNullOrEmpty(cardId) || defaultVariant == null) return false;
        try
        {
            if (!TextByNativeId.TryGetValue(cardId, out string rawText) || string.IsNullOrEmpty(rawText))
                return false;
            text = NormalizeRawText(defaultVariant(rawText));
            return !string.IsNullOrEmpty(text);
        }
        catch
        {
            text = null;
            return false;
        }
    }

    internal static string NormalizeRawText(string text) =>
        text != null && text.EndsWith(ObsoleteSpellDamageSuffix, StringComparison.Ordinal)
            ? text.Substring(0, text.Length - ObsoleteSpellDamageSuffix.Length)
            : text;

    private static Dictionary<string, string> BuildTextByNativeId()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var card in LocalEconomy.Catalog) Add(result, card);
        }
        catch { }
        var heroesAndPowers = LocalDecks.PlayerHeroes.SelectMany(id => new[] {
            id, Cards.FromAssetId(Cards.FromId(id)[SabberStoneCore.Enums.GameTag.HERO_POWER]).Id
        });
        foreach (string id in heroesAndPowers.Concat(AdditionalEngineIds).Concat(LocalDecks.TestStandardCardIds).Concat(LocalCardCatalog.SupplementalGeneratedIds)
                     .Concat(LocalDecks.TestLoaners.SelectMany(deck => deck.CardIds)).Concat(LocalCardCatalog.LegacyGeneratedPublicIds).Concat(LocalCardCatalog.CardStateGeneratedIds))
        {
            try { Add(result, Cards.FromId(id)); }
            catch { }
        }
        return result;
    }

    private static void Add(Dictionary<string, string> result, EngineCard card)
    {
        if (card != null) result[NativeMatch.TransportCardId(card.Id)] = card.Text;
    }
}
