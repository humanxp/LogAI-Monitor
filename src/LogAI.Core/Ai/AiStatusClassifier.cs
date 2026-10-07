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
        // 单条分析的 category 是"类型"（security/network/...）而非级别，这里把类型
        // 也归到 4 档：security → critical，performance/application/system/network → warning。
        string bucket = status.ToLowerInvariant() switch
        {
            "critical" or "fatal" or "emergency" or "alert" or "security" => "critical",
            "error" or "err" or "warning" or "warn" or "notice"
                or "performance" or "application" or "system" or "network" => "warning",
            "info" or "informational" or "healthy" or "ok" or "normal" => "healthy",
            _ => "other",
        };

        if (bucket == "critical" && !allowCritical) return "warning";
        // 模型偶尔判 healthy 却在 issues_found 里列了真实故障（Transfer failed / open
        // failed / Failed to send），自相矛盾。按"字面故障词"确定性升到 warning。
        if (bucket == "healthy" && HasFailureIssue(obj)) return "warning";
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

    /// <summary>
    /// 判断一条日志是否"真正危急"。级别标签不可信——线上有设备把
    /// "start NTP update"（开始 NTP 同步，纯例行）标成 emergency，于是只凭级别就能
    /// 骗过 critical 闸门，让整批例行日志被判成 critical。所以这里要求**级别危急
    /// 且消息里确实出现故障特征词**。critical 闸门（AiHistoryWriter / AnalyzeApi）
    /// 据此决定是否允许判 critical。
    /// </summary>
    public static bool IsGenuinelyCritical(string? severity, string? message)
    {
        if (severity is null) return false;
        string s = severity.ToLowerInvariant();
        if (s is not ("emergency" or "emerg" or "alert" or "critical" or "crit" or "fatal")) return false;

        string m = (message ?? "").ToLowerInvariant();
        foreach (string marker in FailureMarkers)
            if (m.Contains(marker, StringComparison.Ordinal)) return true;
        return false;
    }

    private static readonly string[] FailureMarkers =
    [
        "failed", "failure", " down", "unreachable", "refused", "killed",
        "out of memory", "panic", "segfault", "breach", "crashed",
        "fatal", "halted", "timed out", "unresponsive", "data loss",
    ];

    /// <summary>
    /// issues_found 里是否有一条带故障词。模型偶尔判 healthy（认为整体没问题）却把
    /// 真实故障也列进了 issues（Transfer failed / open(...) failed / Failed to send），
    /// 二者自相矛盾。按"字面故障词"这个确定性规则把 healthy 升到 warning——
    /// 与提示词里的"字面出现 failed 即算真问题"同一口径，只是不指望模型自觉遵守。
    /// </summary>
    private static bool HasFailureIssue(JsonObject obj)
    {
        if (obj["issues_found"] is not JsonArray arr) return false;
        foreach (var item in arr)
        {
            string text = (item as JsonValue)?.TryGetValue<string>(out string? s) == true && s is not null
                ? s.ToLowerInvariant() : item?.ToString().ToLowerInvariant() ?? "";
            foreach (string marker in FailureMarkers)
                if (text.Contains(marker, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
