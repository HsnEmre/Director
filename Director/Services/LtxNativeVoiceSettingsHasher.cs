using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Director.Models;

namespace Director.Services;

public static class LtxNativeVoiceSettingsHasher
{
    public static string Compute(LtxNativeVoiceProfile profile)
    {
        var source = new
        {
            profile.VoiceDescription,
            profile.Language,
            profile.SpeakingStyle,
            profile.PerceivedAge,
            profile.GenderPresentation,
            profile.AccentDescription,
            profile.PitchDescription,
            profile.TempoDescription
        };
        var json = JsonSerializer.Serialize(source, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}
