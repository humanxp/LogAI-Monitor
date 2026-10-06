// 把分析结果归到 4 档：critical / warning / healthy / other。分类口径与
// /api/ai-history/stats 完全一致。存成 ai_history 哈希里的 status 字段 + 独立的
// ai_history:status 小哈希（内部字段，不对外服务），避免统计端点每次页面加载都
// 读 + 解析整份 analysis JSON。
//
// critical 刻意收得很紧：只有模型明确判成 critical（真故障/入侵/服务不可用）才算。
// error / notice 归到 warning——模型（3B 小模型）习惯把"重复的良性报错"也标成
// error/critical，若 error 也进 critical，统计里 critical 会常年占一半、失去意义。

using System.Text.Json;
using System.Text.Json.Nodes;

namespace LogAI.Core.Ai;

public static class AiStatusClassifier
{
    /// <summary>4 档，从严重到轻微；末尾 other 表示无法判定。</summary>
    public static readonly string[] Statuses = ["critical", "warning", "healthy", "other"];

    /// <summary>
    /// 归类。<paramref name="allowCritical"/> = 这批日志里确实存在 emergency/alert/critical
    /// 级别的原始日志（由调用方查级别后传入）。传 false 时即便模型说 critical 也降级为
    /// warning——3B 模型习惯把一长串重复的 error/info 消息说成 critical，光靠提示词压不住。
    /// </summary>
    public static string Classify(string type, JsonNode? analysis, bool allowCritical = true)
    {
        // 防御：JsonNode 对 string 有隐式转换，误把 JSON 字符串传进来时这里拿到的是
        // JsonValue（而非 JsonObject），下面会全部落进 other。发现是字符串就先解析。
        if (analysis is JsonValue jv && jv.TryGetValue<string>(out string? raw) && raw.Length > 0)
        {
            try { analysis = JsonNode.Parse(raw); }
            catch (JsonException) { return "other"; }
        }
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
            // 保险：模型说 critical 却又数不出一个 critical 问题（critical_count=0），
            // 按 warning 处理——critical 至少要有它自己认定的一个问题撑着。
            if (status.Equals("critical", StringComparison.OrdinalIgnoreCase)
                && obj["critical_count"] is JsonValue cc && cc.TryGetValue<int>(out int n) && n <= 0)
                status = "warning";
        }

        // 同义写法一并归一，避免模型偶尔换个词就掉进 other。
        string bucket = status.ToLowerInvariant() switch
        {
            "critical" or "fatal" or "emergency" or "alert" => "critical",
            "error" or "err" or "warning" or "warn" or "notice" => "warning",
            "info" or "informational" or "healthy" or "ok" or "normal" => "healthy",
            _ => "other",
        };

        if (bucket == "critical" && !allowCritical) return "warning";
        return bucket;
    }

    /// <summary>
    /// 字符串重载：先解析再分类。刻意不依赖 JsonNode 对 string 的隐式转换——
    /// 那会把整串 JSON 变成单个 JsonValue（而非 JsonObject），从而把一切误判成
    /// "other"（回填时踩过这个坑，加 allowCritical 参数时又踩了一次）。
    /// </summary>
    public static string Classify(string type, string? analysisRaw, bool allowCritical = true)
    {
        if (string.IsNullOrEmpty(analysisRaw)) return "other";
        try
        {
            return Classify(type, JsonNode.Parse(analysisRaw), allowCritical);
        }
        catch (JsonException)
        {
            return "other";
        }
    }

    private static string Text(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.TryGetValue<string>(out string? text) ? text : "";
}
