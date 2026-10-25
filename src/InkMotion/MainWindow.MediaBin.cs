using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace InkMotion;

public sealed partial class MainWindow
{
    private IReadOnlyList<ProjectMediaAsset> _mediaAssets = [];
    private IReadOnlyList<string> _mediaDiscoveryMatches = [];
    private CancellationTokenSource? _mediaDiscoveryCancellation;
    private bool _mediaBinClosed;
    private bool _updatingMediaBin;

    private void UpdateProjectMediaBin()
    {
        if (_mediaBinClosed || ProjectMediaBinList is null)
        {
            return;
        }

        ProjectMediaAsset? previous = (ProjectMediaBinList.SelectedItem as ComboBoxItem)?.Tag as ProjectMediaAsset;
        string? previousPath = previous?.Path;
        ProjectMediaKind? previousKind = previous?.Kind;
        _mediaAssets = ProjectMediaBin.Build(_videoTracks, _audioTracks.Select(track => track.Project).ToArray());
        _updatingMediaBin = true;
        ProjectMediaBinList.Items.Clear();
        foreach (ProjectMediaAsset asset in _mediaAssets)
        {
            ProjectMediaBinList.Items.Add(new ComboBoxItem { Content = asset.DisplayName, Tag = asset });
        }
        ProjectMediaBinList.SelectedItem = ProjectMediaBinList.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is ProjectMediaAsset asset &&
                asset.Kind == previousKind && string.Equals(asset.Path, previousPath, StringComparison.OrdinalIgnoreCase));
        _updatingMediaBin = false;
        ProjectMediaBin_SelectionChanged(ProjectMediaBinList, null!);
    }

    private void ProjectMediaBin_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingMediaBin || ProjectMediaBinList?.SelectedItem is not ComboBoxItem { Tag: ProjectMediaAsset asset })
        {
            FindMediaMatchesButton.IsEnabled = false;
            RelinkMediaMatchButton.IsEnabled = false;
            return;
        }

        FindMediaMatchesButton.IsEnabled = asset.IsMissing;
        ProjectMediaBinStatus.Text = asset.IsMissing
            ? $"Missing path: {asset.Path}"
            : $"Available path: {asset.Path}";
        ShowMediaMatchesForSelection();
    }

    private void ShowMediaMatchesForSelection()
    {
        MediaBinMatchesList.Items.Clear();
        if (ProjectMediaBinList.SelectedItem is not ComboBoxItem { Tag: ProjectMediaAsset asset } || !asset.IsMissing)
        {
            RelinkMediaMatchButton.IsEnabled = false;
            return;
        }

        string name = Path.GetFileName(asset.Path);
        foreach (string path in _mediaDiscoveryMatches.Where(path =>
            string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase) &&
            IsCompatibleMediaCandidate(asset.Kind, path)))
        {
            MediaBinMatchesList.Items.Add(new ComboBoxItem { Content = path, Tag = path });
        }
        RelinkMediaMatchButton.IsEnabled = MediaBinMatchesList.Items.Count > 0;
    }

    private void MediaBinMatch_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RelinkMediaMatchButton.IsEnabled =
            ProjectMediaBinList.SelectedItem is ComboBoxItem { Tag: ProjectMediaAsset { IsMissing: true } } &&
            MediaBinMatchesList.SelectedItem is ComboBoxItem { Tag: string };
    }

    private async void FindMediaMatches_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is null || _mediaBinClosed)
        {
            return;
        }

        _mediaDiscoveryCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _mediaDiscoveryCancellation = cancellation;
        string projectBeforeSearch = EditorProjectSerialiser.Serialise(CreateProjectSnapshot());
        FindMediaMatchesButton.IsEnabled = false;
        ProjectMediaBinStatus.Text = $"Searching {folder.Path} recursively for exact missing filenames...";
        try
        {
            MediaDiscoveryResult result = await Task.Run(
                () => ProjectMediaBin.FindUniqueMatches(_mediaAssets, folder.Path, cancellation.Token));
            if (cancellation.IsCancellationRequested || _mediaBinClosed ||
                !ReferenceEquals(_mediaDiscoveryCancellation, cancellation))
            {
                return;
            }
            if (projectBeforeSearch != EditorProjectSerialiser.Serialise(CreateProjectSnapshot()))
            {
                ProjectMediaBinStatus.Text = "The project changed during the search. Refresh the media bin and scan again.";
                return;
            }

            _mediaDiscoveryMatches = result.UniqueMatches;
            ShowMediaMatchesForSelection();
            ProjectMediaBinStatus.Text =
                $"Scanned {result.ScannedFiles:N0} supported media file(s). {result.UniqueMatches.Count:N0} unique filename match(es); " +
                $"{result.AmbiguousMatches:N0} missing asset(s) have duplicate filename matches and need manual relinking.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or
            ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            if (!cancellation.IsCancellationRequested && !_mediaBinClosed)
            {
                ProjectMediaBinStatus.Text = $"Media search failed: {ex.Message}";
                StatusText.Text = "Could not search for missing project media.";
            }
        }
        finally
        {
            if (!_mediaBinClosed && ReferenceEquals(_mediaDiscoveryCancellation, cancellation))
            {
                _mediaDiscoveryCancellation = null;
                FindMediaMatchesButton.IsEnabled =
                    ProjectMediaBinList.SelectedItem is ComboBoxItem { Tag: ProjectMediaAsset { IsMissing: true } };
            }
        }
    }

    private async void RelinkMediaMatch_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectMediaBinList.SelectedItem is not ComboBoxItem { Tag: ProjectMediaAsset asset } ||
            MediaBinMatchesList.SelectedItem is not ComboBoxItem { Tag: string replacement })
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        string before = EditorProjectSerialiser.Serialise(CreateProjectSnapshot());
        if (!asset.IsMissing || !string.Equals(Path.GetFileName(asset.Path), Path.GetFileName(replacement),
            StringComparison.OrdinalIgnoreCase) || !IsCompatibleMediaCandidate(asset.Kind, replacement))
        {
            ProjectMediaBinStatus.Text = "The missing asset or discovered replacement changed. Refresh and search again.";
            UpdateProjectMediaBin();
            return;
        }

        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(replacement);
            StorageFile? prepared = await PrepareMediaImportAsync(file);
            if (prepared is null)
            {
                ProjectMediaBinStatus.Text = StatusText.Text;
                return;
            }
            file = prepared;
            double duration = asset.Kind == ProjectMediaKind.Video ? await ProbeVideoDurationAsync(file) : 0;
            int channels = 0;
            if (asset.Kind == ProjectMediaKind.Audio)
            {
                (duration, channels) = await Task.Run(() =>
                {
                    using var reader = new NAudio.Wave.AudioFileReader(file.Path);
                    return (AudioSourcePosition.DurationSeconds(reader), reader.WaveFormat.Channels);
                });
            }

            if (_mediaBinClosed || before != EditorProjectSerialiser.Serialise(CreateProjectSnapshot()) ||
                ProjectMediaBinList.SelectedItem is not ComboBoxItem { Tag: ProjectMediaAsset currentAsset } ||
                !ReferenceEquals(currentAsset, asset) ||
                MediaBinMatchesList.SelectedItem is not ComboBoxItem { Tag: string currentReplacement } ||
                !string.Equals(currentReplacement, replacement, StringComparison.OrdinalIgnoreCase))
            {
                ProjectMediaBinStatus.Text = "The project or selection changed before relinking. Select the missing asset and match again.";
                return;
            }

            BeginEdit();
            int count;
            if (asset.Kind == ProjectMediaKind.Audio)
            {
                bool useReplacementEnd = MediaBinUseReplacementEnd.IsChecked == true;
                var durations = _audioTracks.Where(track => track.HasSource)
                    .ToDictionary(track => track.Project, track => track.SourceDurationSeconds);
                AudioTrackProject selected = _audioTracks.First(track =>
                    string.Equals(track.Project.AudioPath, asset.Path, StringComparison.OrdinalIgnoreCase)).Project;
                count = AudioSourceRelinking.RelinkMatchingSources(
                    _audioTracks.Select(track => track.Project).ToArray(), selected, file.Path,
                    duration, channels, durations, useReplacementEnd);
                MarkEditChanged();
                CommitEdit();
                ClearAudioSpectrum();
                AudioTrackRuntime[] affected = _audioTracks.Where(track =>
                    string.Equals(track.Project.AudioPath, file.Path, StringComparison.OrdinalIgnoreCase)).ToArray();
                foreach (AudioTrackRuntime track in affected)
                {
                    OpenAudioPlayer(track);
                    if (track.HasSource)
                    {
                        await LoadAudioWaveformAsync(track);
                    }
                }
                UpdateSelectedAudioTrack();
                RefreshAudioTracks();
            }
            else
            {
                if (asset.Kind == ProjectMediaKind.Video && duration <= 0)
                {
                    throw new InvalidOperationException("The replacement video duration could not be read.");
                }
                TimelineClipLocation selected = asset.TimelineUses.First();
                count = VideoEditTools.RelinkMatchingSources(
                    _videoTracks, selected.Track, selected.Clip, file.Path, duration);
                RegisterTimelineSource(file);
                MarkEditChanged();
                CommitEdit();
                RefreshVideoTimeline();
                SeekToCurrentFrame();
            }

            UpdateProjectMediaBin();
            ProjectMediaBinStatus.Text = $"{count} matching {asset.Kind.ToString().ToLowerInvariant()} source use(s) relinked to {file.Name}. Undo restores the previous links.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or NotSupportedException or FormatException or NAudio.MmException or
            System.Runtime.InteropServices.COMException)
        {
            CommitEdit();
            ProjectMediaBinStatus.Text = $"Could not relink discovered media: {ex.Message}";
            StatusText.Text = "Missing media was not fully relinked.";
            UpdateProjectMediaBin();
        }
    }

    private static bool IsCompatibleMediaCandidate(ProjectMediaKind kind, string path)
    {
        string extension = Path.GetExtension(path);
        return kind switch
        {
            ProjectMediaKind.Audio => new[] { ".mp3", ".wav", ".m4a", ".aac", ".wma", ".flac" }
                .Contains(extension, StringComparer.OrdinalIgnoreCase),
            ProjectMediaKind.Image => VideoTimelineEditing.ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase),
            ProjectMediaKind.Video => VideoFileTypes.SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase),
            _ => false,
        };
    }
}
