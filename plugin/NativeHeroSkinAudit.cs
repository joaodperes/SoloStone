#if !SOLOSTONE_RELEASE
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MiniJSON;
using UnityEngine;

// Asset reads only: never instantiate/activate a spell to inventory it.
internal static class NativeHeroSkinAudit
{
    private static string skin;
    private static JsonNode result;
    private static int generation, pending;
    private static float deadline;
    internal static object Read(string cardId)
    {
        NativeTestControl.AssertMainThread();
        if (!HeroSkinData.All.TryGetValue(cardId, out var definition)) throw new ArgumentException("Unknown pinned hero skin " + cardId);
        if (pending != 0 && Time.realtimeSinceStartup >= deadline)
        { generation++; pending = 0; result["status"] = "timeout"; }
        if (skin == cardId) return Snapshot();
        if (pending != 0) throw new InvalidOperationException("Skin asset audit pending: " + skin);
        skin = cardId;
        int ticket = ++generation;
        deadline = Time.realtimeSinceStartup + 20;
        var cards = new JsonList();
        result = new JsonNode { { "skin", cardId }, { "baseHero", definition.BaseHero }, { "staticAttack", definition.StaticAttack },
            { "corner", definition.Corner }, { "status", "loading" }, { "cards", cards },
            { "graphProof", "Spell tables only; live attacks/powers and FX state traces prove completion" } };
        HeroSkinData.All.TryGetValue(definition.BaseHero, out var baseDefinition);
        var ids = new[] { cardId, definition.Power, definition.UpgradedPower, definition.BaseHero,
            baseDefinition?.Power, baseDefinition?.UpgradedPower }.Where(id => !string.IsNullOrEmpty(id)).Distinct().ToArray();
        pending = ids.Length;
        foreach (string id in ids)
        {
            var row = new JsonNode { { "cardId", id }, { "status", "loading" } };
            cards.Add(row);
            try
            {
                DefLoader.Get().LoadCardDefAsync(id, (loadedId, disposable, user) => {
                    try
                    {
                        if (generation != ticket) return;
                        var def = disposable?.CardDef;
                        if (def == null) throw new InvalidOperationException("Missing offline CardDef");
                        var entries = new JsonList();
                        bool truncated = false;
                        foreach (var field in typeof(CardDef).GetFields(BindingFlags.Instance | BindingFlags.Public))
                            if (field.Name.IndexOf("Spell", StringComparison.OrdinalIgnoreCase) >= 0
                                || field.Name.IndexOf("Emote", StringComparison.OrdinalIgnoreCase) >= 0
                                || field.Name.IndexOf("Effect", StringComparison.OrdinalIgnoreCase) >= 0)
                                Walk(field.GetValue(def), field.Name, entries, 0, ref truncated);
                        row["spells"] = entries; row["status"] = truncated ? "truncated" : "loaded";
                    }
                    catch (Exception error) { row["status"] = "failed"; row["error"] = error.Message; }
                    finally
                    {
                        disposable?.Dispose();
                        if (generation == ticket && --pending == 0) result["status"] = "complete";
                    }
                });
            }
            catch (Exception error)
            {
                row["status"] = "failed"; row["error"] = error.Message;
                if (--pending == 0) result["status"] = "complete";
            }
        }
        return Snapshot();
    }
    // Socket serialization runs off-thread. Never enqueue the cache that later asset callbacks mutate.
    private static object Snapshot() => Json.Deserialize(Json.Serialize(result));
    private static void Walk(object value, string path, JsonList rows, int depth, ref bool truncated)
    {
        if (value == null) return;
        if (depth > 6 || rows.Count >= 512) { truncated = true; return; }
        if (value is string text)
        {
            if (text.Length != 0) rows.Add(new JsonNode { { "field", path }, { "value", text } });
            return;
        }
        if (value is IEnumerable list)
        {
            int index = 0;
            foreach (var item in list)
            {
                if (index == 128) { truncated = true; break; }
                Walk(item, path + "[" + index++ + "]", rows, depth + 1, ref truncated);
            }
            return;
        }
        if (value.GetType().IsEnum)
        { rows.Add(new JsonNode { { "field", path }, { "value", value.ToString() }, { "number", Convert.ToInt32(value) } }); return; }
        if (value.GetType() != typeof(CardEffectDef) && value.GetType() != typeof(SpellTableOverride)
            && value.GetType() != typeof(EmoteEntryDef)) return;
        foreach (var field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            Walk(field.GetValue(value), path + "." + field.Name, rows, depth + 1, ref truncated);
    }
}
#endif
