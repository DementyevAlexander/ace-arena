using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Setup;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class GatewayRuntimeCheckoutTests
    {
        [Test]
        public void LocalWindowsCheckoutIsCanonicalizedBeforeStrictInstalledRuntimeValidation()
        {
            var fixtureRoot = Path.Combine(Path.GetTempPath(), "sgg-runtime-checkout-" + Guid.NewGuid().ToString("N"));
            var packageRoot = Path.Combine(fixtureRoot, "package");
            var installedRoot = Path.Combine(fixtureRoot, "installed");
            var sourceRoot = Path.Combine(UnityMcpPackagePaths.PackageResolvedPath, "Editor", "GatewayRuntime");
            var artifactPaths = new[]
            {
                "gateway-cli.mjs", "gateway-cli.LEGAL.txt", "THIRD_PARTY_NOTICES.md", "sbom.cdx.json",
                "opencode-presentation/sgg-plugin-manifest.json", "opencode-presentation/package.json", "opencode-presentation/server.js",
            };

            try
            {
                foreach (var relativePath in artifactPaths)
                {
                    var original = File.ReadAllBytes(Path.Combine(sourceRoot, relativePath));
                    var converted = new List<byte>(original.Length + original.Length / 20);
                    foreach (var value in original)
                    {
                        if (value == '\n') converted.Add((byte)'\r');
                        converted.Add(value);
                    }
                    var packagePath = Path.Combine(packageRoot, relativePath);
                    var installedPath = Path.Combine(installedRoot, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(packagePath));
                    Directory.CreateDirectory(Path.GetDirectoryName(installedPath));
                    File.WriteAllBytes(packagePath, converted.ToArray());
                    File.WriteAllBytes(installedPath, converted.ToArray());
                    File.SetAttributes(installedPath, File.GetAttributes(installedPath) | FileAttributes.ReadOnly);

                    using (var sha256 = SHA256.Create())
                    {
                        var expectedSha256 = BitConverter.ToString(sha256.ComputeHash(original)).Replace("-", string.Empty).ToLowerInvariant();
                        GatewayInstaller.CanonicalizeCopiedArtifact(packageRoot, installedRoot, relativePath, expectedSha256);
                    }
                    Assert.That(File.ReadAllBytes(installedPath), Is.EqualTo(original), relativePath);
                }
                File.Copy(Path.Combine(sourceRoot, "gateway-runtime.json"), Path.Combine(packageRoot, "gateway-runtime.json"));
                File.Copy(Path.Combine(sourceRoot, "gateway-runtime.json"), Path.Combine(installedRoot, "gateway-runtime.json"));

                Assert.That(GatewayInstaller.ValidateRuntimeMetadataAtRoot(packageRoot, true, out var packageMessage), Is.True, packageMessage);
                Assert.That(GatewayInstaller.ValidateRuntimeMetadataAtRoot(packageRoot, false, out _), Is.False);
                Assert.That(GatewayInstaller.ValidateRuntimeMetadataAtRoot(installedRoot, false, out var installedMessage), Is.True, installedMessage);

                var cliPath = Path.Combine(packageRoot, "gateway-cli.mjs");
                var bytes = File.ReadAllBytes(cliPath);
                bytes[0] ^= 1;
                File.WriteAllBytes(cliPath, bytes);
                Assert.That(GatewayInstaller.ValidateRuntimeMetadataAtRoot(packageRoot, true, out var tamperedMessage), Is.False, tamperedMessage);

                bytes[0] ^= 1;
                var firstCr = Array.IndexOf(bytes, (byte)'\r');
                Assert.That(firstCr, Is.GreaterThanOrEqualTo(0));
                var mixed = new byte[bytes.Length - 1];
                Buffer.BlockCopy(bytes, 0, mixed, 0, firstCr);
                Buffer.BlockCopy(bytes, firstCr + 1, mixed, firstCr, bytes.Length - firstCr - 1);
                File.WriteAllBytes(cliPath, mixed);
                Assert.That(GatewayInstaller.ValidateRuntimeMetadataAtRoot(packageRoot, true, out var mixedMessage), Is.False, mixedMessage);
            }
            finally
            {
                if (Directory.Exists(fixtureRoot))
                {
                    foreach (var file in Directory.GetFiles(fixtureRoot, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    Directory.Delete(fixtureRoot, true);
                }
            }
        }
    }
}
