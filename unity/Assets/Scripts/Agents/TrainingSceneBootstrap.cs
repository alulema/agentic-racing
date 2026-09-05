using UnityEngine;

namespace AgenticRacing.Agents
{
    /// <summary>
    /// Spawns a grid of <see cref="TrainingArena"/>s, each with a distinct
    /// circuit seed and spaced far enough apart that their rays never see a
    /// neighbour. One scene with this component is the whole training build; run
    /// several such builds with <c>--num-envs</c> on the VM for more parallelism
    /// (CLAUDE.md §2.3).
    /// </summary>
    public sealed class TrainingSceneBootstrap : MonoBehaviour
    {
        [SerializeField, Min(1)] private int arenaCount = 9;
        [SerializeField] private int baseSeed = 1000;
        [SerializeField] private float spacing = 4000f;

        private void Awake()
        {
            int cols = Mathf.CeilToInt(Mathf.Sqrt(arenaCount));
            for (int i = 0; i < arenaCount; i++)
            {
                int r = i / cols;
                int c = i % cols;
                var go = new GameObject($"Arena_{i}");
                // Inactive so we can set the seed before TrainingArena.Awake runs.
                go.SetActive(false);
                go.transform.SetParent(transform, false);
                go.transform.position = new Vector3(c * spacing, 0f, r * spacing);
                go.AddComponent<TrainingArena>().SetSeed(baseSeed + i);
                go.SetActive(true);
            }
        }
    }
}
