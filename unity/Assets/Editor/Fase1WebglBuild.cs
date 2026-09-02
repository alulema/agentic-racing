using System.IO;
using AgenticRacing.Demo;
using AgenticRacing.Track;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

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
    /// few WebGL PlayerSettings and restores them afterwards, so it must not leave
    /// ProjectSettings.asset dirty — CI's build keeps Brotli and the .br/.gz path
    /// that Fase 0 validated.
    /// </summary>
    public static class Fase1WebglBuild
    {
        private const string ScenePath = "Assets/Scenes/TrackDemo.unity";
        private const string OutputDir = "Builds/track-demo";

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

            BuildReport report;
            try
            {
                // "APPLICATION:Default" is the built-in template this project uses.
                PlayerSettings.WebGL.template = "APPLICATION:Default";
                PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
                PlayerSettings.WebGL.dataCaching = false;
                PlayerSettings.runInBackground = true;

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
                AssetDatabase.SaveAssets();
            }

            var s = report.summary;
            Debug.Log($"[Fase1WebglBuild] result={s.result} errors={s.totalErrors} " +
                      $"size={s.totalSize} bytes -> {OutputDir}");

            if (Application.isBatchMode)
                EditorApplication.Exit(s.result == BuildResult.Succeeded ? 0 : 1);
        }
    }
}
