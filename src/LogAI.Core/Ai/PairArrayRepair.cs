// Rewrites the malformed array form local models produce:
//
//     "issues_found": [ "Host": "description", "Other": "text" ]
//
// JSON does not allow key:value pairs inside an array, so the reply fails to
// parse and a whole batch used to be lost. The intent is obvious, so the text
// is rewritten into objects before parsing:
//
//     "issues_found": [ { "Host": "description" }, { "Other": "text" } ]
//
// The scan is character based (no regex) so it cannot be confused by braces or
// commas inside string values.

using System.Text;

namespace LogAI.Core.Ai;

public static class PairArrayRepair
{
    public static string Apply(string json)
    {
        var output = new StringBuilder(json.Length + 32);
        var containers = new Stack<char>();      // '[' or '{'
        var pendingCloses = new Stack<int>();    // implicit objects open per container
        bool inString = false, escaped = false, changed = false;

        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];

            if (inString)
            {
                output.Append(c);
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"' && containers.Count > 0 && containers.Peek() == '[')
            {
                // A string directly inside an array: is it followed by ':' ?
                int end = EndOfString(json, i);
                if (end > 0)
                {
                    int colon = end + 1;
                    while (colon < json.Length && char.IsWhiteSpace(json[colon])) colon++;
                    if (colon < json.Length && json[colon] == ':')
                    {
                        output.Append('{').Append(json, i, end - i + 1).Append(':');
                        pendingCloses.Push(pendingCloses.Pop() + 1);
                        changed = true;
                        inString = false;
                        i = colon;                          // resume after the colon
                        continue;
                    }
                }
                output.Append(c);
                inString = true;
                continue;
            }

            switch (c)
            {
                case '"':
                    output.Append(c);
                    inString = true;
                    break;
                case '{':
                case '[':
                    output.Append(c);
                    containers.Push(c);
                    pendingCloses.Push(0);
                    break;
                case ',':
                    // A comma at the current level terminates an implicit object.
                    if (pendingCloses.Count > 0 && pendingCloses.Peek() > 0)
                    {
                        output.Append('}', pendingCloses.Pop());
                        pendingCloses.Push(0);
                    }
                    output.Append(c);
                    break;
                case '}':
                case ']':
                    if (pendingCloses.Count > 0 && pendingCloses.Peek() > 0)
                    {
                        output.Append('}', pendingCloses.Pop());
                        pendingCloses.Push(0);
                    }
                    output.Append(c);
                    if (containers.Count > 0) containers.Pop();
                    if (pendingCloses.Count > 0) pendingCloses.Pop();
                    break;
                default:
                    output.Append(c);
                    break;
            }
        }

        return changed ? output.ToString() : json;
    }

    /// <summary>Index of the closing quote, or -1 when the string never closes.</summary>
    private static int EndOfString(string text, int start)
    {
        bool escaped = false;
        for (int i = start + 1; i < text.Length; i++)
        {
            if (escaped) { escaped = false; continue; }
            if (text[i] == '\\') { escaped = true; continue; }
            if (text[i] == '"') return i;
        }
        return -1;
    }
}
