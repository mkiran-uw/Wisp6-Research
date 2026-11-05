namespace CameraReader;

public sealed record CameraWindow(byte State, byte Error, ushort FrameId, ushort Chunk, ushort Size,
    byte Token, byte[] Payload)
{
    public static CameraWindow Parse(byte[] data)
    {
        if (data.Length != 32 || data[0] != 0xCA || data[1] != 1 || data[2] > 3 || data[10] > 20)
            throw new InvalidDataException("Invalid camera window or unsupported protocol.");
        return new(data[2], data[3], LlrpWire.ReadWord(data.AsSpan(4)), LlrpWire.ReadWord(data.AsSpan(6)),
            LlrpWire.ReadWord(data.AsSpan(8)), data[11], data[12..(12 + data[10])]);
    }
}

public sealed record CameraFrame(ushort Id, ushort Width, ushort Height, byte[] Pixels)
{
    public byte[] Pgm() => LlrpWire.Join(System.Text.Encoding.ASCII.GetBytes($"P5\n{Width} {Height}\n255\n"), Pixels);
}

public sealed class CameraStream(ICameraTransport transport, int retries = 5)
{
    public static ushort Crc16(byte[] data)
    {
        ushort crc = 0xFFFF;
        foreach (byte value in data)
        {
            crc ^= (ushort)(value << 8);
            for (int bit = 0; bit < 8; bit++)
                crc = (ushort)((crc << 1) ^ ((crc & 0x8000) != 0 ? 0x1021 : 0));
        }
        return crc;
    }

    private async Task<CameraWindow> SelectAsync(ushort command, Func<CameraWindow, bool> matches,
        CancellationToken cancellation)
    {
        for (int attempt = 1; attempt <= retries; attempt++)
        {
            try
            {
                await transport.WriteAsync(command, cancellation);
                CameraWindow window = CameraWindow.Parse(await transport.ReadAsync(cancellation));
                if (matches(window))
                {
                    if (window.State == 3 || window.Error != 0)
                        throw new InvalidDataException($"Camera error {window.Error} (state {window.State}). " +
                            "See the firmware error table.");
                    return window;
                }
                Console.Error.WriteLine($"Retry {attempt}/{retries}: stale camera window for command {command:X4}.");
            }
            catch (TagAccessException error)
            {
                Console.Error.WriteLine($"Retry {attempt}/{retries}: {error.Message}");
            }
            await Task.Delay(20, cancellation);
        }
        throw new IOException($"Camera command {command:X4} failed after {retries} attempts.");
    }

    public async Task<CameraFrame> CaptureAsync(byte token, bool testPattern, CancellationToken cancellation)
    {
        ushort command = (ushort)(0x8000 | (testPattern ? 0x0100 : 0) | token);
        CameraWindow metadata = await SelectAsync(command,
            window => window.Token == token && window.Chunk == 0xFFFF && window.State is 2 or 3, cancellation);
        if (metadata.Payload.Length != 8 || metadata.Size != 19764 || metadata.Payload[6] != 20 ||
            metadata.Payload[7] != (testPattern ? 1 : 0))
            throw new InvalidDataException("Unsupported camera dimensions, chunk size or capture mode.");
        ushort width = LlrpWire.ReadWord(metadata.Payload);
        ushort height = LlrpWire.ReadWord(metadata.Payload.AsSpan(2));
        if (width != 162 || height != 122 || width * height != metadata.Size)
            throw new InvalidDataException("Invalid camera image dimensions.");
        ushort checksum = LlrpWire.ReadWord(metadata.Payload.AsSpan(4));
        byte[] image = new byte[metadata.Size];
        for (ushort chunk = 0; chunk * 20 < image.Length; chunk++)
        {
            CameraWindow window = await SelectAsync(chunk,
                value => value.FrameId == metadata.FrameId && value.Token == token &&
                    (value.Chunk == chunk || value.State == 3), cancellation);
            int offset = chunk * 20;
            int count = Math.Min(20, image.Length - offset);
            if (window.State != 2 || window.Size != metadata.Size || window.Payload.Length != count)
                throw new InvalidDataException($"Invalid image chunk {chunk}.");
            window.Payload.CopyTo(image, offset);
        }
        if (Crc16(image) != checksum)
            throw new InvalidDataException("Frame checksum mismatch; image was not saved.");
        return new(metadata.FrameId, width, height, image);
    }
}
