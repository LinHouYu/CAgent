using System.Text.Json;

namespace CAgent.Core.Protocol;

public sealed class ToolCallRequest
{
    public required string ToolName { get; init; }
    public required JsonElement Arguments { get; init; }
    public required string RawJson { get; init; }

    public string GetString(string propertyName, string defaultValue = "")
    {
        if (Arguments.ValueKind == JsonValueKind.Object &&
            Arguments.TryGetProperty(propertyName, out var prop))
        {
            return prop.ValueKind switch
            {
                JsonValueKind.String => prop.GetString() ?? defaultValue,
                JsonValueKind.Null or JsonValueKind.Undefined => defaultValue,
                _ => prop.ToString()
            };
        }
        return defaultValue;
    }

    public IReadOnlyList<string> GetStringArray(string propertyName)
    {
        if (Arguments.ValueKind != JsonValueKind.Object ||
            !Arguments.TryGetProperty(propertyName, out var prop))
        {
            return [];
        }

        if (prop.ValueKind == JsonValueKind.String)
        {
            var single = prop.GetString();
            return string.IsNullOrWhiteSpace(single) ? [] : [single];
        }

        if (prop.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in prop.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var val = item.GetString();
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        list.Add(val);
                    }
                }
            }
            return list;
        }

        return [];
    }

    public int GetInt(string propertyName, int defaultValue)
    {
        if (Arguments.ValueKind == JsonValueKind.Object &&
            Arguments.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var val))
            {
                return val;
            }
            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var parsed))
            {
                return parsed;
            }
        }
        return defaultValue;
    }

    public bool GetBool(string propertyName, bool defaultValue = false)
    {
        if (Arguments.ValueKind == JsonValueKind.Object &&
            Arguments.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return prop.GetBoolean();
            }
            if (prop.ValueKind == JsonValueKind.String && bool.TryParse(prop.GetString(), out var parsed))
            {
                return parsed;
            }
        }
        return defaultValue;
    }

    public string GetHumanSummary()
    {
        return ToolName.ToLowerInvariant() switch
        {
            "run_shell" => $"[run_shell] {TruncateInline(GetString("command"), 90)}",
            "read_file" => $"[read_file] {GetString("path")}",
            "write_file" => GetBool("append")
                ? $"[write_file · 追加] {GetString("path")}"
                : $"[write_file · 写入] {GetString("path")}",
            "edit_file" => $"[edit_file · 局部修改] {GetString("path")}",
            "list_dir" => $"[list_dir] {GetString("path", ".")}",
            "screenshot" => "[screenshot] 截取当前桌面屏幕与鼠标位置",
            "attach_file" or "upload_file" => $"[attach_file] 上传多模态文件 ({FormatAttachTargets()})",
            "ui_automation" => $"[ui_automation] {GetString("action", "操作窗口/键鼠")}",
            _ => $"[{ToolName}] {TruncateInline(RawJson, 80)}"
        };
    }

    private string FormatAttachTargets()
    {
        var arr = GetStringArray("paths");
        if (arr.Count > 0)
        {
            return string.Join(", ", arr.Take(3));
        }
        return GetString("path", "文件");
    }

    private static string TruncateInline(string value, int maxLen)
    {
        var singleLine = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return singleLine.Length <= maxLen ? singleLine : $"{singleLine[..maxLen]}...";
    }
}

public sealed record TruncatedOutput(
    string Content,
    bool WasTruncated,
    string? FullOutputLogPath);
