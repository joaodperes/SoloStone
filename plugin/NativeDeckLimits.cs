using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

// Retail hard-codes a 27-deck Collection tray: 29 TraySections (27 doors + 2 spare), the New Deck gate, the
// last used/unused slot scans, the delete animation's slot shuffle (25/26) and the "N/27" counter. The deck picker
// pages are already dynamic (9 per page). Each literal is replaced by a call that keeps retail values unless the
// local probe is active, then shifts it to NativeUIProjection.NativeDeckLimit.
internal static partial class NativeUIProbe
{
    // Native method -> literal -> exact occurrence count in the pinned client (native-bindings pins the same IL).
    internal static readonly (Type Type, string Method, bool Iterator, int[] Literals)[] DeckLimitSites = {
        (typeof(DeckTrayDeckListContent), "CreateTraySections", false, new[] { 27, 29 }),
        (typeof(DeckTrayDeckListContent), "RefreshTraySectionPositions", false, new[] { 29 }),
        (typeof(DeckTrayDeckListContent), "GetLastUnusedTraySection", false, new[] { 27 }),
        (typeof(DeckTrayDeckListContent), "GetLastUsedTraySection", false, new[] { 27 }),
        (typeof(DeckTrayDeckListContent), "CanShowNewDeckButton", false, new[] { 27 }),
        (typeof(DeckTrayDeckListContent), "DeleteDeckAnimation", true, new[] { 25, 26, 26 }),
        (typeof(CollectionDeckTray), "OnDeckCountUpdated", false, new[] { 27 }),
    };

    private static void InitializeDeckLimits(Harmony harmony)
    {
        var transpiler = new HarmonyMethod(typeof(NativeUIProbe), nameof(DeckLimitLiterals));
        foreach (var site in DeckLimitSites)
        {
            MethodInfo method = Required(site.Type, site.Method);
            if (site.Iterator)
                method = AccessTools.EnumeratorMoveNext(method) ?? throw new MissingMethodException(site.Type.FullName, site.Method + ".MoveNext");
            harmony.Patch(method, transpiler: transpiler);
        }
        harmony.Patch(Required(typeof(DeckTrayDeckListContent), "CanShowNewDeckButton"),
            postfix: new HarmonyMethod(typeof(NativeUIProbe), nameof(GateNewDeckButton)));
    }

    // Postfix after the shifted slot gate: New Deck also needs room under the personal cap (NativeUIProjection.ShowNewDeckButton).
    private static void GateNewDeckButton(ref bool __result)
    {
        if (!__result || !active) return;
        try { __result = NativeUIProjection.ShowNewDeckButton(true, Profile.CustomDeckCount, SessionLoanerKeys().Count()); }
        catch (Exception) { __result = false; }
    }

    private static IEnumerable<CodeInstruction> DeckLimitLiterals(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var code = instructions.ToList();
        var site = DeckLimitSites.Single(row => row.Iterator
            ? __originalMethod.DeclaringType?.DeclaringType == row.Type && __originalMethod.DeclaringType.Name.Contains("<" + row.Method + ">")
            : __originalMethod.DeclaringType == row.Type && __originalMethod.Name == row.Method);
        MethodInfo slots = Required(typeof(NativeUIProbe), nameof(DeckTraySlots));
        var found = new List<int>();
        for (int i = 0; i < code.Count; i++)
        {
            CodeInstruction instruction = code[i];
            if (!(instruction.opcode == OpCodes.Ldc_I4_S || instruction.opcode == OpCodes.Ldc_I4)) continue;
            int value = Convert.ToInt32(instruction.operand);
            if (!site.Literals.Contains(value)) continue;
            found.Add(value);
            // Keep the literal as the argument: ldc <retail>; call DeckTraySlots(int). Labels stay on the ldc.
            code.Insert(++i, new CodeInstruction(OpCodes.Call, slots));
        }
        if (!found.OrderBy(v => v).SequenceEqual(site.Literals.OrderBy(v => v)))
            throw new InvalidOperationException("Deck tray limit literals changed in " + site.Type.Name + "." + site.Method
                + ": [" + string.Join(",", found) + "]");
        return code;
    }

    // Called from transpiled native code: never throws.
    internal static int DeckTraySlots(int retail)
    {
        try { return active ? NativeUIProjection.TraySlots(retail) : retail; }
        catch (Exception) { return retail; }
    }
}
