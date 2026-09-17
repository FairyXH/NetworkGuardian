# Rewrites x:Bind inside DataTemplate blocks to classic Binding.
#
# Why: the WinUI 1.8.260803003 XAML compiler fails with a masked
# "WMC9999 Could not find any resources ... ErrorMessages.resources" error when a DataTemplate
# declares x:DataType pointing at a type from the project's own assembly and a later pass tries to
# resolve property names on it. Classic Binding resolves at runtime and is unaffected.
#
# Nested DataTemplates are handled by tracking depth.

# Default targets: every XAML file of the WinUI app.
$repoRoot = Split-Path -Parent $PSScriptRoot
$Files = @(
    (Join-Path $repoRoot 'src\NetworkGuardian.App\MainWindow.xaml')
)
$Files += Get-ChildItem (Join-Path $repoRoot 'src\NetworkGuardian.App\Views') -Filter '*.xaml' |
    Select-Object -ExpandProperty FullName

function Convert-Templates {
    param([string]$Text)

    $output = New-Object System.Text.StringBuilder
    $index = 0
    $depth = 0

    while ($index -lt $Text.Length) {
        if ($Text.Substring($index).StartsWith("<DataTemplate")) {
            $end = $Text.IndexOf('>', $index)
            if ($end -lt 0) { break }

            $tag = $Text.Substring($index, $end - $index + 1)
            $tag = [regex]::Replace($tag, '\s+x:DataType="[^"]*"', '')
            [void]$output.Append($tag)
            $index = $end + 1
            $depth++
            continue
        }

        if ($Text.Substring($index).StartsWith("</DataTemplate>")) {
            [void]$output.Append("</DataTemplate>")
            $index += "</DataTemplate>".Length
            if ($depth -gt 0) { $depth-- }
            continue
        }

        if ($depth -gt 0 -and $Text.Substring($index).StartsWith("{x:Bind ")) {
            [void]$output.Append("{Binding ")
            $index += "{x:Bind ".Length
            continue
        }

        [void]$output.Append($Text[$index])
        $index++
    }

    return $output.ToString()
}

foreach ($file in $Files) {
    if (-not (Test-Path $file)) {
        Write-Host "skip (missing): $file"
        continue
    }

    $original = Get-Content $file -Raw
    $converted = Convert-Templates -Text $original

    if ($converted -eq $original) {
        Write-Host "unchanged: $file"
        continue
    }

    Set-Content -Path $file -Value $converted -NoNewline -Encoding utf8
    Write-Host "converted: $file"
}
