using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class roomTransition : MonoBehaviour
{
    public GameObject vCam;

    private void OnTriggerEnter2D(Collider2D other) 
    {
        if(TasHooks.MuteCallbacks) return;
        if(other.CompareTag("Player") && !other.isTrigger)
        {
            vCam.SetActive(true);
            if(NetHooks.RoomEntered != null) NetHooks.RoomEntered(this);
        }
    }

    private void OnTriggerExit2D(Collider2D other) 
    {
        if(TasHooks.MuteCallbacks) return;
        if(other.CompareTag("Player") && !other.isTrigger)
        {
            vCam.SetActive(false);
        }
    }
}
