r"""
bridge.py -- The one channel between worker threads and the Tk main thread.

Tk objects may only be touched from the thread that created them. That was
already true under the GIL, where an accidental cross-thread call often got
away with it; this program runs on a free-threaded Python 3.14 build, where it
will not. So the rule is made structural rather than remembered: workers
produce `Event` values, the main thread drains them and updates widgets, and
nothing else crosses.

Everything in this module is deliberately free of tkinter, including the pump
pacing policy. That is not tidiness -- it is what makes the thread-model logic
testable without a display, which matters because the display is the one part
of this architecture that cannot be verified any other way.

Why polling instead of a callback per event
-------------------------------------------
The obvious design is for the worker to schedule its own UI update with
`root.after_idle`. It cannot: `after` is a Tk call and therefore main-thread
only. So the worker posts to a queue and the main thread polls. Polling costs
one `get_nowait` per tick and buys the ability to bound how much work a single
tick does, which is what keeps the window responsive when a job emits tens of
thousands of events.
"""

import queue
import time
from dataclasses import dataclass, field
from typing import Any

# Event kinds. Strings rather than an Enum because they end up in the task log
# and are easier to read there as-is.
LOG = "log"
PROGRESS = "progress"
STATE = "state"
TASK_FINISHED = "task_finished"
QUEUE_CHANGED = "queue_changed"


@dataclass(frozen=True)
class Event:
    """One thing that happened, in a form safe to hand to another thread.

    Frozen and made only of primitives on purpose: a mutable payload could be
    written by the worker while the main thread reads it, which is exactly the
    race this module exists to prevent.
    """

    kind: str
    task_id: str | None = None
    text: str = ""
    fraction: float | None = None
    level: str = "info"
    detail: dict[str, Any] = field(default_factory=dict)


def log(text: str, task_id: str | None = None, level: str = "info") -> Event:
    return Event(kind=LOG, task_id=task_id, text=text, level=level)


def progress(task_id: str, fraction: float, text: str = "") -> Event:
    return Event(kind=PROGRESS, task_id=task_id, fraction=fraction, text=text)


def state(task_id: str, text: str) -> Event:
    return Event(kind=STATE, task_id=task_id, text=text)


def finished(task_id: str, detail: dict[str, Any] | None = None) -> Event:
    return Event(kind=TASK_FINISHED, task_id=task_id, detail=detail or {})


class EventBridge:
    """A bounded, lock-free-to-produce queue of `Event`s.

    Bounded because an unbounded queue turns a runaway worker into an
    out-of-memory crash rather than a stalled job. When it fills, the oldest
    is dropped and the loss is counted, so the UI can say "N events dropped"
    instead of quietly showing a log with holes in it.
    """

    def __init__(self, capacity: int = 20_000):
        self._queue: queue.Queue[Event] = queue.Queue(maxsize=capacity)
        self._dropped = 0

    @property
    def dropped(self) -> int:
        """Events discarded because the queue was full.

        A non-zero value is a defect worth surfacing, not swallowing: it means
        the main thread is not draining as fast as a worker is producing, and
        the log the operator is reading is incomplete.
        """
        return self._dropped

    def post(self, event: Event) -> None:
        """Called from any thread. Never blocks, never raises."""
        try:
            self._queue.put_nowait(event)
        except queue.Full:
            # Drop the OLDEST so the newest state still arrives. Progress is
            # the bulk of traffic and only the latest value matters; dropping
            # the newest would leave the UI showing a stale number indefinitely.
            try:
                self._queue.get_nowait()
                self._queue.task_done()
            except queue.Empty:  # pragma: no cover - only under a race
                pass
            self._dropped += 1
            try:
                self._queue.put_nowait(event)
            except queue.Full:  # pragma: no cover - only under a race
                self._dropped += 1

    def drain(self, limit: int = 200) -> list[Event]:
        """Called from the main thread only. Returns at most `limit` events.

        The limit is the mechanism that keeps the window responsive: a tick
        that processes a bounded number of events always returns to the event
        loop, so no amount of pending output can freeze a frame. What is left
        waits for the next tick, and the pacer below notices.
        """
        out = []
        for _ in range(limit):
            try:
                out.append(self._queue.get_nowait())
            except queue.Empty:
                break
        return out

    def pending(self) -> int:
        return self._queue.qsize()


class PumpPacer:
    """Decides how long the main thread waits before draining again.

    Two failure modes to avoid, and they pull in opposite directions:

      * Polling every 10 ms while a job writes 223,000 files burns CPU on an
        otherwise idle window, for events that arrive seconds apart.
      * Polling every second makes the cancel button feel broken -- the
        operator clicks, nothing happens for a second, and they click again.

    100 ms when there is work is under the threshold where a click feels
    unacknowledged, and 300 ms when there is not is a 3x saving on an idle
    window. Three consecutive empty ticks are required before backing off, so
    a job that pauses between zoom levels does not oscillate between the two
    rates.
    """

    BUSY_MS = 100
    IDLE_MS = 300
    IDLE_AFTER = 3

    def __init__(self):
        self._empty_ticks = 0

    @property
    def idle_ticks(self) -> int:
        return self._empty_ticks

    def next_delay_ms(self, got_any: bool) -> int:
        """Record the outcome of a drain and return the next delay."""
        if got_any:
            self._empty_ticks = 0
            return self.BUSY_MS
        self._empty_ticks += 1
        return self.BUSY_MS if self._empty_ticks < self.IDLE_AFTER else self.IDLE_MS


class Throttle:
    """Rate-limits a producer so it cannot flood the bridge.

    Used by the packaging job, which walks 223,000 files and would otherwise
    emit an event per file. Merging at the source is the cheapest place to do
    it -- the alternative is to queue two hundred thousand events and merge
    them in the UI thread, which is the work the UI thread can least afford.

    Time-based rather than count-based, because the two jobs differ by four
    orders of magnitude in event rate and a single count threshold is wrong
    for at least one of them.
    """

    def __init__(self, interval_s: float = 0.25):
        self._interval = interval_s
        self._last = 0.0

    def ready(self) -> bool:
        """True if enough time has passed; always True on the first call."""
        now = time.monotonic()
        if now - self._last < self._interval:
            return False
        self._last = now
        return True
