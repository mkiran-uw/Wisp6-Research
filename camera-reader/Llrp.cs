using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace CameraReader;

public sealed class ReaderException(string message) : Exception(message);
public sealed class TagAccessException(string message) : Exception(message);

public sealed record LlrpMessage(ushort Type, uint Id, byte[] Body);
public sealed record LlrpParameter(ushort Type, byte[] Body);
public sealed record AccessResult(string Epc, uint AccessId, ushort OpId, byte Result, ushort Count, byte[] Data);

public static class LlrpWire
{
    public static byte[] Word(ushort value)
    {
        byte[] data = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(data, value);
        return data;
    }

    public static byte[] Dword(uint value)
    {
        byte[] data = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(data, value);
        return data;
    }

    public static ushort ReadWord(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt16BigEndian(data);
    public static uint ReadDword(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt32BigEndian(data);
    public static byte[] Join(params byte[][] parts) => parts.SelectMany(part => part).ToArray();
    public static byte[] Parameter(ushort type, params byte[][] parts)
    {
        byte[] body = Join(parts);
        return Join(Word(type), Word(checked((ushort)(body.Length + 4))), body);
    }

    public static byte[] Message(ushort type, uint id, byte[] body) =>
        Join(Word((ushort)(0x0400 | type)), Dword(checked((uint)body.Length + 10)), Dword(id), body);

    public static IEnumerable<LlrpParameter> Parameters(byte[] data)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            ushort type;
            int header;
            int length;
            if ((data[offset] & 0x80) != 0)
            {
                type = (ushort)(data[offset] & 0x7F);
                header = 1;
                length = 1 + (type switch
                {
                    1 or 7 or 8 or 10 or 11 or 12 or 14 or 15 or 17 or 19 or 20 => 2,
                    2 or 3 or 4 or 5 => 8,
                    6 => 1,
                    9 or 16 or 18 => 4,
                    13 => 12,
                    _ => throw new ReaderException($"Unsupported LLRP TV parameter {type}.")
                });
            }
            else
            {
                if (data.Length - offset < 4)
                    throw new ReaderException("Truncated LLRP parameter header.");
                type = (ushort)(ReadWord(data.AsSpan(offset)) & 0x03FF);
                header = 4;
                length = ReadWord(data.AsSpan(offset + 2));
            }
            if (length < header || length > data.Length - offset)
                throw new ReaderException("Invalid LLRP parameter length.");
            yield return new(type, data[(offset + header)..(offset + length)]);
            offset += length;
        }
    }

    public static byte[] Inventory(uint id, ushort antenna, ushort mode)
    {
        // WISP6's Select handler matches exactly the first 16 EPC bits at bit 32.
        byte[] mask = Parameter(332, [0x40], Word(32), Word(16), [0xCA, 0x01]);
        byte[] filter = Parameter(331, [0x40], mask, Parameter(334, [0]));
        byte[] command = Parameter(330, [0], filter,
            Parameter(335, Word(mode), Word(0)),
            Parameter(336, [0], Word(1), Dword(0)));
        byte[] inventory = Parameter(186, Word(1), [1],
            Parameter(222, Word(antenna), command));
        byte[] aiSpec = Parameter(183, Word(1), Word(antenna),
            Parameter(184, [0], Dword(0)), inventory);
        byte[] boundary = Parameter(178, Parameter(179, [0]), Parameter(182, [0], Dword(0)));
        byte[] report = Parameter(237, [1], Word(1), Parameter(238, Word(0x8040)));
        return Parameter(177, Dword(id), [0, 0], boundary, aiSpec, report);
    }

    public static byte[] Access(uint accessId, uint inventoryId, ushort antenna, string epc,
        ushort opId, ushort? write)
    {
        byte[] epcBytes = Convert.FromHexString(epc);
        if (epcBytes.Length != 12)
            throw new ArgumentException("Target EPC must contain exactly 12 bytes.", nameof(epc));
        byte[] target = Parameter(339, [0x60], Word(32), Word(96),
            Enumerable.Repeat((byte)0xFF, 12).ToArray(), Word(96), epcBytes);
        byte[] op = write.HasValue
            ? Parameter(342, Word(opId), Dword(0), [0xC0], Word(0), Word(1), Word(write.Value))
            : Parameter(341, Word(opId), Dword(0), [0xC0], Word(0), Word(16));
        return Parameter(207, Dword(accessId), Word(antenna), [1, 0], Dword(inventoryId),
            Parameter(208, [1], Word(1)),
            Parameter(209, Parameter(338, target), op), Parameter(239, [1]));
    }

    public static IEnumerable<AccessResult> Results(byte[] report)
    {
        foreach (LlrpParameter tag in Parameters(report).Where(p => p.Type == 240))
        {
            LlrpParameter[] fields = Parameters(tag.Body).ToArray();
            LlrpParameter? epc = fields.FirstOrDefault(p => p.Type is 13 or 241);
            LlrpParameter? access = fields.FirstOrDefault(p => p.Type == 16);
            if (epc is not null && (epc.Type == 13 ? epc.Body.Length != 12 :
                epc.Body.Length != 14 || ReadWord(epc.Body) != 96))
                throw new ReaderException("Camera tag report must contain a 96-bit EPC.");
            if (access is not null && access.Body.Length != 4)
                throw new ReaderException("Invalid AccessSpecID in tag report.");
            string epcText = epc is null ? "" : Convert.ToHexString(
                epc.Type == 13 ? epc.Body : epc.Body[2..]);
            uint accessId = access is null ? 0 : ReadDword(access.Body);
            foreach (LlrpParameter result in fields.Where(p => p.Type is 349 or 350))
            {
                if (result.Body.Length < 5)
                    throw new ReaderException("Truncated LLRP access result.");
                ushort count = ReadWord(result.Body.AsSpan(3));
                byte[] data = result.Type == 349 ? result.Body[5..] : [];
                if (result.Type == 349 && data.Length != count * 2)
                    throw new ReaderException("LLRP read result length does not match its word count.");
                yield return new(epcText, accessId, ReadWord(result.Body.AsSpan(1)),
                    result.Body[0], count, data);
            }
        }
    }
}

public interface ICameraTransport
{
    Task WriteAsync(ushort command, CancellationToken cancellation);
    Task<byte[]> ReadAsync(CancellationToken cancellation);
}

public sealed class R420Transport(string epc, ushort antenna, ushort mode, TimeSpan timeout)
    : ICameraTransport, IAsyncDisposable
{
    private const uint InventoryId = 0xCA010001;
    private readonly TcpClient client = new();
    private readonly Queue<AccessResult> results = new();
    private NetworkStream? stream;
    private uint messageId;
    private uint accessId = InventoryId;
    private ushort opId;
    private bool inventoryAdded;
    private bool inventoryStarted;

    public async Task ConnectAsync(string host, CancellationToken cancellation, int port = 5084)
    {
        await client.ConnectAsync(host, port, cancellation);
        stream = client.GetStream();
        // Do not reset reader settings or delete another application's specs.
        await RequestAsync(20, LlrpWire.Inventory(InventoryId, antenna, mode), cancellation);
        inventoryAdded = true;
        await RequestAsync(24, LlrpWire.Dword(InventoryId), cancellation);
        await RequestAsync(22, LlrpWire.Dword(InventoryId), cancellation);
        inventoryStarted = true;
    }

    private async Task<LlrpMessage> ReceiveAsync(CancellationToken cancellation)
    {
        NetworkStream connection = stream ?? throw new InvalidOperationException("Reader is not connected.");
        byte[] header = new byte[10];
        await connection.ReadExactlyAsync(header, cancellation);
        ushort versionType = LlrpWire.ReadWord(header);
        uint length = LlrpWire.ReadDword(header.AsSpan(2));
        if (((versionType >> 10) & 7) != 1 || length < 10 || length > 1_048_576)
            throw new ReaderException("Invalid LLRP message header.");
        byte[] body = new byte[checked((int)length - 10)];
        await connection.ReadExactlyAsync(body, cancellation);
        return new((ushort)(versionType & 0x03FF), LlrpWire.ReadDword(header.AsSpan(6)), body);
    }

    private async Task DispatchAsync(LlrpMessage message, CancellationToken cancellation)
    {
        if (message.Type == 62)
            await SendAsync(72, message.Id, [], cancellation);
        else if (message.Type == 61)
        {
            foreach (AccessResult result in LlrpWire.Results(message.Body))
                results.Enqueue(result);
        }
        else if (message.Type == 63)
        {
            foreach (LlrpParameter notification in LlrpWire.Parameters(message.Body).Where(p => p.Type == 246))
            {
                foreach (LlrpParameter item in LlrpWire.Parameters(notification.Body))
                {
                    if (item.Type == 256)
                    {
                        if (item.Body.Length != 2 || LlrpWire.ReadWord(item.Body) != 0)
                            throw new ReaderException("Reader rejected the LLRP connection.");
                    }
                    if (item.Type == 252)
                        throw new ReaderException("Reader exception: " + Convert.ToHexString(item.Body));
                }
            }
        }
    }

    private Task SendAsync(ushort type, uint id, byte[] body, CancellationToken cancellation) =>
        (stream ?? throw new InvalidOperationException("Reader is not connected."))
            .WriteAsync(LlrpWire.Message(type, id, body), cancellation).AsTask();

    private async Task RequestAsync(ushort type, byte[] body, CancellationToken cancellation)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        uint id = ++messageId;
        try
        {
            await SendAsync(type, id, body, deadline.Token);
            while (true)
            {
                LlrpMessage reply = await ReceiveAsync(deadline.Token);
                if (reply.Id == id && reply.Type is not (61 or 62 or 63))
                {
                    if (reply.Type != type + 10 && reply.Type != 100)
                        throw new ReaderException($"Unexpected LLRP response {reply.Type} for request {type}.");
                    LlrpParameter status = LlrpWire.Parameters(reply.Body).FirstOrDefault(p => p.Type == 287)
                        ?? throw new ReaderException("Reader response has no LLRP status.");
                    if (status.Body.Length < 4)
                        throw new ReaderException("Truncated LLRP status.");
                    ushort code = LlrpWire.ReadWord(status.Body);
                    ushort textLength = LlrpWire.ReadWord(status.Body.AsSpan(2));
                    if (textLength > status.Body.Length - 4)
                        throw new ReaderException("Truncated LLRP status description.");
                    if (code != 0)
                        throw new ReaderException($"LLRP request {type} failed ({code}): " +
                            Encoding.UTF8.GetString(status.Body, 4, textLength));
                    return;
                }
                await DispatchAsync(reply, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            // A deadline may interrupt a TCP frame: never continue on a desynchronized stream.
            client.Close();
            throw new ReaderException($"LLRP request {type} timed out; connection closed.");
        }
        catch (OperationCanceledException)
        {
            client.Close();
            throw;
        }
    }

    private async Task<byte[]> AccessAsync(ushort? write, CancellationToken cancellation)
    {
        uint currentAccess = ++accessId;
        ushort currentOp = ++opId;
        results.Clear();
        await RequestAsync(40, LlrpWire.Access(currentAccess, InventoryId, antenna, epc, currentOp, write),
            cancellation);
        try
        {
            await RequestAsync(42, LlrpWire.Dword(currentAccess), cancellation);
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(timeout);
            while (true)
            {
                while (results.TryDequeue(out AccessResult? result))
                {
                    if (result.AccessId != currentAccess || result.OpId != currentOp || result.Epc != epc)
                        continue;
                    if (result.Result != 0)
                        throw new TagAccessException($"Tag {(write.HasValue ? "Write" : "Read")} failed ({result.Result}).");
                    if (result.Count != (write.HasValue ? 1 : 16))
                        throw new TagAccessException("Tag access returned an unexpected word count.");
                    return result.Data;
                }
                await DispatchAsync(await ReceiveAsync(deadline.Token), deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            client.Close();
            throw new ReaderException("Tag access timed out; connection closed. Check EPC, RF power and reader mode.");
        }
        catch (OperationCanceledException)
        {
            client.Close();
            throw;
        }
        finally
        {
            if (client.Connected)
                await RequestAsync(41, LlrpWire.Dword(currentAccess), cancellation);
        }
    }

    public async Task WriteAsync(ushort command, CancellationToken cancellation) =>
        await AccessAsync(command, cancellation);

    public Task<byte[]> ReadAsync(CancellationToken cancellation) => AccessAsync(null, cancellation);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (client.Connected && stream is not null)
            {
                if (inventoryStarted)
                    await RequestAsync(23, LlrpWire.Dword(InventoryId), CancellationToken.None);
                if (inventoryAdded)
                    await RequestAsync(21, LlrpWire.Dword(InventoryId), CancellationToken.None);
            }
        }
        finally
        {
            stream?.Dispose();
            client.Dispose();
        }
    }
}
