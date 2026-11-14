using EchoType.Core;

namespace EchoType.Windows;

public sealed partial class SecureStore
{
    public VoiceApplicationProfile[] GetVoiceProfiles() => Read<VoiceApplicationProfile>("voice-profile", 1000).Select(p => p with { Settings = p.Settings.MigrateSelections() }).OrderBy(p => p.Name).ToArray();
    public void SaveVoiceProfile(VoiceApplicationProfile profile)
    {
        profile = profile with { Settings = profile.Settings.MigrateSelections() };
        profile.Validate(); Put("voice-profile", profile.ApplicationPath.ToUpperInvariant(), profile, DateTimeOffset.UtcNow);
    }
    public void DeleteVoiceProfile(string path) => Delete("voice-profile", path.ToUpperInvariant());
    public PersonalWord[] GetPersonalWords() => Read<PersonalWord>("personal-word", 2000).OrderBy(w => w.Correct).ToArray();
    public void SavePersonalWord(PersonalWord word) { word.Validate(); Put("personal-word", word.Id, word, DateTimeOffset.UtcNow); }
    public void DeletePersonalWord(string id) => Delete("personal-word", id);
}
