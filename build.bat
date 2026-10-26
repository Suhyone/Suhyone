@echo off
echo Compiling ScreenKeepAlive...
"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /target:winexe /optimize /platform:anycpu /r:System.Windows.Forms.dll /r:System.Drawing.dll /win32icon:"%~dp0app.ico" /out:"%~dp0ScreenKeepAlive.exe" "%~dp0Program.cs"
if %ERRORLEVEL% EQU 0 (
    copy /y "%~dp0ScreenKeepAlive.exe" "%~dp0โปรแกรมกันจอดับ.exe" >nul
    echo ==============================================
    echo  Build Successful!
    echo  Generated:
    echo    - ScreenKeepAlive.exe
    echo    - โปรแกรมกันจอดับ.exe
    echo ==============================================
) else (
    echo Build Failed!
)
pause
