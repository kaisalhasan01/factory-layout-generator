# Builds the engine and the Unity player, then captures screenshots without anyone watching.
#   .\tools\build_and_shoot.ps1                     build + blueprint and 3D shots of demo-line
#   .\tools\build_and_shoot.ps1 -Layout layouts\x.json -NoBuild
#   .\tools\build_and_shoot.ps1 -Frames            also capture the blueprint-to-3D transition
param(
    [string]$Layout = "layouts\demo-line.json",
    [string]$Out = "logs\shots",
    [switch]$NoBuild,
    [switch]$Frames,
    [switch]$Tour,
    [string]$Extra = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$unity = "$env:ProgramFiles\Unity\Hub\Editor\6000.5.7f1\Editor\Unity.exe"
$exe = Join-Path $root "Build\FactoryLayout\FactoryLayout.exe"
$layoutPath = Join-Path $root $Layout
$outDir = Join-Path $root $Out
New-Item -ItemType Directory -Force $outDir | Out-Null

if (-not $NoBuild) {
    dotnet build (Join-Path $root "src\FactoryLayout.Core") -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "engine build failed" }
    $log = Join-Path $root "logs\build.log"
    $p = Start-Process $unity -ArgumentList @("-batchmode", "-nographics", "-quit", "-projectPath", "`"$root\unity`"",
        "-executeMethod", "FactoryBuild.BuildPlayer", "-logFile", "`"$log`"") -Wait -PassThru
    if ($p.ExitCode -ne 0) {
        Select-String -Path $log -Pattern "error CS" | Select-Object -First 20 | ForEach-Object { $_.Line }
        throw "Unity build failed (see $log)"
    }
}

$name = [IO.Path]::GetFileNameWithoutExtension($Layout)
$common = @("-screen-width", "1600", "-screen-height", "900", "-screen-fullscreen", "0", "-layout", "`"$layoutPath`"")
if ($Extra) { $common += $Extra.Split(" ") }

function Shot([string]$file, [string[]]$more) {
    $args = $common + $more + @("-screenshot", "`"$(Join-Path $outDir $file)`"", "-logFile", "`"$(Join-Path $root "logs\player.log")`"")
    Start-Process $exe -ArgumentList $args -Wait | Out-Null
    Write-Host "  $file"
}
Shot "$name`_blueprint.png" @("-step", "conveyor")
Shot "$name`_equipment.png" @("-step", "equipment")
Shot "$name`_3d.png" @("-view", "3d")
if ($Tour) { Shot "$name`_tour.png" @("-step", "equipment", "-tour", "3") }

if ($Frames) {
    $fdir = Join-Path $outDir "$name`_frames"
    if (Test-Path $fdir) { Remove-Item -Recurse -Force $fdir }
    Start-Process $exe -ArgumentList ($common + @("-frames", "`"$fdir`"", "-logFile", "`"$(Join-Path $root "logs\player.log")`"")) -Wait | Out-Null
    Write-Host "  $(Get-ChildItem $fdir | Measure-Object | Select-Object -ExpandProperty Count) frames in $fdir"
}
