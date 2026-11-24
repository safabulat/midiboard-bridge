@echo off
:: Midiboard Bridge (GUI) - builds with the C# compiler that ships with Windows (.NET Framework 4.8). No SDK needed.
:: Output: app\bin\MidiboardBridge.exe
set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "FW=C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
set "WM=C:\Windows\System32\WinMetadata"
set "OUT=%~dp0bin"
if not exist "%OUT%" mkdir "%OUT%"
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /utf8output /codepage:65001 /out:"%OUT%\MidiboardBridge.exe" /win32icon:"%~dp0icon.ico" ^
  /r:"%WM%\Windows.Devices.winmd" /r:"%WM%\Windows.Foundation.winmd" /r:"%WM%\Windows.Storage.winmd" /r:"%WM%\Windows.Security.winmd" ^
  /r:"%FW%\System.Runtime.dll" /r:"%FW%\System.Runtime.InteropServices.WindowsRuntime.dll" /r:"%FW%\System.Threading.Tasks.dll" ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  "%~dp0Core.cs" "%~dp0MainForm.cs" "%~dp0AssemblyInfo.cs" || exit /b 1
echo Built: %OUT%\MidiboardBridge.exe
