param(
    [string]$Exe = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\DesktopJumpList\DesktopJumpList.csproj'
if (-not $Exe) {
    $Exe = Join-Path $root 'src\DesktopJumpList\bin\Debug\net8.0-windows\DesktopJumpList.exe'
}
if (-not (Test-Path $Exe)) {
    & dotnet build $project -c Debug -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
$out = Join-Path $root 'artifacts\preview'
New-Item -ItemType Directory -Force -Path $out | Out-Null

$jobs = @(
    @('shortcuts', 'light'), @('shortcuts', 'dark'),
    @('insights', 'light'), @('insights', 'dark'), @('insights-bottom', 'light'),
    @('editor', 'light'), @('editor', 'dark'),
    @('settings', 'light'), @('settings', 'dark'),
    @('dock', 'light'), @('dock', 'dark'), @('empty', 'light')
)

foreach ($job in $jobs) {
    $view = $job[0]
    $theme = $job[1]
    $shot = Join-Path $out "final-$view-$theme.png"
    $process = Start-Process -FilePath $Exe -ArgumentList @('--preview', "--theme=$theme", "--shot=$shot", "--view=$view") -PassThru
    if (-not $process.WaitForExit(20000)) {
        $process.Kill()
        throw "Timed out rendering $view / $theme."
    }
    if (-not (Test-Path $shot)) { throw "Missing render for $view / $theme." }
    Write-Host "rendered $view / $theme"
}

Write-Host 'Preview assets written to artifacts/preview. Run tools/compose-docs.ps1 next.'
