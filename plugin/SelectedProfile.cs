using System;

internal static class SelectedProfile
{
    internal static void Initialize(OfflineLaunchContext launch, Action<LocalProfile> ladder, Action<LocalProfile> native)
    {
        LocalProfile profile = OpenStartup(launch, launch.ProfilePath);
        try { ladder(profile); native(profile); }
        catch { profile.Dispose(); throw; }
    }

    // requireExisting: an auto-opened save must already be on disk. A missing file is a failure (the picker takes over), never a
    // new save with the welcome gift.
    internal static LocalProfile OpenStartup(OfflineLaunchContext launch, string profilePath, bool requireExisting = false)
    {
        OfflineGuard.ValidateProfileFiles(profilePath);
        if (requireExisting && !System.IO.File.Exists(profilePath)) throw new System.IO.FileNotFoundException("Save file is missing; nothing was created.");
#if STANDARD_PROOF
        if (launch.SnapshotId != OfflineGuard.StandardSnapshot) throw new InvalidOperationException("Snapshot binding missing");
        LocalProfile profile = LocalProfile.OpenSnapshot(profilePath);
#else
        if (launch.SnapshotId != null) throw new InvalidOperationException("Normal build rejects snapshot profile");
        ProfileOptions options = ProfileCatalog.OptionsForProfile(launch.RootProfilePath, profilePath);
#if !SOLOSTONE_INSTALLER || SOLOSTONE_TEST_HOOKS
        LocalProfileBackups.ProcessRestoreRequest(profilePath, options);
#endif
        LocalProfile profile = new LocalProfile(profilePath, options);
#endif
        try
        {
#if !STANDARD_PROOF
            // #57: a missing profile.xml (first launch, fixed lane/Mac path) is created here with the welcome gift in its
            // first commit. Catalog saves already got it in ProfileCatalog.Create; an existing file never does.
            profile.GrantWelcomeIfNew();
            profile.RecoverInterrupted();
#endif
            return profile;
        }
        catch { profile.Dispose(); throw; }
    }
}
