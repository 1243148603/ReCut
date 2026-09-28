#!/usr/bin/env python
"""
bundle.py -- Locate the bundled CTB / GDAL resources.

Recut never asks the user to install Anaconda or GDAL (NFR-02). Everything it
needs is shipped inside the distribution, and this module is the single place
that knows where that is.

Two layouts must both work:

  development   D:\\Map\\recut\\recut\\core\\bundle.py  ->  D:\\Map\\ctb\\
  packaged      <install dir>\\Recut.exe              ->  <install dir>\\ctb\\

Keeping the two behind one function is deliberate: the packaging scheme is the
project's largest open risk (the runtime is a free-threaded Python 3.14 build,
where PyInstaller support is uncertain). If the scheme changes -- PyInstaller,
an embeddable Python distribution, or a wrapped conda env -- only this file
should need to change.

Why the tools are invoked directly rather than through ctb\\ctb.cmd
------------------------------------------------------------------
ctb.cmd exists and works, but README section 6.2 documents two hard constraints
on .cmd files that make them a poor foundation for a GUI:

  * cmd.exe parses .cmd in the OEM codepage (GBK here), so UTF-8 Chinese
    comments corrupt parsing and get executed as commands.
  * A LF-only .cmd fails at `goto :label` with
    "system cannot find the batch label - collect", because cmd.exe seeks the
    label by byte offset. Whether it works depends only on where the label
    lands in the file -- so an unrelated one-line edit can break it, and
    testing on one machine proves nothing about another.

Both are landmines under a tool that must "not crash, not fail silently"
(NFR-04). Calling the .exe with an explicit environment reproduces exactly what
ctb.cmd does, without the batch parser in the loop.
"""

import os
import sys
from pathlib import Path

# The four tools the bundle ships. Kept as a tuple so a typo in a caller fails
# here, at the single point where tool names are known, rather than surfacing
# later as a confusing "file not found" from Popen.
CTB_TOOLS = ("ctb-tile", "ctb-info", "ctb-extents", "ctb-export")

# Environment variable that overrides discovery. Used by tests and by anyone
# pointing Recut at a CTB bundle that lives somewhere unusual.
CTB_HOME_ENV = "RECUT_CTB_HOME"

# How far up from this file to look for a sibling `ctb` directory before giving
# up. Four levels covers RECUT_ROOT/recut/core/bundle.py -> RECUT_ROOT (3 up),
# with one to spare for a src/ layout. A cap is used rather than walking to the
# filesystem root so that a stray D:\ctb on some machine cannot silently win.
_MAX_WALK_UP = 4


class BundleNotFound(RuntimeError):
    """The CTB distribution could not be located."""


def _is_ctb_home(path: Path) -> bool:
    """A directory is a CTB home if the tool it must run is inside it.

    Only ctb-tile is probed. It is the tool the pipeline cannot work without,
    and requiring every tool to be present would reject a deliberately trimmed
    bundle.
    """
    return (path / "bin" / "ctb-tile.exe").is_file()


def _candidate_homes():
    """Yield places to look, most specific first.

    Order matters and is the whole content of this function:

      1. RECUT_CTB_HOME -- an explicit instruction always wins.
      2. Next to the running program -- the packaged layout, where the bundle
         is a sibling of the executable.
      3. Walking up from this source file -- the development layout, where the
         bundle is a sibling of the `recut` package rather than of a Python
         interpreter. This is the case that makes `python -m recut` work in a
         checkout.

    Step 2 is tried before step 3 on purpose: a packaged build stays correct
    even if a copy of the source tree happens to be nearby.
    """
    override = os.environ.get(CTB_HOME_ENV)
    if override:
        yield Path(override), f"{CTB_HOME_ENV}={override}"

    # sys.executable is the python.exe (or embedded interpreter) that is
    # running, which for a packaged build sits beside the bundled ctb\.
    if sys.executable:
        yield Path(sys.executable).resolve().parent / "ctb", "beside the interpreter"

    # __file__ is <root>/recut/core/bundle.py, so start one level up: the
    # package directory itself never contains ctb\.
    here = Path(__file__).resolve().parent
    for _ in range(_MAX_WALK_UP):
        here = here.parent
        yield here / "ctb", f"walking up from {__file__}"
        if here.parent == here:  # reached the filesystem root
            break


def find_ctb_home() -> Path:
    """Return the CTB distribution root, or raise BundleNotFound.

    The error lists every place that was tried. A bare "not found" would leave
    the operator guessing which of the three rules failed, and this is the
    first thing that breaks on a machine where Recut was unzipped somewhere
    unexpected.
    """
    tried = []
    for path, why in _candidate_homes():
        tried.append(f"  {path}  ({why})")
        if _is_ctb_home(path):
            return path.resolve()

    raise BundleNotFound(
        "Could not find the CTB distribution. Looked for a directory "
        "containing bin\\ctb-tile.exe in:\n"
        + "\n".join(tried)
        + f"\n\nSet {CTB_HOME_ENV} to the bundle root to override."
    )


def ctb_tool(name: str, home: Path | None = None) -> Path:
    """Return the full path to one of the bundled CTB tools."""
    if name not in CTB_TOOLS:
        raise ValueError(
            f"unknown CTB tool {name!r}; expected one of {', '.join(CTB_TOOLS)}"
        )
    home = home or find_ctb_home()
    exe = home / "bin" / f"{name}.exe"
    if not exe.is_file():
        raise BundleNotFound(f"CTB tool missing from the bundle: {exe}")
    return exe


def ctb_env(home: Path | None = None, base: dict | None = None) -> dict:
    """Return an environment that lets the CTB tools find proj.db.

    Without PROJ_DATA every CTB invocation prints

        ERROR 1: PROJ: proj_create_from_database: Cannot find proj.db

    and fails. The reason is that proj.db lives in share\\proj\\, which is NOT
    beside the DLLs in bin\\ -- so copying the DLLs (what the tutorial
    suggests) is not enough, and the failure also reproduces on a machine that
    does have conda installed, making it certain on a clean one.

    `base` defaults to a copy of os.environ, so the child keeps PATH and
    SystemRoot. Passing a stripped environment to a Windows process is its own
    class of bug.
    """
    home = home or find_ctb_home()
    env = dict(os.environ if base is None else base)

    proj = home / "share" / "proj"
    gdal = home / "share" / "gdal"
    # Checked rather than assumed: a bundle that lost its share\ tree would
    # otherwise fail deep inside GDAL with a proj.db error that names neither
    # the bundle nor the directory that is actually missing.
    for name, path in (("PROJ_DATA", proj), ("GDAL_DATA", gdal)):
        if not path.is_dir():
            raise BundleNotFound(
                f"{name} directory missing from the bundle: {path}\n"
                f"The CTB tools cannot run without it."
            )
        env[name] = str(path)

    return env
