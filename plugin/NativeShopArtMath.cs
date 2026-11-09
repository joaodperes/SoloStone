using System;
using System.Collections.Generic;
using System.Linq;

// Unity-free parts of the shop tile art (NativeShopTileArt) and the sub-tab cradle, so ShopTests can run them offline.
// Pixels are RGBA bytes, rows bottom-up as Unity reads them.
internal static class NativeShopArtMath
{
    // Plate in the ticket face's shape (0.73 wide per tall).
    internal const int PlateWidth = 256, PlateHeight = 350;
    internal const float ArtCentre = 0.5f;

    // Board render framing (live 2026-10-28): every allowlisted board has a "TableTop" 24 x 16.8 units at
    // (-7.83, -1.16, -3.48) from its root; the frame and corner props sit in a 15.8 x 12.3 window of it, offset
    // (-0.58, 0, -0.24) from its centre. Drawn a quarter turn round (world +X up) into RenderWidth pixels of height.
    internal const int RenderWidth = 1024;
    internal const float AreaX = 15.8f, AreaZ = 12.3f, OffsetX = -0.58f, OffsetZ = -0.24f;
    internal const float TableX = -7.83f, TableY = -1.16f, TableZ = -3.48f;
    // Bump when the renderer changes in a way the framing numbers below do not capture.
    internal const int RenderVersion = 4; // 4: v3 plates could hold magenta (unloaded materials); re-render them.
    internal static int RenderHeightPixels => RenderWidth;
    internal static int RenderWidthPixels => (int)Math.Round(RenderWidth * AreaZ / AreaX);
    // Stable across runs and runtimes (FNV-1a over the invariant framing text); part of every cache header.
    internal static readonly int FramingKey = Fnv(string.Join("|", new object[] { RenderVersion, PlateWidth, PlateHeight, ArtCentre,
        RenderWidth, AreaX, AreaZ, OffsetX, OffsetZ, TableX, TableY, TableZ, "quarter-turn" }
        .Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture))));

    internal static int Fnv(string text)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in text) { hash ^= c; hash *= 16777619; }
            return (int)hash;
        }
    }

    // The art fitted inside maxWidth x maxHeight (box-filtered, alpha-weighted), centred on a dark gradient plate.
    internal static byte[] Plate(byte[] art, int artWidth, int artHeight, int maxWidth, int maxHeight)
    {
        if (art == null || artWidth <= 0 || artHeight <= 0 || art.Length != artWidth * artHeight * 4) throw new ArgumentException("art size");
        var plate = new byte[PlateWidth * PlateHeight * 4];
        for (int y = 0; y < PlateHeight; y++)
        {
            float t = (float)y / (PlateHeight - 1);
            byte r = Mix(30, 58, t), g = Mix(18, 36, t), b = Mix(44, 82, t);
            for (int x = 0; x < PlateWidth; x++) { int at = (y * PlateWidth + x) * 4; plate[at] = r; plate[at + 1] = g; plate[at + 2] = b; plate[at + 3] = 255; }
        }
        float scale = Math.Min((float)maxWidth / artWidth, (float)maxHeight / artHeight);
        int width = Math.Max(1, Math.Min(PlateWidth, (int)(artWidth * scale))), height = Math.Max(1, Math.Min(PlateHeight, (int)(artHeight * scale)));
        int left = (PlateWidth - width) / 2, low = Math.Max(0, Math.Min(PlateHeight - height, (int)(PlateHeight * ArtCentre) - height / 2));
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int x0 = Math.Min(artWidth - 1, (int)(x / scale)), x1 = Math.Min(artWidth, Math.Max(x0 + 1, (int)((x + 1) / scale)));
                int y0 = Math.Min(artHeight - 1, (int)(y / scale)), y1 = Math.Min(artHeight, Math.Max(y0 + 1, (int)((y + 1) / scale)));
                float r = 0, g = 0, b = 0, a = 0; int n = 0;
                for (int sy = y0; sy < y1; sy++)
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int s = (sy * artWidth + sx) * 4; float w = art[s + 3] / 255f;
                        r += art[s] * w; g += art[s + 1] * w; b += art[s + 2] * w; a += w; n++;
                    }
                if (a <= 0) continue;
                int at = ((low + y) * PlateWidth + left + x) * 4; float k = a / n;
                plate[at] = Mix(plate[at], (byte)(r / a), k); plate[at + 1] = Mix(plate[at + 1], (byte)(g / a), k);
                plate[at + 2] = Mix(plate[at + 2], (byte)(b / a), k);
            }
        return plate;
    }

    private static byte Mix(byte a, byte b, float t) => (byte)(a + (b - a) * t);

    // Additive effect shaders leave alpha 0 over correct colour; a board window is all board, so it is made opaque.
    internal static void Opaque(byte[] rgba) { for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255; }

    // A render worth keeping: not mostly black (unloaded/unwarmed first frame), not a flat fill (nothing drawn).
    internal static bool Plausible(byte[] rgba, int width, int height)
    {
        if (rgba == null || width <= 0 || height <= 0 || rgba.Length != width * height * 4) return false;
        double sum = 0, squares = 0; int dark = 0, magenta = 0, n = 0;
        int step = Math.Max(1, width * height / 20000);
        for (int i = 0; i < width * height; i += step)
        {
            int at = i * 4;
            double l = 0.299 * rgba[at] + 0.587 * rgba[at + 1] + 0.114 * rgba[at + 2];
            sum += l; squares += l * l; n++;
            if (l < 16) dark++;
            if (rgba[at] > 240 && rgba[at + 2] > 240 && rgba[at + 1] < 16) magenta++; // Unity's missing-shader colour
        }
        double mean = sum / n, deviation = Math.Sqrt(Math.Max(0, squares / n - mean * mean));
        return mean >= 40 && deviation >= 12 && dark < n / 2 && magenta * 200 < n; // under 0.5% magenta
    }

    // Plate cache file: "SSBP", format 1, framing key, width, height, then RGBA. Anything else is ignored (and re-rendered).
    private const int Magic = 0x50425353, Format = 1, Header = 20;
    internal static byte[] Encode(byte[] plate, int framing)
    {
        if (plate == null || plate.Length != PlateWidth * PlateHeight * 4) throw new ArgumentException("plate size");
        var bytes = new byte[Header + plate.Length];
        BitConverter.GetBytes(Magic).CopyTo(bytes, 0); BitConverter.GetBytes(Format).CopyTo(bytes, 4);
        BitConverter.GetBytes(framing).CopyTo(bytes, 8); BitConverter.GetBytes(PlateWidth).CopyTo(bytes, 12);
        BitConverter.GetBytes(PlateHeight).CopyTo(bytes, 16);
        Buffer.BlockCopy(plate, 0, bytes, Header, plate.Length);
        return bytes;
    }
    internal static byte[] Decode(byte[] bytes, int framing, out string reason)
    {
        reason = null;
        if (bytes == null || bytes.Length != Header + PlateWidth * PlateHeight * 4) { reason = "size"; return null; }
        if (BitConverter.ToInt32(bytes, 0) != Magic || BitConverter.ToInt32(bytes, 4) != Format) { reason = "format"; return null; }
        if (BitConverter.ToInt32(bytes, 8) != framing) { reason = "framing"; return null; }
        if (BitConverter.ToInt32(bytes, 12) != PlateWidth || BitConverter.ToInt32(bytes, 16) != PlateHeight) { reason = "dimensions"; return null; }
        var plate = new byte[PlateWidth * PlateHeight * 4];
        Buffer.BlockCopy(bytes, Header, plate, 0, plate.Length);
        if (!Plausible(plate, PlateWidth, PlateHeight)) { reason = "pixels"; return null; }
        return plate;
    }
    // Ticket material texture transform: the native sampled range (offset + uv * scale over the mesh UV bounds) is mapped
    // onto 0..1 of the art. Returns scaleX, scaleY, offsetX, offsetY.
    internal static float[] TicketTransform(float minU, float minV, float maxU, float maxV, float scaleX, float scaleY, float offsetX, float offsetY)
    {
        float loU = offsetX + minU * scaleX, loV = offsetY + minV * scaleY, hiU = offsetX + maxU * scaleX, hiV = offsetY + maxV * scaleY;
        float sizeU = Math.Max(1e-4f, hiU - loU), sizeV = Math.Max(1e-4f, hiV - loV);
        return new[] { scaleX / sizeU, scaleY / sizeV, (offsetX - loU) / sizeU, (offsetY - loV) / sizeV };
    }

    // Sub-tab cradle: hidden in the local shop when the active category leaves sockets empty.
    // ---- Recipe price plate on Deck tiles (NativeShopTilePrices) ----
    // [Shop] RecipeTilePlate: "none", or ';'-separated key=value: lift = levels from the native price text up to the copied
    // plate (0-4; live 2026-10-30 the text is PrimaryPriceTag/PriceTag/PriceData/Amount, so 2 = PriceTag: frame, coin
    // and text; 1 = PriceData drew only a coin); x / y = the copy's offset from the native plate, in
    // native plate widths / heights along the screen's right / up; scale = of the native plate (0.3-2); text = the label,
    // {0} = the recipe's price; queue = the copied text's UberText render-queue increment (-1 = auto: one above the copied
    // frame, 0-100 fixed). A bad or unknown entry keeps its default and is logged.
    // Live 2026-10-30 (PC, standardsingle): y=0.85 sits between the deck art and the native price, clear of the name.
    internal const string DefaultRecipePlate = "lift=2; x=0; y=0.85; scale=1; text={0} Recipe";
    internal sealed class RecipePlate
    {
        internal bool Enabled = true;
        internal int Lift = 2;
        internal float X, Y = 0.85f, Scale = 1f;
        internal string Text = "{0} Recipe";
        internal int Queue = -1;
        internal string Key => Enabled ? string.Join("|", Queue, Lift, X.ToString("R", Invariant), Y.ToString("R", Invariant), Scale.ToString("R", Invariant), Text) : "none";
        internal string Label(string price) => string.Format(Invariant, Text, price);
    }
    private static readonly System.Globalization.CultureInfo Invariant = System.Globalization.CultureInfo.InvariantCulture;
    internal static RecipePlate ParseRecipePlate(string value, Action<string> log = null)
    {
        var plate = new RecipePlate();
        string setting = (value ?? "").Trim();
        if (setting.Equals("none", StringComparison.OrdinalIgnoreCase)) { plate.Enabled = false; return plate; }
        if (setting.Length == 0) setting = DefaultRecipePlate;
        foreach (string entry in setting.Split(';'))
        {
            if (entry.Trim().Length == 0) continue;
            int at = entry.IndexOf('=');
            string key = at < 0 ? entry.Trim().ToLowerInvariant() : entry.Substring(0, at).Trim().ToLowerInvariant();
            string text = at < 0 ? "" : entry.Substring(at + 1).Trim();
            bool number = float.TryParse(text, System.Globalization.NumberStyles.Float, Invariant, out float n) && !float.IsNaN(n) && !float.IsInfinity(n);
            bool ok = true;
            switch (key)
            {
                case "lift": ok = number && n == Math.Floor(n) && n >= 0 && n <= 4; if (ok) plate.Lift = (int)n; break;
                case "x": ok = number && Math.Abs(n) <= 10; if (ok) plate.X = n; break;
                case "y": ok = number && Math.Abs(n) <= 10; if (ok) plate.Y = n; break;
                case "scale": ok = number && n >= 0.3f && n <= 2f; if (ok) plate.Scale = n; break;
                case "text": ok = ValidLabel(text); if (ok) plate.Text = text; break;
                case "queue": ok = number && n == Math.Floor(n) && n >= -1 && n <= 100; if (ok) plate.Queue = (int)n; break;
                default: ok = false; break;
            }
            if (!ok) log?.Invoke("[NativeShop] RecipeTilePlate: ignored '" + entry.Trim() + "'");
        }
        return plate;
    }
    private static bool ValidLabel(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 24 || !text.Contains("{0}")) return false;
        try { string.Format(Invariant, text, "100"); return true; }
        catch (FormatException) { return false; }
    }

    internal static bool HideCradle(bool enabled, string mode, int used, int sockets) =>
        enabled && !string.Equals(mode, "native", StringComparison.OrdinalIgnoreCase) && sockets > 0 && used >= 0 && used < sockets;
}

// Which board plate to make next, never two at once. With the shop open, its shown tiles go first (on screen before off
// screen, then request order), back to back. With the shop closed, the same leftover requests and then the offered boards
// are pre-warmed only at an idle hub after QuietBeforePrewarm seconds without input, one per PrewarmSpacing seconds.
// A job interrupted by input or a match is parked (made again later); one that fails is given up for the session.
internal sealed class NativeShopArtQueue
{
    internal const float PrewarmSpacing = 1.5f, QuietBeforePrewarm = 4f;
    // Deck 2026-10-28: a cold board costs one 50-80 ms frame, and the first touch of a shared bundle about 1 s.
    internal static bool Quiet(float now, float lastInput) => now - lastInput >= QuietBeforePrewarm;
    // Heavy stages (load, instantiate, draw) run while the shop is open, or at an idle hub with no recent input.
    internal static bool HeavyAllowed(bool shopOpen, bool idle, bool quiet) => shopOpen || idle && quiet;
    private readonly List<int> requested = new List<int>();
    private readonly HashSet<int> visible = new HashSet<int>();
    private readonly HashSet<int> done = new HashSet<int>();
    private readonly List<int> prewarm = new List<int>();
    private float lastStart = float.NegativeInfinity;
    internal bool Busy { get; private set; }
    internal int Current { get; private set; } = -1;

    internal void Request(int board, bool onScreen)
    {
        if (done.Contains(board) || board == Current) return;
        if (!requested.Contains(board)) requested.Add(board);
        if (onScreen) visible.Add(board);
    }
    internal void SetPrewarm(IEnumerable<int> boards) { prewarm.Clear(); prewarm.AddRange(boards.Where(b => !done.Contains(b)).Distinct()); }
    internal bool Has(int board) => done.Contains(board);
    // Next board to start, or -1.
    internal int Next(bool shopOpen, bool idle, bool quiet, float now)
    {
        if (Busy) return -1;
        int next = -1;
        if (shopOpen)
        {
            if (requested.Count != 0) { int shown = requested.FindIndex(visible.Contains); next = requested[shown >= 0 ? shown : 0]; }
        }
        else if (idle && quiet && now - lastStart >= PrewarmSpacing)
        {
            // Leftover shop requests are pre-warm now: same gate and spacing, ahead of the rest.
            if (requested.Count != 0) next = requested[0];
            else { int warm = prewarm.FindIndex(b => !done.Contains(b)); next = warm >= 0 ? prewarm[warm] : -1; }
        }
        if (next < 0) return -1;
        requested.Remove(next); visible.Remove(next); prewarm.Remove(next);
        Busy = true; Current = next; lastStart = now;
        return next;
    }
    // Made, or given up for this session (an error or a failed load): never queued again until Reset.
    internal void Finish(int board) { done.Add(board); requested.Remove(board); visible.Remove(board); prewarm.Remove(board); if (board == Current) { Busy = false; Current = -1; } }
    // Interrupted (input, a match): first in line for the next pre-warm, spacing restarts.
    internal void Park(int board, float now)
    {
        if (board == Current) { Busy = false; Current = -1; }
        if (!done.Contains(board)) { prewarm.Remove(board); prewarm.Insert(0, board); }
        lastStart = now;
    }
    // TileArt mode changed: everything is made again.
    internal void Reset() { requested.Clear(); visible.Clear(); done.Clear(); prewarm.Clear(); Busy = false; Current = -1; lastStart = float.NegativeInfinity; }
}
