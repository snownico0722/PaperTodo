from pathlib import Path

p = Path('src/MaterialMenuOpening.cs')
s = p.read_text(encoding='utf-8-sig')
def replace(old, new):
    global s
    assert s.count(old) == 1, old
    s = s.replace(old, new)
replace('''        internal CancellationToken Token { get; }
        private bool _disposed;
        internal OpeningRequest() => Token = _source.Token;''', '''        internal CancellationToken Token { get; }
        internal UIElement? Target { get; }
        private bool _disposed;
        internal OpeningRequest(UIElement? target)
        {
            Target = target;
            Token = _source.Token;
        }''')
replace('''            var request = _request = new OpeningRequest();''', '''            var request = _request = new OpeningRequest(_target());''')
replace('''            _anchor = _target() as FrameworkElement;
            if (_anchor != null) _anchor.Unloaded += OnAnchorUnloaded;''', '''            _anchor = request.Target as FrameworkElement;
            if (_anchor != null)
            {
                _anchor.Unloaded += OnAnchorUnloaded;
                _anchor.IsVisibleChanged += OnAnchorVisibilityChanged;
            }''')
replace('''            if (ReferenceEquals(request, _request) && !_owner.Dispatcher.HasShutdownStarted)
            {
                DetachPendingInput();''', '''            // Hiding keeps a WPF tree loaded; a reusable menu may also receive a new target
            // while its readback is in flight. Neither can authorize this old request to open.
            if (ReferenceEquals(request, _request) &&
                (_owner.Dispatcher.HasShutdownStarted || !ReferenceEquals(request.Target, _target())))
                Cancel();
            if (ReferenceEquals(request, _request))
            {
                DetachPendingInput();''')
replace('''        if (_anchor != null) _anchor.Unloaded -= OnAnchorUnloaded;
        _anchor = null;
    }
    private void OnAnchorUnloaded(object sender, RoutedEventArgs e) => _owner.SetCurrentValue(_isOpen, false);''', '''        if (_anchor != null)
        {
            _anchor.Unloaded -= OnAnchorUnloaded;
            _anchor.IsVisibleChanged -= OnAnchorVisibilityChanged;
        }
        _anchor = null;
    }
    private void OnAnchorUnloaded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _anchor)) _owner.SetCurrentValue(_isOpen, false);
    }
    private void OnAnchorVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _anchor) && e.NewValue is false)
            _owner.SetCurrentValue(_isOpen, false);
    }''')
p.write_text(s, encoding='utf-8', newline='\n')
