using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// The level part of the online co-op: our own Pete, the other players' Petes, and the world the host runs.
public partial class NetGame
{
    // ---------- The team's progress (the host's game is the boss of this) ----------

    private struct SpawnedPickup
    {
        public int Enemy;
        public Vector2 At;
    }

    private int teamPowers;
    private readonly HashSet<int> collectedOrbs = new HashSet<int>();
    private readonly HashSet<int> takenPickups = new HashSet<int>();
    private readonly Dictionary<int, SpawnedPickup> spawnedPickups = new Dictionary<int, SpawnedPickup>();
    private int nextPickupId = 1000;
    private bool finished;

    public int TeamPowers { get { return teamPowers; } }

    private void ResetTeam()
    {
        teamPowers = 0;
        collectedOrbs.Clear();
        takenPickups.Clear();
        spawnedPickups.Clear();
        nextPickupId = 1000;
        finished = false;
    }

    private void WriteTeam(NetWriter w)
    {
        w.Byte(teamPowers);
        w.Byte(collectedOrbs.Count);
        foreach(int o in collectedOrbs) w.Byte(o);
        w.Short(takenPickups.Count);
        foreach(int p in takenPickups) w.Int(p);
        w.Short(spawnedPickups.Count);
        foreach(KeyValuePair<int, SpawnedPickup> kv in spawnedPickups) w.Int(kv.Key).Byte(kv.Value.Enemy).Float(kv.Value.At.x).Float(kv.Value.At.y);
    }

    private void ReadTeam(NetReader r)
    {
        ResetTeam();
        teamPowers = r.Byte();
        int n = r.Byte();
        for(int i = 0; i < n; i++) collectedOrbs.Add(r.Byte());
        n = r.Short();
        for(int i = 0; i < n; i++) takenPickups.Add(r.Int());
        n = r.Short();
        for(int i = 0; i < n; i++)
        {
            int id = r.Int();
            SpawnedPickup sp;
            sp.Enemy = r.Byte();
            sp.At = new Vector2(r.Float(), r.Float());
            spawnedPickups[id] = sp;
        }
    }

    // ---------- The level ----------

    private bool inWorld;
    private GameObject pete;
    private Rigidbody2D peteBody;
    private PlayerHealth peteHealth;
    private PowerStatus petePowers;
    private Animator peteAnim;
    private SpriteRenderer peteSprite;
    private Ground peteGround;
    private TimerManager timer;
    private GameFinish gameFinish;
    private Vector3 respawnPoint;
    private bool lookForSafeSpot;
    private byte teleports;

    private class Enemy
    {
        public EnemyHealth Health;
        public Transform T;
        public Rigidbody2D Body;
        public SpriteRenderer Sprite;
        public Animator Anim;
        public bool HasWalkingParam;
        public EnemyChase Chaser;
        public Collider2D[] ChaseTriggers;
        public float Z;
        public float ScaleX;
        public readonly NetStream Stream = new NetStream();
        public float HitCooldown;
    }

    private readonly List<Enemy> enemies = new List<Enemy>();
    private readonly Dictionary<EnemyHealth, int> enemyIndex = new Dictionary<EnemyHealth, int>();
    private readonly List<PowerUp> orbs = new List<PowerUp>();
    private readonly Dictionary<int, HealthPickup> pickups = new Dictionary<int, HealthPickup>();
    private readonly Dictionary<HealthPickup, int> pickupIds = new Dictionary<HealthPickup, int>();
    private Transform hitOrigin;

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if(!IsOnline)
        {
            return;
        }
        SceneReady(scene.buildIndex);
    }

    private void SceneReady(int scene)
    {
        inWorld = false;
        if(Players.ContainsKey(LocalId)) Players[LocalId].Scene = scene;
        if(scene == WorldScene)
        {
            SetUpWorld();
        }
        if(IsClient && LocalId >= 0)
        {
            Send(0, W(Msg.Ready).Byte(scene), true);
        }
        if(IsHost)
        {
            SendPlayerList();
        }
    }

    // A stable number for every object, the same in everyone's game (they all load the same level)
    private static string PathOf(Transform t)
    {
        string path = t.GetSiblingIndex().ToString("D3");
        while(t.parent != null)
        {
            t = t.parent;
            path = t.GetSiblingIndex().ToString("D3") + "/" + path;
        }
        return path;
    }

    private static List<T> Sorted<T>() where T : Component
    {
        List<T> list = new List<T>(FindObjectsOfType<T>(true));
        list.Sort((a, b) => string.CompareOrdinal(PathOf(a.transform), PathOf(b.transform)));
        return list;
    }

    private void SetUpWorld()
    {
        pete = GameObject.FindGameObjectWithTag("Player");
        if(pete == null)
        {
            return;
        }
        peteBody = pete.GetComponent<Rigidbody2D>();
        peteHealth = pete.GetComponent<PlayerHealth>();
        petePowers = pete.GetComponent<PowerStatus>();
        peteAnim = pete.GetComponent<Animator>();
        peteSprite = pete.GetComponent<SpriteRenderer>();
        peteGround = pete.GetComponent<Ground>();
        GameObject tm = GameObject.FindGameObjectWithTag("TimerManager");
        timer = tm != null ? tm.GetComponent<TimerManager>() : null;
        gameFinish = FindObjectOfType<GameFinish>(true);
        respawnPoint = pete.transform.position;
        lookForSafeSpot = false;
        finished = false;
        if(hitOrigin == null)
        {
            GameObject h = new GameObject("[Online] sword hit point");
            DontDestroyOnLoad(h);
            hitOrigin = h.transform;
        }

        enemies.Clear();
        enemyIndex.Clear();
        foreach(EnemyHealth h in Sorted<EnemyHealth>())
        {
            Enemy e = new Enemy();
            e.Health = h;
            e.T = h.transform;
            e.Body = h.GetComponent<Rigidbody2D>();
            e.Sprite = h.GetComponent<SpriteRenderer>();
            e.Anim = h.GetComponent<Animator>();
            e.Chaser = h.GetComponent<EnemyChase>();
            e.Z = e.T.position.z;
            e.ScaleX = Mathf.Abs(e.T.localScale.x);
            if(e.Anim != null)
            {
                foreach(AnimatorControllerParameter par in e.Anim.parameters)
                {
                    if(par.name == "isWalking") e.HasWalkingParam = true;
                }
            }
            List<Collider2D> triggers = new List<Collider2D>();
            foreach(Collider2D c in h.GetComponents<Collider2D>())
            {
                if(c.isTrigger) triggers.Add(c);
            }
            e.ChaseTriggers = triggers.ToArray();
            if(IsClient && e.Body != null && e.Body.bodyType == RigidbodyType2D.Dynamic)
            {
                // The host moves the enemies; here they only follow (Pete can still bump into them and get hurt)
                e.Body.bodyType = RigidbodyType2D.Kinematic;
                e.Body.velocity = Vector2.zero;
            }
            enemyIndex[h] = enemies.Count;
            enemies.Add(e);
        }
        orbs.Clear();
        orbs.AddRange(Sorted<PowerUp>());
        pickups.Clear();
        pickupIds.Clear();
        int pid = 0;
        foreach(HealthPickup hp in Sorted<HealthPickup>())
        {
            pickups[pid] = hp;
            pickupIds[hp] = pid;
            pid++;
        }

        // Catch up with what the team has done already (when joining a run that's going)
        foreach(int o in collectedOrbs)
        {
            if(o < orbs.Count) orbs[o].NetCollected(0);
        }
        if(petePowers != null) petePowers.Powers = teamPowers;
        foreach(KeyValuePair<int, SpawnedPickup> kv in spawnedPickups)
        {
            MakeSpawnedPickup(kv.Key, kv.Value);
        }
        foreach(int id in takenPickups)
        {
            HidePickup(id);
        }

        inWorld = true;
        foreach(Player p in Players.Values)
        {
            if(p.Id != LocalId && (p.Scene == WorldScene || IsClient)) MakePuppet(p);
        }
    }

    private void MakePuppet(Player p)
    {
        if(p.Id == LocalId || !inWorld || pete == null)
        {
            return;
        }
        if(p.Puppet == null)
        {
            p.Puppet = NetPuppet.Create(pete, p.Id, p.Name, Tints[Mathf.Abs(p.Id) % Tints.Length]);
        }
        p.Puppet.PlayerName = p.Name;
    }

    private void ReceiveState(Player p, PeteState s)
    {
        p.State = s;
        p.HasState = true;
        if(p.Puppet == null && inWorld && p.Scene == WorldScene)
        {
            MakePuppet(p);
        }
        if(p.Puppet != null)
        {
            p.Puppet.Receive(s);
        }
        // The run's timer starts when anyone starts moving
        if(IsHost && inWorld && timer != null && !timer.timerOn && !finished && (s.Flags & PeteState.TimerStarted) != 0)
        {
            timer.StartTimer();
        }
    }

    private void SendLocalState()
    {
        if(pete == null || peteBody == null)
        {
            return;
        }
        PeteState s;
        s.Time = Time.unscaledTime;
        s.Pos = peteBody.position;
        s.Vel = peteBody.velocity;
        s.Facing = (sbyte)(pete.transform.localScale.x < 0 ? -1 : 1);
        s.Anim = 0;
        if(peteAnim != null)
        {
            for(int i = 0; i < NetPuppet.AnimBools.Length; i++)
            {
                if(peteAnim.GetBool(NetPuppet.AnimBools[i]))
                {
                    s.Anim = (byte)i;
                    break;
                }
            }
        }
        s.Flags = 0;
        if(peteSprite != null && peteSprite.color.g < 0.5f) s.Flags |= PeteState.Red;
        if(timer != null && timer.timerOn) s.Flags |= PeteState.TimerStarted;
        s.Hp = (byte)Mathf.Clamp(peteHealth != null ? peteHealth.HP : 0, 0, 255);
        s.Teleports = teleports;
        NetWriter w = W(Msg.PeteState);
        if(IsHost) w.Int(LocalId);
        WriteState(w, s);
        Broadcast(w, false);
    }

    // ---------- Every frame in the level ----------

    private void WorldUpdate()
    {
        if(pete == null)
        {
            return;
        }
        // Remember a safe place to start again: where Pete first stands in each room
        if(lookForSafeSpot && peteGround != null && peteGround.GetOnGround() && Mathf.Abs(peteBody.velocity.y) < 0.01f)
        {
            respawnPoint = pete.transform.position;
            lookForSafeSpot = false;
        }
        if(IsClient)
        {
            // The enemies go where the host says
            foreach(Enemy e in enemies)
            {
                if(e.T == null || !e.Stream.HasData || !e.Health.Alive) continue;
                Vector2 p = e.Stream.Position();
                e.T.position = new Vector3(p.x, p.y, e.Z);
            }
        }
        foreach(Enemy e in enemies)
        {
            if(e.HitCooldown > 0f) e.HitCooldown -= Time.deltaTime;
        }
    }

    private void FixedUpdate()
    {
        if(!inWorld || !IsHost)
        {
            return;
        }
        // Flying enemies notice the other players too
        foreach(Enemy e in enemies)
        {
            if(e.Chaser == null || !e.Chaser.enabled || e.Chaser.IsChasing || e.ChaseTriggers.Length == 0) continue;
            foreach(Player p in Players.Values)
            {
                if(p.Id == LocalId || !p.HasState || p.Scene != WorldScene) continue;
                foreach(Collider2D c in e.ChaseTriggers)
                {
                    if(c != null && c.enabled && c.OverlapPoint(p.State.Pos))
                    {
                        e.Chaser.StartChasing();
                        break;
                    }
                }
            }
        }
    }

    // Enemies chase whoever is closest
    private Vector3? NearestPlayer(Vector3 from)
    {
        Vector3 best = pete != null ? pete.transform.position : from;
        float bestDist = pete != null ? ((Vector2)(best - from)).sqrMagnitude : float.MaxValue;
        foreach(Player p in Players.Values)
        {
            if(p.Id == LocalId || !p.HasState || p.Scene != WorldScene || p.Puppet == null) continue;
            if(Time.unscaledTime - p.Puppet.Stream.LastArrival > 3f) continue;
            Vector2 at = p.State.Pos;
            float d = (at - (Vector2)from).sqrMagnitude;
            if(d < bestDist)
            {
                bestDist = d;
                best = new Vector3(at.x, at.y, from.z);
            }
        }
        return best;
    }

    // ---------- Enemies ----------

    private void SendWorld()
    {
        NetWriter w = W(Msg.WorldState).Float(Time.unscaledTime);
        w.Float(timer != null ? timer.CurrentTime : 0f).Bool(timer != null && timer.timerOn).Byte(teamPowers);
        w.Byte(enemies.Count);
        foreach(Enemy e in enemies)
        {
            int flags = 0;
            bool active = e.T != null && e.T.gameObject.activeSelf;
            if(active) flags |= 1;
            if(e.Health != null && e.Health.Alive) flags |= 2;
            if(e.Sprite != null && e.Sprite.color.g < 0.5f) flags |= 4;
            if(e.HasWalkingParam && e.Anim != null && e.Anim.isActiveAndEnabled && e.Anim.GetBool("isWalking")) flags |= 8;
            Vector3 p = e.T != null ? e.T.position : Vector3.zero;
            w.Byte(flags).Float(p.x).Float(p.y).SByte(e.T != null && e.T.localScale.x < 0 ? -1 : 1);
        }
        Broadcast(w, false);
    }

    private void ClientApplyWorld(NetReader r)
    {
        float hostTime = r.Float();
        float time = r.Float();
        bool timerOn = r.Bool();
        r.Byte();
        int n = r.Byte();
        if(!inWorld)
        {
            return;
        }
        if(timer != null && !finished)
        {
            if(timerOn && (!timer.timerOn || Mathf.Abs(timer.CurrentTime - time) > 0.15f))
            {
                timer.NetSetTime(time, true);
            }
        }
        for(int i = 0; i < n; i++)
        {
            int flags = r.Byte();
            Vector2 p = new Vector2(r.Float(), r.Float());
            int facing = r.SByte();
            if(i >= enemies.Count) continue;
            Enemy e = enemies[i];
            if(e.T == null) continue;
            bool alive = (flags & 2) != 0;
            if(!alive && e.Health.Alive)
            {
                e.Health.NetDie();
            }
            if((flags & 1) == 0)
            {
                if(e.T.gameObject.activeSelf) e.T.gameObject.SetActive(false);
                continue;
            }
            e.Stream.Add(hostTime, p, Vector2.zero);
            Vector3 s = e.T.localScale;
            e.T.localScale = new Vector3(facing * e.ScaleX, s.y, s.z);
            if(e.Sprite != null && alive) e.Sprite.color = (flags & 4) != 0 ? Color.red : Color.white;
            if(e.HasWalkingParam && e.Anim != null && e.Anim.isActiveAndEnabled) e.Anim.SetBool("isWalking", (flags & 8) != 0);
        }
    }

    // Our sword hit an enemy: on a joining player's game the host applies it (we only feel the bounce straight away)
    private bool OnEnemyHit(EnemyHealth enemy, int amount, Transform kbOrigin, bool isDown)
    {
        if(!IsClient)
        {
            return false;
        }
        int i;
        if(!enemyIndex.TryGetValue(enemy, out i) || !enemy.Alive)
        {
            return true;
        }
        Enemy e = enemies[i];
        if(e.HitCooldown > 0f)
        {
            return true;
        }
        e.HitCooldown = 0.2f;
        Vector3 at = kbOrigin != null ? kbOrigin.position : enemy.transform.position;
        Send(0, W(Msg.Hit).Byte(i).Byte(amount).Float(at.x).Float(at.y).Bool(isDown), true);
        if(enemy.CanBeHit)
        {
            AudioManager.Instance.PlaySound(enemy.CompareTag("Secret") ? "O_Fish" : "E_Hit");
            if(isDown && enemy.IsEnemy)
            {
                KnockbackTrigger k = enemy.GetComponent<KnockbackTrigger>();
                if(k != null) k.ReverseKnockback(kbOrigin);
            }
        }
        return true;
    }

    private void HostApplyHit(int index, int amount, Vector2 swordAt, bool isDown)
    {
        if(index < 0 || index >= enemies.Count || enemies[index].Health == null)
        {
            return;
        }
        hitOrigin.position = new Vector3(swordAt.x, swordAt.y, enemies[index].Z);
        NetHooks.SuppressPogo = true;
        try
        {
            enemies[index].Health.doDamage(amount, hitOrigin, isDown);
        }
        finally
        {
            NetHooks.SuppressPogo = false;
        }
    }

    // ---------- Orbs ----------

    private readonly HashSet<int> orbAsked = new HashSet<int>();

    private void OnOrbTouched(PowerUp orb)
    {
        int i = orbs.IndexOf(orb);
        if(i < 0 || collectedOrbs.Contains(i))
        {
            return;
        }
        if(IsHost)
        {
            HostCollectOrb(i);
        }
        else if(orbAsked.Add(i))
        {
            Send(0, W(Msg.Orb).Byte(i), true);
        }
    }

    private void HostCollectOrb(int i)
    {
        if(i < 0 || i >= orbs.Count || collectedOrbs.Contains(i))
        {
            return;
        }
        teamPowers++;
        Broadcast(W(Msg.Orb).Byte(i).Byte(teamPowers), true);
        ApplyOrb(i, teamPowers, true);
    }

    private void ApplyOrb(int i, int powers, bool sound)
    {
        teamPowers = Mathf.Max(teamPowers, powers);
        orbAsked.Remove(i);
        if(!collectedOrbs.Add(i) || !inWorld || i >= orbs.Count)
        {
            return;
        }
        orbs[i].NetCollected(teamPowers);
        if(petePowers != null) petePowers.Powers = teamPowers;
        if(sound) AudioManager.Instance.PlaySound("O_FlyOrb");
        Say(teamPowers >= 2 ? "The team has both orbs! Get back to the start to finish." : "The team has " + teamPowers + " of 2 orbs.", 5f);
    }

    // ---------- Health pickups ----------

    private void OnPickupTaken(HealthPickup pickup)
    {
        int id;
        if(!pickupIds.TryGetValue(pickup, out id))
        {
            return;
        }
        takenPickups.Add(id);
        Broadcast(W(Msg.PickupGone).Int(id), true);
    }

    private void HidePickup(int id)
    {
        takenPickups.Add(id);
        HealthPickup p;
        if(pickups.TryGetValue(id, out p) && p != null && p.gameObject.activeSelf)
        {
            p.gameObject.SetActive(false);
        }
    }

    private void OnPickupSpawned(GameObject pickup, HealthDrop drop)
    {
        HealthPickup hp = pickup != null ? pickup.GetComponent<HealthPickup>() : null;
        EnemyHealth from = drop != null ? drop.GetComponent<EnemyHealth>() : null;
        int enemy;
        if(hp == null || from == null || !enemyIndex.TryGetValue(from, out enemy))
        {
            return;
        }
        int id = nextPickupId++;
        pickups[id] = hp;
        pickupIds[hp] = id;
        SpawnedPickup sp;
        sp.Enemy = enemy;
        sp.At = pickup.transform.position;
        spawnedPickups[id] = sp;
        Broadcast(W(Msg.PickupNew).Int(id).Byte(enemy).Float(sp.At.x).Float(sp.At.y), true);
    }

    private void MakeSpawnedPickup(int id, SpawnedPickup sp)
    {
        if(pickups.ContainsKey(id) || sp.Enemy >= enemies.Count)
        {
            return;
        }
        HealthDrop drop = enemies[sp.Enemy].Health != null ? enemies[sp.Enemy].Health.GetComponent<HealthDrop>() : null;
        if(drop == null || drop.Prefab == null)
        {
            return;
        }
        GameObject g = Instantiate(drop.Prefab, new Vector3(sp.At.x, sp.At.y, 0f), Quaternion.identity);
        HealthPickup hp = g.GetComponent<HealthPickup>();
        if(hp != null)
        {
            pickups[id] = hp;
            pickupIds[hp] = id;
        }
        if(takenPickups.Contains(id))
        {
            g.SetActive(false);
        }
    }

    // ---------- Dying and the finish ----------

    private void OnRoomEntered(roomTransition room)
    {
        lookForSafeSpot = true;
    }

    private void RespawnLocal()
    {
        if(pete == null || peteBody == null)
        {
            return;
        }
        pete.transform.position = respawnPoint;
        peteBody.position = respawnPoint;
        peteBody.velocity = Vector2.zero;
        if(peteHealth != null) peteHealth.NetRestoreHealth();
        teleports++;
        Say("Back on your feet! (You start again where you first stood in this room.)", 4f);
    }

    private bool OnFinishTouched()
    {
        if(finished)
        {
            return true;
        }
        if(IsHost)
        {
            HostFinish();
        }
        else
        {
            Send(0, W(Msg.Finish), true);
        }
        return true;
    }

    private void HostFinish()
    {
        if(finished)
        {
            return;
        }
        float t = timer != null ? timer.CurrentTime : 0f;
        Broadcast(W(Msg.Finish).Float(t), true);
        ApplyFinish(t);
        hostScene = EndScene;
    }

    private void ApplyFinish(float time)
    {
        if(finished || !inWorld)
        {
            return;
        }
        finished = true;
        if(timer != null) timer.NetSetTime(time, true);
        lastRunTime = time;
        if(gameFinish != null)
        {
            gameFinish.FinishNow();
        }
    }

    public float lastRunTime = -1f;
}
