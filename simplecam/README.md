# simplecam: C/assembly continuous-camera workspace

`simplecam` is an MSP430FR5969 Code Composer Studio firmware project, like
simpleAckDemo. It contains **C and MSP430 assembly**, not C# firmware.
It uses the existing HM01B0 capture and reliable RFID transfer implementation
through CCS linked resources rather than a second copy of that implementation.

Continuous operation is **reader-controlled**:

```text
PC requests capture -> HM01B0 frame captured into WISP FRAM
  -> R420 reads all chunks -> PC checks CRC and saves the image
  -> PC requests the next capture -> repeat
```

This avoids overwriting an image before the reader finishes downloading it.
It is repeated complete-frame acquisition, not simultaneous capture/transmit
or a guaranteed video frame rate. Physical camera/R420 operation still requires
verification with your wiring, power supply and RF settings.

## Projects and sources

The workspace imports these three projects without copying their directories:

| Project | Role |
|---|---|
| [simplecam](.project) | Application to select, build and load onto the WISP |
| [wisp-base](../wisp-base/.project) | Shared RFID, board, timer and math C/assembly library |
| [HimaxCamera](../HimaxCamera/.project) | Original camera application and shared driver/protocol sources |

The linked files visible in the `simplecam` CCS project include:

- [main.c](../HimaxCamera/main.c): application loop and capture requests.
- [camera_protocol.c](../HimaxCamera/camera_protocol.c) and
  [camera_protocol.h](../HimaxCamera/camera_protocol.h): frame/chunk protocol.
- [Camera](../HimaxCamera/Camera/): HM01B0 registers, I2C setup and DMA capture.
- [isr-link.asm](../HimaxCamera/isr-link.asm) and
  [catchall.c](../HimaxCamera/catchall.c): interrupt-vector setup.
- [cam.h](../HimaxCamera/cam.h) and
  [lnk_msp430fr5969.cmd](../HimaxCamera/lnk_msp430fr5969.cmd): image dimensions
  and reserved FRAM region.

Editing a linked file changes the shared implementation used by both camera
projects. `HimaxCamera` is a project reference, not a library linked into
`simplecam`: only `wisp-base/build/wisp-base.lib` is linked into the executable.
The projects produce separate executables; do not load both applications.

## Create/open the workspace and build

Close CCS windows using this workspace before running the headless setup/build.
From the repository root in PowerShell:

```powershell
.\simplecam\setup-workspace.ps1 -Build
```

The script defaults to CCS under `C:\ti\ccs1210\ccs` and creates/imports into
`workspaces\simplecam`. It can be rerun; it verifies the locations of existing
project registrations and refuses conflicting projects from another checkout.
It builds with the actual CCS project builder, not a substitute compiler-only
build, and checks the frame-buffer allocation and timeout vector in the map.
Workspace metadata and generated firmware are not committed.

Override paths or select Release if needed:

```powershell
.\simplecam\setup-workspace.ps1 `
  -CcsRoot 'C:\ti\ccs1210\ccs' `
  -Workspace 'C:\temp\UW-PhD\Wisp6-Research\workspaces\simplecam' `
  -Configuration Release -Build
```

Omit `-Build` to import only. Then open CCS using the printed workspace path
(**File > Switch Workspace > Other** if CCS is already open).

For manual setup, use **Project > Import CCS Projects**, browse to the repository
root and select `wisp-base`, `HimaxCamera` and `simplecam`. Import the existing
projects in place, without copying them. Do not also import both demo projects
named simpleAckDemo. Build `wisp-base` in its **build** configuration, then select
and build `simplecam` in **Debug**.

The checked-in [.project](.project), [.cproject](.cproject) and
[.ccsproject](.ccsproject) are the normal import path.
[simplecam.projectspec](simplecam.projectspec) records the CCS scaffolding
recipe; do not import it on top of the already imported `simplecam` project.
Keep `simplecam`, `HimaxCamera` and `wisp-base` as sibling directories so the
portable linked-resource paths resolve correctly.

## Load the firmware

1. Select **simplecam** as the active project in CCS.
2. Connect your MSP430 debugger and verify stable MCU and camera power.
3. Start Debug using the MSP430FR5969 target configuration.
4. Load the newly built `simplecam\Debug\simplecam.out` (or the Release output
   if that is the configuration you built).
5. Click **Resume/Run**. A target halted at a breakpoint cannot service RFID.
6. Inventory the device with the R420 and record its full 24-digit EPC,
   beginning `CA01`.

The frame buffer occupies exactly 19,764 NOINIT bytes at `0x7000..0xBD33`.
The firmware uses EABI, a large code model, restricted data model, a 320-byte
stack and no heap. All projects select TI MSP430 compiler **21.6.0.LTS**.

Validation completed: a fresh CCS workspace import, Debug and Release target
builds, frame/vector map checks, all 989 chunks in the native C protocol tests,
and 1,997 checks in the existing PC receiver suite. The inherited RFID
assembler warning about a NOP before setting GIE remains documented; it does
not prevent linking. No physical device capture has been verified yet.

## Receive repeated frames on the PC

The existing [camera-reader](../camera-reader/) is unchanged and runs on the
PC, not on the WISP. It remains C#/.NET 9 by your selected scope.
The C/assembly firmware cannot create PC image files or control the R420 by
itself; the receiver drives the next-capture requests.

First follow the [camera streaming instructions](../docs/camera-streaming-instructions.md)
to check wiring, inventory, test-pattern capture and one real scene.
For continuous capture, substitute your reader address and actual camera EPC:

```powershell
$reader = '192.168.1.100'
$epc = 'REPLACE_WITH_YOUR_24_HEX_DIGIT_CAMERA_EPC'
dotnet run --project camera-reader\CameraReader.csproj -- `
  $reader $epc --antenna 1 --frames 0 --output camera-frames
```

`--frames 0` requests frames until **Ctrl+C** or an error. The receiver saves
per-frame PGM files and updates `latest.pgm`. Monitor disk space during long
runs. There is no automatic reconnect, live video viewer or brownout recovery.
See the [implementation record](../docs/camera-streaming-implementation.md)
for protocol details and hardware-validation limitations.
