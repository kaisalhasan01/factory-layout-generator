using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Generates the scene and builds the player, so nothing has to be set up by hand:
///   Unity.exe -batchmode -quit -projectPath unity -executeMethod FactoryBuild.BuildPlayer
/// The scene holds a single FactoryApp; camera, light and every model are made at runtime.
/// </summary>
public static class FactoryBuild
{
    const string ScenePath = "Assets/Scenes/Factory.unity";
    const string OutDir = "../Build/FactoryLayout";

    [MenuItem("Factory/Build scene")]
    public static void BuildScene()
    {
        Directory.CreateDirectory("Assets/Scenes");
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        new GameObject("FactoryApp").AddComponent<FactoryApp>();
        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        Debug.Log("FactoryBuild: scene written to " + ScenePath);
    }

    [MenuItem("Factory/Build player")]
    public static void BuildPlayer()
    {
        BuildScene();
        // Every material is created at runtime, so the build cannot see that these shaders are
        // used and strips them; the player then renders magenta. Force them in.
        IncludeShader("Standard");
        IncludeShader("Sprites/Default");

        PlayerSettings.companyName = "Kais Alhasan";
        PlayerSettings.productName = "Factory Layout";
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 1600;
        PlayerSettings.defaultScreenHeight = 900;
        PlayerSettings.resizableWindow = true;
        PlayerSettings.runInBackground = true;
        PlayerSettings.colorSpace = ColorSpace.Linear;

        var exe = Path.Combine(OutDir, "FactoryLayout.exe");
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = exe,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None,
        });
        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
        {
            Debug.LogError("FactoryBuild: build failed: " + report.summary.result);
            if (Application.isBatchMode) EditorApplication.Exit(1);
            return;
        }

        // Ship the example layouts next to the exe (the app's Load list reads this folder).
        var src = Path.GetFullPath("../layouts");
        var dst = Path.Combine(OutDir, "layouts");
        Directory.CreateDirectory(dst);
        if (Directory.Exists(src))
            foreach (var f in Directory.GetFiles(src, "*.json"))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        Debug.Log("FactoryBuild: player built at " + Path.GetFullPath(exe));
    }

    static void IncludeShader(string name)
    {
        var shader = Shader.Find(name);
        if (shader == null) { Debug.LogError("FactoryBuild: shader not found: " + name); return; }
        var settings = AssetDatabase.LoadAssetAtPath<GraphicsSettings>("ProjectSettings/GraphicsSettings.asset");
        var so = new SerializedObject(settings);
        var list = so.FindProperty("m_AlwaysIncludedShaders");
        for (int i = 0; i < list.arraySize; i++)
            if (list.GetArrayElementAtIndex(i).objectReferenceValue == shader) return;
        list.InsertArrayElementAtIndex(list.arraySize);
        list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
        so.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();
        Debug.Log("FactoryBuild: added " + name + " to Always Included Shaders");
    }
}
