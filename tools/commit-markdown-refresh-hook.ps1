$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$path = Join-Path $root "src/MarkdownTextBox.cs"
$text = [System.IO.File]::ReadAllText($path)

$oldEvents = @'
    public event Action? ImageContextMenuClosed;

    public bool IsImageContextMenuOpen { get; private set; }
'@
$newEvents = @'
    public event Action? ImageContextMenuClosed;

    /// <summary>
    /// Raised after Markdown presentation settings/theme/typography have changed but before the
    /// TextView is synchronously refreshed. Presentation layers use this boundary to update native
    /// line-collapse state before AvalonEdit constructs visual lines.
    /// </summary>
    internal event Action? MarkdownPresentationRefreshing;

    public bool IsImageContextMenuOpen { get; private set; }
'@
$oldRefresh = @'
    public void RefreshVisualStyle()
    {
        Foreground = Theme.TextBrush;
'@
$newRefresh = @'
    public void RefreshVisualStyle()
    {
        MarkdownPresentationRefreshing?.Invoke();
        Foreground = Theme.TextBrush;
'@

foreach ($replacement in @(
    @($oldEvents, $newEvents),
    @($oldRefresh, $newRefresh))) {
    $old = $replacement[0]
    $new = $replacement[1]
    $first = $text.IndexOf($old, [System.StringComparison]::Ordinal)
    if ($first -lt 0 -or $text.IndexOf($old, $first + $old.Length, [System.StringComparison]::Ordinal) -ge 0) {
        throw "Expected exactly one MarkdownTextBox patch anchor."
    }
    $text = $text.Replace($old, $new, [System.StringComparison]::Ordinal)
}

[System.IO.File]::WriteAllText($path, $text, [System.Text.UTF8Encoding]::new($false))
