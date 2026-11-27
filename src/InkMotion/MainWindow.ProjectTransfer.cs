using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace InkMotion;

public sealed partial class MainWindow
{
    private bool PrepareProjectTransfer()
    {
        if (_compoundContexts.Count > 0 || _switchingCompound || IsAudioRecordingInProgress ||
            _screenRecorderWindow?.IsRecording == true)
        {
            StatusText.Text = "Return to the main timeline and stop recording before transferring a project.";
            return false;
        }
        PausePlayback();
        FinishActiveEdit();
        return true;
    }

    private async void PackageProject_Click(object sender, RoutedEventArgs e)
    {
        if (!PrepareProjectTransfer()) return;
        var picker = new FileSavePicker { SuggestedFileName = "Project", DefaultFileExtension = ProjectPackage.Extension };
        picker.FileTypeChoices.Add("InkMotion project with linked media", [ProjectPackage.Extension]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try
        {
            EditorProject snapshot = CreateProjectSnapshot();
            string directory = _projectFile is null ? Environment.CurrentDirectory : Path.GetDirectoryName(_projectFile.Path)!;
            StatusText.Text = "Packaging linked media…";
            await Task.Run(() => ProjectPackage.Create(snapshot, file.Path, directory));
            StatusText.Text = $"Package saved · {file.Name}. Open it to extract into a new folder; the current project is unchanged.";
        }
        catch (Exception ex) when (IsProjectFileException(ex))
        {
            StatusText.Text = $"Could not package project: {ex.Message}";
        }
    }

    private async void ExportPictureTimeline_Click(object sender, RoutedEventArgs e)
    {
        if (!PrepareProjectTransfer()) return;
        try
        {
            EditorProject snapshot = CreateProjectSnapshot();
            var metadata = new Dictionary<string, PictureMediaInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in snapshot.VideoTracks.Where(t => t.Kind == TimelineTrackKind.Video)
                .SelectMany(t => t.Clips).Select(c => c.SourcePath).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                StorageFile source = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
                var properties = await source.Properties.GetVideoPropertiesAsync();
                var extra = await source.Properties.RetrievePropertiesAsync(["System.Video.FrameRate"]);
                if (!extra.TryGetValue("System.Video.FrameRate", out object? value) || value is not uint fps || fps == 0)
                    throw new InvalidDataException($"Windows could not determine the source frame rate: {source.Name}");
                metadata[source.Path] = new(fps / 1000.0, (int)properties.Width, (int)properties.Height);
            }
            PictureTimeline timeline = PictureInterchange.FromProject(snapshot, metadata);
            var notice = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Export picture cuts only?",
                Content = "FCP 7 XML / CMX 3600 EDL transfer picture cuts, gaps and source trims only. Embedded audio is omitted and will be muted on import. Proxy selection, clip/track names and editor metadata are not transferred. EDL also omits canvas size and requires one track at 24/30 fps NDF. Unsupported effects and other timeline material stop export. Use a package for a complete project.",
                PrimaryButtonText = "Choose export file",
                CloseButtonText = "Cancel",
            };
            if (await notice.ShowAsync() != ContentDialogResult.Primary) return;
            var picker = new FileSavePicker { SuggestedFileName = "Picture edit", DefaultFileExtension = ".xml" };
            picker.FileTypeChoices.Add("Final Cut Pro 7 XML (xmeml v5), picture cuts", [".xml"]);
            picker.FileTypeChoices.Add("CMX 3600 EDL, picture cuts", [".edl"]);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            StorageFile? file = await picker.PickSaveFileAsync();
            if (file is null) return;
            string text = file.FileType.Equals(".edl", StringComparison.OrdinalIgnoreCase)
                ? PictureInterchange.WriteEdl(timeline) : PictureInterchange.WriteXml(timeline);
            if (timeline.Tracks.SelectMany(t => t.Cuts).Any(c => c.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The export would overwrite source media.");
            await FileIO.WriteTextAsync(file, text);
            StatusText.Text = $"Picture cuts exported · {file.Name}. No audio or editor metadata was transferred.";
        }
        catch (Exception ex) when (IsProjectFileException(ex) || ex is System.Xml.XmlException or OverflowException)
        {
            StatusText.Text = $"Could not export picture timeline: {ex.Message}";
        }
    }

    private async void ImportPictureTimeline_Click(object sender, RoutedEventArgs e)
    {
        if (!PrepareProjectTransfer()) return;
        StorageFile? file = await PickTimelineFileAsync([".xml", ".edl"]);
        if (file is null) return;
        try
        {
            var rateSelector = new ComboBox { ItemsSource = new[] { 24, 30 }, SelectedItem = (int)_frameRate };
            if (rateSelector.SelectedIndex < 0) rateSelector.SelectedIndex = 0;
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(new TextBlock
            {
                Text = "This replaces the current project. Save any changes first. Only plain picture cuts are imported; embedded audio is muted. XML must be a single FCP 7 sequence (not FCPXML). EDL has no frame-rate metadata: choose its actual NDF frame rate below. EDL file-name-only references are resolved beside the EDL; missing media remains offline for relinking. Effects, transitions, drop-frame, retiming and other unsupported material are rejected.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 500,
            });
            bool edl = file.FileType.Equals(".edl", StringComparison.OrdinalIgnoreCase);
            if (edl) panel.Children.Add(rateSelector);
            var notice = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Import picture timeline?",
                Content = panel,
                PrimaryButtonText = "Replace project",
                CloseButtonText = "Cancel",
            };
            if (await notice.ShowAsync() != ContentDialogResult.Primary) return;
            string before = EditorProjectSerialiser.Serialise(CreateProjectSnapshot());
            if (new FileInfo(file.Path).Length > 64 * 1024 * 1024)
                throw new InvalidDataException("Interchange file exceeds 64 MiB.");
            string text = await FileIO.ReadTextAsync(file);
            PictureTimeline timeline = edl
                ? PictureInterchange.ReadEdl(text, (int)rateSelector.SelectedItem, Path.GetDirectoryName(file.Path)!)
                : PictureInterchange.ReadXml(text);
            EditorProject project = PictureInterchange.ToProject(timeline);
            if (before != EditorProjectSerialiser.Serialise(CreateProjectSnapshot()))
                throw new InvalidDataException("The project changed during import; import again.");
            ClearEditingClipboard();
            ClearHistory();
            RestoreProjectState(project);
            await LoadAudioTracksAsync(project.AudioTracks);
            _projectFile = null;
            UnsavedIndicator.Visibility = Visibility.Visible;
            string? warning = await LoadTimelineSourcesAsync();
            StatusText.Text = $"Picture cuts imported · {file.Name}. Embedded audio is muted; save as an InkMotion project." +
                (warning is null ? "" : $" {warning}");
        }
        catch (Exception ex) when (IsProjectFileException(ex) || ex is System.Xml.XmlException or OverflowException)
        {
            StatusText.Text = $"Could not import picture timeline: {ex.Message}";
        }
    }
}
