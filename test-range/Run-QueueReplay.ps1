param(
    [Parameter(Mandatory = $true)][string]$ControlArchive,
    [string]$ManagedPath = $env:CS2_MANAGED_PATH,
    [string]$Candidate = (Join-Path $PSScriptRoot '../src/bin/Release/net472/Overtake.dll'),
    [string]$Output = (Join-Path $PSScriptRoot 'bin/queue-replay.json'),
    [switch]$LegacyApproach
)
$ErrorActionPreference = 'Stop'
if (-not $ManagedPath -or -not (Test-Path -LiteralPath (Join-Path $ManagedPath 'Game.dll'))) {
    throw 'Set CS2_MANAGED_PATH or pass -ManagedPath for the installed Cities2_Data/Managed directory.'
}
if (-not (Test-Path -LiteralPath $Candidate)) { throw 'Build the candidate Release DLL or pass -Candidate.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archivePath = (Resolve-Path -LiteralPath $ControlArchive).Path
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $entry = @($archive.Entries | Where-Object Name -eq 'TwoLanePassing.dll')
    if ($entry.Count -ne 1) { throw 'Expected exactly one TwoLanePassing.dll (the mod''s former name) in the historical archive.' }
    $controlDir = Join-Path $PSScriptRoot 'bin/historical-0.1.1-control'
    New-Item -ItemType Directory -Force $controlDir | Out-Null
    $control = Join-Path $controlDir 'TwoLanePassing.dll'
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry[0], $control, $true)
} finally {
    $archive.Dispose()
}
$arguments = @('--queue-control', $control, '--native-assembly', $Candidate, '--managed', $ManagedPath, '--output', $Output)
if ($LegacyApproach) { $arguments += '--legacy-approach' }
dotnet run --project (Join-Path $PSScriptRoot 'Overtake.TestRange.csproj') -c Release -- @arguments
if ($LASTEXITCODE -ne 0) { throw "Queue replay failed with exit code $LASTEXITCODE." }
