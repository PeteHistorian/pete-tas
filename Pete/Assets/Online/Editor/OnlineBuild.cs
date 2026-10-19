using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Builds "Pete Online.exe": the normal game plus online co-op ("Play with friends" on the title screen).
// From the command line: Unity.exe -quit -projectPath <Pete> -executeMethod OnlineBuild.BuildWindows [-onlineBuildOut <folder>]
public static class OnlineBuild
{
    [MenuItem("Pete Online/Build Pete Online.exe")]
    public static void BuildWindows()
    {
        string root = Directory.GetParent(Path.GetDirectoryName(Application.dataPath)).FullName;
        string outDir = Path.Combine(root, "PeteOnline Build");
        string[] args = System.Environment.GetCommandLineArgs();
        for(int i = 0; i < args.Length - 1; i++)
        {
            if(args[i] == "-onlineBuildOut")
            {
                outDir = args[i + 1];
            }
        }
        string exe = Path.Combine(outDir, "Pete Online.exe");

        // Only for this build: its own name (so it keeps its own settings) and it keeps running in the background
        // (so a host who clicks another window doesn't freeze everyone). Everything is put back afterwards.
        string oldName = PlayerSettings.productName;
        FullScreenMode oldMode = PlayerSettings.fullScreenMode;
        bool oldResizable = PlayerSettings.resizableWindow;
        int oldW = PlayerSettings.defaultScreenWidth;
        int oldH = PlayerSettings.defaultScreenHeight;
        bool oldBackground = PlayerSettings.runInBackground;
        bool ok = false;
        try
        {
            PlayerSettings.productName = "Pete Online";
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.defaultScreenWidth = 1600;
            PlayerSettings.defaultScreenHeight = 900;
            PlayerSettings.runInBackground = true;

            BuildPlayerOptions options = new BuildPlayerOptions();
            options.scenes = new[] { "Assets/Scenes/Title.unity", "Assets/Scenes/World.unity", "Assets/Scenes/End.unity" };
            options.locationPathName = exe;
            options.target = BuildTarget.StandaloneWindows64;
            options.options = BuildOptions.None;
            options.extraScriptingDefines = new[] { "PETE_ONLINE" };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            ok = report.summary.result == BuildResult.Succeeded;
            Debug.Log("[Online] Build " + report.summary.result + ": " + exe + " (" + (report.summary.totalSize / (1024 * 1024)) + " MB, " + report.summary.totalErrors + " errors)");
        }
        finally
        {
            PlayerSettings.productName = oldName;
            PlayerSettings.fullScreenMode = oldMode;
            PlayerSettings.resizableWindow = oldResizable;
            PlayerSettings.defaultScreenWidth = oldW;
            PlayerSettings.defaultScreenHeight = oldH;
            PlayerSettings.runInBackground = oldBackground;
            AssetDatabase.SaveAssets();
        }
        if(Application.isBatchMode || System.Environment.CommandLine.Contains("-executeMethod"))
        {
            EditorApplication.Exit(ok ? 0 : 1);
        }
        else if(ok)
        {
            EditorUtility.RevealInFinder(exe);
        }
    }
}
