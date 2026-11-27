using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.Graphics.Imaging;

namespace InkMotion;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _colourImportCancellation;
    private bool _colourImportClosed;

    private async void BrowseColourTool_Click(object sender, RoutedEventArgs e)
    {
        StorageFile? file = await PickTimelineFileAsync([".exe"]);
        if (file is null) return;
        if (sender is FrameworkElement { Tag: "raw" }) RawToolPath.Text = file.Path;
        else OcioToolPath.Text = file.Path;
    }

    private async void ImportAcesStill_Click(object sender, RoutedEventArgs e) =>
        await ImportColourStillAsync(raw: false);

    private async void ImportRawStill_Click(object sender, RoutedEventArgs e) =>
        await ImportColourStillAsync(raw: true);

    private async Task ImportColourStillAsync(bool raw)
    {
        if (_colourImportCancellation is not null) return;
        if (_projectColour is not null)
        {
            StatusText.Text = "Colour-managed still imports create SDR editing copies. Use an SDR project.";
            return;
        }
        PausePlayback();
        FinishActiveEdit();
        StorageFile? file = await PickTimelineFileAsync(raw ? LocalColourImport.RawExtensions : [".exr"]);
        if (file is null || _colourImportClosed) return;
        string before = EditorProjectSerialiser.Serialise(CreateProjectSnapshot());
        int frame = PlayheadFrameNumber;
        string executable = (raw ? RawToolPath.Text : OcioToolPath.Text).Trim();
        string inputSpace = AcesInputSpace.SelectedIndex == 1 ? "ACES2065-1" : "ACEScg";
        bool cameraWhiteBalance = RawWhiteBalance.SelectedIndex != 1;
        double exposure = RawDevelopmentExposure.Value;
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InkMotion", "ImportedMedia");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        _colourImportCancellation = timeout;
        ColourImportControls.IsEnabled = false;
        CancelColourImportButton.IsEnabled = true;
        StatusText.Text = raw ? "Developing the RAW sensor image locally. The source remains unchanged." :
            "Applying the local ACES display transform. The source remains unchanged.";
        PreparedColourStill? prepared = null;
        bool added = false;
        try
        {
            prepared = await Task.Run(() => raw
                ? LocalColourImport.PrepareRaw(file.Path, executable, directory, cameraWhiteBalance, exposure, timeout.Token)
                : LocalColourImport.PrepareAces(file.Path, executable, directory, inputSpace, timeout.Token));
            if (_colourImportClosed) return;
            StorageFile result = await StorageFile.GetFileFromPathAsync(prepared.Path);
            if (_colourImportClosed) return;
            using (var stream = await result.OpenAsync(FileAccessMode.Read))
            {
                var decoder = await BitmapDecoder.CreateAsync(stream);
                if ((long)decoder.PixelWidth * decoder.PixelHeight > LocalColourImport.MaximumPixels)
                    throw new InvalidDataException("The editing copy exceeds 64 megapixels.");
            }
            timeout.Token.ThrowIfCancellationRequested();
            if (_colourImportClosed) return;
            using (var bitmap = await CanvasBitmap.LoadAsync(CompositionSurface.Device, prepared.Path))
            {
                if ((long)bitmap.SizeInPixels.Width * bitmap.SizeInPixels.Height > LocalColourImport.MaximumPixels)
                    throw new InvalidDataException("The editing copy exceeds 64 megapixels.");
            }
            timeout.Token.ThrowIfCancellationRequested();
            if (_colourImportClosed) return;
            if (!CanApplyLanBackgroundEdit()) return;
            if (before != EditorProjectSerialiser.Serialise(CreateProjectSnapshot()) || frame != PlayheadFrameNumber)
                throw new InvalidOperationException("The project or playhead changed during conversion. Import again.");
            RegisterTimelineSource(result);
            AddTimelineClip(TimelineTrackKind.Image, new TimelineClipProject
            {
                SourcePath = result.Path, StartFrame = frame, ColourSourceDescription = prepared.Description,
            }, raw ? "Added developed RAW still as an SDR editing copy." : "Added ACES display-referred SDR editing copy.");
            added = true;
        }
        catch (OperationCanceledException)
        {
            if (!_colourImportClosed) StatusText.Text = "Colour import cancelled or its five-minute limit was reached.";
        }
        catch (Exception ex) when (IsFileAccessException(ex) ||
            ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            if (!_colourImportClosed) StatusText.Text = $"Colour import failed: {ex.Message}";
        }
        finally
        {
            if (!added && prepared is not null)
            {
                try { File.Delete(prepared.Path); }
                catch (Exception ex) when (IsFileAccessException(ex))
                {
                    System.Diagnostics.Trace.TraceError($"Could not remove unused colour copy {prepared.Path}: {ex.Message}");
                    if (!_colourImportClosed) StatusText.Text += $" Could not remove unused editing copy: {prepared.Path}: {ex.Message}";
                }
            }
            _colourImportCancellation = null;
            if (!_colourImportClosed)
            {
                ColourImportControls.IsEnabled = true;
                CancelColourImportButton.IsEnabled = false;
            }
        }
    }

    private void CancelColourImport_Click(object sender, RoutedEventArgs e) => _colourImportCancellation?.Cancel();
}
