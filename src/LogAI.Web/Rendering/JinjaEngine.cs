// JinjaEngine — a minimal Jinja2-subset renderer.
//
// Scope is deliberately narrow: it implements exactly the subset the existing
// LogAI Monitor templates use, so the template files can be carried over
// byte-for-byte from the Python version.  Supported:
//
//   {% extends "base.html" %}          template inheritance
//   {% block name %} ... {% endblock %}  named, overridable regions
//   {% if expr %} {% endif %}          truthiness, ==, "or"
//   {% for x in xs %}                  and {% for k, v in pairs %}
//   {% with a = expr %} {% endwith %}  lexical binding
//   {{ expr }}                         dotted paths, calls, literals
//   {{ value | safe }}                 no escaping
//   {{ value | default('x') }}         fallback for undefined
//   {{ a or b }}                       first truthy operand
//
// Autoescaping follows Jinja2's html escape (& < > " ').

using System.Collections;
using System.Globalization;
using System.Text;

namespace LogAI.Web.Rendering;

/// <summary>Values visible to a template, with lexical nesting for {% with %}.</summary>
public sealed class Scope
{
    private readonly Scope? _parent;
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    public Scope(Scope? parent = null) => _parent = parent;

    public void Set(string name, object? value) => _values[name] = value;

    public bool TryGet(string name, out object? value)
    {
        for (Scope? s = this; s is not null; s = s._parent)
            if (s._values.TryGetValue(name, out value)) return true;
        value = null;
        return false;
    }

    public object? Get(string name) => TryGet(name, out var v) ? v : null;
}

// ----------------------------------------------------------------- AST

internal abstract class Node
{
    public abstract void Render(StringBuilder sb, Scope scope, RenderContext ctx);
}

internal sealed class TextNode(string text) : Node
{
    public override void Render(StringBuilder sb, Scope scope, RenderContext ctx) => sb.Append(text);
}

internal sealed class ExprNode(JinjaExpression.Expr expr) : Node
{
    public override void Render(StringBuilder sb, Scope scope, RenderContext ctx)
    {
        object? value = expr.Evaluate(scope, ctx);
        if (expr.Raw) sb.Append(ValueFormatter.ToText(value));
        else sb.Append(HtmlEscape(ValueFormatter.ToText(value)));
    }

    internal static string HtmlEscape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&#34;"); break;
                case '\'': sb.Append("&#39;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}

internal sealed class IfNode(JinjaExpression.Expr condition) : Node
{
    public List<Node> Body { get; } = [];
    public List<Node>? Else { get; set; }

    public override void Render(StringBuilder sb, Scope scope, RenderContext ctx)
    {
        bool truthy = ValueFormatter.IsTruthy(condition.Evaluate(scope, ctx));
        var branch = truthy ? Body : Else;
        if (branch is null) return;
        foreach (var node in branch) node.Render(sb, scope, ctx);
    }
}

internal sealed class ForNode(string first, string? second, JinjaExpression.Expr source) : Node
{
    public List<Node> Body { get; } = [];

    public override void Render(StringBuilder sb, Scope scope, RenderContext ctx)
    {
        object? value = source.Evaluate(scope, ctx);
        if (value is null) return;
        foreach (var item in ValueFormatter.Enumerate(value))
        {
            var inner = new Scope(scope);
            if (second is not null)
            {
                var pair = ValueFormatter.Enumerate(item).ToArray();
                inner.Set(first, pair.Length > 0 ? pair[0] : null);
                inner.Set(second, pair.Length > 1 ? pair[1] : null);
            }
            else
            {
                inner.Set(first, item);
            }
            foreach (var node in Body) node.Render(sb, inner, ctx);
        }
    }
}

internal sealed class WithNode(string name, JinjaExpression.Expr value) : Node
{
    public List<Node> Body { get; } = [];

    public override void Render(StringBuilder sb, Scope scope, RenderContext ctx)
    {
        var inner = new Scope(scope);
        inner.Set(name, value.Evaluate(scope, ctx));
        foreach (var node in Body) node.Render(sb, inner, ctx);
    }
}

internal sealed class BlockNode(string name) : Node
{
    public List<Node> Body { get; } = [];

    public override void Render(StringBuilder sb, Scope scope, RenderContext ctx)
    {
        // A child template's block wins over the parent's own content.
        if (ctx.Blocks.TryGetValue(name, out var overridden))
        {
            foreach (var node in overridden) node.Render(sb, scope, ctx);
            return;
        }
        foreach (var node in Body) node.Render(sb, scope, ctx);
    }
}

// ----------------------------------------------------------------- engine

internal sealed class RenderContext
{
    public required Dictionary<string, List<Node>> Blocks { get; init; }
}

internal sealed class Template
{
    public string? Parent { get; set; }
    public List<Node> Body { get; } = [];
    public Dictionary<string, BlockNode> Blocks { get; } = new(StringComparer.Ordinal);
}

/// <summary>Renders the project's Jinja templates.</summary>
public sealed class JinjaEngine
{
    private readonly string _root;
    private readonly Dictionary<string, Template> _cache = new(StringComparer.Ordinal);

    public JinjaEngine(string templateRoot) =>
        _root = Path.GetFullPath(templateRoot);

    public string Render(string templateName, Scope scope) =>
        Render(templateName, scope, new Dictionary<string, List<Node>>(StringComparer.Ordinal));

    private string Render(string templateName, Scope scope, Dictionary<string, List<Node>> blocks)
    {
        var t = Load(templateName);

        // Walk up the inheritance chain, letting the most derived block win.
        var merged = new Dictionary<string, List<Node>>(blocks, StringComparer.Ordinal);
        foreach (var (name, block) in t.Blocks)
            if (!merged.ContainsKey(name))
                merged[name] = block.Body;

        if (t.Parent is not null)
            return Render(t.Parent, scope, merged);

        var sb = new StringBuilder(8192);
        var ctx = new RenderContext { Blocks = merged };
        foreach (var node in t.Body) node.Render(sb, scope, ctx);
        return sb.ToString();
    }

    private Template Load(string name)
    {
        if (_cache.TryGetValue(name, out var cached)) return cached;

        string path = Path.GetFullPath(Path.Combine(_root, name));
        if (!path.StartsWith(_root, StringComparison.Ordinal))
            throw new InvalidOperationException($"template escapes the template root: {name}");

        string source = File.ReadAllText(path);
        // Jinja's keep_trailing_newline defaults to False: one trailing newline
        // is dropped, which is why the Python output has none after </html>.
        if (source.EndsWith('\n')) source = source[..^1];

        var template = Parser.Parse(source);
        _cache[name] = template;
        return template;
    }
}

// ----------------------------------------------------------------- parser

internal static class Parser
{
    public static Template Parse(string source)
    {
        int pos = 0;
        var template = new Template();
        ParseNodes(source, ref pos, template.Body, template.Blocks, template, stopAtEnd: false);
        return template;
    }

    private static void ParseNodes(string src, ref int pos, List<Node> into,
                                   Dictionary<string, BlockNode> blocks,
                                   Template template, bool stopAtEnd)
    {
        var text = new StringBuilder();

        while (pos < src.Length)
        {
            int open = src.IndexOf('{', pos);
            if (open < 0)
            {
                text.Append(src, pos, src.Length - pos);
                pos = src.Length;
                break;
            }

            // Not a tag: keep the brace as literal text.
            if (open + 1 >= src.Length || (src[open + 1] != '{' && src[open + 1] != '%'))
            {
                text.Append(src, pos, open - pos + 1);
                pos = open + 1;
                continue;
            }

            text.Append(src, pos, open - pos);
            bool isExpression = src[open + 1] == '{';
            string closer = isExpression ? "}}" : "%}";
            int close = src.IndexOf(closer, open + 2, StringComparison.Ordinal);
            if (close < 0) throw new InvalidOperationException("unterminated template tag");
            string inner = src[(open + 2)..close].Trim();
            pos = close + 2;

            if (text.Length > 0)
            {
                into.Add(new TextNode(text.ToString()));
                text.Clear();
            }

            if (isExpression)
            {
                into.Add(new ExprNode(JinjaExpression.Parse(inner)));
                continue;
            }

            // Statement
            string head = inner.Split(' ', 2)[0];

            switch (head)
            {
                case "extends":
                {
                    template.Parent = Unquote(inner["extends".Length..].Trim());
                    break;
                }
                case "block":
                {
                    string name = inner["block".Length..].Trim();
                    var block = new BlockNode(name);
                    blocks[name] = block;
                    into.Add(block);
                    ParseNodes(src, ref pos, block.Body, blocks, template, stopAtEnd: true);
                    TryConsumeTag(src, ref pos, "endblock");
                    break;
                }
                case "endblock":
                    if (stopAtEnd) { pos = open; return; }
                    break;
                case "if":
                {
                    var node = new IfNode(JinjaExpression.Parse(inner[2..].Trim()));
                    into.Add(node);
                    ParseNodes(src, ref pos, node.Body, blocks, template, stopAtEnd: true);

                    // The body stopped at {% else %} or {% endif %}; the cursor sits
                    // on that tag, so the enclosing scope can decide what to do.
                    if (PeekTag(src, pos).Tag == "else")
                    {
                        AdvancePastTag(src, ref pos);
                        node.Else = [];
                        ParseNodes(src, ref pos, node.Else, blocks, template, stopAtEnd: true);
                    }
                    TryConsumeTag(src, ref pos, "endif");
                    break;
                }
                case "else":
                    if (stopAtEnd) { pos = open; return; }
                    break;
                case "endif":
                    if (stopAtEnd) { pos = open; return; }
                    break;
                case "for":
                {
                    var (name, second, expr) = ParseForHeader(inner);
                    var node = new ForNode(name, second, expr);
                    into.Add(node);
                    ParseNodes(src, ref pos, node.Body, blocks, template, stopAtEnd: true);
                    TryConsumeTag(src, ref pos, "endfor");
                    break;
                }
                case "endfor":
                    if (stopAtEnd) { pos = open; return; }
                    break;
                case "with":
                {
                    string assignment = inner["with".Length..].Trim();
                    int eq = assignment.IndexOf('=');
                    string name = assignment[..eq].Trim();
                    var expr = JinjaExpression.Parse(assignment[(eq + 1)..].Trim());
                    var node = new WithNode(name, expr);
                    into.Add(node);
                    ParseNodes(src, ref pos, node.Body, blocks, template, stopAtEnd: true);
                    TryConsumeTag(src, ref pos, "endwith");
                    break;
                }
                case "endwith":
                    if (stopAtEnd) { pos = open; return; }
                    break;
                default:
                    // Unknown tags are ignored rather than fatal, matching Jinja's
                    // permissiveness for extension tags we do not implement.
                    break;
            }
        }

        if (text.Length > 0) into.Add(new TextNode(text.ToString()));
    }

    /// <summary>Consumes the named tag if it comes next; otherwise leaves the cursor alone.</summary>
    private static bool TryConsumeTag(string src, ref int pos, string name)
    {
        var (tag, end) = PeekTag(src, pos);
        if (tag != name) return false;
        pos = end;
        return true;
    }

    private static (string Tag, int End) PeekTag(string src, int pos)
    {
        while (pos < src.Length && char.IsWhiteSpace(src[pos])) pos++;
        if (pos + 1 >= src.Length || src[pos] != '{' || src[pos + 1] != '%') return ("", pos);
        int close = src.IndexOf("%}", pos + 2, StringComparison.Ordinal);
        if (close < 0) return ("", pos);
        string inner = src[(pos + 2)..close].Trim();
        return (inner.Split(' ', 2)[0], close + 2);
    }

    private static void AdvancePastTag(string src, ref int pos)
    {
        var (_, end) = PeekTag(src, pos);
        pos = end;
    }

    private static (string First, string? Second, JinjaExpression.Expr Source) ParseForHeader(string inner)
    {
        // {% for x in xs %}  /  {% for k, v in pairs %}
        string body = inner["for".Length..].Trim();
        int inIdx = body.IndexOf(" in ", StringComparison.Ordinal);
        if (inIdx < 0) throw new InvalidOperationException($"unsupported for: {inner}");
        string targets = body[..inIdx].Trim();
        string src = body[(inIdx + 4)..].Trim();
        string[] names = targets.Split(',', StringSplitOptions.TrimEntries);
        return (names[0], names.Length > 1 ? names[1] : null, JinjaExpression.Parse(src));
    }

    private static string Unquote(string s) => s.Trim().Trim('"', '\'');

    /// <summary>Guards against the engine silently accepting templates it cannot render.</summary>
    public static void AssertSupported(string source, string name)
    {
        foreach (string tag in new[] { "macro", "include", "import", "set", "filter", "call" })
            if (source.Contains("{% " + tag + " ", StringComparison.Ordinal))
                throw new NotSupportedException($"{name}: template tag '{tag}' is not supported");
    }
}
