// 把分析结果归到 7 档严重程度：critical / error / warning / notice / info /
// healthy / other（other = 无法判定）。分类口径与 /api/ai-history/stats 完全一致。
// 存成 ai_history 哈希里的 status 字段 + 独立的 ai_history:status 小哈希（内部字段，
// 不对外服务），避免统计端点每次页面加载都读 + 解析整份 analysis JSON。

using System.Text.Json;
using System.Text.Json.Nodes;

namespace LogAI.Core.Ai;

public static class AiStatusClassifier
{
    /// <summary>7 档严重程度，从严重到轻微；末尾 other 表示无法判定。</summary>
    public static readonly string[] Statuses =
        ["critical", "error", "warning", "notice", "info", "healthy", "other"];

    public static string Classify(string type, JsonNode? analysis)
    {
        if (analysis is not JsonObject obj) return "other";

        string status;
        if (string.Equals(type, "single", StringComparison.Ordinal))
        {
            status = obj["is_critical"] is JsonValue ic && ic.TryGetValue<bool>(out bool critical) && critical
                ? "critical"
                : Text(obj, "category");
        }
        else
        {
            status = Text(obj, "overall_status");
            if (status.Length == 0) status = Text(obj, "category");
        }

        // 7 档各自成桶。同义写法一并归一，避免模型偶尔换个词就掉进 other。
        return status.ToLowerInvariant() switch
        {
            "critical" or "fatal" or "emergency" or "alert" => "critical",
            "error" or "err" => "error",
            "warning" or "warn" => "warning",
            "notice" => "notice",
            "info" or "informational" => "info",
            "healthy" or "ok" or "normal" => "healthy",
            _ => "other",
        };
    }

    /// <summary>
    /// 字符串重载：先解析再分类。刻意不依赖 JsonNode 对 string 的隐式转换——
    /// 那会把整串 JSON 变成单个 JsonValue（而非 JsonObject），从而把一切误判成
    /// "other"（回填时踩过这个坑）。
    /// </summary>
    public static string Classify(string type, string? analysisRaw)
    {
        if (string.IsNullOrEmpty(analysisRaw)) return "other";
        try
        {
            return Classify(type, JsonNode.Parse(analysisRaw));
        }
        catch (JsonException)
        {
            return "other";
        }
    }

    private static string Text(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.TryGetValue<string>(out string? text) ? text : "";
}
