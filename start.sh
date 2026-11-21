#!/bin/sh
# Starts ProductivityApp from this folder, without installing it (macOS and Linux): sh start.sh
# The first time, the app of the release folder is unpacked into .run/ here. That first time, each step is written to
# the first-launch log, and the app adds its own start to it:
#   macOS  ~/Library/Application Support/ProductivityApp/first-launch.txt
#   Linux  ~/.local/share/ProductivityApp/first-launch.txt
set -u
here=$(cd "$(dirname "$0")" && pwd -P)
cpu=$(uname -m)
case "$(uname -s)" in
  Darwin)
    log="$HOME/Library/Application Support/ProductivityApp/first-launch.txt"
    major=$(sw_vers -productVersion 2>/dev/null | cut -d. -f1)
    if [ -n "$major" ] && [ "$major" -lt 14 ] 2>/dev/null; then
      echo "ProductivityApp needs macOS 14 (Sonoma) or later; this Mac has macOS $(sw_vers -productVersion)."
      exit 1
    fi
    if [ "$cpu" = arm64 ] || [ "$(sysctl -n hw.optional.arm64 2>/dev/null)" = 1 ]; then pack=macos-apple-silicon.zip; else pack=macos-intel.zip; fi ;;
  Linux)
    log="${XDG_DATA_HOME:-$HOME/.local/share}/ProductivityApp/first-launch.txt"
    case "$cpu" in
      x86_64|amd64) pack=linux-x64.tar.gz ;;
      aarch64|arm64) pack=linux-arm64.tar.gz ;;
      *) echo "ProductivityApp runs on x64 and ARM64 processors, not on $cpu."; exit 1 ;;
    esac ;;
  *) echo "On Windows, double-click start.cmd."; exit 1 ;;
esac

file=$(ls "$here"/release/ProductivityApp-*-"$pack" 2>/dev/null | tail -n 1)
if [ -z "$file" ]; then echo "Not found: release/ProductivityApp-<version>-$pack"; exit 1; fi
name=$(basename "$file")
case "$name" in *.zip) base=${name%.zip} ;; *) base=${name%.tar.gz} ;; esac
run="$here/.run/$base"
case "$pack" in *.zip) app="$run/ProductivityApp.app" ;; *) app="$run/ProductivityApp/ProductivityApp" ;; esac

if [ ! -e "$app" ]; then
  # First start of this release: a new first-launch log.
  mkdir -p "$(dirname "$log")"
  : > "$log"
  note() { printf '%s  %s\n' "$(date +%H:%M:%S)" "$*" >> "$log"; }
  note "start.sh: first start of $name, from $here"
  note "system: $(uname -srm)"
  [ "$(uname -s)" = Darwin ] && note "macOS: $(sw_vers -productVersion 2>&1) ($(sw_vers -buildVersion 2>&1))"
  note "unpacking into $run"
  # Earlier releases unpacked here are not needed any more.
  rm -rf "$here/.run"
  mkdir -p "$run.part"
  case "$pack" in
    *.zip) ditto -x -k "$file" "$run.part" >> "$log" 2>&1 ;;
    *) tar -xzf "$file" -C "$run.part" >> "$log" 2>&1 ;;
  esac
  if [ $? -ne 0 ]; then note "unpacking failed"; rm -rf "$run.part"; echo "The app could not be unpacked. Details: $log"; exit 1; fi
  mv "$run.part" "$run"
  note "unpacked"
  if [ "$(uname -s)" = Darwin ]; then
    # A file downloaded with a browser is marked as such: macOS would refuse to open the app.
    xattr -dr com.apple.quarantine "$app" >> "$log" 2>&1
    note "download mark removed"
    if codesign --verify --deep --strict "$app" >> "$log" 2>&1; then note "signature: valid"; else note "signature: NOT valid"; fi
  else
    # The libraries the app needs from the system (WebKitGTK 4.1, libnotify, fontconfig...): listed when missing.
    missing=$(for so in "$run"/ProductivityApp/*.so; do ldd "$so" 2>&1; done | grep "not found" | awk '{print $1}' | sort -u | tr '\n' ' ')
    if [ -n "$missing" ]; then
      note "missing libraries: $missing"
      echo "Missing libraries: $missing"
      echo "Install them with the package manager (WebKitGTK 4.1, libnotify, fontconfig), then run sh start.sh again:"
      echo "  Debian, Ubuntu: sudo apt install libwebkit2gtk-4.1-0 libnotify4 libfontconfig1"
      echo "  Fedora:         sudo dnf install webkit2gtk4.1 libnotify fontconfig"
      echo "  Arch:           sudo pacman -S webkit2gtk-4.1 libnotify fontconfig"
      rm -rf "$run"
      exit 1
    fi
    note "libraries: all found"
    # Reading pictures of statements uses Tesseract; everything else works without it.
    if command -v tesseract > /dev/null 2>&1; then
      note "tesseract: $(tesseract --version 2>&1 | head -n 1)"
    else
      note "tesseract: not installed (pictures of statements cannot be read)"
      echo "To read pictures of statements, install Tesseract with its English and French data"
      echo "  (Debian, Ubuntu: sudo apt install tesseract-ocr tesseract-ocr-eng tesseract-ocr-fra)."
    fi
    # Downloads (exchange rates, holidays) use the system's OpenSSL.
    if ! ls /usr/lib*/libssl.so.* /usr/lib*/*/libssl.so.* /lib*/libssl.so.* /lib*/*/libssl.so.* > /dev/null 2>&1; then note "OpenSSL (libssl) not found: exchange rates and holidays cannot be downloaded"; fi
    if [ -z "${DISPLAY:-}" ] && [ -z "${WAYLAND_DISPLAY:-}" ]; then note "no display found (DISPLAY and WAYLAND_DISPLAY are empty)"; fi
  fi
  note "starting the app"
  first=1
else
  first=0
fi

if [ "$(uname -s)" = Darwin ]; then
  open "$app"
  code=$?
else
  # Its own process, which goes on when this terminal closes. Errors of the first start go to the log.
  if [ $first = 1 ]; then nohup "$app" > /dev/null 2>> "$log" & else nohup "$app" > /dev/null 2>&1 & fi
  code=0
fi
[ $first = 1 ] && printf '%s  %s\n' "$(date +%H:%M:%S)" "start.sh: app started (code $code)" >> "$log"
exit $code
