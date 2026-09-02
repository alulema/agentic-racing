using AgenticRacing.Diagnostics;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AgenticRacing.EditorTools
{
    /// <summary>
    /// Headless (-batchmode -executeMethod) entry point for the Fase 0 risk
    /// spike: opens SampleScene, wires the ONNX + interop smoke test
    /// GameObject into it, and builds to Web. Not part of the game's build
    /// pipeline — this is a throwaway diagnostic runner for Fase 0 only.
    /// </summary>
    public static class Fase0BatchBuild
    {
        private const string ScenePath = "Assets/Scenes/SampleScene.unity";
        // Output name must match BUILD_NAME in web/index.html ("web-test") so the
        // DOM shell finds Build/web-test.* — Unity names the player files after
        // the last path segment of locationPathName.
        private const string OutputDir = "Builds/web-test";

        public static void Build()
        {
            EditorSceneManager.OpenScene(ScenePath);

            var existing = GameObject.Find("Fase0SmokeTest");
            if (existing != null) Object.DestroyImmediate(existing);
            var go = new GameObject("Fase0SmokeTest");
            go.AddComponent<OnnxSmokeTest>();

            var scene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = OutputDir,
                target = BuildTarget.WebGL,
                options = BuildOptions.None,
            };

            var report = BuildPipeline.BuildPlayer(options);
            var summary = report.summary;
            Debug.Log($"[Fase0BatchBuild] result={summary.result} totalErrors={summary.totalErrors} size={summary.totalSize}");

            if (summary.result != BuildResult.Succeeded)
            {
                EditorApplication.Exit(1);
            }
        }
    }
}
