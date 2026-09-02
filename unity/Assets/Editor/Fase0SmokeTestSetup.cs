using AgenticRacing.Diagnostics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AgenticRacing.EditorTools
{
    /// <summary>
    /// One-click setup for the Fase 0 risk spike: creates (or resets) a
    /// GameObject named "Fase0SmokeTest" with the OnnxSmokeTest component in
    /// the currently open scene, and saves the scene. Run this once before
    /// building to Web to validate ONNX inference + DOM interop together.
    /// </summary>
    public static class Fase0SmokeTestSetup
    {
        private const string GameObjectName = "Fase0SmokeTest";

        [MenuItem("Agentic Racing/Fase 0/Setup ONNX + Interop Smoke Test")]
        public static void Setup()
        {
            var existing = GameObject.Find(GameObjectName);
            if (existing != null) Object.DestroyImmediate(existing);

            var go = new GameObject(GameObjectName);
            go.AddComponent<OnnxSmokeTest>();

            var scene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            Debug.Log($"[Fase0SmokeTestSetup] '{GameObjectName}' created in '{scene.name}' and scene saved.");
        }
    }
}
