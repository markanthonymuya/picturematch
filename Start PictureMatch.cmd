@echo off
setlocal
cd /d "%~dp0"
dotnet run --project PictureMatch.csproj
if errorlevel 1 pause
