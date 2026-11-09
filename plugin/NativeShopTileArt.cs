using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEngine;

// Owner 2026-10-28: board, theme and "No pet" tiles showed the native Tavern ticket (a DRAFT item has no native board or
// theme visual, and no client DBF record carries a board or theme thumbnail). The tile keeps its native layout (name,
// price or Owned plate); only its ticket item is repainted with local art and loses its "Tavern" header and dust:
// - a board: its own frame and corner props, drawn offscreen from the player's installed board prefab (PrepareClone, Draw) and
//   cached as a finished plate under BepInEx/cache (versioned, validated); never shipped;
// - Random owned, themes and No pet, and a board whose plate is not ready yet: the Cards tab's native swirl.
// Answering the native marketing-image request instead (tried live) switches the tile to its marketing layout, which
// hides unowned tiles' prices. Any failure leaves the native ticket. [Shop] TileArt: auto, neutral (swirl only),
// none (native tickets, restored at once).
//
// Cost (review 2026-10-28, Steam Deck): plates are made by one job at a time, one stage per frame. The cache read and
// the plate composition run on a worker thread; the prefab load is asynchronous when the loader allows it; the render
// is read back on the frame after it is drawn (the pinned UnityEngine has no texture AsyncGPUReadback). Shown tiles are
// served first while the shop is open. With it closed, leftover requests and then the offered boards are pre-warmed at
// an idle hub only after 4 s without input, one per 1.5 s; input or a match parks a job (resources released) and a
// board that errors is given up for the session (NativeShopArtQueue).
internal static class NativeShopTileArt
{
    private const string TicketPrefix = "Spawnable Library Item: Arena_Ticket";
    private const int RenderLayer = 31, LoadTimeoutFrames = 900;
    private static string mode = "auto";
    private static Action<string> log;
    private static bool inert, described;
    private static Texture2D neutral;
    private static readonly Dictionary<int, Texture2D> boards = new Dictionary<int, Texture2D>();
    private static readonly NativeShopArtQueue queue = new NativeShopArtQueue();
    private static readonly Dictionary<Renderer, Painted> painted = new Dictionary<Renderer, Painted>();
    private static readonly HashSet<GameObject> hidden = new HashSet<GameObject>();
    private static readonly Vector2 TicketUvMin = new Vector2(0f, 0.326f), TicketUvMax = new Vector2(0.493f, 1f);
    private sealed class Painted { internal Material Original, Mine; }

    internal static void Configure(string setting, Action<string> logger)
    {
        log = logger;
        string value = (setting ?? "").Trim().ToLowerInvariant();
        string next = value == "none" || value == "neutral" ? value : "auto";
        if (next == mode) return;
        mode = next;
        if (mode == "none") Restore(); // auto <-> neutral repaints on the next tick.
    }

    // Which local tiles get art: selectable products without a native visual (every theme and board, and No pet).
    internal static bool Wants(LocalShopProduct product) =>
        product != null && NativeShopPurchase.IsSelectable(product) && !(product.Kind == "Pet" && product.Pet != null);

    private static Texture2D Art(LocalShopProduct product, bool onScreen)
    {
        if (product.Kind == "Board" && product.Board.Value != LocalHubCosmetics.RandomBoard && mode == "auto")
        {
            int board = product.Board.Value;
            if (boards.TryGetValue(board, out var ready) && ready != null) return ready;
            queue.Request(board, onScreen); // Until it is ready the tile shows the neutral swirl.
        }
        if (neutral == null) neutral = Neutral();
        return neutral;
    }

    // ---- Painting (shop open, every second frame) ----
    // Every shown art tile's ticket item (UnderData/.../RewardItemDisplay/"Spawnable Library Item: Arena_Ticket"/
    // ArenaTicketDisplay) shows the art on its ticket mesh; its header text and dust are hidden. Only local DRAFT tiles
    // use that item. A pooled ticket reused for another product is repainted.
    internal static void Tick(Component shop)
    {
        if (inert || shop == null || Time.frameCount % 2 != 0) return;
        if (mode == "none") { if (painted.Count != 0 || hidden.Count != 0) Restore(); return; }
        try
        {
            if (Time.frameCount % 120 == 0) Sweep();
            foreach (var slot in shop.GetComponentsInChildren<ShopSlot>(false))
            {
                var shown = slot.BrowserButtonDataModel?.DisplayProduct;
                if (shown == null || !NativeShopPurchase.TryProduct(shown.PmtId, out var local) || !Wants(local)) continue;
                foreach (var item in slot.GetComponentsInChildren<Transform>(false))
                {
                    if (!item.name.StartsWith(TicketPrefix, StringComparison.Ordinal)) continue;
                    foreach (var node in item.GetComponentsInChildren<Transform>(false))
                    {
                        if (node.name == "ArenaText" || node.name == "Dust Particle") { node.gameObject.SetActive(false); hidden.Add(node.gameObject); continue; }
                        if (node.name != "ArenaTicket") continue;
                        var renderer = node.GetComponent<MeshRenderer>();
                        if (renderer == null) continue;
                        var art = Art(local, renderer.isVisible);
                        if (art != null) Paint(renderer, art);
                    }
                }
            }
        }
        catch (Exception error) { inert = true; log?.Invoke("[NativeShop] inert: tile art: " + error.Message); }
    }

    // The ticket mesh's UVs cover a sub-rect of the ticket texture; the material's texture transform maps that sub-rect
    // onto the whole art. A renderer keeps one private material, created on its first paint and destroyed with it.
    private static void Paint(Renderer renderer, Texture2D art)
    {
        if (!painted.TryGetValue(renderer, out var entry) || entry.Mine == null)
        {
            var mesh = renderer.GetComponent<MeshFilter>()?.sharedMesh;
            var source = renderer.sharedMaterial;
            if (entry != null && entry.Original != null) source = entry.Original;
            var material = new Material(source) { name = "SoloStoneTileArt" };
            // The pinned ticket mesh is not CPU-readable; live 2026-10-28 its face samples about u 0..0.49, v 0.33..1.0 of
            // the 1024x1024 ArenaTicket texture (measured from a painted plate). A readable mesh gives exact bounds.
            Vector2 min = TicketUvMin, max = TicketUvMax;
            try
            {
                var uv = mesh != null && mesh.isReadable ? mesh.uv : null;
                if (uv != null && uv.Length != 0)
                {
                    min = new Vector2(uv.Min(v => v.x), uv.Min(v => v.y)); max = new Vector2(uv.Max(v => v.x), uv.Max(v => v.y));
                }
            }
            catch (Exception) { min = TicketUvMin; max = TicketUvMax; }
            var map = NativeShopArtMath.TicketTransform(min.x, min.y, max.x, max.y, source.mainTextureScale.x, source.mainTextureScale.y,
                source.mainTextureOffset.x, source.mainTextureOffset.y);
            material.mainTextureScale = new Vector2(map[0], map[1]);
            material.mainTextureOffset = new Vector2(map[2], map[3]);
            if (!described)
            {
                described = true;
                log?.Invoke("[NativeShop] tile art ticket: shader=" + source.shader?.name + " texture=" + source.mainTexture?.name
                    + " " + source.mainTexture?.width + "x" + source.mainTexture?.height + " uv=" + min + max + " readable=" + (mesh != null && mesh.isReadable));
            }
            painted[renderer] = entry = new Painted { Original = source, Mine = material };
        }
        if (entry.Mine.mainTexture != art) entry.Mine.mainTexture = art;
        if (renderer.sharedMaterial != entry.Mine) renderer.sharedMaterial = entry.Mine;
    }

    // Tickets whose tile was destroyed: their private materials go too.
    private static void Sweep()
    {
        foreach (var renderer in painted.Keys.Where(r => r == null).ToList())
        {
            if (painted[renderer].Mine != null) UnityEngine.Object.Destroy(painted[renderer].Mine);
            painted.Remove(renderer);
        }
        hidden.RemoveWhere(node => node == null);
    }

    // TileArt = none mid-session: native ticket material, header and dust come back at once.
    private static void Restore()
    {
        foreach (var pair in painted)
        {
            if (pair.Key != null && pair.Value.Original != null && pair.Key.sharedMaterial == pair.Value.Mine) pair.Key.sharedMaterial = pair.Value.Original;
            if (pair.Value.Mine != null) UnityEngine.Object.Destroy(pair.Value.Mine);
        }
        painted.Clear();
        foreach (var node in hidden) if (node != null) node.SetActive(true);
        hidden.Clear();
    }

    // ---- Plate jobs (every frame; NativeShop.Update) ----
    private enum Stage { Disk, DiskWait, Load, LoadWait, Prepare, Render, Readback, Compose, ComposeWait }
    private sealed class Job
    {
        internal int Board, Frames, LoadFrames;
        internal Stage Stage;
        internal object Handle;
        internal volatile bool Loaded, LoadFailed, WorkerDone;
        internal bool AsyncLoad;
        internal RenderTexture Target;
        internal GameObject Clone;
        internal byte[] Raw, Plate;
        internal int Width, Height;
        internal string Path, Source = "disk", Failure;
        internal double MainMs, LoadMs, PrepareMs, RenderMs, ReadbackMs, WorstMs;
    }
    private static Job job;
    private static int prewarmVersion = -1, made, worstStageMs;
    private static float lastInput;
    private static Vector3 lastMouse;

    // shopOpen: the native shop is open (shown tiles are served back to back). idle: an idle hub (pre-warm allowed).
    // Every frame (NativeShop.Update): the last mouse, scroll, key or pad input, for the pre-warm quiet gate.
    internal static void SampleInput()
    {
        if (inert) return;
        var mouse = Input.mousePosition;
        if (Input.anyKey || mouse != lastMouse || Input.mouseScrollDelta != Vector2.zero) lastInput = Time.realtimeSinceStartup;
        lastMouse = mouse;
    }

    // shopOpen: the native shop is open (shown tiles are served back to back). idle: an idle hub (pre-warm allowed).
    internal static void Pump(bool shopOpen, bool idle)
    {
        if (inert) return;
        try
        {
            if (mode != "auto") { if (job != null) Abandon("TileArt " + mode); return; }
            float now = Time.realtimeSinceStartup;
            bool quiet = NativeShopArtQueue.Quiet(now, lastInput);
            if (job == null)
            {
                if (prewarmVersion != NativeShopPurchase.CatalogueVersion && NativeShopPurchase.Products != null)
                {
                    prewarmVersion = NativeShopPurchase.CatalogueVersion;
                    queue.SetPrewarm(NativeShopPurchase.Products.Where(p => p.Kind == "Board" && p.Board.Value != LocalHubCosmetics.RandomBoard)
                        .Select(p => p.Board.Value).Where(b => !boards.ContainsKey(b)));
                }
                int board = queue.Next(shopOpen, idle, quiet, now);
                if (board < 0) return;
                job = new Job { Board = board, Stage = Stage.Disk, Path = CachePath(board) };
            }
            // A heavy stage (load, instantiate, draw) waits for the shop or a quiet idle hub; the job is parked meanwhile.
            if (Heavy(job.Stage) && !NativeShopArtQueue.HeavyAllowed(shopOpen, idle, quiet)) { Park("input"); return; }
            var watch = Stopwatch.StartNew();
            Step(job);
            double ms = watch.Elapsed.TotalMilliseconds;
            if (job != null) { job.Frames++; job.MainMs += ms; job.WorstMs = Math.Max(job.WorstMs, ms); }
            worstStageMs = Math.Max(worstStageMs, (int)ms);
        }
        catch (Exception error)
        {
            // A board that throws is given up for the session (never retried in a loop).
            if (job != null) Fail(job, "error: " + (error.InnerException ?? error).Message);
            else log?.Invoke("[NativeShop] board art job: " + (error.InnerException ?? error).Message);
        }
    }

    // Neither the shop nor an idle hub (a match, a transition): a job holding a prefab, clone or render texture is
    // parked and releases them; a worker-thread stage keeps going.
    internal static void Suspend()
    {
        if (job == null || inert) return;
        try { if (job.Handle != null || job.Clone != null || job.Target != null || Heavy(job.Stage) || job.Stage == Stage.LoadWait) Park("paused"); }
        catch (Exception error) { log?.Invoke("[NativeShop] board art suspend: " + error.Message); job = null; }
    }

    private static bool Heavy(Stage stage) => stage == Stage.Load || stage == Stage.Prepare || stage == Stage.Render || stage == Stage.Readback;

    private static void Park(string reason)
    {
        Cleanup(job);
        queue.Park(job.Board, Time.realtimeSinceStartup);
        log?.Invoke("[NativeShop] board art " + job.Board + ": parked (" + reason + ")");
        job = null;
    }

    private static void Step(Job current)
    {
        switch (current.Stage)
        {
            case Stage.Disk:
                current.Stage = Stage.DiskWait;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        if (File.Exists(current.Path))
                        {
                            current.Plate = NativeShopArtMath.Decode(File.ReadAllBytes(current.Path), NativeShopArtMath.FramingKey, out string reason);
                            if (current.Plate == null) current.Failure = "cache " + reason;
                        }
                    }
                    catch (Exception error) { current.Failure = "cache " + error.Message; }
                    finally { current.WorkerDone = true; }
                });
                return;
            case Stage.DiskWait:
                if (!current.WorkerDone) return;
                if (current.Plate != null) { Complete(current); return; }
                if (current.Failure != null) log?.Invoke("[NativeShop] board art " + current.Board + ": " + current.Failure + "; rendering again");
                current.Failure = null; current.WorkerDone = false; current.Source = "render";
                current.Stage = Stage.Load;
                return;
            case Stage.Load:
            {
                var record = GameDbf.Board.GetRecord(current.Board);
                if (record == null || string.IsNullOrEmpty(record.Prefab)) { Fail(current, "no board record"); return; }
                var watch = Stopwatch.StartNew();
                StartLoad(record.Prefab, current);
                current.LoadMs += watch.Elapsed.TotalMilliseconds;
                current.Stage = Stage.LoadWait;
                return;
            }
            case Stage.LoadWait:
                if (current.LoadFailed) { Fail(current, "prefab not loaded"); return; }
                if (!current.Loaded) { if (++current.LoadFrames > LoadTimeoutFrames) Fail(current, "prefab load timed out"); return; }
                current.Stage = Stage.Prepare;
                return;
            case Stage.Prepare:
            {
                var watch = Stopwatch.StartNew();
                var prefab = current.Handle?.GetType().GetProperty("Asset")?.GetValue(current.Handle, null) as GameObject;
                if (prefab == null) { Fail(current, "prefab not loaded"); return; }
                current.Clone = PrepareClone(prefab, current.Board);
                // The asset handle stays held until the clone is drawn: releasing it here let the prefab's bundle
                // materials unload before the next frame's draw (Windows: magenta props, integration-1).
                current.PrepareMs = watch.Elapsed.TotalMilliseconds;
                current.Stage = Stage.Render; // Drawn next frame: instantiate and strip, then draw, never in one frame.
                return;
            }
            case Stage.Render:
            {
                var watch = Stopwatch.StartNew();
                current.Target = Draw(current.Clone);
                UnityEngine.Object.DestroyImmediate(current.Clone); current.Clone = null;
                (current.Handle as IDisposable)?.Dispose(); current.Handle = null;
                current.Width = current.Target.width; current.Height = current.Target.height;
                current.Stage = Stage.Readback; // Read next frame, once the GPU has drawn it (no stall on the draw itself).
                current.RenderMs = watch.Elapsed.TotalMilliseconds;
                return;
            }
            case Stage.Readback:
            {
                var watch = Stopwatch.StartNew();
                current.Raw = ReadNow(current.Target);
                ReleaseTarget(current);
                current.ReadbackMs = watch.Elapsed.TotalMilliseconds;
                current.Stage = Stage.Compose;
                return;
            }
            case Stage.Compose:
                current.Stage = Stage.ComposeWait;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        if (current.Raw == null || current.Raw.Length != current.Width * current.Height * 4) { current.Failure = "readback size"; return; }
                        NativeShopArtMath.Opaque(current.Raw);
                        if (!NativeShopArtMath.Plausible(current.Raw, current.Width, current.Height)) { current.Failure = "implausible render (dark, flat or magenta); not cached"; return; }
                        current.Plate = NativeShopArtMath.Plate(current.Raw, current.Width, current.Height, NativeShopArtMath.PlateWidth, NativeShopArtMath.PlateHeight);
                        current.Raw = null;
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(current.Path));
                        SweepStalePlates(System.IO.Path.GetDirectoryName(current.Path));
                        File.WriteAllBytes(current.Path + ".tmp", NativeShopArtMath.Encode(current.Plate, NativeShopArtMath.FramingKey));
                        if (File.Exists(current.Path)) File.Delete(current.Path);
                        File.Move(current.Path + ".tmp", current.Path);
                    }
                    catch (Exception error) { current.Failure = current.Failure ?? "plate " + error.Message; }
                    finally { current.WorkerDone = true; }
                });
                return;
            case Stage.ComposeWait:
                if (!current.WorkerDone) return;
                if (current.Plate == null) { Fail(current, current.Failure ?? "no plate"); return; }
                if (current.Failure != null) log?.Invoke("[NativeShop] board art " + current.Board + ": " + current.Failure);
                Complete(current);
                return;
        }
    }

    private static void Complete(Job current)
    {
        boards[current.Board] = Texture(current.Plate, "SoloStoneBoardArt_" + current.Board);
        made++;
        log?.Invoke("[NativeShop] board art " + current.Board + ": " + current.Source + " frames=" + current.Frames
            + " main=" + current.MainMs.ToString("0.0") + "ms worst=" + current.WorstMs.ToString("0.0") + "ms"
            + (current.Source == "render" ? " load=" + current.LoadMs.ToString("0.0") + "ms/" + current.LoadFrames + "f " + (current.AsyncLoad ? "async" : "sync")
                + " prepare=" + current.PrepareMs.ToString("0.0") + "ms render=" + current.RenderMs.ToString("0.0") + "ms readback=" + current.ReadbackMs.ToString("0.0") + "ms" : "")
            + " ready=" + made + " worstStage=" + worstStageMs + "ms");
        queue.Finish(current.Board);
        job = null;
    }
    private static void Fail(Job current, string reason)
    {
        log?.Invoke("[NativeShop] board art " + current.Board + ": " + reason + "; neutral icon this session");
        Cleanup(current);
        queue.Finish(current.Board);
        job = null;
    }
    // TileArt mode change only: the queue starts over when auto comes back.
    private static void Abandon(string reason)
    {
        Cleanup(job);
        log?.Invoke("[NativeShop] board art " + job.Board + ": abandoned (" + reason + ")");
        queue.Reset();
        prewarmVersion = -1;
        job = null;
    }
    private static void Cleanup(Job current)
    {
        (current.Handle as IDisposable)?.Dispose(); current.Handle = null;
        if (current.Clone != null) { UnityEngine.Object.DestroyImmediate(current.Clone); current.Clone = null; }
        ReleaseTarget(current); // A pending readback of a released target is discarded with the job.
    }
    private static void ReleaseTarget(Job current) { if (current.Target != null) { RenderTexture.ReleaseTemporary(current.Target); current.Target = null; } }

    // Plates of older render versions are never read again: delete them once per session, on the first cache write.
    private static int staleSwept;
    private static void SweepStalePlates(string folder)
    {
        if (Interlocked.Exchange(ref staleSwept, 1) != 0) return;
        try
        {
            string current = "plate-v" + NativeShopArtMath.RenderVersion + "-";
            foreach (string file in Directory.GetFiles(folder, "plate-v*.rgba"))
                if (!System.IO.Path.GetFileName(file).StartsWith(current, StringComparison.Ordinal)) File.Delete(file);
        }
        catch (Exception) { } // Best effort: a stale file is only wasted disk.
    }

    private static string CachePath(int board) => System.IO.Path.Combine(System.IO.Path.Combine(BepInEx.Paths.CachePath, "SoloStoneBoardArt"),
        "plate-v" + NativeShopArtMath.RenderVersion + "-" + board + ".rgba");

    // ---- Board render ----
    // What makes a board recognisable is its frame, corner props and colour, so the whole prefab is drawn, top-down. The
    // loaded prefab asset is switched inactive only for the Instantiate call, so the clone never runs Awake; every
    // script, animator, audio source, collider and rigidbody is removed before the clone is activated on an otherwise
    // unused layer far below the scene (PrepareClone, one frame). Next frame (Draw) it is activated, drawn by one manual
    // Camera.Render into a temporary render texture and destroyed with the camera before any game camera draws. The
    // texture is read back the frame after.
    // Frame 1: an inactive, stripped clone (kept inactive, so nothing draws or runs it until Draw).
    private static GameObject PrepareClone(GameObject prefab, int board)
    {
        GameObject clone = null;
        bool wasActive = prefab.activeSelf;
        try
        {
            try { prefab.SetActive(false); clone = UnityEngine.Object.Instantiate(prefab, new Vector3(0f, -5000f, 0f), Quaternion.identity); }
            finally { prefab.SetActive(wasActive); }
            clone.name = "SoloStoneBoardRender_" + board;
            for (int pass = 0; pass < 3; pass++) // Components another one requires go on a later pass.
            {
                int removed = 0;
                foreach (var component in clone.GetComponentsInChildren<Component>(true))
                {
                    if (component == null || component is Transform || component is Renderer || component is MeshFilter || component is Light) continue;
                    if (!(component is Behaviour) && !(component is Collider) && !(component is Rigidbody)) continue;
                    try { UnityEngine.Object.DestroyImmediate(component); removed++; } catch (Exception) { }
                }
                if (removed == 0) break;
            }
            foreach (var light in clone.GetComponentsInChildren<Light>(true)) light.cullingMask = 1 << RenderLayer;
            foreach (var node in clone.GetComponentsInChildren<Transform>(true)) node.gameObject.layer = RenderLayer;
            var result = clone; clone = null;
            return result;
        }
        finally { if (clone != null) UnityEngine.Object.DestroyImmediate(clone); }
    }

    // Frame 2: activate, one manual render into a temporary render texture; the caller destroys the clone at once.
    private static RenderTexture Draw(GameObject clone)
    {
        GameObject rig = null;
        RenderTexture target = null;
        try
        {
            clone.SetActive(true);
            var table = clone.GetComponentsInChildren<MeshRenderer>(false).FirstOrDefault(r => r.name == "TableTop");
            var root = clone.transform.position;
            var centre = (table != null ? table.bounds.center : root + new Vector3(NativeShopArtMath.TableX, NativeShopArtMath.TableY, NativeShopArtMath.TableZ))
                + new Vector3(NativeShopArtMath.OffsetX, 0f, NativeShopArtMath.OffsetZ);
            rig = new GameObject("SoloStoneBoardCamera") { hideFlags = HideFlags.HideAndDontSave };
            rig.transform.position = centre + Vector3.up * 500f;
            rig.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.right); // A quarter turn: fills the portrait ticket face.
            var camera = rig.AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true; camera.orthographicSize = NativeShopArtMath.AreaX / 2f; camera.aspect = NativeShopArtMath.AreaZ / NativeShopArtMath.AreaX;
            camera.nearClipPlane = 1f; camera.farClipPlane = 1500f;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            camera.cullingMask = 1 << RenderLayer;
            target = RenderTexture.GetTemporary(NativeShopArtMath.RenderWidthPixels, NativeShopArtMath.RenderHeightPixels, 24, RenderTextureFormat.ARGB32);
            camera.targetTexture = target;
            camera.Render();
            camera.targetTexture = null;
            var result = target; target = null;
            return result;
        }
        finally
        {
            if (rig != null) UnityEngine.Object.DestroyImmediate(rig);
            if (target != null) RenderTexture.ReleaseTemporary(target);
        }
    }

    private static byte[] ReadNow(RenderTexture target)
    {
        var previous = RenderTexture.active;
        try
        {
            RenderTexture.active = target;
            var read = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
            read.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            var pixels = read.GetPixels32();
            UnityEngine.Object.Destroy(read);
            var bytes = new byte[pixels.Length * 4];
            for (int i = 0; i < pixels.Length; i++) { bytes[i * 4] = pixels[i].r; bytes[i * 4 + 1] = pixels[i].g; bytes[i * 4 + 2] = pixels[i].b; bytes[i * 4 + 3] = pixels[i].a; }
            return bytes;
        }
        finally { RenderTexture.active = previous; }
    }

    // AssetLoader.LoadAsset<GameObject>: the asset-manager types live in an assembly the plugin does not reference, so
    // the calls are resolved by shape. The asynchronous overload (AssetReference, AssetHandleCallback<T>, object,
    // AssetLoadingOptions) is used when its callback can be bound; otherwise the synchronous one.
    private static MethodInfo loadSync, loadAsync;
    private static bool asyncLoadBroken;
    private static object loader;
    private static void StartLoad(string reference, Job current)
    {
        if (loader == null)
            loader = typeof(AssetLoader).GetMethod("Get", BindingFlags.Public | BindingFlags.Static).Invoke(null, null)
                ?? throw new InvalidOperationException("asset loader unavailable");
        var methods = typeof(AssetLoader).GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name == "LoadAsset" && m.IsGenericMethodDefinition).ToArray();
        if (!asyncLoadBroken)
        {
            try
            {
                if (loadAsync == null)
                    loadAsync = methods.First(m => m.GetParameters().Length == 4 && m.GetParameters()[0].ParameterType.Name == "AssetReference"
                        && typeof(Delegate).IsAssignableFrom(m.GetParameters()[1].ParameterType)).MakeGenericMethod(typeof(GameObject));
                var parameters = loadAsync.GetParameters();
                var callback = Delegate.CreateDelegate(parameters[1].ParameterType,
                    typeof(NativeShopTileArt).GetMethod(nameof(OnLoaded), BindingFlags.NonPublic | BindingFlags.Static));
                current.AsyncLoad = true;
                if ((bool)loadAsync.Invoke(loader, new[] { Activator.CreateInstance(parameters[0].ParameterType, reference), callback, (object)current,
                    Enum.ToObject(parameters[3].ParameterType, 0) })) return;
                current.AsyncLoad = false; current.LoadFailed = true; return;
            }
            catch (Exception error)
            {
                asyncLoadBroken = true; current.AsyncLoad = false;
                log?.Invoke("[NativeShop] board art: asynchronous prefab load unavailable (" + (error.InnerException ?? error).Message + "); loading synchronously");
            }
        }
        if (loadSync == null)
            loadSync = methods.First(m => m.GetParameters().Length == 2 && !m.GetParameters()[0].ParameterType.IsByRef
                && m.GetParameters()[0].ParameterType.Name == "AssetReference" && m.GetParameters()[1].ParameterType.IsEnum).MakeGenericMethod(typeof(GameObject));
        var syncParameters = loadSync.GetParameters();
        current.Handle = loadSync.Invoke(loader, new[] { Activator.CreateInstance(syncParameters[0].ParameterType, reference), Enum.ToObject(syncParameters[1].ParameterType, 0) });
        if (current.Handle == null) current.LoadFailed = true; else current.Loaded = true;
    }
    // AssetHandleCallback<GameObject>(AssetReference, AssetHandle<GameObject>, object), bound by parameter contravariance.
    private static void OnLoaded(object reference, object handle, object data)
    {
        var owner = data as Job;
        // Parked, failed or abandoned meanwhile, or already loaded by the synchronous fallback: not ours to keep.
        if (owner == null || !ReferenceEquals(owner, job) || owner.Handle != null) { (handle as IDisposable)?.Dispose(); return; }
        owner.Handle = handle;
        if (handle == null) owner.LoadFailed = true; else owner.Loaded = true;
    }

    // ---- Neutral plate: the Cards tab's native Hearthstone swirl (any local tab's icon if Cards is absent). ----
    private static Texture2D Neutral()
    {
        var shop = Shop.Get();
        if (shop == null) return null;
        var tabs = shop.GetComponentsInChildren<ShopTab>(true);
        Sprite icon = null;
        foreach (var tab in tabs.OrderBy(t => t.CurrentData?.Id == NativeShopTabs.Cards ? 0 : NativeShopTabs.Ids.Contains(t.CurrentData?.Id) ? 1 : 2))
        {
            icon = tab.GetComponentsInChildren<SpriteRenderer>(true).FirstOrDefault(r => r.gameObject.name == "BaseIcon" && r.sprite != null)?.sprite;
            if (icon != null) break;
        }
        if (icon == null) return null;
        // The atlas location (textureRect) is not in the pinned reference stubs; the runtime has it, else the sprite rect.
        var rect = typeof(Sprite).GetProperty("textureRect")?.GetValue(icon, null) as Rect? ?? icon.rect;
        var texture = icon.texture;
        var box = new[] { rect.x / texture.width, rect.y / texture.height, rect.width / texture.width, rect.height / texture.height };
        const int iconSide = 176;
        return Texture(NativeShopArtMath.Plate(CopyRegion(texture, box, iconSide, iconSide), iconSide, iconSide, 168, 168), "SoloStoneTileArt_Neutral");
    }

    private static Texture2D Texture(byte[] plate, string name)
    {
        var pixels = new Color32[plate.Length / 4];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(plate[i * 4], plate[i * 4 + 1], plate[i * 4 + 2], plate[i * 4 + 3]);
        var result = new Texture2D(NativeShopArtMath.PlateWidth, NativeShopArtMath.PlateHeight, TextureFormat.RGBA32, false) { name = name };
        result.SetPixels32(pixels);
        result.Apply(false, true);
        return result;
    }

    // GPU textures are not CPU-readable: blit the normalized sub-rect into a temporary render target and read it back.
    private static byte[] CopyRegion(Texture source, float[] box, int width, int height)
    {
        var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
        try { Graphics.Blit(source, target, new Vector2(box[2], box[3]), new Vector2(box[0], box[1])); return ReadNow(target); }
        finally { RenderTexture.ReleaseTemporary(target); }
    }
}
