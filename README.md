# bmw-mac-diag

**BMW diagnostics on macOS with a cheap K+DCAN USB cable — no Windows, no virtual machine.**

Double-click, pick a number from the menu: read and clear fault memory of every control unit, watch live engine data
with graphs, get a drive report in the browser. Built on [EdiabasLib](https://github.com/uholeschak/ediabaslib)
(the open-source EDIABAS implementation by Ulrich Holeschak).

[Русская версия](README.ru.md)

> **Status: early.** Tested on one car — BMW E93 320d 2010 (N47 diesel), Apple Silicon Mac,
> a clone FT232RL K+DCAN cable. It should work for other E-series BMWs (E90–E93 etc.) with D-CAN or K-line,
> but that is not verified yet. **F-series and newer: not yet** — they need an ENET cable; support can be added
> if someone with an F-series car can test it. Reports and profiles for other cars are welcome.

## What it does

- **Fault memory** of all control units: one scan shows every unit with its number of faults; open a unit to see its faults.
- **Clearing faults** of a chosen unit — it first reads the faults and saves them to a file, then asks for confirmation.
- **Live engine data** at ~8 updates per second, with a 30-second sparkline next to every value, recorded to CSV.
- **Live graphs** (key G): target vs actual for boost and air mass, temperature before the DPF — see at a glance whether
  the turbo and the EGR follow what the engine unit asks for.
- **Drive report** in the browser from a recording: synchronized charts, values on hover, zoom, summary, table.
  Works offline — easy to send to a mechanic.
- **Recognises the car by itself**: asks the engine unit which variant it is and picks or creates a live profile for it.
- English and Russian interface (follows the macOS language).

## What you need

- A Mac (Apple Silicon tested; Intel should work, untested).
- A **K+DCAN USB cable** with an FTDI FT232R chip (the common cheap one with a switch; clones work).
  macOS has a built-in driver — nothing to install. The cable shows up as `/dev/cu.usbserial-XXXXXXXX`.
- **.NET 10 SDK**.
- **SGBD files** (`*.PRG`, `*.GRP`) — the descriptions of the control units. They are BMW property and are
  **not included**; you have to get them yourself (they come with BMW diagnostic software packages).
  See [SGBD files](#sgbd-files).

## Install

```sh
# 1. .NET 10 SDK — either (asks for your admin password):
brew install --cask dotnet-sdk
#    or per-user, no admin rights:
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0

# 2. get it
git clone https://github.com/sakharniy/bmw-mac-diag.git
```

That's it — the first start builds everything (it downloads the official EdiabasLib release once, ~90 MB,
and checks its checksum). To build by hand: `./build.sh`.

## Quick start

1. Plug the cable into the Mac and into the car, switch the **ignition on**.
2. Double-click **`BMW Diag.command`** in the `bmw-mac-diag` folder. Terminal opens.
   (Tip: drag the file into the Dock to start it with one click.)
3. **First start** checks everything by itself:
   - **SGBD files** — if there are none, it says so and where to put them; you can also drag your folder into the window;
   - **key files** — `T_GRTB.PRG` (needed to recognise the units), `D_MOTOR.GRP`, the unit files of the scan list;
   - **the car** — asks the engine unit which variant it is (e.g. `D72N47B0`) and picks the matching profile from
     `profiles/`, or **creates one** from the unit's own table of values (engine speed, temperatures, boost, air mass,
     EGR, DPF, voltage… plus the fuel level from the instrument cluster if it answers).
     Auto profiles are saved in `~/.config/bmw-mac-diag/profiles/`. No car at hand? Detect later in Settings.
4. Then you get the menu:

```
  1  Live engine data (screen + CSV log)
  2  Engine faults
  3  Scan all control units
  4  Identify engine control unit
  5  Clear faults of a control unit (choose from a list)
  6  Settings (SGBD folder, car profile, language)
  7  Report of a recording (opens in the browser)
  0  Quit
```

- **1 — live data.** Keys: **G** numbers ⇄ graphs, **Q** or Ctrl+C back to the menu. Everything is recorded to `logs/live-*.csv`.
- **3 — scan.** Units are numbered with their fault counts; type a number to see that unit's faults, then `c` to clear them.
- **5 — clear.** Choose a unit (the list shows the counts from the last scan). Before clearing, the faults with details
  are saved to `logs/faults-before-clear-*.txt`; clearing needs `yes` (or `да`). Afterwards the unit is read again:
  faults that are active right now come back immediately.
- **6 — settings.** Four steps; each shows the current value, **Enter keeps it**.
- **7 — report.** Pick a recording (Enter = the latest), the report opens in the browser.

**Language:** follows the macOS system language; change it in Settings, with `--lang en|ru`, or `BMWDIAG_LANG`.
Fault texts come from the SGBD files themselves (German/English) and are not translated.

## The cable

1. `./bmwdiag ports` (or the menu header) should show `/dev/cu.usbserial-…`.
2. The switch on the cable selects the protocol: **D-CAN for cars built after ~03/2008**, K-line (pins 7+8) for older ones.
   If you get `IFH-0003`, flip the switch first — it is the most common cause and not a fault.
3. Only one program can use the cable at a time: close the live view before running something else.
4. Unplug the cable when you are done: it draws power from the car.

Windows guides tell you to set the cable's FTDI latency timer to 1 ms — on macOS this is not needed.

## SGBD files

Every control unit needs its SGBD file. Copy them flat into `ecu/`, or choose your folder in Settings.

Group files (`D_MOTOR.GRP`, `D_EGS.GRP`, …) ask the unit who it is and pick the right variant — this needs a
**recent `T_GRTB.PRG`** (the assignment table). If a group file says *No variant found*, your `T_GRTB.PRG` is too old;
you can still call a variant `.PRG` directly.

**Use the right variant.** An old or wrong variant often still reads faults (that part is standard) but gives
garbage or empty live values. Example from the test car: `D71N47A0.PRG` read faults fine, but every live value was
empty ("Too less data") — the unit is really a `D72N47B0`, which `T_GRTB.PRG` maps from the unit's
`ID_VAR_INDEX` / `ID_DIAG_INDEX`.

## Command line

Everything in the menu is also a command (`./bmwdiag --help` for the full list):

```sh
./bmwdiag                             # the menu
./bmwdiag setup                       # settings
./bmwdiag detect                      # recognise the engine, pick or create its live profile
./bmwdiag ident  D_MOTOR.GRP          # which unit / variant is this?
./bmwdiag faults D_MOTOR.GRP          # read fault memory
./bmwdiag clear  D_MOTOR.GRP          # clear fault memory (saves the faults first, asks)
./bmwdiag scan                        # faults of all units listed in scan/e9x.json
./bmwdiag live   [--graphs]           # live data with the profile from the settings
./bmwdiag report [file.csv]           # HTML report (default: the latest recording)
./bmwdiag jobs   KOMB87.PRG           # what can this unit do?
./bmwdiag job    KOMB87.PRG STATUS_TANKINHALT              # run any job, print all results
./bmwdiag job    D72N47B0.PRG _ARGUMENTS FS_LESEN_DETAIL   # which arguments does a job take?
./bmwdiag job    D72N47B0.PRG FS_LESEN_DETAIL 0x4B81       # job with arguments ("a;b;c", or "hex:01 02")
./bmwdiag watch  D72N47B0.PRG STATUS_UBATT --seconds 30    # repeat a job, CSV of all numeric results
./bmwdiag raw    D72N47B0.PRG "22 40 22"                   # raw KWP2000 request (read services only)
```

Options: `--port <dev>`, `--ecu <dir>`, `--log <file>` / `--no-log`, `--graphs`, `--lang en|ru`,
`--trace` (EDIABAS communication trace into `trace/`). Settings live in `~/.config/bmw-mac-diag/config.json`:

```json
{ "ecuPath": "~/bmw/ecu", "port": "auto", "profile": "~/bmw-mac-diag/profiles/n47-dde721.json", "language": "auto" }
```

## Profiles

A profile (JSON) says which SGBD to use and which values to show: labels (with optional `*_ru` Russian labels),
units, scaling, groups, graphs. [`profiles/n47-dde721.json`](profiles/n47-dde721.json) is a complete example for
the N47D20C diesel. Usually you don't need to write one — `detect` creates it. To make or tune one by hand:

1. `./bmwdiag jobs <SGBD>` — look for `STATUS_MESSWERTBLOCK_LESEN` (BMW DME/DDE engine units) or single `STATUS_…` jobs.
2. `./bmwdiag job <SGBD> _TABLE MesswerteTab` — the table of values: column `ARG` goes into `"arg"`, `RESULTNAME` into `"result"`.
3. Test: `./bmwdiag job <SGBD> STATUS_MESSWERTBLOCK_LESEN "JA;INMOT;IUBAT"`.
4. Values that need their own job use `"job"` instead of `"arg"`; values from other units go into `"extras"`.
5. Graphs: `"charts": [{ "title": "Boost", "series": ["SPLAD", "IPLAD"], "minSpan": 0.3 }]` —
   up to two values with the same unit per graph; without `"charts"` the usual target/actual pairs are used.

The scan list [`scan/e9x.json`](scan/e9x.json) (E90–E93) is a plain list of `name` / `sgbd`; make your own for other models.

## Troubleshooting

| Message | Meaning |
|---|---|
| `IFH-0018` | cannot open the serial port — cable not plugged into the Mac, or another program (e.g. an open live view) uses it |
| `IFH-0003` | the cable does not answer — ignition off, or **flip the D-CAN/K-line switch** |
| `SYS-0010` | the car does not answer — ignition on? cable seated? try once more (the first try sometimes fails) |
| `IFH-0009` | this unit does not answer — wrong SGBD, unit not fitted, or it only answers with the engine running |
| `No variant found` | group file does not know the unit — newer `T_GRTB.PRG` needed, or use the `.PRG` directly |
| live values empty / garbage, `Too less data` | wrong SGBD variant for this unit — see [SGBD files](#sgbd-files) |

## Limitations and safety

- Only tested on one car (E93, D-CAN). F-series and newer are not supported yet: they are diagnosed over ENET
  (Ethernet through the same OBD socket, cable with an RJ45 plug). EdiabasLib can do ENET, so support can be added
  if someone with an F-series car and an ENET cable can test it.
- Everything is read-only except clearing faults. `raw` only allows read services.
- Be careful with unfamiliar jobs on safety units: while trying different SGBDs and status jobs on the DSC (ABS) unit,
  the ABS warning light came on for a moment on the test car (nothing was stored). Plain reading while driving was fine.
- Do not read the screen while driving. Let a passenger watch it, or just record and look at the report later.

## License

GPL-3.0-or-later (see [LICENSE](LICENSE)), because it uses EdiabasLib, which is GPL-3.0.
The build downloads EdiabasLib and its dependencies (BouncyCastle — MIT-style, Newtonsoft.Json — MIT,
InTheHand.Bluetooth — Apache-2.0, System.IO.Ports — MIT) from the official EdiabasLib release; they are not stored here.

Not affiliated with BMW AG. BMW SGBD files are not part of this project.
