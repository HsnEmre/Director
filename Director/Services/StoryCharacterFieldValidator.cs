using System.Globalization;
using System.Text;
using Director.Dtos.StoryGeneration;

namespace Director.Services;

public sealed class StoryCharacterValidationException : InvalidOperationException
{
    public StoryCharacterValidationException(IReadOnlyList<StoryCharacterValidationIssue> issues)
        : base("Karakter alanlari dogrulanamadi. Hikaye veritabanina kaydedilmedi.")
    {
        Issues = issues;
        TechnicalDetails = string.Join(" | ", issues.Select(issue =>
            $"CharacterIndex={issue.CharacterIndex}; Field={issue.FieldName}; ActualLength={issue.ActualLength}; MaxLength={issue.MaxLength}; Reason={issue.Reason}"));
    }

    public IReadOnlyList<StoryCharacterValidationIssue> Issues { get; }
    public string TechnicalDetails { get; }
}

public sealed record StoryCharacterValidationIssue(
    int CharacterIndex,
    string CharacterKey,
    string FieldName,
    int ActualLength,
    int MaxLength,
    string Reason);

public static class StoryCharacterFieldValidator
{
    public const int CharacterKeyMaxLength = 80;
    public const int NameMaxLength = 160;
    public const int RoleMaxLength = 80;

    private static readonly string[] AppearanceTerms =
    {
        "hair", "eye", "face", "skin", "beard", "scar", "tall", "short", "slender", "broad", "appearance",
        "sac", "goz", "yuz", "cilt", "sakal", "yara", "uzun boy", "kisa boy", "gorunum", "fiziksel"
    };

    private static readonly string[] ClothingTerms =
    {
        "wear", "wears", "worn", "clothing", "coat", "robe", "armor", "armour", "leather", "fur", "helmet", "boots", "belt", "cloak", "sword", "equipment",
        "giy", "kiyafet", "kurk", "zirh", "deri", "migfer", "cizme", "kemer", "pelerin", "kilic", "ekipman"
    };

    public static void Validate(StoryBibleResponse bible)
    {
        var issues = ValidateIssues(bible);
        if (issues.Count > 0)
        {
            throw new StoryCharacterValidationException(issues);
        }
    }

    public static List<StoryCharacterValidationIssue> ValidateIssues(StoryBibleResponse bible)
    {
        var issues = new List<StoryCharacterValidationIssue>();
        if (bible.Characters.Count == 0)
        {
            return issues;
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var character in bible.Characters.Select((value, index) => new { value, index }))
        {
            ValidateRequired(issues, character.index, character.value.CharacterKey, "characterKey", character.value.CharacterKey, CharacterKeyMaxLength);
            ValidateRequired(issues, character.index, character.value.CharacterKey, "name", character.value.Name, NameMaxLength);
            ValidateRequired(issues, character.index, character.value.CharacterKey, "role", character.value.Role, RoleMaxLength);
            ValidateRequired(issues, character.index, character.value.CharacterKey, "physicalDescription", character.value.PhysicalDescription, int.MaxValue);
            ValidateRequired(issues, character.index, character.value.CharacterKey, "clothingDescription", character.value.ClothingDescription, int.MaxValue);

            var key = character.value.CharacterKey.Trim();
            if (!string.IsNullOrWhiteSpace(key) && !keys.Add(key))
            {
                issues.Add(new StoryCharacterValidationIssue(character.index, key, "characterKey", key.Length, CharacterKeyMaxLength, "Duplicate characterKey."));
            }

            ValidateRoleShape(issues, character.index, character.value.CharacterKey, character.value.Role);
        }

        return issues;
    }

    private static void ValidateRequired(
        List<StoryCharacterValidationIssue> issues,
        int index,
        string characterKey,
        string fieldName,
        string value,
        int maxLength)
    {
        var length = value.Trim().Length;
        if (length == 0)
        {
            issues.Add(new StoryCharacterValidationIssue(index, characterKey, fieldName, length, maxLength, "Required field is empty."));
        }
        else if (length > maxLength)
        {
            issues.Add(new StoryCharacterValidationIssue(index, characterKey, fieldName, length, maxLength, "Field exceeds configured maximum length."));
        }
    }

    private static void ValidateRoleShape(List<StoryCharacterValidationIssue> issues, int index, string characterKey, string role)
    {
        var trimmed = role.Trim();
        var normalized = Normalize(trimmed);
        var wordCount = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount > 4 || trimmed.Contains('.') || trimmed.Contains(',') || trimmed.Contains(';'))
        {
            issues.Add(new StoryCharacterValidationIssue(index, characterKey, "role", trimmed.Length, RoleMaxLength, "Role must be a short narrative function, not a sentence or paragraph."));
        }

        if (AppearanceTerms.Any(term => normalized.Contains(term, StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new StoryCharacterValidationIssue(index, characterKey, "role", trimmed.Length, RoleMaxLength, "Role contains appearance details that belong in physicalDescription."));
        }

        if (ClothingTerms.Any(term => normalized.Contains(term, StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new StoryCharacterValidationIssue(index, characterKey, "role", trimmed.Length, RoleMaxLength, "Role contains clothing/equipment details that belong in clothingDescription."));
        }
    }

    private static string Normalize(string value)
    {
        var formD = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(formD.Length);
        foreach (var ch in formD)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(ch);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
