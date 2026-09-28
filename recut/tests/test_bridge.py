"""bridge.py -- the worker-to-UI channel, tested without a display.

This is the part of the thread model that can be pinned down by test. What
cannot is whether Tk itself behaves when driven from the pump; that is what
`python -m recut --self-test` is for. Keeping the two separate means a failure
here is unambiguously a logic bug rather than a windowing problem.
"""

import threading
import time
import unittest

from recut.ui.bridge import (
    PROGRESS,
    Event,
    EventBridge,
    PumpPacer,
    Throttle,
    finished,
    progress,
)


class TestEventBridge(unittest.TestCase):
    def test_drain_returns_posted_events_in_order(self):
        bridge = EventBridge()
        for i in range(5):
            bridge.post(progress("t1", i / 5))
        got = bridge.drain()
        self.assertEqual([e.fraction for e in got], [0.0, 0.2, 0.4, 0.6, 0.8])
        self.assertTrue(all(e.kind == PROGRESS for e in got))

    def test_drain_respects_the_limit_and_leaves_the_rest(self):
        """A tick that returns everything pending would let a burst of events
        hold the main thread past the point where the window stops redrawing.
        The limit is what makes that impossible."""
        bridge = EventBridge()
        for i in range(50):
            bridge.post(progress("t1", i / 50))
        first = bridge.drain(limit=20)
        self.assertEqual(len(first), 20)
        self.assertEqual(bridge.pending(), 30)
        self.assertEqual(bridge.drain(limit=20)[0].fraction, 20 / 50)

    def test_drain_on_an_empty_bridge_returns_nothing(self):
        self.assertEqual(EventBridge().drain(), [])

    def test_a_full_bridge_drops_the_oldest_not_the_newest(self):
        """When the UI cannot keep up, the newest state is the one worth
        having: a progress bar stuck at a stale value is worse than one that
        skipped a step, and a log missing its most recent line is misleading.
        """
        bridge = EventBridge(capacity=4)
        for i in range(10):
            bridge.post(progress("t1", i / 10))
        got = bridge.drain()
        self.assertEqual(len(got), 4)
        self.assertEqual(got[-1].fraction, 0.9, "the newest event must survive")
        self.assertEqual(bridge.dropped, 6)

    def test_dropped_events_are_counted_not_hidden(self):
        """Silent loss is the thing the counter exists to prevent."""
        bridge = EventBridge(capacity=2)
        self.assertEqual(bridge.dropped, 0)
        for i in range(5):
            bridge.post(progress("t1", i))
        self.assertEqual(bridge.dropped, 3)

    def test_post_is_safe_from_many_threads(self):
        """The producer side is called from worker threads, so it must not
        need a lock held by the consumer."""
        bridge = EventBridge(capacity=10_000)

        # The id is passed as an argument rather than closed over. A list
        # comprehension shares one scope across its iterations, so a lambda
        # capturing `n` would have every thread post as the final value.
        def flood(n):
            for _ in range(200):
                bridge.post(finished(f"t{n}"))

        threads = [threading.Thread(target=flood, args=(n,)) for n in range(8)]
        for t in threads:
            t.start()
        for t in threads:
            t.join()

        seen, total = set(), 0
        while True:
            batch = bridge.drain(limit=500)
            if not batch:
                break
            total += len(batch)
            seen.update(e.task_id for e in batch)
        self.assertEqual(total, 1600)
        self.assertEqual(seen, {f"t{n}" for n in range(8)})


class TestPumpPacer(unittest.TestCase):
    def test_stays_busy_while_events_keep_arriving(self):
        pacer = PumpPacer()
        for _ in range(10):
            self.assertEqual(pacer.next_delay_ms(True), PumpPacer.BUSY_MS)

    def test_backs_off_only_after_several_empty_ticks(self):
        """A job that pauses between zoom levels must not make the poll rate
        flap between fast and slow on every gap."""
        pacer = PumpPacer()
        self.assertEqual(pacer.next_delay_ms(False), PumpPacer.BUSY_MS)
        self.assertEqual(pacer.next_delay_ms(False), PumpPacer.BUSY_MS)
        self.assertEqual(pacer.next_delay_ms(False), PumpPacer.IDLE_MS)

    def test_one_event_resets_the_backoff(self):
        """This is what keeps the cancel button honest: a burst of output
        after a quiet spell is picked up at full rate immediately."""
        pacer = PumpPacer()
        for _ in range(5):
            pacer.next_delay_ms(False)
        self.assertEqual(pacer.next_delay_ms(True), PumpPacer.BUSY_MS)
        self.assertEqual(pacer.idle_ticks, 0)

    def test_busy_delay_is_short_enough_to_feel_immediate(self):
        # 100 ms is under the ~150 ms where a click stops feeling connected to
        # its effect. If this constant is raised, raise it deliberately.
        self.assertLessEqual(PumpPacer.BUSY_MS, 150)


class TestThrottle(unittest.TestCase):
    def test_first_call_is_always_allowed(self):
        """Otherwise a job that emits one progress event and finishes would
        emit nothing at all."""
        self.assertTrue(Throttle(interval_s=10.0).ready())

    def test_rapid_calls_are_suppressed(self):
        throttle = Throttle(interval_s=10.0)
        self.assertTrue(throttle.ready())
        self.assertFalse(throttle.ready())
        self.assertFalse(throttle.ready())

    def test_calls_are_allowed_again_after_the_interval(self):
        throttle = Throttle(interval_s=0.05)
        self.assertTrue(throttle.ready())
        time.sleep(0.06)
        self.assertTrue(throttle.ready())


class TestEventShape(unittest.TestCase):
    def test_events_are_immutable(self):
        """A mutable event could be written by the worker while the main
        thread reads it -- the race this whole module exists to prevent."""
        event = progress("t1", 0.5)
        with self.assertRaises(Exception):
            event.fraction = 0.9

    def test_events_carry_no_tk_objects(self):
        """The rule that makes the bridge safe, asserted rather than trusted:
        every field must be a primitive or a plain container."""
        event = Event(kind=PROGRESS, task_id="t1", text="x",
                      fraction=0.5, detail={"a": 1})
        for value in (event.kind, event.task_id, event.text, event.fraction,
                      event.level, event.detail):
            self.assertNotIn(type(value).__module__, ("tkinter", "_tkinter"))

    def test_progress_detail_defaults_to_a_fresh_dict(self):
        """A shared default would let one event's detail leak into another."""
        a, b = progress("t1", 0.1), progress("t2", 0.2)
        self.assertIsNot(a.detail, b.detail)


if __name__ == "__main__":
    unittest.main()
