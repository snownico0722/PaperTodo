using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace PaperTodo;

/// <summary>
/// One animation-frame scheduler per UI dispatcher. Presenters still own their transitions and
/// reconcile pipelines; the shared scheduler only batches frame advances and cursor sampling.
/// </summary>
internal sealed class EdgeCapsuleFrameScheduler
{
    private static readonly ConditionalWeakTable<Dispatcher, EdgeCapsuleFrameScheduler> Schedulers = new();

    private readonly DispatcherTimer _timer;
    private readonly List<EdgeCapsulePresenter> _presenters = new();
    private bool _isTicking;

    private EdgeCapsuleFrameScheduler(Dispatcher dispatcher)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _timer.Tick += OnTick;
    }

    public static EdgeCapsuleFrameScheduler For(Dispatcher dispatcher) =>
        Schedulers.GetValue(dispatcher, static key => new EdgeCapsuleFrameScheduler(key));

    public void Activate(EdgeCapsulePresenter presenter)
    {
        if (!_presenters.Contains(presenter))
        {
            _presenters.Add(presenter);
        }
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    public void Deactivate(EdgeCapsulePresenter presenter)
    {
        // Removing from the list while another presenter's reconcile is running would invalidate
        // the backwards iteration. The post-tick sweep observes the presenter's inactive flag.
        if (_isTicking)
        {
            return;
        }

        _presenters.Remove(presenter);
        StopWhenEmpty();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var initialCount = _presenters.Count;
        var pointer = WindowNative.TryGetCursorScreenPosition(out var currentPointer)
            ? currentPointer
            : (DeviceScreenPoint?)null;
        _isTicking = true;
        try
        {
            // Iterate backwards so a completing presenter can be removed without a per-frame
            // snapshot allocation. Presenters activated during this tick start on the next one.
            for (var index = initialCount - 1; index >= 0; index--)
            {
                var presenter = _presenters[index];
                if (!presenter.AdvanceSharedFrame(this, pointer))
                {
                    _presenters.RemoveAt(index);
                }
            }
        }
        finally
        {
            _isTicking = false;
        }

        for (var index = _presenters.Count - 1; index >= 0; index--)
        {
            if (!_presenters[index].UsesSharedFrameScheduler(this))
            {
                _presenters.RemoveAt(index);
            }
        }
        StopWhenEmpty();
    }

    private void StopWhenEmpty()
    {
        if (_presenters.Count == 0)
        {
            _timer.Stop();
        }
    }
}
