#if SOLOSTONE_INSTALLER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SoloStone;

// Refusing a launch says nothing about the release, so it gets its own exit code: the launcher neither restores nor holds for it.
internal sealed class InstallerRefusal : InvalidOperationException
{
    internal readonly int ExitCode;
    internal InstallerRefusal(int exitCode, string message) : base(message) { ExitCode = exitCode; }
}

// Explicit build mode: regular lab/candidate plugins retain their fixed account and path guard.
internal static class InstallerRuntime
{
    internal const int ExitHooks = 42, ExitFirewallRefused = 47, ExitRefused = 48, ExitSetupFailed = 49, ExitStartupError = 50;
    // Startup work that is not a hook: a save problem or a full disk says nothing about the release.
    internal static T Prepare<T>(Func<T> step) {
        try { return step(); }
        catch(InstallerRefusal) { throw; }
        // Disk, access and data errors, and the guard's own refusals. Anything else leaves here for ExitCodeFor: a missing method is 42, a null reference 50.
        catch(Exception e) when(e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException
            || e is InvalidDataException || e is FormatException || e is System.Xml.XmlException) { throw new InstallerRefusal(ExitSetupFailed,e.Message); }
    }
    internal static void Prepare(Action step) { Prepare<object>(() => { step(); return null; }); }
    // Awake's last word, for what Prepare does not wrap: Config.Bind writes a file too. Exit 42 holds a version for good, so it is kept for a hook that cannot exist on this client.
    // A disk or access error says nothing about the release (49). Anything else may be the release, so the launcher restores at once and retries a day later (50); a second time holds for good.
    internal static int ExitCodeFor(Exception error) {
        Exception root=error;
        for(var e=error;e!=null;e=e.InnerException) {
            var refusal=e as InstallerRefusal;
            if(refusal!=null) return refusal.ExitCode;
            if(e is NativeCardMismatchException || IsHookFailure(e)) return ExitHooks;
            root=e;
        }
        // A file the release should have shipped, or one antivirus held while it loaded: it may be the release or this PC, so one exit must not hold it for good.
        if(root is FileNotFoundException || root is DirectoryNotFoundException || root is FileLoadException) return ExitStartupError;
        return root is IOException || root is UnauthorizedAccessException?ExitSetupFailed:ExitStartupError;
    }
    static bool IsHookFailure(Exception e) {
        if(e is MissingMemberException || e is TypeLoadException) return true;
        var missing=e as FileNotFoundException;
        if(missing!=null && missing.FileName!=null && missing.FileName.IndexOf(", Version=",StringComparison.Ordinal)>=0) return true; // An assembly, not a data file.
        // Harmony throws its own type from PatchAll and plain ones from Patch: both have its frames on the stack.
        return e.GetType().FullName.StartsWith("HarmonyLib.",StringComparison.Ordinal) || (e.StackTrace??"").IndexOf("HarmonyLib.",StringComparison.Ordinal)>=0;
    }
    // Majors must match tested-client.json (a launcher test proves it). Minor, patch and hashes are the launcher's policy.
    internal const int CompiledMajor=37;
    internal static string Resolve(string executable, string localData, string[] args, bool candidate, bool wine)
        => Resolve(executable,localData,args,candidate,wine,WindowsInstallPaths.RegisteredFolders,
            install=>FirewallRegistry.Verify(install,FirewallProtection.Executables(install)),RuntimeCosmeticVersion);

    // A compile-time reference would inline the value from our reference DLLs, so this reads the running client's.
    private static string RuntimeCosmeticVersion() {
        var field=Type.GetType("Network, Assembly-CSharp")?.GetField("CosmeticVersion",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static);
        if(field==null) return null;
        return (field.IsLiteral?field.GetRawConstantValue():field.GetValue(null)) as string;
    }

    // Host queries are injectable so admission, including direct launches, can be tested without Windows COM.
    internal static string Resolve(string executable,string localData,string[] args,bool candidate,bool wine,
        Func<string[]> registered,Action<InstallRecord> firewall,Func<string> cosmeticVersion)
    {
        int index=Array.IndexOf(args,"-hs-installer-root");
        if(index<0) {
            if(args.Any(a=>a!=null && a.StartsWith("-hs-installer-root",StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Invalid installer root selector.");
            return null;
        }
        if(candidate || wine || OfflineGuard.LaunchLane(args)!=0 || index+1>=args.Length
            || args.Count(a=>a!=null && a.StartsWith("-hs-installer-root",StringComparison.OrdinalIgnoreCase))!=1)
            throw new InvalidOperationException("Installer launch selector rejected.");
        try { return Admit(executable,localData,args,index,registered,firewall,cosmeticVersion); }
        catch(InstallerRefusal) { throw; }
        catch(Exception e) when(e is InvalidOperationException || e is IOException || e is UnauthorizedAccessException) { throw new InstallerRefusal(ExitRefused,e.Message); }
    }
    private static string Admit(string executable,string localData,string[] args,int index,Func<string[]> registered,Action<InstallRecord> firewall,Func<string> cosmeticVersion)
    {
        string root=Safety.Canonical(args[index+1]);
        var folders=registered();
        Safety.Destination(root,folders);
        // Includes reports, report files, config/log paths and fixture paths before any plugin I/O uses them.
        Safety.Tree(root).ToArray();
        var install=InstallRecord.Load(root);
        Safety.Destination(install.Client,folders);
        Safety.Tree(install.Client).ToArray();
        if(!Safety.Same(PhysicalPaths.Long(Path.GetFullPath(executable)),Path.Combine(install.Client,"Hearthstone.exe"))
            || !Safety.Same(localData,Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            || install.Payload==null)
            throw new InvalidOperationException("Installer identity or copy path rejected.");
        // Never redirect LOCALAPPDATA/USERPROFILE. Current user's existing save location stays unchanged.
        Safety.Canonical(PublicNames.SaveRoot(localData));
        int runAt=Array.IndexOf(args,"-hs-native-ui-run");
        if(runAt<0 || runAt+1>=args.Length) throw new InvalidOperationException("This game was not started by SoloStone. Start it from the SoloStone launcher.");
        LaunchGrant.Check(Path.Combine(root,"reports"),args[runAt+1],install.ClientVersion);
        CheckClientVersion(cosmeticVersion(),install.ClientVersion);
        try { firewall(install); }
        catch(InvalidOperationException e) { throw new InstallerRefusal(ExitFirewallRefused,e.Message); }
        Hashes.Verify(install.Client,install.Files);
        install.Payload.Verify(install.Client);
        return root;
    }
    private static void CheckClientVersion(string running,string recorded) {
        string[] parts=(running??"").Split('.');
        int major;
        if(parts.Length!=2 || !int.TryParse(parts[0],out major) || !int.TryParse(parts[1],out _) || recorded==null)
            throw new InvalidOperationException("The running client's version could not be read.");
        if(major!=CompiledMajor) throw new InvalidOperationException("This SoloStone release does not run on Hearthstone "+major+".");
        if(!recorded.StartsWith(running+".",StringComparison.Ordinal))
            throw new InvalidOperationException("The running client differs from the copy SoloStone recorded. Reinstall SoloStone.");
    }
}
#endif
