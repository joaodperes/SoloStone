using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BepInEx.Logging;
using HarmonyLib;
using MiniJSON;
using UnityEngine;

internal static class NativeReportUI
{
    internal const string RowName = "SoloStoneReportIssue";
    private static ManualLogSource log;
    private static OfflineLaunchContext launch;
    private static UIBButton menuButton;
    private static NativeReportMenu popup;
    private static volatile Completion completed;
    // unseen: an export finished while the form was closed; the next open shows it (same id, COPY PATH) before starting over.
    private static bool exporting, unseen;
    private static ButtonListMenuDef fittedMenu;
    private static Vector3 nativePosition, nativeScale;
    private static ReportRedaction redaction;
    private static ReportRedaction Redaction => redaction ?? (redaction = BuildRedaction());
    private static ReportRedaction BuildRedaction()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var built = ReportRedaction.FromEnvironment(Environment.UserName, Environment.MachineName, localAppData, Environment.GetEnvironmentVariable);
#if SOLOSTONE_INSTALLER
        // The install root, the saves folder and the player's own Hearthstone folder can sit outside the profile (any drive, any folder name).
        try
        {
            if (launch != null && launch.InstalledRoot != null)
            {
                built.AddPath(SoloStone.PublicNames.SaveRoot(localAppData), "<saves>");
                built.AddPath(launch.InstalledRoot, "<solostone>");
                var record = SoloStone.InstallRecord.Load(launch.InstalledRoot);
                built.AddPath(record.Client, "<solostone>\\client");
                built.AddPath(record.Source, "<hearthstone>");
            }
        }
        catch (Exception failure) { log?.LogWarning("REPORT_ISSUE install paths not added to redaction: " + failure.GetType().Name); }
#endif
        return built;
    }
    private static ReportExport.Result result;
    private static string reportId, matchId, error, displayPath, revealMode;
    private sealed class Completion { internal ReportExport.Result Result; internal string Error; }
    internal static bool Active => popup != null && popup.IsShown();
    internal static bool Exporting => exporting;
    internal static bool CanExport => !exporting && result == null;
    // Offered after every successful export: cheap, and helps players who closed Explorer.
    internal static bool CopyVisible => result != null && !result.Pending;
    // The preview is built and verified but not saved: SAVE ZIP and BACK are offered.
    internal static bool PreviewPending => result != null && result.Pending;
    internal static bool IncludeSave { get; private set; }
    internal static string Body => NativeReportText.Guidance + "\nPaste this id in your post: " + reportId;
    internal static string Status => exporting ? "Preparing the preview..." : error != null ? "Export failed: " + error
        : result == null ? NativeReportText.SaveHint
        : result.Pending ? NativeReportText.Preview(PreviewFiles(result), result.IncludeSave, result.Replaced)
        : "Exported. Attach this zip:\n" + NativeReportText.WrapPath(displayPath);
    private static List<KeyValuePair<string, long>> PreviewFiles(ReportExport.Result preview)
    {
        var files = new List<KeyValuePair<string, long>>();
        foreach (object entry in preview.Entries)
            if (entry is Dictionary<string, object> row && row.TryGetValue("name", out object name) && row.TryGetValue("size", out object size))
                files.Add(new KeyValuePair<string, long>((string)name, Convert.ToInt64(size)));
        return files;
    }
    internal static IEnumerable<KeyValuePair<PegUIElement, string>> Controls
    {
        get
        {
            if (menuButton != null) yield return new KeyValuePair<PegUIElement, string>(menuButton, "menu.report-issue");
            if (popup != null) foreach (var control in popup.Controls) yield return control;
        }
    }
    internal static JsonNode State()
    {
        var state = result?.ToJson() ?? new JsonNode();
        state["reportId"] = reportId; state["active"] = Active; state["exporting"] = exporting;
        state["includeSave"] = IncludeSave; state["error"] = error; state["revealMode"] = revealMode;
        state["displayPath"] = displayPath; state["discordEnabled"] = NativeReportText.DiscordEnabled;
        return state;
    }
    internal static void Initialize(Harmony harmony, ManualLogSource logger, OfflineLaunchContext context)
    {
        log = logger; launch = context;
        harmony.Patch(AccessTools.Method(typeof(GameMenu), "Awake"), postfix: new HarmonyMethod(typeof(NativeReportUI), nameof(AddRow)));
        harmony.Patch(AccessTools.Method(typeof(GameMenu), "GetButtons"), postfix: new HarmonyMethod(typeof(NativeReportUI), nameof(ListRow)));
        harmony.Patch(AccessTools.Method(typeof(GameMenu), "Show"), prefix: new HarmonyMethod(typeof(NativeReportUI), nameof(PrepareMenu)));
        harmony.Patch(AccessTools.Method(typeof(GameMenu), "LayoutMenu"), postfix: new HarmonyMethod(typeof(NativeReportUI), nameof(FitMenu)) { priority = Priority.Last });
        harmony.Patch(AccessTools.Method(typeof(BnetBar), "HandleEscapeKey"), prefix: new HarmonyMethod(typeof(NativeReportUI), nameof(BeforeEscape)) { priority = Priority.First });
    }
    private static bool BeforeEscape(ref bool __result) { if (!Active) return true; Close(); __result = true; return false; }
    private static void PrepareMenu(GameMenu __instance) => Guard(() => {
        AccessTools.Field(typeof(ButtonListMenu), "NORMAL_SCALE").SetValue(__instance, Vector3.one);
        // Re-fit from the native placement on every show: a resolution change (Deck docked/handheld) keeps no old offset.
        var menu = AccessTools.Field(typeof(ButtonListMenu), "m_menu").GetValue(__instance) as ButtonListMenuDef;
        if (menu != null && menu == fittedMenu) { menu.transform.localPosition = nativePosition; menu.transform.localScale = nativeScale; }
    });
    internal static void Guard(Action action)
    {
        try { action(); } catch (Exception failure) { Failed(failure); }
    }
    internal static void Failed(Exception failure) => log?.LogWarning("REPORT_ISSUE " + Safe(failure.GetType().Name + ": " + failure.Message));
    // Worker/handler errors reach the log, the on-screen status, ui state and hslive: redact them first.
    private static string Safe(string text)
    {
        try { return Redaction.Redact(text); } catch (Exception) { return "(error text withheld: redaction failed)"; }
    }
    private static void AddRow(GameMenu __instance) => Guard(() => {
        menuButton = null;
        menuButton = __instance.CreateMenuButton(RowName, "Report Issue", _ => Guard(() => {
            // Native menu hide and form show do not consult GameState or engine work.
            var bar = BnetBar.Get();
            if (bar != null) bar.HideGameMenu(); else __instance.GameMenuHide();
            Open();
        }));
        menuButton.SetText("Report Issue");
    });
    private static void ListRow(GameMenu __instance, ref List<UIBButton> __result)
    {
        try
        {
            if (menuButton == null || __result == null || __result.Contains(menuButton) || !menuButton.transform.IsChildOf(__instance.transform.root)) return;
            int quit = __result.IndexOf(AccessTools.Field(typeof(GameMenu), "m_quitButton").GetValue(__instance) as UIBButton);
            __result.Insert(quit < 0 ? __result.Count : quit, menuButton);
        }
        catch (Exception failure) { log?.LogWarning("REPORT_ISSUE menu: " + failure.Message); }
    }
    private static void FitMenu(GameMenu __instance) => Guard(() => {
        if (menuButton == null || !menuButton.gameObject.activeInHierarchy) return;
        var menu = AccessTools.Field(typeof(ButtonListMenu), "m_menu").GetValue(__instance) as ButtonListMenuDef;
        if (menu == null) return;
        if (menu != fittedMenu) { fittedMenu = menu; nativePosition = menu.transform.localPosition; nativeScale = menu.transform.localScale; }
        var camera = CameraUtils.FindFirstByLayerMask(1 << menu.gameObject.layer);
        if (camera == null) return;
        Rect rect = DeckLayout.ScreenRect(menu.gameObject, camera);
        if (rect.width <= 0 || rect.height <= 0) return;
        menu.transform.localScale *= Math.Min(1f, Math.Min(Screen.width * .9f / rect.width, Screen.height * .9f / rect.height));
        // Show's native punch animation runs after LayoutMenu and otherwise restores scale=1 over this fit.
        AccessTools.Field(typeof(ButtonListMenu), "NORMAL_SCALE").SetValue(__instance, menu.transform.localScale);
        rect = DeckLayout.ScreenRect(menu.gameObject, camera);
        float x = Mathf.Clamp(rect.center.x, Screen.width * .05f + rect.width / 2, Screen.width * .95f - rect.width / 2);
        float y = Mathf.Clamp(rect.center.y, Screen.height * .05f + rect.height / 2, Screen.height * .95f - rect.height / 2);
        Vector3 at = camera.WorldToScreenPoint(menu.transform.position);
        menu.transform.position += camera.ScreenToWorldPoint(new Vector3(x, y, at.z)) - camera.ScreenToWorldPoint(new Vector3(rect.center.x, rect.center.y, at.z));
    });
    private static void Open()
    {
        if (popup == null) popup = new GameObject("SoloStoneReportDialog").AddComponent<NativeReportMenu>();
        // An in-flight export keeps its save choice; a result finished while closed is shown once before starting over.
        if (exporting || unseen) { unseen = false; popup.Open(); return; }
        DiscardPreview(); // a preview left behind by a form hidden some other way
        IncludeSave = false;
        matchId = ClientBridge.DiagnosticMatchId ?? LocalLadder.MatchId;
        reportId = NativeReportText.ReportId(DateTime.UtcNow, matchId);
        result = null; error = displayPath = revealMode = null;
        popup.Open();
    }
    internal static void ToggleSave() => Guard(() => { if (CanExport) { IncludeSave = !IncludeSave; popup.Refresh(); } });
    internal static void Close() => Guard(() => { DiscardPreview(); popup?.Hide(); });
    // Closing with an unsaved preview throws it away: only a saved zip is a report.
    private static void DiscardPreview()
    {
        if (!PreviewPending) return;
        ReportExport.Discard(result); result = null; error = displayPath = revealMode = null; unseen = false;
    }
    internal static void Back() => Guard(() => { if (PreviewPending && !exporting) { DiscardPreview(); popup.Refresh(); } });
    internal static void Save() => Guard(() => {
        if (!PreviewPending || exporting) return;
        try { ReportExport.Commit(result); }
        catch (Exception failure) { error = Safe(failure.GetType().Name + ": " + failure.Message); Failed(failure); popup.Refresh(); return; }
        bool wine = Wine.IsWine, dryRun = NativeReportText.DryRun(launch.LaneId);
        displayPath = wine ? Wine.UnixPath(result.ZipPath) : result.ZipPath;
        revealMode = wine ? "copy-path" : dryRun ? "dry-run" : "explorer";
        if (!wine && dryRun) log?.LogInfo("REPORT_ISSUE dry-run reveal " + result.ZipPath);
        if (!wine && !dryRun)
        {
            try
            {
                // explorer.exe /select normally exits 1 even on success: only an exception or no process means fallback.
                using (var process = Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + result.ZipPath + "\"") { UseShellExecute = true }))
                    if (process == null) revealMode = "copy-path";
            }
            catch { revealMode = "copy-path"; }
        }
        popup.Refresh();
    });
    internal static void CopyPath() => Guard(() => { if (CopyVisible) GUIUtility.systemCopyBuffer = displayPath ?? result.ZipPath; });
    internal static void Discord() => Guard(() => {
        if (!NativeReportText.DiscordEnabled) return;
        if (!NativeHelpText.OpensBrowser(launch.LaneId)) log?.LogInfo("REPORT_ISSUE dry-run OpenURL " + NativeReportText.BugReportsUrl);
        else Application.OpenURL(NativeReportText.BugReportsUrl);
    });
    // Scoped to the native RELEASE, never a sticky test-mode flag affecting a player's later click.
    internal static void TestRelease(Action release) => NativeReportText.TestRelease(release);
    internal static void Export()
    {
        try { BeginExport(); }
        catch (Exception failure)
        {
            exporting = false; error = Safe(failure.GetType().Name + ": " + failure.Message);
            Guard(() => popup?.Refresh()); Failed(failure);
        }
    }
    private static void BeginExport()
    {
        if (!CanExport) return;
        string profile = Environment.GetEnvironmentVariable("USERPROFILE");
        var request = new ReportExport.Request {
            Reports = launch.Reports, ProfilePath = ClientBridge.DiagnosticProfilePath ?? launch.ProfilePath,
            BepInExLog = Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput.log"),
            PlayerLog = PlayerLogPath(profile, Application.productName),
            PluginDll = typeof(OfflineProbe).Assembly.Location, EngineDll = typeof(SabberStoneCore.Model.Game).Assembly.Location,
            RuntimeManifest = Path.Combine(launch.Root, "tools", "Runtime-Manifest.json"),
            Revision = launch.Deck ? NativeReportText.DeckLabelPath(Environment.GetEnvironmentVariable("WINEHOMEDIR"), Environment.GetEnvironmentVariable("HOME"))
                : Path.Combine(launch.Root, "REVISION"),
            ReportId = reportId, MatchId = matchId, Utc = DateTime.UtcNow, IncludeSave = IncludeSave, Preview = true,
            Redaction = Redaction,
            Metadata = new Dictionary<string, object> {
                { "buildVersion", typeof(OfflineProbe).Assembly.GetName().Version.ToString() },
                { "buildLabel", launch.SnapshotId ?? "SoloStone" }, { "os", Environment.OSVersion.ToString() },
                { "unityOs", SystemInfo.operatingSystem }, { "deck", launch.Deck }, { "lane", launch.LaneId }
            }
        };
        exporting = true;
        try
        {
            popup.Refresh();
            // Dedicated background thread, not the ThreadPool, so lethal-search Task.Run workers cannot starve it.
            var worker = new Thread(() => {
                var done = new Completion();
                try
                {
                    bool wine = Wine.IsWine;
                    request.Metadata["wine"] = wine; request.Metadata["wineVersion"] = Wine.Version();
                    request.Metadata["wineHost"] = Wine.HostVersion();
                    done.Result = ReportExport.Export(request);
                }
                catch (Exception failure) { done.Error = Safe(failure.GetType().Name + ": " + failure.Message); }
                completed = done;
            }) { IsBackground = true, Name = "SoloStoneReportExport" };
            try { worker.Priority = System.Threading.ThreadPriority.BelowNormal; } catch (Exception) { } // best effort on Mono/Wine
            worker.Start();
        }
        catch { exporting = false; throw; }
    }
    // Before ClientBridge.Update, so a stalled bridge never owns completion delivery.
    // Also applies deferred form rebuilds, so a row is never destroyed inside its own release handler.
    internal static void Update() => Guard(() => {
        var done = completed;
        if (done != null)
        {
            completed = null; exporting = false; result = done.Result; error = done.Error;
            if (error != null) log?.LogWarning("REPORT_ISSUE export failed: " + error);
            if (Active) { unseen = false; popup.Refresh(); } else unseen = true;
        }
        popup?.ApplyRefresh();
    });
    private static string PlayerLogPath(string profile, string product)
    {
        // This pinned Unity lacks consoleLogPath. Launchers use -logFile; honor it before the normal LocalLow path.
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++) if (string.Equals(args[i], "-logFile", StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        // companyName is also absent in the reference subset; this pinned client's native company is fixed.
        return string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, "AppData", "LocalLow", "Blizzard Entertainment", product, "Player.log");
    }
    private static class Wine
    {
        [DllImport("kernel32", CharSet = CharSet.Ansi)] private static extern IntPtr GetModuleHandleA(string name);
        [DllImport("kernel32", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32")] private static extern bool HeapFree(IntPtr heap, uint flags, IntPtr pointer);
        [DllImport("kernel32")] private static extern IntPtr GetProcessHeap();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GetVersion();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void GetHost(out IntPtr system, out IntPtr release);
        [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)] private delegate IntPtr GetUnix([MarshalAs(UnmanagedType.LPWStr)] string path);
        private static IntPtr Find(string module, string name) { var handle = GetModuleHandleA(module); return handle == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(handle, name); }
        internal static bool IsWine { get { try { return Find("ntdll.dll", "wine_get_version") != IntPtr.Zero; } catch { return false; } } }
        internal static string Version() { try { var address = Find("ntdll.dll", "wine_get_version"); return address == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(((GetVersion)Marshal.GetDelegateForFunctionPointer(address, typeof(GetVersion)))()); } catch { return null; } }
        internal static string HostVersion()
        {
            try { var address = Find("ntdll.dll", "wine_get_host_version"); if (address == IntPtr.Zero) return null;
                ((GetHost)Marshal.GetDelegateForFunctionPointer(address, typeof(GetHost)))(out IntPtr system, out IntPtr release);
                return Marshal.PtrToStringAnsi(system) + " " + Marshal.PtrToStringAnsi(release); } catch { return null; }
        }
        internal static string UnixPath(string path)
        {
            IntPtr value = IntPtr.Zero;
            try { var address = Find("kernel32.dll", "wine_get_unix_file_name"); if (address == IntPtr.Zero) return path;
                value = ((GetUnix)Marshal.GetDelegateForFunctionPointer(address, typeof(GetUnix)))(path);
                if (value == IntPtr.Zero) return path;
                // The Unix name is UTF-8, not the ANSI code page.
                int length = 0; while (Marshal.ReadByte(value, length) != 0) length++;
                var bytes = new byte[length]; Marshal.Copy(value, bytes, 0, length);
                return Encoding.UTF8.GetString(bytes); }
            catch { return path; }
            finally { if (value != IntPtr.Zero) HeapFree(GetProcessHeap(), 0, value); }
        }
    }
}

// The same native menu board/planks used by the profile form, with native UberText slices for guidance/status.
internal sealed class NativeReportMenu : ButtonListMenu
{
    private readonly List<UIBButton> rows = new List<UIBButton>();
    private readonly Dictionary<PegUIElement, string> controls = new Dictionary<PegUIElement, string>();
    private readonly List<GameObject> owned = new List<GameObject>();
    private readonly List<KeyValuePair<GameObject, bool>> slices = new List<KeyValuePair<GameObject, bool>>();
    private bool ownsDialog;
    internal IEnumerable<KeyValuePair<PegUIElement, string>> Controls => controls;
    protected override void Awake() => NativeReportUI.Guard(() => { m_showAnimation = false; base.Awake(); Hide(); });
    protected override List<UIBButton> GetButtons() => rows;
    internal void Open() { dirty = false; Build(); Show(); }
    private bool dirty;
    // Deferred to NativeReportUI.Update: the pressed row's release handler may still be on the stack.
    internal void Refresh() => dirty = true;
    internal void ApplyRefresh()
    {
        if (!dirty) return;
        dirty = false; bool shown = IsShown(); if (shown) Hide(); Build(); if (shown) Show(false);
    }
    private UIBButton Row(string id, string text, Action action, bool enabled = true)
    {
        var row = CreateMenuButton(id, text, _ => { try { action(); } catch { } });
        row.SetText(text);
        row.SetEnabled(enabled); if (!enabled) NativeButtonTint.Apply(row, NativeButtonTint.Tint.Grey);
        rows.Add(row); owned.Add(row.gameObject); controls[row] = id; slices.Add(new KeyValuePair<GameObject, bool>(row.gameObject, false));
        return row;
    }
    private void Text(string value, UIBButton template)
    {
        var source = ProfileFormKit.Label(template);
        if (source == null) throw new InvalidOperationException("Native report label template missing");
        var copy = UnityEngine.Object.Instantiate(source.gameObject, m_menu.m_buttonContainer.transform, true);
        copy.transform.localPosition = Vector3.zero; copy.transform.localScale *= .7f;
        var text = copy.GetComponent(ProfileFormKit.TextType);
        ProfileFormKit.ScaleRect(text, "Width", 1.65f); ProfileFormKit.ScaleRect(text, "Height", 4f);
        ProfileFormKit.SetText(text, value); ProfileFormKit.SetColor(text, new Color(.22f, .15f, .09f, 1));
        ProfileFormKit.UpdateNow(text);
        owned.Add(copy); slices.Add(new KeyValuePair<GameObject, bool>(copy, true));
    }
    private void Build()
    {
        var all = AccessTools.Field(typeof(ButtonListMenu), "m_allButtons").GetValue(this) as List<UIBButton>;
        all?.Clear();
        foreach (var item in owned) if (item != null) { item.SetActive(false); UnityEngine.Object.Destroy(item); }
        owned.Clear(); rows.Clear(); controls.Clear(); slices.Clear();
        var header = AccessTools.Field(typeof(ButtonListMenuDef), "m_headerText").GetValue(m_menu) as Component;
        ProfileFormKit.SetText(header, NativeReportText.Header);
        var toggle = Row("report.include-save", NativeReportText.IncludeSaveLabel(NativeReportUI.IncludeSave),
            NativeReportUI.ToggleSave, NativeReportUI.CanExport);
        // The profile form's native plank-toggle fallback is synchronous, including on a frozen presentation queue.
        Text(NativeReportUI.Body, toggle);
        var guidance = slices[slices.Count - 1]; slices.RemoveAt(slices.Count - 1); slices.Insert(0, guidance);
        // Preview first: SAVE ZIP writes exactly what the preview verified; BACK or closing throws it away.
        if (NativeReportUI.PreviewPending)
        {
            Row("report.save", NativeReportText.SaveLabel, NativeReportUI.Save);
            Row("report.back", NativeReportText.BackLabel, NativeReportUI.Back);
        }
        else if (NativeReportUI.CanExport || NativeReportUI.Exporting)
            Row("report.export", NativeReportUI.Exporting ? "Preparing..." : NativeReportText.PreviewLabel, NativeReportUI.Export,
                NativeReportUI.CanExport);
        Row("report.discord", NativeReportText.DiscordLabel, NativeReportUI.Discord, NativeReportText.DiscordEnabled);
        Text(NativeReportUI.Status, toggle);
        if (NativeReportUI.CopyVisible) Row("report.copy-path", "COPY PATH", NativeReportUI.CopyPath);
        Row("report.close", "CLOSE", NativeReportUI.Close);
    }
    public override void Show(bool playSound = true)
    {
        if (ownsDialog) return;
        int before = DeckExperience.GameDialogCount;
        try { base.Show(playSound); ownsDialog = true; }
        catch (Exception failure) { if (DeckExperience.GameDialogCount > before) { ownsDialog = true; Hide(); } else gameObject.SetActive(false); NativeReportUI.Failed(failure); }
    }
    public override void Hide()
    {
        if (!ownsDialog) { gameObject.SetActive(false); return; }
        try { NativeReportUI.Guard(() => base.Hide()); } finally { ownsDialog = false; }
    }
    protected override void OnDestroy() => NativeReportUI.Guard(() => { Hide(); base.OnDestroy(); });
    protected override void OnBlockerRelease(UIEvent e) => NativeReportUI.Close();
    protected override void LayoutMenu() => NativeReportUI.Guard(LayoutReport);
    private void LayoutReport()
    {
        var container = m_menu.m_buttonContainer; container.ClearSlices();
        foreach (var slice in slices)
        {
            slice.Key.SetActive(true); // Native Show hides every cached plank immediately before LayoutMenu.
            container.AddSlice(slice.Key, Vector3.zero, Vector3.zero, false, true, false, slice.Value);
        }
        container.UpdateSlices(); LayoutMenuBackground();
        var camera = CameraUtils.FindFirstByLayerMask(1 << m_menu.gameObject.layer);
        if (camera == null) return;
        // Native background sizing omits caption text bounds; grow it to cover the text slices too.
        Rect frame = DeckLayout.ScreenRect(m_menu.m_background.gameObject, camera);
        float bottom = frame.yMin;
        foreach (var slice in slices) bottom = Math.Min(bottom, DeckLayout.ScreenRect(slice.Key, camera).yMin);
        if (bottom < frame.yMin)
        {
            Vector3 atFrame = camera.WorldToScreenPoint(m_menu.m_background.transform.position);
            float worldPerPixel = (camera.ScreenToWorldPoint(atFrame + Vector3.up) - camera.ScreenToWorldPoint(atFrame)).magnitude;
            OrientedBounds bounds = TransformUtil.ComputeOrientedWorldBounds(container.gameObject, default, default, null, true, true, true);
            float height = bounds.Extents[2].magnitude * 2 + (frame.yMin - bottom) * worldPerPixel;
            m_menu.m_background.SetSize(bounds.Extents[0].magnitude * 2, height);
            m_menu.m_border.SetSize(bounds.Extents[0].magnitude * 2, height);
            float shift = frame.yMax - DeckLayout.ScreenRect(m_menu.m_background.gameObject, camera).yMax;
            Vector3 move = camera.ScreenToWorldPoint(atFrame + Vector3.up * shift) - camera.ScreenToWorldPoint(atFrame);
            m_menu.m_background.transform.position += move; m_menu.m_border.transform.position += move;
        }
        Rect rect = DeckLayout.ScreenRect(m_menu.gameObject, camera);
        if (rect.width <= 0 || rect.height <= 0) return;
        float scale = Math.Min(Screen.width * .86f / rect.width, Screen.height * .86f / rect.height);
        m_menu.transform.localScale *= scale;
        rect = DeckLayout.ScreenRect(m_menu.gameObject, camera);
        var at = camera.WorldToScreenPoint(m_menu.transform.position);
        m_menu.transform.position += camera.ScreenToWorldPoint(new Vector3(Screen.width / 2f, Screen.height / 2f, at.z))
            - camera.ScreenToWorldPoint(new Vector3(rect.center.x, rect.center.y, at.z));
    }
}
