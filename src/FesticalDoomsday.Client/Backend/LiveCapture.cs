using System.Diagnostics;
using System.IO;

namespace FesticalDoomsday.Client.Backend;

/// <summary>Runs the Wireshark command line tool (tshark).</summary>
internal static class Tshark
{
    private static readonly string[] Candidates =
    {
        @"C:\Program Files\Wireshark\tshark.exe",
        @"C:\Program Files (x86)\Wireshark\tshark.exe",
        "/usr/bin/tshark",
        "/usr/local/bin/tshark",
        "/opt/homebrew/bin/tshark",
        "tshark"
    };

    /// <summary>Finds tshark on the machine, or explains how to install it.</summary>
    public static string Find()
    {
        foreach (var candidate in Candidates)
        {
            if (Path.IsPathRooted(candidate))
            {
                if (File.Exists(candidate))
                    return candidate;
                continue;
            }

            var found = SearchPath(candidate);
            if (found is not null)
                return found;
        }

        throw new FileNotFoundException(
            "tshark not found - install Wireshark (npcap is already running).");
    }

    private static string? SearchPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory.Trim('"'), executable);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (File.Exists(candidate))
                return candidate;
            if (File.Exists(candidate + ".exe"))
                return candidate + ".exe";
        }

        return null;
    }

    /// <summary>Run tshark and return its standard output.</summary>
    public static string Run(string executable, IEnumerable<string> args, int timeoutSeconds = 180)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in args)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("tshark could not be started.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutSeconds * 1000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new TimeoutException("tshark did not finish in time.");
        }

        var stdout = output.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            var stderr = error.GetAwaiter().GetResult();
            throw new InvalidOperationException(stderr[..Math.Min(400, stderr.Length)]);
        }

        return stdout;
    }
}

/// <summary>One reassembled TCP stream of a capture.</summary>
public sealed class TcpStream
{
    public TcpStream((string Ip, int Port) a, (string Ip, int Port) b)
    {
        A = a;
        B = b;
    }

    public (string Ip, int Port) A { get; }
    public (string Ip, int Port) B { get; }
    public byte[] DataA { get; set; } = Array.Empty<byte>();
    public byte[] DataB { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// LiveCapture.cs - read the traffic of a capture file.
///
/// The login is two-stage and both stages travel as plain TCP protobuf, so a
/// capture sees them in clear text. This module reassembles the TCP streams of
/// a capture and decodes them with the game's own message names. Ported from
/// live_capture.py.
/// </summary>
internal static class LiveCapture
{
    public static string FindTshark() => Tshark.Find();

    /// <summary>
    /// Reassemble every TCP stream of a capture.
    /// NOTE: ip.src/ip.dst are used - tcp.src/tcp.dst are not valid field names
    /// in current tshark builds (they silently produce an empty table).
    /// </summary>
    public static Dictionary<int, TcpStream> ReadStreams(string tshark, string pcapPath)
    {
        var output = Tshark.Run(tshark, new[]
        {
            "-r", pcapPath, "-T", "fields",
            "-e", "tcp.stream", "-e", "ip.src", "-e", "tcp.srcport",
            "-e", "ip.dst", "-e", "tcp.dstport",
            "-e", "tcp.seq_raw", "-e", "tcp.payload",
            "-E", "separator=|"
        });

        var streams = new Dictionary<int, TcpStream>();
        var segments = new Dictionary<int, Dictionary<(string Source, long Sequence), byte[]>>();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.TrimEnd('\r').Split('|');
            if (parts.Length < 7)
                continue;
            var sidText = parts[0];
            var src = parts[1];
            var sport = parts[2];
            var dst = parts[3];
            var dport = parts[4];
            var seqRaw = parts[5];
            var payload = parts[6];
            if (string.IsNullOrEmpty(sidText) || string.IsNullOrEmpty(payload))
                continue;
            if (!int.TryParse(sidText, out var sid))
                continue;
            if (!long.TryParse(seqRaw, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var sequence))
            {
                continue;
            }

            if (!streams.TryGetValue(sid, out var stream))
            {
                stream = new TcpStream((src, ParsePort(sport)), (dst, ParsePort(dport)));
                streams[sid] = stream;
                segments[sid] = new Dictionary<(string, long), byte[]>();
            }

            // Keyed by (src, seq) so a retransmitted segment is not counted twice.
            var key = (src, sequence);
            if (!segments[sid].ContainsKey(key))
                segments[sid][key] = Convert.FromHexString(payload);
        }

        foreach (var (sid, stream) in streams)
        {
            var map = segments[sid];
            stream.DataA = Join(map.Where(item => item.Key.Source == stream.A.Ip)
                .OrderBy(item => item.Key.Sequence).Select(item => item.Value));
            stream.DataB = Join(map.Where(item => item.Key.Source == stream.B.Ip)
                .OrderBy(item => item.Key.Sequence).Select(item => item.Value));
        }

        return streams;
    }

    private static int ParsePort(string value) =>
        int.TryParse(value, out var port) ? port : 0;

    private static byte[] Join(IEnumerable<byte[]> parts)
    {
        var list = parts.ToList();
        var total = list.Sum(part => part.Length);
        var joined = new byte[total];
        var offset = 0;
        foreach (var part in list)
        {
            Array.Copy(part, 0, joined, offset, part.Length);
            offset += part.Length;
        }

        return joined;
    }

    /// <summary>Feed a reassembled TCP blob through the frame reader; return the frames.</summary>
    public static List<(ushort Opcode, byte[] Body)> DecodeStream(byte[] blob, Action<string>? log = null)
    {
        var reader = new Protocol.FrameReader();
        try
        {
            return reader.Feed(blob);
        }
        catch (ProtocolError exception)
        {
            log?.Invoke($"    (frame reader stopped: {exception.Message})");
            return new List<(ushort, byte[])>();
        }
    }
}
