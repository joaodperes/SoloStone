using System;
using System.Collections.Generic;
using System.Reflection;

// Type lookup by name with AccessTools.TypeByName's answers but not its cost. The bundled HarmonyX walks every type of
// every loaded assembly on each call (~100 ms per lookup on the Deck, ~21 s of plugin Awake before this). Here an exact
// name is asked of each assembly in load order (the same first match a FullName walk finds); only a bare name that no
// assembly knows falls back to the short-name walk. Hits are cached; misses are not, so a later-loaded assembly is seen.
internal static class GameTypes
{
    private static readonly Dictionary<string, Type> Cache = new Dictionary<string, Type>(StringComparer.Ordinal);
    internal static Func<Assembly[]> Assemblies = () => AppDomain.CurrentDomain.GetAssemblies();
    internal static int Walks { get; private set; }

    internal static Type ByName(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        lock (Cache)
            if (Cache.TryGetValue(name, out Type known)) return known;
        Type type = Resolve(name);
        if (type != null) lock (Cache) Cache[name] = type;
        return type;
    }

    private static Type Resolve(string name)
    {
        // TypeByName's Type.GetType ran in HarmonyX's own context: core library types and assembly-qualified names only.
        Type type = null;
        try { type = name.IndexOf(',') >= 0 ? Type.GetType(name, false) : typeof(object).Assembly.GetType(name, false); }
        catch (Exception) { }
        if (type != null) return type;
        Assembly[] assemblies = Assemblies();
        foreach (Assembly assembly in assemblies)
        {
            try { type = assembly.GetType(name, false); } catch (Exception) { type = null; }
            if (type != null) return type;
        }
        // A type's short Name never holds '.', '+' or ','; such names can only match a full name, already asked above.
        if (name.IndexOfAny(new[] { '.', '+', ',', '[' }) >= 0) return null;
        Walks++;
        foreach (Assembly assembly in assemblies)
            foreach (Type candidate in TypesOf(assembly))
                if (candidate != null && candidate.Name == name) return candidate;
        return null;
    }

    private static Type[] TypesOf(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException error) { return error.Types ?? Type.EmptyTypes; }
        catch (Exception) { return Type.EmptyTypes; }
    }
}
