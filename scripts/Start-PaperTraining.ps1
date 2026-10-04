Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projects = @(
    @{ Name = 'MarketData'; Directory = 'Trading.Workers.MarketData'; Assembly = 'Trading.Workers.MarketData.dll' },
    @{ Name = 'Experiments'; Directory = 'Trading.Workers.Experiments'; Assembly = 'Trading.Workers.Experiments.dll' },
    @{ Name = 'Web'; Directory = 'Trading.Web'; Assembly = 'Trading.Web.dll' }
)

& dotnet build (Join-Path $root 'Trading.sln') '-p:BaseOutputPath=artifacts\paper-start\'
if ($LASTEXITCODE -ne 0) { throw 'Solution build failed; no trading processes were started.' }

$env:DOTNET_ENVIRONMENT = 'Development'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = 'https://localhost:5200'
$started = @()
try {
    foreach ($project in $projects) {
        $directory = Join-Path $root (Join-Path 'src' $project.Directory)
        $assembly = Join-Path $directory (Join-Path 'artifacts\paper-start\Debug\net8.0' $project.Assembly)
        if (-not (Test-Path -LiteralPath $assembly)) {
            throw "Missing $($project.Name) assembly: $assembly"
        }
        $process = Start-Process -FilePath 'dotnet' -ArgumentList @("`"$assembly`"") `
            -WorkingDirectory $directory -NoNewWindow -PassThru
        $started += $process
    }
    Start-Sleep -Seconds 2
    foreach ($process in $started) {
        $process.Refresh()
        if ($process.HasExited) {
            throw "A paper training process exited during startup (PID $($process.Id), code $($process.ExitCode))."
        }
    }
    Write-Host 'Paper training services and web app started. Open https://localhost:5200/workspace/trade'
    Write-Host 'Press Ctrl+C to stop all three processes.'
    $started[-1].WaitForExit()
    if ($started[-1].ExitCode -ne 0) {
        throw "The web app exited with code $($started[-1].ExitCode)."
    }
}
finally {
    foreach ($process in $started) {
        $process.Refresh()
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force
        }
        $process.Dispose()
    }
}
