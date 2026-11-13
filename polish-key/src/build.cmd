@echo off
setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set OUT=%~dp0..\out\PolishKey.exe
if not exist "%~dp0..\out" mkdir "%~dp0..\out"
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /codepage:65001 ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ^
  /out:"%OUT%" "%~dp0PolishKey.cs"
if errorlevel 1 exit /b 1
echo built %OUT%
