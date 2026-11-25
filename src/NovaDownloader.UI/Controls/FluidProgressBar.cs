using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using NovaDownloader.Core.Models;

namespace NovaDownloader.UI.Controls;

/// <summary>
/// A 60-120 FPS spring-damper physics-driven progress bar with luminous gradients,
/// velocity-reactive leading-edge glow, continuous liquid shimmer, and smooth glass depth.
/// </summary>
public class FluidProgressBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(FluidProgressBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnTargetChanged));

    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status),
        typeof(DownloadStatus),
        typeof(FluidProgressBar),
        new FrameworkPropertyMetadata(DownloadStatus.Pending, FrameworkPropertyMetadataOptions.AffectsRender, OnStatusChanged));

    public static readonly DependencyProperty ShowTextProperty = DependencyProperty.Register(
        nameof(ShowText),
        typeof(bool),
        typeof(FluidProgressBar),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize),
        typeof(double),
        typeof(FluidProgressBar),
        new FrameworkPropertyMetadata(11.5, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public DownloadStatus Status
    {
        get => (DownloadStatus)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public bool ShowText
    {
        get => (bool)GetValue(ShowTextProperty);
        set => SetValue(ShowTextProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    // 2nd-order critically damped spring-damper physics state
    private double _position;
    private double _velocity;
    private double _target;
    private double _shimmerPhase;
    private long _lastTimestamp;
    private bool _isRenderingHooked;
    private bool _hasInitializedValue;

    // Physics constants: omega = natural frequency (rad/s), zeta = damping ratio
    private const double SpringOmega = 8.5;
    private const double DampingZeta = 0.94;
    private const double MaxSubStep = 0.008;

    private static readonly Pen TrackBorderPen = CreateFrozenPen(Color.FromRgb(0x2B, 0x32, 0x45), 1.0);
    private static readonly Brush TrackBgBrush = CreateFrozenBrush(Color.FromRgb(0x10, 0x13, 0x1B));
    private static readonly Brush TrackTopShadowBrush = CreateFrozenVerticalGradient(
        Color.FromArgb(0x55, 0x00, 0x00, 0x00),
        Color.FromArgb(0x00, 0x00, 0x00, 0x00));
    private static readonly Brush GlassSheenBrush = CreateFrozenVerticalGradient(
        Color.FromArgb(0x36, 0xFF, 0xFF, 0xFF),
        Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF),
        Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF));

    private static readonly Typeface LabelTypeface = new(
        new FontFamily("Segoe UI"),
        FontStyles.Normal,
        FontWeights.Bold,
        FontStretches.Normal);

    public FluidProgressBar()
    {
        SnapsToDevicePixels = true;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FluidProgressBar bar) return;
        var next = Math.Clamp((double)e.NewValue, 0.0, 100.0);
        bar._target = next;

        if (!bar._hasInitializedValue)
        {
            bar._position = next;
            bar._velocity = 0.0;
            bar._hasInitializedValue = true;
        }

        bar.EnsureRenderingLoop();
    }

    private static void OnStatusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FluidProgressBar bar) return;
        bar.EnsureRenderingLoop();
        bar.InvalidateVisual();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _lastTimestamp = Stopwatch.GetTimestamp();
        EnsureRenderingLoop();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachRenderingLoop();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            _lastTimestamp = Stopwatch.GetTimestamp();
            EnsureRenderingLoop();
        }
        else
        {
            DetachRenderingLoop();
        }
    }

    private void EnsureRenderingLoop()
    {
        if (_isRenderingHooked || !IsLoaded || !IsVisible) return;

        var needsAnimation = Status == DownloadStatus.Downloading
            || Math.Abs(_target - _position) > 0.01
            || Math.Abs(_velocity) > 0.01;

        if (!needsAnimation) return;

        _lastTimestamp = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnCompositionRendering;
        _isRenderingHooked = true;
    }

    private void DetachRenderingLoop()
    {
        if (!_isRenderingHooked) return;
        CompositionTarget.Rendering -= OnCompositionRendering;
        _isRenderingHooked = false;
    }

    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var dt = (now - _lastTimestamp) / (double)Stopwatch.Frequency;
        _lastTimestamp = now;

        // Clamp dt to prevent physics jumps after window drag/minimize
        if (dt <= 0.0) return;
        if (dt > 0.05) dt = 0.05;

        // Sub-stepped 2nd-order spring-damper integration
        var remaining = dt;
        while (remaining > 0.0)
        {
            var step = Math.Min(remaining, MaxSubStep);
            var displacement = _target - _position;
            var accel = (SpringOmega * SpringOmega * displacement) - (2.0 * DampingZeta * SpringOmega * _velocity);

            _velocity += accel * step;
            _position += _velocity * step;

            if (_position < 0.0)
            {
                _position = 0.0;
                _velocity = 0.0;
            }
            else if (_position > 100.0)
            {
                _position = 100.0;
                _velocity = 0.0;
            }

            remaining -= step;
        }

        // Advance liquid shimmer wave smoothly
        if (Status == DownloadStatus.Downloading)
        {
            _shimmerPhase = (_shimmerPhase + dt * 0.52) % 1.0;
        }

        InvalidateVisual();

        // Zero-CPU auto-sleep when settled and not actively downloading
        if (Status != DownloadStatus.Downloading
            && Math.Abs(_target - _position) < 0.01
            && Math.Abs(_velocity) < 0.01)
        {
            _position = _target;
            _velocity = 0.0;
            DetachRenderingLoop();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 2 || h <= 2) return;

        var radius = h / 2.0;
        var outerRect = new Rect(0, 0, w, h);
        var innerRect = new Rect(1, 1, Math.Max(0, w - 2), Math.Max(0, h - 2));
        var innerRadius = Math.Max(1.0, radius - 1.0);

        // 1. Track Background + Top Inner Shadow + Border
        dc.DrawRoundedRectangle(TrackBgBrush, null, outerRect, radius, radius);
        dc.DrawRoundedRectangle(TrackTopShadowBrush, null, new Rect(1, 1, Math.Max(0, w - 2), Math.Max(1, h * 0.48)), innerRadius, innerRadius);

        // 2. Clipped Capsule Fill Area
        var fillWidth = Math.Clamp(_position / 100.0, 0.0, 1.0) * innerRect.Width;
        if (fillWidth > 0.5)
        {
            var clipGeo = new RectangleGeometry(innerRect, innerRadius, innerRadius);
            clipGeo.Freeze();
            dc.PushClip(clipGeo);

            var fillRect = new Rect(innerRect.X, innerRect.Y, fillWidth, innerRect.Height);

            // Base luminous multi-stop gradient
            var fillBrush = CreateStatusGradient(Status);
            dc.DrawRectangle(fillBrush, null, fillRect);

            // Traveling liquid shimmer wave while Downloading
            if (Status == DownloadStatus.Downloading && fillWidth > 12)
            {
                var shimmerWidth = Math.Max(48.0, innerRect.Width * 0.28);
                var travelSpan = fillWidth + shimmerWidth * 2;
                var shimmerX = innerRect.X - shimmerWidth + (_shimmerPhase * travelSpan);

                var shimmerBrush = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 0.2),
                    GradientStops =
                    [
                        new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.0),
                        new GradientStop(Color.FromArgb(0x4A, 0xFF, 0xFF, 0xFF), 0.5),
                        new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0)
                    ]
                };
                shimmerBrush.Freeze();

                dc.PushClip(new RectangleGeometry(fillRect));
                dc.DrawRectangle(shimmerBrush, null, new Rect(shimmerX, innerRect.Y, shimmerWidth, innerRect.Height));
                dc.Pop();
            }

            // Velocity-reactive glowing plasma leading edge
            if (Status == DownloadStatus.Downloading && fillWidth > 4 && fillWidth < innerRect.Width - 1)
            {
                var speedFactor = Math.Clamp(Math.Abs(_velocity) / 18.0, 0.25, 1.0);
                var tipAlpha = (byte)(110 + 135 * speedFactor);
                var tipWidth = 14.0 + 10.0 * speedFactor;
                var tipX = Math.Max(innerRect.X, innerRect.X + fillWidth - tipWidth);

                var tipBrush = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0.5),
                    EndPoint = new Point(1, 0.5),
                    GradientStops =
                    [
                        new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.0),
                        new GradientStop(Color.FromArgb(tipAlpha, 0xE0, 0xF2, 0xFE), 0.85),
                        new GradientStop(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 1.0)
                    ]
                };
                tipBrush.Freeze();
                dc.DrawRectangle(tipBrush, null, new Rect(tipX, innerRect.Y, innerRect.X + fillWidth - tipX, innerRect.Height));
            }

            // Smooth top glass curve reflection (no harsh hard-edged stripe)
            var sheenHeight = innerRect.Height * 0.56;
            dc.DrawRectangle(GlassSheenBrush, null, new Rect(fillRect.X, fillRect.Y, fillRect.Width, sheenHeight));

            dc.Pop(); // End capsule clip
        }

        // Crisp outer rim
        dc.DrawRoundedRectangle(null, TrackBorderPen, new Rect(0.5, 0.5, w - 1, h - 1), radius, radius);

        // 3. Centered Tabular Percentage Text
        if (ShowText)
        {
            var labelText = Status == DownloadStatus.Paused
                ? $"Paused ({_position:F1}%)"
                : $"{_position:F1}%";

            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var formatted = new FormattedText(
                labelText,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                LabelTypeface,
                FontSize,
                Brushes.White,
                dpi);

            var tx = (w - formatted.Width) / 2.0;
            var ty = (h - formatted.Height) / 2.0;

            // Soft shadow pass for crisp readability over both dark track and bright fill
            var shadow = new FormattedText(
                labelText,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                LabelTypeface,
                FontSize,
                CreateFrozenBrush(Color.FromArgb(0xCC, 0x05, 0x07, 0x0B)),
                dpi);

            dc.DrawText(shadow, new Point(tx, ty + 1.0));
            dc.DrawText(formatted, new Point(tx, ty));
        }
    }

    private static LinearGradientBrush CreateStatusGradient(DownloadStatus status)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5)
        };

        switch (status)
        {
            case DownloadStatus.Paused:
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xD9, 0x77, 0x06), 0.0));
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xF5, 0x9E, 0x0B), 0.5));
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xFB, 0xBF, 0x24), 1.0));
                break;
            case DownloadStatus.Completed:
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x05, 0x96, 0x69), 0.0));
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x10, 0xB9, 0x81), 0.5));
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x34, 0xD3, 0x99), 1.0));
                break;
            case DownloadStatus.Failed:
            case DownloadStatus.Cancelled:
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x99, 0x1B, 0x1B), 0.0));
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xEF, 0x44, 0x44), 1.0));
                break;
            default:
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x06, 0xB6, 0xD4), 0.0));
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x3B, 0x82, 0xF6), 0.45));
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x8B, 0x5C, 0xF6), 0.80));
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xC0, 0x84, 0xFC), 1.0));
                break;
        }

        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }

    private static Pen CreateFrozenPen(Color color, double thickness)
    {
        var p = new Pen(CreateFrozenBrush(color), thickness);
        p.Freeze();
        return p;
    }

    private static LinearGradientBrush CreateFrozenVerticalGradient(params Color[] colors)
    {
        var b = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1)
        };
        for (var i = 0; i < colors.Length; i++)
        {
            var offset = colors.Length == 1 ? 0.0 : (double)i / (colors.Length - 1);
            b.GradientStops.Add(new GradientStop(colors[i], offset));
        }
        b.Freeze();
        return b;
    }
}
