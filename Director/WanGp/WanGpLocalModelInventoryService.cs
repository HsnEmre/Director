using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.IO;
using Director.Options;
using Director.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace Director.WanGp;

public sealed class WanGpLocalModelInventoryService : IWanGpLocalModelInventoryService
{
    private static readonly string[] ModelExtensions = [".safetensors", ".gguf", ".bin", ".pt", ".pth"];
    private readonly WanGpOptions _options;
    private IReadOnlyDictionary<string, WanGpLocalModelInventoryItem>? _cache;
    private DateTime _cacheAt;
    private string _cacheKey = string.Empty;

    public WanGpLocalModelInventoryService(IOptions<WanGpOptions> options)
    {
        _options = options.Value;
    }

    public Task<IReadOnlyDictionary<string, WanGpLocalModelInventoryItem>> GetInventoryAsync(
        IReadOnlyList<WanGpModelInfo> models,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var requestedKey = string.Join("|", models.Select(model => model.ModelType).OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        if (!forceRefresh &&
            _cache is not null &&
            string.Equals(_cacheKey, requestedKey, StringComparison.OrdinalIgnoreCase) &&
            DateTime.Now - _cacheAt < TimeSpan.FromMinutes(2))
        {
            return Task.FromResult(_cache);
        }

        var result = new Dictionary<string, WanGpLocalModelInventoryItem>(StringComparer.OrdinalIgnoreCase);
        var root = Path.GetFullPath(_options.RootPath);
        var files = EnumerateModelFiles(root, cancellationToken);
        foreach (var model in models.Where(model => !string.IsNullOrWhiteSpace(model.ModelType)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result[model.ModelType] = MatchModel(model, files, root);
        }

        _cache = result;
        _cacheAt = DateTime.Now;
        _cacheKey = requestedKey;
        return Task.FromResult<IReadOnlyDictionary<string, WanGpLocalModelInventoryItem>>(result);
    }

    private static List<FileInfo> EnumerateModelFiles(string root, CancellationToken cancellationToken)
    {
        var allowedRoots = new[]
        {
            Path.Combine(root, "ckpts"),
            Path.Combine(root, "models"),
            Path.Combine(root, "loras"),
            Path.Combine(root, "finetunes")
        };

        var files = new List<FileInfo>();
        foreach (var directory in allowedRoots.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new FileInfo(file);
                if (info.Length > 0 && ModelExtensions.Contains(info.Extension, StringComparer.OrdinalIgnoreCase))
                {
                    files.Add(info);
                }
            }
        }

        return files;
    }

    private static WanGpLocalModelInventoryItem MatchModel(WanGpModelInfo model, IReadOnlyList<FileInfo> files, string root)
    {
        var modelKey = Normalize(model.ModelType);
        var displayKey = Normalize(model.DisplayName);
        var exact = files.FirstOrDefault(file => Normalize(Path.GetFileName(file.Name)).Contains(modelKey));
        if (exact is not null)
        {
            return Installed(model.ModelType, exact, "checkpoint filename matched model_type");
        }

        var defaultMatch = MatchDefaultConfig(model, files, root);
        if (defaultMatch is not null)
        {
            return defaultMatch;
        }

        if (!string.IsNullOrWhiteSpace(displayKey))
        {
            var displayMatch = files.FirstOrDefault(file => Normalize(Path.GetFileName(file.Name)).Contains(displayKey));
            if (displayMatch is not null)
            {
                return Installed(model.ModelType, displayMatch, "checkpoint filename matched display name");
            }
        }

        var tokens = BuildMatchTokens(modelKey, model.DisplayName).ToArray();
        var partial = tokens.Length > 0
            ? files.FirstOrDefault(file =>
            {
                var filename = Normalize(Path.GetFileName(file.Name));
                var matches = tokens.Count(filename.Contains);
                return matches >= Math.Max(2, tokens.Length - 1);
            })
            : null;

        if (partial is not null)
        {
            return new WanGpLocalModelInventoryItem
            {
                ModelType = model.ModelType,
                Status = WanGpModelInstallStatus.Partial,
                CheckpointPath = partial.FullName,
                Evidence = "partial checkpoint filename match",
                CheckedAt = DateTime.Now
            };
        }

        return new WanGpLocalModelInventoryItem
        {
            ModelType = model.ModelType,
            Status = WanGpModelInstallStatus.Missing,
            Evidence = "no checkpoint evidence under configured WanGP root",
            CheckedAt = DateTime.Now
        };
    }

    private static WanGpLocalModelInventoryItem? MatchDefaultConfig(WanGpModelInfo model, IReadOnlyList<FileInfo> files, string root)
    {
        var defaultsPath = Path.Combine(root, "defaults", $"{model.ModelType}.json");
        if (!File.Exists(defaultsPath))
        {
            return null;
        }

        try
        {
            var json = JsonNode.Parse(File.ReadAllText(defaultsPath));
            var candidateNames = ExtractPrimaryCandidateFileNames(json).ToArray();
            foreach (var candidateName in candidateNames)
            {
                var match = files.FirstOrDefault(file => string.Equals(file.Name, candidateName, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    return Installed(model.ModelType, match, $"default config URL matched {Path.GetFileName(defaultsPath)}");
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static IEnumerable<string> ExtractPrimaryCandidateFileNames(JsonNode? node)
    {
        if (node is JsonObject root &&
            root.TryGetPropertyValue("model", out var modelNode) &&
            modelNode is JsonObject modelObject &&
            modelObject.TryGetPropertyValue("URLs", out var urlsNode))
        {
            return ExtractCandidateFileNames(urlsNode).Distinct(StringComparer.OrdinalIgnoreCase);
        }

        return ExtractCandidateFileNames(node).Distinct(StringComparer.OrdinalIgnoreCase);
    }
    private static IEnumerable<string> ExtractCandidateFileNames(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                foreach (var value in ExtractCandidateFileNames(pair.Value))
                {
                    yield return value;
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                foreach (var value in ExtractCandidateFileNames(item))
                {
                    yield return value;
                }
            }
        }
        else if (node is not null)
        {
            var value = node.ToString();
            foreach (var extension in ModelExtensions)
            {
                var extensionIndex = value.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
                if (extensionIndex < 0)
                {
                    continue;
                }

                var end = extensionIndex + extension.Length;
                var start = Math.Max(value.LastIndexOf('/', Math.Max(0, extensionIndex - 1)), value.LastIndexOf('\\', Math.Max(0, extensionIndex - 1))) + 1;
                if (start < end)
                {
                    yield return Uri.UnescapeDataString(value[start..end]);
                }
            }
        }
    }

    private static IEnumerable<string> BuildMatchTokens(string modelKey, string displayName)
    {
        foreach (var token in modelKey.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Length >= 3)
            {
                yield return token;
            }
        }

        var displayKey = Normalize(displayName);
        foreach (var token in displayKey.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Length >= 4 && token is not "video" and not "image" and not "light")
            {
                yield return token;
            }
        }
    }

    private static WanGpLocalModelInventoryItem Installed(string modelType, FileInfo file, string evidence)
    {
        return new WanGpLocalModelInventoryItem
        {
            ModelType = modelType,
            Status = WanGpModelInstallStatus.Installed,
            CheckpointPath = file.FullName,
            Evidence = evidence,
            CheckedAt = DateTime.Now
        };
    }

    private static string Normalize(string value)
    {
        return Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
    }
}

