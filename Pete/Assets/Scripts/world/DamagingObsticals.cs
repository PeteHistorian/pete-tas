using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class DamagingObsticals : MonoBehaviour
{
    private void OnCollisionEnter2D(Collision2D other) 
    {
        if(TasHooks.MuteCallbacks) return;
        if(other.collider.CompareTag("Player"))
        {
            GetComponent<EnemyAttack>().DamagePlayer(other.collider);
        }    
    }
}
