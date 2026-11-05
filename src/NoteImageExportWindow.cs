using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace PaperTodo;

internal sealed class NoteImageExportWindow : Window
{
    private readonly string _noteTitle;
    private readonly Func<CancellationToken, NoteImageArtifact> _compose;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Image _preview = new() { Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _save = new() { MinWidth = 110, Padding = new Thickness(18, 9, 18, 9), IsEnabled = false };
    private NoteImageArtifact? _artifact;
    private bool _closed;

    internal NoteImageExportWindow(Window owner, string title, Func<CancellationToken, NoteImageArtifact> compose)
    {
        Owner = owner;
        _noteTitle = title;
        _compose = compose;
        Title = Strings.Get("EditorImageExport");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 760; Height = Math.Min(860, SystemParameters.WorkArea.Height * .88);
        MinWidth = 440; MinHeight = 360;
        ShowInTaskbar = false;
        Background = Theme.PaperBrush;
        Foreground = Theme.TextBrush;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        FontSize = AppTypography.Scale(13);
        var root = new DockPanel();
        var actions = new DockPanel { Margin = new Thickness(22, 16, 22, 16), LastChildFill = true };
        _save.Content = Strings.Get("EditorImageExportSave");
        _save.Click += async (_, _) => await SaveAsync();
        DockPanel.SetDock(_save, Dock.Right);
        actions.Children.Add(_save);
        _status.Margin = new Thickness(0, 0, 16, 0);
        _status.Text = Strings.Get("EditorImageExportPreparing");
        actions.Children.Add(_status);
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);
        var heading = new TextBlock
        {
            Text = Strings.Get("EditorImageExportPreview"), FontSize = AppTypography.Scale(17),
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(22, 18, 22, 16)
        };
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);
        var mat = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(232, 229, 223)),
            Padding = new Thickness(24), Child = _preview
        };
        var scroll = new ScrollViewer
        {
            Content = mat, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        scroll.SizeChanged += (_, _) =>
            _preview.Width = Math.Max(200, Math.Min(NoteImageArtifact.Width, scroll.ActualWidth - 70));
        root.Children.Add(scroll);
        Content = root;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        Loaded += async (_, _) => await PrepareAsync();
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); };
    }

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
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closed) _status.Text = Strings.Format("NoteExportFailure", ex.Message);
        }
    }

    private async Task SaveAsync()
    {
        if (_artifact is not { } artifact || !_save.IsEnabled) return;
        var dialog = new SaveFileDialog
        {
            Title = Title,
            Filter = Strings.Get("EditorImageExportJpeg") + " (*.jpg)|*.jpg|" +
                Strings.Get("EditorImageExportPng") + " (*.png)|*.png",
            FilterIndex = 1,
            DefaultExt = ".jpg", AddExtension = true, OverwritePrompt = true,
            FileName = NotePortableExport.SafeName(_noteTitle)
        };
        if (dialog.ShowDialog(this) != true || _closed) return;
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
