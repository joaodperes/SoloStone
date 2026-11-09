using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using EngineGame = SabberStoneCore.Model.Game;

// No profile references: recording can fail without participating in match settlement.
public sealed class PlaytestRecord : IMatchRecorder, IDisposable
{
#if SOLOSTONE_RELEASE
    public static string RecordDirectory = null;
#else
    public static string RecordDirectory = @"C:\hs-native-ui-"+@"prototype\reports\playtest";
#endif
    public const int RetainedMatches = 50;
    public const long MaximumBytes = 16 * 1024 * 1024;
    public const long TotalMaximumBytes = 100 * 1024 * 1024;
    // Owned exclusively by the shared disk worker, including bounded-close tails from prior matches.
    private static readonly HashSet<string> ActiveFiles = new HashSet<string>(StringComparer.Ordinal);
    private string activePath;
    private long writtenBytes;
    private int bufferedFx;
    private readonly StringBuilder fxBatch = new StringBuilder();
    private string lastHuman, lastNpc;
    public long LastSequence { get; private set; }
    private readonly long maximumBytes;
    private TextWriter writer;
    private readonly Func<Dictionary<string, object>> header;
    private readonly Action<string> log;
    private readonly BackgroundDiskWriter disk;
    private Dictionary<string, object> pending;
    private bool started, sizeStopped, humanActionPending;
    private readonly Func<DateTime> utcNow;
    private readonly Func<double> elapsedMilliseconds;
    private readonly string startedUtc;
    private double lastHumanMs, longestHumanGapMs, humanIdleOver60sMs;
    private int humanActions;
    private volatile bool failed, closed;
    public bool Enabled => !failed && !closed && disk.Accepting;
    public PlaytestRecord(TextWriter writer, Dictionary<string, object> header, Action<string> log = null,
        long maximumBytes = MaximumBytes, int queueCapacity = BackgroundDiskWriter.DefaultCapacity,
        Func<DateTime> utcNow = null, Func<double> elapsedMilliseconds = null, long eventBudgetBytes = MaximumEventBytes)
        : this(writer, () => header, log, maximumBytes, queueCapacity, utcNow, elapsedMilliseconds) { eventBudget = eventBudgetBytes; }
    private PlaytestRecord(TextWriter writer, Func<Dictionary<string, object>> header, Action<string> log,
        long maximumBytes, int queueCapacity = BackgroundDiskWriter.DefaultCapacity,
        Func<DateTime> utcNow = null, Func<double> elapsedMilliseconds = null)
    {
        this.writer = writer; this.header = header; this.log = log; this.maximumBytes = maximumBytes; eventBudget = MaximumEventBytes;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        this.elapsedMilliseconds = elapsedMilliseconds ?? (() => Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency));
        startedUtc = this.utcNow().ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        lastHumanMs = this.elapsedMilliseconds();
        disk = new BackgroundDiskWriter(Finish, Fail, queueCapacity);
    }
    public static PlaytestRecord Open(string directory, string matchId, Func<Dictionary<string, object>> header,
        Action<string> log = null, Assembly pluginAssembly = null)
    {
        var recorder = new PlaytestRecord(null, header, log, MaximumBytes);
        recorder.pluginAssembly = pluginAssembly;
        recorder.disk.TryEnqueue(() => {
            if (string.IsNullOrEmpty(matchId) || matchId.Any(c => !(char.IsLetterOrDigit(c) || c == '-')))
                throw new ArgumentException("Invalid playtest match id.");
            Directory.CreateDirectory(directory);
            string path = Path.GetFullPath(Path.Combine(directory, matchId + ".jsonl"));
            recorder.writer = new StreamWriter(new FileStream(path,
                FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            recorder.activePath = path; ActiveFiles.Add(path);
            recorder.engineHash = AssemblyHash(typeof(EngineGame).Assembly);
            if (pluginAssembly != null) recorder.pluginHash = AssemblyHash(pluginAssembly);
            recorder.Retain(directory);
        });
        return recorder;
    }
    private string engineHash, pluginHash;
    private Assembly pluginAssembly;
    private void Retain(string directory)
    {
        try
        {
            var files = new DirectoryInfo(directory).GetFiles("*.jsonl");
            int active = files.Count(f => ActiveFiles.Contains(f.FullName));
            long budget = TotalMaximumBytes - active * MaximumBytes;
            var oldFiles = files.Where(f => !ActiveFiles.Contains(f.FullName))
                .OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal).ToList();
            long total = oldFiles.Sum(f => f.Length);
            int remaining = oldFiles.Count;
            foreach (var old in oldFiles)
            {
                if (remaining + active <= RetainedMatches && total <= budget) break;
                try
                {
                    long length = old.Length;
                    // Read-only journals are ineligible on Unix too (unlink otherwise ignores this flag).
                    if ((old.Attributes & FileAttributes.ReadOnly) != 0) throw new IOException("Read-only journal.");
                    old.Delete(); total -= length; remaining--;
                }
                catch (Exception error) { Warn("playtest retention skipped " + old.Name + ": " + error.Message); }
            }
            if (remaining + active > RetainedMatches || total > budget)
                Warn("playtest retention budget unavailable; old journals could not be removed.");
        }
        catch (Exception error) { Warn("playtest retention failed: " + error.Message); }
    }
    private void Warn(string message) { try { log?.Invoke(message); } catch { } }
    private void Fail(Exception error)
    {
        failed = true;
        Warn("playtest-record disabled: " + error.Message);
    }
    private void Finish(string reason)
    {
        try
        {
            if (writer != null && reason != null)
            {
                FlushOnDisk();
                writer.WriteLine(Serialize(new Dictionary<string, object> { { "type", "recording-stopped" }, { "reason", reason } }));
                Warn("playtest-record disabled: " + reason + "; replayable prefix retained.");
            }
            FlushOnDisk();
        }
        finally
        {
            try { writer?.Dispose(); }
            finally { writer = null; if (activePath != null) ActiveFiles.Remove(activePath); }
        }
    }
    private void Write(Dictionary<string, object> row, bool fx = false, string serialized = null)
    {
        if (!Enabled) return;
        try
        {
            if (!started)
            {
                // Capture all live objects on Unity; closures below carry only immutable JSON text.
                var opening = new Dictionary<string, object>(header());
                opening["startedUtc"] = startedUtc;
                opening["snapshotEncoding"] = "prefix-suffix-v1";
                opening.Remove("engineAssemblySha256");
                if (pluginAssembly != null) opening.Remove("pluginBuild");
                string text = Serialize(opening);
                if (!disk.TryEnqueue(() => {
                    var hashes = ",\"engineAssemblySha256\":" + Serialize(engineHash);
                    if (pluginHash != null) hashes += ",\"pluginBuild\":" + Serialize(pluginHash);
                    Line(text.Substring(0, text.Length - 1) + hashes + "}");
                }, Encoding.UTF8.GetByteCount(text))) return;
                started = true;
            }
            string line = serialized ?? Serialize(row);
            // Event rows and their markers sit before this row, so they reference the sequence completed BEFORE it.
            long carrierAfter = row.TryGetValue("sequence", out object rowSequence) ? Convert.ToInt64(rowSequence, CultureInfo.InvariantCulture) - 1 : LastSequence;
            string events = TakeEvents(carrierAfter);
            disk.TryEnqueue(() => { WriteEvents(events, carrierAfter); Line(line, fx); if (!fx || ++bufferedFx >= 32) FlushOnDisk(); },
                Encoding.UTF8.GetByteCount(line) + (events == null ? 0 : Encoding.UTF8.GetByteCount(events)));
        }
        catch (Exception)
        {
            // Even capture failures close/mark on the disk thread, never through a synchronous logger.
            failed = true; disk.Complete("capture-failed");
        }
    }
    private void Line(string line, bool fx = false)
    {
        if (sizeStopped) return;
        long bytes = Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(writer.NewLine);
        if (writtenBytes + bytes > maximumBytes - 256)
        {
            sizeStopped = true; disk.Complete("size-limit"); return;
        }
        if (fx) fxBatch.Append(line).Append(writer.NewLine);
        else { FlushOnDisk(); writer.WriteLine(line); }
        writtenBytes += bytes;
    }
    private void FlushOnDisk()
    {
        if (fxBatch.Length != 0) { writer.Write(fxBatch.ToString()); fxBatch.Clear(); bufferedFx = 0; }
        writer?.Flush();
    }
    // Telemetry tick only requests a flush. Terminal paths may wait at most 250ms.
    public void Flush()
    {
        if (!Enabled) return;
        long flushAfter = LastSequence;
        string events = TakeEvents(flushAfter);
        // Waiting event rows go out alone only when the queue has room; otherwise they wait for the next row (never fatal).
        if (events != null && !disk.TryEnqueueOptional(() => { WriteEvents(events, flushAfter); FlushOnDisk(); }, Encoding.UTF8.GetByteCount(events)))
        { pendingEvents.Insert(0, events.TrimEnd('\n')); pendingEventBytes += events.Length; }
        else if (events == null) disk.TryEnqueue(FlushOnDisk);
    }
    public bool FlushAndWait(int milliseconds = BackgroundDiskWriter.ShutdownMilliseconds)
    { Flush(); return disk.WaitForIdle(milliseconds); }
    public void OnFx(Dictionary<string, object> row)
    {
        if (!Enabled) return;
        try
        {
            // Caller hands off a fresh scalar row; Write serializes it before returning.
            row["type"] = "fx"; row["afterSequence"] = LastSequence; Write(row, true);
        }
        catch (Exception) { failed = true; disk.Complete("capture-failed"); }
    }
    // Local diagnostics, like the opening decks and NPC decisions; never sent to the gameplay UI.
    public string OnChoiceOpen(Dictionary<string, object> row)
    {
        row["type"] = "choice-open"; row["afterSequence"] = LastSequence;
        string serialized = Serialize(row);
        Write(row, serialized: serialized);
        return serialized;
    }
    // Match event log lines (MatchEventLog), one `events` row per history batch. Diagnostics must never cost replay data:
    //  - they ride in the disk work item of the NEXT replay/fx row, so they take no queue slot of their own (a queue overflow
    //    closes the whole journal, replay rows included); alone they only use the optional, non-fatal enqueue;
    //  - they are budgeted separately from the journal's 16 MiB: replay rows never compete with them for the size limit;
    //  - waiting rows are capped in memory.
    // Past the budget a single `truncated` row is written; the BepInEx log keeps the lines the per-turn cap allowed.
    public const long MaximumEventBytes = 3L * 1024 * 1024;
    private const int MaximumPendingEventBytes = 512 * 1024;
    private readonly long eventBudget;
    private long eventBytesWritten;
    private bool eventsStopped, eventsMarked;
    private readonly List<string> pendingEvents = new List<string>();
    private int pendingEventBytes, eventsDropped;
    public void OnEvents(string[] lines)
    {
        if (!Enabled || lines == null || lines.Length == 0) return;
        try
        {
            var row = new Dictionary<string, object> { { "type", "events" }, { "afterSequence", LastSequence }, { "lines", lines } };
            string serialized = Serialize(row);
            int bytes = Encoding.UTF8.GetByteCount(serialized);
            if (pendingEventBytes + bytes > MaximumPendingEventBytes) { eventsDropped += lines.Length; return; }
            pendingEvents.Add(serialized); pendingEventBytes += bytes;
        }
        catch (Exception) { eventsDropped += lines.Length; }
    }
    // Takes the waiting event rows for the next disk work item (producer thread only). Marker rows are stamped with the
    // afterSequence of the rows they sit next to (the carrier's), never with LastSequence read at some later moment:
    // match_log.py rejects an events row whose afterSequence is not the action count at its position.
    private string TakeEvents(long markerAfter)
    {
        if (pendingEvents.Count == 0 && eventsDropped == 0) return null;
        var text = new StringBuilder();
        foreach (var row in pendingEvents) text.Append(row).Append('\n');
        if (eventsDropped != 0)
        {
            text.Append(Serialize(new Dictionary<string, object> { { "type", "events" }, { "afterSequence", markerAfter }, { "lines", new string[0] },
                { "truncated", eventsDropped + " event lines were not recorded here (writer backlog); the BepInEx log has them (its per-turn cap applies)" } })).Append('\n');
            eventsDropped = 0;
        }
        pendingEvents.Clear(); pendingEventBytes = 0;
        return text.ToString();
    }
    // Disk thread. Event rows are whole lines; each has its own byte budget and is buffered with the fx batch.
    private void WriteEvents(string text, long markerAfter)
    {
        if (text == null || eventsStopped || sizeStopped) return;
        foreach (var line in text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            long bytes = Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(writer.NewLine);
            if (eventBytesWritten + bytes > eventBudget)
            {
                eventsStopped = true;
                if (!eventsMarked)
                {
                    eventsMarked = true;
                    fxBatch.Append(Serialize(new Dictionary<string, object> { { "type", "events" }, { "afterSequence", markerAfter }, { "lines", new string[0] },
                        { "truncated", "event rows stop at " + eventBudget + " bytes; the BepInEx log has the rest (its per-turn cap applies)" } })).Append(writer.NewLine);
                }
                return;
            }
            eventBytesWritten += bytes;
            fxBatch.Append(line).Append(writer.NewLine);
        }
    }
    private static Dictionary<string, object> Event(string type) => new Dictionary<string, object> { { "type", type } };
    private void HumanAction()
    {
        // Mulligan/concede can invoke two hooks before the same post-action hash.
        if (humanActionPending) return;
        humanActionPending = true;
        double now = elapsedMilliseconds(), gap = Math.Max(0, now - lastHumanMs);
        lastHumanMs = now;
        humanActions++;
        longestHumanGapMs = Math.Max(longestHumanGapMs, gap);
        humanIdleOver60sMs += Math.Max(0, gap - 60000);
    }
    public void OnMulligan(int playerId, IReadOnlyList<int> keptEntityIds)
    { if (playerId == 1) HumanAction(); pending = Event("mulligan"); pending["seat"] = playerId; pending["keeps"] = keptEntityIds.ToArray(); }
    public void OnDecision(GoldenContext context, GoldenDecision trace, string lethalResult, bool planReused,
        int nodes, double thinkMilliseconds, double maxSliceMilliseconds)
    {
        pending = Event("decision"); pending["seat"] = 2; pending["turn"] = context.Turn;
        pending["action"] = trace.Action.ToString(); pending["decisionIndex"] = context.DecisionIndex;
        pending["artificialSeed"] = context.ArtificialSeed; pending["driverKind"] = trace.DriverKind;
        pending["value"] = trace.Value; pending["stop"] = trace.Stop; pending["fallback"] = trace.FallbackReason ?? trace.SearchInfo.FallbackWhy;
        pending["nodes"] = nodes; pending["thinkMs"] = thinkMilliseconds; pending["maxSliceMs"] = maxSliceMilliseconds;
        pending["planNodeCap"] = trace.SearchInfo.PlanNodeCap;
        pending["searchBudgetMs"] = trace.SearchInfo.SearchBudgetMilliseconds;
        pending["searchInterrupted"] = trace.SearchInfo.Interrupted;
        pending["validationNodeCap"] = trace.SearchInfo.ValidationNodeCap;
        pending["validationNodes"] = trace.SearchInfo.ValidationNodes;
        pending["unscheduledNodes"] = trace.SearchInfo.UnscheduledNodes;
        pending["validationCompared"] = trace.SearchInfo.ValidationCompared;
        pending["validationDeadline"] = trace.SearchInfo.ValidationDeadline;
        pending["validationFallback"] = trace.SearchInfo.ValidationFallback;
        pending["mistakeKind"] = trace.SearchInfo.Mistake;
        if (trace.SearchInfo.FastFinish != null) pending["fastFinish"] = trace.SearchInfo.FastFinish;
        if (trace.SearchInfo.Replan != null) pending["replan"] = trace.SearchInfo.Replan;
        pending["lethal"] = lethalResult; pending["reuse"] = planReused;
        pending["candidates"] = trace.Candidates.Select(p => new Dictionary<string, object> {
            { "action", p.Key.ToString() }, { "value", p.Value } }).ToArray();
    }
    public void OnHumanAction(int turn, string botAction, int optionsIndex, IReadOnlyList<int> choices, bool conceded)
    {
        HumanAction();
        // Source-engine mulligans also emit a human-action hook before their one post-action hash.
        if (pending == null || (string)pending["type"] != "mulligan") pending = Event("human-action");
        pending["seat"] = 1; pending["turn"] = turn; pending["action"] = botAction;
        pending["optionsIndex"] = optionsIndex; pending["choices"] = choices.ToArray(); pending["conceded"] = conceded;
    }
    public void OnImplicitAction(string kind) { pending = Event(kind); }
    // Legacy opponents have no search trace. Preserve a runner's richer decision when present.
    public void OnNpcAction(int turn, string action)
    {
        if (pending != null)
        {
            if ((string)pending["type"] == "decision") pending["action"] = action;
            return;
        }
        pending = Event("decision"); pending["seat"] = 2; pending["turn"] = turn; pending["action"] = action;
    }
    public void CaptureState(EngineGame game)
    {
        if (pending == null) pending = Event("implicit-action");
        pending["stateTurn"] = game.Turn; pending["currentSeat"] = game.CurrentPlayer.PlayerId;
        pending["human"] = Snapshot(MatchSnapshot.Side(game.Player1, false), ref lastHuman);
        pending["npc"] = Snapshot(MatchSnapshot.Side(game.Player2, true), ref lastNpc);
        pending["rngState"] = MatchRecorder.RandomState(game);
    }
    // Exact string deltas keep board reconstruction lossless, without repeating unchanged actors/keywords.
    private static object Snapshot(string current, ref string previous)
    {
        object encoded = current;
        if (previous != null)
        {
            int prefix = 0, suffix = 0;
            while (prefix < Math.Min(previous.Length, current.Length) && previous[prefix] == current[prefix]) prefix++;
            while (suffix < Math.Min(previous.Length, current.Length) - prefix
                && previous[previous.Length - suffix - 1] == current[current.Length - suffix - 1]) suffix++;
            var delta = new object[] { prefix, suffix, current.Substring(prefix, current.Length - prefix - suffix) };
            if (Serialize(delta).Length < Serialize(current).Length) encoded = delta;
        }
        previous = current; return encoded;
    }
    public void CorrectDecision(GoldenDecision decision)
    {
        if (pending == null || (string)pending["type"] != "decision") return;
        pending["action"] = decision.Action.ToString(); pending["driverKind"] = decision.DriverKind;
        pending["fallback"] = decision.FallbackReason ?? decision.SearchInfo.FallbackWhy;
        pending["candidates"] = decision.Candidates.Select(p => new Dictionary<string, object> {
            { "action", p.Key.ToString() }, { "value", p.Value } }).ToArray();
    }
    public void OnPostAction(long sequence, string replayFingerprintSha256)
    {
        var row = pending ?? Event("implicit-action"); pending = null; humanActionPending = false;
        LastSequence = sequence;
        row["sequence"] = sequence; row["fingerprint"] = replayFingerprintSha256; Write(row);
    }
    public void OnEmote(int playerId, string emote, int publicEventIndex)
    { var row = Event("emote"); row["seat"] = playerId; row["emote"] = emote; row["publicEventIndex"] = publicEventIndex; Write(row); }
    public void OnConcession(int playerId, int turn)
    {
        if (playerId == 1) HumanAction();
        var row = Event("concession"); row["seat"] = playerId; row["turn"] = turn; Write(row);
        if (pending == null) { pending = Event("concede-action"); pending["seat"] = playerId; pending["turn"] = turn; }
        pending["conceded"] = true;
    }
    public void OnResult(string result, int turns)
    {
        var row = Event("result"); row["result"] = result; row["turns"] = turns;
        row["endedUtc"] = utcNow().ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        row["humanActions"] = humanActions; row["longestHumanGapMs"] = longestHumanGapMs;
        row["humanIdleOver60sMs"] = humanIdleOver60sMs;
        Write(row); FlushAndWait();
    }
    public void Dispose()
    {
        if (closed) return;
        try { Flush(); } catch (Exception) { }
        closed = true; disk.Complete();
        disk.WaitForClose(); // Terminal FX remain accepted until the bridge closes the journal.
    }
    // Both engine targets carry the same source stamp; their DLL bytes deliberately differ.
    public static string EngineSourceIdentity { get; } = SourceIdentity();
    private static string SourceIdentity() => typeof(EngineGame).Assembly
        .GetCustomAttributes(typeof(AssemblyMetadataAttribute), false).Cast<AssemblyMetadataAttribute>()
        .SingleOrDefault(a => a.Key == "EngineSourceIdentity")?.Value;
    public static string AssemblyHash(Assembly assembly)
    {
        using (var sha = SHA256.Create()) using (var stream = File.OpenRead(assembly.Location))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
    public static Dictionary<string, object> Header(string matchId, NpcProfile profile, LocalDeck human, LocalDeck npc,
        int seed, string sourceIdentity, string poolHash, string pluginBuild, PlaytestQueue queue,
        NpcPresentationSettings appliedPresentation = null, int? appliedTurnNodes = null,
        IDictionary<string, object> encounter = null, string debugScenario = null, string npcStartHero = null, int firstPlayer = 1)
    {
        var row = Event("header"); row["schema"] = 1; row["matchId"] = matchId;
        row["npcId"] = profile?.Id ?? npc.Id; row["npcVersion"] = profile?.Version ?? 0;
        row["npcName"] = profile?.DisplayName ?? npc.Opponent;
        row["startedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        if (npcStartHero != null && npcStartHero != npc.Hero) row["npcStartHero"] = npcStartHero;
        row["humanDeck"] = Deck(human); row["npcDeck"] = Deck(npc); row["engineSeed"] = seed; row["firstPlayer"] = firstPlayer;
        row["engineSourceIdentity"] = sourceIdentity;
        var settings = appliedPresentation ?? queue?.Presentation() ?? new NpcPresentationSettings();
        row["poolHash"] = poolHash; row["pluginBuild"] = pluginBuild;
        row["knobs"] = new Dictionary<string, object> {
            { "npc", profile?.Id ?? npc.Id }, { "emotes", settings.Emotes ? "on" : "off" }, { "hover", settings.Hover ? "on" : "off" },
            { "skin", settings.Skin ? "on" : "off" }, { "cardback", settings.CardBack ? (object)(settings.CardBackOverride ?? profile?.CardBack ?? 0) : "off" },
            { "turnNodes", appliedTurnNodes ?? queue?.TurnNodes ?? profile?.Budget.TurnNodes ?? 0 },
            { "nativeStartingTaunt", settings.NativeStartingTaunt ? "on" : "off" } };
        // World NPC matches (ladder or world-id playtest): who was met and how (skill tier, personality, rating, reason).
        if (encounter != null) row["encounter"] = new Dictionary<string, object>(encounter);
        // Replay applies OpeningHand overrides before offers and remaining scenario settings at main-begin.
        if (debugScenario != null) row["debugScenario"] = debugScenario;
        row["fingerprintKind"] = "match-recorder-v1";
        return row;
    }
    public static Dictionary<string, object> Deck(LocalDeck deck)
    {
        var row = new Dictionary<string, object> { { "id", deck.Id }, { "hero", deck.Hero }, { "style", deck.Style.ToString() },
            { "cards", deck.CardIds }, { "sideboards", deck.Sideboards.OrderBy(p => p.Key, StringComparer.Ordinal)
                .ToDictionary(p => p.Key, p => (object)p.Value) } };
        row["hash"] = MatchRecorder.Hash(Serialize(row)); return row;
    }
    // Shared serializer keeps schema independent of Unity's MiniJSON and of the harness target framework.
    public static string Serialize(object value)
    {
        if (value == null) return "null";
        if (value is string) {
            var output = new StringBuilder("\"");
            foreach (char c in (string)value)
            {
                if (c == '"' || c == '\\') output.Append('\\').Append(c);
                else if (c < 32) output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else output.Append(c);
            }
            return output.Append('"').ToString();
        }
        if (value is bool) return (bool)value ? "true" : "false";
        if (value is IDictionary) {
            var pairs = new List<string>();
            foreach (DictionaryEntry pair in (IDictionary)value) pairs.Add(Serialize((string)pair.Key) + ":" + Serialize(pair.Value));
            return "{" + string.Join(",", pairs) + "}";
        }
        if (value is IEnumerable) return "[" + string.Join(",", ((IEnumerable)value).Cast<object>().Select(Serialize)) + "]";
        string number = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (number == "NaN" || number == "Infinity" || number == "-Infinity") return "null";
        return number;
    }
}
