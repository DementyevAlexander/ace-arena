using System;

namespace SGG.UnityMCP.Editor.Commands
{
    public sealed class CommandExecutionResult
    {
        public string Status { get; private set; } = "ok";
        public string DataJson { get; private set; } = "{}";
        public string ErrorCode { get; private set; } = string.Empty;
        public string ErrorMessage { get; private set; } = string.Empty;
        public string ErrorDetailsJson { get; private set; } = string.Empty;
        public int DurationMs { get; set; }

        public static CommandExecutionResult Ok(string dataJson)
        {
            return new CommandExecutionResult
            {
                Status = "ok",
                DataJson = string.IsNullOrEmpty(dataJson) ? "{}" : dataJson,
            };
        }

        public static CommandExecutionResult Error(string code, string message, string detailsJson = "")
        {
            return new CommandExecutionResult
            {
                Status = "error",
                ErrorCode = string.IsNullOrEmpty(code) ? "unity_exception" : code,
                ErrorMessage = string.IsNullOrEmpty(message) ? "Unity command failed" : message,
                ErrorDetailsJson = string.IsNullOrEmpty(detailsJson) ? string.Empty : detailsJson,
            };
        }

        public static CommandExecutionResult ValidationError(string code, string message, string field, string issueCode)
        {
            var details = "{\"runtime_validation\":{\"contract\":\"sgg.unity_mcp.runtime_validation.v1\",\"issues\":[{\"path\":["
                + JsonUtil.String(field ?? string.Empty)
                + "],\"issue_code\":" + JsonUtil.String(issueCode ?? string.Empty) + "}]}}";
            return Error(code, message, details);
        }

        public static CommandExecutionResult ValidationError(string code, string message, string field, int index, string issueCode)
        {
            var details = "{\"runtime_validation\":{\"contract\":\"sgg.unity_mcp.runtime_validation.v1\",\"issues\":[{\"path\":["
                + JsonUtil.String(field ?? string.Empty) + "," + Math.Max(0, index)
                + "],\"issue_code\":" + JsonUtil.String(issueCode ?? string.Empty) + "}]}}";
            return Error(code, message, details);
        }

        public string ToJson(string requestId)
        {
            var json = "{\"schema_version\":1";
            if (!string.IsNullOrEmpty(requestId))
            {
                json += ",\"request_id\":" + JsonUtil.String(requestId);
            }

            json += ",\"status\":" + JsonUtil.String(Status);
            if (Status == "ok")
            {
                json += ",\"data\":" + DataJson;
            }
            else
            {
                json += ",\"error\":{\"code\":" + JsonUtil.String(ErrorCode)
                    + ",\"message\":" + JsonUtil.String(ErrorMessage);
                if (!string.IsNullOrEmpty(ErrorDetailsJson))
                {
                    json += ",\"details\":" + ErrorDetailsJson;
                }
                json += "}";
            }

            json += ",\"diagnostics\":[]";
            json += ",\"duration_ms\":" + Math.Max(0, DurationMs);
            json += "}";
            return json;
        }
    }
}
