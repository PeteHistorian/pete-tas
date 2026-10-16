using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using TMPro;

// One saved moment of the game (a savestate).
public class TasSnapshot
{
    public int Frame;
    public int EntryCount;
    public bool[] Present;
    public float[] F;
    public int[] U;   // saved but not part of the fingerprint (physics sleep flags)
    public int[] I;
    public double[] D;
    public object[] O;
    public UnityEngine.Random.State RandomState;
    public double GameTime;
    public bool PlayerDied;
    public bool Finished;
    public ulong Fingerprint;

    public long ApproxBytes
    {
        get { return 64 + F.Length * 4L + I.Length * 4L + U.Length * 4L + D.Length * 8L + O.Length * 8L + Present.Length; }
    }
}

// Knows every object in the level that can change, and saves/restores them.
public class TasStateRegistry
{
    // ---------- What kinds of script fields we can save ----------
    private enum Kind { Float, Int, Bool, Enum, Double, Vector2, Vector3, Quaternion, Color, ListDouble, Object }

    private class FieldSlot
    {
        public FieldInfo Field;
        public Kind Kind;
    }

    private class Entry
    {
        public GameObject Go;
        public Transform T;
        public Rigidbody2D Rb;
        public Behaviour[] Behaviours;
        public Renderer[] Renderers;
        public SpriteRenderer Sprite;
        public Animator Anim;
        public AnimatorControllerParameter[] AnimParams;
        public MonoBehaviour[] Scripts;
        public FieldSlot[][] ScriptFields;
        public TMP_Text Text;
        public bool Spawned;
        public bool EverActive;
    }

    // Scripts whose state doesn't affect gameplay (audio, menus) or that manage scene loading.
    private static readonly HashSet<string> ExcludedTypes = new HashSet<string>
    {
        "AudioManager", "MusicManager", "VolumeSlider", "VersionControll", "SceneChangeManager", "MainMenu", "DontDestroyOnLoad"
    };

    private static readonly Dictionary<Type, FieldSlot[]> fieldCache = new Dictionary<Type, FieldSlot[]>();
    private static readonly HashSet<string> warnedFields = new HashSet<string>();

    private readonly List<Entry> entries = new List<Entry>();
    private readonly HashSet<int> knownRoots = new HashSet<int>();
    private readonly List<FieldSlot> statics = new List<FieldSlot>();
    private Transform tasRoot;
    private bool initialized;

    // Reused while capturing so we don't allocate lists every frame.
    private readonly List<float> wf = new List<float>(4096);
    private readonly List<int> wi = new List<int>(4096);
    private readonly List<double> wd = new List<double>(64);
    private readonly List<object> wo = new List<object>(512);
    private readonly List<int> wu = new List<int>(128);

    public int EntryCount { get { return entries.Count; } }

    public TasStateRegistry(Transform tasRoot)
    {
        this.tasRoot = tasRoot;
    }

    public static bool IsGameScript(MonoBehaviour mb)
    {
        if(mb == null)
        {
            return false;
        }
        Type t = mb.GetType();
        if(t.Assembly != typeof(TasHooks).Assembly)
        {
            return false;
        }
        if(t.Name.StartsWith("Tas") || ExcludedTypes.Contains(t.Name))
        {
            return false;
        }
        if(t.Namespace != null && t.Namespace.StartsWith("TMPro"))
        {
            return false;
        }
        return true;
    }

    // ---------- Building the list of objects ----------

    public void Build()
    {
        entries.Clear();
        knownRoots.Clear();
        statics.Clear();
        ScanForNewObjects(false);

        // Static fields of game scripts (for example the player's current animation)
        HashSet<Type> seen = new HashSet<Type>();
        foreach(Entry e in entries)
        {
            foreach(MonoBehaviour mb in e.Scripts)
            {
                if(mb == null || !seen.Add(mb.GetType()))
                {
                    continue;
                }
                for(Type t = mb.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                {
                    foreach(FieldInfo f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if(f.IsLiteral || f.IsInitOnly)
                        {
                            continue;
                        }
                        FieldSlot slot = MakeSlot(f);
                        if(slot != null)
                        {
                            statics.Add(slot);
                        }
                    }
                }
            }
        }
        initialized = true;
    }

    private IEnumerable<GameObject> AllRoots()
    {
        for(int s = 0; s < SceneManager.sceneCount; s++)
        {
            Scene scene = SceneManager.GetSceneAt(s);
            if(!scene.isLoaded)
            {
                continue;
            }
            foreach(GameObject root in scene.GetRootGameObjects())
            {
                yield return root;
            }
        }
        if(tasRoot != null)
        {
            // Game objects that survive scene loads live in the same hidden scene as the TAS tool
            foreach(GameObject root in tasRoot.gameObject.scene.GetRootGameObjects())
            {
                yield return root;
            }
        }
    }

    // Looks for objects the game created since the last check (for example health pickups).
    public void ScanForNewObjects(bool spawned)
    {
        List<GameObject> newRoots = null;
        foreach(GameObject root in AllRoots())
        {
            if(root == null || (tasRoot != null && root.transform == tasRoot))
            {
                continue;
            }
            if(root.name.StartsWith("[TAS]"))
            {
                continue;
            }
            if(knownRoots.Add(root.GetInstanceID()))
            {
                if(newRoots == null)
                {
                    newRoots = new List<GameObject>();
                }
                newRoots.Add(root);
            }
        }
        if(newRoots == null)
        {
            return;
        }

        // Objects that game scripts point at (UI messages, room cameras, weapons) must be saved too
        HashSet<GameObject> referenced = new HashSet<GameObject>();
        List<Transform> all = new List<Transform>();
        foreach(GameObject root in newRoots)
        {
            root.GetComponentsInChildren(true, all);
            foreach(Transform t in all)
            {
                foreach(MonoBehaviour mb in t.GetComponents<MonoBehaviour>())
                {
                    if(!IsGameScript(mb))
                    {
                        continue;
                    }
                    foreach(FieldSlot slot in SlotsFor(mb.GetType()))
                    {
                        if(slot.Kind != Kind.Object)
                        {
                            continue;
                        }
                        object v = slot.Field.GetValue(mb);
                        GameObject g = v as GameObject;
                        if(g == null && v is Component)
                        {
                            Component c = (Component)v;
                            if(c != null)
                            {
                                g = c.gameObject;
                            }
                        }
                        if(g != null)
                        {
                            referenced.Add(g);
                        }
                    }
                }
            }
        }
        foreach(GameObject root in newRoots)
        {
            root.GetComponentsInChildren(true, all);
            foreach(Transform t in all)
            {
                // The camera only follows Pete; the TAS handles it separately (and the free camera moves it)
                if(t.GetComponent<Camera>() != null)
                {
                    continue;
                }
                if(IsDynamic(t.gameObject) || referenced.Contains(t.gameObject))
                {
                    entries.Add(MakeEntry(t.gameObject, spawned));
                }
            }
        }
    }

    private static bool IsDynamic(GameObject go)
    {
        if(go.GetComponent<Rigidbody2D>() != null || go.GetComponent<Collider2D>() != null || go.GetComponent<Animator>() != null || go.GetComponent<TMP_Text>() != null)
        {
            return true;
        }
        foreach(MonoBehaviour mb in go.GetComponents<MonoBehaviour>())
        {
            if(IsGameScript(mb))
            {
                return true;
            }
            if(mb != null && mb.GetType().Namespace == "Cinemachine")
            {
                return true;
            }
        }
        return false;
    }

    private Entry MakeEntry(GameObject go, bool spawned)
    {
        Entry e = new Entry();
        e.Go = go;
        e.T = go.transform;
        e.Rb = go.GetComponent<Rigidbody2D>();
        e.Behaviours = go.GetComponents<Behaviour>();
        e.Renderers = go.GetComponents<Renderer>();
        e.Sprite = go.GetComponent<SpriteRenderer>();
        e.Anim = go.GetComponent<Animator>();
        e.Text = go.GetComponent<TMP_Text>();
        e.Spawned = spawned;
        List<MonoBehaviour> scripts = new List<MonoBehaviour>();
        foreach(MonoBehaviour mb in go.GetComponents<MonoBehaviour>())
        {
            if(IsGameScript(mb))
            {
                scripts.Add(mb);
            }
        }
        e.Scripts = scripts.ToArray();
        e.ScriptFields = new FieldSlot[e.Scripts.Length][];
        for(int i = 0; i < e.Scripts.Length; i++)
        {
            e.ScriptFields[i] = SlotsFor(e.Scripts[i].GetType());
        }
        return e;
    }

    private static FieldSlot[] SlotsFor(Type type)
    {
        FieldSlot[] slots;
        if(fieldCache.TryGetValue(type, out slots))
        {
            return slots;
        }
        List<FieldSlot> list = new List<FieldSlot>();
        for(Type t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
        {
            foreach(FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                FieldSlot slot = MakeSlot(f);
                if(slot != null)
                {
                    list.Add(slot);
                }
            }
        }
        slots = list.ToArray();
        fieldCache[type] = slots;
        return slots;
    }

    private static FieldSlot MakeSlot(FieldInfo f)
    {
        Type ft = f.FieldType;
        Kind kind;
        if(ft == typeof(float)) kind = Kind.Float;
        else if(ft == typeof(int)) kind = Kind.Int;
        else if(ft == typeof(bool)) kind = Kind.Bool;
        else if(ft.IsEnum) kind = Kind.Enum;
        else if(ft == typeof(double)) kind = Kind.Double;
        else if(ft == typeof(Vector2)) kind = Kind.Vector2;
        else if(ft == typeof(Vector3)) kind = Kind.Vector3;
        else if(ft == typeof(Quaternion)) kind = Kind.Quaternion;
        else if(ft == typeof(Color)) kind = Kind.Color;
        else if(ft == typeof(List<double>)) kind = Kind.ListDouble;
        else if(typeof(Delegate).IsAssignableFrom(ft)) return null;
        else
        {
            kind = Kind.Object;
            if(!ft.IsValueType && ft != typeof(string) && !typeof(UnityEngine.Object).IsAssignableFrom(ft))
            {
                string key = f.DeclaringType.Name + "." + f.Name;
                if(warnedFields.Add(key))
                {
                    Debug.Log("[TAS] Field " + key + " (" + ft.Name + ") is saved by reference only.");
                }
            }
        }
        FieldSlot slot = new FieldSlot();
        slot.Field = f;
        slot.Kind = kind;
        return slot;
    }

    // ---------- Capture ----------

    public TasSnapshot Capture(int frame)
    {
        if(!initialized)
        {
            Build();
        }
        ScanForNewObjects(true);
        wf.Clear(); wi.Clear(); wd.Clear(); wo.Clear(); wu.Clear();
        skipF.Clear(); skipI.Clear(); skipD.Clear();
        TasSnapshot s = new TasSnapshot();
        s.Frame = frame;
        s.EntryCount = entries.Count;
        s.Present = new bool[entries.Count];
        for(int i = 0; i < entries.Count; i++)
        {
            Entry e = entries[i];
            if(e.Go == null)
            {
                continue;
            }
            s.Present[i] = true;
            CaptureEntry(e);
        }
        if(labeling) ctx = "static ";
        foreach(FieldSlot slot in statics)
        {
            WriteField(slot, null);
        }
        s.F = wf.ToArray();
        s.U = wu.ToArray();
        s.I = wi.ToArray();
        s.D = wd.ToArray();
        s.O = wo.ToArray();
        s.RandomState = UnityEngine.Random.state;
        s.GameTime = TasHooks.GameTime;
        s.PlayerDied = TasHooks.PlayerDied;
        s.Finished = TasHooks.Finished;
        s.Fingerprint = ComputeFingerprint(s, skipF, skipI, skipD);
        return s;
    }

    // While "labeling", every saved number also gets a name, so differences can be explained.
    private bool labeling;
    private readonly List<string> lf = new List<string>();
    private readonly List<string> li = new List<string>();
    private readonly List<string> ld = new List<string>();
    private readonly List<string> lu = new List<string>();
    private string ctx = "";

    private void AF(float v, string n) { wf.Add(v); if(labeling) lf.Add(ctx + n); }
    private void AI(int v, string n) { wi.Add(v); if(labeling) li.Add(ctx + n); }
    private void AD(double v, string n) { wd.Add(v); if(labeling) ld.Add(ctx + n); }

    private static string PathOf(Transform t)
    {
        string path = t.name;
        while(t.parent != null)
        {
            t = t.parent;
            path = t.name + "/" + path;
        }
        return path;
    }

    private void CaptureEntry(Entry e)
    {
        if(labeling) ctx = PathOf(e.T) + " ";
        AI(e.Go.activeSelf ? 1 : 0, "active");
        if(e.Go.activeInHierarchy)
        {
            e.EverActive = true;
        }
        // Scripts on objects that were never switched on haven't set themselves up yet (Awake/Start)
        wu.Add(e.EverActive ? 1 : 0); if(labeling) lu.Add(ctx + "set up");
        Vector3 p = e.T.localPosition;
        Quaternion q = e.T.localRotation;
        Vector3 sc = e.T.localScale;
        AF(p.x, "pos.x"); AF(p.y, "pos.y"); AF(p.z, "pos.z");
        AF(q.x, "rot.x"); AF(q.y, "rot.y"); AF(q.z, "rot.z"); AF(q.w, "rot.w");
        AF(sc.x, "scale.x"); AF(sc.y, "scale.y"); AF(sc.z, "scale.z");

        if(e.Rb != null)
        {
            Rigidbody2D rb = e.Rb;
            Vector2 rp = rb.position;
            Vector2 rv = rb.velocity;
            AF(rp.x, "rb.pos.x"); AF(rp.y, "rb.pos.y"); AF(rb.rotation, "rb.rot");
            AF(rv.x, "rb.vel.x"); AF(rv.y, "rb.vel.y"); AF(rb.angularVelocity, "rb.angVel"); AF(rb.gravityScale, "rb.gravity");
            AI((int)rb.bodyType, "rb.type"); AI(rb.simulated ? 1 : 0, "rb.simulated"); AI(rb.IsAwake() ? 1 : 0, "rb.awake");
            // Moved by a script since the last physics step? (Walking enemies move their transform directly.)
            Vector3 wp = e.T.position;
            bool ahead = Mathf.Abs(wp.x - rp.x) > 1e-4f || Mathf.Abs(wp.y - rp.y) > 1e-4f;
            AI(ahead ? 1 : 0, "rb.ahead");
        }
        foreach(Behaviour b in e.Behaviours)
        {
            // Scripts the TAS switched off for the pause count as switched on
            AI(b != null && (b.enabled || TasManager.Frozen.Contains(b)) ? 1 : 0, labeling && b != null ? b.GetType().Name + ".enabled" : "enabled");
        }
        foreach(Renderer r in e.Renderers)
        {
            AI(r != null && r.enabled ? 1 : 0, labeling && r != null ? r.GetType().Name + ".enabled" : "renderer");
        }
        if(e.Sprite != null)
        {
            Color c = e.Sprite.color;
            AF(c.r, "color.r"); AF(c.g, "color.g"); AF(c.b, "color.b"); AF(c.a, "color.a");
            AI(e.Sprite.flipX ? 1 : 0, "flipX");
            wo.Add(e.Sprite.sprite);
        }
        if(e.Anim != null)
        {
            CaptureAnimator(e);
        }
        // Switched-off objects can't do anything, and their scripts may not have set themselves up yet,
        // so their script values are saved but don't count towards the fingerprint.
        bool hashScripts = e.Go.activeInHierarchy;
        int f0 = wf.Count, i0 = wi.Count, d0 = wd.Count;
        for(int i = 0; i < e.Scripts.Length; i++)
        {
            MonoBehaviour mb = e.Scripts[i];
            string scriptCtx = labeling ? PathOf(e.T) + " " + mb.GetType().Name + "." : "";
            foreach(FieldSlot slot in e.ScriptFields[i])
            {
                if(labeling) ctx = scriptCtx;
                WriteField(slot, mb);
            }
        }
        if(!hashScripts && e.Scripts.Length > 0)
        {
            skipF.Add(new Vector2Int(f0, wf.Count));
            skipI.Add(new Vector2Int(i0, wi.Count));
            skipD.Add(new Vector2Int(d0, wd.Count));
        }
        if(e.Text != null)
        {
            wo.Add(e.Text.text);
        }
    }

    private readonly List<Vector2Int> skipF = new List<Vector2Int>();
    private readonly List<Vector2Int> skipI = new List<Vector2Int>();
    private readonly List<Vector2Int> skipD = new List<Vector2Int>();

    private void CaptureAnimator(Entry e)
    {
        Animator a = e.Anim;
        bool valid = a.isActiveAndEnabled && a.runtimeAnimatorController != null && a.isInitialized;
        if(!valid)
        {
            AI(0, "anim.valid");
            return;
        }
        if(e.AnimParams == null)
        {
            e.AnimParams = a.parameters;
        }
        int layers = a.layerCount;
        AI(1, "anim.valid");
        AI(layers, "anim.layers");
        for(int l = 0; l < layers; l++)
        {
            AnimatorStateInfo info = a.GetCurrentAnimatorStateInfo(l);
            AI(info.fullPathHash, "anim.state");
            AF(info.normalizedTime, "anim.time");
            bool inTransition = a.IsInTransition(l);
            AI(inTransition ? 1 : 0, "anim.inTransition");
            if(inTransition)
            {
                AnimatorStateInfo next = a.GetNextAnimatorStateInfo(l);
                AnimatorTransitionInfo tr = a.GetAnimatorTransitionInfo(l);
                AI(next.fullPathHash, "anim.next");
                AF(next.normalizedTime, "anim.nextTime");
                AF(tr.duration, "anim.transDuration");
                AF(tr.normalizedTime, "anim.transTime");
                AI(tr.durationUnit == DurationUnit.Fixed ? 1 : 0, "anim.transFixed");
            }
            else
            {
                AI(0, "anim.next");
                AF(0f, "anim.nextTime");
                AF(0f, "anim.transDuration");
                AF(0f, "anim.transTime");
                AI(0, "anim.transFixed");
            }
        }
        AI(e.AnimParams.Length, "anim.params");
        foreach(AnimatorControllerParameter p in e.AnimParams)
        {
            switch(p.type)
            {
                case AnimatorControllerParameterType.Float: AF(a.GetFloat(p.nameHash), "anim." + p.name); break;
                case AnimatorControllerParameterType.Int: AI(a.GetInteger(p.nameHash), "anim." + p.name); break;
                default: AI(a.GetBool(p.nameHash) ? 1 : 0, "anim." + p.name); break;
            }
        }
    }

    private void WriteField(FieldSlot slot, object target)
    {
        object v = slot.Field.GetValue(target);
        string n = labeling ? (target == null ? slot.Field.DeclaringType.Name + "." : "") + slot.Field.Name : null;
        switch(slot.Kind)
        {
            case Kind.Float: AF((float)v, n); break;
            case Kind.Int: AI((int)v, n); break;
            case Kind.Bool: AI((bool)v ? 1 : 0, n); break;
            case Kind.Enum: AI(Convert.ToInt32(v), n); break;
            case Kind.Double: AD((double)v, n); break;
            case Kind.Vector2: { Vector2 x = (Vector2)v; AF(x.x, n + ".x"); AF(x.y, n + ".y"); break; }
            case Kind.Vector3: { Vector3 x = (Vector3)v; AF(x.x, n + ".x"); AF(x.y, n + ".y"); AF(x.z, n + ".z"); break; }
            case Kind.Quaternion: { Quaternion x = (Quaternion)v; AF(x.x, n + ".x"); AF(x.y, n + ".y"); AF(x.z, n + ".z"); AF(x.w, n + ".w"); break; }
            case Kind.Color: { Color x = (Color)v; AF(x.r, n + ".r"); AF(x.g, n + ".g"); AF(x.b, n + ".b"); AF(x.a, n + ".a"); break; }
            case Kind.ListDouble:
            {
                List<double> list = (List<double>)v;
                int count = list == null ? -1 : list.Count;
                AI(count, n + ".Count");
                for(int k = 0; k < count; k++)
                {
                    AD(list[k], n + "[" + k + "]");
                }
                break;
            }
            default: wo.Add(v); break;
        }
    }

    // Lists saved values whose name contains some text (for testing), e.g. "Pete anim".
    public string Values(TasSnapshot s, string contains)
    {
        labeling = true;
        lf.Clear(); li.Clear(); ld.Clear(); lu.Clear();
        try
        {
            Capture(-1);
        }
        finally
        {
            labeling = false;
            ctx = "";
        }
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        for(int i = 0; i < li.Count && i < s.I.Length; i++) if(li[i].Contains(contains)) sb.Append(li[i].Substring(li[i].LastIndexOf(' ') + 1)).Append('=').Append(s.I[i]).Append(' ');
        for(int i = 0; i < lf.Count && i < s.F.Length; i++) if(lf[i].Contains(contains)) sb.Append(lf[i].Substring(lf[i].LastIndexOf(' ') + 1)).Append('=').Append(s.F[i].ToString("0.####")).Append(' ');
        return sb.ToString();
    }

    // Explains how two savestates differ, naming the objects and values (for testing).
    public string DescribeDiff(TasSnapshot a, TasSnapshot b, int max = 12)
    {
        labeling = true;
        lf.Clear(); li.Clear(); ld.Clear(); lu.Clear();
        try
        {
            Capture(-1);
        }
        finally
        {
            labeling = false;
            ctx = "";
        }
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        int shown = 0;
        if(a.EntryCount != b.EntryCount) sb.AppendLine("  object count " + a.EntryCount + " vs " + b.EntryCount);
        for(int i = 0; i < Math.Min(a.I.Length, b.I.Length) && shown < max; i++)
        {
            if(a.I[i] != b.I[i]) { sb.AppendLine("  " + (i < li.Count ? li[i] : "I" + i) + ": " + a.I[i] + " vs " + b.I[i]); shown++; }
        }
        for(int i = 0; i < Math.Min(a.F.Length, b.F.Length) && shown < max; i++)
        {
            if(Bits(a.F[i]) != Bits(b.F[i])) { sb.AppendLine("  " + (i < lf.Count ? lf[i] : "F" + i) + ": " + a.F[i].ToString("R") + " vs " + b.F[i].ToString("R")); shown++; }
        }
        for(int i = 0; i < Math.Min(a.D.Length, b.D.Length) && shown < max; i++)
        {
            if(a.D[i] != b.D[i]) { sb.AppendLine("  " + (i < ld.Count ? ld[i] : "D" + i) + ": " + a.D[i].ToString("R") + " vs " + b.D[i].ToString("R")); shown++; }
        }
        if(a.F.Length != b.F.Length || a.I.Length != b.I.Length || a.D.Length != b.D.Length) sb.AppendLine("  sizes differ");
        int setupDiffs = 0;
        for(int i = 0; i < Math.Min(a.U.Length, b.U.Length); i++) if(a.U[i] != b.U[i]) setupDiffs++;
        if(setupDiffs > 0) sb.AppendLine("  (" + setupDiffs + " objects differ only in whether they've been switched on yet; not counted)");
        if(a.GameTime != b.GameTime) sb.AppendLine("  game time " + a.GameTime.ToString("R") + " vs " + b.GameTime.ToString("R"));
        int[] ra = RandomStateParts(a.RandomState), rb = RandomStateParts(b.RandomState);
        for(int i = 0; i < ra.Length; i++) if(ra[i] != rb[i]) { sb.AppendLine("  random state differs"); break; }
        if(a.PlayerDied != b.PlayerDied || a.Finished != b.Finished) sb.AppendLine("  died/finished flags differ");
        return sb.Length == 0 ? "  (no differences)" : sb.ToString();
    }

    // ---------- Restore ----------

    private class Reader
    {
        public TasSnapshot S;
        public int F, I, D, O;
        public float NF() { return S.F[F++]; }
        public int NI() { return S.I[I++]; }
        public int U;
        public int NU() { return S.U[U++]; }
        public double ND() { return S.D[D++]; }
        public object NO() { return S.O[O++]; }
    }

    // Puts the whole game back to a savestate.
    // primeWith is the savestate one frame earlier: we load it first and let the physics engine look at it,
    // so its idea of "what was already touching" matches the original run.
    public void Restore(TasSnapshot snap, TasSnapshot primeWith)
    {
        bool wasMuted = TasHooks.MuteCallbacks;
        TasHooks.MuteCallbacks = true;
        try
        {
            if(primeWith != null)
            {
                Apply(primeWith, true);
                SimulateZeroStep();
            }
            Apply(snap, false);
        }
        finally
        {
            TasHooks.MuteCallbacks = wasMuted;
        }
    }

    // Length of the tiny physics step used to warm up the physics engine after loading a savestate.
    // It has to be more than zero so the engine also creates contacts between things that are just about to touch.
    public static float PrimeStep = 0.00001f;

    // How animators are put back: 0 = Play only (applied on the next animator update), 1 = Play and apply straight away
    public static int AnimRestoreMode = 1;
    private bool applyingPrime;

    private static void SimulateZeroStep()
    {
        SimulationMode2D mode = Physics2D.simulationMode;
        Physics2D.simulationMode = SimulationMode2D.Script;
        try
        {
            Physics2D.Simulate(PrimeStep);
        }
        finally
        {
            Physics2D.simulationMode = mode;
        }
    }

    // Where things go once every object's settings are back (filled in while reading a savestate).
    private struct PendingPlace
    {
        public Entry E;
        public Vector3 Pos;
        public Quaternion Rot;
        public Vector3 Scale;
        public bool HasRb;
        public Vector2 RbPos;
        public float RbRot;
        public bool Awake;
        public bool Ahead;
    }

    private readonly List<PendingPlace> pending = new List<PendingPlace>(256);

    private void Apply(TasSnapshot s, bool priming)
    {
        applyingPrime = priming;
        Reader r = new Reader();
        r.S = s;
        pending.Clear();
        // Objects created after this savestate are hidden, not destroyed, so later savestates can bring them back
        for(int i = s.EntryCount; i < entries.Count; i++)
        {
            Entry e = entries[i];
            if(e.Go != null && e.Spawned && e.Go.activeSelf)
            {
                e.Go.SetActive(false);
            }
        }
        for(int i = 0; i < s.EntryCount; i++)
        {
            if(!s.Present[i])
            {
                continue;
            }
            ApplyEntry(entries[i], r);
        }
        foreach(FieldSlot slot in statics)
        {
            ReadField(slot, null, r, true);
        }

        // Positions, in an order that gives the physics engine the exact same numbers as the original run:
        // 1. every transform, 2. let physics copy them, 3. put each body at its exact saved spot.
        foreach(PendingPlace pp in pending)
        {
            Transform t = pp.E.T;
            if(!SameVec(t.localPosition, pp.Pos)) t.localPosition = pp.Pos;
            if(!SameQuat(t.localRotation, pp.Rot)) t.localRotation = pp.Rot;
            if(!SameVec(t.localScale, pp.Scale)) t.localScale = pp.Scale;
        }
        Physics2D.SyncTransforms();
        foreach(PendingPlace pp in pending)
        {
            if(!pp.HasRb)
            {
                continue;
            }
            Rigidbody2D rb = pp.E.Rb;
            // Enemies that walk by moving their transform are one step ahead of their physics body.
            // While priming, physics should see them where the next physics step will (the transform).
            if(priming && pp.Ahead)
            {
                continue;
            }
            if(!SameVec2(rb.position, pp.RbPos)) rb.position = pp.RbPos;
            if(!SameFloat(rb.rotation, pp.RbRot)) rb.rotation = pp.RbRot;
            if(!priming && pp.Ahead)
            {
                // Flag the transform as moved again, so the next physics step picks it up like it did originally
                Transform t = pp.E.T;
                t.localPosition = pp.Pos + new Vector3(0.001f, 0f, 0f);
                t.localPosition = pp.Pos;
            }
        }
        foreach(PendingPlace pp in pending)
        {
            if(!pp.HasRb)
            {
                continue;
            }
            Rigidbody2D rb = pp.E.Rb;
            if(pp.Awake && !rb.IsAwake()) rb.WakeUp();
            else if(!pp.Awake && rb.IsAwake()) rb.Sleep();
        }

        UnityEngine.Random.state = s.RandomState;
        TasHooks.TimeOffset = Time.timeAsDouble - s.GameTime;
        TasHooks.PlayerDied = s.PlayerDied;
        TasHooks.Finished = s.Finished;
    }

    private void ApplyEntry(Entry e, Reader r)
    {
        bool live = e != null && e.Go != null;
        bool active = r.NI() == 1;
        bool setUp = r.NU() == 1;
        if(live && e.Go.activeSelf != active)
        {
            e.Go.SetActive(active);
        }
        // Going back to before this object was ever switched on: its scripts have set themselves up since
        // (Awake/Start only run once), so keep those values instead of the blank ones from back then.
        bool keepScriptFields = !setUp && e.EverActive;
        PendingPlace pp = new PendingPlace();
        pp.E = e;
        pp.Pos = new Vector3(r.NF(), r.NF(), r.NF());
        pp.Rot = new Quaternion(r.NF(), r.NF(), r.NF(), r.NF());
        pp.Scale = new Vector3(r.NF(), r.NF(), r.NF());
        if(e.Rb != null)
        {
            Vector2 rp = new Vector2(r.NF(), r.NF());
            float rot = r.NF();
            Vector2 rv = new Vector2(r.NF(), r.NF());
            float av = r.NF();
            float gs = r.NF();
            RigidbodyType2D type = (RigidbodyType2D)r.NI();
            bool sim = r.NI() == 1;
            bool awake = r.NI() == 1;
            bool ahead = r.NI() == 1;
            if(live)
            {
                Rigidbody2D rb = e.Rb;
                if(rb.bodyType != type) rb.bodyType = type;
                if(rb.simulated != sim) rb.simulated = sim;
                if(type != RigidbodyType2D.Static)
                {
                    rb.velocity = rv;
                    rb.angularVelocity = av;
                }
                rb.gravityScale = gs;
                pp.HasRb = true;
                pp.RbPos = rp;
                pp.RbRot = rot;
                pp.Awake = awake;
                pp.Ahead = ahead;
            }
        }
        if(live)
        {
            pending.Add(pp);
        }
        foreach(Behaviour b in e.Behaviours)
        {
            bool on = r.NI() == 1;
            if(live && b != null && b.enabled != on) b.enabled = on;
        }
        foreach(Renderer rend in e.Renderers)
        {
            bool on = r.NI() == 1;
            if(live && rend != null && rend.enabled != on) rend.enabled = on;
        }
        if(e.Sprite != null)
        {
            Color c = new Color(r.NF(), r.NF(), r.NF(), r.NF());
            bool flip = r.NI() == 1;
            Sprite sp = r.NO() as Sprite;
            if(live && e.Sprite != null)
            {
                if(e.Sprite.color != c) e.Sprite.color = c;
                if(e.Sprite.flipX != flip) e.Sprite.flipX = flip;
                if(e.Sprite.sprite != sp) e.Sprite.sprite = sp;
            }
        }
        if(e.Anim != null)
        {
            ApplyAnimator(e, r, live);
        }
        for(int i = 0; i < e.Scripts.Length; i++)
        {
            MonoBehaviour mb = e.Scripts[i];
            bool liveScript = live && mb != null && !keepScriptFields;
            foreach(FieldSlot slot in e.ScriptFields[i])
            {
                ReadField(slot, mb, r, liveScript);
            }
        }
        if(e.Text != null)
        {
            string text = r.NO() as string;
            if(live && e.Text != null && e.Text.text != text) e.Text.text = text;
        }
    }

    private void ApplyAnimator(Entry e, Reader r, bool live)
    {
        bool valid = r.NI() == 1;
        if(!valid)
        {
            return;
        }
        Animator a = e.Anim;
        // While priming only the physics matters, so animations are left alone
        bool canApply = live && !applyingPrime && a != null && a.isActiveAndEnabled && a.runtimeAnimatorController != null;
        int layers = r.NI();
        for(int l = 0; l < layers; l++)
        {
            int hash = r.NI();
            float t = r.NF();
            bool inTransition = r.NI() == 1;
            int nextHash = r.NI();
            float nextTime = r.NF();
            float transDuration = r.NF();
            float transTime = r.NF();
            bool fixedDuration = r.NI() == 1;
            if(!canApply || l >= a.layerCount || hash == 0)
            {
                continue;
            }
            if(inTransition && nextHash != 0)
            {
                if(transDuration <= 0.0001f)
                {
                    // An instant change that hadn't been shown yet: jump straight to where it was going
                    a.Play(nextHash, l, nextTime);
                }
                else
                {
                    a.Play(hash, l, t);
                    if(fixedDuration) a.CrossFadeInFixedTime(nextHash, transDuration, l, nextTime, transTime);
                    else a.CrossFade(nextHash, transDuration, l, nextTime, transTime);
                }
            }
            else
            {
                a.Play(hash, l, t);
            }
        }
        int count = r.NI();
        AnimatorControllerParameter[] ps = e.AnimParams;
        for(int k = 0; k < count; k++)
        {
            AnimatorControllerParameter p = ps != null && k < ps.Length ? ps[k] : null;
            if(p == null)
            {
                // Parameter list unknown: the layout is int-only in that case
                r.NI();
                continue;
            }
            switch(p.type)
            {
                case AnimatorControllerParameterType.Float:
                {
                    float v = r.NF();
                    if(canApply) a.SetFloat(p.nameHash, v);
                    break;
                }
                case AnimatorControllerParameterType.Int:
                {
                    int v = r.NI();
                    if(canApply) a.SetInteger(p.nameHash, v);
                    break;
                }
                case AnimatorControllerParameterType.Trigger:
                {
                    bool v = r.NI() == 1;
                    if(canApply)
                    {
                        if(v) a.SetTrigger(p.nameHash);
                        else a.ResetTrigger(p.nameHash);
                    }
                    break;
                }
                default:
                {
                    bool v = r.NI() == 1;
                    if(canApply) a.SetBool(p.nameHash, v);
                    break;
                }
            }
        }
        if(canApply && AnimRestoreMode >= 1)
        {
            // Apply the state right now. Otherwise Unity applies it during the next frame instead of moving it on,
            // and the animation ends up one frame behind.
            a.Update(0f);
        }
    }

    private void ReadField(FieldSlot slot, object target, Reader r, bool apply)
    {
        object v;
        switch(slot.Kind)
        {
            case Kind.Float: v = r.NF(); break;
            case Kind.Int: v = r.NI(); break;
            case Kind.Bool: v = r.NI() == 1; break;
            case Kind.Enum: v = Enum.ToObject(slot.Field.FieldType, r.NI()); break;
            case Kind.Double: v = r.ND(); break;
            case Kind.Vector2: v = new Vector2(r.NF(), r.NF()); break;
            case Kind.Vector3: v = new Vector3(r.NF(), r.NF(), r.NF()); break;
            case Kind.Quaternion: v = new Quaternion(r.NF(), r.NF(), r.NF(), r.NF()); break;
            case Kind.Color: v = new Color(r.NF(), r.NF(), r.NF(), r.NF()); break;
            case Kind.ListDouble:
            {
                int n = r.NI();
                if(n < 0)
                {
                    v = null;
                    break;
                }
                List<double> current = apply ? slot.Field.GetValue(target) as List<double> : null;
                List<double> list = current ?? new List<double>(n);
                if(apply)
                {
                    list.Clear();
                }
                for(int k = 0; k < n; k++)
                {
                    double d = r.ND();
                    if(apply) list.Add(d);
                }
                v = list;
                break;
            }
            default: v = r.NO(); break;
        }
        if(apply && (target != null || slot.Field.IsStatic))
        {
            slot.Field.SetValue(target, v);
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
    private struct FloatBits
    {
        [System.Runtime.InteropServices.FieldOffset(0)] public float F;
        [System.Runtime.InteropServices.FieldOffset(0)] public int I;
    }

    public static int Bits(float f)
    {
        FloatBits b = new FloatBits();
        b.F = f;
        return b.I;
    }

    private static bool SameFloat(float a, float b)
    {
        return Bits(a) == Bits(b);
    }

    private static bool SameVec(Vector3 a, Vector3 b)
    {
        return SameFloat(a.x, b.x) && SameFloat(a.y, b.y) && SameFloat(a.z, b.z);
    }

    private static bool SameVec2(Vector2 a, Vector2 b)
    {
        return SameFloat(a.x, b.x) && SameFloat(a.y, b.y);
    }

    private static bool SameQuat(Quaternion a, Quaternion b)
    {
        return SameFloat(a.x, b.x) && SameFloat(a.y, b.y) && SameFloat(a.z, b.z) && SameFloat(a.w, b.w);
    }

    // ---------- Fingerprint ----------
    // A hash of every saved number. Two runs that end up in the same state have the same fingerprint.

    private static bool Skipped(List<Vector2Int> ranges, int index, ref int cursor)
    {
        while(cursor < ranges.Count && index >= ranges[cursor].y) cursor++;
        return cursor < ranges.Count && index >= ranges[cursor].x && index < ranges[cursor].y;
    }

    private static ulong ComputeFingerprint(TasSnapshot s, List<Vector2Int> skipF, List<Vector2Int> skipI, List<Vector2Int> skipD)
    {
        ulong h = 14695981039346656037UL;
        unchecked
        {
            int c = 0;
            for(int i = 0; i < s.F.Length; i++)
            {
                if(Skipped(skipF, i, ref c)) continue;
                h = (h ^ (uint)Bits(s.F[i])) * 1099511628211UL;
            }
            c = 0;
            for(int i = 0; i < s.I.Length; i++)
            {
                if(Skipped(skipI, i, ref c)) continue;
                h = (h ^ (uint)s.I[i]) * 1099511628211UL;
            }
            c = 0;
            for(int i = 0; i < s.D.Length; i++)
            {
                if(Skipped(skipD, i, ref c)) continue;
                h = (h ^ (ulong)BitConverter.DoubleToInt64Bits(s.D[i])) * 1099511628211UL;
            }
            h = (h ^ (ulong)BitConverter.DoubleToInt64Bits(s.GameTime)) * 1099511628211UL;
            foreach(int part in RandomStateParts(s.RandomState))
            {
                h = (h ^ (uint)part) * 1099511628211UL;
            }
            h = (h ^ (s.PlayerDied ? 1UL : 0UL)) * 1099511628211UL;
            h = (h ^ (s.Finished ? 2UL : 0UL)) * 1099511628211UL;
        }
        return h;
    }

    private static FieldInfo[] randomFields;

    // UnityEngine.Random.State keeps its four numbers private, so read them by reflection.
    public static int[] RandomStateParts(UnityEngine.Random.State state)
    {
        if(randomFields == null)
        {
            List<FieldInfo> list = new List<FieldInfo>();
            foreach(FieldInfo f in typeof(UnityEngine.Random.State).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if(f.FieldType == typeof(int))
                {
                    list.Add(f);
                }
            }
            randomFields = list.ToArray();
        }
        object boxed = state;
        int[] parts = new int[randomFields.Length];
        for(int i = 0; i < randomFields.Length; i++)
        {
            parts[i] = (int)randomFields[i].GetValue(boxed);
        }
        return parts;
    }
}
