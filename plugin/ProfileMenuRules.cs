// Change Profile's decisions with the Unity state taken as arguments, so test-control runs them on the Mac.
internal enum ProfileEscape { Pass, Swallow, Close, LeaveWizard, LeavePace }
internal struct ManageRowPlan { internal string Id, Label; internal bool Locked, Marked; }

internal static class ProfileMenuRules
{
    // The Game Menu's own in-match test (GameMenu.IsInGameMenu) goes false on the game-over screen and during loads, so the
    // Change Profile row is reachable inside GAMEPLAY: the whole scene counts, plus a requested, starting or pending match.
    internal static bool InMatch(bool sceneKnown, bool gameplay, bool gameplayRequested, string ladderScreen, bool pendingMatch) =>
        !sceneKnown || gameplay || gameplayRequested || ladderScreen == "Playing" || ladderScreen == "Starting" || pendingMatch;

    internal static bool ManageAllowed(bool atStartup, bool bound, bool inMatch) => atStartup || bound && !inMatch;

    // Tapping a row asks to delete it (id save.delete:<id>, never save.slot:). The root (legacy) save lives outside the profiles
    // folder: disabled, unmarked. So is the save open right now, under its own id save.current:<id>.
    internal static ManageRowPlan ManageRow(string id, string label, bool root, bool open) => new ManageRowPlan {
        Id = open ? NativeSaveDeleteText.CurrentRowId(id) : NativeSaveDeleteText.RowId(id),
        Label = open ? NativeSaveDeleteText.CurrentLabel(label) : root ? label + " (cannot delete)" : NativeSaveDeleteText.MarkedLabel(label),
        Locked = root || open, Marked = !root && !open
    };

    // Native Escape knows only native menus: past them it opens the Game Menu over this one, and Options from there stacks on it.
    // Startup keeps that (its Game Menu is the way to Quit). A dialog and the name field take the key themselves before this.
    internal static ProfileEscape Escape(bool shown, bool startup, bool dialog, bool editing, bool loading, bool wizard, bool pacePage) =>
        !shown || startup ? ProfileEscape.Pass
        : dialog || editing || loading ? ProfileEscape.Swallow
        : wizard ? ProfileEscape.LeaveWizard
        : pacePage ? ProfileEscape.LeavePace
        : ProfileEscape.Close;
}
