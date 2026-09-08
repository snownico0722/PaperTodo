$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$utf8 = [System.Text.UTF8Encoding]::new($false)

function Replace-Once(
    [string]$RelativePath,
    [string]$Old,
    [string]$New) {
    $path = Join-Path $root $RelativePath
    $text = [System.IO.File]::ReadAllText($path)
    $first = $text.IndexOf($Old, [System.StringComparison]::Ordinal)
    if ($first -lt 0) {
        throw "${RelativePath}: patch anchor not found."
    }
    if ($text.IndexOf($Old, $first + $Old.Length, [System.StringComparison]::Ordinal) -ge 0) {
        throw "${RelativePath}: patch anchor is not unique."
    }
    [System.IO.File]::WriteAllText(
        $path,
        $text.Replace($Old, $New, [System.StringComparison]::Ordinal),
        $utf8)
}

Replace-Once "src/AppController.SettingsSidebar.cs" @'
            Data = Geometry.Parse("M 1,1 L 7,7 M 7,1 L 1,7"),
            Stroke = TrayWeakTextBrush,
            StrokeThickness = 1.2,
'@ @'
            Data = Geometry.Parse("M 1,1 L 7,7 M 7,1 L 1,7"),
            StrokeThickness = 1.2,
'@

Replace-Once "src/AppController.SettingsSidebar.cs" @'
            VerticalAlignment = VerticalAlignment.Center
        };

        var closeButton = new Button
'@ @'
            VerticalAlignment = VerticalAlignment.Center
        };
        closeGlyph.SetBinding(
            Shape.StrokeProperty,
            new System.Windows.Data.Binding(nameof(Control.Foreground))
            {
                RelativeSource = new System.Windows.Data.RelativeSource(
                    System.Windows.Data.RelativeSourceMode.FindAncestor,
                    typeof(Button),
                    1)
            });

        var closeButton = new Button
'@

Replace-Once "src/AppController.Settings.cs" @'
        var path = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
        path.Name = "CheckMark";
        path.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M 3,7.1 L 6,10 L 11,4"));
'@ @'
        var path = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
        path.Name = "CheckMark";
        // Keep the original layout box stable so changing the geometry is a real one-DIP shift
        // instead of being partly cancelled by Path natural-size re-centering at some DPI scales.
        path.SetValue(FrameworkElement.WidthProperty, 13.0);
        path.SetValue(FrameworkElement.HeightProperty, 12.0);
        path.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M 3,7.1 L 6,10 L 11,4"));
'@

Write-Output "PR #211 review fixes applied."
