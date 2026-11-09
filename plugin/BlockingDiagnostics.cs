using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using MiniJSON;

// Client-free, read-only reflection so missing members cannot break native processing.
internal static class BlockingDiagnostics
{
    internal const int MaxLength = 2048;
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    internal static string Bound(string text) => text == null || text.Length <= MaxLength ? text : text.Substring(0, MaxLength - 1) + "…";

    internal static JsonNode Read(object front) => new JsonNode {
        { "blockingCauses", Bound(Call(front, "BuildServerBlockingCausesString") as string) },
        { "blockingSpells", Describe(Get(front, "m_serverBlockingSpells"), false) },
        { "blockingSpellControllers", Describe(Get(front, "m_serverBlockingSpellControllers"), true) }
    };

    internal static object Get(object target, string name)
    {
        try
        {
            for (var type = target?.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField(name, Members);
                if (field != null) return field.GetValue(target);
                var property = type.GetProperty(name, Members);
                if (property != null) return property.GetValue(target, null);
            }
        }
        catch (Exception) { }
        return null;
    }

    private static object Call(object target, string name)
    {
        try
        {
            for (var type = target?.GetType(); type != null; type = type.BaseType)
            {
                var method = type.GetMethod(name, Members, null, Type.EmptyTypes, null);
                if (method != null) return method.Invoke(target, null);
            }
        }
        catch (Exception) { }
        return null;
    }

    private static string Describe(object collection, bool controller)
    {
        if (!(collection is IEnumerable items)) return null;
        try
        {
            var text = new StringBuilder();
            int count = 0;
            foreach (object item in items)
            {
                // Bound enumeration as well as output, including corrupt/destroyed entries.
                if (count++ == 16 || text.Length >= MaxLength) { text.Append("…"); break; }
                if (text.Length != 0) text.Append("; ");
                if (item == null) { text.Append("null"); continue; }
                object source = Call(item, controller ? "GetSource" : "GetSourceCard");
                string card = Call(Call(source, "GetEntity"), "GetCardId") as string;
                var detail = new JsonNode { { "type", item.GetType().Name }, { "name", Bound(Get(item, "name") as string) }, { "sourceCard", card } };
                if (controller)
                {
                    object task = Call(item, "GetPowerTaskList");
                    detail["taskList"] = Call(task, "GetId");
                    detail["blockType"] = Call(task, "GetBlockType")?.ToString();
                    object spell = Get(item, "m_sourceAttackSpell") ?? Get(item, "m_powerSpell") ?? Get(item, "m_triggerSpell") ?? Get(item, "m_secretSpell");
                    detail["attackType"] = Get(item, "m_attackType")?.ToString();
                    detail["spellName"] = Bound(Get(spell, "name") as string);
                    detail["spellState"] = Get(spell, "m_activeStateType")?.ToString();
                    detail["fsmState"] = Bound(Get(Get(spell, "m_fsm"), "ActiveStateName") as string);
                    detail["pendingTasks"] = Get(task, "m_pendingTasks");
                }
                else
                {
                    detail["state"] = Get(item, "m_activeStateType")?.ToString();
                    object fsm = Get(item, "m_fsm");
                    detail["fsm"] = Bound(Get(fsm, "FsmName") as string);
                    detail["fsmState"] = Bound(Get(fsm, "ActiveStateName") as string);
                }
                text.Append(Json.Serialize(detail));
            }
            return Bound(text.ToString());
        }
        catch (Exception) { return null; }
    }
}

// Unity's monotonic clock; rates are independent for A/B/A cause changes within a stall.
internal sealed class BlockedTelemetryGate
{
    private readonly Dictionary<string, double> emitted = new Dictionary<string, double>();
    private string match, progress;
    private double since, lastTime;
    private bool tracking;

    internal bool Poll(double now, bool enabled, bool inMatch, bool blocked, string matchId, string taskProgress, string causes)
    {
        if (!enabled || !inMatch || !blocked) { tracking = false; emitted.Clear(); return false; }
        if (!tracking || match != matchId || progress != taskProgress || now < lastTime)
        {
            tracking = true; match = matchId; progress = taskProgress; since = now; emitted.Clear();
        }
        lastTime = now;
        if (now - since <= 5) return false;
        string key = BlockingDiagnostics.Bound(causes) ?? "null";
        if (emitted.TryGetValue(key, out double previous) && now - previous < 5) return false;
        foreach (string expired in emitted.Where(pair => now - pair.Value >= 5).Select(pair => pair.Key).ToArray()) emitted.Remove(expired);
        if (emitted.Count >= 64) return false;
        emitted[key] = now;
        return true;
    }
}
