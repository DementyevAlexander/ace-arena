using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Paths;

namespace SGG.UnityMCP.Editor.Commands
{
    internal static class PrefabYamlIntegrity
    {
        internal const int MaxIssues = 64;
        internal const int DefaultScanBytes = 262144;
        internal const int HardMaxScanBytes = 1048576;

        const int MaxObjects = 4096;
        const int MaxChildrenPerObject = 256;
        const int MaxEvidence = 16;
        const int MaxPathChars = 512;
        const int MaxNameChars = 256;
        const int MaxLineChars = 8192;
        const int MaxLineNumber = 100000;
        const int MaxHierarchyDepth = 64;
        const int MaxDiagnosticMessages = 256;
        const int MaxDiagnosticMessageChars = 8192;
        const int MaxObservationPayloadChars = 12288;
        const int ReadBufferSize = 8192;
        const string InvalidAssetPathError = "invalid_asset_path";
        const string AssetNotFoundError = "asset_not_found";
        const string AssetReadFailedError = "asset_read_failed";
        const string AssetReadDeniedError = "asset_read_denied";
        const string InvalidUtf8Error = "invalid_utf8";
        const string InvalidPrefabYamlError = "invalid_prefab_yaml";
        const string PathReparseRejectedError = "path_reparse_rejected";
        const string AssetLoadFailedError = "asset_load_failed";
        const string TransformChildMarker = "Transform child";
        const string CantBeLoaded = "can't be loaded";
        const string CantBeLoadedAscii = "cant be loaded";
        const string ObservationPayloadHeader = "transform_child_cant_be_loaded_v1";

        static readonly Encoding s_utf8 = new UTF8Encoding(false, true);
        static readonly string[] s_nextValidActions =
        {
            "prefab.write_risk_report",
            "prefab.edit.preview",
            "logs.console.query"
        };

        internal static PrefabYamlIntegrityResult AnalyzePrefabAsset(string prefabPath, int maxIssues, int maxScanBytes)
        {
            if (string.IsNullOrEmpty(prefabPath) || prefabPath.Length > MaxPathChars)
            {
                return PrefabYamlIntegrityResult.Failure(string.Empty, false, false, InvalidAssetPathError);
            }

            string assetPath;
            try
            {
                assetPath = PathGuard.RequireUnityAssetPath(prefabPath);
            }
            catch (Exception)
            {
                return PrefabYamlIntegrityResult.Failure(string.Empty, false, false, InvalidAssetPathError);
            }

            if (assetPath.Length > MaxPathChars || !assetPath.StartsWith("Assets/", StringComparison.Ordinal)
                || !assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                return PrefabYamlIntegrityResult.Failure(string.Empty, false, false, InvalidAssetPathError);
            }

            var scanBytes = ClampScanBytes(maxScanBytes);
            var fullPath = Path.Combine(ProjectPaths.ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
            if (ContainsReparsePoint(ProjectPaths.ProjectRoot, assetPath))
            {
                return PrefabYamlIntegrityResult.Failure(assetPath, File.Exists(fullPath), false, PathReparseRejectedError);
            }
            try
            {
                using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, ReadBufferSize, FileOptions.SequentialScan))
                {
                    var read = ReadBounded(stream, scanBytes);
                    if (!string.IsNullOrEmpty(read.Error))
                    {
                        return PrefabYamlIntegrityResult.Failure(assetPath, true, read.ScanTruncated, read.Error);
                    }

                    return AnalyzeText(assetPath, true, read.Text, maxIssues, read.ScanTruncated, string.Empty);
                }
            }
            catch (FileNotFoundException)
            {
                return PrefabYamlIntegrityResult.Failure(assetPath, false, false, AssetNotFoundError);
            }
            catch (DirectoryNotFoundException)
            {
                return PrefabYamlIntegrityResult.Failure(assetPath, false, false, AssetNotFoundError);
            }
            catch (UnauthorizedAccessException)
            {
                return PrefabYamlIntegrityResult.Failure(assetPath, false, false, AssetReadDeniedError);
            }
            catch (IOException)
            {
                return PrefabYamlIntegrityResult.Failure(assetPath, false, false, AssetReadFailedError);
            }
            catch (Exception)
            {
                return PrefabYamlIntegrityResult.Failure(assetPath, false, false, AssetReadFailedError);
            }
        }

        internal static PrefabYamlIntegrityResult AnalyzeYamlForTests(string assetPath, string yaml, int maxIssues, int maxScanBytes)
        {
            if (string.IsNullOrEmpty(assetPath) || assetPath.Length > MaxPathChars)
            {
                return PrefabYamlIntegrityResult.Failure(string.Empty, false, false, InvalidAssetPathError);
            }

            string normalizedAssetPath;
            try
            {
                normalizedAssetPath = PathGuard.RequireUnityAssetPath(assetPath);
            }
            catch (Exception)
            {
                return PrefabYamlIntegrityResult.Failure(string.Empty, false, false, InvalidAssetPathError);
            }

            if (normalizedAssetPath.Length > MaxPathChars || !normalizedAssetPath.StartsWith("Assets/", StringComparison.Ordinal)
                || !normalizedAssetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                return PrefabYamlIntegrityResult.Failure(string.Empty, false, false, InvalidAssetPathError);
            }

            BoundedText bounded;
            try
            {
                bounded = BoundYaml(yaml ?? string.Empty, ClampScanBytes(maxScanBytes));
            }
            catch (EncoderFallbackException)
            {
                return PrefabYamlIntegrityResult.Failure(normalizedAssetPath, true, false, InvalidUtf8Error);
            }

            return AnalyzeText(normalizedAssetPath, true, bounded.Text, maxIssues, bounded.ScanTruncated, string.Empty);
        }

        internal static string PrefabImportDiagnosticsJson(IEnumerable<string> messages)
        {
            return PrefabImportDiagnosticsJsonFromObservationPayload(PrefabImportObservationPayload(messages));
        }

        internal static string PrefabImportObservationPayload(IEnumerable<string> messages)
        {
            var payload = string.Empty;
            var inspected = 0;
            if (messages != null)
            {
                try
                {
                    foreach (var message in messages)
                    {
                        if (inspected >= MaxDiagnosticMessages)
                        {
                            if (!string.IsNullOrEmpty(payload))
                            {
                                payload = MergePrefabImportObservationPayloads(payload, ObservationPayload(new List<string>(), true));
                            }
                            break;
                        }
                        inspected++;

                        if (!TryBuildPrefabImportObservationPayload(message, out var messagePayload))
                        {
                            continue;
                        }
                        payload = MergePrefabImportObservationPayloads(payload, messagePayload);
                    }
                }
                catch (Exception)
                {
                    if (!string.IsNullOrEmpty(payload))
                    {
                        payload = MergePrefabImportObservationPayloads(payload, ObservationPayload(new List<string>(), true));
                    }
                }
            }
            return payload;
        }

        static string BuildPrefabImportDiagnosticsJson(List<string> paths, bool scanTruncated)
        {
            var builder = new StringBuilder(512);
            builder.Append("{\"schema_version\":1,\"read_only\":true");
            builder.Append(",\"has_transform_child_cant_be_loaded\":true");
            builder.Append(",\"error_code\":\"asset_import_failed\"");
            builder.Append(",\"scan_truncated\":").Append(JsonUtil.Bool(scanTruncated));
            builder.Append(",\"asset_paths\":[");
            for (var index = 0; index < paths.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append(JsonUtil.String(paths[index]));
            }

            builder.Append("],\"next_valid_actions\":[");
            for (var index = 0; index < s_nextValidActions.Length; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append(JsonUtil.String(s_nextValidActions[index]));
            }

            builder.Append("]}");
            return builder.ToString();
        }

        internal static bool TryBuildPrefabImportObservationPayload(string message, out string payload)
        {
            payload = string.Empty;
            var boundedMessage = Bounded(message ?? string.Empty, MaxDiagnosticMessageChars);
            if (!HasTransformChildLoadPhrase(boundedMessage))
            {
                return false;
            }
            var paths = new List<string>();
            var truncated = ExtractPrefabPaths(boundedMessage, paths);
            payload = ObservationPayload(paths, truncated);
            return true;
        }

        internal static string MergePrefabImportObservationPayloads(string left, string right)
        {
            if (!TryParseObservationPayload(left, out var leftPaths, out var leftTruncated))
            {
                return TryParseObservationPayload(right, out _, out _) ? right : string.Empty;
            }
            if (!TryParseObservationPayload(right, out var rightPaths, out var rightTruncated))
            {
                return left;
            }
            for (var index = 0; index < rightPaths.Count; index++)
            {
                var path = rightPaths[index];
                if (leftPaths.Contains(path))
                {
                    continue;
                }
                if (leftPaths.Count >= MaxEvidence)
                {
                    leftTruncated = true;
                    continue;
                }
                leftPaths.Add(path);
            }
            return ObservationPayload(leftPaths, leftTruncated || rightTruncated);
        }

        internal static string PrefabImportDiagnosticsJsonFromObservationPayload(string payload)
        {
            return TryParseObservationPayload(payload, out var paths, out var truncated)
                ? BuildPrefabImportDiagnosticsJson(paths, truncated)
                : string.Empty;
        }

        internal static string BoundedAssetLoadError(bool assetLoadable, string assetLoadError)
        {
            if (assetLoadable || string.IsNullOrEmpty(assetLoadError))
            {
                return string.Empty;
            }

            switch (assetLoadError)
            {
                case "asset_import_failed":
                case AssetLoadFailedError:
                case AssetNotFoundError:
                    return assetLoadError;
                default:
                    return AssetLoadFailedError;
            }
        }

        static PrefabYamlIntegrityResult AnalyzeText(string assetPath, bool fileExists, string yaml, int maxIssues, bool scanTruncated, string readError)
        {
            var state = new ParserState(scanTruncated);
            ParseYaml(yaml ?? string.Empty, state);
            if (string.IsNullOrEmpty(readError) && !state.ScanTruncated && !state.StructurallyValid)
            {
                readError = InvalidPrefabYamlError;
            }
            var issues = state.StructurallyValid && !state.ScanTruncated
                ? BuildIssues(state, ClampIssues(maxIssues))
                : new List<PrefabYamlIntegrityIssue>();
            var analysisComplete = fileExists && string.IsNullOrEmpty(readError) && !state.ScanTruncated;
            return new PrefabYamlIntegrityResult(assetPath, fileExists, analysisComplete, state.ScanTruncated, state.IssuesTruncated, readError, issues, state.Evidence);
        }

        static BoundedRead ReadBounded(FileStream stream, int maxBytes)
        {
            var buffer = new byte[maxBytes];
            var total = 0;
            try
            {
                while (total < buffer.Length)
                {
                    var count = stream.Read(buffer, total, buffer.Length - total);
                    if (count <= 0)
                    {
                        break;
                    }

                    total += count;
                }

                var scanTruncated = stream.Length > total;
                string text;
                try
                {
                    text = s_utf8.GetString(buffer, 0, total);
                }
                catch (DecoderFallbackException)
                {
                    return new BoundedRead(string.Empty, scanTruncated, InvalidUtf8Error);
                }

                return new BoundedRead(text, scanTruncated, string.Empty);
            }
            catch (IOException)
            {
                return new BoundedRead(string.Empty, false, AssetReadFailedError);
            }
            catch (UnauthorizedAccessException)
            {
                return new BoundedRead(string.Empty, false, AssetReadDeniedError);
            }
            catch (Exception)
            {
                return new BoundedRead(string.Empty, false, AssetReadFailedError);
            }
        }

        static BoundedText BoundYaml(string yaml, int maxBytes)
        {
            var builder = new StringBuilder(Math.Min(yaml.Length, maxBytes));
            var bytes = 0;
            for (var index = 0; index < yaml.Length; index++)
            {
                var charCount = 1;
                if (char.IsHighSurrogate(yaml[index]) && index + 1 < yaml.Length && char.IsLowSurrogate(yaml[index + 1]))
                {
                    charCount = 2;
                }

                var charBytes = s_utf8.GetByteCount(yaml, index, charCount);
                if (bytes + charBytes > maxBytes)
                {
                    return new BoundedText(builder.ToString(), true);
                }

                builder.Append(yaml, index, charCount);
                bytes += charBytes;
                index += charCount - 1;
            }

            return new BoundedText(builder.ToString(), false);
        }

        static void ParseYaml(string yaml, ParserState state)
        {
            ParsedRecord current = null;
            var childrenSection = false;
            using (var reader = new StringReader(yaml))
            {
                string line;
                var lineNumber = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    lineNumber++;
                    if (lineNumber > MaxLineNumber)
                    {
                        state.ScanTruncated = true;
                        break;
                    }

                    if (line.Length > MaxLineChars)
                    {
                        state.ScanTruncated = true;
                        continue;
                    }

                    if (lineNumber == 1 && line.TrimStart('\uFEFF').StartsWith("%YAML 1.", StringComparison.Ordinal))
                    {
                        state.SawYamlDirective = true;
                    }

                    if (TryParseHeader(line, out var classId, out var fileId, out var stripped))
                    {
                        current = null;
                        childrenSection = false;
                        if (state.Records.Count >= MaxObjects)
                        {
                            state.ScanTruncated = true;
                            continue;
                        }

                        if (state.ById.ContainsKey(fileId))
                        {
                            state.ScanTruncated = true;
                            continue;
                        }

                        current = new ParsedRecord(classId, fileId, lineNumber, stripped);
                        state.Records.Add(current);
                        state.ById.Add(fileId, current);
                        if (classId == 1 && !stripped) state.SawGameObject = true;
                        if ((classId == 4 || classId == 224) && !stripped) state.SawTransform = true;
                        continue;
                    }

                    if (current == null)
                    {
                        continue;
                    }

                    if (current.IsTransform)
                    {
                        if (TryField(line, "m_Children", out var childrenValue))
                        {
                            current.HasChildrenField = true;
                            current.ChildrenListExpectedEntry = false;
                            if (IsEmptyYamlList(childrenValue))
                            {
                                childrenSection = false;
                            }
                            else if (TryParseFileId(childrenValue, out var inlineChildId))
                            {
                                AddChild(current, inlineChildId, lineNumber, state);
                                childrenSection = false;
                            }
                            else if (string.IsNullOrEmpty(childrenValue))
                            {
                                childrenSection = true;
                                current.ChildrenListExpectedEntry = true;
                            }
                            else
                            {
                                childrenSection = false;
                                current.ChildrenMalformed = true;
                            }

                            continue;
                        }

                        if (childrenSection)
                        {
                            var trimmed = line.TrimStart();
                            if (trimmed.StartsWith("-", StringComparison.Ordinal))
                            {
                                if (TryParseFileId(trimmed, out var childId))
                                {
                                    AddChild(current, childId, lineNumber, state);
                                    current.ChildrenListExpectedEntry = false;
                                }
                                else
                                {
                                    current.ChildrenMalformed = true;
                                }

                                continue;
                            }

                            childrenSection = false;
                        }

                        if (TryField(line, "m_GameObject", out var gameObjectValue) && TryParseFileId(gameObjectValue, out var gameObjectId))
                        {
                            current.HasGameObject = true;
                            current.GameObjectId = gameObjectId;
                        }
                        else if (TryField(line, "m_Father", out var fatherValue) && TryParseFileId(fatherValue, out var fatherId))
                        {
                            current.HasFather = true;
                            current.FatherId = fatherId;
                        }
                    }

                    if (current.ClassId == 1 && TryField(line, "m_Name", out var nameValue))
                    {
                        current.Name = BoundedName(nameValue);
                    }

                    if (current.ClassId == 1001 && TryGuid(line, out var guid))
                    {
                        if (state.Evidence.Count >= MaxEvidence)
                        {
                            state.ScanTruncated = true;
                        }
                        else
                        {
                            state.Evidence.Add(new PrefabYamlIntegrityEvidence(guid, current.FileId, lineNumber));
                        }
                    }
                }
            }
        }

        static List<PrefabYamlIntegrityIssue> BuildIssues(ParserState state, int maxIssues)
        {
            var issues = new List<PrefabYamlIntegrityIssue>();
            for (var recordIndex = 0; recordIndex < state.Records.Count; recordIndex++)
            {
                var record = state.Records[recordIndex];
                if (!record.IsTransform || record.IsStripped)
                {
                    continue;
                }

                var path = BuildPath(state, record.FileId);
                for (var childIndex = 0; childIndex < record.Children.Count; childIndex++)
                {
                    var child = record.Children[childIndex];
                    if (child.FileId == 0)
                    {
                        continue;
                    }

                    if (!state.ById.TryGetValue(child.FileId, out var referenced))
                    {
                        if (state.ScanTruncated)
                        {
                            continue;
                        }

                        if (!TryAddIssue(issues, maxIssues, new PrefabYamlIntegrityIssue("missing_transform_file_id", record.FileId, record.ClassId, path, child.FileId, child.Line, 0)))
                        {
                            state.IssuesTruncated = true;
                            return issues;
                        }

                        continue;
                    }

                    if (!referenced.IsTransform)
                    {
                        if (!TryAddIssue(issues, maxIssues, new PrefabYamlIntegrityIssue("child_not_transform", record.FileId, record.ClassId, path, child.FileId, child.Line, referenced.ClassId)))
                        {
                            state.IssuesTruncated = true;
                            return issues;
                        }
                    }
                }
            }

            return issues;
        }

        static bool TryAddIssue(List<PrefabYamlIntegrityIssue> issues, int maxIssues, PrefabYamlIntegrityIssue issue)
        {
            if (issues.Count >= maxIssues)
            {
                return false;
            }

            issues.Add(issue);
            return true;
        }

        static string BuildPath(ParserState state, long transformId)
        {
            var names = new List<string>();
            var visited = new HashSet<long>();
            var currentId = transformId;
            for (var depth = 0; depth < MaxHierarchyDepth && currentId != 0; depth++)
            {
                if (!visited.Add(currentId) || !state.ById.TryGetValue(currentId, out var transform) || !transform.IsTransform)
                {
                    return string.Empty;
                }

                if (!transform.HasGameObject || !state.ById.TryGetValue(transform.GameObjectId, out var gameObject) || gameObject.ClassId != 1 || string.IsNullOrEmpty(gameObject.Name))
                {
                    return string.Empty;
                }

                names.Add(gameObject.Name);
                currentId = transform.HasFather ? transform.FatherId : 0;
            }

            if (currentId != 0 || names.Count == 0)
            {
                return string.Empty;
            }

            var length = 0;
            for (var index = 0; index < names.Count; index++)
            {
                length += names[index].Length + 1;
                if (length > MaxPathChars)
                {
                    return string.Empty;
                }
            }

            var builder = new StringBuilder(length);
            for (var index = names.Count - 1; index >= 0; index--)
            {
                builder.Append('/').Append(names[index]);
            }

            return builder.ToString();
        }

        static void AddChild(ParsedRecord record, long fileId, int line, ParserState state)
        {
            if (fileId == 0)
            {
                record.ChildrenMalformed = true;
                return;
            }
            if (record.Children.Count >= MaxChildrenPerObject)
            {
                state.ScanTruncated = true;
                return;
            }
            for (var index = 0; index < record.Children.Count; index++)
            {
                if (record.Children[index].FileId == fileId)
                {
                    record.ChildrenMalformed = true;
                    return;
                }
            }

            record.Children.Add(new ChildReference(fileId, line));
        }

        static bool HasValidKnownFatherChain(ParserState state, long transformId)
        {
            var visited = new HashSet<long>();
            var currentId = transformId;
            for (var depth = 0; depth < MaxHierarchyDepth && currentId != 0; depth++)
            {
                if (!visited.Add(currentId) || !state.ById.TryGetValue(currentId, out var current) || !current.IsTransform)
                {
                    return false;
                }
                if (current.IsStripped)
                {
                    return true;
                }
                if (!current.HasFather)
                {
                    return false;
                }
                currentId = current.FatherId;
            }
            return currentId == 0;
        }

        static bool TryParseHeader(string line, out int classId, out long fileId, out bool stripped)
        {
            classId = 0;
            fileId = 0;
            stripped = false;
            var marker = line.IndexOf("!u!", StringComparison.Ordinal);
            if (marker < 0 || !line.TrimStart().StartsWith("---", StringComparison.Ordinal))
            {
                return false;
            }

            var classStart = marker + 3;
            var classEnd = classStart;
            while (classEnd < line.Length && char.IsDigit(line[classEnd]))
            {
                classEnd++;
            }

            if (classEnd == classStart || !int.TryParse(line.Substring(classStart, classEnd - classStart), NumberStyles.None, CultureInfo.InvariantCulture, out classId))
            {
                return false;
            }

            var idMarker = line.IndexOf('&', classEnd);
            if (idMarker < 0)
            {
                return false;
            }

            var idStart = idMarker + 1;
            var idEnd = idStart;
            if (idEnd < line.Length && line[idEnd] == '-')
            {
                idEnd++;
            }

            while (idEnd < line.Length && char.IsDigit(line[idEnd]))
            {
                idEnd++;
            }

            stripped = line.IndexOf(" stripped", idEnd, StringComparison.Ordinal) >= 0;
            return idEnd > idStart && long.TryParse(line.Substring(idStart, idEnd - idStart), NumberStyles.Integer, CultureInfo.InvariantCulture, out fileId);
        }

        static bool TryField(string line, string field, out string value)
        {
            value = string.Empty;
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith(field, StringComparison.Ordinal) || trimmed.Length <= field.Length || trimmed[field.Length] != ':')
            {
                return false;
            }

            value = trimmed.Substring(field.Length + 1).Trim();
            return true;
        }

        static bool TryParseFileId(string value, out long fileId)
        {
            fileId = 0;
            var candidate = (value ?? string.Empty).Trim();
            if (candidate.StartsWith("-", StringComparison.Ordinal))
            {
                candidate = candidate.Substring(1).TrimStart();
            }
            if (candidate.Length < 2 || candidate[0] != '{' || candidate[candidate.Length - 1] != '}')
            {
                return false;
            }
            candidate = candidate.Substring(1, candidate.Length - 2).Trim();
            if (!candidate.StartsWith("fileID:", StringComparison.Ordinal))
            {
                return false;
            }

            var start = "fileID:".Length;
            while (start < candidate.Length && char.IsWhiteSpace(candidate[start]))
            {
                start++;
            }

            var end = start;
            if (end < candidate.Length && candidate[end] == '-')
            {
                end++;
            }

            while (end < candidate.Length && char.IsDigit(candidate[end]))
            {
                end++;
            }
            var numberEnd = end;
            while (end < candidate.Length && char.IsWhiteSpace(candidate[end]))
            {
                end++;
            }

            return numberEnd > start && end == candidate.Length
                && long.TryParse(candidate.Substring(start, numberEnd - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out fileId);
        }

        static bool TryGuid(string line, out string guid)
        {
            guid = string.Empty;
            var marker = line.IndexOf("guid:", StringComparison.Ordinal);
            if (marker < 0)
            {
                return false;
            }

            var start = marker + "guid:".Length;
            while (start < line.Length && char.IsWhiteSpace(line[start]))
            {
                start++;
            }

            var end = start;
            while (end < line.Length && !char.IsWhiteSpace(line[end]) && line[end] != ',' && line[end] != '}')
            {
                end++;
            }

            if (end - start != 32)
            {
                return false;
            }

            for (var index = start; index < end; index++)
            {
                if (!IsHex(line[index]))
                {
                    return false;
                }
            }

            guid = line.Substring(start, end - start).ToLowerInvariant();
            return true;
        }

        static bool IsHex(char value)
        {
            return (value >= '0' && value <= '9')
                || (value >= 'a' && value <= 'f')
                || (value >= 'A' && value <= 'F');
        }

        static bool IsEmptyYamlList(string value)
        {
            return value == "[]" || value == "{}";
        }

        static string BoundedName(string value)
        {
            var name = value.Trim();
            if (name.Length >= 2 && ((name[0] == '"' && name[name.Length - 1] == '"') || (name[0] == '\'' && name[name.Length - 1] == '\'')))
            {
                name = name.Substring(1, name.Length - 2);
            }

            return name.Length <= MaxNameChars ? name : name.Substring(0, MaxNameChars);
        }

        static int ClampIssues(int value)
        {
            return Math.Max(0, Math.Min(MaxIssues, value));
        }

        static int ClampScanBytes(int value)
        {
            return Math.Max(1, Math.Min(HardMaxScanBytes, value <= 0 ? DefaultScanBytes : value));
        }

        static string Bounded(string value, int maxChars)
        {
            if (value == null || value.Length <= maxChars)
            {
                return value ?? string.Empty;
            }

            return value.Substring(0, maxChars);
        }

        static bool ContainsReparsePoint(string projectRoot, string assetPath)
        {
            try
            {
                var current = Path.GetFullPath(projectRoot);
                var root = current.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                    ? current
                    : current + Path.DirectorySeparatorChar;
                var fullPath = Path.GetFullPath(Path.Combine(current, assetPath.Replace('/', Path.DirectorySeparatorChar)));
                if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                var parts = assetPath.Split('/');
                for (var index = 0; index < parts.Length; index++)
                {
                    current = Path.Combine(current, parts[index]);
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    {
                        return true;
                    }
                }
                return false;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        static string ObservationPayload(List<string> paths, bool truncated)
        {
            var builder = new StringBuilder(ObservationPayloadHeader.Length + 4 + paths.Count * 64);
            builder.Append(ObservationPayloadHeader).Append('\n').Append(truncated ? '1' : '0');
            for (var index = 0; index < paths.Count; index++)
            {
                builder.Append('\n').Append(paths[index]);
            }
            return builder.ToString();
        }

        static bool TryParseObservationPayload(string payload, out List<string> paths, out bool truncated)
        {
            paths = new List<string>();
            truncated = false;
            if (string.IsNullOrEmpty(payload) || payload.Length > MaxObservationPayloadChars)
            {
                return false;
            }
            var lines = payload.Split('\n');
            if (lines.Length < 2 || lines.Length > MaxEvidence + 2 || lines[0] != ObservationPayloadHeader
                || (lines[1] != "0" && lines[1] != "1"))
            {
                return false;
            }
            truncated = lines[1] == "1";
            for (var index = 2; index < lines.Length; index++)
            {
                var candidate = lines[index];
                try
                {
                    var normalized = PathGuard.RequireUnityAssetPath(candidate);
                    if (!normalized.StartsWith("Assets/", StringComparison.Ordinal)
                        || !normalized.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                        || normalized.Length > MaxPathChars || paths.Contains(normalized))
                    {
                        return false;
                    }
                    paths.Add(normalized);
                }
                catch (Exception)
                {
                    return false;
                }
            }
            return true;
        }

        static bool HasTransformChildLoadPhrase(string message)
        {
            var childMarker = message.IndexOf(TransformChildMarker, StringComparison.Ordinal);
            if (childMarker < 0)
            {
                return false;
            }

            var loadedMarker = message.IndexOf(CantBeLoaded, childMarker, StringComparison.Ordinal);
            if (loadedMarker < 0)
            {
                loadedMarker = message.IndexOf(CantBeLoadedAscii, childMarker, StringComparison.Ordinal);
            }
            return loadedMarker >= childMarker && loadedMarker - childMarker <= 256;
        }

        static bool ExtractPrefabPaths(string message, List<string> paths)
        {
            var cursor = 0;
            var truncated = false;
            while (cursor < message.Length)
            {
                var start = message.IndexOf("Assets/", cursor, StringComparison.Ordinal);
                if (start < 0)
                {
                    return truncated;
                }

                var extension = message.IndexOf(".prefab", start, StringComparison.OrdinalIgnoreCase);
                if (extension < 0)
                {
                    return truncated;
                }

                var candidate = message.Substring(start, extension - start + ".prefab".Length);
                if (candidate.Length > MaxPathChars)
                {
                    cursor = extension + ".prefab".Length;
                    continue;
                }

                try
                {
                    var normalized = PathGuard.RequireUnityAssetPath(candidate);
                    if (normalized.StartsWith("Assets/", StringComparison.Ordinal) && normalized.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) && normalized.Length <= MaxPathChars && !paths.Contains(normalized))
                    {
                        if (paths.Count < MaxEvidence)
                        {
                            paths.Add(normalized);
                        }
                        else
                        {
                            truncated = true;
                        }
                    }
                }
                catch (Exception)
                {
                }

                cursor = extension + ".prefab".Length;
            }
            return truncated;
        }

        sealed class ParserState
        {
            internal readonly List<ParsedRecord> Records = new List<ParsedRecord>();
            internal readonly Dictionary<long, ParsedRecord> ById = new Dictionary<long, ParsedRecord>();
            internal readonly List<PrefabYamlIntegrityEvidence> Evidence = new List<PrefabYamlIntegrityEvidence>();
            internal bool ScanTruncated;
            internal bool SawYamlDirective;
            internal bool SawGameObject;
            internal bool SawTransform;
            internal bool IssuesTruncated;

            internal ParserState(bool scanTruncated)
            {
                ScanTruncated = scanTruncated;
            }

            internal bool StructurallyValid
            {
                get
                {
                    if (!SawYamlDirective || !SawGameObject || !SawTransform)
                    {
                        return false;
                    }
                    for (var index = 0; index < Records.Count; index++)
                    {
                        var record = Records[index];
                        if (!record.IsTransform || record.IsStripped)
                        {
                            continue;
                        }
                        if (!record.HasGameObject || !record.HasFather || !record.HasChildrenField
                            || record.ChildrenMalformed || record.ChildrenListExpectedEntry
                            || !ById.TryGetValue(record.GameObjectId, out var gameObject) || gameObject.ClassId != 1)
                        {
                            return false;
                        }
                        if (record.FatherId != 0
                            && (!ById.TryGetValue(record.FatherId, out var father) || !father.IsTransform))
                        {
                            return false;
                        }
                        for (var childIndex = 0; childIndex < record.Children.Count; childIndex++)
                        {
                            var childId = record.Children[childIndex].FileId;
                            if (ById.TryGetValue(childId, out var child) && child.IsTransform && !child.IsStripped
                                && (!child.HasFather || child.FatherId != record.FileId))
                            {
                                return false;
                            }
                        }
                        if (!HasValidKnownFatherChain(this, record.FileId))
                        {
                            return false;
                        }
                    }
                    return true;
                }
            }
        }

        sealed class ParsedRecord
        {
            internal readonly int ClassId;
            internal readonly long FileId;
            internal readonly int HeaderLine;
            internal readonly bool IsStripped;
            internal readonly List<ChildReference> Children = new List<ChildReference>();
            internal string Name = string.Empty;
            internal bool HasGameObject;
            internal long GameObjectId;
            internal bool HasFather;
            internal long FatherId;
            internal bool HasChildrenField;
            internal bool ChildrenMalformed;
            internal bool ChildrenListExpectedEntry;

            internal ParsedRecord(int classId, long fileId, int headerLine, bool isStripped)
            {
                ClassId = classId;
                FileId = fileId;
                HeaderLine = headerLine;
                IsStripped = isStripped;
            }

            internal bool IsTransform
            {
                get { return ClassId == 4 || ClassId == 224; }
            }
        }

        struct ChildReference
        {
            internal readonly long FileId;
            internal readonly int Line;

            internal ChildReference(long fileId, int line)
            {
                FileId = fileId;
                Line = line;
            }
        }

        sealed class BoundedRead
        {
            internal readonly string Text;
            internal readonly bool ScanTruncated;
            internal readonly string Error;

            internal BoundedRead(string text, bool scanTruncated, string error)
            {
                Text = text;
                ScanTruncated = scanTruncated;
                Error = error;
            }
        }

        sealed class BoundedText
        {
            internal readonly string Text;
            internal readonly bool ScanTruncated;

            internal BoundedText(string text, bool scanTruncated)
            {
                Text = text;
                ScanTruncated = scanTruncated;
            }
        }
    }

    internal sealed class PrefabYamlIntegrityResult
    {
        readonly List<PrefabYamlIntegrityIssue> m_issues;
        readonly List<PrefabYamlIntegrityEvidence> m_evidence;

        internal PrefabYamlIntegrityResult(string assetPath, bool fileExists, bool analysisComplete, bool scanTruncated, bool issuesTruncated, string readError, IEnumerable<PrefabYamlIntegrityIssue> issues, IEnumerable<PrefabYamlIntegrityEvidence> evidence)
        {
            AssetPath = assetPath ?? string.Empty;
            FileExists = fileExists;
            AnalysisComplete = analysisComplete;
            ScanTruncated = scanTruncated;
            IssuesTruncated = issuesTruncated;
            ReadError = readError ?? string.Empty;
            m_issues = new List<PrefabYamlIntegrityIssue>(issues ?? new PrefabYamlIntegrityIssue[0]);
            m_evidence = new List<PrefabYamlIntegrityEvidence>(evidence ?? new PrefabYamlIntegrityEvidence[0]);
            HasIssues = m_issues.Count > 0;
            IssueCount = m_issues.Count;
            ShouldSkipUnityLoad = !FileExists || !AnalysisComplete || HasIssues;
        }

        internal string AssetPath { get; private set; }
        internal string ReadError { get; private set; }
        internal IReadOnlyList<PrefabYamlIntegrityIssue> Issues { get { return m_issues; } }
        internal IReadOnlyList<PrefabYamlIntegrityEvidence> Evidence { get { return m_evidence; } }
        public bool FileExists { get; private set; }
        public bool AnalysisComplete { get; private set; }
        public bool ScanTruncated { get; private set; }
        public bool IssuesTruncated { get; private set; }
        public bool HasIssues { get; private set; }
        public int IssueCount { get; private set; }
        public bool ShouldSkipUnityLoad { get; private set; }

        internal static PrefabYamlIntegrityResult Failure(string assetPath, bool fileExists, bool scanTruncated, string readError)
        {
            return new PrefabYamlIntegrityResult(assetPath, fileExists, false, scanTruncated, false, readError, null, null);
        }

        public string ToJson(bool assetLoadable, string assetLoadError)
        {
            var builder = new StringBuilder(1024);
            builder.Append("{\"schema_version\":1,\"read_only\":true");
            builder.Append(",\"asset_path\":").Append(JsonUtil.String(AssetPath));
            builder.Append(",\"file_exists\":").Append(JsonUtil.Bool(FileExists));
            builder.Append(",\"analysis_complete\":").Append(JsonUtil.Bool(AnalysisComplete));
            builder.Append(",\"read_error\":").Append(JsonUtil.String(ReadError));
            builder.Append(",\"asset_loadable\":").Append(JsonUtil.Bool(assetLoadable));
            builder.Append(",\"asset_load_error\":").Append(JsonUtil.String(PrefabYamlIntegrity.BoundedAssetLoadError(assetLoadable, assetLoadError)));
            builder.Append(",\"scan_truncated\":").Append(JsonUtil.Bool(ScanTruncated));
            builder.Append(",\"issues_truncated\":").Append(JsonUtil.Bool(IssuesTruncated));
            builder.Append(",\"has_issues\":").Append(JsonUtil.Bool(HasIssues));
            builder.Append(",\"issue_count\":").Append(IssueCount.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"issues\":[");
            for (var index = 0; index < m_issues.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var issue = m_issues[index];
                builder.Append("{\"kind\":").Append(JsonUtil.String(issue.Code));
                builder.Append(",\"parent_transform_file_id\":").Append(JsonUtil.String(issue.ParentTransformFileId.ToString(CultureInfo.InvariantCulture)));
                builder.Append(",\"parent_transform_type\":").Append(JsonUtil.String(issue.ParentTransformClassId == 224 ? "RectTransform" : "Transform"));
                builder.Append(",\"parent_path\":").Append(JsonUtil.String(issue.ParentPath));
                builder.Append(",\"stale_child_file_id\":").Append(JsonUtil.String(issue.StaleChildFileId.ToString(CultureInfo.InvariantCulture)));
                builder.Append(",\"child_reference_line\":").Append(issue.ChildReferenceLine.ToString(CultureInfo.InvariantCulture));
                if (issue.ActualClassId != 0)
                {
                    builder.Append(",\"actual_class_id\":").Append(issue.ActualClassId.ToString(CultureInfo.InvariantCulture));
                }
                builder.Append(",\"suspected_cause\":\"stale Transform.m_Children entry after raw YAML or nested PrefabInstance removal\"}");
            }

            builder.Append("],\"suspected_nested_prefab_evidence\":[");
            for (var index = 0; index < m_evidence.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var evidence = m_evidence[index];
                builder.Append("{\"source_guid\":").Append(JsonUtil.String(evidence.Guid));
                builder.Append(",\"prefab_instance_file_id\":").Append(JsonUtil.String(evidence.FileId.ToString(CultureInfo.InvariantCulture)));
                builder.Append(",\"source_line\":").Append(evidence.Line.ToString(CultureInfo.InvariantCulture)).Append('}');
            }

            builder.Append("]}");
            return builder.ToString();
        }
    }

    internal sealed class PrefabYamlIntegrityIssue
    {
        internal readonly string Code;
        internal readonly long ParentTransformFileId;
        internal readonly int ParentTransformClassId;
        internal readonly string ParentPath;
        internal readonly long StaleChildFileId;
        internal readonly int ChildReferenceLine;
        internal readonly int ActualClassId;

        internal PrefabYamlIntegrityIssue(string code, long parentTransformFileId, int parentTransformClassId, string parentPath, long staleChildFileId, int childReferenceLine, int actualClassId)
        {
            Code = code;
            ParentTransformFileId = parentTransformFileId;
            ParentTransformClassId = parentTransformClassId;
            ParentPath = parentPath ?? string.Empty;
            StaleChildFileId = staleChildFileId;
            ChildReferenceLine = childReferenceLine;
            ActualClassId = actualClassId;
        }
    }

    internal sealed class PrefabYamlIntegrityEvidence
    {
        internal readonly string Guid;
        internal readonly long FileId;
        internal readonly int Line;

        internal PrefabYamlIntegrityEvidence(string guid, long fileId, int line)
        {
            Guid = guid ?? string.Empty;
            FileId = fileId;
            Line = line;
        }
    }
}
