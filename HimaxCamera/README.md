# HM01B0 capture and R420 image transfer

This project replaces the original one-shot EPC image demo with reader-requested,
repeatable capture and a retryable User-memory transfer window. It does **not**
turn the RFID link into high-frame-rate video. The host downloads a complete
image before requesting another.

The camera and core platform sources were restored from this repository's
`origin/mkiran_uw/led-callbacks` revision
`c0dd323b3a257d41824813598194855546fe6f92`. See [the license](../license.txt).
The existing [Hello-Wisp6 demo](../Hello-Wisp6/main.c) is unchanged.

## Build and program

1. Import both [wisp-base](../wisp-base/.project) and
   [HimaxCamera](.project) into the same Code Composer Studio workspace.
   Do not import both existing projects named `simpleAckDemo` into that workspace.
2. Both projects select TI MSP430 compiler **21.6.0.LTS**, matching your CCS
   installation. Keep MSP430X, EABI and the restricted data model consistent
   between the library and application. The camera uses a 320-byte stack and
   no dynamic allocation.
3. Build `wisp-base` using its `build` configuration, then `HimaxCamera` using
   `Debug`. The camera project links `wisp-base/build/wisp-base.lib`.
   The inherited GNU Makefile is not the camera/CCS build procedure.
   Alternatively, the verified command-line build compiles the core and camera
   directly without requiring a CCS workspace:

   ```powershell
   .\HimaxCamera\build.ps1
   ```

   Its defaults point to your `C:\ti\ccs1210` installation. Override
   `-CompilerRoot` and `-DeviceInclude` when using another installation.
   The executable and checked map are written to `HimaxCamera\Debug`.
4. Check the linker map: `.camera_frame` must occupy exactly 19,764 bytes at
   `0x7000..0xBD33`. No code or other data may overlap that region.
   The camera linker file reserves it explicitly as NOINIT, not a RAM buffer.
5. Load the camera executable with your MSP430 debugger and run it. The stable
   EPC begins `CA01`, ends with the tag's INFO-memory ID, and does not change
   for each image packet. Read the **complete 24-hex-digit EPC** using your
   existing R420 inventory program.

This project supplies the missing Timer1_A0 RFID timeout vector in addition to
the existing RFID vectors. Camera PORT3 and DMA vectors remain owned by the
camera driver. I2C is polled with a timer deadline and uses no USCI interrupt.

## Hardware checks

The retained driver targets the original WISP6 HM01B0 daughterboard wiring:

| Signal | MCU pin/peripheral |
|---|---|
| Camera enable | P3.3 |
| Camera external clock | PJ.0, SMCLK at 4 MHz during capture |
| I2C SDA/SCL | P1.6 / P1.7, UCB0, address `0x24`, 400 kHz |
| Serial pixel input | P1.5, UCA0 SOMI |
| Pixel clock | P2.0, UCA0 CLK |
| Other SPI mux pin retained from original driver | P2.1, UCA0 SIMO |
| Frame valid | P3.6 |
| RF switch/TX enable | P4.3 |

Confirm these against your actual daughterboard revision before programming.
Verify the camera supply rails and I2C pull-ups electrically. A debugger
connection alone does not guarantee camera power, sufficient current, or a
camera-to-PC data connection.

Register configuration retains the original 162 x 122, 8-bit grayscale,
1-bit serial output and gated pixel clock. Each capture restores the camera
clock after RFID, resets/configures the sensor, verifies **both** model-ID
bytes (`01 B0`), arms SPI/DMA at a falling FVLD edge, and receives the following
frame. The camera and camera interrupts/DMA are disabled on success or failure.
I2C waits are bounded at approximately 100 ms; frame capture is bounded at
approximately 2 seconds after streaming begins.

## Run the receiver

Install a .NET 9 SDK/runtime or a compatible newer SDK with the .NET 9 runtime.
The receiver has no NuGet/proprietary SDK dependency; it uses LLRP on TCP 5084.

From the repository root, replace the host and EPC below with your reader and
the camera's actual EPC:

```powershell
dotnet run --project camera-reader\CameraReader.csproj -- speedway-reader.local CA0100000000000000601234 --antenna 1 --frames 1 --test-pattern
```

Start with a test-pattern frame to check clocking and pixel alignment, then
capture actual images continuously:

```powershell
dotnet run --project camera-reader\CameraReader.csproj -- speedway-reader.local CA0100000000000000601234 --antenna 1 --frames 0 --output camera-frames
```

The default is one frame. `--frames 0` runs until Ctrl+C. Each verified frame
is saved as a binary grayscale PGM file; `latest.pgm` is atomically replaced.
Open PGM files with an image viewer that supports that format. Continuous
capture retains each frame, so monitor available disk space.

Use an FCC-compatible R420 and your already-working RF power/antenna settings.
The receiver uses session 0, population 1, and mode index 0 by default; override
with `--mode-index N` only with a known working WISP-compatible setting. The
firmware transmits FM0, so arbitrary dense/Miller modes are not interchangeable.
The inventory Select mask matches `CA01` at EPC bit pointer 32, length 16,
which the existing WISP Select handler supports; each AccessSpec additionally
targets the full EPC.

Close other R420 host applications before connecting. The receiver does not
factory-reset the reader, change its RF power, or delete all existing specs.
It creates/removes its own ROSpec (`0xCA010001`) and per-operation AccessSpecs.
If a previous connection died before cleanup and a duplicate-spec error occurs,
remove only that abandoned camera ROSpec/associated AccessSpecs using your
reader administration tool, or restart the reader when no other users depend
on it.

## Wire protocol

All multibyte values are **big-endian**. Use ordinary Gen2 **Write**, not
BlockWrite, to **User bank 3, word pointer 0, one word**. Wait for each access
result before issuing the next operation; do not put Write and Read into one
AccessSpec.

| Written value | Action |
|---|---|
| `0x8000 \| token` | Capture an image; token is 0..255 |
| `0x8100 \| token` | Capture with walking-one test pattern |
| `0x4000` | Select status/metadata without taking another image |
| `0..988` | Select an image chunk |

Repeating the **same full capture command** does not take another image, even
if its first Write response was lost or capture failed. Change the token to
request a new capture. The receiver reads the previous token when starting
and increments it. It can wrap after 255 because only the most recent request
is retained. Successful captures increment a 16-bit frame ID; failures do not.

After selection, use **Read of User bank 3, word pointer 0, exactly 16 words**:

| Byte offset | Field |
|---|---|
| 0 | Magic `CA` |
| 1 | Protocol version `01` |
| 2 | State: idle=0, busy=1, ready=2, error=3 |
| 3 | Error code |
| 4..5 | Frame ID |
| 6..7 | Chunk index; `FFFF` means metadata |
| 8..9 | Image size, 19764 |
| 10 | Valid payload byte count |
| 11 | Capture token |
| 12..31 | Payload; unused bytes are zero |

Metadata has 8 valid payload bytes: width (2), height (2), image CRC16 (2),
chunk size (1, always 20), test-pattern flag (1).
CRC is CCITT-FALSE: polynomial `0x1021`, initial `0xFFFF`, no reflection/xor-out.
The 989 image chunks contain 20 bytes each except chunk 988, which contains
**4** bytes. Re-selecting or re-reading a chunk never advances the image.

The full frame CRC, frame ID, token, chunk index and payload length are checked
before saving. Failed tag accesses/stale windows are retried up to five times.
Malformed reports, camera errors, checksum mismatches and network failures
terminate with an explicit error; they do not produce a successful image.
A TCP deadline/cancellation can interrupt a message, so the receiver closes
that connection rather than trying to reuse a potentially desynchronized stream.

Camera work executes after the Write callback and outside `WISP_doRFID`.
During capture the device temporarily stops responding to RFID. Do not expect
to read a live `busy` state during that interval. The reader's 10-second access
deadline accommodates capture, initialization and reinventory.

| Error | Meaning / first check |
|---|---|
| 1 | I2C NACK: camera power, address, SDA/SCL |
| 2 | I2C timeout: bus stuck, pull-ups, clock |
| 3 | I2C arbitration loss |
| 4 | Model ID is not `01 B0` |
| 5 | Frame timeout: camera clock, FVLD, pixel clock/data |
| 6 | Unsupported command or chunk index |
| 7 | No valid captured frame |
| 8 | Write did not target User bank word 0 |

RAM session metadata is reset on boot. The FRAM allocation survives, but old
pixels are never advertised as a valid frame after reset. Brownout-resumable
streaming would need additional persistent checkpoints; this implementation
should first be validated with stable external/debugger-supplied power.

## Validation

From the repository root on Windows with MSVC and .NET installed:

```powershell
.\tests\run.ps1
```

The native C tests check all 989 chunks, final-chunk bounds/zero padding,
command replay, token/frame wrap, errors and CRC. They emit a wire fixture
that the C# tests consume to check actual cross-language agreement and full
image reconstruction. Additional receiver tests exercise retries, corrupted
images, stale frames, malformed LLRP messages, fragmented TCP delivery,
keepalive handling and owned-spec cleanup using a loopback reader.

The complete firmware also compiles/links with TI MSP430 **21.6.0.LTS**, and the
build script checks the frame allocation and timeout vector in the actual map.
There is an inherited assembler warning in `WISP_doRFID.asm` about a NOP before
setting GIE; it is not introduced by the camera changes.

These checks do **not** substitute for electrical verification or a real R420
capture test. Before calling the setup operational, flash the executable,
capture and inspect a test pattern, capture a real scene, and measure
complete-frame transfer time.
