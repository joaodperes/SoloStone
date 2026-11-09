using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Serialization;

public sealed class ProfileSlot {
    public string Id; public string Name; public string CreatedUtc;
    // Listing-only health: a damaged slot is shown but cannot be opened; never persisted.
    [XmlIgnore] public string Unavailable;
}
public sealed class ProfileCatalogData { public int Version = 1; public string SelectedId; public List<ProfileSlot> Slots = new List<ProfileSlot>(); }
public sealed class ProfileOptions {
    // Whole-object copy so a future option field is never dropped (all fields are values or strings).
    internal ProfileOptions Clone() => (ProfileOptions)MemberwiseClone();
    public int Version = 1; public string MetaId = ProfileCatalog.MetaId;
    public bool AllCards; public bool AllCosmetics; public bool InfiniteGold; public bool InfiniteDust; public bool StartLegendOne;
    public double LegendRating;
    // Grind tier chosen on the Create save form (optional element, same Version 1 schema). profile-options.xml keeps only
    // the creation choice; profile.xml owns the live pace (LocalProfile.SetGrindTier).
    public string GrindTier;
    public bool ShouldSerializeGrindTier() => GrindTier != null;
    // Sandbox "All decks" (owner 2026-10-28). Optional element, written only when set: options files from before it load
    // unchanged (false), and saves without it stay readable by builds that predate it. Same Version 1 schema.
    public bool AllDecks;
    public bool ShouldSerializeAllDecks() => AllDecks;
    // A Sandbox save: any creation-time cheat option. Not serialized (a method, not a field).
    internal bool IsSandbox() => AllCards || AllCosmetics || InfiniteGold || InfiniteDust || StartLegendOne || AllDecks;
}
public sealed class ProfileSwitchRequest { public int Version; public string RunId; public string Token; public string TargetId; }
// Read-only picker facts for the delete confirmation; Readable=false means the save's files could not be fully read.
// PendingUnknown: an I/O or access error (a tool or scanner holding the file) hid whether a match is pending; deleting then waits for a retry.
public sealed class ProfileSummary
{
    public string Name, CreatedUtc, Rank; public bool Sandbox, HasPendingMatch, PendingUnknown, Readable; public int Gold, DeckCount;
}
// Outcome strings: deleted, move-deferred (slot gone, folder moved by the next reconcile), refused-pending/legacy/unknown/in-use/unreadable/current, failed:<reason>.
public sealed class ProfileDeleteResult
{
    public string Id, Name, Moved = "none", Outcome;
    public bool SlotRemoved => Outcome == "deleted" || Outcome == "move-deferred";
}

// What the client does at launch. Auto: open TargetId straight away through the ordinary picked-save path. Picker: show the
// startup picker (Reason says why; Notice, when set, is shown to the player on top of it). Mutable only for Failure.
internal sealed class StartupChoice
{
    internal const string Auto = "auto", Picker = "picker";
    internal string Mode, Reason, TargetId, TargetPath, TargetName, Request, Notice, Failure;
    // True once ResolveLaunch ran the picker's Reconcile (before the choice, while nothing is bound); LogLines are its log output.
    internal bool Reconciled;
    internal readonly System.Collections.Generic.List<string> LogLines = new System.Collections.Generic.List<string>();
    internal bool IsAuto => Mode == Auto;
    // Dialog header for the notice: a failure says so, an unfinished last start just asks the player to choose.
    internal string NoticeHeader => Reason == ProfileCatalog.StartupReasons.UncleanExit ? "CHOOSE A SAVE" : "SAVE UNAVAILABLE";
    // The real open threw after the checks passed: the picker takes over and the failure is kept for the log and test control.
    internal string FailOpen(Exception error)
    {
        Mode = Picker; Reason = ProfileCatalog.StartupReasons.OpenFailed; TargetPath = null;
        Failure = ProfileCatalog.Reason(error);
        return Notice = ProfileCatalog.StartupNotice.OpenFailed(TargetName);
    }
    internal string LogLine() => "STARTUP_SAVE mode=" + Mode + " reason=" + Reason
        + " id=" + ProfileCatalog.LogId(TargetId) + " name=" + (TargetName ?? "none") + " request=" + (Request ?? "none")
        + (Failure == null ? "" : " failure=" + Failure);
}

// Arrival at Home is not "the save works": a pending reward, the welcome scroll or a toast can still crash it. The unfinished-start
// marker clears only after Home has been idle (no presentation, dialog, selector or transition) for HoldSeconds in a row.
internal sealed class StartupHomeSettle
{
    internal const double HoldSeconds = 10;
    private double? since;
    // leftHome: the player used Home (went to Play, the collection, packs...) after it appeared. Navigation is only possible once the
    // presentations that own Home input are gone, so it proves Home worked as well as the idle hold does.
    internal bool Tick(double now, bool homeIdle, bool leftHome = false)
    {
        if (leftHome) return true;
        if (!homeIdle) { since = null; return false; }
        if (since == null) since = now;
        return now - since.Value >= HoldSeconds;
    }
}

// What ResolveLaunch decided for a picker-capable launch. Selector false: the client binds Profile itself (relaunch target or auto-open).
internal sealed class StartupLaunch
{
    internal string Profile, RelaunchTarget;
    internal bool Selector;
    internal StartupChoice Startup;
}

public static class ProfileCatalog
{
    internal static Action<string> FaultHook;
    public const int MaximumSlots = 20, UnlimitedGold = 999999;
    public const long UnlimitedDust = 999999;
    public const string MetaId = "violet-hold", MetaLabel = "Violet Hold — September 2026";
    public const string BacklogTooltip = "Not done yet! In the backlog";
    internal const string LegacyId = "legacy";
    private const string DeletedFolder = ".deleted";
    internal const int KeptDeleted = 5;
    private const string DeletedStamp = "yyyyMMddTHHmmssfff'Z'";
    private const int DeletedStampLength = 19;
    private const string OrphanSuffix = "-orphan";
    private static readonly XmlSerializer CatalogXml = new XmlSerializer(typeof(ProfileCatalogData));
    private static readonly XmlSerializer OptionsXml = new XmlSerializer(typeof(ProfileOptions));
    private static readonly XmlSerializer SwitchXml = new XmlSerializer(typeof(ProfileSwitchRequest));

    public static string CatalogPath(string legacy) => Path.Combine(Root(legacy), "profiles.xml");
    public static string SwitchPath(string legacy) => Path.Combine(Root(legacy), "profile-switch.xml");
    public static IReadOnlyList<ProfileSlot> List(string legacy)
    {
        legacy = CanonicalLegacy(legacy);
        return Load(legacy).Slots.Select(row => { var copy = Copy(row); copy.Unavailable = SlotProblem(legacy, row); return copy; }).ToArray();
    }
    public static string SelectedId(string legacy) => Load(legacy).SelectedId;

    public static string ProfilePathForId(string legacy, string id)
    {
        legacy = CanonicalLegacy(legacy);
        var slot = Load(legacy).Slots.SingleOrDefault(row => row.Id == id)
            ?? throw new InvalidOperationException("Unknown save selection.");
        string path = SlotProfile(legacy, slot.Id);
        RequireSlot(path, slot.Name, slot.Id != LegacyId);
        return path;
    }

    public static void CommitSelection(string legacy, string id)
    {
        legacy = CanonicalLegacy(legacy);
        using (Lease(legacy))
        {
            var catalog = LoadUnlocked(legacy);
            var slot = catalog.Slots.SingleOrDefault(row => row.Id == id)
                ?? throw new InvalidOperationException("Unknown save selection.");
            RequireSlot(SlotProfile(legacy, slot.Id), slot.Name, slot.Id != LegacyId);
            catalog.SelectedId = slot.Id;
            Commit(legacy, catalog);
        }
    }

    public static string ResolveActiveProfile(string legacy)
    {
        legacy = CanonicalLegacy(legacy); var catalog = Load(legacy);
        if (catalog.Slots.Count == 0) return legacy;
        // The selector launch always opens the picker, which shows a damaged selected slot as unavailable.
        // A deleted selected save clears the selection (slots remain): the picker launch then starts unbound on the root path.
        if (catalog.SelectedId == null) return legacy;
        var slot = catalog.Slots.Single(row => row.Id == catalog.SelectedId);
        return SlotProfile(legacy, slot.Id);
    }

    public static ProfileOptions OptionsForProfile(string legacy, string profile)
    {
        legacy = CanonicalLegacy(legacy); profile = Path.GetFullPath(profile);
        if (Same(profile, legacy)) return new ProfileOptions();
        var slot = Load(legacy).Slots.SingleOrDefault(row => Same(SlotProfile(legacy, row.Id), profile))
            ?? throw new InvalidOperationException("Profile is not present in the save catalog.");
        return ReadOptions(SlotOptions(legacy, slot.Id));
    }

    internal static ProfileOptions OptionsBeside(string profile)
    {
        string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(profile)), "profile-options.xml");
        return File.Exists(path) ? ReadOptions(path) : new ProfileOptions();
    }

    // recordIfFirst: the first save of an empty catalog is recorded as the selected one. The startup picker and Change Profile pass false and
    // record the save themselves once it has opened, so a save that never opened is never the one the next launch tries.
    public static ProfileSlot Create(string legacy, string name, ProfileOptions options, bool recordIfFirst = true)
    {
        legacy = CanonicalLegacy(legacy); name = ValidateName(name);
        // Copy: a failed in-game create must not leave a frozen rating in the wizard's live options.
        options = options?.Clone();
        if (options != null && options.StartLegendOne && options.LegendRating == 0) options.LegendRating = LocalProfile.LegendOneCreationRating();
        ValidateOptions(options); Directory.CreateDirectory(Root(legacy));
        using (Lease(legacy))
        {
            var catalog = LoadUnlocked(legacy);
            if (catalog.Slots.Count >= MaximumSlots) throw new InvalidOperationException("Maximum 20 saves reached.");
            if (catalog.Slots.Any(row => string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("A save with that name already exists.");
            string id = Guid.NewGuid().ToString("N"), profiles = ProfilesRoot(legacy);
            string staging = Path.Combine(profiles, ".creating-" + id), final = SlotRoot(legacy, id);
            OfflineGuard.RejectReparsePoints(profiles); Directory.CreateDirectory(profiles);
            if (Directory.Exists(staging) || Directory.Exists(final)) throw new IOException("Save staging path already exists.");
            Directory.CreateDirectory(staging); WriteNew(OptionsXml, Path.Combine(staging, "profile-options.xml"), options);
            string path = Path.Combine(staging, "profile.xml");
            using (var profile = new LocalProfile(path, options)) profile.PersistNewProfile(options);
            using (var profile = new LocalProfile(path, options)) { }
            Directory.Move(staging, final);
            var slot = new ProfileSlot { Id = id, Name = name, CreatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) };
            catalog.Slots.Add(slot); if (recordIfFirst && catalog.SelectedId == null) catalog.SelectedId = id; Commit(legacy, catalog); return Copy(slot);
        }
    }

    // ---- Delete a save (startup picker, or in-game Manage for any save but the open one) -----------------------------
    // The slot is removed and committed first, then its folder moves in one rename to profiles/.deleted/<id>-<UTC stamp>.
    // A crash between the two leaves an unlisted folder that Reconcile moves on the next picker open. Only an explicit
    // Delete ever purges old .deleted entries (newest KeptDeleted stay); Reconcile never destroys data. The root
    // (legacy) save lives outside profiles/ and is never touched.
    public static ProfileSummary Describe(string legacy, string id)
    {
        legacy = CanonicalLegacy(legacy);
        var slot = Load(legacy).Slots.SingleOrDefault(row => row.Id == id) ?? throw new InvalidOperationException("Unknown save selection.");
        return Summarize(legacy, slot);
    }

    private static bool IsIo(Exception error)
    {
        for (; error != null; error = error.InnerException) if (error is IOException || error is UnauthorizedAccessException) return true;
        return false;
    }

    // Strictly read-only: no LocalProfile (it locks, migrates and writes backups), only the static file reader. Pending is
    // read from profile.xml, or from profile.xml.tmp when profile.xml is missing; a readable profile.xml beside a stray .tmp
    // is the only truth. Corrupt XML reads as no pending (a damaged save stays deletable); an I/O or access error does not.
    private static ProfileSummary Summarize(string legacy, ProfileSlot slot)
    {
        var summary = new ProfileSummary { Name = slot.Name, CreatedUtc = slot.CreatedUtc, Rank = LocalProfile.RankName(0) };
        if (slot.Id == LegacyId) return summary;
        string path, options;
        try
        {
            path = SlotProfile(legacy, slot.Id); options = SlotOptions(legacy, slot.Id);
            OfflineGuard.ValidateProfileFiles(path); OfflineGuard.RejectReparsePoints(options);
        }
        catch (Exception) { return summary; }
        LocalProfileData data = null;
        try
        {
            FaultHook?.Invoke("read-profile:" + slot.Id);
            if (File.Exists(path)) data = LocalProfile.ReadFile(path);
            else if (File.Exists(path + ".tmp")) summary.HasPendingMatch = LocalProfile.ReadFile(path + ".tmp")?.Pending != null;
        }
        catch (Exception error) { if (IsIo(error)) summary.PendingUnknown = true; }
        if (data != null)
        {
            summary.HasPendingMatch = data.Pending != null;
            summary.Rank = LocalProfile.RankName(Math.Max(0, data.LadderState?.StarIndex ?? 0));
            summary.Gold = data.GoldBalance; summary.DeckCount = data.CustomDecks?.Count ?? 0;
        }
        ProfileOptions read = null;
        try { read = File.Exists(options) ? ReadOptions(options) : null; }
        catch (Exception) { }
        if (read != null) { summary.Sandbox = read.IsSandbox(); if (read.InfiniteGold) summary.Gold = UnlimitedGold; }
        summary.Readable = data != null && read != null && !summary.PendingUnknown;
        return summary;
    }

    public static string DeleteLogLine(string id, string name, string moved, string outcome) =>
        "PROFILE_DELETE id=" + LogId(id) + " name=" + (name ?? "none") + " moved=" + (moved ?? "none") + " outcome=" + outcome;
    internal static string LogId(string id) => id != null && ValidId(id) ? id : "invalid";

    // Picker state only; the per-save rule is IsOpenSave. Unbound: only the startup picker may delete (anything else is a
    // state nothing should reach, refused closed). Bound: never while a match is running or pending.
    public static string DeleteStateBlock(bool startupPending, bool bound, bool inMatch) =>
        bound ? (inMatch ? "refused-match" : null) : startupPending ? null : "refused-bound";

    // The save this process has open (openProfile is the bound LocalProfile's own path; null when unbound) is never deletable.
    // A bound process committed SelectedId to that same save (ClientBridge.BindStartupPath undoes the bind if the commit
    // fails), so the two only disagree if the catalog was changed from outside: then both count as open, so a delete can never
    // touch the save in use nor clear the selection under it.
    public static bool IsOpenSave(string legacy, string id, string selectedId, string openProfile)
    {
        if (openProfile == null) return false;
        if (id == selectedId) return true;
        return id != null && ValidId(id) && Same(SlotProfile(CanonicalLegacy(legacy), id), openProfile);
    }

    // What the picker asks before offering a confirm.
    public static string DeleteBlock(string legacy, string id, bool startupPending, string openProfile, bool inMatch) =>
        DeleteStateBlock(startupPending, openProfile != null, inMatch)
        ?? (IsOpenSave(legacy, id, SelectedId(legacy), openProfile) ? "refused-current" : null);

    // The picker's delete: its state first, then Delete with the save open now. openProfile is the bound LocalProfile's path, null when unbound.
    public static ProfileDeleteResult DeleteFromPicker(string legacy, string id, Action<string> log, bool startupPending, string openProfile, bool inMatch)
    {
        string blocked = DeleteStateBlock(startupPending, openProfile != null, inMatch);
        if (blocked == null) return Delete(legacy, id, log, openProfile);
        try { log?.Invoke(DeleteLogLine(LogId(id), null, null, blocked)); }
        catch (Exception) { }
        return new ProfileDeleteResult { Id = LogId(id), Outcome = blocked };
    }

    // One PROFILE_DELETE log line per attempt, on every path. openProfile: the save open in this process, refused here under
    // the catalog lease whatever the UI offered.
    public static ProfileDeleteResult Delete(string legacy, string id, Action<string> log, string openProfile = null)
    {
        var result = new ProfileDeleteResult { Id = LogId(id) };
        try
        {
            legacy = CanonicalLegacy(legacy);
            using (Lease(legacy)) DeleteLocked(legacy, id, result, log, openProfile);
        }
        catch (Exception error) { result.Outcome = "failed:" + Reason(error); }
        try { log?.Invoke(DeleteLogLine(result.Id, result.Name, result.Moved, result.Outcome)); }
        catch (Exception) { }
        return result;
    }

    private static void DeleteLocked(string legacy, string id, ProfileDeleteResult result, Action<string> log, string openProfile)
    {
        var catalog = LoadUnlocked(legacy);
        var slot = catalog.Slots.SingleOrDefault(row => row.Id == id);
        if (slot == null) { result.Outcome = "refused-unknown"; return; }
        result.Name = slot.Name;
        if (slot.Id == LegacyId) { result.Outcome = "refused-legacy"; return; }
        if (IsOpenSave(legacy, slot.Id, catalog.SelectedId, openProfile)) { result.Outcome = "refused-current"; return; }
        string profiles = ProfilesRoot(legacy), source = SlotRoot(legacy, slot.Id);
        OfflineGuard.RejectReparsePoints(profiles); OfflineGuard.RejectReparsePoints(source);
        OfflineGuard.RejectReparsePoints(Path.Combine(profiles, DeletedFolder));
        bool folder = Directory.Exists(source);
        var summary = folder ? Summarize(legacy, slot) : null;
        if (summary != null && summary.PendingUnknown) { result.Outcome = "refused-unreadable"; return; }
        if (summary != null && summary.HasPendingMatch) { result.Outcome = "refused-pending"; return; }
        if (folder && InUse(source)) { result.Outcome = "refused-in-use"; return; }
        catalog.Slots.Remove(slot);
        if (catalog.SelectedId == slot.Id) catalog.SelectedId = null;
        Commit(legacy, catalog);
        result.Outcome = "deleted";
        if (folder)
        {
            try { FaultHook?.Invoke("before-delete-move:" + slot.Id); result.Moved = MoveToDeleted(legacy, source, slot.Id, false); }
            catch (Exception) { result.Outcome = "move-deferred"; }
        }
        PurgeDeleted(legacy, log, result.Moved == "none" ? null : result.Moved.Substring(result.Moved.LastIndexOf('/') + 1));
    }

    // A live process holding the save's own lock (a bound save elsewhere) must keep its folder.
    private static bool InUse(string slotRoot)
    {
        string path = Path.Combine(slotRoot, "profile.xml.lock");
        OfflineGuard.RejectReparsePoints(path);
        if (!File.Exists(path)) return false;
        try { using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return false; }
        catch (IOException) { return true; }
    }

    // Returns the moved folder relative to the save root, forward slashes.
    // orphan: a folder Reconcile found (never listed, so possibly a healthy save after a lost catalog). Its name ends in
    // -orphan, which ParseDeleted rejects: purge never removes it and it never counts toward the kept entries.
    private static string MoveToDeleted(string legacy, string source, string id, bool orphan)
    {
        string deleted = Path.Combine(ProfilesRoot(legacy), DeletedFolder);
        OfflineGuard.RejectReparsePoints(deleted); OfflineGuard.RejectReparsePoints(source);
        Directory.CreateDirectory(deleted);
        string entry = id + "-" + DateTime.UtcNow.ToString(DeletedStamp, CultureInfo.InvariantCulture) + (orphan ? OrphanSuffix : ""), target = Path.Combine(deleted, entry);
        OfflineGuard.RejectReparsePoints(target);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Deleted-save target already exists.");
        Directory.Move(source, target);
        return "profiles/" + DeletedFolder + "/" + entry;
    }

    // Folders under profiles/.deleted named <32-hex id>-<stamp>; anything else is not ours and is ignored.
    private static bool ParseDeleted(string name, out DateTime stamp)
    {
        stamp = default(DateTime);
        return name != null && name.Length == 32 + 1 + DeletedStampLength && name[32] == '-' && ValidId(name.Substring(0, 32))
            && DateTime.TryParseExact(name.Substring(33), DeletedStamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out stamp);
    }

    public static IReadOnlyList<string> DeletedFolders(string legacy)
    {
        string deleted = Path.Combine(ProfilesRoot(CanonicalLegacy(legacy)), DeletedFolder);
        if (!Directory.Exists(deleted)) return new string[0];
        return Directory.GetDirectories(deleted).Select(dir => Path.GetFileName(dir)).Where(name => ParseDeleted(name, out _) || name.EndsWith(OrphanSuffix, StringComparison.Ordinal) && ParseDeleted(name.Substring(0, name.Length - OrphanSuffix.Length), out _))
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }

    // Keep the newest KeptDeleted by the stamp in the folder name (a move keeps the mtime, so it says nothing). An entry
    // holding a reparse point anywhere is left alone: nothing is followed out of the save root.
    private static void PurgeDeleted(string legacy, Action<string> log, string justMoved)
    {
        int removed = 0, skipped = 0, failed = 0;
        try
        {
            string deleted = Path.Combine(ProfilesRoot(legacy), DeletedFolder);
            if (!Directory.Exists(deleted)) return;
            OfflineGuard.RejectReparsePoints(deleted);
            var entries = new List<KeyValuePair<DateTime, string>>();
            foreach (string dir in Directory.GetDirectories(deleted))
                if (Path.GetFileName(dir) != justMoved && ParseDeleted(Path.GetFileName(dir), out DateTime stamp)) entries.Add(new KeyValuePair<DateTime, string>(stamp, dir));
            // The folder just moved is always kept (a clock rolled back cannot make it the oldest) and counts as one of the kept.
            foreach (var old in entries.OrderByDescending(row => row.Key).ThenByDescending(row => row.Value, StringComparer.Ordinal).Skip(KeptDeleted - (justMoved == null ? 0 : 1)))
            {
                try
                {
                    if (!PlainTree(old.Value)) { skipped++; continue; }
                    Directory.Delete(old.Value, true); removed++;
                }
                catch (Exception) { failed++; }
            }
        }
        catch (Exception) { failed++; }
        // Logging only when something happened (an ordinary delete purges nothing); a log failure never fails the delete.
        if (removed + skipped + failed > 0) try { log?.Invoke("PROFILE_PURGE removed=" + removed + " skipped=" + skipped + " failed=" + failed); } catch (Exception) { }
    }
    private static bool PlainTree(string root)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            foreach (string child in Directory.EnumerateFileSystemEntries(current))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) return false;
                if (Directory.Exists(child)) pending.Push(child);
            }
        }
        return true;
    }

    // Startup picker only, never while a save is bound. Finishes what a crashed delete left and removes what an
    // interrupted create/delete orphaned: a listed folder-less slot is dropped, an unlisted 32-hex folder is moved to
    // .deleted. Never purges. A missing catalog (fabricated fresh by LoadUnlocked) or missing profiles/ folder does nothing.
    public static void Reconcile(string legacy, Action<string> log)
    {
        SaveHold.Require("reconciling saves");
        var dropped = new List<string>(); var moved = new List<string>(); string skipped = null, failed = null;
        try
        {
            legacy = CanonicalLegacy(legacy);
            using (Lease(legacy))
            {
                var catalog = LoadUnlocked(legacy);
                string profiles = ProfilesRoot(legacy);
                if (!File.Exists(CatalogPath(legacy))) skipped = "no-catalog";
                else if (!Directory.Exists(profiles)) skipped = "no-profiles-folder";
                else
                {
                    OfflineGuard.RejectReparsePoints(profiles);
                    var listed = new HashSet<string>(catalog.Slots.Select(row => row.Id), StringComparer.Ordinal);
                    // A slot is dropped only when it is absent from a successful listing of profiles/ (an access error aborts here).
                    FaultHook?.Invoke("before-reconcile-enumerate");
                    var folders = new HashSet<string>(Directory.GetDirectories(profiles).Select(dir => Path.GetFileName(dir)), StringComparer.OrdinalIgnoreCase);
                    foreach (var row in catalog.Slots)
                        if (row.Id != LegacyId && !folders.Contains(row.Id)) dropped.Add(row.Id);
                    if (dropped.Count > 0)
                    {
                        catalog.Slots.RemoveAll(row => dropped.Contains(row.Id));
                        if (catalog.SelectedId != null && dropped.Contains(catalog.SelectedId)) catalog.SelectedId = null;
                        Commit(legacy, catalog);
                    }
                    foreach (string name in folders.OrderBy(name => name, StringComparer.Ordinal))
                    {
                        if (name == LegacyId || !ValidId(name) || listed.Contains(name)) continue;
                        try { MoveToDeleted(legacy, SlotRoot(legacy, name), name, true); moved.Add(name); }
                        catch (Exception error) { failed = Reason(error); }
                    }
                }
            }
        }
        catch (Exception error) { failed = Reason(error); }
        try
        {
            log?.Invoke("PROFILE_RECONCILE dropped=" + (dropped.Count == 0 ? "none" : string.Join(",", dropped)) + " moved=" + (moved.Count == 0 ? "none" : string.Join(",", moved))
                + (skipped == null ? "" : " skipped=" + skipped) + (failed == null ? "" : " failed=" + failed));
        }
        catch (Exception) { }
    }

    internal static string Reason(Exception error)
    {
        string text = (error.GetType().Name + ": " + error.Message).Replace('\r', ' ').Replace('\n', ' ');
        return text.Length > 160 ? text.Substring(0, 160) : text;
    }

    // ---- Startup save: open the last-opened save on launch ------------------------------------------------------------
    // "Last opened" is SelectedId in profiles.xml, the one record the picker, Change Profile and tooling already share. It is
    // committed only after a save has fully opened (ClientBridge.BindStartupPath) and lives outside every save file. Decide
    // reads only (apart from the unfinished-start marker): it never opens a LocalProfile, takes a lease on a save, or writes,
    // repairs, migrates or purges a save. It never throws: every failure is a Picker choice with a reason, so the player always
    // lands on the picker or in a save. The real open that follows is the picked-save path and does what opening by hand does.
    // A catalog that cannot be read is not handled here (ResolveActiveProfile fails closed first, as it always did).
    internal static class StartupReasons
    {
        internal const string LastOpened = "last-opened", RequestedSave = "requested-save", RequestedPicker = "requested-picker",
            RequestInvalid = "request-invalid", NoSaves = "no-saves", NoCatalog = "no-catalog", NoneRecorded = "none-recorded",
            UncleanExit = "unclean-exit", UnknownSave = "unknown-save", SaveMissing = "save-missing", OptionsMissing = "options-missing",
            OptionsUnreadable = "options-unreadable", LinksRejected = "links-rejected", SaveUnreadable = "save-unreadable",
            SaveIncompatible = "save-incompatible", CheckFailed = "check-failed", OpenFailed = "open-failed",
            RelaunchUnavailable = "relaunch-unavailable";
    }

    // Wording rule: the checks before the open read only, so a refusal there says nothing was opened or changed. Once the real
    // open has started it is the same path as opening the save by hand (recovery, upgrade, a settled abandoned match), so a failure
    // there promises no more than that.
    internal static class StartupNotice
    {
        internal const string RelaunchUnavailable = "The save you chose could not be opened: it changed or was damaged after you picked it. Nothing was opened, so nothing was changed. Choose a save to continue.";
        internal static string Plain(string reason)
        {
            switch (reason)
            {
                case StartupReasons.SaveMissing: return "its save file is missing.";
                case StartupReasons.OptionsMissing: return "its options file is missing.";
                case StartupReasons.OptionsUnreadable: return "its options file cannot be read.";
                case StartupReasons.LinksRejected: return "its files are links, which are refused.";
                case StartupReasons.SaveUnreadable: return "its save file cannot be read.";
                case StartupReasons.SaveIncompatible: return "it is from a version this build cannot open.";
                default: return "it could not be checked.";
            }
        }
        internal static string NotOpened(string name, string reason) => "SoloStone did not open your last save" + (name == null ? "" : ", " + name)
            + ": " + Plain(reason) + " It was only checked, not opened, so nothing was changed. Choose a save to continue; details are in the game log.";
        internal static string OpenFailed(string name) => (name ?? "Your last save") + " could not finish opening. Opening a save can run recovery or an upgrade before it fails, "
            + "so it may have changed, exactly as if you had opened it yourself. Retry or choose another; details are in the game log.";
        internal static string Unclean(string name) => "SoloStone closed before the main menu finished loading last time, so it is not opening "
            + (name == null ? "your last save" : name) + " automatically. Choose a save to continue.";
        internal static string Dropped(string name) => (name == null ? "Your last save" : name) + " is no longer in the saves folder, so it was taken off the list. Nothing else was changed. Choose a save to continue.";
    }

    internal static class Startup
    {
        internal const string RequestFile = "startup-save.txt", MarkerFile = "startup-open.txt", RequestInvalid = "!invalid";
        private const int MaximumFile = 512;

        internal static string RootDirectory(string legacy) => Root(CanonicalLegacy(legacy));
        internal static string RequestPath(string legacy) => Path.Combine(RootDirectory(legacy), RequestFile);
        internal static string MarkerPath(string legacy) => Path.Combine(RootDirectory(legacy), MarkerFile);

        // One-shot tool request ("picker" or a slot id), consumed whether or not it is valid so it never repeats. null: none.
        // Written by tooling before a launch (tools/remote); a player never has one.
        internal static string ConsumeRequest(string legacy)
        {
            SaveHold.Require("the startup save request");
            string path;
            try { path = RequestPath(legacy); if (!File.Exists(path)) return null; }
            catch (Exception) { return null; }
            string text = null;
            try
            {
                OfflineGuard.RejectReparsePoints(path);
                if (new FileInfo(path).Length <= MaximumFile) text = File.ReadAllText(path).Trim();
            }
            catch (Exception) { }
            // A request that cannot be removed would repeat on every launch: treat it as invalid (the picker) instead of obeying it.
            try { File.Delete(path); } catch (Exception) { return RequestInvalid; }
            return text == "picker" || text != null && ValidId(text) ? text : RequestInvalid;
        }

        public sealed class Marker { public string SlotId, RunId; }

        // The save the previous launch opened but never confirmed at Home. A present file that cannot be read still counts.
        internal static Marker ReadMarker(string legacy)
        {
            try
            {
                string path = MarkerPath(legacy);
                if (!File.Exists(path)) return null;
                var marker = new Marker { SlotId = "unknown" };
                try
                {
                    OfflineGuard.RejectReparsePoints(path);
                    if (new FileInfo(path).Length <= MaximumFile)
                        foreach (string line in File.ReadAllLines(path))
                        {
                            if (line.StartsWith("slotId=", StringComparison.Ordinal) && ValidId(line.Substring(7))) marker.SlotId = line.Substring(7);
                            else if (line.StartsWith("runId=", StringComparison.Ordinal)) marker.RunId = line.Substring(6);
                        }
                }
                catch (Exception) { }
                return marker;
            }
            catch (Exception) { return null; }
        }

        // Written after a save opens, removed once the client reaches Home. A launch that finds it left over never auto-opens:
        // the player gets the picker, so a save that dies before Home can never lock them out of choosing another.
        internal static void Arm(string legacy, string slotId, string runId)
        {
            SaveHold.Require("the startup marker");
            if (!ValidId(slotId) || runId == null || runId.IndexOfAny(new[] { '\r', '\n' }) >= 0) throw new InvalidOperationException("Invalid startup marker values.");
            string path = MarkerPath(legacy), temporary = path + ".tmp";
            OfflineGuard.RejectReparsePoints(path); OfflineGuard.RejectReparsePoints(temporary);
            if (File.Exists(temporary)) File.Delete(temporary);
            File.WriteAllLines(temporary, new[] { "schema=1", "slotId=" + slotId, "runId=" + runId, "utc=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) });
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }

        // Marker for a bind. Auto-open: Decide armed it before the checks (an Arm failure there already sent the launch to the picker), so it
        // is simply ours. Any other bind (a pick, a created save, Change Profile): best effort. A marker that cannot be written must not
        // keep a player out of every save; the protection is only absent for that pick, and a later launch that finds an older marker still
        // shows the picker. Never throws; returns whether the run owns a marker to clear.
        internal static bool BeginOpen(string legacy, string slotId, string runId, bool armedByDecide, out string warning)
        {
            warning = null;
            if (armedByDecide) return true;
            try { Arm(legacy, slotId, runId); return true; }
            catch (Exception error) { warning = Reason(error); return false; }
        }

        // Only the run that armed it clears it.
        internal static bool Confirm(string legacy, string runId)
        {
            SaveHold.Require("the startup marker");
            var marker = ReadMarker(legacy);
            if (marker == null || marker.RunId != runId) return false;
            File.Delete(MarkerPath(legacy));
            return true;
        }

        private static StartupChoice Picker(string reason, string request, ProfileSlot slot, string notice) =>
            new StartupChoice { Mode = StartupChoice.Picker, Reason = reason, Request = request, TargetId = slot?.Id, TargetName = slot?.Name, Notice = notice };

        // request: ConsumeRequest's value. marker: ReadMarker's. runId: when given, the unfinished-start marker is armed for the
        // target BEFORE any read of that save (a crash in the checks or the open leaves it) and cleared again if the outcome
        // is a picker. Never throws.
        internal static StartupChoice Decide(string legacy, string request, Marker marker, string runId = null)
        {
            string requested = request == null || request == RequestInvalid ? null : request;
            string reportRequest = request == RequestInvalid ? "invalid" : request;
            bool armed = false;
            StartupChoice Done(StartupChoice choice)
            {
                if (armed && !choice.IsAuto) { try { Confirm(legacy, runId); } catch (Exception) { } }
                return choice;
            }
            try
            {
                if (request == "picker") return Picker(StartupReasons.RequestedPicker, reportRequest, null, null);
                if (request == RequestInvalid) return Picker(StartupReasons.RequestInvalid, reportRequest, null, null);
                legacy = CanonicalLegacy(legacy);
                string catalogPath = CatalogPath(legacy);
                bool persisted = File.Exists(catalogPath) || File.Exists(catalogPath + ".tmp") || File.Exists(catalogPath + ".bak");
                var catalog = Load(legacy);
                ProfileSlot slot;
                if (requested != null)
                {
                    // A tool asked for this save: same checks as the recorded one, but neither the record nor a leftover marker applies.
                    slot = catalog.Slots.SingleOrDefault(row => row.Id == requested);
                    if (slot == null) return Picker(StartupReasons.UnknownSave, reportRequest, null, null);
                }
                else
                {
                    if (catalog.Slots.Count == 0) return Picker(StartupReasons.NoSaves, null, null, null);
                    // A fabricated catalog (only a root profile.xml, no profiles.xml yet) records nothing: the player picks once.
                    if (!persisted) return Picker(StartupReasons.NoCatalog, null, null, null);
                    // Nothing recorded: the last-opened save was deleted. Several saves or one, the player chooses; the client
                    // never opens a save the player has not opened before.
                    if (catalog.SelectedId == null) return Picker(StartupReasons.NoneRecorded, null, null, null);
                    slot = catalog.Slots.Single(row => row.Id == catalog.SelectedId);
                    if (marker != null)
                    {
                        var unfinished = catalog.Slots.SingleOrDefault(row => row.Id == marker.SlotId) ?? slot;
                        return Picker(StartupReasons.UncleanExit, null, unfinished, StartupNotice.Unclean(unfinished.Name));
                    }
                }
                if (runId != null) { Arm(legacy, slot.Id, runId); armed = true; }
                FaultHook?.Invoke("startup-checks:" + slot.Id);   // test seam: the marker must already be down here
                string problem = SlotProblem(legacy, slot);
                if (problem != null) { string why = ProblemReason(problem); return Done(Picker(why, reportRequest, slot, StartupNotice.NotOpened(slot.Name, why))); }
                string path = SlotProfile(legacy, slot.Id);
                string unreadable = Preflight(path);
                if (unreadable != null) return Done(Picker(unreadable, reportRequest, slot, StartupNotice.NotOpened(slot.Name, unreadable)));
                return new StartupChoice { Mode = StartupChoice.Auto, Reason = requested != null ? StartupReasons.RequestedSave : StartupReasons.LastOpened,
                    Request = reportRequest, TargetId = slot.Id, TargetPath = path, TargetName = slot.Name };
            }
            catch (Exception error)
            {
                // Reason() is the same one-line form the other catalog logs use; the code stays stable for tools.
                return Done(new StartupChoice { Mode = StartupChoice.Picker, Reason = StartupReasons.CheckFailed, Request = reportRequest,
                    Failure = Reason(error), Notice = StartupNotice.NotOpened(null, StartupReasons.CheckFailed) });
            }
        }

        // Everything OfflineGuard.ResolveLaunch does for a picker-capable launch, separable from the platform check so it runs on any
        // host: consume the tool request; an explicit Change Profile relaunch binds its target (and if that target is no longer
        // usable, the picker with a notice, never the previously recorded save); otherwise finish what a crashed delete/create left
        // (the picker's Reconcile, now while nothing is bound), then choose between auto-open and the picker. Throws only for the
        // conditions it always threw for: an unreadable catalog, malformed relaunch selectors.
        internal static StartupLaunch ResolveLaunch(string rootProfile, string run, string relaunchPrevious, string relaunchToken)
        {
            string request = ConsumeRequest(rootProfile);
            var result = new StartupLaunch { Profile = rootProfile, Selector = true };
            if (TryResolveRelaunch(rootProfile, run, relaunchPrevious, relaunchToken, out string target, out string targetPath))
            {
                result.Profile = targetPath; result.RelaunchTarget = target; result.Selector = false;
                return result;
            }
            string droppedName = null, droppedId = null;
            var lines = new System.Collections.Generic.List<string>();
            try
            {
                var before = Load(CanonicalLegacy(rootProfile));
                Reconcile(rootProfile, lines.Add);
                var after = Load(CanonicalLegacy(rootProfile));
                if (before.SelectedId != null && after.SelectedId == null) { droppedId = before.SelectedId; droppedName = before.Slots.SingleOrDefault(row => row.Id == droppedId)?.Name; }
            }
            catch (Exception error) { lines.Add("PROFILE_RECONCILE failed=" + Reason(error)); }
            result.Profile = ResolveActiveProfile(rootProfile);
            StartupChoice choice;
            if (relaunchPrevious != null || relaunchToken != null)
                choice = Picker(StartupReasons.RelaunchUnavailable, null, null, StartupNotice.RelaunchUnavailable);
            else
            {
                choice = Decide(rootProfile, request, ReadMarker(rootProfile), run);
                if (!choice.IsAuto && (choice.Reason == StartupReasons.NoneRecorded || choice.Reason == StartupReasons.NoSaves) && droppedId != null)
                {
                    choice.Reason = StartupReasons.SaveMissing; choice.TargetId = droppedId; choice.TargetName = droppedName;
                    choice.Notice = StartupNotice.Dropped(droppedName);
                }
            }
            choice.Reconciled = true; choice.LogLines.AddRange(lines);
            if (choice.IsAuto) { result.Profile = choice.TargetPath; result.Selector = false; }
            result.Startup = choice;
            return result;
        }

        private static string ProblemReason(string problem)
        {
            switch (problem)
            {
                case "save file missing": return StartupReasons.SaveMissing;
                case "options missing": return StartupReasons.OptionsMissing;
                case "linked files rejected": return StartupReasons.LinksRejected;
                default: return StartupReasons.OptionsUnreadable;
            }
        }

        // Read-only: parse the save file and check its version, the constructor's first two gates. Everything the constructor does
        // after that is the ordinary open (validation, then backup, upgrade, recovery, settling an interrupted match): the real open
        // runs the same code as opening the save by hand, so what it writes is what opening by hand writes. No lock file or copy here.
        private static string Preflight(string path)
        {
            try
            {
                var data = LocalProfile.ReadFile(path);
                if (data == null) return StartupReasons.SaveUnreadable;
                return LocalProfile.IsSupportedVersion(data.Version) ? null : StartupReasons.SaveIncompatible;
            }
            catch (Exception) { return StartupReasons.SaveUnreadable; }
        }
    }

    // Old process only writes intent. Catalog stays bound to old profile until its lease/process ends.
    public static string RequestRelaunch(string legacy, string runId, string targetId)
    {
        legacy = CanonicalLegacy(legacy);
        if (!Guid.TryParseExact(runId, "D", out _)) throw new InvalidOperationException("Invalid launch ID.");
        using (Lease(legacy))
        {
            var slot = LoadUnlocked(legacy).Slots.SingleOrDefault(row => row.Id == targetId) ?? throw new InvalidOperationException("Unknown save selection.");
            RequireSlot(SlotProfile(legacy, slot.Id), slot.Name, slot.Id != LegacyId);
            string token = Guid.NewGuid().ToString("N");
            WriteAtomic(SwitchXml, SwitchPath(legacy), new ProfileSwitchRequest { Version = 1, RunId = runId, Token = token, TargetId = targetId }, false);
            return token;
        }
    }

    // Only an immediate launcher successor carrying both one-shot values may bypass the ordinary startup picker.
    // This validates metadata only. The selected marker is committed after ClientBridge opens the target ledger.
    public static bool TryResolveRelaunch(string legacy, string newRunId, string previousRunId, string token,
        out string targetId, out string targetPath)
    {
        targetId = targetPath = null;
        legacy = CanonicalLegacy(legacy);
        if (previousRunId == null && token == null) return false;
        var request = ReadSwitch(legacy, newRunId, previousRunId, token);
        var slot = Load(legacy).Slots.SingleOrDefault(row => row.Id == request.TargetId)
            ?? throw new InvalidOperationException("Relaunch target is not in save catalog.");
        // A target damaged after the old process validated it falls back to the ordinary picker.
        if (SlotProblem(legacy, slot) != null) return false;
        targetId = slot.Id;
        targetPath = SlotProfile(legacy, slot.Id);
        return true;
    }

    public static void CompleteRelaunch(string legacy, string newRunId, string previousRunId, string token, string targetId)
    {
        legacy = CanonicalLegacy(legacy);
        var request = ReadSwitch(legacy, newRunId, previousRunId, token);
        if (request.TargetId != targetId) throw new InvalidOperationException("Save relaunch target changed.");
        using (Lease(legacy))
        {
            var catalog = LoadUnlocked(legacy);
            var slot = catalog.Slots.SingleOrDefault(row => row.Id == request.TargetId) ?? throw new InvalidOperationException("Relaunch target is not in save catalog.");
            string path = SlotProfile(legacy, slot.Id); RequireSlot(path, slot.Name, slot.Id != LegacyId);
            catalog.SelectedId = slot.Id;
            Commit(legacy, catalog);
            File.Delete(SwitchPath(legacy));
        }
    }

    private static ProfileSwitchRequest ReadSwitch(string legacy, string newRunId, string previousRunId, string token)
    {
        if (!Guid.TryParseExact(newRunId, "D", out _) || !Guid.TryParseExact(previousRunId, "D", out _)
            || newRunId == previousRunId
            || string.IsNullOrEmpty(token) || token.Length != 32 || !token.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Invalid save relaunch selectors.");
        string requestPath = SwitchPath(legacy);
        OfflineGuard.RejectReparsePoints(requestPath);
        if (!File.Exists(requestPath)) throw new InvalidOperationException("Save relaunch request is missing.");
        var request = Read<ProfileSwitchRequest>(SwitchXml, requestPath, 4096);
        if (request == null || request.Version != 1 || request.RunId != previousRunId || request.Token != token
            || !Guid.TryParseExact(request.RunId, "D", out _))
            throw new InvalidOperationException("Save relaunch request does not match its launcher successor.");
        return request;
    }

    private static ProfileCatalogData Load(string legacy) { legacy = CanonicalLegacy(legacy); using (Lease(legacy)) return LoadUnlocked(legacy); }
    private static ProfileCatalogData LoadUnlocked(string legacy)
    {
        string path = CatalogPath(legacy); OfflineGuard.RejectReparsePoints(path);
        if (!File.Exists(path)) {
            var recovered = RecoverCatalog(legacy, path);
            if (recovered != null) return recovered;
            var fresh = new ProfileCatalogData();
            if (File.Exists(legacy)) { fresh.Slots.Add(new ProfileSlot { Id = LegacyId, Name = "Save 1", CreatedUtc = File.GetCreationTimeUtc(legacy).ToString("o", CultureInfo.InvariantCulture) }); fresh.SelectedId = LegacyId; }
            return fresh;
        }
        ProfileCatalogData catalog = Read<ProfileCatalogData>(CatalogXml, path, 65536);
        ValidateCatalog(legacy, catalog); return catalog;
    }
    // File.Replace is not atomic on Windows or Wine: it renames profiles.xml to .bak, then moves .tmp over it. A crash
    // between the two leaves no catalog beside a complete .tmp and the previous .bak (a first-ever write that died
    // before its move leaves only .tmp). That state is never a fresh catalog: promote the newest valid copy (.tmp, then
    // .bak) or fail closed. Callers hold the catalog lease.
    private static ProfileCatalogData RecoverCatalog(string legacy, string path)
    {
        string temporary = path + ".tmp", backup = path + ".bak";
        OfflineGuard.RejectReparsePoints(temporary); OfflineGuard.RejectReparsePoints(backup);
        if (!File.Exists(temporary) && !File.Exists(backup)) return null;
        var errors = new List<string>();
        foreach (string candidate in new[] { temporary, backup })
        {
            if (!File.Exists(candidate)) continue;
            ProfileCatalogData catalog;
            try { catalog = Read<ProfileCatalogData>(CatalogXml, candidate, 65536); ValidateCatalog(legacy, catalog); }
            catch (Exception error) when (error is InvalidOperationException || error is IOException || error is XmlException)
            { errors.Add(Path.GetFileName(candidate) + ": " + error.Message); continue; }
            // A partial .tmp left behind after the .bak wins is set aside by the next write (the target exists then).
            if (candidate == temporary) File.Move(temporary, path); else File.Copy(backup, path);
            return catalog;
        }
        throw new InvalidOperationException("The save list is missing and no recovery copy is readable; nothing was reset ("
            + string.Join("; ", errors) + ").");
    }
    private static void ValidateCatalog(string legacy, ProfileCatalogData catalog)
    {
        if (catalog == null || catalog.Version != 1 || catalog.Slots == null || catalog.Slots.Count > MaximumSlots
            || catalog.Slots.Any(row => row == null || !ValidId(row.Id) || ValidateName(row.Name) != row.Name || !DateTime.TryParse(row.CreatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
            || catalog.Slots.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() != catalog.Slots.Count
            || catalog.Slots.Select(row => row.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != catalog.Slots.Count
            // A cleared selection is valid with slots (a deleted selected save); a named one must be a slot.
            || (catalog.Slots.Count == 0 ? catalog.SelectedId != null : catalog.SelectedId != null && catalog.Slots.All(row => row.Id != catalog.SelectedId))) throw new InvalidOperationException("Unsupported or corrupt save catalog.");
        // Structure only. Slot files are checked per slot so one damaged save never hides the healthy ones.
        foreach (var row in catalog.Slots) SlotProfile(legacy, row.Id);
    }
    // Short picker reason, or null when the slot's files and options are usable.
    internal static string SlotProblem(string legacy, ProfileSlot slot)
    {
        try
        {
            string path = SlotProfile(legacy, slot.Id);
            OfflineGuard.ValidateProfileFiles(path);
            if (!File.Exists(path)) return "save file missing";
            if (slot.Id == LegacyId) return null;
            string options = Path.Combine(Path.GetDirectoryName(path), "profile-options.xml");
            OfflineGuard.RejectReparsePoints(options);
            if (!File.Exists(options)) return "options missing";
            ReadOptions(options);
            return null;
        }
        catch (Exception error) when (error is InvalidOperationException || error is IOException || error is UnauthorizedAccessException || error is XmlException)
        {
            return error.Message.StartsWith("Reparse", StringComparison.Ordinal) ? "linked files rejected" : "options unreadable";
        }
    }
    private static void Commit(string legacy, ProfileCatalogData catalog) { ValidateCatalog(legacy, catalog); WriteAtomic(CatalogXml, CatalogPath(legacy), catalog, true); }
    private static void WriteAtomic(XmlSerializer serializer, string path, object value, bool backup)
    {
        string temporary = path + ".tmp"; OfflineGuard.RejectReparsePoints(path); OfflineGuard.RejectReparsePoints(temporary);
        // Callers hold the catalog lease, so a leftover temp is a crashed write, never a live one. It is set aside (kept
        // as evidence) only when the target exists; a missing catalog with a temp is the mid-replace state that
        // RecoverCatalog must resolve first, so refuse rather than overwrite the only complete copy.
        if (File.Exists(temporary))
        {
            if (backup && !File.Exists(path)) throw new IOException("The save list write was interrupted; reopen the save picker to recover it.");
            File.Move(temporary, temporary + ".stale-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N"));
        }
        OfflineGuard.RejectReparsePoints(path + ".bak"); WriteNew(serializer, temporary, value);
        try
        {
            FaultHook?.Invoke("before-replace:" + Path.GetFileName(path));
            // Not atomic on Windows/Wine (target -> .bak, then temp -> target); RecoverCatalog handles a crash in between.
            if (File.Exists(path)) File.Replace(temporary, path, backup ? path + ".bak" : null); else File.Move(temporary, path);
        }
        catch
        {
            if (File.Exists(temporary))
                File.Move(temporary, temporary + ".failed-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)
                    + "-" + Guid.NewGuid().ToString("N"));
            throw;
        }
    }
    private static void WriteNew(XmlSerializer serializer, string path, object value) { using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { serializer.Serialize(stream, value); stream.Flush(true); } }
    private static ProfileOptions ReadOptions(string path) { OfflineGuard.RejectReparsePoints(path); if (!File.Exists(path)) throw new InvalidOperationException("Save options are missing."); var value = Read<ProfileOptions>(OptionsXml, path, 8192); ValidateOptions(value); return value; }
    private static T Read<T>(XmlSerializer serializer, string path, long maximum)
    {
        if (new FileInfo(path).Length > maximum) throw new InvalidOperationException("Save metadata is too large.");
        using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = maximum }))
            return (T)serializer.Deserialize(reader, new XmlDeserializationEvents {
                OnUnknownAttribute = (sender, e) => { throw new InvalidOperationException("Unknown save metadata attribute."); },
                OnUnknownElement = (sender, e) => { throw new InvalidOperationException("Unknown save metadata element."); },
                OnUnknownNode = (sender, e) => { if (e.NodeType != XmlNodeType.Whitespace) throw new InvalidOperationException("Unknown save metadata node."); }
            });
    }
    private static void ValidateOptions(ProfileOptions value) { if (value == null || value.Version != 1 || value.MetaId != MetaId
        || value.GrindTier != null && !GrindTiers.IsKnown(value.GrindTier)
        || double.IsNaN(value.LegendRating) || double.IsInfinity(value.LegendRating)
        || (value.StartLegendOne ? value.LegendRating <= 0 : value.LegendRating != 0)) throw new InvalidOperationException("Unsupported save options."); }
    private static void RequireSlot(string path, string name, bool options) { OfflineGuard.ValidateProfileFiles(path); if (!File.Exists(path)) throw new InvalidOperationException("Save file is missing: " + name); if (options) ReadOptions(Path.Combine(Path.GetDirectoryName(path), "profile-options.xml")); }
    // Every catalog read and write comes through here, and the lock file alone changes the save tree.
    private static FileStream Lease(string legacy) { SaveHold.Require("the save catalog"); string path = CatalogPath(legacy) + ".lock"; OfflineGuard.RejectReparsePoints(path); Directory.CreateDirectory(Path.GetDirectoryName(path)); return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
    private static string CanonicalLegacy(string path) { path = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path))); if (!string.Equals(Path.GetFileName(path), "profile.xml", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Save catalog root must be profile.xml."); OfflineGuard.ValidateProfileFiles(path); return path; }
    private static string Root(string legacy) => Path.GetDirectoryName(Path.GetFullPath(legacy));
    private static string ProfilesRoot(string legacy) => Path.Combine(Root(legacy), "profiles");
    private static string SlotRoot(string legacy, string id) => Path.Combine(ProfilesRoot(legacy), id);
    private static string SlotOptions(string legacy, string id) => Path.Combine(SlotRoot(legacy, id), "profile-options.xml");
    private static string SlotProfile(string legacy, string id) { if (id == LegacyId) return legacy; if (!ValidId(id)) throw new InvalidOperationException("Invalid save ID."); string root = Path.GetFullPath(ProfilesRoot(legacy)) + Path.DirectorySeparatorChar, path = Path.GetFullPath(Path.Combine(root, id, "profile.xml")); if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Save path escaped its root."); return path; }
    private static string ValidateName(string name) { string value = name?.Trim(); if (string.IsNullOrEmpty(value) || value.Length > 32 || value.Any(c => char.IsControl(c) || c == '<' || c == '>')) throw new InvalidOperationException("Save name must be 1–32 plain characters."); return value; }
    private static bool ValidId(string id) => id == LegacyId || id != null && id.Length == 32 && id.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
    private static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    private static ProfileSlot Copy(ProfileSlot value) => new ProfileSlot { Id = value.Id, Name = value.Name, CreatedUtc = value.CreatedUtc };
}
