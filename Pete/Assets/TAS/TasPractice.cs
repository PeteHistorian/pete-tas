using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Buttons = PeteInput.Buttons;

// Practice mode: play one room of the level for real, as often as you like.
//  - Each practice level is one visit to a room in one of your runs (some rooms are visited twice: on the way
//    out to the orbs and on the way back). It starts exactly where that run enters the room: the run is replayed up
//    to there, so Pete, his speed, HP, the orbs, the enemies and the timer are all as they were.
//  - The level waits until you press a button, then the game runs at real speed with your controls.
//  - Reaching the room your run goes to next is a clear (the last level ends at the finish). Dying is a miss.
//  - R starts the level again straight away. Tries, clears and your best time are kept for each level, and your
//    best clear races you as a see-through Pete.
// While you practice, the TAS panel, its keys and its drawings step aside, and the TAS keeps no tries, undo steps
// or autosave of practice attempts. Your own movie comes back when you leave the Practice tab.
public class TasPractice : MonoBehaviour
{
    public static TasPractice I;

    public enum State { Off, Loading, Ready, Playing, Result }
    public State Now { get; private set; }

    // True while a practice level is on screen (the TAS panel and its keys step aside)
    public static bool InLevel
    {
        get { return I != null && I.Now != State.Off; }
    }

    // ---------- Settings (remembered between runs) ----------
    public static bool RaceGhost = true;
    public static bool ShowInputs = true;
    private const string GhostKey = "TAS.Practice.Ghost";
    private const string InputsKey = "TAS.Practice.Inputs";

    // ---------- Rooms and levels ----------
    public class Room
    {
        public string Name;
        public Collider2D Area;
    }

    public class Level
    {
        public int Number;
        public string Key;          // "Room 2>Room 3#1": the room, where it goes next, and which visit
        public string Name;         // "Room 2" or "Room 2 (back)"
        public string RoomName;
        public string NextName;     // null = the run finishes in this room
        public int StartFrame;
        public int RunFrames;       // how long your run takes through it
        public string RunName;      // the run it comes from
        public TasMovie Run;
        public Stats S;
    }

    public class Stats
    {
        public int Tries, Clears;
        public int Best = -1;       // frames
        public List<GhostFrame> Ghost;
    }

    public struct GhostFrame
    {
        public Vector2 Pos;
        public float ScaleX;
        public string Sprite;
    }

    public readonly List<Level> Levels = new List<Level>();
    public string RunName = "";
    public string Status = "";
    public Level Current { get; private set; }

    // Looking through runs for levels ("All my runs" goes through every saved run)
    public const string AllRuns = "All my runs";
    private readonly Queue<string> toScan = new Queue<string>();
    private readonly List<Level> found = new List<Level>();
    private int scanTotal;
    public bool Scanning { get; private set; }
    private List<Room> rooms;
    private readonly Dictionary<string, Stats> stats = new Dictionary<string, Stats>();
    private bool statsLoaded;

    // Your own movie, kept while you practice
    private TasMovie savedMovie;
    private string savedName;
    private int savedFrame;
    private bool savedDirty;
    public bool HoldsYourMovie { get { return savedMovie != null; } }

    // The try being played
    private readonly List<GhostFrame> attempt = new List<GhostFrame>();
    private int lastFrame;
    private Room inRoom;
    private float resultUntil;
    private string resultText = "";
    private bool resultGood;
    private int lastTime = -1;
    private SpriteRenderer ghost;
    private Dictionary<string, Sprite> sprites;

    private TasManager M { get { return TasManager.I; } }

    public static string Folder
    {
        get { return Path.Combine(TasManager.TasFolder, "Practice"); }
    }

    private void Awake()
    {
        I = this;
        RaceGhost = PlayerPrefs.GetInt(GhostKey, 1) == 1;
        ShowInputs = PlayerPrefs.GetInt(InputsKey, 1) == 1;
    }

    public static void SaveSettings()
    {
        PlayerPrefs.SetInt(GhostKey, RaceGhost ? 1 : 0);
        PlayerPrefs.SetInt(InputsKey, ShowInputs ? 1 : 0);
        PlayerPrefs.Save();
    }

    // =====================================================================
    //  Finding the levels in a run
    // =====================================================================

    // The game's rooms: every roomTransition trigger (each one switches the camera to that room)
    private List<Room> Rooms()
    {
        if(rooms != null && rooms.Count > 0 && rooms[0].Area != null)
        {
            return rooms;
        }
        rooms = new List<Room>();
        foreach(roomTransition t in FindObjectsOfType<roomTransition>(true))
        {
            Collider2D c = t.GetComponent<Collider2D>();
            if(c == null)
            {
                continue;
            }
            Room r = new Room();
            r.Area = c;
            // "Room 1 (1)" is the room above Room 1
            r.Name = t.gameObject.name == "Room 1 (1)" ? "Room 1 top" : t.gameObject.name;
            rooms.Add(r);
        }
        return rooms;
    }

    // The room Pete is in. Rooms touch (and have small gaps), so he stays in the one he was in until he's
    // clearly inside another.
    private Room RoomAt(Vector2 p, Room was)
    {
        if(was != null && was.Area != null && was.Area.OverlapPoint(p))
        {
            return was;
        }
        foreach(Room r in Rooms())
        {
            if(r.Area != null && r.Area.OverlapPoint(p))
            {
                return r;
            }
        }
        return was;
    }

    // The runs you can practice from: saved movies, the most likely full runs first
    public static List<string> RunChoices()
    {
        List<string> names = new List<string>();
        try
        {
            foreach(string f in Directory.GetFiles(TasManager.TasFolder, "*.txt"))
            {
                string n = Path.GetFileNameWithoutExtension(f);
                if(n != "autosave" && !n.StartsWith("selftest", StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(n);
                }
            }
        }
        catch(Exception)
        {
        }
        Func<string, int> rank = n =>
        {
            string l = n.ToLowerInvariant();
            if(l.Contains("glitchless")) return 0;
            if(l.Contains("world record") || l.Contains("any%")) return 1;
            return 2;
        };
        names.Sort((a, b) => rank(a) != rank(b) ? rank(a).CompareTo(rank(b)) : string.Compare(a, b, StringComparison.OrdinalIgnoreCase));
        return names;
    }

    // Plays each run (quickly, out of sight) and splits it into one level per room visit. With "All my runs", every
    // saved run is looked through and each room crossing is kept once (from the first run that has it).
    public void FindLevels(string choice)
    {
        if(M == null || M.Busy || Now != State.Off || Scanning)
        {
            return;
        }
        List<string> names = choice == AllRuns ? RunChoices() : new List<string> { choice };
        KeepYourMovie();
        RunName = choice;
        Levels.Clear();
        found.Clear();
        toScan.Clear();
        foreach(string n in names)
        {
            toScan.Enqueue(n);
        }
        scanTotal = names.Count;
        Scanning = true;
        ScanNext();
    }

    private void ScanNext()
    {
        while(toScan.Count > 0)
        {
            string name = toScan.Dequeue();
            TasMovie movie;
            try
            {
                movie = TasMovie.Load(M.MoviePath(name));
            }
            catch(Exception)
            {
                continue;
            }
            if(movie.IsPractice || movie.Length < 10)
            {
                // A run that puts Pete somewhere isn't a real run
                continue;
            }
            Status = "Looking for rooms in \"" + name + "\" (" + (scanTotal - toScan.Count) + " of " + scanTotal + ")…";
            string n = name;
            TasMovie m = movie;
            M.LoadQuiet(movie.Clone(), "practice", movie.Length, () =>
            {
                Collect(n, m);
                ScanNext();
            });
            return;
        }
        Scanning = false;
        Levels.Clear();
        foreach(Level L in found)
        {
            L.Number = Levels.Count + 1;
            Levels.Add(L);
        }
        List<string> missing = new List<string>();
        foreach(Room r in Rooms())
        {
            if(!Levels.Exists(L => L.RoomName == r.Name)) missing.Add(r.Name);
        }
        missing.Sort(string.CompareOrdinal);
        Status = Levels.Count == 0
            ? "No room crossings found. Save a run that goes from one room to the next."
            : Levels.Count + " levels. Press Play on one." + (missing.Count > 0 ? " No run goes through " + string.Join(", ", missing.ToArray()) + " yet: save a run that does, then press Find again." : "");
        SaveLevels();
    }

    // The levels in the run that has just been played out
    private void Collect(string runName, TasMovie run)
    {
        IList<Vector2> pos = M.RoutePositions;
        // Room visits: [room, first frame]
        List<KeyValuePair<Room, int>> visits = new List<KeyValuePair<Room, int>>();
        Room cur = null;
        for(int f = 0; f < pos.Count; f++)
        {
            Room r = RoomAt(pos[f], cur);
            if(r != null && r != cur)
            {
                visits.Add(new KeyValuePair<Room, int>(r, f));
                cur = r;
            }
        }
        // A quick pass through a room's corner isn't a level of its own
        const int MinFrames = 25;
        for(int i = visits.Count - 2; i >= 1; i--)
        {
            if(visits[i + 1].Value - visits[i].Value < MinFrames)
            {
                visits.RemoveAt(i);
            }
        }
        for(int i = visits.Count - 1; i >= 1; i--)
        {
            if(visits[i].Key == visits[i - 1].Key)
            {
                visits.RemoveAt(i);
            }
        }
        bool finishes = M.FinishFrame >= 0;
        int end = finishes ? M.FinishFrame : pos.Count - 1;
        Dictionary<Room, int> seen = new Dictionary<Room, int>();
        for(int i = 0; i < visits.Count; i++)
        {
            Room r = visits[i].Key;
            bool last = i == visits.Count - 1;
            if(last && !finishes)
            {
                // The run stops in this room without finishing, so there's nothing to reach
                break;
            }
            int n;
            seen.TryGetValue(r, out n);
            seen[r] = ++n;
            Level L = new Level();
            L.RoomName = r.Name;
            L.NextName = last ? null : visits[i + 1].Key.Name;
            L.Name = r.Name + (n == 2 ? " (back)" : n > 2 ? " (visit " + n + ")" : "");
            L.Key = r.Name + ">" + (L.NextName ?? "finish") + "#" + n;
            if(found.Exists(x => x.Key == L.Key))
            {
                // An earlier run already has this room crossing
                continue;
            }
            L.RunName = runName;
            L.Run = run;
            L.StartFrame = visits[i].Value;
            L.RunFrames = (last ? end : visits[i + 1].Value) - L.StartFrame;
            if(!last)
            {
                // Your run's time is measured the same way a clear is: the first frame Pete is in the next room
                Room at = RoomAt(pos[L.StartFrame], null);
                for(int f = L.StartFrame + 1; f < pos.Count; f++)
                {
                    at = RoomAt(pos[f], at);
                    if(at != null && at.Name == L.NextName)
                    {
                        L.RunFrames = f - L.StartFrame;
                        break;
                    }
                }
            }
            L.S = StatsFor(L.Key);
            found.Add(L);
        }
    }

    // =====================================================================
    //  Playing a level
    // =====================================================================

    public void Play(Level L)
    {
        if(M == null || M.Busy || L.Run == null || Scanning)
        {
            return;
        }
        KeepYourMovie();
        Current = L;
        Now = State.Loading;
        inRoom = null;
        TasMovie movie = L.Run.Clone();
        movie.TruncateAfter(L.StartFrame);
        M.LoadQuiet(movie, "practice", L.StartFrame, GetReady);
    }

    // R: the same level again from its start
    public void Retry()
    {
        if(Current == null || M.Busy)
        {
            return;
        }
        M.Pause();
        M.Recording = false;
        Now = State.Loading;
        M.Seek(Current.StartFrame, GetReady);
    }

    // Esc: back to the list of levels (your movie comes back when you leave the Practice tab)
    public void Stop()
    {
        if(M != null)
        {
            M.Pause();
            M.Recording = false;
        }
        Now = State.Off;
        Current = null;
        HideGhost();
        if(TasHud.I != null)
        {
            TasHud.I.Hidden = false;
        }
    }

    private void GetReady()
    {
        if(Current == null)
        {
            return;
        }
        // Nothing of the last try is kept after the level's start
        M.ClearAfter(Current.StartFrame);
        M.Recording = false;
        attempt.Clear();
        inRoom = M.Player != null && M.Player.Valid ? RoomAt(M.Player.Position, null) : null;
        Now = State.Ready;
    }

    private void Update()
    {
        if(Now == State.Off || M == null)
        {
            return;
        }
        if(Input.GetKeyDown(KeyCode.Escape))
        {
            Stop();
            return;
        }
        if(Input.GetKeyDown(KeyCode.R) && Now != State.Loading)
        {
            Retry();
            return;
        }
        switch(Now)
        {
            case State.Ready:
                // Waits for you: the level starts on the first button you press
                if(M.IsIdle && TasManager.ReadPlayerInput() != Buttons.None)
                {
                    Current.S.Tries++;
                    SaveStats();
                    lastFrame = M.Frame;
                    Remember();
                    M.Recording = true;
                    M.TogglePlay();
                    Now = State.Playing;
                }
                break;

            case State.Playing:
                Follow();
                break;

            case State.Result:
                if(Time.unscaledTime >= resultUntil)
                {
                    Retry();
                }
                break;
        }
    }

    private void Follow()
    {
        if(M.Player == null || !M.Player.Valid)
        {
            return;
        }
        if(M.Frame != lastFrame)
        {
            lastFrame = M.Frame;
            Remember();
            inRoom = RoomAt(M.Player.Position, inRoom);
        }
        int frames = M.Frame - Current.StartFrame;
        bool cleared = Current.NextName == null ? TasHooks.Finished : inRoom != null && inRoom.Name == Current.NextName;
        if(cleared)
        {
            Cleared(frames);
        }
        else if(TasHooks.PlayerDied)
        {
            Missed("Pete died. Again!");
        }
        else if(M.CurrentMode == TasManager.Mode.Paused && !M.Busy)
        {
            // Something stopped the game (like the TAS pausing on its own)
            Missed("Stopped. Again!");
        }
    }

    private void Remember()
    {
        TasPlayerInfo p = M.Player;
        GhostFrame g = new GhostFrame();
        g.Pos = p.Go.transform.position;
        g.ScaleX = p.Go.transform.localScale.x;
        g.Sprite = p.Sprite != null && p.Sprite.sprite != null ? p.Sprite.sprite.name : "";
        int k = M.Frame - Current.StartFrame;
        while(attempt.Count < k)
        {
            attempt.Add(attempt.Count > 0 ? attempt[attempt.Count - 1] : g);
        }
        if(k >= 0 && k == attempt.Count)
        {
            attempt.Add(g);
        }
    }

    private void Cleared(int frames)
    {
        M.Pause();
        M.Recording = false;
        Stats s = Current.S;
        s.Clears++;
        int old = s.Best;
        lastTime = frames;
        if(old < 0 || frames < old)
        {
            s.Best = frames;
            s.Ghost = new List<GhostFrame>(attempt);
            SaveGhost(Current.Key, s.Ghost);
            resultText = "CLEAR!  " + Secs(frames) + (old < 0 ? "  First clear!" : "  New best! (" + Diff(frames - old) + ")");
        }
        else
        {
            resultText = "CLEAR!  " + Secs(frames) + "  (best " + Secs(old) + ", " + Diff(frames - old) + ")";
        }
        resultGood = true;
        SaveStats();
        Now = State.Result;
        resultUntil = Time.unscaledTime + 1.6f;
    }

    private void Missed(string why)
    {
        M.Pause();
        M.Recording = false;
        resultText = why;
        resultGood = false;
        Now = State.Result;
        resultUntil = Time.unscaledTime + 0.9f;
    }

    // ---------- Your movie ----------

    private void KeepYourMovie()
    {
        if(savedMovie != null)
        {
            return;
        }
        savedMovie = M.Movie.Clone();
        savedName = M.MovieName;
        savedFrame = M.Frame;
        savedDirty = M.MovieDirty;
        M.Quiet = true;
    }

    // Puts your own movie back (when you leave the Practice tab)
    public void GiveBackYourMovie()
    {
        if(savedMovie == null || M == null || M.Busy)
        {
            return;
        }
        Stop();
        TasMovie movie = savedMovie;
        string name = savedName;
        int frame = savedFrame;
        bool dirty = savedDirty;
        savedMovie = null;
        M.LoadQuiet(movie, name, frame, () =>
        {
            M.Quiet = false;
            M.MovieDirty = dirty;
        });
    }

    // =====================================================================
    //  Saved stats and ghosts (TAS Files\Practice)
    // =====================================================================

    private Stats StatsFor(string key)
    {
        LoadStats();
        Stats s;
        if(!stats.TryGetValue(key, out s))
        {
            s = new Stats();
            stats[key] = s;
        }
        if(s.Ghost == null && s.Best >= 0)
        {
            s.Ghost = LoadGhost(key);
        }
        return s;
    }

    private void LoadStats()
    {
        if(statsLoaded)
        {
            return;
        }
        statsLoaded = true;
        string path = Path.Combine(Folder, "stats.txt");
        if(!File.Exists(path))
        {
            return;
        }
        try
        {
            foreach(string line in File.ReadAllLines(path))
            {
                if(line.StartsWith("#") || line.Trim().Length == 0) continue;
                string[] p = line.Split('\t');
                if(p.Length < 4) continue;
                Stats s = new Stats();
                int.TryParse(p[1], out s.Tries);
                int.TryParse(p[2], out s.Clears);
                if(!int.TryParse(p[3], out s.Best)) s.Best = -1;
                stats[p[0]] = s;
            }
        }
        catch(Exception ex)
        {
            Debug.LogWarning("[TAS] Could not read practice stats: " + ex.Message);
        }
    }

    private void SaveStats()
    {
        if(TasManager.SelfTestMode && !SelfTestWrites)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Folder);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# Pete TAS practice: level, tries, clears, best (frames, 50 per second)");
            foreach(KeyValuePair<string, Stats> kv in stats)
            {
                sb.Append(kv.Key).Append('\t').Append(kv.Value.Tries).Append('\t').Append(kv.Value.Clears).Append('\t').Append(kv.Value.Best).AppendLine();
            }
            File.WriteAllText(Path.Combine(Folder, "stats.txt"), sb.ToString());
        }
        catch(Exception ex)
        {
            Debug.LogWarning("[TAS] Could not save practice stats: " + ex.Message);
        }
    }

    // The self-test can let practice write its files (into a folder of its own)
    public static bool SelfTestWrites;

    private static string GhostPath(string key)
    {
        StringBuilder sb = new StringBuilder();
        foreach(char c in key)
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : c == ' ' ? ' ' : '_');
        }
        return Path.Combine(Folder, "Ghosts", sb.ToString() + ".txt");
    }

    private void SaveGhost(string key, List<GhostFrame> g)
    {
        if(TasManager.SelfTestMode && !SelfTestWrites)
        {
            return;
        }
        try
        {
            string path = GhostPath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# Pete TAS practice ghost: x y scaleX sprite (one line per frame)");
            foreach(GhostFrame f in g)
            {
                sb.Append(f.Pos.x.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                  .Append(f.Pos.y.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                  .Append(f.ScaleX.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                  .Append(f.Sprite).AppendLine();
            }
            File.WriteAllText(path, sb.ToString());
        }
        catch(Exception ex)
        {
            Debug.LogWarning("[TAS] Could not save the practice ghost: " + ex.Message);
        }
    }

    private static List<GhostFrame> LoadGhost(string key)
    {
        string path = GhostPath(key);
        if(!File.Exists(path))
        {
            return null;
        }
        try
        {
            List<GhostFrame> g = new List<GhostFrame>();
            foreach(string line in File.ReadAllLines(path))
            {
                if(line.StartsWith("#")) continue;
                string[] p = line.Split(new[] { ' ' }, 4);
                if(p.Length < 3) continue;
                GhostFrame f = new GhostFrame();
                f.Pos = new Vector2(float.Parse(p[0], CultureInfo.InvariantCulture), float.Parse(p[1], CultureInfo.InvariantCulture));
                f.ScaleX = float.Parse(p[2], CultureInfo.InvariantCulture);
                f.Sprite = p.Length > 3 ? p[3] : "";
                g.Add(f);
            }
            return g;
        }
        catch(Exception)
        {
            return null;
        }
    }

    // The levels found last time, so the list is there straight away next time
    private void SaveLevels()
    {
        if(TasManager.SelfTestMode && !SelfTestWrites)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Folder);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("#from\t" + RunName + "\t" + Status);
            foreach(Level L in Levels)
            {
                sb.Append(L.StartFrame).Append('\t').Append(L.RunFrames).Append('\t').Append(L.RoomName).Append('\t')
                  .Append(L.NextName ?? "").Append('\t').Append(L.Name).Append('\t').Append(L.Key).Append('\t')
                  .Append(L.RunName).Append('\t').Append(L.Run.Length).Append('\t').Append(Hash(L.Run)).AppendLine();
            }
            File.WriteAllText(Path.Combine(Folder, "levels.txt"), sb.ToString());
        }
        catch(Exception ex)
        {
            Debug.LogWarning("[TAS] Could not save the practice levels: " + ex.Message);
        }
    }

    // Reads the levels found last time. A level whose run has changed since is left out.
    private bool savedLevelsRead;

    public void LoadSavedLevels()
    {
        if(Levels.Count > 0 || Scanning || savedLevelsRead)
        {
            return;
        }
        savedLevelsRead = true;
        string path = Path.Combine(Folder, "levels.txt");
        if(!File.Exists(path))
        {
            return;
        }
        try
        {
            string[] lines = File.ReadAllLines(path);
            string[] head = lines.Length > 0 ? lines[0].Split('\t') : new string[0];
            if(head.Length < 2 || head[0] != "#from")
            {
                return;
            }
            Dictionary<string, TasMovie> runs = new Dictionary<string, TasMovie>();
            int changed = 0;
            for(int i = 1; i < lines.Length; i++)
            {
                string[] p = lines[i].Split('\t');
                if(p.Length < 9) continue;
                TasMovie movie;
                if(!runs.TryGetValue(p[6], out movie))
                {
                    try { movie = TasMovie.Load(TasManager.I.MoviePath(p[6])); } catch(Exception) { movie = null; }
                    runs[p[6]] = movie;
                }
                if(movie == null || movie.Length.ToString() != p[7] || Hash(movie) != p[8])
                {
                    changed++;
                    continue;
                }
                Level L = new Level();
                L.Number = Levels.Count + 1;
                L.StartFrame = int.Parse(p[0]);
                L.RunFrames = int.Parse(p[1]);
                L.RoomName = p[2];
                L.NextName = p[3].Length > 0 ? p[3] : null;
                L.Name = p[4];
                L.Key = p[5];
                L.RunName = p[6];
                L.Run = movie;
                L.S = StatsFor(L.Key);
                Levels.Add(L);
            }
            RunName = head[1];
            Status = changed > 0 ? "Some runs changed since their rooms were found. Press Find the rooms again."
                : head.Length > 2 && head[2].Length > 0 ? head[2] : Levels.Count + " levels. Press Play on one.";
        }
        catch(Exception)
        {
            Levels.Clear();
        }
    }

    private static string Hash(TasMovie m)
    {
        ulong h = 1469598103934665603UL;
        foreach(char c in m.ToText())
        {
            h = (h ^ c) * 1099511628211UL;
        }
        return h.ToString("x16");
    }

    // =====================================================================
    //  On screen: your best as a see-through Pete, and the practice bar
    // =====================================================================

    private void LateUpdate()
    {
        bool show = RaceGhost && Current != null && Current.S.Ghost != null && Current.S.Ghost.Count > 0
            && (Now == State.Ready || Now == State.Playing || Now == State.Result) && M.Player != null && M.Player.Valid;
        if(!show)
        {
            HideGhost();
            return;
        }
        List<GhostFrame> g = Current.S.Ghost;
        int k = Mathf.Max(0, M.Frame - Current.StartFrame);
        if(k >= g.Count)
        {
            // Your best already got there
            HideGhost();
            return;
        }
        if(ghost == null)
        {
            GameObject go = new GameObject("[TAS] Practice ghost");
            go.transform.SetParent(transform, false);
            ghost = go.AddComponent<SpriteRenderer>();
        }
        SpriteRenderer pete = M.Player.Sprite;
        GhostFrame f = g[k];
        ghost.sprite = FindSprite(f.Sprite) ?? (pete != null ? pete.sprite : null);
        ghost.sortingLayerID = pete != null ? pete.sortingLayerID : 0;
        ghost.sortingOrder = pete != null ? pete.sortingOrder - 1 : 0;
        ghost.color = new Color(0.45f, 0.85f, 1f, 0.5f);
        Vector3 ls = M.Player.Go.transform.lossyScale;
        ghost.transform.localScale = new Vector3(Mathf.Abs(ls.x) * Mathf.Sign(f.ScaleX == 0 ? 1 : f.ScaleX), Mathf.Abs(ls.y), 1f);
        ghost.transform.position = new Vector3(f.Pos.x, f.Pos.y, M.Player.Go.transform.position.z + 0.01f);
        ghost.gameObject.SetActive(true);
    }

    private void HideGhost()
    {
        if(ghost != null && ghost.gameObject.activeSelf)
        {
            ghost.gameObject.SetActive(false);
        }
    }

    private Sprite FindSprite(string name)
    {
        if(string.IsNullOrEmpty(name))
        {
            return null;
        }
        if(sprites == null)
        {
            sprites = new Dictionary<string, Sprite>();
            foreach(Sprite s in Resources.FindObjectsOfTypeAll<Sprite>())
            {
                if(s != null && !sprites.ContainsKey(s.name))
                {
                    sprites[s.name] = s;
                }
            }
        }
        Sprite found;
        return sprites.TryGetValue(name, out found) ? found : null;
    }

    public static string Secs(int frames)
    {
        return (frames / 50f).ToString("0.00", CultureInfo.InvariantCulture) + " s";
    }

    private static string Diff(int frames)
    {
        return (frames <= 0 ? "-" : "+") + (Mathf.Abs(frames) / 50f).ToString("0.00", CultureInfo.InvariantCulture) + " s";
    }

    public static string Rate(Stats s)
    {
        return s.Tries == 0 ? "-" : Mathf.RoundToInt(100f * s.Clears / s.Tries) + "%";
    }

    private GUIStyle barStyle, timeStyle, textStyle, keyStyle, keyOnStyle, hintStyle;
    private Texture2D dark, keyOn, keyOff;

    private void Styles()
    {
        if(barStyle != null)
        {
            return;
        }
        dark = Solid(new Color(0.06f, 0.07f, 0.1f, 0.82f));
        keyOn = Solid(new Color(1f, 0.82f, 0.35f, 0.95f));
        keyOff = Solid(new Color(0.2f, 0.22f, 0.28f, 0.75f));
        barStyle = new GUIStyle();
        barStyle.normal.background = dark;
        textStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, richText = true, alignment = TextAnchor.MiddleCenter };
        textStyle.normal.textColor = Color.white;
        timeStyle = new GUIStyle(textStyle) { fontSize = 34, fontStyle = FontStyle.Bold };
        hintStyle = new GUIStyle(textStyle) { fontSize = 13, alignment = TextAnchor.MiddleLeft };
        hintStyle.normal.textColor = new Color(1f, 1f, 1f, 0.7f);
        keyStyle = new GUIStyle(textStyle) { fontSize = 14, fontStyle = FontStyle.Bold };
        keyStyle.normal.background = keyOff;
        keyOnStyle = new GUIStyle(keyStyle);
        keyOnStyle.normal.background = keyOn;
        keyOnStyle.normal.textColor = Color.black;
    }

    private static Texture2D Solid(Color c)
    {
        Texture2D t = new Texture2D(1, 1);
        t.hideFlags = HideFlags.HideAndDontSave;
        t.SetPixel(0, 0, c);
        t.Apply();
        return t;
    }

    private void OnGUI()
    {
        if(Now == State.Off || Current == null || M == null)
        {
            return;
        }
        Styles();
        GUI.depth = -10;
        float scale = Mathf.Clamp(Screen.height / 900f, 0.7f, 2.5f);
        float vw = Screen.width / scale, vh = Screen.height / scale;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));

        // The practice bar: the level, the time, and how it's going
        Stats s = Current.S;
        Rect bar = new Rect(vw / 2 - 280, 10, 560, 112);
        GUI.Box(bar, GUIContent.none, barStyle);
        GUI.Label(new Rect(bar.x, bar.y + 6, bar.width, 22), "<b>" + Current.Number + ". " + Current.Name + "</b>  <color=#9aa3b5>to " + (Current.NextName ?? "the finish") + "</color>", textStyle);
        int frames = Mathf.Max(0, M.Frame - Current.StartFrame);
        string big, under;
        if(Now == State.Loading)
        {
            big = "…";
            under = "Getting the room ready";
        }
        else if(Now == State.Ready)
        {
            big = Secs(0);
            under = "Press any button to go";
        }
        else if(Now == State.Result)
        {
            big = resultGood ? Secs(lastTime) : Secs(frames);
            under = "<color=" + (resultGood ? "#6ee08a" : "#ff7a7a") + "><b>" + resultText + "</b></color>";
        }
        else
        {
            big = Secs(frames);
            int ghostLeft = s.Ghost != null ? s.Ghost.Count - 1 - frames : -1;
            under = s.Best >= 0 ? "Best " + Secs(s.Best) + (RaceGhost && ghostLeft >= 0 ? "  (your best is the see-through Pete)" : "") : "Reach " + (Current.NextName ?? "the finish") + "!";
        }
        GUI.Label(new Rect(bar.x, bar.y + 26, bar.width, 40), big, timeStyle);
        GUI.Label(new Rect(bar.x, bar.y + 64, bar.width, 22), under, textStyle);
        GUI.Label(new Rect(bar.x, bar.y + 86, bar.width, 22), "<color=#9aa3b5>Tries " + s.Tries + "   Clears " + s.Clears + " (" + Rate(s) + ")   Best " + (s.Best >= 0 ? Secs(s.Best) : "-") + "   Your run " + Secs(Current.RunFrames) + "</color>", textStyle);

        // Your buttons
        if(ShowInputs)
        {
            Buttons b = Now == State.Playing ? PeteInput.TasHeld : TasManager.ReadPlayerInput();
            string[] names = { "←", "→", "↑", "↓", "JUMP", "ATK" };
            Buttons[] keys = { Buttons.Left, Buttons.Right, Buttons.Up, Buttons.Down, Buttons.Jump, Buttons.Attack };
            float w = 52, gap = 6, total = names.Length * w + (names.Length - 1) * gap;
            for(int i = 0; i < names.Length; i++)
            {
                Rect r = new Rect(vw / 2 - total / 2 + i * (w + gap), vh - 52, w, 36);
                GUI.Label(r, names[i], (b & keys[i]) != 0 ? keyOnStyle : keyStyle);
            }
        }
        // (bottom right: the game shows its version number bottom left)
        hintStyle.alignment = TextAnchor.MiddleRight;
        GUI.Label(new Rect(vw - 532, vh - 30, 520, 22), "R: start the room again     Esc: back to the list", hintStyle);
    }
}
