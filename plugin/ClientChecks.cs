using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

// V1 and V2 on the real client, and the save hold they guard (release plan R2). Nothing may create, select, restore, migrate, grant or recover a
// save before both pass, on any version. Exit 42 on a failure: this release cannot run on this client. Its saves were not touched.
internal static class ClientChecks
{
    internal const int ExitBlocked = 42;
    // A stuck load says nothing about this release, so it must not hold the release for good as exit 42 does.
#if SOLOSTONE_INSTALLER
    internal const int ExitStuck = InstallerRuntime.ExitStartupError;
#else
    internal const int ExitStuck = ExitBlocked;
#endif
    internal enum Step { Waiting, Passed, Failed, Stuck }

    private static string reports, runId;
    private static ManualLogSource log;
    private static bool armed;
    private static float deadline;
    private static string[] rows;
    private static string plantedCard;

    // One-shot, for a live test on the real client: a planted row or card must block like a real one. Absent from a release build.
    private static void ReadPlant(string reportsFolder, ref List<string> extraRows)
    {
#if !SOLOSTONE_INSTALLER || SOLOSTONE_TEST_HOOKS
        string path = Path.Combine(reportsFolder, "client-check-plant.txt");
        try
        {
            if (!File.Exists(path)) return;
            string[] lines = File.ReadAllLines(path);
            File.Delete(path);
            foreach (string line in lines)
            {
                if (line.StartsWith("hook=", StringComparison.Ordinal)) extraRows.Add("M\t*\t" + line.Substring(5) + "\tPlanted\t*");
                else if (line.StartsWith("card=", StringComparison.Ordinal)) plantedCard = line.Substring(5);
            }
        }
        catch (IOException) { }
#endif
    }

    // V1. Null: passed. Runs before any hook or save is touched.
    internal static string CheckBindings(string reportsFolder, string run, ManualLogSource logger)
    {
        reports = reportsFolder; runId = run; log = logger;
        ClientCheckReport.Write(reports, runId, ClientCheckReport.Held, "bindings", null);
        rows = ClientBindings.ReadRows(typeof(ClientChecks).Assembly.GetManifestResourceStream(ClientBindings.Resource));
        var planted = new List<string>();
        ReadPlant(reports, ref planted);
        var missing = ClientBindings.Missing(rows.Concat(planted), RunningClient());
        log.LogInfo("CLIENT_CHECK bindings rows=" + rows.Length + " missing=" + missing.Count);
        if (missing.Count == 0) return null;
        foreach (string target in missing) log.LogFatal("CLIENT_CHECK missing " + target);
        string detail = ClientBindings.Summary("client code targets", missing);
        ClientCheckReport.Write(reports, runId, ClientCheckReport.Refused, "bindings", detail);
        return detail;
    }

    private static ClientBindings.Client RunningClient() => new ClientBindings.Client
    {
        Assembly = name => { try { return Assembly.Load(new AssemblyName(name)) != null; } catch (Exception error) when (error is IOException || error is BadImageFormatException) { return false; } },
        Type = (assembly, name) => {
            if (assembly == "*") { LoadClientAssemblies(); return GameTypes.ByName(name); }
            try { return Assembly.Load(new AssemblyName(assembly)).GetType(name, false); }
            catch (Exception error) when (error is IOException || error is BadImageFormatException) { return null; }
        },
        Iterator = method => AccessTools.EnumeratorMoveNext(method) != null
    };

    // A row with no assembly names a type outside references/game, and GameTypes only asks what is already loaded this early in startup.
    private static bool clientLoaded;
    private static void LoadClientAssemblies()
    {
        if (clientLoaded) return;
        clientLoaded = true;
        var pending = new Queue<AssemblyName>(new[] { new AssemblyName("Assembly-CSharp") });
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count != 0)
        {
            AssemblyName name = pending.Dequeue();
            if (!seen.Add(name.Name) || name.Name.StartsWith("System", StringComparison.Ordinal) || name.Name == "mscorlib" || name.Name == "netstandard") continue;
            try { foreach (AssemblyName child in Assembly.Load(name).GetReferencedAssemblies()) pending.Enqueue(child); }
            catch (Exception error) when (error is IOException || error is BadImageFormatException) { }
        }
    }

    // Awake finished without a block: V2 waits for the client's card records, and the save hold waits for V2.
    internal static void ArmCardCheck(float now)
    {
        armed = true; deadline = now + ClientCheckReport.CardWaitSeconds;
        ClientCheckReport.Write(reports, runId, ClientCheckReport.Held, "cards", null);
    }

    internal static Step Tick(float now)
    {
        if (!armed) return Step.Waiting;
        if (!GameDbf.IsLoaded || DefLoader.Get() == null || !DefLoader.Get().HasLoadedEntityDefs() || GameDbf.GetIndex() == null)
        {
            if (now < deadline) return Step.Waiting;
            armed = false;
            Refuse("cards", "The client did not finish loading its cards.");
            return Step.Stuck;
        }
        armed = false;
        // GameDbfIndex answers null for an unknown id. DefLoader.GetEntityDef would raise a client fatal error for one.
        var index = GameDbf.GetIndex();
        var pool = LocalCardCatalog.ActivePoolIds();
        if (plantedCard != null) pool = pool.Concat(new[] { plantedCard }).ToArray();
        if (pool.Count == 0) return Refuse("cards", "This SoloStone build has no card pool.");
        var missing = ClientBindings.MissingCards(pool, id => index.GetCardRecord(id) != null);
        log.LogInfo("CLIENT_CHECK cards pool=" + pool.Count + " missing=" + missing.Count);
        if (missing.Count != 0)
        {
            foreach (string id in missing) log.LogFatal("CLIENT_CHECK missing card " + id);
            return Refuse("cards", ClientBindings.Summary("cards", missing));
        }
        return Step.Passed;
    }

    internal static void ReleaseCardCheck() => ClientCheckReport.Write(reports, runId, ClientCheckReport.Released, "cards", null);

    internal static Step Refuse(string check, string detail)
    {
        log.LogFatal("CLIENT_CHECK refused " + check + ": " + detail);
        ClientCheckReport.Write(reports, runId, ClientCheckReport.Refused, check, detail);
        return Step.Failed;
    }

    private static readonly HashSet<MethodBase> selfAdapting = new HashSet<MethodBase>();
    // A patch from a name filter that observes whatever exists. A retail rename must not block a player over it: see NativeResultsUI.Initialize.
    internal static void NoteSelfAdapting(MethodBase method) => selfAdapting.Add(method);

    // Every patch registered so far against the manifest, for tools. A failure here says nothing about the client, so it can never block.
    internal static void ReportCoverage()
    {
        try
        {
            if (rows == null) return;
            var patched = Harmony.GetAllPatchedMethods().ToArray();
            ICollection<MethodBase> notRequired = selfAdapting;
            string counts = "patched=" + patched.Length + " selfAdapting=" + selfAdapting.Count;
#if !SOLOSTONE_RELEASE
            var labOnly = ClientBindings.LabOnly(patched, method => Harmony.GetPatchInfo(method)?.Owners);
            notRequired = new HashSet<MethodBase>(selfAdapting.Concat(labOnly));
            counts += " labOnly=" + labOnly.Count;
#endif
            var uncovered = ClientBindings.Uncovered(patched, rows, notRequired);
            string line = counts + " uncovered=" + uncovered.Count;
            log.LogInfo("CLIENT_CHECK patch coverage " + line + (uncovered.Count == 0 ? "" : " " + string.Join(", ", uncovered.ToArray())));
            File.WriteAllLines(Path.Combine(reports, ClientCheckReport.CoverageFile), new[] { "runId=" + runId, line }.Concat(uncovered).ToArray());
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { log.LogWarning("CLIENT_CHECK patch coverage not written: " + error.Message); }
    }
}
