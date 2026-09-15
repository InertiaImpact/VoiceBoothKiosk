using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VoiceBoothKiosk.App.ViewModels;
using VoiceBoothKiosk.Supervision;

namespace VoiceBoothKiosk.App;

public interface IMainWindowActions
{
    ICommand StartSessionCommand { get; }
    bool CanOpenConfiguration { get; }
    Task InitializeAsync();
    Task OpenConfigurationAsync();
    Task SeekAsync(TimeSpan position);
    Task CommitClipNameAsync();
    void NotifyUserActivity();
    void HandleEscape();
}

public partial class MainWindow : Window
{
    private sealed record OriginalSize(double Value);
    private enum ProtectedHoldAction
    {
        Settings,
        Exit,
    }

    private static readonly TimeSpan ProtectedHoldDuration = TimeSpan.FromSeconds(4);
    private static bool IsWindowedTestMode =>
        Environment.GetCommandLineArgs().Contains("--windowed", StringComparer.OrdinalIgnoreCase);

    private bool _administratorExitRequested;
    private readonly ConditionalWeakTable<DependencyObject, OriginalSize> _originalFontSizes = new();
    private readonly ConditionalWeakTable<Button, OriginalSize> _originalButtonHeights = new();
    private bool _hasAppliedTextScale;
    private bool _hasAppliedControlSize;
    private double _appliedTextScale = 1;
    private double _appliedButtonHeight = 50;
    private readonly DispatcherTimer _protectedHoldTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private Button? _protectedHoldButton;
    private TouchDevice? _protectedHoldTouchDevice;
    private long _protectedHoldStartedTimestamp;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        PreviewMouseMove += OnUserMouseActivity;
        PreviewMouseDown += OnUserMouseActivity;
        PreviewTouchDown += OnUserTouchActivity;
        ClipListBox.ItemContainerGenerator.StatusChanged += OnClipContainerStatusChanged;
        _protectedHoldTimer.Tick += ProtectedHoldTimer_Tick;

        if (IsWindowedTestMode)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = WindowState.Normal;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.ClipNameFocusRequested += OnClipNameFocusRequested;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        if (DataContext is IMainWindowActions actions)
        {
            await RunUiActionAsync(actions.InitializeAsync);
        }

        if (DataContext is MainViewModel configuredViewModel)
        {
            ApplyWindowPolicy(configuredViewModel);
            ApplyTextScale(configuredViewModel.TextScale);
            ApplyControlSize(configuredViewModel.ControlButtonMinHeight);
        }

        if (!SessionNameTextBox.IsKeyboardFocusWithin)
        {
            SessionNameTextBox.Focus();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.ClipNameFocusRequested -= OnClipNameFocusRequested;
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
        ClipListBox.ItemContainerGenerator.StatusChanged -= OnClipContainerStatusChanged;
        _protectedHoldTimer.Tick -= ProtectedHoldTimer_Tick;
        CancelProtectedHold();
        base.OnClosed(e);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.TextScale) && sender is MainViewModel viewModel)
        {
            ApplyTextScale(viewModel.TextScale);
        }
        else if (e.PropertyName == nameof(MainViewModel.ControlButtonMinHeight) && sender is MainViewModel sizingViewModel)
        {
            ApplyControlSize(sizingViewModel.ControlButtonMinHeight);
        }
        else if (e.PropertyName is nameof(MainViewModel.FullScreen) or nameof(MainViewModel.AlwaysOnTop) &&
                 sender is MainViewModel windowPolicyViewModel)
        {
            ApplyWindowPolicy(windowPolicyViewModel);
        }
    }

    private void ApplyWindowPolicy(MainViewModel viewModel)
    {
        Topmost = viewModel.AlwaysOnTop;
        if (viewModel.FullScreen)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            return;
        }

        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        WindowState = WindowState.Normal;
    }

    private void ApplyControlSize(double baseHeight, bool includeNewElements = false)
    {
        baseHeight = Math.Clamp(baseHeight, 40, 64);
        if (!includeNewElements && Math.Abs(baseHeight - _appliedButtonHeight) < 0.001 && _hasAppliedControlSize)
        {
            return;
        }
        _appliedButtonHeight = baseHeight;
        var scale = baseHeight / 50d;

        var pending = new Queue<DependencyObject>();
        pending.Enqueue(this);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (current is Button button)
            {
                if (!_originalButtonHeights.TryGetValue(button, out var originalSize))
                {
                    originalSize = new(button.MinHeight);
                    _originalButtonHeights.Add(button, originalSize);
                }
                var targetHeight = Math.Max(32, originalSize.Value * scale);
                if (Math.Abs(button.MinHeight - targetHeight) > 0.001)
                {
                    button.MinHeight = targetHeight;
                }
            }
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
            {
                pending.Enqueue(VisualTreeHelper.GetChild(current, index));
            }
        }
        _hasAppliedControlSize = true;
    }

    private void ApplyTextScale(double scale, bool includeNewElements = false)
    {
        scale = Math.Clamp(scale, 0.75, 2.0);
        if (!includeNewElements && Math.Abs(scale - _appliedTextScale) < 0.001 && _hasAppliedTextScale)
        {
            return;
        }
        _appliedTextScale = scale;

        var pending = new Queue<DependencyObject>();
        pending.Enqueue(this);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            switch (current)
            {
                case TextBlock text:
                    if (!_originalFontSizes.TryGetValue(text, out var originalTextSize))
                    {
                        originalTextSize = new(text.FontSize);
                        _originalFontSizes.Add(text, originalTextSize);
                    }
                    var scaledTextSize = originalTextSize.Value * scale;
                    if (Math.Abs(text.FontSize - scaledTextSize) > 0.001)
                    {
                        text.FontSize = scaledTextSize;
                    }
                    break;
                case Control control when control is not Window:
                    if (!_originalFontSizes.TryGetValue(control, out var originalControlSize))
                    {
                        originalControlSize = new(control.FontSize);
                        _originalFontSizes.Add(control, originalControlSize);
                    }
                    var scaledControlSize = originalControlSize.Value * scale;
                    if (Math.Abs(control.FontSize - scaledControlSize) > 0.001)
                    {
                        control.FontSize = scaledControlSize;
                    }
                    break;
            }

            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
            {
                pending.Enqueue(VisualTreeHelper.GetChild(current, index));
            }
        }
        _hasAppliedTextScale = true;
    }

    private void OnClipContainerStatusChanged(object? sender, EventArgs e)
    {
        if (ClipListBox.ItemContainerGenerator.Status != GeneratorStatus.ContainersGenerated)
        {
            return;
        }

        // Clip item templates are materialized after the initial window-level sizing pass.
        // Rescan at background priority so their complete visual trees exist before scaling.
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                ApplyTextScale(_appliedTextScale, includeNewElements: true);
                ApplyControlSize(_appliedButtonHeight, includeNewElements: true);
            }));
    }

    private void OnClipNameFocusRequested(object? sender, EventArgs e)
    {
        ClipNameTextBox.Focus();
        ClipNameTextBox.SelectAll();
    }

    private void SessionNameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not IMainWindowActions actions || !actions.StartSessionCommand.CanExecute(null))
        {
            return;
        }

        actions.StartSessionCommand.Execute(null);
        e.Handled = true;
    }

    private async void WaveformControl_SeekRequested(object? sender, TimeSpan position)
    {
        if (DataContext is IMainWindowActions actions)
        {
            await RunUiActionAsync(() => actions.SeekAsync(position));
        }
    }

    private async void ClipNameTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (DataContext is IMainWindowActions actions)
        {
            ClipNameTextBox.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            await RunUiActionAsync(actions.CommitClipNameAsync);
        }
    }

    private void ProtectedHoldButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button button || !StartProtectedHold(button))
        {
            return;
        }

        button.Focus();
        button.CaptureMouse();
    }

    private void ProtectedHoldButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CancelProtectedHold();
    }

    private void ProtectedHoldButton_MouseLeave(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(sender, _protectedHoldButton))
        {
            CancelProtectedHold();
        }
    }

    private void ProtectedHoldButton_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(sender, _protectedHoldButton) && _protectedHoldTouchDevice is null)
        {
            CancelProtectedHold();
        }
    }

    private void ProtectedHoldButton_PreviewTouchDown(object sender, TouchEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button button || !StartProtectedHold(button))
        {
            return;
        }

        button.Focus();
        _protectedHoldTouchDevice = e.TouchDevice;
        button.CaptureTouch(e.TouchDevice);
    }

    private void ProtectedHoldButton_PreviewTouchUp(object sender, TouchEventArgs e)
    {
        e.Handled = true;
        CancelProtectedHold();
    }

    private void ProtectedHoldButton_PreviewTouchMove(object sender, TouchEventArgs e)
    {
        if (!ReferenceEquals(sender, _protectedHoldButton) || sender is not Button button)
        {
            return;
        }

        var position = e.GetTouchPoint(button).Position;
        if (position.X < 0 || position.Y < 0 || position.X > button.ActualWidth || position.Y > button.ActualHeight)
        {
            CancelProtectedHold();
        }
    }

    private void ProtectedHoldButton_LostTouchCapture(object sender, TouchEventArgs e)
    {
        if (ReferenceEquals(sender, _protectedHoldButton))
        {
            CancelProtectedHold();
        }
    }

    private void ProtectedHoldButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter))
        {
            return;
        }

        e.Handled = true;
        if (!e.IsRepeat && sender is Button button)
        {
            StartProtectedHold(button);
        }
    }

    private void ProtectedHoldButton_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter)
        {
            e.Handled = true;
            CancelProtectedHold();
        }
    }

    private void ProtectedHoldButton_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _protectedHoldButton))
        {
            CancelProtectedHold();
        }
    }

    private bool StartProtectedHold(Button button)
    {
        if (DataContext is not IMainWindowActions actions || !button.IsEnabled || GetProtectedHoldAction(button) is not { } action)
        {
            return false;
        }
        if (action == ProtectedHoldAction.Settings && !actions.CanOpenConfiguration)
        {
            return false;
        }

        if (ReferenceEquals(button, _protectedHoldButton))
        {
            return true;
        }

        CancelProtectedHold();
        _protectedHoldButton = button;
        _protectedHoldStartedTimestamp = Stopwatch.GetTimestamp();
        if (GetProtectedHoldProgress(button) is { } progress)
        {
            progress.Value = 0;
            progress.Visibility = Visibility.Visible;
        }
        _protectedHoldTimer.Start();
        return true;
    }

    private async void ProtectedHoldTimer_Tick(object? sender, EventArgs e)
    {
        var button = _protectedHoldButton;
        if (button is null)
        {
            _protectedHoldTimer.Stop();
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(_protectedHoldStartedTimestamp);
        if (GetProtectedHoldProgress(button) is { } progress)
        {
            progress.Value = Math.Min(ProtectedHoldDuration.TotalSeconds, elapsed.TotalSeconds);
        }
        if (elapsed < ProtectedHoldDuration)
        {
            return;
        }

        var action = GetProtectedHoldAction(button);
        EndProtectedHold(button);
        if (action == ProtectedHoldAction.Exit)
        {
            _administratorExitRequested = true;
            if (SupervisedModeArguments.IsWatchdogChild(Environment.GetCommandLineArgs()))
            {
                System.Windows.Application.Current.Shutdown(SupervisedModeWatchdog.IntentionalExitCode);
            }
            else
            {
                Close();
            }
            return;
        }
        await OpenConfigurationAfterHoldAsync();
    }

    private void CancelProtectedHold()
    {
        if (_protectedHoldButton is { } button)
        {
            EndProtectedHold(button);
        }
        else
        {
            _protectedHoldTimer.Stop();
        }
    }

    private void EndProtectedHold(Button button)
    {
        var touchDevice = _protectedHoldTouchDevice;
        _protectedHoldButton = null;
        _protectedHoldTouchDevice = null;
        _protectedHoldTimer.Stop();
        if (GetProtectedHoldProgress(button) is { } progress)
        {
            progress.Value = 0;
            progress.Visibility = Visibility.Collapsed;
        }

        if (button.IsMouseCaptured)
        {
            button.ReleaseMouseCapture();
        }
        if (touchDevice?.Captured == button)
        {
            button.ReleaseTouchCapture(touchDevice);
        }
    }

    private static ProtectedHoldAction? GetProtectedHoldAction(Button button) => button.Tag switch
    {
        "Settings" => ProtectedHoldAction.Settings,
        "Exit" => ProtectedHoldAction.Exit,
        _ => null,
    };

    private static ProgressBar? GetProtectedHoldProgress(Button button) =>
        button.Content is Panel panel
            ? panel.Children.OfType<ProgressBar>().FirstOrDefault(progress => Equals(progress.Tag, "ProtectedHoldProgress"))
            : null;

    private async Task OpenConfigurationAfterHoldAsync()
    {
        if (DataContext is not IMainWindowActions actions || !actions.CanOpenConfiguration)
        {
            return;
        }

        Topmost = false;
        Hide();
        try
        {
            await RunUiActionAsync(actions.OpenConfigurationAsync);
        }
        finally
        {
            Show();
            if (DataContext is MainViewModel viewModel)
            {
                ApplyWindowPolicy(viewModel);
            }
            Activate();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        NotifyActivity();
        if (e.Key == Key.Escape && DataContext is IMainWindowActions actions)
        {
            actions.HandleEscape();
            e.Handled = true;
            return;
        }

    }

    private void OnUserMouseActivity(object sender, MouseEventArgs e) => NotifyActivity();

    private void OnUserTouchActivity(object? sender, TouchEventArgs e) => NotifyActivity();

    private void NotifyActivity()
    {
        if (DataContext is IMainWindowActions actions)
        {
            actions.NotifyUserActivity();
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_administratorExitRequested || IsWindowedTestMode || DataContext is not IMainWindowActions)
        {
            return;
        }

        e.Cancel = true;
    }

    private static async Task RunUiActionAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Voice Booth", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
