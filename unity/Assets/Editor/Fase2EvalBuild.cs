using System;
using System.IO;
using System.Linq;
using AgenticRacing.Agents;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AgenticRacing.EditorTools
{
    /// <summary>
    /// Builds an offline eval player: the same arena grid as training, but every
    /// agent runs a baked <c>.onnx</c> in InferenceOnly for a fixed window and then
    /// logs an aggregate report (<see cref="EvalRunner"/>). Lets us see what a
    /// trained policy actually does without the Editor.
    ///
    ///   Unity.exe -batchmode -quit -projectPath unity `
    ///     -executeMethod AgenticRacing.EditorTools.Fase2EvalBuild.Build `
    ///     -logFile eval-build.log -evalModel &lt;path-to.onnx&gt;
    ///
    /// The model path comes from <c>-evalModel &lt;path&gt;</c>, else the
    /// <c>AGENTIC_EVAL_MODEL</c> env var, else <c>results/race04/RaceAgent.onnx</c>.
    /// Then: <c>Builds/eval-windows/eval.exe -logFile eval.log</c> and read the
    /// <c>[Eval] REPORT</c> line.
    /// </summary>
    public static class Fase2EvalBuild
    {
        private const string ScenePath = "Assets/Scenes/EvalArena.unity";
        private const string ResourceDir = "Assets/Resources/Eval";
        private const string AssetPath = ResourceDir + "/RaceAgent.onnx";
        private const string OutputDir = "Builds/eval-windows";
        private const string OutputName = "eval.exe";

        public static void Build()
        {
            // A model is optional: `eval.exe -heuristic` ignores it. Bake it when
            // it's there, warn and carry on when it isn't.
            var modelSrc = ResolveModelPath();
            if (File.Exists(modelSrc))
            {
                Directory.CreateDirectory(ResourceDir);
                File.Copy(modelSrc, AssetPath, true);
                AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceUpdate);
                AssetDatabase.Refresh();

                if (AssetDatabase.LoadAssetAtPath<ModelAsset>(AssetPath) == null)
                    Debug.LogWarning($"[Fase2EvalBuild] {AssetPath} did not import as a ModelAsset " +
                                     "(is com.unity.ai.inference healthy?). -heuristic will still work.");
                else
                    Debug.Log($"[Fase2EvalBuild] baked model from {modelSrc}");
            }
            else
            {
                Debug.LogWarning($"[Fase2EvalBuild] no model at {modelSrc} — build is -heuristic only.");
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var go = new GameObject("Eval");
            // 12 arenas: an even 2 per member for the Fase 3 -population run
            // (6 members), and harmless for the other eval modes.
            go.AddComponent<TrainingSceneBootstrap>().SetArenaCount(12);
            go.AddComponent<EvalRunner>();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);

            EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Player;
            PlayerSettings.runInBackground = true;
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

            // ML-Agents still spins up its Grpc.Core communicator at startup even
            // in InferenceOnly; keep the native lib next to the exe so that load
            // doesn't error (same quirk as the training build, docs/Devlog.md).
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
            }

            Debug.Log($"[Fase2EvalBuild] result={s.result} errors={s.totalErrors} -> {OutputDir}/{OutputName}");

            if (Application.isBatchMode)
                EditorApplication.Exit(s.result == BuildResult.Succeeded ? 0 : 1);
        }

        private static string ResolveModelPath()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "-evalModel")
                    return Path.GetFullPath(args[i + 1]);

            var env = Environment.GetEnvironmentVariable("AGENTIC_EVAL_MODEL");
            if (!string.IsNullOrEmpty(env))
                return Path.GetFullPath(env);

            // <repo>/unity/Assets -> <repo>
            var repo = Directory.GetParent(Application.dataPath)!.Parent!.FullName;
            return Path.Combine(repo, "results", "race04", "RaceAgent.onnx");
        }
    }
}
