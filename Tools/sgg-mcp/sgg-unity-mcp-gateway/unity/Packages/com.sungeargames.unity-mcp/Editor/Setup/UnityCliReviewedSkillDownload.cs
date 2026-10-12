using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SGG.UnityMCP.Editor.Setup
{
    // Acquisition is data-only. The generated lock records the bundled reviewed
    // artifact; it is not a skills CLI receipt or a substitute for content hashing.
    internal static class UnityCliReviewedSkillDownload
    {
        internal const int MaxFileBytes = 131072;
        internal const int RequestTimeoutMilliseconds = 15000;
        internal const int ReadTimeoutMilliseconds = 10000;
        internal const int FileTimeoutMilliseconds = 30000;
        internal const int TotalTimeoutMilliseconds = 150000;
        internal static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(3);
        internal const string CanonicalReviewedLock = "{\"version\":1,\"skills\":{\"unity-cli\":{\"source\":\"Unity-Technologies/skills\",\"sourceType\":\"github\",\"skillPath\":\"skills/unity-cli/SKILL.md\",\"computedHash\":\"" + UnityCliProjectSkillInstaller.ReviewedSkillsHash + "\"}}}";

        static readonly IReadOnlyList<string> s_relativePaths = Array.AsReadOnly(new[]
        {
            "CHANGELOG.md",
            "SECURITY.md",
            "SKILL.md",
            "references/auth-license-cloud.md",
            "references/build-run-test.md",
            "references/cloud-automation.md",
            "references/collaboration.md",
            "references/config-hub.md",
            "references/diagnostics-maintenance.md",
            "references/editors-install.md",
            "references/integration-advanced.md",
            "references/playmode-verification-loop.md",
            "references/projects-templates.md",
            "references/version-control.md",
        });

        internal static IReadOnlyList<string> RelativePaths => s_relativePaths;

        internal static bool TryGetPowerShellPath(out string path, out string error)
        {
            path = string.Empty;
            error = "Reviewed skill download requires Windows system PowerShell.";
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return false;
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (string.IsNullOrEmpty(system)) return false;
            var candidate = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(candidate)) return false;
            path = candidate;
            error = string.Empty;
            return true;
        }

        internal static string[] BuildArguments(string stagingRoot)
        {
            if (string.IsNullOrWhiteSpace(stagingRoot)
                || stagingRoot.Length < 4 || !char.IsLetter(stagingRoot[0]) || stagingRoot[1] != ':'
                || (stagingRoot[2] != '\\' && stagingRoot[2] != '/'))
            {
                throw new ArgumentException("Staging must use an absolute local Windows path.", nameof(stagingRoot));
            }
            foreach (var character in stagingRoot)
            {
                if (char.IsControl(character)) throw new ArgumentException("Staging path contains a control character.", nameof(stagingRoot));
            }
            var root = Path.GetFullPath(stagingRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(root, Path.GetPathRoot(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("A volume root cannot be a staging directory.", nameof(stagingRoot));
            }
            var files = new List<string>();
            foreach (var relative in s_relativePaths) files.Add(QuoteLiteral(relative));
            var script = ScriptTemplate
                .Replace("{{SOURCE}}", QuoteLiteral(UnityCliProjectSkillInstaller.ReviewedSource))
                .Replace("{{COMMIT}}", QuoteLiteral(UnityCliProjectSkillInstaller.ReviewedSourceCommit))
                .Replace("{{FILES}}", string.Join(",", files))
                .Replace("{{FILE_COUNT}}", s_relativePaths.Count.ToString(CultureInfo.InvariantCulture))
                .Replace("{{MAX_FILE_BYTES}}", MaxFileBytes.ToString(CultureInfo.InvariantCulture))
                .Replace("{{MAX_TOTAL_BYTES}}", UnityCliProjectSkillInstaller.MaxSkillBytes.ToString(CultureInfo.InvariantCulture))
                .Replace("{{MAX_FILES}}", UnityCliProjectSkillInstaller.MaxSkillFiles.ToString(CultureInfo.InvariantCulture))
                .Replace("{{REQUEST_TIMEOUT}}", RequestTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture))
                .Replace("{{READ_TIMEOUT}}", ReadTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture))
                .Replace("{{FILE_TIMEOUT}}", FileTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture))
                .Replace("{{TOTAL_TIMEOUT}}", TotalTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture))
                .Replace("{{CONTENT_HASH}}", QuoteLiteral(UnityCliProjectSkillInstaller.ReviewedContentHash))
                .Replace("{{LOCK}}", QuoteLiteral(CanonicalReviewedLock));
            // Bind the only caller-controlled value last. Its contents must not
            // be interpreted as template tokens or PowerShell expressions.
            script = script.Replace("{{ROOT}}", QuoteLiteral(root));
            return new[] { "-NoProfile", "-NonInteractive", "-Command", script };
        }

        static string QuoteLiteral(string value) => "'" + value.Replace("'", "''") + "'";

        const string ScriptTemplate = @"
$ErrorActionPreference='Stop';
$ProgressPreference='SilentlyContinue';
Set-StrictMode -Version 2.0;
$root=[IO.Path]::GetFullPath({{ROOT}}).TrimEnd([char]92);
$source={{SOURCE}};
$commit={{COMMIT}};
$files=@({{FILES}});
$maxFileBytes={{MAX_FILE_BYTES}};
$maxTotalBytes={{MAX_TOTAL_BYTES}};
$maxFiles={{MAX_FILES}};
$requestTimeout={{REQUEST_TIMEOUT}};
$readTimeout={{READ_TIMEOUT}};
$fileTimeout={{FILE_TIMEOUT}};
$totalTimeout={{TOTAL_TIMEOUT}};
$expectedHash={{CONTENT_HASH}};
$lockText={{LOCK}};
$budget=[Diagnostics.Stopwatch]::StartNew();

function AssertSafeDirectory([string]$path) {
    $current=New-Object IO.DirectoryInfo($path);
    while ($null -ne $current) {
        $attributes=[IO.File]::GetAttributes($current.FullName);
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            ($attributes -band [IO.FileAttributes]::Directory) -eq 0) {
            throw 'Staging directory or ancestor is unsafe.';
        }
        $current=$current.Parent;
    }
}
function AssertAbsent([string]$path) {
    try { [void][IO.File]::GetAttributes($path); }
    catch [IO.FileNotFoundException] { return; }
    catch [IO.DirectoryNotFoundException] { return; }
    throw 'Staging destination already exists; nothing may be overwritten.';
}
function AssertWithinRoot([string]$path) {
    $full=[IO.Path]::GetFullPath($path);
    if (-not $full.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Staging destination escaped the fixed root.';
    }
}
function NewSafeDirectory([string]$path) {
    AssertWithinRoot $path;
    AssertSafeDirectory ([IO.Path]::GetDirectoryName($path));
    AssertAbsent $path;
    [void][IO.Directory]::CreateDirectory($path);
    AssertSafeDirectory $path;
}
function AssertSafeFile([string]$path) {
    AssertWithinRoot $path;
    AssertSafeDirectory ([IO.Path]::GetDirectoryName($path));
    $attributes=[IO.File]::GetAttributes($path);
    if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        ($attributes -band [IO.FileAttributes]::Directory) -ne 0) { throw 'Staged file is unsafe.'; }
}
function AssertEntries([string]$path,[string[]]$names) {
    AssertSafeDirectory $path;
    $count=0;
    foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($path)) {
        $count++;
        if ($count -gt $maxFiles -or $names -cnotcontains [IO.Path]::GetFileName($entry)) {
            throw 'Staged file set differs from the reviewed whitelist.';
        }
    }
    if ($count -ne $names.Length) { throw 'Staged file set is incomplete.'; }
}
function RemainingMilliseconds([Diagnostics.Stopwatch]$fileBudget) {
    $remaining=[Math]::Min($totalTimeout-$budget.ElapsedMilliseconds,$fileTimeout-$fileBudget.ElapsedMilliseconds);
    if ($remaining -le 0) { throw 'Reviewed download deadline exceeded.'; }
    return [int]$remaining;
}

try {
    if ($root -notmatch '^[A-Za-z]:\\' -or $root.Length -le 3) { throw 'Staging root is not a local child directory.'; }
    AssertSafeDirectory $root;
    foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($root)) { throw 'Staging root must be newly created and empty.'; }
    if ($files.Count -ne {{FILE_COUNT}} -or $files.Count -gt $maxFiles) { throw 'Reviewed whitelist is invalid.'; }
    $agents=Join-Path $root '.agents';
    $skills=Join-Path $agents 'skills';
    $skillRoot=Join-Path $skills 'unity-cli';
    $references=Join-Path $skillRoot 'references';
    NewSafeDirectory $agents;
    NewSafeDirectory $skills;
    NewSafeDirectory $skillRoot;
    NewSafeDirectory $references;
    [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12;
    $buffer=New-Object byte[] 4096;
    [long]$totalBytes=0;
    foreach ($relative in $files) {
        $fileBudget=[Diagnostics.Stopwatch]::StartNew();
        $url=$source+$relative;
        $path=Join-Path $skillRoot ($relative.Replace('/',[IO.Path]::DirectorySeparatorChar));
        AssertWithinRoot $path;
        AssertSafeDirectory ([IO.Path]::GetDirectoryName($path));
        AssertAbsent $path;
        $request=[Net.HttpWebRequest]::Create($url);
        $request.Method='GET';
        $request.AllowAutoRedirect=$false;
        $request.UseDefaultCredentials=$false;
        $request.Credentials=$null;
        $request.PreAuthenticate=$false;
        $request.Proxy=$null;
        $request.KeepAlive=$false;
        $request.MaximumResponseHeadersLength=16;
        $request.AutomaticDecompression=[Net.DecompressionMethods]::None;
        $request.CachePolicy=New-Object Net.Cache.RequestCachePolicy([Net.Cache.RequestCacheLevel]::NoCacheNoStore);
        $request.UserAgent='SGG-Unity-MCP-Reviewed-Skill';
        $request.Timeout=[Math]::Min($requestTimeout,(RemainingMilliseconds $fileBudget));
        $request.ReadWriteTimeout=[Math]::Min($readTimeout,(RemainingMilliseconds $fileBudget));
        $response=$null; $inputStream=$null; $outputStream=$null;
        try {
            $response=$request.GetResponse();
            if ($response.StatusCode -ne [Net.HttpStatusCode]::OK -or $response.ResponseUri.AbsoluteUri -cne $url) {
                throw 'Reviewed source did not return its exact URL with status 200.';
            }
            if ($response.ContentLength -gt $maxFileBytes -or
                ($response.ContentLength -ge 0 -and $totalBytes+$response.ContentLength -gt $maxTotalBytes)) {
                throw 'Reviewed download exceeds the declared byte limits.';
            }
            AssertSafeDirectory ([IO.Path]::GetDirectoryName($path));
            AssertAbsent $path;
            $outputStream=[IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);
            $inputStream=$response.GetResponseStream();
            [long]$fileBytes=0;
            while ($true) {
                $remaining=RemainingMilliseconds $fileBudget;
                if ($inputStream.CanTimeout) { $inputStream.ReadTimeout=[Math]::Min($readTimeout,$remaining); }
                $read=$inputStream.Read($buffer,0,$buffer.Length);
                [void](RemainingMilliseconds $fileBudget);
                if ($read -eq 0) { break; }
                $fileBytes+=$read; $totalBytes+=$read;
                if ($fileBytes -gt $maxFileBytes -or $totalBytes -gt $maxTotalBytes) { throw 'Reviewed download exceeds the streamed byte limits.'; }
                $outputStream.Write($buffer,0,$read);
            }
            if ($fileBytes -eq 0 -or ($response.ContentLength -ge 0 -and $fileBytes -ne $response.ContentLength)) {
                throw 'Reviewed download is empty or incomplete.';
            }
        } finally {
            if ($null -ne $outputStream) { $outputStream.Dispose(); }
            if ($null -ne $inputStream) { $inputStream.Dispose(); }
            if ($null -ne $response) { $response.Close(); }
            $request.Abort();
        }
    }
    AssertEntries $skillRoot @('CHANGELOG.md','SECURITY.md','SKILL.md','references');
    $referenceNames=@($files | Where-Object { $_.StartsWith('references/',[StringComparison]::Ordinal) } | ForEach-Object { $_.Substring(11); });
    AssertEntries $references $referenceNames;
    $ordered=[string[]]$files.Clone();
    [Array]::Sort($ordered,[StringComparer]::Ordinal);
    $hashBuffer=New-Object IO.MemoryStream;
    $sha=[Security.Cryptography.SHA256]::Create();
    [long]$hashFileBytes=0;
    try {
        foreach ($relative in $ordered) {
            if ($budget.ElapsedMilliseconds -ge $totalTimeout) { throw 'Reviewed download deadline exceeded.'; }
            $path=Join-Path $skillRoot ($relative.Replace('/',[IO.Path]::DirectorySeparatorChar));
            AssertSafeFile $path;
            $pathBytes=[Text.Encoding]::UTF8.GetBytes($relative+[char]10);
            $hashBuffer.Write($pathBytes,0,$pathBytes.Length);
            $fileStream=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read);
            try {
                $hashFileBytes+=$fileStream.Length;
                if ($fileStream.Length -gt $maxFileBytes -or $hashFileBytes -gt $maxTotalBytes) { throw 'Staged hash input exceeds byte limits.'; }
                $fileStream.CopyTo($hashBuffer,4096);
            } finally { $fileStream.Dispose(); }
            $hashBuffer.WriteByte(10);
        }
        $hashBuffer.Position=0;
        $contentHash=[BitConverter]::ToString($sha.ComputeHash($hashBuffer)).Replace('-','').ToLowerInvariant();
    } finally { $sha.Dispose(); $hashBuffer.Dispose(); }
    if (-not [string]::Equals($contentHash,$expectedHash,[StringComparison]::Ordinal)) { throw 'Downloaded content does not match the bundled reviewed hash.'; }
    if ($budget.ElapsedMilliseconds -ge $totalTimeout) { throw 'Reviewed download deadline exceeded.'; }
    AssertSafeDirectory $root;
    $lockPath=Join-Path $root 'skills-lock.json';
    AssertWithinRoot $lockPath;
    AssertAbsent $lockPath;
    $lockStream=[IO.File]::Open($lockPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);
    try {
        $lockBytes=[Text.Encoding]::UTF8.GetBytes($lockText);
        $lockStream.Write($lockBytes,0,$lockBytes.Length);
    } finally { $lockStream.Dispose(); }
    @{ success=$true; source=$source; commit=$commit; fileCount=$files.Count; totalBytes=$totalBytes; contentHash=$contentHash; lockKind='gateway_reviewed_acquisition_metadata'; } | ConvertTo-Json -Compress;
    exit 0;
} catch {
    [Console]::Error.WriteLine('Reviewed skill download failed: '+$_.Exception.Message);
    exit 1;
}
";
    }
}
