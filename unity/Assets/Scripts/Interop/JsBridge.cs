using System.Runtime.InteropServices;
using UnityEngine;

namespace AgenticRacing.Interop
{
    /// <summary>
    /// C# side of the Unity -> DOM bridge (see WebGLBridge.jslib). Outside
    /// WebGL builds this is a no-op logged to the console, so the same
    /// calling code works in the Editor and in standalone test runs.
    /// </summary>
    public static class JsBridge
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void SendToDom(string message);
#endif

        public static void Send(string message)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            SendToDom(message);
#else
            Debug.Log($"[JsBridge] (no-op outside WebGL) {message}");
#endif
        }
    }
}
