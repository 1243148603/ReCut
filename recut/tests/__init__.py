"""Test package.

Putting the repository root on sys.path here rather than in every test module
means `python -m unittest discover` works from a fresh checkout with no
install step and no PYTHONPATH fiddling. Recut is run from source during
development; there is nothing to install.
"""

import sys
from pathlib import Path

_ROOT = Path(__file__).resolve().parents[1]
if str(_ROOT) not in sys.path:
    sys.path.insert(0, str(_ROOT))
