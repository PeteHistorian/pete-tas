using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using UnityEngine;
using UnityEngine.SceneManagement;

// Online co-op: everyone plays in the same world.
// - Each player's own game moves their own Pete, so it feels instant.
// - The host's game runs everything else (enemies, orbs, health pickups, the finish) and tells everyone.
// - Orbs count for the whole team, and when anyone finishes with both, the run ends for everyone.
// - Someone who dies starts again where they first stood in the room they were in; everyone else keeps going.
public partial class NetGame : MonoBehaviour
{
    public static NetGame I;
    public const string Version = "pete-online-1";
    public const ushort DefaultPort = 7777;
    public const int MaxPlayers = 6;
    public const int TitleScene = 0, WorldScene = 1, EndScene = 2;

    public enum Mode { Offline, Host, Client }
    public Mode Current = Mode.Offline;
    public int LocalId = -1;
    public string LocalName = "Player";
    public string HostAddress = "";
    public ushort Port = DefaultPort;

    public class Player
    {
        public int Id;
        public string Name = "";
        public int Connection = -1;
        public int Ping;
        public int Scene;
        public PeteState State;
        public bool HasState;
        public NetPuppet Puppet;
    }

    public readonly SortedDictionary<int, Player> Players = new SortedDictionary<int, Player>();

    private NetTransport net;
    private int runNumber;
    private int hostScene;

    private enum Msg : byte
    {
        Hello = 1,      // joining player -> host: version, name
        Welcome,        // host -> joining player: your number, which screen, the team's progress
        Reject,         // host -> joining player: why not
        PlayerList,     // host -> everyone: who's playing
        LoadScene,      // host -> everyone: go to this screen (start, play again, back to the title)
        PeteState,      // anyone: where their Pete is (sent often, may get lost)
        WorldState,     // host -> everyone: the enemies and the timer (sent often, may get lost)
        Hit,            // joining player -> host: my sword hit enemy N
        Orb,            // joining player -> host: I touched orb N / host -> everyone: orb N is the team's now
        PickupGone,     // anyone: health pickup N was taken
        PickupNew,      // host -> everyone: an enemy dropped health pickup N here
        Finish,         // joining player -> host: I reached the finish / host -> everyone: the run is over, time T
        Ping,
        Pong,
        Ready,          // joining player -> host: I've loaded this screen
        Test            // the automatic test's own messages
    }

    // How each player's Pete looks in everyone else's game (you always see yourself as the normal blue Pete)
    public static readonly Color[] Tints =
    {
        new Color(1f, 0.85f, 0.3f), new Color(1f, 0.5f, 0.7f), new Color(0.5f, 1f, 0.45f),
        new Color(0.75f, 0.55f, 1f), new Color(1f, 0.55f, 0.3f), new Color(1f, 0.4f, 0.4f)
    };

    // ---------- Start up ----------

#if PETE_ONLINE
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
#endif
    private static void AutoStart()
    {
        if(I != null)
        {
            return;
        }
        GameObject go = new GameObject("[Online]");
        DontDestroyOnLoad(go);
        go.AddComponent<NetGame>();
    }

    // Lets the Editor (or a test) switch the online mode on
    public static void EnsureRunning()
    {
        AutoStart();
    }

    private void Awake()
    {
        I = this;
        LocalName = PlayerPrefs.GetString("online.name", Environment.UserName);
        HostAddress = PlayerPrefs.GetString("online.address", "");
        Port = (ushort)PlayerPrefs.GetInt("online.port", DefaultPort);
        if(LocalName.Length == 0) LocalName = "Player";
        SceneManager.sceneLoaded += OnSceneLoaded;
        Application.runInBackground = true;
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        Leave(false);
    }

    private void OnApplicationQuit()
    {
        Leave(false);
    }

    public bool IsHost { get { return Current == Mode.Host; } }
    public bool IsClient { get { return Current == Mode.Client; } }
    public bool IsOnline { get { return Current != Mode.Offline; } }

    // ---------- Hosting and joining ----------

    public void Host()
    {
        Leave(false);
        SaveSettings();
        net = new NetTransport();
        Hook();
        if(!net.StartHost(Port))
        {
            Say(net.Problem, 8f);
            net = null;
            return;
        }
        Current = Mode.Host;
        LocalId = 0;
        Players.Clear();
        Player me = new Player();
        me.Id = 0;
        me.Name = LocalName;
        me.Scene = SceneManager.GetActiveScene().buildIndex;
        Players[0] = me;
        hostScene = me.Scene;
        ResetTeam();
        SetHooks();
        Say("Hosting on port " + Port + ". Friends can join now.", 6f);
    }

    public void Join(string address)
    {
        Leave(false);
        address = address.Trim();
        ushort port = Port;
        int colon = address.LastIndexOf(':');
        if(colon > 0 && colon < address.Length - 1)
        {
            ushort p;
            if(ushort.TryParse(address.Substring(colon + 1), out p))
            {
                port = p;
                address = address.Substring(0, colon);
            }
        }
        if(address.Length == 0)
        {
            Say("Type the host's address first.");
            return;
        }
        HostAddress = address + (port != DefaultPort ? ":" + port : "");
        SaveSettings();
        net = new NetTransport();
        Hook();
        if(!net.StartClient(address, port))
        {
            Say(net.Problem, 8f);
            net = null;
            return;
        }
        Current = Mode.Client;
        LocalId = -1;
        Players.Clear();
        ResetTeam();
        SetHooks();
        Say("Connecting to " + address + ":" + port + "...", 10f);
    }

    public void Leave(bool backToTitle)
    {
        bool wasOnline = IsOnline;
        if(net != null)
        {
            net.Dispose();
            net = null;
        }
        foreach(Player p in Players.Values)
        {
            if(p.Puppet != null) Destroy(p.Puppet.gameObject);
        }
        Players.Clear();
        Current = Mode.Offline;
        LocalId = -1;
        NetHooks.Reset();
        inWorld = false;
        if(wasOnline && backToTitle && SceneManager.GetActiveScene().buildIndex != TitleScene)
        {
            SceneManager.LoadScene(TitleScene);
        }
    }

    private void SaveSettings()
    {
        PlayerPrefs.SetString("online.name", LocalName);
        PlayerPrefs.SetString("online.address", HostAddress);
        PlayerPrefs.SetInt("online.port", Port);
        PlayerPrefs.Save();
    }

    private void Hook()
    {
        net.Connected += OnConnected;
        net.Disconnected += OnDisconnected;
        net.Received += OnReceived;
    }

    private void SetHooks()
    {
        NetHooks.Online = true;
        NetHooks.IsClient = IsClient;
        NetHooks.PlayPressed = OnPlayPressed;
        NetHooks.EnemyHit = OnEnemyHit;
        NetHooks.Respawn = RespawnLocal;
        NetHooks.OrbTouched = OnOrbTouched;
        NetHooks.PickupTaken = OnPickupTaken;
        NetHooks.PickupSpawned = IsHost ? (Action<GameObject, HealthDrop>)OnPickupSpawned : null;
        NetHooks.Finish = OnFinishTouched;
        NetHooks.RoomEntered = OnRoomEntered;
        NetHooks.NearestPlayer = IsHost ? (Func<Vector3, Vector3?>)NearestPlayer : null;
    }

    // The host starts the run (or plays again) for everyone
    public void StartRun()
    {
        if(!IsHost)
        {
            return;
        }
        runNumber++;
        ResetTeam();
        Broadcast(W(Msg.LoadScene).Byte(WorldScene).Int(runNumber), true);
        LoadSceneHere(WorldScene);
    }

    public void BackToTitle()
    {
        if(!IsHost)
        {
            return;
        }
        Broadcast(W(Msg.LoadScene).Byte(TitleScene).Int(runNumber), true);
        LoadSceneHere(TitleScene);
    }

    private bool OnPlayPressed()
    {
        if(IsHost)
        {
            StartRun();
        }
        else if(IsClient)
        {
            Say("The host starts the run for everyone.");
        }
        return IsOnline;
    }

    private void LoadSceneHere(int scene)
    {
        hostScene = scene;
        if(Players.ContainsKey(LocalId)) Players[LocalId].Scene = scene;
        SceneManager.LoadScene(scene);
    }

    // ---------- Connections ----------

    private void OnConnected(int connection)
    {
        if(IsClient)
        {
            Send(0, W(Msg.Hello).Str(Version).Str(LocalName), true);
            Say("Connected. Saying hello to the host...");
        }
    }

    private void OnDisconnected(int connection)
    {
        if(IsClient)
        {
            string why = net != null && net.Problem.Length > 0 ? net.Problem : "Lost the connection to the host.";
            Leave(true);
            Say(why, 10f);
            return;
        }
        Player gone = null;
        foreach(Player p in Players.Values)
        {
            if(p.Connection == connection) gone = p;
        }
        if(gone != null)
        {
            if(gone.Puppet != null) Destroy(gone.Puppet.gameObject);
            Players.Remove(gone.Id);
            Say(gone.Name + " left.");
            SendPlayerList();
        }
    }

    private void OnReceived(int connection, byte[] data)
    {
        try
        {
            Handle(connection, new NetReader(data));
        }
        catch(Exception ex)
        {
            Debug.LogWarning("[Online] Bad message: " + ex.Message);
        }
    }

    private void Handle(int connection, NetReader r)
    {
        Msg type = (Msg)r.Byte();
        if(IsHost)
        {
            HandleOnHost(connection, type, r);
        }
        else
        {
            HandleOnClient(type, r);
        }
    }

    private Player FromConnection(int connection)
    {
        foreach(Player p in Players.Values)
        {
            if(p.Connection == connection) return p;
        }
        return null;
    }

    private void HandleOnHost(int connection, Msg type, NetReader r)
    {
        Player from = FromConnection(connection);
        switch(type)
        {
            case Msg.Hello:
            {
                string version = r.Str();
                string name = Clean(r.Str());
                if(version != Version)
                {
                    SendTo(connection, W(Msg.Reject).Str("The host has a different version of Pete Online (" + Version + "). Get the same version as the host."));
                    return;
                }
                if(Players.Count >= MaxPlayers)
                {
                    SendTo(connection, W(Msg.Reject).Str("The game is full (" + MaxPlayers + " players)."));
                    return;
                }
                Player p = new Player();
                p.Id = connection;
                p.Connection = connection;
                p.Name = UniqueName(name);
                p.Scene = TitleScene;
                Players[p.Id] = p;
                NetWriter w = W(Msg.Welcome).Int(p.Id).Str(p.Name).Byte(hostScene).Int(runNumber);
                WriteTeam(w);
                SendTo(connection, w);
                SendPlayerList();
                Say(p.Name + " joined.");
                if(inWorld) MakePuppet(p);
                break;
            }
            case Msg.PeteState:
            {
                if(from == null) return;
                PeteState s = ReadState(r);
                ReceiveState(from, s);
                // Pass it on to everyone else
                NetWriter w = W(Msg.PeteState).Int(from.Id);
                WriteState(w, s);
                Broadcast(w, false, connection);
                break;
            }
            case Msg.Pong:
            {
                if(from == null) return;
                float sent = r.Float();
                from.Ping = Mathf.RoundToInt((Time.unscaledTime - sent) * 1000f);
                break;
            }
            case Msg.Ready:
            {
                if(from == null) return;
                from.Scene = r.Byte();
                if(from.Scene == WorldScene && inWorld) MakePuppet(from);
                SendPlayerList();
                break;
            }
            case Msg.Hit:
                if(from != null) HostApplyHit(r.Byte(), r.Byte(), new Vector2(r.Float(), r.Float()), r.Bool());
                break;
            case Msg.Orb:
                if(from != null) HostCollectOrb(r.Byte());
                break;
            case Msg.PickupGone:
            {
                int id = r.Int();
                HidePickup(id);
                Broadcast(W(Msg.PickupGone).Int(id), true, connection);
                break;
            }
            case Msg.Finish:
                if(from != null && teamPowers >= 2) HostFinish();
                break;
            case Msg.Test:
                if(NetTest.I != null) NetTest.I.OnHostMessage(from, r);
                break;
        }
    }

    private void HandleOnClient(Msg type, NetReader r)
    {
        switch(type)
        {
            case Msg.Reject:
            {
                string why = r.Str();
                Leave(true);
                Say(why, 10f);
                break;
            }
            case Msg.Welcome:
            {
                LocalId = r.Int();
                LocalName = r.Str();
                int scene = r.Byte();
                runNumber = r.Int();
                ReadTeam(r);
                Say("Joined the game as " + LocalName + ".", 5f);
                hostScene = scene;
                if(SceneManager.GetActiveScene().buildIndex != scene)
                {
                    SceneManager.LoadScene(scene);
                }
                else
                {
                    SceneReady(scene);
                }
                break;
            }
            case Msg.PlayerList:
            {
                int n = r.Byte();
                HashSet<int> seen = new HashSet<int>();
                for(int i = 0; i < n; i++)
                {
                    int id = r.Int();
                    string name = r.Str();
                    int ping = r.Short();
                    int scene = r.Byte();
                    seen.Add(id);
                    Player p;
                    if(!Players.TryGetValue(id, out p))
                    {
                        p = new Player();
                        p.Id = id;
                        Players[id] = p;
                    }
                    p.Name = name;
                    p.Ping = ping;
                    p.Scene = scene;
                    if(inWorld && id != LocalId && scene == WorldScene) MakePuppet(p);
                }
                foreach(int id in new List<int>(Players.Keys))
                {
                    if(!seen.Contains(id))
                    {
                        if(Players[id].Puppet != null) Destroy(Players[id].Puppet.gameObject);
                        Players.Remove(id);
                    }
                }
                break;
            }
            case Msg.LoadScene:
            {
                int scene = r.Byte();
                int run = r.Int();
                if(scene == WorldScene && run != runNumber)
                {
                    ResetTeam();
                }
                runNumber = run;
                hostScene = scene;
                SceneManager.LoadScene(scene);
                break;
            }
            case Msg.PeteState:
            {
                int id = r.Int();
                PeteState s = ReadState(r);
                Player p;
                if(Players.TryGetValue(id, out p) && id != LocalId) ReceiveState(p, s);
                break;
            }
            case Msg.WorldState:
                ClientApplyWorld(r);
                break;
            case Msg.Ping:
                Send(0, W(Msg.Pong).Float(r.Float()), false);
                break;
            case Msg.Orb:
            {
                int orb = r.Byte();
                int powers = r.Byte();
                ApplyOrb(orb, powers, true);
                break;
            }
            case Msg.PickupGone:
                HidePickup(r.Int());
                break;
            case Msg.PickupNew:
            {
                int id = r.Int();
                int enemy = r.Byte();
                Vector2 at = new Vector2(r.Float(), r.Float());
                SpawnedPickup sp;
                sp.Enemy = enemy;
                sp.At = at;
                spawnedPickups[id] = sp;
                if(inWorld) MakeSpawnedPickup(id, sp);
                break;
            }
            case Msg.Finish:
                ApplyFinish(r.Float());
                break;
            case Msg.Test:
                if(NetTest.I != null) NetTest.I.OnClientMessage(r);
                break;
        }
    }

    private static string Clean(string name)
    {
        name = (name ?? "").Trim();
        if(name.Length > 16) name = name.Substring(0, 16);
        return name.Length == 0 ? "Player" : name;
    }

    private string UniqueName(string name)
    {
        string n = name;
        for(int i = 2; ; i++)
        {
            bool taken = false;
            foreach(Player p in Players.Values)
            {
                if(string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)) taken = true;
            }
            if(!taken) return n;
            n = name + " " + i;
        }
    }

    private void SendPlayerList()
    {
        if(!IsHost)
        {
            return;
        }
        NetWriter w = W(Msg.PlayerList).Byte(Players.Count);
        foreach(Player p in Players.Values)
        {
            w.Int(p.Id).Str(p.Name).Short(p.Id == LocalId ? 0 : p.Ping).Byte(p.Scene);
        }
        Broadcast(w, true);
    }

    // ---------- Every frame ----------

    private float nextState, nextWorld, nextPing, nextList;

    private void Update()
    {
        if(net == null)
        {
            return;
        }
        net.Update();
        if(net == null)
        {
            return;
        }
        float now = Time.unscaledTime;
        if(inWorld && LocalId >= 0 && now >= nextState)
        {
            nextState = now + 1f / 30f;
            SendLocalState();
        }
        if(IsHost)
        {
            if(inWorld && now >= nextWorld)
            {
                nextWorld = now + 1f / 20f;
                SendWorld();
            }
            if(now >= nextPing)
            {
                nextPing = now + 1f;
                Broadcast(W(Msg.Ping).Float(now), false);
            }
            if(now >= nextList)
            {
                nextList = now + 2f;
                SendPlayerList();
            }
        }
        if(inWorld)
        {
            WorldUpdate();
        }
    }

    // ---------- Sending ----------

    private static NetWriter W(Msg type)
    {
        return new NetWriter().Byte((byte)type);
    }

    private void Send(int connection, NetWriter w, bool reliable)
    {
        if(net != null) net.Send(connection, w.Bytes(), reliable);
    }

    private void SendTo(int connection, NetWriter w)
    {
        Send(connection, w, true);
    }

    // Host: to every player; a joining player: to the host
    private void Broadcast(NetWriter w, bool reliable, int except = -1)
    {
        if(net == null)
        {
            return;
        }
        if(IsHost)
        {
            net.SendAll(w.Bytes(), reliable, except);
        }
        else
        {
            net.Send(0, w.Bytes(), reliable);
        }
    }

    public void SendTest(NetWriter body)
    {
        NetWriter w = W(Msg.Test);
        w.Raw(body.Bytes());
        Broadcast(w, true);
    }

    private static void WriteState(NetWriter w, PeteState s)
    {
        w.Float(s.Time).Float(s.Pos.x).Float(s.Pos.y).Float(s.Vel.x).Float(s.Vel.y).SByte(s.Facing).Byte(s.Anim).Byte(s.Flags).Byte(s.Hp).Byte(s.Teleports);
    }

    private static PeteState ReadState(NetReader r)
    {
        PeteState s;
        s.Time = r.Float();
        s.Pos = new Vector2(r.Float(), r.Float());
        s.Vel = new Vector2(r.Float(), r.Float());
        s.Facing = r.SByte();
        s.Anim = r.Byte();
        s.Flags = r.Byte();
        s.Hp = r.Byte();
        s.Teleports = r.Byte();
        return s;
    }

    // ---------- Messages on screen ----------

    public string Message = "";
    public float MessageUntil;

    public void Say(string text, float seconds = 4f)
    {
        Message = text;
        MessageUntil = Time.unscaledTime + seconds;
        Debug.Log("[Online] " + text);
    }
}

// Writes a message, piece by piece
public class NetWriter
{
    private readonly MemoryStream ms = new MemoryStream(64);
    private readonly BinaryWriter w;

    public NetWriter() { w = new BinaryWriter(ms); }
    public NetWriter Byte(int v) { w.Write((byte)v); return this; }
    public NetWriter SByte(int v) { w.Write((sbyte)v); return this; }
    public NetWriter Bool(bool v) { w.Write(v); return this; }
    public NetWriter Short(int v) { w.Write((short)Mathf.Clamp(v, short.MinValue, short.MaxValue)); return this; }
    public NetWriter Int(int v) { w.Write(v); return this; }
    public NetWriter Float(float v) { w.Write(v); return this; }
    public NetWriter Str(string v) { w.Write(v ?? ""); return this; }
    public NetWriter Raw(byte[] v) { w.Write(v); return this; }
    public byte[] Bytes() { w.Flush(); return ms.ToArray(); }
}

// Reads a message back, piece by piece
public class NetReader
{
    private readonly BinaryReader r;

    public NetReader(byte[] data) { r = new BinaryReader(new MemoryStream(data)); }
    public byte Byte() { return r.ReadByte(); }
    public sbyte SByte() { return r.ReadSByte(); }
    public bool Bool() { return r.ReadBoolean(); }
    public short Short() { return r.ReadInt16(); }
    public int Int() { return r.ReadInt32(); }
    public float Float() { return r.ReadSingle(); }
    public string Str() { return r.ReadString(); }
}
