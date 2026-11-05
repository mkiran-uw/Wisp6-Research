// deeptest: verify the receiver against windows emitted by the production C firmware.
using CameraReader;
using System.Text;

if (args.Length != 1)
    throw new ArgumentException("Pass the firmware-generated window fixture path.");
byte[] fixture = File.ReadAllBytes(args[0]);
Assert(fixture.Length == 32 * 990, "complete C wire fixture");
byte[] expected = Enumerable.Range(0, 19764).Select(i => (byte)(i * 17 + 3)).ToArray();
int checks = 0;

void Check(bool condition, string name)
{
    Assert(condition, name);
    checks++;
}

static void Assert(bool condition, string name)
{
    if (!condition)
        throw new Exception("FAILED: " + name);
}

static async Task ThrowsAsync<T>(Func<Task> action, string name) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception("FAILED: expected " + typeof(T).Name + " for " + name);
}

static void Throws<T>(Action action, string name) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception("FAILED: expected " + typeof(T).Name + " for " + name);
}

Check(CameraStream.Crc16(Encoding.ASCII.GetBytes("123456789")) == 0x29B1, "CRC reference vector");
Check(CameraStream.Crc16([]) == 0xFFFF, "CRC empty input");
for (int index = 0; index < 990; index++)
{
    CameraWindow window = CameraWindow.Parse(fixture[(index * 32)..((index + 1) * 32)]);
    Check(window.State == 2 && window.FrameId == 1 && window.Token == 43, "C header agreement");
    if (index == 0)
        Check(window.Chunk == 0xFFFF && window.Payload.Length == 8, "C metadata");
    else
    {
        int offset = (index - 1) * 20;
        Check(window.Chunk == index - 1 && window.Payload.SequenceEqual(
            expected[offset..Math.Min(offset + 20, expected.Length)]), "C chunk agreement");
    }
}

byte[] valid = fixture[..32];
foreach ((int position, byte value) in new (int, byte)[] { (0, 0), (1, 2), (2, 4), (10, 21) })
{
    byte[] invalid = (byte[])valid.Clone();
    invalid[position] = value;
    Throws<InvalidDataException>(() => CameraWindow.Parse(invalid), "window field " + position);
}
Throws<InvalidDataException>(() => CameraWindow.Parse(valid[..31]), "short window");
Throws<InvalidDataException>(() => CameraWindow.Parse([.. valid, 0]), "oversized window");

FakeCamera transport = new(fixture);
CameraStream camera = new(transport);
CameraFrame frame = await camera.CaptureAsync(44, false, CancellationToken.None);
Check(frame.Id == 2 && frame.Width == 162 && frame.Height == 122, "frame metadata");
Check(frame.Pixels.SequenceEqual(expected), "full frame reconstruction");
Check(frame.Pgm().SequenceEqual(LlrpWire.Join(Encoding.ASCII.GetBytes("P5\n162 122\n255\n"), expected)), "PGM shape");
Check(transport.Captures == 1 && transport.Writes == 990, "one capture plus 989 selections");
frame = await camera.CaptureAsync(45, true, CancellationToken.None);
Check(frame.Id == 3 && transport.Captures == 2, "repeated capture with test pattern");
frame = await camera.CaptureAsync(45, true, CancellationToken.None);
Check(frame.Id == 3 && transport.Captures == 2, "duplicate capture token");
await camera.CaptureAsync(255, false, CancellationToken.None);
await camera.CaptureAsync(0, false, CancellationToken.None);
Check(transport.Captures == 4, "token wraparound");

FakeCamera lostWrite = new(fixture) { LoseCaptureResponse = true, StaleChunkOnce = 400 };
CameraFrame retried = await new CameraStream(lostWrite).CaptureAsync(46, false, CancellationToken.None);
Check(retried.Pixels.SequenceEqual(expected) && lostWrite.Captures == 1, "lost Write and stale chunk retry");
FakeCamera errorCamera = new(fixture) { CameraError = 5 };
await ThrowsAsync<InvalidDataException>(() => new CameraStream(errorCamera).CaptureAsync(47, false, CancellationToken.None),
    "capture timeout reported by firmware");
FakeCamera corrupt = new(fixture) { CorruptChunk = 988 };
await ThrowsAsync<InvalidDataException>(() => new CameraStream(corrupt).CaptureAsync(48, false, CancellationToken.None),
    "frame checksum");
FakeCamera wrongFrame = new(fixture) { WrongFrameChunk = 3 };
await ThrowsAsync<IOException>(() => new CameraStream(wrongFrame, 2).CaptureAsync(49, false, CancellationToken.None),
    "frame identity changes");
FakeCamera shortTail = new(fixture) { ShortChunk = 988 };
await ThrowsAsync<InvalidDataException>(() => new CameraStream(shortTail).CaptureAsync(50, false, CancellationToken.None),
    "partial tail length");
FakeCamera failedAccess = new(fixture) { FailAllWrites = true };
await ThrowsAsync<IOException>(() => new CameraStream(failedAccess, 2).CaptureAsync(51, false, CancellationToken.None),
    "bounded retries");
using (CancellationTokenSource cancelled = new())
{
    cancelled.Cancel();
    await ThrowsAsync<OperationCanceledException>(() => camera.CaptureAsync(52, false, cancelled.Token), "cancellation");
}

string epc = "CA0100000000000000601234";
byte[] readSpec = LlrpWire.Access(123, 456, 1, epc, 7, null);
LlrpParameter access = LlrpWire.Parameters(readSpec).Single();
Check(access.Type == 207 && access.Body[..12].SequenceEqual(Convert.FromHexString("0000007B00010100000001C8")),
    "AccessSpec fixed header");
LlrpParameter accessCommand = LlrpWire.Parameters(access.Body[12..]).Single(p => p.Type == 209);
LlrpParameter read = LlrpWire.Parameters(accessCommand.Body).Single(p => p.Type == 341);
Check(read.Body.SequenceEqual(Convert.FromHexString("000700000000C000000010")), "Read word zero, count sixteen");
byte[] writeSpec = LlrpWire.Access(124, 456, 1, epc, 8, 0x812A);
LlrpParameter writeAccess = LlrpWire.Parameters(writeSpec).Single();
LlrpParameter writeCommand = LlrpWire.Parameters(writeAccess.Body[12..]).Single(p => p.Type == 209);
Check(LlrpWire.Parameters(writeCommand.Body).Single(p => p.Type == 342).Body.SequenceEqual(
    Convert.FromHexString("000800000000C000000001812A")), "Write single word and byte order");
Check(LlrpWire.Message(40, 1, []).SequenceEqual(Convert.FromHexString("04280000000A00000001")), "LLRP message header");
Throws<ArgumentException>(() => LlrpWire.Access(1, 2, 1, "CA01", 1, null), "short target EPC");
Throws<ReaderException>(() => LlrpWire.Parameters([0, 1, 0]).ToArray(), "short TLV header");
Throws<ReaderException>(() => LlrpWire.Parameters([0, 1, 0, 3]).ToArray(), "TLV length below header");
Throws<ReaderException>(() => LlrpWire.Parameters([0, 1, 0, 9]).ToArray(), "TLV beyond packet");
Throws<ReaderException>(() => LlrpWire.Parameters([0xFE]).ToArray(), "unknown TV");
Throws<ReaderException>(() => LlrpWire.Parameters([0x8D, 0]).ToArray(), "truncated EPC-96");
Check(!LlrpWire.Parameters([]).Any(), "empty parameters");

byte[] tag = LlrpWire.Parameter(240, [0x8D], Convert.FromHexString(epc), [0x90], LlrpWire.Dword(123),
    LlrpWire.Parameter(349, [0], LlrpWire.Word(7), LlrpWire.Word(16), valid));
AccessResult result = LlrpWire.Results(tag).Single();
Check(result.Epc == epc && result.AccessId == 123 && result.OpId == 7 && result.Data.SequenceEqual(valid),
    "read report decoding with TV identifiers");
byte[] writeTag = LlrpWire.Parameter(240, LlrpWire.Parameter(241, LlrpWire.Word(96), Convert.FromHexString(epc)),
    [0x90], LlrpWire.Dword(124), LlrpWire.Parameter(350, [0], LlrpWire.Word(8), LlrpWire.Word(1)));
Check(LlrpWire.Results(writeTag).Single().Count == 1, "write report with EPCData");
Throws<ReaderException>(() => LlrpWire.Results(LlrpWire.Parameter(240,
    LlrpWire.Parameter(349, [0, 0]))).ToArray(), "short access result");
Throws<ReaderException>(() => LlrpWire.Results(LlrpWire.Parameter(240,
    LlrpWire.Parameter(349, [0], LlrpWire.Word(1), LlrpWire.Word(16), [0]))).ToArray(), "wrong read word count");
await ScriptedReader.RunAsync(epc, valid);
Console.WriteLine($"Receiver tests passed: {checks} checks, C/C# wire agreement, retries, malformed reports and TCP reader lifecycle.");

sealed class FakeCamera(byte[] fixture) : ICameraTransport
{
    private ushort command = 0x4000;
    private ushort captureCommand;
    private ushort frameId = 1;
    public int Captures { get; private set; }
    public int Writes { get; private set; }
    public bool LoseCaptureResponse { get; set; }
    public bool FailAllWrites { get; init; }
    public int StaleChunkOnce { get; set; } = -1;
    public int CorruptChunk { get; init; } = -1;
    public int WrongFrameChunk { get; init; } = -1;
    public int ShortChunk { get; init; } = -1;
    public byte CameraError { get; init; }

    public Task WriteAsync(ushort value, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        Writes++;
        if (FailAllWrites)
            throw new TagAccessException("simulated RF failure");
        command = value;
        if ((value & 0xFE00) == 0x8000 && value != captureCommand)
        {
            captureCommand = value;
            frameId++;
            Captures++;
        }
        if ((value & 0xFE00) == 0x8000 && LoseCaptureResponse)
        {
            LoseCaptureResponse = false;
            throw new TagAccessException("capture applied but Write response lost");
        }
        return Task.CompletedTask;
    }

    public Task<byte[]> ReadAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        int index = command < 989 ? command + 1 : 0;
        byte[] window = fixture[(index * 32)..((index + 1) * 32)];
        LlrpWire.Word(frameId).CopyTo(window, 4);
        window[11] = (byte)captureCommand;
        if (index == 0)
            window[19] = (byte)((captureCommand >> 8) & 1);
        if (CameraError != 0)
        {
            window[2] = 3;
            window[3] = CameraError;
        }
        if (command == StaleChunkOnce)
        {
            window[7] ^= 1;
            StaleChunkOnce = -1;
        }
        if (command == CorruptChunk)
            window[12] ^= 1;
        if (command == WrongFrameChunk)
            window[5]++;
        if (command == ShortChunk)
            window[10]--;
        return Task.FromResult(window);
    }
}
