using System.Data.Common;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Director.Services;

public sealed record DatabaseIdentity(string Provider, string Hash)
{
    public string ShortHash => Hash[..Math.Min(12, Hash.Length)];

    public static DatabaseIdentity Create(string? providerName, string connectionString)
    {
        var provider = Normalize(providerName ?? "unknown");
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        string normalizedIdentity;

        if (provider.Contains("sqlserver", StringComparison.Ordinal))
        {
            normalizedIdentity = $"server={Normalize(Read(builder, "Data Source", "Server", "Address", "Addr", "Network Address"))};database={Normalize(Read(builder, "Initial Catalog", "Database"))}";
        }
        else if (provider.Contains("sqlite", StringComparison.Ordinal))
        {
            var source = Read(builder, "Data Source", "Filename");
            var normalizedPath = source is ":memory:" or ""
                ? Normalize(source)
                : Normalize(Path.GetFullPath(Environment.ExpandEnvironmentVariables(source)));
            normalizedIdentity = $"path={normalizedPath}";
        }
        else
        {
            var safeParts = new[]
                {
                    ("server", Read(builder, "Data Source", "Server", "Host")),
                    ("database", Read(builder, "Initial Catalog", "Database", "Filename")),
                    ("port", Read(builder, "Port"))
                }
                .Where(item => !string.IsNullOrWhiteSpace(item.Item2))
                .Select(item => $"{item.Item1}={Normalize(item.Item2)}");
            normalizedIdentity = string.Join(";", safeParts);
        }

        var material = $"{provider}|{normalizedIdentity}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return new DatabaseIdentity(provider, hash);
    }

    private static string Read(DbConnectionStringBuilder builder, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (builder.TryGetValue(key, out var value) && value is not null)
            {
                return value.ToString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static string Normalize(string value) => value.Trim().Replace('\\', '/').ToLowerInvariant();
}
