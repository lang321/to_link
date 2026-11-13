"""REQ-REPO-20261113-01: shared repository paths for build and development tools."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SERVER = ROOT / 'server'
WINDOWS = ROOT / 'client' / 'windows'
DEPLOY = ROOT / 'deploy' / 'linux'
DIST = ROOT / 'dist'
PRIVATE = ROOT / 'private'
