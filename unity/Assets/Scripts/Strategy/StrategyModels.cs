using System;
using System.Collections.Generic;
using AgenticRacing.Vehicle;

namespace AgenticRacing.Strategy
{
    /// <summary>
    /// Stable per-car context sent on every call (CLAUDE.md §6.7): the pit wall's
    /// role is fixed, so is the circuit map and the pilot profile. The server
    /// puts this first in the prompt; identical bytes across a car's calls let
    /// Ollama reuse the KV-cache while the model stays loaded. Built once per car
    /// by the race scene.
    /// </summary>
    public sealed class StrategyContext
    {
        public string CarId;
        public string PilotProfile;
        public string TrackName;
        public float TrackLengthM;
        public int TotalLaps;
        public IReadOnlyList<CornerInfo> Corners = Array.Empty<CornerInfo>();
    }

    /// <summary>One numbered corner for the circuit map (§2.1: turn numbers are a
    /// stable shared reference between LLM, pilot and viewer).</summary>
    public readonly struct CornerInfo
    {
        public readonly int Index;
        public readonly string Direction; // "left" | "right"
        public readonly string Severity;  // "hairpin" | "slow" | "medium" | "fast"

        public CornerInfo(int index, string direction, string severity)
        {
            Index = index;
            Direction = direction;
            Severity = severity;
        }
    }

    // --- response DTOs (parsed with JsonUtility.FromJson) ---------------------
    //
    // Shape mirrors server/schemas.py StrategyEnvelope. JsonUtility can't do
    // nullable numbers, but the response has none: enums arrive as strings,
    // target_rival as a string ("" when the model chose null), focus_corners as
    // an int array. `strategy` is null on a fallback envelope.

    [Serializable]
    public sealed class StrategyEnvelopeDto
    {
        public string status;              // "ok" | "fallback"
        public StrategyResponseDto strategy; // null unless status == "ok"
        public string reason;              // why fallback (rejected/offline/busy/...)
        public int latency_ms;
        public LlmStatusDto llm;
    }

    [Serializable]
    public sealed class StrategyResponseDto
    {
        public string directive;        // attack | defend | conserve | push
        public string aggression;       // low | medium | high
        public string risk_tolerance;   // low | medium | high
        public string target_rival;     // a car_id, or ""
        public int[] focus_corners;
        public string radio;            // <= 15 words
        public string rationale;        // <= 40 words, stored for Fase 6.1
    }

    [Serializable]
    public sealed class LlmStatusDto
    {
        public string mode;   // "online" | "offline"
        public int p95_ms;
        public int calls;
        public int rejected;
        public int failed;
        public int inflight;
    }

    /// <summary>Parsed + validated result the strategist acts on. Either a valid
    /// directive to apply, or a reason to keep the current one (§6.8).</summary>
    public readonly struct StrategyDecision
    {
        public readonly bool Applied;
        public readonly RaceDirective Directive;   // valid only when Applied
        public readonly string TargetRival;
        public readonly int[] FocusCorners;
        public readonly string Radio;
        public readonly string Rationale;
        public readonly string Reason;             // why not applied, or "ok"
        public readonly int LatencyMs;

        public StrategyDecision(bool applied, RaceDirective directive, string targetRival,
            int[] focusCorners, string radio, string rationale, string reason, int latencyMs)
        {
            Applied = applied;
            Directive = directive;
            TargetRival = targetRival;
            FocusCorners = focusCorners ?? Array.Empty<int>();
            Radio = radio;
            Rationale = rationale;
            Reason = reason;
            LatencyMs = latencyMs;
        }

        public static StrategyDecision Keep(string reason, int latencyMs = 0) =>
            new StrategyDecision(false, default, null, null, null, null, reason, latencyMs);
    }
}
