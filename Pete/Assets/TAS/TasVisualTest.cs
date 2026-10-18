using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Buttons = PeteInput.Buttons;

// Takes screenshots of the TAS tool in action (start with "-tasVisualTest"), so the screens can be checked.
public class TasVisualTest : MonoBehaviour
{
    private TasManager M { get { return TasManager.I; } }
    private string folder;
    private int shot;

    // Steps back from inside OnGUI, the same way clicking the "<" button does
    private bool stepBackFromGui;

    private void OnGUI()
    {
        if(stepBackFromGui && Event.current.type == EventType.Layout)
        {
            stepBackFromGui = false;
            M.StepBack(1);
        }
    }

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

        // Sections: a section between two gates with a saved version, shown in the Sections tab
        {
            List<TasSection> userSections = new List<TasSection>(TasSections.All);
            TasSections.All.Clear();
            M.Seek(150);
            yield return WaitIdle();
            TasSection vs = TasSections.Create("Visual test", true, M.Timeline[60].Pos, M.Timeline[130].Pos);
            TasSections.SaveVersion(vs);
            M.Seek(120);
            yield return WaitIdle();
            TasHud.I.ShowTab("Sections");
            yield return Shot("sections-tab");
            TasSections.All.Clear();
            TasSections.All.AddRange(userSections);
            try { Directory.Delete(vs.Folder, true); } catch { }
        }

        // Center camera, plus a selection in the input grid and the presets list
        TasHud.I.Select(100, 110);
        TasHud.I.ShowTab("Movie");
        TasFreeCam.I.ToggleCenter();
        yield return Shot("center-camera-selection");
        TasFreeCam.I.ToggleCenter();
        TasHud.I.ClearSelection();

        // Filling a whole selection: Jump on for frames 100-140 in one go, then undo it
        TasHud.I.Select(100, 140);
        TasHud.I.FillSelectionColumn(4);
        yield return WaitIdle();
        int filled = 0;
        for(int f = 100; f <= 140; f++) if((M.Movie.Get(f) & Buttons.Jump) != 0) filled++;
        M.Undo();
        yield return WaitIdle();
        int afterUndo = 0;
        for(int f = 100; f <= 140; f++) if((M.Movie.Get(f) & Buttons.Jump) != 0) afterUndo++;
        string fillInfo = filled + " of 41 frames got J, " + afterUndo + " left after undo";
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

        // Stepping back keeps the level on screen (a still picture) instead of flashing an empty screen
        M.Seek(60);
        yield return WaitIdle();
        yield return Shot("before-stepping-back");
        stepBackFromGui = true;
        while(stepBackFromGui) yield return null;
        bool wasSeeking = M.CurrentMode == TasManager.Mode.Seeking;
        bool stillPicture = TasHud.I.ShowingStill;
        yield return new WaitForEndOfFrame();
        shot++;
        ScreenCapture.CaptureScreenshot(Path.Combine(folder, shot.ToString("00") + "-while-stepping-back.png"));
        yield return WaitIdle();
        string freezeInfo = "seeking " + wasSeeking + ", still picture " + stillPicture + ", back on frame " + M.Frame;

        // Prediction lines: on the ground just before the first jump, and in the air
        TasPredict.Show = true;
        TasHud.I.ShowTab("Ghosts");
        M.Seek(24);
        yield return WaitIdle();
        yield return Shot("prediction-ground");
        M.Seek(33);
        yield return WaitIdle();
        yield return Shot("prediction-air");
        // The green line followed to where the movie's inputs end, then letting go until Pete stops
        TasPredict.PathToEnd = true;
        M.Seek(120);
        yield return WaitIdle();
        if(TasFreeCam.I != null && !TasFreeCam.I.CenterOnPete) TasFreeCam.I.ToggleCenter();
        yield return Shot("prediction-to-end");
        string toEndInfo = "no green line";
        if(TasPredict.I != null && TasPredict.I.Path != null)
        {
            toEndInfo = TasPredict.I.Path.Pos.Count + " points, labels:";
            foreach(KeyValuePair<int, string> l in TasPredict.I.Path.Labels) toEndInfo += " [" + l.Value + " @" + l.Key + "]";
        }
        if(TasFreeCam.I != null && TasFreeCam.I.CenterOnPete) TasFreeCam.I.ToggleCenter();
        TasPredict.PathToEnd = false;
        string predictInfo = TasPredict.I != null ? TasPredict.I.Lines.Count + " lines, " + TasPredict.I.LastMs.ToString("0.0") + " ms, error " + TasPredict.I.PathError.ToString("0.0000") + (TasPredict.I.Problem.Length > 0 ? ", problem " + TasPredict.I.Problem : "") : "none";
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

        // Export the whole (short) movie as a video file and wait until it's saved
        M.VideoEndScreen = false;
        M.StartVideo(true);
        float started = Time.realtimeSinceStartup;
        while(M.CurrentMode != TasManager.Mode.Video && Time.realtimeSinceStartup - started < 60f) yield return null;
        while((M.CurrentMode == TasManager.Mode.Video || TasVideoExport.I.Exporting || TasVideoExport.I.Finishing) && Time.realtimeSinceStartup - started < 180f) yield return null;
        string exportInfo = "export frames " + TasVideoExport.I.FramesWritten + ", file " + TasVideoExport.I.LastFile + ", result " + TasVideoExport.I.LastResult;

        // Prediction lines near enemies: play the self-test's long run and stop where an enemy is close
        string enemyInfo = "";
        M.ReplaceMovie(TasSelfTest.MakeMovie(1500, 7), "visual-test-enemies", 200);
        yield return WaitIdle();
        TasPredict.Show = true;
        TasHud.I.ShowTab("Ghosts");
        if(TasFreeCam.I != null && !TasFreeCam.I.CenterOnPete) TasFreeCam.I.ToggleCenter();
        bool shotHit = false, shotPogo = false, shotWill = false;
        for(int f = 200; f < 1160 && !(shotHit && shotPogo && shotWill); f += 6)
        {
            M.Seek(f);
            yield return WaitIdle();
            if(M.Frame != f || TasPredict.I == null) break;
            TasPredict.I.ComputeNow();
            bool hasHit = false, hasPogo = false;
            foreach(TasPrediction line2 in TasPredict.I.Lines)
            {
                if(line2.Color == TasPredict.HitColor) hasHit = true;
                if(line2.Color == TasPredict.AttackColor || line2.Color == TasPredict.AttackUpColor || line2.Color == TasPredict.AttackDownColor) hasPogo = true;
            }
            TasPrediction pathNow = TasPredict.I.Path;
            if(!shotWill && pathNow != null && pathNow.Hits.Exists(h => h >= 12))
            {
                yield return Shot("prediction-will-hit");
                enemyInfo += " will-hit warning at frame " + f + ";";
                shotWill = true;
            }
            if((hasHit && !shotHit) || (hasPogo && !shotPogo))
            {
                string kind = hasPogo && !shotPogo ? "attack" : "enemy";
                yield return Shot("prediction-" + kind);
                enemyInfo += " " + kind + " at frame " + f + " (" + TasPredict.I.Lines.Count + " lines, " + TasPredict.I.LastMs.ToString("0.0") + " ms);";
                if(hasHit) shotHit = true;
                if(hasPogo) shotPogo = true;
            }
        }
        if(TasFreeCam.I != null && TasFreeCam.I.CenterOnPete) TasFreeCam.I.ToggleCenter();
        predictInfo += " |" + (enemyInfo.Length > 0 ? enemyInfo : " no enemy lines found");

        // The user's own run, if a copy is here (prediction lines with names, where they asked about them)
        if(File.Exists(Path.Combine(TasManager.TasFolder, "user-run3.txt")))
        {
            M.LoadMovie("user-run3");
            yield return WaitIdle();
            TasPredict.Frames = 100;
            TasPredict.ShowKnockback = true;
            if(TasFreeCam.I != null && !TasFreeCam.I.CenterOnPete) TasFreeCam.I.ToggleCenter();
            foreach(int uf in new[] { 141, 164 })
            {
                M.Seek(uf);
                yield return WaitIdle();
                yield return Shot("user-run-" + uf);
            }
            if(TasFreeCam.I != null && TasFreeCam.I.CenterOnPete) TasFreeCam.I.ToggleCenter();
        }

        File.WriteAllText(Path.Combine(folder, "done.txt"), "ok, desync " + M.DesyncFrame + ", tick errors " + M.TickLockErrors + " | " + exportInfo + " | predictions " + predictInfo + " | step back: " + freezeInfo + " | to end: " + toEndInfo + " | fill: " + fillInfo);
        Application.Quit(0);
    }
}
