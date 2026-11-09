using BepInEx.Configuration;

// Global, like Turn Timer. Matches read it once when they begin; a failed save restores the previous value.
internal sealed class DeckInferenceSettings
{
    private readonly ConfigFile file;
    private readonly ConfigEntry<bool> enabled;
    internal bool Enabled => enabled.Value;

    internal DeckInferenceSettings(ConfigFile file)
    {
        this.file = file;
        enabled = file.Bind("Gameplay", "NpcDeckInference", false,
            "Experimental: NPCs guess your deck from the cards you have played, instead of assuming a typical deck of your class. Public information only. Applies from the next match. Off by default. Also in Options.");
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
