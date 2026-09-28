#!/usr/bin/env python
r"""
resolve_ctb_deps.py -- Compute CTB's true minimal DLL dependency set.

Why this exists:
  Tutorial section 2.3 step 4 says `copy <conda_geo>\Library\bin\*.dll` -- a blanket
  copy of all 177 conda DLLs. That drags in ~450MB of Intel MKL plus 32MB ICU,
  yielding a ~687MB bundle for a tool that needs a small fraction of it.
  For Recut's NFR-02 (self-contained, shippable) that is untenable.

Method:
  Recursively resolve PE imports with dumpbin, starting from the four CTB tools
  plus ctb.dll. A DLL is kept only if it (a) is reachable from a CTB binary and
  (b) actually exists in the candidate search dirs (i.e. it is a shipped
  dependency, not a Windows system DLL).

  Condition (b) is what separates the two kinds of DLL that show up as imports:
  ones shipped alongside the app, and OS-provided ones. Rather than enumerate
  every system DLL (impossible), it looks for the name in the conda bin dir --
  anything not found there is assumed to be a system DLL and is dropped.

Result on this machine: 49 DLLs, ~119MB (bin+share) versus ~687MB for the
tutorial's blanket `copy <conda_geo>\Library\bin\*.dll`.

Usage:
  python resolve_ctb_deps.py <bin_dir> <search_dir> [<search_dir> ...]
  e.g. python resolve_ctb_deps.py <bundle>\bin <conda_geo>\Library\bin

Output is TSV-ish on stdout (name<TAB>bytes), plus a total line. The checked-in
copy of that output is baseline\manifests\ctb_min_dlls.txt.

(Raw string: Windows paths in this docstring are literal, not escapes.)
"""

import os
import re
import subprocess
import sys

# Pinned to this machine's MSVC toolset. Bump the version if VS is updated, or
# just point it at wherever dumpbin.exe lives.
DUMPBIN = r"D:\VStudio\VC\Tools\MSVC\14.44.35207\bin\Hostx64\x64\dumpbin.exe"

# Windows system DLLs are never shipped with the app. Matched as a prefix
# against the lowercase name -- passed to str.startswith as a tuple.
# The api-ms-win/ext-ms-win entries are the API-set stubs, which are the ones
# most likely to slip into a naive import listing and look like real DLLs.
SYSTEM_PREFIXES = (
    "api-ms-", "ext-ms-", "kernel32", "user32", "advapi32", "shell32", "ole32",
    "oleaut32", "gdi32", "msvcrt", "ntdll", "ws2_32", "crypt32", "secur32",
    "shlwapi", "version", "winmm", "comdlg32", "comctl32", "rpcrt4", "setupapi",
    "dbghelp", "psapi", "imm32", "uxtheme", "dwmapi", "bcrypt", "ncrypt",
    "dnsapi", "iphlpapi", "netapi32", "userenv", "wtsapi32", "mpr", "wininet",
    "urlmon", "wintrust", "cfgmgr32", "powrprof", "pdh", "authz",
)

# Indented exactly four spaces in dumpbin's "dependents" listing. Anchoring on
# that indentation is what keeps this from matching filenames elsewhere in the
# report (e.g. the DLL's own path on the first line).
IMPORT_RE = re.compile(r"^\s{4}([A-Za-z0-9_\-\.]+\.dll)\s*$", re.IGNORECASE)


def imports_of(path):
    """Return the set of DLL names directly imported by a PE file.

    Returns an empty set on failure rather than raising: a DLL that cannot be
    parsed is a leaf in the graph, and one unreadable file should not abort a
    scan of the whole bundle.
    """
    try:
        out = subprocess.run(
            [DUMPBIN, "/nologo", "/dependents", path],
            capture_output=True, text=True, timeout=60,
            errors="replace",
        ).stdout
    except Exception as exc:                                  # noqa: BLE001
        print(f"  [warn] dumpbin failed on {os.path.basename(path)}: {exc}",
              file=sys.stderr)
        return set()

    # Only the block after "following dependencies" is of interest; it ends at
    # the first unindented non-empty line.
    names = set()
    in_deps = False
    for line in out.splitlines():
        if "following dependencies" in line:
            in_deps = True
            continue
        if in_deps:
            m = IMPORT_RE.match(line)
            if m:
                names.add(m.group(1))
            elif line.strip() and not line.startswith(" "):
                break
    return names


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2

    bin_dir = sys.argv[1]
    search_dirs = sys.argv[2:]

    # Build an index of every candidate DLL by lowercase name. First dir wins
    # (setdefault), so pass search dirs in priority order. Windows resolves DLL
    # names case-insensitively, hence the lowercasing throughout.
    available = {}
    for d in search_dirs:
        if not os.path.isdir(d):
            continue
        for fn in os.listdir(d):
            if fn.lower().endswith(".dll"):
                available.setdefault(fn.lower(), os.path.join(d, fn))

    # Seed with the tools + ctb.dll (note: ctb-tile.exe imports ctb.dll, so the
    # recursion would find it anyway -- seeding is just explicit).
    seeds = [
        os.path.join(bin_dir, fn)
        for fn in os.listdir(bin_dir)
        if fn.lower().endswith(".exe") or fn.lower() == "ctb.dll"
    ]

    # Breadth/depth-irrelevant transitive closure. `seen` guards against cycles
    # (PE import graphs do contain them) and `needed` against re-queuing.
    needed, queue, seen = set(), list(seeds), set()
    while queue:
        cur = queue.pop()
        if cur.lower() in seen or not os.path.isfile(cur):
            continue
        seen.add(cur.lower())
        for dep in imports_of(cur):
            low = dep.lower()
            if low.startswith(SYSTEM_PREFIXES):
                continue
            # Not in `available` => assumed to be an OS DLL, dropped. This is
            # the same test as SYSTEM_PREFIXES but by exclusion rather than by
            # name, and it is the one that catches new system DLLs this list
            # has never heard of.
            if low in available:
                if low not in needed:
                    needed.add(low)
                    queue.append(available[low])

    # Emit results
    print(f"# CTB minimal DLL set -- {len(needed)} DLLs "
          f"(vs {len(available)} candidates)\n")
    total = 0
    for low in sorted(needed):
        p = available[low]
        sz = os.path.getsize(p)
        total += sz
        print(f"{os.path.basename(p)}\t{sz}")

    print(f"\n# total bytes: {total} ({total / 1024 / 1024:.1f} MB)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
