# WISP6 camera streaming: implementation and change record

This document explains the changes in implementation commit
[`472cfcc`](https://github.com/mkiran-uw/Wisp6-Research/commit/472cfcc4813e844b5493321143c631d41d16dc79):
**Add reliable HM01B0 capture and R420 image streaming**.

**Status:** the firmware compiles and links with the actual TI MSP430 compiler,
the receiver builds, and the software tests pass. Physical camera capture,
R420 image transfer and achievable frame rate have **not** yet been verified.
Here, "streaming" means repeatedly capturing and downloading complete still
images, not a demonstrated high-frame-rate video feed.

For day-to-day build, flash and receiver instructions, see the
[camera operating guide](../HimaxCamera/README.md).

### Follow-up: Dedicated simplecam CCS workspace

The [simplecam project](../simplecam/README.md) packages the same implementation
as a dedicated C/assembly CCS application. It links the camera application,
protocol, driver and interrupt sources from `HimaxCamera`, and links the
`wisp-base` library. Its workspace imports all three firmware projects.
There is no copied/diverging camera implementation or C# code on the MCU.
The existing PC receiver still controls repeated capture and full-frame
download; this addition does not change the wire protocol or introduce
autonomous frame overwrites.

[setup-workspace.ps1](../simplecam/setup-workspace.ps1) imports in place, supports
repeat runs and checks for conflicting project registrations. With `-Build`,
it uses the actual CCS headless builder and verifies the 19,764-byte NOINIT
frame allocation and Timer1_A0 vector owner. The checked-in CCS metadata uses
portable sibling-directory links; workspace metadata and binaries stay local.

## 1. Goal and starting point

The existing RFID demo could read and write a WISP6 tag. Connecting the
daughterboard and HM01B0 camera did not, by itself, configure the sensor or
provide a PC image receiver.

The working application was in [Hello-Wisp6/main.c](../Hello-Wisp6/main.c).
The initial `main` checkout did not contain the camera example or most of the
core WISP library. Those sources existed on
`origin/mkiran_uw/led-callbacks`, revision
`c0dd323b3a257d41824813598194855546fe6f92`.

The original camera example already supplied useful hardware-specific work:

- HM01B0 register settings for 162 x 122, 8-bit grayscale images.
- I2C configuration and external camera clock generation.
- One-bit serial pixel reception through the MSP430 SPI peripheral and DMA.
- Frame-valid synchronization.
- A one-shot image transfer through changing EPC inventory replies.

However, it did not provide a reliable repeated-capture interface. Its image
storage was a hard-coded address rather than a reserved linker allocation;
I2C and capture waits could hang; EPC chunk numbering wrapped; the last copy
could exceed the image length; and the application stopped taking new images
after completing its first transfer.

## 2. Architecture after the changes

The new design separates camera acquisition from RFID transport:

```text
PC receiver
    |
    | LLRP over TCP port 5084
    v
Impinj R420
    |
    | Gen2 Write: capture command
    v
WISP6 application
    |
    | I2C: configure HM01B0 registers
    | External clock + FVLD: synchronize acquisition
    | SPI slave + DMA: receive serial pixels
    v
Reserved FRAM frame buffer
    |
    | Gen2 Write: select chunk
    | Gen2 Read: return a 32-byte User-memory window
    v
PC receiver: validate, reconstruct, check CRC, save image
    |
    +---- request the next frame
```

I2C is used for camera control, **not** for reading the image pixels.
The HM01B0's one-bit serial pixel stream is received using UCA0 in SPI slave
mode; the camera supplies the pixel clock. This is not an SPI-master register
read from the camera.

EPC inventory remains useful for identifying and selecting the WISP.
Image payloads now travel through ordinary Gen2 User-memory Read commands.

## 3. Firmware changes

### 3.1 Restore the required project and library sources

The implementation restored [HimaxCamera](../HimaxCamera/) and the missing
[wisp-base](../wisp-base/) project sources from the existing branch, including
the RFID assembly, initialization, pin definitions and public interfaces.
The already-present platform globals were retained.

This explains why the implementation commit contains many files: a substantial
part is restored platform code, not newly invented camera logic. The original
[license](../license.txt) was also restored.

The existing [Hello-Wisp6 application](../Hello-Wisp6/main.c) was not rewritten
or replaced. Camera functionality is a separate firmware application.

### 3.2 Reserve the frame buffer in FRAM

The MCU has only 2 KB of SRAM, whereas the image is:

```text
162 columns x 122 rows x 1 byte/pixel = 19,764 bytes
```

[main.c](../HimaxCamera/main.c) declares the image in `.camera_frame`.
[The linker command file](../HimaxCamera/lnk_msp430fr5969.cmd) reserves:

| Region | Address range | Purpose |
|---|---|---|
| Lower FRAM | `0x4400..0x6FFF` | Firmware/data placement |
| Camera frame | `0x7000..0xBD33` | Exactly 19,764 image bytes |
| FRAM after camera | `0xBD34..0xFF7F` | Additional firmware/data placement |
| Upper FRAM | Starts at `0x10000` | Existing additional FRAM region |

The frame section is marked **NOINIT in the linker**, avoiding startup
initialization of a large image and preventing other allocations from using
the same memory.

During the actual TI build, compiler 21.6 rejected combining a `DATA_SECTION`
pragma and a `NOINIT` pragma on the same variable. The fix was to keep the
custom data-section pragma and express NOINIT only in the linker file.

### 3.3 Replace unbounded initialization and capture

[HM.c](../HimaxCamera/Camera/HM.c) now implements a single bounded
`camera_capture` operation:

1. Disable the RFID receive interrupt during acquisition.
2. Stop/reset camera peripherals and clear the previous completion flag.
3. Restore MCLK to 16 MHz and SMCLK to 4 MHz after RFID clock changes.
4. Enable the camera, output its clock on PJ.0, and allow startup settling.
5. Configure UCB0 I2C at address `0x24`, approximately 400 kHz.
6. Reset the sensor and verify both model-ID bytes, `01 B0`.
7. Apply the retained resolution, exposure, binning and gated-clock settings.
8. Select normal pixels or the walking-one test pattern.
9. Configure the SPI slave and DMA destination using the actual image pointer.
10. Start streaming, synchronize at falling FVLD, and capture the next frame.
11. Confirm DMA completion before declaring the image ready.
12. Disable the camera, its clock, DMA, camera interrupts and timeout timer.
    Restore the saved RFID receive-enable and interrupt state.

I2C polling uses Timer2_A and reports NACK, arbitration loss and a timeout.
Each polling wait is bounded at approximately 100 ms; the frame wait is bounded
at approximately 2 seconds after streaming starts. These are individual wait
limits, not a single 100 ms deadline for the entire register configuration.

Initialization stops on the first error rather than continuing as if the camera
were configured. The code resets its peripherals on cleanup; it does not
implement an additional manual SCL-pulsing recovery procedure for a physically
stuck I2C bus.

DMA now uses non-repeating single-transfer mode for one complete image.
At the FVLD boundary the SPI receiver is reset for byte alignment before DMA is
enabled. Target-sized DMA address writes use TI's 20-bit-address intrinsic,
removing the pointer-to-smaller-integer warnings found during the target build.

### 3.4 Give each interrupt vector one owner

The camera owns PORT3/FVLD and DMA interrupts. I2C is polled and does not need
a USCI interrupt.

The RFID timeout ISR is exported from
[Timer1A0_ISR.asm](../wisp-base/RFID/Timer1A0_ISR.asm) and connected to `.int41`
by [isr-link.asm](../HimaxCamera/isr-link.asm). Its vector was removed from the
camera's [catch-all ISR](../HimaxCamera/catchall.c).

The actual target link detected the duplicate `.int41` ownership before this
last change. Removing the catch-all assignment resolved the link failure and
left the RFID timeout connected to the intended ISR.

### 3.5 Keep capture outside RFID callbacks

The Write callback in [main.c](../HimaxCamera/main.c) only records the bank,
word pointer and command value. The main loop processes the request after
`WISP_doRFID` returns.

This avoids putting camera initialization, delays or DMA acquisition inside
time-sensitive RFID protocol callbacks.

The application validates that camera commands target User bank 3, word 0.
It prepares the selected read window before the next Read transaction.
The existing RFID Read callback executes **after** transmission, so it is not
the place to prepare data for that same response.

## 4. New capture and transfer protocol

[cam.h](../HimaxCamera/cam.h) defines the constants.
[camera_protocol.h](../HimaxCamera/camera_protocol.h) and
[camera_protocol.c](../HimaxCamera/camera_protocol.c) implement the state,
command handling, checksum and byte serialization independently of MCU registers.
That separation also makes the protocol directly testable on a PC.

### Commands

Use a single-word Gen2 Write to User bank 3, word pointer 0:

| Value | Meaning |
|---|---|
| `0x8000 \| token` | Request a normal image |
| `0x8100 \| token` | Request a walking-one test-pattern image |
| `0x4000` | Select status/metadata |
| `0..988` | Select an image chunk |

The token is an 8-bit request identifier. Repeating the **same full capture
command** returns the current result without starting another capture. This
also applies after a capture error. A new request requires a different token
or mode/token combination.

This matters when the camera accepts a Write but its RFID response is lost:
retrying that Write must not overwrite the frame that the host is downloading.
Only the most recent request is remembered; this is not a permanent command
history or general replay-protection mechanism.

Successful captures increment a 16-bit frame ID. Failed captures do not.
Tokens and frame IDs can wrap.

### Read window and image chunks

Use a separate Gen2 Read of User bank 3, word pointer 0, **exactly 16 words**.
The 32-byte response contains a 12-byte header and up to 20 payload bytes:

| Bytes | Field |
|---|---|
| 0 | Magic `CA` |
| 1 | Protocol version `01` |
| 2 | State: idle, busy, ready or error |
| 3 | Error code |
| 4..5 | Frame ID |
| 6..7 | Chunk index, or `FFFF` for metadata |
| 8..9 | Total image size |
| 10 | Valid payload length |
| 11 | Capture token |
| 12..31 | Payload, with unused bytes zero-filled |

All multibyte fields use big-endian byte order.
Metadata contains width, height, full-image CRC16, chunk size and test-pattern
flag. Image CRC is CCITT-FALSE: polynomial `0x1021`, initial value `0xFFFF`,
no reflection and no final XOR.

The frame contains **989 chunks**: indices 0 through 987 carry 20 bytes each;
index 988 carries only **4 bytes**. The final copy is bounded and the remaining
window bytes are zero-filled.

Reading or re-selecting a chunk never advances the stored image. This lets the
receiver retry, detect stale windows and reconstruct bytes at known offsets,
without relying on the old wrapping EPC sequence number.

During capture the MCU is not servicing RFID. Although a busy state exists
internally, the host should not expect to poll it live during that interval.
Transfer resumes after capture completes or reports an error.

## 5. PC receiver changes

The new [camera-reader project](../camera-reader/CameraReader.csproj) is a .NET 9
console receiver with no external NuGet or proprietary SDK dependency.

| File | Responsibility |
|---|---|
| [Llrp.cs](../camera-reader/Llrp.cs) | R420 TCP connection, LLRP encoding/decoding, ROSpec/AccessSpec management and tag operations |
| [CameraStream.cs](../camera-reader/CameraStream.cs) | Camera-window parsing, request retries, frame reconstruction, identity/length checks and CRC |
| [Program.cs](../camera-reader/Program.cs) | Command-line arguments, repeated capture, cancellation, saving images and error reporting |

The receiver:

- Connects to the R420 using LLRP on TCP 5084.
- Inventories camera tags using a `CA01` mask at EPC bit pointer 32, length 16,
  matching the existing WISP Select implementation.
- Targets the complete 96-bit EPC in each AccessSpec.
- Keeps Write and Read operations in separate AccessSpecs, allowing the firmware
  main loop to prepare the requested window.
- Reads the previous capture token at startup and uses a different one.
- Checks frame ID, token, chunk index, total size and valid payload length.
- Retries failed tag accesses and stale windows up to five times, with logging.
- Rejects malformed reports, camera errors and full-frame checksum mismatches.
- Handles keepalives and fragmented TCP messages.
- Closes the connection on a TCP deadline/cancellation rather than reusing a
  potentially partially consumed message stream.
- Creates/removes its own reader specs instead of resetting the reader or
  deleting another application's specs.
- Saves verified grayscale PGM files and atomically replaces `latest.pgm`.

`--frames 1` is the default. `--frames 0` repeats until Ctrl+C.
The receiver is an image downloader, not a GUI viewer or browser video server.
Continuous operation retains each frame, so disk usage grows.

## 6. Build and repository changes

The camera and core CCS metadata now select **TI MSP430 21.6.0.LTS**, matching
the supplied CCS 12.1 installation. The camera uses a 320-byte stack and no heap.
The obsolete camera `libmath.a` dependency was removed, and the camera Release
configuration was connected to the core library as well.

[build.ps1](../HimaxCamera/build.ps1) supplies the verified command-line build.
It compiles the core and camera directly using MSP430X, the large code model,
restricted data model and EABI; links the executable; then checks the actual
map for the frame allocation and timeout vector.

[.gitignore](../.gitignore) excludes .NET build outputs, generated images and
test-analysis data in addition to the existing embedded build exclusions.
The source and scripts were committed; the generated firmware executable,
object files, map and captured images are not part of the implementation commit.

## 7. Validation performed

| Check | Result | What it establishes |
|---|---|---|
| Full TI 21.6.0.LTS firmware compile and link | Passed | Target compiler accepts the camera/core code and produces an executable |
| Actual linker-map checks | Passed | Frame is at `0x7000` with size `0x4D34`; `.int41` is allocated |
| Native firmware protocol tests | Passed | All 989 chunks, tail bounds/zero padding, request replay, errors, wrap and CRC |
| Receiver tests | 1,997 checks passed, plus error-path and TCP lifecycle tests | C/C# wire agreement, reconstruction, retry behavior, malformed input handling and simulated reader operations |
| Receiver Release build | Passed, zero warnings/errors | PC receiver builds without external packages |
| Unchanged RFID demo compilation against restored core | Passed | Its source remains compatible with the restored platform interface |
| Physical HM01B0 acquisition and R420 transfer | Pending | Requires running the flashed hardware |
| Actual image quality and frame rate | Pending | Requires inspecting images and timing complete transfers |

The native tests in
[camera_protocol_test.c](../tests/firmware/camera_protocol_test.c) emit windows
using the **production C protocol**, not just a separately written mock format.
The [receiver tests](../tests/camera-reader/Program.cs) consume that fixture.
[ScriptedReader.cs](../tests/camera-reader/ScriptedReader.cs) exercises a real
loopback TCP connection, fragmented packets, keepalive acknowledgment and
reader-spec cleanup. [run.ps1](../tests/run.ps1) runs the combined checks and
removes its temporary fixture and native build files.

The TI build still reports an inherited assembler warning about a NOP before
setting GIE in
[WISP_doRFID.asm](../wisp-base/RFID/WISP_doRFID.asm).
It was not introduced or silently treated as a new camera fix.

## 8. Reproduce the build and capture workflow

Run these commands from the repository root on Windows:

```powershell
# Build the target executable and check its map.
.\HimaxCamera\build.ps1

# Run native firmware, C/C# protocol and simulated-reader tests.
.\tests\run.ps1

# Build the PC receiver.
dotnet build camera-reader\CameraReader.csproj --configuration Release
```

Load `HimaxCamera\Debug\HimaxCamera.out` using CCS, run the WISP, and obtain
the camera's complete EPC through your existing inventory program.
Replace the example hostname and EPC below with your actual R420 and tag:

```powershell
# First inspect a test-pattern frame.
dotnet run --project camera-reader\CameraReader.csproj -- speedway-reader.local CA0100000000000000601234 --antenna 1 --frames 1 --test-pattern

# Then request repeated real-scene images.
dotnet run --project camera-reader\CameraReader.csproj -- speedway-reader.local CA0100000000000000601234 --antenna 1 --frames 0 --output camera-frames
```

Close other R420 applications before connecting. Preserve your known working
RF power settings and use a WISP-compatible reader mode; the default mode index
is 0. A camera supply, correct daughterboard wiring and I2C pull-ups still need
electrical verification. A debugger connection alone is not proof of camera
power.

## 9. Remaining limitations and acceptance checks

- Capture and RFID transfer are sequential, not simultaneous. The single frame
  stays stable while it is downloaded.
- The design is intentionally conservative: each frame normally needs 990
  Writes and 990 Reads, before retries and the startup status exchange.
  These transaction counts are not a measured frame-rate claim.
- The reader retries recoverable tag-level failures, but does not automatically
  reconnect after a broken TCP connection or resume a partially saved frame.
- Session metadata lives in RAM. A reset invalidates the advertised frame even
  though FRAM pixels remain; brownout-resumable capture/transfer is not implemented.
- A failed capture returns an error; request a new token after resolving the
  cause rather than replaying the same failed capture command.
- The supplied program writes PGM images. Live GUI display, compression,
  alternate resolutions and double buffering were not added.

To declare the complete physical setup operational:

1. Verify the power rails, pin mapping and camera clock.
2. Flash and run the verified executable.
3. Check the test-pattern image for frame and byte alignment.
4. Capture a real scene and confirm pixel orientation and exposure.
5. Download repeated images through the real R420.
6. Confirm failures are reported explicitly when camera/RF connectivity is lost.
7. Measure complete-frame transfer time and determine whether it meets the
   application's desired update rate.
