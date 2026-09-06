namespace PaperTodo;

/// <summary>One shared light/dark pair. A late load cannot resurrect a disabled/stale skin.</summary>
internal sealed class MicaMaterialCache
{
    private readonly object _gate = new();
    private readonly Func<Task<MicaMaterial?>> _load;
    private int _revision;
    private bool _requested;
    private MicaMaterial? _current;

    public MicaMaterialCache(Func<Task<MicaMaterial?>> load) => _load = load;

    public MicaMaterial? Current
    {
        get { lock (_gate) return _current; }
    }

    public async Task<bool> RefreshAsync(bool enabled, bool invalidate = false)
    {
        int revision;
        lock (_gate)
        {
            if (!enabled)
            {
                _revision++;
                _requested = false;
                var changed = _current != null;
                _current = null;
                return changed;
            }
            if (invalidate)
            {
                _revision++;
                _requested = false;
            }
            if (_requested) return false;
            _requested = true;
            revision = _revision;
        }

        var material = await _load().ConfigureAwait(false);
        lock (_gate)
        {
            if (revision != _revision) return false;
            _current = material;
            // A failed read is cached too; retry on the next wallpaper change or selection,
            // not once per window, theme getter, mouse move or render frame.
            return true;
        }
    }
}
