"""Compatibility entry point; new builds can use prepare-eos-mobile.py."""
from pathlib import Path
import runpy

if __name__ == '__main__':
    runpy.run_path(str(Path(__file__).with_name('prepare-eos-mobile.py')), run_name='__main__')
