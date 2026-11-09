internal enum NativeHomeControl
{
    Unsupported,
    Traditional,
    Collection,
    OpenPacks,
    Store
}

internal enum NativeMenuTarget
{
    None,
    Picker,
    Collection,
    Pack,
    // The local shop already opened the store (NativeShop fires the STORE press itself): nothing to route or block.
    LocalShop
}

internal static class NativeMenuFlow
{
    internal static NativeMenuTarget HomeRoute(NativeHomeControl control)
    {
        if (control == NativeHomeControl.Traditional) return NativeMenuTarget.Picker;
        if (control == NativeHomeControl.Collection) return NativeMenuTarget.Collection;
        if (control == NativeHomeControl.OpenPacks) return NativeMenuTarget.Pack;
        if (control == NativeHomeControl.Store) return NativeMenuTarget.LocalShop;
        return NativeMenuTarget.None;
    }

    internal static bool CollectionExitAllowed(bool loggedIn, bool localActive, bool pickerOrigin) =>
        loggedIn || (localActive && pickerOrigin);

    internal static bool FatalPackDisconnectReturnsHome(bool replayActive) => !replayActive;
}
