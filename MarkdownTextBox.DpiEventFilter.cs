using System;
using System.Windows;

namespace PaperTodo;

public sealed partial class MarkdownTextBox
{
    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        AddHandler(
            Window.DpiChangedEvent,
            new DpiChangedEventHandler(OnDescendantDpiChanged));
    }

    private void OnDescendantDpiChanged(object sender, DpiChangedEventArgs e)
    {
        e.Handled = true;
    }
}
