using UnityEngine;

// Small switches the online co-op mode uses to talk to the game.
// In normal play (and in the TAS) they are all off, so the game behaves exactly like before.
public static class NetHooks
{
    // Playing online with other people.
    public static bool Online;

    // Someone else's game runs the world (enemies, orbs, the finish); this game only runs its own Pete.
    public static bool IsClient;

    // While another player's sword hit is being applied here: the down-swing bounce is theirs, not ours.
    public static bool SuppressPogo;

    // Each of these, when set, is told about something that happened to our own Pete or the world.
    // The ones returning bool return true when the online code took care of it (so the game skips its own handling).
    public static System.Func<EnemyHealth, int, Transform, bool, bool> EnemyHit;
    public static System.Action Respawn;
    public static System.Action<PowerUp> OrbTouched;
    public static System.Action<HealthPickup> PickupTaken;
    public static System.Action<GameObject, HealthDrop> PickupSpawned;
    public static System.Func<bool> Finish;
    public static System.Func<bool> PlayPressed;
    public static System.Action<roomTransition> RoomEntered;

    // Where the nearest player is (enemies chase whoever is closest), or null to chase our own Pete.
    public static System.Func<Vector3, Vector3?> NearestPlayer;

    public static void Reset()
    {
        Online = false;
        IsClient = false;
        SuppressPogo = false;
        EnemyHit = null;
        Respawn = null;
        OrbTouched = null;
        PickupTaken = null;
        PickupSpawned = null;
        Finish = null;
        PlayPressed = null;
        RoomEntered = null;
        NearestPlayer = null;
    }
}
