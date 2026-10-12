using System;
using System.IO;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Setup;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class McpFtueConfigTests
    {
        // The Node setup parity test consumes this same fixture; do not derive it from the generator.
        const string ExpectedWindowsStdioScript = "$root=Join-Path $env:LOCALAPPDATA 'SGG\\UnityMcpGateway'; $pointer=Join-Path $root 'gateway\\active-runtime.json'; $runtime=(Get-Content -Raw $pointer | ConvertFrom-Json).runtime_root; $node=Join-Path $root 'runtime\\node-v22.16.0-win-x64\\node.exe'; $cli=Join-Path $runtime 'gateway-cli.mjs'; & $node $cli stdio-shim --url 'http://127.0.0.1:28182/mcp' --token-env 'CUSTOM_MCP_TOKEN' --legacy-text-result";
        const string RemoteUrl = "http://10.0.0.2:28182/mcp";
        const string RemoteFields = "\"type\":\"remote\",\"url\":\"http://10.0.0.2:28182/mcp\",\"headers\":{\"X-SGG-Unity-MCP-Token\":\"{env:CUSTOM_MCP_TOKEN}\"},\"oauth\":false";

        [TestCase(GatewayNetworkModeManager.ModeWindowsStdio)]
        [TestCase(GatewayNetworkModeManager.ModeRemoteHttp)]
        public void GeneratedNativeV2SnippetHasExactShapeAndIsReady(string mode)
        {
            var status = new GatewayNetworkModeManager.NetworkStatus
            {
                SelectedMode = mode,
                McpUrl = mode == GatewayNetworkModeManager.ModeRemoteHttp ? RemoteUrl : "http://127.0.0.1:28182/mcp",
                TokenEnv = "CUSTOM_MCP_TOKEN",
            };
            var snippet = GatewayNetworkModeManager.OpenCodeConfigSnippet(status);
            // JsonUtility needs a valid C# field name for the otherwise unchanged JSON server map.
            var parsed = JsonUtility.FromJson<SnippetFixture>(snippet.Replace("\"sgg-unity-mcp\"", "\"server\""));
            Assert.That(parsed.mcp.servers, Is.Not.Null);
            var server = parsed.mcp.servers.server;
            Assert.That(server.disabled, Is.False);
            Assert.That(server.timeout.startup, Is.EqualTo(30000));
            Assert.That(server.timeout.catalog, Is.EqualTo(30000));
            Assert.That(server.timeout.execution, Is.EqualTo(3900000));
            Assert.That(snippet, Does.Contain("\"disabled\": false").And.Not.Contain("\"enabled\""));
            if (mode == GatewayNetworkModeManager.ModeWindowsStdio)
            {
                Assert.That(server.type, Is.EqualTo("local"));
                Assert.That(server.protocol, Is.EqualTo("legacy"));
                Assert.That(server.command, Is.EqualTo(new[] { "powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", ExpectedWindowsStdioScript }));
            }
            else
            {
                Assert.That(server.type, Is.EqualTo("remote"));
                Assert.That(server.url, Is.EqualTo(RemoteUrl));
                Assert.That(snippet, Does.Contain("\"oauth\": false"));
                Assert.That(snippet, Does.Contain("{env:CUSTOM_MCP_TOKEN}").And.Not.Contain("$CUSTOM_MCP_TOKEN"));
            }
            AssertReady(snippet, mode, status.McpUrl, true);
        }

        [TestCase("", true)]
        [TestCase("\"disabled\":false", true)]
        [TestCase("\"disabled\":true", false)]
        [TestCase("\"disabled\":\"false\"", false)]
        [TestCase("\"disabled\":null", false)]
        [TestCase("\"disabled\":0", false)]
        [TestCase("\"disabled\":false,\"disabled\":false", false)]
        [TestCase("\"disabled\":false,\"disabl\\u0065d\":false", false)]
        [TestCase("\"enabled\":true", false)]
        public void NativeV2EnablementIsOptionalStrictAndDuplicateSafe(string properties, bool ready)
        {
            AssertReady(NativeRemoteDocument(properties), GatewayNetworkModeManager.ModeRemoteHttp, RemoteUrl, ready);
        }

        [TestCase("", "", true)]
        [TestCase("\"timeout\":{}", "", true)]
        [TestCase("\"timeout\":{\"catalog\":30000}", "", true)]
        [TestCase("\"timeout\":{\"execution\":3900000}", "", true)]
        [TestCase("\"timeout\":{\"execution\":43200000}", "", true)]
        [TestCase("\"timeout\":{\"execution\":3.9e6}", "", true)]
        [TestCase("", "\"timeout\":{\"execution\":43200000}", true)]
        [TestCase("\"timeout\":{\"catalog\":30000}", "\"timeout\":{\"execution\":3900000}", true)]
        [TestCase("", "\"timeout\":{\"execution\":30000}", false)]
        [TestCase("\"timeout\":{\"execution\":3900000}", "\"timeout\":{\"execution\":30000}", true)]
        [TestCase("\"timeout\":{\"execution\":30000}", "\"timeout\":{\"execution\":43200000}", false)]
        [TestCase("\"timeout\":3900000", "", false)]
        [TestCase("\"timeout\":null", "", false)]
        [TestCase("\"timeout\":{\"execution\":\"3900000\"}", "", false)]
        [TestCase("\"timeout\":{\"execution\":3900000.5}", "", false)]
        [TestCase("\"timeout\":{\"execution\":0}", "", false)]
        [TestCase("\"timeout\":{\"execution\":-1}", "", false)]
        [TestCase("\"timeout\":{\"execution\":1e309}", "", false)]
        [TestCase("\"timeout\":{\"execution\":9007199254740992}", "", false)]
        [TestCase("\"timeout\":{\"startup\":false}", "", false)]
        [TestCase("\"timeout\":{\"catalog\":0}", "", false)]
        [TestCase("\"timeout\":{\"execution\":3900000,\"execution\":43200000}", "", false)]
        [TestCase("\"timeout\":{\"executon\":3900000}", "", false)]
        [TestCase("", "\"timeout\":30000", false)]
        [TestCase("", "\"timeout\":{\"startup\":0}", false)]
        public void NativeV2TimeoutsUsePositiveIntegerOverridesAndDocumentedInheritance(string serverProperties, string mcpProperties, bool ready)
        {
            AssertReady(NativeRemoteDocument(serverProperties, mcpProperties), GatewayNetworkModeManager.ModeRemoteHttp, RemoteUrl, ready);
        }

        [TestCase("30000", "", "", false)]
        [TestCase("7200000", "", "", true)]
        [TestCase("0", "", "", false)]
        [TestCase("1e309", "", "", false)]
        [TestCase("\"7200000\"", "", "", false)]
        [TestCase("30000", "\"timeout\":{\"execution\":3900000}", "", true)]
        [TestCase("30000", "\"timeout\":{\"catalog\":60000}", "", false)]
        [TestCase("7200000", "\"timeout\":{\"execution\":30000}", "", false)]
        [TestCase("30000", "", "\"timeout\":{\"execution\":3900000}", true)]
        [TestCase("7200000", "", "\"timeout\":{\"execution\":30000}", false)]
        [TestCase("7200000", "\"timeout\":{\"execution\":30000}", "\"timeout\":{\"execution\":3900000}", true)]
        public void SupportedLegacyTimeoutDefaultPrecedesNativeMembersAndServerOverrides(string legacy, string mcpProperties, string serverProperties, bool ready)
        {
            var source = NativeRemoteDocument(serverProperties, mcpProperties);
            source = source.Insert(1, "\"experimental\":{\"mcp_timeout\":" + legacy + "},");
            AssertReady(source, GatewayNetworkModeManager.ModeRemoteHttp, RemoteUrl, ready);
        }

        [TestCase("\"codemode\":true", true)]
        [TestCase("\"codemode\":false", true)]
        [TestCase("\"codemode\":\"false\"", false)]
        [TestCase("\"codemode\":null", false)]
        [TestCase("\"codemode\":0", false)]
        [TestCase("\"protocol\":\"legacy\"", true)]
        [TestCase("\"protocol\":\"auto\"", true)]
        [TestCase("\"protocol\":\"2026-07-28\"", true)]
        [TestCase("\"protocol\":\"other\"", false)]
        [TestCase("\"protocol\":false", false)]
        [TestCase("\"custom_unknown\":{\"preserve\":true}", true)]
        public void NativeOptionalFieldTypesAreValidatedWithoutRejectingUnknownFields(string properties, bool ready)
        {
            AssertReady(NativeRemoteDocument(properties), GatewayNetworkModeManager.ModeRemoteHttp, RemoteUrl, ready);
        }

        [TestCase("\"extra\":1,", false)]
        [TestCase("\"extra\":null,", false)]
        [TestCase("\"extra\":\"value\",", true)]
        public void NativeHeadersRequireStringValuesBeyondTheOwnedTokenHeader(string extra, bool ready)
        {
            AssertReady(NativeRemoteDocument().Replace("\"headers\":{", "\"headers\":{" + extra), GatewayNetworkModeManager.ModeRemoteHttp, RemoteUrl, ready);
        }

        [TestCase("\"legacy\"", true)]
        [TestCase("\"auto\"", true)]
        [TestCase("\"2026-07-28\"", false)]
        [TestCase("\"other\"", false)]
        [TestCase("null", false)]
        [TestCase("1", false)]
        public void NativeWindowsShimRejectsRequiredModernOrInvalidProtocols(string protocol, bool ready)
        {
            var status = new GatewayNetworkModeManager.NetworkStatus { SelectedMode = GatewayNetworkModeManager.ModeWindowsStdio, McpUrl = "http://127.0.0.1:28182/mcp", TokenEnv = "CUSTOM_MCP_TOKEN" };
            var source = GatewayNetworkModeManager.OpenCodeConfigSnippet(status).Replace("\"protocol\": \"legacy\"", "\"protocol\": " + protocol);
            AssertReady(source, status.SelectedMode, status.McpUrl, ready);
        }

        [TestCase("{\"A\":\"value\"}", true)]
        [TestCase("{}", true)]
        [TestCase("{\"A\":1}", false)]
        [TestCase("[]", false)]
        [TestCase("null", false)]
        public void NativeWindowsEnvironmentRequiresStringValues(string environment, bool ready)
        {
            var status = new GatewayNetworkModeManager.NetworkStatus { SelectedMode = GatewayNetworkModeManager.ModeWindowsStdio, McpUrl = "http://127.0.0.1:28182/mcp", TokenEnv = "CUSTOM_MCP_TOKEN" };
            var source = GatewayNetworkModeManager.OpenCodeConfigSnippet(status).Replace("\"disabled\": false", "\"environment\": " + environment + ", \"disabled\": false");
            AssertReady(source, status.SelectedMode, status.McpUrl, ready);
        }

        [TestCase("", true)]
        [TestCase("\"fixture-cwd\"", true)]
        [TestCase("\"\"", true)]
        [TestCase("false", false)]
        [TestCase("null", false)]
        [TestCase("1", false)]
        [TestCase("[]", false)]
        [TestCase("{}", false)]
        public void NativeWindowsCwdMustBeAbsentOrString(string cwd, bool ready)
        {
            var status = new GatewayNetworkModeManager.NetworkStatus { SelectedMode = GatewayNetworkModeManager.ModeWindowsStdio, McpUrl = "http://127.0.0.1:28182/mcp", TokenEnv = "CUSTOM_MCP_TOKEN" };
            var source = GatewayNetworkModeManager.OpenCodeConfigSnippet(status);
            if (cwd.Length != 0) source = source.Replace("\"disabled\": false", "\"cwd\": " + cwd + ", \"disabled\": false");
            AssertReady(source, status.SelectedMode, status.McpUrl, ready);
        }

        [TestCase("\"oauth\":false", "\"oauth\":true")]
        [TestCase("\"oauth\":false", "\"oauth\":{}")]
        [TestCase("\"oauth\":false", "\"oauth\":\"false\"")]
        [TestCase(",\"oauth\":false", "")]
        [TestCase("{env:CUSTOM_MCP_TOKEN}", "$CUSTOM_MCP_TOKEN")]
        [TestCase("{env:CUSTOM_MCP_TOKEN}", "inline-secret")]
        [TestCase("{env:CUSTOM_MCP_TOKEN}", "{env:OTHER_TOKEN}")]
        [TestCase("http://10.0.0.2:28182/mcp", "http://10.0.0.3:28182/mcp")]
        [TestCase("\"headers\":{", "\"header\":{")]
        [TestCase("\"type\":\"remote\"", "\"type\":\"local\"")]
        public void NativeV2RemoteRequiresExactEndpointEnvironmentReferenceAndNoOAuth(string original, string replacement)
        {
            AssertReady(NativeRemoteDocument().Replace(original, replacement), GatewayNetworkModeManager.ModeRemoteHttp, RemoteUrl, false);
        }

        [TestCase("<windows-host-ip>")]
        [TestCase("https://10.0.0.2:28182/mcp")]
        [TestCase("http://10.0.0.2:28182/mcp?token=not-allowed")]
        [TestCase("http://10.0.0.2:28182/mcp#fragment")]
        [TestCase("http://user:password@10.0.0.2:28182/mcp")]
        [TestCase("http://10.0.0.2:28182/other-path")]
        [TestCase("not-a-url")]
        public void NativeRemoteRejectsUnsafeOrNonGatewayEndpointEvenWhenItMatches(string url)
        {
            AssertReady(NativeRemoteDocument().Replace(RemoteUrl, url), GatewayNetworkModeManager.ModeRemoteHttp, url, false);
        }

        [TestCase("null")]
        [TestCase("[]")]
        [TestCase("\"invalid\"")]
        [TestCase("{\"sgg-unity-mcp\":null}")]
        [TestCase("{\"sgg-unity-mcp\":[]}")]
        [TestCase("{\"sgg-unity-mcp\":{\"disabled\":true}}")]
        public void MalformedNativeContainerOrServerNeverFallsBackToLegacy(string servers)
        {
            var legacy = "\"sgg-unity-mcp\":{" + RemoteFields + ",\"enabled\":true,\"timeout\":3900000}";
            AssertReady("{\"mcp\":{" + legacy + ",\"servers\":" + servers + "}}", GatewayNetworkModeManager.ModeRemoteHttp, RemoteUrl, false);
        }

        [Test]
        public void NativeSameNameServerWinsRegardlessOfLegacyKeyOrder()
        {
            var native = "\"servers\":{\"sgg-unity-mcp\":{" + RemoteFields + "}}";
            var legacy = "\"sgg-unity-mcp\":{\"enabled\":false,\"timeout\":1}";
            AssertReady("{\"mcp\":{" + native + "," + legacy + "}}", GatewayNetworkModeManager.ModeRemoteHttp, RemoteUrl, true);
            AssertReady("{\"mcp\":{" + legacy + "," + native + "}}", GatewayNetworkModeManager.ModeRemoteHttp, RemoteUrl, true);
        }

        [Test]
        public void KnownLegacyServerShapeRequiresRealV2TokenSubstitutionAndNoOAuth()
        {
            var legacy = "{\"mcp\":{\"sgg-unity-mcp\":{" + RemoteFields + ",\"enabled\":true,\"timeout\":3900000}}}";
            AssertReady(legacy, GatewayNetworkModeManager.ModeRemoteHttp, RemoteUrl, true);
            AssertReady(legacy.Replace("3900000", "3.9e6"), GatewayNetworkModeManager.ModeRemoteHttp, RemoteUrl, true);
            AssertReady(legacy.Replace("3900000", "30000"), GatewayNetworkModeManager.ModeRemoteHttp, RemoteUrl, false);
            AssertReady(legacy.Replace("3900000", "1e309"), GatewayNetworkModeManager.ModeRemoteHttp, RemoteUrl, false);
            AssertReady(legacy.Replace("3900000", "3900000.5"), GatewayNetworkModeManager.ModeRemoteHttp, RemoteUrl, false);
            AssertReady(legacy.Replace("{env:CUSTOM_MCP_TOKEN}", "$CUSTOM_MCP_TOKEN"), GatewayNetworkModeManager.ModeRemoteHttp, RemoteUrl, false);
            AssertReady(legacy.Replace(",\"oauth\":false", ""), GatewayNetworkModeManager.ModeRemoteHttp, RemoteUrl, false);
        }

        [Test]
        public void NativeJsoncAcceptsCommentsAndTrailingCommasButRejectsDuplicateContainers()
        {
            AssertReady("{// comment\n\"mcp\":{/* block */\"servers\":{\"sgg-unity-mcp\":{" + RemoteFields + ",},},},}", GatewayNetworkModeManager.ModeRemoteHttp, RemoteUrl, true);
            AssertReady("{\"mcp\":{},\"mcp\":{\"servers\":{\"sgg-unity-mcp\":{" + RemoteFields + "}}}}", GatewayNetworkModeManager.ModeRemoteHttp, RemoteUrl, false);
            AssertReady("{\"mcp\":{\"servers\":{},\"servers\":{\"sgg-unity-mcp\":{" + RemoteFields + "}}}}", GatewayNetworkModeManager.ModeRemoteHttp, RemoteUrl, false);
            AssertReady("{\"mcp\":{\"servers\":{\"sgg-unity-mcp\":{" + RemoteFields + "},\"sgg-unity-mcp\":{" + RemoteFields + "}}}}", GatewayNetworkModeManager.ModeRemoteHttp, RemoteUrl, false);
            AssertReady(NativeRemoteDocument().Replace("\"type\":\"remote\",", "\"type\":\"remote\" "), GatewayNetworkModeManager.ModeRemoteHttp, RemoteUrl, false);
        }

        [Test]
        public void NativeStdioRejectsScriptDriftAndNonLoopbackButKeepsPowerShellQuoting()
        {
            var status = new GatewayNetworkModeManager.NetworkStatus
            {
                SelectedMode = GatewayNetworkModeManager.ModeWindowsStdio,
                McpUrl = "http://127.0.0.1:28182/mcp",
                TokenEnv = "CUSTOM_MCP_TOKEN",
            };
            var snippet = GatewayNetworkModeManager.OpenCodeConfigSnippet(status);
            AssertReady(snippet.Replace(" --legacy-text-result", ""), status.SelectedMode, status.McpUrl, false);
            AssertReady(snippet.Replace(" --legacy-text-result", " --legacy-text-result; Write-Output bad"), status.SelectedMode, status.McpUrl, false);
            AssertReady(snippet.Replace("CUSTOM_MCP_TOKEN", "OTHER_TOKEN"), status.SelectedMode, status.McpUrl, false);
            status.McpUrl = "http://10.0.0.2:28182/mcp";
            AssertReady(GatewayNetworkModeManager.OpenCodeConfigSnippet(status), status.SelectedMode, status.McpUrl, false);
            status.McpUrl = "http://127.0.0.1:28182/mcp";
            status.TokenEnv = "TOKEN'NAME";
            var quoted = GatewayNetworkModeManager.OpenCodeConfigSnippet(status);
            Assert.That(quoted, Does.Contain("--token-env 'TOKEN''NAME' --legacy-text-result"));
        }

        static string NativeRemoteDocument(string serverProperties = "", string mcpProperties = "")
        {
            return "{\"mcp\":{" + (mcpProperties.Length == 0 ? "" : mcpProperties + ",")
                + "\"servers\":{\"sgg-unity-mcp\":{" + RemoteFields + (serverProperties.Length == 0 ? "" : "," + serverProperties) + "}}}}";
        }

        static void AssertReady(string source, string mode, string url, bool expected)
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, source);
                Assert.That(GatewayNetworkModeManager.IsOpenCodeConfigReady(path, mode, url, "CUSTOM_MCP_TOKEN"), Is.EqualTo(expected), source);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Serializable]
        sealed class SnippetFixture { public McpFixture mcp; }
        [Serializable]
        sealed class McpFixture { public ServersFixture servers; }
        [Serializable]
        sealed class ServersFixture { public ServerFixture server; }
        [Serializable]
        sealed class ServerFixture
        {
            public string type;
            public string protocol;
            public string url;
            public string[] command;
            public bool disabled;
            public TimeoutFixture timeout;
        }
        [Serializable]
        sealed class TimeoutFixture { public int startup; public int catalog; public int execution; }

        [Test]
        public void OpenCodeReadinessUsesOnlyTheNamedServerObject()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "{\"note\":\"sgg-unity-mcp stdio-shim powershell.exe\",\"mcp\":{\"other\":{\"type\":\"local\",\"enabled\":true}}}");
                Assert.That(GatewayNetworkModeManager.IsOpenCodeConfigReady(path, GatewayNetworkModeManager.ModeWindowsStdio, "http://127.0.0.1:27182/mcp"), Is.False);

                File.WriteAllText(path, "{// compact JSONC\n\"mcp\":{\"sgg-unity-mcp\":{\"type\":\"remote\",\"url\":\"http://10.0.0.2:27182/mcp\",\"headers\":{\"X-SGG-Unity-MCP-Token\":\"$SGG_UNITY_MCP_TOKEN\"},\"enabled\":true,\"timeout\":3900000}}}");
                Assert.That(GatewayNetworkModeManager.IsOpenCodeConfigReady(path, GatewayNetworkModeManager.ModeRemoteHttp, "http://10.0.0.2:27182/mcp"), Is.False);

                File.WriteAllText(path, "{\"mcp\":{\"sgg-unity-mcp\":{\"type\":\"remote\",\"url\":\"http://10.0.0.2:27182/mcp\",\"headers\":{\"X-SGG-Unity-MCP-Token\":\"$SGG_UNITY_MCP_TOKEN\"},\"enabled\":true,\"timeout\":3.9e6}}}");
                Assert.That(GatewayNetworkModeManager.IsOpenCodeConfigReady(path, GatewayNetworkModeManager.ModeRemoteHttp, "http://10.0.0.2:27182/mcp"), Is.False);

                File.WriteAllText(path, "{\"mcp\":{\"sgg-unity-mcp\":{\"type\":\"remote\",\"url\":\"http://10.0.0.2:27182/mcp\",\"headers\":{\"X-SGG-Unity-MCP-Token\":\"$SGG_UNITY_MCP_TOKEN\"},\"enabled\":true,\"timeout\":30000}}}");
                Assert.That(GatewayNetworkModeManager.IsOpenCodeConfigReady(path, GatewayNetworkModeManager.ModeRemoteHttp, "http://10.0.0.2:27182/mcp"), Is.False);

                File.WriteAllText(path, "{\"mcp\":{\"sgg-unity-mcp\":{\"type\":\"remote\",\"url\":\"http://10.0.0.2:27182/mcp\",\"enabled\":true}}}");
                Assert.That(GatewayNetworkModeManager.IsOpenCodeConfigReady(path, GatewayNetworkModeManager.ModeRemoteHttp, "http://10.0.0.2:27182/mcp"), Is.False);

                File.WriteAllText(path, "{\"profiles\":{\"mcp\":{\"sgg-unity-mcp\":{\"type\":\"remote\",\"url\":\"http://10.0.0.2:27182/mcp\",\"headers\":{\"X-SGG-Unity-MCP-Token\":\"$SGG_UNITY_MCP_TOKEN\"},\"enabled\":true}}}}");
                Assert.That(GatewayNetworkModeManager.IsOpenCodeConfigReady(path, GatewayNetworkModeManager.ModeRemoteHttp, "http://10.0.0.2:27182/mcp"), Is.False);

                File.WriteAllText(path, "{\"mcp\":{\"other\":{\"sgg-unity-mcp\":{\"type\":\"remote\",\"url\":\"http://10.0.0.2:27182/mcp\",\"headers\":{\"X-SGG-Unity-MCP-Token\":\"$SGG_UNITY_MCP_TOKEN\"},\"enabled\":true}}}}");
                Assert.That(GatewayNetworkModeManager.IsOpenCodeConfigReady(path, GatewayNetworkModeManager.ModeRemoteHttp, "http://10.0.0.2:27182/mcp"), Is.False);

                File.WriteAllText(path, "{\"mcp\":{\"sgg-unity-mcp\":{\"type\":\"local\",\"command\":\"powershell.exe stdio-shim http://127.0.0.1:27182/mcp\",\"enabled\":true}}}");
                Assert.That(GatewayNetworkModeManager.IsOpenCodeConfigReady(path, GatewayNetworkModeManager.ModeWindowsStdio, "http://127.0.0.1:27182/mcp"), Is.False);

                File.WriteAllText(path, "{\"mcp\":{\"sgg-unity-mcp\":{\"type\":\"local\",\"command\":[\"powershell.exe\",\"-NoProfile\",\"-ExecutionPolicy\",\"Bypass\",\"-Command\",\"$pointer='active-runtime.json'; $cli='gateway-cli.mjs'; & $node $cli stdio-shim --url http://127.0.0.1:27182/mcp --token-env 'SGG_UNITY_MCP_TOKEN'\"],\"enabled\":true}}}");
                Assert.That(GatewayNetworkModeManager.IsOpenCodeConfigReady(path, GatewayNetworkModeManager.ModeWindowsStdio, "http://127.0.0.1:27182/mcp"), Is.False);

                File.WriteAllText(path, "{\"mcp\":{\"sgg-unity-mcp\":{\"type\":\"remote\" \"url\":\"http://10.0.0.2:27182/mcp\",\"headers\":{\"X-SGG-Unity-MCP-Token\":\"$SGG_UNITY_MCP_TOKEN\"},\"enabled\":true}}}");
                Assert.That(GatewayNetworkModeManager.IsOpenCodeConfigReady(path, GatewayNetworkModeManager.ModeRemoteHttp, "http://10.0.0.2:27182/mcp"), Is.False);

                File.WriteAllText(path, "{\"mcp\":{\"sgg-unity-mcp\":{\"type\":\"remote\",\"url\":\"<windows-host-ip>\",\"headers\":{\"X-SGG-Unity-MCP-Token\":\"$SGG_UNITY_MCP_TOKEN\"},\"enabled\":true}}}");
                Assert.That(GatewayNetworkModeManager.IsOpenCodeConfigReady(path, GatewayNetworkModeManager.ModeRemoteHttp, "<windows-host-ip>"), Is.False);

                File.WriteAllText(path, "{\"broken\":nope,\"mcp\":{\"sgg-unity-mcp\":{\"type\":\"remote\",\"url\":\"http://10.0.0.2:27182/mcp\",\"headers\":{\"X-SGG-Unity-MCP-Token\":\"$SGG_UNITY_MCP_TOKEN\"},\"enabled\":true}}}");
                Assert.That(GatewayNetworkModeManager.IsOpenCodeConfigReady(path, GatewayNetworkModeManager.ModeRemoteHttp, "http://10.0.0.2:27182/mcp"), Is.False);

                var localStatus = new GatewayNetworkModeManager.NetworkStatus
                {
                    SelectedMode = GatewayNetworkModeManager.ModeWindowsStdio,
                    McpUrl = "http://127.0.0.1:28182/mcp",
                    TokenEnv = "CUSTOM_MCP_TOKEN",
                };
                File.WriteAllText(path, GatewayNetworkModeManager.OpenCodeConfigSnippet(localStatus));
                Assert.That(GatewayNetworkModeManager.IsOpenCodeConfigReady(path, localStatus.SelectedMode, localStatus.McpUrl, localStatus.TokenEnv), Is.True);

                var remoteStatus = new GatewayNetworkModeManager.NetworkStatus
                {
                    SelectedMode = GatewayNetworkModeManager.ModeRemoteHttp,
                    McpUrl = "http://10.0.0.2:28182/mcp",
                    TokenEnv = "CUSTOM_MCP_TOKEN",
                };
                File.WriteAllText(path, GatewayNetworkModeManager.OpenCodeConfigSnippet(remoteStatus));
                Assert.That(GatewayNetworkModeManager.IsOpenCodeConfigReady(path, remoteStatus.SelectedMode, remoteStatus.McpUrl, remoteStatus.TokenEnv), Is.True);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
