namespace PaperTodo;

public sealed partial class AppController
{
    internal bool IsPluginEnabled(string? providerId)
    {
        var normalized = providerId?.Trim() ?? string.Empty;
        if (normalized.Length == 0 ||
            string.Equals(
                normalized,
                PaperBodyProviderIds.Markdown,
                StringComparison.Ordinal))
        {
            return true;
        }

        return !State.DisabledPluginIds.Contains(normalized, StringComparer.Ordinal);
    }

    private void TogglePluginEnabled(PaperBodyPluginDescriptor descriptor)
    {
        if (descriptor.Kind == PaperBodyPluginKind.BuiltIn)
        {
            return;
        }

        SetPluginEnabled(descriptor.Id, !IsPluginEnabled(descriptor.Id));
    }

    private void SetPluginEnabled(string providerId, bool enabled)
    {
        var normalized = providerId.Trim();
        if (normalized.Length == 0 ||
            string.Equals(
                normalized,
                PaperBodyProviderIds.Markdown,
                StringComparison.Ordinal) ||
            IsPluginEnabled(normalized) == enabled)
        {
            return;
        }

        if (enabled)
        {
            State.DisabledPluginIds.RemoveAll(value =>
                string.Equals(value, normalized, StringComparison.Ordinal));
        }
        else
        {
            State.DisabledPluginIds.Add(normalized);
        }

        MarkDirty();

        // Rebuild only Papers that are already bound to this provider. BodyProviderId and all
        // provider/per-paper state stay intact; the window swaps between the real session and the
        // host-owned disabled placeholder.
        foreach (var window in _windows.Values.ToArray())
        {
            window.RefreshPluginEnabledState(normalized);
        }

        // Runtime, shortcuts, Global Top Bar/Todo contributions and other provider-owned work all
        // reconcile from the same host-owned enablement gate.
        RefreshPluginShortcuts();
        ReconcilePluginRuntimes();
        if (_settingsWindow is { IsVisible: true } &&
            _settingsPage == SettingsPage.Plugins)
        {
            _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(
                (Action)RefreshSettingsWindowContent,
                System.Windows.Threading.DispatcherPriority.Background);
        }
        else
        {
            QueuePluginStatusUiRefresh();
        }
    }
}
