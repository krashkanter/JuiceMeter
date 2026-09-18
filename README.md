# Juice Meter

Tray applet that measures how many **units** (kWh) of electricity your laptop
pulls from the wall — the thing your bill actually charges you for.

![Juice Meter dashboard](docs/screenshot.png)

## What it does

- Samples power once a second and integrates it into energy
- Tracks today / this month / lifetime, in units and in money
- Live wattage drawn into the tray icon
- Plain-CSV ledger you can open in a spreadsheet and check

## Why it is not just "read a wattage"

Nothing on a laptop measures wall draw directly, so it uses two sources:

| | Source | Accuracy |
| --- | --- | --- |
| **On battery** | The pack's fuel gauge, over `IOCTL_BATTERY_*` | Measured. The best sensor on the machine. |
| **On AC** | CPU package + GPU + a learned baseline, then adapter losses | Modelled. |

The baseline — panel, board, RAM, SSD, fans — is learned while you are on
battery, bucketed by screen brightness, and replayed on AC. Every reading is
labelled `MEASURED`, `MODELLED` or `ESTIMATED` so you know which you are looking
at.

## Requirements

- Windows 10 or 11, x64
- A laptop with a smart battery (any modern one)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) —
  or use the self-contained build, which needs nothing
- **Administrator** for CPU package power. Without it, battery measurement is
  unaffected but AC figures stay rough estimates and the baseline cannot learn.

Discrete GPU power needs NVIDIA (NVML). Switching between Standard and Eco in
G-Helper is safe.

## Install

Download from [Releases](../../releases), unzip, run `JuiceMeter.exe`. No
installer, no service.

Or build it:

```powershell
git clone https://github.com/krashkanter/JuiceMeter.git
cd JuiceMeter
.\build.ps1
```

Output lands in `dist\`. Add `-SelfContained` to bundle the runtime. Building
needs the .NET 8 SDK.

## Using it

Closing the window parks it in the tray; quit from the tray menu. The tray icon
shows live watts, with a coloured bar for the source (amber = battery, teal =
charging, blue = AC). Right-click it for today's and this month's units.

| Flag | Effect |
| --- | --- |
| `--tray` | Start hidden in the tray. |
| `--probe [seconds]` | Dump every sensor and how the model turns it into watts, then exit. |
| `--screenshot [file]` | Render the dashboard to a PNG. `--settings`, `--light`, `--dark` also accepted. |

## Your data

`%LOCALAPPDATA%\JuiceMeter\` — one CSV per month, one row per minute, plus
`state.json` for lifetime totals.

Sleep and hibernate are recorded as **gaps**, not integrated across. "Reset
counters" never deletes anything; it moves the history folder aside.

## Accuracy

Adapter (90%) and charging (92%) efficiency are assumptions, not measurements —
change them in Settings if you own a wall meter. Anything the package sensors
cannot see (USB charging, an external drive) lands outside the model. A wall
socket meter is still ground truth; this is the closest you get from inside the
machine.

## Built with

.NET 8 + WinForms · [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
for CPU/GPU · Win32 `DeviceIoControl` for the battery

## Licence

MIT — see [LICENSE](LICENSE).
