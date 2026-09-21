from pathlib import Path
import re
R=Path('.')
def change(p, fn):
    q=R/p; s=q.read_text(encoding='utf-8-sig'); t=fn(s)
    assert t!=s,p
    q.write_text(t,encoding='utf-8',newline='\n')
def rep(s,a,b,count=1):
    assert s.count(a)==count,(a[:100],s.count(a),count)
    return s.replace(a,b)
def reads(s):
    i=s.index('    public PaperMutationResult CreatePaper(')
    return rep(s[:i],'        _controller.PrepareExternalPaperOperation();\n','',4)+s[i:]
change('src/PaperCommandService.cs',reads)
change('src/PaperCommandService.NoteAssets.cs',lambda s:rep(s,'        _controller.PrepareExternalPaperOperation();\n',''))
def datastore(s):
    a=s.index('internal sealed record PaperBodyPluginDataReadIssue('); b=s.index('internal sealed class PaperBodyStoredState',a); s=s[:a]+s[b:]
    s=rep(s,'    private const string RecoveredSuffix = ".json.recovered";\n','')
    s=rep(s,'    private readonly HashSet<string> _recoveredProviderIds = new(StringComparer.Ordinal);\n    private readonly Dictionary<string, PaperBodyPluginDataReadIssue> _readIssues =\n        new(StringComparer.Ordinal);\n','')
    a=s.index('    public bool TryGetReadIssue('); b=s.index('    public void RemovePaperStateEverywhere(',a); s=s[:a]+s[b:]
    a=s.index('        PluginDataDocument document;\n',s.index('    private PluginDataDocument Load(')); b=s.index('        _cache.Add(providerId, document);',a)
    s=s[:a]+'''        var path = DataPath(providerId);
        // A missing file is a new plugin. An unreadable file is a failed read, not empty data.
        var document = File.Exists(path) ? ReadDocument(path) : NewDocument();

'''+s[b:]
    a=s.index('        foreach (var path in Directory.EnumerateFiles(\n                     DataRoot,\n                     "*" + RecoveredSuffix,'); b=s.index('        return providerIds;',a); s=s[:a]+s[b:]
    a=s.index('    private void SaveNow(string providerId, PluginDataDocument document)'); b=s.index('    internal void SuppressFinalFlushOnDispose()',a)
    s=s[:a]+'''    private void SaveNow(string providerId, PluginDataDocument document)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        _atomicWriter.Write(DataPath(providerId), bytes);
    }

'''+s[b:]
    s=rep(s,'    private string RecoveredDataPath(string providerId) =>\n        Path.Combine(DataRoot, providerId + RecoveredSuffix);\n\n','')
    return s.replace('cleanup of other plugins. Load records the problem for the plugin page.','cleanup of other plugins. Reads of that plugin still report the original failure.')
change('src/PaperBodyPluginDataStore.cs',datastore)
change('src/AppController.Mcp.cs',lambda s:rep(s,'            if (PaperBodyPlugins.DataStore.TryGetReadIssue(pluginId, out _) ||\n                !CodexMcpPermission.CanEnable(settings))','            if (!CodexMcpPermission.CanEnable(settings))'))
def plugins(s):
    s=rep(s,'            root.Children.Add(BuildPluginDescriptorCard(descriptor));','''            try
            {
                root.Children.Add(BuildPluginDescriptorCard(descriptor));
            }
            catch (Exception ex)
            {
                root.Children.Add(BuildPluginIssueCard(new PaperBodyPluginLoadIssue(
                    descriptor.SourcePath, ex.GetBaseException().Message)));
            }''')
    a=s.index('        PaperBodyPluginDataReadIssue? dataIssue = null;'); b=s.index('        if (settings.Length > 0)',a); s=s[:a]+s[b:]
    s=rep(s,'PluginStatusFor(descriptor, dataIssue != null)','PluginStatusFor(descriptor)',2)
    a=s.index('        if (dataIssue != null)'); b=s.index('        Grid.SetColumn(text, 0);',a); return s[:a]+s[b:]
change('src/AppController.Plugins.cs',plugins)
change('src/AppController.PluginStatus.cs',lambda s:rep(rep(s,'        PaperBodyPluginDescriptor descriptor,\n        bool hasDataIssue)','        PaperBodyPluginDescriptor descriptor)'), '        if (hasDataIssue ||\n            HasPluginRuntimeFailure(descriptor.Id) ||','        if (HasPluginRuntimeFailure(descriptor.Id) ||'))
def shortcuts(s):
    return rep(s,'''                var binding = bindingOverrides != null &&
                              bindingOverrides.TryGetValue(commandId, out var overridden)
                    ? overridden
                    : ReadPluginShortcutBinding(descriptor, setting);
''','''                string binding;
                try
                {
                    binding = bindingOverrides != null &&
                              bindingOverrides.TryGetValue(commandId, out var overridden)
                        ? overridden
                        : ReadPluginShortcutBinding(descriptor, setting);
                }
                catch (Exception ex)
                {
                    // A failed plugin read must not abort registration of unrelated shortcuts.
                    Trace.TraceWarning("Plugin shortcut settings could not be read: {0}: {1}",
                        descriptor.Id, ex.GetBaseException().Message);
                    _pluginShortcutStatuses[commandId] = ShortcutUiStatus.RegistrationFailed;
                    continue;
                }
''')
change('src/AppController.PluginShortcuts.cs',shortcuts)
change('plugin-samples/PaperTodo.Plugin.ReviewArchive/ReviewArchiveStore.cs',lambda s:rep(rep(s,'            _document = ReadDocument(_path) ??\n                ReadDocument(_path + ".bak") ??\n                new ReviewArchiveDocument();','            _document = ReadDocument(_path) ?? new ReviewArchiveDocument();'), '            if (File.Exists(path))\n            {\n                File.Copy(path, path + ".bak", overwrite: true);\n            }\n',''))
def body(s):
    s=rep(s,'        bool disableOnFailure = true)','        bool disableOnFailure = false)')
    s=rep(s,'        if (failure != null)\n        {\n            if (!disableOnFailure ||','''        if (failure != null)
        {
            Trace.TraceWarning("Plugin body callback failed: {0}: {1}", _paper.BodyProviderId, failure);
            if (!disableOnFailure ||''')
    return rep(s,'        InvokeBodySession(item => item.Commit());','        InvokeBodySession(item => item.Commit(), disableOnFailure: true);')
change('src/PaperWindow.PluginBodies.cs',body)
def permissions(s):
    s=rep(s,'''            if (todos.Any(item =>
                    item.Done ||
                    item.ReminderAt.HasValue ||
                    !string.IsNullOrWhiteSpace(item.LinkedPaperId)))
            {
                Require(PaperTodoPermissionNames.TodosUpdate);
            }
''','')
    s=rep(s,'if (type == PaperTypes.Todo && request.Todos is { Count: > 0 } todos)','if (type == PaperTypes.Todo && request.Todos is { Count: > 0 })')
    return rep(s,'''        if ((request.Todos ?? []).Any(item =>
                item.Done ||
                item.ReminderAt.HasValue ||
                !string.IsNullOrWhiteSpace(item.LinkedPaperId)))
        {
            Require(PaperTodoPermissionNames.TodosUpdate);
        }
''','')
change('src/PaperBodyPluginHostApi.cs',permissions)
def mcp(s):
    s=rep(s,'        RequireFullWritesForTodoMetadata(todos);\n','',2)
    a=s.index('    private void RequireFullWritesForTodoMetadata(');b=s.index('    private void RequireAdditiveWrites()',a); return s[:a]+s[b:]
change('src/McpCommandService.cs',mcp)
change('src/McpTools.cs',lambda s:rep(rep(rep(s,'Whether the todo starts completed. Setting true requires PaperTodo full writes.','Whether the new todo starts completed.'), 'Optional ISO 8601 future reminder date/time with UTC offset. Requires PaperTodo full writes.','Optional ISO 8601 future reminder date/time with UTC offset for the new todo.'), 'Optional PaperTodo paper ID to link from this todo, such as a Note containing longer details. Requires PaperTodo full writes.','Optional PaperTodo paper ID to link from the new todo, such as a Note containing longer details.'))
def tooltips(s):
    s=rep(s,'''NormalizeText(
                source.ToolTip,
                MaximumToolTipLength,
                required: false,
                "invalid_todo_action_tooltip",
                "Todo action tooltip")''','NormalizeToolTip(source.ToolTip, "invalid_todo_action_tooltip")')
    s=rep(s,'''NormalizeText(
                    label.ToolTip,
                    MaximumToolTipLength,
                    required: false,
                    "invalid_topbar_label_tooltip",
                    "Top-bar label tooltip")''','NormalizeToolTip(label.ToolTip, "invalid_topbar_label_tooltip")')
    anchor='    private static string NormalizeText(\n'
    return rep(s,anchor,'''    private static string NormalizeToolTip(string? value, string code)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length > MaximumToolTipLength)
            throw new PaperTodoPluginException(code,
                $"Tooltips cannot exceed {MaximumToolTipLength} characters.");
        return text;
    }

'''+anchor)
change('src/PluginContributionPolicy.cs',tooltips)
p=R/'src/WebPaperBodySession.cs';s=p.read_text();a=s.index('    private static bool TryOpenExternalNavigation(Uri uri)');b=s.index('\n    }',a)+6
method=s[a:b].replace('private static bool','internal static bool')
s=s[:a]+s[b:];s=s.replace('TryOpenExternalNavigation(', 'WebPluginRuntimeInfrastructure.TryOpenExternalNavigation(').replace('    }\n\n\n\n    private void ShowWebView()','    }\n\n    private void ShowWebView()');p.write_text(s,encoding='utf-8',newline='\n')
q=R/'src/WebPaperBodySession.SharedRuntime.cs';t=q.read_text()
if 'using System.Diagnostics;' not in t:t='using System.Diagnostics;\n'+t
pos=t.index('{',t.index('internal static class WebPluginRuntimeInfrastructure'))+1
t=t[:pos]+'\n'+method+'\n'+t[pos:];q.write_text(t,encoding='utf-8',newline='\n')
def popup(s):
    s=rep(s,'''        if (!WebPluginRuntimeInfrastructure.IsSameOrigin(e.Uri, _origin))
        {
            e.Cancel = true;
            return;
        }''','''        if (!WebPluginRuntimeInfrastructure.IsSameOrigin(e.Uri, _origin))
        {
            e.Cancel = true;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
                _ = WebPluginRuntimeInfrastructure.TryOpenExternalNavigation(uri);
            return;
        }''')
    return rep(s,'''    private static void OnNewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs e) =>
        e.Handled = true;''','''    private void OnNewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!_disposed && !WebPluginRuntimeInfrastructure.IsSameOrigin(e.Uri, _origin) &&
            Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
            _ = WebPluginRuntimeInfrastructure.TryOpenExternalNavigation(uri);
    }''')
change('src/WebPluginPopupContent.cs',popup)
def startup(s):
    s=rep(s,'                    "startup-ready");','                    "startup-ready",\n                    retry: slot.FailureCount > 0);')
    return rep(s,'            HandlePluginRuntimeFailure(slot, descriptor, ex, "start");','''            // FailureCount is already nonzero only when a running Runtime started recovery.
            // Do not retry a plugin that has never completed startup.
            HandlePluginRuntimeFailure(slot, descriptor, ex, "start", retry: slot.FailureCount > 0);''')
change('src/AppController.PluginRuntime.cs',startup)
for q in (R/'Resources').glob('Strings*.resx'):
    s=q.read_text();t=re.sub(r'  <data name="PluginsDataRecovery(?:Pending|Active)"[^>]*>\n.*?\n  </data>\n','',s,flags=re.S)
    assert t!=s,q
    q.write_text(t,encoding='utf-8',newline='\n')
print('production edits complete')
