// Options > Pace (owner 2026-11-04): test-control id, copy and the availability rule. Pure, so test-control checks it on the Mac.
internal static class NativeOptionsPaceText
{
    internal const string Id = "options.pace", ObjectName = "SoloStonePaceOption", LabelPrefix = "Pace: ";
    internal static string Label(string tierName) => LabelPrefix + tierName;
    // No save (the startup picker) has no pace to change. Pace is stamped per match: mid-match the plank stays away.
    // Feats owns the screen: its Game Menu lists no Change Profile either (NativeProfileSelector.ListMenuRow).
    internal static bool Wanted(bool bound, bool inMatch, bool feats) => bound && !inMatch && !feats;
}
