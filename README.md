# Kainos Digital Modes (FT8 / FT4)

A module for [Kainos](https://github.com/PNWHam-K7JUS/Kainos), the Hermes Lite 2 SDR program: FT8 and FT4 inside Kainos, in the Kainos look, with no virtual audio cables or CAT set-up.

- **Band activity** and **RX frequency** lists. CQs are gold, messages to you are red and your own transmissions are violet. Double-click a decode to answer it.
- **Automatic sequencing** of the standard QSO (Tx1 to Tx6), as WSJT-X does it. A free-text Tx7 is there too.
- An **audio frequency strip**: left-click sets RX, right-click (or Shift-click) sets TX. The panadapter shows RX and TX lines.
- **Band menu**: tunes to the band's FT8 or FT4 frequency and sets DIGU.
- **Log QSO**: when a QSO completes, a window opens with its details filled in. You can check or add to them before logging, or turn the window off to log straight away. Each QSO is added to `kainos-digimodes.adi` (Settings, "Open the log folder").
- **Works with GridTracker, JTAlert, N1MM+, Log4OM, HRD, DXKeeper and others.** The module sends WSJT-X's UDP messages to 127.0.0.1:2237 by default: decodes, status, and logged QSOs (as a QSO message and as ADIF). It also takes their requests: double-click a station in GridTracker to answer it, halt TX, send free text, or replay recent decodes. Set those programs up as you would for WSJT-X.
- **Waterfall** of the 0 to 3000 Hz audio, with the last period's stations labelled along its top. It takes about a third of the pane, so it grows when you drag the pane taller.
- **PSK Reporter** (off by default; Settings): every 5 minutes the stations you decode are reported to pskreporter.info, in the same format WSJT-X uses.
- **Worked before**: each decode shows the station's country, and a tag when it's new: **NEW DXCC**, **NEW BAND** (an entity new on this band), **NEW GRID** or **GRID BAND**. Stations already worked on this band are dimmed. This uses your QSOs here plus, if you add one in Settings, your main log exported as ADIF. Countries come from cty.dat.
- **Filters** on band activity: **CQ only** (calls to you still show) and **Hide worked**.
- **Clock check**: the stations' median DT is shown beside the clock. If it's 0.6 s or more off, you're told to synchronise the PC's clock.
- **ALL.TXT**: every decode and transmission is written to the log folder in WSJT-X's format.
- **Sounds** when someone calls you, and optionally when a new DXCC entity calls CQ.
- **Tx watchdog** and **Halt Tx**. Transmitting also stops if the radio is un-keyed elsewhere.

## Installing

In Kainos: **Setup > Modules**, choose **Digital Modes (FT8 / FT4)** and **Install**. The module is downloaded from this repository's GitHub releases and checked against its SHA-256. Then open it from **Modes > FT8 / FT4** (or the menu bar in Classic layout).

To test a build before it's released, use **Install from file...** with the zip from `build.ps1`.

## Before you transmit

- **Set your callsign and grid square** in Setup > DSP > FreeDV (RADE). The module uses those.
- **Keep the PC's clock right**, to within about a second. Use Windows Time or a tool such as Meinberg NTP.
- Use **DIGU**, with a filter covering roughly 100 to 3000 Hz.
- Set the **transmit level** (the "..." settings) so the ALC barely moves, as for any digital mode.

## How it works

| Part | What it does |
|---|---|
| `Decode/SlotRecorder.cs` | Takes the receiver's demodulated audio from Kainos's digital tap and resamples it to 12 kHz. It keeps the last minute and stamps each sample with its UTC time. |
| `Decode/Jt9Decoder.cs` | When a period ends, writes it to a WAV file and runs WSJT-X's `jt9` on it (`-8` for FT8, `-5` for FT4). This is the same decoder WSJT-X uses. |
| `Ftx/Pack77.cs` | Packs the 77-bit message with WSJT-X's own `ft8code`, so every message type is packed exactly as WSJT-X packs it. |
| `Ftx/FtxEncoder.cs` | Adds the CRC, encodes the LDPC (174,91) code and maps the bits to tones (ported from ft8_lib, MIT). The self-test checks it against `ft8code`'s tones. |
| `Ftx/FtxModulator.cs` | Generates the GFSK audio at Kainos's transmit rate, starting 0.5 s into the period. |
| `Qso/QsoEngine.cs` | Generates the six messages and runs the auto-sequencing. Logging is in `AdifLog.cs`. |
| `DigiEngine.cs` | The period clock and decode schedule, plus timed transmit through Kainos's transmit audio feed. |
| `UI/` | The pane, drawn in the Kainos colours. |

## Building

You need Visual Studio 2022 or later (.NET desktop development), a Kainos build (for `Kainos.ModuleApi.dll`) and WSJT-X (for `jt9.exe`, `ft8code.exe` and their DLLs).

```
.\build.ps1 -Wsjtx C:\WSJT\wsjtx
```

The script builds the module, then runs `tests\Kainos.DigiModes.SelfTest`. The self-test checks:

- the encoder against `ft8code`;
- that FT8 and FT4 survive the full path to `jt9` (tones, GFSK at 48 kHz, noise, the 12 kHz resampler) and decode at the right frequency with DT 0.0;
- the slot recorder's timing, the sequencer, and the output parser.

It then writes `dist\kainos-digimodes-<version>.zip` and `dist\registry-entry.json`. To publish a version:

1. Create a GitHub release `v<version>` with the zip attached.
2. Add the registry entry to `modules.json` in the Kainos repository.

## Licence

GPL version 3 or later (see `LICENSE`). For the third-party parts, see `THIRD-PARTY.md`.
