@echo off
chcp 65001 >nul
echo 2 | dotnet run --project test/SOPTest/SOPTest.csproj --no-build
