using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SGG.UnityMCP.Editor.Bridge;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Setup
{
    internal static class GatewayTestHistoryManager
    {
        internal enum Operation { Status, PreviewCleanup, Cleanup, Configure }

        internal sealed class Target
        {
            internal string NodePath;
            internal string CliPath;
            internal string CliSha256;
            internal string ConfigPath;
            internal string RuntimeRoot;
            internal string PackageVersion;
        }

        [Serializable]
        internal sealed class Error
        {
            public string code;
        }

        [Serializable]
        internal sealed class Result
        {
            public string status;
            public GatewayTestHistoryModel.Snapshot data;
            public Error error;
            internal bool Success => status == "ok" && GatewayTestHistoryModel.ValidSnapshot(data);
            internal bool ConfigurationSaved => status == "ok" && GatewayTestHistoryModel.ValidSettingsSnapshot(data) && data.configuration_saved && data.restart_required;
            internal static Result Fail(string code) => new Result { status = "error", error = new Error { code = code } };
        }

        // Resolve Unity/package state on the main thread; execute the bounded local CLI off it.
        internal static Target CaptureTarget()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor) return null;
            var packageRoot = UnityMcpPackagePaths.PackageResolvedPath;
            if (string.IsNullOrEmpty(packageRoot)) return null;
            var runtimeRoot = Path.Combine(packageRoot, "Editor", "GatewayRuntime");
            return new Target
            {
                NodePath = NodeRuntimeInstaller.PortableNodePath,
                CliPath = Path.Combine(runtimeRoot, "gateway-cli.mjs"),
                RuntimeRoot = runtimeRoot,
                PackageVersion = UnityBridgeRuntime.PackageVersion,
                ConfigPath = UnityMcpPackagePaths.UserGatewayConfigPath,
            };
        }

        [Serializable]
        sealed class ManifestArtifact { public string sha256; }
        [Serializable]
        sealed class ManifestArtifacts { public ManifestArtifact gateway_cli; }
        [Serializable]
        sealed class Manifest { public string version; public ManifestArtifacts artifacts; }

        internal static Result Run(Target target, Operation operation, GatewayTestHistoryModel.Snapshot binding = null,
            int sizeMiB = GatewayTestHistoryModel.DefaultMiB, int maxBindings = GatewayTestHistoryModel.DefaultBindings, bool confirmed = false, bool preserveByteLimit = false)
        {
            if (target == null) return Result.Fail("runtime_or_config_unavailable");
            try
            {
                var cliHash = target.CliSha256;
                if (!string.IsNullOrEmpty(target.RuntimeRoot))
                {
                    if (!GatewayInstaller.ValidateRuntimeMetadataAtRoot(target.RuntimeRoot, true, out _)) return Result.Fail("runtime_integrity_changed");
                    var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(Path.Combine(target.RuntimeRoot, "gateway-runtime.json")));
                    if (manifest?.version != target.PackageVersion) return Result.Fail("runtime_integrity_changed");
                    cliHash = manifest.artifacts.gateway_cli.sha256;
                }
                if (!File.Exists(target.ConfigPath) || !File.Exists(target.NodePath)
                    || !NodeRuntimeInstaller.ValidateNode(target.NodePath, out _, out _)) return Result.Fail("runtime_or_config_unavailable");
                if (!GatewayInstaller.TryReadCanonicalCheckoutArtifact(target.CliPath, cliHash, out _))
                    return Result.Fail("runtime_integrity_changed");
                var arguments = BuildArguments(target, operation, binding, sizeMiB, maxBindings, confirmed, preserveByteLimit);
                var succeeded = ProcessUtil.TryRun(target.NodePath, arguments, Path.GetDirectoryName(target.CliPath),
                    out var stdout, out _, out var truncated, 20000);
                if (truncated) return Result.Fail("admin_output_truncated");
                var result = ParseResult(stdout, operation);
                if (!succeeded && result.status == "ok") return Result.Fail("admin_outcome_uncertain");
                return result;
            }
            catch (ArgumentException) { return Result.Fail("invalid_admin_arguments"); }
            catch { return Result.Fail("admin_outcome_uncertain"); }
        }

        internal static string BuildArguments(Target target, Operation operation, GatewayTestHistoryModel.Snapshot binding,
            int sizeMiB, int maxBindings, bool confirmed, bool preserveByteLimit = false)
        {
            if (target == null || string.IsNullOrEmpty(target.CliPath) || string.IsNullOrEmpty(target.ConfigPath)) throw new ArgumentException();
            var action = operation == Operation.Status ? "status" : operation == Operation.PreviewCleanup ? "preview-cleanup"
                : operation == Operation.Cleanup ? "cleanup" : operation == Operation.Configure ? "configure" : throw new ArgumentException();
            var args = new List<string> { target.CliPath, "--test-run-store", action, "--config", target.ConfigPath, "--json" };
            if (operation == Operation.Cleanup)
            {
                if (!confirmed || !GatewayTestHistoryModel.CanConfirmCleanup(binding, DateTime.UtcNow)) throw new ArgumentException();
                args.AddRange(new[] { "--confirm", "--preview-fingerprint", binding.preview_fingerprint });
            }
            if (operation == Operation.Configure)
            {
                if (!confirmed || !GatewayTestHistoryModel.ValidLimits(sizeMiB, maxBindings)
                    || binding == null || !GatewayTestHistoryModel.IsFingerprint(binding.config_fingerprint)) throw new ArgumentException();
                var maxBytes = preserveByteLimit ? binding.max_bytes : sizeMiB * GatewayTestHistoryModel.MiB;
                if (maxBytes < GatewayTestHistoryModel.MiB || maxBytes > GatewayTestHistoryModel.MaxMiB * GatewayTestHistoryModel.MiB) throw new ArgumentException();
                args.AddRange(new[] { "--confirm", "--config-fingerprint", binding.config_fingerprint,
                    "--max-bytes", maxBytes.ToString(CultureInfo.InvariantCulture),
                    "--max-bindings", maxBindings.ToString(CultureInfo.InvariantCulture) });
            }
            return BoundedProcessOperation.BuildArguments(args);
        }

        internal static Result ParseResult(string source, Operation operation = Operation.Status)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(source) || source.Length > 16384) return Result.Fail("admin_response_invalid");
                var result = JsonUtility.FromJson<Result>(source.Trim());
                if (result == null || (result.status != "ok" && result.status != "error")) return Result.Fail("admin_response_invalid");
                if (result.status == "ok" && !GatewayTestHistoryModel.ValidSnapshot(result.data)
                    && !(operation == Operation.Configure && result.ConfigurationSaved && result.data.capacity_state == "unavailable")) return Result.Fail("admin_response_invalid");
                if (result.status == "error" && (result.error == null || string.IsNullOrEmpty(result.error.code))) return Result.Fail("admin_response_invalid");
                if (!PipelineJsonEvidenceParser.TryReadStringPath(source, new[] { "status" }, out var status) || status != result.status) return Result.Fail("admin_response_invalid");
                if (!PipelineJsonEvidenceParser.TryReadInt64Path(source, new[] { "schema_version" }, out var schema) || schema != 1) return Result.Fail("admin_response_invalid");
                if (status == "ok")
                {
                    var expectedAction = operation == Operation.Status ? "status" : operation == Operation.PreviewCleanup ? "preview-cleanup" : operation == Operation.Cleanup ? "cleanup" : "configure";
                    if (!PipelineJsonEvidenceParser.TryReadStringPath(source, new[] { "action" }, out var action) || action != expectedAction) return Result.Fail("admin_response_invalid");
                    foreach (var field in new[] { "max_bytes", "max_bindings", "retention_days" })
                        if (!PipelineJsonEvidenceParser.TryReadInt64Path(source, new[] { "data", field }, out var value) || value < 0) return Result.Fail("admin_response_invalid");
                    if (operation == Operation.Configure)
                    {
                        if (!PipelineJsonEvidenceParser.TryReadBoolPath(source, new[] { "data", "configuration_saved" }, out var saved) || !saved
                            || !PipelineJsonEvidenceParser.TryReadBoolPath(source, new[] { "data", "restart_required" }, out var restart) || !restart) return Result.Fail("admin_response_invalid");
                    }
                    if (result.data.capacity_state != "unavailable")
                        foreach (var field in new[] { "bytes", "bindings" })
                            if (!PipelineJsonEvidenceParser.TryReadInt64Path(source, new[] { "data", field }, out var value) || value < 0) return Result.Fail("admin_response_invalid");
                    if (operation == Operation.PreviewCleanup || operation == Operation.Cleanup)
                    {
                        var field = operation == Operation.Cleanup ? "removed" : "candidates";
                        foreach (var name in new[] { "bytes", "bindings", "files" })
                            if (!PipelineJsonEvidenceParser.TryReadInt64Path(source, new[] { "data", field, name }, out var count) || count < 0) return Result.Fail("admin_response_invalid");
                        if (operation == Operation.PreviewCleanup)
                            foreach (var name in new[] { "active", "unknown", "invalid", "foreign", "batch_limit" })
                                if (!PipelineJsonEvidenceParser.TryReadInt64Path(source, new[] { "data", "preserved", name }, out var count) || count < 0) return Result.Fail("admin_response_invalid");
                    }
                }
                return result;
            }
            catch { return Result.Fail("admin_response_invalid"); }
        }
    }
}
