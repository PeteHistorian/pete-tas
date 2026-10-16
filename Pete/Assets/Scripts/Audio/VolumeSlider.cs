using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class VolumeSlider : MonoBehaviour
{

    [SerializeField] private Slider soundSlider;
    [SerializeField] private Slider musicSlider;

    void Awake()
    {
        CheckSoundVolume();
        CheckMusicVolume();
    }

    private void CheckSoundVolume()
    {
        if(!PlayerPrefs.HasKey("soundVolume"))
        {
            PlayerPrefs.SetFloat("soundVolume", 0.5f);
            LoadSoundVolume();
        }
        else
        {
            LoadSoundVolume();
        }
    }

    private void CheckMusicVolume()
    {
        if(!PlayerPrefs.HasKey("musicVolume"))
        {
            PlayerPrefs.SetFloat("musicVolume", 0.5f);
            LoadMusicVolume();
        }
        else
        {
            LoadMusicVolume();
        }
    }

    private void LoadSoundVolume()
    {
        soundSlider.value = PlayerPrefs.GetFloat("soundVolume");
    }

    private void LoadMusicVolume()
    {
        musicSlider.value = PlayerPrefs.GetFloat("musicVolume");
    }

    private void SaveSoundVolume()
    {
        PlayerPrefs.SetFloat("soundVolume", soundSlider.value);
    }

    private void SaveMusicVolume()
    {
        PlayerPrefs.SetFloat("musicVolume", musicSlider.value);
    }

    public void ChangeSoundVolume() 
    {
        SaveSoundVolume();
        AudioManager.Instance.ChangeGenralVolume(soundSlider.value);
    }

    public void ChangeMusicVolume()
    {
        SaveMusicVolume();
        MusicManager.Instance.musicVolume = musicSlider.value;
        MusicManager.Instance.ReloadVolume();
    }
}
