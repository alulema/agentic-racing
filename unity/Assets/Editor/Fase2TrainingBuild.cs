using System.IO;
using AgenticRacing.Agents;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AgenticRacing.EditorTools
{
    /// <summary>
    /// Linux player build of the ML-Agents training scene, for the Azure spot VM
    /// (CLAUDE.md §2.3). It is a normal StandaloneLinux64 player (only "Linux
    /// Build Support (IL2CPP)" is required, §9 — NOT the Dedicated Server module)
    /// that the VM runs headless: `mlagents-learn --env=... --no-graphics`.
    ///
    ///   -batchmode -nographics -quit -executeMethod \
    ///     AgenticRacing.EditorTools.Fase2TrainingBuild.Build
    /// </summary>
    public static class Fase2TrainingBuild
    {
        private const string ScenePath = "Assets/Scenes/TrainArena.unity";
        private const string OutputDir = "Builds/train-linux";
        private const string OutputName = "train.x86_64";

        public static void Build()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var go = new GameObject("Training");
            go.AddComponent<TrainingSceneBootstrap>();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);

            // Force the plain Player subtarget: a stale "Server" value persists in
            // EditorUserBuildSettings and needs the Dedicated Server module.
            EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Player;

            if (Directory.Exists(OutputDir)) Directory.Delete(OutputDir, true);
            Directory.CreateDirectory(OutputDir);

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = Path.Combine(OutputDir, OutputName),
                target = BuildTarget.StandaloneLinux64,
                subtarget = (int)StandaloneBuildSubtarget.Player,
                options = BuildOptions.None,
            });

            var s = report.summary;
            Debug.Log($"[Fase2TrainingBuild] result={s.result} errors={s.totalErrors} " +
                      $"size={s.totalSize} -> {OutputDir}/{OutputName}");

            if (Application.isBatchMode)
                EditorApplication.Exit(s.result == BuildResult.Succeeded ? 0 : 1);
        }
    }
}
