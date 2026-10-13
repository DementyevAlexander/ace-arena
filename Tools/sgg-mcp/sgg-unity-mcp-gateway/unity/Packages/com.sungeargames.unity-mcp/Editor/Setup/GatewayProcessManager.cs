using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Setup
{
    public static class GatewayProcessManager
    {
        const int HealthPort = 27182;
        const string HealthUrl = "http://127.0.0.1:27182/health";
        const string McpTokenEnvName = "SGG_UNITY_MCP_TOKEN";
        const string McpTokenHeaderName = "X-SGG-Unity-MCP-Token";
        const int HealthTimeoutMs = 2000;
        const int ExistingListenerHealthRecoveryTimeoutMs = 10000;
        const int MaxHealthBodyCharacters = 65536;
        const int MaxHealthHeaderBytes = 8192;
        static Process s_process;
        static volatile bool s_preferTcpHealth;
        static string s_lastLogPath = string.Empty;

        public static string LogDirectory => Path.Combine(UnityMcpPackagePaths.LocalAppDataRoot, "logs");

        public static GatewayRuntimeStatus GetStatus(bool probeHealth = false)
        {
            var status = new GatewayRuntimeStatus
            {
                HealthUrl = HealthUrl,
                HealthProbed = probeHealth,
                StartedByThisEditor = s_process != null,
                StartedProcessId = s_process != null ? s_process.Id : 0,
                LogDirectory = LogDirectory,
                LogPath = LatestLogPath(),
            };

            status.LogTail = ReadTail(status.LogPath, 2000);
            status.ProcessAlive = s_process != null && !s_process.HasExited;
            if (probeHealth)
            {
                status.HealthOk = ProbeHealth(out var healthBody, out var healthError);
                status.HealthBody = healthBody;
                PopulateHealthMetadata(status);
                status.Message = status.HealthOk ? "Gateway health endpoint is reachable." : healthError;
            }
            else
            {
                status.Message = status.ProcessAlive ? "Gateway process was started by this Unity Editor session." : "Gateway health was not probed. Use Refresh Health.";
            }
            return status;
        }

        public static NodeRuntimeInstaller.InstallResult StartGateway()
        {
            return StartOrRestartGateway(GatewayInstaller.GetStatus(), Application.platform == RuntimePlatform.WindowsEditor, false);
        }

        internal static NodeRuntimeInstaller.InstallResult StartOrRestartGateway(GatewayInstaller.GatewayInstallStatus gatewayStatus, bool windowsEditor, bool forceRestart)
        {
            var nodeStatus = NodeRuntimeInstaller.GetStatus(windowsEditor);
            if (!nodeStatus.IsAvailable)
            {
                return NodeRuntimeInstaller.InstallResult.Fail("Node 22+ is not available. Install portable Node first.");
            }

            if (!gatewayStatus.CanStart)
            {
                return NodeRuntimeInstaller.InstallResult.Fail(gatewayStatus.Message);
            }

            var currentStatus = GetStatus(true);
            if (forceRestart)
            {
                if (!StopRunningGateway(windowsEditor, out var forcedStopMessage, allowAbsentGateway: true))
                {
                    return NodeRuntimeInstaller.InstallResult.Fail("Gateway restart stopped before start. " + forcedStopMessage);
                }

                var forcedStart = StartGateway(nodeStatus, gatewayStatus);
                return forcedStart.Success
                    ? NodeRuntimeInstaller.InstallResult.Ok("Gateway restart completed. Stop: " + forcedStopMessage + " Start: " + forcedStart.Message)
                    : NodeRuntimeInstaller.InstallResult.Fail("Gateway restart failed after stop. Stop: " + forcedStopMessage + " Start: " + forcedStart.Message);
            }

            if (currentStatus.HealthOk)
            {
                if (GatewayRuntimeVersionMatches(currentStatus, gatewayStatus))
                {
                    return NodeRuntimeInstaller.InstallResult.Ok("Gateway is already running at " + HealthUrl);
                }

                if (!StopRunningGateway(windowsEditor, out var stopMessage))
                {
                    return NodeRuntimeInstaller.InstallResult.Fail("Gateway is already running version " + DisplayVersion(currentStatus.HealthVersion) + " but installed runtime is " + DisplayVersion(gatewayStatus.InstalledRuntimeVersion) + ". Could not stop the old gateway. " + stopMessage);
                }
            }
            else if (IsUnauthorizedHealthFailure(currentStatus.Message))
            {
                if (!StopRunningGateway(windowsEditor, out var stopMessage))
                {
                    return NodeRuntimeInstaller.InstallResult.Fail("Gateway is already running but health rejected the current MCP token. Could not stop the stale gateway listener. " + stopMessage);
                }
            }
            else if (windowsEditor)
            {
                if (!TryInspectListeningPid(windowsEditor, out var listenerPid, out var listenerMessage))
                {
                    return NodeRuntimeInstaller.InstallResult.Fail("Gateway health probe failed and the local listener could not be inspected. No process was started. " + listenerMessage);
                }

                if (listenerPid > 0)
                {
                    if (!WaitForHealth(ExistingListenerHealthRecoveryTimeoutMs, out var recoveredHealthBody, out var recoveredHealthError, false))
                    {
                        return NodeRuntimeInstaller.InstallResult.Fail("Gateway health probe failed, and the listener on port " + HealthPort + " with PID " + listenerPid + " remained unavailable after a bounded retry. No second process was started. Use the explicit Restart Gateway action to stop it, or Refresh Health and try again. " + recoveredHealthError);
                    }

                    currentStatus.HealthOk = true;
                    currentStatus.HealthBody = recoveredHealthBody;
                    PopulateHealthMetadata(currentStatus);
                    if (string.IsNullOrEmpty(currentStatus.HealthVersion) || string.IsNullOrEmpty(gatewayStatus.InstalledRuntimeVersion))
                    {
                        return NodeRuntimeInstaller.InstallResult.Fail("Gateway health recovered, but its runtime version could not be verified. No process was changed. Refresh Health or use the explicit Restart Gateway action.");
                    }
                    if (string.Equals(currentStatus.HealthVersion, gatewayStatus.InstalledRuntimeVersion, StringComparison.Ordinal))
                    {
                        return NodeRuntimeInstaller.InstallResult.Ok("Gateway health recovered during the bounded retry and the installed runtime is already running at " + HealthUrl);
                    }
                    if (!StopRunningGateway(windowsEditor, out var recoveredStopMessage))
                    {
                        return NodeRuntimeInstaller.InstallResult.Fail("Gateway health recovered at version " + DisplayVersion(currentStatus.HealthVersion) + " but installed runtime is " + DisplayVersion(gatewayStatus.InstalledRuntimeVersion) + ". Could not stop the old gateway. " + recoveredStopMessage);
                    }
                }
            }

            return StartGateway(nodeStatus, gatewayStatus);
        }

        static NodeRuntimeInstaller.InstallResult StartGateway(NodeRuntimeInstaller.NodeRuntimeStatus nodeStatus, GatewayInstaller.GatewayInstallStatus gatewayStatus)
        {
            if (!GatewayInstaller.ValidateRuntime(gatewayStatus, nodeStatus, out var validationMessage))
            {
                return NodeRuntimeInstaller.InstallResult.Fail("Gateway runtime failed validation: " + validationMessage + ". Reinstall runtime and try again.");
            }

            var configResult = GatewayInstaller.EnsureUserConfigFiles(gatewayStatus);
            if (!configResult.Success)
            {
                return configResult;
            }

            var arguments = ProcessUtil.Quote(gatewayStatus.BundledCliPath);

            arguments += " --config " + ProcessUtil.Quote(gatewayStatus.ConfigPath)
                + " --policy " + ProcessUtil.Quote(gatewayStatus.PolicyPath);

            var pathPrepend = nodeStatus.IsPortable ? Path.GetDirectoryName(nodeStatus.NodePath) : null;
            var logPath = CreateLogPath();
            s_lastLogPath = logPath;
            try
            {
                s_process = ProcessUtil.StartDetached(nodeStatus.NodePath, arguments, gatewayStatus.RuntimeRoot, pathPrepend, logPath);
            }
            catch (Exception exception)
            {
                return NodeRuntimeInstaller.InstallResult.Fail("Failed to start gateway process. Log: " + logPath + ". " + exception.Message);
            }

            if (s_process == null)
            {
                return NodeRuntimeInstaller.InstallResult.Fail("Failed to start gateway process. Log: " + logPath);
            }

            if (WaitForHealth(5000, out var healthError))
            {
                return NodeRuntimeInstaller.InstallResult.Ok("Gateway process started and health is reachable. PID: " + s_process.Id + ". Log: " + logPath);
            }

            if (s_process.HasExited)
            {
                System.Threading.Thread.Sleep(150);
                return NodeRuntimeInstaller.InstallResult.Fail("Gateway process exited immediately with code " + s_process.ExitCode + ". " + healthError + LogHint(logPath));
            }

            return NodeRuntimeInstaller.InstallResult.Fail("Gateway process started but health is not reachable yet. PID: " + s_process.Id + ". " + healthError + LogHint(logPath));
        }

        public static NodeRuntimeInstaller.InstallResult StopGateway()
        {
            return StopGateway(Application.platform == RuntimePlatform.WindowsEditor);
        }

        internal static NodeRuntimeInstaller.InstallResult StopGateway(bool windowsEditor)
        {
            return StopRunningGateway(windowsEditor, out var message)
                ? NodeRuntimeInstaller.InstallResult.Ok(message)
                : NodeRuntimeInstaller.InstallResult.Fail("This Unity Editor session did not start the gateway process. " + message);
        }

        static bool StopRunningGateway(bool windowsEditor, out string message, bool allowAbsentGateway = false)
        {
            if (s_process == null)
            {
                return StopGatewayByListeningPort(windowsEditor, out message, allowAbsentGateway);
            }

            try
            {
                if (!s_process.HasExited)
                {
                    s_process.Kill();
                    if (!s_process.WaitForExit(3000))
                    {
                        message = "Gateway process did not stop within 3 seconds. PID: " + s_process.Id;
                        return false;
                    }
                    s_process.Dispose();
                    s_process = null;
                    message = "Gateway process stopped.";
                    return true;
                }

                s_process.Dispose();
                s_process = null;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                return false;
            }

            return StopGatewayByListeningPort(windowsEditor, out message, allowAbsentGateway);
        }

        static bool GatewayRuntimeVersionMatches(GatewayRuntimeStatus currentStatus, GatewayInstaller.GatewayInstallStatus gatewayStatus)
        {
            return string.IsNullOrEmpty(currentStatus.HealthVersion)
                || string.IsNullOrEmpty(gatewayStatus.InstalledRuntimeVersion)
                || string.Equals(currentStatus.HealthVersion, gatewayStatus.InstalledRuntimeVersion, StringComparison.Ordinal);
        }

        static bool IsUnauthorizedHealthFailure(string message)
        {
            return !string.IsNullOrEmpty(message)
                && (message.IndexOf("401", StringComparison.OrdinalIgnoreCase) >= 0
                    || message.IndexOf("Unauthorized", StringComparison.OrdinalIgnoreCase) >= 0
                    || message.IndexOf("invalid_token", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        static string DisplayVersion(string value)
        {
            return string.IsNullOrEmpty(value) ? "unknown" : value;
        }

        static bool StopGatewayByListeningPort(bool windowsEditor, out string message, bool allowAbsentGateway = false)
        {
            message = string.Empty;
            if (!windowsEditor)
            {
                message = "Stopping an external gateway by port is currently implemented for Windows Editor only.";
                return false;
            }

            return StopInspectedGatewayListener(allowAbsentGateway,
                (out int pid, out string inspectionMessage) => TryInspectListeningPid(windowsEditor, out pid, out inspectionMessage),
                StopGatewayListenerProcess, out message);
        }

        internal delegate bool InspectGatewayListener(out int pid, out string message);
        internal delegate bool StopGatewayListener(int pid, out string message);

        internal static bool StopInspectedGatewayListener(bool allowAbsentGateway, InspectGatewayListener inspect, StopGatewayListener stop, out string message)
        {
            if (!inspect(out var pid, out message))
            {
                return false;
            }

            if (pid < 0)
            {
                message = "Gateway listener inspection returned an invalid PID; no process was changed.";
                return false;
            }

            if (pid == 0)
            {
                message = "No gateway listener was found on port " + HealthPort + ".";
                return allowAbsentGateway;
            }

            return stop(pid, out message);
        }

        static bool StopGatewayListenerProcess(int pid, out string message)
        {
            try
            {
                using (var process = Process.GetProcessById(pid))
                {
                    process.Kill();
                    if (!process.WaitForExit(3000))
                    {
                        message = "Gateway listener on port " + HealthPort + " did not stop within 3 seconds. PID: " + pid;
                        return false;
                    }
                }
                message = "Gateway listener on port " + HealthPort + " stopped. PID: " + pid;
                return true;
            }
            catch (Exception exception)
            {
                message = "Could not stop gateway listener on port " + HealthPort + " with PID " + pid + ": " + exception.Message;
                return false;
            }
        }

        static bool TryInspectListeningPid(bool windowsEditor, out int pid, out string message)
        {
            pid = 0;
            message = string.Empty;
            if (!windowsEditor)
            {
                message = "Inspecting the Gateway listener is currently implemented for Windows Editor only.";
                return false;
            }

            if (!ProcessUtil.TryRunFiltered("netstat.exe", "-ano -p tcp", null, line => IsListeningPortLine(line, HealthPort), out var stdout, out var stderr, out var outputTruncated, 8000))
            {
                message = "Could not inspect TCP listeners: " + (string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim());
                return false;
            }

            if (outputTruncated)
            {
                message = "Could not inspect TCP listeners completely because netstat output exceeded the bounded capture limit.";
                return false;
            }

            if (!TryFindListeningPid(stdout, HealthPort, out pid, out var parseMessage))
            {
                message = parseMessage;
                return false;
            }
            return true;
        }

        internal static bool TryFindListeningPid(string netstatOutput, int port, out int pid, out string message)
        {
            pid = 0;
            message = string.Empty;
            foreach (var line in netstatOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!IsListeningPortLine(line, port))
                {
                    continue;
                }

                if (!TryParseListeningPid(line, port, out var parsedPid))
                {
                    message = "Could not parse the PID for the Gateway TCP listener on port " + port + ".";
                    return false;
                }

                if (pid > 0 && pid != parsedPid)
                {
                    message = "Multiple processes appear to listen on Gateway port " + port + "; no process was changed.";
                    return false;
                }

                pid = parsedPid;
            }

            return true;
        }

        internal static bool TryParseListeningPid(string line, int port, out int pid)
        {
            pid = 0;
            if (!IsListeningPortLine(line, port))
            {
                return false;
            }

            var parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 5 && int.TryParse(parts[4], out pid) && pid > 0;
        }

        static bool IsListeningPortLine(string line, int port)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            var trimmed = line.Trim();
            if (!trimmed.StartsWith("TCP", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 4
                && string.Equals(parts[3], "LISTENING", StringComparison.OrdinalIgnoreCase)
                && AddressUsesPort(parts[1], port);
        }

        static bool AddressUsesPort(string address, int port)
        {
            return address.EndsWith(":" + port, StringComparison.OrdinalIgnoreCase);
        }

        static bool ProbeHealth(out string body, out string error)
        {
            body = string.Empty;
            var phase = "request";

            if (s_preferTcpHealth && TryProbeHealthOverTcp(out body, out error))
            {
                return true;
            }

            error = string.Empty;

            try
            {
                var request = (HttpWebRequest)WebRequest.Create(HealthUrl);
                request.Method = "GET";
                request.Proxy = null;
                request.KeepAlive = false;
                request.Timeout = HealthTimeoutMs;
                request.ReadWriteTimeout = HealthTimeoutMs;
                var token = Environment.GetEnvironmentVariable(McpTokenEnvName);
                if (!string.IsNullOrEmpty(token))
                {
                    request.Headers[McpTokenHeaderName] = token;
                }

                var response = (HttpWebResponse)request.GetResponse();
                phase = "response body";
                using (response)
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream))
                {
                    var buffer = new char[4096];
                    var builder = new System.Text.StringBuilder();
                    while (builder.Length <= MaxHealthBodyCharacters)
                    {
                        var count = reader.Read(buffer, 0, Math.Min(buffer.Length, MaxHealthBodyCharacters + 1 - builder.Length));
                        if (count <= 0) break;
                        builder.Append(buffer, 0, count);
                    }

                    if (builder.Length > MaxHealthBodyCharacters)
                    {
                        error = "Gateway health response exceeded the bounded body limit.";
                        return false;
                    }

                    body = builder.ToString();
                    var statusCode = (int)response.StatusCode;
                    return statusCode >= 200 && statusCode < 300;
                }
            }
            catch (WebException exception)
            {
                if (phase == "request" && exception.Status == WebExceptionStatus.Timeout)
                {
                    if (TryProbeHealthOverTcp(out body, out var fallbackError))
                    {
                        s_preferTcpHealth = true;
                        return true;
                    }

                    error = "Gateway health request timed out; TCP fallback: " + fallbackError;
                    return false;
                }

                var response = exception.Response as HttpWebResponse;
                error = "Gateway health " + phase + ": " + (response == null || exception.Status != WebExceptionStatus.ProtocolError
                    ? exception.Message
                    : ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + " " + response.StatusDescription);
                response?.Dispose();
                return false;
            }
            catch (Exception exception)
            {
                error = "Gateway health " + phase + ": " + exception.Message;
                return false;
            }
        }

        static bool TryProbeHealthOverTcp(out string body, out string error)
        {
            body = string.Empty;
            error = string.Empty;
            try
            {
                var token = Environment.GetEnvironmentVariable(McpTokenEnvName);
                if (!TryBuildHealthTcpRequest(token, out var requestBytes, out error))
                {
                    return false;
                }

                using (var client = new TcpClient(AddressFamily.InterNetwork))
                {
                    var deadline = DateTime.UtcNow.AddMilliseconds(5000);
                    var connection = client.BeginConnect(IPAddress.Loopback, HealthPort, null, null);
                    using (var handle = connection.AsyncWaitHandle)
                    {
                        if (!handle.WaitOne(HealthTimeoutMs))
                        {
                            error = "Loopback connection timed out.";
                            return false;
                        }

                        client.EndConnect(connection);
                    }

                    client.SendTimeout = HealthTimeoutMs;
                    using (var stream = client.GetStream())
                    using (var payload = new MemoryStream())
                    {
                        stream.Write(requestBytes, 0, requestBytes.Length);

                        var buffer = new byte[4096];
                        var maxBytes = MaxHealthHeaderBytes + 4 * MaxHealthBodyCharacters;
                        while (DateTime.UtcNow < deadline)
                        {
                            stream.ReadTimeout = Math.Max(1, Math.Min(HealthTimeoutMs, (int)(deadline - DateTime.UtcNow).TotalMilliseconds));
                            var count = stream.Read(buffer, 0, Math.Min(buffer.Length, maxBytes + 1 - (int)payload.Length));
                            if (count == 0) break;
                            payload.Write(buffer, 0, count);
                            if (payload.Length > maxBytes)
                            {
                                error = "Gateway health response exceeded the bounded byte limit.";
                                return false;
                            }
                        }

                        if (DateTime.UtcNow >= deadline)
                        {
                            error = "Gateway health response timed out.";
                            return false;
                        }

                        return TryParseHealthTcpResponse(payload.ToArray(), out body, out error);
                    }
                }
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        internal static bool TryBuildHealthTcpRequest(string token, out byte[] requestBytes, out string error)
        {
            requestBytes = null;
            error = string.Empty;
            if (!string.IsNullOrEmpty(token))
            {
                if (token.Length > 4096)
                {
                    error = "MCP token is too long for a bounded health request.";
                    return false;
                }

                foreach (var character in token)
                {
                    if (character < '!' || character > '~')
                    {
                        error = "MCP token contains an invalid HTTP header character.";
                        return false;
                    }
                }
            }

            var request = "GET /health HTTP/1.0\r\nHost: 127.0.0.1:" + HealthPort
                + "\r\nConnection: close\r\n"
                + (string.IsNullOrEmpty(token) ? string.Empty : McpTokenHeaderName + ": " + token + "\r\n")
                + "\r\n";
            requestBytes = Encoding.ASCII.GetBytes(request);
            return true;
        }

        internal static bool TryParseHealthTcpResponse(byte[] bytes, out string body, out string error)
        {
            body = string.Empty;
            error = string.Empty;
            if (bytes == null || bytes.Length > MaxHealthHeaderBytes + 4 * MaxHealthBodyCharacters)
            {
                error = "Gateway health response exceeded the bounded byte limit.";
                return false;
            }

            var headerEnd = -1;
            for (var index = 0; index + 3 < bytes.Length && index < MaxHealthHeaderBytes; index++)
            {
                if (bytes[index] == 13 && bytes[index + 1] == 10 && bytes[index + 2] == 13 && bytes[index + 3] == 10)
                {
                    headerEnd = index;
                    break;
                }
            }

            if (headerEnd < 0)
            {
                error = "Gateway health HTTP headers are missing or too large.";
                return false;
            }

            var headers = Encoding.ASCII.GetString(bytes, 0, headerEnd);
            var statusLineEnd = headers.IndexOf("\r\n", StringComparison.Ordinal);
            var statusLine = statusLineEnd < 0 ? headers : headers.Substring(0, statusLineEnd);
            var statusParts = statusLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (statusParts.Length < 2 || !statusParts[0].StartsWith("HTTP/1.", StringComparison.Ordinal)
                || !int.TryParse(statusParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var statusCode))
            {
                error = "Gateway health HTTP status line is invalid.";
                return false;
            }

            if (statusCode < 200 || statusCode >= 300)
            {
                error = "Gateway health HTTP status " + statusCode.ToString(CultureInfo.InvariantCulture) + ".";
                return false;
            }

            if (headers.IndexOf("\r\nTransfer-Encoding:", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                error = "Gateway health HTTP/1.0 response used unsupported transfer encoding.";
                return false;
            }

            try
            {
                body = new UTF8Encoding(false, true).GetString(bytes, headerEnd + 4, bytes.Length - headerEnd - 4);
            }
            catch (DecoderFallbackException)
            {
                error = "Gateway health response is not valid UTF-8.";
                return false;
            }

            if (body.Length > MaxHealthBodyCharacters)
            {
                body = string.Empty;
                error = "Gateway health response exceeded the bounded body limit.";
                return false;
            }

            if (string.IsNullOrEmpty(ParseHealthMetadata(body).HealthVersion))
            {
                body = string.Empty;
                error = "Gateway health response has no valid runtime version.";
                return false;
            }

            return true;
        }

        static void PopulateHealthMetadata(GatewayRuntimeStatus status)
        {
            var parsed = ParseHealthMetadata(status.HealthBody);
            status.HealthVersion = parsed.HealthVersion;
            status.LiveProjectsCount = parsed.LiveProjectsCount;
            status.StaleProjectsCount = parsed.StaleProjectsCount;
            status.LiveProjectRecordsCount = parsed.LiveProjectRecordsCount;
            status.StaleProjectRecordsCount = parsed.StaleProjectRecordsCount;
            status.InvalidProjectRecordsCount = parsed.InvalidProjectRecordsCount;
            status.ClientRootsMetadataAvailable = parsed.ClientRootsMetadataAvailable;
            status.ClientRootsMetadataMalformed = parsed.ClientRootsMetadataMalformed;
            status.ClientRootsObservationCount = parsed.ClientRootsObservationCount;
            status.ClientRootsObservedCount = parsed.ClientRootsObservedCount;
            status.ClientRootsUnavailableCount = parsed.ClientRootsUnavailableCount;
            status.ClientRootHashes = parsed.ClientRootHashes;
            status.PolicyMetadataAvailable = parsed.PolicyMetadataAvailable;
            status.PolicyMetadataMalformed = parsed.PolicyMetadataMalformed;
            status.PolicyState = parsed.PolicyState;
            status.PolicyConfigured = parsed.PolicyConfigured;
            status.PolicyDegraded = parsed.PolicyDegraded;
            status.PolicyRevision = parsed.PolicyRevision;
            status.PolicyLoadedAt = parsed.PolicyLoadedAt;
            status.PolicyLastAttemptAt = parsed.PolicyLastAttemptAt;
            status.PolicyLastErrorKind = parsed.PolicyLastErrorKind;
            status.PolicyLastErrorCode = parsed.PolicyLastErrorCode;
            status.PolicyDegradedSince = parsed.PolicyDegradedSince;
        }

        internal static GatewayRuntimeStatus ParseHealthMetadata(string healthBody)
        {
            var status = new GatewayRuntimeStatus();
            if (string.IsNullOrEmpty(healthBody))
            {
                return status;
            }

            HealthPayload payload;
            try
            {
                payload = JsonUtility.FromJson<HealthPayload>(healthBody);
            }
            catch
            {
                return status;
            }

            if (payload == null)
            {
                return status;
            }

            status.HealthVersion = BoundedHealthText(payload.version, 64);
            status.LiveProjectsCount = NonNegative(payload.live_projects_count);
            status.StaleProjectsCount = NonNegative(payload.stale_projects_count);
            status.LiveProjectRecordsCount = NonNegative(payload.live_project_records_count);
            status.StaleProjectRecordsCount = NonNegative(payload.stale_project_records_count);
            status.InvalidProjectRecordsCount = NonNegative(payload.invalid_project_records_count);
            ParseClientRootsHealth(payload.client_roots, status);

            // JsonUtility can materialize a default nested object even when an older payload omits it.
            if (!Commands.JsonUtil.HasProperty(healthBody, "policy") || payload.policy == null)
            {
                return status;
            }

            if (!TryValidatePolicyHealth(payload.policy))
            {
                status.PolicyMetadataMalformed = true;
                return status;
            }

            status.PolicyMetadataAvailable = true;
            status.PolicyState = payload.policy.state;
            status.PolicyConfigured = payload.policy.configured;
            status.PolicyDegraded = payload.policy.degraded;
            status.PolicyRevision = payload.policy.effective_policy_revision ?? string.Empty;
            status.PolicyLoadedAt = payload.policy.loaded_at ?? string.Empty;
            status.PolicyLastAttemptAt = payload.policy.last_attempt_at ?? string.Empty;
            status.PolicyLastErrorKind = payload.policy.last_error_kind ?? string.Empty;
            status.PolicyLastErrorCode = payload.policy.last_error_code ?? string.Empty;
            status.PolicyDegradedSince = payload.policy.degraded_since ?? string.Empty;
            return status;
        }

        static void ParseClientRootsHealth(ClientRootsHealthPayload clientRoots, GatewayRuntimeStatus status)
        {
            if (clientRoots == null)
            {
                return;
            }

            if (clientRoots.schema_version != 1 || clientRoots.observations == null || clientRoots.observations.Length > 128)
            {
                status.ClientRootsMetadataMalformed = true;
                return;
            }

            var hashes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var observation in clientRoots.observations)
            {
                if (observation == null
                    || !Allowed(observation.status, "observed", "unsupported", "unavailable")
                    || observation.root_hashes == null
                    || observation.root_hashes.Length > 8)
                {
                    status.ClientRootsMetadataMalformed = true;
                    return;
                }

                status.ClientRootsObservationCount++;
                if (observation.status == "observed") status.ClientRootsObservedCount++;
                else status.ClientRootsUnavailableCount++;
                foreach (var hash in observation.root_hashes)
                {
                    if (!IsSha256Identity(hash))
                    {
                        status.ClientRootsMetadataMalformed = true;
                        return;
                    }
                    hashes.Add(hash);
                }
            }

            status.ClientRootsMetadataAvailable = true;
            status.ClientRootHashes = new string[hashes.Count];
            hashes.CopyTo(status.ClientRootHashes);
            Array.Sort(status.ClientRootHashes, StringComparer.Ordinal);
        }

        static bool IsSha256Identity(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal))
            {
                return false;
            }
            for (var index = 7; index < value.Length; index++)
            {
                var current = value[index];
                if (!((current >= '0' && current <= '9') || (current >= 'a' && current <= 'f')))
                {
                    return false;
                }
            }
            return true;
        }

        static int NonNegative(int value)
        {
            return Math.Max(0, value);
        }

        static string BoundedHealthText(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }

        static bool TryValidatePolicyHealth(PolicyHealthPayload policy)
        {
            if (!Allowed(policy.state, "unconfigured", "active", "degraded", "error")
                || !NullableAllowed(policy.last_error_kind, "missing", "unreadable", "too_large", "unstable", "invalid_json", "invalid_schema")
                || !NullableAllowed(policy.last_error_code, "policy_reload_failed", "policy_invalid")
                || !ValidNullableTimestamp(policy.loaded_at)
                || !ValidNullableTimestamp(policy.last_attempt_at)
                || !ValidNullableTimestamp(policy.degraded_since)
                || (!string.IsNullOrEmpty(policy.effective_policy_revision) && !ValidPolicyRevision(policy.effective_policy_revision)))
            {
                return false;
            }

            if (policy.state == "unconfigured")
            {
                return !policy.configured && !policy.degraded
                    && AllNull(policy.effective_policy_revision, policy.loaded_at, policy.last_attempt_at, policy.last_error_kind, policy.last_error_code, policy.degraded_since);
            }

            if (policy.state == "active")
            {
                return policy.configured && !policy.degraded
                    && AllPresent(policy.effective_policy_revision, policy.loaded_at, policy.last_attempt_at)
                    && AllNull(policy.last_error_kind, policy.last_error_code, policy.degraded_since);
            }

            if (policy.state == "degraded")
            {
                return policy.configured && policy.degraded
                    && AllPresent(policy.effective_policy_revision, policy.loaded_at, policy.last_attempt_at, policy.last_error_kind, policy.last_error_code, policy.degraded_since);
            }

            return policy.configured && !policy.degraded
                && string.IsNullOrEmpty(policy.effective_policy_revision)
                && string.IsNullOrEmpty(policy.loaded_at)
                && AllPresent(policy.last_attempt_at, policy.last_error_kind, policy.last_error_code)
                && string.IsNullOrEmpty(policy.degraded_since);
        }

        static bool Allowed(string value, params string[] allowed)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            for (var index = 0; index < allowed.Length; index++)
            {
                if (string.Equals(value, allowed[index], StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        static bool NullableAllowed(string value, params string[] allowed)
        {
            return string.IsNullOrEmpty(value) || Allowed(value, allowed);
        }

        static bool ValidPolicyRevision(string value)
        {
            if (value == null || value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal))
            {
                return false;
            }

            for (var index = 7; index < value.Length; index++)
            {
                var character = value[index];
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }

        static bool ValidNullableTimestamp(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return true;
            }

            return DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _);
        }

        static bool AllNull(params string[] values)
        {
            for (var index = 0; index < values.Length; index++)
            {
                if (!string.IsNullOrEmpty(values[index]))
                {
                    return false;
                }
            }

            return true;
        }

        static bool AllPresent(params string[] values)
        {
            for (var index = 0; index < values.Length; index++)
            {
                if (string.IsNullOrEmpty(values[index]))
                {
                    return false;
                }
            }

            return true;
        }

        static bool WaitForHealth(int timeoutMs, out string error)
        {
            return WaitForHealth(timeoutMs, out _, out error, true);
        }

        static bool WaitForHealth(int timeoutMs, out string body, out string error, bool stopWhenStartedProcessExits)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            body = string.Empty;
            error = string.Empty;
            while (DateTime.UtcNow < deadline)
            {
                if (ProbeHealth(out body, out error))
                {
                    return true;
                }

                if (stopWhenStartedProcessExits && s_process != null && s_process.HasExited)
                {
                    return false;
                }

                System.Threading.Thread.Sleep(150);
            }

            return false;
        }

        static string CreateLogPath()
        {
            Directory.CreateDirectory(LogDirectory);
            return Path.Combine(LogDirectory, "gateway-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".log");
        }

        static string LatestLogPath()
        {
            if (!string.IsNullOrEmpty(s_lastLogPath) && File.Exists(s_lastLogPath))
            {
                return s_lastLogPath;
            }

            if (!Directory.Exists(LogDirectory))
            {
                return string.Empty;
            }

            var files = Directory.GetFiles(LogDirectory, "gateway-*.log");
            if (files.Length == 0)
            {
                return string.Empty;
            }

            Array.Sort(files, (left, right) => File.GetLastWriteTimeUtc(right).CompareTo(File.GetLastWriteTimeUtc(left)));
            return files[0];
        }

        static string LogHint(string logPath)
        {
            var tail = ReadTail(logPath, 3000);
            return string.IsNullOrWhiteSpace(tail) ? " Log: " + logPath : " Log: " + logPath + "\n\nRecent gateway log:\n" + tail;
        }

        static string ReadTail(string logPath, int maxChars)
        {
            if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath))
            {
                return string.Empty;
            }

            try
            {
                var text = File.ReadAllText(logPath);
                if (text.Length > maxChars)
                {
                    text = text.Substring(text.Length - maxChars);
                }

                return text.Trim();
            }
            catch (Exception exception)
            {
                return "Could not read gateway log: " + exception.Message;
            }
        }

        public sealed class GatewayRuntimeStatus
        {
            public bool HealthOk;
            public bool HealthProbed;
            public bool StartedByThisEditor;
            public bool ProcessAlive;
            public bool PolicyConfigured;
            public bool PolicyDegraded;
            public bool PolicyMetadataAvailable;
            public bool PolicyMetadataMalformed;
            public bool ClientRootsMetadataAvailable;
            public bool ClientRootsMetadataMalformed;
            public int StartedProcessId;
            public int InvalidProjectRecordsCount;
            public int LiveProjectsCount;
            public int StaleProjectsCount;
            public int LiveProjectRecordsCount;
            public int StaleProjectRecordsCount;
            public int ClientRootsObservationCount;
            public int ClientRootsObservedCount;
            public int ClientRootsUnavailableCount;
            public string HealthUrl = string.Empty;
            public string HealthBody = string.Empty;
            public string HealthVersion = string.Empty;
            public string LogDirectory = string.Empty;
            public string LogPath = string.Empty;
            public string LogTail = string.Empty;
            public string Message = string.Empty;
            public string PolicyState = string.Empty;
            public string PolicyRevision = string.Empty;
            public string PolicyLoadedAt = string.Empty;
            public string PolicyLastAttemptAt = string.Empty;
            public string PolicyLastErrorKind = string.Empty;
            public string PolicyLastErrorCode = string.Empty;
            public string PolicyDegradedSince = string.Empty;
            public string[] ClientRootHashes = Array.Empty<string>();

            public string HealthSummary
            {
                get
                {
                    if (!HealthOk)
                    {
                        return "No response";
                    }

                    var summary = "version=" + (string.IsNullOrEmpty(HealthVersion) ? "unknown" : HealthVersion)
                        + "; projects live=" + LiveProjectsCount + ", stale=" + StaleProjectsCount
                        + "; records live=" + LiveProjectRecordsCount + ", stale=" + StaleProjectRecordsCount + ", invalid=" + InvalidProjectRecordsCount;
                    if (PolicyMetadataMalformed)
                    {
                        return summary + "; policy=unavailable (malformed metadata)";
                    }

                    if (!PolicyMetadataAvailable)
                    {
                        return summary + "; policy=unavailable (older health contract)";
                    }

                    summary += "; policy=" + PolicyState + ", configured=" + (PolicyConfigured ? "yes" : "no") + ", degraded=" + (PolicyDegraded ? "yes" : "no");
                    if (!string.IsNullOrEmpty(PolicyRevision)) summary += ", revision=" + PolicyRevision;
                    if (!string.IsNullOrEmpty(PolicyLoadedAt)) summary += ", loaded=" + PolicyLoadedAt;
                    if (!string.IsNullOrEmpty(PolicyLastAttemptAt)) summary += ", attempt=" + PolicyLastAttemptAt;
                    if (!string.IsNullOrEmpty(PolicyLastErrorCode)) summary += ", error=" + PolicyLastErrorCode + "/" + PolicyLastErrorKind;
                    if (!string.IsNullOrEmpty(PolicyDegradedSince)) summary += ", degraded-since=" + PolicyDegradedSince;
                    return summary;
                }
            }
        }

        [Serializable]
        sealed class HealthPayload
        {
            public string version;
            public int live_projects_count;
            public int stale_projects_count;
            public int live_project_records_count;
            public int stale_project_records_count;
            public int invalid_project_records_count;
            public PolicyHealthPayload policy;
            public ClientRootsHealthPayload client_roots;
        }

        [Serializable]
        sealed class ClientRootsHealthPayload
        {
            public int schema_version;
            public ClientRootsObservationPayload[] observations;
        }

        [Serializable]
        sealed class ClientRootsObservationPayload
        {
            public string status;
            public string[] root_hashes;
        }

        [Serializable]
        sealed class PolicyHealthPayload
        {
            public string state;
            public bool configured;
            public bool degraded;
            public string effective_policy_revision;
            public string loaded_at;
            public string last_attempt_at;
            public string last_error_kind;
            public string last_error_code;
            public string degraded_since;
        }
    }
}
