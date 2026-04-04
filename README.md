# MAIRA SBT SimHub Plugin

A standalone SimHub plugin that drives the **MAIRA Seat Belt Tensioner (SBT)**
hardware over USB serial using SimHub's own normalised telemetry.

No MAIRA application process, no memory-mapped file, no iRacing SDK required.

---

## What it does

* Discovers the MAIRA SBT device by scanning COM ports and performing the
  firmware handshake (`WHAT ARE YOU?` → `MAIRA SBT`).
* Reads longitudinal / lateral / vertical acceleration and pitch / roll from
  SimHub's standard normalised `GameData` each frame.
* Averages three consecutive frames (~60 Hz source → ~20 Hz output) and
  computes left/right belt target positions using the same gravity-compensation,
  normalisation, soft-limiter, and piecewise angle-mapping logic as MAIRA.
* Sends `SLxxxxRyyyy` serial commands to the firmware; sends calibration
  (`NL`/`AL`/`BL`) and max-speed (`ML`) commands whenever the device connects
  or the user clicks **Apply** in the settings panel.
* Exposes a WPF settings panel inside SimHub for all tuning parameters.

---

## SimHub telemetry properties used

| Property | Type | Meaning |
|---|---|---|
| `data.NewData.AccelerationSurge` | `Nullable<double>` | Body-frame longitudinal acceleration (m/s², +ve = forward). Includes gravity projection. |
| `data.NewData.AccelerationSway` | `Nullable<double>` | Body-frame lateral acceleration (m/s², +ve = leftward). Includes gravity projection. |
| `data.NewData.AccelerationHeave` | `Nullable<double>` | Body-frame vertical acceleration (m/s², +ve = upward). At rest ≈ +9.81 m/s². |
| `data.NewData.OrientationPitch` | `double` | Pitch angle in **degrees** (+ve = nose-up). Confirmed against `IMotionInputData.OrientationPitchDegrees`. |
| `data.NewData.OrientationRoll` | `double` | Roll angle in **degrees** (+ve = left-side-up). Confirmed against `IMotionInputData.OrientationRollDegrees`. |

---

## Requirements

* **SimHub** — tested against SimHub 9.x (`C:\Program Files (x86)\SimHub\`).
* **MAIRA SBT hardware** — Arduino Nano running the `SBT.ino` firmware.
* **Visual Studio 2022** (Community or higher) with the `.NET desktop development`
  workload, which includes the .NET Framework 4.8 targeting pack.

---

## Building

### 1. Set the SIMHUB_INSTALL_PATH environment variable

The project file resolves all SimHub assembly references through this variable.

Open **System Properties → Advanced → Environment Variables** and add a
**User** (or System) variable:

```
Name:  SIMHUB_INSTALL_PATH
Value: C:\Program Files (x86)\SimHub\
```

*(Include the trailing backslash.)*

Restart Visual Studio after adding the variable.

### 2. Open and build

```
File → Open → Solution/Project
  → MairaSbtSimHubPlugin.sln
```

Build in **Debug** or **Release**. The post-build event automatically copies
`Maira.SimHub.SbtPlugin.dll` (and its `.pdb`) into the SimHub install folder
using `XCOPY`.

### 3. Start SimHub

SimHub will discover the plugin on startup. The MAIRA SBT entry appears in the
left-hand settings menu.

---

## Configuration

All settings are available inside SimHub under **MAIRA SBT**:

| Section | Setting | Default | Range |
|---|---|---|---|
| Connection | Enable SBT | ✓ | — |
| Connection | Auto-connect on startup | ✓ | — |
| Calibration | Minimum angle | 60° | 0–90° |
| Calibration | Neutral angle | 90° | min–max |
| Calibration | Maximum angle | 120° | 90–180° |
| Calibration | Max motor speed | 25 | 5–50 |
| Surge | Surge max G | 10 G | 0.1–50 G |
| Surge | Subtract gravity | ✗ | — |
| Surge | Invert | ✗ | — |
| Sway | Sway max G | 10 G | 0.1–50 G |
| Sway | Subtract gravity | ✗ | — |
| Sway | Invert | ✗ | — |
| Heave | Heave max G | 10 G | 0.1–50 G |
| Heave | Subtract gravity | ✓ | — |
| Heave | Invert | ✗ | — |

After changing calibration angles or motor speed, click **Apply Calibration** or
**Apply Motor Speed** to push the new values to the hardware.

---

## Debugging

Set the debug launch target in Visual Studio to:

```
C:\Program Files (x86)\SimHub\SimHubWPF.exe
```

(`User.PluginSdkDemo.csproj.user` already contains this setting as a template;
the equivalent is pre-configured in `Maira.SimHub.SbtPlugin.csproj.user`.)

Log messages are written to SimHub's log file
(`C:\Users\<you>\AppData\Local\SimHub\Logs\`) prefixed with `[MairaSbtPlugin]`
and `[SbtSerialHelper]`.

---

## Axis conventions (mirrors MAIRA exactly)

```
Surge normalised = clamp( -AccelerationSurge / G / SurgeMaxG,  -1, 1 )
  positive  → braking   → both belts tighten
  negative  → throttle  → both belts loosen

Sway normalised  = clamp(  AccelerationSway  / G / SwayMaxG,   -1, 1 )
  positive  → left-hand corner  → right belt tighter, left belt looser

Heave normalised = clamp( -AccelerationHeave / G / HeaveMaxG, -1, 1 )
  positive  → crest / light-G   → both belts tighten

Left  = SoftLimiter( Surge + Heave - Sway )
Right = SoftLimiter( Surge + Heave + Sway )
```
