using System;
using System.Collections.Generic;
using System.Linq;
using PegasusShared;
using SabberStoneCore.Model;

internal static class NativeDeckRunes
{
    internal static RuneType[] Order(string hero, IEnumerable<string> ids)
    {
        if (hero != "HERO_11") return new[] { RuneType.RT_NONE, RuneType.RT_NONE, RuneType.RT_NONE };
        var cards = ids.Select(Cards.FromId).ToArray();
        LocalDecks.ValidateRunes(cards, SabberStoneCore.Enums.CardClass.DEATHKNIGHT);
        int[] counts = LocalDecks.RuneCounts(cards);
        return Enumerable.Repeat(RuneType.RT_BLOOD, counts[0]).Concat(Enumerable.Repeat(RuneType.RT_FROST, counts[1]))
            .Concat(Enumerable.Repeat(RuneType.RT_UNHOLY, counts[2])).Concat(Enumerable.Repeat(RuneType.RT_NONE, 3))
            .Take(3).ToArray();
    }

    internal static bool Compatible(string hero, IEnumerable<string> ids, IEnumerable<RuneType> order)
    {
        RuneType[] actual = order.ToArray();
        if (actual.Length != 3 || actual.Any(rune => rune != RuneType.RT_NONE && rune != RuneType.RT_BLOOD
            && rune != RuneType.RT_FROST && rune != RuneType.RT_UNHOLY)) return false;
        RuneType[] required = Order(hero, ids);
        return hero == "HERO_11" ? new[] { RuneType.RT_BLOOD, RuneType.RT_FROST, RuneType.RT_UNHOLY }
            .All(rune => actual.Count(value => value == rune) >= required.Count(value => value == rune))
            : actual.SequenceEqual(required);
    }
}
