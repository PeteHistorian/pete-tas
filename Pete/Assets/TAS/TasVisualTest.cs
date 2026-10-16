using System.Collections;
using System.IO;
using UnityEngine;
using Buttons = PeteInput.Buttons;

// Takes screenshots of the TAS tool in action (start with "-tasVisualTest"), so the screens can be checked.
public class TasVisualTest : MonoBehaviour
{
    private TasManager M { get { return TasManager.I; } }
    private string folder;
    private int shot;

    private IEnumerator WaitIdle()
    {
        yield return null;
        while(M == null || !M.IsIdle)
        {
            yield return null;
        }
    }

    private IEnumerator Shot(string name)
    {
        // A few frames so the panel and the level are drawn
        for(int i = 0; i < 4; i++) yield return null;
        yield return new WaitForEndOfFrame();
        shot++;
        ScreenCapture.CaptureScreenshot(Path.Combine(folder, shot.ToString("00") + "-" + name + ".png"));
        yield return null;
        yield return null;
    }

    private IEnumerator Start()
    {
        folder = Path.Combine(TasManager.TasFolder, "visual-test");
        Directory.CreateDirectory(folder);
        foreach(string f in Directory.GetFiles(folder, "*.png")) File.Delete(f);
        yield return WaitIdle();
        yield return Shot("start");

        // A simple route: run right, jump, run
        TasMovie movie = new TasMovie();
        for(int f = 1; f <= 160; f++)
        {
            Buttons b = Buttons.Right;
            if((f >= 30 && f < 45) || (f >= 95 && f < 100)) b |= Buttons.Jump;
            if(f == 67) b |= Buttons.Attack;
            movie.Set(f, b);
        }
        movie.Markers[30] = "first jump";
        M.ReplaceMovie(movie, "visual-test", 120);
        yield return WaitIdle();
        TasHud.I.ShowTab("Help");
        yield return Shot("frame120-help");

        // Change an earlier jump so the old route becomes a ghost
        M.Seek(60);
        yield return WaitIdle();
        M.SetInput(36, Buttons.Right);
        yield return WaitIdle();
        M.Seek(120);
        yield return WaitIdle();
        Vector2 p = M.Player.Position;
        TasLine line = new TasLine();
        line.Name = "Finish";
        line.Vertical = true;
        line.Center = p + new Vector2(-1.5f, 0.5f);
        line.HalfLength = 3f;
        line.Color = TasHud.LineColor(0);
        M.Lines.Add(line);
        TasHud.I.ShowTab("Lines");
        yield return Shot("ghost-and-line");

        TasHud.I.ShowTab("Ghosts");
        yield return Shot("ghosts-tab");

        // Center camera, plus a selection in the input grid and the presets list
        TasHud.I.Select(100, 110);
        TasHud.I.ShowTab("Movie");
        TasFreeCam.I.ToggleCenter();
        yield return Shot("center-camera-selection");
        TasFreeCam.I.ToggleCenter();
        TasHud.I.ClearSelection();

        // Hitboxes, with and without the level's own collision, while Pete is swinging his sword
        M.Seek(67);
        yield return WaitIdle();
        for(int i = 0; i < 25 && M.Player.CanAttack; i++)
        {
            M.StepForward(1);
            yield return WaitIdle();
        }
        TasDraw.ShowHitboxes = true;
        TasHud.I.Hidden = true;
        yield return Shot("hitboxes");
        TasDraw.ShowLevelCollision = true;
        yield return Shot("hitboxes-level");
        TasHud.I.Hidden = false;
        yield return Shot("hitboxes-panel");
        TasDraw.ShowLevelCollision = false;
        TasDraw.ShowHitboxes = false;
        M.Seek(120);
        yield return WaitIdle();

        // Free camera zoomed out over the whole route
        TasFreeCam.I.Toggle();
        TasFreeCam.I.FitRoute();
        yield return Shot("free-camera");
        TasFreeCam.I.Toggle();

        // Auto-search from frame 60 to the line
        M.Seek(60);
        yield return WaitIdle();
        TasSearch s = new TasSearch();
        s.Kind = TasSearch.Method.Beam;
        s.Target = line;
        s.BeamWidth = 8;
        s.MaxFrames = 120;
        TasHud.I.ShowSearch(s);
        M.StartSearch(s);
        yield return Shot("searching");
        yield return WaitIdle();
        TasHud.I.ShowTab("Search");
        yield return Shot("search-results");

        TasHud.I.ShowTab("Settings");
        yield return Shot("settings");

        // Video playback overlay
        M.VideoEndScreen = false;
        M.StartVideo();
        while(M.CurrentMode != TasManager.Mode.Video) yield return null;
        for(int i = 0; i < 70; i++) yield return null;
        yield return Shot("video");
        M.StopVideo();
        yield return WaitIdle();

        File.WriteAllText(Path.Combine(folder, "done.txt"), "ok, desync " + M.DesyncFrame + ", tick errors " + M.TickLockErrors);
        Application.Quit(0);
    }
}
