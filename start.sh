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
run="$here/.run/${name%%.*}"
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
  rm -rf "$run" "$run.part"
  mkdir -p "$run.part"
  case "$pack" in
    *.zip) ditto -x -k "$file" "$run.part" >> "$log" 2>&1 ;;
    *) tar -xzf "$file" -C "$run.part" >> "$log" 2>&1 ;;
  esac
  if [ $? -ne 0 ]; then note "unpacking failed"; echo "The app could not be unpacked. Details: $log"; exit 1; fi
  mv "$run.part" "$run"
  note "unpacked"
  if [ "$(uname -s)" = Darwin ]; then
    # A file downloaded with a browser is marked as such: macOS would refuse to open the app.
    xattr -dr com.apple.quarantine "$app" >> "$log" 2>&1
    note "download mark removed"
    if codesign --verify --deep --strict "$app" >> "$log" 2>&1; then note "signature: valid"; else note "signature: NOT valid"; fi
  else
    # The libraries the window needs (WebKitGTK 4.1, libnotify): listed when missing.
    missing=$(ldd "$run/ProductivityApp/libPhotino.Native.so" 2>&1 | grep "not found" | awk '{print $1}' | tr '\n' ' ')
    if [ -n "$missing" ]; then
      note "missing libraries: $missing"
      echo "Missing libraries: $missing"
      echo "Install WebKitGTK 4.1 and libnotify with the package manager, then run sh start.sh again:"
      echo "  Debian, Ubuntu: sudo apt install libwebkit2gtk-4.1-0 libnotify4"
      echo "  Fedora:         sudo dnf install webkit2gtk4.1 libnotify"
      echo "  Arch:           sudo pacman -S webkit2gtk-4.1 libnotify"
      rm -rf "$run"
      exit 1
    fi
    note "libraries: all found"
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
