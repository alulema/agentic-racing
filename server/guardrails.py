"""Load guardrails for the strategist proxy (CLAUDE.md §7).

With a local LLM there is no per-token bill, so the risk is CPU saturation: 6
strategists queued against one CPU-only Ollama in a 2 vCPU pod. If calls pile
up, the radio arrives minutes late and the pod chokes. These layers keep that
from happening:

1. Global concurrency gate — at most ``MAX_CONCURRENT`` (1-2) calls reach Ollama
   at once. Anyone who can't get a slot quickly is told to fall back (§7.5).
2. Circuit breaker — if recent calls are too slow (p95 over a threshold) or too
   many fail, flip to "offline" for a cooldown: ``/api/strategy`` returns a
   fallback immediately without touching Ollama, and the UI shows "modo
   offline" (§7.1). It does NOT return an error.
3. Per-IP rate limit — the simulation runs client-side, so this only bounds an
   abusive client hitting ``/api/strategy`` by hand (§7.2).

Everything here is process-local, in-memory, and best-effort — matches the
stateless-server rule (§2.2). Counters feed ``/api/health``.
"""

from __future__ import annotations

import asyncio
import time
from collections import deque
from dataclasses import dataclass, field


@dataclass
class _Window:
    """Fixed-size rolling window of recent latency samples (ms)."""

    size: int = 20
    samples: deque[float] = field(default_factory=lambda: deque(maxlen=20))

    def __post_init__(self) -> None:
        self.samples = deque(maxlen=self.size)

    def add(self, ms: float) -> None:
        self.samples.append(ms)

    def p95(self) -> int:
        if not self.samples:
            return 0
        ordered = sorted(self.samples)
        idx = max(0, int(round(0.95 * (len(ordered) - 1))))
        return int(ordered[idx])


class GuardrailState:
    """Owns the semaphore, the breaker, per-IP buckets and the health counters.

    One instance per process, created in ``main``.
    """

    def __init__(
        self,
        *,
        max_concurrent: int = 1,
        slot_wait_s: float = 0.25,
        breaker_p95_ms: int = 20_000,
        breaker_fail_streak: int = 3,
        breaker_cooldown_s: float = 60.0,
        # A full 6-car grid can legitimately fire ~6 calls at once (everyone
        # crosses the line on lap 1 together). Burst covers that; the slow
        # refill still stops a client scripting the endpoint (§7.2). The real
        # throttle is the per-car client-side cooldown (§6.6).
        rate_limit_per_s: float = 1.0,
        rate_burst: int = 12,
    ) -> None:
        self._sema = asyncio.Semaphore(max_concurrent)
        self._slot_wait_s = slot_wait_s
        self._inflight = 0

        self._latencies = _Window(size=20)
        self._breaker_p95_ms = breaker_p95_ms
        self._breaker_fail_streak = breaker_fail_streak
        self._breaker_cooldown_s = breaker_cooldown_s
        self._fail_streak = 0
        self._offline_until = 0.0

        self._rate_per_s = rate_limit_per_s
        self._rate_burst = rate_burst
        self._buckets: dict[str, tuple[float, float]] = {}  # ip -> (tokens, ts)

        # Health counters (cumulative for the life of the process).
        self.calls = 0  # Ollama calls actually attempted
        self.rejected = 0  # responses that failed schema validation (§6.8)
        self.failed = 0  # transport errors / timeouts

    # -- circuit breaker ---------------------------------------------------

    @property
    def offline(self) -> bool:
        return time.monotonic() < self._offline_until

    def _trip(self) -> None:
        self._offline_until = time.monotonic() + self._breaker_cooldown_s

    def record_success(self, latency_ms: float) -> None:
        self._latencies.add(latency_ms)
        self._fail_streak = 0
        if self._latencies.p95() > self._breaker_p95_ms:
            self._trip()

    def record_failure(self) -> None:
        self.failed += 1
        self._fail_streak += 1
        if self._fail_streak >= self._breaker_fail_streak:
            self._trip()

    # -- per-IP rate limit ----------------------------------------------------

    def allow_ip(self, ip: str) -> bool:
        now = time.monotonic()
        tokens, ts = self._buckets.get(ip, (float(self._rate_burst), now))
        tokens = min(self._rate_burst, tokens + (now - ts) * self._rate_per_s)
        if tokens < 1.0:
            self._buckets[ip] = (tokens, now)
            return False
        self._buckets[ip] = (tokens - 1.0, now)
        return True

    # -- concurrency slot ---------------------------------------------------

    class _Slot:
        def __init__(self, parent: "GuardrailState") -> None:
            self._parent = parent

        async def __aenter__(self) -> None:
            self._parent._inflight += 1

        async def __aexit__(self, *exc: object) -> None:
            self._parent._inflight -= 1
            self._parent._sema.release()

    async def acquire_slot(self) -> "GuardrailState._Slot | None":
        """Try to get a concurrency slot within ``slot_wait_s``.

        Returns an async-context-manager slot, or ``None`` if Ollama is too busy
        (caller should fall back). The semaphore is acquired here and released by
        the slot's ``__aexit__``.
        """
        try:
            await asyncio.wait_for(self._sema.acquire(), timeout=self._slot_wait_s)
        except asyncio.TimeoutError:
            return None
        return GuardrailState._Slot(self)

    # -- health snapshot --------------------------------------------------

    def snapshot(self) -> dict:
        return {
            "mode": "offline" if self.offline else "online",
            "p95_ms": self._latencies.p95(),
            "calls": self.calls,
            "rejected": self.rejected,
            "failed": self.failed,
            "inflight": self._inflight,
        }
