using System.Windows;
using System.Windows.Controls;

namespace PaperTodo;

public sealed partial class AppController
{
    private static void ApplyInjectedNewPaperSize(PaperData paper)
    {
        if (paper.Type == PaperTypes.Note)
        {
            paper.Width = InjectionParameters.NewNoteWidth.Resolve(paper.Width);
            paper.Height = InjectionParameters.NewNoteHeight.Resolve(paper.Height);
        }
        else
        {
            paper.Width = InjectionParameters.NewTodoWidth.Resolve(paper.Width);
            paper.Height = InjectionParameters.NewTodoHeight.Resolve(paper.Height);
        }
    }

    // Called once after typed slots are bound, never from a frame or polling callback.
    // Existing owners refresh their own cached state; injection does not write AppState.
    private void RefreshUserInjectionRuntime(IReadOnlySet<string> changedKeys)
    {
        if (changedKeys.Contains(InjectionParameters.SaveIdleMs.Id))
            _saveTimer.Interval = TimeSpan.FromMilliseconds(InjectionParameters.SaveIdleMs.Value);
        if (changedKeys.Contains(InjectionParameters.SaveForceMs.Id))
            _forceSaveTimer.Interval = TimeSpan.FromMilliseconds(InjectionParameters.SaveForceMs.Value);
        if (changedKeys.Contains(InjectionParameters.FullscreenPollMs.Id))
            _topmostRefreshTimer.Interval = TimeSpan.FromMilliseconds(InjectionParameters.FullscreenPollMs.Value);
        if (changedKeys.Contains(InjectionParameters.FullscreenDebounceMs.Id))
            _fullscreenEventDebounceTimer.Interval = TimeSpan.FromMilliseconds(InjectionParameters.FullscreenDebounceMs.Value);
        if (changedKeys.Contains(InjectionParameters.DisplayRefreshMs.Id))
            _displayMetricsRefreshTimer.Interval = TimeSpan.FromMilliseconds(InjectionParameters.DisplayRefreshMs.Value);

        var typographyChanged = changedKeys.Contains(InjectionParameters.OverallFontScale.Id) ||
            changedKeys.Contains(InjectionParameters.CapsuleIconBase.Id) ||
            changedKeys.Contains(InjectionParameters.CapsuleLabelBase.Id) ||
            changedKeys.Contains(InjectionParameters.TitleFontBase.Id) ||
            changedKeys.Contains(InjectionParameters.MasterGlyphBase.Id) ||
            changedKeys.Contains(InjectionParameters.CapsuleLabelFont.Id) ||
            changedKeys.Contains(InjectionParameters.TitleFont.Id) ||
            changedKeys.Contains(InjectionParameters.MasterLabelFont.Id);
        if (typographyChanged)
        {
            ConfigureSettingsTypography();
            RefreshTypography();
        }

        if (changedKeys.Contains(InjectionParameters.CapsuleGapBase.Id) ||
            changedKeys.Contains(InjectionParameters.CapsuleGapDelta.Id) ||
            changedKeys.Contains(InjectionParameters.CapsuleGapFinal.Id))
            ArrangeDeepCapsules(animate: false);

        RefreshUserInjectionPreview(changedKeys);
        foreach (var window in _windows.Values.ToArray())
            if (!window.IsClosed) window.RefreshUserInjectionRuntime(changedKeys);
        RefreshSettingsWindowContent();
    }

    private static string? InjectionSettingUnavailable(string id)
    {
        // Lock only a completely superseded choice. Partial metric injections still let
        // the ordinary preset control every remaining formula input.
        var overridden = id switch
        {
            "appearance.font_scale" => InjectionParameters.OverallFontScale.HasOverride,
            "title.text_size" => InjectionParameters.TitleFont.HasOverride,
            "capsule.text_size" => InjectionParameters.CapsuleLabelFont.HasOverride,
            "capsule.gap" => InjectionParameters.CapsuleGapFinal.HasOverride,
            "note.text_size" => InjectionParameters.NoteComputedFontSize.HasOverride &&
                InjectionParameters.NoteComputedCodeFontSize.HasOverride &&
                InjectionParameters.NoteComputedHeading1FontSize.HasOverride &&
                InjectionParameters.NoteComputedHeading2FontSize.HasOverride &&
                InjectionParameters.NoteComputedHeading3FontSize.HasOverride,
            "todo.visual_size" => InjectionParameters.TodoMetricTextFontSize.HasOverride &&
                InjectionParameters.TodoMetricTextVerticalPadding.HasOverride &&
                InjectionParameters.TodoMetricAppendMinHeight.HasOverride &&
                InjectionParameters.TodoMetricAppendGlyphFontSize.HasOverride &&
                InjectionParameters.TodoMetricTrashGlyphFontSize.HasOverride &&
                InjectionParameters.TodoMetricLinkedPaperNameFontSize.HasOverride &&
                InjectionParameters.TodoMetricLinkedPaperIconFontSize.HasOverride &&
                InjectionParameters.TodoMetricCheckColumnWidth.HasOverride &&
                InjectionParameters.TodoMetricGhostTextFontSize.HasOverride &&
                InjectionParameters.TodoMetricRowMinHeight.HasOverride,
            _ => false
        };
        return overridden ? SettingsSidebarLocalized(
            "由全局 user.json 覆盖。请在 Good Lock 中移除覆盖并应用；这里保留原设置。",
            "Overridden by global user.json. Remove the override in Good Lock and apply it; the original setting is retained here.",
            "全体の user.json で上書きされています。Good Lock で上書きを削除して適用してください。元の設定は保持されます。",
            "전역 user.json이 덮어쓰고 있습니다. Good Lock에서 재정의를 제거하고 적용하세요. 기존 설정은 유지됩니다.") : null;
    }

    private static UIElement WithInjectionAvailability(string id, UIElement editor)
    {
        if (InjectionSettingUnavailable(id) is { } reason)
        {
            editor.IsEnabled = false;
            ToolTipService.SetToolTip(editor, reason);
            ToolTipService.SetShowOnDisabled(editor, true);
        }
        return editor;
    }
}
