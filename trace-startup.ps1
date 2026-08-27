# Playback startup harness.
#
# Runs the app on a real project with tracing on, gives it time to open every clip and settle,
# then reports what the UI thread actually did. The interval between ticks IS the frame time,
# so a hole in it is a blocked thread - which is what stutters the audio.
#
#   .\trace-startup.ps1                 -> Tests\0-Test8.json
#   .\trace-startup.ps1 Tests\0-Test7.json
param([string]$Project = "Tests\0-Test8.json", [int]$Seconds = 30)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe  = Join-Path $root "bin\Release\VideoDirector.exe"
$log  = Join-Path $env:TEMP "vd-trace.log"
$proj = if ([System.IO.Path]::IsPathRooted($Project)) { $Project } else { Join-Path $root $Project }

if (-not (Test-Path $exe))  { throw "build first: $exe" }
if (-not (Test-Path $proj)) { throw "no project: $proj" }
Remove-Item $log -ErrorAction SilentlyContinue
Get-Process VideoDirector -ErrorAction SilentlyContinue | Stop-Process -Force

$env:VD_TRACE = "1"
$p = Start-Process $exe -ArgumentList @("--play", "`"$proj`"") -PassThru
Start-Sleep -Seconds $Seconds
Get-Process -Id $p.Id -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

if (-not (Test-Path $log)) { throw "no trace written - did playback start?" }

$rows = Import-Csv $log -Delimiter "`t"

# Loading before the transport starts is not a dropped frame - nothing is playing yet. Measure
# from the first tick after preloading finishes, and report the wait on its own.
$preload = $rows | Where-Object { $_.event -like "PRELOAD*" }
$startMs = 0
if ($preload) {
    $done = $preload | Where-Object { $_.event -eq "PRELOAD done" } | Select-Object -Last 1
    if ($done) { $startMs = [int]$done.ms }
}
if ($startMs -gt 0) {
    $rows = $rows | Where-Object { [int]$_.ms -ge $startMs }
    # The first tick after the wait carries a gap that spans it, so it measures the wait and not
    # a dropped frame. Drop exactly that one.
    $first = $rows | Where-Object { $_.gapMs -ne "" } | Select-Object -First 1
    if ($first) { $rows = $rows | Where-Object { -not ($_.ms -eq $first.ms -and $_.tick -eq $first.tick) } }
}
$ticks = $rows | Where-Object { $_.gapMs -ne "" } | ForEach-Object { [int]$_.gapMs }
$holes = $rows | Where-Object { $_.gapMs -ne "" -and [int]$_.gapMs -gt 20 }

"project      : $(Split-Path $proj -Leaf)"
if ($startMs -gt 0) { "preload      : $startMs ms before the clock started (not a dropped frame)" }
"ticks        : $($ticks.Count)"
"median gap   : $(($ticks | Sort-Object)[[int]($ticks.Count/2)]) ms"
"worst gap    : $(($ticks | Measure-Object -Maximum).Maximum) ms"
"frames lost  : $((($ticks | Where-Object { $_ -gt 20 } | Measure-Object -Sum).Sum / 16.7) -as [int]) (at 60Hz)"
""
"loops and collections:"
foreach ($e in ($rows | Where-Object { $_.event -like "LOOP*" -or $_.event -like "GC*" -or $_.event -like "BUFFER*" -or $_.event -like "REACTIVATE*" })) {
    "  {0,6}ms  {1}" -f [int]$e.ms, $e.event
}
""
"stalls over 20ms, with what happened around them:"
foreach ($h in $holes) {
    $at = [int]$h.ms
    $near = $rows | Where-Object { $_.event -ne "" -and [int]$_.ms -ge ($at - 400) -and [int]$_.ms -le $at }
    "  {0,6}ms  gap {1,4}ms   {2}" -f $at, $h.gapMs, (($near | ForEach-Object { $_.event }) -join "; ")
}
