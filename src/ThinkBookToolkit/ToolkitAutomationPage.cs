using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace ThinkBookToolkit;

internal sealed class ToolkitAutomationPage : ToolkitPageBase
{
    private readonly StackPanel _automationRows = new();
    private readonly StackPanel _automationDetails = new();
    private readonly StackPanel _editor = new();
    private Border _editorCard = new();
    private readonly StackPanel _stepRows = new();
    private readonly TextBox _name = new() { MinWidth = 260 };
    private readonly ComboBox _category = new() { MinWidth = 160 };
    private readonly ComboBox _action = new() { MinWidth = 230 };
    private readonly ComboBox _option = new() { MinWidth = 210 };
    private readonly TextBox _value = new() { MinWidth = 210 };
    private readonly TextBox _arguments = new() { MinWidth = 180 };
    private readonly StackPanel _pluginArguments = new();
    private PluginParameterEditor? _pluginParameterEditor;
    private readonly StackPanel _pluginSignals = new();
    private readonly Dictionary<string, CheckBox> _pluginTriggerToggles = new();
    private readonly Dictionary<string, CheckBox> _pluginConditionToggles = new();
    private readonly ComboBox _secondaryOption = new() { MinWidth = 140 };
    private readonly TextBlock _executionInfo = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Expander _executionDetails = new() { IsExpanded = false, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
    private Button _cancelRuns = null!;
    private bool _disposed;
    private readonly TextBlock _argumentsLabel = new();
    private readonly Button _browse;
    private readonly Button _addStep;
    private readonly TextBlock _status;
    private readonly CheckBox _automationEnabled = new();
    private readonly Button _automationExpand;
    private readonly KeyboardMacroPanel _macroPanel;
    private AutomationDefinition? _draft;
    private int? _editingStepIndex;
    private readonly Dictionary<AutomationTriggerKind, CheckBox>
        _triggerToggles = [];

    public ToolkitAutomationPage(ToolkitRuntimeService runtime) : base(runtime)
    {
        DataContext = new AutomationViewModel(runtime);
        _browse = ActionButton(L("浏览", "Browse"));
        _addStep = ActionButton(L("添加步骤", "Add step"), primary: true);
        _automationExpand = ActionButton(L("展开", "Expand"));
        _macroPanel = new KeyboardMacroPanel(
            runtime,
            (text, primary, danger) =>
                ActionButton(text, primary, danger),
            (title, description, control) =>
                SettingRow(title, description, control),
            (title, content, description) =>
                Card(title, content, description, "\uE70F"));
        _status = StatusText();
        Content = BuildLayout();
        RenderAutomations();
        Runtime.MacroChanged += OnMacroChanged;
        Runtime.AutomationChanged += OnExecutionChanged;
        Runtime.AutomationExecution.Changed += OnExecutionChanged;
        OnExecutionChanged(this, EventArgs.Empty);
    }

    private UIElement BuildLayout()
    {
        var root = new StackPanel();
        var create = ActionButton(L("新建自动化", "New automation"), primary: true);
        create.Click += (_, _) => BeginEdit(null);
        _automationExpand.Click += (_, _) => SetAutomationExpanded(
            _automationDetails.Visibility != Visibility.Visible);
        _automationEnabled.IsChecked = Runtime.Settings.AutomationEnabled;
        _automationEnabled.Click += (_, _) =>
        {
            if (!Runtime.TrySetAutomationEnabled(
                    _automationEnabled.IsChecked == true,
                    out var error))
            {
                _automationEnabled.IsChecked = Runtime.Settings.AutomationEnabled;
                _status.Text = L("设置失败：", "Setting failed: ") + error;
            }
        };
        var headerActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        headerActions.Children.Add(new TextBlock
        {
            Text = L("启用自动化", "Enable automation"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        _automationEnabled.Margin = new Thickness(0, 0, 14, 0);
        headerActions.Children.Add(_automationEnabled);
        create.Margin = new Thickness(0, 0, 14, 0);
        _automationExpand.Margin = new Thickness(0);
        headerActions.Children.Add(create);
        headerActions.Children.Add(_automationExpand);
        _automationDetails.Visibility = Visibility.Collapsed;
        _automationDetails.Margin = new Thickness(0);
        _automationRows.Margin = new Thickness(0);
        _automationDetails.Children.Add(_automationRows);

        BuildEditor();
        _editorCard = Card(
            L("编辑自动化", "Edit automation"),
            _editor,
            L(
                "步骤严格按照列表顺序执行；某一步失败时会停止后续步骤。",
                "Steps run strictly in list order; a failed step stops the remaining steps."),
            "\uE70F");
        _editorCard.Visibility = Visibility.Collapsed;
        _automationDetails.Children.Add(_editorCard);
        root.Children.Add(Card(
            L("自动化", "Automation"),
            _automationDetails,
            L(
                "将设备控制、应用操作和延迟按任意顺序组合，并可手动运行或绑定到 Fn 快捷键。",
                "Combine device controls, application actions, and delays in any order, then run them manually or bind them to Fn keys."),
            "\uE771",
            headerAction: headerActions));
        _cancelRuns = ActionButton(L("取消全部运行", "Cancel all runs"), danger: true);
        _cancelRuns.HorizontalAlignment = HorizontalAlignment.Right;
        _cancelRuns.Click += (_, _) => Runtime.CancelAutomation();
        var executionBody = new StackPanel { Margin = new Thickness(4, 10, 4, 0) };
        executionBody.Children.Add(_cancelRuns);
        _executionInfo.Margin = new Thickness(0, 6, 0, 0);
        _executionInfo.Foreground = Brush(Palette.Text);
        executionBody.Children.Add(_executionInfo);
        executionBody.Children.Add(new TextBlock
        {
            Text = L("取消后停止后续步骤；已开始的硬件操作可能完成。", "Cancellation stops subsequent steps; an in-flight hardware operation may finish."),
            Foreground = Brush(Palette.Muted), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0)
        });
        _executionDetails.Header = L("运行记录", "Run history");
        _executionDetails.Foreground = Brush(Palette.Muted);
        _executionDetails.Content = executionBody;
        _automationDetails.Children.Add(_executionDetails);
        root.Children.Add(Card(
            L("键盘宏", "Keyboard macros"),
            _macroPanel,
            L(
                "录制、修改并播放键盘按下/释放序列；可绑定普通按键或作为自动化步骤。",
                "Record, edit, and play keyboard down/up sequences; bind them to a key or use them in automations."),
            "\uE765",
            headerAction: _macroPanel.HeaderActions));
        _status.Margin = new Thickness(4, 2, 4, 10);
        root.Children.Add(_status);
        return root;
    }

    private void BuildEditor()
    {
        _editor.Children.Add(SettingRow(
            L("名称", "Name"),
            L("用于列表和 Fn 快捷键映射。", "Used in the list and Fn-key mappings."),
            _name));
        var triggers = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = 440 };
        foreach (var trigger in Enum.GetValues<AutomationTriggerKind>())
        {
            var toggle = new CheckBox
            {
                Content = TriggerName(trigger),
                Margin = new Thickness(0, 0, 16, 8)
            };
            _triggerToggles[trigger] = toggle;
            triggers.Children.Add(toggle);
        }
        _editor.Children.Add(SettingRow(
            L("事件触发", "Event triggers"),
            L(
                "总开关开启后，在选定事件发生时运行此自动化。游戏事件与固定转速共用同一检测结果。",
                "When automation is enabled, run this automation for selected events. Game events share the fixed-RPM detector."),
            triggers));
        _editor.Children.Add(new TextBlock
        {
            Text = L("步骤", "Steps"),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 12, 2, 8)
        });
        _editor.Children.Add(_stepRows);
        _editor.Children.Add(_pluginSignals);

        var addGrid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        addGrid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        addGrid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        addGrid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        _category.Margin = new Thickness(0, 0, 8, 0);
        _action.Margin = new Thickness(0, 0, 8, 0);
        addGrid.Children.Add(_category);
        Grid.SetColumn(_action, 1);
        addGrid.Children.Add(_action);
        var valueHost = new Grid();
        valueHost.Children.Add(_option);
        valueHost.Children.Add(_value);
        Grid.SetColumn(valueHost, 2);
        addGrid.Children.Add(valueHost);
        for (var row = 0; row < 3; row++) addGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        addGrid.SizeChanged += (_, _) =>
        {
            var stacked = addGrid.ActualWidth < 700;
            var controls = new FrameworkElement[] { _category, _action, valueHost };
            for (var index = 0; index < controls.Length; index++)
            {
                Grid.SetRow(controls[index], stacked ? index : 0);
                Grid.SetColumn(controls[index], stacked ? 0 : index);
                Grid.SetColumnSpan(controls[index], stacked ? 3 : 1);
                controls[index].Margin = stacked ? new Thickness(0, 0, 0, 8) : new Thickness(0, 0, index < 2 ? 8 : 0, 0);
            }
        };
        _editor.Children.Add(addGrid);
        _editor.Children.Add(_pluginArguments);
        _option.SelectionChanged += (_, _) => ConfigurePluginArguments();

        var appDetails = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0)
        };
        _browse.Margin = new Thickness(0, 0, 8, 0);
        _arguments.Margin = new Thickness(0, 0, 8, 0);
        appDetails.Children.Add(_browse);
        _argumentsLabel.Text = L("启动参数", "Arguments");
        _argumentsLabel.VerticalAlignment = VerticalAlignment.Center;
        _argumentsLabel.Margin = new Thickness(0, 0, 6, 0);
        appDetails.Children.Add(_argumentsLabel);
        appDetails.Children.Add(_arguments);
        _secondaryOption.Items.Add(new ComboBoxItem { Content = L("锁定", "Lock"), Tag = "true" });
        _secondaryOption.Items.Add(new ComboBoxItem { Content = L("解除锁定", "Unlock"), Tag = "false" });
        _secondaryOption.Items.Add(new ComboBoxItem { Content = L("切换锁定", "Toggle lock"), Tag = "toggle" });
        _secondaryOption.SelectedIndex = 0;
        appDetails.Children.Add(_secondaryOption);
        appDetails.Children.Add(_addStep);
        _editor.Children.Add(appDetails);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var cancel = ActionButton(L("取消", "Cancel"));
        var save = ActionButton(L("保存", "Save"), primary: true);
        cancel.Click += (_, _) => EndEdit();
        save.Click += (_, _) => SaveDraft();
        footer.Children.Add(cancel);
        footer.Children.Add(save);
        _editor.Children.Add(footer);

        foreach (var category in AvailableCatalogItems()
                     .Select(item => Runtime.IsChinese
                         ? item.CategoryChinese
                         : item.CategoryEnglish)
                     .Distinct(StringComparer.Ordinal))
        {
            _category.Items.Add(category);
        }
        _category.SelectionChanged += (_, _) => PopulateActions();
        _action.SelectionChanged += (_, _) => ConfigureStepInput();
        _browse.Click += (_, _) => BrowseApplication();
        _addStep.Click += (_, _) => AddStep();
        if (_category.Items.Count > 0)
            _category.SelectedIndex = 0;
    }

    private void PopulateActions()
    {
        _action.Items.Clear();
        if (_category.SelectedItem is not string category)
            return;
        foreach (var item in AvailableCatalogItems().Where(item =>
                     (Runtime.IsChinese
                         ? item.CategoryChinese
                         : item.CategoryEnglish) == category))
        {
            _action.Items.Add(new ComboBoxItem
            {
                Content = Runtime.IsChinese
                    ? item.NameChinese
                    : item.NameEnglish,
                Tag = item.Kind
            });
        }
        if (_action.Items.Count > 0)
            _action.SelectedIndex = 0;
    }

    private void ConfigureStepInput()
    {
        _option.Items.Clear();
        _value.Text = string.Empty;
        _arguments.Text = string.Empty;
        _secondaryOption.SelectedIndex = 0;
        if (_action.SelectedItem is not ComboBoxItem
            {
                Tag: AutomationStepKind kind
            })
            return;
        var metadata = AutomationStepCatalog.Metadata(kind);
        _option.Visibility = metadata.InputKind is AutomationStepInputKind.Options or AutomationStepInputKind.OptionsWithValue or AutomationStepInputKind.OptionsWithSwitch or AutomationStepInputKind.PluginAction
            ? Visibility.Visible
            : Visibility.Collapsed;
        _value.Visibility = metadata.InputKind is
                AutomationStepInputKind.Integer or
                AutomationStepInputKind.ApplicationPath
            ? Visibility.Visible
            : Visibility.Collapsed;
        _browse.Visibility = metadata.InputKind ==
            AutomationStepInputKind.ApplicationPath
            ? Visibility.Visible
            : Visibility.Collapsed;
        _arguments.Visibility = metadata.InputKind is AutomationStepInputKind.ApplicationPath or AutomationStepInputKind.OptionsWithValue ? Visibility.Visible : Visibility.Collapsed;
        _secondaryOption.Visibility = metadata.InputKind == AutomationStepInputKind.OptionsWithSwitch ? Visibility.Visible : Visibility.Collapsed;
        _argumentsLabel.Visibility = metadata.InputKind is AutomationStepInputKind.ApplicationPath or AutomationStepInputKind.OptionsWithValue or AutomationStepInputKind.OptionsWithSwitch ? Visibility.Visible : Visibility.Collapsed;
        _argumentsLabel.Text = metadata.InputKind == AutomationStepInputKind.ApplicationPath ? L("启动参数", "Arguments") : L("参数值 / 锁定", "Value / lock");
        _arguments.ToolTip = L("使用当前功耗接口的单位（W、°C、秒；AMD PBO 的 TDC/EDC 为 A）。", "Use the active power interface's units (W, °C, seconds; AMD PBO TDC/EDC use A).");
        foreach (var option in AutomationStepCatalog.Options(kind, Runtime))
        {
            _option.Items.Add(new ComboBoxItem
            {
                Content = Runtime.IsChinese
                    ? option.Chinese
                    : option.English,
                Tag = option.Value
            });
        }
        if (_option.Items.Count > 0)
            _option.SelectedIndex = 0;
        if (metadata.InputKind == AutomationStepInputKind.Integer)
            _value.Text = "0.5";
        ConfigurePluginArguments();
    }

    private void BrowseApplication()
    {
        var dialog = new OpenFileDialog
        {
            Filter = L(
                "应用程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                "Applications (*.exe)|*.exe|All files (*.*)|*.*"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            _value.Text = dialog.FileName;
    }

    private void AddStep()
    {
        if (_draft is null ||
            _action.SelectedItem is not ComboBoxItem
            {
                Tag: AutomationStepKind kind
            })
            return;
        var metadata = AutomationStepCatalog.Metadata(kind);
        var value = metadata.InputKind switch
        {
            AutomationStepInputKind.None => string.Empty,
            AutomationStepInputKind.Options or AutomationStepInputKind.OptionsWithValue or AutomationStepInputKind.OptionsWithSwitch or AutomationStepInputKind.PluginAction =>
                (_option.SelectedItem as ComboBoxItem)?.Tag?.ToString() ??
                string.Empty,
            _ => _value.Text.Trim()
        };
        if (metadata.InputKind != AutomationStepInputKind.None &&
            string.IsNullOrWhiteSpace(value))
        {
            _status.Text = L("请填写步骤参数。", "Enter a value for the step.");
            return;
        }
        if (kind == AutomationStepKind.Delay &&
            (!double.TryParse(
                 value,
                 System.Globalization.NumberStyles.Float,
                 System.Globalization.CultureInfo.InvariantCulture,
                 out var seconds) ||
             seconds is < 0 or > 86400))
        {
            _status.Text = L(
                "延迟必须是 0–86400 秒的有限数值，可以使用小数。",
                "Delay must be a finite value from 0 to 86400 seconds; fractions are allowed.");
            return;
        }
        var newStep = new AutomationStep
        {
            Kind = kind,
            Value = value,
            SecondaryValue = kind == AutomationStepKind.PowerLock
                ? (_secondaryOption.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? ""
                : kind is AutomationStepKind.OpenApplication or AutomationStepKind.PowerValue ? _arguments.Text.Trim() : string.Empty
        };
        if (kind == AutomationStepKind.PluginAction)
        {
            try { newStep.SecondaryValue = _pluginParameterEditor?.Read().GetRawText() ?? "{}"; }
            catch (Exception ex) { _status.Text = ex.Message; return; }
        }
        if (AutomationStepCatalog.Validate(newStep) is { } invalid) { _status.Text = invalid; return; }
        if (_editingStepIndex is { } editingIndex &&
            editingIndex >= 0 && editingIndex < _draft.Steps.Count)
        {
            _draft.Steps[editingIndex] = newStep;
            ResetStepEditor();
            _status.Text = string.Empty;
            RenderSteps();
            return;
        }
        if (_draft.Steps.Count > 0)
        {
            _draft.Steps.Add(new AutomationStep
            {
                Kind = AutomationStepKind.Delay,
                Value = "0.5"
            });
        }
        _draft.Steps.Add(newStep);
        _status.Text = string.Empty;
        RenderSteps();
    }

    private void BeginEdit(AutomationDefinition? source)
    {
        _draft = source is null
            ? new AutomationDefinition
            {
                Name = UniqueDefinitionNames.Create(
                    L("新自动化", "New automation"),
                    Runtime.Settings.Automations.Select(item => item.Name))
            }
            : Clone(source);
        _name.Text = _draft.Name;
        BuildPluginSignals();
        foreach (var pair in _triggerToggles)
            pair.Value.IsChecked = _draft.Triggers.Contains(pair.Key);
        ResetStepEditor();
        SetAutomationExpanded(true);
        _editorCard.Visibility = Visibility.Visible;
        RenderSteps();
    }

    private void EndEdit()
    {
        _draft = null;
        ResetStepEditor();
        _editorCard.Visibility = Visibility.Collapsed;
        _stepRows.Children.Clear();
        _status.Text = string.Empty;
    }

    private void SetAutomationExpanded(bool expanded)
    {
        _automationDetails.Visibility = expanded
            ? Visibility.Visible
            : Visibility.Collapsed;
        _automationExpand.Content = expanded
            ? L("收起", "Collapse")
            : L("展开", "Expand");
    }

    private IEnumerable<AutomationStepMetadata> AvailableCatalogItems() =>
        AutomationStepCatalog.Items.Where(item =>
            AutomationStepCatalog.IsAvailableForCpu(item.Kind, CpuVendorDetector.Manufacturer()) &&
            (item.Kind != AutomationStepKind.PluginAction || Runtime.Plugins.Actions.Any()) &&
            (item.Kind != AutomationStepKind.RunMacro || Runtime.Settings.Macros.Count > 0));

    private void OnMacroChanged(object? sender, EventArgs args)
    {
        var selectedCategory = _category.SelectedItem as string;
        _category.Items.Clear();
        foreach (var category in AvailableCatalogItems()
                     .Select(item => Runtime.IsChinese
                         ? item.CategoryChinese
                         : item.CategoryEnglish)
                     .Distinct(StringComparer.Ordinal))
        {
            _category.Items.Add(category);
        }
        _category.SelectedItem = selectedCategory is not null &&
                                 _category.Items.Contains(selectedCategory)
            ? selectedCategory
            : _category.Items.Count > 0
                ? _category.Items[0]
                : null;
        PopulateActions();
    }

    private void SaveDraft()
    {
        if (_draft is null)
            return;
        var name = _name.Text.Trim();
        if (name.Length == 0 || _draft.Steps.Count == 0)
        {
            _status.Text = L(
                "名称不能为空，并且至少需要一个步骤。",
                "Enter a name and add at least one step.");
            return;
        }
        if (Runtime.Settings.Automations.Any(item =>
                !item.Id.Equals(
                    _draft.Id,
                    StringComparison.OrdinalIgnoreCase) &&
                item.Name.Equals(
                    name,
                    StringComparison.OrdinalIgnoreCase)))
        {
            _status.Text = L(
                "自动化名称不能与现有自动化重复。",
                "Automation names must be unique.");
            return;
        }
        _draft.Name = name;
        _draft.PluginTriggers = _pluginTriggerToggles.Where(p => p.Value.IsChecked == true).Select(p => p.Key).ToList();
        _draft.PluginConditions = _pluginConditionToggles.Where(p => p.Value.IsChecked == true).Select(p => p.Key).ToList();
        _draft.Enabled = Runtime.Settings.Automations.FirstOrDefault(a => a.Id == _draft.Id)?.Enabled ?? _draft.Enabled;
        _draft.Triggers = _triggerToggles
            .Where(pair => pair.Value.IsChecked == true)
            .Select(pair => pair.Key)
            .ToList();
        var values = Runtime.Settings.Automations
            .Where(item => !item.Id.Equals(
                _draft.Id,
                StringComparison.OrdinalIgnoreCase))
            .Select(Clone)
            .Append(Clone(_draft))
            .ToArray();
        if (!Runtime.TrySaveAutomations(values, out var error))
        {
            _status.Text = L("保存失败：", "Save failed: ") + error;
            return;
        }
        EndEdit();
        RenderAutomations();
    }

    private void RenderAutomations()
    {
        _automationRows.Children.Clear();
        foreach (var automation in Runtime.Settings.Automations)
        {
            var actions = new WrapPanel { Orientation = Orientation.Horizontal };
            var run = ActionButton(L("运行", "Run"), primary: true);
            var pending = Runtime.AutomationExecution.IsPending(automation.Id);
            run.IsEnabled = automation.Enabled && !pending;
            var enabled = new CheckBox { IsChecked = automation.Enabled, Content = L("启用", "Enabled"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            enabled.Click += (_, _) =>
            {
                var updated = Runtime.Settings.Automations.Select(a => a.Id == automation.Id ? a with { Enabled = enabled.IsChecked == true } : a);
                if (!Runtime.TrySaveAutomations(updated, out var error)) { enabled.IsChecked = automation.Enabled; _status.Text = error; }
            };
            var stop = ActionButton(L("取消运行", "Cancel run"), danger: true);
            stop.IsEnabled = pending;
            stop.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
            stop.Click += (_, _) => Runtime.CancelAutomation(automation.Id);
            var edit = ActionButton(L("编辑", "Edit"));
            var delete = ActionButton(L("删除", "Delete"), danger: true);
            run.Click += async (_, _) => await RunAsync(automation, run);
            edit.Click += (_, _) => BeginEdit(automation);
            delete.Click += (_, _) => Delete(automation);
            actions.Children.Add(run);
            actions.Children.Insert(0, enabled);
            actions.Children.Add(stop);
            actions.Children.Add(edit);
            actions.Children.Add(delete);
            _automationRows.Children.Add(SettingRow(
                automation.Name,
                L(
                    $"{automation.Steps.Count} 个步骤 · {automation.Triggers.Count} 个事件",
                    $"{automation.Steps.Count} step(s) · {automation.Triggers.Count} trigger(s)"),
                actions));
        }
        if (_automationRows.Children.Count == 0)
        {
            _automationRows.Children.Add(new TextBlock
            {
                Text = L(
                    "尚未定义自动化。",
                    "No automations have been defined."),
                Foreground = Brush(Palette.Muted),
                Margin = new Thickness(2, 4, 2, 4)
            });
        }
    }

    private void RenderSteps()
    {
        _stepRows.Children.Clear();
        if (_draft is null)
            return;
        for (var index = 0; index < _draft.Steps.Count; index++)
        {
            var currentIndex = index;
            var step = _draft.Steps[index];
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var up = ActionButton("↑");
            var down = ActionButton("↓");
            var delete = ActionButton("−", danger: true);
            var edit = ActionButton(L("编辑", "Edit"));
            up.IsEnabled = index > 0;
            down.IsEnabled = index < _draft.Steps.Count - 1;
            up.Click += (_, _) => MoveStep(currentIndex, -1);
            down.Click += (_, _) => MoveStep(currentIndex, 1);
            edit.Click += (_, _) => BeginStepEdit(currentIndex);
            delete.Click += (_, _) =>
            {
                _draft.Steps.RemoveAt(currentIndex);
                RenderSteps();
            };
            actions.Children.Add(up);
            actions.Children.Add(down);
            actions.Children.Add(edit);
            actions.Children.Add(delete);
            var metadata = AutomationStepCatalog.Metadata(step.Kind);
            _stepRows.Children.Add(SettingRow(
                $"{index + 1}. {AutomationStepCatalog.DisplayName(step, Runtime)}",
                Runtime.IsChinese
                    ? metadata.CategoryChinese
                    : metadata.CategoryEnglish,
                actions));
        }
    }

    private void MoveStep(int index, int offset)
    {
        if (_draft is null)
            return;
        var target = index + offset;
        if (target < 0 || target >= _draft.Steps.Count)
            return;
        (_draft.Steps[index], _draft.Steps[target]) =
            (_draft.Steps[target], _draft.Steps[index]);
        RenderSteps();
    }

    private void BeginStepEdit(int index)
    {
        if (_draft is null || index < 0 || index >= _draft.Steps.Count)
            return;
        var step = _draft.Steps[index];
        if (step.Kind == AutomationStepKind.PluginAction && !Runtime.Plugins.Actions.Any(a => a.Action.Id == step.Value))
        {
            ResetStepEditor();
            _status.Text = L("插件动作不可用。已保留原参数，启用对应插件后可继续编辑。", "Plugin action unavailable. Its saved arguments can be edited when the provider is enabled.");
            return;
        }
        if (!AutomationStepCatalog.IsAvailableForCpu(step.Kind, CpuVendorDetector.Manufacturer()))
        {
            ResetStepEditor();
            _status.Text = L("该步骤不适用于当前处理器，原配置已保留；可删除或替换此步骤。", "This step does not match the current CPU. Its configuration is preserved; remove or replace the step.");
            return;
        }
        var metadata = AutomationStepCatalog.Metadata(step.Kind);
        _editingStepIndex = index;
        _category.SelectedItem = Runtime.IsChinese
            ? metadata.CategoryChinese
            : metadata.CategoryEnglish;
        _action.SelectedItem = _action.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => Equals(item.Tag, step.Kind));
        ConfigureStepInput();
        if (metadata.InputKind is AutomationStepInputKind.Options or AutomationStepInputKind.OptionsWithValue or AutomationStepInputKind.OptionsWithSwitch or AutomationStepInputKind.PluginAction)
        {
            _option.SelectedItem = _option.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag?.ToString(),
                    step.Value,
                    StringComparison.OrdinalIgnoreCase));
            if (_option.SelectedItem is null)
            {
                var unavailable = new ComboBoxItem { Content = L("当前不可用：", "Currently unavailable: ") + step.Value, Tag = step.Value };
                _option.Items.Add(unavailable); _option.SelectedItem = unavailable;
            }
        }
        else
        {
            _value.Text = step.Value;
        }
        _arguments.Text = step.SecondaryValue;
        if (step.Kind == AutomationStepKind.PluginAction)
        {
            try { using var args = JsonDocument.Parse(step.SecondaryValue); ConfigurePluginArguments(args.RootElement); }
            catch (JsonException) { _status.Text = L("原插件参数无效，请重新填写。", "Saved plugin arguments are invalid; enter them again."); }
        }
        _secondaryOption.SelectedItem = _secondaryOption.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag?.ToString() == step.SecondaryValue);
        _addStep.Content = L("更新步骤", "Update step");
    }

    private void ResetStepEditor()
    {
        _editingStepIndex = null;
        _addStep.Content = L("添加步骤", "Add step");
    }

    private async Task RunAsync(AutomationDefinition automation, Button button)
    {
        button.IsEnabled = false;
        _status.Text = L(
            $"正在运行：{automation.Name}",
            $"Running: {automation.Name}");
        var result = await Runtime.RunAutomationAsync(automation.Id);
        _status.Text = result.Success
            ? L("自动化执行完成。", "Automation completed.")
            : result.Error;
        button.IsEnabled = true;
    }

    private void Delete(AutomationDefinition automation)
    {
        if (ToolkitMessageBox.Show(
                Window.GetWindow(this),
                L(
                    $"删除自动化“{automation.Name}”？",
                    $"Delete automation “{automation.Name}”?"),
                L("删除自动化", "Delete automation"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        var remaining = Runtime.Settings.Automations
            .Where(item => !item.Id.Equals(
                automation.Id,
                StringComparison.OrdinalIgnoreCase))
            .Select(Clone)
            .ToArray();
        _status.Text = Runtime.TrySaveAutomations(remaining, out var error)
            ? string.Empty
            : L("删除失败：", "Delete failed: ") + error;
        RenderAutomations();
    }

    private static AutomationDefinition Clone(AutomationDefinition source) =>
        new()
        {
            Id = source.Id,
            Name = source.Name,
            Enabled = source.Enabled,
            PluginTriggers = source.PluginTriggers.ToList(),
            PluginConditions = source.PluginConditions.ToList(),
            Steps = source.Steps.Select(step => step with { }).ToList(),
            Triggers = source.Triggers.ToList()
        };

    private void ConfigurePluginArguments(JsonElement? saved = null)
    {
        _pluginArguments.Children.Clear(); _pluginParameterEditor = null;
        if ((_action.SelectedItem as ComboBoxItem)?.Tag is not AutomationStepKind.PluginAction) return;
        var id = (_option.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var action = Runtime.Plugins.Actions.FirstOrDefault(a => a.Action.Id == id).Action;
        if (action is null) return;
        _pluginParameterEditor = new(action.Parameters, Runtime.IsChinese, saved);
        _pluginArguments.Children.Add(_pluginParameterEditor);
    }

    private void BuildPluginSignals()
    {
        _pluginSignals.Children.Clear(); _pluginTriggerToggles.Clear(); _pluginConditionToggles.Clear();
        if (_draft is null) return;
        foreach (var conditions in new[] { false, true })
        {
            var selected = conditions ? _draft.PluginConditions : _draft.PluginTriggers;
            var available = Runtime.Plugins.Installations.Where(p => p.Enabled && p.Error is null).SelectMany(p =>
                (conditions ? p.Manifest.Conditions : p.Manifest.Triggers).Select(s => (s.Id, Title: p.Manifest.Name + " · " + s.Title.Resolve(Runtime.IsChinese)))).ToDictionary(p => p.Id, p => p.Title);
            var ids = available.Keys.Concat(selected).Distinct().ToArray();
            if (ids.Length == 0) continue;
            var row = new WrapPanel { MaxWidth = 440 };
            foreach (var id in ids)
            {
                var toggle = new CheckBox { Content = available.GetValueOrDefault(id, L("不可用：", "Unavailable: ") + id), IsChecked = selected.Contains(id), Margin = new Thickness(0, 4, 14, 4) };
                (conditions ? _pluginConditionToggles : _pluginTriggerToggles)[id] = toggle;
                row.Children.Add(toggle);
            }
            _pluginSignals.Children.Add(SettingRow(conditions ? L("插件条件", "Plugin conditions") : L("插件事件", "Plugin triggers"),
                conditions ? L("运行前必须全部满足；不可用或过期时不执行。", "All must be true before running; unavailable/stale conditions block execution.") : L("插件停用后保留配置，事件不再触发。", "Settings are retained when a plugin is disabled."), row));
        }
    }

    private string TriggerName(AutomationTriggerKind trigger) => trigger switch
    {
        AutomationTriggerKind.AcAdapterConnected =>
            L("电源适配器插入", "AC adapter connected"),
        AutomationTriggerKind.AcAdapterDisconnected =>
            L("电源适配器断开", "AC adapter disconnected"),
        AutomationTriggerKind.GameStarted => L("游戏开启", "Game started"),
        AutomationTriggerKind.GameStopped => L("游戏结束", "Game stopped"),
        AutomationTriggerKind.ToolkitStarted => L("Toolkit 启动完成", "Toolkit started"),
        AutomationTriggerKind.SystemResumed => L("睡眠 / 休眠唤醒", "Resume from sleep / hibernation"),
        AutomationTriggerKind.SessionLocked => L("锁屏", "Session locked"),
        AutomationTriggerKind.SessionUnlocked => L("解锁", "Session unlocked"),
        _ => trigger.ToString()
    };

    private sealed class AutomationViewModel : ToolkitViewModelBase
    {
        public AutomationViewModel(ToolkitRuntimeService runtime)
            : base(runtime)
        {
        }
    }

    public override void Dispose()
    {
        _disposed = true;
        Runtime.AutomationExecution.Changed -= OnExecutionChanged;
        Runtime.AutomationChanged -= OnExecutionChanged;
        Runtime.MacroChanged -= OnMacroChanged;
        _macroPanel.Dispose();
        base.Dispose();
    }

    private void OnExecutionChanged(object? sender, EventArgs args)
    {
        if (_disposed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => OnExecutionChanged(sender, args))); return; }
        _automationEnabled.IsChecked = Runtime.Settings.AutomationEnabled;
        RenderAutomations();
        var allStates = Runtime.AutomationExecution.States;
        var active = allStates.Count(s => s.Phase is AutomationRunPhase.Running or AutomationRunPhase.Queued);
        _executionDetails.Visibility = allStates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _executionDetails.Header = active > 0 ? L($"运行记录 · {active} 项进行中", $"Run history · {active} active") : L("运行记录", "Run history");
        _cancelRuns.IsEnabled = active > 0;
        _cancelRuns.Visibility = active > 0 ? Visibility.Visible : Visibility.Collapsed;
        var states = allStates.Take(8).ToArray();
        _executionInfo.Text = states.Length == 0 ? L("尚无运行记录。", "No runs yet.") : string.Join("\n", states.Select(state =>
            $"{state.Name} · {RunPhase(state.Phase)} · {state.Step}/{state.Total}" + (state.Error.Length == 0 ? "" : " · " + state.Error)));
    }
    private string RunPhase(AutomationRunPhase phase) => phase switch
    {
        AutomationRunPhase.Queued => L("排队中", "Queued"),
        AutomationRunPhase.Running => L("运行中", "Running"),
        AutomationRunPhase.Completed => L("已完成", "Completed"),
        AutomationRunPhase.Cancelled => L("已取消", "Cancelled"),
        _ => L("失败", "Failed")
    };
}
