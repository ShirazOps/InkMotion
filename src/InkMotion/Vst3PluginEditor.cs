using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace InkMotion;

internal sealed class Vst3PluginEditor : IDisposable
{
    private readonly Vst3PluginDescription _plugin;
    private readonly IReadOnlyDictionary<uint, double> _parameterValues;
    private Process? _process;
    private bool _disposed;

    public Vst3PluginEditor(
        Vst3PluginDescription plugin,
        IReadOnlyDictionary<uint, double> parameterValues)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(parameterValues);
        _plugin = plugin;
        _parameterValues = new Dictionary<uint, double>(parameterValues);
    }

    public event Action<uint, double>? ParameterChanged;

    public event Action<string>? StatusChanged;

    public event Action? Exited;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process is not null)
        {
            throw new InvalidOperationException("The VST3 editor worker has already been started.");
        }

        var process = new Process
        {
            StartInfo = Vst3Host.CreateStartInfo(
                "--editor",
                Path.GetFullPath(_plugin.ModulePath),
                _plugin.ClassId),
            EnableRaisingEvents = true,
        };
        process.OutputDataReceived += Process_OutputDataReceived;
        process.ErrorDataReceived += Process_ErrorDataReceived;
        process.Exited += Process_Exited;
        _process = process;
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("The VST3 editor worker could not be started.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.StandardInput.AutoFlush = true;
            process.StandardInput.WriteLine(string.Join(';', _parameterValues
                .OrderBy(parameter => parameter.Key)
                .Select(parameter =>
                    $"{parameter.Key}={parameter.Value.ToString("R", CultureInfo.InvariantCulture)}")));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void SetParameter(uint id, double value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!double.IsFinite(value) || value is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        (_process ?? throw new InvalidOperationException("The VST3 editor worker is not running."))
            .StandardInput.WriteLine(
            $"{id}={value.ToString("R", CultureInfo.InvariantCulture)}");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Process? process = _process;
        if (process is null)
        {
            return;
        }

        try
        {
            process.StandardInput.Dispose();
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(2000))
                {
                    process.Kill();
                    process.WaitForExit(2000);
                }
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        finally
        {
            process.Dispose();
            _process = null;
        }
    }

    private void Process_OutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
        {
            return;
        }

        string[] fields = e.Data.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length == 3 && fields[0] == "P" &&
            uint.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint id) &&
            double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
            double.IsFinite(value) && value is >= 0 and <= 1)
        {
            ParameterChanged?.Invoke(id, value);
            return;
        }

        if (fields.Length >= 2 && fields[0] == "S")
        {
            StatusChanged?.Invoke(fields[1]);
        }
        else if (fields.Length >= 2 && fields[0] == "E")
        {
            StatusChanged?.Invoke($"Native editor could not open: {string.Join(' ', fields.Skip(1))}");
        }
    }

    private void Process_ErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.Data))
        {
            StatusChanged?.Invoke(e.Data);
        }
    }

    private void Process_Exited(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        int exitCode;
        try
        {
            exitCode = _process?.ExitCode ?? 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return;
        }

        if (exitCode != 0)
        {
            StatusChanged?.Invoke($"The VST3 editor worker exited with code {exitCode}.");
        }

        Exited?.Invoke();
    }
}
