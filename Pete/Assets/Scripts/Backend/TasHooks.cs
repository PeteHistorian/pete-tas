using UnityEngine;

// Small switches the TAS tool uses to talk to the game.
// In normal play they are all off, so the game behaves exactly like before.
public static class TasHooks
{
    // True while the TAS tool is running the game.
    public static bool Active;

    // True while the TAS tool rebuilds physics contacts after a rewind.
    // Collision callbacks ignore that moment so nothing happens twice.
    public static bool MuteCallbacks;

    // Set instead of reloading the level when Pete dies in TAS mode.
    public static bool PlayerDied;

    // Set instead of loading the end screen when the run is finished in TAS mode.
    public static bool Finished;

    // Lets the finish trigger load the end screen anyway (used by video playback).
    public static bool AllowFinishSceneLoad;

    // Game time used by the game's timers. In normal play this is exactly Time.time.
    // The TAS tool shifts it when it rewinds, because Unity's own clock can't go backwards.
    public static double TimeOffset;

    // Told about every sound effect the game plays (used by the TAS video export).
    public static System.Action<AudioSource> SoundPlayed;

    public static double GameTime
    {
        get { return Time.timeAsDouble - TimeOffset; }
    }

    public static void Reset()
    {
        Active = false;
        MuteCallbacks = false;
        PlayerDied = false;
        Finished = false;
        AllowFinishSceneLoad = false;
        TimeOffset = 0;
        SoundPlayed = null;
    }
}
