using System;
using ThinkBookToolkit.FanBackend;

namespace ThinkBookToolkit;

internal sealed record PendingFanBackendStartupNotice(
    string BackendIdentity,
    string Title,
    string Content);

internal static class FanBackendStartupNoticePreference
{
    public static bool ReconcileBackend(
        AppSettings settings,
        string backendIdentity)
    {
        if (string.IsNullOrWhiteSpace(backendIdentity) ||
            string.Equals(
                settings.LastFanBackendIdentity,
                backendIdentity,
                StringComparison.Ordinal))
        {
            return false;
        }

        settings.LastFanBackendIdentity = backendIdentity;
        settings.SuppressedFanBackendStartupNoticeIdentity = string.Empty;
        return true;
    }

    public static PendingFanBackendStartupNotice? GetPending(
        AppSettings settings,
        string backendIdentity,
        FanBackendStartupNotice? notice,
        string language)
    {
        if (notice is null ||
            string.IsNullOrWhiteSpace(backendIdentity) ||
            string.Equals(
                settings.SuppressedFanBackendStartupNoticeIdentity,
                backendIdentity,
                StringComparison.Ordinal))
        {
            return null;
        }

        var localized = notice.Resolve(language);
        if (string.IsNullOrWhiteSpace(localized.Title) ||
            string.IsNullOrWhiteSpace(localized.Content))
        {
            return null;
        }

        return new PendingFanBackendStartupNotice(
            backendIdentity,
            localized.Title,
            localized.Content);
    }

    public static bool Suppress(
        AppSettings settings,
        string backendIdentity)
    {
        if (string.IsNullOrWhiteSpace(backendIdentity) ||
            string.Equals(
                settings.SuppressedFanBackendStartupNoticeIdentity,
                backendIdentity,
                StringComparison.Ordinal))
        {
            return false;
        }

        settings.LastFanBackendIdentity = backendIdentity;
        settings.SuppressedFanBackendStartupNoticeIdentity =
            backendIdentity;
        return true;
    }
}
