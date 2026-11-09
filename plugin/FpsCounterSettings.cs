using BepInEx.Configuration;

// Global, like Turn Timer and Handheld UI. Applies the moment it is set; a failed save restores the previous value.
internal sealed class FpsCounterSettings
{
    private readonly ConfigFile file;
    private readonly ConfigEntry<bool> enabled;
    internal bool Enabled => enabled.Value;

    internal FpsCounterSettings(ConfigFile file)
    {
        this.file = file;
        enabled = file.Bind("Display", "FpsCounter", false,
            "Show the frame rate in a screen corner, averaged over half a second. Applies at once. Off by default. Also in Options.");
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
