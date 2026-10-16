using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public class SceneChangeManager : MonoBehaviour
{
    //Managers
    [SerializeField] private StoreTimer storeTimer;
    [SerializeField] private VersionControll versionControll;

    private void OnEnable() 
    {
        SceneManager.sceneLoaded += OnSceneLoaded;    
    }

    private void OnDisable() 
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;    
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if(scene.name == "Title")
        {
            versionControll.EnableVersionText(true);
        }
        else if(scene.name == "End")
        {
            versionControll.EnableVersionText(true);
            storeTimer.DisplayFinishTime();
        }
        else
        {
            versionControll.EnableVersionText(false);
        }
    }
}
