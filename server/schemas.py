"""Pydantic models for the strategist proxy (CLAUDE.md §6.3, §6.4).

The request is split in two:

* ``context`` — everything about the car and the circuit that does NOT change
  during the race (pilot profile, numbered corner map, lap count). The client
  sends the *same* bytes on every call for a given car so Ollama reuses the
  KV-cache of that prefix (§6.7). The server is stateless (§2.2): it does not
  remember the context between calls, it just places it first in the prompt.
* ``telemetry`` — the variable part of §6.3 (event, classification, gaps,
  lap-over-lap notes).

The response is the discrete-level directive of §6.4. Levels are enums, never
free 0..1 floats: a 3B model is not consistent on a continuous scale, and the
Fase 6.3 comparison needs the same buckets across runs.
"""

from __future__ import annotations

from typing import Literal, Optional

from pydantic import BaseModel, Field

EventKind = Literal[
    "lap_completed",
    "rival_in_range",
    "position_change",
    "incident",
    "final_lap",
]
DirectiveKind = Literal["attack", "defend", "conserve", "push"]
Level = Literal["low", "medium", "high"]
Trend = Literal["closing", "stable", "dropping"]
CornerDir = Literal["left", "right"]
CornerSeverity = Literal["hairpin", "slow", "medium", "fast"]


class Corner(BaseModel):
    index: int = Field(ge=1)
    direction: CornerDir
    severity: CornerSeverity


class RaceContext(BaseModel):
    """Stable per-car prefix context (§6.7). Identical across a car's calls."""

    car_id: str
    pilot_profile: str = Field(max_length=400)
    track_name: str = Field(max_length=80)
    track_length_m: float = Field(gt=0)
    total_laps: int = Field(ge=1)
    corners: list[Corner] = Field(default_factory=list, max_length=40)


class SelfState(BaseModel):
    car_id: str
    position: int = Field(ge=1)
    last_lap_time: Optional[float] = None
    best_lap_time: Optional[float] = None
    gap_ahead: Optional[float] = None  # seconds to the car ahead, None if leader
    gap_behind: Optional[float] = None  # seconds to the car behind, None if last
    current_directive: DirectiveKind
    incidents: int = Field(default=0, ge=0)


class RivalState(BaseModel):
    car_id: str
    position: int = Field(ge=1)
    gap: float  # signed seconds: negative = ahead of me, positive = behind me
    last_lap_time: Optional[float] = None
    trend: Trend = "stable"


class Telemetry(BaseModel):
    """The variable part of the payload (§6.3)."""

    event: EventKind
    lap: int = Field(ge=0)
    laps_remaining: int = Field(ge=0)
    me: SelfState
    rivals: list[RivalState] = Field(default_factory=list, max_length=8)
    notes: list[str] = Field(default_factory=list, max_length=8)


class StrategyRequest(BaseModel):
    context: RaceContext
    telemetry: Telemetry


class StrategyResponse(BaseModel):
    """The strategist's directive (§6.4). This is also the JSON Schema handed to
    Ollama as ``format`` so the model is constrained to emit exactly this shape."""

    directive: DirectiveKind
    aggression: Level
    risk_tolerance: Level
    target_rival: Optional[str] = None  # a known car_id, or null
    focus_corners: list[int] = Field(default_factory=list, max_length=4)
    radio: str = Field(max_length=200)  # ~15 words; hard-clamped client-side (§6.8)
    rationale: str = Field(max_length=400)  # ~40 words; stored for Fase 6.1


class LlmStatus(BaseModel):
    mode: Literal["online", "offline"]
    p95_ms: int
    calls: int  # Ollama calls attempted this process
    rejected: int  # responses that failed schema validation (§6.8)
    failed: int  # transport errors / timeouts
    inflight: int


class StrategyEnvelope(BaseModel):
    """Always HTTP 200. ``status`` tells the client what to do:

    * ``ok``        — apply ``strategy``.
    * ``fallback``  — keep the current directive / use the local heuristic
      default. ``reason`` says why (rejected, offline, busy, ollama_error,
      timeout) and is a data point for the technical post (§6.8).
    """

    status: Literal["ok", "fallback"]
    strategy: Optional[StrategyResponse] = None
    reason: Optional[str] = None
    latency_ms: int = 0
    llm: LlmStatus
