using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using Buttons = PeteInput.Buttons;

// One predicted path, drawn as a line in the level.
public class TasPrediction
{
    public string Name;
    public Color Color;
    public float Width = 2.5f;
    // Pos[0] is where Pete is now, Pos[j] is where he'll be j frames later
    public readonly List<Vector2> Pos = new List<Vector2>();
    public readonly List<Vector2> Vel = new List<Vector2>();
    public readonly List<bool> Ground = new List<bool>();
    public readonly List<int> Landings = new List<int>();
    public readonly List<int> Hits = new List<int>();
    public int JumpAt = -1;
    public int DeathAt = -1;
    // Frames where Pete is in knockback (pushed by a hit and/or can't move)
    public readonly List<bool> Knock = new List<bool>();
    // Text shown next to the line ("HIT", "knockback over (10 f)", "lands", "stops here")
    public readonly List<KeyValuePair<int, string>> Labels = new List<KeyValuePair<int, string>>();

    public void AddLabel(int index, string text)
    {
        Labels.Add(new KeyValuePair<int, string>(index, text));
    }

    public bool SameAs(TasPrediction o, float tolerance)
    {
        if(o == null || o.Pos.Count != Pos.Count)
        {
            return false;
        }
        for(int i = 0; i < Pos.Count; i++)
        {
            if((Pos[i] - o.Pos[i]).sqrMagnitude > tolerance * tolerance)
            {
                return false;
            }
        }
        return true;
    }
}

// Prediction lines, like Geometry Dash's "show trajectory": every frame, a stand-in Pete runs ahead
// in a hidden copy of the level and the paths it takes are drawn as lines.
//
// The stand-in uses the same physics engine as the game (in its own little physics world with copies of the
// level's walls, floors, spikes and enemies), and Pete's own movement rules (Move, Jump, Ground, knockback)
// copied step for step. So the green line is where the real Pete will go, without touching the real game.
[DefaultExecutionOrder(30500)]
public class TasPredict : MonoBehaviour
{
    public static TasPredict I;

    // ---------- Settings (View tab) ----------
    public static bool Show = true;
    public static bool ShowPath = true;
    // The green line follows the movie to the end of its inputs, then lets go until Pete stops
    public static bool PathToEnd;
    public static bool ShowLongJump = true;
    public static bool ShowShortJump = true;
    public static bool ShowLetGo = true;
    public static bool ShowHits = true;
    public static bool ShowAttacks = true;
    // Knockback highlighted on the lines, with labels saying how long it lasts and where Pete lands or stops
    public static bool ShowKnockback = true;
    public static bool ShowInVideo;
    public static int Frames = 75;
    public const float HitRange = 6f;
    public const float PogoRange = 4f;
    public const int MaxHitLines = 4;
    public const float SwordReach = 6f;
    // When Pete stops touching one thing and keeps touching another on the same frame, which does the game
    // hear about first? (Picked by the self-test.)
    public static bool ExitsFirst = true;
    // How the hidden world's contacts are rebuilt before each prediction: one normal-length physics step from the
    // frame before (true), or one tiny step (false). Picked by the self-test.
    public static bool FullPrime = true;

    public static readonly Color PathColor = new Color(0.35f, 1f, 0.4f);
    public static readonly Color HoldColor = new Color(0.35f, 0.8f, 1f);
    public static readonly Color TapColor = new Color(0.8f, 0.5f, 1f);
    public static readonly Color ReleaseColor = new Color(1f, 0.9f, 0.3f);
    public static readonly Color HitColor = new Color(1f, 0.32f, 0.32f);
    public static readonly Color PogoColor = new Color(1f, 0.55f, 0.15f);
    public static readonly Color AttackColor = new Color(1f, 0.55f, 0.15f);
    public static readonly Color AttackUpColor = new Color(1f, 0.45f, 0.8f);
    public static readonly Color AttackDownColor = new Color(0.95f, 0.8f, 0.45f);

    // ---------- Results ----------
    public readonly List<TasPrediction> Lines = new List<TasPrediction>();
    public TasPrediction Path { get; private set; }
    public float LastMs;
    // How far the green line is from the real route, on frames where the real route is already known
    public float PathError = -1f;
    public int PathErrorFrames;
    public Vector2 BoxCenter;   // Pete's hitbox, relative to his position
    public Vector2 BoxSize;
    public float FootY;         // from Pete's position down to his feet
    public string Problem = "";

    private string lastKey;

    // For the self-test: write down what nearby enemies do in the first few frames of a prediction
    public bool Trace;
    public readonly System.Text.StringBuilder TraceLog = new System.Text.StringBuilder();

    private void TraceStep(int j)
    {
        Vector2 pp = dummy.position;
        TraceLog.Append("    +" + j + " pete " + pp.ToString("F4") + " vel " + dummy.velocity.ToString("F4") + " ground " + s.OnGround + "\n");
        foreach(Foe f in foes)
        {
            if(f.Simulated && Vector2.Distance(f.Pos, pp) < 4f)
            {
                TraceLog.Append("       " + f.Name + " pos " + f.Pos.ToString("F4") + " z " + f.Z.ToString("F3") + (f.Body != null ? " body " + f.Body.position.ToString("F4") + " vel " + f.Body.velocity.ToString("F4") + " " + f.Body.bodyType : " (fixed)")
                    + " sign " + f.Sign + " chasing " + f.Chasing + " cooldown " + f.Cooldown.ToString("0.00") + "\n");
            }
        }
    }

    // The real enemies near Pete right now (to compare with the trace)
    public string DescribeRealFoes()
    {
        TasManager m = TasManager.I;
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        Vector2 pp = m.Player.Position;
        sb.Append("    real pete " + pp.ToString("F4") + " vel " + m.Player.Velocity.ToString("F4") + " ground " + m.Player.OnGround + "\n");
        foreach(Foe f in foes)
        {
            if(f.Go == null || !f.Go.activeInHierarchy)
            {
                continue;
            }
            Vector3 tp = f.Go.transform.position;
            if(Vector2.Distance(tp, pp) < 4f)
            {
                sb.Append("       " + f.Name + " transform " + tp.ToString("F4") + (f.Real != null ? " body " + f.Real.position.ToString("F4") + " vel " + f.Real.velocity.ToString("F4") + " " + f.Real.bodyType : "")
                    + " scale " + f.Go.transform.localScale.x.ToString("0.00") + " chasing " + (f.Chaser != null && Read<bool>(f.Chaser, "chasing")) + " cooldown " + Read<float>(f.Attack, "currentAttackDelay").ToString("0.00") + "\n");
            }
        }
        return sb.ToString();
    }

    // ---------- The hidden copy of the level ----------
    private Scene world;
    private PhysicsScene2D physics;
    private GameObject builtFor;
    private int builds;
    private Rigidbody2D dummy;
    private GameObject levelHost;
    private readonly List<Copy> peteCopies = new List<Copy>();
    private readonly List<Copy> allCopies = new List<Copy>();
    private readonly Dictionary<int, Copy> byId = new Dictionary<int, Copy>();
    private readonly List<Foe> foes = new List<Foe>();
    private readonly List<Waiting> waiting = new List<Waiting>();
    private int peteBuiltSign = 1;
    private int peteSign = 1;

    // ---------- Pete's sword ----------
    // Frames after the attack press when the sword comes out and goes away again, measured in the game
    // (they match the attack animations' events). Index: 1 = forward, 2 = down, 3 = up.
    private static readonly int[] AttackOn = { 0, 4, 5, 5 };
    private static readonly int[] AttackOff = { 0, 11, 11, 12 };
    private readonly Copy[] weaponCopies = new Copy[4];
    private readonly bool[] weaponOn0 = new bool[4];
    private readonly Vector3[] weaponLocal = new Vector3[4];
    private int weaponDamage = 1;
    private float peteScaleX0, peteScaleY0, peteScaleZ0, peteScaleX, peteScaleZ;
    private HashSet<long> prevSword = new HashSet<long>();
    private HashSet<long> curSword = new HashSet<long>();
    private readonly List<Foe> killed = new List<Foe>();
    private static readonly FieldInfo animationStateField = typeof(PlayerAnimation).GetField("animationState", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

    private class Copy
    {
        public Collider2D Original;
        public CustomCollider2D Col;
        public PhysicsShapeGroup2D Shapes;
        public PhysicsShapeGroup2D Mirrored;
        public bool IsMirrored;
        public bool IsPete;
        public Foe Foe;
        // Pete's sword: 1 = forward, 2 = down, 3 = up (0 = not a sword)
        public int WeaponType;
        // The original's object has EnemyHealth, so the sword can hurt it
        public bool HasHealth;
        // What the game's Ground script reads when Pete touches this
        public float Friction;
        // The original has no Rigidbody2D, so the game's Ground script fails to read a friction and keeps the old one
        public bool FrictionFails;
    }

    // A collider that had no shapes yet (switched off, or in a room that isn't active); copied once it has
    private class Waiting
    {
        public Collider2D Original;
        public GameObject Host;
        public bool ToWorld;
        public bool IsPete;
        public Foe Foe;
    }

    // An enemy or spikes: anything with an EnemyAttack that can hurt Pete
    private class Foe
    {
        public string Name;
        public GameObject Go;
        public EnemyAttack Attack;
        public EnemyWalking Walker;
        public EnemyChase Chaser;
        public EnemyHealth Health;
        public KnockbackTrigger Knock;
        public FlipSprite Flip;
        public Rigidbody2D Real;
        // The copy's body (the same kind as the real one), or null for spikes that never move
        public Rigidbody2D Body;
        public readonly List<Copy> Copies = new List<Copy>();
        public int BuiltSign = 1;
        public bool CanHurt;
        // Read from the game before each set of predictions
        public bool Alive0;
        public bool Near;
        public Vector2 Pos0;
        public Vector2 Vel0;
        public float Rot0;
        public bool Awake0;
        // When the enemy's own knockback (from Pete's sword) ends
        public readonly List<double> Clears0 = new List<double>();
        public readonly List<double> Clears = new List<double>();
        public float Z0;
        public float Cooldown0;
        public int Dir0;
        public bool Chasing0;
        public int Sign0;
        public float Speed;
        public bool GroundChaser;
        public int Damage;
        public float AttackDelay;
        public float StandardScale = 1f;
        public bool Pogoable;
        public float PogoSpeed;
        public float Distance;
        // EnemyHealth and the enemy's own knockback (when Pete's sword hits it)
        public int Health0;
        public bool Damageable;
        public bool IsEnemy;
        public float InvulTime;
        public bool Hit0;
        public readonly List<double> HitClears0 = new List<double>();
        public float KbVel;
        public float KbTime;
        public int HealthLeft;
        public bool Hit;
        public readonly List<double> HitClears = new List<double>();
        public bool Dead;
        // During one prediction
        public Vector2 Pos;
        public float Z;
        public float Cooldown;
        public int Dir;
        public bool Chasing;
        public int Sign;
        public bool Moved;   // a script moved it this frame, so the physics body jumps there next step
        public bool WatchTriggers;   // close enough that its trigger zones can matter for this line

        public bool Moves { get { return Body != null; } }
        // Only enemies that could reach Pete in time take part, so far-off rooms cost nothing
        public bool Simulated { get { return Alive0 && Near && !Dead; } }
    }

    // Pete's script values that change from frame to frame
    private class SimPete
    {
        public Vector2 DesiredVel;
        public bool CanMove;
        public int JumpPhase;
        public float Coyote;
        public float Buffer;
        public bool DesiredJump;
        public bool IsJumping;
        public bool OnGround;
        public float Friction;
        public int Hp;
        public float KbCounter;
        public readonly List<double> Clears = new List<double>();
        // MeleeAttackManager.canAttack, and the attack animation in progress (type, frame it was pressed)
        public bool CanAttack;
        public int AttackType;
        public int AttackStart;

        public void CopyFrom(SimPete o)
        {
            DesiredVel = o.DesiredVel;
            CanMove = o.CanMove;
            JumpPhase = o.JumpPhase;
            Coyote = o.Coyote;
            Buffer = o.Buffer;
            DesiredJump = o.DesiredJump;
            IsJumping = o.IsJumping;
            OnGround = o.OnGround;
            Friction = o.Friction;
            Hp = o.Hp;
            KbCounter = o.KbCounter;
            CanAttack = o.CanAttack;
            AttackType = o.AttackType;
            AttackStart = o.AttackStart;
            Clears.Clear();
            Clears.AddRange(o.Clears);
        }
    }

    // Pete's settings (read from his scripts, so they always match the game)
    private float maxSpeed, maxAcc, maxAirAcc, jumpHeight, downMul, upMul, coyoteTime, bufferTime, defaultGs;
    private float kbTimeHealth, kbTimeTrigger, kbVelocity, peteStandardScale;
    private int maxAirJumps;
    // Pete right now
    private readonly SimPete start = new SimPete();
    private readonly SimPete s = new SimPete();
    private Vector2 pos0, vel0, prevPos, prevVel;
    private Collider2D peteBody;
    private float gs0, peteZ;
    private bool awake0;
    private int peteSign0;
    private double frameTime0, physicsOffset, tickTime;

    private ContactFilter2D everything;
    private readonly ContactPoint2D[] points = new ContactPoint2D[64];
    private readonly Collider2D[] touching = new Collider2D[64];
    private HashSet<long> prevSolid = new HashSet<long>();
    private HashSet<long> curSolid = new HashSet<long>();
    private HashSet<long> prevTrig = new HashSet<long>();
    private HashSet<long> curTrig = new HashSet<long>();
    private readonly HashSet<long> groundPairs = new HashSet<long>();
    private readonly Dictionary<long, Copy> pairOther = new Dictionary<long, Copy>();
    private readonly List<long> scratch = new List<long>();

    private void Awake()
    {
        I = this;
        everything = new ContactFilter2D();
        everything = everything.NoFilter();
    }

    private void OnDestroy()
    {
        if(I == this)
        {
            I = null;
        }
        DestroyWorld();
    }

    // Should the lines be worked out (and drawn) right now?
    public static bool Active(TasManager m)
    {
        if(!Show || m == null || m.Player == null || !m.Player.Valid)
        {
            return false;
        }
        switch(m.CurrentMode)
        {
            case TasManager.Mode.Paused:
            case TasManager.Mode.Playing:
                return true;
            case TasManager.Mode.Video:
                return ShowInVideo;
        }
        return false;
    }

    private void LateUpdate()
    {
        TasManager m = TasManager.I;
        if(!Active(m))
        {
            return;
        }
        if(TasHooks.PlayerDied || TasHooks.Finished)
        {
            Lines.Clear();
            Path = null;
            lastKey = null;
            return;
        }
        string key = Key(m);
        if(key == lastKey)
        {
            return;
        }
        lastKey = key;
        ComputeNow();
    }

    // Everything the lines depend on. When none of it changes, the lines are kept as they are.
    private string Key(TasManager m)
    {
        int h = 17;
        int f = m.Frame;
        for(int j = 0; j <= Frames; j++)
        {
            h = h * 31 + (int)m.Movie.Get(f + j);
        }
        bool live = Live(m);
        Vector2 p = m.Player.Position, v = m.Player.Velocity;
        return f + "|" + m.Restores + "|" + m.Timeline.Count + "|" + m.Movie.Length + "|" + h + "|" + p.x.ToString("R") + "," + p.y.ToString("R") + "|" + v.x.ToString("R") + "," + v.y.ToString("R")
            + "|" + (live ? (int)TasManager.ReadPlayerInput() : -1) + "|" + Frames + ShowPath + PathToEnd + ShowLongJump + ShowShortJump + ShowLetGo + ShowHits + ShowAttacks + ShowKnockback + ExitsFirst + FullPrime;
    }

    // While recording, the line follows the keys you're holding right now
    private static bool Live(TasManager m)
    {
        return m.Recording && (m.CurrentMode == TasManager.Mode.Paused || m.CurrentMode == TasManager.Mode.Playing);
    }

    // =====================================================================
    //  Working out the lines
    // =====================================================================

    public void ComputeNow()
    {
        TasManager m = TasManager.I;
        float t0 = Time.realtimeSinceStartup;
        Lines.Clear();
        Path = null;
        PathError = -1f;
        PathErrorFrames = 0;
        try
        {
            if(m == null || m.Player == null || !m.Player.Valid || !EnsureWorld(m))
            {
                return;
            }
            ReadStart(m);
            int n = Mathf.Max(1, Frames);
            bool live = Live(m);
            // "To the end of the inputs": the whole rest of the movie, then nothing held until Pete stops
            int inputsEnd = -1;
            int pathN = n;
            if(PathToEnd && !live)
            {
                inputsEnd = Mathf.Max(0, m.Movie.Length - m.Frame);
                pathN = Mathf.Clamp(inputsEnd + 150, n, m.CurrentMode == TasManager.Mode.Paused ? 3000 : 400);
            }
            Buttons[] path = PathInputs(m, pathN, live, PathToEnd);

            TasPrediction green = Run(path, pathN, null, null, live ? "Your path (keys you're holding)" : "Your path", PathColor, inputsEnd, true);
            green.Width = 3.5f;
            Path = green;
            if(ShowPath)
            {
                Lines.Add(green);
            }
            if(!live)
            {
                MeasurePathError(m, green);
            }

            Buttons dirs = path[1] & (Buttons.Left | Buttons.Right | Buttons.Up | Buttons.Down);
            bool holdingJump = (path[0] & Buttons.Jump) != 0;
            if(ShowLongJump)
            {
                // Jump held from the next frame on: the highest, longest jump
                AddUnlessSame(Run(Constant(path[0], dirs | Buttons.Jump, n), n, null, null, "Longest jump (hold jump)", HoldColor), green);
            }
            if(ShowShortJump && n >= 2)
            {
                // Jump pressed for a single frame: the lowest hop (if jump is held now, it's let go first)
                Buttons[] tap = Constant(path[0], dirs, n);
                tap[holdingJump ? 2 : 1] |= Buttons.Jump;
                AddUnlessSame(Run(tap, n, null, null, "Shortest jump (tap jump)", TapColor), green);
            }
            if(ShowLetGo)
            {
                AddUnlessSame(Run(Constant(path[0], dirs, n), n, null, null, "Let go of jump", ReleaseColor), green);
            }
            // Attack lines only when something the sword could hit is close enough to matter
            bool inReach = false;
            foreach(Foe f in foes)
            {
                if(f.Simulated && f.Health != null && f.Distance <= SwordReach)
                {
                    inReach = true;
                }
            }
            if(ShowAttacks && start.CanAttack && inReach)
            {
                // Attacking on the next frame (only shown when the swing hits something and changes the route)
                AddUnlessSame(Run(AttackNow(path, n, Buttons.None), n, null, null, "Attack now", AttackColor), green);
                AddUnlessSame(Run(AttackNow(path, n, Buttons.Up), n, null, null, "Attack up now", AttackUpColor), green);
                if(!start.OnGround)
                {
                    AddUnlessSame(Run(AttackNow(path, n, Buttons.Down), n, null, null, "Attack down now (pogo)", AttackDownColor), green);
                }
            }
            if(ShowHits)
            {
                List<Foe> near = new List<Foe>();
                foreach(Foe f in foes)
                {
                    if(f.Alive0 && f.CanHurt && f.Cooldown0 <= 0 && f.Distance <= HitRange)
                    {
                        near.Add(f);
                    }
                }
                near.Sort((a, b) => a.Distance.CompareTo(b.Distance));
                // While the game plays in real time only the closest one, so every frame stays quick
                int most = m.CurrentMode == TasManager.Mode.Paused ? MaxHitLines : 1;
                for(int i = 0; i < near.Count && i < most; i++)
                {
                    TasPrediction hit = Run(path, n, near[i], null, near[i].Name == "spikes" ? "If you touch the spikes" : "If the " + near[i].Name + " hits you", HitColor);
                    hit.Width = 2f;
                    Lines.Add(hit);
                }
            }
            Problem = "";
        }
        catch(Exception ex)
        {
            Debug.LogException(ex);
            Problem = ex.Message;
            Lines.Clear();
            Path = null;
        }
        finally
        {
            LastMs = (Time.realtimeSinceStartup - t0) * 1000f;
        }
    }

    // Times the parts of a prediction step (used by the self-test)
    public string Benchmark()
    {
        TasManager m = TasManager.I;
        if(m == null || m.Player == null || !m.Player.Valid || !EnsureWorld(m))
        {
            return "no world";
        }
        ReadStart(m);
        const int steps = 300;
        System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();
        Func<double> sim = () =>
        {
            dummy.position = pos0;
            dummy.velocity = new Vector2(3f, 0f);
            sw.Reset();
            sw.Start();
            for(int i = 0; i < steps; i++)
            {
                physics.Simulate(Time.fixedDeltaTime);
            }
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds * 1000.0 / steps;
        };
        double all = sim();
        sw.Reset();
        sw.Start();
        for(int i = 0; i < steps; i++)
        {
            ReadSolidContacts(curSolid);
            ReadTriggerContacts(curTrig);
        }
        sw.Stop();
        double contacts = sw.Elapsed.TotalMilliseconds * 1000.0 / steps;
        sw.Reset();
        sw.Start();
        for(int i = 0; i < steps; i++)
        {
            foreach(Foe f in foes)
            {
                if(f.Simulated && f.Moves)
                {
                    f.Body.position = f.Pos0;
                }
            }
        }
        sw.Stop();
        double moveFoes = sw.Elapsed.TotalMilliseconds * 1000.0 / steps;
        int near = 0, shapes = 0;
        foreach(Foe f in foes) if(f.Simulated) near++;
        foreach(Copy c in allCopies) shapes += c.Shapes.shapeCount;
        // Leave everything as the next prediction expects it
        ReadStart(m);
        return "per step (microseconds): simulate " + all.ToString("0.0") + ", reading contacts " + contacts.ToString("0.0") + ", placing enemies " + moveFoes.ToString("0.0")
            + "; " + allCopies.Count + " colliders, " + shapes + " shapes, " + foes.Count + " enemies/spikes (" + near + " near Pete)";
    }

    // Just the green line, following the movie (used by the self-test)
    public TasPrediction PredictPath(int n)
    {
        TasManager m = TasManager.I;
        if(m == null || m.Player == null || !m.Player.Valid || !EnsureWorld(m))
        {
            return null;
        }
        ReadStart(m);
        return Run(PathInputs(m, n, false), n, null, null, "Your path", PathColor);
    }

    private void AddUnlessSame(TasPrediction p, TasPrediction green)
    {
        if(p.SameAs(green, 0.0005f))
        {
            return;
        }
        foreach(TasPrediction other in Lines)
        {
            if(p.SameAs(other, 0.0005f))
            {
                return;
            }
        }
        Lines.Add(p);
    }

    // inputs[0] is the current frame's input (the game needs it to tell "just pressed" from "held")
    private static Buttons[] PathInputs(TasManager m, int n, bool live, bool letGoAtEnd = false)
    {
        Buttons[] a = new Buttons[n + 1];
        int f = m.Frame;
        a[0] = m.Movie.Get(f);
        Buttons keys = live ? TasManager.ReadPlayerInput() : Buttons.None;
        Buttons last = m.Movie.Length > 0 && !letGoAtEnd ? m.Movie.Get(m.Movie.Length) : Buttons.None;
        for(int j = 1; j <= n; j++)
        {
            if(live)
            {
                a[j] = keys;
            }
            else
            {
                // Past the end of the movie, keep holding the last frame's buttons (or let go)
                a[j] = f + j <= m.Movie.Length ? m.Movie.Get(f + j) : last;
            }
        }
        return a;
    }

    // Your inputs, with an attack pressed on the next frame (aimed up, down or forward)
    private static Buttons[] AttackNow(Buttons[] path, int n, Buttons aim)
    {
        Buttons[] a = new Buttons[n + 1];
        Array.Copy(path, a, n + 1);
        int at = 1;
        if((a[0] & Buttons.Attack) != 0)
        {
            // Attack is held right now, so let go first and press on the frame after
            a[1] &= ~Buttons.Attack;
            at = 2;
        }
        if(at <= n)
        {
            a[at] = (a[at] & ~(Buttons.Up | Buttons.Down)) | aim | Buttons.Attack;
        }
        return a;
    }

    private static Buttons[] Constant(Buttons now, Buttons b, int n)
    {
        Buttons[] a = new Buttons[n + 1];
        a[0] = now;
        for(int j = 1; j <= n; j++)
        {
            a[j] = b;
        }
        return a;
    }

    private void MeasurePathError(TasManager m, TasPrediction green)
    {
        int f = m.Frame;
        float worst = 0f;
        int count = 0;
        for(int j = 1; j < green.Pos.Count; j++)
        {
            int g = f + j;
            if(g >= m.Timeline.Count || g > m.Movie.Length)
            {
                break;
            }
            worst = Mathf.Max(worst, Vector2.Distance(green.Pos[j], m.Timeline[g].Pos));
            count++;
        }
        PathErrorFrames = count;
        PathError = count > 0 ? worst : -1f;
    }

    // =====================================================================
    //  Reading the game
    // =====================================================================

    private static readonly BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Dictionary<string, FieldInfo> fieldCache = new Dictionary<string, FieldInfo>();

    private static T Read<T>(object o, string field)
    {
        if(o == null)
        {
            return default(T);
        }
        Type t = o.GetType();
        string key = t.FullName + "." + field;
        FieldInfo fi;
        if(!fieldCache.TryGetValue(key, out fi))
        {
            fi = t.GetField(field, Any);
            fieldCache[key] = fi;
        }
        if(fi == null)
        {
            return default(T);
        }
        object v = fi.GetValue(o);
        return v is T ? (T)v : default(T);
    }

    // Game scripts are switched off while the TAS is paused, so "enabled" alone doesn't say if they're really on
    private static bool On(Behaviour b)
    {
        return b != null && (b.enabled || TasManager.Frozen.Contains(b));
    }

    private static int Sign(float x)
    {
        return x < 0f ? -1 : 1;
    }

    private void ReadStart(TasManager m)
    {
        GameObject pete = m.Player.Go;
        Rigidbody2D rb = m.Player.Rb;
        Move mv = pete.GetComponent<Move>();
        Jump jp = pete.GetComponent<Jump>();
        Ground gr = pete.GetComponent<Ground>();
        PlayerHealth ph = pete.GetComponent<PlayerHealth>();
        KnockbackTrigger kt = pete.GetComponent<KnockbackTrigger>();
        FlipSprite fs = pete.GetComponent<FlipSprite>();

        maxSpeed = Read<float>(mv, "maxSpeed");
        maxAcc = Read<float>(mv, "maxAcceleration");
        maxAirAcc = Read<float>(mv, "maxAirAcceleration");
        start.DesiredVel = Read<Vector2>(mv, "desiredVelocity");
        start.CanMove = mv == null || mv.canMove;

        jumpHeight = Read<float>(jp, "jumpHeight");
        maxAirJumps = Read<int>(jp, "maxAirJumps");
        downMul = Read<float>(jp, "downwardMovementMultiplier");
        upMul = Read<float>(jp, "upwardMovementMultiplier");
        coyoteTime = Read<float>(jp, "coyoteTime");
        bufferTime = Read<float>(jp, "jumpBufferTime");
        defaultGs = Read<float>(jp, "defaultGravityScale");
        start.JumpPhase = Read<int>(jp, "jumpPhase");
        start.Coyote = Read<float>(jp, "coyoteCounter");
        start.Buffer = Read<float>(jp, "jumpBufferCounter");
        start.DesiredJump = Read<bool>(jp, "desiredJump");
        start.IsJumping = Read<bool>(jp, "isJumping");

        start.OnGround = Read<bool>(gr, "onGround");
        start.Friction = Read<float>(gr, "friction");

        start.Hp = Read<int>(ph, "playerHP");
        start.KbCounter = Read<float>(ph, "kbCounter");
        kbTimeHealth = Read<float>(ph, "kbTime");

        kbTimeTrigger = Read<float>(kt, "kbTime");
        kbVelocity = Read<float>(kt, "kbVelocity");
        start.Clears.Clear();
        List<double> pending = Read<List<double>>(kt, "clearVelocityTimes");
        if(pending != null)
        {
            start.Clears.AddRange(pending);
        }
        peteStandardScale = fs != null ? Read<float>(fs, "standardScale") : 1f;
        if(peteStandardScale == 0f)
        {
            peteStandardScale = pete.transform.localScale.x;
        }

        pos0 = rb.position;
        vel0 = rb.velocity;
        gs0 = rb.gravityScale;
        awake0 = rb.IsAwake();
        peteZ = pete.transform.position.z;
        peteSign0 = Sign(pete.transform.localScale.x);
        Vector3 ls = pete.transform.localScale;
        peteScaleX0 = ls.x;
        peteScaleY0 = ls.y;
        peteScaleZ0 = ls.z;
        MeleeAttackManager mam = pete.GetComponent<MeleeAttackManager>();
        weaponDamage = mam != null ? mam.meleeDamage : 1;
        start.CanAttack = m.Player.CanAttack;
        start.AttackType = 0;
        start.AttackStart = 0;
        Animator an = pete.GetComponent<Animator>();
        if(an != null && an.isActiveAndEnabled)
        {
            // Where the attack animation is (the sword comes out on set frames after the press)
            AnimatorStateInfo st = an.GetCurrentAnimatorStateInfo(0);
            int type = st.IsName("Pete-Attack") || st.IsName("Pete-Attack-2") ? 1 : st.IsName("Pete-Attack-Down") ? 2 : st.IsName("Pete-Attack-Up") ? 3 : 0;
            if(type != 0)
            {
                int e = Mathf.RoundToInt(st.normalizedTime * st.length / TasManager.Tick) + 1;
                if(e < AttackOff[type])
                {
                    start.AttackType = type;
                    start.AttackStart = -e;
                }
            }
            else
            {
                // Pressed on this very frame: the animation starts on the next one
                PlayerAnimation pa = pete.GetComponent<PlayerAnimation>();
                if(pa != null && Read<bool>(pa, "isAttacking") && animationStateField != null)
                {
                    string sn = animationStateField.GetValue(null).ToString();
                    type = sn == "ATTACKUP" ? 3 : sn == "ATTACKDOWN" ? 2 : sn == "ATTACK1" || sn == "ATTACK2" ? 1 : 0;
                    if(type != 0)
                    {
                        start.AttackType = type;
                        start.AttackStart = 0;
                    }
                }
            }
        }
        int k = m.Frame;
        if(k >= 1 && k - 1 < m.Timeline.Count)
        {
            prevPos = m.Timeline[k - 1].Pos;
            prevVel = m.Timeline[k - 1].Vel;
        }
        else
        {
            prevPos = pos0;
            prevVel = vel0;
        }
        frameTime0 = TasHooks.GameTime;
        // Inside physics callbacks the game's clock reads the physics time, which runs half a frame behind
        physicsOffset = Time.timeAsDouble - Time.fixedTimeAsDouble;
        tickTime = Time.fixedDeltaTime;

        // Pete's hitbox (for drawing where he lands)
        Bounds all = new Bounds();
        bool any = false;
        peteBody = null;
        foreach(Copy c in peteCopies)
        {
            if(c.Original == null || !c.Original.enabled || c.WeaponType != 0)
            {
                continue;
            }
            if(peteBody == null || c.Original is BoxCollider2D)
            {
                peteBody = c.Original;
            }
            if(!any)
            {
                all = c.Original.bounds;
                any = true;
            }
            else
            {
                all.Encapsulate(c.Original.bounds);
            }
        }
        if(any)
        {
            BoxCenter = (Vector2)all.center - pos0;
            BoxSize = all.size;
            FootY = all.min.y - pos0.y;
        }

        // Enemies further away than this can't get involved before the lines end
        float reach = 8f + Frames * TasManager.Tick * 22f;
        foreach(Foe f in foes)
        {
            f.Alive0 = f.Go != null && f.Go.activeInHierarchy;
            if(!f.Alive0)
            {
                continue;
            }
            Vector3 tp = f.Go.transform.position;
            f.Pos0 = tp;
            f.Z0 = tp.z;
            if(f.Real != null)
            {
                f.Vel0 = f.Real.velocity;
                f.Rot0 = f.Real.rotation;
                f.Awake0 = f.Real.IsAwake();
            }
            f.Clears0.Clear();
            List<double> foeClears = f.Knock != null ? Read<List<double>>(f.Knock, "clearVelocityTimes") : null;
            if(foeClears != null)
            {
                f.Clears0.AddRange(foeClears);
            }
            f.Cooldown0 = Read<float>(f.Attack, "currentAttackDelay");
            f.AttackDelay = Read<float>(f.Attack, "attackDelay");
            f.Damage = Read<int>(f.Attack, "damage");
            if(f.Walker != null)
            {
                f.Speed = Read<float>(f.Walker, "speed");
                f.Dir0 = Read<int>(f.Walker, "direction");
            }
            if(f.Chaser != null)
            {
                f.Speed = Read<float>(f.Chaser, "speed");
                f.GroundChaser = Read<bool>(f.Chaser, "groundEnemy");
                f.Chasing0 = Read<bool>(f.Chaser, "chasing");
            }
            f.Sign0 = Sign(f.Go.transform.localScale.x);
            f.StandardScale = f.Flip != null ? Read<float>(f.Flip, "standardScale") : 1f;
            if(f.StandardScale == 0f)
            {
                f.StandardScale = f.Go.transform.localScale.x;
            }
            // A down-attack can bounce Pete off living enemies that can take a hit
            f.Pogoable = f.Health != null && f.Knock != null && Read<bool>(f.Health, "isEnemy") && Read<bool>(f.Health, "damageable") && Read<int>(f.Health, "currentHealth") > 0;
            f.PogoSpeed = f.Knock != null ? Read<float>(f.Knock, "kbVelocity") : 0f;
            f.KbVel = f.PogoSpeed;
            f.KbTime = f.Knock != null ? Read<float>(f.Knock, "kbTime") : 0f;
            f.HitClears0.Clear();
            if(f.Health != null)
            {
                f.Health0 = Read<int>(f.Health, "currentHealth");
                f.Damageable = Read<bool>(f.Health, "damageable");
                f.IsEnemy = Read<bool>(f.Health, "isEnemy");
                f.InvulTime = Read<float>(f.Health, "invulnerabilityTime");
                f.Hit0 = Read<bool>(f.Health, "hit");
                List<double> hc = Read<List<double>>(f.Health, "turnOffHitTimes");
                if(hc != null)
                {
                    f.HitClears0.AddRange(hc);
                }
            }
            // How far the enemy's (or spikes') actual shapes are from Pete's
            float d = float.MaxValue;
            foreach(Copy c in f.Copies)
            {
                if(peteBody != null && c.Original != null && c.Original.enabled && c.Original.gameObject.activeInHierarchy && !c.Original.isTrigger)
                {
                    ColliderDistance2D cd = peteBody.Distance(c.Original);
                    if(cd.isValid)
                    {
                        d = Mathf.Min(d, Mathf.Max(0f, cd.distance));
                    }
                }
            }
            f.Distance = d;
            f.Near = d <= reach;
        }

        // Switch copies on and off to match the game (dead enemies, collected pickups, rooms)
        RetryWaiting();
        foreach(Copy c in allCopies)
        {
            bool on = c.Original != null && c.Original.enabled && c.Original.gameObject.activeInHierarchy && (c.Foe == null || c.Foe.Simulated);
            if(c.Col.enabled != on)
            {
                c.Col.enabled = on;
            }
        }
        foreach(Foe f in foes)
        {
            f.Dead = false;
            if(f.Body != null && f.Body.simulated != f.Simulated)
            {
                f.Body.simulated = f.Simulated;
            }
        }
        killed.Clear();
        for(int t = 1; t <= 3; t++)
        {
            weaponOn0[t] = weaponCopies[t] != null && weaponCopies[t].Col.enabled;
        }
    }

    // =====================================================================
    //  One prediction: the stand-in Pete plays the inputs, frame by frame
    // =====================================================================

    // inputsEnd: the frame (from now) where the movie's inputs run out; after it, the line stops once Pete is at rest.
    // isPath: the green line, which gets all the labels.
    private TasPrediction Run(Buttons[] inputs, int n, Foe hitBy, Foe pogoOff, string name, Color color, int inputsEnd = -1, bool isPath = false)
    {
        TasPrediction p = new TasPrediction();
        current = p;
        currentIsPath = isPath;
        p.Name = name;
        p.Color = color;
        s.CopyFrom(start);
        float fdt = Time.fixedDeltaTime;
        float udt = TasManager.Tick;

        // Everything back to how it is on this frame
        foreach(Foe f in foes)
        {
            f.Pos = f.Pos0;
            f.Z = f.Z0;
            f.Cooldown = f.Cooldown0;
            f.Dir = f.Dir0;
            f.Chasing = f.Chasing0;
            f.Sign = f.Sign0;
            f.Moved = false;
            f.Clears.Clear();
            f.Clears.AddRange(f.Clears0);
            f.HealthLeft = f.Health0;
            f.Hit = f.Hit0;
            f.HitClears.Clear();
            f.HitClears.AddRange(f.HitClears0);
            f.WatchTriggers = f.Distance <= 8f + n * TasManager.Tick * 10f;
            if(f.Simulated)
            {
                SetFacing(f, f.Sign);
                PlaceFoe(f);
            }
        }
        // Enemies the last prediction defeated come back
        foreach(Foe f in killed)
        {
            f.Dead = false;
            foreach(Copy c in f.Copies)
            {
                c.Col.enabled = c.Original != null && c.Original.enabled && c.Original.gameObject.activeInHierarchy && f.Simulated;
            }
            if(f.Simulated)
            {
                PlaceFoe(f);
            }
        }
        killed.Clear();
        for(int t = 1; t <= 3; t++)
        {
            if(weaponCopies[t] != null && weaponCopies[t].Col.enabled != weaponOn0[t])
            {
                weaponCopies[t].Col.enabled = weaponOn0[t];
            }
        }
        peteSign = peteSign0;
        peteScaleX = peteScaleX0;
        peteScaleZ = peteScaleZ0;
        SetPeteFacing(peteSign);

        // Rebuild the physics contacts the way they were on this frame: one tiny step from the frame before,
        // then put Pete exactly where he is now (the same trick the TAS uses when it loads a savestate)
        // A tiny step is what the TAS uses after loading a savestate, but its contact pushes get scaled up a lot on the
        // next normal step, which goes wrong when something is in the middle of a hard impact. A normal-length step doesn't.
        float prime = FullPrime ? fdt : (TasStateRegistry.PrimeStep > 0f ? TasStateRegistry.PrimeStep : 0.00001f);
        dummy.rotation = 0f;
        dummy.angularVelocity = 0f;
        dummy.position = prevPos;
        dummy.velocity = prevVel;
        dummy.gravityScale = gs0;
        physics.Simulate(prime);
        dummy.position = pos0;
        dummy.velocity = vel0;
        dummy.gravityScale = gs0;
        if(awake0)
        {
            dummy.WakeUp();
        }
        else
        {
            dummy.Sleep();
        }
        foreach(Foe f in foes)
        {
            if(f.Simulated)
            {
                PlaceFoe(f);
            }
        }
        ReadSolidContacts(prevSolid);
        ReadTriggerContacts(prevTrig);
        ReadSwordContacts(prevSword);

        p.Pos.Add(pos0);
        p.Vel.Add(vel0);
        p.Ground.Add(s.OnGround);
        bool knocked = s.Clears.Count > 0 || !s.CanMove;
        p.Knock.Add(knocked);
        kbFrom = knocked ? 0 : -1;
        if(Trace)
        {
            TraceLog.Length = 0;
            TraceStep(0);
        }

        // "What if this enemy hits you right now?" / "What if your down-attack connects right now?"
        if(hitBy != null)
        {
            Hurt(hitBy, 0, p);
            s.KbCounter -= udt;
            s.CanMove = !(s.KbCounter > 0);
            p.Knock[0] = true;
            if(p.DeathAt >= 0)
            {
                return p;
            }
        }
        if(pogoOff != null)
        {
            Vector2 v = dummy.velocity;
            dummy.velocity = new Vector3(v.x, pogoOff.PogoSpeed, 0);
        }

        for(int j = 1; j <= n; j++)
        {
            Buttons cur = inputs[j];
            Buttons prev = inputs[j - 1];

            // ---- FixedUpdate: Move.HandleMovement ----
            if(s.CanMove)
            {
                Vector2 velocity = dummy.velocity;
                float acceleration = s.OnGround ? maxAcc : maxAirAcc;
                float maxSpeedChange = acceleration * fdt;
                velocity.x = Mathf.MoveTowards(velocity.x, s.DesiredVel.x, maxSpeedChange);
                dummy.velocity = velocity;
            }

            // ---- FixedUpdate: Jump ----
            {
                bool onGround = s.OnGround;
                Vector2 velocity = dummy.velocity;
                float bodyVy = velocity.y;
                if(onGround && bodyVy == 0)
                {
                    s.JumpPhase = 0;
                    s.Coyote = coyoteTime;
                    s.IsJumping = false;
                }
                else
                {
                    s.Coyote -= fdt;
                }
                if(s.DesiredJump)
                {
                    s.DesiredJump = false;
                    s.Buffer = bufferTime;
                }
                else if(!s.DesiredJump && s.Buffer > 0)
                {
                    s.Buffer -= fdt;
                }
                if(s.Buffer > 0)
                {
                    if(s.Coyote > 0f || s.JumpPhase < maxAirJumps && s.IsJumping)
                    {
                        if(s.IsJumping)
                        {
                            s.JumpPhase++;
                        }
                        s.Buffer = 0f;
                        s.Coyote = 0f;
                        float jumpSpeed = Mathf.Sqrt(-2f * Physics2D.gravity.y * jumpHeight);
                        s.IsJumping = true;
                        if(velocity.y > 0f)
                        {
                            jumpSpeed = Mathf.Max(jumpSpeed - velocity.y, 0f);
                        }
                        velocity.y += jumpSpeed;
                        if(p.JumpAt < 0)
                        {
                            p.JumpAt = j;
                        }
                    }
                }
                bool hold = (cur & Buttons.Jump) != 0;
                if(hold && bodyVy > 0)
                {
                    dummy.gravityScale = upMul;
                }
                else if(!hold || bodyVy < 0)
                {
                    dummy.gravityScale = downMul;
                }
                else if(bodyVy == 0)
                {
                    dummy.gravityScale = defaultGs;
                }
                dummy.velocity = velocity;
            }

            // ---- Physics step ----
            // Enemies a script moved since the last step jump there first (like Unity syncing a moved transform)
            foreach(Foe f in foes)
            {
                if(f.Simulated && f.Moves && f.Moved)
                {
                    f.Body.position = f.Pos;
                    f.Moved = false;
                }
            }
            physics.Simulate(fdt);
            foreach(Foe f in foes)
            {
                if(f.Simulated && f.Moves)
                {
                    f.Pos = f.Body.position;
                }
            }

            // ---- Collision callbacks: Ground, and enemies/spikes hurting Pete ----
            bool wasGround = s.OnGround;
            ReadSolidContacts(curSolid);
            if(ExitsFirst)
            {
                SolidExits();
            }
            foreach(long key in curSolid)
            {
                if(groundPairs.Contains(key))
                {
                    s.OnGround = true;
                }
                Copy other = pairOther[key];
                if(!other.FrictionFails)
                {
                    s.Friction = other.Friction;
                }
                if(!prevSolid.Contains(key) && other.Foe != null && other.Foe.CanHurt)
                {
                    Hurt(other.Foe, j, p);
                }
            }
            if(!ExitsFirst)
            {
                SolidExits();
            }
            HashSet<long> swap = prevSolid;
            prevSolid = curSolid;
            curSolid = swap;

            // Trigger zones: walking enemies hurt Pete or turn around, chasing enemies notice him
            ReadTriggerContacts(curTrig);
            foreach(long key in curTrig)
            {
                if(prevTrig.Contains(key))
                {
                    continue;
                }
                Copy mine = pairMine[key];
                Copy other = pairOther[key];
                Foe f = mine.Foe;
                bool isPete = other != null && other.IsPete;
                if(f.Walker != null)
                {
                    if(isPete)
                    {
                        Hurt(f, j, p);
                    }
                    else
                    {
                        f.Dir = f.Dir * (-1);
                        f.Sign = Sign(f.Dir * f.StandardScale);
                        SetFacing(f, f.Sign);
                    }
                }
                if(f.Chaser != null && isPete)
                {
                    f.Chasing = true;
                }
            }
            swap = prevTrig;
            prevTrig = curTrig;
            curTrig = swap;

            // Pete's sword touching an enemy (MeleeWeapon.OnTriggerEnter2D)
            ReadSwordContacts(curSword);
            foreach(long key in curSword)
            {
                if(!prevSword.Contains(key))
                {
                    SwordHit(pairOther[key].Foe, pairMine[key].WeaponType, j, p);
                }
            }
            swap = prevSword;
            prevSword = curSword;
            curSword = swap;

            if(!wasGround && s.OnGround)
            {
                p.Landings.Add(j);
                if(isPath && p.Landings.Count == 1)
                {
                    p.AddLabel(j, "lands (+" + j + " f)");
                }
            }

            // ---- Update ----
            float dirX = ((cur & Buttons.Right) != 0 ? 1f : 0f) - ((cur & Buttons.Left) != 0 ? 1f : 0f);
            s.DesiredVel = new Vector2(dirX, 0f) * Mathf.Max(maxSpeed - s.Friction, 0f);
            if((cur & Buttons.Jump) != 0 && (prev & Buttons.Jump) == 0)
            {
                s.DesiredJump = true;
            }
            s.KbCounter -= udt;
            s.CanMove = !(s.KbCounter > 0);
            // MeleeAttackManager.CheckInput: a fresh attack press starts a swing (up, down in the air, or forward)
            if((cur & Buttons.Attack) != 0 && (prev & Buttons.Attack) == 0 && s.CanAttack)
            {
                float vert = ((cur & Buttons.Up) != 0 ? 1f : 0f) - ((cur & Buttons.Down) != 0 ? 1f : 0f);
                int type = 0;
                if(vert > 0.5f) type = 3;
                if(vert < -0.5f && !s.OnGround) type = 2;
                if(vert > -0.5f && vert < 0.5f) type = 1;
                // The same up/down animation doesn't restart; forward swings alternate between two, so they do
                if(type != 0 && !(type == s.AttackType && type != 1))
                {
                    s.AttackType = type;
                    s.AttackStart = j;
                }
            }
            // Pete turns to face where he's going (FlipSprite), which flips his hitbox and sword too
            Vector2 vNow = dummy.velocity;
            if(vNow.x > 0)
            {
                peteScaleZ = peteScaleX;
                peteScaleX = 1 * peteStandardScale;
            }
            else if(vNow.x < 0)
            {
                peteScaleZ = peteScaleX;
                peteScaleX = -1 * peteStandardScale;
            }
            peteSign = Sign(peteScaleX);
            SetPeteFacing(peteSign);
            // The attack animation's events: the sword comes out, then goes away
            if(s.AttackType != 0)
            {
                int e = j - s.AttackStart;
                if(e == AttackOn[s.AttackType])
                {
                    SetSword(s.AttackType, true);
                    s.CanAttack = false;
                }
                if(e >= AttackOff[s.AttackType])
                {
                    SetSword(s.AttackType, false);
                    s.CanAttack = true;
                    s.AttackType = 0;
                }
            }
            Vector2 peteNow = dummy.position;
            foreach(Foe f in foes)
            {
                if(!f.Simulated)
                {
                    continue;
                }
                if(On(f.Attack))
                {
                    f.Cooldown -= udt;
                }
                if(f.Walker != null && On(f.Walker))
                {
                    f.Pos.x = f.Pos.x + udt * f.Dir * f.Speed;
                    f.Moved = true;
                }
                if(f.Chaser != null && On(f.Chaser) && f.Chasing)
                {
                    Vector2 target = f.GroundChaser ? new Vector2(peteNow.x, f.Pos.y) : peteNow;
                    f.Pos = Vector2.MoveTowards(f.Pos, target, f.Speed * udt);
                    f.Z = 0f;
                    f.Moved = true;
                }
                // FlipSprite: enemies turn to face the way they're being pushed
                if(f.Flip != null && f.Moves && On(f.Flip))
                {
                    float fvx = f.Body.velocity.x;
                    if(fvx > 0)
                    {
                        f.Sign = Sign(1 * f.StandardScale);
                    }
                    else if(fvx < 0)
                    {
                        f.Sign = Sign(-1 * f.StandardScale);
                    }
                    SetFacing(f, f.Sign);
                }
            }

            // ---- LateUpdate: the knockback ends ----
            double lateTime = frameTime0 + j * tickTime;
            for(int i = s.Clears.Count - 1; i >= 0; i--)
            {
                if(lateTime >= s.Clears[i])
                {
                    s.Clears.RemoveAt(i);
                    dummy.velocity = new Vector3(0, 0, 0);
                }
            }
            foreach(Foe f in foes)
            {
                for(int i = f.HitClears.Count - 1; i >= 0; i--)
                {
                    if(lateTime >= f.HitClears[i])
                    {
                        f.HitClears.RemoveAt(i);
                        f.Hit = false;
                    }
                }
                if(!f.Simulated || !f.Moves)
                {
                    continue;
                }
                for(int i = f.Clears.Count - 1; i >= 0; i--)
                {
                    if(lateTime >= f.Clears[i])
                    {
                        f.Clears.RemoveAt(i);
                        f.Body.velocity = new Vector3(0, 0, 0);
                    }
                }
            }

            p.Pos.Add(dummy.position);
            p.Vel.Add(dummy.velocity);
            p.Ground.Add(s.OnGround);
            bool knockNow = s.Clears.Count > 0 || !s.CanMove;
            p.Knock.Add(knockNow);
            if(knocked && !knockNow)
            {
                p.AddLabel(j, kbFrom > 0 ? "knockback over (" + (j - kbFrom) + " f)" : "knockback over (+" + j + " f)");
            }
            knocked = knockNow;
            if(isPath && j == inputsEnd && inputsEnd > 0)
            {
                p.AddLabel(j, "inputs end");
            }
            if(inputsEnd >= 0 && j > inputsEnd + 1 && s.OnGround && !knockNow && dummy.velocity.sqrMagnitude < 1e-6f)
            {
                // Out of inputs and standing still: this is where Pete ends up
                p.AddLabel(j, "stops here (+" + j + " f)");
                break;
            }
            if(Trace && j <= 3)
            {
                TraceStep(j);
            }
            if(p.DeathAt >= 0)
            {
                break;
            }
        }
        return p;
    }

    private void SetSword(int type, bool on)
    {
        Copy c = weaponCopies[type];
        if(c != null && c.Col.enabled != on)
        {
            c.Col.enabled = on;
        }
    }

    // Which enemies the sword is touching right now
    private void ReadSwordContacts(HashSet<long> into)
    {
        into.Clear();
        for(int t = 1; t <= 3; t++)
        {
            Copy wc = weaponCopies[t];
            if(wc == null || !wc.Col.enabled)
            {
                continue;
            }
            int cnt = wc.Col.GetContacts(everything, touching);
            for(int i = 0; i < cnt; i++)
            {
                Collider2D o = touching[i];
                Copy oc;
                if(o == null || o.isTrigger || !byId.TryGetValue(o.GetInstanceID(), out oc) || oc.Foe == null || !oc.HasHealth || oc.Foe.Health == null)
                {
                    continue;
                }
                long key = PairKey(wc.Col, o);
                into.Add(key);
                pairMine[key] = wc;
                pairOther[key] = oc;
            }
        }
    }

    // Where the sword's object is (its knockback pushes enemies away from this point)
    private Vector3 SwordPosition(int type)
    {
        Vector2 here = dummy.position;
        Vector3 l = weaponLocal[type];
        return new Vector3(here.x + l.x * peteScaleX, here.y + l.y * peteScaleY0, peteZ + l.z * peteScaleZ);
    }

    // EnemyHealth.doDamage, as the game does it
    private void SwordHit(Foe f, int type, int j, TasPrediction p)
    {
        if(f == null || f.Dead || !f.Damageable || f.Hit || f.HealthLeft <= 0)
        {
            return;
        }
        f.Hit = true;
        f.HealthLeft -= weaponDamage;
        double now = frameTime0 + j * tickTime - physicsOffset;
        if(f.HealthLeft <= 0)
        {
            // Defeated: it stops, and its hitboxes switch off
            f.Dead = true;
            killed.Add(f);
            foreach(Copy c in f.Copies)
            {
                c.Col.enabled = false;
            }
            if(f.Body != null)
            {
                f.Body.velocity = Vector2.zero;
            }
            p.AddLabel(j, "defeats the " + f.Name);
            return;
        }
        f.HitClears.Add(now + f.InvulTime);
        if(!f.IsEnemy)
        {
            return;
        }
        if(type == 2)
        {
            // KnockbackTrigger.ReverseKnockback: the down-swing bounces Pete up
            Vector2 v = dummy.velocity;
            dummy.velocity = new Vector3(v.x, f.KbVel, 0);
            p.AddLabel(j, "pogo off the " + f.Name);
        }
        else if(f.Body != null)
        {
            Vector3 kbDir = new Vector3(f.Pos.x, f.Pos.y, f.Z) - SwordPosition(type);
            f.Body.velocity = kbDir.normalized * f.KbVel;
            f.Clears.Add(now + f.KbTime);
            p.AddLabel(j, "hits the " + f.Name);
        }
    }

    private static void PlaceFoe(Foe f)
    {
        if(f.Body == null)
        {
            return;
        }
        f.Body.position = f.Pos0;
        f.Body.rotation = f.Rot0;
        f.Body.velocity = f.Vel0;
        f.Body.angularVelocity = 0f;
        if(f.Awake0)
        {
            f.Body.WakeUp();
        }
        else
        {
            f.Body.Sleep();
        }
    }

    private void SolidExits()
    {
        foreach(long key in prevSolid)
        {
            if(!curSolid.Contains(key))
            {
                s.OnGround = false;
                s.Friction = 0;
            }
        }
    }

    private TasPrediction current;
    private bool currentIsPath;
    private int kbFrom = -1;

    // PlayerHealth.PlayerDamage + KnockbackTrigger.Knockback, as the game does them
    private void Hurt(Foe f, int j, TasPrediction p)
    {
        if(f.Cooldown > 0)
        {
            return;
        }
        s.Hp -= f.Damage;
        s.KbCounter = kbTimeHealth;
        Vector2 here = dummy.position;
        Vector3 kbDir = new Vector3(here.x, here.y, peteZ) - new Vector3(f.Pos.x, f.Pos.y, f.Z);
        dummy.velocity = kbDir.normalized * kbVelocity;
        s.Clears.Add(frameTime0 + j * tickTime - physicsOffset + kbTimeTrigger);
        f.Cooldown = f.AttackDelay;
        p.Hits.Add(j);
        kbFrom = j;
        if(j > 0)
        {
            p.AddLabel(j, "HIT -" + f.Damage + " HP");
        }
        if(Trace)
        {
            TraceLog.Append("    hit by " + f.Name + " at +" + j + ": pete " + here.ToString("F4") + " z " + peteZ.ToString("F3") + ", " + f.Name + " " + f.Pos.ToString("F4") + " z " + f.Z.ToString("F3") + " -> vel " + dummy.velocity.ToString("F4") + "\n");
        }
        if(s.Hp <= 0 && p.DeathAt < 0)
        {
            p.DeathAt = j;
            p.AddLabel(j, "dies");
        }
    }

    private static long PairKey(Collider2D a, Collider2D b)
    {
        return ((long)a.GetInstanceID() << 32) ^ (uint)b.GetInstanceID();
    }

    private readonly Dictionary<long, Copy> pairMine = new Dictionary<long, Copy>();

    // Which solid things Pete's colliders touch, and which of those count as ground (like Ground.EvaluateCollision)
    private void ReadSolidContacts(HashSet<long> into)
    {
        into.Clear();
        groundPairs.Clear();
        Vector2 centre = dummy.position;
        foreach(Copy pc in peteCopies)
        {
            if(!pc.Col.enabled)
            {
                continue;
            }
            int cnt = pc.Col.GetContacts(everything, points);
            for(int i = 0; i < cnt; i++)
            {
                ContactPoint2D cp = points[i];
                Collider2D other = cp.collider == pc.Col ? cp.otherCollider : cp.collider;
                if(other == null || other.isTrigger || pc.Col.isTrigger)
                {
                    continue;
                }
                Copy oc;
                if(!byId.TryGetValue(other.GetInstanceID(), out oc))
                {
                    continue;
                }
                long key = PairKey(pc.Col, other);
                into.Add(key);
                pairOther[key] = oc;
                // The normal pointing towards Pete (floor under him = straight up)
                Vector2 nrm = cp.normal;
                if(Vector2.Dot(nrm, centre - cp.point) < 0f)
                {
                    nrm = -nrm;
                }
                if(nrm.y >= 0.9f)
                {
                    groundPairs.Add(key);
                }
            }
        }
    }

    // Trigger contacts that involve an enemy (its "notice Pete" / "turn around" zones)
    private void ReadTriggerContacts(HashSet<long> into)
    {
        into.Clear();
        foreach(Foe f in foes)
        {
            // Only walking enemies (turn around / hurt) and chasers that haven't noticed Pete yet care about triggers,
            // and only near the stand-in (far off, turning around can't matter before the line ends)
            if(!f.Simulated || (f.Walker == null && (f.Chaser == null || f.Chasing)))
            {
                continue;
            }
            if(!f.WatchTriggers)
            {
                continue;
            }
            foreach(Copy fc in f.Copies)
            {
                if(!fc.Col.enabled)
                {
                    continue;
                }
                int cnt = fc.Col.GetContacts(everything, touching);
                for(int i = 0; i < cnt; i++)
                {
                    Collider2D o = touching[i];
                    if(o == null || (!fc.Col.isTrigger && !o.isTrigger))
                    {
                        continue;
                    }
                    Copy oc;
                    byId.TryGetValue(o.GetInstanceID(), out oc);
                    if(oc != null && oc.Foe == f)
                    {
                        continue;
                    }
                    long key = PairKey(fc.Col, o);
                    into.Add(key);
                    pairMine[key] = fc;
                    pairOther[key] = oc;
                }
            }
        }
    }

    // =====================================================================
    //  Building the hidden copy of the level
    // =====================================================================

    private bool EnsureWorld(TasManager m)
    {
        GameObject pete = m.Player.Go;
        if(dummy != null && world.IsValid() && world.isLoaded && builtFor == pete)
        {
            return true;
        }
        DestroyWorld();
        builtFor = pete;
        builds++;
        float t0 = Time.realtimeSinceStartup;
        world = SceneManager.CreateScene("[TAS] Prediction " + builds, new CreateSceneParameters(LocalPhysicsMode.Physics2D));
        physics = world.GetPhysicsScene2D();

        // Pete's stand-in: the same kind of body with copies of his solid colliders
        Rigidbody2D real = m.Player.Rb;
        GameObject d = NewObject("[TAS] Prediction Pete");
        dummy = d.AddComponent<Rigidbody2D>();
        dummy.bodyType = RigidbodyType2D.Dynamic;
        dummy.useAutoMass = false;
        dummy.mass = real.mass;
        dummy.drag = real.drag;
        dummy.angularDrag = real.angularDrag;
        dummy.constraints = real.constraints;
        dummy.collisionDetectionMode = real.collisionDetectionMode;
        dummy.sleepMode = real.sleepMode;
        dummy.interpolation = RigidbodyInterpolation2D.None;
        dummy.sharedMaterial = real.sharedMaterial;
        peteBuiltSign = Sign(pete.transform.localScale.x);
        foreach(Collider2D c in pete.GetComponentsInChildren<Collider2D>(true))
        {
            if(c.isTrigger)
            {
                continue;
            }
            Copy pc = AddCopy(c, d, false, true, null);
            if(pc != null)
            {
                peteCopies.Add(pc);
            }
        }

        levelHost = NewObject("[TAS] Prediction level");

        // Pete's three sword hitboxes (switched off until a swing). They're usually off in the game, so they're
        // copied from their box sizes rather than from the physics shapes.
        MeleeAttackManager mam = pete.GetComponent<MeleeAttackManager>();
        if(mam != null)
        {
            string[] fields = { null, "meleeWeapon", "meleeWeaponDown", "meleeWeaponUp" };
            for(int t = 1; t <= 3; t++)
            {
                weaponCopies[t] = null;
                GameObject w = Read<GameObject>(mam, fields[t]);
                BoxCollider2D bc = w != null ? w.GetComponent<BoxCollider2D>() : null;
                if(bc == null)
                {
                    continue;
                }
                weaponLocal[t] = w.transform.localPosition;
                PhysicsShapeGroup2D g = new PhysicsShapeGroup2D();
                Vector2 h = bc.size * 0.5f;
                List<Vector2> corners = new List<Vector2>();
                foreach(Vector2 c in new[] { new Vector2(-h.x, -h.y), new Vector2(h.x, -h.y), new Vector2(h.x, h.y), new Vector2(-h.x, h.y) })
                {
                    corners.Add((Vector2)w.transform.TransformPoint(bc.offset + c) - real.position);
                }
                g.AddPolygon(corners);
                Copy wc = AddCopyFromShapes(bc, d, g, w.CompareTag("Player"), null);
                wc.WeaponType = t;
                wc.Col.enabled = false;
                weaponCopies[t] = wc;
                peteCopies.Add(wc);
            }
        }

        // Enemies: a body each, of the same kind as the real one (so Pete shoves them like in the game).
        // Spikes that never move go into the level.
        foreach(EnemyAttack ea in FindObjectsOfType<EnemyAttack>(true))
        {
            if(IsOurs(ea.gameObject))
            {
                continue;
            }
            Foe f = new Foe();
            f.Go = ea.gameObject;
            f.Name = NiceName(ea.gameObject.name);
            f.Attack = ea;
            f.Walker = ea.GetComponent<EnemyWalking>();
            f.Chaser = ea.GetComponent<EnemyChase>();
            f.Health = ea.GetComponent<EnemyHealth>();
            f.Knock = ea.GetComponent<KnockbackTrigger>();
            f.Flip = ea.GetComponent<FlipSprite>();
            f.CanHurt = f.Walker != null || f.Chaser != null || ea.GetComponent<DamagingObsticals>() != null;
            if(f.Walker == null && f.Chaser == null && ea.GetComponent<DamagingObsticals>() != null)
            {
                f.Name = "spikes";
            }
            Rigidbody2D frb = ea.GetComponent<Rigidbody2D>();
            f.Real = frb;
            GameObject host = levelHost;
            if(frb != null && frb.bodyType != RigidbodyType2D.Static)
            {
                host = NewObject("[TAS] Prediction " + f.Name);
                Vector3 at = ea.transform.position;
                host.transform.position = new Vector3(at.x, at.y, 0f);
                f.Body = host.AddComponent<Rigidbody2D>();
                f.Body.bodyType = frb.bodyType;
                f.Body.useAutoMass = false;
                f.Body.mass = frb.mass;
                f.Body.drag = frb.drag;
                f.Body.angularDrag = frb.angularDrag;
                f.Body.gravityScale = frb.gravityScale;
                f.Body.constraints = frb.constraints;
                f.Body.collisionDetectionMode = frb.collisionDetectionMode;
                f.Body.sleepMode = frb.sleepMode;
                f.Body.interpolation = RigidbodyInterpolation2D.None;
                f.Body.useFullKinematicContacts = frb.useFullKinematicContacts;
                f.Body.sharedMaterial = frb.sharedMaterial;
                f.Body.position = ea.transform.position;
            }
            f.BuiltSign = Sign(ea.transform.localScale.x);
            foreach(Collider2D c in ea.GetComponentsInChildren<Collider2D>(true))
            {
                // Colliders on the enemy's body are copied as they sit on the body; others (like spikes) into the level
                bool onBody = f.Body != null && c.GetComponentInParent<Rigidbody2D>() == frb;
                Copy fc = AddCopy(c, host, !onBody, false, f);
                if(fc != null)
                {
                    f.Copies.Add(fc);
                }
            }
            foes.Add(f);
        }

        // The level itself: everything that doesn't move
        foreach(Collider2D c in FindObjectsOfType<Collider2D>(true))
        {
            if(IsOurs(c.gameObject) || c.transform.IsChildOf(pete.transform) || c.GetComponentInParent<EnemyAttack>() != null)
            {
                continue;
            }
            if(c.usedByComposite)
            {
                continue;
            }
            Rigidbody2D rb = c.GetComponentInParent<Rigidbody2D>();
            if(rb != null && rb.bodyType != RigidbodyType2D.Static)
            {
                continue;
            }
            AddCopy(c, levelHost, true, false, null);
        }
        Debug.Log("[TAS] Prediction lines: copied the level (" + allCopies.Count + " colliders, " + foes.Count + " enemies/spikes, " + waiting.Count + " waiting) in " + ((Time.realtimeSinceStartup - t0) * 1000f).ToString("0") + " ms");
        return true;
    }

    private static bool IsOurs(GameObject go)
    {
        string sn = go.scene.name;
        return go.name.StartsWith("[TAS]") || (sn != null && sn.StartsWith("[TAS]")) || (go.transform.root != null && go.transform.root.name.StartsWith("[TAS]"));
    }

    private static string NiceName(string n)
    {
        int cut = n.IndexOfAny(new[] { '(', ' ' });
        if(cut > 0)
        {
            n = n.Substring(0, cut);
        }
        return n.Length > 0 ? n : "enemy";
    }

    private GameObject NewObject(string name)
    {
        GameObject go = new GameObject(name);
        SceneManager.MoveGameObjectToScene(go, world);
        return go;
    }

    private Copy AddCopy(Collider2D original, GameObject host, bool toWorld, bool isPete, Foe foe)
    {
        PhysicsShapeGroup2D shapes = new PhysicsShapeGroup2D();
        if(!CopyShapes(original, toWorld, host.transform.position, shapes))
        {
            Waiting w = new Waiting();
            w.Original = original;
            w.Host = host;
            w.ToWorld = toWorld;
            w.IsPete = isPete;
            w.Foe = foe;
            waiting.Add(w);
            return null;
        }
        return AddCopyFromShapes(original, host, shapes, isPete, foe);
    }

    private Copy AddCopyFromShapes(Collider2D original, GameObject host, PhysicsShapeGroup2D shapes, bool isPete, Foe foe)
    {
        CustomCollider2D cc = host.AddComponent<CustomCollider2D>();
        cc.isTrigger = original.isTrigger;
        Rigidbody2D orb = original.attachedRigidbody != null ? original.attachedRigidbody : original.GetComponentInParent<Rigidbody2D>();
        cc.sharedMaterial = original.sharedMaterial != null ? original.sharedMaterial : (orb != null ? orb.sharedMaterial : null);
        cc.SetCustomShapes(shapes);
        Copy copy = new Copy();
        copy.Original = original;
        copy.Col = cc;
        copy.Shapes = shapes;
        copy.Mirrored = Mirror(shapes);
        copy.IsPete = isPete;
        copy.Foe = foe;
        copy.HasHealth = original.GetComponent<EnemyHealth>() != null;
        copy.FrictionFails = orb == null;
        copy.Friction = orb != null && orb.sharedMaterial != null ? orb.sharedMaterial.friction : 0f;
        allCopies.Add(copy);
        byId[cc.GetInstanceID()] = copy;
        return copy;
    }

    private void RetryWaiting()
    {
        for(int i = waiting.Count - 1; i >= 0; i--)
        {
            Waiting w = waiting[i];
            if(w.Original == null || w.Host == null)
            {
                waiting.RemoveAt(i);
                continue;
            }
            if(!w.Original.enabled || !w.Original.gameObject.activeInHierarchy)
            {
                continue;
            }
            waiting.RemoveAt(i);
            Copy c = AddCopy(w.Original, w.Host, w.ToWorld, w.IsPete, w.Foe);
            if(c == null)
            {
                continue;
            }
            if(w.IsPete)
            {
                peteCopies.Add(c);
                if(peteSign != peteBuiltSign)
                {
                    SetMirrored(c, true);
                }
            }
            if(w.Foe != null)
            {
                w.Foe.Copies.Add(c);
            }
        }
    }

    private static readonly PhysicsShapeGroup2D source = new PhysicsShapeGroup2D();
    private static readonly List<Vector2> verts = new List<Vector2>();

    // Copies a collider's physics shapes. Shapes on a moving body stay as they sit on that body;
    // anything else is placed in the level (relative to 'origin', where the copy's object is).
    private static bool CopyShapes(Collider2D c, bool toWorld, Vector2 origin, PhysicsShapeGroup2D into)
    {
        into.Clear();
        if(c.GetShapes(source) == 0)
        {
            return false;
        }
        Matrix4x4 m = source.localToWorldMatrix;
        float rs = toWorld ? ((Vector2)m.MultiplyVector(Vector3.right)).magnitude : 1f;
        for(int i = 0; i < source.shapeCount; i++)
        {
            PhysicsShape2D shape = source.GetShape(i);
            source.GetShapeVertices(i, verts);
            if(toWorld)
            {
                for(int k = 0; k < verts.Count; k++)
                {
                    verts[k] = (Vector2)m.MultiplyPoint3x4(verts[k]) - origin;
                }
            }
            float r = shape.radius * rs;
            switch(shape.shapeType)
            {
                case PhysicsShapeType2D.Circle:
                    into.AddCircle(verts[0], r);
                    break;
                case PhysicsShapeType2D.Capsule:
                    into.AddCapsule(verts[0], verts[1], r);
                    break;
                case PhysicsShapeType2D.Polygon:
                {
                    int idx = into.AddPolygon(verts);
                    if(r > 0f)
                    {
                        into.SetShapeRadius(idx, r);
                    }
                    break;
                }
                case PhysicsShapeType2D.Edges:
                {
                    Vector2 a0 = shape.adjacentStart, a1 = shape.adjacentEnd;
                    if(toWorld)
                    {
                        a0 = (Vector2)m.MultiplyPoint3x4(a0) - origin;
                        a1 = (Vector2)m.MultiplyPoint3x4(a1) - origin;
                    }
                    into.AddEdges(verts, shape.useAdjacentStart, shape.useAdjacentEnd, a0, a1, r);
                    break;
                }
            }
        }
        return into.shapeCount > 0;
    }

    // The same shapes flipped left-right around the body's centre (what turning around does to a hitbox)
    private static PhysicsShapeGroup2D Mirror(PhysicsShapeGroup2D g)
    {
        PhysicsShapeGroup2D o = new PhysicsShapeGroup2D();
        for(int i = 0; i < g.shapeCount; i++)
        {
            PhysicsShape2D shape = g.GetShape(i);
            g.GetShapeVertices(i, verts);
            for(int k = 0; k < verts.Count; k++)
            {
                verts[k] = new Vector2(-verts[k].x, verts[k].y);
            }
            switch(shape.shapeType)
            {
                case PhysicsShapeType2D.Circle:
                    o.AddCircle(verts[0], shape.radius);
                    break;
                case PhysicsShapeType2D.Capsule:
                    o.AddCapsule(verts[0], verts[1], shape.radius);
                    break;
                case PhysicsShapeType2D.Polygon:
                {
                    verts.Reverse();
                    int idx = o.AddPolygon(verts);
                    if(shape.radius > 0f)
                    {
                        o.SetShapeRadius(idx, shape.radius);
                    }
                    break;
                }
                case PhysicsShapeType2D.Edges:
                {
                    Vector2 a0 = new Vector2(-shape.adjacentStart.x, shape.adjacentStart.y);
                    Vector2 a1 = new Vector2(-shape.adjacentEnd.x, shape.adjacentEnd.y);
                    o.AddEdges(verts, shape.useAdjacentStart, shape.useAdjacentEnd, a0, a1, shape.radius);
                    break;
                }
            }
        }
        return o;
    }

    private static void SetMirrored(Copy c, bool mirrored)
    {
        if(c.IsMirrored == mirrored)
        {
            return;
        }
        c.IsMirrored = mirrored;
        c.Col.SetCustomShapes(mirrored ? c.Mirrored : c.Shapes);
    }

    private void SetPeteFacing(int sign)
    {
        foreach(Copy c in peteCopies)
        {
            SetMirrored(c, sign != peteBuiltSign);
        }
    }

    private static void SetFacing(Foe f, int sign)
    {
        foreach(Copy c in f.Copies)
        {
            SetMirrored(c, sign != f.BuiltSign);
        }
    }

    private void DestroyWorld()
    {
        if(world.IsValid() && world.isLoaded)
        {
            SceneManager.UnloadSceneAsync(world);
        }
        dummy = null;
        levelHost = null;
        builtFor = null;
        peteCopies.Clear();
        allCopies.Clear();
        byId.Clear();
        foes.Clear();
        waiting.Clear();
        pairOther.Clear();
        pairMine.Clear();
        prevSolid.Clear();
        prevTrig.Clear();
        Lines.Clear();
        Path = null;
        lastKey = null;
    }
}
