using EchoType.Core;

namespace EchoType.Windows;

public sealed partial class SecureStore
{
    public VoiceInputSettings LoadVoiceInputSettings()
    {
        var settings = Read<VoiceInputSettings>("voice-input-settings", 1).FirstOrDefault() ?? new();
        if (settings.FormattingPrompt == VoiceInputSettings.PreviousFormattingPrompt)
        {
            settings = settings with { FormattingPrompt = VoiceInputSettings.DefaultFormattingPrompt };
            SaveVoiceInputSettings(settings);
        }
        return settings.MigrateSelections();
    }
    public void SaveVoiceInputSettings(VoiceInputSettings settings)
    {
        settings = settings.MigrateSelections();
        settings.Validate(); Put("voice-input-settings", "current", settings, DateTimeOffset.UtcNow);
    }
}
