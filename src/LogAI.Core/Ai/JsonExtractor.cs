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
    /// 对提取出的分析结果做确定性后处理。小模型常把同一个问题重复列进
    /// issues_found / recommendations（尤其当输入里同一条例行消息出现很多遍时），
    /// 并据此把 critical_count 数大。这里按字符串精确去重，并把 critical_count
    /// 收进去重后的条数（critical_count 永远不该超过 issues 的条数）。
    /// </summary>
    private static JsonNode? Normalize(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            DedupeStringArray(obj, "issues_found");
            DedupeStringArray(obj, "recommendations");
            if (obj["critical_count"] is JsonValue cc && obj["issues_found"] is JsonArray issues
                && cc.TryGetValue<int>(out int n) && n > issues.Count)
                obj["critical_count"] = issues.Count;
        }
        return node;
    }

    private static void DedupeStringArray(JsonObject obj, string key)
    {
        if (obj[key] is not JsonArray arr) return;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new JsonArray();
        foreach (var item in arr)
        {
            if (item is JsonValue jv && jv.TryGetValue<string>(out string? text) && text is not null)
            {
                if (seen.Add(text)) unique.Add(text);
            }
            else
            {
                unique.Add(item?.DeepClone());   // 非字符串（对象型 issue）原样保留
            }
        }
        obj[key] = unique;
    }
}
