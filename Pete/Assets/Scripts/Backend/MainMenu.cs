using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public class MainMenu : MonoBehaviour
{
    public void Play()
    {
        // Online: the host starts the run for everyone
        if(NetHooks.PlayPressed != null && NetHooks.PlayPressed()) return;
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex + 1);
    }

    public void Restart()
    {
        if(NetHooks.PlayPressed != null && NetHooks.PlayPressed()) return;
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex - 1);
    }
}
