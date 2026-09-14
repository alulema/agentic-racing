"""Unit tests for the concurrency-slot pool in ``guardrails.py`` (§7.5,
Fase 6.3, docs/Devlog.md 2026-09-14). Pure asyncio — no FastAPI, no mocked
Ollama, no HTTP: these just exercise ``GuardrailState``'s endpoint pool
directly.

The property that matters is the one that regressed when concurrency was
just a counting semaphore against one Ollama: two calls held concurrently
must never be bound to the *same* endpoint, because that's what caused calls
to queue behind a single-threaded engine and trip the circuit breaker
instead of failing fast (measured 2026-09-14: fresh-reply rate 36% -> 9.5%).
"""

from __future__ import annotations

import asyncio
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from guardrails import GuardrailState  # noqa: E402


def test_default_single_endpoint_one_slot():
    """No ``ollama_urls`` given: same shape as before this change — one
    endpoint, one concurrency slot."""

    guard = GuardrailState()

    async def run():
        slot = await guard.acquire_slot()
        assert slot is not None
        assert slot.url == "http://127.0.0.1:11434"
        # A second concurrent request has nothing left to claim.
        second = await guard.acquire_slot()
        assert second is None

    asyncio.run(run())


def test_two_endpoints_never_double_booked():
    guard = GuardrailState(ollama_urls=["http://a:11434", "http://b:11434"])

    async def run():
        slot_a = await guard.acquire_slot()
        slot_b = await guard.acquire_slot()
        assert slot_a is not None and slot_b is not None
        assert {slot_a.url, slot_b.url} == {"http://a:11434", "http://b:11434"}
        assert slot_a.url != slot_b.url

        # Both endpoints are now held: a third call must fail to get a slot.
        third = await guard.acquire_slot()
        assert third is None

    asyncio.run(run())


def test_slot_release_returns_its_endpoint_to_the_pool():
    guard = GuardrailState(ollama_urls=["http://a:11434", "http://b:11434"])

    async def run():
        # Claim both endpoints first so the pool is empty, then release only
        # one — that isolates which URL comes back, instead of racing against
        # the other endpoint that was never taken in the first place.
        slot_a = await guard.acquire_slot()
        slot_b = await guard.acquire_slot()
        async with slot_b:  # held open for the whole check
            async with slot_a:
                pass  # released on exit; slot_b is still held
            reacquired = await guard.acquire_slot()
            assert reacquired is not None
            assert reacquired.url == slot_a.url
            # Both endpoints spoken for again (slot_b still open, reacquired
            # holding slot_a's URL) — a third caller gets nothing.
            assert await guard.acquire_slot() is None

    asyncio.run(run())


def test_explicit_max_concurrent_round_robins_fewer_urls():
    """An explicit override still works, round-robining the given endpoints —
    kept for the single-endpoint case (and to be able to reproduce the
    2026-09-14 regression deliberately in a test, not just in production)."""

    guard = GuardrailState(ollama_urls=["http://a:11434"], max_concurrent=3)

    async def run():
        slots = [await guard.acquire_slot() for _ in range(3)]
        assert all(s is not None for s in slots)
        assert all(s.url == "http://a:11434" for s in slots)
        # All 3 slots taken (all bound to the one real endpoint): a 4th call
        # can't get one — this is exactly the queueing-behind-one-engine shape
        # that made fresh-reply rate worse, reproduced here as a unit test
        # rather than a 2-hour race experiment.
        assert await guard.acquire_slot() is None

    asyncio.run(run())


def test_inflight_counter_tracks_held_slots():
    guard = GuardrailState(ollama_urls=["http://a:11434", "http://b:11434"])

    async def run():
        assert guard.snapshot()["inflight"] == 0
        slot = await guard.acquire_slot()
        async with slot:
            assert guard.snapshot()["inflight"] == 1
        assert guard.snapshot()["inflight"] == 0

    asyncio.run(run())
