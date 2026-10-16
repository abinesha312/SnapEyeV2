"""Windows foreground-focus watcher using SetWinEventHook (ctypes only)."""

from __future__ import annotations

import ctypes
import threading
import time
from ctypes import wintypes
from datetime import datetime, timezone
from pathlib import Path
from typing import Callable, Optional

import log_store

user32 = ctypes.WinDLL("user32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

EVENT_SYSTEM_FOREGROUND = 0x0003
WINEVENT_OUTOFCONTEXT = 0x0000
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000

WinEventProcType = ctypes.WINFUNCTYPE(
    None,
    wintypes.HANDLE,  # hWinEventHook
    wintypes.DWORD,   # event
    wintypes.HWND,    # hwnd
    wintypes.LONG,    # idObject
    wintypes.LONG,    # idChild
    wintypes.DWORD,   # dwEventThread
    wintypes.DWORD,   # dwmsEventTime
)

user32.SetWinEventHook.restype = wintypes.HANDLE
user32.SetWinEventHook.argtypes = [
    wintypes.DWORD, wintypes.DWORD, wintypes.HMODULE,
    WinEventProcType, wintypes.DWORD, wintypes.DWORD, wintypes.DWORD,
]
user32.UnhookWinEvent.argtypes = [wintypes.HANDLE]
user32.GetForegroundWindow.restype = wintypes.HWND
user32.GetWindowTextLengthW.argtypes = [wintypes.HWND]
user32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
user32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
user32.GetWindowThreadProcessId.restype = wintypes.DWORD

kernel32.OpenProcess.restype = wintypes.HANDLE
kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
kernel32.QueryFullProcessImageNameW.argtypes = [
    wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD),
]


def _now_iso() -> str:
    return datetime.now().astimezone().isoformat(timespec="milliseconds")


def _window_title(hwnd: int) -> str:
    if not hwnd:
        return ""
    length = user32.GetWindowTextLengthW(hwnd)
    if length <= 0:
        return ""
    buf = ctypes.create_unicode_buffer(length + 1)
    user32.GetWindowTextW(hwnd, buf, length + 1)
    return buf.value


def _window_class(hwnd: int) -> str:
    if not hwnd:
        return ""
    buf = ctypes.create_unicode_buffer(256)
    user32.GetClassNameW(hwnd, buf, 256)
    return buf.value


def _process_name(pid: int) -> str:
    if not pid:
        return ""
    handle = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not handle:
        return ""
    try:
        size = wintypes.DWORD(260)
        buf = ctypes.create_unicode_buffer(size.value)
        if kernel32.QueryFullProcessImageNameW(handle, 0, buf, ctypes.byref(size)):
            return Path(buf.value).name
        return ""
    finally:
        kernel32.CloseHandle(handle)


def _is_snapeye(process: str, title: str, class_name: str) -> bool:
    # Match the SnapEye app process only — not IDE windows whose title contains "SnapEye".
    p = (process or "").lower()
    return p == "snapeye.exe" or p.endswith("\\snapeye.exe") or p == "snapeye"


def snapshot_foreground() -> Optional[dict]:
    hwnd = user32.GetForegroundWindow()
    if not hwnd:
        return None
    pid = wintypes.DWORD(0)
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    process = _process_name(pid.value)
    title = _window_title(hwnd)
    class_name = _window_class(hwnd)
    return {
        "ts": _now_iso(),
        "hwnd": hex(int(hwnd)),
        "pid": int(pid.value),
        "process": process,
        "title": title,
        "class_name": class_name,
        "is_snapeye": _is_snapeye(process, title, class_name),
    }


class FocusWatcher:
    """Hooks EVENT_SYSTEM_FOREGROUND and appends events to log_store."""

    def __init__(self, on_event: Optional[Callable[[dict], None]] = None):
        self._on_event = on_event
        self._hook = None
        self._proc = None  # keep callback alive
        self._thread: Optional[threading.Thread] = None
        self._stop = threading.Event()
        self._running = False
        self._last_hwnd = 0
        self._last_ts = 0.0
        self._debounce_s = 0.05

    @property
    def running(self) -> bool:
        return self._running

    def start(self) -> None:
        if self._running:
            return
        self._stop.clear()
        self._thread = threading.Thread(target=self._run, name="FocusWatcher", daemon=True)
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        if self._thread and self._thread.is_alive():
            self._thread.join(timeout=2.0)
        self._running = False

    def _emit(self, event: dict) -> None:
        log_store.append(event)
        if self._on_event:
            try:
                self._on_event(event)
            except Exception:
                pass

    def _handle_hwnd(self, hwnd: int) -> None:
        if not hwnd:
            return
        now = time.monotonic()
        if hwnd == self._last_hwnd and (now - self._last_ts) < self._debounce_s:
            return
        self._last_hwnd = hwnd
        self._last_ts = now

        pid = wintypes.DWORD(0)
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        process = _process_name(pid.value)
        title = _window_title(hwnd)
        class_name = _window_class(hwnd)
        event = {
            "ts": _now_iso(),
            "hwnd": hex(int(hwnd)),
            "pid": int(pid.value),
            "process": process,
            "title": title,
            "class_name": class_name,
            "is_snapeye": _is_snapeye(process, title, class_name),
        }
        self._emit(event)

    def _run(self) -> None:
        # Callback must be kept as a Python object for the lifetime of the hook.
        @WinEventProcType
        def _callback(hWinEventHook, event, hwnd, idObject, idChild, dwEventThread, dwmsEventTime):
            try:
                # Prefer the hwnd from the event; fall back to current foreground.
                target = int(hwnd) if hwnd else int(user32.GetForegroundWindow() or 0)
                self._handle_hwnd(target)
            except Exception as ex:
                print(f"[FocusWatcher] callback error: {ex}")

        self._proc = _callback
        self._hook = user32.SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,
            0,
            self._proc,
            0,
            0,
            WINEVENT_OUTOFCONTEXT,
        )
        if not self._hook:
            err = ctypes.get_last_error()
            print(f"[FocusWatcher] SetWinEventHook failed (Win32 {err})")
            self._running = False
            return

        self._running = True
        print("[FocusWatcher] Watching EVENT_SYSTEM_FOREGROUND")

        snap = snapshot_foreground()
        if snap:
            self._last_hwnd = int(snap["hwnd"], 16) if isinstance(snap["hwnd"], str) else 0
            self._emit(snap)

        # Message pump required for out-of-context WinEvent hooks.
        msg = wintypes.MSG()
        while not self._stop.is_set():
            # PeekMessage keeps the thread responsive to stop without blocking forever.
            has = user32.PeekMessageW(ctypes.byref(msg), 0, 0, 0, 1)  # PM_REMOVE = 1
            if has:
                user32.TranslateMessage(ctypes.byref(msg))
                user32.DispatchMessageW(ctypes.byref(msg))
            else:
                time.sleep(0.01)

        if self._hook:
            user32.UnhookWinEvent(self._hook)
            self._hook = None
        self._running = False
        print("[FocusWatcher] Stopped")


# PeekMessage / Translate / Dispatch prototypes
user32.PeekMessageW.argtypes = [
    ctypes.POINTER(wintypes.MSG), wintypes.HWND, wintypes.UINT, wintypes.UINT, wintypes.UINT,
]
user32.PeekMessageW.restype = wintypes.BOOL
user32.TranslateMessage.argtypes = [ctypes.POINTER(wintypes.MSG)]
user32.DispatchMessageW.argtypes = [ctypes.POINTER(wintypes.MSG)]
