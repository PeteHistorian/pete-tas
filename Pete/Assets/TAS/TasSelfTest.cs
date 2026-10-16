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
        foreach(float primeStep in new[] { 0.00001f, 0f })
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
