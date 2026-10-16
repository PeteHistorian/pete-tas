using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PowerUp : MonoBehaviour
{
    [SerializeField] private GameObject oneMore;
    [SerializeField] private GameObject returnStart;
    private bool hasGivenPower = false;

    private void OnTriggerEnter2D(Collider2D other) 
    {
        if(TasHooks.MuteCallbacks) return;
        if(other.CompareTag("Player"))
        {
            Collect(other);
            GetComponent<SpriteRenderer>().enabled = false;
        }    
    }

    private void Collect(Collider2D player) 
    {
        if(!hasGivenPower)
        {
            player.GetComponent<PowerStatus>().Powers++;
            AudioManager.Instance.PlaySound("O_FlyOrb");
            hasGivenPower = true;
        }
        if(player.GetComponent<PowerStatus>().Powers == 1)
        {
            oneMore.SetActive(true);
            //Invoke("DisableScreen", 5f);
            disableTextTimes.Add(TasHooks.GameTime + 5);
        }
        else if(player.GetComponent<PowerStatus>().Powers == 2)
        {
            returnStart.SetActive(true);
            //Invoke("DisableScreen", 5f);
            disableTextTimes.Add(TasHooks.GameTime + 5);
        }
    }

    private void DiscableScreen()
    {
        oneMore.SetActive(false);
        returnStart.SetActive(false);
    }

    // Times when the message hides and the orb can give power again.
    // These replace the old WaitForSeconds coroutine (same timing) so the TAS tool can save and rewind them.
    private List<double> disableTextTimes = new List<double>();

    private void LateUpdate()
    {
        for(int i = disableTextTimes.Count - 1; i >= 0; i--)
        {
            if(TasHooks.GameTime >= disableTextTimes[i])
            {
                disableTextTimes.RemoveAt(i);
                oneMore.SetActive(false);
                returnStart.SetActive(false);
                hasGivenPower = false;
            }
        }
    }

    private void OnDisable()
    {
        // Coroutines stop when the object is switched off, so pending timers do too
        if(TasHooks.MuteCallbacks) return;
        disableTextTimes.Clear();
    }
}
