using System.Collections.Generic;
using UnityEngine;

// Draws the route lines, ghost routes, finish lines and see-through ghost Petes in the level.
[DefaultExecutionOrder(31000)]
public class TasDraw : MonoBehaviour
{
    public static bool ShowRoute = true;
    public static bool ShowGhostPetes = true;
    public static bool ShowHitboxes;
    public static bool ShowLevelCollision;

    private Material mat;
    private readonly List<SpriteRenderer> ghostPetes = new List<SpriteRenderer>();

    private void OnEnable()
    {
        Camera.onPostRender += DrawWorld;
    }

    private void OnDisable()
    {
        Camera.onPostRender -= DrawWorld;
    }

    private Material Mat()
    {
        if(mat == null)
        {
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if(shader == null)
            {
                return null;
            }
            mat = new Material(shader);
            mat.hideFlags = HideFlags.HideAndDontSave;
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            mat.SetInt("_ZWrite", 0);
            mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
        }
        return mat;
    }

    private static bool ModeAllowsDrawing(TasManager m)
    {
        if(m == null || m.Player == null || !m.Player.Valid)
        {
            return false;
        }
        switch(m.CurrentMode)
        {
            case TasManager.Mode.Booting:
            case TasManager.Mode.Seeking:
            case TasManager.Mode.Searching:
            case TasManager.Mode.VideoEndScreen:
                return false;
        }
        return true;
    }

    // Route lines, ghosts and finish lines
    private static bool ShouldDraw(TasManager m)
    {
        if(!ModeAllowsDrawing(m))
        {
            return false;
        }
        return m.CurrentMode == TasManager.Mode.Video ? m.VideoShowRoute : ShowRoute;
    }

    private static bool ShouldDrawHitboxes(TasManager m)
    {
        if(!ModeAllowsDrawing(m))
        {
            return false;
        }
        return m.CurrentMode == TasManager.Mode.Video ? m.VideoShowHitboxes : ShowHitboxes;
    }

    // ---------------------------------------------------------------
    //  See-through ghost Petes
    // ---------------------------------------------------------------
    private void LateUpdate()
    {
        TasManager m = TasManager.I;
        int used = 0;
        bool show = ShouldDraw(m) && ShowGhostPetes && m.CurrentMode != TasManager.Mode.Video;
        if(show)
        {
            SpriteRenderer pete = m.Player.Sprite;
            foreach(TasGhost g in m.Ghosts)
            {
                if(!g.Visible || g.Pos.Count == 0)
                {
                    continue;
                }
                int f = Mathf.Clamp(m.Frame, 0, g.Pos.Count - 1);
                SpriteRenderer sr = GhostPete(used++);
                sr.sprite = pete != null ? pete.sprite : null;
                sr.sortingLayerID = pete != null ? pete.sortingLayerID : 0;
                sr.sortingOrder = pete != null ? pete.sortingOrder - 1 : 0;
                Color c = g.Color;
                c.a = 0.45f;
                sr.color = c;
                Vector3 ls = m.Player.Go.transform.lossyScale;
                float vx = f < g.Vel.Count ? g.Vel[f].x : 0f;
                float sx = Mathf.Abs(ls.x);
                if(vx < -0.001f) sx = -sx;
                else if(vx <= 0.001f && ls.x < 0) sx = -sx;
                sr.transform.localScale = new Vector3(sx, Mathf.Abs(ls.y), 1f);
                sr.transform.position = new Vector3(g.Pos[f].x, g.Pos[f].y, m.Player.Go.transform.position.z + 0.01f);
                sr.gameObject.SetActive(true);
            }
        }
        for(int i = used; i < ghostPetes.Count; i++)
        {
            if(ghostPetes[i].gameObject.activeSelf)
            {
                ghostPetes[i].gameObject.SetActive(false);
            }
        }
    }

    private SpriteRenderer GhostPete(int i)
    {
        while(ghostPetes.Count <= i)
        {
            GameObject go = new GameObject("[TAS] Ghost Pete");
            go.transform.SetParent(transform, false);
            ghostPetes.Add(go.AddComponent<SpriteRenderer>());
        }
        return ghostPetes[i];
    }

    // ---------------------------------------------------------------
    //  Lines in the world
    // ---------------------------------------------------------------
    private float upp;  // world units per screen pixel

    private void DrawWorld(Camera cam)
    {
        TasManager m = TasManager.I;
        if(cam != Camera.main || !cam.orthographic)
        {
            return;
        }
        bool routes = ShouldDraw(m);
        bool hitboxes = ShouldDrawHitboxes(m);
        bool predictions = ModeAllowsDrawing(m) && TasPredict.I != null && TasPredict.Active(m) && TasPredict.I.Lines.Count > 0;
        if(!routes && !hitboxes && !predictions)
        {
            return;
        }
        Material mt = Mat();
        if(mt == null)
        {
            return;
        }
        upp = cam.orthographicSize * 2f / Mathf.Max(1, cam.pixelHeight);
        float z = 0f;

        GL.PushMatrix();
        mt.SetPass(0);
        GL.Begin(GL.QUADS);
        if(hitboxes)
        {
            DrawHitboxes(cam, z);
        }
        if(routes)
        {
            DrawRoutes(m, z);
        }
        if(predictions)
        {
            DrawPredictions(TasPredict.I, z);
        }
        GL.End();
        GL.PopMatrix();
    }

    private void DrawRoutes(TasManager m, float z)
    {
        // Finish lines
        foreach(TasLine line in m.Lines)
        {
            Seg(line.A, line.B, 4f, line.Color, z);
            Seg(line.A + Vector2.left * 0.15f, line.A + Vector2.right * 0.15f, 3f, line.Color, z);
            Seg(line.B + Vector2.left * 0.15f, line.B + Vector2.right * 0.15f, 3f, line.Color, z);
        }

        // Ghost routes
        foreach(TasGhost g in m.Ghosts)
        {
            if(!g.Visible)
            {
                continue;
            }
            Color c = g.Color;
            c.a = 0.55f;
            for(int f = 1; f < g.Pos.Count; f++)
            {
                Seg(g.Pos[f - 1], g.Pos[f], 2f, c, z);
            }
            for(int f = 0; f < g.Pos.Count; f += 10)
            {
                Dot(g.Pos[f], f % 50 == 0 ? 6f : 4f, c, z);
            }
        }

        // Current route, coloured by how fast Pete is moving
        float maxSpeed = Mathf.Max(1f, m.Player.MaxSpeed);
        List<TasFrameRecord> t = m.Timeline;
        for(int f = 1; f < t.Count; f++)
        {
            float speed = Mathf.Abs(t[f].Vel.x) / maxSpeed;
            Color c = speed > 1.05f ? new Color(1f, 0.35f, 0.3f) : Color.Lerp(new Color(0.3f, 0.55f, 1f), new Color(1f, 1f, 0.6f), speed);
            if(f > m.Frame)
            {
                c.a = 0.45f;
            }
            Seg(t[f - 1].Pos, t[f].Pos, 3f, c, z);
        }
        for(int f = 0; f < t.Count; f += 10)
        {
            Dot(t[f].Pos, f % 50 == 0 ? 7f : 4.5f, new Color(1f, 1f, 1f, f > m.Frame ? 0.4f : 0.9f), z);
        }
        // Labels set on frames
        foreach(KeyValuePair<int, string> mark in m.Movie.Markers)
        {
            if(mark.Key < t.Count)
            {
                Dot(t[mark.Key].Pos, 9f, new Color(1f, 0.6f, 0.1f), z);
            }
        }
        if(m.DeathFrame >= 0 && m.DeathFrame < t.Count)
        {
            Vector2 p = t[m.DeathFrame].Pos;
            float s = 8f * upp;
            Seg(p + new Vector2(-s, -s), p + new Vector2(s, s), 3f, Color.red, z);
            Seg(p + new Vector2(-s, s), p + new Vector2(s, -s), 3f, Color.red, z);
        }
        // Where Pete is right now
        if(m.Frame < t.Count)
        {
            Vector2 p = t[m.Frame].Pos;
            float s = 10f * upp;
            Color c = new Color(1f, 1f, 1f, 0.9f);
            Seg(p + new Vector2(-s, 0), p + new Vector2(-s * 0.4f, 0), 2f, c, z);
            Seg(p + new Vector2(s * 0.4f, 0), p + new Vector2(s, 0), 2f, c, z);
            Seg(p + new Vector2(0, -s), p + new Vector2(0, -s * 0.4f), 2f, c, z);
            Seg(p + new Vector2(0, s * 0.4f), p + new Vector2(0, s), 2f, c, z);
        }
    }

    // ---------------------------------------------------------------
    //  Prediction lines (see TasPredict)
    // ---------------------------------------------------------------
    private void DrawPredictions(TasPredict pr, float z)
    {
        // Back to front, so the green "your path" line ends up on top
        for(int li = pr.Lines.Count - 1; li >= 0; li--)
        {
            TasPrediction p = pr.Lines[li];
            int n = p.Pos.Count;
            for(int i = 1; i < n; i++)
            {
                Color c = p.Color;
                c.a = Mathf.Lerp(0.95f, 0.35f, (float)i / Mathf.Max(1, n - 1));
                Seg(p.Pos[i - 1], p.Pos[i], p.Width, c, z);
            }
            Color dot = p.Color;
            dot.a = 0.8f;
            for(int i = 10; i < n; i += 10)
            {
                Dot(p.Pos[i], i % 50 == 0 ? 7f : 5f, dot, z);
            }
            // Where Pete takes off and where he lands (a little bar under his feet)
            if(p.JumpAt > 0 && p.JumpAt < n)
            {
                Circle(p.Pos[p.JumpAt], 5f * upp, 2f, p.Color, z);
            }
            foreach(int i in p.Landings)
            {
                if(i < n)
                {
                    Vector2 feet = p.Pos[i] + new Vector2(0f, pr.FootY);
                    float w = Mathf.Max(0.2f, pr.BoxSize.x * 0.6f);
                    Seg(feet + new Vector2(-w, 0f), feet + new Vector2(w, 0f), 3f, p.Color, z);
                }
            }
            foreach(int i in p.Hits)
            {
                if(i < n)
                {
                    Cross(p.Pos[i], 7f, TasPredict.HitColor, z);
                }
            }
            if(p.DeathAt >= 0 && p.DeathAt < n)
            {
                Cross(p.Pos[p.DeathAt], 13f, Color.red, z);
            }
        }
        // Pete's outline where the green line first lands
        TasPrediction path = pr.Path;
        if(path != null && pr.Lines.Contains(path) && path.Landings.Count > 0 && path.Landings[0] < path.Pos.Count && pr.BoxSize.x > 0f)
        {
            Vector2 c = path.Pos[path.Landings[0]] + pr.BoxCenter;
            Vector2 h = pr.BoxSize * 0.5f;
            Color col = TasPredict.PathColor;
            col.a = 0.7f;
            Seg(c + new Vector2(-h.x, -h.y), c + new Vector2(h.x, -h.y), 1.5f, col, z);
            Seg(c + new Vector2(h.x, -h.y), c + new Vector2(h.x, h.y), 1.5f, col, z);
            Seg(c + new Vector2(h.x, h.y), c + new Vector2(-h.x, h.y), 1.5f, col, z);
            Seg(c + new Vector2(-h.x, h.y), c + new Vector2(-h.x, -h.y), 1.5f, col, z);
        }
    }

    private void Cross(Vector2 p, float px, Color c, float z)
    {
        float s = px * upp;
        Seg(p + new Vector2(-s, -s), p + new Vector2(s, s), 3f, c, z);
        Seg(p + new Vector2(-s, s), p + new Vector2(s, -s), 3f, c, z);
    }

    // ---------------------------------------------------------------
    //  Hitboxes
    // ---------------------------------------------------------------
    public static readonly Color PeteColor = new Color(0.3f, 1f, 0.35f);
    public static readonly Color SwordColor = new Color(1f, 0.6f, 0.1f);
    public static readonly Color EnemyColor = new Color(1f, 0.25f, 0.25f);
    public static readonly Color PickupColor = new Color(0.3f, 0.85f, 1f);
    public static readonly Color FinishColor = new Color(1f, 0.85f, 0.2f);
    public static readonly Color LevelColor = new Color(1f, 1f, 1f, 0.4f);
    public static readonly Color TriggerColor = new Color(0.75f, 0.55f, 1f, 0.35f);

    private readonly List<Collider2D> colliders = new List<Collider2D>();
    private float collidersFoundAt = -10f;
    private readonly PhysicsShapeGroup2D shapes = new PhysicsShapeGroup2D();
    private readonly List<Vector2> shapeVerts = new List<Vector2>();
    private readonly List<Vector2> worldVerts = new List<Vector2>();

    // Which colour a collider gets, or false if it isn't shown
    private static bool HitboxStyle(Collider2D c, out Color color, out float width)
    {
        GameObject go = c.gameObject;
        width = 2f;
        if(go.CompareTag("Player"))
        {
            color = PeteColor;
            return true;
        }
        if(go.GetComponent<MeleeWeapon>() != null)
        {
            color = SwordColor;
            width = 3f;
            return true;
        }
        if(go.GetComponent<EnemyHealth>() != null || go.GetComponent<EnemyAttack>() != null || go.GetComponent<DamagingObsticals>() != null)
        {
            color = EnemyColor;
            if(c.isTrigger)
            {
                // Enemies' "notice Pete" / turn-around zones
                color.a = 0.35f;
                width = 1.5f;
            }
            return true;
        }
        if(go.GetComponent<HealthPickup>() != null || go.GetComponent<PowerUp>() != null)
        {
            color = PickupColor;
            return true;
        }
        if(go.GetComponent<GameFinish>() != null)
        {
            color = FinishColor;
            return true;
        }
        if(!ShowLevelCollision)
        {
            color = default(Color);
            return false;
        }
        color = c.isTrigger ? TriggerColor : LevelColor;
        width = 1.5f;
        return true;
    }

    private void DrawHitboxes(Camera cam, float z)
    {
        // Objects come and go (pickups, the sword), so look again twice a second
        if(Time.unscaledTime > collidersFoundAt + 0.5f || colliders.Count == 0)
        {
            colliders.Clear();
            colliders.AddRange(FindObjectsOfType<Collider2D>());
            collidersFoundAt = Time.unscaledTime;
        }
        Vector3 lo = cam.ViewportToWorldPoint(Vector3.zero);
        Vector3 hi = cam.ViewportToWorldPoint(Vector3.one);
        Rect view = Rect.MinMaxRect(lo.x - 1f, lo.y - 1f, hi.x + 1f, hi.y + 1f);
        foreach(Collider2D c in colliders)
        {
            if(c == null || !c.enabled || !c.gameObject.activeInHierarchy)
            {
                continue;
            }
            string sceneName = c.gameObject.scene.name;
            if(sceneName != null && sceneName.StartsWith("[TAS]"))
            {
                // The prediction lines' hidden copy of the level
                continue;
            }
            Color color;
            float width;
            if(!HitboxStyle(c, out color, out width))
            {
                continue;
            }
            Bounds b = c.bounds;
            if(b.max.x < view.xMin || b.min.x > view.xMax || b.max.y < view.yMin || b.min.y > view.yMax)
            {
                continue;
            }
            c.GetShapes(shapes);
            Matrix4x4 toWorld = shapes.localToWorldMatrix;
            float radiusScale = ((Vector2)toWorld.MultiplyVector(Vector3.right)).magnitude;
            for(int i = 0; i < shapes.shapeCount; i++)
            {
                PhysicsShape2D shape = shapes.GetShape(i);
                shapes.GetShapeVertices(i, shapeVerts);
                worldVerts.Clear();
                foreach(Vector2 v in shapeVerts)
                {
                    worldVerts.Add(toWorld.MultiplyPoint3x4(v));
                }
                if(worldVerts.Count == 0)
                {
                    continue;
                }
                // Skip tiles far off screen (tilemaps have lots of them)
                Vector2 first = worldVerts[0];
                if(first.x < view.xMin - 2f || first.x > view.xMax + 2f || first.y < view.yMin - 2f || first.y > view.yMax + 2f)
                {
                    continue;
                }
                float radius = shape.radius * radiusScale;
                switch(shape.shapeType)
                {
                    case PhysicsShapeType2D.Circle:
                        Circle(worldVerts[0], radius, width, color, z);
                        break;
                    case PhysicsShapeType2D.Capsule:
                        if(worldVerts.Count >= 2)
                        {
                            Vector2 a = worldVerts[0], d = worldVerts[1];
                            Vector2 dir = d - a;
                            Vector2 n = dir.sqrMagnitude > 1e-8f ? new Vector2(-dir.y, dir.x).normalized * radius : Vector2.zero;
                            Circle(a, radius, width, color, z);
                            Circle(d, radius, width, color, z);
                            Seg(a + n, d + n, width, color, z);
                            Seg(a - n, d - n, width, color, z);
                        }
                        break;
                    case PhysicsShapeType2D.Polygon:
                        for(int k = 0; k < worldVerts.Count; k++)
                        {
                            Seg(worldVerts[k], worldVerts[(k + 1) % worldVerts.Count], width, color, z);
                        }
                        break;
                    default:
                        for(int k = 1; k < worldVerts.Count; k++)
                        {
                            Seg(worldVerts[k - 1], worldVerts[k], width, color, z);
                        }
                        break;
                }
            }
        }
    }

    private void Circle(Vector2 centre, float radius, float px, Color c, float z)
    {
        const int steps = 24;
        Vector2 prev = centre + new Vector2(radius, 0f);
        for(int i = 1; i <= steps; i++)
        {
            float a = i * Mathf.PI * 2f / steps;
            Vector2 next = centre + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
            Seg(prev, next, px, c, z);
            prev = next;
        }
    }

    // A line segment drawn as a thin quad so it has a width in screen pixels.
    private void Seg(Vector2 a, Vector2 b, float px, Color c, float z)
    {
        Vector2 d = b - a;
        float len = d.magnitude;
        if(len < 1e-6f)
        {
            return;
        }
        Vector2 n = new Vector2(-d.y, d.x) / len * (px * 0.5f * upp);
        GL.Color(c);
        GL.Vertex3(a.x - n.x, a.y - n.y, z);
        GL.Vertex3(a.x + n.x, a.y + n.y, z);
        GL.Vertex3(b.x + n.x, b.y + n.y, z);
        GL.Vertex3(b.x - n.x, b.y - n.y, z);
    }

    private void Dot(Vector2 p, float px, Color c, float z)
    {
        float h = px * 0.5f * upp;
        GL.Color(c);
        GL.Vertex3(p.x - h, p.y - h, z);
        GL.Vertex3(p.x - h, p.y + h, z);
        GL.Vertex3(p.x + h, p.y + h, z);
        GL.Vertex3(p.x + h, p.y - h, z);
    }
}
