using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Assets;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Hearthstone.UI;

// Main-menu theme: one native expansion/holiday "box dressing" on the hub, chosen in the shop's Themes tab (owner 2026-10-25).
// Retail picks the dressing as the BOX_DRESSING row whose EVENT_TIMING is active (BoxDressingManager.GetCurrentBoxDressing),
// and Box.ShowEventBoxDressing waits until server event timings arrive. Offline they never arrive, so the hub always shows
// the classic box. This answers the record from the launch theme and lifts the "timings received" gate only for the
// duration of that one native call; no global event timing, clock or server state is faked.
// The theme is fixed per launch: the profile's selection, or for "Random owned" one owned theme rolled at launch.
// Classic -> theme can be dressed live; theme -> theme or theme -> Classic needs a restart (live re-dress leaves the
// previous dressing's destroyed materials on the box), so the shop says the change appears at the next start.
internal static class HubTheme
{
    private const float PollSeconds = 2f;
    private static ManualLogSource log;
    private static ConfigFile config;
    private static ConfigEntry<string> overrideTheme, lastTheme;
    private static PropertyInfo received;
    private static FieldInfo dressingActive;
    private static DateTime stamp;
    private static float next;
    private static bool forcing, catalogLogged, active, lateDressed;
    private static string launch, shown = "", overrideApplied = "";

    // The theme this launch shows (a ThemeKeys value), or null until the profile is available.
    internal static string LaunchTheme => launch;

    internal static void Initialize(Harmony harmony, ManualLogSource logger, ConfigFile file)
    {
        log = logger;
        config = file;
#if !SOLOSTONE_RELEASE
        overrideTheme = file.Bind("Hub", "Theme", "",
            "Verification aid: a native box dressing name (e.g. VIOLET, CATA, HALLOWS_END) shown instead of the player's theme. Empty = the theme chosen in the shop.");
#endif
        lastTheme = file.Bind("Hub", "LastLaunchTheme", "",
            "Managed by SoloStone: the theme the previous launch showed, so \"Random owned\" re-rolls to a different look.");
        overrideApplied = overrideTheme?.Value ?? ""; // no Hub.Theme override in a public build (I2c)
#if !SOLOSTONE_RELEASE
        TestControlShop.LaunchHubTheme = () => launch;
#endif
        try
        {
            received = AccessTools.Property(typeof(EventTimingManager), "HasReceivedEventTimingsFromServer");
            dressingActive = AccessTools.Field(typeof(Box), "m_eventBoxDressingActive");
            harmony.Patch(AccessTools.Method(typeof(BoxDressingManager), "GetCurrentBoxDressing"),
                prefix: new HarmonyMethod(typeof(HubTheme), nameof(CurrentDressing)));
            harmony.Patch(AccessTools.Method(typeof(Box), "ShowEventBoxDressing"),
                prefix: new HarmonyMethod(typeof(HubTheme), nameof(BeforeShow)),
                postfix: new HarmonyMethod(typeof(HubTheme), nameof(AfterShow)));
            stamp = File.GetLastWriteTimeUtc(file.ConfigFilePath);
            active = true;
            log.LogInfo("HUB_THEME installed override=" + (overrideApplied.Length == 0 ? "(none)" : overrideApplied));
        }
        catch (Exception error) { log.LogWarning("HUB_THEME hooks failed; classic box only: " + error); }
    }

    // Resolve once per launch, as soon as the profile is loaded. Never throws.
    private static string Resolve()
    {
        if (launch != null) return launch;
        try
        {
            var profile = LocalLadder.Profile;
            if (profile == null) return null;
            string selected = profile.SelectedHubTheme;
            string previous = lastTheme?.Value ?? "";
            launch = selected == LocalHubCosmetics.RandomTheme
                ? LocalHubCosmetics.RollTheme(profile.UsableHubThemes, previous, new Random().Next())
                : LocalHubCosmetics.KnownTheme(selected) && profile.OwnsHubTheme(selected) ? selected : LocalHubCosmetics.ClassicTheme;
            if (lastTheme != null && lastTheme.Value != launch) lastTheme.Value = launch;
            log.LogInfo("HUB_THEME launch theme=" + launch + " selected=" + selected + " previous=" + (previous.Length == 0 ? "(none)" : previous));
        }
        catch (Exception error) { launch = LocalHubCosmetics.ClassicTheme; log.LogWarning("HUB_THEME resolve failed; classic box " + error); }
        return launch;
    }

    private static bool TryTheme(out BoxDressing.BoxDressingType type)
    {
        type = default(BoxDressing.BoxDressingType);
        string value = (overrideTheme?.Value ?? "").Trim();
        if (value.Length == 0) value = Resolve() ?? "";
        if (value.Length == 0 || value == LocalHubCosmetics.ClassicTheme || value == "TEST_EVENT") return false;
        try { type = (BoxDressing.BoxDressingType)Enum.Parse(typeof(BoxDressing.BoxDressingType), value, true); }
        catch (Exception) { return false; }
        return Enum.IsDefined(typeof(BoxDressing.BoxDressingType), type);
    }

    private static bool CurrentDressing(ref BoxDressingDbfRecord __result)
    {
        try
        {
            if (!TryTheme(out var type)) return true;
            BoxDressingDbfRecord record = null;
            try { record = GameDbf.BoxDressing?.GetRecords().FirstOrDefault(row => row.BoxDressingType == type); }
            catch (Exception) { record = null; }
            if (record == null)
            {
                record = new BoxDressingDbfRecord();
                record.SetVar("BOX_DRESSING_TYPE", type);
            }
            __result = record;
            return false;
        }
        catch (Exception error) { log?.LogWarning("HUB_THEME dressing lookup failed " + error); return true; }
    }

    private static void BeforeShow()
    {
        try
        {
            if (!TryTheme(out _)) return;
            var manager = EventTimingManager.Get();
            if (manager == null || manager.HasReceivedEventTimingsFromServer) return;
            received.SetValue(manager, true, null);
            forcing = true;
        }
        catch (Exception error) { log?.LogWarning("HUB_THEME gate lift failed " + error); }
    }

    // Postfix, not finalizer: if the native call throws, Tick restores the gate on its next poll.
    private static void AfterShow() { RestoreGate(); }

    private static void RestoreGate()
    {
        if (!forcing) return;
        forcing = false;
        try { received.SetValue(EventTimingManager.Get(), false, null); }
        catch (Exception error) { log.LogWarning("HUB_THEME restore failed " + error); }
    }

    private static bool Dressed(Box box) => dressingActive != null && box != null && (bool)dressingActive.GetValue(box);

    // The shop equipped a theme. True when the box shows it now or once the shop closes (a classic box takes a theme
    // live); false when it appears only at the next start (re-dressing or undressing needs a restart).
    internal static bool Equipped(string key)
    {
        try
        {
            if (!active || (overrideTheme?.Value ?? "").Trim().Length != 0) return false;
            string current = Resolve();
            if (current == null) return false;
            if (key == current) return true;
            if (current != LocalHubCosmetics.ClassicTheme || key == LocalHubCosmetics.RandomTheme
                || key == LocalHubCosmetics.ClassicTheme || !LocalHubCosmetics.KnownTheme(key) || Dressed(Box.Get())) return false;
            // Shop.Open hid any dressing; closing it runs UpdateEventBoxDressingWithConfig, which dresses the box.
            launch = key;
            if (lastTheme != null) lastTheme.Value = key;
            log.LogInfo("HUB_THEME live classic -> " + key);
            return true;
        }
        catch (Exception error) { log?.LogWarning("HUB_THEME equip failed " + error); return false; }
    }

    internal static void Tick(float now)
    {
        if (!active || now < next) return;
        next = now + PollSeconds;
        if (forcing) { log.LogWarning("HUB_THEME native call did not complete; restoring gate"); RestoreGate(); }
        try
        {
            var scene = SceneMgr.Get();
            var box = Box.Get();
            if (scene == null || box == null || scene.GetMode() != SceneMgr.Mode.HUB) return;
            if (!catalogLogged) { catalogLogged = true; LogCatalog(box); }
            bool store = StoreManager.Get()?.IsShownOrWaitingToShow() == true;
            // The profile can load after the first dressing call: a classic box takes the launch theme once at HUB.
            if (!lateDressed && Resolve() != null && !store && !Dressed(box) && TryTheme(out _) && shown.StartsWith("none", StringComparison.Ordinal))
            {
                lateDressed = true;
                log.LogInfo("HUB_THEME dressing classic box with launch theme");
                box.UpdateEventBoxDressingWithConfig();
            }
            ReportShown(box);
            if (overrideTheme == null) return;
            var write = File.GetLastWriteTimeUtc(config.ConfigFilePath);
            if (write == stamp) return;
            stamp = write;
            config.Reload();
            string value = (overrideTheme.Value ?? "").Trim();
            if (value == overrideApplied) return;
            overrideApplied = value;
            if (Dressed(box) || value.Length == 0) { log.LogInfo("HUB_THEME override " + (value.Length == 0 ? "(none)" : value) + " applies at next start"); return; }
            log.LogInfo("HUB_THEME override live classic -> " + value);
            box.HideEventBoxDressing();
            box.UpdateEventBoxDressingWithConfig();
        }
        catch (Exception error) { active = false; log.LogWarning("HUB_THEME tick failed; disabled " + error); }
    }

    private static void ReportShown(Box box)
    {
        var spawnable = box.m_eventBoxDressingWidget == null ? null
            : box.m_eventBoxDressingWidget.GetComponentsInChildren<Spawnable>(true).FirstOrDefault();
        var item = spawnable == null ? null : AccessTools.Field(typeof(Spawnable), "m_itemData").GetValue(spawnable) as SpawnableLibrary.ItemData;
        var dressing = box.m_eventBoxDressingWidget == null ? null : box.m_eventBoxDressingWidget.GetComponentInChildren<EventBoxDressing>();
        string now = (item == null ? "none" : item.Name) + " dressing=" + (dressing == null ? "none" : dressing.gameObject.name)
            + " active=" + Dressed(box);
        if (now == shown) return;
        shown = now;
        log.LogInfo("HUB_THEME shown item=" + now);
    }

    private static void LogCatalog(Box box)
    {
        var loader = typeof(AssetLoader).GetMethod("Get").Invoke(null, null);
        var available = typeof(AssetLoader).GetMethod("IsRuntimeAssetVariantAvailable");
        var parameters = available.GetParameters();
        var options = Enum.ToObject(parameters[1].ParameterType, 0);
        Func<string, string> present = reference =>
        {
            if (string.IsNullOrEmpty(reference)) return "n/a";
            try { return (bool)available.Invoke(loader, new[] { Activator.CreateInstance(parameters[0].ParameterType, reference), options }) ? "true" : "false"; }
            catch (Exception error) { return "error:" + error.GetType().Name; }
        };
        var manager = EventTimingManager.Get();
        log.LogInfo("HUB_THEME timingsReceived=" + (manager != null && manager.HasReceivedEventTimingsFromServer));
        var spawnables = box.m_eventBoxDressingWidget == null ? new Spawnable[0]
            : box.m_eventBoxDressingWidget.GetComponentsInChildren<Spawnable>(true);
        foreach (var spawnable in spawnables)
        {
            var library = AccessTools.Field(typeof(Spawnable), "m_spawnableLibrary").GetValue(spawnable) as SpawnableLibrary;
            var items = library == null ? null : AccessTools.Field(typeof(SpawnableLibrary), "m_itemData").GetValue(library) as IList;
            if (items == null) continue;
            foreach (SpawnableLibrary.ItemData item in items)
                log.LogInfo("HUB_THEME item name=" + item.Name + " available=" + present(item.Reference));
        }
    }
}
