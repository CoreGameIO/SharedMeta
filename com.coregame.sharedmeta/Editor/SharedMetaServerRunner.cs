#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SharedMeta.Editor
{
    /// <summary>
    /// Tracks server process PID across domain reloads and ensures cleanup on Editor quit.
    /// </summary>
    [InitializeOnLoad]
    static class SharedMetaServerRunnerTracker
    {
        static SharedMetaServerRunnerTracker()
        {
            int pid = EditorPrefs.GetInt("SharedMeta_Server_PID", -1);
            if (pid > 0)
            {
                try
                {
                    var proc = Process.GetProcessById(pid);
                    if (!proc.HasExited && IsLikelyServerProcess(proc.ProcessName))
                    {
                        SharedMetaServerRunner.ReacquireProcess(proc, pid);
                    }
                    else
                    {
                        EditorPrefs.SetInt("SharedMeta_Server_PID", -1);
                    }
                }
                catch
                {
                    EditorPrefs.SetInt("SharedMeta_Server_PID", -1);
                }
            }

            EditorApplication.quitting += OnEditorQuitting;
        }

        static bool IsLikelyServerProcess(string processName)
        {
            return string.Equals(processName, "dotnet", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(processName, "cmd", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(processName, "bash", StringComparison.OrdinalIgnoreCase);
        }

        static void OnEditorQuitting()
        {
            SharedMetaServerRunner.StopServerStatic();
        }
    }

    /// <summary>
    /// Unity Editor window for running a SharedMeta server directly from the Editor.
    /// Provides Start/Stop controls, console output, and IDE integration.
    /// Output is redirected to a file via shell wrapper so it survives domain reloads.
    /// </summary>
    public class SharedMetaServerRunner : EditorWindow
    {
        // --- Constants ---
        const int MaxLogLines = 2000;
        const double GracefulShutdownTimeout = 60.0;

        // --- EditorPrefs keys (transient process state) ---
        const string PrefPID = "SharedMeta_Server_PID";
        const string PrefLogClearPos = "SharedMeta_Server_LogClearPos";

        // --- Persisted settings ---
        string _serverCsprojPath = "";
        string _dotnetArgs = "";
        string _serverUrl = "http://localhost:5000";
        bool _autoScroll = true;

        // --- Static process tracking ---
        static int _trackedPid = -1;
        static Process? _serverProcess;
        static volatile bool _needsRepaint;
        static volatile bool _serverListening;

        // --- Static log buffer ---
        static readonly List<LogEntry> s_logBuffer = new List<LogEntry>();
        static readonly object s_logLock = new object();

        // --- File-based output (survives domain reload) ---
        static string LogFilePath => Path.Combine(ProjectRoot, "Library", "SharedMetaServerRunner.log");
        static long _logFileReadPos;
        static string _logPartialLine = "";
        static readonly object s_pollLock = new object();

        // --- UI state ---
        Vector2 _logScrollPos;
        string _searchFilter = "";
        ServerStatus _status = ServerStatus.Stopped;
        readonly List<LogEntry> _displayedEntries = new List<LogEntry>();

        // --- Styles (lazy-init) ---
        GUIStyle? _logStyle;
        GUIStyle? _logStyleError;
        GUIStyle? _logStyleWarning;

        enum ServerStatus { Stopped, Starting, Running, Stopping }

        struct LogEntry
        {
            public string Text;
            public LogType Type;
            public DateTime Timestamp;
        }

        // =====================================================================
        // Menu
        // =====================================================================

        [MenuItem("SharedMeta/Server Runner")]
        public static void ShowWindow()
        {
            var window = GetWindow<SharedMetaServerRunner>("SharedMeta Server");
            window.minSize = new Vector2(500, 400);
        }

        // =====================================================================
        // Lifecycle
        // =====================================================================

        void OnEnable()
        {
            LoadSettings();
            TryAutoDetectCsproj();
            RestoreLogFromFile();

            if (_trackedPid > 0 && (_serverProcess == null || _serverProcess.HasExited))
                TryReacquireProcess();

            SyncStatus();
            EditorApplication.update += OnEditorUpdate;
        }

        void OnDisable()
        {
            SaveSettings();
            EditorApplication.update -= OnEditorUpdate;
        }

        static void OnEditorUpdate()
        {
            if (_trackedPid > 0)
            {
                PollLogFile();

                // Detect process exit promptly
                if (_serverProcess != null)
                {
                    try
                    {
                        if (_serverProcess.HasExited)
                        {
                            PollLogFile(); // final flush
                            _needsRepaint = true;
                        }
                    }
                    catch { /* process handle may be invalid */ }
                }
            }

            if (_needsRepaint)
            {
                _needsRepaint = false;
                var windows = Resources.FindObjectsOfTypeAll<SharedMetaServerRunner>();
                foreach (var w in windows)
                {
                    w.SyncStatus();
                    w.Repaint();
                }
            }
        }

        // =====================================================================
        // GUI
        // =====================================================================

        void OnGUI()
        {
            EnsureStyles();
            DrawToolbar();
            DrawConsole();
        }

        void DrawToolbar()
        {
            // --- Row 1: .csproj path ---
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Server Project", GUILayout.Width(100));
            _serverCsprojPath = EditorGUILayout.TextField(_serverCsprojPath);

            if (GUILayout.Button("...", GUILayout.Width(30)))
            {
                var dir = string.IsNullOrEmpty(_serverCsprojPath)
                    ? ""
                    : Path.GetDirectoryName(_serverCsprojPath) ?? "";
                var selected = EditorUtility.OpenFilePanel("Select Server .csproj", dir, "csproj");
                if (!string.IsNullOrEmpty(selected))
                {
                    _serverCsprojPath = selected;
                    SaveSettings();
                }
            }

            if (GUILayout.Button("IDE", GUILayout.Width(40)))
                OpenInIDE();

            if (GUILayout.Button("Reveal", GUILayout.Width(50)))
                RevealInExplorer();

            EditorGUILayout.EndHorizontal();

            // --- Row 2: Extra arguments ---
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Extra Args", GUILayout.Width(100));
            _dotnetArgs = EditorGUILayout.TextField(_dotnetArgs);
            EditorGUILayout.EndHorizontal();

            // --- Row 3: URL + Status ---
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Server URL", GUILayout.Width(100));
            _serverUrl = EditorGUILayout.TextField(_serverUrl);
            GUILayout.FlexibleSpace();
            DrawStatusIndicator();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);

            // --- Row 4: Action buttons ---
            EditorGUILayout.BeginHorizontal();

            bool running = IsServerRunning();

            EditorGUI.BeginDisabledGroup(running || string.IsNullOrEmpty(_serverCsprojPath));
            if (GUILayout.Button("\u25b6 Start", GUILayout.Height(26)))
                StartServer();
            EditorGUI.EndDisabledGroup();

            EditorGUI.BeginDisabledGroup(!running);
            if (GUILayout.Button("\u25a0 Stop", GUILayout.Height(26)))
                StopServer();
            EditorGUI.EndDisabledGroup();

            if (GUILayout.Button("Clear", GUILayout.Height(26), GUILayout.Width(60)))
                ClearLog();

            GUILayout.FlexibleSpace();
            _autoScroll = GUILayout.Toggle(_autoScroll, "Auto-scroll", GUILayout.Width(85));

            EditorGUILayout.EndHorizontal();

            // --- Warning if no csproj ---
            if (string.IsNullOrEmpty(_serverCsprojPath))
            {
                EditorGUILayout.HelpBox(
                    "Select a server .csproj file to get started.",
                    MessageType.Warning);
            }

            EditorGUILayout.Space(2);
        }

        void DrawStatusIndicator()
        {
            var prev = GUI.color;
            string label;
            switch (_status)
            {
                case ServerStatus.Starting:
                    GUI.color = Color.yellow;
                    label = "\u25cf Starting";
                    break;
                case ServerStatus.Running:
                    GUI.color = Color.green;
                    label = "\u25cf Running";
                    break;
                case ServerStatus.Stopping:
                    GUI.color = new Color(1f, 0.6f, 0f);
                    label = "\u25cf Stopping";
                    break;
                default:
                    GUI.color = Color.gray;
                    label = "\u25cf Stopped";
                    break;
            }
            GUILayout.Label(label, GUILayout.Width(80));
            GUI.color = prev;
        }

        void DrawConsole()
        {
            // --- Search bar ---
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("Console", EditorStyles.boldLabel, GUILayout.Width(55));
            GUILayout.FlexibleSpace();
            _searchFilter = EditorGUILayout.TextField(_searchFilter,
                EditorStyles.toolbarSearchField, GUILayout.Width(200));
            EditorGUILayout.EndHorizontal();

            // Snapshot visible entries during Layout to keep control count
            // consistent between Layout and Repaint (avoids IMGUI mismatch).
            if (Event.current.type == EventType.Layout)
            {
                _displayedEntries.Clear();
                lock (s_logLock)
                {
                    bool hasFilter = !string.IsNullOrEmpty(_searchFilter);
                    for (int i = 0; i < s_logBuffer.Count; i++)
                    {
                        var entry = s_logBuffer[i];
                        if (hasFilter &&
                            entry.Text.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        _displayedEntries.Add(entry);
                    }
                }
            }

            // --- Log area ---
            _logScrollPos = EditorGUILayout.BeginScrollView(_logScrollPos);

            for (int i = 0; i < _displayedEntries.Count; i++)
            {
                var entry = _displayedEntries[i];

                GUIStyle style;
                switch (entry.Type)
                {
                    case LogType.Error:
                        style = _logStyleError!;
                        break;
                    case LogType.Warning:
                        style = _logStyleWarning!;
                        break;
                    default:
                        style = _logStyle!;
                        break;
                }

                var timestamp = entry.Timestamp.ToString("HH:mm:ss");
                EditorGUILayout.SelectableLabel(
                    $"[{timestamp}] {entry.Text}",
                    style,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }

            if (_autoScroll)
                _logScrollPos.y = float.MaxValue;

            EditorGUILayout.EndScrollView();
        }

        void EnsureStyles()
        {
            if (_logStyle != null) return;

            _logStyle = new GUIStyle(EditorStyles.label)
            {
                richText = false,
                wordWrap = false,
                font = EditorStyles.label.font,
            };

            _logStyleError = new GUIStyle(_logStyle)
            {
                normal = { textColor = new Color(1f, 0.4f, 0.4f) },
            };

            _logStyleWarning = new GUIStyle(_logStyle)
            {
                normal = { textColor = Color.yellow },
            };
        }

        // =====================================================================
        // Process Management
        // =====================================================================

        void StartServer()
        {
            if (IsServerRunning()) return;

            var csproj = ResolveCsprojPath();
            if (!File.Exists(csproj))
            {
                EditorUtility.DisplayDialog("SharedMeta Server",
                    $"Server project not found:\n{csproj}", "OK");
                return;
            }

            ClearLog();
            EditorPrefs.SetString(PrefLogClearPos, "0"); // new run — show all output
            _status = ServerStatus.Starting;
            _serverListening = false;
            _logFileReadPos = 0;
            _logPartialLine = "";

            var logFile = LogFilePath;
            var dotnetArgs = $"run --project \"{csproj}\"";
            if (!string.IsNullOrEmpty(_dotnetArgs))
                dotnetArgs += " " + _dotnetArgs;

            var workDir = Path.GetDirectoryName(csproj) ?? "";

            // Output is redirected to a file via shell wrapper so it survives domain reloads.
            try
            {
#if UNITY_EDITOR_WIN
                // CreateProcess with CREATE_NEW_PROCESS_GROUP so we can later send
                // a targeted CTRL_BREAK for graceful shutdown without affecting Unity.
                var si = new STARTUPINFOW();
                si.cb = Marshal.SizeOf(si);
                var cmdLine = new StringBuilder(
                    $"cmd.exe /c dotnet {dotnetArgs} >\"{logFile}\" 2>&1");

                if (!CreateProcessW(null!, cmdLine, IntPtr.Zero, IntPtr.Zero, false,
                    CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW, IntPtr.Zero, workDir,
                    ref si, out var pi))
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }

                CloseHandle(pi.hThread);
                _serverProcess = Process.GetProcessById(pi.dwProcessId);
                CloseHandle(pi.hProcess);
                _serverProcess.EnableRaisingEvents = true;
                _serverProcess.Exited += OnProcessExited;
#else
                var startInfo = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = $"-c 'dotnet {dotnetArgs} >\"{logFile}\" 2>&1'",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = workDir,
                };
                _serverProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                _serverProcess.Exited += OnProcessExited;
                _serverProcess.Start();
#endif

                _trackedPid = _serverProcess.Id;
                EditorPrefs.SetInt(PrefPID, _trackedPid);

                AppendLog($"--- Started: dotnet {dotnetArgs} ---", LogType.Log);
                AppendLog($"--- PID: {_trackedPid}, WorkDir: {workDir} ---", LogType.Log);
            }
            catch (Exception ex)
            {
                _status = ServerStatus.Stopped;
                AppendLog($"--- Failed to start: {ex.Message} ---", LogType.Error);
                EditorUtility.DisplayDialog("SharedMeta Server",
                    $"Failed to start server:\n{ex.Message}\n\nMake sure 'dotnet' CLI is in your PATH.",
                    "OK");
            }
        }

        void StopServer()
        {
            if (_serverProcess == null || _serverProcess.HasExited)
            {
                if (_trackedPid > 0)
                {
                    ForceKillProcessTree(_trackedPid);
                    AppendLog($"--- Killed process tree (PID: {_trackedPid}) ---", LogType.Warning);
                }
                _status = ServerStatus.Stopped;
                _serverListening = false;
                ClearPid();
                return;
            }

            _status = ServerStatus.Stopping;
            AppendLog("--- Stopping server (graceful)... ---", LogType.Warning);

            // Send graceful shutdown signal first
            SendGracefulShutdown(_serverProcess.Id);

            // Schedule a force-kill check after timeout
            double killTime = EditorApplication.timeSinceStartup + GracefulShutdownTimeout;
            void ForceKillCheck()
            {
                if (_serverProcess == null || _serverProcess.HasExited)
                {
                    EditorApplication.update -= ForceKillCheck;
                    return;
                }
                if (EditorApplication.timeSinceStartup > killTime)
                {
                    AppendLog("--- Graceful shutdown timed out, force killing... ---", LogType.Error);
                    ForceKillProcessTree(_serverProcess.Id);
                    EditorApplication.update -= ForceKillCheck;
                }
            }
            EditorApplication.update += ForceKillCheck;
        }

        /// <summary>
        /// Static stop for use from EditorApplication.quitting callback.
        /// </summary>
        internal static void StopServerStatic()
        {
            int pid = -1;
            if (_serverProcess != null && !_serverProcess.HasExited)
                pid = _serverProcess.Id;
            else if (_trackedPid > 0)
                pid = _trackedPid;

            if (pid > 0)
            {
                // Attempt graceful shutdown, then force kill if still alive
                SendGracefulShutdown(pid);
                try { _serverProcess?.WaitForExit(2000); } catch { }

                try
                {
                    var check = Process.GetProcessById(pid);
                    if (!check.HasExited)
                        ForceKillProcessTree(pid);
                }
                catch { /* already exited */ }
            }

            _serverListening = false;
            ClearPid();
        }

        void OnProcessExited(object sender, EventArgs e)
        {
            PollLogFile(); // flush remaining output
            int code = -1;
            try { code = _serverProcess?.ExitCode ?? -1; } catch { }
            AppendLog($"--- Server exited (code {code}) ---", LogType.Warning);
            _status = ServerStatus.Stopped;
            _serverListening = false;
            ClearPid();
        }

        bool IsServerRunning()
        {
            if (_serverProcess != null && !_serverProcess.HasExited)
            {
                if (_status == ServerStatus.Stopped)
                    _status = ServerStatus.Running;
                if (_serverListening && _status == ServerStatus.Starting)
                    _status = ServerStatus.Running;
                return true;
            }

            if (_trackedPid > 0)
            {
                try
                {
                    var proc = Process.GetProcessById(_trackedPid);
                    if (!proc.HasExited)
                    {
                        _serverProcess = proc;
                        if (_status == ServerStatus.Stopped)
                            _status = ServerStatus.Running;
                        return true;
                    }
                }
                catch { }
                ClearPid();
            }

            if (_status != ServerStatus.Stopped)
            {
                _status = ServerStatus.Stopped;
                _serverListening = false;
            }
            return false;
        }

        void SyncStatus()
        {
            IsServerRunning(); // side-effect: updates _status
        }

        void TryReacquireProcess()
        {
            if (_trackedPid <= 0) return;
            try
            {
                var proc = Process.GetProcessById(_trackedPid);
                if (!proc.HasExited)
                {
                    _serverProcess = proc;
                    _status = ServerStatus.Running;
                    return;
                }
            }
            catch { }
            ClearPid();
        }

        /// <summary>
        /// Called by SharedMetaServerRunnerTracker on domain reload.
        /// </summary>
        internal static void ReacquireProcess(Process proc, int pid)
        {
            _serverProcess = proc;
            _trackedPid = pid;
            RestoreLogFromFile();
        }

        // =====================================================================
        // File-based output polling
        // =====================================================================

        /// <summary>
        /// Read new content from the log file written by the shell-redirected server process.
        /// Thread-safe — may be called from OnEditorUpdate or OnProcessExited.
        /// </summary>
        static void PollLogFile()
        {
            lock (s_pollLock)
            {
                try
                {
                    var path = LogFilePath;
                    if (!File.Exists(path)) return;

                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                               FileShare.ReadWrite | FileShare.Delete))
                    {
                        // Handle file truncation (new server start)
                        if (fs.Length < _logFileReadPos)
                        {
                            _logFileReadPos = 0;
                            _logPartialLine = "";
                        }

                        if (fs.Length <= _logFileReadPos) return;

                        fs.Seek(_logFileReadPos, SeekOrigin.Begin);
                        using (var reader = new StreamReader(fs))
                        {
                            var content = reader.ReadToEnd();
                            _logFileReadPos = fs.Position;

                            var text = _logPartialLine + content;
                            _logPartialLine = "";

                            var lines = text.Split('\n');
                            int count = lines.Length;

                            // Last element may be a partial line
                            if (text.Length > 0 && text[text.Length - 1] != '\n')
                            {
                                _logPartialLine = lines[count - 1];
                                count--;
                            }

                            for (int i = 0; i < count; i++)
                            {
                                var line = lines[i].TrimEnd('\r');
                                if (line.Length > 0)
                                    AddServerOutputLine(line);
                            }
                        }
                    }
                }
                catch { /* file may be locked momentarily */ }
            }
        }

        static void AddServerOutputLine(string text)
        {
            var type = ClassifyLogLine(text);

            lock (s_logLock)
            {
                s_logBuffer.Add(new LogEntry
                {
                    Text = text,
                    Type = type,
                    Timestamp = DateTime.Now,
                });

                if (s_logBuffer.Count > MaxLogLines)
                    s_logBuffer.RemoveRange(0, s_logBuffer.Count - MaxLogLines);
            }

            // Detect server ready
            if (text.IndexOf("Now listening on", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("Application started", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _serverListening = true;
            }

            _needsRepaint = true;
        }

        static LogType ClassifyLogLine(string text)
        {
            if (text.IndexOf("crit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("exception", StringComparison.OrdinalIgnoreCase) >= 0)
                return LogType.Error;
            // Check "fail" only as standalone log level marker to avoid false positives
            if (text.IndexOf("fail:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                return LogType.Error;
            if (text.IndexOf("warn", StringComparison.OrdinalIgnoreCase) >= 0)
                return LogType.Warning;
            return LogType.Log;
        }

        /// <summary>
        /// Restore log buffer from the log file after domain reload.
        /// Sets read position to end of file so polling picks up only new content.
        /// </summary>
        static void RestoreLogFromFile()
        {
            try
            {
                var path = LogFilePath;
                if (!File.Exists(path)) return;

                // Respect clear position (persisted across domain reloads)
                long clearPos = 0;
                long.TryParse(EditorPrefs.GetString(PrefLogClearPos, "0"), out clearPos);

                lock (s_logLock)
                {
                    if (s_logBuffer.Count > 0) return; // already populated

                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                               FileShare.ReadWrite | FileShare.Delete))
                    {
                        // Skip content before clear position (file may have been truncated)
                        if (clearPos > 0 && clearPos <= fs.Length)
                            fs.Seek(clearPos, SeekOrigin.Begin);

                        using (var reader = new StreamReader(fs))
                        {
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                if (string.IsNullOrEmpty(line)) continue;
                                s_logBuffer.Add(new LogEntry
                                {
                                    Text = line,
                                    Type = ClassifyLogLine(line),
                                    Timestamp = DateTime.Now,
                                });
                            }
                        }
                    }

                    if (s_logBuffer.Count > MaxLogLines)
                        s_logBuffer.RemoveRange(0, s_logBuffer.Count - MaxLogLines);
                }

                // Position to end so polling picks up only new content
                _logFileReadPos = new FileInfo(path).Length;
                _logPartialLine = "";
            }
            catch { /* file may not exist or be locked */ }
        }

        // =====================================================================
        // Platform-specific process termination
        // =====================================================================

#if UNITY_EDITOR_WIN
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFOW
        {
            public int cb;
            public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize;
            public int dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        delegate bool ConsoleCtrlDelegate(int ctrlType);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcessW(
            string lpApplicationName, StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
            bool bInheritHandles, uint dwCreationFlags,
            IntPtr lpEnvironment, string lpCurrentDirectory,
            ref STARTUPINFOW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate handlerRoutine, bool add);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GenerateConsoleCtrlEvent(int dwCtrlEvent, int dwProcessGroupId);

        const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
        const uint CREATE_NO_WINDOW = 0x08000000;
        const int CTRL_BREAK_EVENT = 1;

        // Static field prevents GC of the delegate while registered with the OS.
        static ConsoleCtrlDelegate? s_ctrlHandler;
#endif

        /// <summary>
        /// Send a graceful shutdown signal to the server process.
        /// Windows: CTRL_BREAK targeted to the server's process group (started with CREATE_NEW_PROCESS_GROUP).
        /// macOS/Linux: SIGINT via kill command.
        /// </summary>
        static void SendGracefulShutdown(int pid)
        {
#if UNITY_EDITOR_WIN
            try
            {
                // The server was started with CREATE_NEW_PROCESS_GROUP, so sending
                // CTRL_BREAK_EVENT with its PID as the group ID targets ONLY the
                // server's process group — Unity is never affected.
                FreeConsole();
                if (AttachConsole(pid))
                {
                    s_ctrlHandler = ctrlType => true;
                    SetConsoleCtrlHandler(s_ctrlHandler, true);
                    GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, pid);
                    FreeConsole();
                    SetConsoleCtrlHandler(s_ctrlHandler, false);
                }
            }
            catch { }
#else
            try
            {
                var sigint = Process.Start(new ProcessStartInfo
                {
                    FileName = "kill",
                    Arguments = $"-SIGINT {pid}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                sigint?.WaitForExit(1000);
            }
            catch { }
#endif
        }

        /// <summary>
        /// Force-kill the process tree. Used as a fallback when graceful shutdown times out.
        /// </summary>
        static void ForceKillProcessTree(int pid)
        {
#if UNITY_EDITOR_WIN
            try
            {
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/PID {pid} /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                proc?.WaitForExit(3000);
            }
            catch { }
#else
            try
            {
                var sigkill = Process.Start(new ProcessStartInfo
                {
                    FileName = "kill",
                    Arguments = $"-SIGKILL {pid}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                sigkill?.WaitForExit(1000);
            }
            catch { }
#endif
        }

        // =====================================================================
        // IDE / Explorer
        // =====================================================================

        void OpenInIDE()
        {
            var csproj = ResolveCsprojPath();
            if (string.IsNullOrEmpty(csproj) || !File.Exists(csproj))
            {
                EditorUtility.DisplayDialog("SharedMeta Server",
                    "Server project file not found.", "OK");
                return;
            }

            // Look for .sln in csproj directory or parent
            var dir = Path.GetDirectoryName(csproj)!;
            var slnFiles = Directory.GetFiles(dir, "*.sln");
            if (slnFiles.Length == 0)
            {
                var parent = Directory.GetParent(dir);
                if (parent != null)
                    slnFiles = Directory.GetFiles(parent.FullName, "*.sln");
            }

            var target = slnFiles.Length > 0 ? slnFiles[0] : csproj;
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
            });
        }

        void RevealInExplorer()
        {
            var csproj = ResolveCsprojPath();
            if (string.IsNullOrEmpty(csproj)) return;
            var target = File.Exists(csproj) ? csproj : Path.GetDirectoryName(csproj);
            if (!string.IsNullOrEmpty(target))
                EditorUtility.RevealInFinder(target);
        }

        // =====================================================================
        // Logging (metadata messages — server output comes via file polling)
        // =====================================================================

        static void AppendLog(string text, LogType type)
        {
            lock (s_logLock)
            {
                s_logBuffer.Add(new LogEntry
                {
                    Text = text,
                    Type = type,
                    Timestamp = DateTime.Now,
                });

                if (s_logBuffer.Count > MaxLogLines)
                    s_logBuffer.RemoveRange(0, s_logBuffer.Count - MaxLogLines);
            }

            _needsRepaint = true;
        }

        static void ClearLog()
        {
            lock (s_logLock)
            {
                s_logBuffer.Clear();
            }
            try
            {
                var path = LogFilePath;
                if (File.Exists(path))
                {
                    // Try to truncate the file (works when server is stopped)
                    try
                    {
                        using (new FileStream(path, FileMode.Truncate, FileAccess.Write,
                                   FileShare.ReadWrite | FileShare.Delete)) { }
                        _logFileReadPos = 0;
                        EditorPrefs.SetString(PrefLogClearPos, "0");
                    }
                    catch
                    {
                        // Server holds the file — advance read position instead
                        _logFileReadPos = new FileInfo(path).Length;
                        EditorPrefs.SetString(PrefLogClearPos, _logFileReadPos.ToString());
                    }
                }
            }
            catch { }
            _logPartialLine = "";
            _needsRepaint = true;
        }

        // =====================================================================
        // Settings
        // =====================================================================

        void SaveSettings()
        {
            var s = SharedMetaEditorSettings.instance;
            s.serverCsprojPath = _serverCsprojPath;
            s.serverDotnetArgs = _dotnetArgs;
            s.serverUrl = _serverUrl;
            s.serverAutoScroll = _autoScroll;
            s.Save();
        }

        void LoadSettings()
        {
            var s = SharedMetaEditorSettings.instance;
            s.MigrateFromEditorPrefsIfNeeded();
            _serverCsprojPath = s.serverCsprojPath;
            _dotnetArgs = s.serverDotnetArgs;
            _serverUrl = s.serverUrl;
            _autoScroll = s.serverAutoScroll;
            _trackedPid = EditorPrefs.GetInt(PrefPID, -1);
        }

        void TryAutoDetectCsproj()
        {
            if (!string.IsNullOrEmpty(_serverCsprojPath)) return;

            // Build path from Wizard settings: solutionDir / serverName / serverName.csproj
            var s = SharedMetaEditorSettings.instance;
            var serverName = s.serverProjectName;
            var solutionDir = s.solutionDir;
            if (string.IsNullOrEmpty(serverName) || string.IsNullOrEmpty(solutionDir)) return;

            var relPath = solutionDir.TrimEnd('/', '\\') + "/" + serverName + "/" + serverName + ".csproj";
            var resolved = ResolveRelativePath(relPath);
            if (File.Exists(resolved))
            {
                _serverCsprojPath = relPath;  // store relative path
                SaveSettings();
            }
        }

        string ResolveCsprojPath()
        {
            if (string.IsNullOrEmpty(_serverCsprojPath)) return "";
            if (Path.IsPathRooted(_serverCsprojPath)) return _serverCsprojPath;
            return Path.GetFullPath(Path.Combine(ProjectRoot, _serverCsprojPath));
        }

        static string ResolveRelativePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            if (Path.IsPathRooted(path)) return path;
            return Path.GetFullPath(Path.Combine(ProjectRoot, path));
        }

        static string ProjectRoot =>
            Directory.GetParent(Application.dataPath)!.FullName;

        static void ClearPid()
        {
            _trackedPid = -1;
            EditorPrefs.SetInt(PrefPID, -1);
        }
    }
}
#endif
