using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Aerial
{
    /// <summary>
    /// Append-only log file, so a problem that shows up after hours of running can be read
    /// afterwards. A screensaver has nowhere to print to: there is no console, and Trace only
    /// reaches an attached debugger.
    ///
    /// Lives next to the video cache, in %LOCALAPPDATA%\Aerial\logs. Rotates at 1 MB and keeps one
    /// previous file, so it cannot grow without bound on a machine that idles for weeks.
    ///
    /// Every method swallows its own exceptions: logging must never be the reason playback breaks.
    /// </summary>
    public static class Log
    {
        private const long MaxBytes = 1024 * 1024;

        private static readonly object Gate = new object();
        private static string logPath;
        private static bool started;

        /// <summary>Full path of the current log file, or null before Start() succeeds.</summary>
        public static string FilePath { get { return logPath; } }

        /// <summary>
        /// Opens the log and records what this process is. Safe to call more than once; only the
        /// first call does anything.
        /// </summary>
        public static void Start(string mode)
        {
            lock (Gate)
            {
                if (started) return;
                started = true;

                try
                {
                    var dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Aerial", "logs");
                    Directory.CreateDirectory(dir);
                    logPath = Path.Combine(dir, "aerial.log");
                    RotateIfNeeded();

                    // Existing Trace.WriteLine calls all over the codebase land in the same file.
                    Trace.Listeners.Add(new LogTraceListener());
                }
                catch (Exception)
                {
                    logPath = null;
                }
            }

            var asm = typeof(Log).Assembly.GetName();
            Write("====================================================================");
            Write("Aerial " + asm.Version + " starting, mode " + mode
                  + ", pid " + Process.GetCurrentProcess().Id
                  + ", " + (Environment.Is64BitProcess ? "64-bit" : "32-bit")
                  + ", " + Environment.OSVersion.Version
                  + ", CLR " + Environment.Version);
        }

        /// <summary>Writes one timestamped line.</summary>
        public static void Write(string message)
        {
            if (logPath == null) return;

            try
            {
                var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                           + " " + message + Environment.NewLine;
                lock (Gate)
                {
                    RotateIfNeeded();
                    File.AppendAllText(logPath, line, Encoding.UTF8);
                }
            }
            catch (Exception)
            {
                // Disk full, file locked, folder deleted under us - none of it is worth crashing for.
            }
        }

        /// <summary>Writes a line prefixed with which window/monitor it came from.</summary>
        public static void Write(string scope, string message)
        {
            Write("[" + scope + "] " + message);
        }

        /// <summary>Flattens an exception to one line, innermost message included.</summary>
        public static string Describe(Exception ex)
        {
            if (ex == null) return "(no detail)";

            var text = ex.GetType().Name + ": " + ex.Message;
            var inner = ex.InnerException;
            var depth = 0;
            while (inner != null && depth++ < 3)
            {
                text += " <- " + inner.GetType().Name + ": " + inner.Message;
                var com = inner as System.Runtime.InteropServices.COMException;
                if (com != null)
                    text += " (HRESULT 0x" + com.ErrorCode.ToString("X8", CultureInfo.InvariantCulture) + ")";
                inner = inner.InnerException;
            }
            return text;
        }

        /// <summary>Must be called inside the lock.</summary>
        private static void RotateIfNeeded()
        {
            try
            {
                if (logPath == null || !File.Exists(logPath)) return;
                if (new FileInfo(logPath).Length < MaxBytes) return;

                var previous = logPath + ".1";
                if (File.Exists(previous)) File.Delete(previous);
                File.Move(logPath, previous);
            }
            catch (Exception)
            {
            }
        }

        private sealed class LogTraceListener : TraceListener
        {
            public override void Write(string message) { WriteLine(message); }

            public override void WriteLine(string message)
            {
                if (!string.IsNullOrEmpty(message)) Log.Write("trace: " + message);
            }
        }
    }
}
