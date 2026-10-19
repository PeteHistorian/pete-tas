using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

// What the online mode shows: the "play with friends" panel on the title screen, names over the other players,
// a small player list during the run, and the team's time at the end.
public partial class NetGame
{
    private GUIStyle box, label, small, header, button, field, msgStyle, tagStyle;
    private Texture2D white;
    private bool stylesMade;
    private string joinText;
    private string publicIp = "";
    private bool fetchingIp;
    private float uiScale = 1f;

    private void MakeStyles()
    {
        if(stylesMade && white != null)
        {
            return;
        }
        white = new Texture2D(1, 1);
        white.hideFlags = HideFlags.HideAndDontSave;
        white.SetPixel(0, 0, Color.white);
        white.Apply();
        Texture2D dark = new Texture2D(1, 1);
        dark.hideFlags = HideFlags.HideAndDontSave;
        dark.SetPixel(0, 0, new Color(0.05f, 0.06f, 0.08f, 0.88f));
        dark.Apply();
        box = new GUIStyle(GUI.skin.box);
        box.normal.background = dark;
        label = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true, wordWrap = true };
        label.normal.textColor = new Color(0.92f, 0.94f, 0.97f);
        small = new GUIStyle(label) { fontSize = 12 };
        small.normal.textColor = new Color(0.72f, 0.76f, 0.82f);
        header = new GUIStyle(label) { fontSize = 15, fontStyle = FontStyle.Bold };
        header.normal.textColor = new Color(1f, 0.82f, 0.35f);
        button = new GUIStyle(GUI.skin.button) { fontSize = 14 };
        field = new GUIStyle(GUI.skin.textField) { fontSize = 14 };
        msgStyle = new GUIStyle(label) { alignment = TextAnchor.MiddleCenter, fontSize = 15 };
        tagStyle = new GUIStyle(label) { alignment = TextAnchor.MiddleCenter, fontSize = 13, fontStyle = FontStyle.Bold, wordWrap = false };
        stylesMade = true;
    }

    private void OnGUI()
    {
        if(NetTest.I != null && NetTest.I.Headless)
        {
            return;
        }
        MakeStyles();
        uiScale = Mathf.Clamp(Screen.height / 900f, 0.7f, 2.5f);
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(uiScale, uiScale, 1f));
        float vw = Screen.width / uiScale, vh = Screen.height / uiScale;
        int scene = SceneManager.GetActiveScene().buildIndex;
        if(scene == TitleScene)
        {
            TitlePanel(vw, vh);
        }
        else if(scene == WorldScene && IsOnline)
        {
            NameTags();
            WorldPanel(vw);
        }
        else if(scene == EndScene && IsOnline)
        {
            EndPanel(vw, vh);
        }
        if(Message.Length > 0 && Time.unscaledTime < MessageUntil)
        {
            GUIContent c = new GUIContent(Message);
            float w = Mathf.Min(vw - 40f, msgStyle.CalcSize(c).x + 40f);
            Rect r = new Rect((vw - w) * 0.5f, 12f, w, 34f);
            GUI.Box(r, "", box);
            GUI.Label(r, c, msgStyle);
        }
    }

    private void TitlePanel(float vw, float vh)
    {
        float w = 380f, h = IsOnline ? 380f : 270f;
        // Bottom-left, over the tree trunk, so the story text on the right stays readable
        Rect r = new Rect(16f, vh - h - 34f, w, h);
        GUI.Box(r, "", box);
        GUILayout.BeginArea(new Rect(r.x + 14f, r.y + 10f, r.width - 28f, r.height - 20f));
        GUILayout.Label("PLAY WITH FRIENDS (online co-op)", header);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Your name", small, GUILayout.Width(80));
        string name = GUILayout.TextField(LocalName, 16, field);
        if(name != LocalName && !IsOnline)
        {
            LocalName = name;
        }
        GUILayout.EndHorizontal();
        GUILayout.Space(6);
        if(!IsOnline)
        {
            if(GUILayout.Button("Host a game", button, GUILayout.Height(30)))
            {
                LocalName = Clean(LocalName);
                Host();
            }
            GUILayout.Space(8);
            GUILayout.Label("Join a friend's game: type their address", small);
            if(joinText == null) joinText = HostAddress;
            GUILayout.BeginHorizontal();
            joinText = GUILayout.TextField(joinText, 64, field);
            if(GUILayout.Button("Join", button, GUILayout.Width(70)))
            {
                LocalName = Clean(LocalName);
                Join(joinText);
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("The host sees their address in this panel after pressing Host. Or press Play for a normal game on your own.", small);
        }
        else
        {
            if(IsHost)
            {
                GUILayout.Label("You're hosting (port " + Port + "). Friends join with your address:", label);
                foreach(string ip in LocalAddresses())
                {
                    GUILayout.Label("   <b>" + ip + "</b>  <color=#888888>(same Wi-Fi or a VPN like Radmin)</color>", small);
                }
                GUILayout.BeginHorizontal();
                if(publicIp.Length > 0)
                {
                    GUILayout.Label("   <b>" + publicIp + "</b>  <color=#888888>(over the internet)</color>", small);
                }
                else if(GUILayout.Button(fetchingIp ? "Finding it..." : "Show my internet address", button))
                {
                    if(!fetchingIp) StartCoroutine(FetchPublicIp());
                }
                GUILayout.EndHorizontal();
                GUILayout.Label("Over the internet, UDP port " + Port + " has to be forwarded to this PC on your router (or use a tunnel like playit.gg).", small);
            }
            else
            {
                GUILayout.Label(LocalId >= 0 ? "Connected to the host. Waiting for them to start the run..." : "Connecting...", label);
            }
            GUILayout.Space(4);
            GUILayout.Label("PLAYERS (" + Players.Count + ")", header);
            foreach(Player p in Players.Values)
            {
                Color tint = Tints[Mathf.Abs(p.Id) % Tints.Length];
                GUILayout.Label("<color=#" + ColorUtility.ToHtmlStringRGB(tint) + ">●</color> " + p.Name + (p.Id == 0 ? "  <color=#888888>(host)</color>" : "") + (p.Id == LocalId ? "  <color=#888888>(you)</color>" : "  <color=#888888>" + p.Ping + " ms</color>"), small);
            }
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            if(IsHost && GUILayout.Button("Start the run", button, GUILayout.Height(30)))
            {
                StartRun();
            }
            if(GUILayout.Button(IsHost ? "Stop hosting" : "Leave", button, GUILayout.Height(30), GUILayout.Width(120)))
            {
                Leave(true);
                Say(IsHost ? "Stopped hosting." : "Left the game.");
            }
            GUILayout.EndHorizontal();
        }
        GUILayout.EndArea();
    }

    private void WorldPanel(float vw)
    {
        float w = 230f, h = 44f + Players.Count * 20f;
        Rect r = new Rect(vw - w - 10f, 60f, w, h);
        GUI.Box(r, "", box);
        GUILayout.BeginArea(new Rect(r.x + 10f, r.y + 6f, r.width - 20f, r.height - 10f));
        GUILayout.Label("Team orbs: " + teamPowers + " / 2", small);
        foreach(Player p in Players.Values)
        {
            Color tint = Tints[Mathf.Abs(p.Id) % Tints.Length];
            int hp = p.Id == LocalId ? (peteHealth != null ? peteHealth.HP : 0) : p.State.Hp;
            GUILayout.Label("<color=#" + ColorUtility.ToHtmlStringRGB(tint) + ">●</color> " + p.Name + "  <color=#ff8080>♥" + hp + "</color>" + (p.Id == LocalId ? "" : "  <color=#888888>" + p.Ping + " ms</color>"), small);
        }
        GUILayout.EndArea();
    }

    private void NameTags()
    {
        Camera cam = Camera.main;
        if(cam == null)
        {
            return;
        }
        foreach(Player p in Players.Values)
        {
            if(p.Puppet == null) continue;
            Vector3 sp = cam.WorldToScreenPoint(p.Puppet.transform.position + new Vector3(0f, 1.1f, 0f));
            if(sp.z < 0f) continue;
            Vector2 at = new Vector2(sp.x / uiScale, (Screen.height - sp.y) / uiScale);
            GUIContent c = new GUIContent(p.Name + "  ♥" + p.State.Hp);
            Vector2 size = tagStyle.CalcSize(c);
            Rect r = new Rect(at.x - size.x * 0.5f - 4f, at.y - size.y, size.x + 8f, size.y);
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(r, white);
            GUI.color = old;
            tagStyle.normal.textColor = Tints[Mathf.Abs(p.Id) % Tints.Length];
            GUI.Label(r, c, tagStyle);
        }
    }

    private void EndPanel(float vw, float vh)
    {
        float w = 360f, h = 120f;
        // Under the end screen's own RESTART button, clear of the credits
        Rect r = new Rect(vw * 0.765f - w * 0.5f, vh * 0.7f, w, h);
        GUI.Box(r, "", box);
        GUILayout.BeginArea(new Rect(r.x + 14f, r.y + 10f, r.width - 28f, r.height - 20f));
        GUILayout.Label("TEAM TIME  " + (lastRunTime >= 0f ? FormatTime(lastRunTime) : "--"), header);
        if(IsHost)
        {
            GUILayout.BeginHorizontal();
            if(GUILayout.Button("Play again", button, GUILayout.Height(30))) StartRun();
            if(GUILayout.Button("Back to the title", button, GUILayout.Height(30))) BackToTitle();
            GUILayout.EndHorizontal();
        }
        else
        {
            GUILayout.Label("Waiting for the host to play again...", small);
            if(GUILayout.Button("Leave", button, GUILayout.Width(120))) Leave(true);
        }
        GUILayout.EndArea();
    }

    public static string FormatTime(float t)
    {
        int minutes = (int)t / 60;
        int seconds = (int)t % 60;
        int fraction = (int)(t * 100) % 100;
        return string.Format("{0:00}:{1:00}:{2:00}", minutes, seconds, fraction);
    }

    private static List<string> LocalAddresses()
    {
        List<string> list = new List<string>();
        try
        {
            foreach(IPAddress a in Dns.GetHostAddresses(Dns.GetHostName()))
            {
                if(a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                {
                    list.Add(a.ToString());
                }
            }
        }
        catch
        {
        }
        return list;
    }

    // Asks a public "what's my IP" service, only when the host presses the button
    private IEnumerator FetchPublicIp()
    {
        fetchingIp = true;
        using(UnityWebRequest req = UnityWebRequest.Get("https://api.ipify.org"))
        {
            req.timeout = 8;
            yield return req.SendWebRequest();
            if(req.result == UnityWebRequest.Result.Success)
            {
                publicIp = req.downloadHandler.text.Trim();
            }
            else
            {
                Say("Couldn't find your internet address: " + req.error, 6f);
            }
        }
        fetchingIp = false;
    }
}
