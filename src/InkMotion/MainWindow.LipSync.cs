using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkMotion;

public sealed partial class MainWindow
{
    private void UpdateLipSyncReadout()
    {
        if (InkLipSyncReadout is null) return;
        InkLipSync? sync = CurrentInkLayer.LipSync;
        InkLipSyncReadout.Text = sync is null ? "" : !sync.Enabled ? "Lip-sync preview is off. Use Lip-sync… to edit mouth drawings and cues." :
            InkLipSyncs.IsSourceCurrent(sync, new EditorProject { FrameRate = _frameRate, AudioTracks = _audioTracks.Select(track => track.Project).ToList() })
                ? $"Lip-sync: {sync.Cues.Count} acoustic mouth cues. Review and correct them in Lip-sync…"
                : "Lip-sync audio placement, trim, tempo or project frame rate changed. Animation is suspended; regenerate cues in Lip-sync…";
    }

    private async void LipSync_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentInkLayer.IsLocked) { StatusText.Text = "Unlock the mouth layer before editing lip-sync."; return; }
        PausePlayback(); FinishActiveEdit();
        InkLipSync draft = CurrentInkLayer.LipSync is { } existing ? InkLipSyncs.Clone(existing) : InkLipSyncs.Starter();
        var enabled = new CheckBox { Content = "Preview lip-sync (replaces this layer with its timed mouth drawing)", IsChecked = draft.Enabled };
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Width = 520, Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "On-device, model-free acoustic lip-sync drafts mouth openings and frication from the selected audio. It does not recognise phonemes or words. Review and correct cues; no models, weights or audio are downloaded or uploaded.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(enabled);
        var tabs = new Pivot();
        var generatePanel = new StackPanel { Spacing = 8 };
        var mouthsPanel = new StackPanel { Spacing = 8 };
        var cuesPanel = new StackPanel { Spacing = 8 };
        tabs.Items.Add(new PivotItem { Header = "Analyse audio", Content = generatePanel });
        tabs.Items.Add(new PivotItem { Header = "Mouth drawings", Content = mouthsPanel });
        tabs.Items.Add(new PivotItem { Header = "Timed cues", Content = cuesPanel });
        panel.Children.Add(tabs); panel.Children.Add(message);
        NumberBox Number(string header, double value, double min, double max) => new()
        { Header = header, Value = value, Minimum = min, Maximum = max, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var audio = new ComboBox { Header = "Audio clip (uses its placement, source trim and tempo)", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (AudioTrackRuntime track in _audioTracks.Where(track => track.HasSource))
            audio.Items.Add(new ComboBoxItem { Content = $"{Path.GetFileName(track.Project.AudioPath)} · frame {track.Project.StartFrame}", Tag = track.Project });
        audio.SelectedIndex = audio.Items.Count > 0 ? 0 : -1;
        var silence = Number("Silence threshold (dBFS)", -40, -80, -10);
        var generate = new Button { Content = "Analyse and replace timed cues", IsEnabled = audio.Items.Count > 0 };
        var cancel = new Button { Content = "Cancel analysis", IsEnabled = false };
        generatePanel.Children.Add(audio); generatePanel.Children.Add(silence);
        generatePanel.Children.Add(generate); generatePanel.Children.Add(cancel);
        generatePanel.Children.Add(new TextBlock { Text = "Choose a dialogue clip with little background noise. Changes are frame-aligned, with two-frame smoothing except silence. Drawings are retained when regenerating. Audio edits suspend the old animation until it is regenerated. Several characters can use separate mouth layers and clips.", TextWrapping = TextWrapping.Wrap });
        var mouth = new ComboBox { Header = "Mouth drawing", ItemsSource = Enum.GetValues<InkViseme>(), SelectedIndex = 0 };
        var preview = new CanvasControl { Width = 400, Height = 120 };
        preview.Draw += (canvas, args) =>
        {
            args.DrawingSession.Clear(Microsoft.UI.Colors.DimGray);
            TimelineCompositor.DrawInkStrokes(args.DrawingSession, draft.Mouths[(InkViseme)mouth.SelectedItem],
                point => TimelineCompositor.ToColour(point.BrushColour), 400, 120, 0.5f);
        };
        var x = Number("Mouth centre X (0-1)", 0.5, 0, 1);
        var y = Number("Mouth centre Y (0-1)", 0.6, 0, 1);
        var width = Number("Mouth width (canvas fraction)", 0.12, 0.001, 1);
        var height = Number("Mouth height (canvas fraction)", 0.08, 0.001, 1);
        var geometry = new Grid { ColumnSpacing = 8, RowSpacing = 4 };
        geometry.ColumnDefinitions.Add(new()); geometry.ColumnDefinitions.Add(new());
        geometry.RowDefinitions.Add(new()); geometry.RowDefinitions.Add(new());
        FrameworkElement[] fields = [x, y, width, height];
        for (int index = 0; index < fields.Length; index++)
        { Grid.SetRow(fields[index], index / 2); Grid.SetColumn(fields[index], index % 2); geometry.Children.Add(fields[index]); }
        mouthsPanel.Children.Add(mouth); mouthsPanel.Children.Add(preview); mouthsPanel.Children.Add(geometry);
        var starter = new Button { Content = "Replace library with starter mouths at these bounds" };
        var capture = new Button { Content = "Capture current cel's original layer drawing as this mouth" };
        mouthsPanel.Children.Add(starter); mouthsPanel.Children.Add(capture);
        mouthsPanel.Children.Add(new TextBlock { Text = "Edit selected mouth saves this draft with preview off and loads that mouth into the current cel. Draw with the normal tools, reopen Lip-sync…, select the mouth and Capture it. Only this layer is replaced; keep the face on other layers.", TextWrapping = TextWrapping.Wrap });
        var cueList = new ComboBox { Header = "Saved mouth changes (timeline frames)", HorizontalAlignment = HorizontalAlignment.Stretch };
        var cueFrame = Number("Cue at timeline frame (1-based)", _playheadFramePosition + 1, 1, 86400 * _frameRate + 1);
        var cueMouth = new ComboBox { Header = "Mouth from this frame onwards", ItemsSource = Enum.GetValues<InkViseme>(), SelectedIndex = 0 };
        var set = new Button { Content = "Set / replace cue at frame" };
        var remove = new Button { Content = "Remove cue at frame" };
        cuesPanel.Children.Add(cueList); cuesPanel.Children.Add(cueFrame); cuesPanel.Children.Add(cueMouth);
        cuesPanel.Children.Add(set); cuesPanel.Children.Add(remove);
        void RefreshCues()
        {
            cueList.Items.Clear();
            foreach (InkMouthCue cue in draft.Cues)
                cueList.Items.Add(new ComboBoxItem { Content = $"Frame {cue.Seconds * _frameRate + 1:0.###} · {cue.Mouth}", Tag = cue });
        }
        cueList.SelectionChanged += (_, _) =>
        {
            if (cueList.SelectedItem is not ComboBoxItem { Tag: InkMouthCue cue }) return;
            cueFrame.Value = Math.Round(cue.Seconds * _frameRate) + 1; cueMouth.SelectedItem = cue.Mouth;
        };
        set.Click += (_, _) =>
        {
            try
            {
                if (!double.IsFinite(cueFrame.Value) || cueFrame.Value != Math.Truncate(cueFrame.Value))
                    throw new ArgumentException("Choose a whole timeline frame.");
                InkLipSyncs.SetCue(draft, (cueFrame.Value - 1) / _frameRate, (InkViseme)cueMouth.SelectedItem);
                RefreshCues(); message.Text = "Mouth cue updated.";
            }
            catch (ArgumentException error) { message.Text = error.Message; }
        };
        remove.Click += (_, _) =>
        {
            double seconds = (cueFrame.Value - 1) / _frameRate;
            draft.Cues.RemoveAll(cue => Math.Abs(cue.Seconds - seconds) < 0.000001);
            RefreshCues();
        };
        mouth.SelectionChanged += (_, _) => preview.Invalidate();
        starter.Click += (_, _) =>
        {
            try { draft.Mouths = InkLipSyncs.Starter(x.Value, y.Value, width.Value, height.Value).Mouths; preview.Invalidate(); message.Text = "Starter mouth drawings replaced; timed cues are unchanged."; }
            catch (ArgumentException error) { message.Text = error.Message; }
        };
        capture.Click += (_, _) =>
        {
            List<List<InkPoint>> drawing = InkEditing.CloneStrokes(InkAnimation.GetDisplayedStrokes(_frames, _selectedFrameIndex,
                cel => GetLayerStrokes(cel, _activeInkLayerId)).ToList());
            if (drawing.Count == 0) { message.Text = "Draw a mouth on this layer's current cel first."; return; }
            draft.Mouths[(InkViseme)mouth.SelectedItem] = drawing; preview.Invalidate();
            message.Text = "Original cel drawing captured for the selected mouth.";
        };
        enabled.Click += (_, _) => draft.Enabled = enabled.IsChecked == true;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = $"Lip-sync: {CurrentInkLayer.Name}", Content = panel,
            PrimaryButtonText = "Save lip-sync", SecondaryButtonText = "Edit selected mouth", CloseButtonText = "Cancel",
        };
        CancellationTokenSource? analysis = null;
        Task? analysisTask = null;
        async Task Analyse()
        {
            if (audio.SelectedItem is not ComboBoxItem { Tag: AudioTrackProject source }) return;
            AudioTrackProject snapshot = AudioClipEditing.Clone(source);
            double threshold = silence.Value;
            double frameRate = _frameRate;
            InkLipSync librarySnapshot = InkLipSyncs.Clone(draft);
            using var cancellation = new CancellationTokenSource();
            analysis = cancellation;
            generate.IsEnabled = false; cancel.IsEnabled = true;
            dialog.IsPrimaryButtonEnabled = false; dialog.IsSecondaryButtonEnabled = false;
            message.Text = "Analysing audio locally…";
            try
            {
                InkLipSync result = await Task.Run(() => InkLipSyncs.Analyse(snapshot, frameRate, threshold, librarySnapshot, cancellation.Token));
                result.Mouths = draft.Mouths;
                draft = result; enabled.IsChecked = true; RefreshCues();
                message.Text = $"Drafted {draft.Cues.Count} changes across frames {draft.StartSeconds * _frameRate + 1:0}–{draft.EndSeconds * _frameRate + 1:0.###}. Review Timed cues before saving.";
            }
            catch (OperationCanceledException) { message.Text = "Analysis cancelled; previous cues are unchanged."; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
                InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException)
            { message.Text = $"Could not analyse lip-sync: {error.Message}"; }
            finally
            {
                analysis = null; generate.IsEnabled = true; cancel.IsEnabled = false;
                dialog.IsPrimaryButtonEnabled = true; dialog.IsSecondaryButtonEnabled = true;
            }
        }
        generate.Click += (_, _) => { if (analysisTask is not { IsCompleted: false }) analysisTask = Analyse(); };
        cancel.Click += (_, _) => analysis?.Cancel();
        dialog.Closing += (_, _) => analysis?.Cancel();
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try { InkLipSyncs.Validate(draft); }
            catch (InvalidDataException error) { args.Cancel = true; message.Text = error.Message; }
        };
        dialog.SecondaryButtonClick += (_, args) =>
        {
            if (_characterRig is { Enabled: true } && CurrentInkLayer.RigBinding is not null ||
                CurrentInkLayer.ObjectTracking is { Enabled: true })
            { args.Cancel = true; message.Text = "Turn rig preview and Follow object off before loading original mouth artwork."; }
        };
        RefreshCues();
        ContentDialogResult choice;
        try { choice = await dialog.ShowAsync(); }
        finally
        {
            analysis?.Cancel();
            if (analysisTask is not null) await analysisTask;
            preview.RemoveFromVisualTree();
        }
        if (choice == ContentDialogResult.None) return;
        BeginEdit();
        if (choice == ContentDialogResult.Secondary)
        {
            draft.Enabled = false;
            PromoteInterpolatedFrameForEditing();
            List<List<InkPoint>> drawing = draft.Mouths[(InkViseme)mouth.SelectedItem];
            CurrentStrokes.Clear(); CurrentStrokes.AddRange(InkEditing.CloneStrokes(drawing));
        }
        CurrentInkLayer.LipSync = InkLipSyncs.Clone(draft);
        MarkEditChanged(); CommitEdit(); RenderCurrentInk(); RefreshTimeline();
        StatusText.Text = choice == ContentDialogResult.Secondary
            ? "Mouth loaded for editing. Draw, reopen Lip-sync and Capture the original cel drawing for this mouth."
            : "Lip-sync saved. Acoustic cues are a draft; review them against the dialogue and correct the timed mouths.";
    }
}
