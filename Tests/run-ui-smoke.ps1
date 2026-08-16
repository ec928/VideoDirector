# UI functional smoke test.
#
# Launches the real application and inspects it through UI Automation. This is deliberately thin:
# it is not trying to drive the editor, it is checking the things a headless test cannot -- that the
# process starts, that XAML actually parses (a bad binding or a missing handler throws inside
# InitializeComponent), that a real window is created and mapped, and that the automation tree comes
# up with the expected controls in it.
#
# Exit code 0 = pass, 1 = fail. Prints one line per check.

$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot '..\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\VideoDirector.exe'
$failures = 0
$checks = 0

function Check($name, $ok, $detail = '') {
    $script:checks++
    if ($ok) { Write-Output "  PASS  $name" }
    else { $script:failures++; Write-Output "  FAIL  $name $detail" }
}

Write-Output 'UI functional smoke'
Write-Output '-------------------'

if (-not (Test-Path $exe)) {
    Write-Output "  FAIL  build output present ($exe)"
    exit 1
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$proc = Start-Process -FilePath $exe -PassThru
try {
    # Wait for a real, mapped main window rather than sleeping a fixed amount.
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline -and $proc.MainWindowHandle -eq 0) {
        Start-Sleep -Milliseconds 250
        $proc.Refresh()
    }

    Check 'process is running' (-not $proc.HasExited) "(exited with $($proc.ExitCode))"
    if ($proc.HasExited) { exit 1 }

    Check 'main window created' ($proc.MainWindowHandle -ne 0)
    if ($proc.MainWindowHandle -eq 0) { exit 1 }

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
    Check 'automation tree reachable' ($null -ne $root)

    # Give the visual tree a moment to populate before walking it.
    Start-Sleep -Seconds 3

    $descendants = $root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    Check 'window has a populated automation tree' ($descendants.Count -gt 0) "(count=$($descendants.Count))"

    $names = @()
    foreach ($d in $descendants) {
        try { if ($d.Current.Name) { $names += $d.Current.Name } } catch { }
    }

    # The four track load buttons are built in code from the unified track list, so their presence
    # confirms the timeline actually laid itself out against the new model.
    foreach ($t in 1..4) {
        Check "track $t label present" ($names -contains "Track $t")
    }

    # Transport and toolbar controls, by tooltip/name.
    Check 'export button present' (($names | Where-Object { $_ -match 'Export' }).Count -gt 0)
    Check 'play/pause control present' (($names | Where-Object { $_ -match 'Play or pause' }).Count -gt 0)
    Check 'timeline zoom controls present' (($names | Where-Object { $_ -match 'Zoom in timeline' }).Count -gt 0)
    Check 'snapping toggle present' (($names | Where-Object { $_ -match 'Snapping' }).Count -gt 0)
    Check 'mode badge present' (($names | Where-Object { $_ -match 'Editor mode' }).Count -gt 0)

    # Phase B1: with nothing selected and the panel unpinned, the properties panel hides itself
    # entirely rather than showing an empty shell.
    Check 'properties panel hidden with no selection' (($names | Where-Object { $_ -match 'No clip selected' }).Count -eq 0)

    Check 'still running after interaction' (-not $proc.HasExited)
}
finally {
    if (-not $proc.HasExited) { $proc.Kill(); $proc.WaitForExit(5000) }
}

Write-Output '-------------------'
Write-Output "$($checks - $failures)/$checks checks passed"
if ($failures -gt 0) { exit 1 } else { exit 0 }
