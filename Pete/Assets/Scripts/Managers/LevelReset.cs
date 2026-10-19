using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public class LevelReset : MonoBehaviour
{
    private void Update() 
    {
        if(PeteInput.GetKeyDown(KeyCode.R))
        {
            RESETLEVEL();
        }
        if(PeteInput.GetButton("Reset"))
        {
            RESETLEVEL();
        }
    }

    public static void RESETLEVEL()
    {
        // In TAS mode the TAS tool stops on the death frame instead of reloading the level
        if(TasHooks.Active)
        {
            TasHooks.PlayerDied = true;
            return;
        }
        // Online: only this player starts again (at their last safe spot), not the whole level
        if(NetHooks.Respawn != null)
        {
            NetHooks.Respawn();
            return;
        }
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }
    
}
