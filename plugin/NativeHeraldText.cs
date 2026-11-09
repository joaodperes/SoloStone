// Held Herald card text: which Soldier the client names (HeraldCardTextBuilder). Pure, no game types, so engine-proof and the
// bindings check can read it. The pinned builder reads tag 4534 on the CONTROLLER only; ClientBridge.HeraldColossalName prefixes it
// to read the CARD's own tag 4534 first (the engine publishes each Herald card's own Soldier class there).
internal static class NativeHeraldText
{
    // Same string the pinned builder returns for an unknown class (Assembly-CSharp 786376bd..., Mono.Cecil 2026-11-05).
    internal const string DefaultKey = "GAMEPLAY_HERALD_DEFAULT";

    // TAG_CLASS values the pinned GetHeraldColossalName(Entity) switches on: 1 Death Knight, then 7 Rogue, 8 Shaman, 9 Warlock,
    // 10 Warrior, 14 Demon Hunter (jump table read from IL). Null = no card-level answer: the original runs.
    internal static string Key(int tagClass)
    {
        switch (tagClass)
        {
            case 1: return "GAMEPLAY_HERALD_DEATHKNIGHT";
            case 7: return "GAMEPLAY_HERALD_ROGUE";
            case 8: return "GAMEPLAY_HERALD_SHAMAN";
            case 9: return "GAMEPLAY_HERALD_WARLOCK";
            case 10: return "GAMEPLAY_HERALD_WARRIOR";
            case 14: return "GAMEPLAY_HERALD_DEMONHUNTER";
            default: return null;
        }
    }

    // Keyword tooltip ("Herald" hover) text keys of TooltipPanelManager.SetupKeywordPanelIfNecessary: same class switch (1, 7-10, 14),
    // default GLOBAL_KEYWORD_HERALD_TEXT for any other value.
    internal const string DefaultTooltipKey = "GLOBAL_KEYWORD_HERALD_TEXT";
    internal static string TooltipKey(int tagClass)
    {
        switch (tagClass)
        {
            case 1: return "GLOBAL_KEYWORD_HERALD_DEATH_KNIGHT_TEXT";
            case 7: return "GLOBAL_KEYWORD_HERALD_ROGUE_TEXT";
            case 8: return "GLOBAL_KEYWORD_HERALD_SHAMAN_TEXT";
            case 9: return "GLOBAL_KEYWORD_HERALD_WARLOCK_TEXT";
            case 10: return "GLOBAL_KEYWORD_HERALD_WARRIOR_TEXT";
            case 14: return "GLOBAL_KEYWORD_HERALD_DEMON_HUNTER_TEXT";
            default: return DefaultTooltipKey;
        }
    }
    // The class the tooltip switches on: the card's own tag 4534 when it names a Soldier, else the controller's (original behaviour).
    internal static int TooltipClass(int cardTag, int controllerTag) => Key(cardTag) != null ? cardTag : controllerTag;

    // What the player reads for a held Herald card: its own Soldier if the card tag names one, else the controller's.
    internal static string Shown(int cardTag, int controllerTag) => Key(cardTag) ?? Key(controllerTag) ?? DefaultKey;
}
