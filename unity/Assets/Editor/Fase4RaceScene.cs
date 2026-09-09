using System.Collections.Generic;
using System.IO;
using AgenticRacing.Agents;
using AgenticRacing.Track;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

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
    /// and its .meta). <see cref="BuildWebGL"/> builds that scene for WebGL and
    /// merges the player into <c>/web</c> next to the DOM-overlay
    /// <c>index.html</c>, renaming the files to the <c>web-test</c> prefix
    /// <c>web/app.js</c> expects — a local stand-in for CI's assemble step, so a
    /// Windows build can be served straight off <c>/web</c> by the FastAPI proxy
    /// (<c>STATIC_DIR=&lt;repo&gt;/web</c>). Brotli-compressed, like CI.
    /// </summary>
    public static class Fase4RaceScene
    {
        private const string ScenePath = "Assets/Scenes/Race.unity";
        private const string OutputDir = "Builds/race-demo";
        private const string WebBuildName = "web-test";   // must match web/app.js BUILD_NAME

        // RaceSceneBootstrap builds its materials at runtime via Shader.Find, so
        // nothing references these at build time and Unity strips their variants
        // -> magenta track in the player. Force them into Always Included Shaders
        // for the build, then restore (same as Fase1WebglBuild).
        private static readonly string[] ForceIncludeShaders =
        {
            "Universal Render Pipeline/Unlit",
            "Universal Render Pipeline/Lit",
            "Sprites/Default",
        };

        public static void Setup()
        {
            WriteScene();
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        /// <summary>Create and save the one-object race scene. Kept separate from
        /// <see cref="Setup"/> so <see cref="BuildWebGL"/> can reuse it without
        /// the batch-mode <c>EditorApplication.Exit</c> that ends <see cref="Setup"/>.</summary>
        private static void WriteScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var go = new GameObject("Race");
            go.AddComponent<TrackConfig>();
            go.AddComponent<RaceSceneBootstrap>();
            EditorSceneManager.MarkSceneDirty(scene);
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log($"[Fase4RaceScene] wrote {ScenePath}");
        }

        public static void BuildWebGL()
        {
            WriteScene();

            string prevTemplate = PlayerSettings.WebGL.template;
            var prevCompression = PlayerSettings.WebGL.compressionFormat;
            bool prevDataCaching = PlayerSettings.WebGL.dataCaching;
            bool prevRunInBackground = PlayerSettings.runInBackground;
            List<Shader> addedShaders = null;

            BuildReport report;
            try
            {
                PlayerSettings.WebGL.template = "APPLICATION:Default";
                PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Brotli;
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
            Debug.Log($"[Fase4RaceScene] BuildWebGL result={s.result} errors={s.totalErrors} " +
                      $"size={s.totalSize} bytes -> {OutputDir}");

            if (s.result == BuildResult.Succeeded)
                MergeIntoWeb();

            if (Application.isBatchMode)
                EditorApplication.Exit(s.result == BuildResult.Succeeded ? 0 : 1);
        }

        /// <summary>
        /// Copy the player's <c>Build/</c> (and <c>StreamingAssets/</c> if any)
        /// into <c>&lt;repo&gt;/web/</c>, renaming the <c>&lt;OutputDir name&gt;.*</c>
        /// prefix to <c>web-test.*</c> so <c>web/app.js</c> loads it as-is. Mirrors
        /// CI's "Assemble /web static root" step; keeps our own index.html.
        /// </summary>
        private static void MergeIntoWeb()
        {
            string repo = Directory.GetParent(Application.dataPath)!.Parent!.FullName;
            string webDir = Path.Combine(repo, "web");
            string srcBuild = Path.Combine(OutputDir, "Build");
            string dstBuild = Path.Combine(webDir, "Build");

            if (!Directory.Exists(srcBuild))
            {
                Debug.LogWarning($"[Fase4RaceScene] {srcBuild} not found — nothing merged into /web.");
                return;
            }

            string srcPrefix = Path.GetFileName(OutputDir);   // "race-demo"
            if (Directory.Exists(dstBuild)) Directory.Delete(dstBuild, true);
            Directory.CreateDirectory(dstBuild);

            foreach (string file in Directory.GetFiles(srcBuild))
            {
                string name = Path.GetFileName(file);
                string renamed = name.StartsWith(srcPrefix + ".")
                    ? WebBuildName + name.Substring(srcPrefix.Length)
                    : name;
                File.Copy(file, Path.Combine(dstBuild, renamed), true);
            }

            string srcStreaming = Path.Combine(OutputDir, "StreamingAssets");
            if (Directory.Exists(srcStreaming))
            {
                string dstStreaming = Path.Combine(webDir, "StreamingAssets");
                if (Directory.Exists(dstStreaming)) Directory.Delete(dstStreaming, true);
                CopyDir(srcStreaming, dstStreaming);
            }

            int n = Directory.GetFiles(dstBuild).Length;
            Debug.Log($"[Fase4RaceScene] merged {n} player files into {dstBuild} " +
                      $"(prefix {srcPrefix}.* -> {WebBuildName}.*). Serve with STATIC_DIR={webDir}");
        }

        private static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (string d in Directory.GetDirectories(src))
                CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
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
                    Debug.LogWarning($"[Fase4RaceScene] shader not found, cannot force-include: {name}");
                    continue;
                }

                bool present = false;
                for (int i = 0; i < arr.arraySize; i++)
                    if (arr.GetArrayElementAtIndex(i).objectReferenceValue == sh) { present = true; break; }
                if (present) continue;

                arr.arraySize++;
                arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = sh;
                added.Add(sh);
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
