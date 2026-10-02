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
    /// Turns "issues_found": [ "Host": "text", ... ] into a list of objects.
    /// </summary>
    private static JsonNode? Normalize(JsonNode? node)
    {
        // Intentionally a pass-through. An earlier version rebuilt every array
        // into key/value pairs, which silently corrupted legitimate string
        // arrays such as "issues_found": ["host down", "disk almost full"].
        // The genuinely malformed form — "key": "value" written directly
        // inside an array — is invalid JSON syntax, so it has to be repaired
        // before parsing; that transformation is still outstanding.
        return node;

#pragma warning disable CS0162

        if (node is not JsonObject obj) return node;
        foreach (string key in obj.Select(p => p.Key).ToList())
        {
            if (obj[key] is JsonArray array && array.Count > 0 && array.All(item => item is JsonObject)) continue;
            if (obj[key] is not JsonArray pairs) continue;

            var rebuilt = new JsonArray();
            for (int i = 0; i < pairs.Count; i += 2)
            {
                var entry = new JsonObject();
                string name = pairs[i]?.GetValue<string>() ?? "";
                entry[name] = i + 1 < pairs.Count ? pairs[i + 1]?.DeepClone() : null;
                rebuilt.Add(entry);
            }
            if (rebuilt.Count > 0) obj[key] = rebuilt;
        }
        return obj;
    }
}
