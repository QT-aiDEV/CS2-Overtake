param(
    [string]$Output = (Join-Path $PSScriptRoot 'bin/range-results.json'),
    [string]$NativeAssembly,
    [string]$ManagedPath = $env:CS2_MANAGED_PATH,
    [string]$BaselineAssembly,
    [string]$RegressionBaselineAssembly,
    [int]$Iterations = 100000
)
$ErrorActionPreference = 'Stop'
$previousTiering = $env:DOTNET_TieredCompilation
try {
    $env:DOTNET_TieredCompilation = '0'
    $arguments = @('run', '--project', (Join-Path $PSScriptRoot 'Overtake.TestRange.csproj'), '-c', 'Release', '--', '--output', $Output, '--iterations', "$Iterations")
    if ($NativeAssembly) {
        if (-not $ManagedPath) { throw 'Native value checks require -ManagedPath or CS2_MANAGED_PATH.' }
        $arguments += @('--native-assembly', $NativeAssembly, '--managed', $ManagedPath)
    }
    if ($BaselineAssembly) { $arguments += @('--baseline-assembly', $BaselineAssembly) }
    if ($RegressionBaselineAssembly) { $arguments += @('--regression-baseline-assembly', $RegressionBaselineAssembly) }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Decision range failed with exit code $LASTEXITCODE. Inspect $Output for the first failed contract." }
} finally {
    $env:DOTNET_TieredCompilation = $previousTiering
}
