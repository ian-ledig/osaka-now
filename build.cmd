@echo off
rem Uses the C# compiler that ships with Windows; no SDK needed.
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize /codepage:65001 /r:System.Web.Extensions.dll /out:"%~dp0OsakaEvents.exe" "%~dp0OsakaEvents.cs"
