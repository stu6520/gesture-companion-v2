using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using ToggleButton = System.Windows.Controls.Primitives.ToggleButton;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseWheelEventArgs = System.Windows.Input.MouseWheelEventArgs;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;

namespace GestureCompanionPointerHost;

public partial class SettingsWindow : Window
{
    private readonly PointerHostSettings _originalSettings;
    private readonly Dictionary<string, TextBox> _boxes = new();
    private readonly Dictionary<string, ToggleButton> _checks = new();
    private readonly Dictionary<string, string> _disabledPlaceholders = new();
    private readonly List<Action> _optionStateRefreshers = new();
    private readonly Dictionary<string, SliderBinding> _sliders = new();

    public PointerHostSettings ResultSettings { get; private set; }

    public SettingsWindow(PointerHostSettings current)
    {
        InitializeComponent();
        _originalSettings = current.Clone();
        ResultSettings = current.Clone();
        BuildRows();
        LoadSettingsToUi(ResultSettings);
    }

    private void BuildRows()
    {
        AddRow("Pinch open", nameof(PointerHostSettings.PinchOpenShortcut));
        AddRow("Pinch close", nameof(PointerHostSettings.PinchCloseShortcut));
        AddRow("Rotate clockwise", nameof(PointerHostSettings.RotateClockwiseShortcut));
        AddRow("Rotate counter-clockwise", nameof(PointerHostSettings.RotateCounterClockwiseShortcut));
        AddRow("Pan drag (+ Left drag)", nameof(PointerHostSettings.PanDragShortcut));
        AddRow("1-finger slide", nameof(PointerHostSettings.OneFingerSlideShortcut),
            nameof(PointerHostSettings.OneFingerSlideUsesPan), "Pan",
            "Use the Pan key, sensitivity, and left-drag behavior");
        AddRow("1-finger hold (+ Left hold)", nameof(PointerHostSettings.OneFingerHoldShortcut),
            nameof(PointerHostSettings.OneFingerHoldUsesRightClick), "Right click",
            "Send one right click instead of holding the mapped hotkey");

        AddRow("2-finger tap", nameof(PointerHostSettings.TwoFingerTapShortcut));
        AddRow("3-finger tap", nameof(PointerHostSettings.ThreeFingerTapShortcut));
        AddRow("4-finger tap", nameof(PointerHostSettings.FourFingerTapShortcut));

        RowsPanel.Children.Add(new TextBlock
        {
            Text = "Gesture Response",
            FontSize = 14,
            Foreground = System.Windows.Media.Brushes.LightCyan,
            Margin = new Thickness(0, 10, 0, 6)
        });

        var responseGroups = new Grid();
        responseGroups.ColumnDefinitions.Add(new ColumnDefinition());
        responseGroups.ColumnDefinitions.Add(new ColumnDefinition());
        responseGroups.ColumnDefinitions.Add(new ColumnDefinition());
        AddResponseGroup(responseGroups, 0, "Zoom",
            nameof(PointerHostSettings.ZoomStepPixels), 1, 60, "px",
            nameof(PointerHostSettings.ZoomIntervalMilliseconds));
        AddResponseGroup(responseGroups, 1, "Rotate",
            nameof(PointerHostSettings.RotateStepDegrees), 1, 15, "deg",
            nameof(PointerHostSettings.RotateIntervalMilliseconds));
        AddResponseGroup(responseGroups, 2, "Pan",
            nameof(PointerHostSettings.PanStepPixels), 1, 60, "px", null);
        RowsPanel.Children.Add(responseGroups);
    }

    private void AddRow(string label, string propertyName, string? optionCheckProperty = null,
        string? optionLabel = null, string? optionToolTip = null)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock { Text = label };
        text.Style = (Style)RowsPanel.FindResource("RowLabelStyle");
        row.Children.Add(text);

        var box = new TextBox { Tag = propertyName, Margin = new Thickness(8, 0, 8, 0) };
        box.Style = (Style)RowsPanel.FindResource("HotkeyBoxStyle");
        box.PreviewKeyDown += HotkeyBox_OnPreviewKeyDown;
        Grid.SetColumn(box, 1);
        row.Children.Add(box);
        _boxes[propertyName] = box;


        var delete = new Button
        {
            Content = "\u232B",
            ToolTip = "Remove last key",
            Tag = box,
            Width = 32,
            Height = 26,
            FontSize = 15,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI Symbol"),
            Margin = new Thickness(0, 0, 4, 0)
        };
        delete.Style = (Style)RowsPanel.FindResource("HotkeyRowButtonStyle");
        delete.Click += DeleteHotkey_OnClick;
        Grid.SetColumn(delete, 2);
        row.Children.Add(delete);

        var clear = new Button
        {
            Content = "\uE8BB",
            ToolTip = "Clear hotkey",
            Tag = box,
            Width = 32,
            Height = 26,
            FontSize = 12,
            FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets")
        };
        clear.Style = (Style)RowsPanel.FindResource("HotkeyRowButtonStyle");
        clear.Click += ClearHotkey_OnClick;
        Grid.SetColumn(clear, 3);
        row.Children.Add(clear);

        if (optionCheckProperty is not null)
        {
            var optionCheck = new ToggleButton
            {
                Content = optionLabel ?? string.Empty,
                ToolTip = optionToolTip,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(207, 239, 255)),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(8, 0, 0, 0),
                MinWidth = 88,
                Height = 28
            };
            optionCheck.Style = (Style)RowsPanel.FindResource("GlassyToggleButtonStyle");
            var disabledPlaceholder = $"Key disabled for {optionLabel}";
            _disabledPlaceholders[propertyName] = disabledPlaceholder;
            void UpdateStandaloneMappingState()
            {
                var enabled = optionCheck.IsChecked != true;
                if (!enabled && string.IsNullOrWhiteSpace(box.Text))
                {
                    box.Text = disabledPlaceholder;
                }
                else if (enabled && box.Text == disabledPlaceholder)
                {
                    box.Text = string.Empty;
                }

                box.Foreground = box.Text == disabledPlaceholder
                    ? new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(126, 146, 157))
                    : new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(234, 248, 255));
                box.IsEnabled = enabled;
                delete.IsEnabled = enabled;
                clear.IsEnabled = enabled;
            }
            optionCheck.Checked += (_, _) => UpdateStandaloneMappingState();
            optionCheck.Unchecked += (_, _) => UpdateStandaloneMappingState();
            Grid.SetColumn(optionCheck, 4);
            row.Children.Add(optionCheck);
            _checks[optionCheckProperty] = optionCheck;
            _optionStateRefreshers.Add(UpdateStandaloneMappingState);
            UpdateStandaloneMappingState();
        }

        RowsPanel.Children.Add(row);
    }

    private void AddResponseGroup(Grid parent, int column, string title,
        string movementProperty, double movementMinimum, double movementMaximum, string movementSuffix,
        string? intervalProperty)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = System.Windows.Media.Brushes.LightCyan,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4)
        });
        AddCompactSlider(panel, "Sensitivity", movementProperty, movementMinimum, movementMaximum, movementSuffix, "Low", "High");
        if (intervalProperty is not null)
        {
            AddCompactSlider(panel, "Speed", intervalProperty, 0, 100, "ms", "Slow", "Fast");
        }

        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(9, 7, 9, 6),
            Margin = new Thickness(column == 0 ? 0 : 4, 0, column == 2 ? 0 : 4, 0),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(20, 0, 0, 0)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(68, 191, 231, 243)),
            BorderThickness = new Thickness(1),
            Child = panel
        };
        Grid.SetColumn(card, column);
        parent.Children.Add(card);
    }

    private void AddCompactSlider(Panel parent, string label, string propertyName,
        double minimum, double maximum, string suffix, string lowLabel, string highLabel)
    {
        var row = new Grid { Margin = new Thickness(0, 1, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        row.ColumnDefinitions.Add(new ColumnDefinition());

        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(207, 239, 255)),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 7, 7, 0)
        };
        row.Children.Add(labelText);

        var valueText = new TextBlock
        {
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(235, 250, 255)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0),
            IsHitTestVisible = false
        };
        var slider = new Slider
        {
            Minimum = minimum,
            Maximum = maximum,
            TickFrequency = 1,
            IsSnapToTickEnabled = true
        };
        slider.Style = (Style)RowsPanel.FindResource("GlassySliderStyle");

        var dragSurface = new Border
        {
            Background = System.Windows.Media.Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Arrow,
            Tag = slider
        };
        dragSurface.PreviewMouseWheel += Slider_OnPreviewMouseWheel;
        dragSurface.PreviewMouseLeftButtonDown += Slider_OnPreviewMouseLeftButtonDown;
        dragSurface.PreviewMouseMove += Slider_OnPreviewMouseMove;
        dragSurface.PreviewMouseLeftButtonUp += Slider_OnPreviewMouseLeftButtonUp;

        var sliderColumn = new StackPanel();
        var sliderMeter = new Grid { Height = 26 };
        sliderMeter.Children.Add(slider);
        sliderMeter.Children.Add(dragSurface);
        sliderMeter.Children.Add(valueText);
        sliderColumn.Children.Add(sliderMeter);
        sliderColumn.Children.Add(CreateSliderScaleLabels(lowLabel, highLabel));
        Grid.SetColumn(sliderColumn, 1);
        row.Children.Add(sliderColumn);
        parent.Children.Add(row);

        var binding = new SliderBinding(slider, valueText, suffix, ShowAsPercentage: true);
        slider.ValueChanged += (_, _) => UpdateSliderValue(binding);
        _sliders[propertyName] = binding;
        UpdateSliderValue(binding);
    }
    private static void Slider_OnPreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement surface || surface.Tag is not Slider slider)
        {
            return;
        }

        surface.CaptureMouse();
        SetSliderValueFromPointer(slider, e.GetPosition(slider).X);
        e.Handled = true;
    }

    private static void Slider_OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not FrameworkElement surface ||
            surface.Tag is not Slider slider ||
            !surface.IsMouseCaptured ||
            e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            return;
        }

        SetSliderValueFromPointer(slider, e.GetPosition(slider).X);
        e.Handled = true;
    }

    private static void Slider_OnPreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement surface ||
            surface.Tag is not Slider slider ||
            !surface.IsMouseCaptured)
        {
            return;
        }

        SetSliderValueFromPointer(slider, e.GetPosition(slider).X);
        surface.ReleaseMouseCapture();
        e.Handled = true;
    }

    private static void SetSliderValueFromPointer(Slider slider, double pointerX)
    {
        if (slider.ActualWidth <= 0)
        {
            return;
        }

        var ratio = Math.Clamp(pointerX / slider.ActualWidth, 0, 1);
        var value = slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
        var step = slider.TickFrequency > 0 ? slider.TickFrequency : 1;
        slider.Value = Math.Clamp(
            Math.Round(value / step, MidpointRounding.AwayFromZero) * step,
            slider.Minimum,
            slider.Maximum);
    }
    private static void Slider_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Slider slider } || e.Delta == 0)
        {
            return;
        }

        var notchCount = Math.Max(1, Math.Abs(e.Delta) / 120);
        var direction = Math.Sign(e.Delta);
        var step = slider.TickFrequency > 0 ? slider.TickFrequency : 1;
        slider.Value = Math.Clamp(
            slider.Value + direction * notchCount * step,
            slider.Minimum,
            slider.Maximum);
        e.Handled = true;
    }
    private static Grid CreateSliderScaleLabels(string lowLabel, string highLabel)
    {
        var labels = new Grid { Margin = new Thickness(2, -1, 2, 3) };
        labels.ColumnDefinitions.Add(new ColumnDefinition());
        labels.ColumnDefinitions.Add(new ColumnDefinition());
        var low = new TextBlock
        {
            Text = lowLabel,
            FontSize = 9,
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(175, 207, 239, 255)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        var high = new TextBlock
        {
            Text = highLabel,
            FontSize = 9,
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(175, 207, 239, 255)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        Grid.SetColumn(high, 1);
        labels.Children.Add(low);
        labels.Children.Add(high);
        return labels;
    }

    private static double ToSliderValue(SliderBinding binding, double settingValue) =>
        binding.Slider.Minimum + binding.Slider.Maximum - settingValue;

    private static double ToSettingValue(SliderBinding binding) =>
        binding.Slider.Minimum + binding.Slider.Maximum - binding.Slider.Value;

    private static void UpdateSliderValue(SliderBinding binding)
    {
        if (binding.ShowAsPercentage)
        {
            var range = binding.Slider.Maximum - binding.Slider.Minimum;
            var percentage = range <= 0
                ? 0
                : (binding.Slider.Value - binding.Slider.Minimum) / range * 100;
            binding.ValueText.Text = $"{percentage:0}%";
            return;
        }

        var settingValue = ToSettingValue(binding);
        binding.ValueText.Text = $"{settingValue:0}{binding.Suffix}";
    }
    private void LoadSettingsToUi(PointerHostSettings settings)
    {
        var type = typeof(PointerHostSettings);
        foreach (var pair in _boxes)
        {
            pair.Value.Text = (string?)type.GetProperty(pair.Key)?.GetValue(settings) ?? string.Empty;
        }
        foreach (var pair in _checks)
        {
            pair.Value.IsChecked = (bool?)type.GetProperty(pair.Key)?.GetValue(settings) == true;
        }
        foreach (var refresh in _optionStateRefreshers)
        {
            refresh();
        }
        foreach (var pair in _sliders)
        {
            var settingValue = Convert.ToDouble(type.GetProperty(pair.Key)?.GetValue(settings), CultureInfo.InvariantCulture);
            pair.Value.Slider.Value = ToSliderValue(pair.Value, settingValue);
            UpdateSliderValue(pair.Value);
        }
    }

    private void CaptureUi()
    {
        var type = typeof(PointerHostSettings);
        foreach (var pair in _boxes)
        {
            var value = pair.Value.Text.Trim();
            if (_disabledPlaceholders.TryGetValue(pair.Key, out var placeholder) && value == placeholder)
            {
                value = string.Empty;
            }
            type.GetProperty(pair.Key)?.SetValue(ResultSettings, value);
        }
        foreach (var pair in _checks)
        {
            type.GetProperty(pair.Key)?.SetValue(ResultSettings, pair.Value.IsChecked == true);
        }
        foreach (var pair in _sliders)
        {
            type.GetProperty(pair.Key)?.SetValue(ResultSettings, ToSettingValue(pair.Value));
        }
        ResultSettings.Normalize();
    }

    private void ApplyButton_OnClick(object sender, RoutedEventArgs e)
    {
        CaptureUi();
        DialogResult = true;
        Close();
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ResetButton_OnClick(object sender, RoutedEventArgs e) => LoadSettingsToUi(_originalSettings.Clone());
    private void DefaultButton_OnClick(object sender, RoutedEventArgs e) => LoadSettingsToUi(PointerHostSettings.CreateDefaults());

    private void HotkeyBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (e.Key == Key.Tab) return;
        if (e.Key == Key.Escape)
        {
            box.Text = string.Empty;
            e.Handled = true;
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
            Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }

        var parts = new List<string>();
        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

        var keyPart = KeyToShortcutToken(key);
        if (!string.IsNullOrWhiteSpace(keyPart))
        {
            parts.Add(keyPart);
            box.Text = string.Join("+", parts);
        }
        e.Handled = true;
    }

    private static string KeyToShortcutToken(Key key)
    {
        if (key >= Key.A && key <= Key.Z) return key.ToString().ToUpperInvariant();
        if (key >= Key.D0 && key <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return ((char)('0' + (key - Key.NumPad0))).ToString();
        if (key >= Key.F1 && key <= Key.F24) return key.ToString().ToUpperInvariant();
        return key switch
        {
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Home => "Home",
            Key.End => "End",
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            Key.Space => "Space",
            Key.OemPlus or Key.Add => "Plus",
            Key.OemMinus or Key.Subtract => "Minus",
            _ => string.Empty
        };
    }

    private void ClearHotkey_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TextBox box }) box.Text = string.Empty;
    }

    private void DeleteHotkey_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TextBox box } || string.IsNullOrEmpty(box.Text)) return;
        var parts = box.Text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
        box.Text = string.Join("+", parts);
    }

    private sealed record SliderBinding(Slider Slider, TextBlock ValueText, string Suffix, bool ShowAsPercentage);}
