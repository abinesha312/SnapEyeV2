using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SnapEye.Config;

namespace SnapEye.Services
{
    /// <summary>
    /// Checks whether the SnapEye backend (ChromaDB + FastAPI, run via Podman Compose -
    /// see podman-compose.yml / docs/backend-deployment.md) is reachable. The backend is
    /// a long-lived service started once (e.g. at Windows logon via the "SnapEye Backend"
    /// scheduled task), independent of this app's lifecycle - so under normal operation
    /// this service only polls /health, it never launches or kills the backend itself.
    ///
    /// For engineers without Podman installed locally, setting the environment variable
    /// SNAPEYE_DEV_SPAWN_BACKEND=1 restores the old behavior of spawning
    /// `python run_server.py` directly as a hidden child process.
    /// </summary>
    public static class BackendProcessService
    {
        private static Process? backendProcess;
        private static IntPtr jobHandle = IntPtr.Zero;
        private static StreamWriter? logWriter;
        private static readonly object logLock = new object();

        /// <summary>
        /// Completes when the backend is reachable (true) or could not be made
        /// reachable (false). Other services may await this before first contact.
        /// </summary>
        public static Task<bool> Ready { get; private set; } = Task.FromResult(false);

        /// <summary>True when we're using a backend we didn't spawn ourselves (the normal case).</summary>
        public static bool UsingExternalBackend { get; private set; }

        /// <summary>
        /// Human-readable reason the backend isn't reachable (e.g. Podman stack not started).
        /// Empty when things are fine. Surfaced in the UI so the user sees the real cause
        /// instead of a generic error.
        /// </summary>
        public static string LastStartError { get; private set; } = string.Empty;

        private static readonly object startLock = new object();
        private static bool podmanStartAttempted;

        private static bool DevSpawnEnabled =>
            Environment.GetEnvironmentVariable("SNAPEYE_DEV_SPAWN_BACKEND") == "1";

        /// <summary>True while a Podman Compose bring-up was triggered and we're waiting on /health.</summary>
        public static bool PodmanStackStarting { get; private set; }

        /// <summary>
        /// Ensure a backend is reachable: check /health, and only fall back to spawning our
        /// own process when SNAPEYE_DEV_SPAWN_BACKEND=1 is set. Idempotent and safe to call
        /// repeatedly (e.g. at startup AND again when Listen is pressed): a check already in
        /// flight is reused. Never throws.
        /// </summary>
        public static Task<bool> EnsureBackendAsync()
        {
            lock (startLock)
            {
                var current = Ready;

                // A check/start is already running — reuse it instead of duplicating it.
                if (!current.IsCompleted)
                    return current;

                // Previously succeeded and (if we spawned it ourselves) the process is still
                // alive — nothing to do.
                if (current.Status == TaskStatus.RanToCompletion && current.Result)
                {
                    if (UsingExternalBackend || (backendProcess != null && !backendProcess.HasExited))
                        return current;
                }

                // Otherwise (never checked, failed, or a dev-spawned process died): (re)check.
                Ready = EnsureBackendCoreAsync();
                return Ready;
            }
        }

        private static async Task<bool> EnsureBackendCoreAsync()
        {
            try
            {
                LastStartError = string.Empty;

                // Normal path: the backend is managed by Podman Compose and should already
                // be up. We only check reachability - starting/stopping it is Podman's job.
                if (await IsHealthyAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false))
                {
                    UsingExternalBackend = true;
                    Trace.WriteLine("[Backend] Backend is reachable");
                    return true;
                }

                if (!DevSpawnEnabled)
                {
                    // Give a backend that's mid-startup (scheduled task / prior compose up)
                    // a short window before we try to start the stack ourselves.
                    bool healthy = await WaitForHealthyPollAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                    if (healthy)
                    {
                        UsingExternalBackend = true;
                        return true;
                    }

                    // Auto-start Podman Compose once per process so Listen doesn't stall
                    // 10–30s waiting for a stack the user never manually launched.
                    if (!podmanStartAttempted)
                    {
                        podmanStartAttempted = true;
                        if (TryStartPodmanStack())
                        {
                            PodmanStackStarting = true;
                            try
                            {
                                healthy = await WaitForHealthyPollAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
                                if (healthy)
                                {
                                    UsingExternalBackend = true;
                                    Trace.WriteLine("[Backend] Podman stack is up and healthy");
                                    return true;
                                }
                            }
                            finally
                            {
                                PodmanStackStarting = false;
                            }
                        }
                    }

                    LastStartError =
                        "Backend not running. Start it via scripts\\start-snapeye-backend.ps1 " +
                        "(see docs\\backend-deployment.md) or install the SnapEye Backend scheduled task.";
                    Trace.WriteLine("[Backend] Not reachable after Podman auto-start attempt");
                    return false;
                }

                return await DevSpawnBackendAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LastStartError = ex.Message;
                Trace.WriteLine($"[Backend] EnsureBackendCoreAsync failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Stop the backend we spawned in dev-mode (no-op otherwise - Podman owns its lifecycle).</summary>
        public static void Stop()
        {
            try
            {
                if (backendProcess != null && !backendProcess.HasExited)
                {
                    Trace.WriteLine("[Backend] Stopping dev-spawned backend process");
                    backendProcess.Kill(entireProcessTree: true);
                    backendProcess.WaitForExit(3000);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Backend] Stop failed: {ex.Message}");
            }
            finally
            {
                backendProcess?.Dispose();
                backendProcess = null;
                if (jobHandle != IntPtr.Zero)
                {
                    CloseHandle(jobHandle);
                    jobHandle = IntPtr.Zero;
                }
                lock (logLock)
                {
                    logWriter?.Dispose();
                    logWriter = null;
                }
            }
        }

        // === Health checking ===

        private static async Task<bool> IsHealthyAsync(TimeSpan timeout)
        {
            try
            {
                using var client = new HttpClient { Timeout = timeout };
                var response = await client.GetAsync($"{AppConfig.BackendHttpUrl}/health").ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Poll /health every 500ms until reachable or the timeout elapses.</summary>
        private static async Task<bool> WaitForHealthyPollAsync(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (await IsHealthyAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false))
                    return true;
                await Task.Delay(500).ConfigureAwait(false);
            }
            return false;
        }

        // === Dev-only fallback: spawn `python run_server.py` directly ===
        // Only reached when SNAPEYE_DEV_SPAWN_BACKEND=1 is set. Not used in normal operation,
        // where the backend is started once via Podman Compose (see podman-compose.yml).

        private static async Task<bool> DevSpawnBackendAsync()
        {
            string? backendDir = FindBackendDirectory();
            if (backendDir == null)
            {
                LastStartError = "backend folder not found (set SNAPEYE_BACKEND_DIR)";
                Trace.WriteLine("[Backend] backend folder not found; set SNAPEYE_BACKEND_DIR to enable dev auto-start");
                return false;
            }

            try
            {
                var (python, argPrefix) = FindPythonInvocation(backendDir);
                string arguments = argPrefix + "run_server.py";
                Trace.WriteLine($"[Backend] Dev-spawning: \"{python}\" {arguments} (cwd={backendDir})");

                OpenLog();

                var psi = new ProcessStartInfo
                {
                    FileName = python,
                    Arguments = arguments,
                    WorkingDirectory = backendDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
                psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

                var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, e) => WriteLog(e.Data);
                process.ErrorDataReceived += (_, e) => WriteLog(e.Data);

                if (!process.Start())
                {
                    LastStartError = "could not launch Python process";
                    Trace.WriteLine("[Backend] Process.Start returned false");
                    return false;
                }

                backendProcess = process;
                AttachKillOnCloseJob(process);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                bool healthy = await WaitForHealthyAsync(process, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                Trace.WriteLine(healthy
                    ? "[Backend] Dev-spawned backend is up and healthy"
                    : "[Backend] Dev-spawned backend did not become healthy in time (see backend.log)");
                if (!healthy && string.IsNullOrEmpty(LastStartError))
                    LastStartError = "backend did not become healthy in time (see %AppData%\\SnapEye\\logs\\backend.log)";
                return healthy;
            }
            catch (System.ComponentModel.Win32Exception wex)
            {
                // Almost always "python not found on PATH".
                LastStartError = $"Python not found ({wex.Message}). Install Python or set up backend/venv.";
                Trace.WriteLine($"[Backend] Dev auto-start failed (python missing?): {wex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                LastStartError = ex.Message;
                Trace.WriteLine($"[Backend] Dev auto-start failed: {ex.Message}");
                return false;
            }
        }

        private static async Task<bool> WaitForHealthyAsync(Process process, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (process.HasExited)
                {
                    LastStartError = $"backend process exited early (code {process.ExitCode}); see %AppData%\\SnapEye\\logs\\backend.log";
                    Trace.WriteLine($"[Backend] Process exited early with code {process.ExitCode}");
                    return false;
                }
                if (await IsHealthyAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false))
                    return true;
                await Task.Delay(500).ConfigureAwait(false);
            }
            return false;
        }

        /// <summary>
        /// Fire-and-forget launch of scripts/start-snapeye-backend.ps1 (Podman machine +
        /// podman-compose up -d). Returns false when the script can't be found or Podman
        /// isn't installed — callers fall back to a clear LastStartError.
        /// </summary>
        private static bool TryStartPodmanStack()
        {
            try
            {
                string? script = FindStartBackendScript();
                if (script == null)
                {
                    Trace.WriteLine("[Backend] start-snapeye-backend.ps1 not found; skipping Podman auto-start");
                    return false;
                }

                Trace.WriteLine($"[Backend] Auto-starting Podman stack: {script}");
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-ExecutionPolicy Bypass -NoProfile -WindowStyle Hidden -File \"{script}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Backend] Podman auto-start failed: {ex.Message}");
                return false;
            }
        }

        private static string? FindStartBackendScript()
        {
            foreach (var start in new[] { AppDomain.CurrentDomain.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                var dir = new DirectoryInfo(start);
                for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
                {
                    string candidate = Path.Combine(dir.FullName, "scripts", "start-snapeye-backend.ps1");
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            return null;
        }

        // === Locating the backend and python (dev-mode only) ===

        private static string? FindBackendDirectory()
        {
            // Explicit override wins
            string? overrideDir = Environment.GetEnvironmentVariable("SNAPEYE_BACKEND_DIR");
            if (!string.IsNullOrEmpty(overrideDir) && File.Exists(Path.Combine(overrideDir, "run_server.py")))
                return overrideDir;

            // Walk up from both the exe location (dev: bin\Debug\net8.0-windows) and the
            // current directory looking for a sibling "backend" folder.
            foreach (var start in new[] { AppDomain.CurrentDomain.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                var dir = new DirectoryInfo(start);
                for (int i = 0; i < 7 && dir != null; i++, dir = dir.Parent)
                {
                    string candidate = Path.Combine(dir.FullName, "backend");
                    if (File.Exists(Path.Combine(candidate, "run_server.py")))
                        return candidate;
                }
            }
            return null;
        }

        /// <summary>
        /// Resolves how to launch Python for the backend. Prefers a project venv, then a
        /// <c>python.exe</c> resolvable on PATH (covers conda/base and system installs), then
        /// the <c>py</c> launcher. Returns the executable plus the argument prefix so callers
        /// can prepend it to <c>run_server.py</c>. Falls back to bare "python" (which surfaces
        /// a clear error via the Win32Exception handler if Python truly isn't installed).
        /// </summary>
        private static (string fileName, string argPrefix) FindPythonInvocation(string backendDir)
        {
            string? repoRoot = Directory.GetParent(backendDir)?.FullName;
            var venvCandidates = new[]
            {
                Path.Combine(backendDir, "venv", "Scripts", "python.exe"),
                Path.Combine(backendDir, ".venv", "Scripts", "python.exe"),
                repoRoot == null ? null : Path.Combine(repoRoot, "venv", "Scripts", "python.exe"),
                repoRoot == null ? null : Path.Combine(repoRoot, ".venv", "Scripts", "python.exe"),
            };
            foreach (var c in venvCandidates)
            {
                if (c != null && File.Exists(c))
                    return (c, "");
            }

            // No venv (common on this machine): use whatever python is on PATH.
            var onPath = ResolveExecutableOnPath("python.exe");
            if (onPath != null)
                return (onPath, "");

            // Windows py launcher as a last structured attempt.
            var py = ResolveExecutableOnPath("py.exe");
            if (py != null)
                return (py, "-3 ");

            return ("python", ""); // last resort; Win32Exception handler explains if missing
        }

        /// <summary>Finds an executable by scanning the PATH environment variable.</summary>
        private static string? ResolveExecutableOnPath(string exeName)
        {
            try
            {
                string path = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in path.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string full;
                    try { full = Path.Combine(dir.Trim(), exeName); }
                    catch { continue; }
                    if (File.Exists(full))
                        return full;
                }
            }
            catch { /* best effort */ }
            return null;
        }

        // === Logging (dev-mode only) ===

        private static void OpenLog()
        {
            try
            {
                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnapEye", "logs");
                Directory.CreateDirectory(logDir);
                lock (logLock)
                {
                    logWriter = new StreamWriter(Path.Combine(logDir, "backend.log"), append: false) { AutoFlush = true };
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Backend] Could not open backend.log: {ex.Message}");
            }
        }

        private static void WriteLog(string? line)
        {
            if (line == null) return;
            lock (logLock)
            {
                try { logWriter?.WriteLine(line); } catch { /* best effort */ }
            }
        }

        // === Job object (dev-mode only): the OS kills the dev-spawned backend whenever the
        // app exits, even on a hard crash, so no watchdog or monitoring process is needed. ===

        private static void AttachKillOnCloseJob(Process process)
        {
            try
            {
                jobHandle = CreateJobObject(IntPtr.Zero, null);
                if (jobHandle == IntPtr.Zero) return;

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                    },
                };

                int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                IntPtr infoPtr = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(info, infoPtr, false);
                    if (SetInformationJobObject(jobHandle, JobObjectExtendedLimitInformation, infoPtr, (uint)length))
                        AssignProcessToJobObject(jobHandle, process.Handle);
                }
                finally
                {
                    Marshal.FreeHGlobal(infoPtr);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Backend] Job object setup failed (backend still stops on normal exit): {ex.Message}");
            }
        }

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);

        [DllImport("kernel32.dll")]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
