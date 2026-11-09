using BepInEx.Configuration;

// Global, like Handheld UI. Matches capture the value at creation; changing Options cannot reset a running clock.
internal sealed class TurnTimerSettings
{
    private readonly ConfigFile file;
    private readonly ConfigEntry<bool> enabled;
    internal bool Enabled => enabled.Value;

    internal TurnTimerSettings(ConfigFile file)
    {
        this.file = file;
        enabled = file.Bind("Gameplay", "TurnTimer", false,
            "Limit player turns using the native burning rope. Applies from the next match. Off by default.");
    }

    internal void Set(bool value)
    {
        bool previous = Enabled, automatic = file.SaveOnConfigSet;
        try
        {
            file.SaveOnConfigSet = false;
            enabled.Value = value;
            try { file.Save(); }
            catch { enabled.Value = previous; throw; }
        }
        finally { file.SaveOnConfigSet = automatic; }
    }
}
