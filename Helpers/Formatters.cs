using cc.Models;

namespace cc.Helpers;

public static class Formatters
{
    public static string FormatTimestamp(double ms)
    {
        if (ms <= 0) return "-";
        return DateTimeOffset.FromUnixTimeMilliseconds((long)ms).LocalDateTime.ToString("g");
    }

    public static string FormatLocation(AgentConnection c)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(c.City)) parts.Add(c.City);
        if (!string.IsNullOrEmpty(c.Region) && c.Region != c.City) parts.Add(c.Region);
        if (!string.IsNullOrEmpty(c.Country)) parts.Add(c.Country);
        return parts.Count > 0 ? string.Join(", ", parts) : "-";
    }

    public static string FormatListenerLocation(EventListenerConnection c)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(c.City)) parts.Add(c.City);
        if (!string.IsNullOrEmpty(c.Country)) parts.Add(c.Country);
        return parts.Count > 0 ? string.Join(", ", parts) : "-";
    }

    public static string FormatNetwork(AgentConnection c)
    {
        if (c.Asn > 0 && !string.IsNullOrEmpty(c.AsOrganization))
            return $"AS{c.Asn} ({c.AsOrganization})";
        if (c.Asn > 0)
            return $"AS{c.Asn}";
        return "-";
    }

    public static string FormatCoords(AgentConnection c)
    {
        if (!string.IsNullOrEmpty(c.Latitude) && !string.IsNullOrEmpty(c.Longitude))
            return $"{c.Latitude}, {c.Longitude}";
        return "";
    }

    public static string FormatSize(ulong bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }

    public static string FormatHexDump(byte[] data)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < data.Length; i += 16)
        {
            sb.Append($"{i:X8}  ");
            var end = Math.Min(i + 16, data.Length);
            for (int j = i; j < end; j++)
            {
                sb.Append($"{data[j]:X2} ");
                if (j == i + 7) sb.Append(' ');
            }
            for (int j = end; j < i + 16; j++)
            {
                sb.Append("   ");
                if (j == i + 7) sb.Append(' ');
            }
            sb.Append(" |");
            for (int j = i; j < end; j++)
            {
                var c = (char)data[j];
                sb.Append(c >= 32 && c < 127 ? c : '.');
            }
            sb.AppendLine("|");
        }
        return sb.ToString();
    }
}
