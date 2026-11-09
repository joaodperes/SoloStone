using System;
using System.Collections.Generic;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MiniJSON;

// All disk work, hashing and archive verification run on the export worker. No bridge, engine or Unity dependency.
internal static class ReportExport
{
    internal const int MiB = 1024 * 1024, MaxZipBytes = 9 * 1000 * 1000;
    // Uncompressed per-source budgets. Journals keep their HEAD; logs keep their tail; the save is whole or omitted.
    internal sealed class Caps { internal string Name; internal int Journal, Previous, Log, Save; }
    internal static readonly Caps DeflateCaps = new Caps { Name = "deflate", Journal = 16 * MiB, Previous = 4 * MiB, Log = 4 * MiB, Save = 16 * MiB };
    internal static readonly Caps StoredCaps = new Caps { Name = "stored", Journal = 3 * MiB, Previous = MiB, Log = 3 * MiB / 2, Save = 3 * MiB / 2 };
    internal sealed class Request
    {
        internal string Reports, ProfilePath, BepInExLog, PlayerLog, PluginDll, EngineDll, RuntimeManifest, Revision;
        internal string ReportId, MatchId;
        internal DateTime Utc;
        internal bool IncludeSave;
        // A preview builds and verifies the whole zip as <name>.zip.preview. Commit renames it into place, Discard deletes it.
        internal bool Preview;
        // Test switches only: force the STORED fallback without breaking the global probe; lower the zip limit.
        internal bool ForceStored;
        internal long ZipLimit = MaxZipBytes;
        internal ReportRedaction Redaction;
        internal Dictionary<string, object> Metadata = new Dictionary<string, object>();
    }
    internal sealed class Result
    {
        internal string ZipPath, ReportId, MatchId;
        // A preview has not been saved yet: ZipPath is the .preview file and FinalPath is where Commit puts it.
        internal string FinalPath;
        internal bool Pending => FinalPath != null && ZipPath != FinalPath;
        // Personal details (user name, PC name, folders) replaced across every entry.
        internal long Replaced;
        internal List<object> Entries;
        internal List<string> Leftovers;
        internal bool IncludeSave;
        internal string Compression, CapProfile;
        internal int Identities;
        internal JsonNode ToJson() => (JsonNode)Normalize(new Dictionary<string, object> {
            { "zipPath", ZipPath }, { "reportId", ReportId }, { "matchId", MatchId }, { "entries", Entries },
            { "pending", Pending }, { "replaced", Replaced },
            { "exportIncludeSave", IncludeSave }, { "compression", Compression }, { "capProfile", CapProfile },
            { "redaction", new Dictionary<string, object> { { "ok", Leftovers.Count == 0 }, { "leftovers", Leftovers }, { "identities", (long)Identities } } }
        });
    }
    // Blizzard MiniJSON serializes only its own JsonNode/JsonList containers, not BCL dictionaries/lists.
    internal static object Normalize(object value)
    {
        if (value is JsonNode || value is JsonList) return value;
        if (value is IDictionary dictionary)
        {
            var node = new JsonNode(); foreach (DictionaryEntry pair in dictionary) node.Add((string)pair.Key, Normalize(pair.Value)); return node;
        }
        if (value is IEnumerable sequence && !(value is string))
        {
            var list = new JsonList(); foreach (object item in sequence) list.Add(Normalize(item)); return list;
        }
        return value;
    }
    internal static string Serialize(object value) => Json.Serialize(Normalize(value));
    internal static string HashFile(string path)
    {
        using (var file = Open(path)) using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
    }
    private static FileStream Open(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    internal static Result Export(Request request)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(request.ReportId ?? "", @"^SR-[0-9]{8}-[0-9]{6}-[A-Za-z0-9]+-[a-f0-9]{4}$"))
            throw new ArgumentException("Invalid report id");
        bool deflate = !request.ForceStored && ReportArchive.DeflateAvailable;
        var caps = deflate ? DeflateCaps : StoredCaps;
        string directory = Path.Combine(request.Reports, "bug-reports"); Directory.CreateDirectory(directory);
        string finalPath = Path.Combine(directory, "SoloStone-report-" + request.ReportId + ".zip");
        string path = request.Preview ? finalPath + PreviewSuffix : finalPath;
        // An earlier preview the player never saved (the game closed): not a report, so it goes.
        if (request.Preview) DeleteStalePreviews(directory);
        bool created = false;
        try
        {
            Built built;
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                while (true)
                {
                    built = Collect(request, caps, deflate);
                    file.SetLength(0); file.Position = 0; ReportArchive.Write(file, built.Entries, deflate); file.Flush();
                    if (file.Length < request.ZipLimit) break;
                    // Incompressible sources: rebuild once with the small STORED budgets, still deflated.
                    if (caps == StoredCaps)
                        throw new IOException("Report size limit exceeded: " + file.Length + " bytes (" + Mode(deflate) + ", " + caps.Name + " caps) >= " + request.ZipLimit);
                    caps = StoredCaps;
                }
            }
            var hits = ReportArchive.Check(path, request.Redaction, request.ZipLimit);
            if (hits.Count != 0) throw new IOException("Report redaction verification failed (" + hits.Count + " kinds)");
            return new Result { ZipPath = path, FinalPath = finalPath, ReportId = request.ReportId, MatchId = built.Match, Entries = built.Details, Leftovers = hits,
                IncludeSave = request.IncludeSave, Compression = Mode(deflate), CapProfile = caps.Name, Identities = request.Redaction.Identities,
                Replaced = built.Replaced };
        }
        catch { if (created) { try { File.Delete(path); } catch { } } throw; }
    }
    private static string Mode(bool deflate) => deflate ? "deflate" : "stored";
    internal const string PreviewSuffix = ".preview";
    // The previewed zip becomes the report, byte for byte: what the player looked at is what they attach.
    internal static void Commit(Result result)
    {
        if (!result.Pending) return;
        if (!File.Exists(result.ZipPath)) throw new IOException("The previewed report is gone; preview it again.");
        if (File.Exists(result.FinalPath)) throw new IOException("A report with this id already exists.");
        File.Move(result.ZipPath, result.FinalPath);
        result.ZipPath = result.FinalPath;
    }
    internal static void Discard(Result result)
    {
        if (result == null || !result.Pending) return;
        try { File.Delete(result.ZipPath); } catch (Exception) { }
    }
    private static void DeleteStalePreviews(string directory)
    {
        try { foreach (string stale in Directory.GetFiles(directory, "*.zip" + PreviewSuffix)) { try { File.Delete(stale); } catch (Exception) { } } }
        catch (Exception) { }
    }
    // The placeholders the redaction leaves behind. Counted in the finished entries, so the number is what is in the zip.
    private static readonly string[] Placeholders = { "<user>", "<machine>", "<localappdata>", "<userprofile>", "<battlenet>", "<solostone>", "<hearthstone>", "<saves>" };
    internal static long CountReplaced(IEnumerable<ReportArchive.Entry> entries)
    {
        long total = 0;
        foreach (var entry in entries)
            foreach (string placeholder in Placeholders)
            {
                byte[] needle = Encoding.ASCII.GetBytes(placeholder);
                for (int at = IndexOf(entry.Bytes, needle, 0); at >= 0; at = IndexOf(entry.Bytes, needle, at + needle.Length)) total++;
            }
        return total;
    }
    private static int IndexOf(byte[] data, byte[] needle, int start)
    {
        for (int i = start; i + needle.Length <= data.Length; i++)
        {
            if (data[i] != needle[0]) continue;
            int j = 1; while (j < needle.Length && data[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }
    private sealed class Built { internal List<ReportArchive.Entry> Entries; internal List<object> Details; internal string Match; internal long Replaced; }
    private static Built Collect(Request request, Caps caps, bool deflate)
    {
        var entries = new List<ReportArchive.Entry>(); var details = new List<object>(); var missing = new List<string>();
        var metadata = new Dictionary<string, object>(request.Metadata);
        string match = request.MatchId;
        string folder = Path.Combine(request.Reports, "playtest");
        string[] journals = new string[0];
        try { journals = new DirectoryInfo(folder).GetFiles("*.jsonl").OrderByDescending(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.Ordinal).Take(2).Select(file => file.FullName).ToArray(); }
        catch (Exception) { missing.Add("journals/"); }
        if (journals.Length == 0 && !missing.Contains("journals/")) missing.Add("journals/");
        if (string.IsNullOrEmpty(match) && journals.Length != 0) match = Path.GetFileNameWithoutExtension(journals[0]);
        for (int i = 0; i < journals.Length; i++)
            Add(request, entries, details, missing, journals[i], "journals/" + Path.GetFileName(journals[i]), i == 0 ? caps.Journal : caps.Previous, true);
        Add(request, entries, details, missing, request.BepInExLog, "logs/LogOutput.log", caps.Log, false);
        Add(request, entries, details, missing, request.PlayerLog, "logs/Player.log", caps.Log, false);
        // The selected profile is self-contained XML; backup history/other saves and lock/transaction files are excluded.
        // A cut save is useless and misleading: include it whole or omit it.
        if (request.IncludeSave) AddSave(request, entries, details, missing, metadata, caps.Save);
        metadata["schema"] = 1; metadata["reportId"] = request.ReportId; metadata["utc"] = request.Utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        metadata["matchId"] = match; metadata["includeSave"] = request.IncludeSave;
        metadata["compression"] = Mode(deflate); metadata["capProfile"] = caps.Name;
        metadata["journals"] = journals.Select(Path.GetFileName).ToArray(); metadata["entries"] = details; metadata["missing"] = missing;
        metadata["pluginSha256"] = HashOrMissing(request.PluginDll, "pluginSha256", missing);
        metadata["engineSha256"] = HashOrMissing(request.EngineDll, "engineSha256", missing);
        string runtime = OptionalText(request.RuntimeManifest, "runtimeManifest", missing);
        var manifest = string.IsNullOrEmpty(runtime) ? null : Json.Deserialize(runtime) as JsonNode;
        metadata["runtimeManifest"] = manifest != null && manifest.ContainsKey("active") ? manifest["active"] : null;
        metadata["runtimeLabel"] = OptionalText(request.Revision, "runtimeLabel", missing);
        // report.json lists itself; size is informational for the fixed-point serialized metadata, not a source file.
        var report = new Dictionary<string, object> { { "name", "report.json" }, { "originalBytes", 0L }, { "size", 0L }, { "truncated", false } };
        details.Add(report);
        byte[] json = null;
        for (int i = 0; i < 6; i++)
        {
            string original = Serialize(metadata); json = Encoding.UTF8.GetBytes(request.Redaction.Redact(original, true));
            long originalSize = Encoding.UTF8.GetByteCount(original);
            if ((long)report["size"] == json.Length && (long)report["originalBytes"] == originalSize) break;
            report["size"] = (long)json.Length; report["originalBytes"] = originalSize;
        }
        if (json.Length > 128 * 1024) throw new IOException("Report metadata size limit exceeded");
        entries.Add(new ReportArchive.Entry { Name = "report.json", Bytes = json });
        return new Built { Entries = entries, Details = details, Match = match, Replaced = CountReplaced(entries) };
    }
    private static string HashOrMissing(string path, string name, List<string> missing)
    {
        try { return HashFile(path); } catch (Exception) { missing.Add(name); return null; }
    }
    private static string OptionalText(string path, string name, List<string> missing)
    {
        try { using (var file = Open(path)) using (var reader = new StreamReader(file)) { if (file.Length > 256 * 1024) throw new IOException(); return reader.ReadToEnd(); } }
        catch (Exception) { missing.Add(name); return null; }
    }
    private static void AddSave(Request request, List<ReportArchive.Entry> entries, List<object> details, List<string> missing,
        Dictionary<string, object> metadata, int cap)
    {
        const string name = "save/profile.xml";
        try
        {
            byte[] raw;
            using (var file = Open(request.ProfilePath))
            {
                long length = file.Length;
                if (length > cap) { TooLarge(missing, metadata, length, cap); return; }
                // Read to EOF (bounded): the selected profile is replaced atomically, so this handle sees one whole file.
                using (var copy = new MemoryStream((int)length))
                {
                    var buffer = new byte[81920]; int got;
                    while ((got = file.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        copy.Write(buffer, 0, got);
                        if (copy.Length > cap) { TooLarge(missing, metadata, copy.Length, cap); return; }
                    }
                    raw = copy.ToArray();
                }
            }
            byte[] bytes = Encoding.UTF8.GetBytes(request.Redaction.Redact(Encoding.UTF8.GetString(raw)));
            if (bytes.Length > cap) { TooLarge(missing, metadata, raw.Length, cap); return; }
            entries.Add(new ReportArchive.Entry { Name = name, Bytes = bytes });
            details.Add(new Dictionary<string, object> { { "name", name }, { "originalBytes", (long)raw.Length }, { "size", (long)bytes.Length }, { "truncated", false } });
        }
        catch (Exception) { missing.Add(name); }
    }
    private static void TooLarge(List<string> missing, Dictionary<string, object> metadata, long bytes, int cap)
    {
        missing.Add("save-too-large"); metadata["saveTooLarge"] = new Dictionary<string, object> { { "bytes", bytes }, { "limit", (long)cap } };
    }
    private static void Add(Request request, List<ReportArchive.Entry> entries, List<object> details, List<string> missing,
        string path, string name, int cap, bool journal)
    {
        try
        {
            byte[] raw; long original, cut, eventBytes = 0; bool capped;
            using (var file = Open(path))
            {
                // Stable length snapshot: the live journal is still appended by the bridge's background writer.
                original = file.Length;
                // Reserve marker/redaction expansion space inside each entry's budget.
                int length = (int)Math.Min(original, cap - 1024); raw = new byte[length];
                capped = original > length; bool tail = !journal && capped;
                if (tail) file.Seek(original - length, SeekOrigin.Begin);
                int read = 0, got;
                if (journal && capped)
                {
                    // A journal that does not fit loses its match event rows first, never replay rows: they are diagnostics, the
                    // BepInEx log keeps the lines, and the head of the replay rows is what an investigation needs.
                    int all = (int)Math.Min(original, 24L * MiB); var whole = new byte[all];
                    while (read < all && (got = file.Read(whole, read, all - read)) > 0) read += got;
                    if (read != all) Array.Resize(ref whole, read);
                    whole = Slice(whole, 0, Array.LastIndexOf(whole, (byte)'\n') + 1);
                    raw = RemoveEventRows(whole);
                    eventBytes = whole.Length - raw.Length;
                    if (raw.Length > length) raw = Slice(raw, 0, Array.LastIndexOf(raw, (byte)'\n', length - 1) + 1);
                    read = length = raw.Length;
                    capped = original - eventBytes > raw.Length;
                }
                else
                {
                while (read < length && (got = file.Read(raw, read, length - read)) > 0) read += got;
                }
                if (read != length) Array.Resize(ref raw, read);
                // Only complete JSONL rows keep sequence/delta replay valid (the snapshot may end mid-row). Logs can lose their first partial line.
                if (journal) raw = Slice(raw, 0, Array.LastIndexOf(raw, (byte)'\n') + 1);
                else if (capped) { int line = Array.IndexOf(raw, (byte)'\n'); if (line >= 0) raw = Slice(raw, line + 1, raw.Length - line - 1); }
                // Bytes beyond the snapshot (journal rows appended after it) are not a cap cut.
                cut = journal && !capped ? 0 : Math.Max(0, original - eventBytes - raw.Length);
            }
            string text = request.Redaction.Redact(Encoding.UTF8.GetString(raw), journal);
            // Redaction can expand tiny usernames: cap again after replacement, retaining complete UTF-8 text/JSONL rows.
            while (Encoding.UTF8.GetByteCount(text) > cap - 512)
            {
                int remove = Math.Max(1, raw.Length / 10);
                if (journal) { int end = Array.LastIndexOf(raw, (byte)'\n', Math.Max(0, raw.Length - remove - 1)); raw = Slice(raw, 0, end + 1); }
                else
                {
                    // Keep log lines whole: realign to the byte after the next newline before redacting again.
                    int line = Array.IndexOf(raw, (byte)'\n', Math.Max(0, Math.Min(remove, raw.Length - 1)));
                    raw = line < 0 ? new byte[0] : Slice(raw, line + 1, raw.Length - line - 1);
                }
                cut = original - raw.Length;
                text = request.Redaction.Redact(Encoding.UTF8.GetString(raw), journal);
            }
            if (cut > 0)
            {
                // match_log.py requires the header and all prior prefix-suffix snapshots: keep HEAD, mark at end.
                string marker = journal ? Serialize(new Dictionary<string, object> { { "type", "recording-stopped" }, { "reason", "report cap: " + cut + " bytes cut; head retained" } }) + "\n"
                    : "[SoloStone report: " + cut + " bytes cut; tail retained]\n";
                text = journal ? text + marker : marker + text;
            }
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            string safeName = request.Redaction.Redact(name);
            entries.Add(new ReportArchive.Entry { Name = safeName, Bytes = bytes });
            details.Add(new Dictionary<string, object> { { "name", safeName }, { "originalBytes", original }, { "size", (long)bytes.Length }, { "truncated", cut > 0 }, { "eventRowsOmitted", eventBytes > 0 } });
        }
        catch (Exception) { missing.Add(request.Redaction.Redact(name)); }
    }
    private static readonly byte[] EventRowPrefix = Encoding.ASCII.GetBytes("{\"type\":\"events\"");
    // Whole rows of a JSONL byte block, minus the match event rows.
    internal static byte[] RemoveEventRows(byte[] jsonl)
    {
        var kept = new MemoryStream(jsonl.Length);
        int start = 0;
        while (start < jsonl.Length)
        {
            int end = Array.IndexOf(jsonl, (byte)'\n', start);
            end = end < 0 ? jsonl.Length : end + 1;
            bool events = end - start >= EventRowPrefix.Length;
            for (int i = 0; events && i < EventRowPrefix.Length; i++) events = jsonl[start + i] == EventRowPrefix[i];
            if (!events) kept.Write(jsonl, start, end - start);
            start = end;
        }
        return kept.ToArray();
    }
    private static byte[] Slice(byte[] source, int start, int count)
    {
        if (start == 0 && count == source.Length) return source;
        var copy = new byte[Math.Max(0, count)]; Array.Copy(source, start, copy, 0, copy.Length); return copy;
    }
}
