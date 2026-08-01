@echo off
setlocal
title CrxShield End-to-End Verification

:: Self-elevate (the driver device is restricted to SYSTEM/Administrators)
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator rights...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

"%~dp0VerifyDriver\bin\Release\net8.0-windows\VerifyCrxShield.exe"
echo.
pause
