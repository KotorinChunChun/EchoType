using EchoType.Core;
using EchoType.Windows;

namespace EchoType.App;

public sealed partial class VoiceInputController
{
    private VoiceInputSettings _commonSettings = new();
    private VoiceApplicationProfile? _profile;
    private VoiceApplicationProfile[] _profiles = [];
    private PersonalWord[] _words = [];
    private string _application = "";
    private bool _personalizationChanged;
    public string TargetApplicationPath => _target?.ApplicationPath ?? _lastExternal?.ApplicationPath ?? _application;
    public string ProfileName => _profile?.Name ?? "共通設定";
    internal SecureStore PersonalizationStore => _store;
    private VoiceInputSettings Effective(VoiceInputSettings settings)
    {
        var enriched = VoicePersonalization.Enrich(settings, _store.LoadSettings(), _words, _application);
        return enriched with { Recognition = _liveAsr.EffectiveSettings(enriched.Recognition) };
    }
    public string EngineRestartNotice => _liveAsr.Runtime.VoiceEngineSettings?.Notice(Settings.Recognition with { Performance = _store.LoadSettings().Performance }) ?? "";
    private void PersistSettings(VoiceInputSettings settings)
    {
        settings.Validate();
        if (_profile is not null)
        {
            _profile = _profile with { Settings = settings with { PersonalCorrections = "[]", ExternalSpeech = new() } };
            _store.SaveVoiceProfile(_profile);
            _profiles = _profiles.Where(p => !string.Equals(p.ApplicationPath, _profile.ApplicationPath, StringComparison.OrdinalIgnoreCase)).Append(_profile).ToArray();
            _commonSettings = new VoiceApplicationProfile("", "", _commonSettings).Apply(settings) with { PersonalCorrections = "[]" };
            _store.SaveVoiceInputSettings(_commonSettings);
        }
        else { _commonSettings = settings with { PersonalCorrections = "[]" }; _store.SaveVoiceInputSettings(_commonSettings); }
        Settings = settings;
    }
    private void SelectApplication(string application)
    {
        if (!_personalizationChanged && string.Equals(_application, application, StringComparison.OrdinalIgnoreCase)) return;
        if ((_session?.Entries.Count ?? 0) > 0 || !string.IsNullOrEmpty(_window?.DraftText)) return;
        _application = application;
        _personalizationChanged = false;
        _profile = _profiles.FirstOrDefault(p => string.Equals(p.ApplicationPath, application, StringComparison.OrdinalIgnoreCase));
        Settings = _profile?.Apply(_commonSettings) ?? _commonSettings;
        _window?.ShowProfileSettings(Settings);
    }
    public bool CanEditPersonalization => !ConfigurationLocked;
    public void RefreshPersonalization()
    {
        _profiles = _store.GetVoiceProfiles(); _words = _store.GetPersonalWords();
        _personalizationChanged = true;
        if (ConfigurationLocked || (_session?.Entries.Count ?? 0) > 0 || !string.IsNullOrEmpty(_window?.DraftText)) return;
        _profile = _profiles.FirstOrDefault(p => string.Equals(p.ApplicationPath, _application, StringComparison.OrdinalIgnoreCase));
        Settings = _profile?.Apply(_commonSettings) ?? _commonSettings;
        _personalizationChanged = false;
        _window?.ShowProfileSettings(Settings);
    }
}
