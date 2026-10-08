using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ThinkBookToolkit.FanBackend;
using ThinkBookToolkit.PluginApi;
using ThinkBookToolkit.PluginUi;

namespace ThinkBookToolkit;

internal sealed class ToolkitCustomPluginPage : ToolkitPageBase
{
    private readonly PluginInstallation _plugin;
    private readonly PageContext _context;
    private IToolkitPluginPage? _page;
    private bool _disposed, _failed, _updating;

    internal ToolkitCustomPluginPage(ToolkitRuntimeService runtime, PluginInstallation plugin, PluginPage page) : base(runtime)
    {
        _plugin = plugin;
        _context = new PageContext(this, page.Id);
        try
        {
            _context.EnsureActive();
            if (page.View is null || !plugin.Manifest.Permissions.Contains("ui.custom"))
                throw new UnauthorizedAccessException("Custom pages require ui.custom permission.");
            _context.Refresh();
            _page = PluginUiLoader.Create(plugin, page.View);
            var view = _page.CreateView(_context) ?? throw new InvalidOperationException("The plugin returned no view.");
            if (view.Parent is not null) throw new InvalidOperationException("The plugin view already belongs to another page.");
            Content = view;
            runtime.SnapshotChanged += OnSnapshotChanged;
            OnSnapshotChanged(this, EventArgs.Empty);
        }
        catch (Exception ex) { Fail(ex); }
    }
    private void OnSnapshotChanged(object? sender, EventArgs args)
    {
        if (_disposed || _failed || _updating || Runtime.Plugins.IsSuspendedForExit) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => OnSnapshotChanged(sender, args))); return; }
        if (!_plugin.Enabled || _plugin.Error is not null) { _failed = true; ReleaseView(); return; }
        _updating = true;
        try { _context.EnsureActive(); _context.Refresh(); _page!.Update(_context.State); }
        catch (Exception ex) { Fail(ex); }
        finally { _updating = false; }
    }
    private void Fail(Exception ex)
    {
        if (_failed) return;
        _failed = true;
        ReleaseView();
        Content = Card(L("插件页面加载失败", "Plugin page failed"), new TextBlock
        {
            Text = ex.GetBaseException().Message + "\n" + L("请在插件管理页检查此插件；内置页面将恢复。", "Check this plugin in Plugin management; built-in pages will be restored."),
            TextWrapping = TextWrapping.Wrap
        });
        Runtime.Plugins.ReportUiFailure(_plugin, ex);
    }
    private void ReleaseView()
    {
        Runtime.SnapshotChanged -= OnSnapshotChanged;
        _context.Deactivate(); Content = null;
        var page = _page; _page = null;
        if (page is null) return;
        try { page.Dispose(); }
        catch (Exception ex) { Runtime.Plugins.ReportUiFailure(_plugin, ex); }
    }
    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true; ReleaseView(); base.Dispose();
    }
    private sealed class PageContext : IPluginPageContext
    {
        private readonly ToolkitCustomPluginPage owner;
        private readonly string pageId;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly CancellationToken _token;
        private bool _active = true;
        internal PageContext(ToolkitCustomPluginPage owner, string pageId)
        {
            this.owner = owner; this.pageId = pageId; _token = _lifetime.Token;
        }
        public string PluginId => owner._plugin.Manifest.Id;
        public string PageId => pageId;
        public string PluginDirectory => owner._plugin.Directory;
        public PluginPageState State { get; private set; } = null!;
        public CancellationToken Lifetime => _token;
        internal void EnsureActive()
        {
            if (!_active || owner._disposed || owner.Runtime.Plugins.IsSuspendedForExit || !owner._plugin.Enabled || owner._plugin.Error is not null)
                throw new InvalidOperationException("This plugin page is no longer active.");
        }
        internal void Refresh()
        {
            var plugin = owner._plugin;
            var request = new PluginRequest("view", PluginHostBridge.Context(owner.Runtime, plugin.Manifest.Permissions),
                plugin.Manifest.Settings.ToDictionary(s => s.Id, s => owner.Runtime.Plugins.SettingValue(plugin, s)));
            State = new(request, owner.Runtime.IsDark, owner.Runtime.Settings.Language);
        }
        public Task SetSettingAsync(string id, JsonElement value)
        {
            var copy = value.Clone();
            return OnUiAsync(() =>
            {
                var setting = owner._plugin.Manifest.Settings.FirstOrDefault(s => s.Id == id)
                    ?? throw new ArgumentException("This setting does not belong to the plugin.");
                return owner.Runtime.Plugins.SetSettingAsync(owner._plugin, setting, copy);
            });
        }
        public Task ExecuteAsync(HostCommand command) => OnUiAsync(() =>
            PluginHostBridge.ExecuteAsync(owner.Runtime, owner._plugin.Manifest.Permissions, command));
        public async Task<bool> ShowToastAsync(string message, bool isError = false)
        {
            var accepted = false;
            await OnUiAsync(() =>
            {
                accepted = owner.Runtime.Plugins.ShowToast(owner._plugin, new PluginToast(message, isError));
                return Task.CompletedTask;
            });
            return accepted;
        }
        private Task OnUiAsync(Func<Task> action)
        {
            async Task Run() { EnsureActive(); await action(); }
            return owner.Dispatcher.CheckAccess() ? Run() : owner.Dispatcher.InvokeAsync(Run).Task.Unwrap();
        }
        internal void Deactivate()
        {
            if (!_active) return;
            _active = false;
            try { _lifetime.Cancel(); }
            catch (Exception ex) { ToolkitLog.Error("Plugin page cancellation callback failed.", ex); }
            finally { _lifetime.Dispose(); }
        }
    }
}

internal static class PluginUiLoader
{
    // WPF resource/static caches make unloading unreliable. Dispose the views;
    // managed/native assemblies remain loaded until Toolkit exits.
    private static readonly Dictionary<string, Assembly> Assemblies = new(StringComparer.OrdinalIgnoreCase);
    internal static bool IsLoaded(string directory) => Assemblies.Keys.Any(key => key.StartsWith(
        Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    internal static IToolkitPluginPage Create(PluginInstallation plugin, PluginPageView entry)
    {
        if (PluginFanBackendPackage.Fingerprint(plugin.Directory) != plugin.Fingerprint)
            throw new InvalidDataException("插件文件已改变，请重启后重新授权。 / Plugin files changed; restart and approve again.");
        var path = Path.GetFullPath(Path.Combine(plugin.Directory, entry.Assembly));
        var key = path + "|" + plugin.Fingerprint;
        if (!Assemblies.TryGetValue(key, out var assembly))
            Assemblies[key] = assembly = new PageAssemblyContext(path).LoadFromAssemblyPath(path);
        var type = assembly.GetType(entry.Type, throwOnError: true)!;
        if (!type.IsPublic || type.IsAbstract || !typeof(IToolkitPluginPage).IsAssignableFrom(type))
            throw new InvalidDataException("Custom view type must implement IToolkitPluginPage.");
        return (IToolkitPluginPage)(Activator.CreateInstance(type) ?? throw new InvalidOperationException("Cannot create plugin page."));
    }
    private sealed class PageAssemblyContext(string entry) : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver = new(entry);
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(IToolkitPluginPage).Assembly.GetName().Name) return typeof(IToolkitPluginPage).Assembly;
            if (name.Name == typeof(IToolkitPlugin).Assembly.GetName().Name) return typeof(IToolkitPlugin).Assembly;
            var path = _resolver.ResolveAssemblyToPath(name);
            if (path is null && File.Exists(Path.Combine(Path.GetDirectoryName(entry)!, name.Name + ".dll")))
                path = Path.Combine(Path.GetDirectoryName(entry)!, name.Name + ".dll");
            return path is null ? null : LoadFromAssemblyPath(path);
        }
        protected override IntPtr LoadUnmanagedDll(string name)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(name);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
