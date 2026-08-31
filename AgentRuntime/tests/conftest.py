import sys
from pathlib import Path


AGENT_RUNTIME_ROOT = Path(__file__).resolve().parents[1]

if str(AGENT_RUNTIME_ROOT) not in sys.path:
    sys.path.insert(0, str(AGENT_RUNTIME_ROOT))
