using System;

// Filled by the playtest queue only after each native capability is qualified.
public sealed class NpcPresentationSettings
{
    public bool Emotes { get; set; }
    public bool Hover { get; set; }
    public bool Skin { get; set; }
    public bool CardBack { get; set; }
    public int? CardBackOverride { get; set; }
    // shortcut: native START taunt is not observable reliably; allow explicit qualification override.
    public bool NativeStartingTaunt { get; set; } = true;
    // True only for the ladder defaults below; the playtest knob never sets it and always overrides them.
    public bool Ladder { get; set; }

    // NPC World v1 (seam S8): what a ladder encounter shows when no playtest knob is present.
    // Emotes, hover, skin and card back are qualified natively (golden-bot playtests), so they are on. Cosmetics
    // that the client does not know fall back instead of breaking the match: an unknown skin (or a skin whose
    // power the client lacks) shows the base hero; an unknown or default card back leaves the default back.
    // skinKnown / cardBackKnown are the client's own lookups; null accepts any non-empty id / positive back.
    public static NpcPresentationSettings LadderDefaults(NpcProfile profile,
        Func<string, bool> skinKnown = null, Func<int, bool> cardBackKnown = null, Func<string, bool> skinPowerKnown = null)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        return new NpcPresentationSettings {
            Ladder = true, Emotes = true, Hover = true,
            Skin = SkinUsable(profile, skinKnown, skinPowerKnown), CardBack = CardBackUsable(profile, cardBackKnown) };
    }
    public static bool SkinUsable(NpcProfile profile, Func<string, bool> skinKnown, Func<string, bool> skinPowerKnown = null)
    {
        if (string.IsNullOrEmpty(profile.HeroSkin)) return false;
        if (skinKnown == null) return true;
        return Safe(skinKnown, profile.HeroSkin)
            && (string.IsNullOrEmpty(profile.HeroSkinPower) || Safe(skinPowerKnown ?? skinKnown, profile.HeroSkinPower));
    }
    public static bool CardBackUsable(NpcProfile profile, Func<int, bool> cardBackKnown)
    {
        if (profile.CardBack <= 0) return false;
        if (cardBackKnown == null) return true;
        try { return cardBackKnown(profile.CardBack); } catch (Exception) { return false; }
    }
    private static bool Safe(Func<string, bool> known, string id)
    {
        try { return known(id); } catch (Exception) { return false; }
    }

    // Shape checks on the client's own card definition (cosmetics are not in the engine DB): a skin must be a HERO
    // card of the deck hero's class, and its power a HERO_POWER card. Unknown (null) always fails, so the match
    // shows the base hero. Names are the client's TAG_CARDTYPE / TAG_CLASS strings.
    public static bool IsHeroSkin(string cardType, string cardClass, string baseHeroClass) =>
        cardType == "HERO" && !string.IsNullOrEmpty(cardClass) && cardClass == baseHeroClass;
    public static bool IsHeroPower(string cardType) => cardType == "HERO_POWER";
}
