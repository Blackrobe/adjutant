@echo off
rem Builds adjutant.exe with the .NET Framework 4.8 C# compiler that ships with Windows (C# 5, no SDK).
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /out:"%~dp0adjutant.exe" ^
  /lib:"%FW%\WPF" ^
  /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll /r:System.Xaml.dll ^
  /r:System.Web.Extensions.dll ^
  "%~dp0adjutant.cs"
