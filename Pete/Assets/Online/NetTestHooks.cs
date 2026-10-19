using System.Collections.Generic;
using UnityEngine;

// Small windows into the online mode for the automatic test (NetTest)
public partial class NetGame
{
    public bool InWorld { get { return inWorld; } }

    public NetPuppet PuppetOf(int id)
    {
        Player p;
        return Players.TryGetValue(id, out p) ? p.Puppet : null;
    }

    public Vector2 TestPetePos()
    {
        return peteBody != null ? peteBody.position : Vector2.zero;
    }

    public void TestPeteTo(Vector2 at)
    {
        if(peteBody == null) return;
        pete.transform.position = new Vector3(at.x, at.y, pete.transform.position.z);
        peteBody.position = at;
        peteBody.velocity = Vector2.zero;
    }

    public Vector2 TestRespawnPoint()
    {
        return respawnPoint;
    }

    public List<Vector2> TestEnemyPositions()
    {
        List<Vector2> list = new List<Vector2>();
        foreach(Enemy e in enemies)
        {
            list.Add(e.T != null ? (Vector2)e.T.position : Vector2.zero);
        }
        return list;
    }

    public int TestFirstEnemy()
    {
        for(int i = 0; i < enemies.Count; i++)
        {
            EnemyHealth h = enemies[i].Health;
            if(h != null && h.IsEnemy && h.CanBeHit && h.gameObject.activeInHierarchy) return i;
        }
        return -1;
    }

    public bool TestEnemyAlive(int i)
    {
        return i >= 0 && i < enemies.Count && enemies[i].Health != null && enemies[i].Health.Alive;
    }

    public void TestHitEnemy(int i)
    {
        if(i >= 0 && i < enemies.Count && enemies[i].Health != null && pete != null)
        {
            enemies[i].Health.doDamage(1, pete.transform, false);
        }
    }

    public void TestTouchOrb(int i)
    {
        if(i >= 0 && i < orbs.Count) OnOrbTouched(orbs[i]);
    }

    public void TestHostCollectOrb(int i)
    {
        HostCollectOrb(i);
    }

    public void TestReachFinish()
    {
        OnFinishTouched();
    }
}
