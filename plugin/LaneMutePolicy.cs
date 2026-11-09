using System;

// Pure policy for muting test client copies. No Unity/native types, so TestControl checks compile it directly.
// Lane 0 is the owner's own game and keeps its sound exactly as configured; lanes 1-3 are test copies that all run
// on the one Windows desktop, so they are launched silent (owner 2026-10-25). The lane comes from the
// "-hs-native-ui-lane N" launch argument that Run-Client.ps1 passes only for lanes 1-3.
internal static class LaneMutePolicy
{
    internal static bool ShouldMute(int lane) { return lane >= 1 && lane <= 3; }

    // Native Option names (enum member names) forced off. Master and music enables, plus every volume slider.
    internal static bool ForcesBoolOff(string optionName)
    {
        return optionName == "SOUND" || optionName == "MUSIC";
    }

    internal static bool ForcesFloatZero(string optionName)
    {
        switch (optionName)
        {
            case "SOUND_VOLUME":
            case "MUSIC_VOLUME":
            case "AMBIENCE_VOLUME":
            case "DIALOG_VOLUME":
            case "SOUND_EFFECT_VOLUME":
                return true;
            default:
                return false;
        }
    }
}
