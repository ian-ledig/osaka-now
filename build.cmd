@echo off
rem Builds OsakaNow.exe with the C# compiler that ships with Windows; no SDK or packages needed.
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize /codepage:65001 /win32icon:"%~dp0assets\icon.ico" /r:System.Web.Extensions.dll /out:"%~dp0OsakaNow.exe" "%~dp0OsakaNow.cs"
