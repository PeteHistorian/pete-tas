using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// The "Pete TAS" menu at the top of the Unity Editor.
[InitializeOnLoad]
public static class TasMenu
{
    private const string PlayKey = "PeteTAS.PlayTAS";

    static TasMenu()
    {
        EditorApplication.playModeStateChanged += state =>
        {
            if(state == PlayModeStateChange.EnteredEditMode)
            {
                // Pressing the normal Play button next time plays the normal game
                SessionState.SetBool(PlayKey, false);
            }
        };
    }

    [MenuItem("Pete TAS/Play TAS", false, 1)]
    private static void PlayTas()
    {
        StartTas(false);
    }

    [MenuItem("Pete TAS/Run Self-Test", false, 20)]
    private static void RunSelfTest()
    {
        StartTas(true);
    }

    // Command line: Unity.exe -projectPath <Pete> -executeMethod TasMenu.RunSelfTestFromCommandLine -tasEditorSelfTest
    public static void RunSelfTestFromCommandLine()
    {
        StartTas(true);
    }

    private static void StartTas(bool selfTest)
    {
        if(EditorApplication.isPlaying)
        {
            return;
        }
        if(!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            return;
        }
        EditorSceneManager.OpenScene("Assets/Scenes/World.unity");
        SessionState.SetBool(PlayKey, true);
        SessionState.SetBool("PeteTAS.SelfTest", selfTest);
        EditorApplication.isPlaying = true;
    }

    [MenuItem("Pete TAS/Open TAS Files Folder", false, 40)]
    private static void OpenFolder()
    {
        string path = Path.Combine(Path.GetDirectoryName(Application.dataPath), "TAS Files");
        Directory.CreateDirectory(path);
        EditorUtility.RevealInFinder(path);
    }

    [MenuItem("Pete TAS/Build Pete TAS.exe", false, 60)]
    private static void Build()
    {
        TasBuild.BuildWindows();
    }
}
