using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Buttons = PeteInput.Buttons;

// A part of the run between two gates you place in the level. Its time is how long Pete takes from crossing the
// start gate to crossing the end gate, and its saved versions are the inputs of different ways to do it.
public class TasSection
{
    public string Name;
    public TasLine Start = new TasLine();
    public TasLine End = new TasLine();
    public Color Color;
    public string Folder;
    // Sorted fastest first
    public readonly List<TasSectionVersion> Versions = new List<TasSectionVersion>();

    // Where the current route crosses the gates (frame, with a fraction for "part way through the frame"), or -1
    public float StartCross = -1f;
    public float EndCross = -1f;
    public int MeasuredVersion = -1;
    public int MeasuredCount = -1;

    public float Time
    {
        get { return StartCross >= 0f && EndCross >= 0f ? EndCross - StartCross : -1f; }
    }

    // The frame whose input moves Pete across the start gate, and the one that moves him across the end gate
    public int FirstFrame
    {
        get { return StartCross >= 0f ? Mathf.FloorToInt(StartCross) + 1 : -1; }
    }

    public int LastFrame
    {
        get { return EndCross >= 0f ? Mathf.FloorToInt(EndCross) + 1 : -1; }
    }

    public TasSectionVersion Best
    {
        get { return Versions.Count > 0 ? Versions[0] : null; }
    }
}

// One saved way of doing a section
public class TasSectionVersion
{
    public List<Buttons> Inputs = new List<Buttons>();
    // Gate to gate, in frames
    public float Frames;
    // How Pete came in when it was saved: how far into the first frame he crossed the start gate, and his speed
    public float EntryFraction;
    public Vector2 EntryVel;
    public string FromMovie = "";
    public bool Practice;
    public string File;
}

// A saved place to start from: the inputs that lead there, so loading it is always exact
public class TasStartPoint
{
    public string Name;
    public int Frame;
    public TasMovie Prefix;
    public Vector2 Pos;
    public Vector2 Vel;
    public string FromMovie = "";
    public string File;

    public bool Practice
    {
        get { return Prefix != null && Prefix.IsPractice; }
    }
}

public static class TasSections
{
    private static TasManager M { get { return TasManager.I; } }

    // Draw the section gates in the level
    public static bool ShowGates = true;

    public static string SectionFolder { get { return Path.Combine(TasManager.TasFolder, "Sections"); } }
    public static string StartFolder { get { return Path.Combine(TasManager.TasFolder, "Start points"); } }

    private static readonly Color[] Colors =
    {
        new Color(0.3f, 0.95f, 0.85f), new Color(1f, 0.6f, 0.25f), new Color(0.75f, 0.55f, 1f),
        new Color(0.5f, 1f, 0.35f), new Color(1f, 0.45f, 0.65f), new Color(0.45f, 0.7f, 1f)
    };

    private static List<TasSection> all;

    public static List<TasSection> All
    {
        get
        {
            if(all == null)
            {
                all = LoadSections();
            }
            return all;
        }
    }

    // ---------- Measuring ----------

    // Where the current route crosses this section's gates (cached until the route changes)
    public static void Measure(TasSection s)
    {
        TasManager m = M;
        if(m == null)
        {
            return;
        }
        if(s.MeasuredVersion == m.RouteVersion && s.MeasuredCount == m.RoutePositions.Count)
        {
            return;
        }
        s.MeasuredVersion = m.RouteVersion;
        s.MeasuredCount = m.RoutePositions.Count;
        s.StartCross = s.Start.FirstCrossing(m.RoutePositions);
        s.EndCross = s.StartCross >= 0f ? s.End.FirstCrossing(m.RoutePositions, Mathf.FloorToInt(s.StartCross) + 1) : -1f;
    }

    public static string FramesText(float frames)
    {
        return frames.ToString("0.00", CultureInfo.InvariantCulture) + " f";
    }

    // ---------- Making sections ----------

    public static TasSection Create(string name, bool vertical, Vector2 start, Vector2 end)
    {
        TasSection s = new TasSection();
        s.Name = UniqueName(TasManager.SanitizeName(string.IsNullOrEmpty(name) ? "Section " + (All.Count + 1) : name));
        s.Start.Vertical = vertical;
        s.Start.Center = start;
        s.End.Vertical = vertical;
        s.End.Center = end;
        s.Start.Name = s.Name + " start";
        s.End.Name = s.Name + " end";
        s.Color = Colors[All.Count % Colors.Length];
        s.Start.Color = s.Color;
        s.End.Color = s.Color;
        s.Folder = Path.Combine(SectionFolder, s.Name);
        All.Add(s);
        SaveGates(s);
        return s;
    }

    private static string UniqueName(string name)
    {
        string n = name;
        for(int i = 2; All.Exists(o => string.Equals(o.Name, n, StringComparison.OrdinalIgnoreCase)); i++)
        {
            n = name + " " + i;
        }
        return n;
    }

    public static void Resize(TasSection s, float delta)
    {
        float h = Mathf.Clamp(s.Start.HalfLength + delta, 0.5f, 40f);
        s.Start.HalfLength = h;
        s.End.HalfLength = h;
        s.MeasuredVersion = -1;
        SaveGates(s);
    }

    public static void Delete(TasSection s)
    {
        All.Remove(s);
        try
        {
            if(Directory.Exists(s.Folder))
            {
                // Kept, just renamed, in case it was a mistake
                string bin = s.Folder + " (removed " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + ")";
                Directory.Move(s.Folder, bin);
            }
        }
        catch(Exception ex)
        {
            Debug.LogWarning(ex.Message);
        }
    }

    // ---------- Versions ----------

    public static void SaveVersion(TasSection s)
    {
        TasManager m = M;
        Measure(s);
        if(s.StartCross < 0f)
        {
            m.Say("Your route doesn't cross the start of " + s.Name + " yet. Play up to it first.");
            return;
        }
        if(s.EndCross < 0f)
        {
            m.Say("Your route doesn't reach the end of " + s.Name + " yet. Play (or step) past the end gate, then save.");
            return;
        }
        int a = s.FirstFrame, b = s.LastFrame;
        TasSectionVersion v = new TasSectionVersion();
        v.Inputs = m.Movie.GetRange(a, b - a + 1);
        foreach(TasSectionVersion o in s.Versions)
        {
            if(SameInputs(o.Inputs, v.Inputs))
            {
                m.Say("This way of doing " + s.Name + " is already saved (" + FramesText(o.Frames) + ").");
                return;
            }
        }
        v.Frames = s.Time;
        v.EntryFraction = s.StartCross - (a - 1);
        v.EntryVel = a - 1 < m.Timeline.Count ? m.Timeline[a - 1].Vel : Vector2.zero;
        v.FromMovie = m.MovieName;
        v.Practice = m.Movie.IsPractice && m.Movie.TeleportFrame < a;
        TasSectionVersion best = s.Best;
        try
        {
            Directory.CreateDirectory(s.Folder);
            v.File = Path.Combine(s.Folder, v.Frames.ToString("000.00", CultureInfo.InvariantCulture) + "f " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + ".txt");
            TasMovie piece = new TasMovie();
            piece.SetRange(1, v.Inputs);
            string header = "#version frames=" + v.Frames.ToString("R", CultureInfo.InvariantCulture)
                + " entry=" + v.EntryFraction.ToString("R", CultureInfo.InvariantCulture)
                + " vx=" + v.EntryVel.x.ToString("R", CultureInfo.InvariantCulture)
                + " vy=" + v.EntryVel.y.ToString("R", CultureInfo.InvariantCulture)
                + " practice=" + (v.Practice ? 1 : 0)
                + " from=" + v.FromMovie.Replace(' ', '_') + "\n";
            File.WriteAllText(v.File, "# Pete TAS section version: " + s.Name + "\n" + header + piece.ToText());
        }
        catch(Exception ex)
        {
            m.Say("Could not save the version: " + ex.Message, 8f);
            return;
        }
        s.Versions.Add(v);
        s.Versions.Sort((x, y) => x.Frames.CompareTo(y.Frames));
        string news = best == null ? "the first saved version"
            : v.Frames < best.Frames - 0.0001f ? "your new best, " + FramesText(best.Frames - v.Frames) + " faster than before!"
            : "the best is still " + FramesText(best.Frames);
        m.Say("Saved " + s.Name + ": " + FramesText(v.Frames) + " (" + v.Inputs.Count + " frames of inputs), " + news, 7f);
    }

    public static void DeleteVersion(TasSection s, TasSectionVersion v)
    {
        s.Versions.Remove(v);
        try
        {
            if(v.File != null && File.Exists(v.File))
            {
                File.Move(v.File, v.File + ".removed");
            }
        }
        catch(Exception ex)
        {
            Debug.LogWarning(ex.Message);
        }
    }

    // Whether the movie has exactly these inputs from this frame on
    public static bool SameRange(TasMovie movie, int from, List<Buttons> inputs)
    {
        return from >= 1 && SameInputs(movie.GetRange(from, inputs.Count), inputs);
    }

    private static bool SameInputs(List<Buttons> a, List<Buttons> b)
    {
        if(a.Count != b.Count)
        {
            return false;
        }
        for(int i = 0; i < a.Count; i++)
        {
            if(a[i] != b[i]) return false;
        }
        return true;
    }

    // Puts a saved version into the run, starting on the frame Pete crosses the start gate (so it lines up however
    // Pete got there), replacing what was there. Afterwards it tells you whether it works the same as when saved.
    public static void UseVersion(TasSection s, TasSectionVersion v, Action<string> done = null)
    {
        TasManager m = M;
        if(m == null || m.Busy)
        {
            return;
        }
        Measure(s);
        if(s.StartCross < 0f)
        {
            string no = "Your route doesn't cross the start of " + s.Name + ", so there's nowhere to put it. Play up to the start gate first.";
            m.Say(no, 7f);
            if(done != null) done(no);
            return;
        }
        int a = s.FirstFrame;
        // What's there now: up to the end gate, or (if the route doesn't get there) the rest of the movie
        int remove = s.EndCross >= 0f ? s.LastFrame - a + 1 : Mathf.Max(0, m.Movie.Length - a + 1);
        float entryNow = s.StartCross - (a - 1);
        Vector2 velNow = a - 1 < m.Timeline.Count ? m.Timeline[a - 1].Vel : Vector2.zero;
        m.ReplaceFrames(a, remove, v.Inputs);
        int check = a + v.Inputs.Count + 2;
        m.Seek(check, () =>
        {
            s.MeasuredVersion = -1;
            Measure(s);
            string text;
            if(s.EndCross < 0f)
            {
                text = s.Name + ": put in the " + FramesText(v.Frames) + " version at frame " + a + ", but Pete no longer reaches the end gate with it.";
            }
            else
            {
                float diff = s.Time - v.Frames;
                text = s.Name + ": put in the " + FramesText(v.Frames) + " version at frame " + a + ". It takes " + FramesText(s.Time)
                    + (Mathf.Abs(diff) < 0.005f ? ", exactly as when it was saved." : " here (" + FramesText(Mathf.Abs(diff)) + (diff > 0 ? " slower" : " faster") + " than when it was saved).");
            }
            float dv = (velNow - v.EntryVel).magnitude;
            if(dv > 0.01f || Mathf.Abs(entryNow - v.EntryFraction) > 0.01f)
            {
                text += " Pete comes in at " + velNow.x.ToString("0.00") + ", " + velNow.y.ToString("0.00") + " u/s; it was made coming in at " + v.EntryVel.x.ToString("0.00") + ", " + v.EntryVel.y.ToString("0.00") + ".";
            }
            m.Say(text, 10f);
            if(done != null) done(text);
        }, true);
    }

    // ---------- Building a run ----------

    public static bool Building { get; private set; }
    public static string LastBuild = "";

    // For every section the route goes through, in order: put in its best version where Pete crosses its start gate
    public static void BuildRun()
    {
        TasManager m = M;
        if(m == null || m.Busy || Building)
        {
            return;
        }
        Building = true;
        StringBuilder report = new StringBuilder();
        int startLength = m.Movie.Length;
        HashSet<TasSection> done = new HashSet<TasSection>();
        m.Seek(m.Movie.Length, () => BuildStep(done, report, startLength), true);
    }

    private static void BuildStep(HashSet<TasSection> done, StringBuilder report, int startLength)
    {
        TasManager m = M;
        TasSection next = null;
        foreach(TasSection s in All)
        {
            if(done.Contains(s))
            {
                continue;
            }
            Measure(s);
            if(s.StartCross >= 0f && s.Best != null && (next == null || s.StartCross < next.StartCross))
            {
                next = s;
            }
        }
        if(next == null)
        {
            Building = false;
            int saved = startLength - m.Movie.Length;
            LastBuild = (report.Length > 0 ? report.ToString() : "No sections with saved versions are on your route.\n")
                + "The movie went from " + startLength + " to " + m.Movie.Length + " frames" + (saved > 0 ? " (" + saved + " frames faster)." : ".");
            m.Say("Run built. " + LastBuild.Replace("\n", " "), 12f);
            return;
        }
        done.Add(next);
        TasSectionVersion best = next.Best;
        if(next.Time >= 0f && next.Time <= best.Frames + 0.005f && SameInputs(m.Movie.GetRange(next.FirstFrame, best.Inputs.Count), best.Inputs))
        {
            report.Append(next.Name + ": already the best (" + FramesText(next.Time) + ")\n");
            BuildStep(done, report, startLength);
            return;
        }
        float before = next.Time;
        UseVersion(next, best, text =>
        {
            report.Append(next.Name + ": " + (before >= 0f ? FramesText(before) : "didn't reach the end") + " -> " + (next.Time >= 0f ? FramesText(next.Time) : "doesn't reach the end") + "\n");
            // See the whole rest of the run before the next one, so its start gate is found
            m.Seek(m.Movie.Length, () => BuildStep(done, report, startLength), true);
        });
    }

    // ---------- Start points ----------

    public static List<TasStartPoint> StartPoints = new List<TasStartPoint>();
    private static float startsRead;

    public static void RefreshStartPoints(bool now = false)
    {
        if(!now && UnityEngine.Time.unscaledTime < startsRead)
        {
            return;
        }
        startsRead = UnityEngine.Time.unscaledTime + 3f;
        List<TasStartPoint> list = new List<TasStartPoint>();
        try
        {
            Directory.CreateDirectory(StartFolder);
            string[] files = Directory.GetFiles(StartFolder, "*.txt");
            Array.Sort(files);
            foreach(string f in files)
            {
                TasStartPoint sp = ReadStartPoint(f);
                if(sp != null) list.Add(sp);
            }
        }
        catch(Exception ex)
        {
            Debug.LogWarning(ex.Message);
        }
        StartPoints = list;
    }

    // Saves where Pete is now (the current frame) as a start point
    public static void SaveStartPoint(string name)
    {
        TasManager m = M;
        if(m == null || m.Player == null || !m.Player.Valid)
        {
            return;
        }
        name = TasManager.SanitizeName(string.IsNullOrEmpty(name) ? "start " + m.Frame : name);
        TasMovie prefix = m.Movie.Clone();
        prefix.TruncateAfter(m.Frame);
        prefix.Markers.Clear();
        string path = Path.Combine(StartFolder, name + ".txt");
        try
        {
            Directory.CreateDirectory(StartFolder);
            Vector2 p = m.Player.Position, v = m.Player.Velocity;
            string header = "# Pete TAS start point: the inputs that lead to frame " + m.Frame + "\n"
                + "#startpoint frame=" + m.Frame
                + " x=" + p.x.ToString("R", CultureInfo.InvariantCulture) + " y=" + p.y.ToString("R", CultureInfo.InvariantCulture)
                + " vx=" + v.x.ToString("R", CultureInfo.InvariantCulture) + " vy=" + v.y.ToString("R", CultureInfo.InvariantCulture)
                + " from=" + m.MovieName.Replace(' ', '_') + "\n";
            File.WriteAllText(path, header + prefix.ToText());
        }
        catch(Exception ex)
        {
            m.Say("Could not save the start point: " + ex.Message, 8f);
            return;
        }
        RefreshStartPoints(true);
        m.Say("Saved start point \"" + name + "\" (frame " + m.Frame + (m.Movie.IsPractice ? ", practice" : "") + "). Go there any time from the Sections tab.", 6f);
    }

    // Goes to a start point. If the movie already leads there, it just jumps there and keeps the rest of the movie;
    // otherwise the movie becomes the start point's inputs (your old route is kept as a try, and Undo works).
    public static void GoTo(TasStartPoint sp)
    {
        TasManager m = M;
        if(m == null || m.Busy || sp.Prefix == null)
        {
            return;
        }
        bool leadsThere = m.Movie.SameTeleport(sp.Prefix) || (sp.Prefix.TeleportFrame < 1 && m.Movie.TeleportFrame > sp.Frame);
        for(int f = 1; leadsThere && f <= sp.Frame; f++)
        {
            leadsThere = m.Movie.Get(f) == sp.Prefix.Get(f);
        }
        if(leadsThere)
        {
            m.Seek(sp.Frame);
            m.Say("At start point \"" + sp.Name + "\" (frame " + sp.Frame + "). Your movie already led here, so the rest of it is still there.", 6f);
            return;
        }
        m.ReplaceMovie(sp.Prefix.Clone(), sp.Name, sp.Frame);
        m.Say("Started from \"" + sp.Name + "\" (frame " + sp.Frame + "). The movie is now its inputs, so save it under a new name if you want to keep it. Your old route is kept as a try.", 8f);
    }

    public static void DeleteStartPoint(TasStartPoint sp)
    {
        try
        {
            if(File.Exists(sp.File))
            {
                File.Move(sp.File, sp.File + ".removed");
            }
        }
        catch(Exception ex)
        {
            Debug.LogWarning(ex.Message);
        }
        RefreshStartPoints(true);
    }

    private static TasStartPoint ReadStartPoint(string file)
    {
        try
        {
            string text = File.ReadAllText(file);
            TasStartPoint sp = new TasStartPoint();
            sp.Name = Path.GetFileNameWithoutExtension(file);
            sp.File = file;
            sp.Prefix = TasMovie.FromText(text);
            sp.Frame = sp.Prefix.Length;
            Dictionary<string, string> kv = Header(text, "#startpoint");
            string s;
            if(kv.TryGetValue("frame", out s)) int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out sp.Frame);
            sp.Pos = new Vector2(F(kv, "x"), F(kv, "y"));
            sp.Vel = new Vector2(F(kv, "vx"), F(kv, "vy"));
            if(kv.TryGetValue("from", out s)) sp.FromMovie = s.Replace('_', ' ');
            return sp;
        }
        catch(Exception ex)
        {
            Debug.LogWarning("[TAS] Couldn't read start point " + file + ": " + ex.Message);
            return null;
        }
    }

    // ---------- Files ----------

    private static void SaveGates(TasSection s)
    {
        try
        {
            Directory.CreateDirectory(s.Folder);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# Pete TAS section: where it starts and ends (gates in the level). Saved versions are the other files here.");
            sb.AppendLine(GateLine("start", s.Start));
            sb.AppendLine(GateLine("end", s.End));
            File.WriteAllText(Path.Combine(s.Folder, "section.txt"), sb.ToString());
        }
        catch(Exception ex)
        {
            if(M != null) M.Say("Could not save the section: " + ex.Message, 8f);
        }
    }

    private static string GateLine(string which, TasLine g)
    {
        return which + " " + (g.Vertical ? "V" : "H") + " " + g.Center.x.ToString("R", CultureInfo.InvariantCulture) + " " + g.Center.y.ToString("R", CultureInfo.InvariantCulture) + " " + g.HalfLength.ToString("R", CultureInfo.InvariantCulture);
    }

    private static List<TasSection> LoadSections()
    {
        List<TasSection> list = new List<TasSection>();
        try
        {
            Directory.CreateDirectory(SectionFolder);
            string[] dirs = Directory.GetDirectories(SectionFolder);
            Array.Sort(dirs);
            foreach(string dir in dirs)
            {
                string gates = Path.Combine(dir, "section.txt");
                if(!File.Exists(gates))
                {
                    continue;
                }
                TasSection s = new TasSection();
                s.Name = Path.GetFileName(dir);
                s.Folder = dir;
                s.Color = Colors[list.Count % Colors.Length];
                foreach(string raw in File.ReadAllLines(gates))
                {
                    string[] p = raw.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if(p.Length < 5 || (p[0] != "start" && p[0] != "end"))
                    {
                        continue;
                    }
                    TasLine g = p[0] == "start" ? s.Start : s.End;
                    g.Vertical = p[1] == "V";
                    g.Center = new Vector2(Fl(p[2]), Fl(p[3]));
                    g.HalfLength = Mathf.Max(0.5f, Fl(p[4]));
                }
                s.Start.Name = s.Name + " start";
                s.End.Name = s.Name + " end";
                s.Start.Color = s.Color;
                s.End.Color = s.Color;
                foreach(string vf in Directory.GetFiles(dir, "*.txt"))
                {
                    if(Path.GetFileName(vf) == "section.txt")
                    {
                        continue;
                    }
                    TasSectionVersion v = ReadVersion(vf);
                    if(v != null) s.Versions.Add(v);
                }
                s.Versions.Sort((x, y) => x.Frames.CompareTo(y.Frames));
                list.Add(s);
            }
        }
        catch(Exception ex)
        {
            Debug.LogWarning("[TAS] Couldn't read the sections: " + ex.Message);
        }
        return list;
    }

    private static TasSectionVersion ReadVersion(string file)
    {
        try
        {
            string text = File.ReadAllText(file);
            TasMovie piece = TasMovie.FromText(text);
            TasSectionVersion v = new TasSectionVersion();
            v.File = file;
            v.Inputs = piece.GetRange(1, piece.Length);
            Dictionary<string, string> kv = Header(text, "#version");
            v.Frames = kv.ContainsKey("frames") ? F(kv, "frames") : piece.Length;
            v.EntryFraction = F(kv, "entry");
            v.EntryVel = new Vector2(F(kv, "vx"), F(kv, "vy"));
            string s;
            v.Practice = kv.TryGetValue("practice", out s) && s == "1";
            if(kv.TryGetValue("from", out s)) v.FromMovie = s.Replace('_', ' ');
            return v;
        }
        catch(Exception ex)
        {
            Debug.LogWarning("[TAS] Couldn't read section version " + file + ": " + ex.Message);
            return null;
        }
    }

    // "key=value key=value" pairs on the line that starts with the tag
    private static Dictionary<string, string> Header(string text, string tag)
    {
        Dictionary<string, string> kv = new Dictionary<string, string>();
        foreach(string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if(!line.StartsWith(tag + " ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            foreach(string part in line.Substring(tag.Length).Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if(eq > 0) kv[part.Substring(0, eq)] = part.Substring(eq + 1);
            }
            break;
        }
        return kv;
    }

    private static float F(Dictionary<string, string> kv, string key)
    {
        string s;
        return kv.TryGetValue(key, out s) ? Fl(s) : 0f;
    }

    private static float Fl(string s)
    {
        float f;
        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f) ? f : 0f;
    }
}
