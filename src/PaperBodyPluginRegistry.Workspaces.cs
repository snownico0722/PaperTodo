namespace PaperTodo;

internal sealed partial class PaperBodyPluginRegistry
{
    // Installed code/descriptors remain process-wide. Only a fully stopped workspace's data
    // store is replaced; Native assemblies are neither rediscovered nor repeatedly loaded.
    internal PaperBodyPluginDataStore ExchangeWorkspaceDataStore(PaperBodyPluginDataStore next)
    {
        ArgumentNullException.ThrowIfNull(next);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var old = _dataStore;
        _dataStore = next;
        return old;
    }

    internal void RefreshWorkspaceLocalization()
    {
        foreach (var (id, descriptor) in _descriptors.ToArray())
        {
            if (descriptor.Manifest is not { } manifest) continue;
            PaperBodyPluginLocalization.ValidateAndApply(manifest, UiLanguages.EffectiveUiCulture);
            var next = descriptor with
            {
                DisplayName = string.IsNullOrWhiteSpace(manifest.Name) ? id : manifest.Name.Trim(),
                Description = manifest.Description?.Trim() ?? ""
            };
            _descriptors[id] = next;
            if (_loadedNativeByDirectory.TryGetValue(descriptor.PluginDirectory, out var loaded))
                _loadedNativeByDirectory[descriptor.PluginDirectory] = loaded with { Descriptor = next };
        }
    }
}
