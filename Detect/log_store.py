"""Thread-safe JSON event store for focus-change logs.

In-memory list is the source of truth; disk is updated with retries so a concurrent
reader (or AV scan) cannot fail the watcher with WinError 32.
"""

from __future__ import annotations

import json
import os
import threading
import time
import uuid
from pathlib import Path
from typing import Any

MAX_EVENTS = 2000

_DATA_DIR = Path(__file__).resolve().parent / "data"
_LOG_PATH = _DATA_DIR / "focus_events.json"
_lock = threading.RLock()
_events: list[dict[str, Any]] | None = None


def _ensure_dir() -> None:
    _DATA_DIR.mkdir(parents=True, exist_ok=True)


def _read_disk() -> list[dict[str, Any]]:
    if not _LOG_PATH.exists():
        return []
    try:
        with open(_LOG_PATH, "r", encoding="utf-8") as f:
            data = json.load(f)
        if isinstance(data, list):
            return [e for e in data if isinstance(e, dict)]
    except (json.JSONDecodeError, OSError):
        return []
    return []


def _atomic_write(payload: list[dict[str, Any]]) -> None:
    _ensure_dir()
    text = json.dumps(payload, indent=2, ensure_ascii=False)
    # Unique temp name avoids clashing with a previous .tmp left locked.
    tmp = _LOG_PATH.with_name(f"{_LOG_PATH.stem}.{uuid.uuid4().hex}.tmp")
    last_err: OSError | None = None
    try:
        with open(tmp, "w", encoding="utf-8") as f:
            f.write(text)
            f.flush()
            os.fsync(f.fileno())
        for attempt in range(8):
            try:
                os.replace(tmp, _LOG_PATH)
                return
            except OSError as ex:
                last_err = ex
                time.sleep(0.02 * (attempt + 1))
        if last_err:
            raise last_err
    finally:
        try:
            if tmp.exists():
                tmp.unlink()
        except OSError:
            pass


def _ensure_loaded() -> list[dict[str, Any]]:
    global _events
    if _events is None:
        _events = _read_disk()
    return _events


def load() -> list[dict[str, Any]]:
    with _lock:
        return list(_ensure_loaded())


def save(events: list[dict[str, Any]]) -> None:
    with _lock:
        global _events
        trimmed = events[-MAX_EVENTS:] if len(events) > MAX_EVENTS else list(events)
        _events = trimmed
        _atomic_write(trimmed)


def append(event: dict[str, Any]) -> int:
    """Append one event. Returns the new total count."""
    with _lock:
        events = _ensure_loaded()
        events.append(event)
        if len(events) > MAX_EVENTS:
            del events[:-MAX_EVENTS]
        try:
            _atomic_write(events)
        except OSError as ex:
            # Keep memory state even if disk briefly fails.
            print(f"[log_store] write warning: {ex}")
        return len(events)


def list_since(index: int = 0) -> tuple[list[dict[str, Any]], int]:
    """Return events from index onward and the total length."""
    with _lock:
        events = _ensure_loaded()
        total = len(events)
        if index < 0:
            index = 0
        if index > total:
            index = total
        return list(events[index:]), total


def clear() -> None:
    with _lock:
        global _events
        _events = []
        try:
            _atomic_write([])
        except OSError as ex:
            print(f"[log_store] clear write warning: {ex}")


def path() -> Path:
    return _LOG_PATH
