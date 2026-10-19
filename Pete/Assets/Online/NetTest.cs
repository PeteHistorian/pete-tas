using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using Buttons = PeteInput.Buttons;

// Automatic test of the online mode: one copy of the game hosts, another joins, and they check each other.
//   "Pete Online.exe" -netTest host -netPort 7811
//   "Pete Online.exe" -netTest join -netAddress 127.0.0.1:7811
// Each writes its results to net-test-host.txt / net-test-client.txt next to the exe, then quits.
[DefaultExecutionOrder(-32000)]
public class NetTest : MonoBehaviour
{
    public static NetTest I;
    public bool Headless;

    private bool isHost;
    private readonly StringBuilder report = new StringBuilder();
    private bool failed;
    private Buttons lastHeld;

    // Messages from the other side: the step it finished and what it saw
    private readonly Queue<string[]> inbox = new Queue<string[]>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void MaybeStart()
    {
        string role = Arg("-netTest");
        if(role == null || I != null)
        {
            return;
        }
        NetGame.EnsureRunning();
        GameObject go = new GameObject("[Online test]");
        DontDestroyOnLoad(go);
        NetTest t = go.AddComponent<NetTest>();
        t.isHost = role == "host";
        t.Headless = Application.isBatchMode;
    }

    private static string Arg(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for(int i = 0; i < args.Length - 1; i++)
        {
            if(string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }
        return null;
    }

    private void Awake()
    {
        I = this;
        Application.targetFrameRate = 60;
    }

    private void Update()
    {
        PeteInput.ScriptPrevious = lastHeld;
        lastHeld = PeteInput.ScriptHeld;
    }

    private IEnumerator Start()
    {
        yield return null;
        NetGame g = NetGame.I;
        string port = Arg("-netPort") ?? "7811";
        g.Port = ushort.Parse(port);
        Log("Pete Online test (" + (isHost ? "host" : "joining player") + ")  " + DateTime.Now);
        if(isHost)
        {
            yield return HostSide(g);
        }
        else
        {
            yield return ClientSide(g);
        }
        Log("");
        Log(failed ? "SOME CHECKS FAILED" : "ALL CHECKS PASSED");
        try
        {
            string dir = Path.GetDirectoryName(Application.dataPath);
            File.WriteAllText(Path.Combine(dir, isHost ? "net-test-host.txt" : "net-test-client.txt"), report.ToString());
        }
        catch(Exception ex)
        {
            Debug.LogError(ex);
        }
        yield return new WaitForSecondsRealtime(1f);
        Application.Quit(failed ? 1 : 0);
    }

    // "-netShots <folder>": screenshots of what each player sees (only when the game has a window)
    private IEnumerator Shot(string name)
    {
        string dir = Arg("-netShots");
        if(dir == null || Headless)
        {
            yield break;
        }
        Directory.CreateDirectory(dir);
        yield return null;
        yield return new WaitForEndOfFrame();
        ScreenCapture.CaptureScreenshot(Path.Combine(dir, (isHost ? "host-" : "friend-") + name + ".png"));
        yield return null;
        yield return null;
    }

    private void Log(string line)
    {
        report.AppendLine(line);
        Debug.Log("[NET TEST] " + line);
    }

    private void Check(bool ok, string what)
    {
        Log((ok ? "PASS  " : "FAIL  ") + what);
        if(!ok) failed = true;
    }

    private void Tell(params string[] parts)
    {
        NetWriter w = new NetWriter().Byte(parts.Length);
        foreach(string p in parts) w.Str(p);
        NetGame.I.SendTest(w);
    }

    public void OnHostMessage(NetGame.Player from, NetReader r)
    {
        Read(r);
    }

    public void OnClientMessage(NetReader r)
    {
        Read(r);
    }

    private void Read(NetReader r)
    {
        int n = r.Byte();
        string[] parts = new string[n];
        for(int i = 0; i < n; i++) parts[i] = r.Str();
        inbox.Enqueue(parts);
    }

    // Waits for the other side to say this (up to "seconds")
    private IEnumerator Await(string what, float seconds, Action<string[]> got)
    {
        float until = Time.realtimeSinceStartup + seconds;
        while(Time.realtimeSinceStartup < until)
        {
            while(inbox.Count > 0)
            {
                string[] m = inbox.Dequeue();
                if(m.Length > 0 && m[0] == what)
                {
                    got(m);
                    yield break;
                }
            }
            yield return null;
        }
        got(null);
    }

    private static float F(string s)
    {
        return float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string S(float f)
    {
        return f.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IEnumerator WaitUntil(Func<bool> condition, float seconds)
    {
        float until = Time.realtimeSinceStartup + seconds;
        while(!condition() && Time.realtimeSinceStartup < until)
        {
            yield return null;
        }
    }

    // ---------- The host ----------

    private IEnumerator HostSide(NetGame g)
    {
        g.LocalName = "Host";
        g.Host();
        Check(g.IsHost, "Hosting started on port " + g.Port);
        yield return WaitUntil(() => g.Players.Count >= 2, 60f);
        Check(g.Players.Count >= 2, "A friend joined (" + g.Players.Count + " players)");
        if(g.Players.Count < 2) yield break;
        int friend = -1;
        foreach(NetGame.Player p in g.Players.Values) if(p.Id != g.LocalId) friend = p.Id;
        string friendName = g.Players[friend].Name;
        Log("The friend is called \"" + friendName + "\"");
        yield return new WaitForSecondsRealtime(0.5f);
        yield return Shot("1-title");
        Tell("shot", "1-title");
        yield return new WaitForSecondsRealtime(1f);

        g.StartRun();
        yield return WaitUntil(() => g.InWorld && g.Players[friend].Scene == NetGame.WorldScene, 60f);
        Check(g.InWorld && g.Players[friend].Scene == NetGame.WorldScene, "The run started for both (both are in the level)");
        yield return new WaitForSecondsRealtime(1.5f);
        Check(g.PuppetOf(friend) != null, "The friend's Pete is shown here");

        // 1. The friend runs right for a moment and stops: their Pete here ends up where they are
        Tell("move");
        string[] done = null;
        yield return Await("moved", 20f, m => done = m);
        yield return new WaitForSecondsRealtime(0.5f);
        yield return Shot("2-level");
        Tell("shot", "2-level");
        yield return new WaitForSecondsRealtime(1f);
        if(done != null)
        {
            Vector2 theirs = new Vector2(F(done[1]), F(done[2]));
            Vector2 start = new Vector2(F(done[3]), F(done[4]));
            NetPuppet pup = g.PuppetOf(friend);
            Vector2 shown = pup != null ? (Vector2)pup.transform.position : new Vector2(-9999, -9999);
            Check((theirs - start).magnitude > 1f && (shown - theirs).magnitude < 0.05f, "When the friend runs and stops, their Pete here stops in the same spot (moved " + (theirs - start).magnitude.ToString("0.00") + ", off by " + (shown - theirs).magnitude.ToString("0.000") + ")");
        }
        else
        {
            Check(false, "The friend ran and reported back");
        }

        // 2. The enemies are where the host has them
        Tell("enemies");
        yield return Await("enemies", 10f, m => done = m);
        if(done != null)
        {
            List<Vector2> mine = g.TestEnemyPositions();
            int count = (done.Length - 1) / 2;
            float worst = 0f;
            for(int i = 0; i < count && i < mine.Count; i++)
            {
                worst = Mathf.Max(worst, (new Vector2(F(done[1 + i * 2]), F(done[2 + i * 2])) - mine[i]).magnitude);
            }
            Check(count == mine.Count && count > 0 && worst < 1f, "The friend sees all " + count + " enemies where the host has them (worst " + worst.ToString("0.00") + " units apart, they keep moving)");
        }
        else
        {
            Check(false, "The friend reported the enemies");
        }

        // 3. The friend's sword hits an enemy: the host's game applies it, and the friend sees it defeated
        int target = g.TestFirstEnemy();
        Tell("hit", target.ToString());
        yield return WaitUntil(() => !g.TestEnemyAlive(target), 10f);
        Check(target >= 0 && !g.TestEnemyAlive(target), "The friend's sword hits defeat enemy " + target + " in the host's game");
        yield return Await("hit", 10f, m => done = m);
        Check(done != null && done[1] == "dead", "...and it's defeated in the friend's game too");

        // 4. The friend grabs an orb: it counts for the team
        Tell("orb");
        yield return WaitUntil(() => g.TeamPowers >= 1, 10f);
        Check(g.TeamPowers == 1, "The friend's orb counts for the team here (" + g.TeamPowers + " of 2)");
        yield return Await("orb", 10f, m => done = m);
        Check(done != null && done[1] == "1", "...and the friend's game says the team has 1 orb");

        // 5. The friend dies (or presses R): only they start again, nobody else's level reloads
        Tell("respawn");
        yield return Await("respawned", 10f, m => done = m);
        Check(done != null && done[1] == "1" && g.InWorld, "When the friend respawns, only their Pete moves back (the host is still in the run)");

        // 6. The host grabs the other orb, the friend reaches the finish: the run ends for everyone with the same time
        g.TestHostCollectOrb(1);
        yield return new WaitForSecondsRealtime(0.5f);
        Tell("finish");
        yield return WaitUntil(() => SceneManager.GetActiveScene().buildIndex == NetGame.EndScene, 10f);
        Check(SceneManager.GetActiveScene().buildIndex == NetGame.EndScene, "When the friend reaches the finish with both orbs, the host's game ends the run too");
        yield return Await("finished", 10f, m => done = m);
        Check(done != null && done[1] == NetGame.FormatTime(g.lastRunTime), "...and both show the same team time (" + NetGame.FormatTime(g.lastRunTime) + (done != null ? " / " + done[1] : "") + ")");
        yield return new WaitForSecondsRealtime(0.5f);
        yield return Shot("3-end");
        Tell("shot", "3-end");
        yield return new WaitForSecondsRealtime(1f);
        Tell("bye");
        yield return new WaitForSecondsRealtime(1f);
        g.Leave(false);
    }

    // ---------- The joining player ----------

    private IEnumerator ClientSide(NetGame g)
    {
        g.LocalName = "Friend";
        g.Join(Arg("-netAddress") ?? "127.0.0.1:7811");
        yield return WaitUntil(() => g.LocalId >= 0, 30f);
        Check(g.LocalId >= 0, "Joined the host (player number " + g.LocalId + ")");
        if(g.LocalId < 0) yield break;
        yield return WaitUntil(() => g.InWorld, 60f);
        Check(g.InWorld, "The host started the run and this game loaded the level");
        while(true)
        {
            while(inbox.Count == 0)
            {
                if(!g.IsOnline)
                {
                    Log("The host is gone.");
                    yield break;
                }
                yield return null;
            }
            string[] m = inbox.Dequeue();
            switch(m[0])
            {
                case "move":
                {
                    Vector2 start = g.TestPetePos();
                    PeteInput.Scripted = true;
                    PeteInput.ScriptHeld = Buttons.Right;
                    yield return new WaitForSecondsRealtime(1.2f);
                    PeteInput.ScriptHeld = Buttons.None;
                    yield return new WaitForSecondsRealtime(1.5f);
                    PeteInput.Scripted = false;
                    Vector2 end = g.TestPetePos();
                    Tell("moved", S(end.x), S(end.y), S(start.x), S(start.y));
                    break;
                }
                case "enemies":
                {
                    List<string> parts = new List<string> { "enemies" };
                    foreach(Vector2 p in g.TestEnemyPositions())
                    {
                        parts.Add(S(p.x));
                        parts.Add(S(p.y));
                    }
                    Tell(parts.ToArray());
                    break;
                }
                case "hit":
                {
                    int target = int.Parse(m[1]);
                    for(int tries = 0; tries < 15 && g.TestEnemyAlive(target); tries++)
                    {
                        g.TestHitEnemy(target);
                        yield return new WaitForSecondsRealtime(0.35f);
                    }
                    yield return new WaitForSecondsRealtime(0.5f);
                    Tell("hit", g.TestEnemyAlive(target) ? "alive" : "dead");
                    break;
                }
                case "orb":
                {
                    g.TestTouchOrb(0);
                    yield return WaitUntil(() => g.TeamPowers >= 1, 10f);
                    Tell("orb", g.TeamPowers.ToString());
                    break;
                }
                case "respawn":
                {
                    g.TestPeteTo(g.TestPetePos() + new Vector2(0f, 3f));
                    yield return new WaitForSecondsRealtime(0.3f);
                    LevelReset.RESETLEVEL();
                    yield return new WaitForSecondsRealtime(0.5f);
                    bool ok = g.InWorld && (g.TestPetePos() - g.TestRespawnPoint()).magnitude < 0.5f;
                    Tell("respawned", ok ? "1" : "0");
                    break;
                }
                case "finish":
                {
                    g.TestReachFinish();
                    yield return WaitUntil(() => SceneManager.GetActiveScene().buildIndex == NetGame.EndScene, 10f);
                    Check(SceneManager.GetActiveScene().buildIndex == NetGame.EndScene, "Reaching the finish ended the run (the end screen is showing)");
                    Tell("finished", NetGame.FormatTime(g.lastRunTime));
                    break;
                }
                case "shot":
                    yield return Shot(m[1]);
                    break;
                case "bye":
                    Check(true, "The host finished the test");
                    yield break;
            }
        }
    }
}
