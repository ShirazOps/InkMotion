using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Storage;

namespace InkMotion;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _colourImportCancellation;
    private bool _colourImportClosed;

    private async void BrowseColourTool_Click(object sender, RoutedEventArgs e)
    {
        StorageFile? file = await PickTimelineFileAsync([".exe"]);
        if (file is not null) OcioToolPath.Text = file.Path;
    }

    private async void ImportAcesStill_Click(object sender, RoutedEventArgs e)
    {
        if (_colourImportCancellation is not null) return;
        if (_projectColour is not null)
        {
            StatusText.Text = "ACES display imports create SDR editing copies. Use an SDR project.";
            return;
        }
        PausePlayback();
        FinishActiveEdit();
        StorageFile? file = await PickTimelineFileAsync([".exr"]);
        if (file is null || _colourImportClosed) return;
        string before = EditorProjectSerialiser.Serialise(CreateProjectSnapshot());
        int frame = PlayheadFrameNumber;
        string executable = OcioToolPath.Text.Trim();
        string inputSpace = AcesInputSpace.SelectedIndex == 1 ? "ACES2065-1" : "ACEScg";
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InkMotion", "ImportedMedia");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        _colourImportCancellation = timeout;
        ColourImportControls.IsEnabled = false;
        CancelColourImportButton.IsEnabled = true;
        StatusText.Text = "Applying the local ACES display transform. The source remains unchanged.";
        PreparedColourStill? prepared = null;
        bool added = false;
        try
        {
            prepared = await Task.Run(() => LocalColourImport.PrepareAces(
                file.Path, executable, directory, inputSpace, timeout.Token));
            if (_colourImportClosed) return;
            using (var bitmap = await CanvasBitmap.LoadAsync(CompositionSurface.Device, prepared.Path))
            {
                if ((long)bitmap.SizeInPixels.Width * bitmap.SizeInPixels.Height > LocalColourImport.MaximumPixels)
                    throw new InvalidDataException("The editing copy exceeds 64 megapixels.");
            }
            timeout.Token.ThrowIfCancellationRequested();
            if (_colourImportClosed) return;
            if (before != EditorProjectSerialiser.Serialise(CreateProjectSnapshot()) || frame != PlayheadFrameNumber)
                throw new InvalidOperationException("The project or playhead changed during conversion. Import again.");
            StorageFile result = await StorageFile.GetFileFromPathAsync(prepared.Path);
            timeout.Token.ThrowIfCancellationRequested();
            if (_colourImportClosed || before != EditorProjectSerialiser.Serialise(CreateProjectSnapshot()) ||
                frame != PlayheadFrameNumber)
                throw new InvalidOperationException("The project or playhead changed during import. Import again.");
            RegisterTimelineSource(result);
            AddTimelineClip(TimelineTrackKind.Image, new TimelineClipProject
            {
                SourcePath = result.Path, StartFrame = frame, ColourSourceDescription = prepared.Description,
            }, "Added ACES display-referred SDR editing copy.");
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
            if (!added && prepared is not null) File.Delete(prepared.Path);
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
