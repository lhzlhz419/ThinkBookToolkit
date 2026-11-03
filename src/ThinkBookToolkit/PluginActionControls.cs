using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ThinkBookToolkit.PluginApi;

namespace ThinkBookToolkit;

internal sealed class PluginParameterEditor : StackPanel
{
    private readonly Dictionary<string, Func<JsonElement>> _readers = new();
    private readonly PluginActionParameter[] _parameters;
    internal PluginParameterEditor(PluginActionParameter[] parameters, bool chinese, JsonElement? saved = null)
    {
        _parameters = parameters;
        foreach (var p in parameters)
        {
            var value = saved is { ValueKind: JsonValueKind.Object } args && args.TryGetProperty(p.Id, out var existing) ? existing : p.DefaultValue;
            if (!ToolkitPluginManager.ValidSetting(PluginExtensionPolicy.Parameter(p), value)) value = p.DefaultValue;
            Children.Add(new TextBlock { Text = p.Title.Resolve(chinese), Margin = new Thickness(0, 8, 0, 4), TextWrapping = TextWrapping.Wrap });
            if (p.Kind == "boolean")
            {
                var control = new CheckBox { IsChecked = value.GetBoolean() }; Children.Add(control);
                _readers[p.Id] = () => JsonSerializer.SerializeToElement(control.IsChecked == true);
            }
            else if (p.Kind == "choice")
            {
                var control = new ComboBox { ItemsSource = p.Choices, SelectedItem = value.GetString(), MinWidth = 150 }; Children.Add(control);
                _readers[p.Id] = () => JsonSerializer.SerializeToElement(control.SelectedItem?.ToString());
            }
            else
            {
                var control = new TextBox { Text = value.ToString(), MinWidth = 180 }; Children.Add(control);
                _readers[p.Id] = () => p.Kind == "number" ? JsonSerializer.SerializeToElement(double.Parse(control.Text, CultureInfo.CurrentCulture)) : JsonSerializer.SerializeToElement(control.Text);
            }
        }
    }
    internal JsonElement Read()
    {
        var values = _readers.ToDictionary(p => p.Key, p => p.Value());
        foreach (var parameter in _parameters)
            if (!ToolkitPluginManager.ValidSetting(PluginExtensionPolicy.Parameter(parameter), values[parameter.Id]))
                throw new ArgumentException("Invalid parameter: " + parameter.Id);
        return JsonSerializer.SerializeToElement(values);
    }
}

internal static class PluginActionControls
{
    internal static FrameworkElement Create(ToolkitRuntimeService runtime, PluginAction action)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 12) };
        var parameters = new PluginParameterEditor(action.Parameters, runtime.IsChinese);
        var run = new Button { Content = action.Title.Resolve(runtime.IsChinese), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        run.Click += async (_, _) =>
        {
            run.IsEnabled = false;
            try { status.Text = await runtime.Plugins.InvokeActionAsync(action.Id, parameters.Read()) ?? runtime.L("已完成", "Completed"); }
            catch (Exception ex) { status.Text = ex.GetBaseException().Message; }
            finally { run.IsEnabled = true; }
        };
        panel.Children.Add(parameters); panel.Children.Add(run); panel.Children.Add(status); return panel;
    }
}
