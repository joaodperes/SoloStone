using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MiniJSON;

// Unity owns capture/ring access. The shared disk worker owns all file access.
internal sealed class FxTelemetryRecord
{
    internal string RunId, MatchId, Event, BlockType, CardId, EffectCardId, Side, ControllerType, Prefab, SpellName, Error;
    internal string BlockingCauses, BlockingSpells, BlockingSpellControllers, StateName, ReachedStateName;
    internal int? Frame, TaskList, SourceEntity, EffectIndex, SubOption, Controller, EffectId, SpellId, State, RequestedState, Targets;
    internal int? SubSpellId, TriggerKeyword;
    internal float? Realtime;
    internal bool? EffectPresent, SpellPresent, Result, LocalPending, LocalActive, LocalUnresolved;
    internal const string Header = "sequence\trunId\tmatchId\tevent\tframe\trealtime\ttaskList\tblockType\tsourceEntity\tcardId\teffectCardId\teffectIndex\tsubOption\tcontroller\tside\tcontrollerType\teffectId\teffectPresent\tprefab\tspellId\tspellPresent\tspellName\tstate\trequestedState\ttargets\tresult\tlocalPending\tlocalActive\tlocalUnresolved\terror\tblockingCauses\tblockingSpells\tblockingSpellControllers\tsubSpellId\ttriggerKeyword\tstateName\treachedStateName";

    private object[] Values(int sequence) => new object[] {
        sequence, RunId, MatchId, Event, Frame, Realtime, TaskList, BlockType, SourceEntity, CardId, EffectCardId,
        EffectIndex, SubOption, Controller, Side, ControllerType, EffectId, EffectPresent, Prefab, SpellId, SpellPresent,
        SpellName, State, RequestedState, Targets, Result, LocalPending, LocalActive, LocalUnresolved, Error,
        BlockingCauses, BlockingSpells, BlockingSpellControllers, SubSpellId, TriggerKeyword, StateName, ReachedStateName
    };
    private static readonly string[] Columns = Header.Split('\t');
    internal Captured Capture(int sequence) => new Captured(Values(sequence));
    private static int Limit(int column) => column >= 30 && column <= 32 ? BlockingDiagnostics.MaxLength : 256;
    internal static string Bound(string text, int limit) => text.Length > limit ? text.Substring(0, limit - 1) + "…" : text;
    internal sealed class Captured
    {
        private readonly object[] values;
        internal Captured(object[] values)
        {
            this.values = values;
            for (int i = 0; i < values.Length; i++)
                if (values[i] is string text) values[i] = Bound(text, Limit(i));
        }
        internal string Format() => string.Join("\t", values.Select(Cell));
        internal Dictionary<string, object> Journal()
        {
            var row = new Dictionary<string, object>(values.Length + 2);
            for (int i = 0; i < values.Length; i++)
                if (values[i] != null) row[i == 0 ? "fxSequence" : Columns[i]] = values[i];
            return row;
        }
    }
    internal bool HoverOnly => Error == null && Result != false && SpellPresent != false && EffectPresent != false
        && TaskList == null && SubSpellId == null
        && (HasHoverName(Prefab) || HasHoverName(SpellName));
    internal static bool HasHoverName(string name) => name != null
        && name.IndexOf("ProposeUsageSpell", StringComparison.OrdinalIgnoreCase) >= 0;

    private static string Cell(object value)
    {
        if (value == null) return "null";
        string text = value is bool flag ? (flag ? "1" : "0") : Convert.ToString(value, CultureInfo.InvariantCulture);
        return text.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
    }
}

internal sealed class FxTelemetryLog : IDisposable
{
    private const long MaxBytes = 20 * 1024 * 1024;
    private readonly string path;
    private readonly Queue<KeyValuePair<int, string>> records = new Queue<KeyValuePair<int, string>>();
    private readonly Queue<Dictionary<string, object>> history = new Queue<Dictionary<string, object>>();
    private float? evictedRealtime;
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
    private bool headerChecked;
    internal int Sequence { get; private set; }
    private BackgroundDiskWriter disk;
    internal bool DiskStopped => disk != null && !disk.Accepting;
    internal string DiskError => disk?.Error != null ? disk.Error.GetType().Name + ": " + disk.Error.Message : disk?.StopReason;
    internal int HoverFiltered { get; private set; }
    internal FxTelemetryLog(string path) { this.path = path; }
    internal bool FilterHoverName(string name)
    {
        if (!FxTelemetryRecord.HasHoverName(name)) return false;
        HoverFiltered++; return true;
    }
    internal static bool Enabled(bool configured, bool testControl, bool inMatch) => inMatch && (configured || testControl);

    internal bool Record(bool configured, bool testControl, bool inMatch, FxTelemetryRecord record, bool writeDisk = true,
        Action<Dictionary<string, object>> journal = null, bool trackLifecycle = true)
    {
        if (!Enabled(configured, testControl, inMatch)) return false;
        if (record.HoverOnly) { HoverFiltered++; return false; }
        var captured = record.Capture(++Sequence);
        string line = captured.Format();
        records.Enqueue(new KeyValuePair<int, string>(Sequence, line));
        var journalRow = journal != null || trackLifecycle ? captured.Journal() : null;
        if (records.Count > 512) records.Dequeue();
        if (trackLifecycle)
        {
            history.Enqueue(journalRow);
            if (history.Count > 512)
            {
                var evicted = history.Dequeue();
                if (evicted.TryGetValue("realtime", out object time)) evictedRealtime = Convert.ToSingle(time, CultureInfo.InvariantCulture);
            }
        }
        journal?.Invoke(journalRow);
        if (!writeDisk || DiskStopped) return true;
        if (disk == null) disk = new BackgroundDiskWriter(_ => { }, _ => { });
        disk.TryEnqueue(() => Append(line), Utf8.GetByteCount(line));
        return true;
    }
    internal bool FlushAndWait(int milliseconds = BackgroundDiskWriter.ShutdownMilliseconds) => disk?.WaitForIdle(milliseconds) ?? true;
    public void Dispose() { disk?.Complete(); disk?.WaitForClose(); }
    private void Append(string line)
    {
        if (disk.StopReason == "size-limit") return;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        if (!headerChecked && File.Exists(path))
        {
            string oldHeader;
            using (var reader = new StreamReader(path, Utf8, true)) oldHeader = reader.ReadLine();
            if (oldHeader != null && oldHeader != FxTelemetryRecord.Header)
            {
                string hash;
                using (var sha = SHA256.Create())
                    hash = BitConverter.ToString(sha.ComputeHash(Utf8.GetBytes(oldHeader))).Replace("-", "").ToLowerInvariant();
                string stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", CultureInfo.InvariantCulture);
                File.Move(path, Path.Combine(Path.GetDirectoryName(path), "fx-telemetry-" + stamp + "-" + hash + ".tsv"));
            }
        }
        using (var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
        {
            headerChecked = true;
            string text = (file.Length == 0 ? FxTelemetryRecord.Header + "\n" : "") + line + "\n";
            byte[] bytes = Utf8.GetBytes(text);
            if (file.Length + bytes.Length > MaxBytes) { disk.Complete("size-limit"); return; }
            file.Seek(0, SeekOrigin.End);
            file.Write(bytes, 0, bytes.Length);
        }
    }

    internal JsonNode Status(float now, int seconds, string name, int source, string match,
        IDictionary<int, string> live, IEnumerable<Dictionary<string, object>> identities = null)
    {
        float cutoff = now - seconds;
        var rows = history.Concat(identities ?? Enumerable.Empty<Dictionary<string, object>>()).Where(row => row.TryGetValue("matchId", out var id) && Equals(id, match)).ToArray();
        bool Match(Dictionary<string, object> row) =>
            (source == 0 || row.TryGetValue("sourceEntity", out var entity) && Convert.ToInt32(entity) == source)
            && (string.IsNullOrEmpty(name) || new[] { "prefab", "spellName" }.Any(key => row.TryGetValue(key, out var value)
                && Convert.ToString(value).IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0));
        var groups = rows.Where(row => row.ContainsKey("spellId")).GroupBy(row => Convert.ToInt32(row["spellId"]))
            .Where(group => group.Any(Match)).OrderBy(group => group.Key);
        var spells = new JsonList(); int activeCount = 0, matches = 0;
        foreach (var group in groups)
        {
            int id = group.Key; var all = group.ToArray();
            object Latest(string key) => all.LastOrDefault(row => row.ContainsKey(key)) is Dictionary<string, object> row ? row[key] : null;
            var recent = all.Where(row => row.TryGetValue("realtime", out var time) && Convert.ToSingle(time) >= cutoff
                && (source == 0 || !row.TryGetValue("sourceEntity", out var entity) || Convert.ToInt32(entity) == source)).ToArray();
            bool exists = live.TryGetValue(id, out var current);
            bool active = exists && current != "NONE" && current != "0"
                && (source == 0 || Latest("sourceEntity") is object entity && Convert.ToInt32(entity) == source);
            if (recent.Length == 0 && !active) continue;
            if (active) activeCount++;
            matches++;
            if (spells.Count >= 128) continue;
            var states = recent.SelectMany(row => new[] { "stateName", "reachedStateName" }
                .Where(row.ContainsKey).Select(key => Convert.ToString(row[key]))).Distinct().Cast<object>();
            spells.Add(new JsonNode { { "spellId", id }, { "spellName", Latest("spellName") }, { "prefab", Latest("prefab") },
                { "sourceEntity", source == 0 ? Latest("sourceEntity") : source }, { "states", TestControlProtocol.List(states) },
                { "currentSourceEntity", Latest("sourceEntity") }, { "currentState", exists ? current : "destroyed" }, { "active", active }, { "events", recent.Length } });
        }
        // A capped ring is evidence only for the portion of the requested window it still retains.
        return new JsonNode { { "seconds", seconds }, { "now", now }, { "name", name }, { "sourceEntity", source },
            { "windowComplete", !evictedRealtime.HasValue || evictedRealtime.Value < cutoff },
            { "spells", spells }, { "activeCount", activeCount }, { "matched", matches > 0 }, { "totalMatches", matches }, { "truncated", matches > spells.Count } };
    }

    internal JsonNode Read(int after, int limit)
    {
        var page = new List<KeyValuePair<int, string>>();
        int bytes = 0;
        foreach (var record in records.Where(record => record.Key > after).Take(limit))
        {
            bytes += Utf8.GetByteCount(Json.Serialize(record.Value)) + 1;
            if (bytes > TestControlProtocol.MaxResponse / 2) break;
            page.Add(record);
        }
        int first = records.Count == 0 ? 0 : records.Peek().Key;
        return new JsonNode {
            { "header", FxTelemetryRecord.Header }, { "records", TestControlProtocol.List(page.Select(record => (object)record.Value)) },
            { "firstSequence", first }, { "lastSequence", Sequence }, { "nextSequence", page.Count == 0 ? after : page.Last().Key },
            { "dropped", first > 0 && after < first - 1 }, { "diskStopped", DiskStopped }, { "diskError", DiskError },
            { "hoverFiltered", HoverFiltered }
        };
    }
}
