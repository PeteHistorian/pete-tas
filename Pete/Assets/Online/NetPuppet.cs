using System.Collections.Generic;
using UnityEngine;

// What one player's Pete is doing on one frame, as sent over the network
public struct PeteState
{
    public float Time;      // the sender's clock
    public Vector2 Pos;
    public Vector2 Vel;
    public sbyte Facing;
    public byte Anim;
    public byte Flags;
    public byte Hp;
    public byte Teleports;  // goes up each time they respawn, so their Pete jumps there instead of sliding

    public const byte Red = 1;          // just got hit
    public const byte TimerStarted = 2; // they've started moving (the run's timer starts then)
}

// Smooths a stream of positions from another game: it shows them a tenth of a second late, sliding between the
// positions it received, so the movement looks smooth even though updates arrive in bursts.
public class NetStream
{
    public const float Delay = 0.1f;

    private struct Sample
    {
        public float Time;
        public Vector2 Pos;
        public Vector2 Vel;
    }

    private readonly List<Sample> samples = new List<Sample>();
    private float offset;
    private bool haveOffset;
    public float LastArrival = -999f;
    public Vector2 Latest;

    public bool HasData { get { return samples.Count > 0; } }

    public void Add(float senderTime, Vector2 pos, Vector2 vel)
    {
        float now = UnityEngine.Time.unscaledTime;
        float sample = senderTime - now;
        if(!haveOffset)
        {
            offset = sample;
            haveOffset = true;
        }
        else
        {
            // Quick to follow when messages arrive faster than expected, slow when one is late
            offset = sample > offset ? Mathf.Lerp(offset, sample, 0.3f) : Mathf.Lerp(offset, sample, 0.02f);
        }
        if(samples.Count > 0 && senderTime <= samples[samples.Count - 1].Time)
        {
            return;
        }
        Sample s;
        s.Time = senderTime;
        s.Pos = pos;
        s.Vel = vel;
        samples.Add(s);
        if(samples.Count > 40)
        {
            samples.RemoveAt(0);
        }
        LastArrival = now;
        Latest = pos;
    }

    public void Clear()
    {
        samples.Clear();
    }

    // Where to show it now
    public Vector2 Position()
    {
        if(samples.Count == 0)
        {
            return Latest;
        }
        float t = UnityEngine.Time.unscaledTime + offset - Delay;
        if(t <= samples[0].Time)
        {
            return samples[0].Pos;
        }
        for(int i = samples.Count - 1; i > 0; i--)
        {
            Sample a = samples[i - 1], b = samples[i];
            if(t >= a.Time && t <= b.Time)
            {
                float f = (t - a.Time) / Mathf.Max(0.0001f, b.Time - a.Time);
                return Vector2.Lerp(a.Pos, b.Pos, f);
            }
        }
        // Newer than anything received: keep going the way they were going, for a little while
        Sample last = samples[samples.Count - 1];
        float ahead = Mathf.Min(t - last.Time, 0.15f);
        return last.Pos + last.Vel * ahead;
    }
}

// Another player's Pete as seen here: just the picture and its animation, no physics (players pass through each other)
public class NetPuppet : MonoBehaviour
{
    public static readonly string[] AnimBools = { "IDLE", "RUNNING", "JUMPING", "FALLING", "UP", "DOWN", "ATTACKUP", "ATTACKDOWN", "ATTACK1", "ATTACK2" };

    public int PlayerId;
    public string PlayerName = "";
    public Color Tint = Color.white;
    public PeteState State;
    public readonly NetStream Stream = new NetStream();

    private Animator animator;
    private SpriteRenderer sprite;
    private float scaleX = 1f;
    private int shownAnim = -1;
    private byte teleports;
    private bool haveState;

    public static NetPuppet Create(GameObject localPete, int id, string name, Color tint)
    {
        GameObject go = new GameObject("Online player " + id + " (" + name + ")");
        NetPuppet p = go.AddComponent<NetPuppet>();
        p.PlayerId = id;
        p.PlayerName = name;
        p.Tint = tint;
        go.AddComponent<NetPuppetEvents>();
        p.sprite = go.AddComponent<SpriteRenderer>();
        p.animator = go.AddComponent<Animator>();
        if(localPete != null)
        {
            SpriteRenderer src = localPete.GetComponent<SpriteRenderer>();
            Animator anim = localPete.GetComponent<Animator>();
            if(src != null)
            {
                p.sprite.sprite = src.sprite;
                p.sprite.sharedMaterial = src.sharedMaterial;
                p.sprite.sortingLayerID = src.sortingLayerID;
                p.sprite.sortingOrder = src.sortingOrder - 1;
            }
            if(anim != null)
            {
                p.animator.runtimeAnimatorController = anim.runtimeAnimatorController;
                p.animator.applyRootMotion = false;
            }
            Vector3 s = localPete.transform.localScale;
            p.scaleX = Mathf.Abs(s.x);
            go.transform.localScale = new Vector3(p.scaleX, s.y, s.z);
            go.transform.position = localPete.transform.position;
        }
        p.sprite.color = tint;
        return p;
    }

    public void Receive(PeteState s)
    {
        if(haveState && s.Teleports != teleports)
        {
            Stream.Clear();
        }
        teleports = s.Teleports;
        State = s;
        haveState = true;
        Stream.Add(s.Time, s.Pos, s.Vel);
    }

    private void LateUpdate()
    {
        if(!haveState)
        {
            return;
        }
        Vector2 p = Stream.Position();
        transform.position = new Vector3(p.x, p.y, transform.position.z);
        Vector3 s = transform.localScale;
        transform.localScale = new Vector3((State.Facing < 0 ? -1f : 1f) * scaleX, s.y, s.z);
        if(animator != null && animator.runtimeAnimatorController != null && State.Anim != shownAnim && State.Anim < AnimBools.Length)
        {
            for(int i = 0; i < AnimBools.Length; i++)
            {
                animator.SetBool(AnimBools[i], i == State.Anim);
            }
            shownAnim = State.Anim;
        }
        sprite.color = (State.Flags & PeteState.Red) != 0 ? Color.red : Tint;
    }
}

// Pete's attack animations call these on the real Pete (to switch the sword on and off). The copies of other
// players only show the animation, so they just ignore them.
public class NetPuppetEvents : MonoBehaviour
{
    public void EnableWeapon(int weapon) { }
    public void DisableWeapon() { }
}
