param([string]$ManagedPath = $env:CS2_MANAGED_PATH)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$receiptPath = Join-Path $repo 'tests/bin/preflight-receipt.json'
if (Test-Path -LiteralPath $receiptPath) { Remove-Item -LiteralPath $receiptPath }
if (-not $ManagedPath -or -not (Test-Path -LiteralPath (Join-Path $ManagedPath 'Game.dll'))) {
    throw 'Set CS2_MANAGED_PATH to the installed Cities2_Data/Managed directory.'
}
Push-Location $repo
try {
    & dotnet run --project tests/Overtake.Checks.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Production logic regression failed. Stopping.' }
    & dotnet build src/Overtake.csproj -c Release "-p:ManagedPath=$ManagedPath" --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Production build failed. Stopping.' }
    $dll = Join-Path $repo 'src/bin/Release/net472/Overtake.dll'
    & dotnet run --project tests/NativeInputs/Overtake.NativeInputs.csproj -c Release -- $dll $ManagedPath
    if ($LASTEXITCODE -ne 0) { throw 'Native-input integration preflight failed. Stopping.' }
    & (Join-Path $repo 'test-range/Run-TestRange.ps1') -NativeAssembly $dll -ManagedPath $ManagedPath
    if ($LASTEXITCODE -ne 0) { throw 'Decision range failed. Stopping.' }
    $receipt = [ordered]@{
        passed = $true
        utc = [DateTime]::UtcNow.ToString('O')
        assemblySha256 = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
        gameSha256 = (Get-FileHash -LiteralPath (Join-Path $ManagedPath 'Game.dll') -Algorithm SHA256).Hash
        scope = 'Offline production logic and native-input route/admission preflight. No physical movement, ECS mutation or save/load proof.'
    }
    $receipt | ConvertTo-Json | Set-Content -LiteralPath $receiptPath
    Write-Output "OFFLINE PREFLIGHT PASSED for $($receipt.assemblySha256)"
} finally { Pop-Location }
