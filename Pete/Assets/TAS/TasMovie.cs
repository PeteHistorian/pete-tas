using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Buttons = PeteInput.Buttons;

// The list of inputs for every frame of the TAS, plus marker labels.
// Frame 0 is the starting state, so inputs start at frame 1.
public class TasMovie
{
    private readonly List<Buttons> frames = new List<Buttons> { Buttons.None };
    public readonly SortedDictionary<int, string> Markers = new SortedDictionary<int, string>();
    // Practice only: at the end of this frame Pete is put at TeleportTo, standing still (-1 = never).
    // It's part of the movie, so replays and going back always do it the same way.
    public int TeleportFrame = -1;
    public Vector2 TeleportTo;

    public bool IsPractice
    {
        get { return TeleportFrame >= 1; }
    }

    // Number of the last frame that has an input.
    public int Length
    {
        get { return frames.Count - 1; }
    }

    public Buttons Get(int frame)
    {
        if(frame <= 0 || frame >= frames.Count)
        {
            return Buttons.None;
        }
        return frames[frame];
    }

    public void Set(int frame, Buttons buttons)
    {
        if(frame <= 0)
        {
            return;
        }
        while(frames.Count <= frame)
        {
            frames.Add(Buttons.None);
        }
        frames[frame] = buttons;
    }

    public void Insert(int frame, Buttons buttons)
    {
        if(frame <= 0)
        {
            return;
        }
        while(frames.Count < frame)
        {
            frames.Add(Buttons.None);
        }
        frames.Insert(frame, buttons);
        ShiftMarkers(frame, 1);
    }

    public void Delete(int frame)
    {
        if(frame <= 0 || frame >= frames.Count)
        {
            return;
        }
        frames.RemoveAt(frame);
        Markers.Remove(frame);
        if(TeleportFrame == frame) TeleportFrame = -1;
        ShiftMarkers(frame + 1, -1);
    }

    // The inputs for frames from..from+count-1.
    public List<Buttons> GetRange(int from, int count)
    {
        List<Buttons> list = new List<Buttons>(Math.Max(0, count));
        for(int i = 0; i < count; i++)
        {
            list.Add(Get(from + i));
        }
        return list;
    }

    // Puts these inputs in before "frame"; everything from there on moves later by inputs.Count.
    public void InsertRange(int frame, IList<Buttons> inputs)
    {
        if(frame <= 0 || inputs.Count == 0)
        {
            return;
        }
        while(frames.Count < frame)
        {
            frames.Add(Buttons.None);
        }
        frames.InsertRange(frame, inputs);
        ShiftMarkers(frame, inputs.Count);
    }

    // Removes "count" frames starting at "frame"; everything after moves earlier.
    public void DeleteRange(int frame, int count)
    {
        if(frame <= 0 || frame >= frames.Count || count <= 0)
        {
            return;
        }
        count = Math.Min(count, frames.Count - frame);
        frames.RemoveRange(frame, count);
        for(int f = frame; f < frame + count; f++)
        {
            Markers.Remove(f);
        }
        if(TeleportFrame >= frame && TeleportFrame < frame + count) TeleportFrame = -1;
        ShiftMarkers(frame + count, -count);
    }

    // Writes these inputs over frames frame..frame+inputs.Count-1 (nothing moves).
    public void SetRange(int frame, IList<Buttons> inputs)
    {
        for(int i = 0; i < inputs.Count; i++)
        {
            Set(frame + i, inputs[i]);
        }
    }

    // Removes every input after the given frame.
    public void TruncateAfter(int frame)
    {
        if(frame < 0)
        {
            frame = 0;
        }
        if(frames.Count > frame + 1)
        {
            frames.RemoveRange(frame + 1, frames.Count - frame - 1);
        }
        if(TeleportFrame > frame) TeleportFrame = -1;
    }

    public void Clear()
    {
        frames.Clear();
        frames.Add(Buttons.None);
        Markers.Clear();
        TeleportFrame = -1;
    }

    public TasMovie Clone()
    {
        TasMovie copy = new TasMovie();
        copy.frames.Clear();
        copy.frames.AddRange(frames);
        foreach(var m in Markers)
        {
            copy.Markers[m.Key] = m.Value;
        }
        copy.TeleportFrame = TeleportFrame;
        copy.TeleportTo = TeleportTo;
        return copy;
    }

    public bool SameInputs(TasMovie other)
    {
        if(!SameTeleport(other))
        {
            return false;
        }
        int n = Math.Max(Length, other.Length);
        for(int f = 1; f <= n; f++)
        {
            if(Get(f) != other.Get(f))
            {
                return false;
            }
        }
        return true;
    }

    public bool SameTeleport(TasMovie other)
    {
        if(TeleportFrame < 1 && other.TeleportFrame < 1)
        {
            return true;
        }
        return TeleportFrame == other.TeleportFrame && TeleportTo == other.TeleportTo;
    }

    private void ShiftMarkers(int fromFrame, int delta)
    {
        if(TeleportFrame >= fromFrame)
        {
            TeleportFrame += delta;
            if(TeleportFrame < 1) TeleportFrame = -1;
        }
        List<KeyValuePair<int, string>> moved = new List<KeyValuePair<int, string>>();
        foreach(var m in Markers)
        {
            if(m.Key >= fromFrame)
            {
                moved.Add(m);
            }
        }
        foreach(var m in moved)
        {
            Markers.Remove(m.Key);
        }
        foreach(var m in moved)
        {
            int to = m.Key + delta;
            if(to > 0)
            {
                Markers[to] = m.Value;
            }
        }
    }

    // ---------- Text format ----------
    // One line per run of identical inputs: "frames,keys" e.g. "12,R,J".
    // Keys: L R U D J (jump) A (attack). Lines starting with # are comments.
    // "#marker 120 First orb" stores a label on frame 120.

    public static string ButtonsToKeys(Buttons b)
    {
        StringBuilder sb = new StringBuilder();
        if((b & Buttons.Left) != 0) sb.Append(",L");
        if((b & Buttons.Right) != 0) sb.Append(",R");
        if((b & Buttons.Up) != 0) sb.Append(",U");
        if((b & Buttons.Down) != 0) sb.Append(",D");
        if((b & Buttons.Jump) != 0) sb.Append(",J");
        if((b & Buttons.Attack) != 0) sb.Append(",A");
        return sb.ToString();
    }

    public static string ButtonsToShortText(Buttons b)
    {
        string keys = ButtonsToKeys(b).Replace(",", " ").Trim();
        return keys.Length == 0 ? "-" : keys;
    }

    public string ToText()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("# Pete TAS movie");
        sb.AppendLine("# Each line: how many frames, then the buttons held (L R U D J=jump A=attack).");
        sb.AppendLine("# The game runs 50 frames per second. Lines starting with # are comments.");
        foreach(var m in Markers)
        {
            sb.Append("#marker ").Append(m.Key.ToString(CultureInfo.InvariantCulture)).Append(' ').AppendLine(m.Value);
        }
        if(TeleportFrame >= 1)
        {
            sb.AppendLine("# PRACTICE: on this frame Pete is put somewhere else (not a real run)");
            sb.Append("#teleport ").Append(TeleportFrame.ToString(CultureInfo.InvariantCulture)).Append(' ')
              .Append(TeleportTo.x.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
              .AppendLine(TeleportTo.y.ToString("R", CultureInfo.InvariantCulture));
        }
        int f = 1;
        while(f <= Length)
        {
            Buttons b = frames[f];
            int run = 1;
            while(f + run <= Length && frames[f + run] == b)
            {
                run++;
            }
            sb.Append(run.ToString(CultureInfo.InvariantCulture).PadLeft(5)).Append(ButtonsToKeys(b)).AppendLine();
            f += run;
        }
        return sb.ToString();
    }

    public static TasMovie FromText(string text)
    {
        TasMovie movie = new TasMovie();
        int frame = 1;
        using(StringReader reader = new StringReader(text))
        {
            string line;
            int lineNumber = 0;
            while((line = reader.ReadLine()) != null)
            {
                lineNumber++;
                string t = line.Trim();
                if(t.Length == 0)
                {
                    continue;
                }
                if(t.StartsWith("#marker", StringComparison.OrdinalIgnoreCase))
                {
                    string rest = t.Substring(7).Trim();
                    int space = rest.IndexOf(' ');
                    string num = space < 0 ? rest : rest.Substring(0, space);
                    int markerFrame;
                    if(int.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out markerFrame))
                    {
                        movie.Markers[markerFrame] = space < 0 ? "" : rest.Substring(space + 1).Trim();
                    }
                    continue;
                }
                if(t.StartsWith("#teleport", StringComparison.OrdinalIgnoreCase))
                {
                    string[] tp = t.Substring(9).Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    int tf;
                    float tx, ty;
                    if(tp.Length >= 3 && int.TryParse(tp[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out tf)
                        && float.TryParse(tp[1], NumberStyles.Float, CultureInfo.InvariantCulture, out tx)
                        && float.TryParse(tp[2], NumberStyles.Float, CultureInfo.InvariantCulture, out ty))
                    {
                        movie.TeleportFrame = tf;
                        movie.TeleportTo = new Vector2(tx, ty);
                    }
                    continue;
                }
                if(t.StartsWith("#"))
                {
                    continue;
                }
                string[] parts = t.Split(',');
                int count;
                if(!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out count) || count < 0)
                {
                    throw new FormatException("Line " + lineNumber + ": expected a frame count, got \"" + parts[0].Trim() + "\"");
                }
                Buttons b = Buttons.None;
                for(int i = 1; i < parts.Length; i++)
                {
                    string key = parts[i].Trim().ToUpperInvariant();
                    switch(key)
                    {
                        case "": break;
                        case "L": b |= Buttons.Left; break;
                        case "R": b |= Buttons.Right; break;
                        case "U": b |= Buttons.Up; break;
                        case "D": b |= Buttons.Down; break;
                        case "J": b |= Buttons.Jump; break;
                        case "A": b |= Buttons.Attack; break;
                        default: throw new FormatException("Line " + lineNumber + ": unknown button \"" + key + "\"");
                    }
                }
                for(int i = 0; i < count; i++)
                {
                    movie.Set(frame++, b);
                }
            }
        }
        return movie;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, ToText());
    }

    public static TasMovie Load(string path)
    {
        return FromText(File.ReadAllText(path));
    }
}
