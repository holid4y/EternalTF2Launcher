import os
import sys

# The launcher runs this with `python -I`, which leaves the script directory off sys.path.
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from eternal_engine.cli import main  # noqa: E402

if __name__ == "__main__":
    sys.exit(main())
