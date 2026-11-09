using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using SoloStone;

static partial class Program
{
    static int count;
    static readonly List<string> skipped=new List<string>();
    // Visible in the summary, and fatal with SOLOSTONE_REQUIRE_ALL=1 (installer/test-all.sh sets it).
    static void Skip(string what) { skipped.Add(what); Console.WriteLine("SKIP "+what); }
    static void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
    static void Refuses(Action action, string name) {
        try { action(); } catch (InvalidOperationException) { count++; return; }
        throw new Exception("Did not refuse: " + name);
    }
    static void LinkDirectory(string link,string target) {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link,target); return; }
        using(var process=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
            "/d /c mklink /J \""+link+"\" \""+target+"\"") { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true })) {
            process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd(); process.WaitForExit();
            if(process.ExitCode!=0) throw new Exception("Could not create test junction (exit "+process.ExitCode+")");
        }
    }
    static Func<ClientFacts,Verdict> Policy(TestedClient tested) => facts=>ClientPolicy.Decide(facts,tested,null,null,BindingResult.NotAvailable,null);
    static SetupOptions SetupOf(string source,string root,string bundle,TestedClient tested,string saves,string client=null,bool accept=false) => new SetupOptions {
        Source=source, Root=root, ClientPath=client, Bundle=bundle, SaveRoot=saves, Check=Policy(tested), AcceptedWarning=accept };
    static int Main(string[] args) {
        if (args.Length > 0 && args[0] == "tool") return Tool(args.Skip(1).ToArray());
        string temp = Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(), "solostone-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try {
            Atomic.FlushToDisk=false; // A flush per journal save is what makes a swap slow on a Mac, and tests simulate crashes by exception.
            // Fast loop for this swap work: dotnet run -- --retry
            if (args.Contains("--retry")) { EarlyFailures(temp); ReusedBackups(temp); LastSmallRound(temp); FailingSwaps(temp); FinalRoundRelease(temp); KillThenRetry(temp); BusyFiles(temp); Console.WriteLine("PASS " + count + " retry checks"); return 0; }
            if (args.Contains("--preflight")) { AppOnlyPreflight(temp); Console.WriteLine("PASS " + count + " app preflight checks"); return 0; }
            if (args.Contains("--policy")) { ClientPolicyTests(temp); Console.WriteLine("PASS " + count + " policy checks"); return 0; }
            if (args.Contains("--roguelike")) { RoguelikeTests(temp); Console.WriteLine("PASS " + count + " roguelike checks"); return 0; }
            // Fast loop for window work: dotnet run -- --gui
            if (args.Contains("--gui")) { GuiTests(); HookTests(temp); GuiBoundaryTests(); Console.WriteLine("PASS " + count + " window checks"); return 0; }
            EarlyFailures(temp);
            ReusedBackups(temp);
            LastSmallRound(temp);
            FailingSwaps(temp);
            KillThenRetry(temp);
            BusyFiles(temp);
            FinalRoundRelease(temp);
            FinalRoundRegistry(temp);
            FinalRoundRecovery(temp);
            FinalRoundHealth(temp);
            LastRoundTests(temp);
            ElevationSourceRegressions();
            PreflightRegressions(temp);
            AppOnlyPreflight(temp);
            PhysicalPathRegressions(temp);
            string source = Path.Combine(temp, "源 client"), root = Path.Combine(temp, "install å"), saves = Path.Combine(temp,"HearthstoneOffline");
            var supported = FakeClient(source,"37.0.0");
            Check(Discovery.Find(new[]{Path.Combine(temp,"absent"), source}).Single() == source, "discovery fake folders");
            Safety.Pair(source, root, new string[0]);
            Refuses(()=>Safety.Pair(source, source, new string[0]), "same");
            Refuses(()=>Safety.Pair(source, Path.Combine(source,"child"), new string[0]), "target inside source");
            Refuses(()=>Safety.Pair(source, temp, new string[0]), "source inside target");
            Refuses(()=>Safety.Pair(source, root, new[]{temp}), "registered destination");
            Refuses(()=>Safety.Canonical("relative"), "relative path");
            Refuses(()=>Safety.Canonical(@"D:\Battle"+@"Net\Hearthstone"), "forbidden real install");
            Refuses(()=>Safety.Canonical(@"C:\Us"+@"ers\HSOffline"+@"Probe\AppData\Local\HearthstoneOffline"), "accepted save");
            Hashes.Verify(source,supported.Files);
            string gameFile=Path.Combine(source,"Hearthstone.exe"), originalGame=File.ReadAllText(gameFile);
            File.WriteAllText(gameFile,"wrong");
            Refuses(()=>Hashes.Verify(source,supported.Files), "hash mismatch");
            Check(!Directory.Exists(root), "hash failure makes no target");
            File.WriteAllText(gameFile,originalGame);
            string link = Path.Combine(temp,"junction"); LinkDirectory(link,source);
            Refuses(()=>Safety.Pair(link,root,new string[0]), "junction source");
            Refuses(()=>Safety.Pair(source,Path.Combine(link,"nested"),new string[0]), "junction target ancestor");
            LinkDirectory(Path.Combine(source,"link"), source);
            Refuses(()=>Safety.Tree(source), "nested link"); Directory.Delete(Path.Combine(source,"link"));
            Directory.CreateDirectory(root); Directory.CreateDirectory(Path.Combine(root,"client"));
            var install = new InstallRecord { Schema=InstallRecord.CurrentSchema, InstallId=Guid.NewGuid().ToString("N"), Root=root,
                Source=source, ClientPath=Installer.DefaultClient(root), ClientVersion="37.0.0", Files=new Dictionary<string,string>(supported.Files) };
            Json.Save(Path.Combine(root,"install.json"), install);
            Refuses(()=>InstallRecord.Load(root), "marker missing");
            File.WriteAllText(Path.Combine(root,"client",InstallRecord.Marker), "wrong");
            Refuses(()=>InstallRecord.Load(root), "marker wrong");
            File.WriteAllText(Path.Combine(root,"client",InstallRecord.Marker),install.InstallId);
            Check(InstallRecord.Load(root).ClientVersion=="37.0.0", "schema round trip");
            install.Schema=1; Json.Save(Path.Combine(root,"install.json"),install);
            Refuses(()=>InstallRecord.Load(root), "schema 1 record dropped"); install.Schema=InstallRecord.CurrentSchema; Json.Save(Path.Combine(root,"install.json"),install);
            string exe=Path.Combine(root,"client","Hearthstone.exe"); File.Copy(gameFile,exe);
            install.Files=new Dictionary<string,string>{{"Hearthstone.exe",supported.Files["Hearthstone.exe"]}}; Json.Save(Path.Combine(root,"install.json"),install);
            var commands=FirewallPlan.Create(install,true);
            Check(commands.Length==1 && commands[0].Contains("dir=out") && commands[0].Contains("action=block") && commands[0].Contains(exe), "firewall outbound copy only");
            FirewallRegressions(install,source);
            Check(FirewallPlan.Create(install,false)[0].Contains("delete rule"), "firewall remove");
            Directory.CreateDirectory(saves); File.WriteAllText(Path.Combine(saves,"profile.xml"),"keep");
            Installer.Uninstall(root,new string[0]);
            Check(!Directory.Exists(Path.Combine(root,"client")) && File.ReadAllText(Path.Combine(saves,"profile.xml"))=="keep", "uninstall copy only keeps saves");
            Check(File.ReadAllText(gameFile)==originalGame, "source untouched");
            string bundle=Path.Combine(temp,"bundle"); Directory.CreateDirectory(bundle);
            var payload=new Payload { Schema=1, BepInExVersion="fake", DoorstopVersion="fake", SourceRevision="test", Files=new Dictionary<string,string>() };
            foreach(string relative in new[]{"winhttp.dll","doorstop_config.ini","client.config","BepInEx/unstripped_corlib/mscorlib.dll","BepInEx/core/BepInEx.dll","BepInEx/core/BepInEx.Preloader.dll","BepInEx/core/0Harmony.dll","BepInEx/plugins/SoloStone.Plugin.dll","BepInEx/plugins/SabberStoneCore.dll"}) {
                string file=Path.Combine(bundle,"payload",relative); Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file,"fake "+relative); payload.Files[relative]=Hashes.FileHash(file);
            }
            payload.PluginSha256=payload.Files["BepInEx/plugins/SoloStone.Plugin.dll"];
            payload.EngineSha256=payload.Files["BepInEx/plugins/SabberStoneCore.dll"];
            Json.Save(Path.Combine(bundle,"payload.json"),payload);
            RecoveryRegressions(temp,source,bundle,supported,payload,saves);
            ClientIdentityTests(temp,bundle);
            ClientIdentityUninstallTests(temp);
            string endToEnd=Path.Combine(temp,"end to end");
            var wrongClient=new TestedClient { Schema=1, Version="38.0.0", Files=supported.Files };
            Refuses(()=>Installer.Setup(SetupOf(source,endToEnd,bundle,wrongClient,saves)),"setup other major");
            Check(!Directory.Exists(endToEnd),"setup block no destination");
            var otherHashes=new TestedClient { Schema=1, Version="37.1.0", Files=supported.Files.ToDictionary(r=>r.Key,r=>new string('A',64)) };
            Refuses(()=>Installer.Setup(SetupOf(source,endToEnd,bundle,otherHashes,saves)),"setup warning needs acceptance");
            Check(!Directory.Exists(endToEnd),"setup unaccepted warning no destination");
            Directory.CreateDirectory(Path.Combine(source,"BepInEx","plugins")); File.WriteAllText(Path.Combine(source,"BepInEx","plugins","other.dll"),"unsafe source mod");
            File.WriteAllText(Path.Combine(source,"client.config"),"retail config");
            File.WriteAllText(Path.Combine(source,"winhttp.dll"),"other loader");
            string sourceHash=TreeHash(source);
            var installed=Installer.Setup(SetupOf(source,endToEnd,bundle,supported,saves));
            Check(InstallRecord.Load(endToEnd).InstallId==installed.InstallId,"complete setup record and marker");
            Check(!File.Exists(Path.Combine(installed.Client,"BepInEx","plugins","other.dll")),"other mods excluded");
            Check(File.ReadAllText(Path.Combine(installed.Client,"winhttp.dll"))=="fake winhttp.dll","fresh verified loader");
            Check(TreeHash(source)==sourceHash,"setup leaves the player's Hearthstone folder byte-identical");
            installed.Payload.Verify(installed.Client);
            Check(OfflineGuard.IsIsolated(@"TESTPC\HSNative"+"UIProbe","TESTPC",@"C:\hs-native-ui-"+@"prototype\client\Hearthstone.exe",@"C:\Us"+@"ers\HSNative"+@"UIProbe\AppData\Local"),"lab guard still accepts its account");
            Check(!OfflineGuard.IsIsolated(@"TESTPC\player","TESTPC",executable: null, localData: null),"lab guard still rejects player");
            GuardDiagnosisRegressions();
#if SOLOSTONE_INSTALLER
            RuntimeAdmission(installed,endToEnd,source);
#endif
            Refuses(()=>Installer.Setup(SetupOf(source,endToEnd,bundle,supported,saves)),"repeat setup refuses overwrite");
            File.WriteAllText(Path.Combine(installed.Client,InstallRecord.Marker),"wrong");
            Refuses(()=>Installer.Uninstall(endToEnd,new string[0]),"uninstall wrong marker");
            Check(File.Exists(Path.Combine(installed.Client,"Hearthstone.exe")),"uninstall wrong marker preserves copy");
            File.WriteAllText(Path.Combine(installed.Client,InstallRecord.Marker),installed.InstallId);
            Installer.Uninstall(endToEnd,new string[0]);
            string switchFile=Path.Combine(temp,"profile-switch.xml"), run=Guid.NewGuid().ToString("D"), token=Guid.NewGuid().ToString("N");
            File.WriteAllText(switchFile,"<ProfileSwitchRequest><Version>1</Version><RunId>"+run+"</RunId><Token>"+token+"</Token><TargetId>legacy</TargetId></ProfileSwitchRequest>");
            Check(Relaunch.Read(switchFile,run,DateTime.UtcNow)==token,"exit 46 token");
            Refuses(()=>Relaunch.Read(switchFile,Guid.NewGuid().ToString("D"),DateTime.UtcNow),"foreign run token");
            File.SetLastWriteTimeUtc(switchFile,DateTime.UtcNow.AddMinutes(-10));
            Refuses(()=>Relaunch.Read(switchFile,run,DateTime.UtcNow),"stale switch request");
            Refuses(()=>Safety.Child(root,"../escape"),"payload traversal");
            Refuses(()=>Safety.Child(root,"/absolute"),"absolute payload");
            LayoutTests(temp,source,bundle,supported,payload);
            ClientPolicyTests(temp);
            MinisignTests(temp);
            ManifestTests(temp);
            NetworkTests(temp);
            EngineTests(temp);
            PluginSubsetTests(temp);
            ClientCheckTests(temp);
            Round2Tests(temp,source,bundle,supported,saves);
            BoundaryTests();
            GuiTests();
            HookTests(temp);
            RoguelikeTests(temp);
            if(skipped.Count!=0 && Environment.GetEnvironmentVariable("SOLOSTONE_REQUIRE_ALL")=="1") { Console.Error.WriteLine("FAIL: skipped with SOLOSTONE_REQUIRE_ALL=1: "+string.Join("; ",skipped)); return 1; }
            Console.WriteLine("PASS "+count+" installer checks, "+skipped.Count+" skipped"+(skipped.Count==0?"":" ("+string.Join("; ",skipped)+")")); return 0;
        } catch(Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { Directory.Delete(temp,true); }
    }
}
