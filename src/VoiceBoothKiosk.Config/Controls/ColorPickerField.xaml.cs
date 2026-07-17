using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace VoiceBoothKiosk.Config.Controls;

public partial class ColorPickerField : UserControl, INotifyPropertyChanged
{
    public static readonly DependencyProperty SelectedColorProperty = DependencyProperty.Register(
        nameof(SelectedColor),
        typeof(string),
        typeof(ColorPickerField),
        new FrameworkPropertyMetadata(
            "#000000",
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            SelectedColorChanged));

    public static readonly DependencyProperty FieldNameProperty = DependencyProperty.Register(
        nameof(FieldName),
        typeof(string),
        typeof(ColorPickerField),
        new PropertyMetadata("Color hexadecimal value"));

    private double _draftRed;
    private double _draftGreen;
    private double _draftBlue;
    private Brush _draftBrush = Brushes.Black;
    private string _draftHex = "#000000";
    private Brush _currentBrush = Brushes.Black;
    private Brush _currentBorderBrush = new SolidColorBrush(Color.FromRgb(102, 112, 133));
    private string _currentColorDescription = "Open visual color picker";

    public ColorPickerField()
    {
        InitializeComponent();
        UpdateCurrentPreview(SelectedColor);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SelectedColor
    {
        get => (string)GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    public string FieldName
    {
        get => (string)GetValue(FieldNameProperty);
        set => SetValue(FieldNameProperty, value);
    }

    public double DraftRed
    {
        get => _draftRed;
        set => SetDraftChannel(ref _draftRed, value, nameof(DraftRed));
    }

    public double DraftGreen
    {
        get => _draftGreen;
        set => SetDraftChannel(ref _draftGreen, value, nameof(DraftGreen));
    }

    public double DraftBlue
    {
        get => _draftBlue;
        set => SetDraftChannel(ref _draftBlue, value, nameof(DraftBlue));
    }

    public Brush DraftBrush
    {
        get => _draftBrush;
        private set
        {
            _draftBrush = value;
            OnPropertyChanged();
        }
    }

    public string DraftHex
    {
        get => _draftHex;
        private set
        {
            _draftHex = value;
            OnPropertyChanged();
        }
    }

    public Brush CurrentBrush
    {
        get => _currentBrush;
        private set
        {
            _currentBrush = value;
            OnPropertyChanged();
        }
    }

    public Brush CurrentBorderBrush
    {
        get => _currentBorderBrush;
        private set
        {
            _currentBorderBrush = value;
            OnPropertyChanged();
        }
    }

    public string CurrentColorDescription
    {
        get => _currentColorDescription;
        private set
        {
            _currentColorDescription = value;
            OnPropertyChanged();
        }
    }

    private static void SelectedColorChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
    {
        var picker = (ColorPickerField)dependencyObject;
        picker.UpdateCurrentPreview(eventArgs.NewValue as string);
    }

    private void SwatchButton_Click(object sender, RoutedEventArgs e)
    {
        SetDraft(TryParseColor(SelectedColor, out var color) ? color : Colors.Black);
        PickerPopup.IsOpen = true;
    }

    private void PickerPopup_Opened(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => RedSlider.Focus());
    }

    private void PresetSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value } && TryParseColor(value, out var color))
        {
            SetDraft(color);
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        SelectedColor = DraftHex;
        PickerPopup.IsOpen = false;
        HexTextBox.Focus();
        HexTextBox.CaretIndex = HexTextBox.Text.Length;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        PickerPopup.IsOpen = false;
        SwatchButton.Focus();
    }

    private void PickerPanel_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        PickerPopup.IsOpen = false;
        SwatchButton.Focus();
        e.Handled = true;
    }

    private void SetDraft(Color color)
    {
        _draftRed = color.R;
        _draftGreen = color.G;
        _draftBlue = color.B;
        OnPropertyChanged(nameof(DraftRed));
        OnPropertyChanged(nameof(DraftGreen));
        OnPropertyChanged(nameof(DraftBlue));
        RefreshDraftPreview();
    }

    private void SetDraftChannel(ref double field, double value, string propertyName)
    {
        var normalized = Math.Clamp(Math.Round(value), byte.MinValue, byte.MaxValue);
        if (Math.Abs(field - normalized) < double.Epsilon)
        {
            return;
        }

        field = normalized;
        OnPropertyChanged(propertyName);
        RefreshDraftPreview();
    }

    private void RefreshDraftPreview()
    {
        var color = Color.FromRgb((byte)_draftRed, (byte)_draftGreen, (byte)_draftBlue);
        DraftBrush = CreateFrozenBrush(color);
        DraftHex = FormattableString.Invariant($"#{color.R:X2}{color.G:X2}{color.B:X2}");
    }

    private void UpdateCurrentPreview(string? value)
    {
        if (TryParseColor(value, out var color))
        {
            CurrentBrush = CreateFrozenBrush(color);
            CurrentBorderBrush = CreateFrozenBrush(Color.FromRgb(102, 112, 133));
            CurrentColorDescription = $"Open visual color picker. Current color {value}.";
            return;
        }

        CurrentBrush = Brushes.Transparent;
        CurrentBorderBrush = CreateFrozenBrush(Color.FromRgb(217, 45, 32));
        CurrentColorDescription = "Open visual color picker. The current hexadecimal value is incomplete or invalid.";
    }

    private static bool TryParseColor(string? value, out Color color)
    {
        color = default;
        if (value is null || (value.Length != 7 && value.Length != 9) || value[0] != '#')
        {
            return false;
        }

        var offset = value.Length == 9 ? 3 : 1;
        if (!byte.TryParse(value.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var red) ||
            !byte.TryParse(value.AsSpan(offset + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var green) ||
            !byte.TryParse(value.AsSpan(offset + 4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var blue))
        {
            return false;
        }

        color = Color.FromRgb(red, green, blue);
        return true;
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
