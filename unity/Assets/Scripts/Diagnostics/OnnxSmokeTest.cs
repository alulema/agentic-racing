using System.Diagnostics;
using System.Globalization;
using AgenticRacing.Interop;
using Unity.InferenceEngine;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace AgenticRacing.Diagnostics
{
    /// <summary>
    /// Fase 0 risk spike: loads a trivial ONNX model (3 inputs -> Dense(3,2)
    /// + ReLU -> 2 outputs, see Assets/Resources/Diagnostics/OnnxSmokeTest.onnx)
    /// with com.unity.ai.inference and runs one inference pass. Reports the
    /// result and timing both to the console and to the DOM overlay via
    /// JsBridge, so we can confirm inside the actual WebGL build (not just
    /// the Editor) that: the package loads, CPU backend works in WebGL, and
    /// the Unity -> DOM interop direction works. See CLAUDE.md sección 11,
    /// "Inference Engine + WebGL" y "Backend de inferencia en WebGL".
    /// </summary>
    public class OnnxSmokeTest : MonoBehaviour
    {
        private const string ModelResourcePath = "Diagnostics/OnnxSmokeTest";

        // Reference output for this input, computed by hand in make_toy_onnx.py:
        // Gemm([1,2,3]) + B -> [2.6, 3.4] -> ReLU -> [2.6, 3.4] (unchanged, both positive).
        private static readonly float[] TestInput = { 1f, 2f, 3f };

        private Worker _worker;

        private void Start()
        {
            RunInference();
        }

        /// <summary>
        /// Re-runs inference. Public so the DOM overlay can trigger it via
        /// unityInstance.SendMessage("Fase0SmokeTest", "RunInference") to
        /// validate the DOM -> Unity interop direction too.
        /// </summary>
        public void RunInference()
        {
            var modelAsset = Resources.Load<ModelAsset>(ModelResourcePath);
            if (modelAsset == null)
            {
                Report($"onnx_error:model not found at Resources/{ModelResourcePath}", isError: true);
                return;
            }

            // CPU first: don't assume GPUCompute is available in WebGL (see
            // CLAUDE.md riesgo "Backend de inferencia en WebGL").
            const BackendType backend = BackendType.CPU;

            var model = ModelLoader.Load(modelAsset);
            _worker?.Dispose();
            _worker = new Worker(model, backend);

            using var input = new Tensor<float>(new TensorShape(1, TestInput.Length), TestInput);

            var stopwatch = Stopwatch.StartNew();
            _worker.Schedule(input);
            var output = _worker.PeekOutput() as Tensor<float>;
            var result = output != null ? output.DownloadToArray() : System.Array.Empty<float>();
            stopwatch.Stop();

            // InvariantCulture so the diagnostic string stays machine-parseable
            // (a locale that uses ',' as decimal separator would break "F3"/"F2").
            var resultStr = string.Join(",", System.Array.ConvertAll(
                result, v => v.ToString("F3", CultureInfo.InvariantCulture)));
            var ms = stopwatch.Elapsed.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture);
            Report($"onnx_ok:backend={backend},ms={ms},output=[{resultStr}]");
        }

        private void Report(string message, bool isError = false)
        {
            if (isError) Debug.LogError(message);
            else Debug.Log(message);
            JsBridge.Send(message);
        }

        private void OnDestroy()
        {
            _worker?.Dispose();
        }
    }
}
