using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace ThinkBookToolkit;

internal sealed class ToolkitSoundPage : ToolkitPageBase,
    IControlStateRefreshable
{
    private readonly SoundViewModel _viewModel;
    private readonly CheckBox _dolbyEnabled = new();
    private readonly ComboBox _dolbyProfile = new() { MinWidth = 180 };
    private readonly CheckBox _speakerNoise = new();
    private readonly ComboBox _microphoneMode = new() { MinWidth = 230 };
    private readonly Button _recordVoice;
    private readonly TextBlock _status;
    private bool _syncing;

    public ToolkitSoundPage(ToolkitRuntimeService runtime) : base(runtime)
    {
        _viewModel = new SoundViewModel(runtime);
        DataContext = _viewModel;
        _status = StatusText();
        _status.SetBinding(TextBlock.TextProperty, new Binding(nameof(SoundViewModel.Status)));
        _recordVoice = ActionButton(L("录制我的声音", "Record my voice"));
        Content = BuildLayout();
        Loaded += async (_, _) => await LoadAsync();
    }

    private UIElement BuildLayout()
    {
        AddChoice(_dolbyProfile, L("动态", "Dynamic"), DolbyProfile.Dynamic);
        AddChoice(_dolbyProfile, L("电影", "Movie"), DolbyProfile.Movie);
        AddChoice(_dolbyProfile, L("音乐", "Music"), DolbyProfile.Music);
        AddChoice(_dolbyProfile, L("游戏", "Game"), DolbyProfile.Game);
        AddChoice(_dolbyProfile, L("语音", "Voice"), DolbyProfile.Voice);
        AddChoice(_dolbyProfile, L("自定义", "Custom"), DolbyProfile.Custom);
        AddChoice(_microphoneMode, L("正常", "Normal"), MicrophoneNoiseMode.Normal);
        AddChoice(_microphoneMode, L("声音识别", "Voice recognition"), MicrophoneNoiseMode.VoiceRecognition);
        AddChoice(_microphoneMode, L("仅我的声音", "Only my voice"), MicrophoneNoiseMode.OnlyMyVoice);
        AddChoice(_microphoneMode, L("多人声音", "Multiple voices"), MicrophoneNoiseMode.MultipleVoices);
        AddChoice(_microphoneMode, L("关", "Off"), MicrophoneNoiseMode.Off);

        _dolbyEnabled.Click += async (_, _) =>
        {
            if (!_syncing)
                await RunAsync(() => _viewModel.SetDolbyAsync(_dolbyEnabled.IsChecked == true, Selected(_dolbyProfile, DolbyProfile.Dynamic)));
        };
        _dolbyProfile.SelectionChanged += async (_, _) =>
        {
            if (!_syncing && Selected<DolbyProfile>(_dolbyProfile) is { } profile)
                await RunAsync(() => _viewModel.SetDolbyAsync(_dolbyEnabled.IsChecked == true, profile));
        };
        _speakerNoise.Click += async (_, _) =>
        {
            if (!_syncing)
                await RunAsync(() => _viewModel.SetSpeakerAsync(_speakerNoise.IsChecked == true));
        };
        _microphoneMode.SelectionChanged += async (_, _) =>
        {
            if (!_syncing && Selected<MicrophoneNoiseMode>(_microphoneMode) is { } mode)
                await RunAsync(() => _viewModel.SetMicrophoneAsync(mode));
        };
        _recordVoice.Click += async (_, _) =>
        {
            if (_viewModel.State?.MicrophoneNoise.HasVoiceId == true &&
                MessageBox.Show(
                    L("已有声纹。要重新录制并替换它吗？", "A voice ID already exists. Replace it?"),
                    "ThinkBook Toolkit",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }
            await RunAsync(() => _viewModel.RecordVoiceAsync(
                _viewModel.State?.MicrophoneNoise.HasVoiceId == true));
        };

        var root = new StackPanel();
        if (Show(FeatureIds.DolbyAtmos))
        {
            var dolby = new StackPanel();
            dolby.Children.Add(SettingRow(
                "Dolby Atmos",
                L("启用 Lenovo Dolby DAX 音效。", "Enable Lenovo Dolby DAX audio processing."),
                _dolbyEnabled,
                "\uE767"));
            dolby.Children.Add(SettingRow(
                L("音效配置", "Sound profile"),
                L("选择适合内容类型的 Dolby 配置。", "Choose a Dolby profile for the current content."),
                _dolbyProfile));
            root.Children.Add(Card("Dolby Atmos", dolby, null, "\uE767", "#A984FF"));
        }

        if (Show(FeatureIds.SpeakerNoiseCancellation))
        {
            root.Children.Add(Card(
                L("扬声器降噪", "Speaker noise cancellation"),
                SettingRow(
                    L("扬声器降噪", "Speaker noise cancellation"),
                    L("使用 Lenovo 智能降噪功能。", "Uses Lenovo smart noise reduction."),
                    _speakerNoise,
                    "\uE995"),
                null,
                "\uE995"));
        }

        if (Show(FeatureIds.MicrophoneNoiseCancellation))
        {
            var microphone = new StackPanel();
            microphone.Children.Add(SettingRow(
                L("麦克风降噪模式", "Microphone noise mode"),
                L("可用选项取决于当前降噪芯片和驱动。", "Available options depend on the installed noise processor and driver."),
                _microphoneMode,
                "\uE720"));
            microphone.Children.Add(SettingRow(
                L("个人声纹", "Personal voice ID"),
                L("仅“我的声音”模式使用；录制期间请保持环境安静。", "Used by Only my voice; keep the room quiet while recording."),
                _recordVoice));
            root.Children.Add(Card(
                L("麦克风降噪", "Microphone noise cancellation"),
                microphone,
                L("录制会在本页显示状态，不会打开另一设置窗口。", "Recording status stays on this page."),
                "\uE720",
                "#43B7E8"));
        }

        _status.Margin = new Thickness(4, 2, 4, 10);
        root.Children.Add(_status);
        if (root.Children.Count == 1)
            root.Children.Insert(0, EmptyState(L("此设备没有可用的声音调节功能。", "No sound controls are available on this device.")));
        return root;
    }

    private bool Show(string featureId) => Runtime.Report?.IsAvailable(featureId) != false;

    private async Task LoadAsync()
    {
        if (_viewModel.State is null)
            await _viewModel.LoadAsync();
        SyncControls();
    }

    public Task RefreshControlStateAsync() => LoadAsync();

    private async Task RunAsync(Func<Task> action)
    {
        SetEnabled(false);
        await action();
        SyncControls();
    }

    private void SyncControls()
    {
        _syncing = true;
        if (_viewModel.State is { } state)
        {
            _dolbyEnabled.IsChecked = state.Dolby.Enabled;
            Select(_dolbyProfile, state.Dolby.Profile);
            _speakerNoise.IsChecked = state.SpeakerNoise.Enabled;
            foreach (var item in _microphoneMode.Items.OfType<ComboBoxItem>())
            {
                item.Visibility = item.Tag is MicrophoneNoiseMode mode && Supports(state.MicrophoneNoise, mode)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            Select(_microphoneMode, state.MicrophoneNoise.Mode);
            _recordVoice.Content = state.MicrophoneNoise.HasVoiceId
                ? L("重新录制", "Record again")
                : L("录制我的声音", "Record my voice");
        }
        _syncing = false;
        SetEnabled(true);
    }

    private void SetEnabled(bool value)
    {
        var state = _viewModel.State;
        _dolbyEnabled.IsEnabled = value && state?.Dolby.Available == true;
        _dolbyProfile.IsEnabled = value && state?.Dolby is { Available: true, Enabled: true };
        _speakerNoise.IsEnabled = value && state?.SpeakerNoise.Available == true;
        _microphoneMode.IsEnabled = value && state?.MicrophoneNoise.Available == true;
        _recordVoice.IsEnabled = value && state?.MicrophoneNoise is { Available: true, SupportsOnlyMyVoice: true };
    }

    private static bool Supports(MicrophoneNoiseState state, MicrophoneNoiseMode mode) => mode switch
    {
        MicrophoneNoiseMode.MultipleVoices => state.SupportsMultipleVoices,
        MicrophoneNoiseMode.VoiceRecognition => state.SupportsVoiceRecognition,
        MicrophoneNoiseMode.OnlyMyVoice => state.SupportsOnlyMyVoice,
        _ => true
    };

    private static void AddChoice<T>(ComboBox combo, string label, T value) =>
        combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });

    private static T? Selected<T>(ComboBox combo) where T : struct =>
        combo.SelectedItem is ComboBoxItem { Tag: T value } ? value : null;

    private static T Selected<T>(ComboBox combo, T fallback) where T : struct =>
        Selected<T>(combo) ?? fallback;

    private static void Select<T>(ComboBox combo, T value) =>
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, value));

    private sealed class SoundViewModel : ToolkitViewModelBase
    {
        public SoundViewModel(ToolkitRuntimeService runtime) : base(runtime) { }
        public SoundSettingsState? State { get; private set; }

        public async Task LoadAsync()
        {
            IsBusy = true;
            try
            {
                State = await Task.Run(SoundSettingsController.ReadState);
                Status = string.Empty;
            }
            catch (Exception ex)
            {
                Status = Runtime.L("读取失败：", "Read failed: ") + ex.Message;
            }
            finally { IsBusy = false; }
        }

        public async Task SetDolbyAsync(bool enabled, DolbyProfile profile)
        {
            if (State is null) return;
            var previous = State;
            await WriteAsync(
                async () => State = previous with
                {
                    Dolby = await Task.Run(() => SoundSettingsController.SetDolbyState(enabled, profile))
                },
                () => State = previous);
        }

        public async Task SetSpeakerAsync(bool enabled)
        {
            if (State is null) return;
            var previous = State;
            await WriteAsync(
                async () => State = previous with
                {
                    SpeakerNoise = await Task.Run(() => SoundSettingsController.SetSpeakerNoiseEnabled(enabled))
                },
                () => State = previous);
        }

        public async Task SetMicrophoneAsync(MicrophoneNoiseMode mode)
        {
            if (State is null) return;
            var previous = State;
            await WriteAsync(
                async () => State = previous with
                {
                    MicrophoneNoise = await Task.Run(() => SoundSettingsController.SetMicrophoneNoiseMode(mode))
                },
                () => State = previous);
        }

        public async Task RecordVoiceAsync(bool replace)
        {
            if (State is null) return;
            var previous = State;
            IsBusy = true;
            try
            {
                var recording = Task.Run(() => SoundSettingsController.RecordMicrophoneVoiceId(replace));
                for (var remaining = 20; remaining > 0 && !recording.IsCompleted; remaining--)
                {
                    Status = Runtime.L(
                        $"正在录制声纹：还剩 {remaining} 秒，请保持安静并正常说话。",
                        $"Recording voice ID: {remaining} seconds remaining. Speak normally in a quiet room.");
                    await Task.WhenAny(recording, Task.Delay(1000));
                }
                if (!recording.IsCompleted)
                    Status = Runtime.L("正在处理声纹……", "Processing voice ID…");
                var confirmed = await recording;
                State = previous with { MicrophoneNoise = confirmed };
                Status = Runtime.L("声纹录制完成", "Voice ID recorded");
            }
            catch (Exception ex)
            {
                try
                {
                    State = await Task.Run(SoundSettingsController.ReadState);
                }
                catch
                {
                    State = previous;
                }
                Status = Runtime.L("录制失败：", "Recording failed: ") + ex.Message;
            }
            finally { IsBusy = false; }
        }

        private async Task WriteAsync(Func<Task> action, Action rollback)
        {
            IsBusy = true;
            try
            {
                await action();
                Status = string.Empty;
            }
            catch (Exception ex)
            {
                rollback();
                Status = Runtime.L("写入失败，已恢复原状态：", "Write failed; previous state restored: ") + ex.Message;
            }
            finally { IsBusy = false; }
        }
    }
}
