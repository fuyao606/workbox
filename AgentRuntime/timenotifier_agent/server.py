from __future__ import annotations

import json
import sys
import traceback
from typing import Any

from .workflows import run_workflow


def _write_response(response: dict[str, Any]) -> None:
    sys.stdout.write(json.dumps(response, ensure_ascii=False, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def handle_request(request: dict[str, Any]) -> dict[str, Any]:
    request_id = str(request.get("id") or "")
    workflow = str(request.get("workflow") or "")
    payload = request.get("payload") or {}
    try:
        result = run_workflow(workflow, payload)
        return {
            "id": request_id,
            "ok": True,
            "result": result,
        }
    except Exception as exc:  # noqa: BLE001 - every sidecar failure must become protocol JSON.
        return {
            "id": request_id,
            "ok": False,
            "error": {
                "message": str(exc),
                "type": type(exc).__name__,
                "trace": traceback.format_exc(limit=8),
            },
        }


def main() -> None:
    for line in sys.stdin:
        if not line.strip():
            continue
        try:
            request = json.loads(line)
        except json.JSONDecodeError as exc:
            _write_response({
                "id": "",
                "ok": False,
                "error": {
                    "message": f"Invalid JSON request: {exc}",
                    "type": "JsonDecodeError",
                },
            })
            continue

        _write_response(handle_request(request))
