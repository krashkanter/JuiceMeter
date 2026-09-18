# Juice Meter

A Windows notification-area applet that measures how many **units** of
electricity your laptop actually pulls from the wall.

One unit is one kilowatt-hour — the thing your electricity bill charges you for.
Juice Meter runs in the tray, samples power once a second, integrates it into
energy, and keeps a plain-CSV ledger you can audit.

Built for a ROG Zephyrus M16 (GU603ZU, i7-12700H + RTX 4050) on Windows 11, but
there is nothing model-specific in it: any Windows 10/11 laptop with a smart
battery will work.

![Juice Meter dashboard](docs/screenshot.png)

The UI follows the Windows light/dark setting, like G-Helper does.

---

## Why this is not just "read a wattage and multiply"

A laptop is an awkward thing to meter, because the accurate sensor and the
number you actually want are only sometimes the same thing.

**On battery** the pack's fuel gauge reports charge and discharge power in
milliwatts, measured across a sense resistor. It is the single most accurate
power figure available anywhere on the machine. Juice Meter reads it directly
through `DeviceIoControl` on the battery device — not WMI, because polling
`ROOT\WMI` once a second spins up `WmiPrvSE` and burns a couple of watts of its
own, which would corrupt the very measurement being taken.

**On AC** nothing measures total draw. There is no sensor between the wall and
the board. So Juice Meter models it:

```
system   = CPU package + GPU package + baseline
wall     = (system + charging / charge_efficiency) / adapter_efficiency
```

CPU and GPU package power come from RAPL and NVML — the same counters
Afterburner and HWiNFO show you. The **baseline** is everything else: panel,
board, RAM, SSD, fans, wifi.

### The baseline calibrates itself

This is the part that makes the AC numbers believable.

While you are on battery, the pack tells us true total system power. Subtract
the CPU and GPU package power we can read directly, and whatever is left over
*is* the baseline. Juice Meter banks that residual every second, then reuses it
on AC.

It is bucketed by screen brightness, because the backlight is a large and
highly variable slice of idle draw — a dim battery session should not teach the
model a baseline that is far too low for a bright desk session. A rolling
**median** is used rather than a mean, so that a burst of SSD writes or a phone
charging off a USB port does not drag the baseline upward permanently.

So the laptop calibrates its own AC estimate every time you unplug it.

### Two ledgers, and the difference matters

| Ledger | What it counts |
| --- | --- |
| **Wall energy** | What the socket delivered. Only accrues on AC, and includes energy poured into the battery plus conversion losses. **This is what you are billed for.** |
| **System energy** | What the internals consumed, from socket or pack alike. |

Running an hour on battery adds nothing to the wall ledger, because that energy
was already paid for while charging. Getting this wrong is the most common way
a "power meter" app ends up double-counting.

### Honest confidence labels

Every sample is tagged, and the tag is shown in the header and stored in the CSV:

| Tag | Meaning |
| --- | --- |
| `MEASURED` | On battery. The pack is reporting the number. |
| `MODELLED` | On AC, with CPU + GPU sensors and a calibrated baseline. |
| `ESTIMATED` | On AC with no CPU sensor or no calibration yet. Rough. |

---

## Administrator rights

Juice Meter runs unelevated and is useful that way — battery-side measurement is
completely unaffected. But:

- **CPU package power (RAPL)** requires a kernel driver, and therefore admin.
- **Integrated GPU power (RAPL, graphics domain)** needs the same driver.
- **Discrete GPU power (NVML)** generally works without it.

Without CPU package power, **calibration is deliberately switched off**. The
leftover after subtracting GPU would still be mostly CPU, which swings by tens
of watts, and banking that would double-count the moment CPU power is added back
in on AC. The app says so in a banner rather than quietly producing worse
numbers.

Run it as administrator and let it sit on battery for a couple of minutes to get
the good numbers. Enabling "Start with Windows" while elevated registers a
scheduled task with `HighestAvailable`, so it comes back elevated at login with
no UAC prompt.

> The CPU sensor path uses LibreHardwareMonitor, which loads a signed ring-0
> driver to read MSRs. Some anti-cheat software objects to this. Turn the
> sensors off in Settings if that is a problem; you keep full battery accuracy.

### Two GPUs

This laptop has two, and both report power: the discrete RTX 4050 over NVML, and
the integrated Iris Xe as the RAPL *graphics* domain. That is the same pair
Afterburner lists as GPU 0 and GPU 1, and Juice Meter shows both — the breakdown
bar gets a separate **iGPU** segment, and the details panel gives its wattage.

The catch is that they are not the same kind of number. The integrated GPU sits
on the CPU die, and Intel reports it as PP1, which the package domain *already
contains*. So:

```
system = CPU package + dGPU + baseline        <- the total
CPU package = CPU cores and uncore + iGPU     <- what the bar splits open
```

Adding the integrated figure to the package would bill that draw twice. Juice
Meter therefore only ever totals **package + discrete**, and uses the integrated
reading to carve the CPU bar into two segments. The split only appears when
running elevated, because the graphics domain needs the same driver the package
domain does; without it the bar falls back to a single CPU segment, which is
still correct, just less detailed.

### Switching GPU modes

Flipping G-Helper between Standard and Eco is safe. Eco does not merely disable
the discrete GPU on these machines, it removes the devnode outright, and NVML
does not survive its device vanishing: the next power read dereferences freed
memory and raises an `AccessViolationException`, which .NET treats as a
corrupted-state exception and cannot catch. The process just dies.

Since there is no way to catch it, the call is never made. Juice Meter asks the
configuration manager whether the GPU devnode is started immediately before
every read, parks the GPU sensor the moment it goes away, and only rebuilds the
sensor stack once the device is back and alive. While the dGPU is off it reports
a real 0 W, which is the truth.

---

## Install

Grab the latest build from [Releases](../../releases), unzip anywhere, run
`JuiceMeter.exe`. No installer, no service.

Or build it:

```powershell
git clone https://github.com/krashkanter/JuiceMeter.git
cd JuiceMeter
.\build.ps1
```

Output lands in `dist\`. `build.ps1 -SelfContained` bundles the runtime so the
exe runs on a machine with no .NET installed.

Requires the .NET 8 SDK to build, and the .NET 8 Desktop Runtime to run (unless
self-contained).

---

## Using it

Closing the window parks it in the tray; quit from the tray menu. The tray icon
draws the live wattage straight into the notification area, with a coloured bar
underneath for the power source (amber = battery, teal = charging, blue = AC).

Right-click the tray icon for today's and this month's units at a glance.

### Command line

| Flag | Effect |
| --- | --- |
| `--tray` | Start hidden in the notification area. |
| `--probe [seconds]` | Console mode: dump every sensor reading and how the model turns it into watts, then exit. |
| `--screenshot [file]` | Render the dashboard to a PNG and exit, for bug reports. Add `--settings` for the settings window, or `--light` / `--dark` to force a theme. The meter it spins up is read-only, so a screenshot never adds watt-hours to your ledger. |

`--probe` is the fastest way to see what your machine actually exposes:

```
> JuiceMeter.exe --probe 6

Battery
  present     : True
  device      : AS3GWYF3KC GA50358 (OTI0)
  full charge : 66.3 Wh
  design      : 90.0 Wh

Hardware sensors
  status      : CPU ok, GPU ok
  cpu         : 12th Gen Intel Core i7-12700H (power yes)
  gpu         : NVIDIA GeForce RTX 4050 Laptop GPU (power yes)
  dgpu devnode: PCI\VEN_10DE&DEV_28A1&SUBSYS_1D931043&REV_A1\4&1a2b3c4d&0&0008
  dgpu running: True

time      source       batt_W    cpu_W   igpu_W    gpu_W   base_W  system_W    wall_W
--------  -----------  -------  -------  -------  -------  -------  --------  --------
00:34:24  battery       -19.46     8.31     2.14     1.20    18.26     19.46      0.00
00:34:27  battery       -25.31    12.04     2.31     1.20    24.11     25.31      0.00
```

`igpu_W` is part of `cpu_W`, so the total is `cpu_W + gpu_W + base_W`. A dash in
either CPU column means the sensor needs administrator rights.

---

## Your data

Everything lives in `%LOCALAPPDATA%\JuiceMeter\`. Drop a file named
`portable.txt` next to the exe to keep it beside the exe instead.

```
settings.json            your settings
state.json               lifetime counters + the learned calibration
history\2026-09.csv      one row per accounted minute
raw\2026-09-18.csv       every raw sample (off by default)
juicemeter.log           what happened
```

The history CSV is the source of truth and is deliberately plain text, because
the whole point of this app is a number you can check:

```csv
local_time,unix_seconds,seconds,source,confidence,wall_wh,system_wh,batt_out_wh,batt_in_wh,avg_system_w,avg_wall_w,avg_cpu_w,avg_igpu_w,avg_gpu_w,avg_base_w,batt_pct
2026-09-18 00:34,1789585440,60.0,Battery,Measured,0,0.394521,0.394521,0,23.67,0,0,0,1.2,22.47,86.4
2026-09-18 00:35,1789585500,60.0,AcCharging,Modelled,1.842357,0.512833,0,1.10412,30.77,110.54,8.31,2.14,1.2,21.26,87.1
```

`avg_igpu_w` is a **slice of** `avg_cpu_w`, not an addition to it — see
[Two GPUs](#two-gpus). The system total is `avg_cpu_w + avg_gpu_w + avg_base_w`.
Files written before that column existed are upgraded in place on first write, so
a month never ends up holding rows of two different widths.

Sleep and hibernate are recorded as **gaps**, not integrated across. If the
sampler is starved or the lid is shut, those seconds are counted as skipped
rather than silently billed at the last known wattage. The dashboard footer
tells you how much time was skipped.

"Reset counters" never deletes anything — it moves the history folder aside to
`archive-<timestamp>\`.

---

## Settings worth knowing

| Setting | Default | Notes |
| --- | --- | --- |
| Sample interval | 1000 ms | Longer intervals use less power themselves. |
| Adapter efficiency | 90% | Wall→DC losses in the brick. 88–92% is typical. |
| Charging efficiency | 92% | DC→cells. Energy lost heating the pack. |
| Price per unit | ₹8.00 | Your tariff, per kWh. |
| Fallback baseline | 12 W | Used only until calibration learns the real number. |
| Manual baseline | off | Pin it yourself and skip learning entirely. |

---

## Where the error comes from

Being straight about this, since the number is the whole product:

- **On battery** — as good as your pack's fuel gauge, which is typically within
  a few percent. Cell ageing affects reported capacity, not reported power.
- **On AC, calibrated** — the baseline is assumed constant for a given
  brightness. Things the package sensors cannot see (a USB device charging, an
  external drive spinning, heavy wifi) land outside the model.
- **Adapter and charging efficiency are assumptions, not measurements.** They
  are flat percentages; real brick efficiency varies with load and is worst at
  very light load. If you own a wall meter, measure yours and put the real
  number in Settings.
- **Not calibrated, no CPU sensor** — treat it as an order-of-magnitude figure.
  The app labels this `ESTIMATED` precisely so you do not trust it by accident.

A wall socket meter is still the ground truth. This is the closest you can get
from inside the machine.

---

## Built with

- .NET 8 + WinForms, hand-drawn dark UI, no designer files
- Battery: Win32 `DeviceIoControl` / `IOCTL_BATTERY_*` via SetupAPI
- CPU + GPU: [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
- Brightness: `WmiMonitorBrightness`

## Licence

MIT — see [LICENSE](LICENSE).
