using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace FesticalDoomsday.Client.Backend;

/// <summary>Monotonic clock in seconds, the counterpart of Python's time.monotonic().</summary>
internal static class Monotonic
{
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}

/// <summary>Raised when the game server closed the connection.</summary>
public sealed class ServerClosedException : IOException
{
    public ServerClosedException(string message) : base(message)
    {
    }
}

/// <summary>
/// GameClient.cs - a small raw-TCP client for the [len][opcode][protobuf] protocol.
///
/// It is host/port agnostic. Ported from client.py; the socket work stays
/// blocking, exactly like the Python original, because the backend drives every
/// account from its own thread.
/// </summary>
internal sealed class GameClient : IDisposable
{
    private readonly object _sendLock = new();
    private readonly Protocol.FrameReader _reader = new();
    private readonly Queue<(ushort Opcode, byte[] Body)> _queue = new();

    private Socket? _socket;

    public GameClient(string host, int port, double timeout = 5.0)
    {
        Host = host;
        Port = port;
        Timeout = timeout;
    }

    public string Host { get; }
    public int Port { get; }
    public double Timeout { get; }

    /// <summary>Optional callback for decoded game-server frames.</summary>
    public Action<ushort, byte[]>? OnFrame { get; set; }

    /// <summary>Learned from the server's world pushes.</summary>
    public ulong? ArmyId { get; private set; }

    /// <summary>Server build string from the login reply.</summary>
    public string? Build { get; private set; }

    /// <summary>ErrorCode of the login reply (0 = accepted).</summary>
    public ulong? LoginError { get; private set; }

    /// <summary>COMPRESS_PACKAGE (0x04B3) containers seen.</summary>
    public int Packages { get; private set; }

    /// <summary>Frames that arrived inside those containers.</summary>
    public int PackageFrames { get; private set; }

    /// <summary>Containers we could not unpack (kept opaque).</summary>
    public int PackageErrors { get; private set; }

    // ---- connection -------------------------------------------------------

    public GameClient Connect()
    {
        IPAddress[] addresses;
        try
        {
            addresses = Dns.GetHostAddresses(Host);
        }
        catch (Exception exception)
        {
            throw new SocketException((int)SocketError.HostNotFound, exception.Message);
        }

        if (addresses.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        Exception? last = null;
        foreach (var address in addresses
                     .OrderBy(item => item.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            try
            {
                var connect = socket.ConnectAsync(address, Port);
                if (!connect.Wait(TimeSpan.FromSeconds(Math.Max(0.1, Timeout))))
                    throw new TimeoutException($"connection to {Host}:{Port} timed out");
                connect.GetAwaiter().GetResult();
                socket.ReceiveTimeout = TimeoutMilliseconds;
                socket.SendTimeout = TimeoutMilliseconds;
                _socket = socket;
                return this;
            }
            catch (Exception exception)
            {
                socket.Dispose();
                last = exception is AggregateException aggregate
                    ? aggregate.InnerException ?? exception
                    : exception;
            }
        }

        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    private int TimeoutMilliseconds => (int)Math.Clamp(Timeout * 1000.0, 1.0, int.MaxValue);

    public void Close()
    {
        var socket = Interlocked.Exchange(ref _socket, null);
        if (socket is null)
            return;
        try
        {
            socket.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
            // The peer may already be gone; closing is all that matters.
        }
        catch (ObjectDisposedException)
        {
        }

        socket.Dispose();
    }

    public void Dispose() => Close();

    // ---- raw frames -------------------------------------------------------

    public void Send(byte[] frame)
    {
        var socket = _socket ?? throw new IOException("the game connection is closed.");
        lock (_sendLock)
        {
            var sent = 0;
            while (sent < frame.Length)
            {
                sent += socket.Send(frame, sent, frame.Length - sent, SocketFlags.None);
            }
        }
    }

    /// <summary>Return the next (opcode, body); throws TimeoutException if nothing arrives.</summary>
    public (ushort Opcode, byte[] Body) Recv(double? timeout = null)
    {
        var socket = _socket ?? throw new IOException("the game connection is closed.");
        var deadline = Monotonic.Now + (timeout ?? Timeout);
        while (_queue.Count == 0)
        {
            var left = deadline - Monotonic.Now;
            if (left <= 0)
                throw new TimeoutException("no frame received in time");
            socket.ReceiveTimeout = (int)Math.Clamp(left * 1000.0, 1.0, int.MaxValue);
            var buffer = new byte[65536];
            int read;
            try
            {
                read = socket.Receive(buffer, SocketFlags.None);
            }
            catch (SocketException exception) when (exception.SocketErrorCode is SocketError.TimedOut
                                                       or SocketError.WouldBlock)
            {
                throw new TimeoutException("no frame received in time");
            }

            if (read == 0)
                throw new ServerClosedException("server closed the connection");
            var chunk = new byte[read];
            Array.Copy(buffer, 0, chunk, 0, read);
            Expand(_reader.Feed(chunk));
        }

        return _queue.Dequeue();
    }

    /// <summary>
    /// Unwrap COMPRESS_PACKAGE containers so callers see the real frames.
    /// Almost everything the server sends after enterGame travels inside 0x04B3,
    /// so without this the client would look like it is receiving nothing.
    /// A container that fails to unpack is queued as-is instead of being lost.
    /// </summary>
    private void Expand(List<(ushort Opcode, byte[] Body)> frames)
    {
        foreach (var (opcode, body) in frames)
        {
            if (opcode != Protocol.CompressPackage)
            {
                _queue.Enqueue((opcode, body));
                Learn(opcode, body);
                OnFrame?.Invoke(opcode, body);
                continue;
            }

            Packages++;
            List<(ushort Opcode, byte[] Body)> inner;
            try
            {
                inner = Protocol.UnpackPackage(body, out _);
            }
            catch (ProtocolError)
            {
                PackageErrors++;
                _queue.Enqueue((opcode, body));
                continue;
            }

            PackageFrames += inner.Count;
            foreach (var entry in inner)
            {
                _queue.Enqueue(entry);
            }

            foreach (var (innerOpcode, innerBody) in inner)
            {
                Learn(innerOpcode, innerBody);
                OnFrame?.Invoke(innerOpcode, innerBody);
            }
        }
    }

    /// <summary>Wait for a specific opcode; other frames (state-sync pushes) are returned in skipped.</summary>
    public (byte[] Body, List<(ushort Opcode, byte[] Body)> Skipped) WaitFor(ushort opcode, double? timeout = null)
    {
        var skipped = new List<(ushort, byte[])>();
        var deadline = Monotonic.Now + (timeout ?? Timeout);
        while (true)
        {
            var (op, body) = Recv(Math.Max(0.0, deadline - Monotonic.Now));
            if (op == opcode)
                return (body, skipped);
            skipped.Add((op, body));
        }
    }

    // ---- high-level calls -------------------------------------------------

    /// <summary>
    /// Extract known values from the server's world pushes.
    /// 'army id' is the 48-bit id the client also echoes in its march requests
    /// (0x2790 f3); the server states it in 0x2EEF f1 and again inside the
    /// 0x298D sync-entities notice. 0x2870 is only a mock-server convenience.
    /// </summary>
    private void Learn(ushort opcode, byte[] body)
    {
        if (body.Length == 0)
            return;
        var armyField = opcode switch
        {
            0x2870 => 1,         // mock server shortcut
            0x2BBB => 1,         // G2ClintoRegionNotice
            0x2EEF => 1,         // Gs2ClguildCaptrueActiveBuildingNotice
            0x288B => 3,         // Gs2ClguildTempleAndPassReply
            0x298D => 3,         // Gs2ClsyncEntitiesDataNotice
            _ => -1
        };
        if (armyField < 0)
            return;
        List<PbField> fields;
        try
        {
            fields = Protocol.Parse(body);
        }
        catch (ProtocolError)
        {
            return;
        }

        var value = Protocol.Get(fields, armyField);
        if (value is { IsVarint: true } && value.Varint > (1UL << 40))
            ArmyId = value.Varint;
    }

    public string? Login(ulong playerId, string sessionToken, string deviceId, string version = "1.61.5")
    {
        Send(Protocol.BuildLogin(playerId, sessionToken, deviceId, version));
        var (body, _) = WaitFor(Protocol.LoginAck);
        var fields = Protocol.Parse(body);
        Build = Protocol.DecodeText(Protocol.GetBytes(fields, 3)) ?? string.Empty;
        var error = Protocol.Get(fields, 1);
        LoginError = error is { IsVarint: true } ? error.Varint : 0;
        // MsgGS2CLLoginReply is {ErrorCode(1), ServerVersion(3)}: a non-zero
        // ErrorCode means the login itself was refused - report it, do not
        // blindly continue into the world.
        // The REAL client asks to enter the game within ~30ms. Without it the
        // server never sends the world and eventually drops the connection.
        Send(Protocol.BuildEnterGame());
        // Collect the pushes that follow and learn the army id if announced.
        var deadline = Monotonic.Now + 60.0;
        try
        {
            while (ArmyId is null && Monotonic.Now < deadline)
            {
                var (op, b) = Recv(1.0);
                Learn(op, b);
            }
        }
        catch (TimeoutException)
        {
            // The server did not announce an army id; the caller decides what to do.
        }

        return Build;
    }

    /// <summary>
    /// Wait for the world, then send the novice-record request.
    /// With burst=false only the novice-record step is done.
    /// </summary>
    public HandshakeResult Handshake(ulong configVersion = 10112001, double timeout = 8.0, double quiet = 0.6,
        bool burst = false, ulong? playerId = null, ulong? guildId = null, string? publicIp = null,
        Action<string>? log = null)
    {
        void Say(string message) => log?.Invoke(message);
        var stats = new Dictionary<int, int>();
        var deadline = Monotonic.Now + timeout;

        // Phase A: drain pushes until a quiet gap (the real client waited ~1.2s)
        var last = Monotonic.Now;
        while (Monotonic.Now - last < quiet && Monotonic.Now < deadline)
        {
            (ushort Opcode, byte[] Body) frame;
            try
            {
                frame = Recv(deadline - Monotonic.Now);
            }
            catch (TimeoutException)
            {
                break;
            }

            stats[frame.Opcode] = stats.GetValueOrDefault(frame.Opcode) + 1;
            Learn(frame.Opcode, frame.Body);
            last = Monotonic.Now;
        }

        // Phase B: novice-record request (0x2C96)
        Send(Protocol.BuildNoviceRecord(configVersion));
        Say($"[handshake] sent NOVICE_RECORD guide_id={configVersion}");
        var gotAck = false;
        try
        {
            var (body, _) = WaitFor(Protocol.NoviceRecordAck, 3.0);
            gotAck = true;
            var fields = Protocol.Parse(body);
            Say($"[handshake] NOVICE_RECORD_ACK guide_id={Protocol.GetVarint(fields, 1)} " +
                $"error={Protocol.GetVarint(fields, 2)}");
        }
        catch (TimeoutException)
        {
            Say("[handshake] no NOVICE_RECORD_ACK within 3s (continuing)");
        }

        // Phase C: the startup burst (only if asked; the real client sends it)
        if (burst)
        {
            var frames = Protocol.StartupBurst(ArmyId, guildId, playerId, publicIp);
            Send(frames);
            Say($"[handshake] sent startup burst ({frames.Length} bytes)");
        }

        // Phase D: brief drain of whatever follows
        var end = Monotonic.Now + 0.5;
        while (Monotonic.Now < end)
        {
            try
            {
                var (op, body) = Recv(end - Monotonic.Now);
                stats[op] = stats.GetValueOrDefault(op) + 1;
                Learn(op, body);
            }
            catch (TimeoutException)
            {
                break;
            }
        }

        return new HandshakeResult(ArmyId, gotAck, stats, Packages, PackageFrames, PackageErrors);
    }

    /// <summary>
    /// Hold the connection open like the real client: drain server pushes
    /// continuously, send PING every pingInterval seconds and answer a
    /// server-side PING with PONG if one ever arrives.
    /// </summary>
    public Dictionary<string, int> KeepAlive(double seconds, double pingInterval = 15.0,
        Action<ushort, byte[]>? onFrame = null)
    {
        var stats = new Dictionary<string, int>();
        void Bump(string key) => stats[key] = stats.GetValueOrDefault(key) + 1;

        var end = Monotonic.Now + seconds;
        var nextPing = Monotonic.Now + Math.Min(pingInterval, seconds);
        while (Monotonic.Now < end)
        {
            var now = Monotonic.Now;
            if (now >= nextPing)
            {
                Send(Protocol.BuildPing());
                Bump("ping_sent");
                nextPing = now + pingInterval;
            }

            (ushort Opcode, byte[] Body) frame;
            try
            {
                frame = Recv(Math.Min(0.5, Math.Max(0.05, end - now)));
            }
            catch (TimeoutException)
            {
                continue;
            }
            catch (ServerClosedException)
            {
                Bump("closed");
                break;
            }

            Bump(frame.Opcode.ToString());
            if (frame.Opcode == Protocol.Ping)
            {
                Send(Protocol.BuildPong());
                Bump("pong_sent");
            }

            if (frame.Opcode == Protocol.Pong)
            {
                Bump("pong_seen");
            }

            Learn(frame.Opcode, frame.Body);
            onFrame?.Invoke(frame.Opcode, frame.Body);
        }

        return stats;
    }

    public double Ping()
    {
        var start = Monotonic.Now;
        Send(Protocol.BuildPing());
        WaitFor(Protocol.Pong);
        return (Monotonic.Now - start) * 1000.0;
    }

    // ---- movement helpers -------------------------------------------------

    private byte[] MoveFrame(byte[] frame)
    {
        Send(frame);
        return WaitFor(Protocol.MoveAck).Body;
    }

    public byte[] Move(long x, long y, ulong? armyId = null) =>
        MoveFrame(Protocol.MoveSimple(x, y, armyId ?? ArmyId ?? 0));

    public byte[] MoveToTarget(long x, long y, ulong targetId, ulong? armyId = null) =>
        MoveFrame(Protocol.MoveToTarget(x, y, targetId, armyId ?? ArmyId ?? 0));

    public byte[] ReturnToShelter(ulong? armyId = null) =>
        MoveFrame(Protocol.ReturnToShelter(armyId ?? ArmyId ?? 0));
}

/// <summary>Summary of one handshake run.</summary>
public sealed class HandshakeResult
{
    public HandshakeResult(ulong? armyId, bool configAck, Dictionary<int, int> frames, int packages,
        int packageFrames, int packageErrors)
    {
        ArmyId = armyId;
        ConfigAck = configAck;
        Frames = frames;
        Packages = packages;
        PackageFrames = packageFrames;
        PackageErrors = packageErrors;
    }

    public ulong? ArmyId { get; }
    public bool ConfigAck { get; }
    public Dictionary<int, int> Frames { get; }
    public int Packages { get; }
    public int PackageFrames { get; }
    public int PackageErrors { get; }
}
