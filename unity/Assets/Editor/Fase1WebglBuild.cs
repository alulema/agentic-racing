using System.Collections.Generic;
using System.IO;
using AgenticRacing.Demo;
using AgenticRacing.Track;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace AgenticRacing.EditorTools
{
    /// <summary>
    /// Headless WebGL build of the Fase 1 drivable demo:
    ///   -batchmode -executeMethod AgenticRacing.EditorTools.Fase1WebglBuild.Build
    /// Creates a one-object scene (TrackConfig + TrackDemoBootstrap) and builds it
    /// to Builds/track-demo with compression disabled so a plain static server can
    /// host it (Unity WebGL already uses relative paths — CLAUDE.md §2.2).
    ///
    /// This is a Fase 1 verification build, NOT the shipping pipeline. It tweaks a
    /// few WebGL PlayerSettings and the Always Included Shaders list, and restores
    /// both afterwards, so it must not leave ProjectSettings/GraphicsSettings dirty
    /// — CI's build keeps Brotli and the .br/.gz path that Fase 0 validated.
    /// </summary>
    public static class Fase1WebglBuild
    {
        private const string ScenePath = "Assets/Scenes/TrackDemo.unity";
        private const string OutputDir = "Builds/track-demo";

        // TrackDemoBootstrap builds its materials at runtime via Shader.Find, so
        // nothing references these shaders at build time and Unity strips their
        // variants -> magenta in the player. Forcing them into Always Included
        // Shaders makes Unity compile the full variant set for WebGL.
        private static readonly string[] ForceIncludeShaders =
        {
            "Universal Render Pipeline/Unlit",
            "Universal Render Pipeline/Lit",
            "Sprites/Default",
        };

        public static void Build()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var go = new GameObject("TrackDemo");
            go.AddComponent<TrackConfig>();
            go.AddComponent<TrackDemoBootstrap>();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);

            string prevTemplate = PlayerSettings.WebGL.template;
            var prevCompression = PlayerSettings.WebGL.compressionFormat;
            bool prevDataCaching = PlayerSettings.WebGL.dataCaching;
            bool prevRunInBackground = PlayerSettings.runInBackground;
            List<Shader> addedShaders = null;

            BuildReport report;
            try
            {
                // "APPLICATION:Default" is the built-in template this project uses.
                PlayerSettings.WebGL.template = "APPLICATION:Default";
                PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
                PlayerSettings.WebGL.dataCaching = false;
                PlayerSettings.runInBackground = true;
                addedShaders = AddAlwaysIncludedShaders(ForceIncludeShaders);

                if (Directory.Exists(OutputDir)) Directory.Delete(OutputDir, true);

                report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    locationPathName = OutputDir,
                    target = BuildTarget.WebGL,
                    options = BuildOptions.None,
                });
            }
            finally
            {
                PlayerSettings.WebGL.template = prevTemplate;
                PlayerSettings.WebGL.compressionFormat = prevCompression;
                PlayerSettings.WebGL.dataCaching = prevDataCaching;
                PlayerSettings.runInBackground = prevRunInBackground;
                RemoveAlwaysIncludedShaders(addedShaders);
                AssetDatabase.SaveAssets();
            }

            var s = report.summary;
            Debug.Log($"[Fase1WebglBuild] result={s.result} errors={s.totalErrors} " +
                      $"size={s.totalSize} bytes -> {OutputDir}");

            if (Application.isBatchMode)
                EditorApplication.Exit(s.result == BuildResult.Succeeded ? 0 : 1);
        }

        private static List<Shader> AddAlwaysIncludedShaders(string[] names)
        {
            var added = new List<Shader>();
            var so = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
            var arr = so.FindProperty("m_AlwaysIncludedShaders");

            foreach (string name in names)
            {
                Shader sh = Shader.Find(name);
                if (sh == null)
                {
                    Debug.LogWarning($"[Fase1WebglBuild] shader not found, cannot force-include: {name}");
                    continue;
                }

                bool present = false;
                for (int i = 0; i < arr.arraySize; i++)
                {
                    if (arr.GetArrayElementAtIndex(i).objectReferenceValue == sh) { present = true; break; }
                }
                if (present) continue;

                arr.arraySize++;
                arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = sh;
                added.Add(sh);
                Debug.Log($"[Fase1WebglBuild] force-included shader for the build: {name}");
            }

            so.ApplyModifiedProperties();
            return added;
        }

        private static void RemoveAlwaysIncludedShaders(List<Shader> shaders)
        {
            if (shaders == null || shaders.Count == 0) return;

            var so = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
            var arr = so.FindProperty("m_AlwaysIncludedShaders");

            for (int i = arr.arraySize - 1; i >= 0; i--)
            {
                var prop = arr.GetArrayElementAtIndex(i);
                if (shaders.Contains(prop.objectReferenceValue as Shader))
                {
                    prop.objectReferenceValue = null;   // object-ref arrays: null then delete
                    arr.DeleteArrayElementAtIndex(i);
                }
            }

            so.ApplyModifiedProperties();
        }
    }
}
