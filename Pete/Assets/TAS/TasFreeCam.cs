using UnityEngine;
using Cinemachine;

// Zoom-out route view: takes the camera away from the game so you can look around the whole level.
// Also the "center camera" option, which keeps Pete in the middle of the screen.
[DefaultExecutionOrder(32000)]
public class TasFreeCam : MonoBehaviour
{
    public static TasFreeCam I;
    public bool Active;
    public bool CenterOnPete;
    private float centerSize = -1f;

    private Vector3 camPos;
    private float camSize = 12f;
    private Vector3 lastMouse;

    private void Awake()
    {
        I = this;
    }

    public void Toggle()
    {
        Camera cam = Camera.main;
        if(cam == null)
        {
            return;
        }
        Active = !Active;
        if(Active)
        {
            camPos = cam.transform.position;
            camSize = Mathf.Max(cam.orthographicSize * 2f, 8f);
        }
        else
        {
            SetBrain(cam, true);
            if(!CenterOnPete)
            {
                TasCamState.ShowGameCamera();
            }
        }
    }

    public void ToggleCenter()
    {
        CenterOnPete = !CenterOnPete;
        Camera cam = Camera.main;
        if(CenterOnPete)
        {
            if(cam != null && centerSize <= 0f)
            {
                centerSize = cam.orthographicSize;
            }
            if(TasManager.I != null) TasManager.I.Say("Center camera ON: the camera follows Pete. Scroll the mouse wheel to zoom. C to turn it off.");
        }
        else
        {
            if(cam != null && !Active)
            {
                SetBrain(cam, true);
                TasCamState.ShowGameCamera();
            }
            if(TasManager.I != null) TasManager.I.Say("Center camera OFF: back to the game's own room cameras.");
        }
    }

    // Keeps Pete in the middle of the part of the screen the panels don't cover.
    private void CenterCamera(Camera cam)
    {
        TasManager m = TasManager.I;
        if(m == null || m.Player == null || !m.Player.Valid)
        {
            return;
        }
        SetBrain(cam, false);
        if(centerSize <= 0f)
        {
            centerSize = cam.orthographicSize;
        }
        TasHud hud = TasHud.I;
        bool overUi = hud != null && hud.PointerOverUi(Input.mousePosition);
        float wheel = Input.mouseScrollDelta.y;
        if(!overUi && Mathf.Abs(wheel) > 0.01f)
        {
            centerSize = Mathf.Clamp(centerSize * Mathf.Pow(0.88f, wheel), 2f, 80f);
        }
        cam.orthographicSize = centerSize;
        Rect free = hud != null ? hud.OpenArea() : new Rect(0, 0, Screen.width, Screen.height);
        float unitsPerPixel = centerSize * 2f / Mathf.Max(1, cam.pixelHeight);
        Vector2 screenCentre = new Vector2(cam.pixelWidth * 0.5f, cam.pixelHeight * 0.5f);
        Vector2 freeCentre = new Vector2(free.center.x, cam.pixelHeight - free.center.y);
        Vector2 offset = (screenCentre - freeCentre) * unitsPerPixel;
        Vector2 p = m.Player.Go.transform.position;
        cam.transform.position = new Vector3(p.x + offset.x, p.y + offset.y, cam.transform.position.z);
    }

    private Camera darkenedCam;
    private Color savedBackground;

    // The game's own camera keeps running underneath (so it's exactly where the game would have it when you switch
    // back); this camera just moves the picture after it. While the TAS moves the camera, the empty space outside
    // the level is shown dark instead of the game's bright background colour.
    private void SetBrain(Camera cam, bool on)
    {
        CinemachineBrain brain = cam.GetComponent<CinemachineBrain>();
        if(brain != null && !brain.enabled)
        {
            brain.enabled = true;
        }
        if(!on && darkenedCam != cam)
        {
            darkenedCam = cam;
            savedBackground = cam.backgroundColor;
            cam.backgroundColor = new Color(0.03f, 0.04f, 0.035f);
        }
        else if(on && darkenedCam == cam)
        {
            cam.backgroundColor = savedBackground;
            darkenedCam = null;
        }
    }

    // Zooms out to show the whole route.
    public void FitRoute()
    {
        TasManager m = TasManager.I;
        if(m == null || m.RoutePositions.Count == 0)
        {
            return;
        }
        Vector2 min = m.RoutePositions[0], max = m.RoutePositions[0];
        foreach(Vector2 p in m.RoutePositions)
        {
            min = Vector2.Min(min, p);
            max = Vector2.Max(max, p);
        }
        Camera cam = Camera.main;
        if(cam == null)
        {
            return;
        }
        // Fit the route into the part of the screen the panels don't cover
        Rect free = TasHud.I != null ? TasHud.I.OpenArea() : new Rect(0, 0, Screen.width, Screen.height);
        float pxW = Mathf.Max(100f, free.width), pxH = Mathf.Max(100f, free.height);
        float routeW = (max.x - min.x) + 4f, routeH = (max.y - min.y) + 4f;
        float unitsPerPixel = Mathf.Max(routeW / pxW, routeH / pxH);
        camSize = Mathf.Max(unitsPerPixel * cam.pixelHeight * 0.5f, 6f);
        unitsPerPixel = camSize * 2f / cam.pixelHeight;
        // Shift the camera so the route's centre lands in the middle of the open area
        Vector2 routeCentre = (min + max) * 0.5f;
        Vector2 screenCentre = new Vector2(cam.pixelWidth * 0.5f, cam.pixelHeight * 0.5f);
        Vector2 freeCentre = new Vector2(free.center.x, cam.pixelHeight - free.center.y);
        Vector2 offset = (screenCentre - freeCentre) * unitsPerPixel;
        camPos = new Vector3(routeCentre.x + offset.x, routeCentre.y + offset.y, camPos.z);
    }

    private void LateUpdate()
    {
        Camera cam = Camera.main;
        if(cam == null)
        {
            return;
        }
        if(!Active)
        {
            if(CenterOnPete)
            {
                CenterCamera(cam);
            }
            return;
        }
        // Keep the game's camera system switched off (it comes back after a level reload)
        SetBrain(cam, false);
        TasHud hud = TasHud.I;
        Vector3 mouse = Input.mousePosition;
        bool overUi = hud != null && hud.PointerOverUi(mouse);
        float upp = camSize * 2f / Mathf.Max(1, cam.pixelHeight);

        float wheel = Input.mouseScrollDelta.y;
        if(!overUi && Mathf.Abs(wheel) > 0.01f)
        {
            // Zoom towards the mouse
            Vector3 before = cam.ScreenToWorldPoint(mouse);
            camSize = Mathf.Clamp(camSize * Mathf.Pow(0.88f, wheel), 2f, 400f);
            cam.orthographicSize = camSize;
            cam.transform.position = camPos;
            Vector3 after = cam.ScreenToWorldPoint(mouse);
            camPos += before - after;
        }
        if(Input.GetMouseButton(1) || Input.GetMouseButton(2))
        {
            Vector3 delta = mouse - lastMouse;
            camPos -= new Vector3(delta.x * upp, delta.y * upp, 0f);
        }
        lastMouse = mouse;

        cam.orthographicSize = camSize;
        cam.transform.position = new Vector3(camPos.x, camPos.y, cam.transform.position.z);

        // Click the route to jump to that frame
        TasManager m = TasManager.I;
        if(m != null && !m.Busy && !overUi && Input.GetMouseButtonDown(0) && (hud == null || !hud.PlacingLine))
        {
            Vector3 w = cam.ScreenToWorldPoint(mouse);
            Vector2 click = new Vector2(w.x, w.y);
            int best = -1;
            float bestDist = 14f * upp;
            for(int f = 0; f < m.RoutePositions.Count; f++)
            {
                float d = Vector2.Distance(m.RoutePositions[f], click);
                if(d < bestDist)
                {
                    bestDist = d;
                    best = f;
                }
            }
            if(best >= 0)
            {
                m.Seek(best);
            }
        }
    }
}
