using System.Collections;
using System.Collections.Generic;
using System;
using UnityEngine;
using UnityEngine.UI;

public class AudioManager : MonoBehaviour
{ 
    public static AudioManager Instance {get; private set; } //Instance des Audiomanagers

    public float generalVolume = 1f;

    //Array for all the SoundEffects
    public Sound[] sounds;

    //Function to Instantiate the SoundEffects
    public void PlaySound(string name)
    {
        Sound s = Array.Find(sounds, sound => sound.name == name);
        if(s == null)
        {
            Debug.LogWarning("Sound '" + name + "' was not found!");
            return;
        }
        s.source.pitch = s.pitch + UnityEngine.Random.Range((-1) * s.pitchVariation, s.pitchVariation);
        s.source.Play();
    }    

    private void Awake() 
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

        //Getting SoundEffects
        foreach(Sound s in sounds)
        {
            s.source = gameObject.AddComponent<AudioSource>();
        }
        ReloadVolume();
    }

    public void ChangeGenralVolume(float newVolume)
    {
        generalVolume = newVolume;
        ReloadVolume();
    }


    public void ReloadVolume()
    {
        foreach(Sound s in sounds)
        {
            s.source.clip = s.clip;
            s.source.volume = s.volume * (generalVolume*2);
            s.source.pitch = s.pitch;
        }
    }
}
