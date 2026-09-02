using System.Text;
using AgenticRacing.Track;
using UnityEditor;
using UnityEngine;

namespace AgenticRacing.EditorTools
{
    /// <summary>
    /// Headless (-batchmode -executeMethod AgenticRacing.EditorTools.Fase1TrackValidator.Run)
    /// sweep over a range of seeds. Prints one row per seed and exits non-zero
    /// if any track fails a Fase 1 acceptance property. This is the check the
    /// agent runs to verify track generation without opening the Editor UI.
    ///
    /// Optional CLI args (after "--"): -seedStart N  -seedCount M
    /// </summary>
    public static class Fase1TrackValidator
    {
        public static void Run()
        {
            int seedStart = ArgInt("-seedStart", 1);
            int seedCount = ArgInt("-seedCount", 200);

            var p = TrackParams.Default;
            var sb = new StringBuilder();
            sb.AppendLine($"[Fase1TrackValidator] seeds {seedStart}..{seedStart + seedCount - 1}  " +
                          $"(len {p.MinLength:F0}-{p.MaxLength:F0} m, min corner {p.MinCornerRadius:F0} m)");
            sb.AppendLine("  seed  | effSeed | tries | length m | minR m | points | ok");
            sb.AppendLine("  ------+---------+-------+----------+--------+--------+----");

            int failures = 0;
            float minRadiusSeen = float.MaxValue;
            float shortest = float.MaxValue, longest = 0f;
            int fellBack = 0;

            for (int k = 0; k < seedCount; k++)
            {
                int seed = seedStart + k;
                string row;
                bool ok;
                try
                {
                    TrackData t = TrackGenerator.Generate(seed, p);
                    ok = t.Length >= p.MinLength - 1f
                         && t.Length <= p.MaxLength + 1f
                         && t.MinCornerRadius >= p.MinCornerRadius
                         && !SelfIntersects(t);

                    if (t.Attempts > 1) fellBack++;
                    minRadiusSeen = Mathf.Min(minRadiusSeen, t.MinCornerRadius);
                    shortest = Mathf.Min(shortest, t.Length);
                    longest = Mathf.Max(longest, t.Length);

                    row = $"  {seed,5} | {t.EffectiveSeed,7} | {t.Attempts,5} | {t.Length,8:F0} | " +
                          $"{t.MinCornerRadius,6:F1} | {t.Centerline.Count,6} | {(ok ? "yes" : "NO")}";
                }
                catch (System.Exception e)
                {
                    ok = false;
                    row = $"  {seed,5} | {"-",7} | {"-",5} | {"-",8} | {"-",6} | {"-",6} | EXC {e.GetType().Name}";
                }

                if (!ok) failures++;
                sb.AppendLine(row);
            }

            sb.AppendLine();
            sb.AppendLine($"  tracks needing a derived seed: {fellBack}/{seedCount}");
            sb.AppendLine($"  length span: {shortest:F0}..{longest:F0} m   tightest corner overall: {minRadiusSeen:F1} m");
            sb.AppendLine(failures == 0
                ? $"[Fase1TrackValidator] PASS — {seedCount} seeds, 0 failures"
                : $"[Fase1TrackValidator] FAIL — {failures}/{seedCount} seeds failed");

            Debug.Log(sb.ToString());

            if (Application.isBatchMode)
                EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        private static bool SelfIntersects(TrackData t)
        {
            var loop = t.Centerline;
            int n = loop.Count;
            for (int i = 0; i < n; i++)
            {
                Vector2 a1 = new Vector2(loop[i].x, loop[i].z);
                Vector2 a2 = new Vector2(loop[(i + 1) % n].x, loop[(i + 1) % n].z);
                for (int j = i + 2; j < n; j++)
                {
                    if (i == 0 && j == n - 1) continue;
                    Vector2 b1 = new Vector2(loop[j].x, loop[j].z);
                    Vector2 b2 = new Vector2(loop[(j + 1) % n].x, loop[(j + 1) % n].z);
                    if (Cross(b1, b2, a1) * Cross(b1, b2, a2) < 0f &&
                        Cross(a1, a2, b1) * Cross(a1, a2, b2) < 0f)
                        return true;
                }
            }
            return false;
        }

        private static float Cross(Vector2 a, Vector2 b, Vector2 c)
            => (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);

        private static int ArgInt(string name, int fallback)
        {
            string[] args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name && int.TryParse(args[i + 1], out int v))
                    return v;
            return fallback;
        }
    }
}
