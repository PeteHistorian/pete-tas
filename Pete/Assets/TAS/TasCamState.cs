using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using Cinemachine;

// The game camera's hidden memory on one frame (saved with every savestate).
public class TasCamSnapshot
{
    public CinemachineVirtualCameraBase[] Vcams;
    // Per room camera: the parts that remember things (the camera, its follow and edge-of-room pieces) and their values
    public object[][] Targets;
    public object[][] Values;
    // How many frames ago Cinemachine last updated each room camera (it uses this to catch up on skipped frames)
    public bool[] HasStatus;
    public int[] FixedAgo;
    public int[] LateAgo;
    public object[] Mode;
    public float[] LastDelta;
    // The slide from one room camera to the next
    public object Blend;
    public float BlendStart;
    public object Live;
    public object RoundRobin;
    public ICinemachineCamera PrevActive;
    public GameObject PrevActiveGo;
    public object CurrentState;
    // Cinemachine's guess, per followed object, of whether it moves with physics or at the end of the frame
    public Transform[] TrackKeys;
    public int[] TrackWindowAgo, TrackLastAgo, TrackLate, TrackFixed, TrackWindows;
    public Matrix4x4[] TrackPos;
    public object[] TrackPref;
    // Where the camera itself was
    public Vector3 CamPos;
    public Quaternion CamRot;
    public float CamSize;

    public long ApproxBytes { get { return 96 + (Vcams != null ? Vcams.Length * 700L : 0); } }
}

// The game camera (Cinemachine) remembers things between frames that aren't stored on any object: how far it trails
// behind Pete (its smoothing) and how far along a slide from one room's camera to the next it is. Savestates keep a
// copy, so after going back the camera is exactly where it was on that frame and moves on the way it did in the game.
public static class TasCamState
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    // A copy of a blend between two cameras. A camera in it is either a room camera or another (nested) blend.
    private class BlendCopy
    {
        public object CamA, CamB;
        public AnimationCurve Curve;
        public float Time, Duration;
    }

    private class SourceCopy
    {
        public BlendCopy Blend;
        public object State;
    }

    private class SameObject : IEqualityComparer<object>
    {
        public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
        public int GetHashCode(object o) { return RuntimeHelpers.GetHashCode(o); }
    }

    public static bool Works { get; private set; }
    // Off = the old behaviour (the camera jumps onto Pete after going back); used by the self-test to compare
    public static bool Enabled = true;
    public static string Problem;

    private static FieldInfo fFrameStack, fLive, fPrevActive, fPrevActiveGo, fCurState, fFrameBlend, fFrameStart;
    private static Type frameT, sourceT;
    private static PropertyInfo pSourceBlend;
    private static FieldInfo fSourceState;
    private static ConstructorInfo sourceCtor;
    private static FieldInfo fUpdateStatus, fFixedCount, fSorted, fExtraState, fRoundRobin;
    private static PropertyInfo pExtensions;
    private static FieldInfo sFrame, sFixed, sMode, sDelta;
    private static FieldInfo fTracker, tWindowStart, tLate, tFixed, tWindows, tLastFrame, tLastPos, tPreferred;
    private static ConstructorInfo trackerCtor;
    private static bool initDone;

    private static readonly Dictionary<Type, FieldInfo[]> fieldCache = new Dictionary<Type, FieldInfo[]>();
    private static readonly Dictionary<object, object> map = new Dictionary<object, object>(new SameObject());
    private static readonly List<object> targetList = new List<object>();
    private static readonly List<object> valueList = new List<object>();

    private static void Init()
    {
        if(initDone)
        {
            return;
        }
        initDone = true;
        try
        {
            Type brainT = typeof(CinemachineBrain);
            fFrameStack = Need(brainT.GetField("mFrameStack", Any), "mFrameStack");
            fLive = Need(brainT.GetField("mCurrentLiveCameras", Any), "mCurrentLiveCameras");
            fPrevActive = Need(brainT.GetField("mActiveCameraPreviousFrame", Any), "mActiveCameraPreviousFrame");
            fPrevActiveGo = Need(brainT.GetField("mActiveCameraPreviousFrameGameObject", Any), "mActiveCameraPreviousFrameGameObject");
            fCurState = Need(brainT.GetField("<CurrentCameraState>k__BackingField", Any), "CurrentCameraState");
            frameT = Need(brainT.GetNestedType("BrainFrame", BindingFlags.NonPublic), "BrainFrame");
            fFrameBlend = Need(frameT.GetField("blend", Any), "BrainFrame.blend");
            fFrameStart = Need(frameT.GetField("blendStartPosition", Any), "BrainFrame.blendStartPosition");
            sourceT = Need(typeof(CinemachineBlend).Assembly.GetType("Cinemachine.BlendSourceVirtualCamera"), "BlendSourceVirtualCamera");
            pSourceBlend = Need(sourceT.GetProperty("Blend", Any), "BlendSourceVirtualCamera.Blend");
            fSourceState = Need(sourceT.GetField("<State>k__BackingField", Any), "BlendSourceVirtualCamera.State");
            sourceCtor = Need(sourceT.GetConstructor(Any, null, new[] { typeof(CinemachineBlend) }, null), "BlendSourceVirtualCamera()");
            Type coreT = typeof(CinemachineCore);
            fUpdateStatus = Need(coreT.GetField("mUpdateStatus", Any), "mUpdateStatus");
            fFixedCount = Need(coreT.GetField("s_FixedFrameCount", Any), "s_FixedFrameCount");
            fSorted = Need(coreT.GetField("m_ActiveCamerasAreSorted", Any), "m_ActiveCamerasAreSorted");
            fRoundRobin = Need(coreT.GetField("mRoundRobinVcamLastFrame", Any), "mRoundRobinVcamLastFrame");
            Type statusT = Need(coreT.GetNestedType("UpdateStatus", BindingFlags.NonPublic), "UpdateStatus");
            sFrame = Need(statusT.GetField("lastUpdateFrame", Any), "lastUpdateFrame");
            sFixed = Need(statusT.GetField("lastUpdateFixedFrame", Any), "lastUpdateFixedFrame");
            sMode = Need(statusT.GetField("lastUpdateMode", Any), "lastUpdateMode");
            sDelta = Need(statusT.GetField("lastUpdateDeltaTime", Any), "lastUpdateDeltaTime");
            pExtensions = Need(typeof(CinemachineVirtualCameraBase).GetProperty("mExtensions", Any), "mExtensions");
            fExtraState = Need(typeof(CinemachineExtension).GetField("mExtraState", Any), "mExtraState");
            Type trackerT = Need(typeof(CinemachineBlend).Assembly.GetType("Cinemachine.UpdateTracker"), "UpdateTracker");
            fTracker = Need(trackerT.GetField("mUpdateStatus", Any), "UpdateTracker.mUpdateStatus");
            Type trackT = Need(trackerT.GetNestedType("UpdateStatus", BindingFlags.NonPublic), "UpdateTracker.UpdateStatus");
            tWindowStart = Need(trackT.GetField("windowStart", Any), "windowStart");
            tLate = Need(trackT.GetField("numWindowLateUpdateMoves", Any), "numWindowLateUpdateMoves");
            tFixed = Need(trackT.GetField("numWindowFixedUpdateMoves", Any), "numWindowFixedUpdateMoves");
            tWindows = Need(trackT.GetField("numWindows", Any), "numWindows");
            tLastFrame = Need(trackT.GetField("lastFrameUpdated", Any), "lastFrameUpdated");
            tLastPos = Need(trackT.GetField("lastPos", Any), "lastPos");
            tPreferred = Need(trackT.GetField("<PreferredUpdate>k__BackingField", Any), "PreferredUpdate");
            trackerCtor = Need(trackT.GetConstructor(Any, null, new[] { typeof(int), typeof(Matrix4x4) }, null), "UpdateTracker.UpdateStatus()");
            Works = true;
        }
        catch(Exception ex)
        {
            Works = false;
            Problem = ex.Message;
            Debug.LogWarning("[TAS] The game camera's smoothing can't be saved, so it will jump onto Pete after going back: " + ex.Message);
        }
    }

    private static T Need<T>(T thing, string name) where T : class
    {
        if(thing == null)
        {
            throw new Exception("Cinemachine's " + name + " wasn't found");
        }
        return thing;
    }

    // Everything a Cinemachine piece remembers between frames: its fields that aren't settings
    // (settings are saved with the level and never change while playing).
    private static FieldInfo[] StateFields(Type type)
    {
        FieldInfo[] slots;
        if(fieldCache.TryGetValue(type, out slots))
        {
            return slots;
        }
        List<FieldInfo> list = new List<FieldInfo>();
        for(Type t = type; t != null && t != typeof(MonoBehaviour) && t != typeof(object); t = t.BaseType)
        {
            foreach(FieldInfo f in t.GetFields(Declared))
            {
                if(f.IsLiteral || f.IsInitOnly)
                {
                    continue;
                }
                bool setting = (f.IsPublic || f.IsDefined(typeof(SerializeField), false)) && !f.IsNotSerialized;
                if(setting && !(t.IsClass && !typeof(UnityEngine.Object).IsAssignableFrom(t)))
                {
                    continue;
                }
                Type ft = f.FieldType;
                if(typeof(Delegate).IsAssignableFrom(ft))
                {
                    continue;
                }
                if(ft.IsValueType || typeof(UnityEngine.Object).IsAssignableFrom(ft))
                {
                    list.Add(f);
                }
            }
        }
        slots = list.ToArray();
        fieldCache[type] = slots;
        return slots;
    }

    // The room camera itself, its follow piece (the "transposer"), its edge-of-room piece (the "confiner")
    // and what that piece remembers for this camera.
    private static void TargetsOf(CinemachineVirtualCameraBase vcam, List<object> into)
    {
        into.Clear();
        into.Add(vcam);
        CinemachineVirtualCamera v = vcam as CinemachineVirtualCamera;
        if(v != null)
        {
            CinemachineComponentBase[] pipeline = v.GetComponentPipeline();
            if(pipeline != null)
            {
                foreach(CinemachineComponentBase c in pipeline)
                {
                    if(c != null) into.Add(c);
                }
            }
        }
        IList extensions = pExtensions.GetValue(vcam, null) as IList;
        if(extensions != null)
        {
            foreach(object o in extensions)
            {
                CinemachineExtension ext = o as CinemachineExtension;
                if(ext == null)
                {
                    continue;
                }
                into.Add(ext);
                IDictionary extra = fExtraState.GetValue(ext) as IDictionary;
                if(extra != null && extra.Contains(vcam))
                {
                    object state = extra[vcam];
                    if(state != null) into.Add(state);
                }
            }
        }
    }

    private static CinemachineBrain Brain()
    {
        CinemachineCore core = CinemachineCore.Instance;
        return core.BrainCount > 0 ? core.GetActiveBrain(0) : null;
    }

    public static TasCamSnapshot Capture()
    {
        Init();
        if(!Works || !Enabled)
        {
            return null;
        }
        CinemachineBrain brain = Brain();
        if(brain == null)
        {
            return null;
        }
        TasCamSnapshot s = new TasCamSnapshot();
        map.Clear();
        IList frames = (IList)fFrameStack.GetValue(brain);
        if(frames != null && frames.Count > 0)
        {
            s.Blend = CopyOut((CinemachineBlend)fFrameBlend.GetValue(frames[0]));
            s.BlendStart = (float)fFrameStart.GetValue(frames[0]);
        }
        s.Live = CopyOut((CinemachineBlend)fLive.GetValue(brain));
        s.PrevActive = fPrevActive.GetValue(brain) as ICinemachineCamera;
        s.PrevActiveGo = fPrevActiveGo.GetValue(brain) as GameObject;
        s.CurrentState = fCurState.GetValue(brain);
        Transform ct = brain.transform;
        s.CamPos = ct.position;
        s.CamRot = ct.rotation;
        Camera cam = brain.OutputCamera;
        s.CamSize = cam != null ? cam.orthographicSize : 0f;

        // Every room camera in the level, switched on or not: one that's off still remembers things that matter when
        // it's switched back on. One that has stayed off since the last save can't have changed, so that copy is shared.
        CinemachineCore core = CinemachineCore.Instance;
        s.RoundRobin = fRoundRobin.GetValue(core);
        IDictionary tracked = fTracker.GetValue(null) as IDictionary;
        if(tracked != null)
        {
            int tn = tracked.Count, ti = 0, frame = Time.frameCount;
            s.TrackKeys = new Transform[tn];
            s.TrackWindowAgo = new int[tn];
            s.TrackLastAgo = new int[tn];
            s.TrackLate = new int[tn];
            s.TrackFixed = new int[tn];
            s.TrackWindows = new int[tn];
            s.TrackPos = new Matrix4x4[tn];
            s.TrackPref = new object[tn];
            foreach(DictionaryEntry de in tracked)
            {
                object st = de.Value;
                s.TrackKeys[ti] = de.Key as Transform;
                s.TrackWindowAgo[ti] = frame - (int)tWindowStart.GetValue(st);
                s.TrackLastAgo[ti] = frame - (int)tLastFrame.GetValue(st);
                s.TrackLate[ti] = (int)tLate.GetValue(st);
                s.TrackFixed[ti] = (int)tFixed.GetValue(st);
                s.TrackWindows[ti] = (int)tWindows.GetValue(st);
                s.TrackPos[ti] = (Matrix4x4)tLastPos.GetValue(st);
                s.TrackPref[ti] = tPreferred.GetValue(st);
                ti++;
            }
        }
        CinemachineVirtualCameraBase[] all = AllVcams();
        int n = all.Length;
        s.Vcams = all;
        s.Targets = new object[n][];
        s.Values = new object[n][];
        s.HasStatus = new bool[n];
        s.FixedAgo = new int[n];
        s.LateAgo = new int[n];
        s.Mode = new object[n];
        s.LastDelta = new float[n];
        IDictionary statuses = fUpdateStatus.GetValue(core) as IDictionary;
        int fixedNow = (int)fFixedCount.GetValue(null);
        for(int i = 0; i < n; i++)
        {
            CinemachineVirtualCameraBase vcam = all[i];
            if(vcam == null)
            {
                continue;
            }
            bool off = !vcam.isActiveAndEnabled && !map.ContainsKey(vcam) && !ReferenceEquals(vcam, s.PrevActive);
            Cached c;
            if(off && cache.TryGetValue(vcam, out c) && c.Off && c.Epoch == restoreEpoch)
            {
                s.Targets[i] = c.Targets;
                s.Values[i] = c.Values;
            }
            else
            {
                TargetsOf(vcam, targetList);
                valueList.Clear();
                foreach(object t in targetList)
                {
                    foreach(FieldInfo f in StateFields(t.GetType()))
                    {
                        valueList.Add(f.GetValue(t));
                    }
                }
                s.Targets[i] = targetList.ToArray();
                s.Values[i] = valueList.ToArray();
                c = new Cached();
                c.Off = off;
                c.Epoch = restoreEpoch;
                c.Targets = s.Targets[i];
                c.Values = s.Values[i];
                cache[vcam] = c;
            }
            if(statuses != null && statuses.Contains(vcam))
            {
                object st = statuses[vcam];
                s.HasStatus[i] = true;
                s.FixedAgo[i] = fixedNow - (int)sFixed.GetValue(st);
                s.LateAgo[i] = Time.frameCount - (int)sFrame.GetValue(st);
                s.Mode[i] = sMode.GetValue(st);
                s.LastDelta[i] = (float)sDelta.GetValue(st);
            }
        }
        map.Clear();
        return s;
    }

    // ---------- Paused frames don't exist for the game camera ----------
    // Between TAS frames the game sits paused for any number of screen frames. Cinemachine counts screen frames to
    // decide things (how much to catch up, and whether Pete moves with physics or at the end of a frame), so it would
    // behave differently depending on how long you paused. So the game camera is stopped on paused frames, and when
    // the next TAS frame runs, its frame counts are moved on as if no paused frames happened: it sees one game frame
    // after another, like the real game at 50 frames per second.
    private static int lastRunFrame = -1;
    private static TasCamSnapshot pending;
    private static bool haveGameMethod;
    private static CinemachineBrain.UpdateMethod gameMethod;

    // Called at the end of every screen frame: run the game camera next frame or not
    public static void EndOfFrame(bool runNext)
    {
        Init();
        CinemachineBrain brain = Works ? Brain() : null;
        if(brain == null)
        {
            return;
        }
        if(brain.m_UpdateMethod != CinemachineBrain.UpdateMethod.ManualUpdate)
        {
            if(!haveGameMethod)
            {
                haveGameMethod = true;
                gameMethod = brain.m_UpdateMethod;
            }
            // It ran this frame
            lastRunFrame = Time.frameCount;
        }
        if(!haveGameMethod)
        {
            return;
        }
        brain.m_UpdateMethod = runNext ? gameMethod : CinemachineBrain.UpdateMethod.ManualUpdate;
    }

    // Called at the very start of each TAS frame, before Cinemachine does anything
    public static void BeforeTick()
    {
        Init();
        if(!Works)
        {
            return;
        }
        int now = Time.frameCount;
        if(pending != null)
        {
            ApplyFrameCounts(pending, now);
            pending = null;
        }
        else if(lastRunFrame >= 0 && now - lastRunFrame > 1)
        {
            Shift(now - lastRunFrame - 1);
        }
        lastRunFrame = now - 1;
    }

    // Moves every frame count Cinemachine keeps on by n frames
    private static void Shift(int n)
    {
        IDictionary statuses = fUpdateStatus.GetValue(CinemachineCore.Instance) as IDictionary;
        if(statuses != null)
        {
            foreach(object st in statuses.Values)
            {
                sFrame.SetValue(st, (int)sFrame.GetValue(st) + n);
            }
        }
        IDictionary tracked = fTracker.GetValue(null) as IDictionary;
        if(tracked != null)
        {
            foreach(object st in tracked.Values)
            {
                tWindowStart.SetValue(st, (int)tWindowStart.GetValue(st) + n);
                tLastFrame.SetValue(st, (int)tLastFrame.GetValue(st) + n);
            }
        }
    }

    // Puts in a savestate's frame counts as if it was saved on the frame just before "tickFrame"
    private static void ApplyFrameCounts(TasCamSnapshot s, int tickFrame)
    {
        int saved = tickFrame - 1;
        IDictionary statuses = fUpdateStatus.GetValue(CinemachineCore.Instance) as IDictionary;
        for(int i = 0; statuses != null && i < s.Vcams.Length; i++)
        {
            CinemachineVirtualCameraBase vcam = s.Vcams[i];
            if(vcam != null && s.HasStatus[i] && statuses.Contains(vcam))
            {
                sFrame.SetValue(statuses[vcam], saved - s.LateAgo[i]);
            }
        }
        IDictionary tracked = fTracker.GetValue(null) as IDictionary;
        if(tracked == null || s.TrackKeys == null)
        {
            return;
        }
        // Forget anything that wasn't being followed yet on that frame, and put the rest back
        List<object> keep = new List<object>();
        foreach(Transform k in s.TrackKeys)
        {
            if(k != null) keep.Add(k);
        }
        List<object> drop = new List<object>();
        foreach(object k in tracked.Keys)
        {
            if(!keep.Contains(k)) drop.Add(k);
        }
        foreach(object k in drop)
        {
            tracked.Remove(k);
        }
        for(int i = 0; i < s.TrackKeys.Length; i++)
        {
            Transform k = s.TrackKeys[i];
            if(k == null)
            {
                continue;
            }
            object st = tracked.Contains(k) ? tracked[k] : null;
            if(st == null)
            {
                st = trackerCtor.Invoke(new object[] { saved, s.TrackPos[i] });
                tracked[k] = st;
            }
            tWindowStart.SetValue(st, saved - s.TrackWindowAgo[i]);
            tLastFrame.SetValue(st, saved - s.TrackLastAgo[i]);
            tLate.SetValue(st, s.TrackLate[i]);
            tFixed.SetValue(st, s.TrackFixed[i]);
            tWindows.SetValue(st, s.TrackWindows[i]);
            tLastPos.SetValue(st, s.TrackPos[i]);
            tPreferred.SetValue(st, s.TrackPref[i]);
        }
    }

    // Shows what the game camera is showing (after the center or zoom-out camera was switched off while paused)
    public static void ShowGameCamera()
    {
        Init();
        CinemachineBrain brain = Works ? Brain() : null;
        if(brain == null)
        {
            return;
        }
        CameraState state = brain.CurrentCameraState;
        brain.transform.SetPositionAndRotation(state.FinalPosition, state.FinalOrientation);
        Camera cam = brain.OutputCamera;
        if(cam != null && state.Lens.OrthographicSize > 0f)
        {
            cam.orthographicSize = state.Lens.OrthographicSize;
        }
    }

    // A room camera's last saved copy, shared while it stays switched off
    private class Cached
    {
        public bool Off;
        public int Epoch;
        public object[] Targets;
        public object[] Values;
    }

    private static readonly Dictionary<CinemachineVirtualCameraBase, Cached> cache = new Dictionary<CinemachineVirtualCameraBase, Cached>();
    // Goes up on every load, so nothing saved before it is shared afterwards
    private static int restoreEpoch;
    private static CinemachineVirtualCameraBase[] allVcams;

    private static CinemachineVirtualCameraBase[] AllVcams()
    {
        bool stale = allVcams == null || allVcams.Length == 0;
        for(int i = 0; !stale && i < allVcams.Length; i++)
        {
            stale = allVcams[i] == null;
        }
        if(stale)
        {
            // Found once per level load (the room cameras never change while playing)
            allVcams = UnityEngine.Object.FindObjectsOfType<CinemachineVirtualCameraBase>(true);
            cache.Clear();
        }
        return allVcams;
    }

    // What the game camera is doing, as text (for the self-test)
    public static string Describe()
    {
        Init();
        CinemachineBrain brain = Works ? Brain() : null;
        if(brain == null)
        {
            return "(no camera brain)";
        }
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        Camera cam = brain.OutputCamera;
        sb.Append("cam " + brain.transform.position.ToString("F4") + " size " + (cam != null ? cam.orthographicSize.ToString("F3") : "?"));
        IList frames = (IList)fFrameStack.GetValue(brain);
        if(frames != null && frames.Count > 0)
        {
            sb.Append(" | blend " + BlendText((CinemachineBlend)fFrameBlend.GetValue(frames[0])) + " start " + ((float)fFrameStart.GetValue(frames[0])).ToString("F3"));
        }
        sb.Append(" | live " + BlendText((CinemachineBlend)fLive.GetValue(brain)));
        ICinemachineCamera prev = fPrevActive.GetValue(brain) as ICinemachineCamera;
        sb.Append(" | prev " + (prev != null ? prev.Name : "-"));
        CinemachineCore core = CinemachineCore.Instance;
        IDictionary statuses = fUpdateStatus.GetValue(core) as IDictionary;
        int fixedNow = (int)fFixedCount.GetValue(null);
        IDictionary tracked = fTracker.GetValue(null) as IDictionary;
        if(tracked != null)
        {
            foreach(DictionaryEntry de in tracked)
            {
                Transform k = de.Key as Transform;
                if(k == null || k.GetComponent<Rigidbody2D>() == null) continue;
                object st = de.Value;
                sb.Append(" | " + k.name + " moves " + tPreferred.GetValue(st) + " (window from " + (Time.frameCount - (int)tWindowStart.GetValue(st)) + " frames ago: late " + tLate.GetValue(st) + " fixed " + tFixed.GetValue(st) + ", windows " + tWindows.GetValue(st) + ")");
            }
        }
        foreach(CinemachineVirtualCameraBase v in AllVcams())
        {
            if(v == null || (!v.isActiveAndEnabled && (prev == null || !ReferenceEquals(prev, v))))
            {
                continue;
            }
            sb.Append("\n      " + PathName(v.transform) + (v.isActiveAndEnabled ? "" : " (off)") + " valid " + v.PreviousStateIsValid + " state " + v.State.RawPosition.ToString("F4") + " id " + typeof(CinemachineVirtualCameraBase).GetField("m_ActivationId", Any).GetValue(v));
            CinemachineVirtualCamera vc = v as CinemachineVirtualCamera;
            CinemachineTransposer tr = vc != null ? vc.GetCinemachineComponent<CinemachineTransposer>() : null;
            if(tr != null)
            {
                FieldInfo f = typeof(CinemachineTransposer).GetField("m_PreviousTargetPosition", Any);
                if(f != null) sb.Append(" follow " + ((Vector3)f.GetValue(tr)).ToString("F4"));
            }
            if(statuses != null && statuses.Contains(v))
            {
                object st = statuses[v];
                sb.Append(" updated " + (fixedNow - (int)sFixed.GetValue(st)) + " fixed ago, " + sMode.GetValue(st) + " dt " + ((float)sDelta.GetValue(st)).ToString("F4"));
            }
        }
        return sb.ToString();
    }

    private static string PathName(Transform t)
    {
        return t.parent != null ? t.parent.name + "/" + t.name : t.name;
    }

    private static string BlendText(CinemachineBlend b)
    {
        if(b == null)
        {
            return "null";
        }
        return CamName(b.CamA) + " -> " + CamName(b.CamB) + " t " + b.TimeInBlend.ToString("F4") + "/" + b.Duration.ToString("F3");
    }

    private static string CamName(ICinemachineCamera c)
    {
        if(c == null)
        {
            return "-";
        }
        CinemachineVirtualCameraBase v = c as CinemachineVirtualCameraBase;
        if(v != null)
        {
            return PathName(v.transform);
        }
        CinemachineBlend inner = sourceT.IsInstanceOfType(c) ? pSourceBlend.GetValue(c, null) as CinemachineBlend : null;
        return "[" + BlendText(inner) + "]";
    }

    public static void Restore(TasCamSnapshot s)
    {
        Init();
        if(!Works || s == null)
        {
            return;
        }
        CinemachineBrain brain = Brain();
        if(brain == null)
        {
            return;
        }
        CinemachineCore core = CinemachineCore.Instance;
        IDictionary statuses = fUpdateStatus.GetValue(core) as IDictionary;
        int fixedNow = (int)fFixedCount.GetValue(null);
        for(int i = 0; i < s.Vcams.Length; i++)
        {
            CinemachineVirtualCameraBase vcam = s.Vcams[i];
            if(vcam == null)
            {
                continue;
            }
            // Only if the camera is still built the same way (it always is, unless the level was reloaded)
            TargetsOf(vcam, targetList);
            object[] saved = s.Targets[i];
            if(saved == null)
            {
                continue;
            }
            bool same = targetList.Count == saved.Length;
            for(int t = 0; same && t < saved.Length; t++)
            {
                same = ReferenceEquals(targetList[t], saved[t]);
            }
            if(!same)
            {
                vcam.PreviousStateIsValid = false;
                continue;
            }
            object[] values = s.Values[i];
            int k = 0;
            foreach(object t in saved)
            {
                foreach(FieldInfo f in StateFields(t.GetType()))
                {
                    f.SetValue(t, values[k++]);
                }
            }
            if(statuses != null && statuses.Contains(vcam))
            {
                if(s.HasStatus[i])
                {
                    object st = statuses[vcam];
                    sFixed.SetValue(st, fixedNow - s.FixedAgo[i]);
                    sFrame.SetValue(st, Time.frameCount - s.LateAgo[i]);
                    sMode.SetValue(st, s.Mode[i]);
                    sDelta.SetValue(st, s.LastDelta[i]);
                }
                else
                {
                    // It hadn't been updated yet on that frame
                    statuses.Remove(vcam);
                }
            }
        }
        fRoundRobin.SetValue(core, s.RoundRobin);
        restoreEpoch++;
        pending = s;
        ApplyFrameCounts(s, Time.frameCount + 1);
        // Equal room cameras are picked by which was switched on last, and that order was just restored too
        fSorted.SetValue(core, false);

        map.Clear();
        IList frames = (IList)fFrameStack.GetValue(brain);
        if(frames != null)
        {
            if(frames.Count == 0)
            {
                frames.Add(Activator.CreateInstance(frameT, true));
            }
            if(s.Blend != null)
            {
                CopyIn((BlendCopy)s.Blend, (CinemachineBlend)fFrameBlend.GetValue(frames[0]));
                fFrameStart.SetValue(frames[0], s.BlendStart);
            }
        }
        if(s.Live != null)
        {
            CopyIn((BlendCopy)s.Live, (CinemachineBlend)fLive.GetValue(brain));
        }
        fPrevActive.SetValue(brain, s.PrevActive);
        fPrevActiveGo.SetValue(brain, s.PrevActiveGo);
        fCurState.SetValue(brain, s.CurrentState);
        map.Clear();

        // And the camera itself, so the right picture shows straight away
        brain.transform.SetPositionAndRotation(s.CamPos, s.CamRot);
        Camera cam = brain.OutputCamera;
        if(cam != null && s.CamSize > 0f)
        {
            cam.orthographicSize = s.CamSize;
        }
    }

    // ---------- Copying blends (they can contain other blends, and the same one can appear twice) ----------

    private static BlendCopy CopyOut(CinemachineBlend b)
    {
        if(b == null)
        {
            return null;
        }
        BlendCopy c = new BlendCopy();
        c.CamA = CopyCamOut(b.CamA);
        c.CamB = CopyCamOut(b.CamB);
        c.Curve = b.BlendCurve;
        c.Time = b.TimeInBlend;
        c.Duration = b.Duration;
        return c;
    }

    private static object CopyCamOut(ICinemachineCamera cam)
    {
        if(cam == null)
        {
            return null;
        }
        if(cam is UnityEngine.Object)
        {
            map[cam] = cam;
            return cam;
        }
        if(sourceT.IsInstanceOfType(cam))
        {
            object done;
            if(map.TryGetValue(cam, out done))
            {
                return done;
            }
            SourceCopy sc = new SourceCopy();
            map[cam] = sc;
            sc.Blend = CopyOut(pSourceBlend.GetValue(cam, null) as CinemachineBlend);
            sc.State = fSourceState.GetValue(cam);
            return sc;
        }
        return null;
    }

    private static void CopyIn(BlendCopy c, CinemachineBlend into)
    {
        if(into == null)
        {
            return;
        }
        into.CamA = MakeCam(c.CamA);
        into.CamB = MakeCam(c.CamB);
        into.BlendCurve = c.Curve;
        into.TimeInBlend = c.Time;
        into.Duration = c.Duration;
    }

    private static ICinemachineCamera MakeCam(object saved)
    {
        if(saved == null)
        {
            return null;
        }
        SourceCopy sc = saved as SourceCopy;
        if(sc == null)
        {
            return saved as ICinemachineCamera;
        }
        object done;
        if(map.TryGetValue(sc, out done))
        {
            return (ICinemachineCamera)done;
        }
        CinemachineBlend inner = new CinemachineBlend(null, null, null, 0, 0);
        object source = sourceCtor.Invoke(new object[] { inner });
        map[sc] = source;
        if(sc.Blend != null)
        {
            CopyIn(sc.Blend, inner);
        }
        else
        {
            pSourceBlend.SetValue(source, null, null);
        }
        fSourceState.SetValue(source, sc.State);
        return (ICinemachineCamera)source;
    }
}
