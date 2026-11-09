using System;
using System.Collections.Generic;
using System.Linq;

// Glue between a pending match record and the pure NpcPets resolver (PROPOSED, docs/design/npc-pets.md).
// Reads the shop catalog and the NPC world; writes nothing, and never touches a profile, receipt or pet progress.
internal static class NpcPetSeats
{
    // Admitted pet families only: the shop sells them (catalog row not marked verify). Forms are in growth order.
    internal static IReadOnlyList<NpcPetFamily> Families(LocalShop shop) => shop.Products
        .Where(product => product.Kind == "Pet" && product.Pet != null && !product.verify && shop.KnownPet(product.Pet))
        .Select(product => new NpcPetFamily(product.Pet, shop.PetNativeId(product.Pet), shop.FormsForPet(product.Pet)))
        .ToArray();

    // The pet an NPC opponent shows for a pending record; null for legacy and test opponents (no world id) and for a
    // record without a displayed rating. The home band comes from the world identity, else from the displayed rating.
    // The form follows the rank the player SEES (owner 2026-10-28): the NPC's medal in Ranked, the player's own ladder rank
    // in Casual (NpcPremiums.SeatStarIndex, shared with the premium finishes), never the hidden Casual rating.
    internal static NpcPet For(LocalMatchRecord record, LocalShop shop, int playerStarIndex = 0)
    {
        if (record == null || string.IsNullOrEmpty(record.NpcId) || !(record.NpcRating is double rating)
            || double.IsNaN(rating) || double.IsInfinity(rating)) return null;
        int displayed = NpcPremiums.SeatStarIndex(record.Mode, rating, playerStarIndex);
        var identity = NpcWorldRuntime.World?.Find(record.NpcId);
        int home = identity != null ? LadderRules.IndexForRating(identity.HomeRating) : displayed;
        var seat = new NpcPetSeat(record.NpcId, record.NpcDeckId ?? record.Opponent, home, displayed);
        return NpcPets.Resolve(seat, Families(shop));
    }
}
