using System.Text.Json;
using System.Windows;
using ThinkBookToolkit.PluginApi;

namespace ThinkBookToolkit.PluginUi;

public sealed record PluginPageState(PluginRequest Request, bool IsDark, string Language);

/// <summary>Trusted, in-process WPF page. Calls occur on the UI thread.
/// Dispose must stop timers and unsubscribe events; it is not an assembly unload.</summary>
public interface IToolkitPluginPage : IDisposable
{
    FrameworkElement CreateView(IPluginPageContext context);
    void Update(PluginPageState state);
}

public interface IPluginPageContext
{
    string PluginId { get; }
    string PageId { get; }
    string PluginDirectory { get; }
    PluginPageState State { get; }
    CancellationToken Lifetime { get; }
    /// <summary>Writes a declared setting belonging to this plugin only.</summary>
    Task SetSettingAsync(string id, JsonElement value);
    /// <summary>Uses the existing host.control permission and host validation.</summary>
    Task ExecuteAsync(HostCommand command);
    /// <summary>Requires ui.toast. False means the request was rate-limited.
    /// Submission does not bring a hidden Toolkit window to the foreground.</summary>
    Task<bool> ShowToastAsync(string message, bool isError = false) =>
        Task.FromException<bool>(new NotSupportedException("This host does not support plugin toasts."));
}
