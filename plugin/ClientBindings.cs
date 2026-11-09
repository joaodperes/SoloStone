using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

internal sealed class NativeCardMismatchException : Exception
{
    internal NativeCardMismatchException(string message) : base(message) { }
}

internal sealed class NativeCardDrift
{
    private readonly List<string> rows = new List<string> { "id\tfield\tretail\tfacts" };
    private readonly HashSet<string> cards = new HashSet<string>(StringComparer.Ordinal);
    internal int Cards => cards.Count;
    internal int Fields => rows.Count - 1;
    internal void Add(string id, string field, int retail, int facts)
    {
        if (retail == facts) return;
        cards.Add(id); rows.Add(id + "\t" + field + "\t" + retail + "\t" + facts);
    }
    internal void Write(string path) { try { File.WriteAllLines(path, rows); } catch { } }
}

// V1 (release plan 2a.3): does the running client still have what this plugin binds to? The rows come from plugin/client-bindings.txt, built
// from the compiled plugin by `tool bindings` and held to it by the client-bindings gate node. Unity-free, so tests run it on planted rows.
// Row: kind, assembly (* when the plugin only knows a name), type, member, signature. Kinds: A assembly, T type, M method, C constructor,
// F field, P property, E iterator a transpiler patches. Signature: * for any, (params), return(params), or a field's type.
internal static class ClientBindings
{
    internal const string Resource = "client-bindings.txt";
    // Assemblies the runtime owns or the plugin ships: not the client's to break. One rule, for the scan and for this.
    private static readonly string[] NotClient = { "mscorlib", "netstandard", "System", "Microsoft", "Mono.", "BepInEx", "0Harmony", "HarmonyLib", "MonoMod", "SabberStoneCore",
#if !SOLOSTONE_RELEASE
        "HSNativeUIProbe", // the lab and default builds' plugin name; a public build does not spell it
#endif
        "SoloStone.Plugin" };
    internal static bool IsClientAssembly(string name) => name != null && !NotClient.Any(prefix => name == prefix
        || name.StartsWith(prefix + ".", StringComparison.Ordinal) || (prefix.EndsWith(".", StringComparison.Ordinal) && name.StartsWith(prefix, StringComparison.Ordinal)));
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    internal sealed class Client
    {
        internal Func<string, bool> Assembly;
        internal Func<string, string, Type> Type;      // assembly or "*", full type name.
        internal Func<MethodInfo, bool> Iterator;
    }

    internal static string[] ReadRows(Stream stream)
    {
        if (stream == null) throw new InvalidOperationException("This SoloStone build is missing its client binding list.");
        using (var reader = new StreamReader(stream))
        {
            var rows = reader.ReadToEnd().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Where(line => line[0] != '#').ToArray();
            if (rows.Length == 0) throw new InvalidOperationException("This SoloStone build has an empty client binding list.");
            return rows;
        }
    }

    // The targets this client lacks, in row order. Anything that throws while looking counts as lacking: V1 fails closed.
    internal static List<string> Missing(IEnumerable<string> rows, Client client)
    {
        var missing = new List<string>();
        var methods = new Dictionary<Type, MethodBase[]>();
        var signatures = new Dictionary<MethodBase, string[]>();
        foreach (string row in rows)
        {
            string[] p = row.Split('\t');
            if (p.Length != 5) { missing.Add("unreadable row: " + row); continue; }
            try { if (!Present(p, client, methods, signatures)) missing.Add(Describe(p)); }
            catch (Exception error) when (!(error is OutOfMemoryException)) { missing.Add(Describe(p) + " (" + error.GetType().Name + ")"); }
        }
        return missing;
    }

    internal static string Describe(string[] p)
    {
        switch (p[0])
        {
            case "A": return "assembly " + p[1];
            case "T": return "type " + p[2];
            case "F": return "field " + p[2] + "." + p[3];
            case "P": return "property " + p[2] + "." + p[3];
            default: return "method " + p[2] + "." + p[3] + (p[4] == "*" ? "" : " " + p[4]);
        }
    }

    private static bool Present(string[] p, Client client, Dictionary<Type, MethodBase[]> methods, Dictionary<MethodBase, string[]> signatures)
    {
        string kind = p[0], assembly = p[1], type = p[2], member = p[3], signature = p[4];
        if (kind == "A") return client.Assembly(assembly);
        Type owner = client.Type(assembly, type);
        if (owner == null) return false;
        if (kind == "T") return true;
        if (kind == "C") return FindMethods(owner, methods).Any(method => method is ConstructorInfo && Matches(method, signature, signatures));
        for (Type current = owner; current != null; current = current.BaseType)
        {
            if (kind == "F")
            {
                foreach (FieldInfo field in current.GetFields(Declared))
                    if (field.Name == member && (signature == "*" || Format(field.FieldType) == signature)) return true;
            }
            else if (kind == "P")
            {
                foreach (PropertyInfo property in current.GetProperties(Declared))
                    if (property.Name == member) return true;
            }
            else
            {
                foreach (MethodBase method in FindMethods(current, methods))
                    if (method.Name == member && Matches(method, signature, signatures))
                    {
                        if (kind != "E") return true;
                        if (method is MethodInfo iterator && client.Iterator(iterator)) return true;
                    }
            }
        }
        // A method named on an interface's own base interfaces is not on any BaseType chain.
        if (owner.IsInterface && kind == "M")
            foreach (Type parent in owner.GetInterfaces())
                foreach (MethodBase method in FindMethods(parent, methods))
                    if (method.Name == member && Matches(method, signature, signatures)) return true;
        return false;
    }

    private static MethodBase[] FindMethods(Type type, Dictionary<Type, MethodBase[]> methods)
    {
        if (!methods.TryGetValue(type, out MethodBase[] found))
            methods[type] = found = type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)).ToArray();
        return found;
    }

    private static bool Matches(MethodBase method, string signature, Dictionary<MethodBase, string[]> signatures)
    {
        if (signature == "*") return true;
        if (!signatures.TryGetValue(method, out string[] known))
        {
            string parameters = "(" + string.Join(",", method.GetParameters().Select(parameter => Format(parameter.ParameterType)).ToArray()) + ")";
            signatures[method] = known = new[] { parameters, method is MethodInfo info ? Format(info.ReturnType) + parameters : "System.Void" + parameters };
        }
        return signature == known[0] || signature == known[1];
    }

    // The spelling BindingScan reads from metadata.
    internal static string Format(Type type)
    {
        if (type.IsGenericParameter) return (type.DeclaringMethod != null ? "!!" : "!") + type.GenericParameterPosition;
        if (type.IsByRef) return Format(type.GetElementType()) + "&";
        if (type.IsPointer) return Format(type.GetElementType()) + "*";
        if (type.IsArray) return Format(type.GetElementType()) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        if (type.IsGenericType)
            return type.GetGenericTypeDefinition().FullName + "<" + string.Join(",", type.GetGenericArguments().Select(Format).ToArray()) + ">";
        return type.FullName;
    }

    // The real patch registrations against the rows: a patched client method no row names means the manifest is stale. Compiler-made iterator
    // classes are covered by their method's E row. selfAdapting: patches from a name filter that observes whatever exists, so no method there is required.
    // Reported, never a block: it says nothing about the client. The live run fails on it.
    internal static List<string> Uncovered(IEnumerable<MethodBase> patched, IEnumerable<string> rows, ICollection<MethodBase> selfAdapting)
    {
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (string row in rows)
        {
            string[] p = row.Split('\t');
            if (p.Length == 5 && (p[0] == "M" || p[0] == "C" || p[0] == "E")) named.Add(p[2] + "." + p[3]);
        }
        var uncovered = new List<string>();
        foreach (MethodBase method in patched)
        {
            Type type = method.DeclaringType;
            if (type == null || !IsClientAssembly(type.Assembly.GetName().Name) || selfAdapting.Contains(method)) continue;
            string owner = type.FullName;
            if (method.Name == "MoveNext" && type.DeclaringType != null && type.Name.StartsWith("<", StringComparison.Ordinal))
            {
                int end = type.Name.IndexOf('>');
                if (end > 1 && named.Contains(type.DeclaringType.FullName + "." + type.Name.Substring(1, end - 1))) continue;
            }
            if (!named.Contains(owner + "." + method.Name)) uncovered.Add(owner + "." + method.Name);
        }
        return uncovered;
    }

#if !SOLOSTONE_RELEASE
    // A lab build's own patches have no row: the manifest is the public build's, and a missing test hook must not block a player. They register on this owner, which a
    // public build cannot name, so nothing here can excuse a patch a player's build makes. A target shared code patches too stays required.
    internal const string LabOwner = "local.hs.lab-only";
    internal static List<MethodBase> LabOnly(IEnumerable<MethodBase> patched, Func<MethodBase, IEnumerable<string>> owners) =>
        patched.Where(method => { var names = owners(method); return names != null && names.Any() && names.All(name => name == LabOwner); }).ToList();
#endif

    // V2: the pool cards this client has no record of.
    internal static List<string> MissingCards(IEnumerable<string> pool, Func<string, bool> hasRecord) =>
        pool.Where(id => !hasRecord(id)).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList();

    // What a block tells the player and the launcher: the check, a count and the first few names.
    internal static string Summary(string what, IList<string> missing)
    {
        const int Shown = 5;
        return missing.Count + " " + what + " missing: " + string.Join(", ", missing.Take(Shown).ToArray()) + (missing.Count > Shown ? ", and more" : "");
    }
}

// reports\client-check.txt: where a launch stands in V1, V2 and the save hold. The launcher reads it for a block's reason, and tools read it because
// a blocked client is gone. It lives in reports, so a failed check writes nothing to the save tree.
internal static class ClientCheckReport
{
    internal const string FileName = "client-check.txt";
    internal const string Held = "held", Released = "released", Refused = "refused", CoverageFile = "client-check-coverage.txt";
    // The client never loaded its card records. Native startup is stuck, nothing can be checked, and saves stay on hold. The launcher waits longer than this.
    internal const int CardWaitSeconds = 300;

    internal static void Write(string reports, string runId, string state, string check, string detail)
    {
        string path = Path.Combine(reports, FileName), temporary = path + ".tmp";
        File.WriteAllLines(temporary, new[] {
            "schema=1", "runId=" + runId, "state=" + state, "check=" + (check ?? ""), "detail=" + (detail ?? "").Replace('\r', ' ').Replace('\n', ' '),
            "utc=" + DateTime.UtcNow.ToString("o")
        });
        if (File.Exists(path)) File.Delete(path);
        File.Move(temporary, path);
    }
}
