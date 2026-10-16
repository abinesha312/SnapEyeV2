# Focus Detect

Live Windows **foreground / focus** logger with an HTML dashboard. Use it while testing SnapEye to confirm the overlay does not steal keyboard focus.

## Run

```powershell
cd Detect
python -u server.py
```

Open **http://127.0.0.1:8765/**

Events are appended to `data/focus_events.json` (last 2000 kept).

## E2E check with SnapEye

1. Start Focus Detect and open the dashboard.
2. Click Notepad (or your editor) and type — the feed should show that process.
3. Use SnapEye: hold `Ctrl+Alt+arrows`, drag the resize grip, `Ctrl+Shift+P`, camera.
4. If **SnapEye** badge rows appear while you are still typing in Notepad, focus was stolen.

## API

| Method | Path | Description |
|--------|------|-------------|
| GET | `/` | Dashboard |
| GET | `/api/events?since=N` | Events from index N |
| GET | `/api/status` | Watcher status + last event |
| POST | `/api/clear` | Wipe the log |

## Requirements

- Windows
- Python 3.10+ (stdlib only — see `requirements.txt`)
