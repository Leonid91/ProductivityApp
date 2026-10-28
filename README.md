# Productivity — Finances

## Run the app

Windows 10 or 11 (64-bit). Copy `ProductivityApp.exe` anywhere and double-click it. Nothing else to install: the
.NET runtime is inside the file, and the WebView2 runtime it uses is part of Windows.

Data stays on the computer, in `%LOCALAPPDATA%\ProductivityApp`.

## Build the exe from the source

1. Install the .NET 10 SDK from https://dotnet.microsoft.com/download and Node.js 22 or later (LTS) from https://nodejs.org.
2. In PowerShell, from this folder:

   ```powershell
   .\tools\build.ps1
   ```

   The result is `publish\ProductivityApp.exe`.

## Run the tests

```powershell
.\tools\test.ps1
```

They use fictitious statements generated on the fly. The end-to-end part starts the app off-screen, so no window appears.
