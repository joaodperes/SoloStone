using System;
using System.Collections.Generic;
using System.Linq;

// Presentation only (PROPOSED, docs/design/npc-pets.md): an NPC opponent can show its own cosmetic pet in its corner.
// Everything here is a pure function of the NPC's identity, never of the match seed or any RNG state, so the same
// NPC and deck always show the same pet. Pets are a trait of the person: presence and the owned families come from the
// NPC id and its HOME rating band (stable identity), the shown family from (NPC, deck), and the form from the
// DISPLAYED rank (the medal the player sees). Nothing here reads or writes any profile.
public static class NpcPets
{
    private const long Salt = 0x504554; // "PET"
    private const int PresenceStream = 1, CountStream = 2, PickStream = 3, FormStream = 4, DeckStream = 5;

    // Share of NPCs that own a pet, by home band (Bronze..Legend), notables included. Owner ruling 2026-10-28: Bronze about 5%,
    // Legend about 50%, since not every player who could own one wants one.
    private static readonly double[] BandShare = { 0.05, 0.12, 0.20, 0.30, 0.40, 0.50 };
    // Probability that the form is at least 2 / 3 / 4, at the tier midpoints (Bronze..Diamond) and Legend.
    private static readonly int[] AnchorIndex = { 15, 45, 75, 105, 135, LadderRules.LegendIndex };
    private static readonly double[][] FormAtLeast =
    {
        new[] { 0.10, 0.25, 0.45, 0.65, 0.85, 1.00 },
        new[] { 0.00, 0.05, 0.15, 0.35, 0.60, 0.90 },
        new[] { 0.00, 0.00, 0.03, 0.12, 0.30, 0.75 }
    };

    public static double PresenceShare(LadderBand homeBand) => BandShare[(int)homeBand];

    // Probability that the shown form is at least `form` (2..4) at a displayed star index; 1 for form 1.
    public static double FormAtLeastChance(int form, int starIndex)
    {
        if (form <= 1) return 1;
        if (form > 4) return 0;
        var table = FormAtLeast[form - 2];
        int index = Math.Max(0, Math.Min(LadderRules.LegendIndex, starIndex));
        if (index <= AnchorIndex[0]) return table[0];
        int segment = 1;
        while (AnchorIndex[segment] < index) segment++;
        double t = (double)(index - AnchorIndex[segment - 1]) / (AnchorIndex[segment] - AnchorIndex[segment - 1]);
        return table[segment - 1] + t * (table[segment] - table[segment - 1]);
    }

    // Null: no pet. `catalog` is the admitted families (any order; sorted here by native id so the pick never depends on
    // catalog order). A missing NPC id (legacy and test opponents) never shows a pet.
    public static NpcPet Resolve(NpcPetSeat seat, IReadOnlyList<NpcPetFamily> catalog)
    {
        if (seat == null || string.IsNullOrEmpty(seat.NpcId) || catalog == null) return null;
        var families = catalog.Where(family => family != null && family.Forms != null && family.Forms.Count > 0)
            .OrderBy(family => family.NativeId).ThenBy(family => family.Family, StringComparer.Ordinal).ToList();
        if (families.Count == 0) return null;
        int npc = SkilledPilot.StableHashOf(seat.NpcId);
        var band = NpcWorld.BandForStarIndex(seat.HomeStarIndex);
        if (Roll(npc, PresenceStream) >= PresenceShare(band)) return null;

        // One to three owned families, most NPCs owning one: a partial Fisher-Yates over the sorted catalog.
        double countRoll = Roll(npc, CountStream);
        int count = Math.Min(families.Count, countRoll < 0.50 ? 1 : countRoll < 0.85 ? 2 : 3);
        var pool = new List<NpcPetFamily>(families);
        var owned = new List<NpcPetFamily>();
        for (int i = 0; i < count; i++)
        {
            int pick = StableSeed.Index(StableSeed.Derive(Salt, npc, PickStream, i), pool.Count);
            owned.Add(pool[pick]);
            pool.RemoveAt(pick);
        }
        owned = owned.OrderBy(family => family.NativeId).ThenBy(family => family.Family, StringComparer.Ordinal).ToList();
        int deck = SkilledPilot.StableHashOf(seat.DeckId ?? "");
        var shown = owned[StableSeed.Index(StableSeed.Derive(Salt, npc, deck, DeckStream), owned.Count)];

        // One fixed roll against rising thresholds: a higher displayed rank only ever upgrades the form.
        double u = Roll(npc, FormStream);
        int form = 1;
        for (int k = 2; k <= 4; k++)
            if (u < FormAtLeastChance(k, seat.DisplayedStarIndex)) form = k;
        form = Math.Min(form, shown.Forms.Count);
        return new NpcPet(shown.Family, shown.NativeId, form, shown.Forms[form - 1]);
    }

    private static double Roll(int npc, int stream) =>
        unchecked((uint)StableSeed.Derive(Salt, npc, stream)) / 4294967296.0;
}

// One admitted pet family: Forms in growth order (index 0 = form 1), as LocalShop.FormsForPet lists them.
public sealed class NpcPetFamily
{
    public string Family { get; }
    public int NativeId { get; }
    public IReadOnlyList<string> Forms { get; }
    public NpcPetFamily(string family, int nativeId, IReadOnlyList<string> forms)
    { Family = family; NativeId = nativeId; Forms = forms; }
}

// The NPC seat a native match shows a pet for: world id, deck, home and displayed ladder positions (star indices).
public sealed class NpcPetSeat
{
    public string NpcId { get; }
    public string DeckId { get; }
    public int HomeStarIndex { get; }
    public int DisplayedStarIndex { get; }
    public NpcPetSeat(string npcId, string deckId, int homeStarIndex, int displayedStarIndex)
    { NpcId = npcId; DeckId = deckId; HomeStarIndex = homeStarIndex; DisplayedStarIndex = displayedStarIndex; }
}

public sealed class NpcPet
{
    public string Family { get; }
    public int NativeId { get; }
    public int Form { get; }      // 1..4, the growth stage shown
    public string CardId { get; } // the form's card id
    public NpcPet(string family, int nativeId, int form, string cardId)
    { Family = family; NativeId = nativeId; Form = form; CardId = cardId; }
}
