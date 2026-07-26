using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;

namespace ThinkBookToolkit;

internal sealed class TemperatureEditor : Grid
{
    private readonly Slider _slider = new()
    {
        Minimum = PcManagerEyeCareController.MinimumTemperature,
        Maximum = PcManagerEyeCareController.MaximumTemperature,
        IsDirectionReversed = true,
        IsMoveToPointEnabled = true,
        TickFrequency = 50,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 10, 0)
    };
    private readonly TextBox _valueBox = new()
    {
        Width = 62,
        MinHeight = 26,
        HorizontalContentAlignment = HorizontalAlignment.Right,
        VerticalContentAlignment = VerticalAlignment.Center,
        Padding = new Thickness(4, 1, 4, 1)
    };
    private bool _synchronizing;
    private int _value = PcManagerEyeCareController.FactoryNormalTemperature;

    public TemperatureEditor()
    {
        ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });
        ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });

        _slider.Template = BuildColoredSliderTemplate();
        _slider.ValueChanged += (_, args) =>
        {
            if (_synchronizing)
                return;

            SetValue((int)Math.Round(args.NewValue), notify: true);
        };
        _valueBox.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter)
                return;

            CommitTextValue();
            args.Handled = true;
            Keyboard.ClearFocus();
        };
        _valueBox.LostKeyboardFocus += (_, _) => CommitTextValue();

        Children.Add(_slider);
        Grid.SetColumn(_valueBox, 1);
        Children.Add(_valueBox);
        var unit = new TextBlock
        {
            Text = "K",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(5, 0, 0, 0)
        };
        Grid.SetColumn(unit, 2);
        Children.Add(unit);
        SetValue(_value, notify: false);
    }

    public event EventHandler? ValueChanged;

    public int Value
    {
        get => _value;
        set => SetValue(value, notify: false);
    }

    public bool IsUserInteracting =>
        _slider.IsMouseCaptureWithin ||
        _valueBox.IsKeyboardFocusWithin;

    private void CommitTextValue()
    {
        if (int.TryParse(_valueBox.Text, out var value))
        {
            SetValue(value, notify: true);
            return;
        }

        SetValue(_value, notify: false);
    }

    private void SetValue(int value, bool notify)
    {
        value = Math.Clamp(
            value,
            PcManagerEyeCareController.MinimumTemperature,
            PcManagerEyeCareController.MaximumTemperature);
        var changed = _value != value;
        _value = value;
        _synchronizing = true;
        try
        {
            _slider.Value = value;
            _valueBox.Text = value.ToString();
        }
        finally
        {
            _synchronizing = false;
        }

        if (notify && changed)
            ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private static ControlTemplate BuildColoredSliderTemplate()
    {
        const string xaml = """
            <ControlTemplate
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                TargetType="{x:Type Slider}">
              <Grid Height="24" Background="Transparent">
                <Border Height="6"
                        CornerRadius="3"
                        VerticalAlignment="Center">
                  <Border.Background>
                    <LinearGradientBrush StartPoint="0,0.5" EndPoint="1,0.5">
                      <GradientStop Color="#3478F6" Offset="0" />
                      <GradientStop Color="#26C6DA" Offset="0.25" />
                      <GradientStop Color="#22C55E" Offset="0.5" />
                      <GradientStop Color="#F5C542" Offset="0.72" />
                      <GradientStop Color="#FF635E" Offset="1" />
                    </LinearGradientBrush>
                  </Border.Background>
                </Border>
                <Track x:Name="PART_Track"
                       IsDirectionReversed="{TemplateBinding IsDirectionReversed}">
                  <Track.DecreaseRepeatButton>
                    <RepeatButton Command="{x:Static Slider.DecreaseLarge}"
                                  Background="Transparent"
                                  BorderThickness="0"
                                  Opacity="0" />
                  </Track.DecreaseRepeatButton>
                  <Track.Thumb>
                    <Thumb Width="18" Height="18">
                      <Thumb.Template>
                        <ControlTemplate TargetType="{x:Type Thumb}">
                          <Ellipse Fill="#FFFFFF"
                                   Stroke="#7C8799"
                                   StrokeThickness="1.5" />
                        </ControlTemplate>
                      </Thumb.Template>
                    </Thumb>
                  </Track.Thumb>
                  <Track.IncreaseRepeatButton>
                    <RepeatButton Command="{x:Static Slider.IncreaseLarge}"
                                  Background="Transparent"
                                  BorderThickness="0"
                                  Opacity="0" />
                  </Track.IncreaseRepeatButton>
                </Track>
              </Grid>
            </ControlTemplate>
            """;
        return (ControlTemplate)XamlReader.Parse(xaml);
    }
}
