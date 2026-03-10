using cc.Features.Relay;
using cc.Features.Agents;

namespace cc.Infrastructure;

public static class Formatters
{
    public static string FormatTimestamp(double ms)
    {
        if (ms <= 0) return "-";
        return DateTimeOffset.FromUnixTimeMilliseconds((long)ms).LocalDateTime.ToString("g");
    }

    public static string FormatLocation(string? city, string? region, string? country, string fallback = "-")
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(city)) parts.Add(city);
        if (!string.IsNullOrEmpty(region) && region != city) parts.Add(region);
        if (!string.IsNullOrEmpty(country)) parts.Add(country);
        return parts.Count > 0 ? string.Join(", ", parts) : fallback;
    }

    public static string FormatLocation(AgentConnection c) =>
        FormatLocation(c.City, c.Region, c.Country);

    public static string FormatLocation(AgentRecord r) =>
        FormatLocation(r.City, r.Region, r.Country, "\u2014");

    public static string FormatListenerLocation(EventListenerConnection c)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(c.City)) parts.Add(c.City);
        if (!string.IsNullOrEmpty(c.Country)) parts.Add(c.Country);
        return parts.Count > 0 ? string.Join(", ", parts) : "-";
    }

    public static string FormatNetwork(int asn, string? asOrganization, string fallback = "-")
    {
        if (asn > 0 && !string.IsNullOrEmpty(asOrganization))
            return $"AS{asn} ({asOrganization})";
        if (asn > 0)
            return $"AS{asn}";
        return fallback;
    }

    public static string FormatNetwork(AgentConnection c) =>
        FormatNetwork(c.Asn, c.AsOrganization);

    public static string FormatNetwork(AgentRecord r) =>
        FormatNetwork(r.Asn, r.AsOrganization, "\u2014");

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

    public static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec < 1024) return $"{bytesPerSec:F0} B/s";
        if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024.0:F1} KB/s";
        if (bytesPerSec < 1024 * 1024 * 1024) return $"{bytesPerSec / (1024.0 * 1024):F1} MB/s";
        return $"{bytesPerSec / (1024.0 * 1024 * 1024):F1} GB/s";
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
