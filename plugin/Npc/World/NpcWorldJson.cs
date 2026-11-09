using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

// Typed accessors over the MiniJSON object model (IDictionary<string, object>, IList, long, double, string, bool).
// World code never references MiniJSON or a BCL JSON serializer: hosts inject the parser.
// Plugin: NpcWorldJson.Parser = EmbeddedJson.Parse (strict syntax, Unity-Mono safe). Tests: MiniJSON.Json.Deserialize.
public static class NpcWorldJson
{
    public static Func<string, object> Parser { get; set; }

    public static object Parse(string text)
    {
        if (Parser == null) throw new InvalidOperationException("NpcWorldJson.Parser is not installed.");
        return Parser(text ?? throw new InvalidDataException("Missing JSON."));
    }

    public static IDictionary<string, object> Object(object value) =>
        value as IDictionary<string, object> ?? throw new InvalidDataException("Expected JSON object.");

    public static IList Array(object value) => value as IList ?? throw new InvalidDataException("Expected JSON array.");

    public static IList List(IDictionary<string, object> row, string key, bool required = true)
    {
        object value;
        if (!row.TryGetValue(key, out value) || value == null)
        {
            if (required) throw new InvalidDataException("Missing array: " + key);
            return new object[0];
        }
        return Array(value);
    }

    public static string Text(IDictionary<string, object> row, string key, bool required = true)
    {
        object value;
        if (!row.TryGetValue(key, out value) || value == null)
        {
            if (required) throw new InvalidDataException("Missing string: " + key);
            return null;
        }
        return value as string ?? throw new InvalidDataException("Expected string: " + key);
    }

    public static int Integer(IDictionary<string, object> row, string key, int? fallback = null)
    {
        object value;
        if (!row.TryGetValue(key, out value) || value == null)
        {
            if (fallback.HasValue) return fallback.Value;
            throw new InvalidDataException("Missing integer: " + key);
        }
        if (!(value is long) || (long)value < int.MinValue || (long)value > int.MaxValue)
            throw new InvalidDataException("Expected integer: " + key);
        return (int)(long)value;
    }

    public static double Number(IDictionary<string, object> row, string key, double? fallback = null)
    {
        object value;
        if (!row.TryGetValue(key, out value) || value == null)
        {
            if (fallback.HasValue) return fallback.Value;
            throw new InvalidDataException("Missing number: " + key);
        }
        if (value is long) return (long)value;
        if (value is double && !double.IsNaN((double)value) && !double.IsInfinity((double)value)) return (double)value;
        throw new InvalidDataException("Expected number: " + key);
    }

    public static bool Boolean(IDictionary<string, object> row, string key, bool fallback = false)
    {
        object value;
        if (!row.TryGetValue(key, out value) || value == null) return fallback;
        if (value is bool) return (bool)value;
        throw new InvalidDataException("Expected boolean: " + key);
    }

    public static List<string> Strings(IDictionary<string, object> row, string key, bool required = false)
    {
        var result = new List<string>();
        foreach (object item in List(row, key, required))
            result.Add(item as string ?? throw new InvalidDataException("Expected string in " + key));
        return result;
    }

    public static string Invariant(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
