using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class EnemyHealth : MonoBehaviour
{
   
    [SerializeField]private bool damageable = true;
    [SerializeField]private int healthAmount = 1;
    [SerializeField]private float invulnerabilityTime = .2f;
    [SerializeField]private bool isEnemy;
    private bool hit;
    private int currentHealth;
    private Animator animator;
    private KnockbackTrigger knockbackTrigger;

    private void Start()
    {
        currentHealth = healthAmount;
        knockbackTrigger = GetComponent<KnockbackTrigger>(); 
    }

    public void doDamage(int amount, Transform kbOrigin, bool isDown)
    {
        // Online, on a joining player's game: the hit is sent to the host, who applies it for everyone
        if(NetHooks.EnemyHit != null && NetHooks.EnemyHit(this, amount, kbOrigin, isDown)) return;
        if (damageable && !hit && currentHealth > 0)
        {
            hit = true;
            currentHealth -= amount;
            if(CompareTag("Secret"))
            {
                AudioManager.Instance.PlaySound("O_Fish");
            }
            else
            {
                AudioManager.Instance.PlaySound("E_Hit"); 
            }
            
            if (currentHealth <= 0)
            {
                Death();
                if(isEnemy)
                {    
                   
                }
            }
            else
            {
                turnOffHitTimes.Add(TasHooks.GameTime + invulnerabilityTime);
                if(isEnemy)
                {
                    if(isDown)
                    {
                        knockbackTrigger.ReverseKnockback(kbOrigin);
                    }
                    else
                    {
                        knockbackTrigger.Knockback(kbOrigin);
                    }

                    GetComponent<HitColor>().ChangeToHitColor();
                }
            }
        }
    }

    // Times when the enemy can be hit again.
    // These replace the old WaitForSeconds coroutine (same timing) so the TAS tool can save and rewind them.
    private List<double> turnOffHitTimes = new List<double>();

    private void LateUpdate()
    {
        for(int i = turnOffHitTimes.Count - 1; i >= 0; i--)
        {
            if(TasHooks.GameTime >= turnOffHitTimes[i])
            {
                turnOffHitTimes.RemoveAt(i);
                hit = false;
            }
        }
    }

    private void OnDisable()
    {
        // Coroutines stop when the object is switched off, so pending timers do too
        if(TasHooks.MuteCallbacks) return;
        turnOffHitTimes.Clear();
    }

    private void Death()
    {
        currentHealth = 0;
        if(isEnemy)
        {
            GetComponent<Rigidbody2D>().bodyType = RigidbodyType2D.Static;
            GetComponent<EnemyAttack>().enabled = false;
            // Online, the host decides the health drops
            if(!NetHooks.IsClient) GetComponent<HealthDrop>().EnemyDrop();
            GetComponents<BoxCollider2D>()[0].enabled = false;
            GetComponents<BoxCollider2D>()[1].enabled = false;
            
            if(GetComponent<EnemyWalking>())
            {
                GetComponent<EnemyWalking>().enabled = false;
            }
            if(GetComponent<EnemyChase>())
            {
                GetComponent<EnemyChase>().enabled = false;
            }
            GetComponent<Animator>().SetTrigger("dead"); 
        }
        else
        {
            DisableEnemy();
        }
    }

    public void DisableEnemy()
    {
        gameObject.SetActive(false);
    }

    // Online: the host says this one was defeated
    public void NetDie()
    {
        if(currentHealth > 0 || gameObject.activeSelf)
        {
            Death();
        }
    }

    public bool Alive
    {
        get { return currentHealth > 0; }
    }

    public bool IsEnemy
    {
        get { return isEnemy; }
    }

    public bool CanBeHit
    {
        get { return damageable && !hit && currentHealth > 0; }
    }
}