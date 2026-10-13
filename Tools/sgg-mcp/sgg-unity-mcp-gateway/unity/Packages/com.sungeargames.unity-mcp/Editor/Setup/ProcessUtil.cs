using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace SGG.UnityMCP.Editor.Setup
{
    internal static class ProcessUtil
    {
        const int MaxCapturedCharacters = 16384;

        public static bool TryRun(string fileName, string arguments, string workingDirectory, out string stdout, out string stderr, int timeoutMs = 30000, string extraPathPrepend = null)
        {
            return TryRunCore(fileName, arguments, workingDirectory, null, out stdout, out stderr, out _, timeoutMs, extraPathPrepend);
        }

        public static bool TryRun(string fileName, string arguments, string workingDirectory, out string stdout, out string stderr, out bool outputTruncated, int timeoutMs = 30000, string extraPathPrepend = null)
        {
            return TryRunCore(fileName, arguments, workingDirectory, null, out stdout, out stderr, out outputTruncated, timeoutMs, extraPathPrepend);
        }

        public static bool TryRunFiltered(string fileName, string arguments, string workingDirectory, Func<string, bool> stdoutLineFilter, out string stdout, out string stderr, out bool outputTruncated, int timeoutMs = 30000, string extraPathPrepend = null)
        {
            return TryRunCore(fileName, arguments, workingDirectory, stdoutLineFilter, out stdout, out stderr, out outputTruncated, timeoutMs, extraPathPrepend);
        }

        static bool TryRunCore(string fileName, string arguments, string workingDirectory, Func<string, bool> stdoutLineFilter, out string stdout, out string stderr, out bool outputTruncated, int timeoutMs, string extraPathPrepend)
        {
            stdout = string.Empty;
            stderr = string.Empty;
            outputTruncated = false;

            try
            {
                var useCmd = fileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
                var startInfo = new ProcessStartInfo
                {
                    FileName = useCmd ? "cmd.exe" : fileName,
                    Arguments = useCmd ? "/d /c \"\"" + fileName + "\" " + arguments + "\"" : arguments,
                    WorkingDirectory = string.IsNullOrEmpty(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                PrependPath(startInfo, extraPathPrepend);

                using (var process = new Process())
                {
                    var output = new StringBuilder();
                    var error = new StringBuilder();
                    var outputLock = new object();
                    var errorLock = new object();
                    var capturedOutputTruncated = false;
                    var stdoutFilterFailed = false;
                    process.StartInfo = startInfo;
                    process.OutputDataReceived += (_, args) =>
                    {
                        if (args.Data == null)
                        {
                            return;
                        }

                        try
                        {
                            if (stdoutLineFilter == null || stdoutLineFilter(args.Data))
                            {
                                capturedOutputTruncated |= AppendBounded(output, outputLock, args.Data);
                            }
                        }
                        catch (Exception exception)
                        {
                            stdoutFilterFailed = true;
                            AppendBounded(error, errorLock, "stdout filter failed: " + exception.Message);
                        }
                    };
                    process.ErrorDataReceived += (_, args) =>
                    {
                        if (args.Data != null)
                        {
                            AppendBounded(error, errorLock, args.Data);
                        }
                    };

                    if (!process.Start())
                    {
                        return false;
                    }

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (!process.WaitForExit(timeoutMs))
                    {
                        TryKill(process);
                        stdout = Snapshot(output, outputLock);
                        stderr = Snapshot(error, errorLock);
                        outputTruncated = capturedOutputTruncated;
                        return false;
                    }

                    process.WaitForExit();
                    stdout = Snapshot(output, outputLock);
                    stderr = Snapshot(error, errorLock);
                    outputTruncated = capturedOutputTruncated;
                    return process.ExitCode == 0 && !stdoutFilterFailed;
                }
            }
            catch (Exception exception)
            {
                stderr = exception.Message;
                return false;
            }
        }

        static bool AppendBounded(StringBuilder target, object gate, string line)
        {
            lock (gate)
            {
                if (target.Length >= MaxCapturedCharacters)
                {
                    return true;
                }

                var remaining = MaxCapturedCharacters - target.Length;
                var safe = BoundedProcessOperation.Sanitize(line);
                if (remaining <= 1) return true;
                var truncated = safe.Length > remaining - 1;
                if (truncated) safe = safe.Substring(0, remaining - 1);
                target.Append(safe);
                target.Append('\n');
                return truncated;
            }
        }

        static string Snapshot(StringBuilder source, object gate)
        {
            lock (gate)
            {
                return BoundedProcessOperation.Sanitize(source.ToString());
            }
        }

        public static Process StartDetached(string fileName, string arguments, string workingDirectory, string extraPathPrepend = null, string logPath = null)
        {
            var hasLogPath = !string.IsNullOrEmpty(logPath);
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = string.IsNullOrEmpty(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = hasLogPath,
                RedirectStandardError = hasLogPath,
                CreateNoWindow = true,
            };

            PrependPath(startInfo, extraPathPrepend);
            var process = new Process { StartInfo = startInfo };
            object logLock = null;
            if (hasLogPath)
            {
                var logDirectory = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(logDirectory))
                {
                    Directory.CreateDirectory(logDirectory);
                }

                logLock = new object();
                File.AppendAllText(logPath, DateTime.UtcNow.ToString("u") + " [start] " + fileName + " " + arguments + Environment.NewLine, Encoding.UTF8);
                process.OutputDataReceived += (_, args) => AppendProcessLogLine(logLock, logPath, "stdout", args.Data);
                process.ErrorDataReceived += (_, args) => AppendProcessLogLine(logLock, logPath, "stderr", args.Data);
                process.Exited += (_, __) => AppendProcessExitLogLine(logLock, logPath, process);
            }

            if (!process.Start())
            {
                process.Dispose();
                return null;
            }

            if (hasLogPath)
            {
                process.EnableRaisingEvents = true;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }

            return process;
        }

        public static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        static void PrependPath(ProcessStartInfo startInfo, string extraPathPrepend)
        {
            if (string.IsNullOrEmpty(extraPathPrepend))
            {
                return;
            }

            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            startInfo.EnvironmentVariables["PATH"] = string.IsNullOrEmpty(currentPath)
                ? extraPathPrepend
                : extraPathPrepend + Path.PathSeparator + currentPath;
        }

        static void AppendProcessLogLine(object logLock, string logPath, string stream, string line)
        {
            if (line == null)
            {
                return;
            }

            lock (logLock)
            {
                File.AppendAllText(logPath, DateTime.UtcNow.ToString("u") + " [" + stream + "] " + line + Environment.NewLine, Encoding.UTF8);
            }
        }

        static void AppendProcessExitLogLine(object logLock, string logPath, Process process)
        {
            try
            {
                AppendProcessLogLine(logLock, logPath, "exit", "pid=" + process.Id + " exit_code=" + process.ExitCode);
            }
            catch
            {
                // Exit diagnostics must never affect the Unity process.
            }
        }

        static void TryKill(Process process)
        {
            try
            {
                process.Kill();
            }
            catch
            {
                // Best-effort cleanup for timed-out setup commands.
            }
        }
    }
}
