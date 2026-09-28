r"""
app.py -- The main window, and the event pump that drives it.

At this stage the window runs a deliberately fake job. That is not a stub left
behind by accident: the thread model is the one part of this architecture that
cannot be verified without a display, and everything else is built on top of
it. Proving it early -- that a worker thread can report progress, that cancel
is seen promptly, and that the window keeps redrawing throughout -- costs a
page of code and removes the risk of discovering a structural problem three
milestones later, when it would be expensive.

The three rules this file exists to obey:

  1. Only this thread touches widgets. Workers call `EventBridge.post`.
  2. The pump never blocks and never does I/O. It drains a bounded number of
     events and reschedules itself.
  3. Cancel is a `threading.Event` the worker checks. The main thread sets it
     and returns immediately -- it never joins, waits, or otherwise risks
     freezing on a job that is slow to notice.
"""

import threading
import time
import tkinter as tk
from tkinter import font as tkfont
from tkinter import ttk
from tkinter.scrolledtext import ScrolledText

from . import bridge as events
from .bridge import EventBridge, PumpPacer

# Long jobs emit more log lines than anyone will read, and every retained line
# is memory spent for the rest of the session. Trimming from the front keeps
# the most recent output, which is the part a failure is diagnosed from.
MAX_LOG_LINES = 2000


class DemoJob:
    """A fake pipeline job, shaped like the real ones.

    It reports progress in percentages, logs as it goes, and polls a cancel
    flag between steps -- the same contract the warp, slice and package jobs
    will honour. Keeping the shape identical is what makes validating the pump
    here mean something later.
    """

    def __init__(self, job_id="demo", steps=100, interval=0.05):
        self.job_id = job_id
        self.steps = steps
        self.interval = interval

    def run(self, post, cancel_event) -> bool:
        """Returns True if it finished, False if it was cancelled.

        `post` rather than the bridge itself, so this class does not depend on
        the bridge existing and stays testable on its own.
        """
        post(events.state(self.job_id, "运行中"))
        post(events.log("演示任务开始。", self.job_id))
        for step in range(1, self.steps + 1):
            if cancel_event.is_set():
                post(events.log(
                    f"在第 {step}/{self.steps} 步收到取消，正在退出。",
                    self.job_id, level="warn"))
                return False
            time.sleep(self.interval)
            post(events.progress(self.job_id, step / self.steps,
                                 f"第 {step}/{self.steps} 步"))
            if step % 20 == 0:
                post(events.log(f"已完成 {step}%。", self.job_id))
        post(events.log("演示任务完成。", self.job_id))
        return True


class RecutApp:
    """The main window. One instance per process, constructed on the main
    thread, and never touched from any other."""

    def __init__(self, root: tk.Tk, self_test: bool = False):
        self.root = root
        self.bridge = EventBridge()
        self.pacer = PumpPacer()
        self.self_test = self_test

        self._cancel_event = threading.Event()
        self._worker: threading.Thread | None = None
        self._closing = False
        self._saw_finish = False
        self._max_progress = 0.0
        self._cancel_requested_at: float | None = None
        self._cancel_latency_ms: float | None = None

        # Printed by --self-test; 0 means every assertion held.
        self.exit_code = 0
        self._checks: list[tuple[str, bool]] = []

        self._build()
        self._pump()

    # ---------------------------------------------------------------- build

    def _build(self):
        self.root.title("Recut — DEM 地形瓦片工具")
        self.root.geometry("820x560")
        self.root.minsize(640, 420)

        # Tk 8.6 ships the native Windows look but does not select it by
        # default in every build; falling back silently is right here because
        # a plainer-looking window is not worth refusing to start over.
        style = ttk.Style()
        if "vista" in style.theme_names():
            style.theme_use("vista")

        base = tkfont.nametofont("TkDefaultFont")
        base.configure(size=max(base.cget("size"), 9))

        outer = ttk.Frame(self.root, padding=10)
        outer.pack(fill="both", expand=True)

        toolbar = ttk.Frame(outer)
        toolbar.pack(fill="x")
        self.start_button = ttk.Button(toolbar, text="开始演示任务",
                                       command=self.start_demo)
        self.start_button.pack(side="left")
        self.cancel_button = ttk.Button(toolbar, text="取消", state="disabled",
                                        command=self.cancel)
        self.cancel_button.pack(side="left", padx=(8, 0))

        self.progress = ttk.Progressbar(outer, mode="determinate",
                                        maximum=1.0, value=0.0)
        self.progress.pack(fill="x", pady=(12, 4))

        self.status = ttk.Label(outer, text="就绪。")
        self.status.pack(anchor="w")

        self.log = ScrolledText(outer, height=18, wrap="none",
                                state="disabled")
        self.log.pack(fill="both", expand=True, pady=(10, 0))

        self.root.protocol("WM_DELETE_WINDOW", self.close)

    # ------------------------------------------------------------- controls

    def start_demo(self):
        if self._worker and self._worker.is_alive():
            return
        self._cancel_event = threading.Event()
        self._saw_finish = False
        self.start_button.config(state="disabled")
        self.cancel_button.config(state="normal")
        self.progress.config(value=0.0)
        self._append("")
        self._append("=== 演示任务 ===")

        job = DemoJob()
        self._worker = threading.Thread(
            target=self._run_job, args=(job,), daemon=True,
            name="demo-job")
        self._worker.start()

    def cancel(self):
        """Set the flag and return. Deliberately does not join the worker:
        blocking the main thread on a job that may take a moment to notice
        would freeze the window, which is the exact failure being guarded
        against."""
        if not self._worker or not self._worker.is_alive():
            return
        self.cancel_button.config(state="disabled")
        self.status.config(text="正在取消…")
        self._append("已请求取消。")
        # Timed, because "cancel works" has to mean "cancel is noticed
        # promptly" -- a flag that is only read at the end of a five-minute
        # slice technically stops the job and is useless to the operator.
        self._cancel_requested_at = time.monotonic()
        self._cancel_event.set()

    def close(self):
        """Stop the pump, ask the worker to stop, and destroy the window.

        The worker is a daemon thread, so a job that ignores the cancel flag
        cannot keep the process alive -- a close request must always succeed.
        """
        self._closing = True
        self._cancel_event.set()
        self.root.destroy()

    # ---------------------------------------------------------------- worker

    def _run_job(self, job):
        """Runs on a worker thread. Touches no widgets -- only the bridge."""
        try:
            completed = job.run(self.bridge.post, self._cancel_event)
        except Exception as exc:  # noqa: BLE001 - reported, not swallowed
            self.bridge.post(events.log(f"任务异常：{exc!r}", job.job_id,
                                        level="error"))
            completed = False
        self.bridge.post(events.finished(
            job.job_id, {"completed": completed,
                         "cancelled": self._cancel_event.is_set()}))

    # ------------------------------------------------------------------ pump

    def _pump(self):
        """The whole of the main thread's involvement in a running job.

        Bounded so a burst of output cannot hold the frame, and never
        blocking so an idle window costs nothing.
        """
        batch = self.bridge.drain(limit=200)
        for event in batch:
            self._handle(event)

        if self.bridge.dropped:
            # Surfaced once, loudly, rather than left as a quiet gap in the
            # log the operator is trying to diagnose a failure from.
            self._append(f"[警告] 有 {self.bridge.dropped} 条事件因队列满被丢弃。")

        if not self._closing:
            self.root.after(self.pacer.next_delay_ms(bool(batch)), self._pump)

    def _handle(self, event):
        if event.kind == events.PROGRESS:
            self.progress.config(value=event.fraction)
            self._max_progress = max(self._max_progress, event.fraction or 0.0)
            if event.text:
                self.status.config(text=event.text)
        elif event.kind == events.LOG:
            self._append(event.text)
        elif event.kind == events.STATE:
            self.status.config(text=event.text)
        elif event.kind == events.TASK_FINISHED:
            self._on_finished(event)
        else:
            self._append(f"[{event.kind}] {event.text}")

    def _on_finished(self, event):
        self._saw_finish = True
        if self._cancel_requested_at is not None:
            self._cancel_latency_ms = (
                time.monotonic() - self._cancel_requested_at) * 1000.0
        self.start_button.config(state="normal")
        self.cancel_button.config(state="disabled")
        cancelled = event.detail.get("cancelled", False)
        if cancelled:
            self.progress.config(value=0.0)
            self.status.config(text="已取消。")
        elif event.detail.get("completed"):
            self.progress.config(value=1.0)
            self.status.config(text="完成。")
        else:
            self.status.config(text="失败，详见日志。")

        if self.self_test:
            self._finish_self_test(event.detail)

    def _append(self, line: str):
        self.log.config(state="normal")
        self.log.insert("end", line + "\n")
        # Trim in blocks rather than a line at a time: delete() on a Text
        # widget is O(rest of buffer), so trimming every insert would make a
        # chatty job quadratic for no visible benefit.
        excess = int(self.log.index("end-1c").split(".")[0]) - MAX_LOG_LINES
        if excess > 0:
            self.log.delete("1.0", f"{excess + 1}.0")
        self.log.see("end")
        self.log.config(state="disabled")

    # ------------------------------------------------------------- self-test

    def _finish_self_test(self, detail: dict):
        """Assert that the thread model behaved, then close.

        Every check is about the pump and the cancel path, not about the demo
        job. The demo job is scaffolding, and these assertions are the
        template for the ones that will move onto the real jobs once they
        exist.
        """
        latency = self._cancel_latency_ms
        # ASCII check labels on purpose: this output goes to the console,
        # which is a GBK code page, and Chinese written there arrives as
        # mojibake -- in the one command whose whole job is to be readable
        # when something is wrong. Same reasoning as main.py's argparse help.
        self._checks = [
            ("progress reached the main thread before the cancel",
             self._max_progress > 0.1),
            ("the worker itself saw the cancel flag",
             detail.get("completed") is False),
            ("the main thread received a terminal event", self._saw_finish),
            ("cancel was honoured within one poll interval",
             latency is not None and latency < 500.0),
            ("no events were dropped", self.bridge.dropped == 0),
        ]
        self.exit_code = 0 if all(ok for _, ok in self._checks) else 1
        for label, ok in self._checks:
            print(f"[{'PASS' if ok else 'FAIL'}] {label}")
        print(f"       cancel latency {latency:.0f} ms, "
              f"progress at cancel {self._max_progress:.0%}")
        self.root.after(200, self.root.destroy)

    def run_self_test(self, cancel_after_ms: int = 1200):
        """Start the demo job and cancel it partway through."""
        self.start_demo()
        self.root.after(cancel_after_ms, self.cancel)
