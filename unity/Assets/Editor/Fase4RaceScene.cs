using System.IO;
using AgenticRacing.Agents;
using AgenticRacing.Track;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AgenticRacing.EditorTools
{
    /// <summary>
    /// Fase 4 race scene (CLAUDE.md §6). Everything is built at runtime by
    /// <see cref="RaceSceneBootstrap"/>, so the scene asset is one object with
    /// <see cref="TrackConfig"/> + that bootstrap.
    ///
    ///   Unity.exe -batchmode -quit -projectPath unity `
    ///     -executeMethod AgenticRacing.EditorTools.Fase4RaceScene.Setup -logFile -
    ///
    /// <see cref="Setup"/> materialises <c>Assets/Scenes/Race.unity</c> (commit it
    /// and its .meta). <see cref="BuildWebGL"/> is a local, uncompressed
    /// verification build only — CI's pipeline keeps Brotli and the .br/.gz path
    /// validated in Fase 0.
    /// </summary>
    public static class Fase4RaceScene
    {
        private const string ScenePath = "Assets/Scenes/Race.unity";
        private const string OutputDir = "Builds/race-demo";

        public static void Setup()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var go = new GameObject("Race");
            go.AddComponent<TrackConfig>();
            go.AddComponent<RaceSceneBootstrap>();
            EditorSceneManager.MarkSceneDirty(scene);
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log($"[Fase4RaceScene] wrote {ScenePath}");

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        public static void BuildWebGL()
        {
            Setup();

            string prevTemplate = PlayerSettings.WebGL.template;
            var prevCompression = PlayerSettings.WebGL.compressionFormat;
            bool prevDataCaching = PlayerSettings.WebGL.dataCaching;
            bool prevRunInBackground = PlayerSettings.runInBackground;

            BuildReport report;
            try
            {
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
            Debug.Log($"[Fase4RaceScene] BuildWebGL result={s.result} errors={s.totalErrors} " +
                      $"size={s.totalSize} bytes -> {OutputDir}");

            if (Application.isBatchMode)
                EditorApplication.Exit(s.result == BuildResult.Succeeded ? 0 : 1);
        }
    }
}
