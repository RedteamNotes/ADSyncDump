@echo off
echo ====================================
echo ADSyncDump Build Script
echo ====================================

set CSC_PATH=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe

if not exist "%CSC_PATH%" (
    echo [!] csc.exe not found, please install .NET Framework 4.8
    pause
    exit /b 1
)

echo [*] Compiling x64 binary...
"%CSC_PATH%" /platform:x64 /target:exe /out:ADSyncDump.exe ADSyncDump.cs

if %ERRORLEVEL% EQU 0 (
    echo [+] Build success: ADSyncDump.exe
) else (
    echo [!] Build failed
)

pause
