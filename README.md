# NovaSpice Studio

A modern Windows front-end for **LTspice**, built with C#, WinUI 3 and Win2D.
LTspice stays the simulation engine (it is run in batch mode, `-b`); NovaSpice replaces the
day-to-day experience around it: opening circuits, running, and above all reading the results.

## Features

- **Fluent UI**: custom title bar, command palette (`Ctrl+K`), dark and light themes, toast notifications, drag & drop.
- **GPU waveform viewer** (Win2D): stacked panes with a shared X axis, min/max decimation for large files,
  rectangle zoom, wheel zoom (`Ctrl` = Y, `Shift` = pan), right-drag pan, double-click to fit.
- **A/B cursors** with Δ and 1/Δ, plus a live table of value at A and B, min, max, average, RMS and peak-to-peak
  (time-weighted, so LTspice's variable time step does not skew the results).
- **Hover readout**: a tooltip with every trace's value under the mouse.
- **AC analysis**: automatic magnitude (dB) and unwrapped-phase layout on a log frequency axis.
- **.step support**: each step is drawn in a related colour and labelled from the `.log` file (`C = 10n`).
- **Expressions**: `V(out)*I(L1)`, `V(a,b)`, `db()`, `ph()`, `deriv()`, `integ()`, SPICE suffixes (`1k`, `4.7u`).
- **FFT**: Hann-windowed spectrum of any signal or of the A–B window, with fundamental, DC and THD.
- **Schematic preview** of `.asc` files; double-click a wire, net label or part to plot its voltage or current.
- **Measurements**: `.meas` results (simple and stepped tables) with errors and warnings from the log.
- Export plots to PNG (2×) and data to CSV.
- Reads binary and ASCII `.raw` files, real and complex data, `double` and `fastaccess` layouts, UTF-16 and ASCII headers.

Without LTspice installed, the app still opens `.raw` files and includes two demos (a buck converter and
a stepped RC filter) generated in LTspice's file format.

## Build and run

Requirements: Windows 10 (1809+) or Windows 11, and the [.NET 9 SDK](https://dotnet.microsoft.com/download).

```powershell
cd NovaSpice
dotnet build -c Release
.\bin\x64\Release\net9.0-windows10.0.19041.0\win-x64\NovaSpice.exe
```

The Windows App SDK is bundled (self-contained), so no separate runtime install is needed.
LTspice is found automatically in its usual install folders, or set its path in **Settings**.

## Project layout

| Folder | Contents |
| --- | --- |
| `NovaSpice/Core` | Readers for `.raw`, `.log` and `.asc`, the expression evaluator, DSP (FFT, statistics), the LTspice runner and the demo generator. No UI code. |
| `NovaSpice/Controls` | Win2D controls: `WaveformCanvas` (plotting) and `SchematicCanvas`. |
| `NovaSpice/Views` | WinUI pages: Home, Circuit, Waveforms, Measurements, Settings. |

NovaSpice is independent and is not affiliated with Analog Devices. LTspice is a trademark of Analog Devices, Inc.
