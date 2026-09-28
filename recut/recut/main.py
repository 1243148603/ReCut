r"""
main.py -- Process entry point.

Argument help text is deliberately ASCII while everything the user reads inside
the window is Chinese. The console this runs from is a GBK code page
(D:\Map\README.md section 6 records the measured behaviour), so Chinese written
to stdout comes out as mojibake -- and argparse help is written to stdout, on
the one occasion the user most needs to read it clearly. The window itself has
no such constraint and is fully Chinese.
"""

import argparse
import sys


def parse_args(argv=None):
    parser = argparse.ArgumentParser(
        prog="recut",
        description="Recut - DEM to Cesium terrain tiles, without the command "
                    "line.")
    parser.add_argument(
        "--self-test", action="store_true",
        help="run the built-in thread-model check, print PASS/FAIL, and exit "
             "with 0 only if every check passed")
    parser.add_argument(
        "--versions", action="store_true",
        help="print the versions of the bundled components and exit")
    return parser.parse_args(argv)


def print_versions() -> int:
    """Report what this build is actually running against.

    Worth its own flag because the answer is not obvious from the outside:
    Recut does not use the GDAL on PATH, it uses the one in its own bundle,
    and a support conversation that starts with "which GDAL?" is one that
    could have been avoided.
    """
    import tkinter

    from osgeo import gdal

    from . import __version__
    from .core import bundle

    print(f"Recut      {__version__}")
    print(f"Python     {sys.version.split()[0]}")
    print(f"GDAL       {gdal.VersionInfo('RELEASE_NAME')}")
    print(f"Tk         {tkinter.TkVersion}")
    try:
        home = bundle.find_ctb_home()
        print(f"CTB        {home}")
    except bundle.BundleNotFound as exc:
        print(f"CTB        NOT FOUND\n{exc}")
        return 1
    return 0


def main(argv=None) -> int:
    args = parse_args(argv)

    if args.versions:
        return print_versions()

    # Imported here rather than at module scope so that --versions still works
    # on a machine where Tk cannot open a display.
    import tkinter as tk

    from .ui.app import RecutApp

    root = tk.Tk()
    app = RecutApp(root, self_test=args.self_test)
    if args.self_test:
        app.run_self_test()
    root.mainloop()
    return app.exit_code


if __name__ == "__main__":
    sys.exit(main())
