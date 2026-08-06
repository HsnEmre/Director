namespace Director.Services;

internal static class SceneNegativePromptPolicy
{
    public const int MaxPromptCharacters = 420;

    private static readonly string[] CanonicalImageTerms =
    [
        "text",
        "subtitles",
        "watermark",
        "logo",
        "signature",
        "malformed anatomy",
        "extra limbs",
        "extra fingers",
        "duplicate character",
        "distorted face",
        "inconsistent clothing",
        "low resolution",
        "blurry"
    ];

    private static readonly string[] CanonicalVideoTerms =
    [
        "scene transition",
        "sudden camera jump",
        "identity change",
        "face morphing",
        "clothing change",
        "duplicated limbs",
        "extra characters",
        "flickering",
        "background warping",
        "object teleportation",
        "extreme motion",
        "camera shake",
        "text",
        "subtitles",
        "watermark",
        "logo"
    ];

    public static string SanitizeImage(string? modelValue) =>
        Sanitize(modelValue, CanonicalImageTerms);

    public static string SanitizeVideo(string? modelValue) =>
        Sanitize(modelValue, CanonicalVideoTerms);

    private static string Sanitize(string? modelValue, IReadOnlyList<string> canonicalTerms)
    {
        var terms = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddTerms(canonicalTerms, terms, seen);
        AddTerms(SplitTerms(modelValue), terms, seen);

        var selected = new List<string>();
        foreach (var term in terms)
        {
            var candidate = selected.Count == 0 ? term : string.Join(", ", selected.Append(term));
            if (candidate.Length > MaxPromptCharacters)
            {
                continue;
            }

            selected.Add(term);
        }

        return string.Join(", ", selected.Count == 0 ? canonicalTerms : selected);
    }

    private static void AddTerms(IEnumerable<string> candidates, List<string> terms, HashSet<string> seen)
    {
        foreach (var candidate in candidates)
        {
            var term = NormalizeTerm(candidate);
            if (term.Length == 0 || term.Length > 80)
            {
                continue;
            }

            if (seen.Add(term))
            {
                terms.Add(term);
            }
        }
    }

    private static IEnumerable<string> SplitTerms(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        foreach (var term in value.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return term;
        }
    }

    private static string NormalizeTerm(string value)
    {
        var cleaned = value.Trim().Trim('.', ',', ';', ':', '-', '–', '—', '"', '\'');
        while (cleaned.Contains("  ", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("  ", " ", StringComparison.Ordinal);
        }

        return cleaned;
    }
}
