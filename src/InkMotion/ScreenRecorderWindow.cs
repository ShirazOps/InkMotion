using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkMotion;

public sealed partial class ScreenRecorderWindow : Window
{
    private readonly Func<string, Task> _open;
    private readonly Func<string, bool> _inUse;
    private readonly ObservableCollection<ScreenRegion> _regions = [];
    private readonly StackPanel _configuration = new() { Spacing = 12 };
    private readonly UserControl _configurationHost = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _storage = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _displays = new() { Header = "Connected display (physical desktop pixels)", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumberBox _left = new() { Header = "Left (X)", SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly NumberBox _top = new() { Header = "Top (Y)", SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly NumberBox _width = new() { Header = "Width", Minimum = 2, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly NumberBox _height = new() { Header = "Height", Minimum = 2, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly ComboBox _fps = new() { Header = "Frame rate", ItemsSource = new[] { 15, 24, 30 }, SelectedIndex = 2 };
    private readonly ComboBox _resolution = new() { Header = "Maximum output side (pixels)", ItemsSource = new[] { 1920, 3840 }, SelectedIndex = 0 };
    private readonly ComboBox _system = new() { Header = "System audio output device", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _microphone = new() { Header = "Microphone input device", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _camera = new() { Header = "Webcam", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _cursor = new() { Content = "Include mouse pointer", IsChecked = true };
    private readonly CheckBox _pip = new() { Content = "Place the separate webcam track as picture-in-picture when opened", IsChecked = true };
    private readonly Button _start = new() { Content = "Start recording", IsEnabled = false };
    private readonly Button _stop = new() { Content = "Stop and save", IsEnabled = false };
    private readonly ComboBox _library = new() { Header = "Saved / interrupted recordings", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly DispatcherQueueTimer _timer;
    private IReadOnlyList<RecordingDisplay> _connectedDisplays = [];
    private bool _allowClose;
    private bool _closingRequested;
    private bool _busy;
    private string? _directory;
    private ScreenRecordingManifest? _manifest;
    private CancellationTokenSource? _stopCapture;
    private readonly Stopwatch _clock = new();
    private Task? _run;
    private bool _selectingArea;

    public bool IsRecording => _busy;

    public ScreenRecorderWindow(Func<string, Task> open, Func<string, bool> inUse)
    {
        _open = open;
        _inUse = inUse;
        Title = "InkMotion screen recorder";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(850, 1000));
        var root = new Grid { Padding = new Thickness(24), RowSpacing = 16, RequestedTheme = ElementTheme.Dark,
            Background = new SolidColorBrush(ColorHelper.FromArgb(255, 20, 22, 28)) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var actions = Row(_start, _stop);
        root.Children.Add(actions);
        Grid.SetRow(_status, 1);
        root.Children.Add(_status);
        var body = new StackPanel { Spacing = 18 };
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);
        Content = root;
        _configurationHost.Content = _configuration;
        body.Children.Add(_configurationHost);
        _configuration.Children.Add(Heading("Screen areas"));
        _configuration.Children.Add(Hint("Select one or more displays, or enter regions using global physical-pixel coordinates (negative values are valid). Areas retain their relative desktop positions; unselected areas and monitor gaps are black. Review selections before starting."));
        _configuration.Children.Add(_displays);
        _configuration.Children.Add(Row(Button("Draw box (mouse / pen)", () => SelectAreaAsync(false)),
            Button("Draw freeform (mouse / pen)", () => SelectAreaAsync(true))));
        _configuration.Children.Add(Hint("Draw across screens, then use the selector's Use selection button or Enter. Box corners and edges resize the area. Freeform outlines close automatically; only their bounding box is saved, with unselected pixels black. Repeat to add another area."));
        _configuration.Children.Add(Row(Button("Add full display", () =>
        {
            if (_displays.SelectedItem is RecordingDisplay display) _regions.Add(display.Bounds);
        }), Button("Add all displays", () =>
        {
            _regions.Clear();
            foreach (RecordingDisplay display in _connectedDisplays) _regions.Add(display.Bounds);
        }), Button("Reset to entire screen", () =>
        {
            if (_displays.SelectedItem is not RecordingDisplay display)
                throw new InvalidOperationException("Select a connected display first.");
            _regions.Clear();
            _regions.Add(display.Bounds);
            SetCoordinates(display.Bounds);
        })));
        _configuration.Children.Add(Row(_left, _top, _width, _height));
        _configuration.Children.Add(Button("Add region", () =>
        {
            double[] values = [_left.Value, _top.Value, _width.Value, _height.Value];
            if (values.Any(value => !double.IsFinite(value) || value != Math.Truncate(value) || value < int.MinValue || value > int.MaxValue))
                throw new ArgumentException("Region coordinates and dimensions must be whole physical pixels.");
            var region = new ScreenRegion((int)values[0], (int)values[1], (int)values[2], (int)values[3]);
            ScreenCapturePlan.Create([region], _connectedDisplays.Select(display => display.Bounds).ToArray(), 1920, 30);
            _regions.Add(region);
        }));
        var selection = new ListView { ItemsSource = _regions, MaxHeight = 160, SelectionMode = ListViewSelectionMode.Single };
        selection.SelectionChanged += (_, _) =>
        {
            if (selection.SelectedItem is ScreenRegion region) SetCoordinates(region);
        };
        _configuration.Children.Add(selection);
        _configuration.Children.Add(Button("Remove selected region", () =>
        {
            if (selection.SelectedItem is ScreenRegion region) _regions.Remove(region);
        }));
        _configuration.Children.Add(Row(_fps, _resolution));
        _configuration.Children.Add(_cursor);
        _configuration.Children.Add(Heading("Optional separate sources"));
        _configuration.Children.Add(_system);
        _configuration.Children.Add(_microphone);
        _configuration.Children.Add(_camera);
        _configuration.Children.Add(_pip);
        _configuration.Children.Add(Hint("None means off. System audio captures everything played on the selected output. Microphone and camera require Windows privacy access. Use headphones to avoid echo. Audio sources and webcam remain separate editable tracks."));
        _configuration.Children.Add(Button("Refresh displays and devices", async () => await LoadDevicesAsync()));
        _configuration.Children.Add(Hint("Recording starts after a 3-second countdown. InkMotion is not excluded from screen capture: move or minimise it yourself. Use Stop and save here, or File > Screen recorder to return. This records the interactive desktop only; protected content, secure/UAC desktops, lock screens, HDR fidelity and exclusive fullscreen capture are not guaranteed. 15–30 fps is best-effort; slow frames are repeated to preserve timing. Display changes stop recording."));
        body.Children.Add(Heading("Durable recording library"));
        body.Children.Add(_storage);
        body.Children.Add(_library);
        body.Children.Add(Row(Button("Refresh library", RefreshLibrary), Button("Open / recover project", OpenSelectedAsync),
            Button("Open storage folder", async () =>
            {
                Directory.CreateDirectory(ScreenRecordingStore.Root);
                if (!await Windows.System.Launcher.LaunchFolderPathAsync(ScreenRecordingStore.Root))
                    throw new IOException("Windows could not open the recording folder.");
            })));
        body.Children.Add(Button("Delete selected recording…", DeleteSelectedAsync));
        body.Children.Add(Hint("Files are never automatically removed. Completed 5-second screen segments survive interruption; the unfinished MP4 and any unfinished device files remain for manual recovery. Deleting a recording breaks every project linked to those sources. Close active projects using it before deleting."));
        _displays.SelectionChanged += (_, _) =>
        {
            if (_displays.SelectedItem is not RecordingDisplay display) return;
            SetCoordinates(display.Bounds);
        };
        _start.Click += async (_, _) => await StartAsync();
        _stop.Click += (_, _) => RequestStop();
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => UpdateRecordingStatus();
        AppWindow.Closing += async (_, args) =>
        {
            if (_selectingArea) { args.Cancel = true; return; }
            if (_allowClose || !_busy) return;
            args.Cancel = true;
            await StopAndCloseAsync();
        };
        root.Loaded += async (_, _) =>
        {
            try { await LoadDevicesAsync(); RefreshLibrary(); }
            catch (Exception ex) when (IsCaptureException(ex)) { _status.Text = ex.Message; }
        };
    }

    private void SetCoordinates(ScreenRegion region)
    {
        _left.Value = region.X;
        _top.Value = region.Y;
        _width.Value = region.Width;
        _height.Value = region.Height;
    }

    private async Task SelectAreaAsync(bool freeform)
    {
        if (_selectingArea || _busy) return;
        if (_displays.SelectedItem is not RecordingDisplay display)
            throw new InvalidOperationException("Select a connected display first.");
        double[] previous = [_left.Value, _top.Value, _width.Value, _height.Value];
        _selectingArea = true;
        _configurationHost.IsEnabled = _start.IsEnabled = false;
        try
        {
            ScreenRegion? region = await ScreenRegionSelector.SelectAsync(DesktopRecordingFrames.Displays(),
                display.Bounds, freeform, SetCoordinates);
            if (region is null)
            {
                _left.Value = previous[0]; _top.Value = previous[1];
                _width.Value = previous[2]; _height.Value = previous[3];
                return;
            }
            ScreenCapturePlan.Create([region], _connectedDisplays.Select(d => d.Bounds).ToArray(), 1920, 30);
            _regions.Add(region);
            SetCoordinates(region);
            _status.Text = "Capture area added. Select another area or start recording.";
        }
        finally
        {
            _selectingArea = false;
            _configurationHost.IsEnabled = true;
            _start.IsEnabled = _connectedDisplays.Count > 0;
            Activate();
        }
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private static TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (UIElement child in children) row.Children.Add(child);
        return row;
    }
    private Button Button(string text, Action action) => Button(text, () => { action(); return Task.CompletedTask; });
    private Button Button(string text, Func<Task> action)
    {
        var button = new Button { Content = text };
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception ex) when (IsCaptureException(ex)) { _status.Text = ex.Message; }
        };
        return button;
    }
    private static bool IsCaptureException(Exception ex) => ex is IOException or UnauthorizedAccessException or
        InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or
        System.Runtime.InteropServices.COMException or NotSupportedException or NAudio.MmException or TimeoutException or
        OutOfMemoryException or System.Text.Json.JsonException;

    private sealed record RecordingEntry(string Directory, string Label)
    {
        public override string ToString() => Label;
    }

    private void RefreshLibrary()
    {
        Directory.CreateDirectory(ScreenRecordingStore.Root);
        var entries = new List<RecordingEntry>();
        long bytes = 0;
        foreach (string directory in Directory.EnumerateDirectories(ScreenRecordingStore.Root).OrderDescending())
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            long size = ScreenRecordingStore.StorageBytes(directory);
            bytes += size;
            string label;
            try
            {
                ScreenRecordingManifest manifest = ScreenRecordingStore.Read(directory);
                string status = manifest.Status is "Preparing" or "Recording" &&
                    (!_busy || directory != _directory) ? "Interrupted (unclosed session)" : manifest.Status;
                label = $"{manifest.CreatedUtc.ToLocalTime():g} · {status} · {size / 1048576d:0.0} MiB" +
                    $" · {manifest.Media.Count(media => media.Finalised)} completed source(s)";
            }
            catch (Exception ex) when (IsCaptureException(ex) || ex is System.Text.Json.JsonException)
            {
                label = $"{Path.GetFileName(directory)} · recovery metadata error: {ex.Message}";
            }
            entries.Add(new(directory, label));
        }
        _library.ItemsSource = entries;
        _library.SelectedIndex = entries.Count > 0 ? 0 : -1;
        _storage.Text = $"{bytes / 1073741824d:0.00} GiB in {ScreenRecordingStore.Root}";
    }

    private async Task OpenSelectedAsync()
    {
        if (_busy) throw new InvalidOperationException("Stop recording before opening a saved project.");
        if (_library.SelectedItem is not RecordingEntry entry) return;
        string path = Path.Combine(entry.Directory, "Recording.inkmotion");
        ScreenRecordingManifest manifest = ScreenRecordingStore.Read(entry.Directory);
        if (!File.Exists(path))
        {
            await ScreenRecordingStore.RecoverAsync(entry.Directory, manifest);
            path = ScreenRecordingStore.SaveProject(entry.Directory, manifest);
        }
        await _open(path);
        _status.Text = "The recording project is saved. Incomplete sources, if any, remain in its folder." +
            (manifest.Warnings.Count > 0 ? $"\n{string.Join("\n", manifest.Warnings.TakeLast(4))}" : "");
    }

    private async Task DeleteSelectedAsync()
    {
        if (_busy) throw new InvalidOperationException("Stop recording before deleting saved sources.");
        if (_library.SelectedItem is not RecordingEntry entry) return;
        if (_inUse(entry.Directory)) throw new InvalidOperationException("This recording is in use by the editor. Open another project before deleting it.");
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = "Permanently delete this recording?",
            Content = $"Delete every captured source, recovery metadata and saved project in:\n{entry.Directory}\n\nOther projects linking to these files will lose their media. This cannot be undone.",
            PrimaryButtonText = "Delete recording", CloseButtonText = "Keep recording",
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        ScreenRecordingStore.Delete(entry.Directory);
        RefreshLibrary();
    }

    public async Task StopAndCloseAsync()
    {
        _closingRequested = true;
        RequestStop();
        if (_run is not null) await _run;
        _allowClose = true;
        _timer.Stop();
        Close();
    }

    private void RequestStop()
    {
        _stopCapture?.Cancel();
        _clock.Stop();
        if (_timer.IsRunning && _inputs is not null) _stoppingInputs ??= _inputs.StopAsync();
    }
}
