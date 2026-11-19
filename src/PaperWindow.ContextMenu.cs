using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private bool _deepCapsuleContextMenuInitializationQueued;
    private bool _deepCapsuleContextMenuInitialized;

    private ContextMenu BuildPaperContextMenu(bool forDeepCapsuleSlot = false)
    {
        var menu = CreateContextMenu();
        PopulatePaperContextMenu(menu, forDeepCapsuleSlot);
        // Context menus survive resizes and paper preference changes. Rebuild when
        // opened so right-click and ellipsis reflect the actual visible bar.
        menu.Opened += (_, _) => PopulatePaperContextMenu(menu, forDeepCapsuleSlot);
        return menu;
    }

    private void PopulatePaperContextMenu(ContextMenu menu, bool forDeepCapsuleSlot)
    {
        menu.Items.Clear();
        menu.Items.Add(MenuHeader(Strings.Get("MenuQuick")));

        // Keep the same command order in both menus. Only the expanded paper's own
        // menu skips commands already visible in its top bar; the edge capsule is a
        // separate surface and must retain both creation actions.
        foreach (var action in PaperTopBarShortcuts.NormalizeOrder(_controller.State.TopBarShortcutOrder))
        {
            if (PaperTopBarShortcuts.IsHidden(_controller.State, action)) continue;
            switch (action)
            {
                case PaperTopBarShortcuts.RunScript when
                    !forDeepCapsuleSlot && !_paper.IsCollapsed && !IsTopBarCommandVisible(_runScriptButton):
                    menu.Items.Add(MenuItem(Strings.Get("TopBarRunScript"),
                        (_, _) => _controller.RunTopBarScript(this)));
                    break;
                case PaperTopBarShortcuts.Workspace when
                    _controller.WorkspaceOptions.Enabled &&
                    (forDeepCapsuleSlot || !IsTopBarCommandVisible(_workspaceButton)):
                    menu.Items.Add(BuildWorkspaceShortcutMenuItem(menu));
                    break;
                case PaperTopBarShortcuts.NewTodo when
                    forDeepCapsuleSlot || !IsTopBarCommandVisible(_newTodoButton):
                    menu.Items.Add(MenuItem(Strings.Get("MenuNewTodoPaper"),
                        (_, _) => _controller.CreatePaper(PaperTypes.Todo, show: true, _paper)));
                    break;
                case PaperTopBarShortcuts.NewNote when
                    forDeepCapsuleSlot || !IsTopBarCommandVisible(_newNoteButton):
                    menu.Items.Add(MenuItem(Strings.Get("MenuNewNotePaper"),
                        (_, _) => _controller.CreatePaper(PaperTypes.Note, show: true, _paper)));
                    break;
                case PaperTopBarShortcuts.Association when
                    CanBeginAssociationDrag() && !IsTopBarCommandVisible(_windowBindingButton):
                    menu.Items.Add(BuildAssociationDragMenuItem(menu));
                    break;
                case PaperTopBarShortcuts.ExternalEditor when
                    _paper.Type == PaperTypes.Note && IsCurrentBodyProviderMarkdown &&
                    !IsTopBarCommandVisible(_openMarkdownButton):
                    menu.Items.Add(MenuItem(OpenMarkdownEditorToolTip(),
                        (_, _) => OpenMarkdownInDefaultEditor()));
                    break;
            }
        }

        AttachPluginTopBarActionsToMenu(menu);
        menu.Items.Add(MenuItem(Strings.Get("TraySettings"),
            (_, _) => _controller.OpenSettingsWindow()));

        menu.Items.Add(MenuSeparator());
        menu.Items.Add(MenuHeader(_controller.PaperCapsuleTitle(_paper)));

        if (_paper.Type == PaperTypes.Note)
            menu.Items.Add(BuildPaperBodyProviderMenuItem());

        if (IsExportablePaper(_paper))
        {
            var export = new MenuItem
            {
                Header = Strings.Get("MenuExport"),
                IsEnabled = !_paperExportRunning
            };
            export.Items.Add(MenuItem(Strings.Get("MenuExportCurrent"), (_, _) => ExportPapers(ExportScope.Current)));
            export.Items.Add(MenuItem(Strings.Get("MenuExportAllNotes"), (_, _) => ExportPapers(ExportScope.Notes)));
            export.Items.Add(MenuItem(Strings.Get("MenuExportAllTodos"), (_, _) => ExportPapers(ExportScope.Todos)));
            export.Items.Add(MenuItem(Strings.Get("MenuExportAllPapers"), (_, _) => ExportPapers(ExportScope.All)));
            menu.Items.Add(export);
        }

        AttachPluginPaperMenuActions(menu);

        if (CanDisplayAsCapsule())
        {
            menu.Items.Add(_paper.IsCollapsed
                ? MenuItem(Strings.Get("MenuRestoreWindow"), (_, _) => OpenCapsuleForEditing())
                : MenuItem(Strings.Get("MenuCollapseToCapsule"), (_, _) => SetCollapsedState(true)));
        }

        if (HasExperimentalCapsuleMagnet)
        {
            menu.Items.Add(MenuItem(
                Strings.Get("LabsCapsuleMagnetDetach"),
                (_, _) => DetachExperimentalWindowAttachment(savePosition: true)));
        }

        if (HasExperimentalWindowTether)
        {
            menu.Items.Add(MenuItem(
                Strings.Get("LabsWindowTetherDetach"),
                (_, _) => DetachExperimentalWindowAttachment(savePosition: true)));
        }

        menu.Items.Add(MenuItem(Strings.Get("MenuHide"), (_, _) => _controller.HidePaper(_paper)));
        menu.Items.Add(MenuItem(
            Strings.Get("MenuDelete"),
            (_, _) => DeletePaperFromPaperMenu(),
            isDanger: true));
    }

    private void AttachPluginPaperMenuActions(ContextMenu menu) =>
        // The shared paper menu already rebuilds every item on Opened. Registering
        // another handler here would accumulate callbacks on each rebuild.
        AttachPluginPaperMenuActions(menu, _controller, _paper.Id, refreshOnOpen: false);

    internal static void AttachPluginPaperMenuActions(ContextMenu menu, AppController controller,
        string paperId, bool refreshOnOpen = true)
    {
        var insertionIndex = menu.Items.Count;
        var contributed = new List<MenuItem>();
        void Refresh()
        {
            foreach (var item in contributed) menu.Items.Remove(item);
            contributed.Clear();
            foreach (var binding in controller.GetPluginPaperActions(paperId))
            {
                // Use the existing menu renderer unchanged: no icon, tooltip or custom template.
                var item = MenuItem(binding.Action.Text, (_, _) => controller.InvokePluginPaperAction(binding));
                menu.Items.Insert(insertionIndex + contributed.Count, item);
                contributed.Add(item);
            }
        }
        Refresh();
        // Standalone plugin menus still refresh lazily, but the paper's single builder
        // already owns refresh and must not accumulate another Opened subscriber.
        if (refreshOnOpen)
            menu.Opened += (_, _) => Refresh();
    }

    private void ScheduleDeepCapsuleSlotContextMenuInitialization()
    {
        if (_deepCapsuleContextMenuInitializationQueued ||
            _deepCapsuleContextMenuInitialized ||
            _edgeCapsuleHost == null)
        {
            return;
        }

        var host = _edgeCapsuleHost;
        _deepCapsuleContextMenuInitializationQueued = true;
        Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                _deepCapsuleContextMenuInitializationQueued = false;
                if (IsClosed ||
                    _deepCapsuleContextMenuInitialized ||
                    !ReferenceEquals(host, _edgeCapsuleHost))
                {
                    return;
                }

                host.SetContextMenu(BuildDeepCapsuleSlotContextMenu());
                _deepCapsuleContextMenuInitialized = true;
            }),
            System.Windows.Threading.DispatcherPriority.SystemIdle);
    }

    private ContextMenu BuildDeepCapsuleSlotContextMenu()
    {
        var menu = BuildPaperContextMenu(forDeepCapsuleSlot: true);
        DeepCapsuleContextMenuSession.PrepareOwnerActivation((MaterialContextMenu)menu);

        menu.Opened += (_, _) => _deepCapsuleContextMenuSession.HandleOpened(menu);
        menu.Closed += (_, _) => _deepCapsuleContextMenuSession.HandleClosed(menu);

        return menu;
    }

    private void CloseDeepCapsuleSlotContextMenu() =>
        _deepCapsuleContextMenuSession.Close();

    private void OnDeepCapsuleContextMenuOpenChanged(bool open)
    {
        if (_edgeCapsule.ContextMenuOpen != open)
        {
            SetEdgeCapsuleContextMenuOpen(open);
        }

        // The reducer can reset ContextMenuOpen while detaching; still refresh local topmost so
        // slot hosts mirror the shared owner-set immediately.
        RefreshDeepCapsuleSlotTopmost();
        if (!open)
        {
            InvalidateEdgeCapsulePointer();
        }
    }

    private ContextMenu CreateContextMenu()
    {
        var menu = new MaterialContextMenu();
        InitializeContextMenu(menu);
        return menu;
    }

    // Called once for each owned menu, either at creation or when a Todo placeholder
    // is first requested. Preserve that instance so WPF's TextBox Closed hook survives.
    private void InitializeContextMenu(ContextMenu menu)
    {
        menu.Padding = new Thickness(4, 4, 4, 4);
        menu.FontFamily = AppTypography.UiFontFamily;
        menu.Language = AppTypography.Language;
        menu.FontSize = AppTypography.Scale(13);
        menu.HasDropShadow = true;
        menu.Template = SharedContextMenuTemplate;
        AppTypography.ApplyTextRendering(menu);
        UpdateContextMenuTheme(menu);
        menu.Opened += (_, _) =>
        {
            if (_controller.State.Theme == "system")
            {
                Theme.Invalidate();
            }

            UpdateContextMenuTheme(menu);
            RefreshExperimentalOpacity();
        };
        menu.Closed += (_, _) => Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                RefreshExperimentalOpacity();
                FlushPendingPaperContextMenuRefresh();
            }),
            System.Windows.Threading.DispatcherPriority.Background);

        menu.Resources.Add(typeof(MenuItem), SharedCompactMenuItemStyle);
        RegisterThemedContextMenu(menu);
    }

    private bool IsPaperContextMenuInteractionActive =>
        _paperContextMenuOpening || HasOpenOwnedContextMenu();

    private void BeginPaperContextMenuOpening()
    {
        _paperContextMenuOpening = true;
        var version = ++_paperContextMenuOpeningVersion;
        Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (version != _paperContextMenuOpeningVersion)
                {
                    return;
                }

                _paperContextMenuOpening = false;
                FlushPendingPaperContextMenuRefresh();
            }),
            System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void RegisterThemedContextMenu(ContextMenu menu)
    {
        for (var i = _themedContextMenus.Count - 1; i >= 0; i--)
        {
            if (!_themedContextMenus[i].TryGetTarget(out _))
            {
                _themedContextMenus.RemoveAt(i);
            }
        }

        _themedContextMenus.Add(new WeakReference<ContextMenu>(menu));
    }

    private void RefreshThemedContextMenus()
    {
        for (var i = _themedContextMenus.Count - 1; i >= 0; i--)
        {
            if (_themedContextMenus[i].TryGetTarget(out var menu))
            {
                UpdateContextMenuTheme(menu);
                RefreshContextMenuTypography(menu);
            }
            else
            {
                _themedContextMenus.RemoveAt(i);
            }
        }
    }

    private static void RefreshContextMenuTypography(ContextMenu menu)
    {
        menu.Resources[typeof(MenuItem)] = SharedCompactMenuItemStyle;
        menu.FontFamily = AppTypography.UiFontFamily;
        menu.FontSize = AppTypography.Scale(13);
        menu.Language = AppTypography.Language;
        AppTypography.ApplyTextRendering(menu);
        foreach (var header in menu.Items.OfType<MenuItem>().Where(item => !item.IsEnabled))
        {
            header.FontSize = AppTypography.Scale(12);
        }
    }

    private static void UpdateContextMenuTheme(ContextMenu menu)
    {
        menu.Resources["PaperBrushKey"] = PaperBrush;
        menu.Resources["PaperBorderBrushKey"] = PaperBorderBrush;
        menu.Resources["TextBrushKey"] = TextBrush;
        menu.Resources["WeakTextBrushKey"] = WeakTextBrush;
        menu.Resources["HoverBrushKey"] = HoverBrush;
        menu.Resources["MenuHoverBrushKey"] = MenuHoverBrush;
        menu.Resources["DangerTextBrushKey"] = TrashTextBrush;
        menu.Background = PaperBrush;
        menu.BorderBrush = PaperBorderBrush;
        menu.Foreground = TextBrush;
    }

    private static Separator MenuSeparator()
    {
        return new Separator
        {
            Margin = new Thickness(8, 3, 8, 3),
            Opacity = 0.38
        };
    }

    private static MenuItem MenuHeader(string header)
    {
        var item = new MenuItem
        {
            Header = header,
            IsEnabled = false,
            Padding = new Thickness(8, 2, 10, 2),
            Background = Brushes.Transparent,
            FontSize = AppTypography.Scale(12),
            FontWeight = FontWeights.SemiBold
        };
        item.SetResourceReference(Control.ForegroundProperty, "WeakTextBrushKey");
        return item;
    }

    private static MenuItem MenuItem(
        string header,
        RoutedEventHandler click,
        bool isDanger = false)
    {
        var item = new MenuItem
        {
            Header = header,
            Padding = new Thickness(8, 4, 10, 4),
            Background = Brushes.Transparent
        };
        item.SetResourceReference(
            Control.ForegroundProperty,
            isDanger ? "DangerTextBrushKey" : "TextBrushKey");
        item.Click += click;
        return item;
    }
}
