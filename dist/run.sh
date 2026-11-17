#!/bin/sh
# Starts the VA 520 tester with Mono (Raspberry Pi OS / Linux).
cd "$(dirname "$0")"

# Started from ssh? Use the Pi's own screen.
export DISPLAY="${DISPLAY:-:0}"
# Avoids crashes of Mono WinForms text boxes with some input methods.
export MONO_WINFORMS_XIM_STYLE=disabled

if ! command -v mono >/dev/null 2>&1; then
    echo "mono not found.  sudo apt update && sudo apt install -y mono-complete libgdiplus"
    exit 1
fi

# A serial port needs the 'dialout' group:  sudo usermod -aG dialout $USER   (then log out and in)
exec mono VA520Tester.exe "$@"
