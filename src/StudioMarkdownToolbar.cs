using System;
using System.Windows;
using System.Windows.Controls;

namespace PaperTodo;

// Formatting commands operate on the host's existing editor. Document ownership,
// saving, image storage and window lifecycle deliberately remain with that host.
internal static class StudioMarkdownToolbar
{
    internal static StackPanel Create(
        Func<MarkdownTextBox?> getEditor, Action enterEdit, Action chooseImage, Action? exportImage = null)
    {
        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 6, 0)
        };
        void Add(StudioIcon icon, string resource, Action<MarkdownTextBox> action)
        {
            var button = StudioIcons.Button(icon, Strings.Get(resource));
            button.Click += (_, _) =>
            {
                if (getEditor() is not { } editor) return;
                enterEdit();
                action(editor);
                editor.Focus();
            };
            tools.Children.Add(button);
        }
        Add(StudioIcon.Undo, "EditorUndo", editor => editor.Undo());
        Add(StudioIcon.Redo, "EditorRedo", editor => editor.Redo());
        Add(StudioIcon.Heading, "EditorHeading", editor => editor.InsertLinePrefix("# "));
        Add(StudioIcon.Bold, "EditorBold", editor => editor.WrapSelection("**", "**"));
        Add(StudioIcon.Italic, "EditorItalic", editor => editor.WrapSelection("*", "*"));
        Add(StudioIcon.Strike, "EditorStrike", editor => editor.WrapSelection("~~", "~~"));
        Add(StudioIcon.Bullets, "EditorBullets", editor => editor.InsertLinePrefix("- "));
        Add(StudioIcon.Numbers, "EditorNumbers", editor => editor.InsertLinePrefix("1. "));
        Add(StudioIcon.Quote, "EditorQuote", editor => editor.InsertLinePrefix("> "));
        Add(StudioIcon.Code, "EditorCode", editor => editor.InsertCodeBlock());
        Add(StudioIcon.Link, "EditorLink", editor => editor.InsertMarkdownLink());
        Add(StudioIcon.Image, "EditorImage", _ => chooseImage());
        if (exportImage != null)
        {
            var export = StudioIcons.Button(StudioIcon.ExportImage, Strings.Get("EditorImageExport"));
            export.Click += (_, _) => exportImage();
            tools.Children.Add(export);
        }
        return tools;
    }
}
