#if !SOLOSTONE_RELEASE
using System;
using System.IO;
using System.Reflection;
using MiniJSON;

// Client reads and atomic report publication run on the endpoint's Unity main thread.
internal static class CardBackExport
{
    private const string Root = @"C:\hs-native-ui-"+@"prototype\client\reports";

    internal static JsonNode Export()
    {
        NativeTestControl.AssertMainThread();
        if (!GameDbf.IsLoaded || GameDbf.CardBack == null)
            throw new InvalidOperationException("CardBack database has not loaded");
        // StripUnusedLocales can discard enUS; an explicit locale argument cannot restore it.
        var localization = Type.GetType("Localization, Blizzard.T5.Localization", true);
        var locale = localization.GetMethod("GetActualLocale", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
        if (locale == null || locale.Invoke(null, null)?.ToString() != "enUS")
            throw new InvalidOperationException("CardBack export requires the client locale enUS");
        var records = GameDbf.CardBack.GetRecords();
        if (records == null || records.Count == 0)
            throw new InvalidOperationException("No loaded CardBack records");
        string path = Path.Combine(Root, "cardbacks.json");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        OfflineGuard.RejectReparsePoints(path);
        OfflineGuard.RejectReparsePoints(temporary);
        Directory.CreateDirectory(Root);
        try
        {
            using (var writer = new StreamWriter(temporary, false, TestControlProtocol.Utf8, 64 * 1024))
            {
                writer.Write("{\"locale\":\"enUS\",\"count\":" + records.Count + ",\"cardbacks\":[");
                bool first = true;
                foreach (var record in records)
                {
                    if (record == null) throw new InvalidOperationException("Null loaded CardBack record");
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

    private static JsonNode Record(CardBackDbfRecord record)
    {
        return new JsonNode {
            { "id", record.ID }, { "data1", record.Data1 },
            { "source", (int)record.Source }, { "sourceName", record.Source.ToString() },
            { "enabled", record.Enabled }, { "sortCategory", (int)record.SortCategory },
            { "sortCategoryName", record.SortCategory.ToString() }, { "sortOrder", record.SortOrder },
            { "prefabName", record.PrefabName }, { "name", record.Name?.GetString() },
            { "description", record.Description?.GetString() }, { "isRandomCardBack", record.IsRandomCardBack },
            { "collectionManagerPurchaseProductId", record.CollectionManagerPurchaseProductId }
        };
    }
}
#endif
