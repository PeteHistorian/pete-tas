using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using Cinemachine;
using Buttons = PeteInput.Buttons;

// The heart of the TAS tool. It runs the game one frame (one 50 Hz physics tick) at a time,
// keeps a savestate for every frame so you can go backwards, and replays the movie from the start
// whenever you ask it to check ("Verify") that everything is exact.
[DefaultExecutionOrder(30000)]
public class TasManager : MonoBehaviour
{
    public static TasManager I;
    public static bool SelfTestMode;

    public const float Tick = 0.02f;
    public const string WorldSceneName = "World";
    public const int RandomSeed = 20221223;

    public enum Mode { Booting, Paused, Playing, Seeking, Searching, Video, VideoEndScreen }
    public enum PrimeMode { PreviousFrame, SameFrame, None }

    // ---------- Movie and route ----------
    public TasMovie Movie = new TasMovie();
    public string MovieName = "pete-tas";
    public bool MovieDirty;
    public int Frame { get; private set; }
    public readonly List<TasFrameRecord> Timeline = new List<TasFrameRecord>();
    public readonly List<Vector2> RoutePositions = new List<Vector2>();
    public int RouteVersion;
    private readonly List<TasSnapshot> snapshots = new List<TasSnapshot>();
    public TasStateRegistry Registry;
    public TasPlayerInfo Player;

    public readonly List<TasGhost> Ghosts = new List<TasGhost>();
    public readonly List<TasLine> Lines = new List<TasLine>();
    public int MaxGhosts = 6;
    private int ghostCounter;
    private bool ghostArmed = true;

    // ---------- Status ----------
    public Mode CurrentMode { get; private set; }
    public bool Recording;
    public int SpeedPercent = 100;
    public bool ExactRewind;
    public PrimeMode Priming = PrimeMode.PreviousFrame;
    // How many frames before the target a rewind loads from (then replays forward). More than half a second.
    public int RewindLeadIn = 30;
    public int DeathFrame = -1;
    public int FinishFrame = -1;
    public int DesyncFrame = -1;
    public int TickLockErrors;
    public bool RestoredSinceBoot;
    public string Message = "";
    public float MessageUntil;
    public float BusyProgress;
    public string BusyText = "";
    public long SnapshotBytes;
    public int Restores;
    public float LastRestoreMs;
    public float LastCaptureMs;
    public double ClockOffsetAfterBoot;

    public bool IsIdle
    {
        get { return CurrentMode == Mode.Paused && stepRequests == 0 && !tickPending; }
    }

    public bool Busy
    {
        get { return CurrentMode == Mode.Booting || CurrentMode == Mode.Seeking || CurrentMode == Mode.Searching; }
    }

    public int GreenEnd
    {
        get { return Timeline.Count - 1; }
    }

    public static string TasFolder
    {
        get { return Path.Combine(Path.GetDirectoryName(Application.dataPath), "TAS Files"); }
    }

    // ---------- Video playback ----------
    public bool VideoShowInputs = true;
    public bool VideoShowRoute = false;
    public bool VideoShowHitboxes = false;
    public bool VideoShowTimer = true;
    public bool VideoEndScreen = true;
    // True while video playback is being saved to a file (see TasVideoExport)
    public bool VideoExporting;
    private int videoReturnFrame;

    // ---------- Search ----------
    public TasSearch Search;
    private IEnumerator<bool> searchRoutine;
    public Buttons SearchInput;

    // ---------- Internals ----------
    private bool tickPending;
    private int fixedStepsThisFrame;
    private int stepRequests;
    private int seekTarget = -1;
    private int playFrameCounter;
    private int bootStage = -1;
    private int bootWait;
    private bool sceneLoadedFlag;
    private Action afterBoot;
    private bool fastMode;
    private float savedVolume = 1f;
    private bool cullingSaved;
    private float autosaveAt;
    private Camera fastModeCamera;

    // True while the TAS replays at full speed with the level hidden (going back, reloading, searching)
    public bool FastMode
    {
        get { return fastMode; }
    }

    public static bool ShouldRunTas()
    {
#if UNITY_EDITOR
        return UnityEditor.SessionState.GetBool("PeteTAS.PlayTAS", false) || HasArg("-tas");
#elif PETE_TAS
        return true;
#else
        return HasArg("-tas");
#endif
    }

    public static bool HasArg(string arg)
    {
        foreach(string a in Environment.GetCommandLineArgs())
        {
            if(string.Equals(a, arg, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        TasHooks.Reset();
        PeteInput.TasHeld = Buttons.None;
        PeteInput.TasPrevious = Buttons.None;
        PeteInput.TasEdgesLive = false;
        I = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void AutoStart()
    {
        if(!ShouldRunTas())
        {
            return;
        }
        // Freeze the game before the first scene even starts, so nothing happens until the TAS is ready
        TasHooks.Active = true;
        Time.timeScale = 0f;
        Time.captureDeltaTime = Tick;

        GameObject root = new GameObject("[TAS]");
        DontDestroyOnLoad(root);
        root.AddComponent<TasEarly>();
        root.AddComponent<TasManager>();
        root.AddComponent<TasHud>();
        root.AddComponent<TasDraw>();
        root.AddComponent<TasFreeCam>();
        root.AddComponent<TasVideoExport>();
        root.AddComponent<TasPredict>();
        SelfTestMode = HasArg("-tasSelfTest");
#if UNITY_EDITOR
        SelfTestMode |= UnityEditor.SessionState.GetBool("PeteTAS.SelfTest", false);
#endif
        if(SelfTestMode)
        {
            root.AddComponent<TasSelfTest>();
        }
        else if(HasArg("-tasVisualTest"))
        {
            SelfTestMode = true;
            root.AddComponent<TasVisualTest>();
        }
    }

    private void Awake()
    {
        I = this;
        TasHooks.Active = true;
        Application.runInBackground = true;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 60;
        SceneManager.sceneLoaded += OnSceneLoaded;
        Directory.CreateDirectory(TasFolder);
        string autosave = Path.Combine(TasFolder, "autosave.txt");
        if(File.Exists(autosave) && !SelfTestMode)
        {
            try
            {
                Movie = TasMovie.Load(autosave);
                MovieName = "autosave";
                Say("Loaded your last movie (autosave). Frames: " + Movie.Length);
            }
            catch(Exception ex)
            {
                Debug.LogWarning("[TAS] Could not read autosave: " + ex.Message);
            }
        }
        BeginBoot(null);
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if(I == this)
        {
            I = null;
        }
    }

    private void OnApplicationQuit()
    {
        Autosave(true);
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if(scene.name.StartsWith("[TAS]"))
        {
            // The prediction lines' hidden copy of the level, not a real level
            return;
        }
        sceneLoadedFlag = true;
        if(CurrentMode == Mode.Video && scene.name != WorldSceneName)
        {
            // The finish trigger loaded the end screen during video playback
            CurrentMode = Mode.VideoEndScreen;
            TasHooks.AllowFinishSceneLoad = false;
            SetFastMode(false);
            Time.timeScale = 1f;
            Application.targetFrameRate = VideoExporting ? -1 : 50;
        }
    }

    public void Say(string text, float seconds = 5f)
    {
        Message = text;
        MessageUntil = Time.unscaledTime + seconds;
        Debug.Log("[TAS] " + text);
    }

    // =====================================================================
    //  Frame loop
    // =====================================================================

    // Called by TasEarly at the very start of every physics step, before any game script.
    public void OnFixedStepStart()
    {
        fixedStepsThisFrame++;
        if(!tickPending || fixedStepsThisFrame > 1)
        {
            return;
        }
        TasCamState.BeforeTick();
        int f = Frame + 1;
        Buttons input = InputForTick(f);
        PeteInput.TasPrevious = CurrentMode == Mode.Searching ? searchPrevious : Movie.Get(f - 1);
        PeteInput.TasHeld = input;
        PeteInput.TasEdgesLive = true;
        if(CurrentMode == Mode.Searching)
        {
            searchPrevious = input;
        }
        Frame = f;
    }

    private Buttons searchPrevious;

    private Buttons InputForTick(int f)
    {
        if(CurrentMode == Mode.Searching)
        {
            return SearchInput;
        }
        if(Recording && (CurrentMode == Mode.Paused || CurrentMode == Mode.Playing))
        {
            Buttons keys = ReadPlayerInput();
            if(keys != Movie.Get(f) || f > Movie.Length)
            {
                if(!recordingRemembered)
                {
                    Remember("recording from frame " + f);
                    recordingRemembered = true;
                }
                InvalidateFrom(f, false);
                Movie.Set(f, keys);
                MovieDirty = true;
            }
            return keys;
        }
        if(f > Movie.Length && (CurrentMode == Mode.Paused || CurrentMode == Mode.Playing))
        {
            // Stepping past the end of the movie adds empty frames
            Movie.Set(f, Buttons.None);
            MovieDirty = true;
        }
        return Movie.Get(f);
    }

    // Lets the self-test pretend buttons are being held while recording.
    public static Buttons? FakeHeld;

    // What the player is holding right now, using the same controls as the real game:
    // arrows / WASD / left stick to move and aim, Space / A (Cross) to jump,
    // O / left click / X (Square) to attack.
    public static Buttons ReadPlayerInput()
    {
        if(FakeHeld.HasValue)
        {
            return FakeHeld.Value;
        }
        Buttons b = Buttons.None;
        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");
        if(h < -0.5f) b |= Buttons.Left;
        if(h > 0.5f) b |= Buttons.Right;
        if(v > 0.5f) b |= Buttons.Up;
        if(v < -0.5f) b |= Buttons.Down;
        if(Input.GetButton("Jump")) b |= Buttons.Jump;
        if(Input.GetKey(KeyCode.O) || Input.GetKey(KeyCode.JoystickButton2) || MouseAttack()) b |= Buttons.Attack;
        return b;
    }

    // 0 = button up, 1 = this click is an attack, 2 = this click started on the TAS panel (not an attack)
    private static int mouseClickState;

    private static bool MouseAttack()
    {
        if(!Input.GetMouseButton(0))
        {
            mouseClickState = 0;
            return false;
        }
        if(mouseClickState == 0)
        {
            bool onPanel = TasHud.I != null && (TasHud.I.PointerOverUi(Input.mousePosition) || TasHud.I.Placing);
            bool freeCam = TasFreeCam.I != null && TasFreeCam.I.Active;
            mouseClickState = onPanel || freeCam ? 2 : 1;
        }
        return mouseClickState == 1;
    }

    private void LateUpdate()
    {
        if(tickPending)
        {
            tickPending = false;
            PeteInput.TasEdgesLive = false;
            if(fixedStepsThisFrame != 1)
            {
                TickLockErrors++;
                Debug.LogError("[TAS] Expected exactly 1 physics step this frame, got " + fixedStepsThisFrame);
            }
            if(fixedStepsThisFrame >= 1)
            {
                AfterTick();
            }
        }
        fixedStepsThisFrame = 0;

        if(CurrentMode == Mode.Booting)
        {
            BootStep();
            if(CurrentMode == Mode.Booting)
            {
                TasCamState.EndOfFrame(true);
                return;
            }
        }
        if(CurrentMode == Mode.VideoEndScreen)
        {
            TasCamState.EndOfFrame(true);
            // When an export has recorded a few seconds of the end screen, go straight back to the editor
            bool exportDone = VideoExporting && TasVideoExport.I != null && !TasVideoExport.I.Exporting;
            if(exportDone || Input.GetKeyDown(KeyCode.Escape))
            {
                ReturnFromVideo();
            }
            return;
        }

        bool tick = DecideTick();
        tickPending = tick;
        // The game camera only runs on frames the game runs (see TasCamState)
        TasCamState.EndOfFrame(tick);
        Time.timeScale = tick ? 1f : 0f;
        // Between frames the game's own scripts are switched off, so nothing can sneak in while paused
        FreezeGameScripts(!tick);

        if(MovieDirty && Time.unscaledTime > autosaveAt)
        {
            Autosave(false);
        }
        if(!Recording)
        {
            // The next stretch of recording gets its own undo step
            recordingRemembered = false;
        }
    }

    private bool DecideTick()
    {
        if(needExactReplay && (CurrentMode == Mode.Paused || CurrentMode == Mode.Playing || CurrentMode == Mode.Seeking))
        {
            StartExactReplay();
            return false;
        }
        bool blocked = TasHooks.PlayerDied || TasHooks.Finished;
        switch(CurrentMode)
        {
            case Mode.Paused:
                if(stepRequests > 0)
                {
                    stepRequests--;
                    if(blocked)
                    {
                        stepRequests = 0;
                        Say(TasHooks.PlayerDied ? "Pete died on this frame. Go back and change the inputs." : "The run is finished on this frame.");
                        return false;
                    }
                    return true;
                }
                return false;

            case Mode.Playing:
            {
                if(blocked || (!Recording && Frame >= Movie.Length))
                {
                    SetMode(Mode.Paused);
                    return false;
                }
                int every = SpeedPercent >= 100 ? 1 : SpeedPercent >= 50 ? 2 : 4;
                playFrameCounter++;
                return playFrameCounter % every == 0;
            }

            case Mode.Seeking:
                if(Frame >= seekTarget || blocked)
                {
                    seekTarget = -1;
                    SetMode(Mode.Paused);
                    if(DesyncFrame >= 0 && DesyncFrame != lastReportedDesync)
                    {
                        lastReportedDesync = DesyncFrame;
                        Say("Desync on frame " + DesyncFrame + ": it came out different than last time. Press Verify to get the exact result.", 8f);
                    }
                    if(afterSeek != null)
                    {
                        Action a = afterSeek;
                        afterSeek = null;
                        a();
                    }
                    return CurrentMode == Mode.Seeking || CurrentMode == Mode.Video || CurrentMode == Mode.Searching ? DecideTick() : false;
                }
                BusyProgress = seekTarget > 0 ? (float)Frame / seekTarget : 1f;
                return true;

            case Mode.Video:
                if(blocked && !(TasHooks.Finished && VideoEndScreen))
                {
                    StopVideo();
                    return false;
                }
                if(Frame >= Movie.Length)
                {
                    StopVideo();
                    return false;
                }
                return true;

            case Mode.Searching:
                if(searchRoutine == null)
                {
                    SetMode(Mode.Paused);
                    return false;
                }
                bool more;
                try
                {
                    more = searchRoutine.MoveNext();
                }
                catch(Exception ex)
                {
                    Debug.LogException(ex);
                    Say("Auto-search stopped because of an error: " + ex.Message, 8f);
                    more = false;
                }
                if(!more)
                {
                    searchRoutine = null;
                    FinishSearch();
                    return CurrentMode == Mode.Seeking ? DecideTick() : false;
                }
                return searchRoutine.Current;
        }
        return false;
    }

    // Called after every frame the game runs (used by the self-test to trace what happens)
    public static Action TickHook;

    // ---------- Never show a rewind that came out different ----------
    // Loading a savestate can't recreate everything hidden inside Unity's physics engine (for example the order it
    // reports touches in), so very rarely a rewind comes out a tiny bit different from the first run. When that
    // happens the tool notices (each frame is compared with the first run) and replays from the start instead.
    public bool AutoExact = true;
    public int AutoExactReplays;
    private bool needExactReplay;
    private int suspectFrame = -1;

    private void StartExactReplay()
    {
        needExactReplay = false;
        AutoExactReplays++;
        int target = CurrentMode == Mode.Seeking && seekTarget >= 0 ? seekTarget : Frame;
        bool resumePlaying = CurrentMode == Mode.Playing;
        Action then = afterSeek;
        afterSeek = null;
        seekTarget = -1;
        Say("Frame " + suspectFrame + " came out slightly different after going back, so the tool replayed from the start to get the exact result.", 6f);
        ReplayFromStart(target, () =>
        {
            if(then != null)
            {
                then();
            }
            if(resumePlaying && CurrentMode == Mode.Paused)
            {
                SetMode(Mode.Playing);
            }
        });
    }

    private Action afterSeek;
    private int lastReportedDesync = -1;
    private TasSnapshot lastStart;

    private void AfterTick()
    {
        if(Movie.TeleportFrame >= 1 && Frame == Movie.TeleportFrame && Player != null && Player.Valid)
        {
            ApplyTeleport(Movie.TeleportTo);
        }
        if(CurrentMode == Mode.Searching)
        {
            if(Search != null)
            {
                Search.AfterTick(this);
            }
            return;
        }

        if(Player == null || !Player.Valid)
        {
            // The level is being unloaded (for example the end screen is loading)
            return;
        }
        if(TickHook != null)
        {
            TickHook();
        }
        float t0 = Time.realtimeSinceStartup;
        TasSnapshot snap = Registry.Capture(Frame);
        LastCaptureMs = (Time.realtimeSinceStartup - t0) * 1000f;

        TasFrameRecord rec = new TasFrameRecord();
        rec.Pos = Player.Position;
        rec.Vel = Player.Velocity;
        rec.OnGround = Player.OnGround;
        rec.Input = PeteInput.TasHeld;
        rec.Fingerprint = snap.Fingerprint;
        rec.Verified = !RestoredSinceBoot;

        // Just after loading a savestate, Unity's hidden physics timers are still catching up (for example an idle
        // enemy may "fall asleep" a few frames late). Those catch-up frames aren't compared, and what we already
        // know about them is kept, because it came from a cleaner run.
        bool warmingUp = Frame < warmupUntil;
        if(warmingUp && Frame < Timeline.Count && GetSnapshot(Frame) != null)
        {
            HandleDeathAndFinish();
            return;
        }
        if(warmingUp)
        {
            // Not trustworthy enough to compare against later, so leave the fingerprint blank
            rec.Verified = false;
            rec.Fingerprint = 0;
        }

        if(Frame < Timeline.Count && !warmingUp)
        {
            // We've been on this frame before with the same inputs: it must come out the same
            TasFrameRecord old = Timeline[Frame];
            if(old.Fingerprint == 0)
            {
                // Nothing reliable to compare with (recorded while catching up after a rewind)
            }
            else if(old.Fingerprint == rec.Fingerprint)
            {
                rec.Verified |= old.Verified;
            }
            else if(AutoExact && RestoredSinceBoot)
            {
                // This came from loading a savestate and it's not what the first run did. Keep the first run's
                // record of the frame and replay from the very start (always exact) instead of showing it.
                if(!needExactReplay)
                {
                    suspectFrame = Frame;
                    TasSnapshot before = GetSnapshot(Frame);
                    if(before != null)
                    {
                        Debug.Log("[TAS] Frame " + Frame + " came out different after loading a savestate; replaying from the start:\n" + Registry.DescribeDiff(before, snap, 8));
                    }
                }
                needExactReplay = true;
                HandleDeathAndFinish();
                return;
            }
            else
            {
                if(DesyncFrame < 0 || Frame < DesyncFrame)
                {
                    TasSnapshot before = GetSnapshot(Frame);
                    if(before != null)
                    {
                        Debug.LogWarning("[TAS] Frame " + Frame + " differs from last time:\n" + Registry.DescribeDiff(before, snap, 16));
                    }
                    DesyncFrame = Frame;
                    if(CurrentMode != Mode.Seeking)
                    {
                        Say("Desync on frame " + Frame + ": this frame came out different than last time. Press Verify to get the exact result.", 8f);
                    }
                }
            }
            Timeline[Frame] = rec;
            RoutePositions[Frame] = rec.Pos;
        }
        else if(Frame < Timeline.Count)
        {
            Timeline[Frame] = rec;
            RoutePositions[Frame] = rec.Pos;
        }
        else
        {
            while(Timeline.Count < Frame)
            {
                // Shouldn't happen, but keep the lists lined up
                Timeline.Add(rec);
                RoutePositions.Add(rec.Pos);
            }
            Timeline.Add(rec);
            RoutePositions.Add(rec.Pos);
        }
        RouteVersion++;
        SetSnapshot(Frame, snap);
        HandleDeathAndFinish();
    }

    private void HandleDeathAndFinish()
    {
        if(TasHooks.PlayerDied && DeathFrame != Frame)
        {
            DeathFrame = Frame;
            if(CurrentMode != Mode.Seeking)
            {
                Say("Pete died on frame " + Frame + ". Go back and change the inputs.", 8f);
            }
        }
        if(TasHooks.Finished && FinishFrame != Frame)
        {
            FinishFrame = Frame;
            Say("Finished! Final time " + FormatTime(Player.TimerSeconds) + " (frame " + Frame + ").", 10f);
        }
    }

    // =====================================================================
    //  Booting: start the level fresh (used at start, for Verify and for going back to frame 0)
    // =====================================================================

    private void BeginBoot(Action then)
    {
        FreezeGameScripts(false);
        // No buttons held while the level loads, so every fresh start is identical
        PeteInput.TasHeld = Buttons.None;
        PeteInput.TasPrevious = Buttons.None;
        PeteInput.TasEdgesLive = false;
        SearchInput = Buttons.None;
        searchPrevious = Buttons.None;
        afterBoot = then;
        SetMode(Mode.Booting);
        SetFastMode(true);
        bootStage = 0;
        bootWait = 0;
        BusyText = "Starting the level";
        BusyProgress = 0f;
    }

    private void BootStep()
    {
        switch(bootStage)
        {
            case 0:
            {
                // Line Unity's clock up so every frame contains exactly one physics step.
                // We want "time since last physics step" to sit at half a step.
                double r = Time.timeAsDouble - Time.fixedTimeAsDouble;
                double want = Tick * 0.5;
                double dt = want - r;
                if(Math.Abs(dt) < 1e-6)
                {
                    Time.timeScale = 0f;
                    bootStage = 1;
                    BootStep();
                    return;
                }
                if(dt < 0)
                {
                    dt += Tick;
                }
                Time.captureDeltaTime = (float)dt;
                Time.timeScale = 1f;
                bootStage = 1;
                return;
            }
            case 1:
            {
                Time.timeScale = 0f;
                Time.captureDeltaTime = Tick;
                // Throw away everything the game keeps between scenes so the reload is a true fresh start
                foreach(DontDestroyOnLoad keeper in FindObjectsOfType<DontDestroyOnLoad>(true))
                {
                    if(keeper.gameObject.scene.name == "DontDestroyOnLoad")
                    {
                        Destroy(keeper.gameObject);
                    }
                }
                TasHooks.PlayerDied = false;
                TasHooks.Finished = false;
                TasHooks.AllowFinishSceneLoad = false;
                sceneLoadedFlag = false;
                SceneManager.LoadScene(WorldSceneName);
                bootStage = 2;
                bootWait = 0;
                return;
            }
            case 2:
                Time.timeScale = 0f;
                if(sceneLoadedFlag)
                {
                    bootWait++;
                }
                if(bootWait >= 2)
                {
                    bootStage = 3;
                    BootStep();
                }
                return;
            case 3:
                FinishBoot();
                return;
        }
    }

    private void FinishBoot()
    {
        bootStage = -1;
        ClockOffsetAfterBoot = Time.timeAsDouble - Time.fixedTimeAsDouble;
        if(Math.Abs(ClockOffsetAfterBoot - Tick * 0.5) > 0.001)
        {
            Debug.LogWarning("[TAS] Clock not lined up: " + ClockOffsetAfterBoot);
        }
        TasHooks.TimeOffset = Time.timeAsDouble;
        TasHooks.PlayerDied = false;
        TasHooks.Finished = false;
        UnityEngine.Random.InitState(RandomSeed);
        foreach(MusicManager mm in FindObjectsOfType<MusicManager>(true))
        {
            // Random music changes would use up random numbers at odd times
            mm.CancelInvoke();
        }
        PeteInput.TasHeld = Buttons.None;
        PeteInput.TasPrevious = Buttons.None;
        PeteInput.TasEdgesLive = false;

        Registry = new TasStateRegistry(transform);
        Registry.Build();
        Player = TasPlayerInfo.Find();
        Frame = 0;
        warmupUntil = -1;
        // Old savestates point at objects from the old level, so they're gone.
        // The old route records stay: the replay compares itself against them frame by frame.
        snapshots.Clear();
        SnapshotBytes = 0;
        RestoredSinceBoot = false;
        needExactReplay = false;
        DeathFrame = -1;
        FinishFrame = -1;

        TasSnapshot s0 = Registry.Capture(0);
        TasFrameRecord rec = new TasFrameRecord();
        rec.Pos = Player.Position;
        rec.Vel = Player.Velocity;
        rec.OnGround = Player.OnGround;
        rec.Fingerprint = s0.Fingerprint;
        rec.Verified = true;
        if(Timeline.Count == 0)
        {
            Timeline.Add(rec);
            RoutePositions.Add(rec.Pos);
        }
        else
        {
            if(Timeline[0].Fingerprint != rec.Fingerprint)
            {
                Debug.LogWarning("[TAS] The starting state is different from last time:\n" + (lastStart != null ? Registry.DescribeDiff(lastStart, s0) : "(no earlier start to compare)"));
                DesyncFrame = 0;
            }
            Timeline[0] = rec;
            RoutePositions[0] = rec.Pos;
        }
        RouteVersion++;
        SetSnapshot(0, s0);
        lastStart = s0;

        SetMode(Mode.Paused);
        SetFastMode(false);
        Action then = afterBoot;
        afterBoot = null;
        if(then != null)
        {
            then();
        }
    }

    // =====================================================================
    //  Savestates
    // =====================================================================

    private void SetSnapshot(int frame, TasSnapshot s)
    {
        while(snapshots.Count <= frame)
        {
            snapshots.Add(null);
        }
        if(snapshots[frame] != null)
        {
            SnapshotBytes -= snapshots[frame].ApproxBytes;
        }
        snapshots[frame] = s;
        SnapshotBytes += s.ApproxBytes;
        if(SnapshotBytes > 400L * 1024 * 1024)
        {
            ThinSnapshots();
        }
    }

    public TasSnapshot GetSnapshot(int frame)
    {
        return frame >= 0 && frame < snapshots.Count ? snapshots[frame] : null;
    }

    // Keeps memory in check: far from the current frame we only keep every 10th savestate
    // (plus the one before it, which rewinding needs). Missing frames are re-played quickly when needed.
    private void ThinSnapshots()
    {
        for(int f = 1; f < snapshots.Count; f++)
        {
            if(snapshots[f] == null || Math.Abs(f - Frame) <= 600)
            {
                continue;
            }
            int m = f % 10;
            if(m == 0 || m == 9)
            {
                continue;
            }
            SnapshotBytes -= snapshots[f].ApproxBytes;
            snapshots[f] = null;
        }
    }

    // Loads the savestate for a frame. Returns false if there isn't a usable one.
    private bool RestoreFrame(int k)
    {
        TasSnapshot s = GetSnapshot(k);
        if(s == null || k <= 0)
        {
            return false;
        }
        TasSnapshot prime = null;
        if(Priming == PrimeMode.PreviousFrame)
        {
            prime = GetSnapshot(k - 1);
            if(prime == null)
            {
                return false;
            }
        }
        else if(Priming == PrimeMode.SameFrame)
        {
            prime = s;
        }
        RestoreSnapshot(s, prime, k);
        return true;
    }

    private int warmupUntil = -1;

    // Also used by the auto-search for its own savestates.
    public void RestoreSnapshot(TasSnapshot s, TasSnapshot prime, int frame)
    {
        warmupUntil = frame + RewindLeadIn;
        float t0 = Time.realtimeSinceStartup;
        if(Priming == PrimeMode.None)
        {
            prime = null;
        }
        else if(Priming == PrimeMode.SameFrame)
        {
            prime = s;
        }
        bool wasFrozen = scriptsFrozen;
        FreezeGameScripts(false);
        Registry.Restore(s, prime);
        FreezeGameScripts(wasFrozen);
        LastRestoreMs = (Time.realtimeSinceStartup - t0) * 1000f;
        Restores++;
        Frame = frame;
        RestoredSinceBoot = true;
        if(CurrentMode == Mode.Searching)
        {
            PeteInput.TasHeld = searchPrevious;
        }
        else
        {
            PeteInput.TasHeld = Movie.Get(frame);
            PeteInput.TasPrevious = Movie.Get(frame - 1);
        }
        PeteInput.TasEdgesLive = false;
        if(s.Cam == null || !TasCamState.Works)
        {
            // No saved camera smoothing: put the camera straight onto its target instead
            SnapCameras();
        }
    }

    public TasSnapshot CaptureSnapshot()
    {
        return Registry.Capture(Frame);
    }

    public void SetSearchPrevious(Buttons b)
    {
        searchPrevious = b;
        PeteInput.TasHeld = b;
    }

    // ---------- Freezing the game while paused ----------
    // Unity keeps calling the game's Update() even while the TAS is paused. Most of that does nothing,
    // but some of it (like Pete's animation script reacting to an attack ending) would happen "between"
    // frames and change the result. So while paused, every game script is switched off.
    public static readonly HashSet<Behaviour> Frozen = new HashSet<Behaviour>();
    private bool scriptsFrozen;

    private void FreezeGameScripts(bool freeze)
    {
        if(freeze == scriptsFrozen)
        {
            return;
        }
        bool wasMuted = TasHooks.MuteCallbacks;
        TasHooks.MuteCallbacks = true;
        try
        {
            if(freeze)
            {
                Frozen.Clear();
                foreach(MonoBehaviour mb in FindObjectsOfType<MonoBehaviour>())
                {
                    if(mb.enabled && TasStateRegistry.IsGameScript(mb))
                    {
                        mb.enabled = false;
                        Frozen.Add(mb);
                    }
                }
            }
            else
            {
                foreach(Behaviour b in Frozen)
                {
                    if(b != null)
                    {
                        b.enabled = true;
                    }
                }
                Frozen.Clear();
            }
        }
        finally
        {
            TasHooks.MuteCallbacks = wasMuted;
        }
        scriptsFrozen = freeze;
    }

    private void SnapCameras()
    {
        foreach(CinemachineVirtualCameraBase vcam in FindObjectsOfType<CinemachineVirtualCameraBase>())
        {
            vcam.PreviousStateIsValid = false;
        }
    }

    // =====================================================================
    //  Commands (used by the HUD, hotkeys, search and self-test)
    // =====================================================================

    private void SetMode(Mode m)
    {
        CurrentMode = m;
        switch(m)
        {
            case Mode.Paused:
                SetFastMode(false);
                Application.targetFrameRate = 60;
                break;
            case Mode.Playing:
            case Mode.Video:
                SetFastMode(false);
                Application.targetFrameRate = 50;
                playFrameCounter = 0;
                break;
            case Mode.Booting:
            case Mode.Seeking:
            case Mode.Searching:
                SetFastMode(true);
                break;
        }
    }

    // While seeking or searching we skip drawing the level and mute sound, so it runs much faster.
    private void SetFastMode(bool on)
    {
        if(on == fastMode)
        {
            return;
        }
        fastMode = on;
        if(on)
        {
            Application.targetFrameRate = -1;
            savedVolume = AudioListener.volume;
            AudioListener.volume = 0f;
            fastModeCamera = Camera.main;
            if(fastModeCamera != null)
            {
                // Draw nothing while going fast, by pointing the camera's "what can I see" check at empty space.
                // (Not by emptying its list of layers: the game's camera system uses that list to pick which room
                // camera to follow, so the camera would stop moving with Pete and jump when the fast part ends.)
                fastModeCamera.cullingMatrix = Matrix4x4.Ortho(-1f, 1f, -1f, 1f, 0.1f, 1f) * Matrix4x4.Translate(new Vector3(0f, 0f, 1e7f));
                cullingSaved = true;
            }
        }
        else
        {
            AudioListener.volume = savedVolume;
            if(fastModeCamera != null && cullingSaved)
            {
                fastModeCamera.ResetCullingMatrix();
            }
            fastModeCamera = null;
            cullingSaved = false;
        }
    }

    public void StepForward(int frames = 1)
    {
        if(Busy || CurrentMode == Mode.Video)
        {
            return;
        }
        ghostArmed = true;
        if(CurrentMode == Mode.Playing)
        {
            SetMode(Mode.Paused);
        }
        if(frames <= 1)
        {
            stepRequests++;
        }
        else
        {
            Seek(Frame + frames);
        }
    }

    public void StepBack(int frames = 1)
    {
        if(Busy || CurrentMode == Mode.Video)
        {
            return;
        }
        Seek(Mathf.Max(0, Frame - frames));
    }

    public void TogglePlay()
    {
        if(Busy || CurrentMode == Mode.Video)
        {
            return;
        }
        ghostArmed = true;
        if(CurrentMode == Mode.Playing)
        {
            SetMode(Mode.Paused);
        }
        else
        {
            if(!Recording && Frame >= Movie.Length)
            {
                Say("You're at the end of the movie. Turn on Record (Q) or step forward (F) to add frames.");
                return;
            }
            SetMode(Mode.Playing);
        }
    }

    public void Pause()
    {
        if(CurrentMode == Mode.Playing)
        {
            SetMode(Mode.Paused);
        }
    }

    // Jump to any frame. Uses the savestates when possible, otherwise replays quickly.
    // force: replay even if we're already on that frame (used after an earlier input was changed)
    public void Seek(int target, Action then = null, bool force = false)
    {
        if(CurrentMode == Mode.Booting || CurrentMode == Mode.Searching)
        {
            return;
        }
        if(target < 0)
        {
            target = 0;
        }
        ghostArmed = true;
        stepRequests = 0;
        if(CurrentMode == Mode.Playing || CurrentMode == Mode.Seeking)
        {
            SetMode(Mode.Paused);
        }
        if(target == Frame && !force)
        {
            if(then != null) then();
            return;
        }
        bool goingBack = target < Frame || force;
        if(target == 0 || (goingBack && ExactRewind))
        {
            ReplayFromStart(target, then);
            return;
        }
        // Load a savestate a little before the target and replay the last stretch, so Unity's hidden physics
        // timers (like when things fall asleep) build up exactly as they did the first time.
        int best = -1;
        int limit = Mathf.Min(target - RewindLeadIn, GreenEnd);
        bool worthRestoring = goingBack || target - Frame > RewindLeadIn;
        for(int k = limit; worthRestoring && k >= 1; k--)
        {
            if(!goingBack && k <= Frame)
            {
                break;
            }
            if(GetSnapshot(k) != null && (Priming != PrimeMode.PreviousFrame || GetSnapshot(k - 1) != null))
            {
                best = k;
                break;
            }
        }
        if(best > 0)
        {
            RestoreFrame(best);
        }
        else if(goingBack)
        {
            ReplayFromStart(target, then);
            return;
        }
        if(Frame < target)
        {
            if(Frame >= GreenEnd && (TasHooks.PlayerDied || TasHooks.Finished))
            {
                if(then != null) then();
                return;
            }
            seekTarget = target;
            afterSeek = then;
            BusyText = "Going to frame " + target;
            SetMode(Mode.Seeking);
        }
        else if(then != null)
        {
            then();
        }
    }

    // Reloads the level and replays the movie from the very start: always exact.
    public void ReplayFromStart(int target, Action then = null)
    {
        if(CurrentMode == Mode.Playing || CurrentMode == Mode.Seeking)
        {
            SetMode(Mode.Paused);
        }
        BeginBoot(() =>
        {
            if(target > 0)
            {
                seekTarget = target;
                afterSeek = then;
                BusyText = "Replaying to frame " + target;
                SetMode(Mode.Seeking);
            }
            else if(then != null)
            {
                then();
            }
        });
    }

    // Replays everything from the start and checks it matches what we had.
    public void Verify()
    {
        if(Busy || CurrentMode == Mode.Video)
        {
            return;
        }
        int target = Mathf.Max(Frame, 1);
        DesyncFrame = -1;
        ReplayFromStart(target, () =>
        {
            if(DesyncFrame >= 0)
            {
                Say("Verify found a difference starting at frame " + DesyncFrame + ". The route now shows the exact result.", 10f);
            }
            else
            {
                Say("Verified: frames 0 to " + Frame + " are exact.", 6f);
            }
        });
    }

    public bool AllVerified
    {
        get
        {
            for(int f = 0; f <= Frame && f < Timeline.Count; f++)
            {
                if(!Timeline[f].Verified)
                {
                    return false;
                }
            }
            return true;
        }
    }

    // ---------- Editing inputs ----------

    public void SetInput(int frame, Buttons b)
    {
        if(frame < 1 || Busy)
        {
            return;
        }
        if(Movie.Get(frame) == b && frame <= Movie.Length)
        {
            return;
        }
        Remember("change frame " + frame);
        int keep = Frame;
        InvalidateFrom(frame, true);
        Movie.Set(frame, b);
        MovieDirty = true;
        ResimulateTo(keep, frame);
    }

    // Several edits at once (painting in the input grid), replayed once at the end.
    public void SetInputs(IDictionary<int, Buttons> edits)
    {
        if(Busy || edits.Count == 0)
        {
            return;
        }
        int min = int.MaxValue;
        foreach(KeyValuePair<int, Buttons> e in edits)
        {
            if(e.Key >= 1 && (Movie.Get(e.Key) != e.Value || e.Key > Movie.Length) && e.Key < min)
            {
                min = e.Key;
            }
        }
        if(min == int.MaxValue)
        {
            return;
        }
        int max = 0;
        foreach(int f in edits.Keys)
        {
            max = Mathf.Max(max, f);
        }
        Remember(max > min ? "paint frames " + min + "-" + max : "change frame " + min);
        int keep = Frame;
        InvalidateFrom(min, true);
        foreach(KeyValuePair<int, Buttons> e in edits)
        {
            if(e.Key >= 1)
            {
                Movie.Set(e.Key, e.Value);
            }
        }
        MovieDirty = true;
        ResimulateTo(keep, min);
    }

    public void InsertFrame(int frame)
    {
        InsertFrames(frame, new List<Buttons> { Movie.Get(frame) });
    }

    public void DeleteFrame(int frame)
    {
        DeleteFrames(frame, 1);
    }

    // Puts inputs in before "frame"; everything after moves later (keeps the rest of the run lined up).
    public void InsertFrames(int frame, IList<Buttons> inputs)
    {
        if(frame < 1 || inputs.Count == 0 || Busy)
        {
            return;
        }
        Remember(inputs.Count == 1 ? "insert a frame at " + frame : "insert " + inputs.Count + " frames at " + frame);
        int keep = Frame;
        InvalidateFrom(frame, true);
        Movie.InsertRange(frame, inputs);
        MovieDirty = true;
        ResimulateTo(keep, frame);
    }

    // Removes frames; everything after moves earlier.
    public void DeleteFrames(int frame, int count)
    {
        if(frame < 1 || frame > Movie.Length || count < 1 || Busy)
        {
            return;
        }
        Remember(count == 1 ? "delete frame " + frame : "delete " + count + " frames at " + frame);
        int keep = Frame;
        InvalidateFrom(frame, true);
        Movie.DeleteRange(frame, count);
        MovieDirty = true;
        ResimulateTo(keep, frame);
    }

    // Swaps frames frame..frame+removeCount-1 for new inputs. If the new piece is shorter or longer,
    // everything after moves to match.
    public void ReplaceFrames(int frame, int removeCount, IList<Buttons> inputs)
    {
        if(frame < 1 || Busy)
        {
            return;
        }
        Remember("paste " + inputs.Count + " frames at " + frame);
        int keep = Frame;
        InvalidateFrom(frame, true);
        if(removeCount > 0 && frame <= Movie.Length)
        {
            Movie.DeleteRange(frame, removeCount);
        }
        Movie.InsertRange(frame, inputs);
        MovieDirty = true;
        ResimulateTo(keep, frame);
    }

    // ---------- Undo and redo ----------
    // Before every change to the movie a copy of it is kept. Undo puts the copy back and replays from the
    // first frame that's different, so you see the old result straight away.

    private class UndoStep
    {
        public TasMovie Movie;
        public string Name;
        public string What;
    }

    public const int MaxUndo = 300;
    private readonly List<UndoStep> undoSteps = new List<UndoStep>();
    private readonly List<UndoStep> redoSteps = new List<UndoStep>();
    private bool recordingRemembered;

    public int UndoCount { get { return undoSteps.Count; } }
    public int RedoCount { get { return redoSteps.Count; } }
    public string UndoWhat { get { return undoSteps.Count > 0 ? undoSteps[undoSteps.Count - 1].What : ""; } }
    public string RedoWhat { get { return redoSteps.Count > 0 ? redoSteps[redoSteps.Count - 1].What : ""; } }

    // Call just before changing the movie
    private void Remember(string what)
    {
        UndoStep step = new UndoStep();
        step.Movie = Movie.Clone();
        step.Name = MovieName;
        step.What = what;
        undoSteps.Add(step);
        if(undoSteps.Count > MaxUndo)
        {
            undoSteps.RemoveAt(0);
        }
        redoSteps.Clear();
        recordingRemembered = false;
    }

    public void Undo()
    {
        UndoRedo(undoSteps, redoSteps, true);
    }

    public void Redo()
    {
        UndoRedo(redoSteps, undoSteps, false);
    }

    private void UndoRedo(List<UndoStep> from, List<UndoStep> to, bool undo)
    {
        if(CurrentMode == Mode.Playing)
        {
            SetMode(Mode.Paused);
        }
        if(Busy || CurrentMode == Mode.Video || CurrentMode == Mode.VideoEndScreen)
        {
            return;
        }
        if(from.Count == 0)
        {
            Say(undo ? "Nothing to undo." : "Nothing to redo.");
            return;
        }
        UndoStep step = from[from.Count - 1];
        from.RemoveAt(from.Count - 1);
        UndoStep back = new UndoStep();
        back.Movie = Movie.Clone();
        back.Name = MovieName;
        back.What = step.What;
        to.Add(back);
        recordingRemembered = false;

        int diff = FirstDifference(Movie, step.Movie);
        int keep = Frame;
        if(diff >= 1)
        {
            InvalidateFrom(diff, true);
        }
        Movie = step.Movie.Clone();
        MovieName = step.Name;
        MovieDirty = true;
        Say((undo ? "Undo: " : "Redo: ") + step.What + (diff >= 1 ? " (inputs change from frame " + diff + ")" : "") + ".");
        if(diff >= 1)
        {
            ResimulateTo(keep, diff);
        }
    }

    // The first frame whose input is different, or -1 if every input is the same
    public static int FirstDifference(TasMovie a, TasMovie b)
    {
        int first = -1;
        int n = Math.Max(a.Length, b.Length);
        for(int f = 1; f <= n; f++)
        {
            if(a.Get(f) != b.Get(f))
            {
                first = f;
                break;
            }
        }
        if(!a.SameTeleport(b))
        {
            // A practice move changes everything from the end of its frame
            foreach(int t in new[] { a.TeleportFrame, b.TeleportFrame })
            {
                if(t >= 1 && (first < 0 || t < first)) first = t;
            }
        }
        return first;
    }

    // ---------- Copy, paste and presets ----------

    public readonly List<Buttons> Clipboard = new List<Buttons>();

    public static string PresetFolder
    {
        get { return Path.Combine(TasFolder, "Presets"); }
    }

    public void CopyFrames(int from, int count)
    {
        Clipboard.Clear();
        Clipboard.AddRange(Movie.GetRange(from, count));
        Say("Copied " + count + " frames (" + from + " to " + (from + count - 1) + "). Paste puts them in at the next frame, or over a selection.");
    }

    public void SavePreset(string name, IList<Buttons> inputs)
    {
        try
        {
            name = SanitizeName(name);
            TasMovie piece = new TasMovie();
            piece.SetRange(1, inputs);
            piece.Save(Path.Combine(PresetFolder, name + ".txt"));
            Say("Saved preset \"" + name + "\" (" + inputs.Count + " frames).");
        }
        catch(Exception ex)
        {
            Say("Could not save the preset: " + ex.Message, 8f);
        }
    }

    public List<Buttons> LoadPreset(string name)
    {
        try
        {
            TasMovie piece = TasMovie.Load(Path.Combine(PresetFolder, name + ".txt"));
            return piece.GetRange(1, piece.Length);
        }
        catch(Exception ex)
        {
            Say("Could not read the preset: " + ex.Message, 8f);
            return null;
        }
    }

    public string[] PresetNames()
    {
        try
        {
            Directory.CreateDirectory(PresetFolder);
            string[] files = Directory.GetFiles(PresetFolder, "*.txt");
            Array.Sort(files);
            for(int i = 0; i < files.Length; i++)
            {
                files[i] = Path.GetFileNameWithoutExtension(files[i]);
            }
            return files;
        }
        catch
        {
            return new string[0];
        }
    }

    public void ClearAfter(int frame)
    {
        if(Busy)
        {
            return;
        }
        if(frame + 1 <= Movie.Length)
        {
            Remember("clear everything after frame " + frame);
            InvalidateFrom(frame + 1, true);
            Movie.TruncateAfter(frame);
            MovieDirty = true;
        }
    }

    public void SetMarker(int frame, string text)
    {
        string old;
        Movie.Markers.TryGetValue(frame, out old);
        if((old ?? "") == (text ?? ""))
        {
            return;
        }
        Remember("label on frame " + frame);
        if(string.IsNullOrEmpty(text))
        {
            Movie.Markers.Remove(frame);
        }
        else
        {
            Movie.Markers[frame] = text;
        }
        MovieDirty = true;
    }

    // After an edit at "editFrame", go back before it and replay to where you were, so you see the new result.
    private void ResimulateTo(int keepFrame, int editFrame)
    {
        if(keepFrame >= editFrame)
        {
            Seek(keepFrame, null, true);
        }
    }

    // Forgets everything from this frame on (the inputs changed), keeping the old route as a ghost.
    public void InvalidateFrom(int frame, bool fromEdit)
    {
        if(frame < 1)
        {
            frame = 1;
        }
        if(GreenEnd >= frame && ghostArmed)
        {
            MakeGhost(frame);
            ghostArmed = false;
        }
        if(Timeline.Count > frame)
        {
            Timeline.RemoveRange(frame, Timeline.Count - frame);
            RoutePositions.RemoveRange(frame, RoutePositions.Count - frame);
            RouteVersion++;
        }
        for(int f = frame; f < snapshots.Count; f++)
        {
            if(snapshots[f] != null)
            {
                SnapshotBytes -= snapshots[f].ApproxBytes;
                snapshots[f] = null;
            }
        }
        if(DeathFrame >= frame) DeathFrame = -1;
        if(FinishFrame >= frame) FinishFrame = -1;
        if(DesyncFrame >= frame) DesyncFrame = -1;
    }

    private static readonly Color[] GhostColors =
    {
        new Color(0.35f, 0.75f, 1f), new Color(1f, 0.55f, 0.85f), new Color(0.6f, 1f, 0.45f),
        new Color(1f, 0.75f, 0.3f), new Color(0.75f, 0.6f, 1f), new Color(0.4f, 1f, 0.9f)
    };

    public TasGhost MakeGhost(int branchFrame, string name = null)
    {
        TasGhost g = new TasGhost();
        ghostCounter++;
        g.Name = name ?? ("Try " + ghostCounter);
        g.Inputs = Movie.Clone();
        g.MovieName = MovieName;
        g.Color = GhostColors[(ghostCounter - 1) % GhostColors.Length];
        g.BranchFrame = branchFrame;
        foreach(TasFrameRecord r in Timeline)
        {
            g.Pos.Add(r.Pos);
            g.Vel.Add(r.Vel);
        }
        AddGhost(g);
        return g;
    }

    // ---------- Practice: put Pete somewhere ----------

    // At the end of the current frame Pete is put at "to", standing still. It's saved in the movie, so replays and
    // going back always do the same. Only for practice: it isn't something the real game can do.
    public void SetPracticeTeleport(Vector2 to)
    {
        if(Busy || CurrentMode == Mode.Video || CurrentMode == Mode.VideoEndScreen)
        {
            return;
        }
        if(CurrentMode == Mode.Playing)
        {
            SetMode(Mode.Paused);
        }
        int at = Frame;
        if(at < 1)
        {
            Say("Step forward one frame first (Pete can be moved from frame 1 on).");
            return;
        }
        Remember("move Pete (practice) on frame " + at);
        int first = Movie.TeleportFrame >= 1 ? Mathf.Min(Movie.TeleportFrame, at) : at;
        InvalidateFrom(first, true);
        Movie.TeleportFrame = at;
        Movie.TeleportTo = to;
        MovieDirty = true;
        Say("PRACTICE: Pete is put at " + to.x.ToString("0.00") + ", " + to.y.ToString("0.00") + " on frame " + at + ". This movie is now a practice run (the TAS shows PRACTICE at the top).", 8f);
        Seek(at, null, true);
    }

    public void ClearPracticeTeleport()
    {
        if(Busy || Movie.TeleportFrame < 1)
        {
            return;
        }
        Remember("stop moving Pete (practice)");
        int at = Movie.TeleportFrame;
        int keep = Frame;
        InvalidateFrom(at, true);
        Movie.TeleportFrame = -1;
        MovieDirty = true;
        Say("Pete isn't moved any more: this movie is a real run again.");
        ResimulateTo(keep, at);
    }

    public void ApplyTeleport(Vector2 to)
    {
        Rigidbody2D rb = Player.Rb;
        Transform t = Player.Go.transform;
        t.position = new Vector3(to.x, to.y, t.position.z);
        rb.position = to;
        rb.velocity = Vector2.zero;
        Physics2D.SyncTransforms();
    }

    // Makes an older try your current route again. The route you have now is kept as a try, so nothing is lost
    // (and Undo works too).
    public void UseTry(TasGhost g)
    {
        if(g == null || g.Inputs == null || CurrentMode == Mode.Video || CurrentMode == Mode.VideoEndScreen)
        {
            return;
        }
        if(CurrentMode == Mode.Playing)
        {
            SetMode(Mode.Paused);
        }
        if(Busy)
        {
            return;
        }
        int diff = FirstDifference(Movie, g.Inputs);
        if(diff < 1)
        {
            Ghosts.Remove(g);
            Say(g.Name + " has exactly the same inputs as your route now, so nothing changed.");
            return;
        }
        Remember("go back to " + g.Name);
        int keep = Frame;
        TasGhost now = MakeGhost(diff);
        ghostArmed = false;
        InvalidateFrom(diff, true);
        Ghosts.Remove(g);
        Movie = g.Inputs.Clone();
        MovieDirty = true;
        Say("Went back to " + g.Name + " (its inputs are different from frame " + diff + "). The route you had is kept as " + now.Name + ".", 7f);
        ResimulateTo(keep, diff);
    }

    // Saves an older try's inputs as a movie file (Movie tab), so it's kept for good
    public void SaveTry(TasGhost g)
    {
        if(g == null || g.Inputs == null)
        {
            return;
        }
        string name = SanitizeName((string.IsNullOrEmpty(g.MovieName) ? MovieName : g.MovieName) + " " + g.Name.ToLowerInvariant());
        try
        {
            g.Inputs.Save(MoviePath(name));
            Say("Saved " + g.Name + " as the movie \"" + name + "\" (load it from the Movie tab).", 6f);
        }
        catch(Exception ex)
        {
            Say("Could not save: " + ex.Message, 8f);
        }
    }

    // The first frame where an older try goes a different way from the route you have now (-1 = same so far)
    public int SplitFrame(TasGhost g)
    {
        if(g.SplitVersion == RouteVersion && g.SplitCount == RoutePositions.Count)
        {
            return g.Split;
        }
        int n = Mathf.Min(g.Pos.Count, RoutePositions.Count);
        int split = -1;
        for(int f = 1; f < n; f++)
        {
            if((g.Pos[f] - RoutePositions[f]).sqrMagnitude > 1e-8f)
            {
                split = f;
                break;
            }
        }
        g.Split = split;
        g.SplitVersion = RouteVersion;
        g.SplitCount = RoutePositions.Count;
        return split;
    }

    public void AddGhost(TasGhost g)
    {
        Ghosts.Insert(0, g);
        int unpinned = 0;
        for(int i = 0; i < Ghosts.Count; i++)
        {
            if(!Ghosts[i].Pinned && ++unpinned > MaxGhosts)
            {
                Ghosts.RemoveAt(i);
                i--;
            }
        }
    }

    public int NextGhostNumber()
    {
        return ++ghostCounter;
    }

    // ---------- Files ----------

    public string MoviePath(string name)
    {
        if(!name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            name += ".txt";
        }
        return Path.Combine(TasFolder, name);
    }

    public void SaveMovie(string name)
    {
        try
        {
            name = SanitizeName(name);
            Movie.Save(MoviePath(name));
            MovieName = name;
            MovieDirty = false;
            Autosave(true);
            Say("Saved " + name + ".txt in the TAS Files folder.");
        }
        catch(Exception ex)
        {
            Say("Could not save: " + ex.Message, 8f);
        }
    }

    public void LoadMovie(string name)
    {
        if(Busy || CurrentMode == Mode.Video)
        {
            return;
        }
        try
        {
            TasMovie loaded = TasMovie.Load(MoviePath(name));
            Remember("load " + Path.GetFileNameWithoutExtension(name));
            int keep = Frame;
            InvalidateFrom(1, true);
            Movie = loaded;
            MovieName = Path.GetFileNameWithoutExtension(name);
            MovieDirty = false;
            Say("Loaded " + MovieName + " (" + Movie.Length + " frames).");
            ReplayFromStart(Mathf.Min(keep, Movie.Length));
        }
        catch(Exception ex)
        {
            Say("Could not load: " + ex.Message, 8f);
        }
    }

    // Swaps in a different movie and replays it from the start up to "goTo".
    public void ReplaceMovie(TasMovie movie, string name, int goTo)
    {
        if(Busy || CurrentMode == Mode.Video)
        {
            return;
        }
        Remember("switch to " + name);
        InvalidateFrom(1, true);
        Movie = movie;
        MovieName = name;
        MovieDirty = true;
        ReplayFromStart(Mathf.Min(goTo, Movie.Length));
    }

    public void NewMovie()
    {
        if(Busy || CurrentMode == Mode.Video)
        {
            return;
        }
        Remember("new empty movie");
        InvalidateFrom(1, true);
        Movie = new TasMovie();
        MovieName = "pete-tas";
        MovieDirty = true;
        ReplayFromStart(0);
    }

    public static string SanitizeName(string name)
    {
        if(string.IsNullOrEmpty(name))
        {
            return "pete-tas";
        }
        foreach(char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        if(name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            name = name.Substring(0, name.Length - 4);
        }
        return name.Trim().Length == 0 ? "pete-tas" : name.Trim();
    }

    private void Autosave(bool force)
    {
        autosaveAt = Time.unscaledTime + 20f;
        if(!force && !MovieDirty)
        {
            return;
        }
        if(SelfTestMode)
        {
            return;
        }
        try
        {
            Movie.Save(Path.Combine(TasFolder, "autosave.txt"));
        }
        catch(Exception ex)
        {
            Debug.LogWarning("[TAS] Autosave failed: " + ex.Message);
        }
    }

    // ---------- Video playback ----------

    // export = also save the playback as a video file
    public void StartVideo(bool export = false)
    {
        if(Busy || CurrentMode == Mode.Video)
        {
            return;
        }
        if(Movie.Length < 1)
        {
            Say("The movie is empty. Make some inputs first.");
            return;
        }
        if(export && TasVideoExport.I != null && (TasVideoExport.I.Exporting || TasVideoExport.I.Finishing))
        {
            Say("The last video is still being saved. Try again in a moment.");
            return;
        }
        videoReturnFrame = Frame;
        VideoExporting = export;
        ReplayFromStart(0, () =>
        {
            TasHooks.AllowFinishSceneLoad = VideoEndScreen;
            SetMode(Mode.Video);
            if(VideoExporting && TasVideoExport.I != null)
            {
                // Run as fast as the computer can; every frame is still saved, the clock is the TAS's own
                Application.targetFrameRate = -1;
                TasVideoExport.I.Begin(MovieName);
            }
        });
    }

    public void StopVideo()
    {
        if(CurrentMode == Mode.VideoEndScreen)
        {
            ReturnFromVideo();
            return;
        }
        if(CurrentMode != Mode.Video)
        {
            return;
        }
        TasHooks.AllowFinishSceneLoad = false;
        SetMode(Mode.Paused);
        EndExport();
    }

    private void EndExport()
    {
        if(VideoExporting && TasVideoExport.I != null)
        {
            TasVideoExport.I.RequestStop();
        }
        VideoExporting = false;
    }

    private void ReturnFromVideo()
    {
        EndExport();
        TasHooks.AllowFinishSceneLoad = false;
        Time.timeScale = 0f;
        ReplayFromStart(videoReturnFrame);
    }

    // ---------- Auto-search ----------

    public void StartSearch(TasSearch search)
    {
        if(Busy || CurrentMode == Mode.Video)
        {
            return;
        }
        if(Frame < 1 || GetSnapshot(Frame) == null || GetSnapshot(Frame - 1) == null)
        {
            Say("Step forward at least one frame before starting a search.");
            return;
        }
        Search = search;
        searchPrevious = Movie.Get(Frame);
        searchRoutine = search.Run(this);
        BusyText = "Searching";
        BusyProgress = 0f;
        SetMode(Mode.Searching);
    }

    public void CancelSearch()
    {
        if(CurrentMode == Mode.Searching && Search != null)
        {
            Search.Cancelled = true;
        }
    }

    private void FinishSearch()
    {
        SetMode(Mode.Paused);
        TasSearch s = Search;
        // Put the game back where the search started
        int start = s != null ? s.StartFrame : Frame;
        Seek(start);
        if(s != null)
        {
            s.Finished(this);
        }
    }

    // Writes inputs into the movie (used by "Apply" in the search results) and checks them with an exact replay.
    public void ApplyInputs(int firstFrame, IList<Buttons> inputs, Action then = null)
    {
        if(Busy || inputs.Count == 0)
        {
            return;
        }
        Remember("apply the search result");
        InvalidateFrom(firstFrame, true);
        for(int i = 0; i < inputs.Count; i++)
        {
            Movie.Set(firstFrame + i, inputs[i]);
        }
        MovieDirty = true;
        int target = firstFrame + inputs.Count - 1;
        ReplayFromStart(target, then);
    }

    // ---------- Helpers ----------

    public static string FormatTime(float seconds)
    {
        int minutes = (int)seconds / 60;
        int secs = (int)seconds % 60;
        int hundredths = (int)(seconds * 100) % 100;
        return string.Format("{0:00}:{1:00}:{2:00}", minutes, secs, hundredths);
    }

    public static string FormatFrames(int frames)
    {
        float s = frames * Tick;
        int minutes = (int)(s / 60);
        float rest = s - minutes * 60;
        return string.Format("{0}:{1:00.00}", minutes, rest);
    }
}

// Runs before every game script in each physics step, so the TAS can hand out that frame's inputs first.
[DefaultExecutionOrder(-30000)]
public class TasEarly : MonoBehaviour
{
    private void FixedUpdate()
    {
        if(TasManager.I != null)
        {
            TasManager.I.OnFixedStepStart();
        }
    }
}
