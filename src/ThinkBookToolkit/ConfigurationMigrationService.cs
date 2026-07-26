using System;
using System.IO;
using System.Windows;

namespace ThinkBookToolkit;

internal static class ConfigurationMigrationService
{
    private static string LegacyProfilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".thinkbook_fan_control",
        "fan_curve_profiles.csharp.json");

    private static string LegacySettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".thinkbook_fan_control",
        "app_settings.csharp.json");

    public static void EnsureInitialized()
    {
        var hasToolkitConfiguration =
            File.Exists(CurveProfileStore.ProfilePath) ||
            File.Exists(CurveProfileStore.SettingsPath);
        if (hasToolkitConfiguration)
        {
            EnsureMissingFilesUseDefaults();
            return;
        }

        var hasLegacyConfiguration =
            File.Exists(LegacyProfilePath) ||
            File.Exists(LegacySettingsPath);
        var migrate = hasLegacyConfiguration &&
                      MessageBox.Show(
                          "检测到 ThinkBook Fan Control 配置，是否迁移到 ThinkBook Toolkit？\n\n" +
                          "选择“否”将使用 Toolkit 默认配置。",
                          "迁移配置",
                          MessageBoxButton.YesNo,
                          MessageBoxImage.Question) == MessageBoxResult.Yes;

        if (migrate)
        {
            CopyIfPresent(
                LegacyProfilePath,
                CurveProfileStore.ProfilePath);
            CopyIfPresent(
                LegacySettingsPath,
                CurveProfileStore.SettingsPath);
        }

        EnsureMissingFilesUseDefaults();
    }

    private static void EnsureMissingFilesUseDefaults()
    {
        var profileReady = CopyIfPresent(
            CurveProfileStore.DefaultProfilePath,
            CurveProfileStore.ProfilePath,
            onlyWhenMissing: true);
        var settingsReady = CopyIfPresent(
            CurveProfileStore.DefaultSettingsPath,
            CurveProfileStore.SettingsPath,
            onlyWhenMissing: true);

        if (!profileReady)
            CurveProfileStore.WriteBuiltInProfiles();
        if (!settingsReady)
            CurveProfileStore.WriteBuiltInSettings();
    }

    private static bool CopyIfPresent(
        string source,
        string destination,
        bool onlyWhenMissing = false)
    {
        if (onlyWhenMissing && File.Exists(destination))
            return true;
        if (!File.Exists(source))
            return false;

        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.Copy(source, destination, overwrite: true);
        return true;
    }
}
