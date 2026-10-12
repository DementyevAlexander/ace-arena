using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace SGG.UnityMCP.Editor.Setup
{
    internal sealed class BoundedProcessOperation : IDisposable
    {
        const int MaxCapturedCharacters = 16384;

        readonly object m_outputLock = new object();
        readonly Process m_process;
        readonly StringBuilder m_stdout = new StringBuilder();
        readonly StringBuilder m_stderr = new StringBuilder();
        readonly DateTime m_startedAtUtc;
        readonly TimeSpan m_timeout;
        bool m_completed;
        bool m_timedOut;

        BoundedProcessOperation(Process process, TimeSpan timeout)
        {
            m_process = process;
            m_timeout = timeout;
            m_startedAtUtc = DateTime.UtcNow;
        }

        public bool IsCompleted => m_completed;
        public bool TimedOut => m_timedOut;
        public bool TerminationConfirmed { get; private set; }
        public int ProcessId => m_process.Id;
        public int ExitCode { get; private set; } = -1;
        public string StandardOutput { get; private set; } = string.Empty;
        public string StandardError { get; private set; } = string.Empty;

        public static bool TryStart(string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, out BoundedProcessOperation operation, out string error)
        {
            operation = null;
            error = string.Empty;
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = BuildArguments(arguments),
                    WorkingDirectory = string.IsNullOrEmpty(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                var process = new Process { StartInfo = startInfo };
                var candidate = new BoundedProcessOperation(process, timeout);
                process.OutputDataReceived += candidate.OnOutput;
                process.ErrorDataReceived += candidate.OnError;
                if (!process.Start())
                {
                    process.Dispose();
                    error = "Process did not start.";
                    return false;
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                operation = candidate;
                return true;
            }
            catch (Exception exception)
            {
                error = Sanitize(exception.Message);
                return false;
            }
        }

        public bool Poll()
        {
            if (m_completed)
            {
                return true;
            }

            if (DateTime.UtcNow - m_startedAtUtc > m_timeout)
            {
                m_timedOut = true;
                TerminationConfirmed = TryKill();
                Complete(-1);
                return true;
            }

            try
            {
                if (!m_process.HasExited)
                {
                    return false;
                }

                m_process.WaitForExit();
                TerminationConfirmed = true;
                Complete(m_process.ExitCode);
                return true;
            }
            catch (Exception exception)
            {
                Append(m_stderr, exception.Message);
                Complete(-1);
                return true;
            }
        }

        public void Dispose()
        {
            if (!m_completed)
            {
                TerminationConfirmed = TryKill();
                Complete(-1);
            }

            m_process.Dispose();
        }

        internal static string BuildArguments(IReadOnlyList<string> arguments)
        {
            var result = new StringBuilder();
            for (var index = 0; index < arguments.Count; index++)
            {
                if (index > 0)
                {
                    result.Append(' ');
                }

                result.Append(QuoteArgument(arguments[index] ?? string.Empty));
            }

            return result.ToString();
        }

        internal static string QuoteArgument(string value)
        {
            if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
            {
                return value;
            }

            var result = new StringBuilder("\"");
            var backslashes = 0;
            for (var index = 0; index < value.Length; index++)
            {
                if (value[index] == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (value[index] == '"')
                {
                    result.Append('\\', backslashes * 2 + 1);
                    result.Append('"');
                    backslashes = 0;
                    continue;
                }

                result.Append('\\', backslashes);
                backslashes = 0;
                result.Append(value[index]);
            }

            result.Append('\\', backslashes * 2);
            result.Append('"');
            return result.ToString();
        }

        internal static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var result = new StringBuilder(Math.Min(value.Length, MaxCapturedCharacters));
            for (var index = 0; index < value.Length && result.Length < MaxCapturedCharacters; index++)
            {
                var character = value[index];
                if (character == '\r' || character == '\n' || character == '\t' || character >= ' ')
                {
                    result.Append(character);
                }
            }

            return result.ToString().Trim();
        }

        void OnOutput(object sender, DataReceivedEventArgs args)
        {
            Append(m_stdout, args.Data);
        }

        void OnError(object sender, DataReceivedEventArgs args)
        {
            Append(m_stderr, args.Data);
        }

        void Append(StringBuilder target, string line)
        {
            if (line == null)
            {
                return;
            }

            lock (m_outputLock)
            {
                if (target.Length >= MaxCapturedCharacters)
                {
                    return;
                }

                var remaining = MaxCapturedCharacters - target.Length;
                var safe = Sanitize(line);
                if (safe.Length > remaining)
                {
                    safe = safe.Substring(0, remaining);
                }

                target.AppendLine(safe);
            }
        }

        void Complete(int exitCode)
        {
            lock (m_outputLock)
            {
                ExitCode = exitCode;
                StandardOutput = Sanitize(m_stdout.ToString());
                StandardError = Sanitize(m_stderr.ToString());
                m_completed = true;
            }
        }

        bool TryKill()
        {
            try
            {
                if (m_process.HasExited)
                {
                    return true;
                }

                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
                    var taskkill = string.IsNullOrEmpty(systemDirectory) ? string.Empty : Path.Combine(systemDirectory, "taskkill.exe");
                    if (File.Exists(taskkill))
                    {
                        using (var treeKill = Process.Start(new ProcessStartInfo
                        {
                            FileName = taskkill,
                            Arguments = "/PID " + m_process.Id + " /T /F",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        }))
                        {
                            treeKill?.WaitForExit(5000);
                        }
                    }
                }

                if (!m_process.HasExited)
                {
                    m_process.Kill();
                }

                return m_process.WaitForExit(5000) && m_process.HasExited;
            }
            catch
            {
                // The actual project/package state is rechecked after an uncertain outcome.
                return false;
            }
        }
    }
}
