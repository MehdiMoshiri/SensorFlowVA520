# VA 520 Flow Sensor Tester

[![Build](https://github.com/MehdiMoshiri/SensorFlowVA520/actions/workflows/build.yml/badge.svg)](https://github.com/MehdiMoshiri/SensorFlowVA520/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

🇮🇷 [راهنمای فارسی](README.fa.md)

A test program for the **CS Instruments VA 520** flow sensor (Modbus RTU over RS485, 4..20 mA output, pulse output).
Written in **C# / .NET Framework 4.8 / Windows Forms**, no database, and it also runs with **Mono on a Raspberry Pi**.
Every test variable (port, baud rate, parity, slave ID, word order, timeouts, retries, intervals, ...) is on the form.

> **Independent project.** It is not made, endorsed or supported by CS Instruments. The sensor manual is not part of this
> repository; the register map was written from the manual `VA 520 EN V1.26` (chapter 10 wiring, 11.3.2 Modbus registers,
> 6 and 11.3.6 4..20 mA, 11.3.3 pulse). Check the current manual of your sensor before relying on the numbers.

## Status

* Verified: compiles with C# 5 on .NET 4.8, and **57 automated tests** run the protocol core against the built-in VA 520 simulator
  (CRC, echo removal, byte/word orders, RTU, MBAP, exceptions, register writes, retries, error log, poller).
  The Windows UI was also exercised against the simulator (connect, polling, statistics, log, raw frame, read, scan, stress test).
* **Not verified yet:** running under **Mono / Raspberry Pi**, and talking to a **real serial port and a real sensor**.
  The code only uses simple, portable WinForms / `SerialPort` features, but please try it and open an issue with the log if something breaks.
* The *Auto-detect* and *Write* buttons are covered at core level only, not by clicking through the form.

## Features

* **Modbus RTU (serial), Modbus TCP and RTU over TCP**, function 03/04 for reading, 16 for writing.
* All VA 520 values: flow in 16 units, consumption in 8 units, velocity, gas temperature; min / max / average, trend chart, limit checks.
* Read and change the sensor's own communication settings (ID, baud rate, parity, stop bits, word order).
* Tools: raw hex frames (CRC added automatically), read/write any register, register scan, serial auto-detect,
  stress/stability test with report, and a **VA 520 simulator** with fault injection (dropped replies, bad CRC, delay, echo).
* Calculators for the 4..20 mA output and the pulse output.
* Error log text file next to the exe: `No | Code | Text | Time | Date`, numbered across runs.
* Trace of every TX / RX frame in hex, CSV logging of readings.
* Helpers for awkward RS485 adapters: DTR / RTS handling, RTS direction control, echo removal.

## Quick start

### Windows

1. Download the zip from **Releases**, unzip, run `VA520Tester.exe` (needs .NET Framework 4.8, included in Windows 10/11).
2. Or build it yourself:
   ```
   build_windows.cmd
   ```
   (needs the .NET SDK; it builds `dist\VA520Tester.exe`, builds the self test and runs it). You can also open
   `VA520Tester.csproj` in Visual Studio 2019/2022.

### Raspberry Pi / Linux (Mono)

```bash
sudo apt update
sudo apt install -y mono-complete libgdiplus
sudo usermod -aG dialout $USER      # serial port access; log out and in once afterwards

# release zip: unzip, then
chmod +x run.sh
./run.sh

# or compile on the Pi (no download needed)
chmod +x build_on_pi.sh dist/run.sh
./build_on_pi.sh
./dist/run.sh
```

* A desktop session is needed. If you start it over ssh, `run.sh` sets `DISPLAY=:0` by default.
* Port names: `/dev/ttyUSB0` (USB adapter), `/dev/ttyAMA0` or `/dev/serial0` (the Pi's UART). You can also type a name by hand.
* If `mono-complete` is missing from your distribution, install Mono from https://www.mono-project.com/download/stable/ .
* The window also fits small screens (e.g. 800x480); the Connection tab scrolls.

## Wiring the sensor (manual chapter 10)

Connector **A** (M12, A-coded, seen from the terminal side):

| Pin | Wire | Function |
|---|---|---|
| 1 | brown | +VB (supply 18..36 VDC) |
| 2 | white | RS485 **A** (Modbus +) |
| 3 | blue | -VB (0 V) |
| 4 | black | RS485 **B** (Modbus -) |
| 5 | grey | 4..20 mA (I+) |

* Use a **USB <-> RS485** adapter (preferably with automatic direction control). A to A, B to B; swap them if there is no answer.
* If the sensor is the last device on the bus, switch the internal 120 ohm terminator (DIP switch in the sensor head) **on**,
  or put a 120 ohm resistor between pins 2 and 4. The DIP switch only switches the terminating resistor.
* Factory settings: **slave ID 1, 19200 baud, 8 data bits, parity Even, 1 stop bit, word order ABCD**.
* If your adapter switches direction with RTS, enable *RTS controls RS485 direction* in the Connection tab
  (direction control through a GPIO pin is not supported).
* The sensor is a Modbus **slave**: it never sends anything by itself, it only answers a valid request for its ID.
  Silence on the line without a request is normal.

## Tabs

The top bar always has **Connect / Disconnect / Start polling / Stop / Read once / Auto-reconnect** and the link state.

### Connection - all test variables
* **Link:** transport (`Serial RTU`, `Modbus TCP`, `RTU over TCP`), slave ID, read function (03 or 04), word/byte order
  (`ABCD / CDAB / BADC / DCBA`), address offset (for devices whose addresses are shifted by one).
* **Serial port:** port, baud rate (1200..115200), data bits, parity, stop bits, DTR, *Keep RTS high*, RTS direction control and its delays,
  *Converter echoes my request: remove the echo*.
* **TCP:** host, port, connect timeout.
* **Timing:** response timeout, retries, silence before sending, end-of-frame silence, pause after a reply, and
  *Skip rest of the cycle after a timeout* (default off: every ticked value is requested in every cycle even when one of them times out;
  when on, the rest of that cycle is skipped, so cycles finish faster when the sensor is silent).
* **Error log / CSV / settings:** maximum log size, suppression of repeated errors, CSV logging, save/load settings (`VA520Tester.ini`).
* **Custom channels:** four user-defined registers (address + type) that appear in the Monitor list.
* **Limit checks:** low/high limits for flow, velocity and gas temperature; violations are written to the error log.

### Monitor
All sensor values (tick the ones to poll). Per value: current, min, max, average, time and status.
On the right: communication statistics (requests, successful, timeouts, CRC errors, sensor exceptions, retries, success rate,
response time last/avg/min/max, cycle time) and a trend chart of the selected value.

### Sensor settings
Read and write the sensor's communication registers 2000..2004: Modbus ID, baud rate, parity, stop bits, word order - one by one or all five in a request.
After a write the program offers to change its own connection settings and reconnect.
(The manual does not say whether a change is active immediately or after a restart of the sensor; this tab lets you test it.)

### Tools
* **Raw frame:** send any hex bytes (CRC / MBAP added automatically) and decode the reply.
* **Read / write registers:** read any address (raw / Float32 / UInt32 / ...), repeat at an interval, write with function 16.
* **Register scan:** scan an address range for registers that answer.
* **Auto-detect (serial):** find unknown baud rate / parity / stop bits / ID (reads register 2000 only).
* **Stress / stability test:** N requests in a row; success rate, error kinds, min/avg/median/p95/max response time and value statistics; *Save report* writes `TestReport_*.txt`.
* **Simulator:** a VA 520 look-alike on `127.0.0.1` so you can try the program without a sensor, with fault injection and an "echo every request" option.

### Calculators
* **4..20 mA:** pick the model / range (manual chapter 6), enter the current measured with a multimeter, and get the equivalent flow,
  the deviation from the Modbus value and the accuracy band (+-1.5 % of reading +-0.3 % of full scale).
* **Pulse:** pulse frequency for a given flow, the 50 Hz limit check, number of pulses for a volume / time.

### Log
Errors of this session, *View error log file*, *Write test error* (to check the log works) and the full hex trace of TX / RX frames.

## Error log file

`ErrorLog.txt` is created **next to the exe** (if that folder is not writable: `~/.local/share/VA520Tester/` on Linux,
`%LOCALAPPDATA%\VA520Tester\` on Windows; the exact path is shown in the Log tab). One line per error:

```
No | Code | Text | Time | Date
0001 | E1002 | Flow [m3/h] addr 1100: No response from slave 1 within 1000 ms | 14:32:10.123 | 2026-11-07
0002 | E1003 | CRC error: received 1A2B, calculated 3C4D (...) | 14:32:11.456 | 2026-11-07
```

The number continues across runs; a file larger than 5 MB (configurable) is moved to `ErrorLog.txt.old`.

| Code | Meaning | Code | Meaning |
|---|---|---|---|
| E1001 | cannot open port | E1011 | value outside limits |
| E1002 | timeout (no answer) | E1012 | register write failed |
| E1003 | CRC error | E1013 | unexpected program error |
| E1004 | reply from wrong slave ID | E1014 | settings file error |
| E1005 | wrong function code in reply | E1015 | CSV file error |
| E1006 | short / incomplete frame | E1016 | not connected |
| E1007 | port I/O error | E1018 | manually entered test error |
| E1008 | TCP connection error | E1019 | simulator error |
| E1009 | invalid MBAP header | E1101..E1111 | **exception from the sensor** = 1100 + Modbus code (E1102 = illegal data address) |
| E1010 | invalid value (NaN / Infinity) | E1000 | general (e.g. "N repeated errors suppressed") |

## Registers used (function 03 / 16)

"Register Address" in the manual = wire address (Modbus register number - 1). A Float32 / UInt32 occupies two registers (4 bytes).

| Address | Value | Address | Value |
|---|---|---|---|
| 1100 | Flow m3/h | 1268 | Consumption m3 (before comma, UInt32) |
| 1108 / 1116 / 1124 | Flow Nm3/h, m3/min, Nm3/min | 1274..1310 | Consumption Nm3, ltr, Nltr, cf, Ncf, kg, kWh |
| 1132..1172 | Flow ltr/h, Nltr/h, ltr/min, Nltr/min, ltr/s, Nltr/s | 1346 / 1354 | Velocity m/s, Nm/s |
| 1180 / 1188 | Flow cfm, Ncfm | 1362 / 1370 | Velocity ft/min, Nft/min |
| 1196 / 1204 / 1212 / 1220 | Flow kg/h, kg/min, kg/s, kW | 1418 / 1426 | Gas temperature degC, degF |
| 2000..2004 | Modbus ID, baud rate (0..5), parity (0 none, 1 even, 2 odd), stop bits (0 = 1, 1 = 2), word order (0xABCD / 0xCDAB) - read/write | | |

### Reading a register with a plain serial terminal

Modbus RTU is only a few bytes on the line. Send these in HEX mode (slave ID 1, CRC included), no CR/LF appended:

| Read | Frame |
|---|---|
| ID, baud, parity, stop bits, word order (2000, 5 registers) | `01 03 07 D0 00 05 85 44` |
| Flow m3/h (1100, 2 registers) | `01 03 04 4C 00 02 04 EC` |
| Velocity m/s (1346) | `01 03 05 42 00 02 64 D3` |
| Gas temperature degC (1418) | `01 03 05 8A 00 02 E5 2D` |
| Consumption m3 (1268) | `01 03 04 F4 00 02 84 C9` |

A good answer for Flow looks like `01 03 04 D1 D2 D3 D4 CRC CRC` (D1..D4 = Float32, word order ABCD).
The *Raw frame* tool does the same and adds the CRC for you.

## Suggested way to test a sensor

1. **Connection:** set the port and parameters (the defaults are the factory settings). Unknown? Use Tools > **Auto-detect**.
2. **Connect**, then **Sensor settings** > *Read current values* (confirms the link and the ID).
3. **Monitor** > **Start polling**: flow, velocity, temperature, consumption. With no flow the reading should be about 0
   (otherwise set the zero point as described in the manual, 11.3.1.5).
4. **Stress test** (e.g. 5000 requests): success rate and response times; look at the log for errors. Vary timeout / retries / gap.
5. Switch **Limit checks** on and run a few hours of polling with **CSV log** (soak test).
6. **4..20 mA:** read the current with a multimeter and compare with the Modbus flow in Calculators > 4..20 mA.
7. **Pulse:** for a chosen flow / volume, check the expected pulse frequency and count in Calculators against your own counter.

## Troubleshooting: the sensor does not answer

First switch on **Log > Trace every TX / RX frame**: every request shows `TX ...` and `RX ...` (or `RX (no bytes received)`).

| Symptom | Meaning / what to do |
|---|---|
| Only **timeouts**, `RX (no bytes received)` | The sensor is silent: check the **slave ID** (shown on the sensor display, default 1), the **parity** (factory default is **Even**, not None), baud rate, **A/B swapped**, 24 V supply |
| Still silent, adapter has no automatic direction control | Try **Set DTR high** and **Keep RTS high** (many tools, e.g. QModMaster, assert both lines) or **RTS controls RS485 direction** |
| RX is **identical to TX**, or another program says `Invalid PDU` | The adapter echoes the request. Enable **Converter echoes my request: remove the echo**. If there is still no answer after removing the echo, the sensor is silent (see the first row) |
| `E1003` (CRC) or `E1006` (short frame) with garbled bytes | Wrong baud rate / parity, long cable without termination, adapter switching direction too late |
| `E1102` (illegal data address) | The link works; set **Address offset** to 1 or check the addresses with Register scan |

The simulator (Tools > Simulator) has an "Echo every request first" option to see the echo behaviour without hardware.

## Known limits

* The manual does not describe the sensor's status / error registers (heater error, low voltage, ...); they are only visible on the sensor display,
  so the program does not read them. More values are in a separate vendor document (`VA5xx_Modbus_RTU_Slave_Installation`);
  use **Register scan** and **Custom channels** to discover and watch extra registers.
* The pulse and 4..20 mA outputs are not measured by the program (separate hardware is needed); it only offers comparison calculators.
* Consumption is defined in the manual only as the part "before the comma" (UInt32).
* Direction control of an RS485 transceiver through a Raspberry Pi GPIO pin is not implemented.

## Development

```
src/                   C# sources (WinForms built in code, no designer files)
tests/SelfTest/        console self test: protocol core against the simulator, no hardware
VA520Tester.csproj     net48 WinExe (dotnet build / Visual Studio)
build_windows.cmd      build + self test on Windows
build_on_pi.sh         build with Mono's mcs on the Pi
dist/run.sh            start script for Mono
```

* **Keep the code at C# 5 syntax** (`<LangVersion>5</LangVersion>`): no `?.`, string interpolation, `nameof`, expression-bodied members, etc.
  Mono's `mcs` on the Pi does not support newer syntax. Avoid `Process.Start`, the Chart control and other things that behave differently on Mono.
* Run the self test before sending a change:
  ```
  dotnet build tests/SelfTest/SelfTest.csproj -c Release -o tests/SelfTest/bin
  tests/SelfTest/bin/VA520SelfTest.exe
  ```
* Please include the `ErrorLog.txt` lines and the TX/RX trace when you report a problem with a real sensor.

## License

[MIT](LICENSE).
