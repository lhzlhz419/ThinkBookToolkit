using System;
using System.Globalization;
using System.Windows.Data;
using ThinkBookToolkit.PluginApi;

namespace ThinkBookToolkit;

internal static class PluginOverviewBinding
{
    internal static Binding Create(ToolkitPluginManager manager, PluginInstallation plugin, PluginOverviewItem item) =>
        new(nameof(ToolkitPluginManager.Revision)) { Source = manager, Mode = BindingMode.OneWay, Converter = new TextConverter(manager, plugin, item) };

    private sealed class TextConverter(ToolkitPluginManager manager, PluginInstallation plugin, PluginOverviewItem item) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => manager.OverviewText(plugin, item);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
