@echo off
setlocal
title CrxShield Driver Loader

:: ============================================================
::  Start-CrxShield.cmd
::  Run after every reboot until the driver loads successfully.
::  - Tries to start the CrxShield service.
::  - If the kernel rejects the signature (error 577), offers to
::    enable testsigning and reboot. Run again after the reboot.
:: ============================================================

:: --- Self-elevate (UAC prompt) ---
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator rights...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo ============================================
echo   CrxShield post-reboot driver loader
echo ============================================
echo.
echo [step 1] Trying to start CrxShield service...
echo.
sc start CrxShield
set RC=%errorlevel%

if %RC%==0 goto success
if %RC%==1056 goto success   :: already running

echo.
echo --------------------------------------------
echo  Start failed with error %RC%.
if %RC%==577 echo  577 = kernel Code Integrity rejected the signature.
echo --------------------------------------------
echo.
choice /c YN /n /m "Enable testsigning mode and reboot now? (Y/N) "
if errorlevel 2 goto abort

bcdedit /set testsigning on
if %errorlevel% neq 0 (
    echo.
    echo ERROR: bcdedit failed. If it says the value is protected
    echo by Secure Boot policy, disable Secure Boot in BIOS first.
    pause
    exit /b 1
)

echo.
echo Testsigning enabled. Rebooting in 10 seconds ^(Ctrl+C to cancel^)...
echo After the reboot, run this script again to start the driver.
shutdown /r /t 10
pause
exit /b 0

:success
echo.
echo [step 2] SUCCESS - CrxShield driver is running.
echo You can now start CrxMem and use kernel mode.
echo.
pause
exit /b 0

:abort
echo.
echo Aborted - no changes made. The driver is NOT loaded.
pause
exit /b 1
