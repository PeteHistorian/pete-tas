using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PauseManager : MonoBehaviour
{
    [SerializeField] private GameObject pauseOverlay;    
    private bool isPaused = false; 

    void Update()
    {
        if(PeteInput.GetButtonDown("Pause"))
        {
            if(isPaused)
            {
                Resume();
            }
            else
            {
                Pause();
            }
        }
    }

    private void Pause()
    {
        pauseOverlay.SetActive(true);
        // Online the game keeps going for everyone else, so it doesn't stop here either
        if(!NetHooks.Online) Time.timeScale = 0f;
        isPaused = true;
    }

    public void Resume()
    {
        pauseOverlay.SetActive(false);
        Time.timeScale = 1f;
        isPaused = false;
    }

    public void Quit()
    {
        Application.Quit();
    }
}
