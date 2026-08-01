@echo off
call "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\Tools\VsDevCmd.bat" -arch=x86 >nul 2>&1
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" "C:\Users\admin\Documents\CrxMem\FxpStrip\fxp.vcxproj" /p:Configuration=Release /p:Platform=Win32 /v:m /nologo
exit /b %errorlevel%
