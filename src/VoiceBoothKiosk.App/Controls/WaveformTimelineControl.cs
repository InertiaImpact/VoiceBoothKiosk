using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;

namespace VoiceBoothKiosk.App.Controls;

public sealed class WaveformTimelineControl : FrameworkElement
{
    private sealed class WaveformTimelineAutomationPeer(WaveformTimelineControl owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(WaveformTimelineControl);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
    }

    private const double TimelineHeight = 30;

    public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(
        nameof(Peaks), typeof(IEnumerable), typeof(WaveformTimelineControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnPeaksChanged));

    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(
        nameof(Duration), typeof(TimeSpan), typeof(WaveformTimelineControl),
        new FrameworkPropertyMetadata(TimeSpan.Zero, FrameworkPropertyMetadataOptions.AffectsRender, OnDurationChanged));

    public static readonly DependencyProperty PlayheadProperty = DependencyProperty.Register(
        nameof(Playhead), typeof(TimeSpan), typeof(WaveformTimelineControl),
        new FrameworkPropertyMetadata(TimeSpan.Zero, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsRecordingProperty = DependencyProperty.Register(
        nameof(IsRecording), typeof(bool), typeof(WaveformTimelineControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnIsRecordingChanged));

    public static readonly DependencyProperty WaveformBrushProperty = DependencyProperty.Register(
        nameof(WaveformBrush), typeof(Brush), typeof(WaveformTimelineControl),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(76, 201, 240)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RecordingBrushProperty = DependencyProperty.Register(
        nameof(RecordingBrush), typeof(Brush), typeof(WaveformTimelineControl),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(240, 68, 90)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WaveformBackgroundProperty = DependencyProperty.Register(
        nameof(WaveformBackground), typeof(Brush), typeof(WaveformTimelineControl),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(7, 17, 31)), FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Peaks
    {
        get => (IEnumerable?)GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    public TimeSpan Duration
    {
        get => (TimeSpan)GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public TimeSpan Playhead
    {
        get => (TimeSpan)GetValue(PlayheadProperty);
        set => SetValue(PlayheadProperty, value);
    }

    public bool IsRecording
    {
        get => (bool)GetValue(IsRecordingProperty);
        set => SetValue(IsRecordingProperty, value);
    }

    public Brush WaveformBrush
    {
        get => (Brush)GetValue(WaveformBrushProperty);
        set => SetValue(WaveformBrushProperty, value);
    }

    public Brush RecordingBrush
    {
        get => (Brush)GetValue(RecordingBrushProperty);
        set => SetValue(RecordingBrushProperty, value);
    }

    public Brush WaveformBackground
    {
        get => (Brush)GetValue(WaveformBackgroundProperty);
        set => SetValue(WaveformBackgroundProperty, value);
    }

    public event EventHandler<TimeSpan>? SeekRequested;

    private TimeSpan? _selectionAnchor;
    private TimeSpan? _selectionCurrent;
    private double _selectionAnchorX;
    private bool _isSelecting;
    private bool _selectionDragged;
    private TouchDevice? _activeTouchDevice;

    public WaveformTimelineControl()
    {
        Focusable = true;
        Cursor = Cursors.Cross;
        MinHeight = 230;
        MouseLeftButtonDown += OnPointerDown;
        MouseMove += OnPointerMove;
        MouseLeftButtonUp += OnPointerUp;
        TouchDown += OnTouchDown;
        TouchMove += OnTouchMove;
        TouchUp += OnTouchUp;
        KeyDown += OnKeyDown;
        AutomationProperties.SetHelpText(this, "Click to move the playhead, or drag across the waveform to measure a time selection.");
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new WaveformTimelineAutomationPeer(this);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var bounds = new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight));
        if (bounds.IsEmpty)
        {
            return;
        }

        drawingContext.DrawRoundedRectangle(
            WaveformBackground,
            new Pen(new SolidColorBrush(Color.FromRgb(42, 62, 89)), 1),
            bounds,
            10,
            10);

        var waveTop = TimelineHeight;
        var waveHeight = Math.Max(1, ActualHeight - TimelineHeight);
        var midY = waveTop + waveHeight / 2;
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(80, 85, 112, 140)), 1);
        gridPen.Freeze();

        for (var index = 1; index < 10; index++)
        {
            var x = ActualWidth * index / 10d;
            drawingContext.DrawLine(gridPen, new Point(x, waveTop), new Point(x, ActualHeight));
        }

        drawingContext.DrawLine(gridPen, new Point(0, midY), new Point(ActualWidth, midY));
        DrawTimeline(drawingContext, TimelineHeight);

        var peakValues = ReadPeakValues();
        if (peakValues.Count == 0)
        {
            DrawEmptyMessage(drawingContext, waveTop, waveHeight);
        }
        else
        {
            var waveformBrush = IsRecording ? RecordingBrush : WaveformBrush;

            var visibleColumns = Math.Max(1, (int)ActualWidth);
            // Map every screen column back to the full source range. The previous minimum
            // ratio of one repeated the newest live peak across all unused columns, making
            // the centerline appear to pulse as an unrelated volume animation.
            var sourcePerColumn = peakValues.Count / (double)visibleColumns;
            for (var column = 0; column < visibleColumns; column++)
            {
                var sourceStart = Math.Min(peakValues.Count - 1, (int)Math.Floor(column * sourcePerColumn));
                var sourceEnd = Math.Min(
                    peakValues.Count,
                    Math.Max(sourceStart + 1, (int)Math.Ceiling((column + 1) * sourcePerColumn)));
                var peak = 0d;
                for (var sourceIndex = sourceStart; sourceIndex < sourceEnd; sourceIndex++)
                {
                    peak = Math.Max(peak, Math.Abs(peakValues[sourceIndex]));
                }

                peak = Math.Clamp(peak, 0.002, 1);
                var halfHeight = peak * waveHeight * 0.46;
                drawingContext.DrawRectangle(
                    waveformBrush,
                    null,
                    new Rect(column, midY - halfHeight, 1.2, Math.Max(1, halfHeight * 2)));
            }
        }

        DrawSelection(drawingContext);

        if (Duration > TimeSpan.Zero)
        {
            var fraction = Math.Clamp(Playhead.TotalMilliseconds / Duration.TotalMilliseconds, 0, 1);
            var x = fraction * ActualWidth;
            var playheadPen = new Pen(Brushes.White, 2);
            playheadPen.Freeze();
            drawingContext.DrawLine(playheadPen, new Point(x, TimelineHeight), new Point(x, ActualHeight));
            drawingContext.DrawGeometry(Brushes.White, null, CreateTriangle(
                new Point(x - 6, TimelineHeight),
                new Point(x + 6, TimelineHeight),
                new Point(x, TimelineHeight + 8)));
        }
    }

    private void DrawSelection(DrawingContext drawingContext)
    {
        if (!TryGetSelection(out var start, out var end) || ActualWidth <= 0)
        {
            return;
        }

        var startX = start.TotalMilliseconds / Duration.TotalMilliseconds * ActualWidth;
        var endX = end.TotalMilliseconds / Duration.TotalMilliseconds * ActualWidth;
        var selectionBrush = new SolidColorBrush(Color.FromArgb(62, 76, 201, 240));
        selectionBrush.Freeze();
        var edgePen = new Pen(new SolidColorBrush(Color.FromRgb(76, 201, 240)), 2);
        edgePen.Freeze();
        drawingContext.DrawRectangle(
            selectionBrush,
            null,
            new Rect(startX, TimelineHeight, Math.Max(1, endX - startX), Math.Max(1, ActualHeight - TimelineHeight)));
        drawingContext.DrawLine(edgePen, new Point(startX, TimelineHeight), new Point(startX, ActualHeight));
        drawingContext.DrawLine(edgePen, new Point(endX, TimelineHeight), new Point(endX, ActualHeight));

        var label = BuildSelectionLabel(start, end);
        var text = new FormattedText(
            label,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            13,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        const double horizontalPadding = 10;
        const double verticalPadding = 5;
        var badgeWidth = text.Width + horizontalPadding * 2;
        var badgeHeight = text.Height + verticalPadding * 2;
        var preferredX = (startX + endX - badgeWidth) / 2;
        var maximumX = Math.Max(5, ActualWidth - badgeWidth - 5);
        var badgeX = Math.Clamp(preferredX, 5, maximumX);
        var badge = new Rect(badgeX, TimelineHeight + 8, Math.Min(badgeWidth, Math.Max(1, ActualWidth - 10)), badgeHeight);
        var badgeBrush = new SolidColorBrush(Color.FromArgb(230, 11, 25, 44));
        badgeBrush.Freeze();
        drawingContext.DrawRoundedRectangle(badgeBrush, edgePen, badge, 6, 6);
        drawingContext.PushClip(new RectangleGeometry(badge));
        drawingContext.DrawText(text, new Point(badge.X + horizontalPadding, badge.Y + verticalPadding));
        drawingContext.Pop();
    }

    private void DrawTimeline(DrawingContext drawingContext, double height)
    {
        var foreground = new SolidColorBrush(Color.FromRgb(158, 176, 196));
        foreground.Freeze();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var totalSeconds = Math.Max(0, Duration.TotalSeconds);

        for (var index = 0; index <= 5; index++)
        {
            var time = TimeSpan.FromSeconds(totalSeconds * index / 5d);
            var label = totalSeconds >= 3600
                ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
            var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 12, foreground, dpi);
            var x = Math.Clamp((ActualWidth - text.Width) * index / 5d, 5, Math.Max(5, ActualWidth - text.Width - 5));
            drawingContext.DrawText(text, new Point(x, (height - text.Height) / 2));
        }
    }

    private void DrawEmptyMessage(DrawingContext drawingContext, double top, double height)
    {
        var foreground = new SolidColorBrush(Color.FromRgb(122, 145, 169));
        foreground.Freeze();
        var text = new FormattedText(
            "Your waveform will appear here",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            18,
            foreground,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawingContext.DrawText(text, new Point(Math.Max(10, (ActualWidth - text.Width) / 2), top + (height - text.Height) / 2));
    }

    private List<double> ReadPeakValues()
    {
        var result = new List<double>();
        if (Peaks is null)
        {
            return result;
        }

        foreach (var value in Peaks)
        {
            if (value is double doubleValue)
            {
                result.Add(doubleValue);
            }
            else if (value is float floatValue)
            {
                result.Add(floatValue);
            }
        }

        return result;
    }

    private void OnPointerDown(object sender, MouseButtonEventArgs e)
    {
        if (!BeginSelection(e.GetPosition(this).X))
        {
            return;
        }

        CaptureMouse();
        e.Handled = true;
    }

    private void OnPointerMove(object sender, MouseEventArgs e)
    {
        if (!_isSelecting || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        UpdateSelection(e.GetPosition(this).X);
        e.Handled = true;
    }

    private void OnPointerUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSelecting)
        {
            return;
        }

        FinishSelection(e.GetPosition(this).X);
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnTouchDown(object? sender, TouchEventArgs e)
    {
        if (!BeginSelection(e.GetTouchPoint(this).Position.X))
        {
            return;
        }

        _activeTouchDevice = e.TouchDevice;
        e.TouchDevice.Capture(this);
        e.Handled = true;
    }

    private void OnTouchMove(object? sender, TouchEventArgs e)
    {
        if (!_isSelecting || e.TouchDevice != _activeTouchDevice)
        {
            return;
        }

        UpdateSelection(e.GetTouchPoint(this).Position.X);
        e.Handled = true;
    }

    private void OnTouchUp(object? sender, TouchEventArgs e)
    {
        if (!_isSelecting || e.TouchDevice != _activeTouchDevice)
        {
            return;
        }

        FinishSelection(e.GetTouchPoint(this).Position.X);
        e.TouchDevice.Capture(null);
        _activeTouchDevice = null;
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !TryGetSelection(out _, out _))
        {
            return;
        }

        ClearSelection();
        e.Handled = true;
    }

    private bool BeginSelection(double x)
    {
        if (Duration <= TimeSpan.Zero || ActualWidth <= 0 || IsRecording)
        {
            return false;
        }

        Focus();
        _selectionAnchorX = Math.Clamp(x, 0, ActualWidth);
        _selectionAnchor = TimeAt(_selectionAnchorX);
        _selectionCurrent = _selectionAnchor;
        _selectionDragged = false;
        _isSelecting = true;
        InvalidateVisual();
        return true;
    }

    private void UpdateSelection(double x)
    {
        var clampedX = Math.Clamp(x, 0, ActualWidth);
        if (Math.Abs(clampedX - _selectionAnchorX) >= SystemParameters.MinimumHorizontalDragDistance)
        {
            _selectionDragged = true;
        }
        _selectionCurrent = TimeAt(clampedX);
        InvalidateVisual();
    }

    private void FinishSelection(double x)
    {
        UpdateSelection(x);
        _isSelecting = false;
        if (!_selectionDragged || !TryGetSelection(out var start, out var end))
        {
            ClearSelection();
            RequestSeek(x);
            return;
        }

        AutomationProperties.SetHelpText(this, BuildSelectionLabel(start, end));
        InvalidateVisual();
    }

    private TimeSpan TimeAt(double x)
    {
        var fraction = Math.Clamp(x / ActualWidth, 0, 1);
        return TimeSpan.FromMilliseconds(Duration.TotalMilliseconds * fraction);
    }

    private bool TryGetSelection(out TimeSpan start, out TimeSpan end)
    {
        start = TimeSpan.Zero;
        end = TimeSpan.Zero;
        if (_selectionAnchor is null || _selectionCurrent is null || Duration <= TimeSpan.Zero)
        {
            return false;
        }

        start = _selectionAnchor.Value <= _selectionCurrent.Value ? _selectionAnchor.Value : _selectionCurrent.Value;
        end = _selectionAnchor.Value <= _selectionCurrent.Value ? _selectionCurrent.Value : _selectionAnchor.Value;
        return end > start;
    }

    private void ClearSelection()
    {
        _selectionAnchor = null;
        _selectionCurrent = null;
        _selectionDragged = false;
        _isSelecting = false;
        AutomationProperties.SetHelpText(this, "Click to move the playhead, or drag across the waveform to measure a time selection.");
        InvalidateVisual();
    }

    private static string BuildSelectionLabel(TimeSpan start, TimeSpan end) =>
        $"{FormatSelectionTime(start)} – {FormatSelectionTime(end)}  •  {FormatSelectionTime(end - start)} selected";

    private static string FormatSelectionTime(TimeSpan value) => value.TotalHours >= 1
        ? value.ToString(@"h\:mm\:ss\.fff", CultureInfo.InvariantCulture)
        : value.ToString(@"m\:ss\.fff", CultureInfo.InvariantCulture);

    private void RequestSeek(double x)
    {
        if (Duration <= TimeSpan.Zero || ActualWidth <= 0)
        {
            return;
        }

        var fraction = Math.Clamp(x / ActualWidth, 0, 1);
        SeekRequested?.Invoke(this, TimeSpan.FromMilliseconds(Duration.TotalMilliseconds * fraction));
    }

    private static StreamGeometry CreateTriangle(Point first, Point second, Point third)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(first, true, true);
            context.LineTo(second, true, false);
            context.LineTo(third, true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static void OnPeaksChanged(DependencyObject sender, DependencyPropertyChangedEventArgs eventArgs)
    {
        var control = (WaveformTimelineControl)sender;
        if (eventArgs.OldValue is INotifyCollectionChanged oldCollection)
        {
            oldCollection.CollectionChanged -= control.OnPeakCollectionChanged;
        }
        if (eventArgs.NewValue is INotifyCollectionChanged newCollection)
        {
            newCollection.CollectionChanged += control.OnPeakCollectionChanged;
        }
        control.ClearSelection();
        control.InvalidateVisual();
    }

    private static void OnDurationChanged(DependencyObject sender, DependencyPropertyChangedEventArgs eventArgs)
    {
        var control = (WaveformTimelineControl)sender;
        if ((TimeSpan)eventArgs.NewValue <= TimeSpan.Zero)
        {
            control.ClearSelection();
        }
    }

    private static void OnIsRecordingChanged(DependencyObject sender, DependencyPropertyChangedEventArgs eventArgs)
    {
        if ((bool)eventArgs.NewValue)
        {
            ((WaveformTimelineControl)sender).ClearSelection();
        }
    }

    private void OnPeakCollectionChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs) => InvalidateVisual();
}
