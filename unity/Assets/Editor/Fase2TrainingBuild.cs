using System.IO;
using System.Linq;
using AgenticRacing.Agents;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AgenticRacing.EditorTools
{
    /// <summary>
    /// Windows player build of the ML-Agents training scene ("Windows Build
    /// Support (Mono)" required, §9 — NOT the Dedicated Server module). It is a
    /// normal StandaloneWindows64 player that a human runs headless:
    /// `mlagents-learn --env=...`.
    ///
    ///   -batchmode -nographics -quit -executeMethod \
    ///     AgenticRacing.EditorTools.Fase2TrainingBuild.Build
    ///
    /// Windows, not Linux: Unity 6 dropped the Mono scripting backend for the
    /// Linux Standalone target (IL2CPP is the only option left there), and
    /// ML-Agents' bundled Grpc.Core communicator cannot run under IL2CPP — its
    /// native log-redirection callback isn't AOT-safe (missing
    /// [MonoPInvokeCallback], throws `System.NotSupportedException` at startup
    /// on IL2CPP, works fine under Mono's JIT). Diagnosed 2026-09-04: see
    /// docs/Devlog.md. IL2CPP itself is unaffected for the WebGL demo build —
    /// this only concerns the training player, which never ships.
    /// </summary>
    public static class Fase2TrainingBuild
    {
        private const string ScenePath = "Assets/Scenes/TrainArena.unity";
        private const string OutputDir = "Builds/train-windows";
        private const string OutputName = "train.exe";

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

            // Force Mono for Standalone: ML-Agents' communicator needs it (see
            // class doc above). Requires "Windows Build Support (Mono)" (§9).
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);

            if (Directory.Exists(OutputDir)) Directory.Delete(OutputDir, true);
            Directory.CreateDirectory(OutputDir);

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = Path.Combine(OutputDir, OutputName),
                target = BuildTarget.StandaloneWindows64,
                subtarget = (int)StandaloneBuildSubtarget.Player,
                options = BuildOptions.None,
            });

            var s = report.summary;

            // Grpc.Core (ML-Agents' communicator) looks for its native library
            // next to the executable; Unity's build pipeline instead places it
            // under <Product>_Data/Plugins/.../grpc_csharp_ext.x64.dll. Copy it
            // up so `mlagents-learn` can actually connect (diagnosed 2026-09-04,
            // docs/Devlog.md — hit this first on the Linux/.so equivalent).
            if (s.result == BuildResult.Succeeded)
            {
                var nativeLib = Directory
                    .GetFiles(OutputDir, "grpc_csharp_ext.x64.dll", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (nativeLib != null)
                {
                    var dest = Path.Combine(OutputDir, Path.GetFileName(nativeLib));
                    if (!File.Exists(dest)) File.Copy(nativeLib, dest);
                }
                else
                {
                    Debug.LogWarning("[Fase2TrainingBuild] grpc_csharp_ext.x64.dll not found under " +
                                      $"{OutputDir} — mlagents-learn will fail to connect without it.");
                }
            }

            Debug.Log($"[Fase2TrainingBuild] result={s.result} errors={s.totalErrors} " +
                      $"size={s.totalSize} -> {OutputDir}/{OutputName}");

            if (Application.isBatchMode)
                EditorApplication.Exit(s.result == BuildResult.Succeeded ? 0 : 1);
        }
    }
}
