using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using Buttons = PeteInput.Buttons;

// Automatic checks for the TAS tool. Start the TAS with "-tasSelfTest" on the command line.
// Writes the results to "TAS Files/selftest-results.txt" and quits.
public class TasSelfTest : MonoBehaviour
{
    private readonly StringBuilder report = new StringBuilder();
    private bool failed;

    private TasManager M { get { return TasManager.I; } }

    private void Log(string line)
    {
        report.AppendLine(line);
        Debug.Log("[TAS SELFTEST] " + line);
    }

    private void Check(bool ok, string what)
    {
        Log((ok ? "PASS  " : "FAIL  ") + what);
        if(!ok)
        {
            failed = true;
        }
    }

    private IEnumerator WaitIdle(int timeoutFrames = 200000)
    {
        int n = 0;
        yield return null;
        while(M == null || !M.IsIdle)
        {
            if(++n > timeoutFrames)
            {
                Log("Timed out waiting for the TAS");
                failed = true;
                yield break;
            }
            yield return null;
        }
    }

    private IEnumerator Start()
    {
        float t0 = Time.realtimeSinceStartup;
        Log("Pete TAS self-test  " + DateTime.Now);
        yield return WaitIdle();
        if(TasManager.HasArg("-tasProbeAttacks"))
        {
            yield return ProbeAttacks();
            yield break;
        }
        string primeStepArg = ArgAfter("-primeStep");
        if(primeStepArg != null)
        {
            TasStateRegistry.PrimeStep = float.Parse(primeStepArg, System.Globalization.CultureInfo.InvariantCulture);
        }
        string primeStepsArg = ArgAfter("-primeSteps");
        if(primeStepsArg != null)
        {
            TasStateRegistry.PrimeSteps = int.Parse(primeStepsArg);
        }
        Log("Rewind warm-up: " + TasStateRegistry.PrimeSteps + " step(s) of " + TasStateRegistry.PrimeStep);
        string probeMovie = ArgAfter("-tasProbePredict");
        if(probeMovie != null)
        {
            yield return ProbePredict(probeMovie, int.Parse(ArgAfter("-frame") ?? "1"));
            yield break;
        }
        if(TasManager.HasArg("-noCamSave"))
        {
            TasCamState.Enabled = false;
            Log("Saving the game camera's smoothing is switched off for this test (the old behaviour)");
        }
        string camMovie = ArgAfter("-tasCheckCamera");
        if(camMovie != null)
        {
            M.LoadMovie(camMovie);
            yield return WaitIdle();
            Log("Camera check on " + camMovie + " (" + M.Movie.Length + " frames)");
            yield return CameraTest(M.Movie.Length, true);
            Finish("check-camera.txt");
            yield break;
        }
        string checkMovie = ArgAfter("-tasCheckMovie");
        if(checkMovie != null)
        {
            yield return CheckMovie(checkMovie);
            yield break;
        }
        Check(M.Player != null && M.Player.Valid, "Level loaded and Pete found");
        Log("Saved objects: " + M.Registry.EntryCount + ", clock offset " + (M.ClockOffsetAfterBoot * 1000).ToString("0.000") + " ms");
        int frames = 1500;
        if(TasManager.HasArg("-short"))
        {
            frames = 400;
        }

        // 1. Play a long scripted movie with a bit of everything
        TasMovie movie = MakeMovie(frames, 7);
        M.ReplaceMovie(movie, "selftest", frames);
        float playStart = Time.realtimeSinceStartup;
        yield return WaitIdle();
        float playSecs = Time.realtimeSinceStartup - playStart;
        Check(M.Frame == frames || M.DeathFrame > 0, "Played to frame " + M.Frame + (M.DeathFrame > 0 ? " (Pete died on " + M.DeathFrame + ")" : ""));
        Log("Speed: " + (M.Frame / Mathf.Max(0.001f, playSecs)).ToString("0") + " frames per second while replaying");
        Check(M.TickLockErrors == 0, "Every frame ran exactly one physics step (errors: " + M.TickLockErrors + ")");
        int peteAsleep = 0;
        for(int f = 0; f <= M.Frame; f++)
        {
            TasSnapshot sn = M.GetSnapshot(f);
            if(sn != null && M.Registry.Values(sn, "=== Player ===/Pete rb.awake").Contains("rb.awake=0")) peteAsleep++;
        }
        Log("Frames where Pete's physics body was asleep: " + peteAsleep);
        int last = M.Frame;
        Vector2 endPos = M.Player.Position;
        Log("Pete ended at " + endPos + ", orbs " + M.Player.Orbs + ", HP " + M.Player.Hp + ", timer " + TasManager.FormatTime(M.Player.TimerSeconds));
        Log("Savestate size: " + (M.SnapshotBytes / (1024f * 1024f) / Mathf.Max(1, last)).ToString("0.000") + " MB per frame, capture " + M.LastCaptureMs.ToString("0.00") + " ms");
        ulong[] first = Fingerprints(last);
        Log("Timer probe (first run): " + TimerProbe(120));

        // 2. Exact replay gives the same result
        M.Verify();
        yield return WaitIdle();
        Check(M.DesyncFrame < 0, "Replaying from the start gives the same frames (first difference: " + M.DesyncFrame + ")");
        ulong[] clean = Fingerprints(last);
        Log("Timer probe (verify): " + TimerProbe(120));
        int same = 0;
        for(int f = 0; f <= last; f++) if(first[f] == clean[f]) same++;
        Log("Replay matched " + same + " of " + (last + 1) + " frames");

        // 3. Rewind and play again: compare with the clean run, for each rewind physics fix
        string bestMode = "";
        int bestBad = int.MaxValue;
        int bestAnimMode = 1;
        int bestLeadIn = int.MaxValue;
        float bestStep = 0.00001f;
        // Measure how exact rewinds really are, without the automatic replay from the start that hides differences
        M.AutoExact = false;
        foreach(float primeStep in new[] { 0.00001f, 0.02f })
        {
            int animMode = 1;
            TasManager.PrimeMode mode = TasManager.PrimeMode.PreviousFrame;
            TasStateRegistry.AnimRestoreMode = animMode;
            TasStateRegistry.PrimeStep = primeStep;
            Log("Physics warm-up step " + primeStep + ":");
            M.Priming = mode;
            M.Verify();
            yield return WaitIdle();
            clean = Fingerprints(last);
            TasSnapshot[] cleanSnaps = new TasSnapshot[last + 1];
            for(int f = 0; f <= last; f++) cleanSnaps[f] = M.GetSnapshot(f);
            Vector2[] cleanPos = new Vector2[last + 1];
            for(int f = 0; f <= last; f++) cleanPos[f] = M.Timeline[f].Pos;
            int checks = 0, bad = 0, firstBad = -1, restoreDiffs = 0, explained = 0, leadInDesyncs = 0;
            int within1mm = 0, within1cm = 0;
            float worst = 0f;
            float restoreMs = 0;
            int restores = 0;
            for(int k = 20; k + 30 <= last; k += 37)
            {
                M.DesyncFrame = -1;
                M.Seek(k);
                yield return WaitIdle();
                if(M.DesyncFrame >= 0)
                {
                    // Something came out different while replaying up to the target
                    if(leadInDesyncs++ < 2) Log("  replaying up to " + k + " differed at frame " + M.DesyncFrame);
                }
                restoreMs += M.LastRestoreMs;
                restores++;
                if(M.Frame != k)
                {
                    Log("  could not rewind to " + k);
                    bad++;
                    continue;
                }
                // Straight after loading, the game should look exactly like the savestate
                TasSnapshot now = M.Registry.Capture(k);
                if(now.Fingerprint != cleanSnaps[k].Fingerprint)
                {
                    restoreDiffs++;
                    if(restoreDiffs <= 2)
                    {
                        Log("  [" + mode + "] right after loading frame " + k + " these differ:\n" + M.Registry.DescribeDiff(cleanSnaps[k], now));
                    }
                }
                bool drifted = false;
                float maxErr = 0f;
                for(int j = 1; j <= 30; j++)
                {
                    M.StepForward(1);
                    yield return WaitIdle();
                    int f = k + j;
                    checks++;
                    maxErr = Mathf.Max(maxErr, Vector2.Distance(M.Timeline[f].Pos, cleanPos[f]));
                    if(!drifted && M.Timeline[f].Fingerprint != clean[f])
                    {
                        drifted = true;
                        bad++;
                        if(firstBad < 0) firstBad = f;
                        if(explained++ < 3)
                        {
                            string diff = M.Registry.DescribeDiff(cleanSnaps[f], M.GetSnapshot(f));
                            Log("  [" + mode + "] rewound to " + k + ", frame " + f + " came out different:\n" + diff);
                            if(diff.Contains("anim"))
                            {
                                // Show how Pete's animation went, frame by frame, in both runs
                                for(int t = Mathf.Max(k - 2, 1); t <= f; t++)
                                {
                                    TasSnapshot after = t > k ? M.GetSnapshot(t) : null;
                                    Log("    frame " + t + " clean: " + M.Registry.Values(cleanSnaps[t], "=== Player ===/Pete anim") + (after != null ? "\n               rewound: " + M.Registry.Values(after, "=== Player ===/Pete anim") : ""));
                                }
                            }
                        }
                    }
                }
                if(maxErr < 0.001f) within1mm++;
                if(maxErr < 0.01f) within1cm++;
                worst = Mathf.Max(worst, maxErr);
            }
            Log("  loads that weren't identical straight away: " + restoreDiffs);
            Log("  Pete's position 30 frames after a rewind: " + within1mm + " of " + restores + " within 0.001, " + within1cm + " within 0.01, worst " + worst.ToString("0.0000"));
            int rewinds = restores;
            Log("Rewind fix " + mode + ": " + (rewinds - bad) + " of " + rewinds + " rewinds came out exact for 30 frames" + (firstBad >= 0 ? " (first drift at frame " + firstBad + ")" : "") + ", avg rewind " + (restoreMs / Mathf.Max(1, restores)).ToString("0.00") + " ms");
            Log("  rewinds that differed while replaying up to the target: " + leadInDesyncs);
            if(bad + leadInDesyncs < bestBad + (bestLeadIn == int.MaxValue ? 0 : bestLeadIn) || bestBad == int.MaxValue)
            {
                bestBad = bad;
                bestLeadIn = leadInDesyncs;
                bestMode = mode.ToString() + ", warm-up step " + primeStep;
                bestAnimMode = animMode;
                bestStep = primeStep;
            }
        }
        Log("Best rewind fix: " + bestMode);
        Check(bestBad == 0 && bestLeadIn == 0, "Rewinding and replaying matches the clean run (best: " + bestMode + ", drifted rewinds " + bestBad + ", lead-in differences " + bestLeadIn + ")");
        TasStateRegistry.PrimeStep = bestStep;
        M.Priming = TasManager.PrimeMode.PreviousFrame;
        TasStateRegistry.AnimRestoreMode = bestAnimMode;
        M.AutoExact = true;

        // 3b. Changing an earlier input while standing on a later frame replays to the exact new result
        {
            int at = Mathf.Min(300, last - 1);
            int editAt = at - 20;
            M.Seek(at);
            yield return WaitIdle();
            Buttons old = M.Movie.Get(editAt);
            Buttons changed = old ^ Buttons.Jump;
            M.DesyncFrame = -1;
            M.SetInput(editAt, changed);
            yield return WaitIdle();
            bool stayed = M.Frame == at && M.GreenEnd >= at;
            ulong afterEdit = at < M.Timeline.Count ? M.Timeline[at].Fingerprint : 0;
            M.Verify();
            yield return WaitIdle();
            Check(stayed && M.DesyncFrame < 0 && M.Timeline[at].Fingerprint == afterEdit, "Changing an earlier input (frame " + editAt + ") while on frame " + at + " gives the exact result (desync " + M.DesyncFrame + ")");
            M.SetInput(editAt, old);
            yield return WaitIdle();
        }

        // 3c. Recording while playing at normal speed: pressing attack is saved and Pete really swings
        {
            int from = Mathf.Min(200, last - 60);
            M.Seek(from);
            yield return WaitIdle();
            M.Recording = true;
            TasManager.FakeHeld = Buttons.None;
            M.TogglePlay();
            int startFrame = M.Frame;
            int swingFrames = 0;
            int guard = 0;
            while(M.Frame < startFrame + 40 && guard++ < 2000)
            {
                int rel = M.Frame - startFrame;
                // A short tap on attack, like pressing O once
                TasManager.FakeHeld = rel >= 5 && rel < 9 ? Buttons.Attack : Buttons.None;
                yield return null;
                if(M.Player != null && M.Player.Valid && !M.Player.CanAttack) swingFrames++;
            }
            M.Pause();
            TasManager.FakeHeld = null;
            M.Recording = false;
            yield return WaitIdle();
            int savedAttackFrames = 0;
            for(int f = startFrame + 1; f <= startFrame + 40; f++)
            {
                if((M.Movie.Get(f) & Buttons.Attack) != 0) savedAttackFrames++;
            }
            Check(savedAttackFrames > 0 && swingFrames > 0, "Recording at normal speed: the attack press is saved (" + savedAttackFrames + " frames) and Pete swings (" + swingFrames + " frames)");
        }

        // 3d. Moving frames around keeps the rest of the run lined up
        {
            TasMovie before = M.Movie.Clone();
            int at = Mathf.Min(150, last - 60);
            M.Seek(at + 20);
            yield return WaitIdle();
            List<Buttons> five = new List<Buttons> { Buttons.None, Buttons.None, Buttons.None, Buttons.None, Buttons.None };
            M.InsertFrames(at, five);
            yield return WaitIdle();
            bool shifted = true;
            for(int k = 0; k < 30; k++)
            {
                if(M.Movie.Get(at + 5 + k) != before.Get(at + k)) shifted = false;
            }
            M.DeleteFrames(at, 5);
            yield return WaitIdle();
            Check(shifted && M.Movie.SameInputs(before), "Inserting 5 frames moves everything after 5 frames later, and deleting them puts it all back");

            List<Buttons> piece = M.Movie.GetRange(at, 10);
            M.SavePreset("selftest-preset", piece);
            List<Buttons> loaded = M.LoadPreset("selftest-preset");
            bool presetSame = loaded != null && loaded.Count == piece.Count;
            for(int k = 0; presetSame && k < piece.Count; k++) presetSame = loaded[k] == piece[k];
            M.ReplaceFrames(at + 30, 3, loaded);
            yield return WaitIdle();
            bool replaced = true;
            for(int k = 0; k < 10; k++) if(M.Movie.Get(at + 30 + k) != piece[k]) replaced = false;
            for(int k = 0; k < 20; k++) if(M.Movie.Get(at + 40 + k) != before.Get(at + 33 + k)) replaced = false;
            Check(presetSame && replaced, "A preset saves and loads, and pasting it over 3 selected frames moves the rest 7 frames later");
            M.Verify();
            yield return WaitIdle();
            Check(M.DesyncFrame < 0, "After moving frames around, an exact replay agrees with what the tool showed (desync " + M.DesyncFrame + ")");
            try { File.Delete(Path.Combine(TasManager.PresetFolder, "selftest-preset.txt")); } catch { }
            M.ReplaceMovie(before, "selftest", last);
            yield return WaitIdle();
        }

        // 3e. Undo and redo put the movie, and where Pete ends up, back exactly
        {
            int at = Mathf.Min(250, last - 1);
            M.Seek(at);
            yield return WaitIdle();
            TasMovie original = M.Movie.Clone();
            ulong originalPrint = M.Timeline[at].Fingerprint;
            int undoBefore = M.UndoCount;
            M.SetInput(at - 30, M.Movie.Get(at - 30) ^ Buttons.Jump);
            yield return WaitIdle();
            M.InsertFrames(at - 20, new List<Buttons> { Buttons.Right, Buttons.Right, Buttons.Right });
            yield return WaitIdle();
            TasMovie edited = M.Movie.Clone();
            ulong editedPrint = M.Timeline[at].Fingerprint;
            M.Undo();
            yield return WaitIdle();
            M.Undo();
            yield return WaitIdle();
            bool undone = M.Movie.SameInputs(original) && M.Frame == at && M.Timeline.Count > at && M.Timeline[at].Fingerprint == originalPrint;
            Check(undone, "Undo twice puts back the inputs and Pete's exact result on frame " + at + " (same inputs " + M.Movie.SameInputs(original) + ", frame " + M.Frame + ")");
            M.Redo();
            yield return WaitIdle();
            M.Redo();
            yield return WaitIdle();
            bool redone = M.Movie.SameInputs(edited) && M.Frame == at && M.Timeline.Count > at && M.Timeline[at].Fingerprint == editedPrint;
            Check(redone && M.UndoCount == undoBefore + 2 && M.RedoCount == 0, "Redo twice brings both changes back exactly (undo steps " + (M.UndoCount - undoBefore) + ", redo steps left " + M.RedoCount + ")");
            M.Verify();
            yield return WaitIdle();
            Check(M.DesyncFrame < 0, "After undo and redo, an exact replay agrees with what the tool showed (desync " + M.DesyncFrame + ")");
            M.Undo();
            yield return WaitIdle();
            M.Undo();
            yield return WaitIdle();
            Check(M.Movie.SameInputs(original), "Undoing again leaves the original movie");
        }

        // 3g. Going back to an older try brings back its inputs and its exact route, and keeps the route you had
        {
            int at = Mathf.Min(200, last - 1);
            M.Seek(at);
            yield return WaitIdle();
            TasMovie original = M.Movie.Clone();
            ulong originalPrint = M.Timeline[at].Fingerprint;
            M.Ghosts.Clear();
            M.SetInput(at - 40, M.Movie.Get(at - 40) ^ Buttons.Right);
            yield return WaitIdle();
            TasMovie edited = M.Movie.Clone();
            TasGhost oldTry = M.Ghosts.Count > 0 ? M.Ghosts[0] : null;
            int split = oldTry != null ? M.SplitFrame(oldTry) : -9;
            Check(oldTry != null && oldTry.Inputs != null && oldTry.Inputs.SameInputs(original), "Changing an input keeps the old route as a try, with its inputs (" + (oldTry != null ? oldTry.Name : "none") + ", goes a different way from frame " + split + ")");
            M.UseTry(oldTry);
            yield return WaitIdle();
            bool wentBack = M.Movie.SameInputs(original) && M.Frame == at && M.Timeline.Count > at && M.Timeline[at].Fingerprint == originalPrint;
            bool kept = M.Ghosts.Exists(g => g.Inputs != null && g.Inputs.SameInputs(edited)) && !M.Ghosts.Contains(oldTry);
            Check(wentBack && kept, "\"Go back\" to a try puts back its inputs and Pete's exact result on frame " + at + ", and keeps the changed route as a try (back " + wentBack + ", kept " + kept + ")");
            M.Verify();
            yield return WaitIdle();
            Check(M.DesyncFrame < 0, "After going back to a try, an exact replay agrees (desync " + M.DesyncFrame + ")");
        }

        // 3h. Sections, start points and practice moves
        yield return SectionsTest(last);

        // 3f. The game camera is exactly where the game would have it, however you got to the frame
        yield return CameraTest(last, false);

        // 4. Movie text format round trip
        string text = movie.ToText();
        TasMovie back = TasMovie.FromText(text);
        Check(back.SameInputs(movie) && back.Length == movie.Length, "Movie file save/load keeps every input (" + movie.Length + " frames, " + text.Split('\n').Length + " lines)");
        string path = Path.Combine(TasManager.TasFolder, "selftest-movie.txt");
        movie.Save(path);
        Check(TasMovie.Load(path).SameInputs(movie), "Movie file on disk reads back the same");

        // 5. Normal play is untouched: with TAS mode off, PeteInput reads Unity's Input directly
        TasHooks.Active = false;
        bool passThrough = PeteInput.GetAxisRaw("Horizontal") == Input.GetAxisRaw("Horizontal") && PeteInput.GetButton("Jump") == Input.GetButton("Jump");
        TasHooks.Active = true;
        Check(passThrough, "With TAS mode off, game input comes straight from the keyboard");

        // 6. Auto-search on a short run to a line just ahead of Pete
        int searchFrom = Mathf.Min(120, last - 1);
        M.Seek(searchFrom);
        yield return WaitIdle();
        // Put the line across the route the movie really takes, so we know it can be reached
        Vector2 p = M.Player.Position;
        int g = searchFrom + 10;
        while(g < Mathf.Min(last, searchFrom + 70) && Mathf.Abs(M.Timeline[g].Pos.x - p.x) < 2f) g++;
        Vector2 q = M.Timeline[g].Pos;
        TasLine line = new TasLine();
        line.Name = "Test line";
        line.Vertical = true;
        line.Center = new Vector2((p.x + q.x) * 0.5f, (p.y + q.y) * 0.5f);
        line.HalfLength = 3f;
        line.Color = Color.red;
        float movieCross = line.FirstCrossing(M.RoutePositions, searchFrom + 1);
        Log("Search test line at " + line.Center + "; the movie itself reaches it at +" + (movieCross >= 0 ? (movieCross - searchFrom).ToString("0.00") : "never"));
        M.Lines.Add(line);
        Log("Search start state: live timer " + M.Player.TimerSeconds.ToString("0.000") + ", savestate " + M.Registry.Values(M.GetSnapshot(searchFrom), "TimerManager.currentTime") + ", frame " + M.Frame);
        foreach(TasSearch.Method method in new[] { TasSearch.Method.Beam, TasSearch.Method.JumpTiming })
        {
            TasSearch s = new TasSearch();
            s.Kind = method;
            s.Target = line;
            s.Window = 10;
            s.MaxHold = 8;
            s.BeamWidth = 6;
            s.MaxFrames = 80;
            // Attacks make the search load savestates in the middle of attack animations
            s.TryAttacks = method == TasSearch.Method.Beam;
            float st = Time.realtimeSinceStartup;
            M.StartSearch(s);
            yield return WaitIdle();
            float secs = Time.realtimeSinceStartup - st;
            bool found = s.Results.Count > 0 && s.Results[0].Crossing >= 0;
            Log(s.Describe() + ": tried " + s.Tried + " frames in " + secs.ToString("0.0") + " s" + (found ? ", best +" + s.Results[0].Crossing.ToString("0.00") + " frames" : ", line not reached") + ", results " + s.Results.Count + (s.Results.Count > 0 ? ", closest miss " + s.Results[0].DistanceLeft.ToString("0.00") + " end " + s.Results[0].Path[s.Results[0].Path.Count - 1] : ""));
            if(movieCross >= 0) Check(found && s.Results[0].Crossing <= movieCross - searchFrom + 0.001f, s.Describe() + " finds a way at least as fast as the movie");
            Check(M.Frame == searchFrom, s.Describe() + " puts the game back where it started");
            if(found)
            {
                TasSearch.Result r = s.Results[0];
                bool done = false;
                M.ApplyInputs(searchFrom + 1, r.Inputs, () => { done = true; });
                yield return WaitIdle();
                Log("  Timer probe (after apply): " + TimerProbe(searchFrom));
                float c = line.FirstCrossing(M.RoutePositions, searchFrom + 1);
                float verified = c >= 0 ? c - searchFrom : -1f;
                // Where did the search's own simulation first differ from the exact replay?
                bool pathMatches = true;
                for(int i = 1; i < r.Path.Count && searchFrom + i < M.RoutePositions.Count; i++)
                {
                    float d = Vector2.Distance(r.Path[i], M.RoutePositions[searchFrom + i]);
                    bool printDiff = i - 1 < r.Fingerprints.Count && r.Fingerprints[i - 1] != M.Timeline[searchFrom + i].Fingerprint;
                    if(d > 1e-6f || printDiff)
                    {
                        pathMatches = false;
                        Log("  search path first differs from the exact replay at +" + i + " (distance " + d.ToString("0.000000") + (printDiff ? ", state differs" : "") + ")");
                        if(r.FirstSnaps.Count > 0) Log("  search's own first frame: " + M.Registry.Values(r.FirstSnaps[0], "TimerManager.currentTime") + " frame " + r.FirstSnaps[0].Frame);
                        if(i - 1 < r.FirstSnaps.Count && M.GetSnapshot(searchFrom + i) != null)
                        {
                            Log(M.Registry.DescribeDiff(M.GetSnapshot(searchFrom + i), r.FirstSnaps[i - 1], 20));
                        }
                        Log("  inputs: movie " + TasMovie.ButtonsToShortText(M.Movie.Get(searchFrom)) + " then search " + TasMovie.ButtonsToShortText(r.Inputs[0]) + ", " + (r.Inputs.Count > 1 ? TasMovie.ButtonsToShortText(r.Inputs[1]) : ""));
                        break;
                    }
                }
                Check(done && verified >= 0 && Mathf.Abs(verified - r.Crossing) < 0.01f, s.Describe() + " result checks out with an exact replay (search said +" + r.Crossing.ToString("0.00") + ", replay says +" + verified.ToString("0.00") + ")");
                if(r.Fingerprints.Count > 0) Check(pathMatches, s.Describe() + " simulated every frame exactly like the real replay");
                Check(M.DesyncFrame < 0, s.Describe() + " left no differences behind (desync " + M.DesyncFrame + ")");
                M.Seek(searchFrom);
                yield return WaitIdle();
            }
        }

        // 7. Prediction lines
        yield return PredictionTest();

        Log("");
        Log((failed ? "SOME CHECKS FAILED" : "ALL CHECKS PASSED") + "  (" + (Time.realtimeSinceStartup - t0).ToString("0") + " s)");
        try
        {
            File.WriteAllText(Path.Combine(TasManager.TasFolder, "selftest-results.txt"), report.ToString());
        }
        catch(Exception ex)
        {
            Debug.LogError(ex);
        }
        yield return null;
#if UNITY_EDITOR
        if(TasManager.HasArg("-tasEditorSelfTest"))
        {
            UnityEditor.EditorApplication.Exit(failed ? 1 : 0);
        }
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit(failed ? 1 : 0);
#endif
    }

    private static string ArgAfter(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for(int i = 0; i < args.Length - 1; i++)
        {
            if(string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private void Finish(string file)
    {
        try
        {
            File.WriteAllText(Path.Combine(TasManager.TasFolder, file), report.ToString());
        }
        catch(Exception ex)
        {
            Debug.LogError(ex);
        }
        Application.Quit(0);
    }

    // Records, frame by frame, when the sword is out after each kind of attack press (for the prediction lines)
    private IEnumerator ProbeAttacks()
    {
        TasMovie mv = new TasMovie();
        for(int f = 1; f <= 120; f++) mv.Set(f, Buttons.None);
        mv.Set(10, Buttons.Attack);
        mv.Set(40, Buttons.Up | Buttons.Attack);
        for(int f = 70; f < 82; f++) mv.Set(f, Buttons.Jump);
        mv.Set(76, Buttons.Down | Buttons.Attack | Buttons.Jump);
        mv.Set(100, Buttons.Attack);
        mv.Set(101, Buttons.Attack);
        M.ReplaceMovie(mv, "attack-probe", 0);
        yield return WaitIdle();
        GameObject pete = M.Player.Go;
        MeleeAttackManager mam = pete.GetComponent<MeleeAttackManager>();
        System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        GameObject side = (GameObject)typeof(MeleeAttackManager).GetField("meleeWeapon", any).GetValue(mam);
        GameObject down = (GameObject)typeof(MeleeAttackManager).GetField("meleeWeaponDown", any).GetValue(mam);
        GameObject up = (GameObject)typeof(MeleeAttackManager).GetField("meleeWeaponUp", any).GetValue(mam);
        Animator an = pete.GetComponent<Animator>();
        string[] states = { "Pete-Attack", "Pete-Attack-2", "Pete-Attack-Down", "Pete-Attack-Up", "Pete-Idle 0", "Pete-Running 0", "Pete-Jumping 0", "Pete-Falling 0", "Pete-Up", "Pete-Down" };
        Log("frame input | sword side/down/up | canAttack | animator state, time | ground");
        for(int f = 1; f <= 120; f++)
        {
            M.StepForward(1);
            yield return WaitIdle();
            AnimatorStateInfo st = an.GetCurrentAnimatorStateInfo(0);
            string state = "?";
            foreach(string sname in states)
            {
                if(st.IsName(sname)) state = sname;
            }
            Log(f + " " + TasMovie.ButtonsToShortText(M.Movie.Get(f)) + " | " + (side.activeSelf ? "S" : "-") + (down.activeSelf ? "D" : "-") + (up.activeSelf ? "U" : "-")
                + " | " + M.Player.CanAttack + " | " + state + " " + (st.normalizedTime * st.length).ToString("0.000") + " | " + M.Player.OnGround);
        }
        Log("side weapon local " + side.transform.localPosition.ToString("F4") + " down " + down.transform.localPosition.ToString("F4") + " up " + up.transform.localPosition.ToString("F4") + ", pete scale " + pete.transform.localScale.ToString("F3"));
        Finish("attack-probe.txt");
    }

    // Prints every prediction line from one frame of a movie, step by step (to look into odd-looking lines)
    private IEnumerator ProbePredict(string name, int frame)
    {
        M.LoadMovie(name);
        yield return WaitIdle();
        M.Seek(frame);
        yield return WaitIdle();
        TasPredict pr = TasPredict.I;
        TasPredict.ShowPath = TasPredict.ShowLongJump = TasPredict.ShowShortJump = TasPredict.ShowLetGo = TasPredict.ShowHits = TasPredict.ShowAttacks = TasPredict.ShowKnockback = true;
        TasPredict.Frames = 60;
        pr.ComputeNow();
        Log("Frame " + M.Frame + ": " + DescribeNow() + " canMove " + M.Player.CanMove + " kb " + M.Player.KnockbackLeft.ToString("R") + " input " + TasMovie.ButtonsToShortText(M.Movie.Get(M.Frame)) + " next " + TasMovie.ButtonsToShortText(M.Movie.Get(M.Frame + 1)));
        Log(pr.DescribeRealFoes());
        foreach(TasPrediction p in pr.Lines)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(p.Name + " [" + p.Tag + "]: " + p.Pos.Count + " points, jump at " + p.JumpAt + ", hits " + string.Join(",", p.Hits) + ", death " + p.DeathAt + ", same as path " + p.SameAsPath + ", labels");
            foreach(KeyValuePair<int, string> l in p.Labels) sb.Append(" [" + l.Value + " @" + l.Key + "]");
            Log(sb.ToString());
            for(int j = 0; j < p.Pos.Count && j <= 24; j++)
            {
                Log("   +" + j + " pos " + V(p.Pos[j]) + " vel " + V(p.Vel[j]) + " ground " + p.Ground[j] + (j < p.Knock.Count && p.Knock[j] ? " KNOCK" : ""));
            }
        }
        // What the real game does with the movie's own inputs, for comparison with the green line
        Log("Real game with the movie's inputs:");
        for(int j = 1; j <= 24 && M.Frame < M.Movie.Length; j++)
        {
            M.StepForward(1);
            yield return WaitIdle();
            Log("   +" + j + " " + DescribeNow());
        }
        Finish("probe-predict.txt");
    }

    // Loads a movie, makes a clean run of it, then goes back one frame at a time (like holding B) and
    // reports every rewind that comes out different
    private IEnumerator CheckMovie(string name)
    {
        if(TasManager.HasArg("-noPredict"))
        {
            TasPredict.Show = false;
            Log("Prediction lines switched off for this check");
        }
        M.LoadMovie(name);
        yield return WaitIdle();
        M.Seek(M.Movie.Length);
        yield return WaitIdle();
        M.Verify();
        yield return WaitIdle();
        int last = M.Frame;
        Log("Clean run of " + name + " to frame " + last + " (movie " + M.Movie.Length + " frames), death " + M.DeathFrame + ", desync " + M.DesyncFrame);
        M.DesyncFrame = -1;
        M.AutoExact = false;
        int bad = 0;
        for(int k = last - 1; k >= 1; k--)
        {
            M.Seek(k);
            yield return WaitIdle();
            if(M.DesyncFrame >= 0)
            {
                bad++;
                badFrames.Add(k);
                Log("Going back to " + k + ": frame " + M.DesyncFrame + " came out different");
                M.DesyncFrame = -1;
                M.Verify();
                yield return WaitIdle();
                M.DesyncFrame = -1;
            }
        }
        Log("Rewinds that came out different: " + bad + " of " + (last - 1));
        // Same again with the automatic replay from the start switched on: no difference should ever be shown
        M.AutoExact = true;
        M.Verify();
        yield return WaitIdle();
        M.DesyncFrame = -1;
        int replaysBefore = M.AutoExactReplays;
        int shown = 0;
        for(int k = last - 1; k >= 1; k--)
        {
            M.Seek(k);
            yield return WaitIdle();
            if(M.DesyncFrame >= 0 || M.Frame != k)
            {
                shown++;
                Log("With the safety net on, going back to " + k + " still showed a difference (frame " + M.DesyncFrame + ", now on " + M.Frame + ")");
                M.DesyncFrame = -1;
            }
        }
        Log("With the safety net on: differences shown " + shown + ", automatic replays from the start " + (M.AutoExactReplays - replaysBefore));
        M.AutoExact = false;
        // Trace a few of them frame by frame against the clean run
        // (earliest ones first: they're the least tangled up with earlier differences)
        int traced = TasManager.HasArg("-noTrace") ? 99 : 0;
        for(int bi = badFrames.Count - 1; bi >= 0 && traced < 6; bi--, traced++)
        {
            yield return TraceRewind(badFrames[bi]);
        }
        Finish("check-movie.txt");
    }

    // ---------- The game camera ----------
    // After going back, stepping, playing slowly or switching camera modes, the camera must be exactly where a
    // straight run from the start (what the real game does) has it on that frame.
    private Vector3[] camRef;
    private Vector2[] camPete;
    private bool[] camHave;
    private string[] camDesc;
    private string camLastDesc = "";
    private int camLastFrame = -1;
    private int camChecked, camBad, camSkipped, camLogged;
    private float camWorst;
    private string camWhat = "";

    // Camera position, with the zoom as the third number
    private static Vector3 CamNow()
    {
        Camera c = Camera.main;
        if(c == null)
        {
            return Vector3.zero;
        }
        Vector3 p = c.transform.position;
        return new Vector3(p.x, p.y, c.orthographicSize);
    }

    private void CompareCam()
    {
        int f = M.Frame;
        if(camRef == null || f < 1 || f >= camRef.Length || !camHave[f] || M.Player == null || !M.Player.Valid)
        {
            return;
        }
        if(Vector2.Distance(M.Player.Position, camPete[f]) > 1e-5f)
        {
            // Pete himself went somewhere else (a physics difference, checked elsewhere), so the camera can't match
            camSkipped++;
            return;
        }
        camChecked++;
        string desc = TasCamState.Describe();
        Vector3 now = CamNow();
        float err = Vector3.Distance(now, camRef[f]);
        camWorst = Mathf.Max(camWorst, err);
        if(err > 0.0005f)
        {
            camBad++;
            if(camLogged++ < 10)
            {
                Log("  " + camWhat + ": on frame " + f + " the camera is " + err.ToString("0.0000") + " off (" + now.ToString("F4") + ", clean run " + camRef[f].ToString("F4") + ")");
            }
            if(camLogged <= 2)
            {
                Log("    clean run, frame " + (f - 1) + ": " + (f >= 1 ? camDesc[f - 1] : "") + "\n    clean run, frame " + f + ": " + camDesc[f]);
                Log("    this time, frame " + camLastFrame + ": " + camLastDesc + "\n    this time, frame " + f + ": " + desc);
            }
        }
        camLastDesc = desc;
        camLastFrame = f;
    }

    private IEnumerator CameraTest(int last, bool thorough)
    {
        // The clean run: straight from the start without going back, like the real game
        camRef = new Vector3[last + 1];
        camPete = new Vector2[last + 1];
        camHave = new bool[last + 1];
        camDesc = new string[last + 1];
        TasManager.TickHook = () =>
        {
            int f = M.Frame;
            if(f >= 0 && f <= last && M.Player != null && M.Player.Valid)
            {
                camRef[f] = CamNow();
                camDesc[f] = TasCamState.Describe();
                camPete[f] = M.Player.Position;
                camHave[f] = true;
            }
        };
        M.ReplayFromStart(last);
        yield return WaitIdle();
        last = Mathf.Min(last, M.Frame);
        Log("Camera: clean run to frame " + last);
        camChecked = camBad = camSkipped = camLogged = 0;
        camWorst = 0f;
        TasManager.TickHook = CompareCam;
        System.Random rng = new System.Random(11);

        // Going back one frame at a time (holding B); every frame replayed on the way is compared too
        camWhat = "going back";
        int stopAt = Mathf.Max(1, last - (thorough ? 400 : 60));
        for(int k = last - 1; k >= stopAt; k--)
        {
            M.Seek(k);
            yield return WaitIdle();
            CompareCam();
            yield return null;
            CompareCam();
        }
        // Jumping around the run
        camWhat = "jumping to a frame";
        for(int i = 0; i < (thorough ? 80 : 15); i++)
        {
            int from = M.Frame, to = rng.Next(1, last);
            camWhat = "jumping from frame " + from + " to " + to;
            M.Seek(to);
            yield return WaitIdle();
            CompareCam();
        }
        Log("Camera: after going back and jumping around, " + camBad + " of " + camChecked + " frames off");
        // Stepping forward one frame at a time, with pauses in between
        camWhat = "stepping forward";
        M.Seek(rng.Next(1, Mathf.Max(2, last / 2)));
        yield return WaitIdle();
        for(int j = 0; j < (thorough ? 200 : 60) && M.Frame < last; j++)
        {
            M.StepForward(1);
            yield return WaitIdle();
            yield return null;
        }
        // Playing at half speed
        camWhat = "playing at 50%";
        int speed = M.SpeedPercent;
        M.SpeedPercent = 50;
        M.Seek(rng.Next(1, Mathf.Max(2, last / 2)));
        yield return WaitIdle();
        int playTo = Mathf.Min(last, M.Frame + (thorough ? 150 : 50));
        M.TogglePlay();
        for(int n = 0; n < 2000 && M.Frame < playTo && M.CurrentMode == TasManager.Mode.Playing; n++)
        {
            yield return null;
        }
        M.Pause();
        M.SpeedPercent = speed;
        yield return WaitIdle();
        // The center and zoom-out cameras: the game camera keeps running underneath, so switching back
        // shows exactly what the game would
        TasFreeCam fc = TasFreeCam.I;
        if(fc != null)
        {
            for(int mode = 0; mode < 2; mode++)
            {
                camWhat = mode == 0 ? "center camera" : "zoom-out camera";
                M.Seek(rng.Next(1, Mathf.Max(2, last - 60)));
                yield return WaitIdle();
                if(mode == 0) fc.ToggleCenter(); else fc.Toggle();
                for(int j = 0; j < 40 && M.Frame < last; j++)
                {
                    M.StepForward(1);
                    yield return WaitIdle();
                }
                M.StepBack(7);
                yield return WaitIdle();
                if(mode == 0) fc.ToggleCenter(); else fc.Toggle();
                yield return null;
                yield return null;
                camWhat += " (after switching back)";
                CompareCam();
            }
        }
        TasManager.TickHook = null;
        camWhat = "";
        Log("Camera: compared " + camChecked + " frames, " + camBad + " off by more than 0.0005 (worst " + camWorst.ToString("0.0000") + "), " + camSkipped + " skipped because Pete himself was somewhere else");
        Check(camBad == 0 && camChecked > 0, "The game camera is exactly where a straight run has it after going back, jumping around, stepping, playing at 50% and switching camera modes (" + camBad + " of " + camChecked + " frames off, worst " + camWorst.ToString("0.0000") + ")");
    }

    // "Will you get hit?": from many frames of the run, the first hit the green line warns about must be exactly the
    // first time the real game takes away HP (and no warning when the real game doesn't)
    private IEnumerator WillHitTest(TasPredict pr)
    {
        const int ahead = 50;
        int last = M.Movie.Length;
        int[] hp = new int[last + 2];
        bool[] have = new bool[last + 2];
        TasManager.TickHook = () =>
        {
            int f = M.Frame;
            if(f >= 0 && f <= last && M.Player != null && M.Player.Valid)
            {
                hp[f] = M.Player.Hp;
                have[f] = true;
            }
        };
        M.ReplayFromStart(last);
        yield return WaitIdle();
        TasManager.TickHook = null;
        last = Mathf.Min(last, M.Frame);
        if(M.DeathFrame > 0)
        {
            last = Mathf.Min(last, M.DeathFrame - 1);
        }
        int realHits = 0;
        for(int f = 2; f <= last; f++)
        {
            if(have[f] && have[f - 1] && hp[f] < hp[f - 1]) realHits++;
        }
        M.ReplayFromStart(5);
        yield return WaitIdle();
        int starts = 0, right = 0, warned = 0, shown = 0;
        for(int k = 5; k + ahead <= last; k += 7)
        {
            if(k > M.Frame)
            {
                M.Seek(k);
                yield return WaitIdle();
            }
            if(M.Frame != k)
            {
                continue;
            }
            TasPrediction p = pr.PredictPath(ahead);
            if(p == null)
            {
                continue;
            }
            int predicted = -1;
            foreach(int h in p.Hits)
            {
                if(h >= 1 && (predicted < 0 || h < predicted)) predicted = h;
            }
            int real = -1;
            for(int j = 1; j <= ahead && k + j <= last; j++)
            {
                if(have[k + j] && have[k + j - 1] && hp[k + j] < hp[k + j - 1])
                {
                    real = j;
                    break;
                }
            }
            starts++;
            if(predicted >= 1) warned++;
            if(predicted == real)
            {
                right++;
            }
            else if(shown++ < 5)
            {
                Log("  from frame " + k + ": the warning says " + (predicted >= 1 ? "a hit at +" + predicted : "no hit") + ", the real game " + (real >= 1 ? "takes HP at +" + real : "doesn't take HP") + " (within " + ahead + " frames)");
            }
        }
        Log("Will you get hit: real hits in the run " + realHits + ", starts checked " + starts + ", warnings shown " + warned);
        Check(starts > 0 && right == starts && (warned > 0 || realHits == 0), "\"Will you get hit?\" is right from every frame checked: it warns exactly when, and only when, the real game takes HP (" + right + " of " + starts + " starts, " + warned + " with a warning)");
    }

    private IEnumerator SectionsTest(int last)
    {
        TasMovie original = M.Movie.Clone();
        string originalName = M.MovieName;
        M.Seek(Mathf.Min(last, 700));
        yield return WaitIdle();
        // Only the test's own section takes part (any sections saved in TAS Files are put back afterwards)
        List<TasSection> userSections = new List<TasSection>(TasSections.All);
        TasSections.All.Clear();
        // A section from about frame 150 to about frame 260 of the route
        Vector2 a0 = M.Timeline[149].Pos, a1 = M.Timeline[150].Pos, b0 = M.Timeline[259].Pos, b1 = M.Timeline[260].Pos;
        bool vertical = Mathf.Abs(a1.x - a0.x) >= Mathf.Abs(a1.y - a0.y);
        TasSection s = TasSections.Create("selftest section", vertical, (a0 + a1) * 0.5f, (b0 + b1) * 0.5f);
        TasSections.Measure(s);
        float t1 = s.Time;
        int first1 = s.FirstFrame;
        Log("Section: crosses the start on " + s.StartCross.ToString("0.00") + ", the end on " + s.EndCross.ToString("0.00") + ", takes " + t1.ToString("0.00") + " frames");
        TasSections.SaveVersion(s);
        TasSectionVersion v = s.Best;
        Check(t1 > 0f && v != null && Mathf.Abs(v.Frames - t1) < 0.0001f && v.Inputs.Count == s.LastFrame - s.FirstFrame + 1, "A section between two gates is timed and its version saved (" + t1.ToString("0.00") + " frames, " + (v != null ? v.Inputs.Count : 0) + " inputs)");

        // Break the section (nothing held through it), then put the saved version back: exactly the same time again
        Dictionary<int, Buttons> blank = new Dictionary<int, Buttons>();
        for(int f = s.FirstFrame; f <= s.LastFrame; f++) blank[f] = Buttons.None;
        M.SetInputs(blank);
        yield return WaitIdle();
        M.Seek(Mathf.Min(M.Movie.Length, 700));
        yield return WaitIdle();
        TasSections.Measure(s);
        float broken = s.Time;
        string backText = "";
        TasSections.UseVersion(s, v, text => backText = text);
        yield return WaitIdle();
        TasSections.Measure(s);
        Check(s.FirstFrame == first1 && TasSections.SameRange(M.Movie, first1, v.Inputs) && Mathf.Abs(s.Time - t1) < 0.005f, "\"Use\" puts a saved version back exactly: " + (broken >= 0f ? broken.ToString("0.00") : "not finishing") + " with nothing held, " + (s.Time >= 0f ? s.Time.ToString("0.00") : "-") + " with the version (saved " + t1.ToString("0.00") + ")");
        M.ReplaceMovie(original.Clone(), originalName, Mathf.Min(original.Length, 700));
        yield return WaitIdle();

        // Make Pete arrive later: stand still for a few frames earlier on, without moving the section's inputs
        Dictionary<int, Buttons> idle = new Dictionary<int, Buttons>();
        for(int f = first1 - 60; f < first1 - 54; f++) idle[f] = Buttons.None;
        M.SetInputs(idle);
        yield return WaitIdle();
        M.Seek(Mathf.Min(M.Movie.Length, 700));
        yield return WaitIdle();
        TasSections.Measure(s);
        float t2 = s.Time;
        int first2 = s.FirstFrame;
        Log("After standing still earlier: the route enters the section on frame " + first2 + " (was " + first1 + "), it takes " + (t2 >= 0f ? t2.ToString("0.00") : "-") + " frames with the old inputs where they were");
        string result = "";
        if(first2 > 0)
        {
            TasSections.UseVersion(s, v, text => result = text);
            yield return WaitIdle();
            TasSections.Measure(s);
            bool placed = TasSections.SameRange(M.Movie, s.FirstFrame, v.Inputs);
            Log("Use version: " + result);
            Check(placed && s.FirstFrame == first2, "\"Use\" puts a saved version in exactly where Pete now crosses the start gate (frame " + s.FirstFrame + ", was " + first1 + ")");
            bool same = s.Time >= 0f && Mathf.Abs(s.Time - t1) < 0.005f;
            Check(same || result.Contains("comes in at"), "Lined up there, the section takes the same time, or the tool says Pete came in differently (" + (s.Time >= 0f ? s.Time.ToString("0.00") : "doesn't finish") + " vs " + t1.ToString("0.00") + (same ? "" : ", warned") + ")");
        }
        else
        {
            Check(false, "After standing still earlier, Pete still crosses the start gate");
        }
        M.ReplaceMovie(original.Clone(), originalName, Mathf.Min(original.Length, 700));
        yield return WaitIdle();

        // Building the run with only the route's own version changes nothing
        int lengthBefore = M.Movie.Length;
        TasSections.BuildRun();
        float waited = 0f;
        while((TasSections.Building || !M.IsIdle) && waited < 120f)
        {
            waited += Time.unscaledDeltaTime;
            yield return null;
        }
        Check(M.Movie.SameInputs(original) && M.Movie.Length == lengthBefore, "Build the run with the route's own best version leaves it as it was (" + TasSections.LastBuild.Replace("\n", " ") + ")");
        TasSections.All.Clear();
        TasSections.All.AddRange(userSections);
        try { System.IO.Directory.Delete(s.Folder, true); } catch(Exception ex) { Log("  couldn't remove the test section: " + ex.Message); }

        // Start points: save one, start an empty movie, go back to it: everything is as it was
        M.Seek(300);
        yield return WaitIdle();
        Vector2 pos300 = M.Player.Position;
        ulong print300 = M.Timeline[300].Fingerprint;
        TasSections.SaveStartPoint("selftest start");
        M.NewMovie();
        yield return WaitIdle();
        TasSections.RefreshStartPoints(true);
        TasStartPoint sp = TasSections.StartPoints.Find(x => x.Name == "selftest start");
        if(sp != null)
        {
            TasSections.GoTo(sp);
            yield return WaitIdle();
        }
        Check(sp != null && M.Frame == 300 && M.Player.Position == pos300 && M.Timeline.Count > 300 && M.Timeline[300].Fingerprint == print300, "A start point brings back frame 300 exactly, even from an empty movie (frame " + M.Frame + ")");
        if(sp != null)
        {
            try { System.IO.File.Delete(sp.File); } catch { }
            TasSections.RefreshStartPoints(true);
        }

        // Practice: put Pete somewhere; replays and going back do it the same way every time
        M.ReplaceMovie(original.Clone(), originalName, 120);
        yield return WaitIdle();
        Vector2 to = M.Player.Position + new Vector2(0f, 1.5f);
        M.SetPracticeTeleport(to);
        yield return WaitIdle();
        bool moved = M.Movie.IsPractice && M.Frame == 120 && (M.Player.Position - to).sqrMagnitude < 1e-8f;
        M.Seek(160);
        yield return WaitIdle();
        Vector2 at160 = M.Player.Position;
        M.DesyncFrame = -1;
        M.Verify();
        yield return WaitIdle();
        bool replayed = M.DesyncFrame < 0 && M.Player.Position == at160;
        M.Seek(110);
        yield return WaitIdle();
        M.Seek(160);
        yield return WaitIdle();
        bool back = M.DesyncFrame < 0 && M.Player.Position == at160;
        TasMovie round = TasMovie.FromText(M.Movie.ToText());
        Check(moved && replayed && back && round.TeleportFrame == 120 && round.TeleportTo == to, "Practice: Pete is put where you click, and replays, going back and the movie file all do it the same (moved " + moved + ", replay " + replayed + ", back " + back + ")");
        M.ClearPracticeTeleport();
        yield return WaitIdle();
        Check(!M.Movie.IsPractice && M.Movie.SameInputs(original), "Undo it: the movie is a real run again");
        M.ReplaceMovie(original.Clone(), originalName, Mathf.Min(original.Length, last));
        yield return WaitIdle();
    }

    private readonly List<int> badFrames = new List<int>();
    private readonly Dictionary<int, string> trace = new Dictionary<int, string>();
    private string soundsThisTick = "";

    // Pete's state after a frame, as text (for comparing two runs)
    private string DescribeNow()
    {
        TasManager m = M;
        if(m.Player == null || !m.Player.Valid) return "-";
        Rigidbody2D rb = m.Player.Rb;
        GameObject pete = m.Player.Go;
        StringBuilder sb = new StringBuilder();
        sb.Append("pos " + rb.position.x.ToString("R") + "," + rb.position.y.ToString("R") + " vel " + rb.velocity.x.ToString("R") + "," + rb.velocity.y.ToString("R"));
        System.Reflection.FieldInfo landed = typeof(Jump).GetField("hasLanded", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        sb.Append(" ground " + m.Player.OnGround + " landed " + (landed != null ? landed.GetValue(pete.GetComponent<Jump>()) : "?") + " coyote " + m.Player.CoyoteLeft.ToString("R") + " awake " + rb.IsAwake());
        ContactPoint2D[] cps = new ContactPoint2D[32];
        int n = rb.GetContacts(cps);
        List<string> cs = new List<string>();
        for(int i = 0; i < n; i++)
        {
            Collider2D other = cps[i].collider != null && cps[i].collider.attachedRigidbody == rb ? cps[i].otherCollider : cps[i].collider;
            cs.Add((other != null ? other.name : "?") + (cps[i].collider != null && cps[i].collider.isTrigger || cps[i].otherCollider != null && cps[i].otherCollider.isTrigger ? "(trigger)" : "") + ":" + cps[i].normal.ToString("F2"));
        }
        cs.Sort();
        sb.Append(" contacts[" + string.Join(" ", cs) + "]");
        sb.Append(" random " + string.Join(",", TasStateRegistry.RandomStateParts(UnityEngine.Random.state)));
        return sb.ToString();
    }

    private void RecordTick()
    {
        trace[M.Frame] = DescribeNow() + " sounds[" + soundsThisTick + "]";
        soundsThisTick = "";
    }

    // Goes back to frame k the way stepping back does, and compares every frame of the replay with a clean run
    private IEnumerator TraceRewind(int k)
    {
        int from = Mathf.Max(1, k - 34);
        TasHooks.SoundPlayed = src => soundsThisTick += (src != null && src.clip != null ? src.clip.name : "?") + " ";
        TasManager.TickHook = RecordTick;
        trace.Clear();
        soundsThisTick = "";
        M.ReplayFromStart(from);
        yield return WaitIdle();
        while(M.Frame < k + 3)
        {
            M.StepForward(1);
            yield return WaitIdle();
        }
        Dictionary<int, string> clean = new Dictionary<int, string>(trace);
        trace.Clear();
        soundsThisTick = "";
        M.Seek(k);
        int loaded = M.Frame;
        string afterLoad = DescribeNow();
        yield return WaitIdle();
        TasManager.TickHook = null;
        TasHooks.SoundPlayed = null;
        Log("Trace of going back to " + k + " (loaded the savestate of frame " + loaded + "):");
        string c0;
        if(clean.TryGetValue(loaded, out c0))
        {
            string cleanNoSound = c0.Substring(0, c0.IndexOf(" sounds[") < 0 ? c0.Length : c0.IndexOf(" sounds["));
            if(cleanNoSound != afterLoad)
            {
                Log("  right after loading, frame " + loaded + " differs:\n    clean:  " + cleanNoSound + "\n    loaded: " + afterLoad);
            }
        }
        int shown = 0;
        for(int f = loaded + 1; f <= k && shown < 3; f++)
        {
            string a, b;
            clean.TryGetValue(f, out a);
            trace.TryGetValue(f, out b);
            if(a != b)
            {
                shown++;
                Log("  frame " + f + " differs:\n    clean:  " + a + "\n    replay: " + b);
            }
        }
        if(shown == 0)
        {
            Log("  every traced frame matched");
        }
    }

    // The green prediction line should follow the route the real game takes, and working the lines out
    // must never change the real game.
    private IEnumerator PredictionTest()
    {
        TasPredict pr = TasPredict.I;
        if(pr == null)
        {
            Check(false, "Prediction lines are running");
            yield break;
        }
        yield return WillHitTest(pr);
        Log("Prediction speed: " + pr.Benchmark());
        // A clean run of the whole movie to compare against
        M.Verify();
        yield return WaitIdle();
        M.Seek(M.Movie.Length);
        yield return WaitIdle();
        M.DesyncFrame = -1;
        int last = Mathf.Min(M.Movie.Length, M.Timeline.Count - 1);
        if(M.DeathFrame > 0)
        {
            last = Mathf.Min(last, M.DeathFrame - 1);
        }
        TasFrameRecord[] real = new TasFrameRecord[last + 1];
        for(int f = 0; f <= last; f++)
        {
            real[f] = M.Timeline[f];
        }
        const int ahead = 50;
        bool bestSetting = TasPredict.FullPrime;
        int bestScore = -1, bestStarts = 0, bestWithin = 0;
        foreach(bool fullPrime in new[] { true, false })
        {
            TasPredict.FullPrime = fullPrime;
            M.ReplayFromStart(5);
            yield return WaitIdle();
            int starts = 0, exact = 0, within1cm = 0, within10cm = 0, shown = 0, disturbed = 0, sumGood = 0;
            float worst = 0f, ms = 0f;
            for(int k = 5; k + ahead <= last; k += 23)
            {
                if(k > M.Frame)
                {
                    // Less than the rewind lead-in, so this just plays forward (no savestate loading)
                    M.Seek(k);
                    yield return WaitIdle();
                }
                if(M.Frame != k)
                {
                    continue;
                }
                ulong before = M.Registry.Capture(k).Fingerprint;
                float t1 = Time.realtimeSinceStartup;
                TasPrediction p = pr.PredictPath(ahead);
                ms += (Time.realtimeSinceStartup - t1) * 1000f;
                if(M.Registry.Capture(k).Fingerprint != before)
                {
                    disturbed++;
                }
                if(p == null)
                {
                    continue;
                }
                starts++;
                float maxErr = 0f;
                int good = 0, firstMiss = -1;
                for(int j = 1; j < p.Pos.Count && k + j <= last; j++)
                {
                    float e = Vector2.Distance(p.Pos[j], real[k + j].Pos);
                    maxErr = Mathf.Max(maxErr, e);
                    if(e >= 0.001f && firstMiss < 0)
                    {
                        firstMiss = j;
                    }
                    if(e < 0.01f && good == j - 1)
                    {
                        good = j;
                    }
                }
                if(maxErr < 0.001f) exact++;
                if(maxErr < 0.01f) within1cm++;
                if(maxErr < 0.1f) within10cm++;
                worst = Mathf.Max(worst, maxErr);
                sumGood += good;
                if(firstMiss > 0 && shown < 5)
                {
                    shown++;
                    int f = k + firstMiss;
                    StringBuilder d = new StringBuilder();
                    d.Append("  from frame " + k + ", first off at +" + firstMiss + " (frame " + f + "): predicted pos " + V(p.Pos[firstMiss]) + " vel " + V(p.Vel[firstMiss]) + " ground " + p.Ground[firstMiss]
                        + " / real pos " + V(real[f].Pos) + " vel " + V(real[f].Vel) + " ground " + real[f].OnGround
                        + "; inputs " + TasMovie.ButtonsToShortText(M.Movie.Get(f - 1)) + " > " + TasMovie.ButtonsToShortText(M.Movie.Get(f))
                        + (p.Hits.Count > 0 ? ", predicted hits at +" + string.Join(",", p.Hits) : "") + (p.JumpAt > 0 ? ", jump at +" + p.JumpAt : ""));
                    if(firstMiss > 1)
                    {
                        d.Append("\n    frame before: predicted vel " + V(p.Vel[firstMiss - 1]) + " ground " + p.Ground[firstMiss - 1] + " / real vel " + V(real[f - 1].Vel) + " ground " + real[f - 1].OnGround);
                    }
                    Log(d.ToString());
                    if(firstMiss <= 2 && fullPrime)
                    {
                        // Show what the stand-in and the enemies did, next to what the real ones did
                        Log("  real on frame " + k + ":\n" + pr.DescribeRealFoes());
                        pr.Trace = true;
                        pr.PredictPath(ahead);
                        pr.Trace = false;
                        Log("  predicted:\n" + pr.TraceLog);
                        for(int t = 1; t <= 2; t++)
                        {
                            M.Seek(k + t);
                            yield return WaitIdle();
                            Log("  real on frame " + (k + t) + ":\n" + pr.DescribeRealFoes());
                        }
                    }
                }
            }
            Log("Prediction lines (" + (fullPrime ? "normal warm-up step" : "tiny warm-up step") + "): " + exact + " of " + starts + " starts match the real route for " + ahead + " frames within 0.001, "
                + within1cm + " within 0.01, " + within10cm + " within 0.1, worst " + worst.ToString("0.000")
                + ", on average " + (sumGood / (float)Mathf.Max(1, starts)).ToString("0.0") + " frames before drifting 0.01, " + (ms / Mathf.Max(1, starts)).ToString("0.00") + " ms per line");
            Check(disturbed == 0, "Working out the prediction lines leaves the game untouched (" + disturbed + " changed)");
            int score = exact * 1000 + within1cm;
            if(score > bestScore)
            {
                bestScore = score;
                bestSetting = fullPrime;
                bestStarts = starts;
                bestWithin = within1cm;
            }
        }
        TasPredict.FullPrime = bestSetting;
        Log("Prediction lines use: " + (bestSetting ? "normal warm-up step" : "tiny warm-up step"));
        Check(M.DesyncFrame < 0, "The run still replays exactly while prediction lines are being worked out (desync " + M.DesyncFrame + ")");
        Check(bestStarts > 0 && bestWithin >= bestStarts * 0.8f, "The green prediction line stays within 0.01 of the real route for " + ahead + " frames (" + bestWithin + " of " + bestStarts + " starts)");

        // All the lines at once, from somewhere in the middle of the run
        M.Seek(Mathf.Min(last, 300));
        yield return WaitIdle();
        pr.ComputeNow();
        Log("All prediction lines from frame " + M.Frame + ": " + pr.Lines.Count + " lines (" + LineNames(pr) + ") in " + pr.LastMs.ToString("0.0") + " ms" + (pr.Problem.Length > 0 ? ", problem: " + pr.Problem : ""));
        Check(pr.Problem.Length == 0 && pr.Lines.Count > 0, "All the prediction lines can be worked out");
    }

    private static string V(Vector2 v)
    {
        return "(" + v.x.ToString("0.0000") + ", " + v.y.ToString("0.0000") + ")";
    }

    private static string LineNames(TasPredict pr)
    {
        List<string> names = new List<string>();
        foreach(TasPrediction p in pr.Lines)
        {
            names.Add(p.Name);
        }
        return string.Join(", ", names);
    }

    // When did the game's speedrun timer start, and what did it say at a frame? (for testing)
    private string TimerProbe(int at)
    {
        int start = -1;
        for(int f = 0; f <= M.Frame && f < 400; f++)
        {
            TasSnapshot sn = M.GetSnapshot(f);
            if(sn != null && M.Registry.Values(sn, "TimerManager.timerOn").Contains("timerOn=1"))
            {
                start = f;
                break;
            }
        }
        TasSnapshot a = M.GetSnapshot(at);
        return "timer starts on frame " + start + ", at frame " + at + ": " + (a != null ? M.Registry.Values(a, "TimerManager.currentTime") : "-") + " desync " + M.DesyncFrame + ", first inputs " + TasMovie.ButtonsToShortText(M.Movie.Get(1)) + " / " + TasMovie.ButtonsToShortText(M.Movie.Get(2));
    }

    private ulong[] Fingerprints(int last)
    {
        ulong[] a = new ulong[last + 1];
        for(int f = 0; f <= last && f < M.Timeline.Count; f++)
        {
            a[f] = M.Timeline[f].Fingerprint;
        }
        return a;
    }

    // A made-up but busy movie: running both ways, jumps of different heights, attacks in all directions.
    public static TasMovie MakeMovie(int frames, int seed)
    {
        System.Random rng = new System.Random(seed);
        TasMovie m = new TasMovie();
        int f = 1;
        while(f <= frames)
        {
            int len = rng.Next(3, 30);
            Buttons dir;
            int d = rng.Next(10);
            dir = d < 6 ? Buttons.Right : d < 8 ? Buttons.Left : Buttons.None;
            bool jump = rng.Next(3) == 0;
            int jumpLen = rng.Next(1, len + 1);
            int attackAt = rng.Next(4) == 0 ? rng.Next(len) : -1;
            Buttons aim = rng.Next(3) == 0 ? Buttons.Down : rng.Next(2) == 0 ? Buttons.Up : Buttons.None;
            for(int i = 0; i < len && f <= frames; i++, f++)
            {
                Buttons b = dir;
                if(jump && i < jumpLen) b |= Buttons.Jump;
                if(i == attackAt) b |= Buttons.Attack | aim;
                m.Set(f, b);
            }
        }
        return m;
    }
}
