using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Buttons = PeteInput.Buttons;

// Everything you see on screen in the TAS tool: the info panel, the input grid,
// the speed graph, the tabs, and all the keyboard shortcuts.
public class TasHud : MonoBehaviour
{
    public static TasHud I;
    public bool Hidden;

    private enum Tab { Movie, Ghosts, Lines, Search, Video, Settings, Help }
    private Tab tab = Tab.Help;

    private const float RefHeight = 900f;
    private float scale = 1f;
    private float vw, vh;

    // Screen areas covered by the HUD (in real pixels, y from the top), so clicks there don't reach the level
    private readonly List<Rect> uiRects = new List<Rect>();

    private GUIStyle box, label, small, bold, big, rowLabel, cell, cellOn, button, tabButton, toggle, msg, field, header, tag;
    private Texture2D white;
    private Texture2D dark;
    private Material glMat;
    private bool stylesReady;

    // Input grid
    private int rollTop = 1;
    private bool rollFollow = true;
    private bool painting;
    private int paintColumn;
    private bool paintValue;
    private readonly Dictionary<int, Buttons> paintEdits = new Dictionary<int, Buttons>();
    private string markerText = "";
    private int markerFrame = -1;

    // Selected range of frames in the input grid (Shift+click frame numbers)
    private int selA = -1, selB = -1;
    private int shiftCount = 1;
    private string presetName = "";
    private string presetDeleteArmed;
    private Vector2 presetScroll;
    private string[] presets = new string[0];
    private int[] presetLengths = new int[0];
    private float presetsRefreshed;

    public bool HasSelection { get { return selA >= 1 && selB >= 1; } }
    public int SelLo { get { return Mathf.Min(selA, selB); } }
    public int SelHi { get { return Mathf.Max(selA, selB); } }
    public int SelCount { get { return HasSelection ? SelHi - SelLo + 1 : 0; } }

    public void Select(int from, int to)
    {
        selA = from;
        selB = to;
    }

    public void ClearSelection()
    {
        selA = selB = -1;
    }

    // Key repeat for stepping
    private float holdStart;
    private float lastRepeat;

    // Movie tab
    private string saveName = "";
    private Vector2 fileScroll;
    private string[] files = new string[0];
    private float filesRefreshed;

    // Lines tab / placing
    public bool PlacingLine;
    public bool PlacingVertical = true;

    // Search tab
    private int searchLineIndex;
    private TasSearch.Method searchMethod = TasSearch.Method.Beam;
    private int searchWindow = 30;
    private int searchHold = 20;
    private int searchBeam = 16;
    private int searchMaxFrames = 250;
    private bool searchAttacks;
    private Buttons searchDir = Buttons.Right;
    private TasSearch lastSearch;
    private Vector2 ghostScroll, lineScroll;

    private TasManager M { get { return TasManager.I; } }
    private bool ctrlHeld;

    private void Awake()
    {
        I = this;
    }

    // Used by the screenshot test
    public void ShowTab(string name)
    {
        tab = (Tab)Enum.Parse(typeof(Tab), name);
        Hidden = false;
    }

    public void ShowSearch(TasSearch s)
    {
        lastSearch = s;
    }

    // =====================================================================
    //  Keyboard shortcuts
    // =====================================================================

    // ---------- The still picture ----------
    // Every normal frame, the finished screen (level and panels) is copied. While the TAS goes back, reloads or
    // searches, it replays at full speed with the level hidden, and that copy is shown instead of an empty screen.
    private RenderTexture lastScreen;
    private bool lastScreenValid;
    private float busySince;
    private bool wasBusy;
    public static bool StillFlipped;

    public bool ShowingStill
    {
        get { return M != null && M.FastMode && lastScreenValid && lastScreen != null && lastScreen.width == Screen.width && lastScreen.height == Screen.height; }
    }

    private IEnumerator Start()
    {
        // The screen copy comes out upside down on Direct3D (where texture rows start at the top)
        StillFlipped = SystemInfo.graphicsUVStartsAtTop;
        WaitForEndOfFrame end = new WaitForEndOfFrame();
        while(true)
        {
            yield return end;
            CaptureScreen();
        }
    }

    private void TrackBusy()
    {
        if(M.Busy && !wasBusy)
        {
            busySince = Time.unscaledTime;
        }
        wasBusy = M.Busy;
    }

    private void CaptureScreen()
    {
        if(M == null || M.FastMode || Application.isBatchMode || M.CurrentMode == TasManager.Mode.Video || M.CurrentMode == TasManager.Mode.VideoEndScreen)
        {
            return;
        }
        if(M.Player == null || !M.Player.Valid || Screen.width < 8 || Screen.height < 8)
        {
            return;
        }
        try
        {
            if(lastScreen == null || lastScreen.width != Screen.width || lastScreen.height != Screen.height)
            {
                if(lastScreen != null)
                {
                    lastScreen.Release();
                    Destroy(lastScreen);
                }
                lastScreen = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32);
                lastScreen.hideFlags = HideFlags.HideAndDontSave;
            }
            ScreenCapture.CaptureScreenshotIntoRenderTexture(lastScreen);
            lastScreenValid = true;
        }
        catch(Exception ex)
        {
            lastScreenValid = false;
            Debug.LogWarning("[TAS] Could not keep a copy of the screen: " + ex.Message);
        }
    }

    private void Update()
    {
        if(M == null)
        {
            return;
        }
        TrackBusy();
        if(GUIUtility.keyboardControl != 0)
        {
            // Typing in a text box
            if(Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Return))
            {
                GUIUtility.keyboardControl = 0;
            }
            return;
        }
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        ctrlHeld = ctrl;

        if(Input.GetKeyDown(KeyCode.Escape))
        {
            if(PlacingLine) PlacingLine = false;
            else if(HasSelection) ClearSelection();
            else if(M.CurrentMode == TasManager.Mode.Video || M.CurrentMode == TasManager.Mode.VideoEndScreen) M.StopVideo();
            else if(M.CurrentMode == TasManager.Mode.Searching) M.CancelSearch();
            else if(Hidden) Hidden = false;
        }
        if(M.CurrentMode == TasManager.Mode.Video || M.CurrentMode == TasManager.Mode.VideoEndScreen)
        {
            return;
        }
        if(Input.GetKeyDown(KeyCode.H)) Hidden = !Hidden;
        if(Input.GetKeyDown(KeyCode.F1)) { tab = Tab.Help; Hidden = false; }
        if(Input.GetKeyDown(KeyCode.G)) { tab = Tab.Ghosts; Hidden = false; }
        if(Input.GetKeyDown(KeyCode.L)) { tab = Tab.Lines; Hidden = false; }
        if(Input.GetKeyDown(KeyCode.M) && TasFreeCam.I != null) TasFreeCam.I.Toggle();
        if(Input.GetKeyDown(KeyCode.X)) ToggleHitboxes();
        if(Input.GetKeyDown(KeyCode.T)) TogglePredictions();
        if(Input.GetKeyDown(KeyCode.C) && !ctrlHeld && TasFreeCam.I != null) TasFreeCam.I.ToggleCenter();
        if(M.Busy)
        {
            return;
        }
        if(Input.GetKeyDown(KeyCode.P)) M.TogglePlay();
        if(Input.GetKeyDown(KeyCode.Q)) ToggleRecord();
        if(!ctrl && Input.GetKeyDown(KeyCode.V)) M.Verify();
        if(Input.GetKeyDown(KeyCode.Home)) M.Seek(0);
        if(Input.GetKeyDown(KeyCode.End)) M.Seek(M.Movie.Length);
        if(ctrl && Input.GetKeyDown(KeyCode.S)) M.SaveMovie(M.MovieName);
        if(ctrl && Input.GetKeyDown(KeyCode.O)) { tab = Tab.Movie; Hidden = false; }
        if(ctrl && Input.GetKeyDown(KeyCode.Z)) { if(shift) M.Redo(); else M.Undo(); }
        if(ctrl && Input.GetKeyDown(KeyCode.Y)) M.Redo();
        if(ctrl && Input.GetKeyDown(KeyCode.C)) CopySelection();
        if(ctrl && Input.GetKeyDown(KeyCode.V)) PasteInputs(M.Clipboard, "the copied frames");
        if(Input.GetKeyDown(KeyCode.Delete)) DeleteSelectionOrFrames();

        StepKey(KeyCode.F, shift ? 10 : 1, true);
        StepKey(KeyCode.B, shift ? 10 : 1, false);

        if(PlacingLine && Input.GetMouseButtonDown(0) && !PointerOverUi(Input.mousePosition))
        {
            Camera cam = Camera.main;
            if(cam != null)
            {
                Vector3 w = cam.ScreenToWorldPoint(Input.mousePosition);
                TasLine line = new TasLine();
                line.Vertical = PlacingVertical;
                line.Center = new Vector2(w.x, w.y);
                line.Name = (M.Lines.Count == 0 ? "Finish" : "Line " + (M.Lines.Count + 1));
                line.Color = LineColor(M.Lines.Count);
                M.Lines.Add(line);
                M.RouteVersion++;
                PlacingLine = false;
                M.Say("Placed " + line.Name + ". The Lines tab shows which route reaches it first.");
            }
        }
    }

    private void StepKey(KeyCode key, int amount, bool forward)
    {
        if(Input.GetKeyDown(key))
        {
            holdStart = Time.unscaledTime;
            lastRepeat = Time.unscaledTime;
            DoStep(amount, forward);
        }
        else if(Input.GetKey(key) && Time.unscaledTime - holdStart > 0.35f && Time.unscaledTime - lastRepeat > 0.03f)
        {
            lastRepeat = Time.unscaledTime;
            DoStep(amount, forward);
        }
    }

    private void DoStep(int amount, bool forward)
    {
        if(forward) M.StepForward(amount);
        else M.StepBack(amount);
    }

    private void ToggleRecord()
    {
        M.Recording = !M.Recording;
        M.Say(M.Recording
            ? "Recording ON: play with the normal game controls (keyboard, mouse or controller) and step with F or play with P."
            : "Recording OFF: stepping plays the inputs already in the movie.");
    }

    // The part of the screen (in pixels, y from the top) not covered by the panels
    public Rect OpenArea()
    {
        if(Hidden || M == null || M.CurrentMode == TasManager.Mode.Video || M.CurrentMode == TasManager.Mode.VideoEndScreen)
        {
            return new Rect(0, 0, Screen.width, Screen.height);
        }
        float left = (336f + 16f) * scale;
        float right = (292f + 16f) * scale;
        float bottom = 166f * scale;
        return new Rect(left, 0, Mathf.Max(100f, Screen.width - left - right), Mathf.Max(100f, Screen.height - bottom));
    }

    public bool PointerOverUi(Vector2 mouse)
    {
        if(Hidden || M == null)
        {
            return false;
        }
        Vector2 p = new Vector2(mouse.x, Screen.height - mouse.y);
        foreach(Rect r in uiRects)
        {
            if(r.Contains(p))
            {
                return true;
            }
        }
        return false;
    }

    // ---------- Selection, copy and paste ----------

    private void CopySelection()
    {
        if(!HasSelection)
        {
            M.Say("Select some frames first: Shift+click two frame numbers in the INPUTS grid.");
            return;
        }
        M.CopyFrames(SelLo, SelCount);
    }

    // Puts a piece of inputs in. With frames selected, the piece replaces them (and everything after moves
    // by the difference in length). Otherwise it goes in at the next frame, pushing everything after later.
    public void PasteInputs(List<Buttons> inputs, string what)
    {
        if(inputs == null || inputs.Count == 0)
        {
            M.Say("Nothing to paste yet. Copy some frames (Ctrl+C) or pick a preset.");
            return;
        }
        if(M.Busy)
        {
            return;
        }
        List<Buttons> piece = new List<Buttons>(inputs);
        if(HasSelection)
        {
            int at = SelLo, removed = SelCount;
            M.ReplaceFrames(at, removed, piece);
            int moved = piece.Count - removed;
            M.Say("Replaced frames " + at + "-" + (at + removed - 1) + " with " + what + " (" + piece.Count + " frames)." +
                (moved == 0 ? "" : " Everything after moved " + Mathf.Abs(moved) + " frames " + (moved > 0 ? "later." : "earlier.")), 8f);
            Select(at, at + piece.Count - 1);
        }
        else
        {
            int at = M.Frame + 1;
            M.InsertFrames(at, piece);
            M.Say("Pasted " + what + " (" + piece.Count + " frames) at frame " + at + ". Everything after moved " + piece.Count + " frames later.", 8f);
            Select(at, at + piece.Count - 1);
        }
    }

    private void DeleteSelectionOrFrames()
    {
        if(M.Busy)
        {
            return;
        }
        if(HasSelection)
        {
            int at = SelLo, count = SelCount;
            M.DeleteFrames(at, count);
            M.Say("Deleted frames " + at + "-" + (at + count - 1) + ". Everything after moved " + count + " frames earlier.");
            ClearSelection();
        }
        else
        {
            M.Say("Select some frames first (Shift+click frame numbers), or use \"Earlier -N\" to delete frames after the current one.");
        }
    }

    private void ToggleHitboxes()
    {
        TasDraw.ShowHitboxes = !TasDraw.ShowHitboxes;
        if(M != null)
        {
            M.Say(TasDraw.ShowHitboxes
                ? "Hitboxes ON: green = Pete, orange = sword swing, red = enemies & spikes, blue = pickups & orbs, gold = finish."
                : "Hitboxes OFF.");
        }
    }

    private void TogglePredictions()
    {
        TasPredict.Show = !TasPredict.Show;
        if(M != null)
        {
            M.Say(TasPredict.Show
                ? "Prediction lines ON: green = where your inputs take Pete, blue = hold jump, yellow = let go of jump, red = if an enemy hits you, orange = pogo bounce."
                : "Prediction lines OFF.");
        }
    }

    private void PredictionControls()
    {
        GUILayout.BeginHorizontal();
        bool show = GUILayout.Toggle(TasPredict.Show, " Prediction lines (T)", toggle);
        if(show != TasPredict.Show) TogglePredictions();
        GUILayout.FlexibleSpace();
        if(GUILayout.Button("-", button, GUILayout.Width(24))) TasPredict.Frames = Mathf.Max(25, TasPredict.Frames - 25);
        GUILayout.Label((TasPredict.Frames * TasManager.Tick).ToString("0.0") + " s", label, GUILayout.Width(40));
        if(GUILayout.Button("+", button, GUILayout.Width(24))) TasPredict.Frames = Mathf.Min(250, TasPredict.Frames + 25);
        GUILayout.EndHorizontal();
        if(!TasPredict.Show)
        {
            return;
        }
        GUILayout.BeginHorizontal();
        TasPredict.ShowPath = ColorToggle(TasPredict.ShowPath, TasPredict.PathColor, "Your path", 110);
        TasPredict.PathToEnd = GUILayout.Toggle(TasPredict.PathToEnd, new GUIContent(" ...to where inputs end", "Follow the whole movie, then let go and show where Pete stops"), toggle);
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        TasPredict.ShowLongJump = ColorToggle(TasPredict.ShowLongJump, TasPredict.HoldColor, "Longest jump", 140);
        TasPredict.ShowShortJump = ColorToggle(TasPredict.ShowShortJump, TasPredict.TapColor, "Shortest jump");
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        TasPredict.ShowLetGo = ColorToggle(TasPredict.ShowLetGo, TasPredict.ReleaseColor, "Let go of jump", 140);
        TasPredict.ShowHits = ColorToggle(TasPredict.ShowHits, TasPredict.HitColor, "If an enemy hits you");
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        TasPredict.ShowAttacks = ColorToggle(TasPredict.ShowAttacks, TasPredict.PogoColor, "Attacks", 140);
        TasPredict.ShowKnockback = ColorToggle(TasPredict.ShowKnockback, new Color(1f, 0.15f, 0.15f), "Knockback & labels");
        GUILayout.EndHorizontal();
        TasPredict pr = TasPredict.I;
        if(pr != null)
        {
            string acc;
            if(pr.Problem.Length > 0)
            {
                acc = "<color=#ff6060>Couldn't work out the lines: " + pr.Problem + "</color>";
            }
            else if(pr.PathError >= 0f)
            {
                bool good = pr.PathError < 0.01f;
                acc = "Green line vs the real route (" + pr.PathErrorFrames + " frames ahead): " + (good ? "<color=#60ff70>" : "<color=#ffd040>") + "off by " + pr.PathError.ToString("0.000") + "</color>";
            }
            else
            {
                acc = "Lines show where Pete goes from this frame. Dots every 10 frames, bars where he lands.";
            }
            GUILayout.Label(acc + "  <color=#888888>(" + pr.LastMs.ToString("0.0") + " ms)</color>", small);
        }
    }

    private bool ColorToggle(bool on, Color c, string text, float width = 0f)
    {
        Color old = GUI.color;
        GUI.color = c;
        GUILayout.Label("■", label, GUILayout.Width(12));
        GUI.color = old;
        return width > 0f ? GUILayout.Toggle(on, " " + text, toggle, GUILayout.Width(width)) : GUILayout.Toggle(on, " " + text, toggle);
    }

    // Text next to the prediction lines ("HIT", "knockback over (10 f)", "lands", "stops here")
    private readonly List<Rect> placedTags = new List<Rect>();

    private void DrawPredictionLabels()
    {
        TasPredict pr = TasPredict.I;
        if(pr == null || !TasPredict.ShowKnockback || !TasPredict.Active(M) || M.Busy || Event.current.type != EventType.Repaint)
        {
            return;
        }
        Camera cam = Camera.main;
        if(cam == null)
        {
            return;
        }
        placedTags.Clear();
        Color old = GUI.color;
        foreach(TasPrediction p in pr.Lines)
        {
            foreach(KeyValuePair<int, string> l in p.Labels)
            {
                if(l.Key < 0 || l.Key >= p.Pos.Count)
                {
                    continue;
                }
                Vector3 sp = cam.WorldToScreenPoint(new Vector3(p.Pos[l.Key].x, p.Pos[l.Key].y, 0f));
                Vector2 at = new Vector2(sp.x / scale, (Screen.height - sp.y) / scale);
                GUIContent content = new GUIContent(l.Value);
                Vector2 size = tag.CalcSize(content);
                Rect r = new Rect(at.x - size.x * 0.5f - 3f, at.y - size.y - 16f, size.x + 6f, size.y);
                // Don't stack labels on top of each other
                for(int tries = 0; tries < 6; tries++)
                {
                    bool clash = false;
                    foreach(Rect o in placedTags)
                    {
                        if(o.Overlaps(r)) { clash = true; break; }
                    }
                    if(!clash) break;
                    r.y -= size.y + 2f;
                }
                placedTags.Add(r);
                GUI.color = new Color(0f, 0f, 0f, 0.72f);
                GUI.DrawTexture(r, Texture2D.whiteTexture);
                GUI.color = old;
                tag.normal.textColor = p.Color;
                GUI.Label(r, content, tag);
            }
        }
        GUI.color = old;
    }

    private void HitboxLegend()
    {
        GUILayout.BeginHorizontal();
        LegendItem(TasDraw.PeteColor, "Pete");
        LegendItem(TasDraw.SwordColor, "Sword");
        LegendItem(TasDraw.EnemyColor, "Enemy/spikes");
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        LegendItem(TasDraw.PickupColor, "Pickup/orb");
        LegendItem(TasDraw.FinishColor, "Finish");
        if(TasDraw.ShowLevelCollision) LegendItem(new Color(1f, 1f, 1f, 0.8f), "Level");
        GUILayout.EndHorizontal();
        GUILayout.Label("Faint red = where an enemy notices Pete or turns around.", small);
    }

    private void LegendItem(Color c, string text)
    {
        Color old = GUI.color;
        GUI.color = c;
        GUILayout.Label("■", label, GUILayout.Width(12));
        GUI.color = old;
        GUILayout.Label(text, small, GUILayout.Width(84));
    }

    public static Color LineColor(int i)
    {
        Color[] c = { new Color(1f, 0.3f, 0.3f), new Color(1f, 0.85f, 0.2f), new Color(0.3f, 1f, 0.5f), new Color(0.4f, 0.8f, 1f), new Color(1f, 0.5f, 1f) };
        return c[i % c.Length];
    }

    // =====================================================================
    //  Drawing
    // =====================================================================

    private void MakeStyles()
    {
        if(stylesReady && white != null && dark != null)
        {
            return;
        }
        // HideAndDontSave stops Unity from throwing these away when the level reloads
        white = new Texture2D(1, 1);
        white.hideFlags = HideFlags.HideAndDontSave;
        white.SetPixel(0, 0, Color.white);
        white.Apply();
        dark = new Texture2D(1, 1);
        dark.hideFlags = HideFlags.HideAndDontSave;
        dark.SetPixel(0, 0, new Color(0.06f, 0.07f, 0.09f, 0.88f));
        dark.Apply();

        box = new GUIStyle(GUI.skin.box);
        box.normal.background = dark;
        box.padding = new RectOffset(8, 8, 6, 6);
        label = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true };
        label.normal.textColor = new Color(0.9f, 0.92f, 0.95f);
        label.margin = new RectOffset(0, 0, 0, 0);
        label.padding = new RectOffset(0, 0, 1, 1);
        small = new GUIStyle(label) { fontSize = 11 };
        small.normal.textColor = new Color(0.7f, 0.74f, 0.8f);
        bold = new GUIStyle(label) { fontStyle = FontStyle.Bold };
        big = new GUIStyle(label) { fontSize = 20, fontStyle = FontStyle.Bold };
        header = new GUIStyle(label) { fontSize = 12, fontStyle = FontStyle.Bold };
        header.normal.textColor = new Color(1f, 0.82f, 0.35f);
        rowLabel = new GUIStyle(label) { fontSize = 11, alignment = TextAnchor.MiddleRight };
        cell = new GUIStyle(GUI.skin.box) { fontSize = 11, alignment = TextAnchor.MiddleCenter, margin = new RectOffset(0, 0, 0, 0), padding = new RectOffset(0, 0, 0, 0) };
        cell.normal.background = white;
        cell.normal.textColor = new Color(0.45f, 0.48f, 0.55f);
        cellOn = new GUIStyle(cell) { fontStyle = FontStyle.Bold };
        cellOn.normal.textColor = Color.black;
        button = new GUIStyle(GUI.skin.button) { fontSize = 12 };
        tabButton = new GUIStyle(GUI.skin.button) { fontSize = 11, padding = new RectOffset(1, 1, 2, 2) };
        toggle = new GUIStyle(GUI.skin.toggle) { fontSize = 12 };
        toggle.normal.textColor = label.normal.textColor;
        toggle.onNormal.textColor = label.normal.textColor;
        msg = new GUIStyle(label) { fontSize = 14, wordWrap = true, alignment = TextAnchor.MiddleCenter };
        field = new GUIStyle(GUI.skin.textField) { fontSize = 12 };
        tag = new GUIStyle(GUI.skin.label) { fontSize = 11, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, padding = new RectOffset(2, 2, 1, 1) };

        Shader shader = Shader.Find("Hidden/Internal-Colored");
        glMat = new Material(shader);
        glMat.hideFlags = HideFlags.HideAndDontSave;
        glMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        glMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        glMat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
        glMat.SetInt("_ZWrite", 0);
        glMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
        stylesReady = true;
    }

    private void OnGUI()
    {
        if(M == null)
        {
            return;
        }
        MakeStyles();
        if(ShowingStill && Event.current.type == EventType.Repaint)
        {
            // While the TAS replays at full speed with the level hidden, keep showing the screen as it was
            GUI.matrix = Matrix4x4.identity;
            GUI.DrawTextureWithTexCoords(new Rect(0, 0, Screen.width, Screen.height), lastScreen, StillFlipped ? new Rect(0, 1, 1, -1) : new Rect(0, 0, 1, 1), false);
        }
        scale = Mathf.Clamp(Screen.height / RefHeight, 0.7f, 2.5f);
        vw = Screen.width / scale;
        vh = Screen.height / scale;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
        if(Event.current.type == EventType.Layout)
        {
            uiRects.Clear();
        }
        DrawPredictionLabels();

        if(M.CurrentMode == TasManager.Mode.Video || M.CurrentMode == TasManager.Mode.VideoEndScreen)
        {
            DrawVideoOverlay();
            return;
        }
        TrackBusy();
        if(M.Busy)
        {
            // Quick jumps (like stepping back) just keep the still picture up; longer ones show progress
            if(!ShowingStill || Time.unscaledTime - busySince > 0.35f)
            {
                DrawBusy();
            }
            return;
        }
        if(Hidden)
        {
            GUI.Label(new Rect(8, vh - 22, 400, 20), "TAS panel hidden. Press H to show it.", small);
            return;
        }

        if(M.Player == null || !M.Player.Valid)
        {
            return;
        }

        float leftW = 336f;
        DrawStatus(new Rect(8, 8, leftW, 398));
        DrawTabs(new Rect(8, 414, leftW, vh - 422));
        float rollW = 292f;
        DrawRoll(new Rect(vw - rollW - 8, 8, rollW, vh - 16));
        float gx = leftW + 16;
        float gw = vw - rollW - 16 - gx;
        if(gw > 200)
        {
            DrawGraph(new Rect(gx, vh - 158, gw, 150));
        }
        DrawMessage(new Rect(gx, 8, Mathf.Max(200, gw), 60));
        if(PlacingLine)
        {
            Rect r = new Rect(gx, 72, Mathf.Max(200, gw), 26);
            GUI.Box(r, "", box);
            GUI.Label(r, "Click in the level to place the line (" + (PlacingVertical ? "up-down" : "left-right") + "). Esc cancels.", msg);
        }
        if(TasFreeCam.I != null && TasFreeCam.I.Active)
        {
            Rect r = new Rect(gx, PlacingLine ? 102 : 72, Mathf.Max(200, gw), 26);
            GUI.Box(r, "", box);
            GUI.Label(r, "Free camera: scroll to zoom, right-drag to move, click the route to jump there. M to exit.", msg);
        }
    }

    private void Area(Rect r)
    {
        GUI.Box(r, "", box);
        if(Event.current.type == EventType.Layout || Event.current.type == EventType.Repaint)
        {
            Rect real = new Rect(r.x * scale, r.y * scale, r.width * scale, r.height * scale);
            if(!uiRects.Contains(real)) uiRects.Add(real);
        }
    }

    private static string Light(TasManager m)
    {
        if(m.DesyncFrame >= 0) return "<color=#ff5050>●</color>";
        return m.AllVerified ? "<color=#50e070>●</color>" : "<color=#ffd040>●</color>";
    }

    private void DrawStatus(Rect r)
    {
        Area(r);
        TasManager m = M;
        TasPlayerInfo p = m.Player;
        GUILayout.BeginArea(new Rect(r.x + 10, r.y + 6, r.width - 20, r.height - 12));

        GUILayout.BeginHorizontal();
        GUILayout.Label("<b>PETE TAS</b>  " + Light(m) + " <size=11>" + VerifyText(m) + "</size>", label);
        GUILayout.FlexibleSpace();
        GUILayout.Label(ModeText(m), small);
        GUILayout.EndHorizontal();

        GUILayout.Label("Frame " + m.Frame + "<size=12>  of " + m.Movie.Length + "</size>", big);
        GUILayout.Label("TAS time " + TasManager.FormatFrames(m.Frame) + "    In-game timer " + TasManager.FormatTime(p.TimerSeconds) + (p.TimerRunning ? "" : " (not started)"), small);

        GUILayout.Space(4);
        GUILayout.BeginHorizontal();
        if(GUILayout.Button(new GUIContent("|<", "Back to the start (Home)"), button, GUILayout.Width(34))) m.Seek(0);
        if(GUILayout.Button(new GUIContent("<<", "Back 10 frames (Shift+B)"), button, GUILayout.Width(34))) m.StepBack(10);
        if(GUILayout.Button(new GUIContent("<", "Back 1 frame (B)"), button, GUILayout.Width(34))) m.StepBack(1);
        if(GUILayout.Button(new GUIContent(m.CurrentMode == TasManager.Mode.Playing ? "Pause" : "Play", "Play / pause (P)"), button, GUILayout.Width(60))) m.TogglePlay();
        if(GUILayout.Button(new GUIContent(">", "Forward 1 frame (F)"), button, GUILayout.Width(34))) m.StepForward(1);
        if(GUILayout.Button(new GUIContent(">>", "Forward 10 frames (Shift+F)"), button, GUILayout.Width(34))) m.StepForward(10);
        if(GUILayout.Button(new GUIContent(">|", "Go to the end of the movie (End)"), button, GUILayout.Width(34))) m.Seek(m.Movie.Length);
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        Color old = GUI.backgroundColor;
        if(m.Recording) GUI.backgroundColor = new Color(1f, 0.35f, 0.35f);
        if(GUILayout.Button(m.Recording ? "● Recording" : "Record (Q)", button, GUILayout.Width(96))) ToggleRecord();
        GUI.backgroundColor = old;
        if(GUILayout.Button("Speed " + m.SpeedPercent + "%", button, GUILayout.Width(92)))
        {
            m.SpeedPercent = m.SpeedPercent == 100 ? 50 : m.SpeedPercent == 50 ? 25 : 100;
        }
        if(GUILayout.Button(new GUIContent("Verify (V)", "Replay from the start to check every frame is exact"), button)) m.Verify();
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUI.enabled = m.UndoCount > 0;
        if(GUILayout.Button("Undo  (Ctrl+Z)", button, GUILayout.Width(146))) m.Undo();
        GUI.enabled = m.RedoCount > 0;
        if(GUILayout.Button("Redo  (Ctrl+Y)", button)) m.Redo();
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        GUILayout.Space(6);
        GUILayout.Label("MOVEMENT", header);
        Vector2 pos = p.Position;
        Vector2 vel = p.Velocity;
        Row("Position", F(pos.x, 3) + ",  " + F(pos.y, 3));
        Row("Velocity", F(vel.x, 2) + ",  " + F(vel.y, 2) + " u/s   (" + F(vel.x * TasManager.Tick, 3) + ", " + F(vel.y * TasManager.Tick, 3) + " per frame)");
        Row("Speed", F(vel.magnitude, 2) + " u/s    Gravity x" + F(p.GravityScale, 2));
        if(m.Frame > 0 && m.Frame < m.Timeline.Count)
        {
            Vector2 moved = m.Timeline[m.Frame].Pos - m.Timeline[m.Frame - 1].Pos;
            Row("Last frame moved", F(moved.x, 4) + ",  " + F(moved.y, 4));
        }
        Row("On ground", YesNo(p.OnGround) + "     Jumping " + YesNo(p.IsJumping));
        Row("Coyote time", FramesLeft(p.CoyoteLeft) + "    Jump buffer " + FramesLeft(p.JumpBufferLeft));
        Row("Can move", p.CanMove ? "yes" : "<color=#ff8080>no (knockback " + FramesLeft(p.KnockbackLeft) + ")</color>");
        Row("HP / Orbs", p.Hp + "  /  " + p.Orbs + "     Attack ready " + YesNo(p.CanAttack));

        GUILayout.Space(4);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Inputs this frame", small, GUILayout.Width(110));
        Buttons cur = m.Movie.Get(m.Frame);
        InputBox("L", cur, Buttons.Left); InputBox("R", cur, Buttons.Right); InputBox("U", cur, Buttons.Up);
        InputBox("D", cur, Buttons.Down); InputBox("J", cur, Buttons.Jump); InputBox("A", cur, Buttons.Attack);
        GUILayout.EndHorizontal();
        if(m.DeathFrame >= 0) GUILayout.Label("<color=#ff6060>Pete dies on frame " + m.DeathFrame + "</color>", small);
        if(m.FinishFrame >= 0) GUILayout.Label("<color=#ffd040>Finished on frame " + m.FinishFrame + "</color>", small);
        GUILayout.EndArea();
    }

    private static string VerifyText(TasManager m)
    {
        if(m.DesyncFrame >= 0) return "desync at " + m.DesyncFrame;
        return m.AllVerified ? "exact" : "not verified";
    }

    private static string ModeText(TasManager m)
    {
        switch(m.CurrentMode)
        {
            case TasManager.Mode.Playing: return "playing " + m.SpeedPercent + "%" + (m.Recording ? " · rec" : "");
            case TasManager.Mode.Paused: return m.Recording ? "paused · rec" : "paused";
            case TasManager.Mode.Seeking: return "replaying";
            case TasManager.Mode.Searching: return "searching";
            case TasManager.Mode.Booting: return "loading";
            default: return m.CurrentMode.ToString().ToLower();
        }
    }

    private void Row(string name, string value)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(name, small, GUILayout.Width(110));
        GUILayout.Label(value, label);
        GUILayout.EndHorizontal();
    }

    private void InputBox(string text, Buttons cur, Buttons b)
    {
        bool on = (cur & b) != 0;
        Color old = GUI.backgroundColor;
        GUI.backgroundColor = on ? new Color(1f, 0.85f, 0.3f) : new Color(0.25f, 0.27f, 0.32f);
        GUILayout.Box(text, on ? cellOn : cell, GUILayout.Width(26), GUILayout.Height(20));
        GUI.backgroundColor = old;
    }

    private static string F(float v, int decimals)
    {
        return v.ToString("F" + decimals);
    }

    private static string YesNo(bool b)
    {
        return b ? "<color=#80ff90>yes</color>" : "no";
    }

    private static string FramesLeft(float seconds)
    {
        if(seconds <= 0f) return "0 f";
        return Mathf.CeilToInt(seconds / TasManager.Tick - 0.0001f) + " f";
    }

    private void DrawMessage(Rect r)
    {
        if(string.IsNullOrEmpty(M.Message) || Time.unscaledTime > M.MessageUntil)
        {
            return;
        }
        Area(r);
        GUI.Label(new Rect(r.x + 8, r.y + 4, r.width - 16, r.height - 8), M.Message, msg);
    }

    private void DrawBusy()
    {
        Rect r = new Rect(vw * 0.5f - 210, vh * 0.5f - 50, 420, M.CurrentMode == TasManager.Mode.Searching ? 104 : 74);
        Area(r);
        GUI.Label(new Rect(r.x + 10, r.y + 8, r.width - 20, 22), M.BusyText, msg);
        Rect bar = new Rect(r.x + 14, r.y + 38, r.width - 28, 16);
        Color old = GUI.color;
        GUI.color = new Color(0.2f, 0.22f, 0.26f);
        GUI.DrawTexture(bar, white);
        GUI.color = new Color(1f, 0.82f, 0.35f);
        GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(M.BusyProgress), bar.height), white);
        GUI.color = old;
        if(M.CurrentMode == TasManager.Mode.Searching)
        {
            GUI.Label(new Rect(r.x + 10, r.y + 58, r.width - 20, 18), "Tried " + (M.Search != null ? M.Search.Tried : 0) + " frames", small);
            if(GUI.Button(new Rect(r.x + r.width * 0.5f - 50, r.y + 76, 100, 22), "Stop (Esc)", button))
            {
                M.CancelSearch();
            }
        }
    }

    // ---------------------------------------------------------------
    //  Input grid ("piano roll")
    // ---------------------------------------------------------------
    private static readonly Buttons[] RollButtons = { Buttons.Left, Buttons.Right, Buttons.Up, Buttons.Down, Buttons.Jump, Buttons.Attack };
    private static readonly string[] RollNames = { "L", "R", "U", "D", "J", "A" };

    private void DrawRoll(Rect r)
    {
        Area(r);
        TasManager m = M;
        const float rowH = 17f;
        const float numW = 52f;
        const float colW = 30f;
        float top = r.y + 30;
        float bottomBar = 150f;
        int rows = Mathf.Max(5, (int)((r.height - 30 - bottomBar) / rowH));

        GUI.Label(new Rect(r.x + 8, r.y + 6, 120, 20), "INPUTS", header);
        rollFollow = GUI.Toggle(new Rect(r.x + r.width - 82, r.y + 6, 80, 20), rollFollow, " Follow", toggle);
        for(int c = 0; c < 6; c++)
        {
            GUI.Label(new Rect(r.x + 8 + numW + c * colW, r.y + 12, colW, 18), RollNames[c], small);
        }

        if(rollFollow)
        {
            rollTop = Mathf.Max(1, m.Frame + 1 - rows / 3);
        }
        Event e = Event.current;
        Rect gridRect = new Rect(r.x, top, r.width, rows * rowH);
        if(e.type == EventType.ScrollWheel && gridRect.Contains(e.mousePosition))
        {
            rollFollow = false;
            rollTop = Mathf.Max(1, rollTop + (int)Mathf.Sign(e.delta.y) * 3);
            e.Use();
        }

        int hoverFrame = -1;
        int hoverCol = -1;
        if(gridRect.Contains(e.mousePosition))
        {
            hoverFrame = rollTop + (int)((e.mousePosition.y - top) / rowH);
            float cx = e.mousePosition.x - (r.x + 8 + numW);
            hoverCol = cx >= 0 ? (int)(cx / colW) : -1;
            if(hoverCol > 5) hoverCol = -1;
        }

        for(int i = 0; i < rows; i++)
        {
            int f = rollTop + i;
            float y = top + i * rowH;
            Rect rowRect = new Rect(r.x + 4, y, r.width - 8, rowH - 1);
            Color bg = new Color(0.12f, 0.13f, 0.16f, 0.9f);
            if(f > m.Movie.Length) bg = new Color(0.09f, 0.09f, 0.1f, 0.9f);
            else if(f < m.Timeline.Count) bg = m.Timeline[f].Verified ? new Color(0.1f, 0.22f, 0.14f, 0.95f) : new Color(0.22f, 0.2f, 0.1f, 0.95f);
            if(f == m.DeathFrame || f == m.DesyncFrame) bg = new Color(0.4f, 0.1f, 0.1f, 0.95f);
            if(f == m.FinishFrame) bg = new Color(0.45f, 0.36f, 0.08f, 0.95f);
            if(f == m.Frame) bg = new Color(0.32f, 0.36f, 0.5f, 1f);
            if(f == m.Frame + 1) bg = Color.Lerp(bg, new Color(0.3f, 0.32f, 0.42f, 1f), 0.5f);
            DrawRect(rowRect, bg);

            string num = f.ToString();
            if(m.Movie.Markers.ContainsKey(f)) num = "◆ " + num;
            if(HasSelection && f >= SelLo && f <= SelHi)
            {
                DrawRect(new Rect(r.x + 4, y, numW - 2, rowH - 1), new Color(0.25f, 0.45f, 0.95f, 0.9f));
            }
            if(GUI.Button(new Rect(r.x + 4, y, numW - 4, rowH - 1), num, rowLabel))
            {
                if(Event.current.shift)
                {
                    // Shift+click: select from the first clicked frame to this one
                    if(!HasSelection) Select(f, f);
                    else selB = f;
                }
                else if(!m.Busy)
                {
                    m.Seek(f);
                }
            }
            Buttons b = m.Movie.Get(f);
            Buttons pending;
            if(paintEdits.TryGetValue(f, out pending))
            {
                b = pending;
            }
            for(int c = 0; c < 6; c++)
            {
                Rect cr = new Rect(r.x + 8 + numW + c * colW, y + 1, colW - 3, rowH - 3);
                bool on = (b & RollButtons[c]) != 0;
                DrawRect(cr, on ? new Color(1f, 0.82f, 0.3f) : new Color(0.2f, 0.22f, 0.27f));
                if(on)
                {
                    GUI.Label(cr, RollNames[c], cellOn);
                }
            }
            string mark;
            if(m.Movie.Markers.TryGetValue(f, out mark))
            {
                GUI.Label(new Rect(r.x + 10 + numW + 6 * colW, y, r.width - numW - 6 * colW - 14, rowH), mark, small);
            }
        }

        // Painting: click a cell to flip it, drag down the column to paint more frames the same way
        if(!m.Busy)
        {
            if(e.type == EventType.MouseDown && e.button == 0 && hoverFrame >= 1 && hoverCol >= 0)
            {
                painting = true;
                paintColumn = hoverCol;
                Buttons cur = m.Movie.Get(hoverFrame);
                paintValue = (cur & RollButtons[hoverCol]) == 0;
                paintEdits.Clear();
                lastPaintFrame = -1;
                Paint(hoverFrame);
                e.Use();
            }
            else if(e.type == EventType.MouseDrag && painting && hoverFrame >= 1)
            {
                Paint(hoverFrame);
                e.Use();
            }
            else if(e.rawType == EventType.MouseUp && painting)
            {
                painting = false;
                Dictionary<int, Buttons> edits = new Dictionary<int, Buttons>(paintEdits);
                paintEdits.Clear();
                m.SetInputs(edits);
                e.Use();
            }
        }

        // Bottom bar: selection, copy/paste, and moving later frames
        float by = r.y + r.height - bottomBar + 4;
        int editFrame = HasSelection ? SelLo : m.Frame + 1;
        string selText = HasSelection
            ? "Selected frames " + SelLo + "-" + SelHi + " (" + SelCount + ")"
            : "Shift+click frame numbers to select frames";
        GUI.Label(new Rect(r.x + 8, by, r.width - 16, 18), selText, small);
        float bw = (r.width - 16 - 9) / 4f;
        if(GUI.Button(new Rect(r.x + 8, by + 18, bw, 22), new GUIContent("Copy", "Copy the selected frames (Ctrl+C)"), button)) CopySelection();
        if(GUI.Button(new Rect(r.x + 11 + bw, by + 18, bw, 22), new GUIContent("Paste", "Paste the copied frames at the next frame, or in place of the selection (Ctrl+V)"), button)) PasteInputs(m.Clipboard, "the copied frames");
        if(GUI.Button(new Rect(r.x + 14 + bw * 2, by + 18, bw, 22), new GUIContent("Delete", "Delete the selected frames; everything after moves up (Delete key)"), button)) DeleteSelectionOrFrames();
        if(GUI.Button(new Rect(r.x + 17 + bw * 3, by + 18, bw, 22), new GUIContent("Unselect", "Clear the selection (Esc)"), button)) ClearSelection();

        GUI.Label(new Rect(r.x + 8, by + 44, r.width - 16, 18), "Move everything from frame " + editFrame + " on:", small);
        if(GUI.Button(new Rect(r.x + 8, by + 62, 22, 22), "-", button)) SetShiftCount(shiftCount - (Event.current.shift ? 10 : 1));
        ShiftCountField(new Rect(r.x + 32, by + 63, 28, 20));
        if(GUI.Button(new Rect(r.x + 62, by + 62, 22, 22), "+", button)) SetShiftCount(shiftCount + (Event.current.shift ? 10 : 1));
        if(GUI.Button(new Rect(r.x + 90, by + 62, 94, 22), new GUIContent("Later +" + shiftCount, "Insert " + shiftCount + " empty frames here, pushing everything after later"), button))
        {
            List<Buttons> blank = new List<Buttons>();
            for(int i = 0; i < shiftCount; i++) blank.Add(Buttons.None);
            m.InsertFrames(editFrame, blank);
            m.Say("Inserted " + shiftCount + " empty frames at frame " + editFrame + ". Everything after moved " + shiftCount + " frames later.");
        }
        if(GUI.Button(new Rect(r.x + 188, by + 62, 94, 22), new GUIContent("Earlier -" + shiftCount, "Delete " + shiftCount + " frames here, pulling everything after earlier"), button))
        {
            m.DeleteFrames(editFrame, shiftCount);
            m.Say("Deleted " + shiftCount + " frames at frame " + editFrame + ". Everything after moved " + shiftCount + " frames earlier.");
        }
        by += 58;

        if(markerFrame != m.Frame)
        {
            markerFrame = m.Frame;
            string t;
            markerText = m.Movie.Markers.TryGetValue(m.Frame, out t) ? t : "";
        }
        GUI.Label(new Rect(r.x + 8, by + 48, 140, 18), "Label for frame " + m.Frame + ":", small);
        markerText = GUI.TextField(new Rect(r.x + 8, by + 66, r.width - 80, 20), markerText, 40, field);
        if(GUI.Button(new Rect(r.x + r.width - 68, by + 66, 60, 20), "Set", button))
        {
            m.SetMarker(m.Frame, markerText.Trim());
            GUIUtility.keyboardControl = 0;
        }
    }

    private string shiftText = "1";

    private void SetShiftCount(int n)
    {
        shiftCount = Mathf.Clamp(n, 1, 9999);
        shiftText = shiftCount.ToString();
    }

    // A box you can type the number of frames into (Enter or Esc to finish)
    private void ShiftCountField(Rect r)
    {
        GUI.SetNextControlName("ShiftCount");
        string typed = GUI.TextField(r, shiftText, 4, field);
        if(typed != shiftText)
        {
            string digits = "";
            foreach(char c in typed)
            {
                if(c >= '0' && c <= '9') digits += c;
            }
            shiftText = digits;
            int n;
            if(int.TryParse(digits, out n) && n >= 1)
            {
                shiftCount = Mathf.Min(9999, n);
            }
        }
        if(GUI.GetNameOfFocusedControl() != "ShiftCount" && shiftText != shiftCount.ToString())
        {
            // Left empty (or 0): go back to the number in use
            shiftText = shiftCount.ToString();
        }
    }

    private int lastPaintFrame = -1;

    // Paints this frame, plus any rows a fast drag skipped over
    private void Paint(int frame)
    {
        int a = lastPaintFrame < 0 ? frame : Mathf.Min(lastPaintFrame, frame);
        int b = lastPaintFrame < 0 ? frame : Mathf.Max(lastPaintFrame, frame);
        for(int f = Mathf.Max(1, a); f <= b; f++)
        {
            PaintOne(f);
        }
        lastPaintFrame = frame;
    }

    private void PaintOne(int frame)
    {
        Buttons b;
        if(!paintEdits.TryGetValue(frame, out b))
        {
            b = M.Movie.Get(frame);
        }
        Buttons flag = RollButtons[paintColumn];
        b = paintValue ? (b | flag) : (b & ~flag);
        // Left and right at the same time cancel out, so painting one removes the other
        if(paintValue && flag == Buttons.Left) b &= ~Buttons.Right;
        if(paintValue && flag == Buttons.Right) b &= ~Buttons.Left;
        paintEdits[frame] = b;
    }

    private void DrawRect(Rect r, Color c)
    {
        if(Event.current.type != EventType.Repaint)
        {
            return;
        }
        Color old = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, white);
        GUI.color = old;
    }

    // ---------------------------------------------------------------
    //  Speed graph
    // ---------------------------------------------------------------
    private void DrawGraph(Rect r)
    {
        Area(r);
        TasManager m = M;
        GUI.Label(new Rect(r.x + 8, r.y + 4, 400, 18), "SPEED   <color=#55d8ff>— x speed</color>   <color=#ff70d0>— y speed</color>   <color=#888888>- - ghost x speed</color>", header);
        if(Event.current.type != EventType.Repaint)
        {
            return;
        }
        Rect plot = new Rect(r.x + 36, r.y + 24, r.width - 46, r.height - 32);
        int span = 200;
        int from = Mathf.Max(0, m.Frame - 150);
        int to = from + span;
        float maxV = 8f;
        for(int f = from; f <= to && f < m.Timeline.Count; f++)
        {
            Vector2 v = m.Timeline[f].Vel;
            maxV = Mathf.Max(maxV, Mathf.Abs(v.x), Mathf.Abs(v.y));
        }
        maxV = Mathf.Ceil(maxV / 4f) * 4f;
        GUI.Label(new Rect(r.x + 2, plot.y - 6, 34, 16), "+" + maxV, small);
        GUI.Label(new Rect(r.x + 2, plot.y + plot.height * 0.5f - 8, 34, 16), "0", small);
        GUI.Label(new Rect(r.x + 2, plot.yMax - 10, 34, 16), "-" + maxV, small);

        TasGhost ghost = null;
        foreach(TasGhost g in m.Ghosts)
        {
            if(g.Visible)
            {
                ghost = g;
                break;
            }
        }

        GL.PushMatrix();
        glMat.SetPass(0);
        GL.LoadPixelMatrix();
        GL.Begin(GL.LINES);
        // axes
        GLLine(plot, from, to, maxV, from, 0, to, 0, new Color(1, 1, 1, 0.25f));
        float cx = PlotX(plot, from, to, m.Frame);
        GLRaw(new Vector2(cx, plot.y), new Vector2(cx, plot.yMax), new Color(1f, 1f, 1f, 0.5f));
        if(ghost != null)
        {
            for(int f = from + 1; f <= to && f < ghost.Vel.Count; f++)
            {
                if(f % 2 == 0)
                {
                    GLLine(plot, from, to, maxV, f - 1, ghost.Vel[f - 1].x, f, ghost.Vel[f].x, new Color(0.6f, 0.6f, 0.6f, 0.8f));
                }
            }
        }
        for(int f = from + 1; f <= to && f < m.Timeline.Count; f++)
        {
            GLLine(plot, from, to, maxV, f - 1, m.Timeline[f - 1].Vel.x, f, m.Timeline[f].Vel.x, new Color(0.33f, 0.85f, 1f));
            GLLine(plot, from, to, maxV, f - 1, m.Timeline[f - 1].Vel.y, f, m.Timeline[f].Vel.y, new Color(1f, 0.45f, 0.82f));
        }
        GL.End();
        GL.PopMatrix();
    }

    private float PlotX(Rect plot, int from, int to, float f)
    {
        return (plot.x + (f - from) / (float)(to - from) * plot.width) * scale;
    }

    private void GLLine(Rect plot, int from, int to, float maxV, float f0, float v0, float f1, float v1, Color c)
    {
        float x0 = PlotX(plot, from, to, f0);
        float x1 = PlotX(plot, from, to, f1);
        float y0 = (plot.y + plot.height * 0.5f - Mathf.Clamp(v0 / maxV, -1, 1) * plot.height * 0.5f) * scale;
        float y1 = (plot.y + plot.height * 0.5f - Mathf.Clamp(v1 / maxV, -1, 1) * plot.height * 0.5f) * scale;
        GLRaw(new Vector2(x0, y0), new Vector2(x1, y1), c);
    }

    private void GLRaw(Vector2 a, Vector2 b, Color c)
    {
        // GUI y goes down, GL pixel y goes up
        GL.Color(c);
        GL.Vertex3(a.x, Screen.height - a.y, 0);
        GL.Vertex3(b.x, Screen.height - b.y, 0);
    }

    // ---------------------------------------------------------------
    //  Tabs
    // ---------------------------------------------------------------
    private void DrawTabs(Rect r)
    {
        Area(r);
        string[] names = { "Movie", "View", "Lines", "Search", "Video", "Setup", "Help" };
        float tw = (r.width - 12) / names.Length;
        for(int i = 0; i < names.Length; i++)
        {
            Color old = GUI.backgroundColor;
            if((int)tab == i) GUI.backgroundColor = new Color(1f, 0.82f, 0.35f);
            if(GUI.Button(new Rect(r.x + 6 + i * tw, r.y + 6, tw - 2, 22), names[i], tabButton))
            {
                tab = (Tab)i;
            }
            GUI.backgroundColor = old;
        }
        GUILayout.BeginArea(new Rect(r.x + 10, r.y + 34, r.width - 20, r.height - 40));
        switch(tab)
        {
            case Tab.Movie: MovieTab(); break;
            case Tab.Ghosts: GhostsTab(); break;
            case Tab.Lines: LinesTab(); break;
            case Tab.Search: SearchTab(); break;
            case Tab.Video: VideoTab(); break;
            case Tab.Settings: SettingsTab(); break;
            case Tab.Help: HelpTab(); break;
        }
        GUILayout.EndArea();
    }

    private void MovieTab()
    {
        TasManager m = M;
        GUILayout.Label("Movie: <b>" + m.MovieName + "</b>" + (m.MovieDirty ? "  (changed)" : ""), label);
        GUILayout.Label(m.Movie.Length + " frames  =  " + TasManager.FormatFrames(m.Movie.Length), small);
        GUILayout.Space(4);
        if(saveName == "") saveName = m.MovieName;
        GUILayout.BeginHorizontal();
        GUILayout.Label("Name", small, GUILayout.Width(40));
        saveName = GUILayout.TextField(saveName, 40, field);
        if(GUILayout.Button("Save", button, GUILayout.Width(60)))
        {
            m.SaveMovie(saveName);
            saveName = m.MovieName;
            GUIUtility.keyboardControl = 0;
            filesRefreshed = 0;
        }
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        if(GUILayout.Button("New empty movie", button)) m.NewMovie();
        if(GUILayout.Button("Open TAS Files folder", button)) OpenFolder(TasManager.TasFolder);
        GUILayout.EndHorizontal();

        GUILayout.Space(6);
        GUILayout.Label("SAVED MOVIES (click to load)", header);
        if(Time.unscaledTime > filesRefreshed)
        {
            filesRefreshed = Time.unscaledTime + 2f;
            try
            {
                files = Directory.GetFiles(TasManager.TasFolder, "*.txt");
                Array.Sort(files);
            }
            catch
            {
                files = new string[0];
            }
        }
        fileScroll = GUILayout.BeginScrollView(fileScroll, GUILayout.Height(110));
        foreach(string path in files)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if(name.StartsWith("selftest")) continue;
            if(GUILayout.Button(name, button))
            {
                m.LoadMovie(name);
                saveName = m.MovieName;
            }
        }
        GUILayout.EndScrollView();
        if(GUILayout.Button(new GUIContent("Clear all inputs after frame " + m.Frame, "Removes every input after the current frame"), button))
        {
            m.ClearAfter(m.Frame);
        }

        GUILayout.Space(6);
        GUILayout.Label("PRESETS (pieces of a run you can paste anywhere)", header);
        GUILayout.BeginHorizontal();
        presetName = GUILayout.TextField(presetName, 40, field);
        if(GUILayout.Button(new GUIContent("Save selection", "Save the selected frames as a preset"), button, GUILayout.Width(104)))
        {
            if(!HasSelection)
            {
                m.Say("Select the frames first: Shift+click two frame numbers in the INPUTS grid.");
            }
            else if(presetName.Trim().Length == 0)
            {
                m.Say("Type a name for the preset first.");
            }
            else
            {
                m.SavePreset(presetName.Trim(), m.Movie.GetRange(SelLo, SelCount));
                presetsRefreshed = 0;
                GUIUtility.keyboardControl = 0;
            }
        }
        GUILayout.EndHorizontal();
        if(Time.unscaledTime > presetsRefreshed)
        {
            presetsRefreshed = Time.unscaledTime + 2f;
            presets = m.PresetNames();
            presetLengths = new int[presets.Length];
            for(int i = 0; i < presets.Length; i++)
            {
                try { presetLengths[i] = TasMovie.Load(Path.Combine(TasManager.PresetFolder, presets[i] + ".txt")).Length; }
                catch { presetLengths[i] = -1; }
            }
        }
        presetScroll = GUILayout.BeginScrollView(presetScroll);
        for(int i = 0; i < presets.Length; i++)
        {
            string name = presets[i];
            GUILayout.BeginHorizontal();
            GUILayout.Label(name + "  (" + presetLengths[i] + " f)", small);
            GUILayout.FlexibleSpace();
            if(GUILayout.Button(new GUIContent("Paste", HasSelection ? "Replace the selected frames with this preset" : "Put this preset in at the next frame"), button, GUILayout.Width(52)))
            {
                PasteInputs(m.LoadPreset(name), "preset \"" + name + "\"");
            }
            bool armed = presetDeleteArmed == name;
            if(GUILayout.Button(armed ? "Sure?" : "X", button, GUILayout.Width(armed ? 50 : 24)))
            {
                if(armed)
                {
                    try { File.Delete(Path.Combine(TasManager.PresetFolder, name + ".txt")); }
                    catch(Exception ex) { m.Say("Could not delete: " + ex.Message); }
                    presetDeleteArmed = null;
                    presetsRefreshed = 0;
                }
                else
                {
                    presetDeleteArmed = name;
                }
            }
            GUILayout.EndHorizontal();
        }
        if(presets.Length == 0)
        {
            GUILayout.Label("No presets yet. Select frames, type a name and press Save selection.", small);
        }
        GUILayout.EndScrollView();
        GUILayout.Label("Paste puts a preset in at the next frame, or in place of the selected frames.", small);
    }

    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Application.OpenURL("file:///" + path.Replace('\\', '/'));
        }
        catch(Exception ex)
        {
            Debug.LogWarning(ex.Message);
        }
    }

    private void GhostsTab()
    {
        TasManager m = M;
        PredictionControls();
        GUILayout.Space(4);
        GUILayout.Label("Each time you change an earlier input, the old route is kept here as a faded ghost line.", small);
        GUILayout.BeginHorizontal();
        TasDraw.ShowGhostPetes = GUILayout.Toggle(TasDraw.ShowGhostPetes, " Ghost Petes", toggle);
        TasDraw.ShowRoute = GUILayout.Toggle(TasDraw.ShowRoute, " Route lines", toggle);
        GUILayout.EndHorizontal();
        if(TasFreeCam.I != null)
        {
            bool cc = GUILayout.Toggle(TasFreeCam.I.CenterOnPete, " Center camera on Pete (C)", toggle);
            if(cc != TasFreeCam.I.CenterOnPete) TasFreeCam.I.ToggleCenter();
        }
        GUILayout.BeginHorizontal();
        bool hb = GUILayout.Toggle(TasDraw.ShowHitboxes, " Hitboxes (X)", toggle);
        if(hb != TasDraw.ShowHitboxes) ToggleHitboxes();
        TasDraw.ShowLevelCollision = GUILayout.Toggle(TasDraw.ShowLevelCollision, " Level collision too", toggle);
        GUILayout.EndHorizontal();
        if(TasDraw.ShowHitboxes)
        {
            HitboxLegend();
        }
        if(GUILayout.Button("Zoom out to see the whole route (M)", button) && TasFreeCam.I != null)
        {
            if(!TasFreeCam.I.Active) TasFreeCam.I.Toggle();
            TasFreeCam.I.FitRoute();
        }
        if(GUILayout.Button("Save current route as a ghost", button))
        {
            TasGhost g = m.MakeGhost(m.GreenEnd + 1, "Saved route " + m.NextGhostNumber());
            g.Pinned = true;
        }
        ghostScroll = GUILayout.BeginScrollView(ghostScroll);
        TasGhost remove = null;
        foreach(TasGhost g in m.Ghosts)
        {
            GUILayout.BeginHorizontal();
            Color old = GUI.color;
            GUI.color = g.Color;
            GUILayout.Label("■", label, GUILayout.Width(14));
            GUI.color = old;
            GUILayout.Label(g.Name, small);
            GUILayout.FlexibleSpace();
            g.Visible = GUILayout.Toggle(g.Visible, "", toggle, GUILayout.Width(18));
            if(GUILayout.Button(g.Pinned ? "Pinned" : "Pin", button, GUILayout.Width(56))) g.Pinned = !g.Pinned;
            if(GUILayout.Button("X", button, GUILayout.Width(24))) remove = g;
            GUILayout.EndHorizontal();
        }
        GUILayout.EndScrollView();
        if(remove != null)
        {
            m.Ghosts.Remove(remove);
        }
        if(m.Ghosts.Count > 0 && GUILayout.Button("Remove all unpinned ghosts", button))
        {
            m.Ghosts.RemoveAll(g => !g.Pinned);
        }
        GUILayout.Label("Pinned ghosts are never removed automatically. Up to " + m.MaxGhosts + " unpinned ghosts are kept.", small);
    }

    private void LinesTab()
    {
        TasManager m = M;
        GUILayout.Label("Put a finish line or checkpoint in the level, then compare which route reaches it first.", small);
        GUILayout.BeginHorizontal();
        if(GUILayout.Button("Add up-down line", button)) { PlacingLine = true; PlacingVertical = true; }
        if(GUILayout.Button("Add left-right line", button)) { PlacingLine = true; PlacingVertical = false; }
        GUILayout.EndHorizontal();

        lineScroll = GUILayout.BeginScrollView(lineScroll);
        TasLine remove = null;
        foreach(TasLine line in m.Lines)
        {
            GUILayout.BeginHorizontal();
            Color old = GUI.color;
            GUI.color = line.Color;
            GUILayout.Label("■", label, GUILayout.Width(14));
            GUI.color = old;
            GUILayout.Label(line.Name, bold, GUILayout.Width(70));
            if(GUILayout.Button("-", button, GUILayout.Width(22))) line.HalfLength = Mathf.Max(0.5f, line.HalfLength - 0.5f);
            GUILayout.Label("size " + (line.HalfLength * 2).ToString("0.#"), small, GUILayout.Width(52));
            if(GUILayout.Button("+", button, GUILayout.Width(22))) line.HalfLength += 0.5f;
            GUILayout.FlexibleSpace();
            if(GUILayout.Button("X", button, GUILayout.Width(24))) remove = line;
            GUILayout.EndHorizontal();

            // Which route gets there first
            float cur = line.FirstCrossing(m.RoutePositions);
            GUILayout.Label("   Current route: " + CrossText(cur, -1f), label);
            foreach(TasGhost g in m.Ghosts)
            {
                if(!g.Visible) continue;
                float gc = line.FirstCrossing(g.Pos);
                GUILayout.Label("   <color=#" + ColorUtility.ToHtmlStringRGB(g.Color) + ">" + g.Name + "</color>: " + CrossText(gc, cur), small);
            }
            GUILayout.Space(4);
        }
        GUILayout.EndScrollView();
        if(remove != null)
        {
            m.Lines.Remove(remove);
            m.RouteVersion++;
        }
        if(m.Lines.Count == 0)
        {
            GUILayout.Label("No lines yet.", small);
        }
    }

    private static string CrossText(float crossing, float compareTo)
    {
        if(crossing < 0) return "doesn't reach it (yet)";
        string s = "frame " + crossing.ToString("0.00");
        if(compareTo >= 0)
        {
            float d = crossing - compareTo;
            if(Mathf.Abs(d) < 0.005f) s += "  (same)";
            else if(d > 0) s += "  <color=#ff8080>(" + d.ToString("0.00") + " frames slower)</color>";
            else s += "  <color=#80ff90>(" + (-d).ToString("0.00") + " frames faster)</color>";
        }
        return s;
    }

    private void SearchTab()
    {
        TasManager m = M;
        GUILayout.Label("Tries many inputs from frame " + m.Frame + " and finds the fastest way to a line.", small);
        if(m.Lines.Count == 0)
        {
            GUILayout.Label("First add a line in the Lines tab.", label);
            if(GUILayout.Button("Go to Lines", button)) tab = Tab.Lines;
            return;
        }
        searchLineIndex = Mathf.Clamp(searchLineIndex, 0, m.Lines.Count - 1);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Target", small, GUILayout.Width(80));
        if(GUILayout.Button(m.Lines[searchLineIndex].Name, button)) searchLineIndex = (searchLineIndex + 1) % m.Lines.Count;
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        GUILayout.Label("Method", small, GUILayout.Width(80));
        if(GUILayout.Button(searchMethod == TasSearch.Method.Beam ? "Beam search (any inputs)" : "Jump timing (one direction)", button))
        {
            searchMethod = searchMethod == TasSearch.Method.Beam ? TasSearch.Method.JumpTiming : TasSearch.Method.Beam;
        }
        GUILayout.EndHorizontal();
        if(searchMethod == TasSearch.Method.JumpTiming)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Direction", small, GUILayout.Width(80));
            if(GUILayout.Button(searchDir == Buttons.Right ? "Right" : searchDir == Buttons.Left ? "Left" : "None", button))
            {
                searchDir = searchDir == Buttons.Right ? Buttons.Left : searchDir == Buttons.Left ? Buttons.None : Buttons.Right;
            }
            GUILayout.EndHorizontal();
            searchWindow = IntRow("Jump starts within", searchWindow, 1, 120, 5);
            searchHold = IntRow("Longest jump hold", searchHold, 1, 60, 5);
        }
        else
        {
            searchBeam = IntRow("Beam width", searchBeam, 2, 64, 4);
            searchAttacks = GUILayout.Toggle(searchAttacks, " Also try attacks (slower)", toggle);
        }
        searchMaxFrames = IntRow("Give up after", searchMaxFrames, 20, 2000, 50);

        if(GUILayout.Button("Start search", button, GUILayout.Height(26)))
        {
            TasSearch s = new TasSearch();
            s.Kind = searchMethod;
            s.Target = m.Lines[searchLineIndex];
            s.Direction = searchDir;
            s.Window = searchWindow;
            s.MaxHold = searchHold;
            s.BeamWidth = searchBeam;
            s.TryAttacks = searchAttacks;
            s.MaxFrames = searchMaxFrames;
            lastSearch = s;
            m.StartSearch(s);
        }

        if(lastSearch != null && lastSearch.Done && lastSearch.Results.Count > 0)
        {
            GUILayout.Space(4);
            GUILayout.Label("RESULTS (from frame " + lastSearch.StartFrame + ")", header);
            for(int i = 0; i < lastSearch.Results.Count; i++)
            {
                TasSearch.Result res = lastSearch.Results[i];
                GUILayout.BeginHorizontal();
                string what = res.Crossing >= 0 ? "reaches it +" + res.Crossing.ToString("0.00") + " f" : "misses by " + res.DistanceLeft.ToString("0.00");
                if(res.VerifiedCrossing > -2f)
                {
                    what += res.VerifiedCrossing >= 0 ? "  <color=#80ff90>(verified +" + res.VerifiedCrossing.ToString("0.00") + ")</color>" : "  <color=#ff8080>(didn't reach it when verified)</color>";
                }
                GUILayout.Label("#" + (i + 1) + "  " + what, small);
                GUILayout.FlexibleSpace();
                if(GUILayout.Button("Ghost", button, GUILayout.Width(52)))
                {
                    m.AddGhost(lastSearch.ToGhost(m, res, i));
                }
                if(GUILayout.Button("Apply", button, GUILayout.Width(52)))
                {
                    ApplyResult(lastSearch, res);
                }
                GUILayout.EndHorizontal();
            }
        }
    }

    private void ApplyResult(TasSearch s, TasSearch.Result res)
    {
        TasManager m = M;
        TasLine line = s.Target;
        int start = s.StartFrame;
        m.ApplyInputs(start + 1, res.Inputs, () =>
        {
            float c = line.FirstCrossing(m.RoutePositions, start + 1);
            res.VerifiedCrossing = c >= 0 ? c - start : -1f;
            if(c >= 0)
            {
                m.Say("Applied and verified: reaches " + line.Name + " on frame " + c.ToString("0.00") + " (+" + (c - start).ToString("0.00") + ").", 10f);
            }
            else
            {
                m.Say("Applied, but the exact replay didn't reach " + line.Name + ". Rewinds during the search drifted a little; try the next result.", 10f);
            }
        });
    }

    private int IntRow(string name, int value, int min, int max, int step)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(name, small, GUILayout.Width(120));
        if(GUILayout.Button("-", button, GUILayout.Width(24))) value -= step;
        GUILayout.Label(value.ToString(), label, GUILayout.Width(44));
        if(GUILayout.Button("+", button, GUILayout.Width(24))) value += step;
        GUILayout.EndHorizontal();
        return Mathf.Clamp(value, min, max);
    }

    private void VideoTab()
    {
        TasManager m = M;
        GUILayout.Label("Plays the whole movie from the start with this panel hidden. Export saves it as a video file.", small);
        m.VideoShowInputs = GUILayout.Toggle(m.VideoShowInputs, " Show pressed buttons", toggle);
        m.VideoShowTimer = GUILayout.Toggle(m.VideoShowTimer, " Show frame counter", toggle);
        m.VideoShowRoute = GUILayout.Toggle(m.VideoShowRoute, " Show route line", toggle);
        m.VideoShowHitboxes = GUILayout.Toggle(m.VideoShowHitboxes, " Show hitboxes", toggle);
        TasPredict.ShowInVideo = GUILayout.Toggle(TasPredict.ShowInVideo, " Show prediction lines", toggle);
        m.VideoEndScreen = GUILayout.Toggle(m.VideoEndScreen, " Go to the end screen when finished", toggle);
        GUILayout.Space(6);
        if(GUILayout.Button("Play video from the start", button, GUILayout.Height(28)))
        {
            m.StartVideo();
        }
        TasVideoExport ex = TasVideoExport.I;
        bool busyExport = ex != null && (ex.Exporting || ex.Finishing);
        GUI.enabled = !busyExport;
        if(GUILayout.Button(busyExport ? "Saving the video..." : "Export video file (MP4 with sound)", button, GUILayout.Height(28)))
        {
            m.StartVideo(true);
        }
        GUI.enabled = true;
        bool hasFfmpeg = TasVideoExport.FindFfmpeg() != null;
        GUILayout.Label(hasFfmpeg
            ? "Exports every frame at 50 fps, with the game's sound, using the size of this window. Make the window bigger (or Alt+Enter for fullscreen) first for a sharper video. It plays faster than normal while it saves."
            : "ffmpeg isn't installed, so the export saves every frame as a picture plus a sound file instead of an MP4.", small);
        GUILayout.BeginHorizontal();
        if(GUILayout.Button("Open videos folder", button))
        {
            OpenFolder(TasVideoExport.VideoFolder);
        }
        GUILayout.EndHorizontal();
        if(ex != null && ex.LastResult.Length > 0)
        {
            GUILayout.Label(ex.LastResult, small);
        }
        GUILayout.Label("Press Esc to stop playback or an export early.", small);
    }

    private void SettingsTab()
    {
        TasManager m = M;
        m.ExactRewind = GUILayout.Toggle(m.ExactRewind, " Exact rewind (always replay from the start; slower)", toggle);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Rewind physics fix", small, GUILayout.Width(120));
        if(GUILayout.Button(m.Priming.ToString(), button))
        {
            m.Priming = m.Priming == TasManager.PrimeMode.PreviousFrame ? TasManager.PrimeMode.SameFrame : m.Priming == TasManager.PrimeMode.SameFrame ? TasManager.PrimeMode.None : TasManager.PrimeMode.PreviousFrame;
        }
        GUILayout.EndHorizontal();
        GUILayout.Space(6);
        GUILayout.Label("DETAILS", header);
        Row("Savestates", (m.SnapshotBytes / (1024f * 1024f)).ToString("0.0") + " MB");
        Row("Rewind time", m.LastRestoreMs.ToString("0.00") + " ms");
        Row("Save time", m.LastCaptureMs.ToString("0.00") + " ms");
        Row("Rewinds done", m.Restores.ToString());
        Row("Tick errors", m.TickLockErrors == 0 ? "none" : "<color=#ff6060>" + m.TickLockErrors + "</color>");
        Row("Clock offset", (m.ClockOffsetAfterBoot * 1000).ToString("0.000") + " ms");
        Row("Saved objects", m.Registry != null ? m.Registry.EntryCount.ToString() : "-");
        GUILayout.Space(6);
        Row("Rewind lead-in", m.RewindLeadIn + " frames");
        GUILayout.Space(6);
        GUILayout.Label("The game runs at 50 frames per second here, the same rate as its physics. " +
            "To rewind, the tool loads a savestate from just over half a second earlier and replays forward, so Unity's hidden physics timers come out the same as the first time. " +
            "Verify replays from the very start and is always exact. The light at the top is green when everything up to now is verified.", small);
    }

    private void HelpTab()
    {
        GUILayout.Label("KEYS", header);
        HelpRow("F / B", "step forward / back one frame (hold to repeat)");
        HelpRow("Shift + F / B", "10 frames");
        HelpRow("P", "play / pause");
        HelpRow("Q", "record on/off");
        HelpRow("Home / End", "go to start / end of the movie");
        HelpRow("V", "verify (exact replay from the start)");
        HelpRow("M", "free camera (zoom out to see the route)");
        HelpRow("X", "show / hide hitboxes");
        HelpRow("T", "show / hide prediction lines");
        HelpRow("C", "center the camera on Pete");
        HelpRow("Shift + click", "select frames (click frame numbers)");
        HelpRow("Ctrl + Z / Y", "undo / redo");
        HelpRow("Ctrl + C / V", "copy / paste frames");
        HelpRow("Delete", "delete the selected frames");
        HelpRow("G / L", "view tab / lines tab");
        HelpRow("H", "hide this panel");
        HelpRow("Ctrl + S", "save the movie");
        HelpRow("Esc", "stop video or search");
        GUILayout.Space(6);
        GUILayout.Label("GAME CONTROLS WHILE RECORDING", header);
        HelpRow("Arrows / A D", "move (or the left stick)");
        HelpRow("W S / Up Down", "aim attacks (or the left stick)");
        HelpRow("Space", "jump, hold = higher (or A / Cross)");
        HelpRow("O / left click", "attack (or X / Square)");
        GUILayout.Space(6);
        GUILayout.Label("Click squares in the INPUTS grid on the right to turn buttons on or off. Drag down a column to paint many frames. Click a frame number to jump to it.", small);
    }

    private void HelpRow(string key, string what)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("<b>" + key + "</b>", label, GUILayout.Width(110));
        GUILayout.Label(what, small);
        GUILayout.EndHorizontal();
    }

    // ---------------------------------------------------------------
    //  Video overlay
    // ---------------------------------------------------------------
    private void DrawVideoOverlay()
    {
        TasManager m = M;
        if(m.CurrentMode == TasManager.Mode.VideoEndScreen)
        {
            if(!m.VideoExporting)
            {
                GUI.Label(new Rect(8, vh - 22, 500, 20), "Press Esc to go back to the TAS editor.", small);
            }
            return;
        }
        if(m.VideoShowInputs)
        {
            // Bottom right, above the frame counter, so it doesn't cover the game's own timer
            Buttons b = m.Movie.Get(m.Frame);
            float s = 30f;
            float totalW = (s + 4) * 3 + 14 + s * 1.6f + 4 + s * 1.8f;
            float x = vw - totalW - 16, y = vh - 40 - s * 2 - 4;
            VideoKey(new Rect(x + s + 4, y, s, s), "↑", (b & Buttons.Up) != 0);
            VideoKey(new Rect(x, y + s + 4, s, s), "←", (b & Buttons.Left) != 0);
            VideoKey(new Rect(x + s + 4, y + s + 4, s, s), "↓", (b & Buttons.Down) != 0);
            VideoKey(new Rect(x + (s + 4) * 2, y + s + 4, s, s), "→", (b & Buttons.Right) != 0);
            VideoKey(new Rect(x + (s + 4) * 3 + 10, y + s + 4, s * 1.6f, s), "Jump", (b & Buttons.Jump) != 0);
            VideoKey(new Rect(x + (s + 4) * 3 + 14 + s * 1.6f, y + s + 4, s * 1.8f, s), "Attack", (b & Buttons.Attack) != 0);
        }
        if(m.VideoShowTimer)
        {
            GUI.Label(new Rect(vw - 260, vh - 30, 250, 24), "<b>Frame " + m.Frame + "</b>   " + TasManager.FormatFrames(m.Frame), new GUIStyle(label) { alignment = TextAnchor.MiddleRight, fontSize = 15 });
        }
    }

    private void VideoKey(Rect r, string text, bool on)
    {
        DrawRect(r, on ? new Color(1f, 0.82f, 0.3f, 0.95f) : new Color(0.1f, 0.1f, 0.12f, 0.6f));
        GUIStyle st = new GUIStyle(cellOn) { fontSize = 13 };
        st.normal.textColor = on ? Color.black : new Color(1, 1, 1, 0.7f);
        st.normal.background = null;
        GUI.Label(r, text, st);
    }
}
