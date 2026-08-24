using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ThinkBookToolkit;

internal sealed class KeyboardMacroPanel : UserControl, IDisposable
{
    private readonly ToolkitRuntimeService _runtime;
    private readonly ToolkitPalette _palette;
    private readonly Func<string, bool, bool, Button> _buttonFactory;
    private readonly Func<string, string, UIElement, Border> _rowFactory;
    private readonly Func<string, UIElement, string, Border>
        _editorCardFactory;
    private readonly StackPanel _details = new();
    private readonly StackPanel _rows = new();
    private readonly StackPanel _editor = new();
    private Border _editorHost = new();
    private readonly StackPanel _eventRows = new();
    private readonly CheckBox _enabled = new();
    private readonly TextBox _name = new() { MinWidth = 240 };
    private readonly Button _expand;
    private readonly Button _record;
    private readonly Button _capture;
    private readonly TextBlock _binding = new();
    private readonly TextBlock _status = new();
    private KeyboardMacroDefinition? _draft;
    private bool _recording;
    private bool _disposed;
    private Button? _eventKeyCaptureButton;
    private string? _eventKeyCaptureOriginalText;
    private bool _capturingTrigger;

    public UIElement HeaderActions { get; }

    public KeyboardMacroPanel(
        ToolkitRuntimeService runtime,
        Func<string, bool, bool, Button> buttonFactory,
        Func<string, string, UIElement, Border> rowFactory,
        Func<string, UIElement, string, Border> editorCardFactory)
    {
        _runtime = runtime;
        _palette = ToolkitPalette.For(runtime.IsDark);
        _buttonFactory = buttonFactory;
        _rowFactory = rowFactory;
        _editorCardFactory = editorCardFactory;
        FontFamily = UiTypography.FontFamilyFor(runtime.Settings.Language);
        FontSize = 14;
        Foreground = Brush(_palette.Text);
        _expand = Button(L("展开", "Expand"));
        _record = Button(L("开始录制", "Start recording"), primary: true);
        _capture = Button(L("捕获按键", "Capture key"));
        HeaderActions = BuildHeaderActions();
        Content = BuildContent();
        Visibility = Visibility.Collapsed;
        RenderMacros();
        _runtime.MacroChanged += OnMacroChanged;
    }

    private UIElement BuildHeaderActions()
    {
        var headerActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        var create = Button(L("新建宏", "New macro"), primary: true);
        create.Click += (_, _) => BeginEdit(null);
        _expand.Click += (_, _) => SetExpanded(
            _details.Visibility != Visibility.Visible);
        _enabled.IsChecked = _runtime.Settings.MacroEnabled;
        _enabled.Click += (_, _) =>
        {
            if (!_runtime.TrySetMacroEnabled(
                    _enabled.IsChecked == true,
                    out var error))
            {
                _enabled.IsChecked = _runtime.Settings.MacroEnabled;
                SetStatus(L("设置失败：", "Setting failed: ") + error);
            }
        };
        headerActions.Children.Add(new TextBlock
        {
            Text = L("启用宏", "Enable macros"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        _enabled.Margin = new Thickness(0, 0, 14, 0);
        headerActions.Children.Add(_enabled);
        create.Margin = new Thickness(0, 0, 14, 0);
        _expand.Margin = new Thickness(0);
        headerActions.Children.Add(create);
        headerActions.Children.Add(_expand);
        return headerActions;
    }

    private UIElement BuildContent()
    {
        var body = new StackPanel();
        _details.Visibility = Visibility.Collapsed;
        _details.Margin = new Thickness(0);
        _details.Children.Add(_rows);
        BuildEditor();
        _editorHost = _editorCardFactory(
            L("编辑宏", "Edit macro"),
            _editor,
            L(
                "事件严格按照列表顺序播放；按键、Down/Up 状态和间隔均可修改。",
                "Events play in list order; keys, Down/Up states, and delays can all be edited."));
        _editorHost.Visibility = Visibility.Collapsed;
        _details.Children.Add(_editorHost);
        body.Children.Add(_details);
        _status.Foreground = Brush(_palette.Danger);
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(2, 10, 2, 0);
        _status.Visibility = Visibility.Collapsed;
        body.Children.Add(_status);
        return body;
    }

    private void BuildEditor()
    {
        _editor.Children.Add(Row(
            L("名称", "Name"),
            L("用于宏列表和自动化步骤。", "Used in macro lists and automation steps."),
            _name));
        var bindingControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        _binding.MinWidth = 130;
        _binding.VerticalAlignment = VerticalAlignment.Center;
        _binding.Margin = new Thickness(0, 0, 10, 0);
        _capture.Click += (_, _) => CaptureTrigger();
        var clearBinding = Button(L("取消绑定", "Clear binding"));
        clearBinding.Click += (_, _) =>
        {
            if (_draft is null)
                return;
            _runtime.CancelMacroTriggerCapture();
            _capturingTrigger = false;
            _capture.Content = L("捕获按键", "Capture key");
            CancelEventKeyCapture();
            _draft.TriggerVirtualKey = null;
            SyncBindingText();
        };
        bindingControls.Children.Add(_binding);
        bindingControls.Children.Add(_capture);
        bindingControls.Children.Add(clearBinding);
        _editor.Children.Add(Row(
            L("绑定按键", "Bound key"),
            L(
                "宏总开关开启时，按下此普通键会播放宏。",
                "When macros are enabled, this key plays the macro."),
            bindingControls));

        var recordActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 8)
        };
        _record.Click += (_, _) => ToggleRecording();
        var addEvent = Button(L("添加事件", "Add event"));
        addEvent.Click += (_, _) =>
        {
            _draft?.Events.Add(new KeyboardMacroEvent
            {
                VirtualKey = 0x41,
                Direction = KeyboardMacroDirection.Down,
                DelayMilliseconds = 0
            });
            RenderEvents();
        };
        recordActions.Children.Add(_record);
        recordActions.Children.Add(addEvent);
        recordActions.Children.Add(new TextBlock
        {
            Text = L(
                "仅可通过“停止录制”按钮结束；录制的键不会发送给其它应用。",
                "Recording ends only with the Stop recording button. Recorded keys are not sent to other applications."),
            Foreground = Brush(_palette.Muted),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 0, 0, 0)
        });
        _editor.Children.Add(recordActions);
        _editor.Children.Add(_eventRows);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var cancel = Button(L("取消", "Cancel"));
        var save = Button(L("保存", "Save"), primary: true);
        cancel.Click += (_, _) => EndEdit();
        save.Click += (_, _) => SaveDraft();
        footer.Children.Add(cancel);
        footer.Children.Add(save);
        _editor.Children.Add(footer);
    }

    private void RenderMacros()
    {
        _rows.Children.Clear();
        foreach (var macro in _runtime.Settings.Macros)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var run = Button(L("运行", "Run"), primary: true);
            var edit = Button(L("编辑", "Edit"));
            var delete = Button(L("删除", "Delete"), danger: true);
            run.Click += async (_, _) => await RunAsync(macro, run);
            edit.Click += (_, _) => BeginEdit(macro);
            delete.Click += (_, _) => Delete(macro);
            actions.Children.Add(run);
            actions.Children.Add(edit);
            actions.Children.Add(delete);
            _rows.Children.Add(Row(
                macro.Name,
                L(
                    $"{macro.Events.Count} 个事件 · " +
                    (macro.TriggerVirtualKey.HasValue
                        ? KeyboardMacroKeyNames.Format(macro.TriggerVirtualKey.Value)
                        : "未绑定按键"),
                    $"{macro.Events.Count} event(s) · " +
                    (macro.TriggerVirtualKey.HasValue
                        ? KeyboardMacroKeyNames.Format(macro.TriggerVirtualKey.Value)
                        : "No key binding")),
                actions));
        }
        if (_rows.Children.Count == 0)
        {
            _rows.Children.Add(new TextBlock
            {
                Text = L("尚未定义键盘宏。", "No keyboard macros have been defined."),
                Foreground = Brush(_palette.Muted),
                Margin = new Thickness(2, 4, 2, 4)
            });
        }
    }

    private void BeginEdit(KeyboardMacroDefinition? source)
    {
        StopRecording();
        CancelEventKeyCapture();
        _draft = source is null
            ? new KeyboardMacroDefinition
            {
                Name = UniqueDefinitionNames.Create(
                    L("新宏", "New macro"),
                    _runtime.Settings.Macros.Select(item => item.Name))
            }
            : Clone(source);
        _name.Text = _draft.Name;
        SyncBindingText();
        RenderEvents();
        _editorHost.Visibility = Visibility.Visible;
        SetExpanded(true);
    }

    private void EndEdit()
    {
        StopRecording();
        CancelEventKeyCapture();
        _capturingTrigger = false;
        _capture.Content = L("捕获按键", "Capture key");
        _draft = null;
        _editorHost.Visibility = Visibility.Collapsed;
        _eventRows.Children.Clear();
        SetStatus(string.Empty);
    }

    private void SaveDraft()
    {
        if (_draft is null)
            return;
        StopRecording();
        _draft.Name = _name.Text.Trim();
        if (_draft.Name.Length == 0 || _draft.Events.Count == 0)
        {
            SetStatus(L(
                "名称不能为空，并且至少需要一个键盘事件。",
                "Enter a name and add at least one keyboard event."));
            return;
        }
        if (_runtime.Settings.Macros.Any(item =>
                !item.Id.Equals(
                    _draft.Id,
                    StringComparison.OrdinalIgnoreCase) &&
                item.Name.Equals(
                    _draft.Name,
                    StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus(L(
                "宏名称不能与现有宏重复。",
                "Macro names must be unique."));
            return;
        }
        var values = _runtime.Settings.Macros
            .Where(item => !item.Id.Equals(
                _draft.Id,
                StringComparison.OrdinalIgnoreCase))
            .Select(Clone)
            .Append(Clone(_draft))
            .ToArray();
        if (!_runtime.TrySaveMacros(values, out var error))
        {
            SetStatus(L("保存失败：", "Save failed: ") + error);
            return;
        }
        EndEdit();
        RenderMacros();
    }

    private void RenderEvents()
    {
        _eventRows.Children.Clear();
        if (_draft is null)
            return;
        for (var index = 0; index < _draft.Events.Count; index++)
        {
            var currentIndex = index;
            var item = _draft.Events[index];
            var controls = new Grid();
            controls.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var key = Button(KeyboardMacroKeyNames.Format(item.VirtualKey));
            key.HorizontalAlignment = HorizontalAlignment.Stretch;
            key.HorizontalContentAlignment = HorizontalAlignment.Center;
            key.MinHeight = 34;
            key.Margin = new Thickness(0, 0, 8, 0);
            key.Click += (_, _) => CaptureEventKey(item, key);
            controls.Children.Add(key);
            var direction = new ComboBox { Margin = new Thickness(0, 0, 8, 0) };
            direction.Items.Add(new ComboBoxItem
            {
                Content = "Down",
                Tag = KeyboardMacroDirection.Down
            });
            direction.Items.Add(new ComboBoxItem
            {
                Content = "Up",
                Tag = KeyboardMacroDirection.Up
            });
            direction.SelectedItem = direction.Items
                .OfType<ComboBoxItem>()
                .First(option => Equals(option.Tag, item.Direction));
            direction.SelectionChanged += (_, _) =>
            {
                if (direction.SelectedItem is ComboBoxItem
                    {
                        Tag: KeyboardMacroDirection value
                    })
                {
                    item.Direction = value;
                }
            };
            Grid.SetColumn(direction, 1);
            controls.Children.Add(direction);
            var delay = new TextBox
            {
                Text = item.DelayMilliseconds.ToString(),
                MinHeight = 34,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = L("此事件前的间隔（毫秒）", "Delay before this event (milliseconds)")
            };
            delay.LostKeyboardFocus += (_, _) =>
            {
                if (int.TryParse(delay.Text, out var value) &&
                    value is >= 0 and <= KeyboardMacroDefaults.MaximumDelayMilliseconds)
                {
                    item.DelayMilliseconds = value;
                    SetStatus(string.Empty);
                }
                else
                {
                    delay.Text = item.DelayMilliseconds.ToString();
                    SetStatus(L(
                        "间隔必须是 0–600000 毫秒的整数。",
                        "Delay must be an integer from 0 to 600000 milliseconds."));
                }
            };
            Grid.SetColumn(delay, 2);
            controls.Children.Add(delay);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var up = Button("↑");
            var down = Button("↓");
            var remove = Button("−", danger: true);
            up.IsEnabled = index > 0;
            down.IsEnabled = index < _draft.Events.Count - 1;
            up.Click += (_, _) => MoveEvent(currentIndex, -1);
            down.Click += (_, _) => MoveEvent(currentIndex, 1);
            remove.Click += (_, _) =>
            {
                _draft.Events.RemoveAt(currentIndex);
                RenderEvents();
            };
            actions.Children.Add(up);
            actions.Children.Add(down);
            actions.Children.Add(remove);
            Grid.SetColumn(actions, 3);
            controls.Children.Add(actions);
            _eventRows.Children.Add(Row(
                $"{index + 1}",
                L("按键 / 状态 / 间隔（ms）", "Key / state / delay (ms)"),
                controls));
        }
    }

    private void MoveEvent(int index, int offset)
    {
        if (_draft is null)
            return;
        var target = index + offset;
        if (target < 0 || target >= _draft.Events.Count)
            return;
        (_draft.Events[index], _draft.Events[target]) =
            (_draft.Events[target], _draft.Events[index]);
        RenderEvents();
    }

    private void CaptureTrigger()
    {
        if (_capturingTrigger)
        {
            _runtime.CancelMacroTriggerCapture();
            _capturingTrigger = false;
            _capture.Content = L("捕获按键", "Capture key");
            return;
        }
        CancelEventKeyCapture();
        _capturingTrigger = true;
        _capture.Content = L("请按一个键…", "Press a key…");
        var error = _runtime.StartMacroTriggerCapture(value =>
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => Apply(value)));
                return;
            }
            Apply(value);
        });
        if (!string.IsNullOrWhiteSpace(error))
        {
            _capturingTrigger = false;
            _capture.Content = L("捕获按键", "Capture key");
            SetStatus(L("无法捕获按键：", "Could not capture the key: ") + error);
        }
        return;

        void Apply(int? value)
        {
            if (_draft is not null)
                _draft.TriggerVirtualKey = value;
            _capturingTrigger = false;
            _capture.Content = L("捕获按键", "Capture key");
            SyncBindingText();
        }
    }

    private void CaptureEventKey(
        KeyboardMacroEvent item,
        Button button)
    {
        if (ReferenceEquals(_eventKeyCaptureButton, button))
        {
            CancelEventKeyCapture();
            return;
        }
        _runtime.CancelMacroTriggerCapture();
        _capturingTrigger = false;
        _capture.Content = L("捕获按键", "Capture key");
        CancelEventKeyCapture();
        _eventKeyCaptureButton = button;
        _eventKeyCaptureOriginalText =
            KeyboardMacroKeyNames.Format(item.VirtualKey);
        button.Content = string.Empty;
        var error = _runtime.StartMacroTriggerCapture(value =>
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => Apply(value)));
                return;
            }
            Apply(value);
        });
        if (!string.IsNullOrWhiteSpace(error))
        {
            CancelEventKeyCapture();
            SetStatus(L(
                "无法捕获按键：",
                "Could not capture the key: ") + error);
        }
        return;

        void Apply(int? value)
        {
            if (value.HasValue)
                item.VirtualKey = value.Value;
            button.Content = KeyboardMacroKeyNames.Format(item.VirtualKey);
            _eventKeyCaptureButton = null;
            _eventKeyCaptureOriginalText = null;
            SetStatus(string.Empty);
        }
    }

    private void CancelEventKeyCapture()
    {
        if (_eventKeyCaptureButton is not null)
        {
            _eventKeyCaptureButton.Content =
                _eventKeyCaptureOriginalText ?? string.Empty;
        }
        _eventKeyCaptureButton = null;
        _eventKeyCaptureOriginalText = null;
        _runtime.CancelMacroTriggerCapture();
    }

    private void ToggleRecording()
    {
        if (_recording)
        {
            StopRecording();
            return;
        }
        if (_draft is null)
            return;
        if (_draft.Events.Count > 0 && MessageBox.Show(
                Window.GetWindow(this),
                L(
                    "重新录制会清空当前事件，是否继续？",
                    "Recording again clears the current events. Continue?"),
                L("重新录制", "Record again"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }
        _draft.Events.Clear();
        RenderEvents();
        var error = _runtime.StartMacroRecording(
            OnRecorded,
            OnRecordingStopped);
        if (!string.IsNullOrWhiteSpace(error))
        {
            SetStatus(L("无法开始录制：", "Could not start recording: ") + error);
            return;
        }
        _recording = true;
        _record.Content = L("停止录制", "Stop recording");
        SetStatus(L(
            "正在录制；点击“停止录制”结束。",
            "Recording; select Stop recording to finish."),
            error: false);
    }

    private void OnRecorded(KeyboardMacroEvent item)
    {
        if (_disposed)
            return;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed)
                return;
            _draft?.Events.Add(item);
            RenderEvents();
        }));
    }

    private void OnRecordingStopped()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnRecordingStopped));
            return;
        }
        _recording = false;
        _record.Content = L("开始录制", "Start recording");
        SetStatus(string.Empty);
    }

    private void StopRecording()
    {
        if (_recording)
            _runtime.StopMacroRecording();
        _recording = false;
        _record.Content = L("开始录制", "Start recording");
    }

    private async Task RunAsync(KeyboardMacroDefinition macro, Button button)
    {
        button.IsEnabled = false;
        var error = await _runtime.RunMacroAsync(
            macro.Id,
            executionSource: "manual");
        SetStatus(string.IsNullOrWhiteSpace(error)
            ? L("宏执行完成。", "Macro completed.")
            : L("宏执行失败：", "Macro failed: ") + error,
            error: !string.IsNullOrWhiteSpace(error));
        button.IsEnabled = true;
    }

    private void Delete(KeyboardMacroDefinition macro)
    {
        var references = _runtime.Settings.Automations.Count(automation =>
            automation.Steps.Any(step =>
                step.Kind == AutomationStepKind.RunMacro &&
                step.Value.Equals(
                    macro.Id,
                    StringComparison.OrdinalIgnoreCase)));
        if (references > 0)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                L(
                    $"有 {references} 个自动化正在使用这个宏。请先从自动化步骤中移除它。",
                    $"{references} automation(s) use this macro. Remove it from those automation steps first."),
                L("无法删除宏", "Cannot delete macro"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(
                Window.GetWindow(this),
                L($"删除宏“{macro.Name}”？", $"Delete macro “{macro.Name}”?"),
                L("删除宏", "Delete macro"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }
        var remaining = _runtime.Settings.Macros
            .Where(item => !item.Id.Equals(macro.Id, StringComparison.OrdinalIgnoreCase))
            .Select(Clone)
            .ToArray();
        if (!_runtime.TrySaveMacros(remaining, out var error))
            SetStatus(L("删除失败：", "Delete failed: ") + error);
        RenderMacros();
    }

    private void SyncBindingText() => _binding.Text =
        _draft?.TriggerVirtualKey is { } key
            ? KeyboardMacroKeyNames.Format(key)
            : L("未绑定", "Not bound");

    private void SetExpanded(bool expanded)
    {
        if (!expanded)
        {
            CancelEventKeyCapture();
            _capturingTrigger = false;
            _capture.Content = L("捕获按键", "Capture key");
        }
        _details.Visibility = expanded
            ? Visibility.Visible
            : Visibility.Collapsed;
        Visibility = expanded || _status.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        _expand.Content = expanded
            ? L("收起", "Collapse")
            : L("展开", "Expand");
    }

    private void OnMacroChanged(object? sender, EventArgs args)
    {
        _enabled.IsChecked = _runtime.Settings.MacroEnabled;
        RenderMacros();
    }

    private Border Row(string title, string description, UIElement control)
        => _rowFactory(title, description, control);

    private Button Button(
        string text,
        bool primary = false,
        bool danger = false)
        => _buttonFactory(text, primary, danger);

    private void SetStatus(string text, bool error = true)
    {
        _status.Text = text;
        _status.Visibility = string.IsNullOrWhiteSpace(text)
            ? Visibility.Collapsed
            : Visibility.Visible;
        Visibility = _details.Visibility == Visibility.Visible ||
                     _status.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        _status.Foreground = Brush(error ? _palette.Danger : _palette.Muted);
    }

    private string L(string chinese, string english) =>
        _runtime.L(chinese, english);

    private static KeyboardMacroDefinition Clone(
        KeyboardMacroDefinition source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        TriggerVirtualKey = source.TriggerVirtualKey,
        Events = source.Events.Select(item => item with { }).ToList()
    };

    private static SolidColorBrush Brush(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopRecording();
        CancelEventKeyCapture();
        _runtime.MacroChanged -= OnMacroChanged;
    }
}
