#if !SOLOSTONE_RELEASE
using System;
using System.Linq;
using MiniJSON;

// Read-only loaded DBF rows; IDs, XP and enabled flags must come from this pinned client, never card-name suffixes.
internal static class PetDatabaseExport
{
    internal static JsonNode Export()
    {
        NativeTestControl.AssertMainThread();
        if (!GameDbf.IsLoaded || GameDbf.Pet == null || GameDbf.PetLevel == null || GameDbf.PetVariant == null || GameDbf.PetXp == null)
            throw new InvalidOperationException("Pet database has not loaded");
        var pets = GameDbf.Pet.GetRecords();
        var levels = GameDbf.PetLevel.GetRecords();
        var variants = GameDbf.PetVariant.GetRecords();
        var xp = GameDbf.PetXp.GetRecords();
        if (pets == null || levels == null || variants == null || xp == null || pets.Count == 0)
            throw new InvalidOperationException("No loaded pet records");
        return new JsonNode {
            { "pets", TestControlProtocol.List(pets.OrderBy(row => row.ID).Select(row => (object)new JsonNode {
                { "id", row.ID }, { "enabled", row.Enabled }, { "name", row.Name?.GetString() },
                { "xpInfo", row.XpInfoRecord?.ID } })) },
            { "levels", TestControlProtocol.List(levels.OrderBy(row => row.PetId).ThenBy(row => row.Level).Select(row => (object)new JsonNode {
                { "id", row.ID }, { "petId", row.PetId }, { "level", row.Level },
                { "petVariantUnlocked", row.PetVariantUnlocked }, { "nextXpNeeded", row.NextXpNeeded } })) },
            { "variants", TestControlProtocol.List(variants.OrderBy(row => row.ID).Select(row => (object)new JsonNode {
                { "id", row.ID }, { "petId", row.PetId }, { "enabled", row.Enabled },
                { "cardDbfId", row.CardId }, { "cardId", GameUtils.TranslateDbIdToCardId(row.CardId) },
                { "name", row.Name?.GetString() }, { "petIcon", row.PetIcon } })) },
            { "xp", TestControlProtocol.List(xp.OrderBy(row => row.ID).Select(row => (object)new JsonNode {
                { "id", row.ID }, { "small", row.SmallXp }, { "medium", row.MediumXp }, { "large", row.LargeXp } })) }
        };
    }
}
#endif
