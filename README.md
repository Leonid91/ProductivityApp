# ProductivityApp

## Install

### Windows

1. Download [ProductivityApp-0.1.0-windows-x64.exe](release/ProductivityApp-0.1.0-windows-x64.exe).
2. Double-click it.
3. If Windows shows "Windows protected your PC", click **More info**, then **Run anyway**.

### macOS

The app needs macOS 14 or later.

1. Download the zip file for your processor:
   - Apple processor: [ProductivityApp-0.1.0-macos-apple-silicon.zip](release/ProductivityApp-0.1.0-macos-apple-silicon.zip)
   - Intel processor: [ProductivityApp-0.1.0-macos-intel.zip](release/ProductivityApp-0.1.0-macos-intel.zip)
2. Double-click the zip file.
3. Drag `ProductivityApp.app` to the Applications folder.
4. In Terminal, run:

   ```bash
   xattr -dr com.apple.quarantine /Applications/ProductivityApp.app
   ```

5. Open ProductivityApp from the Applications folder.

### Linux

#### Debian and Ubuntu

1. Download the package for your processor:
   - x64 processor: [productivityapp_0.1.0_amd64.deb](release/productivityapp_0.1.0_amd64.deb)
   - ARM processor: [productivityapp_0.1.0_arm64.deb](release/productivityapp_0.1.0_arm64.deb)
2. In the download folder, run (on ARM, replace `amd64` with `arm64`):

   ```bash
   sudo apt install ./productivityapp_0.1.0_amd64.deb
   ```

3. Open ProductivityApp from the applications menu.

#### Other distributions

1. With the package manager, install WebKitGTK 4.1, libnotify, and Tesseract with its English and French data.
2. Download the archive for your processor:
   - x64 processor: [ProductivityApp-0.1.0-linux-x64.tar.gz](release/ProductivityApp-0.1.0-linux-x64.tar.gz)
   - ARM processor: [ProductivityApp-0.1.0-linux-arm64.tar.gz](release/ProductivityApp-0.1.0-linux-arm64.tar.gz)
3. In the download folder, run (on ARM, replace `x64` with `arm64`):

   ```bash
   tar -xzf ProductivityApp-0.1.0-linux-x64.tar.gz
   ./ProductivityApp/ProductivityApp
   ```

## Run from the repository

1. Download the repository: click **Code**, then **Download ZIP**.
2. Unzip it.
3. Start the app from the unzipped folder:
   - Windows: double-click `start.cmd`.
   - macOS and Linux: open Terminal in the folder and run `sh start.sh`.

On Linux, `start.sh` lists the libraries to install if some are missing.

## Logs

At its first start, the app writes each step to a text file. If the app does not open, send this file.

- Windows: `%LOCALAPPDATA%\ProductivityApp\first-launch.txt`
- macOS: `~/Library/Application Support/ProductivityApp/first-launch.txt`
- Linux: `~/.local/share/ProductivityApp/first-launch.txt`

To copy the file on macOS, run in Terminal:

```bash
pbcopy < ~/Library/Application\ Support/ProductivityApp/first-launch.txt
```
