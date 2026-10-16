"""
Focus Detect — live logger + HTML dashboard.

Usage:
  cd Detect
  python server.py
  open http://127.0.0.1:8765
"""

from __future__ import annotations

import json
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse

import log_store
from focus_watcher import FocusWatcher

HOST = "127.0.0.1"
PORT = 8765
ROOT = Path(__file__).resolve().parent
DASHBOARD = ROOT / "dashboard.html"

_watcher: FocusWatcher | None = None
_watcher_lock = threading.Lock()


def _get_watcher() -> FocusWatcher | None:
    with _watcher_lock:
        return _watcher


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt: str, *args) -> None:
        sys.stdout.write("[http] " + (fmt % args) + "\n")

    def _cors(self) -> None:
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "Content-Type")

    def _json(self, code: int, payload: dict) -> None:
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self._cors()
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def _html(self, path: Path) -> None:
        if not path.exists():
            self.send_error(404, "dashboard.html missing")
            return
        data = path.read_bytes()
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def do_OPTIONS(self) -> None:
        self.send_response(204)
        self._cors()
        self.end_headers()

    def do_GET(self) -> None:
        parsed = urlparse(self.path)
        path = parsed.path.rstrip("/") or "/"

        if path == "/":
            self._html(DASHBOARD)
            return

        if path == "/api/events":
            qs = parse_qs(parsed.query)
            since = 0
            try:
                since = int((qs.get("since") or ["0"])[0])
            except ValueError:
                since = 0
            events, total = log_store.list_since(since)
            self._json(200, {"events": events, "total": total, "since": since})
            return

        if path == "/api/status":
            w = _get_watcher()
            events, total = log_store.list_since(0)
            last = events[-1] if events else None
            self._json(200, {
                "watcher_running": bool(w and w.running),
                "total": total,
                "last": last,
                "log_path": str(log_store.path()),
            })
            return

        self.send_error(404, "Not found")

    def do_POST(self) -> None:
        parsed = urlparse(self.path)
        path = parsed.path.rstrip("/") or "/"
        # drain body
        length = int(self.headers.get("Content-Length") or 0)
        if length:
            self.rfile.read(length)

        if path == "/api/clear":
            log_store.clear()
            self._json(200, {"ok": True, "total": 0})
            return

        self.send_error(404, "Not found")


def main() -> None:
    global _watcher

    if sys.platform != "win32":
        print("Focus Detect requires Windows (WinEvent hooks).")
        sys.exit(1)

    watcher = FocusWatcher()
    with _watcher_lock:
        _watcher = watcher
    watcher.start()

    server = ThreadingHTTPServer((HOST, PORT), Handler)
    for line in (
        "=" * 56,
        "  Focus Detect — live focus logger",
        "=" * 56,
        f"  Dashboard:  http://{HOST}:{PORT}/",
        f"  Events API: http://{HOST}:{PORT}/api/events",
        f"  Log file:   {log_store.path()}",
        "  Press Ctrl+C to stop",
        "=" * 56,
    ):
        print(line, flush=True)

    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nShutting down…")
    finally:
        watcher.stop()
        server.server_close()


if __name__ == "__main__":
    main()
