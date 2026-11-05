// deeptest: exercise real TCP framing and AccessSpec lifecycle without an RFID device.
using CameraReader;
using System.Net;
using System.Net.Sockets;

static class ScriptedReader
{
    public static async Task RunAsync(string epc, byte[] window)
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(15));
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        List<ushort> operations = [];
        uint inventoryId = 0;
        uint accessId = 0;
        ushort opId = 0;
        bool write = false;
        bool keepaliveAcknowledged = false;
        Task server = Task.Run(async () =>
        {
            using TcpClient connection = await listener.AcceptTcpClientAsync(stop.Token);
            NetworkStream stream = connection.GetStream();
            await stream.WriteAsync(LlrpWire.Message(63, 0, LlrpWire.Parameter(246,
                LlrpWire.Parameter(128, new byte[8]), LlrpWire.Parameter(256, LlrpWire.Word(0)))), stop.Token);
            while (operations.Count < 11)
            {
                byte[] header = new byte[10];
                await stream.ReadExactlyAsync(header, stop.Token);
                ushort type = (ushort)(LlrpWire.ReadWord(header) & 0x03FF);
                uint id = LlrpWire.ReadDword(header.AsSpan(6));
                int length = checked((int)LlrpWire.ReadDword(header.AsSpan(2)) - 10);
                byte[] body = new byte[length];
                await stream.ReadExactlyAsync(body, stop.Token);
                if (type == 72)
                {
                    keepaliveAcknowledged = true;
                    continue;
                }
                operations.Add(type);
                if (type == 20)
                {
                    LlrpParameter roSpec = LlrpWire.Parameters(body).Single();
                    inventoryId = LlrpWire.ReadDword(roSpec.Body);
                    LlrpParameter ai = LlrpWire.Parameters(roSpec.Body[6..]).Single(p => p.Type == 183);
                    LlrpParameter inventory = LlrpWire.Parameters(ai.Body[4..]).Single(p => p.Type == 186);
                    LlrpParameter antenna = LlrpWire.Parameters(inventory.Body[3..]).Single();
                    LlrpParameter command = LlrpWire.Parameters(antenna.Body[2..]).Single();
                    LlrpParameter filter = LlrpWire.Parameters(command.Body[1..]).Single(p => p.Type == 331);
                    byte[] mask = LlrpWire.Parameters(filter.Body[1..]).Single(p => p.Type == 332).Body;
                    Require(mask.SequenceEqual(Convert.FromHexString("4000200010CA01")), "WISP-compatible Select");
                    await stream.WriteAsync(LlrpWire.Message(62, 77, []), stop.Token);
                }
                else if (type == 40)
                {
                    LlrpParameter spec = LlrpWire.Parameters(body).Single();
                    accessId = LlrpWire.ReadDword(spec.Body);
                    Require(LlrpWire.ReadDword(spec.Body.AsSpan(8)) == inventoryId, "AccessSpec ROSpec association");
                    LlrpParameter command = LlrpWire.Parameters(spec.Body[12..]).Single(p => p.Type == 209);
                    LlrpParameter op = LlrpWire.Parameters(command.Body).Single(p => p.Type is 341 or 342);
                    opId = LlrpWire.ReadWord(op.Body);
                    write = op.Type == 342;
                    if (write)
                        Require(LlrpWire.ReadWord(op.Body.AsSpan(11)) == 0x802A, "capture Write data");
                }
                else if (type is 41 or 42)
                    Require(LlrpWire.ReadDword(body) == accessId, "operate only on own AccessSpec");
                else if (type is 21 or 22 or 23 or 24)
                    Require(LlrpWire.ReadDword(body) == inventoryId, "operate only on own ROSpec");
                byte[] reply = LlrpWire.Message((ushort)(type + 10), id,
                    LlrpWire.Parameter(287, LlrpWire.Word(0), LlrpWire.Word(0)));
                // Deliberately fragment headers and bodies across TCP writes.
                await stream.WriteAsync(reply.AsMemory(0, 3), stop.Token);
                await Task.Delay(1, stop.Token);
                await stream.WriteAsync(reply.AsMemory(3), stop.Token);
                if (type == 42)
                {
                    byte[] result = write
                        ? LlrpWire.Parameter(350, [0], LlrpWire.Word(opId), LlrpWire.Word(1))
                        : LlrpWire.Parameter(349, [0], LlrpWire.Word(opId), LlrpWire.Word(16), window);
                    byte[] report = LlrpWire.Parameter(240, [0x8D], Convert.FromHexString(epc), [0x90],
                        LlrpWire.Dword(accessId), result);
                    await stream.WriteAsync(LlrpWire.Message(61, 0, report), stop.Token);
                }
            }
        }, stop.Token);
        try
        {
            await using (R420Transport transport = new(epc, 1, 0, TimeSpan.FromSeconds(5)))
            {
                await transport.ConnectAsync("127.0.0.1", stop.Token, port);
                await transport.WriteAsync(0x802A, stop.Token);
                Require((await transport.ReadAsync(stop.Token)).SequenceEqual(window), "TCP read window");
            }
            // There are 3 inventory setup requests, 2x3 access requests, and 2 cleanup requests.
            stop.CancelAfter(TimeSpan.FromSeconds(2));
            await server;
            Require(keepaliveAcknowledged, "keepalive ACK");
            Require(operations.SequenceEqual(new ushort[] { 20, 24, 22, 40, 42, 41, 40, 42, 41, 23, 21 }),
                "AccessSpec and inventory lifecycle");
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
        }
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
            throw new Exception("FAILED scripted reader: " + name);
    }
}
