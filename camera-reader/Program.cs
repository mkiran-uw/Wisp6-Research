using CameraReader;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: camera-reader HOST EPC [--antenna N] [--mode-index N] [--frames N] " +
        "[--output DIRECTORY] [--test-pattern]\nFrames defaults to 1; use 0 for continuous capture. EPC is 24 hex digits.");
    return 2;
}

try
{
    string host = args[0];
    string epc = args[1].Replace(" ", "").ToUpperInvariant();
    if (epc.Length != 24 || !epc.StartsWith("CA01", StringComparison.Ordinal))
        throw new ArgumentException("Use the complete camera EPC (24 hex digits, starting CA01), not the LED demo EPC.");
    _ = Convert.FromHexString(epc);
    ushort antenna = 1;
    ushort mode = 0;
    int frames = 1;
    string output = "camera-frames";
    bool testPattern = false;
    for (int index = 2; index < args.Length; index++)
    {
        if (args[index] == "--test-pattern")
        {
            testPattern = true;
            continue;
        }
        if (index + 1 == args.Length)
            throw new ArgumentException($"Missing value for {args[index]}.");
        string option = args[index++];
        string value = args[index];
        switch (option)
        {
            case "--antenna": antenna = ushort.Parse(value, CultureInfo.InvariantCulture); break;
            case "--mode-index": mode = ushort.Parse(value, CultureInfo.InvariantCulture); break;
            case "--frames": frames = int.Parse(value, CultureInfo.InvariantCulture); break;
            case "--output": output = value; break;
            default: throw new ArgumentException($"Unknown option {option}.");
        }
    }
    if (antenna is < 1 or > 4 || frames < 0)
        throw new ArgumentException("R420 antenna must be 1..4 and frames must be nonnegative.");
    Directory.CreateDirectory(output);
    using CancellationTokenSource stop = new();
    Console.CancelKeyPress += (_, cancel) => { cancel.Cancel = true; stop.Cancel(); };
    await using R420Transport reader = new(epc, antenna, mode, TimeSpan.FromSeconds(10));
    await reader.ConnectAsync(host, stop.Token);
    CameraStream camera = new(reader);
    // Avoid replaying the previous run's last capture token.
    await reader.WriteAsync(0x4000, stop.Token);
    CameraWindow initial = CameraWindow.Parse(await reader.ReadAsync(stop.Token));
    byte token = (byte)(initial.Token + 1);
    for (int number = 0; frames == 0 || number < frames; number++, token++)
    {
        Stopwatch watch = Stopwatch.StartNew();
        CameraFrame frame = await camera.CaptureAsync(token, testPattern, stop.Token);
        string path = Path.Combine(output, $"frame-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}-{frame.Id:D5}.pgm");
        await File.WriteAllBytesAsync(path, frame.Pgm(), stop.Token);
        string latest = Path.Combine(output, "latest.pgm");
        string temporary = Path.Combine(output, "latest.pgm.tmp");
        await File.WriteAllBytesAsync(temporary, frame.Pgm(), stop.Token);
        File.Move(temporary, latest, true);
        Console.WriteLine($"Frame {frame.Id}: {frame.Width}x{frame.Height}, {frame.Pixels.Length} bytes, " +
            $"{watch.Elapsed.TotalSeconds:F2}s, saved {path}");
    }
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Capture cancelled.");
    return 130;
}
catch (Exception error) when (error is ReaderException or TagAccessException or IOException or
    SocketException or ArgumentException or FormatException or OverflowException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
