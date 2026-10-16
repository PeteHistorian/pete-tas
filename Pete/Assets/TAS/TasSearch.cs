using System.Collections.Generic;
using UnityEngine;
using Buttons = PeteInput.Buttons;

// Auto-search: tries lots of inputs from the current frame and finds the fastest way to reach a finish line.
// It uses the savestates to try each idea, so it runs quickly with the level hidden.
public class TasSearch
{
    public enum Method { JumpTiming, Beam }

    public class Result
    {
        public List<Buttons> Inputs = new List<Buttons>();
        public List<Vector2> Path = new List<Vector2>();
        public List<ulong> Fingerprints = new List<ulong>();
        public List<TasSnapshot> FirstSnaps = new List<TasSnapshot>();
        public float Crossing = -1f;      // frame number (from the search start) where it crosses, or -1
        public float DistanceLeft;
        public string Label;
        public float VerifiedCrossing = -2f;
    }

    // Settings
    public Method Kind = Method.Beam;
    public TasLine Target;
    public Buttons Direction = Buttons.Right;
    public int Window = 30;
    public int MaxHold = 20;
    public int BeamWidth = 16;
    public int MaxFrames = 250;
    public bool TryAttacks;

    // State
    public int StartFrame;
    public bool Cancelled;
    public bool Done;
    public int Tried;
    public string Status = "";
    public readonly List<Result> Results = new List<Result>();

    private Vector2 startPos;
    private Vector2 lastPos;
    private Vector2 lastVel;
    private bool lastDied;
    private bool lastFinished;
    private float bestCrossing = float.MaxValue;
    private float maxSpeedPerFrame = 6.5f * TasManager.Tick;

    public string Describe()
    {
        return Kind == Method.JumpTiming ? "Jump timing" : "Beam search";
    }

    public IEnumerator<bool> Run(TasManager m)
    {
        StartFrame = m.Frame;
        startPos = m.Player.Position;
        maxSpeedPerFrame = Mathf.Max(0.05f, m.Player.MaxSpeed * TasManager.Tick);
        Results.Clear();
        return Kind == Method.JumpTiming ? RunTiming(m) : RunBeam(m);
    }

    // Called by the manager after every search frame.
    public void AfterTick(TasManager m)
    {
        if(m.Player != null && m.Player.Valid)
        {
            lastPos = m.Player.Position;
            lastVel = m.Player.Velocity;
        }
        lastDied = TasHooks.PlayerDied;
        lastFinished = TasHooks.Finished;
        Tried++;
    }

    private float CrossingOf(Vector2 from, Vector2 to)
    {
        float t;
        if(TasLine.SegmentHit(from, to, Target.A, Target.B, out t))
        {
            return t;
        }
        return -1f;
    }

    // ---------------------------------------------------------------
    //  Jump timing: hold one direction, try every jump start and every hold length.
    // ---------------------------------------------------------------
    private IEnumerator<bool> RunTiming(TasManager m)
    {
        TasSnapshot start = m.GetSnapshot(StartFrame);
        TasSnapshot startPrev = m.GetSnapshot(StartFrame - 1);
        Buttons startInput = m.Movie.Get(StartFrame);
        int total = (Window + 1) * MaxHold;
        int done = 0;

        // press = 0 means "never jump"
        for(int press = 0; press <= Window; press++)
        {
            if(Cancelled) yield break;

            // Shared start: hold the direction until the jump frame
            m.RestoreSnapshot(start, startPrev, StartFrame);
            m.SetSearchPrevious(startInput);
            List<Buttons> prefix = new List<Buttons>();
            List<Vector2> prefixPath = new List<Vector2> { startPos };
            TasSnapshot prefixSnap = start, prefixPrev = startPrev;
            Buttons prefixLast = startInput;
            bool prefixEnded = false;
            int prefixLen = press == 0 ? MaxFrames : press - 1;
            for(int i = 0; i < prefixLen; i++)
            {
                if(Cancelled) yield break;
                Vector2 before = m.Player.Position;
                m.SearchInput = Direction;
                yield return true;
                prefix.Add(Direction);
                prefixPath.Add(lastPos);
                float t = CrossingOf(before, lastPos);
                if(lastDied || lastFinished)
                {
                    prefixEnded = true;
                    break;
                }
                if(t >= 0)
                {
                    AddResult(prefix, prefixPath, prefix.Count - 1 + t, 0f, press == 0 ? "No jump" : "Crossed before jumping");
                    prefixEnded = true;
                    break;
                }
                if(prefix.Count >= bestCrossing)
                {
                    prefixEnded = true;
                    break;
                }
                prefixPrev = prefixSnap;
                prefixSnap = m.CaptureSnapshot();
                prefixLast = Direction;
            }
            if(press == 0)
            {
                if(!prefixEnded)
                {
                    AddResult(prefix, prefixPath, -1f, Target.DistanceToGo(startPos, lastPos), "No jump");
                }
                done += MaxHold;
                continue;
            }
            if(prefixEnded)
            {
                done += MaxHold;
                continue;
            }
            int prefixFrame = StartFrame + prefix.Count;

            for(int hold = 1; hold <= MaxHold; hold++)
            {
                if(Cancelled) yield break;
                done++;
                m.BusyProgress = (float)done / total;
                m.BusyText = "Jump timing: jump on +" + press + ", hold " + hold;
                m.RestoreSnapshot(prefixSnap, prefixPrev, prefixFrame);
                m.SetSearchPrevious(prefixLast);
                List<Buttons> inputs = new List<Buttons>(prefix);
                List<Vector2> path = new List<Vector2>(prefixPath);
                float crossing = -1f;
                bool failed = false;
                while(inputs.Count < MaxFrames)
                {
                    if(Cancelled) yield break;
                    int k = inputs.Count - prefix.Count;
                    Buttons b = Direction | (k < hold ? Buttons.Jump : Buttons.None);
                    Vector2 before = m.Player.Position;
                    m.SearchInput = b;
                    yield return true;
                    inputs.Add(b);
                    path.Add(lastPos);
                    if(lastDied)
                    {
                        failed = true;
                        break;
                    }
                    float t = CrossingOf(before, lastPos);
                    if(t >= 0)
                    {
                        crossing = inputs.Count - 1 + t;
                        break;
                    }
                    if(lastFinished || inputs.Count >= bestCrossing)
                    {
                        break;
                    }
                }
                if(!failed)
                {
                    AddResult(inputs, path, crossing, Target.DistanceToGo(startPos, lastPos), "Jump on +" + press + ", hold " + hold);
                }
            }
        }
        Done = true;
    }

    // ---------------------------------------------------------------
    //  Beam search: frame by frame, try every button combo and keep the most promising few.
    // ---------------------------------------------------------------
    private class BeamState
    {
        public TasSnapshot Snap;
        public TasSnapshot Prev;
        public Buttons Last;
        public List<Buttons> Inputs;
        public List<Vector2> Path;
        public List<ulong> Prints;
        public List<TasSnapshot> FirstSnaps;
        public Vector2 Pos;
        public Vector2 Vel;
        public float Crossing = -1f;
        public float Score;
    }

    private List<Buttons> BeamActions()
    {
        List<Buttons> a = new List<Buttons>
        {
            Buttons.Right, Buttons.Right | Buttons.Jump,
            Buttons.None, Buttons.Jump,
            Buttons.Left, Buttons.Left | Buttons.Jump
        };
        if(TryAttacks)
        {
            a.Add(Buttons.Right | Buttons.Attack);
            a.Add(Buttons.Right | Buttons.Down | Buttons.Attack);
            a.Add(Buttons.Right | Buttons.Jump | Buttons.Down | Buttons.Attack);
            a.Add(Buttons.Left | Buttons.Attack);
            a.Add(Buttons.Left | Buttons.Down | Buttons.Attack);
            a.Add(Buttons.Left | Buttons.Jump | Buttons.Down | Buttons.Attack);
            a.Add(Buttons.Down | Buttons.Attack);
            a.Add(Buttons.Up | Buttons.Attack);
        }
        return a;
    }

    private IEnumerator<bool> RunBeam(TasManager m)
    {
        List<Buttons> actions = BeamActions();
        BeamState root = new BeamState();
        root.Snap = m.GetSnapshot(StartFrame);
        root.Prev = m.GetSnapshot(StartFrame - 1);
        root.Last = m.Movie.Get(StartFrame);
        root.Inputs = new List<Buttons>();
        root.Path = new List<Vector2> { startPos };
        root.Prints = new List<ulong>();
        root.FirstSnaps = new List<TasSnapshot>();
        root.Pos = startPos;
        root.Vel = m.Player.Velocity;
        List<BeamState> beam = new List<BeamState> { root };
        List<BeamState> finished = new List<BeamState>();

        for(int depth = 1; depth <= MaxFrames; depth++)
        {
            if(Cancelled) break;
            if(depth > bestCrossing + 1)
            {
                break;
            }
            m.BusyProgress = Mathf.Clamp01((float)depth / Mathf.Min(MaxFrames, bestCrossing < float.MaxValue ? bestCrossing + 1 : MaxFrames));
            m.BusyText = "Beam search: frame +" + depth + (bestCrossing < float.MaxValue ? "  (best so far: " + bestCrossing.ToString("0.00") + ")" : "");
            List<BeamState> next = new List<BeamState>();
            foreach(BeamState st in beam)
            {
                foreach(Buttons a in actions)
                {
                    if(Cancelled) break;
                    m.RestoreSnapshot(st.Snap, st.Prev, StartFrame + depth - 1);
                    m.SetSearchPrevious(st.Last);
                    m.SearchInput = a;
                    yield return true;
                    if(lastDied)
                    {
                        continue;
                    }
                    BeamState c = new BeamState();
                    c.Prev = st.Snap;
                    c.Last = a;
                    c.Inputs = new List<Buttons>(st.Inputs);
                    c.Inputs.Add(a);
                    c.Path = new List<Vector2>(st.Path);
                    c.Path.Add(lastPos);
                    c.Snap = m.CaptureSnapshot();
                    c.Prints = new List<ulong>(st.Prints);
                    c.Prints.Add(c.Snap.Fingerprint);
                    c.FirstSnaps = new List<TasSnapshot>(st.FirstSnaps);
                    if(c.FirstSnaps.Count < 3) c.FirstSnaps.Add(c.Snap);
                    c.Pos = lastPos;
                    c.Vel = lastVel;
                    float t = CrossingOf(st.Pos, lastPos);
                    if(t >= 0)
                    {
                        c.Crossing = depth - 1 + t;
                        if(c.Crossing < bestCrossing)
                        {
                            bestCrossing = c.Crossing;
                        }
                        finished.Add(c);
                        continue;
                    }
                    if(lastFinished)
                    {
                        continue;
                    }
                    // Estimated arrival: frames so far + distance left at top running speed
                    float dist = Mathf.Max(0f, Target.DistanceToGo(startPos, lastPos));
                    c.Score = depth + dist / maxSpeedPerFrame;
                    next.Add(c);
                }
            }
            if(next.Count == 0)
            {
                break;
            }
            // Keep the best few, skipping near-duplicates so the beam stays varied
            next.Sort((x, y) => x.Score.CompareTo(y.Score));
            List<BeamState> kept = new List<BeamState>();
            HashSet<long> seen = new HashSet<long>();
            foreach(BeamState c in next)
            {
                long key = ((long)Mathf.RoundToInt(c.Pos.x * 50) * 73856093L) ^ ((long)Mathf.RoundToInt(c.Pos.y * 50) * 19349663L)
                    ^ ((long)Mathf.RoundToInt(c.Vel.x * 5) * 83492791L) ^ ((long)Mathf.RoundToInt(c.Vel.y * 5) * 2654435761L);
                if(!seen.Add(key))
                {
                    continue;
                }
                kept.Add(c);
                if(kept.Count >= BeamWidth)
                {
                    break;
                }
            }
            beam = kept;
        }

        finished.Sort((x, y) => x.Crossing.CompareTo(y.Crossing));
        foreach(BeamState c in finished)
        {
            AddResult(c.Inputs, c.Path, c.Crossing, 0f, "Beam result", c.Prints, c.FirstSnaps);
        }
        if(Results.Count == 0)
        {
            // Nothing reached the line: report the closest tries
            foreach(BeamState c in beam)
            {
                AddResult(c.Inputs, c.Path, -1f, Target.DistanceToGo(startPos, c.Pos), "Closest try");
            }
        }
        Done = true;
    }

    private void AddResult(List<Buttons> inputs, List<Vector2> path, float crossing, float distLeft, string label, List<ulong> prints = null, List<TasSnapshot> firstSnaps = null)
    {
        Result r = new Result();
        // Inputs after the line is crossed don't matter, so drop them (this also stops near-identical results)
        int keep = crossing >= 0 ? Mathf.Min(inputs.Count, Mathf.CeilToInt(crossing + 0.0001f)) : inputs.Count;
        r.Inputs = inputs.GetRange(0, keep);
        r.Path = path.GetRange(0, Mathf.Min(path.Count, keep + 1));
        if(prints != null) r.Fingerprints = prints.GetRange(0, Mathf.Min(prints.Count, keep));
        if(firstSnaps != null) r.FirstSnaps.AddRange(firstSnaps);
        r.Crossing = crossing;
        r.DistanceLeft = distLeft;
        r.Label = label;
        if(crossing >= 0 && crossing < bestCrossing)
        {
            bestCrossing = crossing;
        }
        // Skip results with the same inputs, or that take exactly the same route (keep the one with fewer button presses)
        for(int i = 0; i < Results.Count; i++)
        {
            Result old = Results[i];
            if(SameInputs(old.Inputs, r.Inputs))
            {
                return;
            }
            if(SamePath(old.Path, r.Path))
            {
                if(Presses(r.Inputs) < Presses(old.Inputs))
                {
                    Results[i] = r;
                }
                return;
            }
        }
        Results.Add(r);
        Results.Sort(CompareResults);
        if(Results.Count > 5)
        {
            Results.RemoveRange(5, Results.Count - 5);
        }
    }

    private static int CompareResults(Result a, Result b)
    {
        bool ac = a.Crossing >= 0, bc = b.Crossing >= 0;
        if(ac != bc) return ac ? -1 : 1;
        if(ac) return a.Crossing.CompareTo(b.Crossing);
        return a.DistanceLeft.CompareTo(b.DistanceLeft);
    }

    private static bool SamePath(List<Vector2> a, List<Vector2> b)
    {
        if(a.Count != b.Count) return false;
        for(int i = 0; i < a.Count; i++)
        {
            if((a[i] - b[i]).sqrMagnitude > 1e-12f) return false;
        }
        return true;
    }

    private static int Presses(List<Buttons> inputs)
    {
        int n = 0;
        foreach(Buttons b in inputs)
        {
            for(int v = (int)b; v != 0; v &= v - 1) n++;
        }
        return n;
    }

    private static bool SameInputs(List<Buttons> a, List<Buttons> b)
    {
        // Trailing frames after crossing don't matter, so compare up to the shorter one
        int n = Mathf.Min(a.Count, b.Count);
        if(a.Count != b.Count) return false;
        for(int i = 0; i < n; i++)
        {
            if(a[i] != b[i]) return false;
        }
        return true;
    }

    public void Finished(TasManager m)
    {
        Done = true;
        if(Cancelled)
        {
            m.Say("Auto-search stopped. Tried " + Tried + " frames.");
        }
        else if(Results.Count > 0 && Results[0].Crossing >= 0)
        {
            m.Say("Auto-search done: best reaches " + Target.Name + " " + Results[0].Crossing.ToString("0.00") + " frames after frame " + StartFrame + ". Press Apply to use it.", 10f);
        }
        else
        {
            m.Say("Auto-search couldn't reach " + Target.Name + ". Try a bigger window or move the line closer.", 10f);
        }
    }

    // Makes a ghost line out of a result so you can see it in the level.
    public TasGhost ToGhost(TasManager m, Result r, int index)
    {
        TasGhost g = new TasGhost();
        g.Name = "Search #" + (index + 1) + (r.Crossing >= 0 ? " (+" + r.Crossing.ToString("0.0") + "f)" : "");
        g.Color = new Color(1f, 0.95f, 0.3f);
        g.BranchFrame = StartFrame + 1;
        g.Inputs = m.Movie.Clone();
        g.Inputs.SetRange(StartFrame + 1, r.Inputs);
        g.MovieName = m.MovieName;
        for(int f = 0; f <= StartFrame && f < m.Timeline.Count; f++)
        {
            g.Pos.Add(m.Timeline[f].Pos);
            g.Vel.Add(m.Timeline[f].Vel);
        }
        for(int i = 1; i < r.Path.Count; i++)
        {
            g.Pos.Add(r.Path[i]);
            Vector2 v = (r.Path[i] - r.Path[i - 1]) / TasManager.Tick;
            g.Vel.Add(v);
        }
        return g;
    }
}
