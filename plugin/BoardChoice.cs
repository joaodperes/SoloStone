using System.Collections.Generic;
using System.Linq;

internal static class BoardChoice
{
    // Constructed boards the client loads in a real match (Windows-verified; docs/design/game-boards.md). Boards 1-7 are
    // free for everyone; the rest are sold in the shop (Themes > Boards) (data/shop/catalog-v1.json, verify=false).
    // Drop a broken id or add a verified one here only, together with its catalogue row.
    // 2026-10-25 lane 2: every id above 7 loaded in a real NPC match (ForceBoardId) with no load failure or retry.
    internal static readonly int[] Allowlist = { 1, 2, 3, 4, 5, 6, 7,
        8, 10, 11, 12, 13, 14, 26, 54, 61, 64, 65, 68, 70, 73, 75, 78, 84, 86, 89, 91,
        106, 118, 120, 126, 129, 130, 131, 133, 134, 140, 141, 142, 143 };
    internal static readonly int[] FreeBoards = { 1, 2, 3, 4, 5, 6, 7 };

    internal static int Pick(int seed, IEnumerable<int> presentIds, int forcedId)
        => Pick(seed, presentIds, forcedId, Allowlist);

    internal static int Pick(int seed, IEnumerable<int> presentIds, int forcedId, IEnumerable<int> allowlist)
    {
        var present = new HashSet<int>(presentIds);
        if (forcedId > 0 && present.Contains(forcedId)) return forcedId;
        var eligible = allowlist.Where(present.Contains).Distinct().OrderBy(id => id).ToArray();
        if (eligible.Length == 0) return 1;
        return eligible[Mix(seed) % (uint)eligible.Length];
    }

    // The player's board cosmetic (owner 2026-10-25), NPC matches included: a fixed owned board always loads when it
    // is verified and present; selected 0 ("Random owned") picks uniformly among owned verified boards from the match
    // seed. Anything unusable falls back to that random pick, then to the free boards, then to board 1.
    internal static int ForPlayer(int seed, IEnumerable<int> presentIds, int forcedId, int selected, IEnumerable<int> owned)
    {
        var present = new HashSet<int>(presentIds);
        var bought = new HashSet<int>(owned ?? Enumerable.Empty<int>());
        if (forcedId > 0 && present.Contains(forcedId)) return forcedId;
        var usable = new HashSet<int>(Allowlist.Where(present.Contains));
        if (selected > 0 && usable.Contains(selected) && (FreeBoards.Contains(selected) || bought.Contains(selected))) return selected;
        var pool = FreeBoards.Concat(bought).Distinct().Where(usable.Contains).ToArray();
        return Pick(seed, present, 0, pool.Length == 0 ? Allowlist : pool);
    }

    private static uint Mix(int seed)
    {
        unchecked
        {
            uint mixed = (uint)seed;
            mixed = (mixed ^ (mixed >> 16)) * 0x85ebca6bu;
            mixed = (mixed ^ (mixed >> 13)) * 0xc2b2ae35u;
            mixed ^= mixed >> 16;
            return mixed;
        }
    }
}
