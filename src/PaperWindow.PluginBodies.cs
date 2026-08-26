using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private readonly PaperBodyHost _paperBodyHost = new();
    private PaperBodyPluginDescriptor? _bodyDescriptor;
    private UIElement? _bodyElement;
    private FrameworkElement? _pluginBodyClipHost;
    private StreamGeometry? _pluginBodyClipGeometry;
    private bool _pluginBodyClipRefreshQueued;
    private IPaperBodyControls? _bodyControls;
    private string _pluginDisplayTitle = "";
    private PaperBodyInputClaims _bodyInputClaims;
    private PaperBodyPluginHostApi? _bodyHostApi;
    private bool _bodyRuntimeVisible;
    private MarkdownPaperBodySession? _markdownBodySession;
    private int _bodySessionGeneration;
    private bool _bodyFailed;
    private readonly object _pendingPluginStateGate = new();
    private readonly Dictionary<(int Generation, string ProviderId), PendingPluginState>
        _pendingPluginStates = new();

    private sealed record PendingPluginState(int Version, string Json);

    private sealed class PaperBodyControls(PaperWindow owner) : IPaperBodyControls
    {
        public void ApplySelectStyle(ComboBox comboBox, double fontSize)
        {
            ArgumentNullException.ThrowIfNull(comboBox);
            PaperSelectControl.ApplyPluginTheme(
                comboBox,
                owner.CurrentPaperBodyTheme(),
                fontSize);
        }
    }

    // Staged Markdown extraction: mutable editor state lives in MarkdownPaperBodySession while
    // the mature interaction methods remain in PaperWindow.Note.cs for this release.
    private MarkdownTextBox? _noteBox
    {
        get => _markdownBodySession?.NoteBox;
        set => RequireMarkdownBodySession().NoteBox = value;
    }
    private UIElement? _noteBodyElement
    {
        get => _markdownBodySession?.CurrentPresenter;
        set => RequireMarkdownBodySession().CurrentPresenter = value;
    }
    private ContextMenu? _notePreviewContextMenu
    {
        get => _markdownBodySession?.PreviewContextMenu;
        set => RequireMarkdownBodySession().PreviewContextMenu = value;
    }
    private Action? _showNotePreview
    {
        get => _markdownBodySession?.ShowPreview;
        set => RequireMarkdownBodySession().ShowPreview = value;
    }
    private int _notePresenterGeneration
    {
        get => _markdownBodySession?.PresenterGeneration ?? 0;
        set => RequireMarkdownBodySession().PresenterGeneration = value;
    }
    private int _noteDeferredWorkGeneration
    {
        get => _markdownBodySession?.DeferredWorkGeneration ?? 0;
        set => RequireMarkdownBodySession().DeferredWorkGeneration = value;
    }
    private Action? _cancelNotePresenterInteractions
    {
        get => _markdownBodySession?.CancelPresenterInteractions;
        set => RequireMarkdownBodySession().CancelPresenterInteractions = value;
    }
    private Action? _settlePendingNoteBodyRebuild
    {
        get => _markdownBodySession?.SettlePendingBodyRebuild;
        set => RequireMarkdownBodySession().SettlePendingBodyRebuild = value;
    }
    private bool _noteContentDirty
    {
        get => _markdownBodySession?.ContentDirty == true;
        set => RequireMarkdownBodySession().ContentDirty = value;
    }
    private bool _applyingExternalNoteChange
    {
        get => _markdownBodySession?.ApplyingExternalChange == true;
        set => RequireMarkdownBodySession().ApplyingExternalChange = value;
    }
    private bool _liveIsScriptCapsule
    {
        get => _markdownBodySession?.LiveIsScriptCapsule == true;
        set => RequireMarkdownBodySession().LiveIsScriptCapsule = value;
    }

    private MarkdownPaperBodySession RequireMarkdownBodySession() =>
        _markdownBodySession ?? throw new InvalidOperationException(
            "Markdown presenter state is unavailable outside a Markdown body session.");

    internal void AttachMarkdownBodySession(MarkdownPaperBodySession session)
    {
        if (_markdownBodySession != null && !ReferenceEquals(_markdownBodySession, session))
        {
            throw new InvalidOperationException("A Markdown body session is already attached.");
        }
        _markdownBodySession = session;
    }

    internal void DetachMarkdownBodySession(MarkdownPaperBodySession session)
    {
        if (ReferenceEquals(_markdownBodySession, session))
        {
            _markdownBodySession = null;
        }
    }

    internal bool IsCurrentBodyProviderMarkdown =>
        _paper.Type == PaperTypes.Note &&
        string.Equals(
            NormalizeBodyProviderId(_paper.BodyProviderId),
            PaperBodyProviderIds.Markdown,
            StringComparison.Ordinal);

    private PaperBodyCapabilities CurrentBodyCapabilities
    {
        get
        {
            if (_paper.Type != PaperTypes.Note || _bodyFailed)
            {
                return PaperBodyCapabilities.None;
            }
            if (_bodyDescriptor != null)
            {
                return _bodyDescriptor.Capabilities;
            }
            return _controller.PaperBodyPlugins.TryGet(
                    NormalizeBodyProviderId(_paper.BodyProviderId),
                    out var descriptor)
                ? descriptor.Capabilities
                : PaperBodyCapabilities.None;
        }
    }

    private bool BodySupports(PaperBodyCapabilities capability) =>
        (CurrentBodyCapabilities & capability) == capability;

    internal bool TryGetPluginDisplayTitle(out string title)
    {
        title = !string.IsNullOrWhiteSpace(_pluginDisplayTitle)
            ? _pluginDisplayTitle
            : _paper.BodyHeaderText;
        return !IsCurrentBodyProviderMarkdown &&
            (!_bodyFailed || HasPluginRuntimePresentationOwner) &&
            !string.IsNullOrWhiteSpace(title);
    }

    internal bool TryGetPluginCapsuleTitle(out string title)
    {
        title = _paper.BodyCapsuleText;
        return !IsCurrentBodyProviderMarkdown &&
            (!_bodyFailed || HasPluginRuntimePresentationOwner) &&
            !string.IsNullOrWhiteSpace(title);
    }

    private bool BodyClaimsInput(PaperBodyInputClaims claim) =>
        !IsCurrentBodyProviderMarkdown &&
        (_bodyInputClaims & claim) == claim;

    private UIElement CreateAndAttachInitialPaperBody()
    {
        _bodyFailed = false;
        var generation = NextBodySessionGeneration();
        var body = CreatePaperBodyView(generation, out var session);
        _paperBodyHost.Attach(session);
        _bodyElement = body;
        return body;
    }

    private int NextBodySessionGeneration()
    {
        lock (_pendingPluginStateGate)
        {
            return ++_bodySessionGeneration;
        }
    }

    private IPaperBodySession CreatePaperBodySession(int generation)
    {
        var providerId = NormalizeBodyProviderId(_paper.BodyProviderId);
        _paper.BodyProviderId = providerId;
        if (string.Equals(providerId, PaperBodyProviderIds.Markdown, StringComparison.Ordinal))
        {
            _controller.PaperBodyPlugins.TryGet(providerId, out var markdownDescriptor);
            _bodyDescriptor = markdownDescriptor;
            return new MarkdownPaperBodySession(
                this,
                _paper,
                _controller.ImageStore);
        }

        if (!_controller.PaperBodyPlugins.TryGet(providerId, out var descriptor))
        {
            _bodyDescriptor = null;
            _bodyFailed = true;
            return new FailedPaperBodySession(
                this,
                providerId,
                Strings.Format("PluginsMissingProviderFormat", providerId));
        }

        _bodyDescriptor = descriptor;
        try
        {
            if (descriptor.Kind == PaperBodyPluginKind.Native)
            {
                var stored = ReadPluginState(descriptor.Id);
                var activation =
                    _controller.PaperBodyPlugins.CreateNativePlugin(descriptor);
                var plugin = activation.Plugin;
                descriptor = activation.Descriptor;
                _bodyDescriptor = descriptor;
                IPaperBodySession? createdSession = null;
                try
                {
                    var migrated = MigrateNativePluginState(plugin, descriptor, stored);
                    var context = CreatePluginContext(descriptor, generation, migrated);
                    createdSession = plugin.Create(context)
                        ?? throw new InvalidOperationException("Plugin returned a null body session.");
                    if (migrated.Version != stored.Version)
                    {
                        SavePluginStateValidated(
                            descriptor.Id,
                            migrated.Version,
                            migrated.Json);
                    }
                    return createdSession;
                }
                finally
                {
                    if (!ReferenceEquals(plugin, createdSession) &&
                        plugin is IDisposable disposable)
                    {
                        try { disposable.Dispose(); } catch { }
                    }
                }
            }

            if (descriptor.Kind == PaperBodyPluginKind.Web && descriptor.Manifest != null)
            {
                var stored = ReadPluginState(descriptor.Id);
                if (stored.Version > descriptor.StateVersion)
                {
                    throw new InvalidOperationException(
                        $"Saved plugin state version {stored.Version} is newer than supported version {descriptor.StateVersion}.");
                }
                var context = CreatePluginContext(descriptor, generation, stored);
                var runtimeOwnsPresentation =
                    descriptor.Manifest.Capabilities.Contains(
                        "appRuntime",
                        StringComparer.Ordinal);
                return new WebPaperBodySession(
                    context,
                    descriptor.Manifest,
                    runtimeOwnsPresentation);
            }

            throw new InvalidOperationException("Plugin descriptor has no usable body factory.");
        }
        catch (Exception ex)
        {
            _bodyHostApi?.Dispose();
            _bodyHostApi = null;
            _bodyRuntimeVisible = false;
            _bodyDescriptor = null;
            _bodyFailed = true;
            var failedSession = new FailedPaperBodySession(
                this,
                descriptor.DisplayName,
                ex.GetBaseException().Message);
            ClearPluginPresentationOnFailure();
            return failedSession;
        }
    }

    private UIElement CreatePaperBodyView(
        int generation,
        out IPaperBodySession session)
    {
        session = CreatePaperBodySession(generation);
        try
        {
            var view = session.View
                ?? throw new InvalidOperationException("Plugin returned a body session with no view.");
            if (view is Window || view.Parent != null)
            {
                throw new InvalidOperationException(
                    "Plugin body View must be an unparented FrameworkElement, not a Window or a reused control.");
            }
            _bodyFailed = session is FailedPaperBodySession;
            return WrapPluginBodyView(view);
        }
        catch (Exception ex)
        {
            try { session.Dispose(); } catch { }
            _bodyHostApi?.Dispose();
            _bodyHostApi = null;
            _bodyRuntimeVisible = false;
            var pluginName = _bodyDescriptor?.DisplayName ?? _paper.BodyProviderId;
            _bodyDescriptor = null;
            _bodyFailed = true;
            session = new FailedPaperBodySession(
                this,
                pluginName,
                ex.GetBaseException().Message);
            ClearPluginPresentationOnFailure();
            return WrapPluginBodyView(session.View);
        }
    }

    private UIElement WrapPluginBodyView(FrameworkElement view)
    {
        if (IsCurrentBodyProviderMarkdown)
        {
            _pluginBodyClipHost = null;
            _pluginBodyClipGeometry = null;
            return view;
        }

        var host = new Grid
        {
            Background = Brushes.Transparent,
            ClipToBounds = true
        };
        host.Children.Add(view);
        host.SizeChanged += (_, _) => QueuePluginBodyClipRefresh();
        _pluginBodyClipHost = host;
        _pluginBodyClipGeometry = new StreamGeometry();
        QueuePluginBodyClipRefresh();
        return host;
    }

    private void QueuePluginBodyClipRefresh()
    {
        if (_pluginBodyClipHost == null || _pluginBodyClipRefreshQueued)
        {
            return;
        }

        _pluginBodyClipRefreshQueued = true;
        _ = Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                _pluginBodyClipRefreshQueued = false;
                RefreshPluginBodyClip();
            }),
            System.Windows.Threading.DispatcherPriority.Render);
    }

    private void OnPaperChromeContextMenuOpening(
        object sender,
        ContextMenuEventArgs e)
    {
        // ContextMenuOpening runs before the popup takes keyboard focus. Mark the complete
        // transition so note preview focus parking and title commits cannot steal or replace
        // a menu during its first open frame. The guard also covers plugin-owned menus routed
        // through the paper chrome, including the path where PaperTodo suppresses its own menu.
        BeginPaperContextMenuOpening();

        var host = _pluginBodyClipHost;
        if (host == null ||
            !BodyClaimsInput(PaperBodyInputClaims.ContextMenu))
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        var insidePluginBody = source != null && IsDescendantOf(source, host);
        if (!insidePluginBody)
        {
            var pointer = Mouse.GetPosition(host);
            insidePluginBody =
                pointer.X >= 0 &&
                pointer.Y >= 0 &&
                pointer.X < host.ActualWidth &&
                pointer.Y < host.ActualHeight;
            if (insidePluginBody)
            {
                source = host.InputHitTest(pointer) as DependencyObject ?? source;
            }
        }
        if (!insidePluginBody)
        {
            return;
        }

        var current = source;
        while (current != null &&
               !ReferenceEquals(current, host))
        {
            var menu = ContextMenuService.GetContextMenu(current);
            if (menu != null &&
                !ReferenceEquals(menu, _paperChrome.ContextMenu))
            {
                // A native plugin supplied its own menu, directly or through a style.
                return;
            }

            current = GetSafeParent(current);
        }

        // Suppress only the PaperTodo menu inherited from the paper chrome. The original
        // right-click remains unhandled and continues to the plugin/WebView.
        e.Handled = true;
    }

    private void RefreshPluginBodyClip()
    {
        var host = _pluginBodyClipHost;
        if (host == null)
        {
            return;
        }

        var width = host.ActualWidth;
        var height = host.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            host.Clip = null;
            return;
        }

        var chromeRadius = _paperChrome?.CornerRadius.BottomLeft ?? ExpandedChromeCornerRadius;
        var radius = Math.Min(
            Math.Max(0, chromeRadius - 1),
            Math.Min(width, height) / 2);

        var geometry = _pluginBodyClipGeometry ??= new StreamGeometry();
        using (var drawing = geometry.Open())
        {
            drawing.BeginFigure(new Point(0, 0), isFilled: true, isClosed: true);
            drawing.LineTo(new Point(width, 0), isStroked: true, isSmoothJoin: false);
            drawing.LineTo(
                new Point(width, Math.Max(0, height - radius)),
                isStroked: true,
                isSmoothJoin: false);
            if (radius > 0)
            {
                drawing.ArcTo(
                    new Point(width - radius, height),
                    new Size(radius, radius),
                    rotationAngle: 0,
                    isLargeArc: false,
                    sweepDirection: SweepDirection.Clockwise,
                    isStroked: true,
                    isSmoothJoin: false);
            }
            drawing.LineTo(
                new Point(radius, height),
                isStroked: true,
                isSmoothJoin: false);
            if (radius > 0)
            {
                drawing.ArcTo(
                    new Point(0, height - radius),
                    new Size(radius, radius),
                    rotationAngle: 0,
                    isLargeArc: false,
                    sweepDirection: SweepDirection.Clockwise,
                    isStroked: true,
                    isSmoothJoin: false);
            }
        }
        if (!ReferenceEquals(host.Clip, geometry))
        {
            host.Clip = geometry;
        }
    }


    private PaperBodyContext CreatePluginContext(
        PaperBodyPluginDescriptor descriptor,
        int generation,
        PaperBodyStoredState storedState)
    {
        var providerId = descriptor.Id;
        _bodyHostApi?.Dispose();
        var hostApi = new PaperBodyPluginHostApi(
            _controller,
            _controller.PaperCommands,
            _paper.Id,
            providerId,
            descriptor.Permissions,
            () => _windowLifecycle == PaperWindowLifecycleState.Alive &&
                  !_bodyFailed &&
                  generation == _bodySessionGeneration &&
                  string.Equals(
                      NormalizeBodyProviderId(_paper.BodyProviderId),
                      providerId,
                      StringComparison.Ordinal),
            () => _bodyRuntimeVisible);
        _bodyHostApi = hostApi;
        var controls = _bodyControls ??= new PaperBodyControls(this);
        var theme = CurrentPaperBodyTheme();
        var runtimeOwnsPresentation =
            descriptor.Manifest?.Capabilities.Contains(
                "appRuntime",
                StringComparer.Ordinal) == true;
        Action<string> setTitle = runtimeOwnsPresentation
            ? _ => { }
            : title => InvokePluginContext(
                generation,
                providerId,
                () => _controller.UpdatePaperTitleFromPlugin(
                    _paper,
                    title,
                    providerId));
        Action<string> setHeaderText = runtimeOwnsPresentation
            ? _ => { }
            : text => InvokePluginContext(
                generation,
                providerId,
                () => SetPluginHeaderText(text));
        Action<PaperCapsulePresentation?> setCapsulePresentation = runtimeOwnsPresentation
            ? _ => { }
            : presentation => InvokePluginContext(
                generation,
                providerId,
                () => SetPluginCapsulePresentation(presentation));
        Action<PaperBodyInputClaims> setInputClaims = claims => InvokePluginContext(
            generation,
            providerId,
            () => SetPluginInputClaims(claims),
            System.Windows.Threading.DispatcherPriority.Input);
        Action markDirty = () => InvokePluginContext(
            generation,
            providerId,
            _controller.MarkDirty);
        Action<string> openExternal = value => InvokePluginContext(
            generation,
            providerId,
            () => OpenPluginExternal(value));
        Action requestReload = () => InvokePluginContext(
            generation,
            providerId,
            ReloadCurrentPaperBody);

        return new PaperBodyContext
        {
            ProviderId = providerId,
            ApiVersion = descriptor.ApiVersion,
            StateJson = storedState.Json ?? "{}",
            StateVersion = storedState.Version,
            TargetStateVersion = descriptor.StateVersion,
            SettingsJson = _controller.PaperBodyPlugins.DataStore.GetSettingsJson(descriptor),
            GrantedPermissions = hostApi.GrantedPermissions,
            Paper = new PaperBodyPaperContext
            {
                PaperId = _paper.Id,
                SetTitle = setTitle,
                SetHeaderText = setHeaderText,
                SetCapsulePresentation = setCapsulePresentation
            },
            Body = new PaperBodySurfaceContext
            {
                Controls = controls,
                Theme = theme,
                SetInputClaims = setInputClaims,
                MarkDirty = markDirty,
                OpenExternal = openExternal,
                RequestReload = requestReload
            },
            Workspace = hostApi,
            Runtime = new PaperPluginRuntimeClient(
                _controller,
                _paper.Id,
                providerId,
                () => _windowLifecycle == PaperWindowLifecycleState.Alive &&
                      !_bodyFailed &&
                      generation == _bodySessionGeneration &&
                      string.Equals(
                          NormalizeBodyProviderId(_paper.BodyProviderId),
                          providerId,
                          StringComparison.Ordinal)),
            SaveStateJson = json => QueuePluginStateSave(
                generation,
                providerId,
                descriptor.StateVersion,
                json)
        };
    }

    private void InvokePluginContext(
        int generation,
        string providerId,
        Action callback,
        System.Windows.Threading.DispatcherPriority priority =
            System.Windows.Threading.DispatcherPriority.Background)
    {
        void Invoke()
        {
            if (_windowLifecycle != PaperWindowLifecycleState.Alive ||
                _bodyFailed ||
                generation != _bodySessionGeneration ||
                !string.Equals(
                    NormalizeBodyProviderId(_paper.BodyProviderId),
                    providerId,
                    StringComparison.Ordinal))
            {
                return;
            }
            callback();
        }

        // Always queue callbacks, even when a plugin calls during Create. This prevents a
        // RequestReload or title/state callback from re-entering body construction.
        _ = Dispatcher.BeginInvoke((Action)(() =>
        {
            try
            {
                Invoke();
            }
            catch (Exception ex)
            {
                if (_windowLifecycle == PaperWindowLifecycleState.Alive)
                {
                    ReplaceBodyWithFailure(ex.GetBaseException().Message);
                }
            }
        }), priority);
    }

    private void QueuePluginStateSave(
        int generation,
        string providerId,
        int stateVersion,
        string? json)
    {
        var normalized = NormalizePluginStateJson(json);
        lock (_pendingPluginStateGate)
        {
            if (generation != _bodySessionGeneration ||
                !string.Equals(
                    NormalizeBodyProviderId(_paper.BodyProviderId),
                    providerId,
                    StringComparison.Ordinal))
            {
                return;
            }

            _pendingPluginStates[(generation, providerId)] =
                new PendingPluginState(Math.Max(1, stateVersion), normalized);
        }

        if (Dispatcher.CheckAccess())
        {
            FlushPendingPluginState(generation, providerId);
            return;
        }

        _ = Dispatcher.BeginInvoke((Action)(() =>
        {
            try
            {
                FlushPendingPluginState(generation, providerId);
            }
            catch (Exception ex)
            {
                if (!IsClosed && generation == _bodySessionGeneration)
                {
                    ReplaceBodyWithFailure(ex.GetBaseException().Message);
                }
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void FlushPendingPluginState(int generation, string providerId)
    {
        PendingPluginState? pending;
        lock (_pendingPluginStateGate)
        {
            if (generation != _bodySessionGeneration ||
                !string.Equals(
                    NormalizeBodyProviderId(_paper.BodyProviderId),
                    providerId,
                    StringComparison.Ordinal) ||
                !_pendingPluginStates.Remove((generation, providerId), out pending))
            {
                return;
            }
        }

        SavePluginStateValidated(providerId, pending.Version, pending.Json);
    }

    private PendingPluginState? InvalidateBodySessionAndTakePending(
        int generation,
        string providerId)
    {
        lock (_pendingPluginStateGate)
        {
            _pendingPluginStates.Remove((generation, providerId), out var pending);
            _bodySessionGeneration++;
            foreach (var key in _pendingPluginStates.Keys
                         .Where(key => key.Generation <= generation)
                         .ToArray())
            {
                _pendingPluginStates.Remove(key);
            }
            return pending;
        }
    }

    private void CommitDisposeAndInvalidateCurrentBody(bool cancelInteractions)
    {
        var generation = _bodySessionGeneration;
        var providerId = NormalizeBodyProviderId(_paper.BodyProviderId);
        // The edge host may currently own a protocol 1.8 mini tree created by this exact session.
        // Restore compact presentation and detach that tree before invalidating or disposing it.
        _controller.CloseEdgeCapsulePreviewForBodySessionReset(this);
        ResetPluginMiniViewCache();
        _paperBodyHost.CommitCancelDispose(cancelInteractions);
        _bodyHostApi?.Dispose();
        _bodyHostApi = null;
        _bodyRuntimeVisible = false;

        var pending = InvalidateBodySessionAndTakePending(generation, providerId);
        if (pending != null)
        {
            SavePluginStateValidated(providerId, pending.Version, pending.Json);
        }

        ResetPluginRuntimeState(
            refreshTitle: _windowLifecycle == PaperWindowLifecycleState.Alive);
    }

    private PaperBodyStoredState ReadPluginState(string providerId) =>
        _controller.PaperBodyPlugins.DataStore.ReadPaperState(providerId, _paper.Id);

    private PaperBodyStoredState MigrateNativePluginState(
        IPaperBodyPlugin plugin,
        PaperBodyPluginDescriptor descriptor,
        PaperBodyStoredState stored)
    {
        if (stored.Version > descriptor.StateVersion)
        {
            throw new InvalidOperationException(
                $"Saved plugin state version {stored.Version} is newer than supported version {descriptor.StateVersion}.");
        }
        if (stored.Version == descriptor.StateVersion)
        {
            return stored;
        }

        var migratedJson = NormalizePluginStateJson(
            plugin.MigrateState(stored.Json ?? "{}", stored.Version));
        return new PaperBodyStoredState
        {
            Version = descriptor.StateVersion,
            Json = migratedJson
        };
    }

    private static string NormalizePluginStateJson(string? json) =>
        PaperBodyPluginDataStore.NormalizeStateJson(json);

    private void SavePluginStateValidated(
        string providerId,
        int stateVersion,
        string normalized)
    {
        _controller.PaperBodyPlugins.DataStore.SavePaperState(
            providerId,
            _paper.Id,
            stateVersion,
            normalized);
    }

    internal static string NormalizePluginDisplayText(string? text)
    {
        var normalized = string.Join(
            " ",
            (text ?? "")
                .Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0));
        return normalized.Length > 120
            ? normalized[..119] + "…"
            : normalized;
    }

    private void SetPluginHeaderText(string? text)
    {
        var normalized = NormalizePluginDisplayText(text);
        if (string.Equals(_pluginDisplayTitle, normalized, StringComparison.Ordinal) &&
            string.Equals(_paper.BodyHeaderText, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _pluginDisplayTitle = normalized;
        _paper.BodyHeaderText = normalized;
        RefreshPaperTitle();
        _controller.NotifyPaperDisplayTitleChanged(_paper.Id);
    }

    private void SetPluginInputClaims(PaperBodyInputClaims claims)
    {
        const PaperBodyInputClaims supportedClaims =
            PaperBodyInputClaims.EscapeKey |
            PaperBodyInputClaims.ContextMenu;
        _bodyInputClaims = claims & supportedClaims;
    }

    private void ResetPluginRuntimeState(bool refreshTitle)
    {
        var preservePaperPresentation = HasPluginRuntimePresentationOwner;
        var hadDisplayTitle =
            !preservePaperPresentation &&
            !string.IsNullOrEmpty(_pluginDisplayTitle);
        var hadCapsulePresentation =
            !preservePaperPresentation &&
            _pluginCapsulePresentation != null;
        if (!preservePaperPresentation)
        {
            _pluginDisplayTitle = "";
            _pluginCapsulePresentation = null;
        }
        ResetPluginCapsuleCustomViews();
        ResetPluginMiniViewCache();
        _bodyInputClaims = PaperBodyInputClaims.None;
        _bodyRuntimeVisible = false;
        if (refreshTitle && hadDisplayTitle && _isShellBuilt)
        {
            RefreshPaperTitle();
        }
        if (hadCapsulePresentation && _isShellBuilt)
        {
            RefreshCapsuleLabel();
            ApplyCurrentCollapsedCapsuleWidth();
        }
        if (hadDisplayTitle)
        {
            _controller.NotifyPaperDisplayTitleChanged(_paper.Id);
        }
    }

    private static void OpenPluginExternal(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "mailto"))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // Opening an external resource is optional plugin behavior.
        }
    }

    internal void SwitchPaperBodyProvider(string providerId)
    {
        if (_paper.Type != PaperTypes.Note || IsClosed)
        {
            return;
        }

        var normalized = NormalizeBodyProviderId(providerId);
        if (string.Equals(
                NormalizeBodyProviderId(_paper.BodyProviderId),
                normalized,
                StringComparison.Ordinal))
        {
            return;
        }
        if (_controller.PaperBodyPlugins.TryGet(normalized, out var targetDescriptor) &&
            targetDescriptor.Kind != PaperBodyPluginKind.BuiltIn &&
            !_controller.CanAssignPluginProvider(_paper, targetDescriptor))
        {
            MessageBox.Show(
                this,
                Strings.Format(
                    "PluginInstanceLimitMessage",
                    targetDescriptor.DisplayName,
                    targetDescriptor.Manifest?.MaxPaperInstances ?? 1),
                Strings.Get("PluginInstanceLimitTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        CommitPendingEditsForSave();
        var previousProviderId = NormalizeBodyProviderId(_paper.BodyProviderId);
        RemoveCurrentPaperBody();
        _paper.BodyProviderId = normalized;
        _paper.BodyHeaderText = "";
        _paper.BodyCapsuleText = "";
        AttachCurrentPaperBody();
        RefreshPaperBodyChrome();
        RefreshPaperTitle();
        _controller.MarkDirty();
    }

    private void ReloadCurrentPaperBody()
    {
        if (_paper.Type != PaperTypes.Note || IsClosed)
        {
            return;
        }

        CommitPendingEditsForSave();
        RemoveCurrentPaperBody();
        AttachCurrentPaperBody();
        RefreshPaperBodyChrome();
        RefreshPaperTitle();
    }

    private void AttachCurrentPaperBody()
    {
        _bodyFailed = false;
        var generation = NextBodySessionGeneration();
        var body = CreatePaperBodyView(generation, out var session);
        _paperBodyHost.Attach(session);
        _bodyElement = body;
        Grid.SetRow(body, 1);
        Panel.SetZIndex(body, 1);
        _shell.Children.Add(body);
        NotifyCurrentPaperBodyVisibility(
            _paper.IsVisible && !_paper.IsCollapsed && WindowState != WindowState.Minimized);
        _controller.QueuePluginStatusRefresh();
    }

    private void RemoveCurrentPaperBody()
    {
        CommitDisposeAndInvalidateCurrentBody(cancelInteractions: true);
        if (_bodyElement != null)
        {
            _shell.Children.Remove(_bodyElement);
        }
        _bodyDescriptor = null;
        _bodyElement = null;
        _pluginBodyClipHost = null;
        _bodyFailed = false;
        _bodyRuntimeVisible = false;
        RemoveTextZoomOverlay();
        _controller.QueuePluginStatusRefresh();
    }

    private void RefreshPaperBodyChrome()
    {
        if (_openMarkdownButton != null)
        {
            _openMarkdownButton.Visibility =
                _controller.State.ShowTopBarExternalOpenButton &&
                IsCurrentBodyProviderMarkdown
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        RemoveTextZoomOverlay();
        if (BodySupports(PaperBodyCapabilities.TextZoom))
        {
            BuildTextZoomOverlay();
            UpdateTextZoom();
        }
        RefreshPaperContextMenus();
    }

    private void RemoveTextZoomOverlay()
    {
        if (_textZoomIndicator?.Parent is UIElement host)
        {
            _shell.Children.Remove(host);
        }
        _textZoomIndicator = null;
    }

    private void InvokeBodySession(
        Action<IPaperBodySession> callback,
        bool disableOnFailure = true)
    {
        var failure = _paperBodyHost.Invoke(callback);
        if (failure != null)
        {
            if (!disableOnFailure ||
                _windowLifecycle != PaperWindowLifecycleState.Alive)
            {
                return;
            }
            ReplaceBodyWithFailure(failure.Message);
        }
    }

    private void ReplaceBodyWithFailure(string message)
    {
        var providerName = _bodyDescriptor?.DisplayName ?? _paper.BodyProviderId;
        CommitDisposeAndInvalidateCurrentBody(cancelInteractions: true);
        if (_bodyElement != null)
        {
            _shell.Children.Remove(_bodyElement);
        }
        ClearPluginPresentationOnFailure();
        var failedSession = new FailedPaperBodySession(this, providerName, message);
        _paperBodyHost.Attach(failedSession);
        _bodyDescriptor = null;
        _bodyFailed = true;
        _bodyElement = WrapPluginBodyView(failedSession.View);
        Grid.SetRow(_bodyElement, 1);
        Panel.SetZIndex(_bodyElement, 1);
        _shell.Children.Add(_bodyElement);
        RefreshPaperBodyChrome();
        _controller.QueuePluginStatusRefresh();
    }

    private void ClearPluginPresentationOnFailure()
    {
        if (HasPluginRuntimePresentationOwner)
        {
            return;
        }

        var hadHeader = !string.IsNullOrEmpty(_paper.BodyHeaderText);
        var hadCapsule = !string.IsNullOrEmpty(_paper.BodyCapsuleText);
        _paper.BodyHeaderText = "";
        _paper.BodyCapsuleText = "";
        if (hadHeader)
        {
            RefreshPaperTitle();
            _controller.NotifyPaperDisplayTitleChanged(_paper.Id);
        }
        if (hadCapsule)
        {
            RefreshCapsuleLabel();
            ApplyCurrentCollapsedCapsuleWidth();
        }
    }

    internal void CommitCurrentPaperBody()
    {
        InvokeBodySession(item => item.Commit());
    }

    internal void CancelCurrentPaperBodyInteractions()
    {
        InvokeBodySession(item => item.CancelInteractions());
    }

    internal void NotifyCurrentPaperBodyVisibility(bool visible)
    {
        if (IsCurrentBodyProviderMarkdown)
        {
            var statusChanged = _bodyRuntimeVisible != visible;
            _bodyRuntimeVisible = visible;
            InvokeBodySession(item => item.OnVisibilityChanged(visible));
            if (statusChanged)
            {
                _controller.QueuePluginStatusRefresh();
            }
            return;
        }

        var runtimeVisible = _paper.IsVisible && visible;
        if (visible)
        {
            // Expansion can come from a hotkey, tray action or another paper. Settle any active
            // edge preview before the full body becomes presented and interactive.
            PrepareEdgeCapsulePreviewForActivation();
        }
        var runtimeStatusChanged = _bodyRuntimeVisible != runtimeVisible;
        _bodyRuntimeVisible = runtimeVisible;
        InvokeBodySession(item =>
        {
            item.OnPresentationChanged(visible);
            item.OnVisibilityChanged(runtimeVisible);
        });
        if (runtimeStatusChanged)
        {
            _controller.QueuePluginStatusRefresh();
        }
    }

    internal void NotifyCurrentPaperBodyActivated()
    {
        InvokeBodySession(item => item.OnActivated());
    }

    internal void NotifyCurrentPaperBodyDeactivated()
    {
        InvokeBodySession(item => item.OnDeactivated());
    }

    internal void NotifyCurrentPaperBodyThemeChanged()
    {
        InvokeBodySession(item => item.OnThemeChanged(CurrentPaperBodyTheme()));
    }

    internal void NotifyCurrentPaperBodyTypographyChanged()
    {
        InvokeBodySession(item => item.OnTypographyChanged(CurrentPaperBodyTheme()));
    }

    internal void NotifyCurrentPaperBodyDpiChanged()
    {
        InvokeBodySession(item => item.OnDpiChanged());
    }

    internal void NotifyPaperBodyPluginSettingsChanged(
        string providerId,
        string settingsJson)
    {
        if (_paper.Type != PaperTypes.Note ||
            IsClosed ||
            IsCurrentBodyProviderMarkdown ||
            !string.Equals(
                NormalizeBodyProviderId(_paper.BodyProviderId),
                providerId,
                StringComparison.Ordinal))
        {
            return;
        }

        InvokeBodySession(item => item.OnSettingsChanged(settingsJson));
    }

    internal void RefreshCurrentPaperBodyFromModel()
    {
        InvokeBodySession(item => item.RefreshFromModel());
    }

    internal void DisposeCurrentPaperBody()
    {
        CommitDisposeAndInvalidateCurrentBody(cancelInteractions: true);
        _bodyDescriptor = null;
        _bodyElement = null;
        _pluginBodyClipHost = null;
        _bodyFailed = false;
    }

    private PaperBodyTheme CurrentPaperBodyTheme()
    {
        return new PaperBodyTheme(
            Theme.IsDark,
            BrushHex(Theme.PaperBrush, "#FFF8E6"),
            BrushHex(Theme.TextBrush, "#202020"),
            BrushHex(Theme.WeakTextBrush, "#707070"),
            BrushHex(Theme.ActiveBrush, "#B07A31"),
            BrushHex(Theme.PaperBorderBrush, "#807050"),
            AppTypography.UiFontFamily.Source,
            AppTypography.ScaleFactor *
                (BodySupports(PaperBodyCapabilities.TextZoom)
                    ? CurrentTextZoom()
                    : 1.0));
    }

    private static string BrushHex(Brush brush, string fallback)
    {
        if (brush is not SolidColorBrush solid)
        {
            return fallback;
        }
        var color = solid.Color;
        // Use the same six-digit RGB form for CSS and WPF ColorConverter consumers.
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static string NormalizeBodyProviderId(string? providerId)
    {
        return string.IsNullOrWhiteSpace(providerId)
            ? PaperBodyProviderIds.Markdown
            : providerId.Trim();
    }

    internal MenuItem BuildPaperBodyProviderMenuItem()
    {
        var root = new MenuItem
        {
            Header = Strings.Get("PaperBodyMenu")
        };
        var currentId = NormalizeBodyProviderId(_paper.BodyProviderId);
        foreach (var descriptor in _controller.PaperBodyPlugins.Descriptors)
        {
            var item = new MenuItem
            {
                Header = descriptor.DisplayName,
                IsCheckable = true,
                IsChecked = string.Equals(currentId, descriptor.Id, StringComparison.Ordinal),
                StaysOpenOnClick = false,
                ToolTip = string.IsNullOrWhiteSpace(descriptor.Description)
                    ? null
                    : descriptor.Description
            };
            var providerId = descriptor.Id;
            item.Click += (_, _) => SwitchPaperBodyProvider(providerId);
            root.Items.Add(item);
        }

        if (!_controller.PaperBodyPlugins.TryGet(currentId, out _))
        {
            root.Items.Add(new Separator());
            root.Items.Add(new MenuItem
            {
                Header = Strings.Format("PluginsMissingProviderFormat", currentId),
                IsEnabled = false
            });
        }
        return root;
    }


    internal void RefreshLegacyMarkdownFromModel()
    {
        if (_paper.Type != PaperTypes.Note || _noteBox == null)
        {
            return;
        }

        var content = _paper.Content ?? "";
        var caret = Math.Clamp(_noteBox.CaretIndex, 0, content.Length);
        _applyingExternalNoteChange = true;
        try
        {
            _noteBox.Text = content;
        }
        finally
        {
            _applyingExternalNoteChange = false;
        }
        _noteBox.CaretIndex = caret;
        _noteContentDirty = false;
        InvalidateEdgeCapsulePreviewContent();

        var wasScriptCapsule = _liveIsScriptCapsule;
        _liveIsScriptCapsule = IsScriptCapsuleDocument(_noteBox);
        if (wasScriptCapsule != _liveIsScriptCapsule)
        {
            RefreshCapsuleLabel();
            RefreshPaperContextMenus();
        }
    }

    internal FrameworkElement CreateMarkdownBodyView() =>
        (FrameworkElement)BuildNoteBody();

    private void ClearPluginRuntimeStateOnFailure()
    {
        ResetPluginRuntimeState(refreshTitle: true);
    }

    private sealed class FailedPaperBodySession : IPaperBodySession
    {
        private readonly PaperWindow _owner;
        public FailedPaperBodySession(PaperWindow owner, string pluginName, string message)
        {
            _owner = owner;
            owner.ClearPluginRuntimeStateOnFailure();
            var layout = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 420
            };
            layout.Children.Add(new TextBlock
            {
                Text = Strings.Get("PluginBodyFailureTitle"),
                Foreground = Theme.TextBrush,
                FontFamily = AppTypography.UiFontFamily,
                FontSize = AppTypography.Scale(14),
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            layout.Children.Add(new TextBlock
            {
                Text = Strings.Format("PluginBodyFailureMessageFormat", pluginName, message),
                Foreground = Theme.WeakTextBrush,
                FontFamily = AppTypography.UiFontFamily,
                FontSize = AppTypography.Scale(12),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 8, 0, 12)
            });

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var retry = CreateButton(Strings.Get("PluginBodyRetry"));
            retry.Click += (_, _) => owner.ReloadCurrentPaperBody();
            var markdown = CreateButton(Strings.Get("PluginBodyUseMarkdown"));
            markdown.Margin = new Thickness(8, 0, 0, 0);
            markdown.Click += (_, _) => owner.SwitchPaperBodyProvider(PaperBodyProviderIds.Markdown);
            buttons.Children.Add(retry);
            buttons.Children.Add(markdown);
            layout.Children.Add(buttons);

            View = new Border
            {
                Padding = new Thickness(20),
                Background = Brushes.Transparent,
                Child = layout
            };
        }

        public FrameworkElement View { get; }

        private static Button CreateButton(string text)
        {
            return new Button
            {
                Content = text,
                Padding = new Thickness(12, 5, 12, 5),
                MinWidth = 76,
                Background = Theme.Tint(28),
                Foreground = Theme.TextBrush,
                BorderBrush = Theme.PaperBorderBrush,
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                FontFamily = AppTypography.UiFontFamily,
                FontSize = AppTypography.Scale(12)
            };
        }

        public void Dispose() { }
    }
}
