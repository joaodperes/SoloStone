using System;
using System.IO;
using System.Runtime.InteropServices;
#if SOLOSTONE_INSTALLER
using SoloStone;
#endif

// Release plan R2. Opening a save migrates, backs up, grants and recovers, so nothing touches the save tree until V1 and V2 pass. Off by default so
// tests and tools that use saves directly never meet it. The plugin engages it first thing in Awake. Each writer below asks here, so a path added
// later fails loudly instead of writing before the checks.
internal static class SaveHold
{
    private static volatile bool held;
    internal static bool Held => held;
    internal static void Engage() { held = true; }
    internal static void Release() { held = false; }
    internal static void Require(string what)
    {
        if (held) throw new InvalidOperationException("Saves are on hold until the client checks pass: " + what);
    }
}

public sealed class OfflineLaunchContext
{
    public int LaneId { get; }
    public string InstalledRoot { get; }
#if SOLOSTONE_RELEASE
    // A public build runs only from an installed copy: there is no lab layout to fall back to.
    public string Root => InstalledRoot;
#else
    public string Root => InstalledRoot ?? OfflineGuard.LaneRoot(LaneId);
#endif
    public string Reports => Root + @"\reports";
#if !SOLOSTONE_RELEASE
    public string ClientPath => Root + @"\client\Hearthstone.exe";
    public int ControlPort => 18765 + LaneId;
#endif
#if !SOLOSTONE_INSTALLER
    public string OptionsUid => "HSNativeUIPrototype" + (LaneId == 0 ? "" : "Lane" + LaneId);
#endif
    public string RunId { get; }
    public string SnapshotId { get; }
    public string RootProfilePath { get; }
    public string ProfilePath { get; private set; }
    public bool ShowProfileSelector { get; private set; }
    public string RelaunchTargetId { get; private set; }
    public string RelaunchPreviousRunId { get; }
    public string RelaunchToken { get; }
    public bool Deck { get; }
    // How the startup save was chosen (null: not a picker-capable launch, or an explicit Change Profile relaunch).
    internal StartupChoice Startup { get; private set; }
    internal string AutoOpenTargetId => Startup != null && Startup.IsAuto ? Startup.TargetId : null;

    internal OfflineLaunchContext(string runId, string snapshotId, string profilePath, int laneId = 0, bool deck = false,
        string rootProfilePath = null, bool showProfileSelector = false, string relaunchTargetId = null,
        string relaunchPreviousRunId = null, string relaunchToken = null, string installedRoot = null, StartupChoice startup = null, bool savesPending = false)
    { this.savesPending = savesPending; Startup = startup; RunId = runId; SnapshotId = snapshotId; ProfilePath = profilePath; RootProfilePath = rootProfilePath ?? profilePath;
      LaneId = laneId; Deck = deck; ShowProfileSelector = showProfileSelector; RelaunchTargetId = relaunchTargetId;
      RelaunchPreviousRunId = relaunchPreviousRunId; RelaunchToken = relaunchToken; InstalledRoot = installedRoot; }

    private bool savesPending;
    // The picker-capable decision. It consumes a tool request, finishes a crashed delete, arms the unfinished-start marker and reads the catalog, so it
    // waits for the save hold. Until then this launch looks like an unbound picker launch: root path, no selector, no choice.
    internal void ResolveSaves()
    {
        if (!savesPending) return;
        SaveHold.Require("choosing the startup save");
        // The whole picker-capable decision lives in ProfileCatalog.Startup.ResolveLaunch (platform-free, unit tested).
        var decided = ProfileCatalog.Startup.ResolveLaunch(RootProfilePath, RunId, RelaunchPreviousRunId, RelaunchToken);
        savesPending = false;
        ProfilePath = decided.Profile; ShowProfileSelector = decided.Selector; RelaunchTargetId = decided.RelaunchTarget; Startup = decided.Startup;
    }
}

// Is this process running with an elevated token? Unity's Mono answers WindowsPrincipal.IsInRole(Administrator) from the token's group list, deny-only
// groups included, so a member of Administrators at normal (filtered) integrity reads as an administrator there although the process has no admin
// rights. The real question is elevation, which is what the launcher (a .NET Framework process, where IsInRole honours the filter) refuses too.
internal static class TokenElevation
{
    private const int TokenElevationClass = 20;
    private const uint TokenQuery = 0x0008;
    [DllImport("advapi32", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int information, out int value, int length, out int returned);
    [DllImport("kernel32")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32")] private static extern bool CloseHandle(IntPtr handle);

    // Fails closed: a token that cannot be read is treated as elevated.
    internal static bool Decide(bool queried, int tokenIsElevated) => !queried || tokenIsElevated != 0;

    internal static bool IsElevated()
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token)) return Decide(false, 0);
            int value, returned;
            return Decide(GetTokenInformation(token, TokenElevationClass, out value, 4, out returned) && returned == 4, value);
        }
        catch (Exception) { return true; }
        finally { if (token != IntPtr.Zero) CloseHandle(token); }
    }
}

public static class OfflineGuard
{
#if MODERN_MECHANICS
    public const string StandardSnapshot = "standard-2026-10-17-modern-mechanics";
#else
    public const string StandardSnapshot = "standard-2026-10-17-core-tradeable";
#endif
    public const string DeckHost = "deck";
#if !SOLOSTONE_RELEASE
    private const string LocalData = @"C:\Us"+@"ers\HSNative"+@"UIProbe\AppData\Local";

    public static string LaneRoot(int lane)
    {
        if (lane < 0 || lane > 3) throw new InvalidOperationException("Invalid test lane");
        return @"C:\hs-native-ui-"+@"prototype" + (lane == 0 ? "" : @"\lanes\" + lane);
    }
#endif

    public static int LaunchLane(string[] args)
    {
        string value = Selector(args, "-hs-native-ui-lane", false);
        if (value == null) return 0;
        if (value.Length != 1 || value[0] < '0' || value[0] > '3')
            throw new InvalidOperationException("Invalid test lane");
        return value[0] - '0';
    }

#if !SOLOSTONE_RELEASE
    public static bool IsIsolated(string identity, string machine, string executable, string localData, int lane = 0)
    {
        return string.Equals(identity, machine + @"\HSNativeUIProbe", StringComparison.OrdinalIgnoreCase)
            && string.Equals(executable, LaneRoot(lane) + @"\client\Hearthstone.exe", StringComparison.OrdinalIgnoreCase)
            && string.Equals(localData, LocalData, StringComparison.OrdinalIgnoreCase);
    }
#endif

    // What the isolation guard found. One predicate decides and explains, so the refusal and its log line cannot drift apart.
    public sealed class GuardVerdict
    {
        public bool DeckInPublicBuild, NotInstalled, LabCheckFailed, Elevated, InAdminGroup;
        public bool Rejected { get { return DeckInPublicBuild || NotInstalled || LabCheckFailed || Elevated || InAdminGroup; } }
    }

    // The guard's whole condition. The three checks are delegates and run lazily, as the old ternary did:
    //  - a Deck launch (Wine tokens always carry Administrators) never runs the elevation or group check;
    //  - labIsolated is the Deck isolation check on a Deck launch, else the lab account/path check, and only a lab build asks it, and only when not installed;
    //  - elevation and the group list are lab and hook-build checks. A public build asks neither: Unity's Mono counts the deny-only Administrators
    //    group of a filtered token, and with UAC turned off every administrator process is elevated (owner 2026-11-08: allow it).
    public static GuardVerdict Judge(bool releaseBuild, bool deck, bool installed, Func<bool> labIsolated, Func<bool> elevated, Func<bool> inAdminGroup)
    {
        var verdict = new GuardVerdict();
        if (releaseBuild)
        {
            if (deck) { verdict.DeckInPublicBuild = true; return verdict; }
            verdict.NotInstalled = !installed;
            // Elevation is allowed in a public build (owner 2026-11-08): with UAC turned off an administrator's processes are always elevated.
            return verdict;
        }
        if (deck) { verdict.LabCheckFailed = !labIsolated(); return verdict; }
        if (!installed) verdict.LabCheckFailed = !labIsolated();
        verdict.Elevated = elevated();
        verdict.InAdminGroup = !verdict.Elevated && inAdminGroup();
        return verdict;
    }

    // The words for a rejected launch, so a log line says which test failed and not only that one did.
    public static string RejectionReasons(string[] args, bool releaseBuild, bool installerBuild, bool deck, GuardVerdict verdict)
    {
        var reasons = new System.Collections.Generic.List<string>();
        bool selector = false;
        foreach (string arg in args ?? new string[0])
            if (arg != null && arg.StartsWith("-hs-installer-root", StringComparison.OrdinalIgnoreCase)) selector = true;
        if (verdict.DeckInPublicBuild) reasons.Add("Deck selector -hs-native-ui-host is not allowed in a public build");
        if (verdict.NotInstalled)
            reasons.Add(!installerBuild ? "this plugin build has no installer mode"
                : !selector ? "installer selector -hs-installer-root is absent from the process arguments"
                : "installer admission returned null although the selector is present");
        if (verdict.LabCheckFailed)
            reasons.Add(deck ? "Deck launch failed the Deck isolation check (path or lane)"
                : "not an installer launch (" + (!installerBuild ? "no installer mode in this build" : selector ? "selector present but admission returned null" : "no -hs-installer-root") + ") and the lab account/path check failed");
        if (verdict.Elevated) reasons.Add("the process is elevated (administrator token)");
        if (verdict.InAdminGroup) reasons.Add("the account is in the Administrators group (lab builds refuse that)");
        if (reasons.Count == 0) reasons.Add("no failed condition identified");
        return string.Join("; ", reasons.ToArray());
    }

    private static readonly string[] SecretFlags = { "-hs-native-ui-run", "-hs-profile-relaunch-token", "-hs-profile-previous-run" };

    private static bool IsSecretFlag(string arg)
    {
        foreach (string flag in SecretFlags) if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // The arguments as the process saw them, with the run and relaunch tokens hidden. Flag names match in any case, and "-flag=value" is hidden too.
    public static string RedactedArguments(string[] args)
    {
        if (args == null) return "(none)";
        var parts = new System.Collections.Generic.List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            bool hide = i > 0 && args[i - 1] != null && IsSecretFlag(args[i - 1]);
            int equals = arg == null ? -1 : arg.IndexOf('=');
            if (equals > 0 && IsSecretFlag(arg.Substring(0, equals))) arg = arg.Substring(0, equals + 1) + "<redacted>";
            parts.Add("[" + (hide ? "<redacted>" : arg) + "]");
        }
        return string.Join(" ", parts.ToArray());
    }

    // Steam Deck (Proton) launches opt in with "-hs-native-ui-host deck". Wine tokens always carry the
    // Administrators group and the prefix user is steamuser, so the Windows standard-account checks
    // cannot hold there; the Deck launcher removes the network instead and the network canary still
    // runs. Off Wine the selector is refused, so Windows launches keep every check.
    public static bool DeckLaunch(string[] args, bool wine)
    {
        string value = Selector(args, "-hs-native-ui-host", false);
        if (value == null) return false;
        if (value != DeckHost || !wine) throw new InvalidOperationException("Deck host selector rejected");
        return true;
    }

#if !SOLOSTONE_RELEASE
    public static bool IsDeckIsolated(string executable, string localData, int lane)
    {
        if (lane != 0 || localData == null) return false;
        string[] parts = localData.Split('\\');
        return string.Equals(executable, LaneRoot(0) + @"\client\Hearthstone.exe", StringComparison.OrdinalIgnoreCase)
            && parts.Length == 5 && string.Equals(parts[0], "C:", StringComparison.OrdinalIgnoreCase)
            && string.Equals(parts[1], "users", StringComparison.OrdinalIgnoreCase) && parts[2].Length > 0
            && parts[2] != "." && parts[2] != ".." && string.Equals(parts[3], "AppData", StringComparison.OrdinalIgnoreCase)
            && string.Equals(parts[4], "Local", StringComparison.OrdinalIgnoreCase);
    }
#endif

    public static OfflineLaunchContext ResolveLaunch(string identity, string machine, string executable,
        string localData, string[] args, bool candidate, bool wine = false)
    {
        OfflineLaunchContext launch = ResolveHeldLaunch(identity, machine, executable, localData, args, candidate, wine);
        launch.ResolveSaves();
        return launch;
    }

    // Identity, paths and selectors. It reads attributes only and writes nothing: the save decision is ResolveSaves, after the hold.
    public static OfflineLaunchContext ResolveHeldLaunch(string identity, string machine, string executable,
        string localData, string[] args, bool candidate, bool wine = false)
    {
        int lane = LaunchLane(args);
        bool deck = DeckLaunch(args, wine);
        string installedRoot = null;
#if SOLOSTONE_INSTALLER
        installedRoot = InstallerRuntime.Resolve(executable, localData, args, candidate, wine);
#endif
#if SOLOSTONE_RELEASE
        if (installedRoot == null || deck) throw new InvalidOperationException("Isolation guard rejected process");
#else
        if (installedRoot == null && (deck ? !IsDeckIsolated(executable, localData, lane) : !IsIsolated(identity, machine, executable, localData, lane)))
            throw new InvalidOperationException("Isolation guard rejected process");
#endif
        if (deck && candidate) throw new InvalidOperationException("Candidate is Windows-only");
        string run = Selector(args, "-hs-native-ui-run", true);
        if (!Guid.TryParseExact(run, "D", out _)) throw new InvalidOperationException("Invalid prototype launch ID");
        if (candidate && lane != 0) throw new InvalidOperationException("Candidate requires lane zero");
        string snapshot = Selector(args, "-hs-standard-candidate", candidate);
        if (candidate ? snapshot != StandardSnapshot : snapshot != null)
            throw new InvalidOperationException("Candidate selector rejected");
#if SOLOSTONE_RELEASE
        string saves = PublicNames.SaveRoot(localData) + @"\";
#else
        // Lab saves keep the lab's name; only an installed copy uses the public one (I2b).
        string saves = (deck || installedRoot != null ? localData : LocalData) + @"\HearthstoneOffline\";
#if SOLOSTONE_INSTALLER
        if (installedRoot != null) saves = PublicNames.SaveRoot(localData) + @"\";
#endif
#endif
        string profile = saves
            + (candidate ? @"tests\" + StandardSnapshot + @"\" : lane == 0 ? "" : @"lanes\" + lane + @"\") + "profile.xml";
        // Windows path semantics are checked on Windows; selector tests also run on macOS.
        if (Path.DirectorySeparatorChar == '\\')
        {
            string canonical = Path.GetFullPath(profile);
            if (!string.Equals(canonical, profile, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Profile canonicalization rejected");
            ValidateProfileFiles(canonical);
            profile = canonical;
        }
        string rootProfile = profile;
        bool selector = !candidate && lane == 0 && Path.DirectorySeparatorChar == '\\';
        string relaunchPrevious = null, relaunchToken = null;
        if (selector)
        {
            relaunchPrevious = Selector(args, "-hs-profile-previous-run", false);
            relaunchToken = Selector(args, "-hs-profile-relaunch-token", false);
        }
        else if (Selector(args, "-hs-profile-previous-run", false) != null
            || Selector(args, "-hs-profile-relaunch-token", false) != null)
            throw new InvalidOperationException("Save relaunch is forbidden for this launch mode.");
        return new OfflineLaunchContext(run, snapshot, profile, lane, deck, rootProfile, false,
            null, relaunchPrevious, relaunchToken, installedRoot, null, selector);
    }

    private static string Selector(string[] args, string name, bool required)
    {
        string value = null;
        if (args == null) throw new InvalidOperationException("Missing launch arguments");
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] != null && args[i].StartsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                if (args[i] != name || value != null || i + 1 >= args.Length || string.IsNullOrEmpty(args[i + 1]))
                    throw new InvalidOperationException("Malformed or duplicate launch selector " + name);
                value = args[++i];
            }
        }
        if (required && value == null) throw new InvalidOperationException("Missing launch selector " + name);
        return value;
    }

    internal static void ValidateProfileFiles(string profile)
    {
        RejectReparsePoints(profile);
        RejectReparsePoints(profile + ".lock");
        RejectReparsePoints(profile + ".tmp");
        RejectReparsePoints(profile + ".restore.tmp");
        RejectReparsePoints(profile + ".restore-tmp");
        string directory = Path.GetDirectoryName(Path.GetFullPath(profile));
        RejectReparsePoints(Path.Combine(directory, "backups"));
#if !SOLOSTONE_INSTALLER || SOLOSTONE_TEST_HOOKS
        RejectReparsePoints(Path.Combine(directory, "restore-request.txt"));
        RejectReparsePoints(Path.Combine(directory, "restore-result.txt"));
        RejectReparsePoints(Path.Combine(directory, "restore-result.txt.tmp"));
#endif
        for (int version = 1; version <= 8; version++)
        {
            RejectReparsePoints(profile + ".v" + version + ".bak");
            RejectReparsePoints(profile + ".v" + version + ".bak.tmp");
        }
    }

    internal static void RejectReparsePoints(string path)
    {
        string full = Path.GetFullPath(path), current = Path.GetPathRoot(full);
        CheckComponent(current);
        foreach (string component in full.Substring(current.Length).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (component.Length == 0) continue;
            current = Path.Combine(current, component);
            CheckComponent(current);
        }
    }

    private static void CheckComponent(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Reparse profile component rejected: " + path);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    public static void SeedProfileIfMissing(OfflineLaunchContext launch, string fixture)
    {
        if (launch == null || launch.SnapshotId != null)
            throw new InvalidOperationException("Normal fixture cannot seed a snapshot profile");
        // An auto-opened catalog save is never seeded: a vanished file must fail the open, not become a fresh save.
        if (!launch.ShowProfileSelector && launch.InstalledRoot == null && launch.AutoOpenTargetId == null) SeedProfileAt(launch.ProfilePath, fixture);
    }

#if !SOLOSTONE_RELEASE
    public static void SeedProfileIfMissing(string localData, string fixture) =>
        SeedProfileAt(Path.Combine(localData, "HearthstoneOffline", "profile.xml"), fixture);
#endif

    private static void SeedProfileAt(string profile, string fixture)
    {
        SaveHold.Require("seeding a save");
        ValidateProfileFiles(profile);
        if (File.Exists(profile)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(profile));
        File.Copy(fixture, profile, false);
    }
}
