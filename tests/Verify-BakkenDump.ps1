<#
.SYNOPSIS
  Asserts that known real values from the Bakken P2-H sample project reached GordonGeoSteer's
  view-models, by checking the viewmodel-dump.txt written by `GordonGeoSteer.exe --selftest`
  (or Help > Dump loaded data). Reference values come from the PZS reverse-engineering notes
  (independently decoded / cross-checked in earlier sessions).

.EXAMPLE
  .\tests\Verify-BakkenDump.ps1 -Dump .\selftest-output\report\viewmodel-dump.txt
#>
param([Parameter(Mandatory = $true)][string]$Dump)

$ErrorActionPreference = 'Stop'
$lines = Get-Content -LiteralPath $Dump -Encoding UTF8
$fail = 0; $pass = 0

function Expect([string]$what, [string]$pathLike, [string]$key, [scriptblock]$test) {
    # plain substring match on the tree path (tree headers contain [ ] which -like would treat as wildcards)
    $hits = $lines | Where-Object { $i = $_.IndexOf(' :: '); $i -ge 0 -and $_.Substring(0, $i).Contains($pathLike) -and $_.Substring($i + 4).StartsWith("$key = ") }
    if (-not $hits) { Write-Host "FAIL  $what  (no line matching '$pathLike' :: $key)" -ForegroundColor Red; $script:fail++; return }
    $vals = @($hits | ForEach-Object { ($_ -split ' = ', 2)[1] })
    $match = $null
    foreach ($v in $vals) { if (& $test $v) { $match = $v; break } }
    if ($null -ne $match) { Write-Host "PASS  $what  [$match]" -ForegroundColor Green; $script:pass++ }
    else { Write-Host "FAIL  $what  got [$($vals -join ' ; ')]" -ForegroundColor Red; $script:fail++ }
}
function Near([string]$s, [double]$x, [double]$tol = 1e-6) { [math]::Abs([double]::Parse($s, [Globalization.CultureInfo]::InvariantCulture) - $x) -le $tol }

# --- import report / inventory
Expect 'cross-checks total = 71'           'Import report' 'crossChecks'       { param($v) $v -eq '71' }
Expect 'cross-checks failed = 0'           'Import report' 'crossChecksFailed' { param($v) $v -eq '0' }
Expect 'inventory = 75 files'              'File inventory' 'files'            { param($v) $v -eq '75' }
Expect 'inventory duplicates = 19'         'File inventory' 'duplicates'       { param($v) $v -eq '19' }

# --- wells / roles / surveys
Expect 'realtime well is the actual well'  'P2_H ST1 [actual] - 05 Realtime/Realtime.pzsdb' 'kind' { param($v) $v -eq 'well' }
Expect 'source well is P2_Pilot.pzsdb'     'P2 Pilot [source] - 01 Offset Data/P2_Pilot.pzsdb' 'kind' { param($v) $v -eq 'well' }
Expect 'H1 copy is unreferenced'           'P2 Pilot [unreferenced] - 01 Offset Data/H1 OffsetWell Data/P2 Pilot.pzsdb' 'kind' { param($v) $v -eq 'well' }
Expect 'realtime survey = 272 stations'    'Realtime.pzsdb > Survey' 'stations' { param($v) $v -eq '272' }
Expect 'pilot survey = 88 stations'        '01 Offset Data/P2_Pilot.pzsdb > Survey' 'stations' { param($v) $v -eq '88' }

# --- RDAH_M (model) curve: 26709 records, 9022.9215135622 - 15699.9518430472 ft
Expect 'RDAH_M records = 26709'            'RDAH_M  [OHMM]' 'loadedSamples' { param($v) $v -eq '26709' }
Expect 'RDAH_M first MD 9022.9215135622'   'RDAH_M  [OHMM]' 'mdFirst' { param($v) Near $v 9022.9215135622 1e-9 }
Expect 'RDAH_M last MD 15699.9518430472'   'RDAH_M  [OHMM]' 'mdLast'  { param($v) Near $v 15699.9518430472 1e-9 }
Expect 'RDAH_M no load error'              'RDAH_M  [OHMM]' 'loadError' { param($v) $v -eq '' }
# --- realtime RDAH: 10038.5 - 15674
Expect 'realtime RDAH first MD 10038.5'    'Realtime.pzsdb > Curves (19) > RDAH  [OHMM]' 'mdFirst' { param($v) Near $v 10038.5 }
Expect 'realtime RDAH last MD 15674'       'Realtime.pzsdb > Curves (19) > RDAH  [OHMM]' 'mdLast'  { param($v) Near $v 15674 }

# --- targets
Expect 'target KOP'        'Targets (3)' 'target' { param($v) $v -like 'KOP|N=0|E=0|TVD=9392.38|Inc=0|Azi=0|Path=VectoredMinimumCurvature' }
Expect 'target Landing_Pt' 'Targets (3)' 'target' { param($v) $v -like 'Landing_Pt|N=447.74|E=452.56|TVD=10029|Inc=90|Azi=45.31|Path=VectoredMinimumCurvature' }
Expect 'target PBHL'       'Targets (3)' 'target' { param($v) $v -like 'PBHL|N=3562|E=3600|TVD=9945.638*|Inc=90|Azi=45.31|Path=VectoredMinimumCurvature' }

# --- geology model
Expect 'bed Middle Member Thickness (8x8 grid)' 'Geology model' 'bed' { param($v) $v -like 'Gridded bed|Middle Member Thickness|GeologyBed|Stretch|srcMD=10130|*|grid=8 x 8' }
Expect 'bed Lower Bakken Top (8x8 grid)'        'Geology model' 'bed' { param($v) $v -like 'Gridded bed|Lower Bakken Top|GeologyBed|Stretch|srcMD=10187|*|grid=8 x 8' }
Expect 'marker Upper Bakken'                    'Geology model' 'bed' { param($v) $v -like 'Marker|Upper Bakken|MarkerBed|*' }
Expect 'beds + markers = 15'                    'Geology model' 'beds' { param($v) $v -eq '15' }
Expect 'steering points = 26'                   'Geology model' 'steeringPoints' { param($v) $v -eq '26' }

Write-Host ""
Write-Host "$pass passed, $fail failed"
if ($fail -gt 0) { exit 1 } else { exit 0 }
