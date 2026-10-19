using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public class GameFinish : MonoBehaviour
{
    private void OnTriggerEnter2D(Collider2D other) 
    {
        if(TasHooks.MuteCallbacks) return;
        if(other.CompareTag("Player"))
        {
            if(other.GetComponent<PowerStatus>().Powers >= 2)
            {
                GameFinished();
            }
        }    
    }

    private void GameFinished()
    {
        // Online: the whole team finishes together (the host decides)
        if(NetHooks.Finish != null && NetHooks.Finish()) return;
        FinishNow();
    }

    public void FinishNow()
    {
        GameObject.FindGameObjectWithTag("TimerManager").GetComponent<TimerManager>().StopTimer();
        // In TAS mode the TAS tool stops on the finish frame instead of loading the end screen
        if(TasHooks.Active && !TasHooks.AllowFinishSceneLoad)
        {
            TasHooks.Finished = true;
            return;
        }
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex+1);
    }
}
