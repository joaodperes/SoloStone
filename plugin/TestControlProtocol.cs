using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MiniJSON;

// One BOARD_ACTOR_INVARIANT episode = a stretch of drained samples where a board actor differs from the engine. A bare
// failed/recovered pair cannot tell a 3 s animation lag from a fault that stayed: every episode ends in exactly one of
// recovered, ended-unrecovered (match over or replaced first) or, while it lasts, persisting. No Unity types.
internal sealed class BoardActorFaultEvent
{
    internal string Status, Match, Faults;
    internal int Frames;
    internal float Seconds;
}
internal sealed class BoardActorFaultTracker
{
    internal const float PersistSeconds = 5f;
    private string match, faults;
    private int sinceFrame;
    private float sinceTime;
    private bool persistLogged;
    internal int Episodes, Recovered, Unrecovered, Persisting, Changes;
    internal float LongestRecoveredSeconds;
    internal bool Active => faults != null;

    // Called with every view sample. `drained` = no client queue blocker (only then is a board difference a verdict);
    // `matchOver` = terminal engine or no longer in gameplay. Returns the line to log now, if any.
    internal BoardActorFaultEvent Observe(string matchId, bool drained, bool matchOver, string current, int frame, float time)
    {
        if (faults != null && (matchOver || matchId != match))
        {
            var ended = Event("ended-unrecovered", frame, time);
            Unrecovered++; faults = null; match = null;
            return ended;
        }
        if (matchOver) return null;
        if (!drained)
        {
            if (faults == null || persistLogged || time - sinceTime < PersistSeconds) return null;
            persistLogged = true; Persisting++;
            return Event("persisting", frame, time);
        }
        current = string.IsNullOrEmpty(current) ? null : current;
        if (current == faults)
        {
            if (faults == null || persistLogged || time - sinceTime < PersistSeconds) return null;
            persistLogged = true; Persisting++;
            return Event("persisting", frame, time);
        }
        if (faults == null)
        {
            faults = current; match = matchId; sinceFrame = frame; sinceTime = time; persistLogged = false; Episodes++;
            return Event("failed", frame, time);
        }
        if (current == null)
        {
            var recovered = Event("recovered", frame, time);
            Recovered++; LongestRecoveredSeconds = Math.Max(LongestRecoveredSeconds, recovered.Seconds);
            faults = null; match = null;
            return recovered;
        }
        // A different fault set inside the same episode: not a second failure and not a recovery.
        faults = current; Changes++;
        return Event("changed", frame, time);
    }
    private BoardActorFaultEvent Event(string status, int frame, float time) => new BoardActorFaultEvent {
        Status = status, Match = match, Faults = faults, Frames = frame - sinceFrame, Seconds = time - sinceTime };
    // Whole-match totals for the state/report: unrecovered must be zero in a healthy playtest.
    internal string Summary(int frame, float time) =>
        "episodes=" + Episodes + " recovered=" + Recovered + " changed=" + Changes + " unrecovered=" + Unrecovered
        + " persisting=" + Persisting + " longestRecoveredSeconds=" + LongestRecoveredSeconds.ToString("0.0", CultureInfo.InvariantCulture)
        + (faults == null ? " active=false" : " active=true activeFrames=" + (frame - sinceFrame) + " activeSeconds="
            + (time - sinceTime).ToString("0.0", CultureInfo.InvariantCulture));
}

// No Unity/client types: this is also compiled by tests/test-control.
internal static class TestControlProtocol
{
    internal const int MaxLine = 64 * 1024, MaxQueue = 32, MaxResponse = 1024 * 1024, MaxLedger = 1024;
    internal const int MulliganReadyFrames = 10, MulliganSendSeconds = 15, ChoiceReadyFrames = 3, EndTurnReadyFrames = 2;
    internal static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    private const string StringToken = "\"(?:[^\"\\\\\\x00-\\x1f]|\\\\(?:[\"\\\\/bfnrt]|u[0-9a-fA-F]{4}))*\"";
    private const string IntegerToken = @"-?(?:0|[1-9][0-9]*)";
    private const string ArrayToken = @"\[[ \t\r\n]*(?:" + IntegerToken + @"(?:[ \t\r\n]*,[ \t\r\n]*" + IntegerToken + @")*)?[ \t\r\n]*\]";
    private const string StringArrayToken = @"\[[ \t\r\n]*(?:" + StringToken + @"(?:[ \t\r\n]*,[ \t\r\n]*" + StringToken + @")*)?[ \t\r\n]*\]";
    private static readonly Regex Field = new Regex(@"\G[ \t\r\n]*(?<key>" + StringToken + @")[ \t\r\n]*:[ \t\r\n]*(?<value>" + StringToken + "|" + IntegerToken + "|" + ArrayToken + "|" + StringArrayToken + @")[ \t\r\n]*(?<end>[,}])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static IEnumerable<string> HandDifferences(int player, int id, string expectedCard, bool inHand, string cardId, bool liveActor)
    {
        string prefix = "player" + player + ":";
        if (!inHand || !liveActor) yield return prefix + "handActorMissing:" + id;
        if (string.IsNullOrEmpty(cardId)) yield return prefix + "handIdentityHidden:" + id;
    }

    internal sealed class BoardActorState
    {
        internal string CardId;
        internal int Position, Attack, Health;
        internal bool Present, Live, Shown;
    }

    internal static IEnumerable<string> BoardDifferences(int player, int id, BoardActorState expected, BoardActorState actual)
    {
        string prefix = "player" + player + ":boardActor:" + id + ":";
        if (!actual.Present || !actual.Live) yield return prefix + "missing";
        else if (!actual.Shown) yield return prefix + "hidden";
        if (actual.CardId != expected.CardId) yield return prefix + "card:" + actual.CardId + "!=" + expected.CardId;
        if (actual.Position != expected.Position) yield return prefix + "position:" + actual.Position + "!=" + expected.Position;
        if (actual.Attack != expected.Attack) yield return prefix + "attack:" + actual.Attack + "!=" + expected.Attack;
        if (actual.Health != expected.Health) yield return prefix + "health:" + actual.Health + "!=" + expected.Health;
    }

    internal static ControlRequest Parse(string line)
    {
        if (TestControlProtocol.Utf8.GetByteCount(line) > MaxLine) throw new FormatException("Line exceeds 64 KiB");
        // MiniJSON accepts missing commas, duplicate keys and trailing input; reject those before parsing.
        string text = line.Trim(' ', '\t', '\r', '\n');
        if (text.Length < 2 || text[0] != '{') throw new FormatException("Expected flat JSON object");
        int offset = 1;
        var fields = new JsonNode();
        while (offset < text.Length)
        {
            Match field = Field.Match(text, offset);
            if (!field.Success) throw new FormatException("Malformed JSON field");
            string key = Json.Deserialize(field.Groups["key"].Value) as string;
            if (fields.ContainsKey(key)) throw new FormatException("Duplicate field");
            string raw = field.Groups["value"].Value;
            object value = Json.Deserialize(raw);
            if (raw[0] == '-' || char.IsDigit(raw[0]))
            {
                if (!long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number)
                    || number < int.MinValue || number > int.MaxValue) throw new FormatException("Integer out of range");
                value = number;
            }
            if (raw[0] == '[')
            {
                var list = value as JsonList;
                if (list == null || list.Count > (key == "productIds" ? 128 : key == "cardIds" ? 50 : 32)) throw new FormatException("Array exceeds limit");
                string members = raw.Substring(1, raw.Length - 2).Trim();
                if (key != "cardIds" && key != "productIds" && members.Length != 0)
                    foreach (string member in members.Split(','))
                        if (!int.TryParse(member.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int _))
                            throw new FormatException("Array integer out of range");
            }
            fields.Add(key, value);
            offset += field.Length;
            if (field.Groups["end"].Value == "}") break;
        }
        if (offset != text.Length || text[text.Length - 1] != '}') throw new FormatException("Trailing or incomplete JSON");
        return new ControlRequest(fields);
    }

    internal static bool Authenticate(ControlRequest request, string runId, string token) =>
        request.Command == "hello" && FixedEquals(request.Text("runId"), runId) && FixedEquals(request.Text("token"), token);

    private static bool FixedEquals(string left, string right)
    {
        int difference = left.Length ^ right.Length;
        for (int i = 0; i < Math.Max(left.Length, right.Length); i++)
            difference |= (i < left.Length ? left[i] : 0) ^ (i < right.Length ? right[i] : 0);
        return difference == 0;
    }

    internal static void RequireIdentity(string requestedMatch, string currentMatch, int requestedId, int currentId)
    {
        if (string.IsNullOrEmpty(currentMatch) || requestedMatch != currentMatch) throw new InvalidOperationException("Stale matchId");
        if (requestedId != currentId || currentId < 0) throw new InvalidOperationException("Stale options/choice ID");
    }

    internal static bool MulliganInitialized(int[] starting, int[] available, int[] chosen)
    {
        return starting.Length != 0 && starting.All(id => id > 0) && starting.Distinct().Count() == starting.Length
            && available.Length == starting.Length && new HashSet<int>(starting).SetEquals(available)
            && chosen.Length == starting.Length && new HashSet<int>(starting).SetEquals(chosen);
    }

    internal static int[] MulliganHeld(int[] starting, int[] available, int[] selected, int[] replace)
    {
        var cards = new HashSet<int>(starting);
        if (cards.Count == 0 || cards.Count != starting.Length || starting.Any(id => id <= 0)
            || available.Length != cards.Count || !cards.SetEquals(available)
            || selected.Distinct().Count() != selected.Length || selected.Any(id => !cards.Contains(id))
            || replace.Distinct().Count() != replace.Length || replace.Any(id => !cards.Contains(id)))
            throw new InvalidOperationException("Invalid mulligan card membership");
        // endpoint-recon-keep/decompiled: MulliganManager.cs:2525-2546,2980-3019;
        // GameState.cs:3228-3234,3268-3270,5621-5651: chosen means HELD, including on the wire.
        return starting.Where(id => !replace.Contains(id)).ToArray();
    }

    internal static bool Flag(string contents, string runId, out string token)
    {
        token = null;
        if (contents == null || contents.Length > 512) return false;
        string[] rows = contents.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        if (rows.Length != 2 || rows[0] != "runId=" + runId || !rows[1].StartsWith("token=", StringComparison.Ordinal)) return false;
        string value = rows[1].Substring(6);
        if (value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c))) return false;
        token = value;
        return true;
    }

    internal static string Hash(string text)
    {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Utf8.GetBytes(text))).Replace("-", "");
    }

    internal static JsonList List(IEnumerable<object> items)
    {
        var list = new JsonList();
        foreach (object item in items) list.Add(item);
        return list;
    }
}

internal sealed class ControlRequest
{
    internal readonly string Id, Command, Hash;
    internal readonly JsonNode Fields;
    internal readonly DateTime ReceivedUtc = DateTime.UtcNow;
    internal bool Mutation => Command == "recover-choice" || Command == "act" || Command == "end-turn" || Command == "mulligan-confirm" || Command == "concede" || Command == "choose" || Command == "press" || Command == "shop-purchase"
        || Command == "squelch" || Command == "unsquelch" || Command == "emote" || Command == "npc-emote" || Command == "npc-concede" || Command == "human-defeat" || Command == "premium-unlock-prepare" || Command == "ui-hover" || Command == "ui-unhover" || Command == "ui-escape" || Command == "choice-click" || Command == "entity-drag" || Command == "ui-drag" || Command == "ui-scroll" || Command == "open-packs" || Command == "achievement-prime" || Command == "turn-timer-debug" || Command == "spell-stall-arm";
    // Human emotes the native emote menu offers (EmoteType names); test-control plays them through the native option.
    internal static readonly string[] HumanEmotes = { "GREETINGS", "WELL_PLAYED", "THANKS", "WOW", "OOPS", "THREATEN" };
    internal ControlRequest(JsonNode fields)
    {
        Fields = fields;
        Id = Text("id"); Command = Text("cmd");
        if (!Regex.IsMatch(Id, @"\A[A-Za-z0-9_.:-]{1,96}\z")) throw new FormatException("Invalid request ID");
        if (Integer("v", 1, 1) != 1) throw new FormatException("Unsupported version");
        string extras;
        switch (Command)
        {
            case "hello": extras = "runId token"; Text("runId"); Text("token"); break;
            case "state": case "export-card-db": case "shop-catalog": case "export-cardbacks": case "export-coins": case "npc-cardbacks": case "cosmetics-state":
            case "achievements-state": case "save-catalog": case "pet-database": extras = ""; break;
            case "buttons": extras = "dump"; if (Has("dump")) Integer("dump", 0, 1); break;
            case "npc-queue": extras = "count mode"; Integer("count", 1, 1000); if (Has("mode") && Text("mode") != "ranked" && Text("mode") != "casual") throw new FormatException("Invalid mode"); break;
            case "cosmetic-check": extras = "productIds"; if (Has("productIds")) ProductIds(); break;
            case "cosmetic-check-status": extras = "runId"; Text("runId"); break;
            case "crafting-state": extras = "cardIds"; if (Has("cardIds")) CardIds(); break;
            case "open-packs": extras = "count"; Integer("count", 1, TestControlPacks.MaxCount); break;
            case "shop-purchase":
                extras = "productId quantity kitId";
                ShopId("productId");
                if (Has("quantity")) Integer("quantity", 1, 50);
                if (Has("kitId")) ShopId("kitId");
                break;
            case "fx-scene":
                extras = "scene root name cursor";
                if (Has("name")) Text("name");
                if (Has("cursor")) {
                    Integer("cursor", 1);
                    if (Has("scene") || Has("root") || Has("name")) throw new FormatException("cursor resumes its original selection; omit scene/root/name");
                }
                if (Has("scene") != Has("root")) throw new FormatException("scene and root must be supplied together");
                if (Has("scene")) { Integer("scene", 0, 7); Integer("root", 0, 4095); }
                break;
            case "fx-status":
                extras = "name source seconds";
                if (Has("name")) Text("name");
                if (Has("source")) Integer("source", 1);
                if (!Has("name") && !Has("source")) throw new FormatException("name or source required");
                Integer("seconds", 1, 600);
                break;
            case "entity-drag":
                extras = "matchId optionsId entityId target position click"; Text("matchId"); Integer("optionsId", 0); Integer("entityId", 1);
                // click: press and release on the source itself (an untargeted board location); exclusive with a destination.
                if (Has("click")) { Integer("click", 1, 1); if (Has("target") || Has("position")) throw new FormatException("click excludes target and position"); }
                else if (Has("target") == Has("position")) throw new FormatException("Supply target, position or click");
                if (Has("target")) Integer("target", 1);
                if (Has("position")) Integer("position", 1, 7);
                break;
            case "pet-log": extras = "after"; if (Has("after")) Integer("after", 0); break;
            case "fx":
                extras = "after limit";
                if (Has("after")) Integer("after", 0);
                if (Has("limit")) Integer("limit", 1, 100);
                break;
            case "turn-timer-debug": extras = "matchId seconds"; Text("matchId"); Integer("seconds", 2, 75); break;
            case "hero-skin-spells": extras = "cardId"; Text("cardId"); break;
            case "options": case "choices": case "concede": extras = "matchId"; Text("matchId"); break;
            // Native squelch of the NPC opponent (EnemyEmoteHandler) and one human emote: local presentation, no game send.
            case "squelch": case "unsquelch": extras = "matchId"; Text("matchId"); break;
            // Debug instant win (the NPC concedes now, settled like its own concession) and instant plain loss (not a concede).
            case "achievement-prime":
                extras = "matchId achievementId"; Text("matchId");
                if (Text("achievementId") != "death-knight-grave-expenditure") throw new FormatException("Unsupported achievement fixture.");
                break;
            case "npc-concede": case "human-defeat": case "premium-unlock-prepare": case "spell-stall-arm": extras = "matchId"; Text("matchId"); break;
            case "emote": case "npc-emote":
                extras = "matchId emote"; Text("matchId");
                if (Array.IndexOf(HumanEmotes, Text("emote")) < 0) throw new FormatException("Unsupported emote");
                break;
            case "act":
                extras = "matchId optionsId index target sub position"; Text("matchId");
                Integer("optionsId", 0); Integer("index", 0, 4095); Integer("target", 0); Integer("sub", -1, 255); Integer("position", 0, 7); break;
            case "end-turn": extras = "matchId optionsId"; Text("matchId"); Integer("optionsId", 0); break;
            case "mulligan-confirm":
                extras = "matchId choiceId replaceEntityIds"; Text("matchId"); Integer("choiceId", 0);
                EntityIds("replaceEntityIds");
                break;
            case "wait-until-idle":
                extras = "matchId afterActionId timeoutMs"; Text("matchId"); Text("afterActionId"); Integer("timeoutMs", 1, 120000); break;
            case "action-status": extras = "actionId"; Text("actionId"); break;
            case "screenshot": extras = ""; break;
            // Read-only: an unobstructed client point on a visible card, for real-cursor hover (tooltips, big card).
            case "locate": extras = "matchId entityId"; Text("matchId"); Integer("entityId", 1); break;
            // Read-only: the card text the client really builds and draws for one visible card (see NativeTestControl.CardText).
            case "card-text": extras = "matchId entityId"; Text("matchId"); Integer("entityId", 1); break;
            case "entity":
                extras = "matchId entityIds"; Text("matchId");
                if (EntityIds("entityIds").Length == 0) throw new FormatException("Entity requires at least one ID");
                break;
            case "recover-choice":
                extras = "matchId choiceId entityIds"; Text("matchId"); Integer("choiceId", 0);
                if (EntityIds("entityIds").Length == 0) throw new FormatException("Recovery requires a selection");
                break;
            case "choice-click":
            case "choose":
                extras = "matchId choiceId entityIds"; Text("matchId"); Integer("choiceId", 0);
                if (EntityIds("entityIds").Length != 1) throw new FormatException("Choose requires exactly one entity");
                break;
            // Native menu UI: read the PegUI tree, wait for it to settle, press one listed element.
            case "ui": extras = "all"; if (Has("all")) Integer("all", 0, 1); break;
            case "wait-ui":
                extras = "timeoutMs element screen"; Integer("timeoutMs", 1, 120000);
                if (Has("element")) UiRules.RequireElementId(Text("element"));
                if (Has("screen")) Text("screen");
                break;
            case "press":
                extras = "element snapshot timeoutMs button"; UiRules.RequireElementId(Text("element"));
                if (Has("snapshot") && !Regex.IsMatch(Text("snapshot"), @"\A[0-9a-f]{12}\z")) throw new FormatException("Invalid snapshot");
                if (Has("timeoutMs")) Integer("timeoutMs", 0, UiRules.MaxPressWaitMs);
                if (Has("button") && Text("button") != "left" && Text("button") != "right") throw new FormatException("Invalid button");
                break;
            // Native tray scroll or roll-over: pointer/scroll state only, no clicks.
            case "ui-scroll":
            case "ui-hover":
                extras = "element snapshot timeoutMs"; UiRules.RequireElementId(Text("element"));
                if (Has("snapshot") && !Regex.IsMatch(Text("snapshot"), @"\A[0-9a-f]{12}\z")) throw new FormatException("Invalid snapshot");
                if (Has("timeoutMs")) Integer("timeoutMs", 0, UiRules.MaxPressWaitMs);
                break;
            case "ui-unhover": extras = ""; break;
            case "ui-escape": extras = ""; break;
            // Native deck tray reorder: press-hold the element's deck box, move to the target deck box's slot, release.
            case "ui-drag":
                extras = "element target snapshot timeoutMs"; UiRules.RequireElementId(Text("element")); UiRules.RequireElementId(Text("target"));
                if (UiRules.DragBlocker(Text("element"), Text("target")) is string drag) throw new FormatException(drag);
                if (Has("snapshot") && !Regex.IsMatch(Text("snapshot"), @"\A[0-9a-f]{12}\z")) throw new FormatException("Invalid snapshot");
                if (Has("timeoutMs")) Integer("timeoutMs", 0, UiRules.MaxPressWaitMs);
                break;
            default: throw new FormatException("Unsupported command");
        }
        var allowed = new HashSet<string>(("v id cmd " + extras).Split(' '));
        if (fields.Keys.Any(key => !allowed.Contains(key))) throw new FormatException("Unexpected field");
        var canonical = new JsonNode();
        foreach (string key in fields.Keys.OrderBy(key => key, StringComparer.Ordinal)) canonical.Add(key, fields[key]);
        Hash = TestControlProtocol.Hash(Json.Serialize(canonical));
    }
    internal string[] CardIds()
    {
        if (!Fields.TryGetValue("cardIds", out object value) || !(value is JsonList list)
            || list.Any(item => !(item is string id) || string.IsNullOrWhiteSpace(id)
                || id.Length > 128 || id.Any(char.IsControl)))
            throw new FormatException("Invalid card IDs");
        var ids = list.Cast<string>().ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) throw new FormatException("Duplicate card IDs");
        return ids;
    }
    internal string[] ProductIds()
    {
        if (!Fields.TryGetValue("productIds", out object value) || !(value is JsonList list)
            || list.Count > 128 || list.Any(item => !(item is string id) || string.IsNullOrWhiteSpace(id)
                || id.Length > 128 || id.Any(char.IsControl)))
            throw new FormatException("Invalid cosmetic product IDs");
        var ids = list.Cast<string>().ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) throw new FormatException("Duplicate product IDs");
        return ids;
    }
    private void ShopId(string key)
    {
        string id = Text(key);
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(char.IsControl))
            throw new FormatException("Invalid shop ID: " + key);
    }
    internal bool Has(string key) => Fields.ContainsKey(key);
    internal string Text(string key)
    {
        if (!Fields.TryGetValue(key, out object value) || !(value is string text) || text.Length == 0 || text.Length > 256)
            throw new FormatException("Invalid string: " + key);
        return text;
    }
    internal int Integer(string key, int min = int.MinValue, int max = int.MaxValue)
    {
        if (!Fields.TryGetValue(key, out object value) || !(value is long number) || number < min || number > max)
            throw new FormatException("Invalid integer: " + key);
        return (int)number;
    }
    internal int[] EntityIds(string key)
    {
        if (!Fields.TryGetValue(key, out object value) || !(value is JsonList list))
            throw new FormatException("Invalid entity IDs: " + key);
        var ids = list.Select(item => Convert.ToInt32(item, CultureInfo.InvariantCulture)).ToArray();
        if (ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Length)
            throw new FormatException("Invalid entity IDs: " + key);
        return ids;
    }
}

internal sealed class ControlLines
{
    private readonly byte[] line = new byte[TestControlProtocol.MaxLine];
    private int count;
    internal bool Partial => count != 0;
    internal void Feed(byte[] bytes, int length, Action<string> receive)
    {
        for (int i = 0; i < length; i++)
        {
            if (bytes[i] == 10)
            {
                if (count == 0) throw new FormatException("Empty line");
                string text = TestControlProtocol.Utf8.GetString(line, 0, count);
                count = 0;
                receive(text);
            }
            else
            {
                if (count == line.Length) throw new FormatException("Line exceeds 64 KiB");
                line[count++] = bytes[i];
            }
        }
    }
}

internal sealed class ControlQueue<T>
{
    private readonly Queue<T> queue = new Queue<T>();
    internal bool Put(T value) { lock (queue) { if (queue.Count == TestControlProtocol.MaxQueue) return false; queue.Enqueue(value); return true; } }
    internal bool Take(out T value) { lock (queue) { if (queue.Count == 0) { value = default(T); return false; } value = queue.Dequeue(); return true; } }
}

internal sealed class ControlOperation
{
    internal readonly ControlRequest Request;
    internal string Phase = "accepted";
    internal int Sends;
    internal JsonNode Detail = new JsonNode();
    internal readonly DateTime AcceptedUtc = DateTime.UtcNow;
    internal DateTime SubmittedUtc;
    internal bool Finished => Phase == "settled" || Phase == "rejected" || Phase == "fault";
    internal ControlOperation(ControlRequest request) { Request = request; }
}

internal sealed class ControlLedger
{
    private readonly Dictionary<string, ControlOperation> entries = new Dictionary<string, ControlOperation>();
    internal ControlOperation Find(string id) { entries.TryGetValue(id, out ControlOperation operation); return operation; }
    internal ControlOperation Accept(ControlRequest request, out bool added)
    {
        added = false;
        ControlOperation existing = Find(request.Id);
        if (existing != null)
        {
            if (existing.Request.Hash != request.Hash) throw new InvalidOperationException("ID reused with different body");
            return existing;
        }
        if (entries.Count == TestControlProtocol.MaxLedger) throw new InvalidOperationException("Action ledger full; restart required");
        var operation = new ControlOperation(request);
        entries.Add(request.Id, operation); added = true;
        return operation;
    }
}

internal sealed class ControlConnection
{
    internal readonly System.Net.Sockets.TcpClient Client;
    internal readonly ControlQueue<JsonNode> Replies = new ControlQueue<JsonNode>();
    internal volatile bool Open = true;
    internal ControlConnection(System.Net.Sockets.TcpClient client) { Client = client; }
    internal void Close() { Open = false; Client.Close(); }
    internal void Reply(JsonNode reply) { if (Open && !Replies.Put(reply)) Close(); }
}

internal sealed class ControlEnvelope
{
    internal readonly ControlConnection Connection;
    internal readonly ControlRequest Request;
    internal ControlEnvelope(ControlConnection connection, ControlRequest request) { Connection = connection; Request = request; }
}

#if !SOLOSTONE_INSTALLER || SOLOSTONE_TEST_HOOKS
// Socket worker owns only DTOs and bounded queues. No callbacks into the game thread.
internal sealed class ControlTransport : IDisposable
{
    internal readonly ControlQueue<ControlEnvelope> Requests = new ControlQueue<ControlEnvelope>();
    private readonly System.Net.Sockets.TcpListener listener;
    private readonly System.Threading.Thread worker;
    private readonly string runId, token;
    private volatile bool stopped;
    private volatile ControlConnection connection;
    internal ControlTransport(string runId, string token, int port = 18765)
    {
        this.runId = runId; this.token = token;
        listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        listener.ExclusiveAddressUse = true;
        try
        {
            listener.Start(1);
            worker = new System.Threading.Thread(Serve) { IsBackground = true, Name = "HS test control" };
            worker.Start();
        }
        catch { listener.Stop(); throw; }
    }
    private void Serve()
    {
        try
        {
            while (!stopped)
            {
                if (!listener.Pending()) { System.Threading.Thread.Sleep(5); continue; }
                var client = listener.AcceptTcpClient();
                client.NoDelay = true; client.SendTimeout = 2000;
                var current = new ControlConnection(client);
                connection = current;
                try { ServeConnection(current); }
                catch (Exception) { /* Invalid input or a dead socket must never terminate the Unity process. */ }
                finally { current.Close(); connection = null; }
            }
        }
        catch (System.Net.Sockets.SocketException) { }
        catch (ObjectDisposedException) { }
    }
    // The endpoint serves one client at a time and every hslive CLI call is its own connection, so a concurrent poller and driver
    // displace each other. Say so on the wire (best effort) instead of a bare close the client cannot tell from a bad token.
    internal static readonly string BusyError = "busy: another hslive client holds the test endpoint (it serves one connection at a time); retry shortly";
    private void RejectBusy(System.Net.Sockets.TcpClient intruder)
    {
        try
        {
            intruder.SendTimeout = 500;
            var reply = new JsonNode { { "v", 1 }, { "runId", runId }, { "id", "endpoint-busy" }, { "phase", "rejected" }, { "busy", true }, { "error", BusyError } };
            byte[] bytes = TestControlProtocol.Utf8.GetBytes(Json.Serialize(reply) + "\n");
            var stream = intruder.GetStream();
            stream.Write(bytes, 0, bytes.Length);
            // Closing with unread input resets the connection and can discard the line just written; let the peer read it and hang up.
            intruder.Client.Shutdown(System.Net.Sockets.SocketShutdown.Send);
            intruder.ReceiveTimeout = 150;
            var sink = new byte[256];
            while (stream.Read(sink, 0, sink.Length) > 0) { }
        }
        catch (Exception) { /* A dead or unreadable intruder socket is simply closed. */ }
        finally { intruder.Close(); }
    }
    private void ServeConnection(ControlConnection current)
    {
        var lines = new ControlLines();
        var buffer = new byte[4096];
        bool authenticated = false;
        DateTime partialSince = DateTime.UtcNow, connected = DateTime.UtcNow;
        var stream = current.Client.GetStream();
        while (!stopped && current.Open)
        {
            // A second local controller cannot wait behind the authenticated connection.
            if (listener.Pending()) RejectBusy(listener.AcceptTcpClient());
            while (current.Replies.Take(out JsonNode reply))
            {
                byte[] bytes = TestControlProtocol.Utf8.GetBytes(Json.Serialize(reply) + "\n");
                if (bytes.Length > TestControlProtocol.MaxResponse) throw new IOException("Response exceeds limit");
                stream.Write(bytes, 0, bytes.Length);
            }
            if ((!authenticated && (DateTime.UtcNow - connected).TotalSeconds > 5)
                || (lines.Partial && (DateTime.UtcNow - partialSince).TotalSeconds > 10)) throw new IOException("Stalled request");
            if (!current.Client.Client.Poll(5000, System.Net.Sockets.SelectMode.SelectRead)) continue;
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (!lines.Partial) partialSince = DateTime.UtcNow;
            lines.Feed(buffer, read, text => {
                partialSince = DateTime.UtcNow;
                ControlRequest request = TestControlProtocol.Parse(text);
                if (!authenticated)
                {
                    if (!TestControlProtocol.Authenticate(request, runId, token)) throw new FormatException("Authentication failed");
                    authenticated = true;
                }
                else if (request.Command == "hello") throw new FormatException("Already authenticated");
                if (!Requests.Put(new ControlEnvelope(current, request))) throw new IOException("Command queue full");
            });
        }
    }
    public void Dispose()
    {
        stopped = true;
        listener.Stop(); connection?.Close();
        worker.Join(250);
        while (Requests.Take(out ControlEnvelope _)) { }
    }
}
#else
// An installer release has no listener. Enabled() is always false, so this is never constructed; the type keeps the rest of the control code compiling.
internal sealed class ControlTransport : IDisposable
{
    internal readonly ControlQueue<ControlEnvelope> Requests = new ControlQueue<ControlEnvelope>();
    internal ControlTransport(string runId, string token, int port = 0) { throw new InvalidOperationException("Test control is not in this build."); }
    public void Dispose() { }
}
#endif

// Native menu UI model. The plugin fills it from PegUI; everything here is pure so tests compile it unchanged.
internal sealed class UiElement
{
    internal string Id, Type, Label, Path, Denied, HitBy, Diag;
    // Why the element is not yet stable (tween / rect / new), so a permanently moving screen can be diagnosed live.
    internal string Unstable;
    // Owning dialog text (header and body): a generic YES/OK button inherits its dialog's purpose for the denylist.
    internal string Context;
    internal int Instance, StableFrames;
    internal bool Enabled, PressDisabled, OnScreen, Hit, Tweening, Selected, ScrollClipped;
    // A control with a selection state (save picker rows, pace and Sandbox checkboxes): `selected` is serialized even when false.
    internal bool Selectable;
    internal long DeckId = -1;
    // The deck box's own deck, as the client holds it: `hslive decks` lists id, name and class without reading card art.
    internal string DeckName, DeckClass;
    // Normalized 1280x720 rect, top-left origin.
    internal float X, Y, W, H;
    // Normalized point that received the hit test when it was not the centre (an uncovered edge of a large catcher).
    internal float HitX = -1, HitY = -1;
    internal object Handle;
    internal JsonNode Pack;
    internal JsonNode ToJson()
    {
        var node = new JsonNode {
            { "id", Id }, { "instance", (long)Instance }, { "type", Type }, { "label", Label ?? "" },
            { "rect", TestControlProtocol.List(new object[] { (long)Math.Round(X), (long)Math.Round(Y), (long)Math.Round(W), (long)Math.Round(H) }) },
            { "enabled", Enabled }, { "hit", Hit }, { "stable", StableFrames >= UiRules.StableFrames }
        };
        if (!OnScreen) node.Add("offScreen", true);
        if (ScrollClipped) node.Add("scrollClipped", true);
        if (PressDisabled) node.Add("pressDisabled", true);
        if (Denied != null) node.Add("denied", Denied);
        if (!Hit && HitBy != null) node.Add("hitBy", HitBy);
        if (NativeReportText.IsControl(Id)) node.Add("hitTest", Hit ? "topmost" : "blocked");
        if (Hit && HitX >= 0) node.Add("point", TestControlProtocol.List(new object[] { (long)Math.Round(HitX), (long)Math.Round(HitY) }));
        if (Diag != null) node.Add("diag", Diag);
        if (Pack != null)
        {
            node.Add("pack", Pack);
            node.Add("hitTest", Hit ? "topmost" : "blocked");
        }
        if (StableFrames < UiRules.StableFrames && Unstable != null) node.Add("unstable", Unstable);
        if (DeckId > 0) node.Add("deckId", DeckId);
        if (DeckId > 0 && DeckName != null) node.Add("deckName", DeckName);
        if (DeckId > 0 && DeckClass != null) node.Add("deckClass", DeckClass);
        if (DeckId > 0 || Selectable || Id != null && Id.StartsWith("save.slot:", StringComparison.Ordinal)) node.Add("selected", Selected);
        return node;
    }
}

// Feats uses the native subcategory scroller's clip bounds, not the normalized screen rect.
internal static class NativeFeatsReveal
{
    internal const int MaxAttempts = 3, SettleFrames = 20, StableFrames = 3;
    internal static bool IsSubcategory(string id) => id != null && id.StartsWith("feats.subcategory:", StringComparison.Ordinal);
    internal static bool ShouldReveal(string id, bool fullyVisible, int stableFrames, int attempts, int frame, int lastFrame) =>
        IsSubcategory(id) && !fullyVisible && attempts < MaxAttempts
        && (attempts == 0 || frame - lastFrame >= SettleFrames && stableFrames >= StableFrames);
}

internal sealed class UiSnapshot
{
    internal JsonList PremiumRewards = new JsonList();
    internal JsonNode PackOpening;
    internal string Scene, Screen, Generation;
    internal bool MatchLive, Truncated;
    internal readonly List<string> Busy = new List<string>(), Moving = new List<string>();
    internal readonly List<JsonNode> Dialogs = new List<JsonNode>();
    internal readonly List<UiElement> Elements = new List<UiElement>();
    internal int Samples;
    internal double ReadMs;
    // Where a read spends its time (label / hit test / rest), to tune the per-frame cost on heavy screens.
    internal double LabelMs, HitMs, EnumerateMs, TweenMs, FinishMs;
    internal bool Idle => Busy.Count == 0 && Moving.Count == 0 && Samples >= UiRules.StableFrames;
    internal UiElement Find(string id) => Elements.FirstOrDefault(element => element.Id == id);
    internal bool OnScreen(string screen) => screen == Scene || screen == Screen;
    internal JsonNode ToJson(IEnumerable<string> extraBusy = null)
    {
        var busy = Busy.Concat(extraBusy ?? Enumerable.Empty<string>()).ToList();
        return new JsonNode {
            { "scene", Scene }, { "screen", Screen }, { "generation", Generation },
            { "packOpening", PackOpening },
            { "premiumRewards", PremiumRewards },
            { "idle", Idle && busy.Count == Busy.Count }, { "busy", TestControlProtocol.List(busy.Cast<object>()) },
            { "moving", TestControlProtocol.List(Moving.Cast<object>()) }, { "matchLive", MatchLive },
            { "dialogs", TestControlProtocol.List(Dialogs.Cast<object>()) }, { "truncated", Truncated }, { "samples", (long)Samples }, { "readMs", Math.Round(ReadMs, 2) },
            { "readParts", new JsonNode { { "labelMs", Math.Round(LabelMs, 2) }, { "hitMs", Math.Round(HitMs, 2) },
                { "enumerateMs", Math.Round(EnumerateMs, 2) }, { "tweenMs", Math.Round(TweenMs, 2) }, { "finishMs", Math.Round(FinishMs, 2) } } },
            { "elements", TestControlProtocol.List(Elements.Select(element => (object)element.ToJson())) }
        };
    }
}

// Per-element stability over contiguous sampled frames (Playwright's "stable" check).
internal sealed class UiTracker
{
    private Dictionary<int, UiElement> last = new Dictionary<int, UiElement>();
    private int lastFrame = int.MinValue, samples;
    internal void Reset() { last.Clear(); lastFrame = int.MinValue; samples = 0; }
    internal void Observe(UiSnapshot snapshot, int frame)
    {
        bool contiguous = lastFrame != int.MinValue && frame == lastFrame + 1;
        if (!contiguous) { last.Clear(); samples = 0; }
        samples++;
        var next = new Dictionary<int, UiElement>();
        foreach (UiElement element in snapshot.Elements)
        {
            bool seen = last.TryGetValue(element.Instance, out UiElement before), same = seen && UiRules.SameRect(before, element);
            element.StableFrames = element.Tweening ? 0 : same ? before.StableFrames + 1 : 1;
            element.Unstable = element.Tweening ? "tween" : !contiguous ? "sampling" : !seen ? "new" : !same ? "rect" : "settling";
            next[element.Instance] = element;
            if (element.OnScreen && element.StableFrames < UiRules.StableFrames) snapshot.Moving.Add(element.Id);
        }
        last = next; lastFrame = frame; snapshot.Samples = samples;
    }
}

internal static class UiRules
{
    internal const int StableFrames = 3, MaxElements = 300, MaxPressWaitMs = 10000, DefaultPressWaitMs = 3000, MaxLabel = 80;
    private const float RectTolerance = .5f;
    // Hit-test sample points as fractions of the on-screen rect (top-left origin): centre, quarter points, then, for
    // large elements only, points near the corners and edge midpoints. A full-screen off-click catcher whose centre is
    // covered by its own page frame still receives a real click near its edges; a covered small button costs 5 tests.
    internal static readonly float[][] HitSamples = {
        new[] { .5f, .5f }, new[] { .25f, .25f }, new[] { .75f, .25f }, new[] { .25f, .75f }, new[] { .75f, .75f },
        new[] { .0625f, .0625f }, new[] { .9375f, .0625f }, new[] { .0625f, .9375f }, new[] { .9375f, .9375f },
        new[] { .5f, .0625f }, new[] { .5f, .9375f }, new[] { .0625f, .5f }, new[] { .9375f, .5f } };
    internal const float LargeHitArea = .25f;
    // widthShare/heightShare: the clipped on-screen rect as a share of the screen.
    internal static int HitSampleCount(float widthShare, float heightShare) => widthShare * heightShare >= LargeHitArea ? HitSamples.Length : 5;
    private static readonly HashSet<string> DeniedWords = new HashSet<string>(StringComparer.Ordinal) {
        "store", "shop", "purchase", "buy", "checkout", "commerce", "payment", "wallet", "redeem",
        "bnet", "battlenet", "friend", "friends", "chat", "login", "logout", "account", "quit", "exit",
        "money", "topup", "cash", "credit", "runestones", "gift", "gifting"
    };
    private static readonly HashSet<string> LocalShopWords = new HashSet<string>(StringComparer.Ordinal) { "store", "shop", "purchase", "buy" };
    private static readonly HashSet<string> LocalShopIds = new HashSet<string>(StringComparer.Ordinal) {
        "home.store", "shop.buy", "shop.ack", "shop.back", "shop.page.close", "shop.quantity.cancel", "dialog.confirm", "dialog.cancel",
        "shop.kit.use", "shop.kit.full", // #57 deck kit question (NativeShopKitText)
        "shop.tab.cards", "shop.tab.decks_1", "shop.tab.decks_2", "shop.tab.skins", "shop.tab.themes", // Categories (NativeShopTabs.Ids), then their sub-tabs.
        "shop.tab.packs", "shop.tab.deck_deathknight", "shop.tab.deck_demonhunter", "shop.tab.deck_druid", "shop.tab.deck_hunter", "shop.tab.deck_mage", "shop.tab.deck_paladin",
        "shop.tab.deck_priest", "shop.tab.deck_rogue", "shop.tab.deck_shaman", "shop.tab.deck_warlock", "shop.tab.deck_warrior", "shop.tab.hero_skins", "shop.tab.card_backs",
        "shop.tab.coin_list", "shop.tab.pet_list", "shop.tab.board_list", "shop.tab.theme_list"
    };
    // Products: exactly the reserved local PMT ranges (packs, card backs, hero skins, recipes, decks) in canonical form.
    internal static bool IsLocalShopId(string id) => id != null && (LocalShopIds.Contains(id)
        || (id.StartsWith("shop.product.", StringComparison.Ordinal) && long.TryParse(id.Substring(13), out long product)
            && NativeShopPurchase.IsLocalPmtId(product) && id == "shop.product." + product)
        || (id.Length == 15 && id.StartsWith("shop.quantity.", StringComparison.Ordinal) && id[14] >= '0' && id[14] <= '6'));
    // Installed only by the isolated shop adapter; it checks live native object identity, never request text.
    internal static Func<UiElement, bool> LocalShopControlAllowed;
    // #57: the welcome popup's OK inherits player copy naming the gift and the Shop as its context. Installed by
    // NativeWelcomeUI, it accepts only the OK button of the live welcome popup this session raised, or the shown welcome
    // reward scroll's own dismiss (welcome.scroll.done), by native identity. Only the context check is lifted; id, type,
    // path and label still pass the denylist (the scroll's label minus the word "gift", WelcomeScrollLabel).
    internal static Func<UiElement, bool> WelcomeControlAllowed;
    // The verified welcome scroll's dismiss may carry the scroll's own title ("Welcome gift"): owner copy wins over the word
    // filter for exactly the word "gift" there; every other denied word in its label still denies.
    internal static string WelcomeScrollLabel(string label) =>
        label == null ? null : System.Text.RegularExpressions.Regex.Replace(label, @"\bgift\b", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // Native Collection tiles and preview controls (scene COLLECTIONMANAGER). On PC, a left click on a card back or
    // hero skin outside deck editing only shows the innkeeper "right-click" hint; the preview opens on RIGHTCLICK
    // (CollectionCardVisual.OnRightClick -> EnterCraftingMode -> CardBack/HeroSkinInfoManager.EnterPreviewWhenReady).
    internal const string CollectionFavorite = "collection.favorite", CollectionPreviewClose = "collection.preview.close";
    internal const string DeckHelperNotOwned = "deckhelper.notowned", DeckHelperClose = "deckhelper.close";
    internal const string CraftingCreate = "crafting.create", CraftingDisenchant = "crafting.disenchant", CraftingClose = "crafting.close";
    internal static string DeckHelperChoiceId(string cardId) => PlainWord(cardId, 64) ? "deckhelper.choice." + cardId : null;
    private const string CardBackTile = "collection.cardback.", HeroSkinTile = "collection.heroskin.", HeroClassTile = "collection.heroclass.", CoinTile = "collection.coin.";
    internal static string CollectionCoinId(int coinId) => coinId >= 1 ? CoinTile + coinId : null;
    internal static string CollectionCardBackId(int cardBackId) => cardBackId >= 0 ? CardBackTile + cardBackId : null;
    // Card ids and class names are plain ASCII words; anything else keeps its path id (and no right-click).
    internal static string CollectionHeroSkinId(string cardId) => PlainWord(cardId, 64) ? HeroSkinTile + cardId : null;
    internal static string CollectionHeroClassId(string heroClass) =>
        PlainWord(heroClass, 32) ? HeroClassTile + heroClass.ToLowerInvariant() : null;
    private static bool PlainWord(string text, int max) => !string.IsNullOrEmpty(text) && text.Length <= max
        && text.All(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_');
    // Canonical tile ids only: the preview gesture never reaches anything but a reviewed collection tile.
    internal static bool IsCollectionPreviewTile(string id)
    {
        if (id == null) return false;
        if (id.StartsWith(CoinTile, StringComparison.Ordinal))
            return int.TryParse(id.Substring(CoinTile.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int coin) && id == CollectionCoinId(coin);
        if (id.StartsWith(CardBackTile, StringComparison.Ordinal))
            return int.TryParse(id.Substring(CardBackTile.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int back) && id == CollectionCardBackId(back);
        return id.StartsWith(HeroSkinTile, StringComparison.Ordinal) && id == CollectionHeroSkinId(id.Substring(HeroSkinTile.Length));
    }
    // Coin gestures connect a canonical collection tile and the native deck coin socket.
    internal static bool IsDeckBoxId(string id) =>
        id != null && id.StartsWith("deck:", StringComparison.Ordinal) && long.TryParse(id.Substring(5), NumberStyles.None, CultureInfo.InvariantCulture, out long deck)
        && deck > 0 && id == "deck:" + deck;
    internal static bool IsCoinTile(string id) => id != null && id.StartsWith(CoinTile, StringComparison.Ordinal)
        && IsCollectionPreviewTile(id);
    internal static bool IsCoinDrag(string element, string target) =>
        IsCoinTile(element) && target == "collection.deck.coin" || element == "collection.deck.coin" && IsCoinTile(target);
    internal static string DragBlocker(string element, string target) =>
        IsCoinDrag(element, target) ? null : !IsDeckBoxId(element) || !IsDeckBoxId(target) ? "Drag needs deck boxes or a coin tile and deck coin socket"
        : element == target ? "Drag needs two different deck boxes" : null;
    // The hub gear (BnetBarMenuButton) opens the native Game Menu. Its type name carries "bnet", so it is denied unless
    // the installed adapter confirms the element is the live BnetBar's own menu button (native identity, never text).
    // The Game Menu's account, login and quit rows keep their own denials.
    internal const string GameMenuId = "home.menu", GameMenuOptionsId = "menu.options", GameMenuConcedeId = "menu.concede", OptionsDismissId = "options.dismiss";
    internal static bool IsMatchEscape(string id) => id == GameMenuId || id == GameMenuConcedeId || NativeReportText.IsControl(id);
    // Test control only, production input never reads it: a live match may open Options to prove what it shows there. Not an
    // escape: a frozen or unresolved match still refuses these.
    internal static bool IsMatchInspection(string id) => id == GameMenuOptionsId || id == OptionsDismissId;
    internal static Func<UiElement, bool> GameMenuControlAllowed;

    // A press is a left click unless it asks for "right", which only collection preview tiles accept.
    internal static string ButtonBlocker(string button, string id) =>
        button == null || button == "left" ? null
        : button == "right" ? (IsCollectionPreviewTile(id) ? null : "Right-click is only for collection preview tiles")
        : "Invalid button";

    internal static void RequireElementId(string id)
    {
        if (id.Any(character => character < ' ' || character == '\u007f')) throw new FormatException("Invalid element id");
    }

    internal static bool SameRect(UiElement a, UiElement b) =>
        a.OnScreen == b.OnScreen && a.ScrollClipped == b.ScrollClipped && Math.Abs(a.X - b.X) <= RectTolerance && Math.Abs(a.Y - b.Y) <= RectTolerance
        && Math.Abs(a.W - b.W) <= RectTolerance && Math.Abs(a.H - b.H) <= RectTolerance;

    // camelCase, digits and punctuation split into lowercase words: "BnetBarStoreButton" -> bnet bar store button.
    internal static List<string> Words(string text)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        void Flush() { if (word.Length != 0) { words.Add(word.ToString().ToLowerInvariant()); word.Clear(); } }
        for (int i = 0; i < (text ?? "").Length; i++)
        {
            char c = text[i];
            if (!char.IsLetterOrDigit(c)) { Flush(); continue; }
            if (word.Length != 0 && char.IsUpper(c) && (char.IsLower(text[i - 1])
                || (i + 1 < text.Length && char.IsLower(text[i + 1]) && char.IsUpper(text[i - 1])))) Flush();
            word.Append(c);
        }
        Flush();
        return words;
    }

    // Commerce stays denied except controls verified by the isolated local gold adapter.
    // Pure in its inputs, so memoized: tokenizing every element's path each sampled frame dominated heavy screens.
    private static readonly Dictionary<string, string> Denials = new Dictionary<string, string>(StringComparer.Ordinal);
    internal static string Deny(string type, string path, string id, string label, bool localGoldControl = false)
    {
        localGoldControl = localGoldControl && IsLocalShopId(id);
        string key = localGoldControl + "\u0001" + id + "\u0001" + type + "\u0001" + path + "\u0001" + label;
        if (Denials.TryGetValue(key, out string known)) return known;
        if (Denials.Count > 8192) Denials.Clear();
        return Denials[key] = DenyUncached(type, path, id, label, localGoldControl);
    }

    private static string DenyUncached(string type, string path, string id, string label, bool localGoldControl)
    {
        foreach (string source in new[] { id, type, path, label })
        {
            List<string> words = Words(source);
            for (int i = 1; i < words.Count; i++)
            {
                if ((words[i - 1] == "virtual" || words[i - 1] == "booster") && words[i] == "currency") return "denylist:currency";
                if (words[i - 1] == "chain" && words[i] == "offer") return "denylist:chainoffer";
                if (words[i - 1] == "top" && words[i] == "up") return "denylist:topup";
                if (words[i - 1] == "battle" && words[i] == "net") return "denylist:battlenet";
            }
            string word = words.FirstOrDefault(word => DeniedWords.Contains(word)
                && !(localGoldControl && LocalShopWords.Contains(word)));
            if (word != null) return "denylist:" + word;
        }
        return null;
    }

    internal static string PathId(string path)
    {
        if (path.Length <= 200) return "path:" + path;
        string tail = path.Substring(path.Length - 180);
        return "path:~" + TestControlProtocol.Hash(path).Substring(0, 8).ToLowerInvariant() + "/" + tail.Substring(tail.IndexOf('/') + 1);
    }

    // Semantic ids stay as given; others use the hierarchy path. Duplicates get #2, #3 in screen order.
    internal static void AssignIds(List<UiElement> elements)
    {
        foreach (UiElement element in elements) if (string.IsNullOrEmpty(element.Id)) element.Id = PathId(element.Path ?? element.Type);
        foreach (var group in elements.GroupBy(element => element.Id).Where(group => group.Count() > 1).ToList())
        {
            int index = 1;
            foreach (UiElement element in group.OrderByDescending(element => element.Id == "shop.back" && element.Hit).ThenBy(element => element.Y).ThenBy(element => element.X).ThenBy(element => element.Instance))
                if (index++ > 1) element.Id += "#" + (index - 1);
        }
    }

    // Structural identity: what is listed and which native instances back it. Moves do not change it.
    internal static string Generation(UiSnapshot snapshot) =>
        TestControlProtocol.Hash(snapshot.Scene + "|" + snapshot.Screen + "|" + string.Join(",",
            snapshot.Elements.Select(element => element.Id + "@" + element.Instance).OrderBy(id => id, StringComparer.Ordinal)))
            .Substring(0, 12).ToLowerInvariant();

    internal static void Finish(UiSnapshot snapshot)
    {
        foreach (UiElement element in snapshot.Elements)
        {
            bool localGoldControl = false;
            try { localGoldControl = LocalShopControlAllowed?.Invoke(element) == true; }
            catch { /* An unavailable native adapter must leave the normal denylist in force. */ }
            // A reviewed semantic id names the control by its owner's field, so its GameObject path is layout, not
            // purpose: the picker's Back was denied by a path word. Unnamed and dialog buttons keep the path check.
            bool reviewed = element.Id != null && !element.Id.StartsWith("dialog.", StringComparison.Ordinal)
                && !element.Id.StartsWith("shop.", StringComparison.Ordinal) && !element.Id.StartsWith("path:", StringComparison.Ordinal);
            bool welcome = false;
            try { welcome = (element.Id == NativeWelcomeText.OkId || element.Id == NativeWelcomeText.ScrollDoneId) && WelcomeControlAllowed?.Invoke(element) == true; }
            catch { /* An unavailable welcome adapter leaves the context check in force. */ }
            bool gear = false;
            try { gear = element.Id == GameMenuId && GameMenuControlAllowed?.Invoke(element) == true; }
            catch { /* An unavailable gear adapter leaves the bnet type denial in force. */ }
            // Deck guide dialog buttons (guide.next, guide.close; ids exist only on our guide popups): the guide is owner-reviewed deck text that
            // may use words like "buy" or "gift" as game terms, so the popup text is not their context. Like every reviewed semantic id (guide.* is not dialog.*) their GameObject path is not checked either; id, type and label still pass the denylist.
            bool guide = NativeDeckGuideText.IsDialogButtonId(element.Id);
            // A pack actor's printed card text is not a commerce action (e.g. a card with "Gift" in its name).
            bool packCard = element.Pack?.ContainsKey("cardId") == true && element.Id?.StartsWith("packs.card:", StringComparison.Ordinal) == true;
            element.Denied = packCard ? Deny(element.Type, null, element.Id, null)
                : gear ? Deny(null, null, element.Id, element.Label) ?? Deny(null, null, element.Id, element.Context)
                : Deny(element.Type, reviewed && !localGoldControl ? null : element.Path, element.Id,
                    welcome && element.Id == NativeWelcomeText.ScrollDoneId ? WelcomeScrollLabel(element.Label) : element.Label, localGoldControl)
                ?? (welcome || guide ? null : Deny(null, null, element.Id, element.Context, localGoldControl));
            if (element.Label != null && element.Label.Length > MaxLabel) element.Label = element.Label.Substring(0, MaxLabel);
        }
        var ordered = snapshot.Elements.OrderByDescending(element => element.Hit).ThenBy(element => element.Y).ThenBy(element => element.X).ToList();
        snapshot.Truncated = ordered.Count > MaxElements;
        snapshot.Elements.Clear(); snapshot.Elements.AddRange(ordered.Take(MaxElements));
        AssignIds(snapshot.Elements);
        snapshot.Generation = Generation(snapshot);
    }

    // Rejections that waiting cannot fix.
    internal static string PermanentBlocker(UiSnapshot snapshot, string id, string generation)
    {
        if (generation != null && generation != snapshot.Generation) return "Stale snapshot " + generation + "; current " + snapshot.Generation;
        if (snapshot.MatchLive && !IsMatchEscape(id) && !IsMatchInspection(id)) return "Live match: use semantic match commands";
        string denied = snapshot.Find(id)?.Denied;
        return denied == null ? null : "Denied by " + denied;
    }

    // Actionability (listed, visible, enabled, receives the click, stable); press waits for these until its deadline.
    internal static string TransientBlocker(UiSnapshot snapshot, string id)
    {
        if (snapshot.Busy.Count != 0) return "Busy: " + string.Join(",", snapshot.Busy);
        UiElement element = snapshot.Find(id);
        if (element == null) return "Element not listed";
        string scrollHint = id == "collection.newdeck" || id.StartsWith("collection.deckcard.", StringComparison.Ordinal) || IsDeckBoxId(id)
            ? " (offscreen: use ui-scroll " + id + ")" : "";
        if (!element.OnScreen) return "Element off screen" + scrollHint;
        if (element.ScrollClipped) return "Element clipped by scroll area" + scrollHint;
        if (!element.Enabled) return "Element disabled";
        if (element.PressDisabled) return "Element press-disabled by its owner";
        if (!element.Hit) return element.Id == PackFlipText.ButtonId || element.Id.StartsWith("packs.", StringComparison.Ordinal)
            ? "Element blocked by " + (element.HitBy ?? "unknown hit target")
            : "Element does not receive the click" + (element.HitBy == null ? "" : "; hit " + element.HitBy);
        if (element.StableFrames < StableFrames) return "Element moving";
        return null;
    }
}
