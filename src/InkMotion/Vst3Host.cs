using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NAudio.Wave;

namespace InkMotion;

public sealed record Vst3PluginDescription(
    string ModulePath,
    string ClassId,
    string Name,
    string Vendor,
    string Version);

public sealed record Vst3Parameter(
    uint Id,
    string Title,
    string Units,
    int StepCount,
    uint Flags,
    double NormalizedValue);

public static class Vst3Host
{
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(20);

    public static IReadOnlyList<Vst3PluginDescription> ScanModule(string modulePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modulePath);
        string fullPath = Path.GetFullPath(modulePath);
        ProcessStartInfo startInfo = CreateStartInfo("--scan", fullPath);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The VST3 host worker could not be started.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(ScanTimeout);
        try
        {
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The VST3 module scan exceeded its time limit.");
        }

        string output = outputTask.GetAwaiter().GetResult();
        string error = errorTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"The VST3 module scan failed with exit code {process.ExitCode}."
                : error.Trim());
        }

        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement effects = document.RootElement.GetProperty("effects");
        return effects.EnumerateArray().Select(effect => new Vst3PluginDescription(
            fullPath,
            effect.GetProperty("classId").GetString() ?? string.Empty,
            effect.GetProperty("name").GetString() ?? string.Empty,
            effect.GetProperty("vendor").GetString() ?? string.Empty,
            effect.GetProperty("version").GetString() ?? string.Empty)).ToArray();
    }

    public static Vst3HostedSampleProvider CreateEffect(
        ISampleProvider source,
        Vst3PluginDescription plugin,
        IReadOnlyDictionary<uint, double>? parameterValues = null,
        bool preserveTail = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(plugin);
        return new Vst3HostedSampleProvider(source, plugin, parameterValues, preserveTail);
    }

    internal static string GetWorkerPath()
    {
        string architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.X64 => "x64",
            Architecture.Arm64 when OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) => "ARM64",
            Architecture.Arm64 => throw new PlatformNotSupportedException(
                "ARM64 VST3 hosting requires Windows 11; existing audio effects remain available."),
            _ => throw new PlatformNotSupportedException(
                $"VST3 hosting is not available for {RuntimeInformation.ProcessArchitecture}."),
        };
        string path = Path.Combine(AppContext.BaseDirectory, "Native", architecture, "InkMotionVst3Host.exe");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException($"The VST3 host worker for {architecture} is not installed.", path);
    }

    internal static ProcessStartInfo CreateStartInfo(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = GetWorkerPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}

public sealed class Vst3HostedSampleProvider : ISampleProvider, IDisposable
{
    private const int MaximumBlockFrames = 8192;
    private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(2);
    private readonly ISampleProvider _source;
    private readonly Process _process;
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly object _ioLock = new();
    private string? _failureMessage;
    private long _tailFramesRemaining;
    private bool _workerTerminated;
    private bool _disposed;

    internal Vst3HostedSampleProvider(
        ISampleProvider source,
        Vst3PluginDescription plugin,
        IReadOnlyDictionary<uint, double>? parameterValues,
        bool preserveTail)
    {
        _source = source;
        if (source.WaveFormat.Channels is < 1 or > 2)
        {
            throw new NotSupportedException("VST3 inserts currently support mono and stereo audio.");
        }

        _process = Process.Start(Vst3Host.CreateStartInfo(
            "--worker",
            Path.GetFullPath(plugin.ModulePath),
            plugin.ClassId,
            source.WaveFormat.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            source.WaveFormat.Channels.ToString(System.Globalization.CultureInfo.InvariantCulture),
            MaximumBlockFrames.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            ?? throw new InvalidOperationException("The VST3 host worker could not be started.");
        _input = _process.StandardInput.BaseStream;
        _output = _process.StandardOutput.BaseStream;
        _ = _process.StandardError.ReadToEndAsync();
        try
        {
            ReadStatus(_output);
            WritePacket(_input, [5]);
            ReadStatus(_output);
            uint tailSamples = ReadUInt32(_output);
            HasInfiniteTail = tailSamples == uint.MaxValue;
            TailDurationSeconds = HasInfiniteTail ? 0 : tailSamples / (double)source.WaveFormat.SampleRate;
            _tailFramesRemaining = preserveTail && !HasInfiniteTail ? tailSamples : 0;
            if (parameterValues is not null)
            {
                foreach ((uint id, double value) in parameterValues)
                {
                    SetParameter(id, value);
                }
            }
        }
        catch
        {
            TerminateWorker();
            throw;
        }
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public string? FailureMessage => Volatile.Read(ref _failureMessage);

    public double TailDurationSeconds { get; }

    public bool HasInfiniteTail { get; }

    public IReadOnlyList<Vst3Parameter> GetParameters()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_ioLock)
        {
            WritePacket(_input, [2]);
            ReadStatus(_output);
            uint count = ReadUInt32(_output);
            if (count > 10000)
            {
                throw new InvalidDataException("The VST3 host returned an invalid parameter count.");
            }

            var parameters = new Vst3Parameter[count];
            for (int index = 0; index < parameters.Length; index++)
            {
                uint id = ReadUInt32(_output);
                int steps = ReadInt32(_output);
                uint flags = ReadUInt32(_output);
                double value = ReadDouble(_output);
                string title = ReadString(_output);
                string units = ReadString(_output);
                parameters[index] = new Vst3Parameter(id, title, units, steps, flags, value);
            }

            return parameters;
        }
    }

    public void SetParameter(uint id, double normalizedValue)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!double.IsFinite(normalizedValue) || normalizedValue is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(normalizedValue));
        }

        lock (_ioLock)
        {
            Span<byte> packet = stackalloc byte[13];
            packet[0] = 3;
            BinaryPrimitives.WriteUInt32LittleEndian(packet[1..], id);
            BinaryPrimitives.WriteInt64LittleEndian(packet[5..], BitConverter.DoubleToInt64Bits(normalizedValue));
            WritePacket(_input, packet);
            ReadStatus(_output);
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ObjectDisposedException.ThrowIf(_disposed, this);
        int read = _source.Read(buffer, offset, count);
        if (read == 0 && _tailFramesRemaining > 0 && FailureMessage is null)
        {
            int frames = (int)Math.Min(count / WaveFormat.Channels, _tailFramesRemaining);
            if (frames == 0)
            {
                return 0;
            }

            read = frames * WaveFormat.Channels;
            Array.Clear(buffer, offset, read);
            _tailFramesRemaining -= frames;
        }

        if (read <= 0 || FailureMessage is not null)
        {
            return read;
        }

        int channels = WaveFormat.Channels;
        if (read % channels != 0)
        {
            Fail("The audio source returned an incomplete sample frame; the VST3 insert was bypassed.");
            return read;
        }

        try
        {
            lock (_ioLock)
            {
                int processedSamples = 0;
                while (processedSamples < read)
                {
                    int frames = Math.Min(MaximumBlockFrames, (read - processedSamples) / channels);
                    ProcessBlock(buffer, offset + processedSamples, frames, channels);
                    processedSamples += frames * channels;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
            InvalidOperationException or TimeoutException or OperationCanceledException or
            ObjectDisposedException or Win32Exception)
        {
            Fail($"The VST3 insert was bypassed: {exception.Message}");
        }

        return read;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_ioLock)
        {
            if (_workerTerminated)
            {
                return;
            }

            try
            {
                WritePacket(_input, [4]);
                ReadStatus(_output);
                if (!_process.WaitForExit((int)IoTimeout.TotalMilliseconds))
                {
                    TerminateWorker();
                    return;
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or
                InvalidOperationException or TimeoutException or OperationCanceledException or ObjectDisposedException)
            {
                TerminateWorker();
                return;
            }

            _process.Dispose();
        }
    }

    private void ProcessBlock(float[] buffer, int offset, int frames, int channels)
    {
        int sampleCount = frames * channels;
        byte[] packet = new byte[5 + sampleCount * sizeof(float)];
        packet[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), (uint)frames);
        for (int index = 0; index < sampleCount; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                packet.AsSpan(5 + index * sizeof(float)),
                BitConverter.SingleToInt32Bits(buffer[offset + index]));
        }

        WritePacket(_input, packet);
        ReadStatus(_output);
        byte[] samples = ReadExact(_output, sampleCount * sizeof(float));
        for (int index = 0; index < sampleCount; index++)
        {
            int bits = BinaryPrimitives.ReadInt32LittleEndian(samples.AsSpan(index * sizeof(float)));
            buffer[offset + index] = BitConverter.Int32BitsToSingle(bits);
        }
    }

    private void Fail(string message)
    {
        if (Interlocked.CompareExchange(ref _failureMessage, message, null) is null)
        {
            TerminateWorker();
        }
    }

    private void TerminateWorker()
    {
        lock (_ioLock)
        {
            if (_workerTerminated)
            {
                return;
            }

            _workerTerminated = true;
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                _process.Dispose();
            }
        }
    }

    private static void ReadStatus(Stream stream)
    {
        byte status = ReadExact(stream, 1)[0];
        if (status == 0)
        {
            return;
        }

        if (status != 1)
        {
            throw new InvalidDataException("The VST3 host returned an unknown response status.");
        }

        uint length = ReadUInt32(stream);
        if (length > 1024 * 1024)
        {
            throw new InvalidDataException("The VST3 host returned an oversized error message.");
        }

        throw new InvalidOperationException(Encoding.UTF8.GetString(ReadExact(stream, (int)length)));
    }

    private static uint ReadUInt32(Stream stream) =>
        BinaryPrimitives.ReadUInt32LittleEndian(ReadExact(stream, sizeof(uint)));

    private static int ReadInt32(Stream stream) =>
        BinaryPrimitives.ReadInt32LittleEndian(ReadExact(stream, sizeof(int)));

    private static double ReadDouble(Stream stream) =>
        BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(ReadExact(stream, sizeof(long))));

    private static string ReadString(Stream stream)
    {
        uint length = ReadUInt32(stream);
        if (length > 1024 * 1024)
        {
            throw new InvalidDataException("The VST3 host returned an oversized parameter string.");
        }

        return Encoding.UTF8.GetString(ReadExact(stream, (int)length));
    }

    private static byte[] ReadExact(Stream stream, int length)
    {
        byte[] buffer = new byte[length];
        int offset = 0;
        using var timeout = new CancellationTokenSource(IoTimeout);
        while (offset < length)
        {
            int read = stream.ReadAsync(buffer.AsMemory(offset), timeout.Token).AsTask().GetAwaiter().GetResult();
            if (read == 0)
            {
                throw new EndOfStreamException("The VST3 host worker closed its output stream.");
            }

            offset += read;
        }

        return buffer;
    }

    private static void WritePacket(Stream stream, ReadOnlySpan<byte> packet)
    {
        using var timeout = new CancellationTokenSource(IoTimeout);
        stream.WriteAsync(packet.ToArray(), timeout.Token).AsTask().GetAwaiter().GetResult();
        stream.FlushAsync(timeout.Token).GetAwaiter().GetResult();
    }
}
