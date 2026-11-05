using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Win32;

namespace PaperTodo;

internal sealed class NoteImageExportWindow : Window
{
    private readonly string _noteTitle;
    private readonly Func<CancellationToken, NoteImageArtifact> _compose;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Image _preview = new() { Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _estimatedSize = new();
    private readonly Button _formatToggle = TodoReminderDialogControls.Button("JPG");
    private readonly Button _save = TodoReminderDialogControls.Button(Strings.Get("EditorImageExportSave"), primary: true);
    private readonly RectangleGeometry _windowClip = new();
    private readonly Border _frame;
    private NoteImageArtifact? _artifact;
    private NoteImageSizePrediction? _sizeEstimate;
    private NoteImageFormat _preferredFormat = NoteImageFormat.Jpeg;
    private bool _closed;

    internal NoteImageExportWindow(Window owner, string title, Func<CancellationToken, NoteImageArtifact> compose)
    {
        Owner = owner;
        _noteTitle = title;
        _compose = compose;
        Title = Strings.Get("EditorImageExport");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 760;
        Height = Math.Min(860, SystemParameters.WorkArea.Height * .88);
        MinWidth = 440;
        MinHeight = 360;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        AllowsTransparency = false;
        ShowInTaskbar = false;
        Background = Theme.PaperBrush;
        FontFamily = AppTypography.UiFontFamily;
        FontSize = AppTypography.Scale(13);
        Language = AppTypography.Language;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        AppTypography.ApplyTextRendering(this);
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(12),
            UseAeroCaptionButtons = false
        });

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var close = TodoReminderDialogControls.Button("×", compact: true);
        close.Click += (_, _) => Close();
        var heading = new TextBlock
        {
            Text = Strings.Get("EditorImageExportPreview"),
            Foreground = Theme.TextBrush,
            FontWeight = FontWeights.SemiBold,
            FontSize = AppTypography.Scale(15),
            VerticalAlignment = VerticalAlignment.Center
        };
        _status.Foreground = Theme.WeakTextBrush;
        _status.FontSize = AppTypography.Scale(11);
        _status.Text = Strings.Get("EditorImageExportPreparing");
        _status.MaxWidth = 280;
        _status.TextAlignment = TextAlignment.Right;
        _status.Margin = new Thickness(10, 0, 16, 0);

        var titleRow = new Grid { Margin = new Thickness(20, 10, 12, 10) };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_status, 1);
        Grid.SetColumn(close, 2);
        titleRow.Children.Add(heading);
        titleRow.Children.Add(_status);
        titleRow.Children.Add(close);
        var titleBar = new Border
        {
            Background = Theme.TitleBarBrush(opaque: true),
            BorderBrush = Theme.PaperBorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = titleRow
        };
        titleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left || close.IsMouseOver) return;
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                return;
            }
            try { DragMove(); }
            catch (InvalidOperationException) { }
        };
        Grid.SetRow(titleBar, 0);
        layout.Children.Add(titleBar);

        var mat = new Border
        {
            Background = Theme.Tint(28),
            Padding = new Thickness(24),
            Child = _preview
        };
        var scroll = new ScrollViewer
        {
            Content = mat,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        scroll.SizeChanged += (_, _) =>
            _preview.Width = Math.Max(200, Math.Min(NoteImageArtifact.Width, scroll.ActualWidth - 70));
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);

        _formatToggle.MinWidth = 74;
        _formatToggle.ToolTip = Strings.Get("EditorImageExportSwitchFormat");
        _formatToggle.Click += (_, _) =>
        {
            _preferredFormat = _preferredFormat == NoteImageFormat.Jpeg
                ? NoteImageFormat.Png : NoteImageFormat.Jpeg;
            _formatToggle.Content = _preferredFormat == NoteImageFormat.Jpeg ? "JPG" : "PNG";
            UpdateEstimatedSize();
        };
        _estimatedSize.Text = Strings.Format("EditorImageExportEstimatedSize", "x");
        _estimatedSize.Foreground = Theme.WeakTextBrush;
        _estimatedSize.FontSize = AppTypography.Scale(12);
        _estimatedSize.VerticalAlignment = VerticalAlignment.Center;
        _estimatedSize.Margin = new Thickness(14, 0, 0, 0);

        var formatRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        formatRow.Children.Add(_formatToggle);
        formatRow.Children.Add(_estimatedSize);
        _save.MinWidth = 110;
        _save.IsEnabled = false;
        _save.Click += async (_, _) => await SaveAsync();

        var footerGrid = new Grid { Margin = new Thickness(20, 14, 20, 14) };
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_save, 1);
        footerGrid.Children.Add(formatRow);
        footerGrid.Children.Add(_save);
        var footer = new Border
        {
            Background = Theme.PaperBrush,
            BorderBrush = Theme.PaperBorderBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = footerGrid
        };
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);

        _frame = new Border
        {
            Background = Theme.PaperBrush,
            BorderBrush = Theme.PaperBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            Clip = _windowClip,
            Child = layout
        };
        Content = _frame;
        _frame.SizeChanged += (_, _) => RefreshCorners();
        StateChanged += (_, _) => RefreshCorners();
        SourceInitialized += (_, _) =>
        {
            // Match the expanded editor's true HWND rounding without a layered window.
            try
            {
                var rounded = 2u;
                _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33,
                    ref rounded, sizeof(uint));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        Loaded += async (_, _) => await PrepareAsync();
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); };
    }

    private void RefreshCorners()
    {
        var radius = WindowState == WindowState.Maximized ? 0d : 12d;
        _frame.CornerRadius = new CornerRadius(radius);
        _windowClip.Rect = new Rect(0, 0, Math.Max(0, _frame.ActualWidth), Math.Max(0, _frame.ActualHeight));
        _windowClip.RadiusX = radius;
        _windowClip.RadiusY = radius;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref uint value, int size);

    private async Task PrepareAsync()
    {
        var cancellation = _lifetime.Token;
        try
        {
            var artifact = await NoteImageExport.OnStaAsync(() => _compose(cancellation));
            if (_closed) return;
            _artifact = artifact;
            _preview.Source = new DrawingImage(artifact.Drawing);
            _status.Text = artifact.MissingImages == 0
                ? Strings.Format("EditorImageExportSize", artifact.PixelWidth, artifact.PixelHeight)
                : Strings.Format("EditorImageExportMissing", artifact.MissingImages);
            _save.IsEnabled = true;
            // Composition already produced both hints; no second STA or rasterization pass.
            _sizeEstimate = artifact.SizeEstimate;
            UpdateEstimatedSize();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closed) _status.Text = Strings.Format("NoteExportFailure", ex.Message);
        }
    }

    private void UpdateEstimatedSize()
    {
        _estimatedSize.Text = _sizeEstimate is not { } estimate ||
            (_preferredFormat == NoteImageFormat.Jpeg && _artifact?.PixelHeight > ushort.MaxValue)
            ? Strings.Format("EditorImageExportEstimatedSize", "x")
            : FormatEstimatedSize(_preferredFormat == NoteImageFormat.Jpeg ? estimate.JpegBytes : estimate.PngBytes);
    }

    internal static string FormatEstimatedSize(long bytes) => bytes < 1024 * 1024
        ? Strings.Get("EditorImageExportEstimatedSizeSmall")
        : Strings.Format("EditorImageExportEstimatedSize", Math.Round(bytes / (1024d * 1024), MidpointRounding.AwayFromZero));

    private async Task SaveAsync()
    {
        if (_artifact is not { } artifact || !_save.IsEnabled) return;
        var preferredPng = _preferredFormat == NoteImageFormat.Png;
        var dialog = new SaveFileDialog
        {
            Title = Title,
            Filter = Strings.Get("EditorImageExportJpeg") + " (*.jpg)|*.jpg|" +
                Strings.Get("EditorImageExportPng") + " (*.png)|*.png",
            FilterIndex = preferredPng ? 2 : 1,
            DefaultExt = preferredPng ? ".png" : ".jpg",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = NotePortableExport.SafeName(_noteTitle)
        };
        if (dialog.ShowDialog(this) != true || _closed) return;
        // The preview is only the initial choice; Windows' file dialog decides.
        var format = dialog.FilterIndex == 2 ? NoteImageFormat.Png : NoteImageFormat.Jpeg;
        _save.IsEnabled = false;
        _status.Text = Strings.Get("EditorImageExportSaving");
        var cancellation = _lifetime.Token;
        try
        {
            await NoteImageExport.OnStaAsync(() =>
            {
                NoteImageExport.Save(dialog.FileName, artifact, cancellation, format);
                return true;
            });
            if (!_closed) _status.Text = Strings.Get("EditorImageExportSaved");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closed) _status.Text = Strings.Format("NoteExportFailure", ex.Message);
        }
        finally { if (!_closed) _save.IsEnabled = true; }
    }
}
