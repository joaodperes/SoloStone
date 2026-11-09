using System;
using System.Text.RegularExpressions;

internal static class NativeReportText
{
    internal const string BugReportsUrl = "";
    internal const string Header = "REPORT AN ISSUE";
    internal const string Guidance = "Attach the zip to your post in #bug-reports. Describe what you did, expected and saw, and when (turn, cards). Your save helps with progress bugs.";
    internal const string SaveHint = "Your save is only included if you switch it on above.";
    // Plain ASCII: native Hearthstone fonts lack checkbox glyphs.
    // The preview step (I2d): what the zip holds, before anything is saved. Names are already redacted. Short enough for the native popup.
    internal const string PreviewLabel = "PREVIEW REPORT", SaveLabel = "SAVE ZIP", BackLabel = "BACK";
    internal const int PreviewRows = 6;
    internal static string Size(long bytes) => bytes < 1024 ? bytes + " B" : bytes < 1024 * 1024 ? (bytes + 512) / 1024 + " KB"
        : ((bytes * 10 + 524288) / 1048576 / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
    internal static string Preview(System.Collections.Generic.IList<System.Collections.Generic.KeyValuePair<string, long>> files, bool includeSave, long replaced)
    {
        var lines = new System.Collections.Generic.List<string> { "Preview. The zip holds:" };
        for (int i = 0; i < files.Count && i < PreviewRows; i++) lines.Add(files[i].Key + "  " + Size(files[i].Value));
        if (files.Count > PreviewRows) lines.Add("and " + (files.Count - PreviewRows) + " more");
        lines.Add(includeSave ? "Your save is included." : "Your save is not included.");
        lines.Add(replaced + " personal details (Windows user, PC name, folders) were replaced. Nothing is uploaded; you attach the zip yourself.");
        lines.Add("SAVE ZIP writes it. BACK discards it.");
        return string.Join("\n", lines);
    }
    internal static string IncludeSaveLabel(bool on) => on ? "INCLUDE MY SAVE: ON" : "INCLUDE MY SAVE: OFF";
    internal static bool DiscordEnabled => !string.IsNullOrEmpty(BugReportsUrl);
    internal static string DiscordLabel => DiscordEnabled ? "OPEN #BUG-REPORTS" : "#BUG-REPORTS (COMING SOON)";
    internal static string ReportId(DateTime utc, string matchId) => "SR-" + utc.ToUniversalTime().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-"
        + (string.IsNullOrEmpty(matchId) ? "nomatch" : Regex.Replace(matchId, "[^A-Za-z0-9]", "").PadRight(6, '0').Substring(0, 6))
        + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
    internal static bool IsControl(string id) => id == "menu.report-issue"
        || id == "report.include-save" || id == "report.export" || id == "report.save" || id == "report.back"
        || id == "report.discord" || id == "report.copy-path" || id == "report.close";
    // Wine hides the Unix HOME: WINEHOMEDIR ("\??\Z:\home\deck") first, then HOME. Deployment keeps the release label beside compat/.
    internal static string DeckLabelPath(string wineHomeDir, string home)
    {
        string root = ReportRedaction.StripNtPrefix(wineHomeDir);
        if (string.IsNullOrEmpty(root)) root = home;
        if (string.IsNullOrEmpty(root)) return null;
        if (root.StartsWith("/", StringComparison.Ordinal)) root = "Z:" + root.Replace('/', '\\');
        return root.TrimEnd('\\', '/') + @"\Games\SoloStone\state\current";
    }
    // Test-control presses run their native release inside this scope: the export started there never opens Explorer.
    // Scoped to the release, never a sticky flag affecting a player's later click.
    private static bool testPress;
    internal static void TestRelease(Action release)
    {
        testPress = true; try { release(); } finally { testPress = false; }
    }
    internal static bool DryRun(int lane) => testPress || lane != 0;
    internal static string WrapPath(string path)
    {
        path = path ?? "";
        var lines = new System.Collections.Generic.List<string>();
        for (int start = 0; start < path.Length; start += 56) lines.Add(path.Substring(start, Math.Min(56, path.Length - start)));
        return string.Join("\n", lines);
    }
}
