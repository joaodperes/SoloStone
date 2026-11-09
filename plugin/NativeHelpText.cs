// SoloStone Help popup: copy and test-control ids. Pure text (no Unity), so test-control checks it on the Mac.
// Lists only built features; themes/boards and adventures are not built yet and stay out until they ship.
internal static class NativeHelpText
{
    // Community invite. Invite codes can change: edit only this line (the popup text and Join Discord both read it).
    internal const string DiscordInvite = "discord.gg/e45QgKHmC2";
    internal const string DiscordUrl = "https://" + DiscordInvite;

    internal const string PopupId = "LOCAL_SOLOSTONE_HELP";
    internal const string HomeId = "home.help", DiscordId = "help.discord", CloseId = "help.close";
    internal const string ButtonLabel = "Help", Header = "ABOUT SOLOSTONE", DiscordLabel = "JOIN DISCORD", CloseLabel = "OK";

    // AlertPopup grows with its text: keep it short enough to fit at 1280x720 without scrolling (test-control checks a budget).
    internal const string Body =
        "Hearthstone offline: the Violet Hold meta against NPC opponents.\n\n" +
        "<b>Ladder</b> Bronze to Legend. NPCs have personalities, emotes and skill; the Elite think hard.\n" +
        "<b>Rewards</b> wins, ladder milestones and class levels; golden cards included.\n" +
        "<b>Achievements</b> 60 permanent, one-time goals. Eligible world matches count; your concessions and playtests do not.\n" +
        "<b>Gold</b> for packs, full decks, deck recipes, card backs and hero skins.\n" +
        "<b>Dust</b> crafts the cards you miss.\n" +
        "<b>No FOMO.</b> No timers, streaks or expiring offers. Ever.\n\n" +
        "A free fan project: unofficial, not affiliated with Blizzard. You must own Hearthstone.\n" +
        "Discord: " + DiscordInvite + "\n\n" +
        "Have fun. Live life.";

    // Only lane 0 (the owner's own game) opens a browser. Test lanes 1-3 share the owner's desktop, where a browser would
    // pop over the full-screen game: they log the URL instead (dry run). An unknown lane is treated as a test lane.
    internal static bool OpensBrowser(int lane) => lane == 0;

    // Confirm is JOIN DISCORD, cancel is OK (also Escape). Other popups keep their own ids (null here).
    internal static string DialogButtonId(string popupId, string button) =>
        popupId != PopupId ? null : button == "confirm" ? DiscordId : button == "cancel" ? CloseId : null;
}
