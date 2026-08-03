using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Director.Models;

namespace Director.Services;

public static class AudioVoiceSettingsHasher
{
    public static string Compute(CharacterVoiceProfile profile)
    {
        var source = new
        {
            profile.ModelType,
            profile.VoicePresetKey,
            profile.Seed,
            profile.CfgScale,
            profile.DoSample,
            profile.Temperature,
            profile.MaxNewTokens,
            profile.Language
        };
        var json = JsonSerializer.Serialize(source, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}
