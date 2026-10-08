@echo off
rem Compiles snip.exe with the .NET Framework compiler built into Windows.
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /target:winexe /win32icon:camera.ico /resource:camera.ico,camera.ico /out:snip.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll snip.cs
