using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class HealthPickup : MonoBehaviour
{
    [SerializeField] int healingAmount;
    private bool hasHealed = false;

    private void OnCollisionEnter2D(Collision2D other) 
    {
        if(TasHooks.MuteCallbacks) return;
        if(other.collider.CompareTag("Player") && hasHealed == false)
        {
            hasHealed = true;
            other.collider.GetComponent<PlayerHealth>().HealPlayer(healingAmount);
            AudioManager.Instance.PlaySound("O_HealthPickup");
            // Hidden instead of destroyed so the TAS tool can bring it back when rewinding
            gameObject.SetActive(false);
        }
    }

}
