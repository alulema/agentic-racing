using System;
using System.Globalization;
using UnityEngine;

namespace AgenticRacing.Track
{
    /// <summary>
    /// Resolves the race seed and lap count. Query params on the hosting page
    /// (<c>?seed=12345&amp;laps=5</c>) win; otherwise the serialized fallback
    /// values are used, which also makes the component usable in the Editor and
    /// in EditMode tests (CLAUDE.md Fase 1).
    /// </summary>
    public sealed class TrackConfig : MonoBehaviour
    {
        [SerializeField] private int fallbackSeed = 12345;
        [SerializeField, Min(1)] private int fallbackLaps = 5;

        [Header("Resolved (read-only at runtime)")]
        [SerializeField] private int resolvedSeed;
        [SerializeField] private int resolvedLaps;

        public int Seed => resolvedSeed;
        public int Laps => resolvedLaps;

        private void Awake() => Resolve();

        /// <summary>Re-reads the URL. Safe to call from the Editor.</summary>
        public void Resolve()
        {
            string url = Application.absoluteURL;
            resolvedSeed = TryGetQueryInt(url, "seed", out int s) ? s : fallbackSeed;
            resolvedLaps = TryGetQueryInt(url, "laps", out int l) && l >= 1 ? l : fallbackLaps;
        }

        internal static bool TryGetQueryInt(string url, string key, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(url)) return false;

            int q = url.IndexOf('?');
            if (q < 0 || q == url.Length - 1) return false;

            string query = url.Substring(q + 1);
            int hash = query.IndexOf('#');
            if (hash >= 0) query = query.Substring(0, hash);

            foreach (string pair in query.Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string name = eq >= 0 ? pair.Substring(0, eq) : pair;
                if (!string.Equals(name, key, StringComparison.OrdinalIgnoreCase)) continue;

                string raw = eq >= 0 ? pair.Substring(eq + 1) : string.Empty;
                raw = Uri.UnescapeDataString(raw).Trim();
                return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            }

            return false;
        }
    }
}
