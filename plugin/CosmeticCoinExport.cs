#if !SOLOSTONE_RELEASE
using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using MiniJSON;

// Client reads and atomic report publication run on the endpoint's Unity main thread.
internal static class CosmeticCoinExport
{
    internal static string Root;

    internal static JsonNode Export()
    {
        NativeTestControl.AssertMainThread();
        if (!GameDbf.IsLoaded || GameDbf.CosmeticCoin == null)
            throw new InvalidOperationException("CosmeticCoin database has not loaded");
        // StripUnusedLocales can discard enUS; an explicit locale argument cannot restore it.
        var localization = Type.GetType("Localization, Blizzard.T5.Localization", true);
        var locale = localization.GetMethod("GetActualLocale", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
        if (locale == null || locale.Invoke(null, null)?.ToString() != "enUS")
            throw new InvalidOperationException("CosmeticCoin export requires the client locale enUS");
        var records = GameDbf.CosmeticCoin.GetRecords();
        if (records == null || records.Count == 0)
            throw new InvalidOperationException("No loaded CosmeticCoin records");
        string path = Path.Combine(Root, "coins.json");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        OfflineGuard.RejectReparsePoints(path);
        OfflineGuard.RejectReparsePoints(temporary);
        Directory.CreateDirectory(Root);
        try
        {
            using (var writer = new StreamWriter(temporary, false, TestControlProtocol.Utf8, 64 * 1024))
            {
                writer.Write("{\"locale\":\"enUS\",\"count\":" + records.Count + ",\"coins\":[");
                bool first = true;
                var ids = new HashSet<int>();
                foreach (var record in records)
                {
                    if (record == null || !ids.Add(record.ID)) throw new InvalidOperationException("Null or duplicate loaded CosmeticCoin record");
                    if (!first) writer.Write(',');
                    writer.Write(Json.Serialize(Record(record)));
                    first = false;
                }
                writer.Write("]}");
                writer.Flush();
                writer.BaseStream.Flush();
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
            return new JsonNode { { "count", records.Count }, { "path", path } };
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static JsonNode Record(CosmeticCoinDbfRecord record)
    {
        return new JsonNode {
            { "id", record.ID }, { "enabled", record.Enabled }, { "cardDbfId", record.CardId },
            { "cardId", record.CardRecord?.NoteMiniGuid }, { "name", record.Name?.GetString() },
            { "description", (record.GetVar("DESCRIPTION") as DbfLocValue)?.GetString() }
        };
    }
}
#endif
