using System;
using System.Text;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class JsonUtil
    {
        public static string String(string value)
        {
            if (value == null)
            {
                return "\"\"";
            }

            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');
            foreach (var character in value)
            {
                switch (character)
                {
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (char.IsControl(character))
                        {
                            builder.Append("\\u").Append(((int)character).ToString("x4"));
                        }
                        else
                        {
                            builder.Append(character);
                        }
                        break;
                }
            }

            builder.Append('"');
            return builder.ToString();
        }

        public static string Bool(bool value)
        {
            return value ? "true" : "false";
        }

        public static string ExtractString(string json, string property)
        {
            var colon = FindPropertyColon(json, property);
            if (colon < 0)
            {
                return string.Empty;
            }

            var quote = IndexOfNextNonWhitespace(json, colon + 1);
            if (quote < 0 || json[quote] != '"')
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            var escaped = false;
            for (var index = quote + 1; index < json.Length; index++)
            {
                var character = json[index];
                if (escaped)
                {
                    if (character == 'u' && index + 4 < json.Length && TryParseHexQuad(json, index + 1, out var unicode))
                    {
                        builder.Append(unicode);
                        index += 4;
                    }
                    else
                    {
                        builder.Append(Unescape(character));
                    }
                    escaped = false;
                    continue;
                }

                if (character == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (character == '"')
                {
                    return builder.ToString();
                }

                builder.Append(character);
            }

            return string.Empty;
        }

        public static int ExtractInt(string json, string property, int defaultValue)
        {
            var colon = FindPropertyColon(json, property);
            if (colon < 0)
            {
                return defaultValue;
            }

            var index = IndexOfNextNonWhitespace(json, colon + 1);
            if (index < 0)
            {
                return defaultValue;
            }

            var start = index;
            if (json[index] == '-')
            {
                index++;
            }

            while (index < json.Length && char.IsDigit(json[index]))
            {
                index++;
            }

            if (index == start || !int.TryParse(json.Substring(start, index - start), out var value))
            {
                return defaultValue;
            }

            return value;
        }

        public static bool HasProperty(string json, string property)
        {
            return FindPropertyColon(json, property) >= 0;
        }

        public static bool ExtractBool(string json, string property, bool defaultValue)
        {
            var colon = FindPropertyColon(json, property);
            if (colon < 0)
            {
                return defaultValue;
            }

            var index = IndexOfNextNonWhitespace(json, colon + 1);
            if (index < 0)
            {
                return defaultValue;
            }

            if (json.IndexOf("true", index, StringComparison.OrdinalIgnoreCase) == index)
            {
                return true;
            }

            if (json.IndexOf("false", index, StringComparison.OrdinalIgnoreCase) == index)
            {
                return false;
            }

            return defaultValue;
        }

        public static string ExtractObject(string json, string property)
        {
            var colon = FindPropertyColon(json, property);
            if (colon < 0)
            {
                return "{}";
            }

            var start = IndexOfNextNonWhitespace(json, colon + 1);
            if (start < 0 || json[start] != '{')
            {
                return "{}";
            }

            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var index = start; index < json.Length; index++)
            {
                var character = json[index];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (character == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (character == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (inString)
                {
                    continue;
                }

                if (character == '{')
                {
                    depth++;
                }
                else if (character == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return json.Substring(start, index - start + 1);
                    }
                }
            }

            return "{}";
        }

        static int FindPropertyColon(string json, string property)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(property))
            {
                return -1;
            }

            var depth = 0;
            for (var index = 0; index < json.Length; index++)
            {
                var character = json[index];
                if (character == '{' || character == '[')
                {
                    depth++;
                    continue;
                }
                if (character == '}' || character == ']')
                {
                    depth--;
                    continue;
                }
                if (character != '"')
                {
                    continue;
                }
                var rootPropertyCandidate = depth == 1;

                var builder = new StringBuilder();
                var escaped = false;
                var end = index + 1;
                for (; end < json.Length; end++)
                {
                    var current = json[end];
                    if (escaped)
                    {
                        if (current == 'u' && end + 4 < json.Length && TryParseHexQuad(json, end + 1, out var unicode))
                        {
                            builder.Append(unicode);
                            end += 4;
                        }
                        else
                        {
                            builder.Append(Unescape(current));
                        }
                        escaped = false;
                    }
                    else if (current == '\\')
                    {
                        escaped = true;
                    }
                    else if (current == '"')
                    {
                        break;
                    }
                    else
                    {
                        builder.Append(current);
                    }
                }
                if (end >= json.Length)
                {
                    return -1;
                }

                var colon = IndexOfNextNonWhitespace(json, end + 1);
                if (rootPropertyCandidate && colon >= 0 && json[colon] == ':' && string.Equals(builder.ToString(), property, StringComparison.Ordinal))
                {
                    return colon;
                }
                index = end;
            }
            return -1;
        }

        static int IndexOfNextNonWhitespace(string value, int start)
        {
            for (var index = start; index < value.Length; index++)
            {
                if (!char.IsWhiteSpace(value[index]))
                {
                    return index;
                }
            }

            return -1;
        }

        static char Unescape(char character)
        {
            switch (character)
            {
                case 'n':
                    return '\n';
                case 'r':
                    return '\r';
                case 't':
                    return '\t';
                case 'b':
                    return '\b';
                case 'f':
                    return '\f';
                default:
                    return character;
            }
        }

        static bool TryParseHexQuad(string value, int start, out char character)
        {
            character = '\0';
            if (string.IsNullOrEmpty(value) || start < 0 || start + 4 > value.Length)
            {
                return false;
            }
            var code = 0;
            for (var index = start; index < start + 4; index++)
            {
                var digit = value[index];
                var hex = digit >= '0' && digit <= '9' ? digit - '0'
                    : digit >= 'a' && digit <= 'f' ? digit - 'a' + 10
                    : digit >= 'A' && digit <= 'F' ? digit - 'A' + 10
                    : -1;
                if (hex < 0)
                {
                    return false;
                }
                code = code * 16 + hex;
            }
            character = (char)code;
            return true;
        }
    }
}
