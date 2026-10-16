using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public class MusicManager : MonoBehaviour
{ 
    public static MusicManager Instance {get; private set; } //Instance des Audiomanagers

    [SerializeField] private Sound mainMenuMusic;
    [SerializeField] private Sound finishMusic;
    [SerializeField] private Sound[] musicTracks;
    
    private AudioSource audioSource;
    private Sound currentSong;
    private AudioClip lastPlayedSong;
    
    public float musicVolume;
    
    // Start is called before the first frame update
    void Start()
    {
        //Instance check
        if(Instance == null)
        {
            Instance = this;
        }    
        else
        {
            Destroy(gameObject);
        }

        audioSource = GetComponent<AudioSource>();
        SelectNewMusic();
    }

    private Sound GetRandomMusic()
    {   
        Sound randomSong;
        do
        {
            randomSong = musicTracks[Random.Range(0, musicTracks.Length)];
        }while(randomSong.clip == lastPlayedSong);
        
        return randomSong;
    }

    void SelectNewMusic()
    {
        if(SceneManager.GetActiveScene().buildIndex == 0)
        {
            currentSong = mainMenuMusic;
        }
        else if(SceneManager.GetActiveScene().buildIndex != SceneManager.sceneCountInBuildSettings-1)
        {
            currentSong = GetRandomMusic();
        }
        else if(SceneManager.GetActiveScene().buildIndex == SceneManager.sceneCountInBuildSettings-1)
        {
            currentSong = finishMusic;
        }

        audioSource.clip = currentSong.clip;
        ReloadVolume();
        audioSource.Play();
        lastPlayedSong = audioSource.clip;

        Invoke("SelectNewMusic", currentSong.clip.length + 3);
    }

    public void ReloadVolume()
    {
        audioSource.volume = currentSong.volume * musicVolume;
    }
}
