# Camera streaming instructions

Use these steps to move from **simpleAckDemo** to the HM01B0 camera application
and download repeated images through your Impinj R420.

**Current status:** the target firmware builds and software tests pass.
Physical camera/R420 streaming still needs to be verified on your hardware.
The interface captures and transfers one complete grayscale image at a time;
it is not a guaranteed video-rate feed.

## 1. Select the correct firmware project

Use **[simplecam](../simplecam/README.md)** for the dedicated C/assembly camera
workspace, not simpleAckDemo. The existing **[HimaxCamera](../HimaxCamera/)**
project remains a compatible alternative using the same shared firmware.

| Component | Purpose |
|---|---|
| [simplecam](../simplecam/README.md) | Dedicated C/assembly firmware project to build and load onto the WISP |
| [HimaxCamera](../HimaxCamera/) | Shared camera sources and alternative firmware application |
| [wisp-base](../wisp-base/) | Core library required by the firmware |
| [camera-reader](../camera-reader/) | PC receiver that talks to the R420 |
| [Hello-Wisp6](../Hello-Wisp6/) | Existing RFID/LED application; not the camera firmware |

Loading either camera application **replaces the simpleAckDemo application on the device**.
The old application remains in the repository and can be built/loaded again
if you need to return to the RFID/LED demo.

## 2. Prepare the hardware and PC

- Connect the WISP6 baseboard, HM01B0 daughterboard and camera using the wiring
  intended for your daughterboard revision.
- Connect the MSP430 debugger for programming.
- Verify that both the MCU and camera have stable power and a common ground.
  A connected debugger alone does not prove that the camera power rails are
  enabled or that sufficient current is available.
- Connect an antenna to the R420 port you intend to use, and place the WISP
  where RFID communication is reliable.
- Keep the R420 RF power and antenna configuration that worked with your demo.
- Install CCS with the TI MSP430 compiler and a .NET 9 SDK/runtime for the PC
  receiver. The receiver targets .NET 9 and has no proprietary SDK dependency.

For pin assignments and electrical checks, see
[Hardware checks](../HimaxCamera/README.md#hardware-checks).

Open PowerShell in the repository root:

```powershell
Set-Location 'C:\temp\UW-PhD\Wisp6-Research'
git pull --ff-only
```

If Git reports local changes or a non-fast-forward update, resolve that normally;
do not discard your local work just to follow this guide.

## 3. Build the firmware

Choose either the CCS project build or the command-line build.

### Recommended: Dedicated simplecam workspace

Close CCS if it is using this workspace, then run from the repository root:

```powershell
.\simplecam\setup-workspace.ps1 -Build
```

This imports `wisp-base`, `HimaxCamera` and `simplecam`, builds the actual CCS
project and checks the resulting linker map. Open the printed
`workspaces\simplecam` workspace in CCS and select **simplecam** as the active
project. The program to load is `simplecam\Debug\simplecam.out`.
For path overrides, Release builds or manual import, see the
[simplecam guide](../simplecam/README.md).
This changes the firmware project/workspace, not the PC receiver: use the same
receiver commands in sections 5-9 below.

### Option A: Build with CCS

These two alternatives build the original `HimaxCamera` application.

1. Use **Project > Import CCS Projects** to import
   [wisp-base](../wisp-base/.project) and
   [HimaxCamera](../HimaxCamera/.project) into the same workspace.
2. Confirm both projects use TI MSP430 compiler **21.6.0.LTS**.
3. Build `wisp-base` using its **build** configuration.
4. Build `HimaxCamera` using its **Debug** configuration.
5. Select **HimaxCamera as the active project** before starting its debug session.

The camera project depends on `wisp-base/build/wisp-base.lib`.
Do not select simpleAckDemo when you intend to load camera firmware.
The two existing demo folders use the same CCS project name; avoid importing
both of those demo projects into the same workspace.

### Option B: Build from PowerShell

From the repository root:

```powershell
.\HimaxCamera\build.ps1
```

The script defaults to your CCS installation:

```text
C:\ti\ccs1210\ccs\tools\compiler\ti-cgt-msp430_21.6.0.LTS
```

If CCS is installed elsewhere, supply the actual compiler and device-header
directories:

```powershell
.\HimaxCamera\build.ps1 `
  -CompilerRoot 'C:\ti\ccs1210\ccs\tools\compiler\ti-cgt-msp430_21.6.0.LTS' `
  -DeviceInclude 'C:\ti\ccs1210\ccs\ccs_base\msp430\include'
```

The script compiles the core and camera directly, links the executable and
checks the frame-buffer allocation and RFID timeout vector in the map.

The resulting program is:

```text
HimaxCamera\Debug\HimaxCamera.out
```

**Build before loading.** Generated executables are ignored by Git, so a fresh
clone/pull of the repository does not provide a prebuilt firmware binary.
There is a documented inherited RFID assembler warning about a NOP before
setting GIE; a successful build must still finish linking and checking the map.

## 4. Load and run the camera firmware on the WISP

### Start a camera debug session

1. Select **simplecam** in CCS, or **HimaxCamera** if you used that alternative.
2. Use its MSP430FR5969 target configuration and your existing debugger.
3. Start **Debug** and confirm CCS loads the executable for the project you built.
4. Click **Resume/Run** so the MCU executes the firmware.

### Or use your existing connected debug session

If you already have a connected MSP430 debug session:

1. Halt the target.
2. Select **Run > Load > Load Program**.
3. Choose the newly built `simplecam\Debug\simplecam.out`, or
   `HimaxCamera\Debug\HimaxCamera.out` for the alternative project.
4. Click **Resume/Run**.

Do not leave the target halted at `main` or a breakpoint while trying to
inventory or capture images. Do not deliberately erase INFO calibration/tag-ID
memory as part of switching applications.

At startup, the camera firmware performs one walking-one test-pattern capture
before it begins RFID service. The P1.1 LED blinks twenty times if the configured
DMA transfer completes, or twice if capture fails. RFID inventory is unavailable
during that initial capture, but the firmware continues into RFID service either
way. This checks camera-to-frame-buffer capture, not pixel alignment or transfer
to the PC. The receiver can still request subsequent captures normally.

## 5. Find the camera EPC and check the reader connection

Use your existing R420 inventory application to read the camera's EPC.
Use inventory only at this stage; do not send the old demo's LED/EPC Write
commands to the camera firmware.

The camera EPC:

- Begins with **`CA01`**.
- Contains **24 hexadecimal characters** in total.
- Ends with the device's INFO-memory tag ID.

Use the full EPC, not just `CA01` and not the previous simpleAckDemo EPC.
If you still see the old demo EPC, verify which executable was loaded and that
the target is running.

After obtaining the EPC, close the inventory application and other R420 host
programs before starting the receiver.

Set these PowerShell variables to your actual values:

```powershell
$reader = 'YOUR_R420_ADDRESS'
$epc = 'YOUR_FULL_24_CHARACTER_CAMERA_EPC'
$antenna = 1
```

The reader can be an IP address or hostname. Replace both placeholder strings
above; they are not usable connection values. Set the antenna to the connected
R420 port, from 1 through 4.

Check LLRP reachability:

```powershell
Test-NetConnection -ComputerName $reader -Port 5084
```

`TcpTestSucceeded` should be `True`. This checks the TCP service, not camera
power, RFID reception or successful image capture.

## 6. Capture one test-pattern image first

From the same repository-root PowerShell session:

```powershell
dotnet run --project camera-reader\CameraReader.csproj -- `
  $reader $epc --antenna $antenna --frames 1 --test-pattern --output camera-frames
```

This requests the HM01B0 walking-one test pattern, downloads all image chunks,
checks frame identity and CRC, and saves a PGM image.

On success, the receiver reports a frame with:

- Dimensions **162 x 122**.
- **19,764 pixel bytes**.
- Complete capture/transfer time in seconds.
- The saved file path.

Inspect the image using a viewer that supports binary grayscale PGM files.
Successful CRC verification establishes transfer integrity, but you must still
inspect the pattern to confirm camera pixel/frame alignment.

Expected output files under the selected output directory:

```text
frame-<timestamp>-<frame-id>.pgm
latest.pgm
```

The timestamp/frame-ID filename preserves that frame. `latest.pgm` is replaced
with the latest verified image.

## 7. Capture a real scene

Remove `--test-pattern`:

```powershell
dotnet run --project camera-reader\CameraReader.csproj -- `
  $reader $epc --antenna $antenna --frames 1 --output camera-frames
```

Inspect the image for scene content, orientation, exposure and alignment before
starting a long continuous run.

## 8. Start continuous capture

Use **`--frames 0`**:

```powershell
dotnet run --project camera-reader\CameraReader.csproj -- `
  $reader $epc --antenna $antenna --frames 0 --output camera-frames
```

The receiver repeats:

```text
Request capture
  -> acquire a complete frame into FRAM
  -> request and read all 989 chunks
  -> validate identity, lengths and full-frame CRC
  -> save the verified image
  -> request the next frame
```

Important operating details:

- The WISP temporarily stops servicing RFID during camera acquisition.
- Capture and transfer are sequential; there is no double buffering.
- Each frame normally requires 990 Writes and 990 Reads, before retries.
- The console reports actual seconds per frame; use that result to assess
  whether the update rate meets your needs.
- All frames are retained. Monitor disk space during long runs.
- The receiver saves images; it does not automatically display a live video
  window.

To capture a fixed number of images instead:

```powershell
dotnet run --project camera-reader\CameraReader.csproj -- `
  $reader $epc --antenna $antenna --frames 10 --output camera-frames
```

## 9. Stop or restart

Press **Ctrl+C** in the receiver terminal to stop continuous capture.
Normally completed operations remove their AccessSpecs, and shutdown attempts
to stop/remove the application's own ROSpec.

A cancellation or network deadline can interrupt a TCP message. In that case
the connection is closed rather than reused; abandoned reader specs can require
cleanup. Do not delete another application's specs or restart a shared reader
without coordinating with its users.

To restart, make sure the WISP is running and launch the receiver again.
The receiver reads the previous capture token and requests a different one.
It starts a new capture, rather than resuming a partial image download.

## 10. Troubleshooting

| Symptom | First checks |
|---|---|
| Camera EPC does not begin `CA01` | Load HimaxCamera, not simpleAckDemo; resume the target; check RF power and antenna |
| TCP 5084 cannot be reached | Reader hostname/IP, network, firewall and other reader applications |
| I2C NACK, error 1 | Camera rail/enable, I2C address, wiring and pull-ups |
| I2C timeout, error 2 | Stuck bus, wiring, pull-ups and camera clock |
| Model-ID error 4 | Camera identity and I2C communication; expected ID is `01 B0` |
| Frame timeout, error 5 | External camera clock, FVLD, pixel clock and serial pixel input |
| Tag-access timeout | Full EPC, correct antenna, WISP power, target running and compatible reader mode |
| Checksum failure or malformed image | Resolve transfer/capture problems; the receiver does not save a checksum-failed frame |
| Duplicate ROSpec error after interrupted run | Clean up only the abandoned camera ROSpec `0xCA010001` and associated AccessSpecs |
| Images arrive slowly | Measure reported seconds per frame; the RFID protocol prioritizes explicit chunk retries over video-rate throughput |

The receiver defaults to reader mode index 0. Use `--mode-index N` only with a
known working WISP-compatible setting; arbitrary Miller/dense modes cannot
replace the firmware's FM0 configuration.

For the complete error table, pin mapping and protocol details, see the
[camera operating guide](../HimaxCamera/README.md). For the explanation of what
changed and why, see the
[implementation record](camera-streaming-implementation.md).

## 11. Optional software checks

These checks do not require the camera or R420:

```powershell
dotnet build camera-reader\CameraReader.csproj --configuration Release
.\tests\run.ps1
```

The test runner requires MSVC C++ build tools in addition to .NET.
It checks native firmware serialization, all image chunks, cross-language
reconstruction, retry/error paths and a simulated TCP reader lifecycle.
Passing those checks does not replace the test-pattern, real-scene and continuous
capture checks on your actual hardware.
