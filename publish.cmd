@echo off
rem Single self-contained exe in dist\
tasklist /FI "IMAGENAME eq DuckDuck.exe" | find /I "DuckDuck.exe" >nul && (
    echo DuckDuck is running and holds dist\DuckDuck.exe. Quit it from the tray, then rerun.
    exit /b 1
)
dotnet publish "%~dp0DuckDuck.csproj" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%~dp0dist"
