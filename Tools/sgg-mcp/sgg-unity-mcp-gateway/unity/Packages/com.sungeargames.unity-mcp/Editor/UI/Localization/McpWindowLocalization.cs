using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using SGG.UnityMCP.Editor.Setup;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace SGG.UnityMCP.Editor.UI.Localization
{
    internal static class McpWindowLocalization
    {
        public const string DefaultLanguage = "en";
        public const string LanguagePrefsKey = "SGG.UnityMCP.McpWindow.Language";

        const string LocalizationFolder = "Editor/UI/Localization";
        const string FilePrefix = "mcp-window.";
        const string FileExtension = ".xlf";
        const int MaxCachedTextEntriesPerLanguage = 4096;

        static readonly Dictionary<string, LocalizationFile> s_files = new Dictionary<string, LocalizationFile>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, Dictionary<string, string>> s_textCacheByLanguage = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        static List<LanguageOption> s_availableLanguages;

        public sealed class LanguageOption
        {
            public LanguageOption(string code, string displayName)
            {
                Code = code;
                DisplayName = displayName;
            }

            public string Code { get; private set; }
            public string DisplayName { get; private set; }
        }

        sealed class LocalizationFile
        {
            public readonly Dictionary<string, string> SourceToTarget = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly Dictionary<string, string> IdToTarget = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        public static string CurrentLanguage
        {
            get { return NormalizeLanguage(EditorPrefs.GetString(LanguagePrefsKey, DefaultLanguage)); }
            set { EditorPrefs.SetString(LanguagePrefsKey, NormalizeLanguage(value)); }
        }

        public static List<LanguageOption> AvailableLanguages()
        {
            if (s_availableLanguages != null)
            {
                return new List<LanguageOption>(s_availableLanguages);
            }

            var languages = new List<LanguageOption>();
            var folder = LocalizationDirectoryPath();
            if (Directory.Exists(folder))
            {
                var files = Directory.GetFiles(folder, FilePrefix + "*" + FileExtension, SearchOption.TopDirectoryOnly);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < files.Length; index++)
                {
                    var code = LanguageCodeFromFile(files[index]);
                    if (string.IsNullOrEmpty(code) || ContainsLanguage(languages, code))
                    {
                        continue;
                    }

                    languages.Add(new LanguageOption(code, LanguageDisplayName(code)));
                }
            }

            if (!ContainsLanguage(languages, DefaultLanguage))
            {
                languages.Insert(0, new LanguageOption(DefaultLanguage, "English"));
            }

            languages.Sort((left, right) =>
            {
                if (string.Equals(left.Code, DefaultLanguage, StringComparison.OrdinalIgnoreCase)) return -1;
                if (string.Equals(right.Code, DefaultLanguage, StringComparison.OrdinalIgnoreCase)) return 1;
                return string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase);
            });

            s_availableLanguages = languages;
            return new List<LanguageOption>(s_availableLanguages);
        }

        public static string CurrentLanguageDisplayName()
        {
            return LanguageDisplayName(CurrentLanguage);
        }

        public static string LanguageCodeForDisplayName(string displayName)
        {
            var languages = AvailableLanguages();
            for (var index = 0; index < languages.Count; index++)
            {
                if (string.Equals(languages[index].DisplayName, displayName, StringComparison.Ordinal))
                {
                    return languages[index].Code;
                }
            }

            return CurrentLanguage;
        }

        public static int LanguageIndex(string code)
        {
            var languages = AvailableLanguages();
            for (var index = 0; index < languages.Count; index++)
            {
                if (string.Equals(languages[index].Code, code, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            return 0;
        }

        public static List<string> LanguageDisplayNames()
        {
            var names = new List<string>();
            var languages = AvailableLanguages();
            for (var index = 0; index < languages.Count; index++)
            {
                names.Add(languages[index].DisplayName);
            }

            return names;
        }

        public static string Text(string source)
        {
            if (string.IsNullOrEmpty(source))
            {
                return source;
            }

            var language = CurrentLanguage;
            var cache = TextCacheForLanguage(language);
            string cached;
            if (cache.TryGetValue(source, out cached))
            {
                return cached;
            }

            var exact = ExactText(source);
            string result;
            if (!string.IsNullOrEmpty(exact))
            {
                result = exact;
            }
            else
            {
                result = PatternText(source);
            }

            if (cache.Count >= MaxCachedTextEntriesPerLanguage)
            {
                cache.Clear();
            }

            cache[source] = result;
            return result;
        }

        public static string Format(string source, params object[] args)
        {
            var exact = ExactText(source);
            return string.Format(string.IsNullOrEmpty(exact) ? source : exact, args);
        }

        static string ExactText(string source)
        {
            var file = Load(CurrentLanguage);
            string value;
            return file.SourceToTarget.TryGetValue(source, out value) && !string.IsNullOrEmpty(value)
                ? value
                : string.Empty;
        }

        static Dictionary<string, string> TextCacheForLanguage(string language)
        {
            Dictionary<string, string> cache;
            var code = string.IsNullOrEmpty(language) ? DefaultLanguage : language;
            if (!s_textCacheByLanguage.TryGetValue(code, out cache))
            {
                cache = new Dictionary<string, string>(StringComparer.Ordinal);
                s_textCacheByLanguage[code] = cache;
            }

            return cache;
        }

        public static void ApplyTo(VisualElement root)
        {
            if (root == null)
            {
                return;
            }

            ApplyElement(root);
            foreach (var child in root.Children())
            {
                ApplyTo(child);
            }
        }

        static void ApplyElement(VisualElement element)
        {
            if (!string.IsNullOrEmpty(element.tooltip))
            {
                element.tooltip = Text(element.tooltip);
            }

            var label = element as Label;
            if (label != null && !string.IsNullOrEmpty(label.text))
            {
                label.text = Text(label.text);
                return;
            }

            var button = element as Button;
            if (button != null && !string.IsNullOrEmpty(button.text))
            {
                button.text = Text(button.text);
            }

            var toolbarToggle = element as ToolbarToggle;
            if (toolbarToggle != null && !string.IsNullOrEmpty(toolbarToggle.text))
            {
                toolbarToggle.text = Text(toolbarToggle.text);
            }

            var textField = element as TextField;
            if (textField != null && !string.IsNullOrEmpty(textField.label))
            {
                textField.label = Text(textField.label);
            }

            var popupField = element as PopupField<string>;
            if (popupField != null && !string.IsNullOrEmpty(popupField.label))
            {
                popupField.label = Text(popupField.label);
            }

            var integerField = element as IntegerField;
            if (integerField != null && !string.IsNullOrEmpty(integerField.label))
            {
                integerField.label = Text(integerField.label);
            }
        }

        static string NormalizeLanguage(string language)
        {
            if (string.IsNullOrEmpty(language))
            {
                return DefaultLanguage;
            }

            var normalized = language.Trim().ToLowerInvariant();
            var languages = AvailableLanguages();
            for (var index = 0; index < languages.Count; index++)
            {
                if (string.Equals(languages[index].Code, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return languages[index].Code;
                }
            }

            return DefaultLanguage;
        }

        static string PatternText(string source)
        {
            var prefixed = PrefixedText(source, "Ready - ")
                ?? PrefixedText(source, "Needs attention - ")
                ?? PrefixedText(source, "Next action - ")
                ?? PrefixedText(source, "Blocked - ")
                ?? PrefixedText(source, "Optional - ")
                ?? PrefixedText(source, "Failed - ");
            if (prefixed != null)
            {
                return prefixed;
            }

            Match match;
            if (TryMatch(source, "^(.+) Running version: (.+)\\.$", out match))
            {
                return Format("{0} Running version: {1}.", Text(match.Groups[1].Value), match.Groups[2].Value);
            }

            if (TryMatch(source, "^(.+) Installed version: (.+)\\.$", out match))
            {
                return Format("{0} Installed version: {1}.", Text(match.Groups[1].Value), match.Groups[2].Value);
            }

            if (TryMatch(source, "^Installed: (.+)\\.$", out match))
            {
                return Format("Installed: {0}.", match.Groups[1].Value);
            }

            if (TryMatch(source, "^(\\d+) of (\\d+) commands; hash (.+)$", out match))
            {
                return Format("{0} of {1} commands; hash {2}", match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value);
            }

            if (TryMatch(source, "^Catalog policy summary: Unity Bridge catalog is not loaded yet \\(0 Unity Bridge \\+ (\\d+) Gateway virtual\\)\\. Click H\\* Refresh after bridge startup completes\\.$", out match))
            {
                return Format("Catalog policy summary: Unity Bridge catalog is not loaded yet (0 Unity Bridge + {0} Gateway virtual). Click H* Refresh after bridge startup completes.", match.Groups[1].Value);
            }

            if (TryMatch(source, "^Catalog policy summary: (\\d+\\+\\d+) commands \\((\\d+) Unity Bridge \\+ (\\d+) Gateway virtual\\), assigned permissions: (<color=#[0-9a-fA-F]+>\\d+/\\d+</color>), Unity Bridge policy: (<color=#[0-9a-fA-F]+>\\d+</color>) Allow/default allow, (<color=#[0-9a-fA-F]+>\\d+</color>) Ask/default ask, (<color=#[0-9a-fA-F]+>\\d+</color>) Deny/default deny, (\\d+) unknown, feature-hidden: (<color=#[0-9a-fA-F]+>\\d+</color>)\\. Refresh after Gateway/runtime update\\.$", out match))
            {
                return Format("Catalog policy summary: {0} commands ({1} Unity Bridge + {2} Gateway virtual), assigned permissions: {3}, Unity Bridge policy: {4} Allow/default allow, {5} Ask/default ask, {6} Deny/default deny, {7} unknown, feature-hidden: {8}. Refresh after Gateway/runtime update.",
                    match.Groups[1].Value,
                    match.Groups[2].Value,
                    match.Groups[3].Value,
                    match.Groups[4].Value,
                    match.Groups[5].Value,
                    match.Groups[6].Value,
                    match.Groups[7].Value,
                    match.Groups[8].Value,
                    match.Groups[9].Value);
            }

            if (TryMatch(source, "^installed=(.+), loaded=(.+), path=(.+)$", out match))
            {
                return Format("installed={0}, loaded={1}, path={2}", match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value);
            }

            if (TryMatch(source, "^installed=(.+), running=(.+)$", out match))
            {
                return Format("installed={0}, running={1}", match.Groups[1].Value, Text(match.Groups[2].Value));
            }

            if (TryMatch(source, "^Start Gateway (.+)$", out match))
            {
                return Format("Start Gateway {0}", match.Groups[1].Value);
            }

            if (TryMatch(source, "^Restart Gateway (.+)$", out match))
            {
                return Format("Restart Gateway {0}", match.Groups[1].Value);
            }

            if (TryMatch(source, "^Install Runtime (.+)$", out match))
            {
                return Format("Install Runtime {0}", match.Groups[1].Value);
            }

            if (TryMatch(source, "^Copy (.+)$", out match))
            {
                return Format("Copy {0}", Text(match.Groups[1].Value));
            }

            if (TryMatch(source, "^copy OpenCode/Cursor/Assistant config with placeholder token and MCP URL (.+)\\.$", out match))
            {
                return Format("copy OpenCode/Cursor/Assistant config with placeholder token and MCP URL {0}.", match.Groups[1].Value);
            }

            if (TryMatch(source, "^Optional baseline: (.+)$", out match))
            {
                return Format("Optional baseline: {0}", Text(match.Groups[1].Value));
            }

            if (TryMatch(source, "^(.+) \\(local runtime/debug; not committed\\)$", out match))
            {
                return Format("{0} (local runtime/debug; not committed)", match.Groups[1].Value);
            }

            if (TryMatch(source, "^(.+) \\(only with explicit versioned=true and policy approval\\)$", out match))
            {
                return Format("{0} (only with explicit versioned=true and policy approval)", match.Groups[1].Value);
            }

            if (TryMatch(source, "^Present, (.+) bytes at (.+)$", out match))
            {
                return Format("Present, {0} bytes at {1}", match.Groups[1].Value, match.Groups[2].Value);
            }

            if (TryMatch(source, "^Missing at (.+)$", out match))
            {
                return Format("Missing at {0}", match.Groups[1].Value);
            }

            if (TryMatch(source, "^(\\d+) enabled, (\\d+) disabled; diagnostics flag (present|missing), plans flag (present|missing), skills flag (present|missing), checkpoints flag (present|missing), artifacts flag (present|missing), policy flag (present|missing)$", out match))
            {
                return Format("{0} enabled, {1} disabled; diagnostics flag {2}, plans flag {3}, skills flag {4}, checkpoints flag {5}, artifacts flag {6}, policy flag {7}",
                    match.Groups[1].Value,
                    match.Groups[2].Value,
                    Text(match.Groups[3].Value),
                    Text(match.Groups[4].Value),
                    Text(match.Groups[5].Value),
                    Text(match.Groups[6].Value),
                    Text(match.Groups[7].Value),
                    Text(match.Groups[8].Value));
            }

            if (TryMatch(source, "^(\\d+) valid, (\\d+) invalid; validation is read-only and never executes SKILL.md\\.$", out match))
            {
                return Format("{0} valid, {1} invalid; validation is read-only and never executes SKILL.md.", match.Groups[1].Value, match.Groups[2].Value);
            }

            if (TryMatch(source, "^Installed: (.+) from (.+)\\. Optional for SGGMcp local parity\\.$", out match))
            {
                return Format("Installed: {0} from {1}. Optional for SGGMcp local parity.", match.Groups[1].Value, match.Groups[2].Value);
            }

            if (TryMatch(source, "^Declared in Packages/manifest.json: (.+)\\. Unity may still be resolving/importing it\\.$", out match))
            {
                return Format("Declared in Packages/manifest.json: {0}. Unity may still be resolving/importing it.", match.Groups[1].Value);
            }

            if (TryMatch(source, "^Declared directly in project manifest: (.+)\\. Unity may still be resolving/importing it\\.$", out match))
            {
                return Format("Declared directly in project manifest: {0}. Unity may still be resolving/importing it.", match.Groups[1].Value);
            }

            if (TryMatch(source, "^Required dependency not installed\\. Bridge discovery and path-bound MCP commands are blocked until (.+) is installed\\.$", out match))
            {
                return Format("Required dependency not installed. Bridge discovery and path-bound MCP commands are blocked until {0} is installed.", match.Groups[1].Value);
            }

            if (TryMatch(source, "^Configured candidate found: (.+)\\. Diagnostic-only; use unity.relay.status from Gateway\\.$", out match))
            {
                return Format("Configured candidate found: {0}. Diagnostic-only; use unity.relay.status from Gateway.", match.Groups[1].Value);
            }

            if (TryMatch(source, "^Configured candidate missing: (.+)\\. Diagnostic-only; use unity.relay.status from Gateway\\.$", out match))
            {
                return Format("Configured candidate missing: {0}. Diagnostic-only; use unity.relay.status from Gateway.", match.Groups[1].Value);
            }

            if (TryMatch(source, "^Candidate root found: (.+)\\. Diagnostic-only; local SGGMcp commands do not depend on it\\.$", out match))
            {
                return Format("Candidate root found: {0}. Diagnostic-only; local SGGMcp commands do not depend on it.", match.Groups[1].Value);
            }

            if (TryMatch(source, "^Not reviewed yet; metadata not found at (.+); unknown skills are not auto-enabled\\.$", out match))
            {
                return Format("Not reviewed yet; metadata not found at {0}; unknown skills are not auto-enabled.", match.Groups[1].Value);
            }

            if (TryMatch(source, "^Review metadata present at (.+) \\(read-only; no write command is implemented\\)\\.$", out match))
            {
                return Format("Review metadata present at {0} (read-only; no write command is implemented).", match.Groups[1].Value);
            }

            if (TryMatch(source, "^(.+) \\(\\.mcp/skills/<skill>/SKILL.md agent workflows; missing folder is optional\\)$", out match))
            {
                return Format("{0} (.mcp/skills/<skill>/SKILL.md agent workflows; missing folder is optional)", match.Groups[1].Value);
            }

            if (TryMatch(source, "^(.+) \\(generic examples only\\)$", out match))
            {
                return Format("{0} (generic examples only)", match.Groups[1].Value);
            }

            if (TryMatch(source, "^(.+) \\(project knowledge/resources, not skill workflows\\)$", out match))
            {
                return Format("{0} (project knowledge/resources, not skill workflows)", match.Groups[1].Value);
            }

            return source;
        }

        static string PrefixedText(string source, string prefix)
        {
            if (!source.StartsWith(prefix, StringComparison.Ordinal))
            {
                return null;
            }

            return Format(prefix + "{0}", Text(source.Substring(prefix.Length)));
        }

        static bool TryMatch(string source, string pattern, out Match match)
        {
            match = Regex.Match(source, pattern, RegexOptions.Singleline);
            return match.Success;
        }

        static string LanguageDisplayName(string code)
        {
            var file = Load(code);
            string displayName;
            if (file.IdToTarget.TryGetValue("language.name", out displayName) && !string.IsNullOrEmpty(displayName))
            {
                return displayName;
            }

            if (string.Equals(code, "ru", StringComparison.OrdinalIgnoreCase))
            {
                return "Русский";
            }

            return "English";
        }

        static bool ContainsLanguage(List<LanguageOption> languages, string code)
        {
            for (var index = 0; index < languages.Count; index++)
            {
                if (string.Equals(languages[index].Code, code, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        static LocalizationFile Load(string language)
        {
            var code = string.IsNullOrEmpty(language) ? DefaultLanguage : language;
            LocalizationFile cached;
            if (s_files.TryGetValue(code, out cached))
            {
                return cached;
            }

            var file = new LocalizationFile();
            var path = LocalizationFilePath(code);
            if (File.Exists(path))
            {
                ParseXliff(File.ReadAllText(path), file);
            }

            s_files[code] = file;
            return file;
        }

        static void ParseXliff(string source, LocalizationFile file)
        {
            foreach (Match match in Regex.Matches(source, "<trans-unit\\s+[^>]*id=\"([^\"]+)\"[^>]*>.*?<source[^>]*>(.*?)</source>.*?<target[^>]*>(.*?)</target>.*?</trans-unit>", RegexOptions.Singleline))
            {
                var id = XmlDecode(match.Groups[1].Value);
                var sourceText = XmlDecode(match.Groups[2].Value);
                var targetText = XmlDecode(match.Groups[3].Value);
                if (string.IsNullOrEmpty(targetText))
                {
                    targetText = sourceText;
                }

                if (!string.Equals(id, "language.name", StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(sourceText)
                    && !file.SourceToTarget.ContainsKey(sourceText))
                {
                    file.SourceToTarget.Add(sourceText, targetText);
                }

                if (!string.IsNullOrEmpty(id) && !file.IdToTarget.ContainsKey(id))
                {
                    file.IdToTarget.Add(id, targetText);
                }
            }
        }

        static string XmlDecode(string value)
        {
            return WebUtility.HtmlDecode(value).Replace("&#10;", "\n").Replace("&#13;", "\r");
        }

        static string LocalizationDirectoryPath()
        {
            var resolvedPath = UnityMcpPackagePaths.PackageResolvedPath;
            if (!string.IsNullOrEmpty(resolvedPath))
            {
                return Path.Combine(resolvedPath, LocalizationFolder.Replace('/', Path.DirectorySeparatorChar));
            }

            return Path.Combine(UnityMcpPackagePaths.PackageAssetPath, LocalizationFolder.Replace('/', Path.DirectorySeparatorChar));
        }

        static string LocalizationFilePath(string language)
        {
            return Path.Combine(LocalizationDirectoryPath(), FilePrefix + language + FileExtension);
        }

        static string LanguageCodeFromFile(string filePath)
        {
            var name = Path.GetFileName(filePath);
            if (string.IsNullOrEmpty(name) || !name.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return name.Substring(FilePrefix.Length, name.Length - FilePrefix.Length - FileExtension.Length).ToLowerInvariant();
        }
    }
}
