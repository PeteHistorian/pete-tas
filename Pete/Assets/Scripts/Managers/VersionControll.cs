using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

public class VersionControll : MonoBehaviour
{
    [SerializeField] private string version = "v1.0";
    [SerializeField] private TMP_Text versionText;
    
    private void Awake() 
    {
        versionText.text = version;
    }

    public void EnableVersionText(bool enable)
    {
        versionText.enabled = enable;
    }
}
