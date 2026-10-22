@echo off
rem Single self-contained exe in dist\
dotnet publish "%~dp0DuckDuck.csproj" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%~dp0dist"
