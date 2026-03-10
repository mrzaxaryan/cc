namespace cc.Features.FileManager;

/// <summary>
/// A single directory entry parsed from the agent's binary directory-listing response.
/// Each entry is a fixed-size 545-byte block laid out as:
///   Bytes 0–511   : Name (up to 256 UTF-16 chars)
///   Bytes 512–519 : CreationTime (uint64, Windows FILETIME)
///   Bytes 520–527 : LastModifiedTime (uint64, Windows FILETIME)
///   Bytes 528–535 : Size in bytes (uint64)
///   Bytes 536–539 : Type (uint32, file-system type attribute)
///   Byte  540     : IsDirectory flag
///   Byte  541     : IsDrive flag
///   Byte  542     : IsHidden flag
///   Byte  543     : IsSystem flag
///   Byte  544     : IsReadOnly flag
/// </summary>
public record DirEntry(
    string Name,
    ulong CreationTime,
    ulong LastModifiedTime,
    ulong Size,
    /// <summary>File-system type attribute (e.g. FILE_ATTRIBUTE_* flags).</summary>
    uint Type,
    bool IsDirectory,
    bool IsDrive,
    bool IsHidden,
    bool IsSystem,
    bool IsReadOnly)
{
    /// <summary>Fixed byte size of one serialized directory entry.</summary>
    private const int EntrySize = 545;

    public static (List<DirEntry> entries, string? error) ParseDirectoryResponse(byte[] response)
    {
        if (response.Length < 4)
            return ([], "Invalid response");

        var status = BitConverter.ToUInt32(response, 0);
        if (status != 0)
            return ([], $"Error listing directory (status={status})");

        if (response.Length < 12)
            return ([], "Invalid response");

        var entryCount = BitConverter.ToUInt64(response, 4);
        var list = new List<DirEntry>();
        var offset = 12;
        for (ulong i = 0; i < entryCount && offset + EntrySize <= response.Length; i++)
        {
            var entry = Parse(response, offset);
            offset += EntrySize;
            if (!string.IsNullOrEmpty(entry.Name))
                list.Add(entry);
        }
        return (list, null);
    }

    public static DirEntry Parse(byte[] data, int offset)
    {
        var nameChars = new char[256];
        int nameLen = 0;
        for (int i = 0; i < 256; i++)
        {
            var ch = BitConverter.ToChar(data, offset + i * 2);
            if (ch == '\0') break;
            nameChars[nameLen++] = ch;
        }
        var name = new string(nameChars, 0, nameLen);

        var creationTime = BitConverter.ToUInt64(data, offset + 512);
        var lastModifiedTime = BitConverter.ToUInt64(data, offset + 520);
        var size = BitConverter.ToUInt64(data, offset + 528);
        var type = BitConverter.ToUInt32(data, offset + 536);
        var isDirectory = data[offset + 540] != 0;
        var isDrive = data[offset + 541] != 0;
        var isHidden = data[offset + 542] != 0;
        var isSystem = data[offset + 543] != 0;
        var isReadOnly = data[offset + 544] != 0;

        return new DirEntry(name, creationTime, lastModifiedTime, size, type, isDirectory, isDrive, isHidden, isSystem, isReadOnly);
    }
}
