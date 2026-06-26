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
    /// Makes SnapEye fully standalone: launches the Python backend as a hidden child
    /// process when no backend is already reachable, waits for /health, and guarantees
    /// the backend dies with the app via a Windows Job Object (kill-on-job-close), so
    /// no external launcher, watchdog, or background monitor is ever required.
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

        /// <summary>True when a backend was already running and we didn't spawn one.</summary>
        public static bool UsingExternalBackend { get; private set; }

        /// <summary>
        /// Ensure a backend is available: reuse an already-running one, otherwise
        /// spawn our own. Safe to call once at startup; never throws.
        /// </summary>
        public static Task<bool> EnsureBackendAsync()
        {
            Ready = EnsureBackendCoreAsync();
            return Ready;
        }

        private static async Task<bool> EnsureBackendCoreAsync()
        {
            try
            {
                // A backend may already be running (developer started it manually).
                if (await IsHealthyAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false))
                {
                    UsingExternalBackend = true;
                    Trace.WriteLine("[Backend] External backend already running, reusing it");
                    return true;
                }

                string? backendDir = FindBackendDirectory();
                if (backendDir == null)
                {
                    Trace.WriteLine("[Backend] backend folder not found; set SNAPEYE_BACKEND_DIR to enable auto-start");
                    return false;
                }

                string python = FindPython(backendDir);
                Trace.WriteLine($"[Backend] Starting: \"{python}\" run_server.py (cwd={backendDir})");

                OpenLog();

                var psi = new ProcessStartInfo
                {
                    FileName = python,
                    Arguments = "run_server.py",
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
                    Trace.WriteLine("[Backend] Process.Start returned false");
                    return false;
                }

                backendProcess = process;
                AttachKillOnCloseJob(process);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                bool healthy = await WaitForHealthyAsync(process, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                Trace.WriteLine(healthy
                    ? "[Backend] Backend is up and healthy"
                    : "[Backend] Backend did not become healthy in time (see backend.log)");
                return healthy;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Backend] Auto-start failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Stop the backend we spawned (no-op for an external backend).</summary>
        public static void Stop()
        {
            try
            {
                if (backendProcess != null && !backendProcess.HasExited)
                {
                    Trace.WriteLine("[Backend] Stopping backend process");
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

        private static async Task<bool> WaitForHealthyAsync(Process process, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (process.HasExited)
                {
                    Trace.WriteLine($"[Backend] Process exited early with code {process.ExitCode}");
                    return false;
                }
                if (await IsHealthyAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false))
                    return true;
                await Task.Delay(500).ConfigureAwait(false);
            }
            return false;
        }

        // === Locating the backend and python ===

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

        private static string FindPython(string backendDir)
        {
            string? repoRoot = Directory.GetParent(backendDir)?.FullName;
            var candidates = new[]
            {
                Path.Combine(backendDir, "venv", "Scripts", "python.exe"),
                Path.Combine(backendDir, ".venv", "Scripts", "python.exe"),
                repoRoot == null ? null : Path.Combine(repoRoot, "venv", "Scripts", "python.exe"),
                repoRoot == null ? null : Path.Combine(repoRoot, ".venv", "Scripts", "python.exe"),
            };
            foreach (var c in candidates)
            {
                if (c != null && File.Exists(c))
                    return c;
            }
            return "python"; // fall back to PATH
        }

        // === Logging ===

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

        // === Job object: the OS kills the backend whenever the app exits, even on a
        // hard crash, so no watchdog or monitoring process is ever needed. ===

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
