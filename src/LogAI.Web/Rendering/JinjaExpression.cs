// Expression evaluation and value formatting for the Jinja subset.
//
// Grammar:
//   expr    := filterChain
//   chain   := or ('|' name ('(' args ')')?)*
//   or      := cmp ('or' cmp)*
//   cmp     := postfix (('==' | '!=') postfix)?
//   postfix := primary ('.' name | '(' args ')')*
//   primary := string | number | True | False | None | name | '(' expr ')'
//
// Calls accept positional and keyword arguments; a call target is either a
// delegate taking CallArgs (used for url_for / get_flashed_messages) or a
// dictionary member named "get" (used for request.cookies.get('x')).

using System.Collections;
using System.Globalization;
using System.Text;

namespace LogAI.Web.Rendering;

internal sealed class CallArgs
{
    public List<object?> Positional { get; } = [];
    public Dictionary<string, object?> Named { get; } = new(StringComparer.Ordinal);

    public object? this[int index] => index < Positional.Count ? Positional[index] : null;
    public object? Get(string name) => Named.TryGetValue(name, out var v) ? v : null;
}

internal static class ValueFormatter
{
    /// <summary>Renders a value the way Jinja's str() would.</summary>
    public static string ToText(object? value) => value switch
    {
        null => "",
        string s => s,
        bool b => b ? "True" : "False",
        double d => d == Math.Floor(d) && Math.Abs(d) < 1e15
            ? ((long)d).ToString(CultureInfo.InvariantCulture)
            : d.ToString("0.######", CultureInfo.InvariantCulture),
        float f => ToText((double)f),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    public static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0,
        int i => i != 0,
        long l => l != 0,
        double d => d != 0,
        decimal m => m != 0,
        ICollection c => c.Count > 0,
        IEnumerable e => e.Cast<object?>().Any(),
        _ => true,
    };

    /// <summary>Iterates like Jinja: dictionaries yield [key, value] pairs.</summary>
    public static IEnumerable<object?> Enumerate(object? value)
    {
        switch (value)
        {
            case null:
                yield break;
            case string s:
                foreach (char c in s) yield return c.ToString();
                yield break;
            case IDictionary dict:
                foreach (DictionaryEntry entry in dict)
                    yield return new object?[] { entry.Key, entry.Value };
                yield break;
            case IEnumerable enumerable:
                foreach (var item in enumerable) yield return item;
                yield break;
            default:
                yield return value;
                yield break;
        }
    }

    public static bool AreEqual(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (a is string sa && b is string sb) return string.Equals(sa, sb, StringComparison.Ordinal);
        if (IsNumber(a) && IsNumber(b))
            return Convert.ToDouble(a, CultureInfo.InvariantCulture)
                .Equals(Convert.ToDouble(b, CultureInfo.InvariantCulture));
        return a.Equals(b);
    }

    private static bool IsNumber(object v) =>
        v is int or long or double or float or decimal or short or byte;
}

internal static class JinjaExpression
{
    internal abstract class Expr
    {
        /// <summary>True when the value must not be HTML escaped ({{ x | safe }}).</summary>
        public bool Raw { get; set; }

        public abstract object? Evaluate(Scope scope, RenderContext ctx);
    }

    // ------------------------------------------------------------- tokens

    private enum Kind { Name, String, Number, Op, End }

    private readonly record struct Token(Kind Kind, string Text);

    private static List<Token> Tokenize(string src)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < src.Length)
        {
            char c = src[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '\'' || c == '"')
            {
                char quote = c;
                i++;
                var sb = new StringBuilder();
                while (i < src.Length && src[i] != quote)
                {
                    if (src[i] == '\\' && i + 1 < src.Length) i++;
                    sb.Append(src[i++]);
                }
                i++; // closing quote
                tokens.Add(new Token(Kind.String, sb.ToString()));
                continue;
            }

            if (char.IsDigit(c))
            {
                int start = i;
                while (i < src.Length && (char.IsDigit(src[i]) || src[i] == '.')) i++;
                tokens.Add(new Token(Kind.Number, src[start..i]));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) i++;
                tokens.Add(new Token(Kind.Name, src[start..i]));
                continue;
            }

            if (i + 1 < src.Length && (src.Substring(i, 2) is "==" or "!=" or "<=" or ">="))
            {
                tokens.Add(new Token(Kind.Op, src.Substring(i, 2)));
                i += 2;
                continue;
            }

            tokens.Add(new Token(Kind.Op, c.ToString()));
            i++;
        }
        tokens.Add(new Token(Kind.End, ""));
        return tokens;
    }

    // ------------------------------------------------------------- parser

    private sealed class ParserState(List<Token> tokens)
    {
        private int _index;

        public Token Peek => tokens[_index];
        public Token Next() => tokens[_index++];

        /// <summary>Cursor position, used for the one-token keyword-argument lookahead.</summary>
        public int Position
        {
            get => _index;
            set => _index = value;
        }

        public bool Accept(string text)
        {
            if (Peek.Text != text) return false;
            _index++;
            return true;
        }

        public bool AcceptName(string text) =>
            Peek.Kind == Kind.Name && Peek.Text == text && Next().Text == text;

        public void Expect(string text)
        {
            if (!Accept(text)) throw new InvalidOperationException($"expected '{text}', found '{Peek.Text}'");
        }
    }

    public static Expr Parse(string source)
    {
        var state = new ParserState(Tokenize(source));
        var expr = ParseChain(state);
        if (state.Peek.Kind != Kind.End)
            throw new InvalidOperationException($"trailing input in expression: {source}");
        return expr;
    }

    private static Expr ParseChain(ParserState s)
    {
        Expr expr = ParseOr(s);
        while (s.Accept("|"))
        {
            string name = s.Next().Text;
            var args = new List<Expr>();
            if (s.Accept("("))
            {
                if (!s.Accept(")"))
                {
                    do { args.Add(ParseChain(s)); } while (s.Accept(","));
                    s.Expect(")");
                }
            }

            switch (name)
            {
                case "safe":
                    expr.Raw = true;
                    break;
                case "default":
                {
                    var inner = expr;
                    var fallback = args.Count > 0 ? args[0] : new LiteralExpr("");
                    expr = new DefaultExpr(inner, fallback) { Raw = inner.Raw };
                    break;
                }
                default:
                    throw new NotSupportedException($"template filter '{name}' is not implemented");
            }
        }
        return expr;
    }

    private static Expr ParseOr(ParserState s)
    {
        Expr left = ParseComparison(s);
        while (s.Peek.Kind == Kind.Name && s.Peek.Text == "or")
        {
            s.Next();
            left = new OrExpr(left, ParseComparison(s));
        }
        return left;
    }

    private static Expr ParseComparison(ParserState s)
    {
        Expr left = ParsePostfix(s);
        if (s.Peek.Kind == Kind.Op && s.Peek.Text is "==" or "!=")
        {
            string op = s.Next().Text;
            Expr right = ParsePostfix(s);
            return new CompareExpr(left, right, op == "==");
        }
        return left;
    }

    private static Expr ParsePostfix(ParserState s)
    {
        Expr expr = ParsePrimary(s);
        while (true)
        {
            if (s.Accept("."))
            {
                string member = s.Next().Text;
                expr = new MemberExpr(expr, member);
            }
            else if (s.Peek.Text == "(")
            {
                s.Next();
                var positional = new List<Expr>();
                var named = new List<(string, Expr)>();
                if (!s.Accept(")"))
                {
                    do
                    {
                        // keyword argument:  name = expr
                        if (s.Peek.Kind == Kind.Name)
                        {
                            int save = s.Position;
                            string maybeName = s.Next().Text;
                            if (s.Accept("="))
                            {
                                named.Add((maybeName, ParseChain(s)));
                                continue;
                            }
                            s.Position = save;   // not a keyword argument after all
                        }
                        positional.Add(ParseChain(s));
                    } while (s.Accept(","));
                    s.Expect(")");
                }
                expr = new CallExpr(expr, positional, named);
            }
            else
            {
                break;
            }
        }
        return expr;
    }

    private static Expr ParsePrimary(ParserState s)
    {
        Token token = s.Next();
        switch (token.Kind)
        {
            case Kind.String:
                return new LiteralExpr(token.Text);
            case Kind.Number:
                return new LiteralExpr(token.Text.Contains('.')
                    ? double.Parse(token.Text, CultureInfo.InvariantCulture)
                    : long.Parse(token.Text, CultureInfo.InvariantCulture));
            case Kind.Name:
                return token.Text switch
                {
                    "True" or "true" => new LiteralExpr(true),
                    "False" or "false" => new LiteralExpr(false),
                    "None" or "none" or "null" => new LiteralExpr(null),
                    _ => new NameExpr(token.Text),
                };
            case Kind.Op when token.Text == "(":
            {
                Expr inner = ParseChain(s);
                s.Expect(")");
                return inner;
            }
            default:
                throw new InvalidOperationException($"unexpected token '{token.Text}'");
        }
    }

    // ------------------------------------------------------------- nodes

    private sealed class LiteralExpr(object? value) : Expr
    {
        public override object? Evaluate(Scope scope, RenderContext ctx) => value;
    }

    private sealed class NameExpr(string name) : Expr
    {
        public override object? Evaluate(Scope scope, RenderContext ctx) =>
            scope.TryGet(name, out var v) ? v : null;
    }

    private sealed class MemberExpr(Expr target, string member) : Expr
    {
        public Expr Target { get; } = target;
        public string Member { get; } = member;

        public override object? Evaluate(Scope scope, RenderContext ctx)
        {
            object? value = Target.Evaluate(scope, ctx);
            switch (value)
            {
                case null:
                    return null;
                case IDictionary<string, object?> map:
                    return map.TryGetValue(Member, out var v) ? v : null;
                case IDictionary dict:
                    return dict.Contains(Member) ? dict[Member] : null;
                default:
                {
                    var prop = value.GetType().GetProperty(Member,
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.IgnoreCase);
                    return prop?.GetValue(value);
                }
            }
        }
    }

    private sealed class CallExpr(Expr target, List<Expr> positional, List<(string Name, Expr Value)> named) : Expr
    {
        public override object? Evaluate(Scope scope, RenderContext ctx)
        {
            var args = new CallArgs();
            foreach (var expr in positional) args.Positional.Add(expr.Evaluate(scope, ctx));
            foreach (var (name, expr) in named) args.Named[name] = expr.Evaluate(scope, ctx);

            // request.cookies.get('sidebar_collapsed') — a method on a mapping.
            if (target is MemberExpr { Member: "get" } member &&
                member.Target.Evaluate(scope, ctx) is IDictionary<string, object?> jar)
            {
                string key = ValueFormatter.ToText(args[0]);
                return jar.TryGetValue(key, out var found) ? found : null;
            }

            object? callee = target.Evaluate(scope, ctx);
            if (callee is Func<CallArgs, object?> func) return func(args);

            throw new InvalidOperationException("attempted to call a value that is not callable");
        }
    }

    private sealed class OrExpr(Expr left, Expr right) : Expr
    {
        public override object? Evaluate(Scope scope, RenderContext ctx)
        {
            object? value = left.Evaluate(scope, ctx);
            return ValueFormatter.IsTruthy(value) ? value : right.Evaluate(scope, ctx);
        }
    }

    private sealed class CompareExpr(Expr left, Expr right, bool equals) : Expr
    {
        public override object? Evaluate(Scope scope, RenderContext ctx)
        {
            bool same = ValueFormatter.AreEqual(left.Evaluate(scope, ctx), right.Evaluate(scope, ctx));
            return equals ? same : !same;
        }
    }

    private sealed class DefaultExpr(Expr inner, Expr fallback) : Expr
    {
        public override object? Evaluate(Scope scope, RenderContext ctx)
        {
            object? value = inner.Evaluate(scope, ctx);
            return value is null ? fallback.Evaluate(scope, ctx) : value;
        }
    }
}
