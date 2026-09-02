using System.Collections.Generic;
using System.IO;
using AgenticRacing.Track;
using UnityEditor;
using UnityEngine;

namespace AgenticRacing.EditorTools
{
    /// <summary>
    /// Headless top-down PNG of a generated circuit, drawn by rasterising the
    /// track data directly into a <see cref="Texture2D"/> (no scene, camera or
    /// lighting needed). Shows the asphalt ribbon, centerline, reference racing
    /// line, start/finish line and numbered corner apexes.
    ///
    /// -batchmode -executeMethod AgenticRacing.EditorTools.Fase1SceneRender.Render
    ///   optional args after "--":  -seed N   -out path.png   -size PX
    /// </summary>
    public static class Fase1SceneRender
    {
        public static void Render()
        {
            int seed = ArgInt("-seed", 12345);
            int size = ArgInt("-size", 1400);
            string outPath = ArgString("-out", $"Builds/track_{seed}.png");

            TrackData track = TrackGenerator.Generate(seed);
            var tex = Rasterise(track, size);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
            File.WriteAllBytes(outPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);

            Debug.Log($"[Fase1SceneRender] seed {track.EffectiveSeed}: {track.Length:F0} m, " +
                      $"{track.Corners.Count} corners, wrote {outPath}");

            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        private static Texture2D Rasterise(TrackData track, int size)
        {
            const int margin = 40;
            var center = track.Centerline;

            // World bounds of the whole ribbon.
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var v in center)
            {
                minX = Mathf.Min(minX, v.x); maxX = Mathf.Max(maxX, v.x);
                minZ = Mathf.Min(minZ, v.z); maxZ = Mathf.Max(maxZ, v.z);
            }
            float pad = track.Width;
            minX -= pad; maxX += pad; minZ -= pad; maxZ += pad;

            float worldSpan = Mathf.Max(maxX - minX, maxZ - minZ);
            float scale = (size - 2 * margin) / worldSpan;

            // Texture2D has a bottom-left origin, so world +Z maps straight to
            // pixel +Y and north ends up at the top of the PNG with no flip.
            Vector2Int ToPix(Vector3 w)
            {
                int px = margin + Mathf.RoundToInt((w.x - minX) * scale);
                int py = margin + Mathf.RoundToInt((w.z - minZ) * scale);
                return new Vector2Int(px, py);
            }

            var px32 = new Color32[size * size];
            var bg = new Color32(24, 26, 30, 255);
            for (int i = 0; i < px32.Length; i++) px32[i] = bg;

            void Plot(int x, int y, Color32 c)
            {
                if ((uint)x >= (uint)size || (uint)y >= (uint)size) return;
                px32[y * size + x] = c;
            }

            void Disc(Vector2Int p, int r, Color32 c)
            {
                for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                    if (dx * dx + dy * dy <= r * r) Plot(p.x + dx, p.y + dy, c);
            }

            void Line(Vector2Int a, Vector2Int b, int thick, Color32 c)
            {
                int dx = Mathf.Abs(b.x - a.x), dy = Mathf.Abs(b.y - a.y);
                int sx = a.x < b.x ? 1 : -1, sy = a.y < b.y ? 1 : -1;
                int err = dx - dy;
                int x = a.x, y = a.y;
                int rad = Mathf.Max(0, thick / 2);
                while (true)
                {
                    Disc(new Vector2Int(x, y), rad, c);
                    if (x == b.x && y == b.y) break;
                    int e2 = 2 * err;
                    if (e2 > -dy) { err -= dy; x += sx; }
                    if (e2 < dx) { err += dx; y += sy; }
                }
            }

            void Poly(IReadOnlyList<Vector3> loop, int thick, Color32 c, int stride = 1)
            {
                for (int i = 0; i < loop.Count; i += stride)
                {
                    int j = (i + stride) % loop.Count;
                    Line(ToPix(loop[i]), ToPix(loop[j]), thick, c);
                }
            }

            // Asphalt: one thick pass along the centerline ~= the ribbon width.
            int ribbonPx = Mathf.Max(3, Mathf.RoundToInt(track.Width * scale));
            Poly(center, ribbonPx, new Color32(60, 63, 68, 255));

            // Centerline (thin, light) and reference racing line (cyan).
            Poly(center, 1, new Color32(150, 150, 155, 255));
            if (track.RacingLine != null)
                Poly(track.RacingLine, Mathf.Max(2, ribbonPx / 8), new Color32(80, 200, 220, 255));

            // Start / finish line.
            Vector3 sideDir = Vector3.Cross(Vector3.up, track.StartDirection).normalized * (track.Width * 0.5f);
            Line(ToPix(track.StartPosition - sideDir), ToPix(track.StartPosition + sideDir),
                 Mathf.Max(2, ribbonPx / 6), new Color32(90, 220, 120, 255));

            // Numbered corner apexes.
            foreach (var corner in track.Corners)
            {
                Vector2Int p = ToPix(corner.ApexPosition(track));
                Color32 c = corner.Direction == CornerDirection.Left
                    ? new Color32(220, 100, 220, 255)
                    : new Color32(230, 90, 90, 255);
                Disc(p, Mathf.Max(3, ribbonPx / 4), c);
                int off = Mathf.Max(10, ribbonPx / 2);
                DrawLabel(Plot, p + new Vector2Int(off, -8), $"T{corner.Index}",
                          new Color32(245, 245, 245, 255), 3);
            }

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.SetPixels32(px32);
            tex.Apply();
            return tex;
        }

        // --- tiny 3x5 bitmap font, digits + 'T' -----------------------------------

        private delegate void PlotFn(int x, int y, Color32 c);

        private static void DrawLabel(PlotFn plot, Vector2Int origin, string text, Color32 c, int scale = 3)
        {
            var shadow = new Color32(12, 12, 15, 255);
            int cx = origin.x;
            foreach (char ch in text)
            {
                string[] rows = Glyph(ch);
                if (rows != null)
                {
                    for (int row = 0; row < 5; row++)
                    for (int col = 0; col < 3; col++)
                        if (rows[row][col] == '#')
                            for (int sy = 0; sy < scale; sy++)
                            for (int sx = 0; sx < scale; sx++)
                            {
                                int x = cx + col * scale + sx;
                                // row 0 is the visual top; pixel +Y is up.
                                int y = origin.y + (4 - row) * scale + sy;
                                plot(x + 1, y - 1, shadow);
                                plot(x, y, c);
                            }
                }
                cx += 4 * scale;
            }
        }

        private static string[] Glyph(char ch) => ch switch
        {
            '0' => new[] { "###", "# #", "# #", "# #", "###" },
            '1' => new[] { " # ", "## ", " # ", " # ", "###" },
            '2' => new[] { "###", "  #", "###", "#  ", "###" },
            '3' => new[] { "###", "  #", "###", "  #", "###" },
            '4' => new[] { "# #", "# #", "###", "  #", "  #" },
            '5' => new[] { "###", "#  ", "###", "  #", "###" },
            '6' => new[] { "###", "#  ", "###", "# #", "###" },
            '7' => new[] { "###", "  #", " # ", " # ", " # " },
            '8' => new[] { "###", "# #", "###", "# #", "###" },
            '9' => new[] { "###", "# #", "###", "  #", "###" },
            'T' => new[] { "###", " # ", " # ", " # ", " # " },
            _ => null,
        };

        private static string[] Args()
        {
            var raw = System.Environment.GetCommandLineArgs();
            var list = new List<string>();
            bool after = false;
            foreach (var a in raw)
            {
                if (after) list.Add(a);
                if (a == "--") after = true;
            }
            return list.ToArray();
        }

        private static int ArgInt(string name, int fallback)
        {
            var a = Args();
            for (int i = 0; i < a.Length - 1; i++)
                if (a[i] == name && int.TryParse(a[i + 1], out int v)) return v;
            return fallback;
        }

        private static string ArgString(string name, string fallback)
        {
            var a = Args();
            for (int i = 0; i < a.Length - 1; i++)
                if (a[i] == name) return a[i + 1];
            return fallback;
        }
    }
}
