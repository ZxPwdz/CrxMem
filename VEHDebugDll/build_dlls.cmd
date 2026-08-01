@echo off
call "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\Tools\VsDevCmd.bat" -arch=x86 >nul 2>&1
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" "C:\Users\admin\Documents\CrxMem\VEHDebugDll\VEHDebug32.vcxproj" /p:Configuration=Release /p:Platform=Win32 /v:m /nologo
if errorlevel 1 exit /b 1
call "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\Tools\VsDevCmd.bat" -arch=amd64 >nul 2>&1
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" "C:\Users\admin\Documents\CrxMem\VEHDebugDll\VEHDebug64.vcxproj" /p:Configuration=Release /p:Platform=x64 /v:m /nologo
exit /b %errorlevel%
