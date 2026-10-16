using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Buttons = PeteInput.Buttons;

// What happened on one frame of the current route.
public struct TasFrameRecord
{
    public Vector2 Pos;
    public Vector2 Vel;
    public bool OnGround;
    public Buttons Input;
    public ulong Fingerprint;
    public bool Verified;
}

// An older attempt, kept as a faded line so you can compare routes.
public class TasGhost
{
    public string Name;
    public Color Color;
    public bool Visible = true;
    public bool Pinned;
    public int BranchFrame;
    public List<Vector2> Pos = new List<Vector2>();
    public List<Vector2> Vel = new List<Vector2>();
    public int Version;
    // The inputs that made this route, so you can go back to it, and the movie it came from
    public TasMovie Inputs;
    public string MovieName;
    // Worked out when needed: the first frame where this route goes a different way from your route now
    public int Split = -1;
    public int SplitVersion = -1;
    public int SplitCount = -1;
}

// A finish line or checkpoint you place in the level.
public class TasLine
{
    public string Name;
    public bool Vertical = true;
    public Vector2 Center;
    public float HalfLength = 3f;
    public Color Color;

    public Vector2 A
    {
        get { return Vertical ? Center + new Vector2(0, -HalfLength) : Center + new Vector2(-HalfLength, 0); }
    }

    public Vector2 B
    {
        get { return Vertical ? Center + new Vector2(0, HalfLength) : Center + new Vector2(HalfLength, 0); }
    }

    // Returns the first frame (with a fraction for "part way through the frame") where the route crosses this line,
    // or -1 if it never does. Frame f is the move from position f-1 to position f.
    public float FirstCrossing(IList<Vector2> pos, int fromFrame = 1)
    {
        Vector2 a = A, b = B;
        for(int f = Mathf.Max(1, fromFrame); f < pos.Count; f++)
        {
            float t;
            if(SegmentHit(pos[f - 1], pos[f], a, b, out t))
            {
                return f - 1 + t;
            }
        }
        return -1f;
    }

    // Signed distance still to go before reaching the line (along the route direction is unknown, so we use
    // the side the route started on). Used by the auto-search to score routes that haven't crossed yet.
    public float DistanceToGo(Vector2 start, Vector2 p)
    {
        if(Vertical)
        {
            float side = Mathf.Sign(Center.x - start.x);
            if(side == 0) side = 1;
            float along = (Center.x - p.x) * side;
            float off = Mathf.Max(0, Mathf.Abs(p.y - Center.y) - HalfLength);
            return along + off;
        }
        else
        {
            float side = Mathf.Sign(Center.y - start.y);
            if(side == 0) side = 1;
            float along = (Center.y - p.y) * side;
            float off = Mathf.Max(0, Mathf.Abs(p.x - Center.x) - HalfLength);
            return along + off;
        }
    }

    public static bool SegmentHit(Vector2 p0, Vector2 p1, Vector2 a, Vector2 b, out float t)
    {
        t = 0;
        Vector2 r = p1 - p0;
        Vector2 s = b - a;
        float denom = r.x * s.y - r.y * s.x;
        if(Mathf.Abs(denom) < 1e-9f)
        {
            return false;
        }
        Vector2 qp = a - p0;
        float tt = (qp.x * s.y - qp.y * s.x) / denom;
        float u = (qp.x * r.y - qp.y * r.x) / denom;
        if(tt > 0f && tt <= 1f && u >= 0f && u <= 1f)
        {
            t = tt;
            return true;
        }
        return false;
    }
}

// Reads Pete's movement numbers (including private ones) for the info panel.
public class TasPlayerInfo
{
    public GameObject Go;
    public Rigidbody2D Rb;
    public SpriteRenderer Sprite;
    private Jump jump;
    private Move move;
    private Ground ground;
    private PlayerHealth health;
    private PowerStatus powers;
    private MeleeAttackManager melee;
    private TimerManager timer;

    private static readonly BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly FieldInfo fCoyote = typeof(Jump).GetField("coyoteCounter", Any);
    private static readonly FieldInfo fBuffer = typeof(Jump).GetField("jumpBufferCounter", Any);
    private static readonly FieldInfo fIsJumping = typeof(Jump).GetField("isJumping", Any);
    private static readonly FieldInfo fHp = typeof(PlayerHealth).GetField("playerHP", Any);
    private static readonly FieldInfo fKb = typeof(PlayerHealth).GetField("kbCounter", Any);
    private static readonly FieldInfo fCanAttack = typeof(MeleeAttackManager).GetField("canAttack", Any);
    private static readonly FieldInfo fTime = typeof(TimerManager).GetField("currentTime", Any);
    private static readonly FieldInfo fMaxSpeed = typeof(Move).GetField("maxSpeed", Any);

    public static TasPlayerInfo Find()
    {
        GameObject go = GameObject.FindGameObjectWithTag("Player");
        if(go == null)
        {
            return null;
        }
        TasPlayerInfo p = new TasPlayerInfo();
        p.Go = go;
        p.Rb = go.GetComponent<Rigidbody2D>();
        p.Sprite = go.GetComponent<SpriteRenderer>();
        p.jump = go.GetComponent<Jump>();
        p.move = go.GetComponent<Move>();
        p.ground = go.GetComponent<Ground>();
        p.health = go.GetComponent<PlayerHealth>();
        p.powers = go.GetComponent<PowerStatus>();
        p.melee = go.GetComponent<MeleeAttackManager>();
        GameObject tm = GameObject.FindGameObjectWithTag("TimerManager");
        p.timer = tm != null ? tm.GetComponent<TimerManager>() : null;
        return p;
    }

    public bool Valid { get { return Go != null && Rb != null; } }
    public Vector2 Position { get { return Rb.position; } }
    public Vector2 Velocity { get { return Rb.velocity; } }
    public float GravityScale { get { return Rb.gravityScale; } }
    public bool OnGround { get { return ground != null && ground.GetOnGround(); } }
    public float Friction { get { return ground != null ? ground.GetFriction() : 0f; } }
    public float CoyoteLeft { get { return jump != null && fCoyote != null ? (float)fCoyote.GetValue(jump) : 0f; } }
    public float JumpBufferLeft { get { return jump != null && fBuffer != null ? (float)fBuffer.GetValue(jump) : 0f; } }
    public bool IsJumping { get { return jump != null && fIsJumping != null && (bool)fIsJumping.GetValue(jump); } }
    public bool CanMove { get { return move == null || move.canMove; } }
    public float KnockbackLeft { get { return health != null && fKb != null ? (float)fKb.GetValue(health) : 0f; } }
    public int Hp { get { return health != null && fHp != null ? (int)fHp.GetValue(health) : 0; } }
    public int Orbs { get { return powers != null ? powers.Powers : 0; } }
    public bool CanAttack { get { return melee != null && fCanAttack != null && (bool)fCanAttack.GetValue(melee); } }
    public bool TimerRunning { get { return timer != null && timer.timerOn; } }
    public float TimerSeconds { get { return timer != null && fTime != null ? (float)fTime.GetValue(timer) : 0f; } }
    public float MaxSpeed { get { return move != null && fMaxSpeed != null ? (float)fMaxSpeed.GetValue(move) : 6.5f; } }
}
