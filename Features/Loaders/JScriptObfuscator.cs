using System.Text;
using Esprima;
using Esprima.Ast;
using Esprima.Utils;

namespace C2.Features.Loaders;

/// <summary>
/// JScript (WSH) obfuscator using Esprima AST — parses JScript, walks the AST
/// in two passes (collect declarations, then collect references), applies
/// position-based source replacements.
///
/// Techniques:
///   1. Variable/function/parameter name randomization
///   2. String literal encoding (hex escapes, split-concat)
///   3. Dead code injection (decoy functions and statements)
///   4. Dot-to-bracket property access randomization
/// </summary>
public static class JScriptObfuscator
{
    private static readonly Random Rng = new();

    public static string Obfuscate(string jscript)
    {
        var parser = new JavaScriptParser(new ParserOptions { Tolerant = true });
        var ast = parser.ParseScript(jscript);

        // Pass 1: collect ALL declared names from the AST itself
        // (function names, var names, function params, catch params)
        // Anything NOT declared in the script is a built-in/global — left alone.
        var declCollector = new DeclarationCollector();
        declCollector.Visit(ast);

        // Build rename map — only names that have a declaration in this script
        var renameMap = new Dictionary<string, string>();
        foreach (var name in declCollector.UserDefined)
            renameMap[name] = RandVar();

        // Pass 2: collect all nodes that need replacement, with source ranges
        var refCollector = new ReferenceCollector(renameMap);
        refCollector.Visit(ast);

        // Build replacement list
        var replacements = new List<(int Start, int End, string NewText)>();

        // Identifier renames
        foreach (var node in refCollector.IdentifiersToRename)
            replacements.Add((node.Range.Start, node.Range.End, renameMap[node.Name]));

        // String literal encoding
        foreach (var lit in refCollector.StringLiterals)
        {
            if (lit.Value is not string s || s.Length == 0 || s.Length > 100)
                continue;
            var encoded = Rng.Next(2) == 0 ? HexEscape(s) : SplitConcat(s);
            replacements.Add((lit.Range.Start, lit.Range.End, encoded));
        }

        // Dot-to-bracket conversion (random subset)
        foreach (var (dotStart, end, propName) in refCollector.DotAccessSpans)
        {
            if (Rng.Next(3) == 0)
            {
                var encoded = HexEscape(propName);
                replacements.Add((dotStart, end, $"[{encoded}]"));
            }
        }

        // Sort reverse by start position, remove overlaps, apply
        replacements.Sort((a, b) => b.Start.CompareTo(a.Start));
        for (var i = replacements.Count - 1; i > 0; i--)
        {
            if (replacements[i].End > replacements[i - 1].Start)
                replacements.RemoveAt(i);
        }

        var sb = new StringBuilder(jscript);
        foreach (var (start, end, newText) in replacements)
            sb.Remove(start, end - start).Insert(start, newText);

        var code = sb.ToString();

        // Inject dead code
        code = InjectDeadCode(code);

        return code;
    }

    // ── Pass 1: Collect all declarations ─────────────────────────────────

    private sealed class DeclarationCollector : AstVisitor
    {
        public readonly HashSet<string> UserDefined = new();

        protected override object? VisitFunctionDeclaration(FunctionDeclaration node)
        {
            if (node.Id is { } id)
                UserDefined.Add(id.Name);
            foreach (var param in node.Params)
                if (param is Identifier pid)
                    UserDefined.Add(pid.Name);
            return base.VisitFunctionDeclaration(node);
        }

        protected override object? VisitVariableDeclarator(VariableDeclarator node)
        {
            if (node.Id is Identifier id)
                UserDefined.Add(id.Name);
            return base.VisitVariableDeclarator(node);
        }

        protected override object? VisitAssignmentExpression(AssignmentExpression node)
        {
            if (node.Left is Identifier id)
                UserDefined.Add(id.Name);
            return base.VisitAssignmentExpression(node);
        }

        protected override object? VisitCatchClause(CatchClause node)
        {
            if (node.Param is Identifier id)
                UserDefined.Add(id.Name);
            return base.VisitCatchClause(node);
        }

        protected override object? VisitFunctionExpression(FunctionExpression node)
        {
            foreach (var param in node.Params)
                if (param is Identifier pid)
                    UserDefined.Add(pid.Name);
            return base.VisitFunctionExpression(node);
        }
    }

    // ── Pass 2: Collect references to rename + literals to encode ────────

    private sealed class ReferenceCollector : AstVisitor
    {
        private readonly Dictionary<string, string> _renameMap;

        public readonly List<Identifier> IdentifiersToRename = new();
        public readonly List<Literal> StringLiterals = new();
        // (dotStart, rangeEnd, propertyName) for dot accesses on non-renamed objects
        public readonly List<(int DotStart, int End, string PropName)> DotAccessSpans = new();

        public ReferenceCollector(Dictionary<string, string> renameMap) => _renameMap = renameMap;

        protected override object? VisitIdentifier(Identifier node)
        {
            if (_renameMap.ContainsKey(node.Name))
                IdentifiersToRename.Add(node);
            return base.VisitIdentifier(node);
        }

        protected override object? VisitLiteral(Literal node)
        {
            if (node.Value is string)
                StringLiterals.Add(node);
            return base.VisitLiteral(node);
        }

        protected override object? VisitMemberExpression(MemberExpression node)
        {
            // Visit the object side normally (it may contain identifiers to rename)
            Visit(node.Object);

            if (!node.Computed && node.Property is Identifier prop)
            {
                // Don't visit the property as an identifier — it's a member name, not a variable.
                // Record it for possible dot→bracket conversion.
                var dotStart = node.Object.Range.End;
                DotAccessSpans.Add((dotStart, node.Range.End, prop.Name));
            }
            else
            {
                // Computed access — visit the property expression normally
                Visit(node.Property);
            }

            // Don't call base — we already visited children manually
            return node;
        }
    }

    // ── String Encoding ─────────────────────────────────────────────────

    private static string HexEscape(string s)
    {
        var sb = new StringBuilder(s.Length * 4 + 2);
        sb.Append('"');
        foreach (var c in s)
            sb.Append($"\\x{(int)c:x2}");
        sb.Append('"');
        return sb.ToString();
    }

    private static string SplitConcat(string s)
    {
        if (s.Length <= 3) return HexEscape(s);
        var parts = new List<string>();
        var i = 0;
        while (i < s.Length)
        {
            var n = Math.Min(Rng.Next(2, 5), s.Length - i);
            var chunk = s.Substring(i, n);
            var sb = new StringBuilder(n * 4 + 2);
            sb.Append('"');
            foreach (var c in chunk)
                sb.Append($"\\x{(int)c:x2}");
            sb.Append('"');
            parts.Add(sb.ToString());
            i += n;
        }
        return "(" + string.Join("+", parts) + ")";
    }

    // ── Dead Code Injection ─────────────────────────────────────────────

    private static string InjectDeadCode(string js)
    {
        var lines = js.Split('\n').ToList();
        var result = new List<string> { DecoyFunc(), "" };

        for (var i = 0; i < lines.Count; i++)
        {
            if (Rng.Next(5) == 0 && lines[i].Trim().Length > 0
                && !lines[i].TrimStart().StartsWith("function"))
            {
                result.Add(DeadStmt());
            }
            result.Add(lines[i]);
        }

        result.Add("");
        result.Add(DecoyFunc());
        return string.Join("\n", result);
    }

    private static string DecoyFunc()
    {
        var n = RandVar(); var p = RandVar(); var l = RandVar();
        return Rng.Next(4) switch
        {
            0 => $"function {n}({p}) {{ var {l} = {p} ^ {Rng.Next(1, 9999)}; return {l} + {Rng.Next(1, 100)}; }}",
            1 => $"function {n}({p}) {{ var {l} = []; for (var i = 0; i < {p}; i++) {l}.push(i * {Rng.Next(2, 50)}); return {l}; }}",
            2 => $"function {n}({p}) {{ if ({p} > {Rng.Next(10, 500)}) return {p} - {Rng.Next(1, 99)}; return {p} + {Rng.Next(1, 99)}; }}",
            _ => $"function {n}({p}) {{ return ({p} % {Rng.Next(2, 17)}) == 0 ? {p} : {p} + {Rng.Next(1, 50)}; }}"
        };
    }

    private static string DeadStmt()
    {
        var v = RandVar();
        return Rng.Next(5) switch
        {
            0 => $"var {v} = {Rng.Next(1000, 99999)};",
            1 => $"var {v} = ({Rng.Next(100)} > {Rng.Next(100)}) ? {Rng.Next(999)} : {Rng.Next(999)};",
            2 => $"var {v} = String.fromCharCode({Rng.Next(65, 91)}, {Rng.Next(65, 91)}, {Rng.Next(65, 91)});",
            3 => $"if (false) {{ var {v} = {Rng.Next(9999)}; }}",
            _ => $"var {v} = parseInt(\"{Rng.Next(100, 9999)}\", 10);"
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string RandVar()
    {
        var sb = new StringBuilder();
        sb.Append("_$"[Rng.Next(2)]);
        for (var i = 0; i < Rng.Next(6, 13); i++)
            sb.Append(Rng.Next(3) switch
            {
                0 => (char)('a' + Rng.Next(26)),
                1 => (char)('A' + Rng.Next(26)),
                _ => '_'
            });
        sb.Append(Rng.Next(100, 999));
        return sb.ToString();
    }
}
