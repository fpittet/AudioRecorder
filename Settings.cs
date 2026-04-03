using System.Configuration;

namespace AudioRecorder.Properties
{
    [SettingsGroupName("AudioRecorder")]
    internal sealed class Settings : ApplicationSettingsBase
    {
        private static readonly Settings _default = (Settings)Synchronized(new Settings());

        public static Settings Default => _default;

        [UserScopedSetting]
        [DefaultSettingValue("")]
        public string LastSaveDirectory
        {
            get => (string)this[nameof(LastSaveDirectory)];
            set => this[nameof(LastSaveDirectory)] = value;
        }
    }
}
