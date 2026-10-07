// Pulls a JSON object out of a model reply.
//
// Local models are unreliable in three specific ways that this handles, all of
// them observed in this deployment:
//   1. the object arrives wrapped in prose or a ```json fence;
//   2. it is cut off mid-token when the reply hits the token budget, leaving
//      unbalanced braces (the "Output truncated at 2048 tokens" case);
//   3. an array of objects is emitted as "key": "value" pairs inside an array,
//      which is not valid JSON and used to make whole batches unparseable.
//
// Strategy order: the whole reply, then a fenced block, then the largest
// balanced brace run, then the same run repaired by closing what is open.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LogAI.Core.Ai;

public static class JsonExtractor
{
    /// <summary>Extracts the first usable JSON object; null when nothing parses.</summary>
    public static JsonNode? Extract(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;

        foreach (string candidate in Candidates(reply))
        {
            if (TryParse(candidate, out JsonNode? node)) return Normalize(node);
        }
        return null;
    }

    private static IEnumerable<string> Candidates(string reply)
    {
        string text = reply.Trim();
        yield return text;

        // A truncated reply is unbalanced by definition, so the repairs have to
        // be attempted on the raw text as well — not only on balanced runs.
        foreach (string repaired in Repairs(text)) yield return repaired;

        int firstBrace = text.IndexOf('{');
        if (firstBrace > 0)
        {
            string tail = text[firstBrace..];
            yield return tail;
            foreach (string repaired in Repairs(tail)) yield return repaired;
        }

        foreach (string fenced in FencedBlocks(text))
        {
            yield return fenced;
            foreach (string repaired in Repairs(fenced)) yield return repaired;
        }

        foreach (string balanced in BalancedObjects(text))
        {
            yield return balanced;
            foreach (string repaired in Repairs(balanced)) yield return repaired;
        }
    }

    private static IEnumerable<string> FencedBlocks(string text)
    {
        int index = 0;
        while (true)
        {
            int open = text.IndexOf("```", index, StringComparison.Ordinal);
            if (open < 0) yield break;
            int start = text.IndexOf('\n', open);
            if (start < 0) yield break;
            int close = text.IndexOf("```", start, StringComparison.Ordinal);
            if (close < 0)
            {
                yield return text[(start + 1)..];       // fence never closed
                yield break;
            }
            yield return text[(start + 1)..close];
            index = close + 3;
        }
    }

    /// <summary>Balanced {...} runs, longest first, ignoring braces inside strings.</summary>
    private static IEnumerable<string> BalancedObjects(string text)
    {
        var found = new List<string>();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '{') continue;
            int depth = 0;
            bool inString = false, escaped = false;
            for (int j = i; j < text.Length; j++)
            {
                char c = text[j];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        found.Add(text[i..(j + 1)]);
                        i = j;
                        break;
                    }
                }
            }
        }
        return found.OrderByDescending(f => f.Length);
    }

    /// <summary>Repairs a truncated object: drops a dangling token, closes what is open.</summary>
    private static IEnumerable<string> Repairs(string candidate)
    {
        string text = candidate.Trim();

        // Drop a trailing partial member, e.g. ... "message": "half a sen
        int lastComma = text.LastIndexOf(',');
        if (lastComma > 0 && !IsCompleteTail(text[lastComma..]))
            yield return Close(text[..lastComma]);

        yield return Close(text);
    }

    private static bool IsCompleteTail(string tail)
    {
        int quotes = tail.Count(c => c == '"') - tail.Count(c => c == '\\');
        return quotes % 2 == 0;
    }

    private static string Close(string text)
    {
        var stack = new Stack<char>();
        bool inString = false, escaped = false;
        foreach (char c in text)
        {
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{': stack.Push('}'); break;
                case '[': stack.Push(']'); break;
                case '}' or ']': if (stack.Count > 0) stack.Pop(); break;
            }
        }

        var builder = new StringBuilder(text);
        if (inString) builder.Append('"');            // close an unterminated string
        while (stack.Count > 0) builder.Append(stack.Pop());
        return builder.ToString();
    }

    private static bool TryParse(string candidate, out JsonNode? node)
    {
        node = null;
        string text = candidate.Trim();
        if (text.Length == 0 || text[0] != '{') return false;
        try
        {
            node = JsonNode.Parse(text);
            return node is JsonObject;
        }
        catch (JsonException)
        {
            // Retry once with the malformed array form rewritten; a reply that
            // uses it is otherwise lost entirely.
            try
            {
                string repaired = PairArrayRepair.Apply(text);
                if (repaired == text) return false;
                node = JsonNode.Parse(repaired);
                return node is JsonObject;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 对提取出的分析结果做最轻的后处理：只补齐缺失字段（数组→[]、critical_count→0、
    /// alert_message→""），保留模型原始输出（不去重、不封顶 critical_count）。
    /// </summary>
    private static JsonNode? Normalize(JsonNode? node)
    {
        if (node is JsonObject obj) EnsureFields(obj);
        return node;
    }

    /// <summary>
    /// 补齐模型偶尔漏掉的字段，保证结果 JSON 始终有全部 6 个键。值给安全默认：
    /// 数组→[]、critical_count→0、alert_message→""。这样界面不会因为缺字段而空一块。
    /// 100% 纯模型：只补缺失字段的默认值，不塞任何程序生成的建议。
    /// </summary>
    private static void EnsureFields(JsonObject obj)
    {
        if (obj["issues_found"] is not JsonArray) obj["issues_found"] = new JsonArray();
        if (obj["recommendations"] is not JsonArray) obj["recommendations"] = new JsonArray();
        if (obj["affected_hosts"] is not JsonArray) obj["affected_hosts"] = new JsonArray();
        if (obj["critical_count"] is null) obj["critical_count"] = 0;
        if (obj["alert_message"] is null) obj["alert_message"] = "";
    }

    /// <summary>
    /// 主机前缀交给模型自己匹配（提示词已要求每条 issue/建议带 [HOST]）。这里只做最轻的
    /// 整理、不猜主机：已有可信 [host] 前缀的保留；没有前缀但文本里显式写了某台主机
    /// （模型自己写的"on 192.168.50.15"）就补上；两者都没有就原样留空，不再程序猜测。
    /// </summary>
    public static void EnsureHostPrefix(JsonObject obj, List<string> batchHosts)
    {
        if (obj["issues_found"] is not JsonArray issues) return;
        if (obj["recommendations"] is not JsonArray recs) return;

        var hosts = new List<string>();
        if (obj["affected_hosts"] is JsonArray affected)
            foreach (var h in affected)
            {
                string t = h?.ToString()?.Trim() ?? "";
                if (t.Length > 0 && !hosts.Contains(t)) hosts.Add(t);
            }
        foreach (string h in batchHosts)
            if (!string.IsNullOrEmpty(h) && !hosts.Contains(h)) hosts.Add(h);

        PrefixHostList(issues, hosts);
        PrefixHostList(recs, hosts);
    }

    /// <summary>text 以可信的 "[host]" 开头时返回该 host；否则返回空串。</summary>
    private static string ExtractHostPrefix(string text)
    {
        if (!text.StartsWith("[", StringComparison.Ordinal)) return "";
        int close = text.IndexOf(']');
        if (close == -1) return "";
        string inner = text.Substring(1, close - 1).Trim();
        return inner.Length > 0
            && !inner.Any(c => c is ' ' or ':' or '=')
            && !inner.Contains('[')
            ? inner
            : "";
    }

    private static void PrefixHostList(JsonArray arr, List<string> hosts)
    {
        for (int i = 0; i < arr.Count; i++)
        {
            string text = arr[i]?.ToString()?.Trim() ?? "";
            if (text.Length == 0) continue;
            if (ExtractHostPrefix(text).Length > 0) continue;
            // 有非主机前缀（如 "[x] foo: bar"）时剥掉重加。
            if (text.StartsWith("[", StringComparison.Ordinal))
            {
                int close = text.IndexOf(']');
                if (close != -1)
                {
                    text = text.Substring(close + 1).Trim();
                    if (text.Length == 0) continue;
                }
            }
            // 只认文本里显式出现的主机；没有就不加（交给模型自己匹配）。
            string matched = hosts.FirstOrDefault(h => !string.IsNullOrEmpty(h) && text.Contains(h, StringComparison.Ordinal)) ?? "";
            if (matched.Length > 0) arr[i] = "[" + matched + "] " + text;
        }
    }

    /// <summary>
    /// 修复旧记录：把 recommendations 里已有的 [host] 前缀剥掉，再用当前匹配逻辑重新挂
    /// 主机（旧版把通用建议错挂到批里第一个主机，导致 R4S/debian 之类对不上）。issues 不动。
    /// 返回是否有前缀被剥掉（调用方据此判断是否需要写回）。
    /// </summary>
    public static bool RehostRecommendations(JsonObject obj)
    {
        if (obj["recommendations"] is not JsonArray recs) return false;
        bool had = false;
        for (int i = 0; i < recs.Count; i++)
        {
            string text = recs[i]?.ToString()?.Trim() ?? "";
            if (ExtractHostPrefix(text).Length == 0) continue;
            recs[i] = text.Substring(text.IndexOf(']') + 1).Trim();
            had = true;
        }
        if (had) EnsureHostPrefix(obj, new List<string>());
        return had;
    }

    /// <summary>
    /// 批量分析结果的必需字段是否齐全且自洽（模型偶尔漏掉 recommendations/critical_count，
    /// 或给了"有 issues 但 recommendations 空"）。不齐全/不自洽时调用方触发纠正性重试。
    /// </summary>
    public static bool HasRequiredFields(JsonNode? node)
    {
        if (node is not JsonObject obj) return false;
        if (!obj.ContainsKey("overall_status") || !obj.ContainsKey("issues_found")
            || !obj.ContainsKey("recommendations") || !obj.ContainsKey("critical_count"))
            return false;
        // 有 issues 就必须有 recommendations，否则重试让模型补一份有建议的完整 JSON。
        if (obj["issues_found"] is JsonArray iss && iss.Count > 0
            && obj["recommendations"] is JsonArray rec && rec.Count == 0)
            return false;
        return true;
    }
}
