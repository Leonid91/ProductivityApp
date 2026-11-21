# ProductivityApp

## Start without installing

1. Download this whole repository (**Code**, then **Download ZIP**), and unzip it.
2. Start the app from that folder:
   - Windows: double-click `start.cmd`.
   - macOS and Linux: open Terminal in that folder and run `sh start.sh`.
     On Linux, it says which libraries to install if some are missing.

Or install it, as below.

## Windows

1. Download [ProductivityApp-0.1.0-windows-x64.exe](release/ProductivityApp-0.1.0-windows-x64.exe).
2. Double-click it. If Windows shows "Windows protected your PC", click **More info**, then **Run anyway**.

## macOS

macOS 14 (Sonoma) or later.

1. Download [ProductivityApp-0.1.0-macos-apple-silicon.zip](release/ProductivityApp-0.1.0-macos-apple-silicon.zip) (Apple processor) or [ProductivityApp-0.1.0-macos-intel.zip](release/ProductivityApp-0.1.0-macos-intel.zip) (Intel processor).
2. Double-click the zip file.
3. Drag `ProductivityApp.app` to the Applications folder.
4. In Terminal, run:

   ```bash
   xattr -dr com.apple.quarantine /Applications/ProductivityApp.app
   ```

5. Open ProductivityApp from the Applications folder.

## Linux

Debian and Ubuntu:

1. Download [productivityapp_0.1.0_amd64.deb](release/productivityapp_0.1.0_amd64.deb) (ARM processor: [productivityapp_0.1.0_arm64.deb](release/productivityapp_0.1.0_arm64.deb)).
2. In the download folder, run:

   ```bash
   sudo apt install ./productivityapp_0.1.0_amd64.deb
   ```

3. Open ProductivityApp from the applications menu.

Other distributions:

1. Install WebKitGTK 4.1, libnotify and Tesseract with its English and French data with the package manager.
2. Download [ProductivityApp-0.1.0-linux-x64.tar.gz](release/ProductivityApp-0.1.0-linux-x64.tar.gz) (ARM processor: [ProductivityApp-0.1.0-linux-arm64.tar.gz](release/ProductivityApp-0.1.0-linux-arm64.tar.gz)).
3. In the download folder, run:

   ```bash
   tar -xzf ProductivityApp-0.1.0-linux-x64.tar.gz
   ./ProductivityApp/ProductivityApp
   ```

## Build from the source

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download) and [Node.js](https://nodejs.org) 22 or later.
2. In this folder, build the interface:

   ```bash
   cd ui
   npm ci
   npm run build
   cd ..
   ```

3. Build and start the app:
   - Windows: `publish\ProductivityApp.exe` is in this repository, built from this code: double-click it. To build
     it again (PowerShell):

     ```powershell
     dotnet publish src\Finance.App\Finance.App.csproj -c Release -f net10.0-windows10.0.19041.0 -o publish
     ```

   - macOS and Linux:

     ```bash
     dotnet run --project src/Finance.App -c Release
     ```

To run the tests: `dotnet test --project tests/Finance.UnitTests/Finance.UnitTests.csproj` (and `tests/Finance.IntegrationTests`).

## First-launch log

The first time the app opens, it writes each step to a small text file. If the app does not open well, send this file.

- Windows: `%LOCALAPPDATA%\ProductivityApp\first-launch.txt`
- macOS: `~/Library/Application Support/ProductivityApp/first-launch.txt`
- Linux: `~/.local/share/ProductivityApp/first-launch.txt`

To copy it on macOS, run in Terminal:

```bash
pbcopy < ~/Library/Application\ Support/ProductivityApp/first-launch.txt
```
