@echo off
setlocal

set "REPOSITORY_ROOT=%~dp0.."
set "OUTPUT_ROOT=%REPOSITORY_ROOT%\artifacts\appcontrol"
set "DOTNET_ENVIRONMENT=Development"
set "ASPNETCORE_ENVIRONMENT=Development"
set "ASPNETCORE_URLS=https://localhost:5200;http://localhost:5225"

call :publish "src\Trading.Web\Trading.Web.csproj" "Trading.Web" false
if errorlevel 1 exit /b %errorlevel%
start "Trading.Web" /D "%OUTPUT_ROOT%\Trading.Web" "%OUTPUT_ROOT%\Trading.Web\Trading.Web.exe"
echo Trading.Web started. Open https://localhost:5200/login

if /I not "%~1"=="--include-paper-workers" exit /b 0

call :publish "src\Trading.Workers.Experiments\Trading.Workers.Experiments.csproj" "Trading.Workers.Experiments" true
if errorlevel 1 exit /b %errorlevel%
call :publish "src\Trading.Workers.MarketData\Trading.Workers.MarketData.csproj" "Trading.Workers.MarketData" false
if errorlevel 1 exit /b %errorlevel%

start "Trading.Workers.Experiments" /D "%OUTPUT_ROOT%\Trading.Workers.Experiments" "%OUTPUT_ROOT%\Trading.Workers.Experiments\Trading.Workers.Experiments.exe"
start "Trading.Workers.MarketData" /D "%OUTPUT_ROOT%\Trading.Workers.MarketData" "%OUTPUT_ROOT%\Trading.Workers.MarketData\Trading.Workers.MarketData.exe"
echo Paper-training and market-data workers started.
echo Close their console windows to stop the development processes.
exit /b 0

:publish
dotnet publish "%REPOSITORY_ROOT%\%~1" -c Debug -r win-x64 --self-contained %~3 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded -o "%OUTPUT_ROOT%\%~2"
if errorlevel 1 (
    echo Single-file publish failed for %~1.
    exit /b %errorlevel%
)
exit /b 0
