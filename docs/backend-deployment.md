# Backend Deployment (Podman)

The SnapEye backend (FastAPI + ChromaDB) runs as a long-lived Podman Compose stack instead
of being spawned by the WPF desktop app on every launch. The app just connects to whatever
is listening on `http://localhost:8080` - see `Application/services/BackendProcessService.cs`.

## One-time setup

1. Install [Podman Desktop](https://podman-desktop.io/) (includes `podman` and `podman machine`).
2. Install `podman-compose`: `pip install podman-compose` (or via your package manager).
3. Populate `backend/.env` with your API keys (`OPENAI_API_KEY`, `DEEPGRAM_API_KEY`, etc.) -
   see `backend/.env.example`. This file is mounted into the backend container via `env_file`.

## Starting the stack

```powershell
scripts\start-snapeye-backend.ps1
```

or directly:

```powershell
podman machine start
podman-compose up -d
```

This brings up two containers:

- `snapeye-chromadb` - ChromaDB in server mode, port `8000`, data persisted in the
  `chromadb_data` volume.
- `snapeye-backend` - the FastAPI app, port `8080`, SQLite data persisted in the
  `snapeye_sqlite` volume, connects to ChromaDB via `CHROMA_HOST=chromadb`.

Both have `restart: unless-stopped`, so they survive reboots once started and don't need to
be re-launched by hand every session.

## Running automatically at logon

Register the start script as a Windows Task Scheduler task so the stack is warm before you
ever open the SnapEye app:

```powershell
schtasks /Create /TN "SnapEye Backend" /TR "powershell -ExecutionPolicy Bypass -File `"$PWD\scripts\start-snapeye-backend.ps1`"" /SC ONLOGON /RL LIMITED
```

## Verifying it's up

```powershell
curl http://localhost:8080/health
curl http://localhost:8000/api/v2/heartbeat   # ChromaDB heartbeat (path may vary by version)
podman ps
```

## Local development without Podman

If `CHROMA_HOST` is unset, `backend/services/rag_service.py` falls back to an embedded
ChromaDB `PersistentClient` writing to `backend/data/chromadb/` - no containers required.
Set the environment variable `SNAPEYE_DEV_SPAWN_BACKEND=1` before launching the WPF app to
restore the old behavior of the app spawning `python run_server.py` itself, for engineers
who don't have Podman installed locally.
