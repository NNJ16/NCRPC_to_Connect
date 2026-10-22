@echo off
cd /d "%~dp0"
start "" cmd /c "timeout /t 2 >nul & start http://localhost:5087"
dotnet run --project src\NcrpToConnect.Web --urls http://localhost:5087
