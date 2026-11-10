using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.GoodLock;

internal partial class GoodLockSession : UserControl, IPaperBodySession
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly PaperBodyContext _context;
    private readonly IPaperInjectionApi _api;
    private readonly Dictionary<string, string> _suggestionHints = new(StringComparer.Ordinal);
    private PaperInjectionSnapshot _snapshot;
    private DraftState _draft;
    private bool _busy;
    private bool _disposed;

    public GoodLockSession(PaperBodyContext context)
    {
        _context = context;
        _api = context.InjectionApi;
        Labels = new GoodLockText(context.UiLanguage);
        _snapshot = _api.Read();
        _draft = JsonSerializer.Deserialize<DraftState>(context.StateJson, JsonOptions) ?? new();

        InitializeComponent();
        DataContext = this;
        ApplyTheme(context.Theme);
        context.Paper.SetTitle("Good Lock");
        context.Paper.SetCapsulePresentation(new PaperCapsulePresentation
        {
            PreferredWidth = PaperCapsulePresentation.AutomaticWidth,
            PlainText = "Good Lock",
            Components = [new PaperCapsuleComponent
            {
                Kind = PaperCapsuleComponentKind.Text,
                Text = "Good Lock",
                Fill = true
            }]
        });
    }

    public GoodLockText Labels { get; }
    public FrameworkElement View => this;

    private void RefreshSnapshot()
    {
        _snapshot = _api.Read();
        _suggestionHints.Clear();
        RenderRows();
    }

    private Dictionary<string, JsonElement> ProposedOverrides()
    {
        var proposed = _draft.ResetAll
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : _snapshot.Overrides.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var id in _draft.Remove)
            proposed.Remove(id);
        foreach (var (id, value) in _draft.Set)
            proposed[id] = value;
        return proposed;
    }

    private void BeginEdit()
    {
        if (_draft.IsEmpty)
            _draft.ExpectedRevision = _snapshot.Revision;
    }

    private void SaveDraft() => _context.SaveStateJson(
        _draft.IsEmpty ? "{}" : JsonSerializer.Serialize(_draft, JsonOptions));

    private void ResetValue(string id)
    {
        BeginEdit();
        _draft.Set.Remove(id);
        _draft.Remove.Add(id);
        _suggestionHints.Remove(id);
        SaveDraft();
        RenderRows();
    }

    private void SuggestValue(string id)
    {
        var proposed = ProposedOverrides();
        if (!proposed.TryGetValue(id, out var value))
            return;
        var suggestion = _api.Suggest(id, value, proposed);
        if (suggestion is null)
        {
            _suggestionHints[id] = Labels.NoSuggestion;
        }
        else
        {
            BeginEdit();
            _draft.Remove.Remove(id);
            _draft.Set[id] = suggestion.Value.Clone();
            _suggestionHints[id] = suggestion.Description;
            SaveDraft();
        }
        RenderRows();
    }

    private void ApplyChanges()
    {
        var result = _api.Apply(_draft.IsEmpty ? null : new PaperInjectionPatch
        {
            ExpectedRevision = _draft.ExpectedRevision,
            ResetAll = _draft.ResetAll,
            Set = _draft.Set,
            Remove = _draft.Remove.ToArray()
        });
        _snapshot = result.Snapshot;
        _draft = new();
        _suggestionHints.Clear();
        SaveDraft();
        RenderRows();
    }

    private void OnApply(object sender, RoutedEventArgs e) => Run(ApplyChanges);

    private void OnResetAll(object sender, RoutedEventArgs e) => Run(() =>
    {
        RefreshSnapshot();
        BeginEdit();
        _draft.ResetAll = true;
        _draft.Set.Clear();
        _draft.Remove.Clear();
        SaveDraft();
        RenderRows();
    });

    private void OnOpenFile(object sender, RoutedEventArgs e) => Run(() =>
    {
        // The first explicit editor request creates an empty file. Existing content is never rewritten here.
        if (!File.Exists(_snapshot.FilePath))
        {
            using var file = new FileStream(_snapshot.FilePath, FileMode.CreateNew, FileAccess.Write);
            file.Write(Encoding.UTF8.GetBytes("{}\n"));
        }
        // Body.OpenExternal handles web and mail links. This native editor button opens
        // only the host's own configuration file with its registered Windows application.
        try
        {
            Process.Start(new ProcessStartInfo(_snapshot.FilePath) { UseShellExecute = true });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1155) // ERROR_NO_ASSOCIATION
        {
            var editor = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
            editor.ArgumentList.Add(_snapshot.FilePath);
            Process.Start(editor);
        }
    });

    private void OnRestart(object sender, RoutedEventArgs e) => Run(() =>
    {
        ApplyChanges();
        _api.Restart();
    });

    private void OnSaveAs(object sender, RoutedEventArgs e) => Run(() =>
    {
        RefreshSnapshot();
        var dialog = new SaveFileDialog
        {
            Title = Labels.SaveAs,
            Filter = Labels.JsonFilter,
            DefaultExt = ".json",
            FileName = "user.good-lock.json",
            AddExtension = true
        };
        var owner = Window.GetWindow(this);
        var accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (accepted != true)
            return;
        if (string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(_snapshot.FilePath),
                StringComparison.OrdinalIgnoreCase))
        {
            ShowMessage(Labels.DifferentFile);
            return;
        }
        File.WriteAllText(dialog.FileName,
            JsonSerializer.Serialize(ProposedOverrides(), JsonOptions) + Environment.NewLine,
            new UTF8Encoding(false));
    });

    private void Run(Action action)
    {
        if (_disposed || _busy)
            return;
        _busy = true;
        try
        {
            action();
        }
        catch (PaperTodoPluginException ex) when (ex.Code == "injection_conflict")
        {
            // Keep only the user's explicit edits, show them over the newly read file, and require
            // another Apply click. Never resubmit a stale full-file snapshot after an external edit.
            RefreshSnapshot();
            _draft.ExpectedRevision = _snapshot.Revision;
            SaveDraft();
            ShowMessage(Labels.FileChanged);
        }
        catch (Exception ex)
        {
            ShowMessage(ex.GetBaseException().Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private void ShowMessage(string message)
    {
        var owner = Window.GetWindow(this);
        if (owner is null)
            MessageBox.Show(message, Labels.ErrorTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show(owner, message, Labels.ErrorTitle, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RenderRows()
    {
        var proposed = ProposedOverrides();
        var parameters = _snapshot.Parameters.ToDictionary(parameter => parameter.Id, StringComparer.Ordinal);
        foreach (var id in _draft.Set.Keys.Concat(_draft.Remove))
            parameters.TryAdd(id, new PaperInjectionParameterSnapshot
            {
                Id = id, Title = id, Type = "unknown", Known = false
            });
        var rows = parameters.Values
            .Where(parameter => parameter.HasFileOverride || parameter.HasAppliedOverride ||
                                _draft.Set.ContainsKey(parameter.Id) || _draft.Remove.Contains(parameter.Id))
            .OrderBy(parameter => parameter.Id, StringComparer.Ordinal)
            .Select(parameter => CreateRow(parameter, proposed))
            .ToArray();
        Rows.ItemsSource = rows;
        EmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResetAllButton.IsEnabled = proposed.Count > 0;
    }

    private ParameterRow CreateRow(PaperInjectionParameterSnapshot parameter,
        IReadOnlyDictionary<string, JsonElement> proposed)
    {
        var hasValue = proposed.TryGetValue(parameter.Id, out var value);
        var edited = _draft.Set.ContainsKey(parameter.Id) || _draft.Remove.Contains(parameter.Id) ||
                     (_draft.ResetAll && parameter.HasFileOverride);
        var status = edited || !parameter.Known ? "pending" : parameter.Status;
        var outside = hasValue && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) &&
                      ((parameter.RecommendedMinimum is { } minimum && number < minimum) ||
                       (parameter.RecommendedMaximum is { } maximum && number > maximum));
        var stateText = !parameter.Known ? Labels.Unknown : status switch
        {
            "applied" => Labels.Applied,
            "restart" => Labels.RestartRequired,
            _ => Labels.Pending
        };
        var defaultText = parameter.DefaultValue.HasValue
            ? FormatValue(parameter.DefaultValue, parameter.Unit)
            : parameter.DefaultDescription ?? "—";

        return new ParameterRow
        {
            Key = parameter.Id,
            Title = string.IsNullOrWhiteSpace(parameter.Title) ? parameter.Id : parameter.Title,
            Description = parameter.Known ? parameter.Description : Labels.Unknown,
            DefaultText = $"{Labels.Default}  {defaultText}",
            ModifiedLabel = Labels.Modified,
            ValueText = hasValue ? FormatValue(value, parameter.Unit) : "—",
            StateText = stateText,
            StateBrush = ResourceBrush(status switch
            {
                "applied" => "Applied",
                "restart" => "Danger",
                _ => "Pending"
            }),
            ValueBrush = ResourceBrush(outside ? "Danger" : "Text"),
            ResetText = Labels.Reset,
            OptimizeText = _suggestionHints.GetValueOrDefault(parameter.Id, Labels.Optimize),
            ResetCommand = new ActionCommand(() => Run(() => ResetValue(parameter.Id)), hasValue),
            OptimizeCommand = new ActionCommand(() => Run(() => SuggestValue(parameter.Id)),
                parameter.Known && hasValue)
        };
    }

    private string FormatValue(JsonElement? value, string unit)
    {
        var text = value?.ValueKind switch
        {
            JsonValueKind.True => Labels.True,
            JsonValueKind.False => Labels.False,
            JsonValueKind.String => value.Value.GetString() ?? "",
            JsonValueKind.Null => "null",
            JsonValueKind.Undefined or null => "—",
            _ => value.Value.GetRawText()
        };
        return text == "—" || string.IsNullOrEmpty(unit) ? text : $"{text} {unit}";
    }

    private SolidColorBrush ResourceBrush(string name) => (SolidColorBrush)Resources[$"GoodLock.{name}"];

    private static SolidColorBrush Brush(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)!);
        brush.Freeze();
        return brush;
    }

    private void ApplyTheme(PaperBodyTheme theme)
    {
        Resources["GoodLock.Text"] = Brush(theme.TextColor);
        Resources["GoodLock.WeakText"] = Brush(theme.WeakTextColor);
        Resources["GoodLock.Border"] = Brush(theme.BorderColor);
        Resources["GoodLock.Accent"] = Brush(theme.AccentColor);
        Resources["GoodLock.Surface"] = Brush(theme.IsDark ? "#14FFFFFF" : "#08000000");
        Resources["GoodLock.Hover"] = Brush(theme.IsDark ? "#28FFFFFF" : "#14000000");
        var accent = (Color)ColorConverter.ConvertFromString(theme.AccentColor)!;
        accent.A = theme.IsDark ? (byte)48 : (byte)26;
        Resources["GoodLock.AccentSurface"] = new SolidColorBrush(accent);
        Resources["GoodLock.Applied"] = Brush(theme.IsDark ? "#72C99A" : "#258555");
        Resources["GoodLock.Pending"] = Brush(theme.IsDark ? "#A0A0A0" : "#858585");
        Resources["GoodLock.Danger"] = Brush(theme.IsDark ? "#EF8B83" : "#B94038");
        Resources["GoodLock.Font"] = new FontFamily(theme.FontFamily);
        Resources["GoodLock.FontSize"] = 11.5 * theme.FontScale;
        Resources["GoodLock.TitleSize"] = 16 * theme.FontScale;
        RenderRows();
    }

    public void OnActivated() => Run(RefreshSnapshot);
    public void OnPresentationChanged(bool visible)
    {
        IsHitTestVisible = visible;
        if (visible)
            Run(RefreshSnapshot);
    }
    public void OnThemeChanged(PaperBodyTheme theme) => ApplyTheme(theme);
    public void OnTypographyChanged(PaperBodyTheme theme) => ApplyTheme(theme);
    public void Dispose() => _disposed = true;

    private sealed class DraftState
    {
        public DraftState() { }

        public string? ExpectedRevision { get; set; }
        public bool ResetAll { get; set; }
        public Dictionary<string, JsonElement> Set { get; set; } = new(StringComparer.Ordinal);
        public HashSet<string> Remove { get; set; } = new(StringComparer.Ordinal);
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsEmpty => !ResetAll && Set.Count == 0 && Remove.Count == 0;
    }

    private sealed class ParameterRow
    {
        public required string Key { get; init; }
        public required string Title { get; init; }
        public required string Description { get; init; }
        public required string DefaultText { get; init; }
        public required string ModifiedLabel { get; init; }
        public required string ValueText { get; init; }
        public string ModifiedText => $"{ModifiedLabel}  {ValueText}";
        public required string StateText { get; init; }
        public required Brush StateBrush { get; init; }
        public required Brush ValueBrush { get; init; }
        public required string ResetText { get; init; }
        public required string OptimizeText { get; init; }
        public required ICommand ResetCommand { get; init; }
        public required ICommand OptimizeCommand { get; init; }
    }

    private sealed class ActionCommand(Action execute, bool enabled) : ICommand
    {
        public bool CanExecute(object? parameter) => enabled;
        public void Execute(object? parameter) => execute();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
