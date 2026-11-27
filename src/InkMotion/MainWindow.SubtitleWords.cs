using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkMotion;

public sealed partial class MainWindow
{
    private async void EditSelectedTitleWords()
    {
        if (_titleDialogOpen ||
            SelectedTimelineClip is not { Track.Kind: TimelineTrackKind.Text or TimelineTrackKind.Subtitle } selected)
        {
            return;
        }
        if (selected.Track.IsLocked)
        {
            StatusText.Text = "Unlock the track before editing word timing and style.";
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        TimelineClipProject clip = selected.Clip;
        double duration = clip.DurationSeconds;
        List<SubtitleRun> runs;
        try
        {
            runs = SubtitleRichText.HasRuns(clip)
                ? SubtitleRichText.CloneRuns(clip.SubtitleRuns)
                : SubtitleRichText.SplitWords(clip.Text);
        }
        catch (InvalidOperationException exception)
        {
            StatusText.Text = exception.Message;
            return;
        }

        var rows = new StackPanel { Spacing = 4 };
        var upcoming = new TextBox
        {
            Header = "Colour before a word's reveal time (#RRGGBB or #AARRGGBB)",
            Text = SubtitleRichText.FormatColour(clip.SubtitleUpcomingColour),
        };
        var error = new TextBlock { Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"], TextWrapping = TextWrapping.Wrap };
        void BuildRows()
        {
            rows.Children.Clear();
            foreach (SubtitleRun run in runs)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                row.Children.Add(new TextBlock
                {
                    Text = run.Text.Trim().Length == 0 ? "(space)" : run.Text.Trim(),
                    Width = 140, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                });
                var bold = new CheckBox { Content = "Bold", IsChecked = run.Bold, MinWidth = 70 };
                bold.Click += (_, _) => run.Bold = bold.IsChecked == true;
                var italic = new CheckBox { Content = "Italic", IsChecked = run.Italic, MinWidth = 70 };
                italic.Click += (_, _) => run.Italic = italic.IsChecked == true;
                var underline = new CheckBox { Content = "Underline", IsChecked = run.Underline, MinWidth = 90 };
                underline.Click += (_, _) => run.Underline = underline.IsChecked == true;
                var colour = new TextBox
                {
                    PlaceholderText = "Clip colour", Width = 110,
                    Text = run.Colour is uint value ? SubtitleRichText.FormatColour(value) : string.Empty,
                };
                colour.Tag = run;
                var reveal = new NumberBox
                {
                    PlaceholderText = "Always", Width = 110, Minimum = 0, Maximum = duration,
                    SmallChange = 0.1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                    Value = run.RevealSeconds ?? double.NaN,
                };
                reveal.ValueChanged += (_, args) => run.RevealSeconds = double.IsNaN(args.NewValue) ? null : args.NewValue;
                ToolTipService.SetToolTip(reveal, "Seconds from the clip start when this word is revealed; blank shows it throughout.");
                row.Children.Add(bold);
                row.Children.Add(italic);
                row.Children.Add(underline);
                row.Children.Add(colour);
                row.Children.Add(reveal);
                rows.Children.Add(row);
            }
        }

        var even = new Button { Content = "Time words evenly" };
        even.Click += (_, _) => { SubtitleRichText.DistributeEvenly(runs, duration); BuildRows(); };
        var reset = new Button { Content = "Reset to plain words" };
        reset.Click += (_, _) => { runs = SubtitleRichText.SplitWords(clip.Text); BuildRows(); };
        var clear = new Button { Content = "Remove word timing and style" };
        clear.Click += (_, _) => { runs.Clear(); BuildRows(); };
        BuildRows();
        var content = new StackPanel { Spacing = 8, MinWidth = 640 };
        content.Children.Add(new TextBlock
        {
            Text = $"Clip length {duration:0.###} s. Reveal times are seconds from the clip start and must not decrease from word to word.",
            TextWrapping = TextWrapping.Wrap,
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(even);
        buttons.Children.Add(reset);
        buttons.Children.Add(clear);
        content.Children.Add(buttons);
        content.Children.Add(upcoming);
        content.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 360 });
        content.Children.Add(error);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Word timing and style",
            Content = content,
            PrimaryButtonText = "Apply", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        uint upcomingColour = clip.SubtitleUpcomingColour;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (!SubtitleRichText.TryParseColour(upcoming.Text, out upcomingColour))
            {
                error.Text = "Enter the upcoming colour as #RRGGBB or #AARRGGBB.";
                args.Cancel = true;
                return;
            }

            foreach (StackPanel row in rows.Children.OfType<StackPanel>())
            {
                TextBox colourBox = row.Children.OfType<TextBox>().Single();
                var run = (SubtitleRun)colourBox.Tag;
                if (string.IsNullOrWhiteSpace(colourBox.Text))
                {
                    run.Colour = null;
                }
                else if (SubtitleRichText.TryParseColour(colourBox.Text, out uint parsed))
                {
                    run.Colour = parsed;
                }
                else
                {
                    error.Text = $"Enter a valid colour for “{run.Text.Trim()}” as #RRGGBB or #AARRGGBB, or leave it blank.";
                    args.Cancel = true;
                    return;
                }
            }

            TimelineClipProject check = VideoTimelineEditing.Clone(clip);
            check.SubtitleRuns = SubtitleRichText.CloneRuns(runs);
            if (!SubtitleRichText.IsValid(check, selected.Track.Kind))
            {
                error.Text = "Reveal times must be within the clip and must not decrease from word to word.";
                args.Cancel = true;
            }
        };

        _titleDialogOpen = true;
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && !selected.Track.IsLocked &&
                _videoTracks.Contains(selected.Track) && selected.Track.Clips.Contains(clip) &&
                (!SubtitleRichText.RunsEqual(clip.SubtitleRuns, runs) || clip.SubtitleUpcomingColour != upcomingColour))
            {
                BeginEdit();
                clip.SubtitleRuns = runs.All(run => !run.Bold && !run.Italic && !run.Underline &&
                        run.Colour is null && run.RevealSeconds is null)
                    ? []
                    : SubtitleRichText.CloneRuns(runs);
                clip.SubtitleUpcomingColour = upcomingColour;
                ApplyClipPreview();
                CommitEdit();
                UpdateVideoInspector();
                StatusText.Text = clip.SubtitleRuns.Count == 0
                    ? "Word timing and styling removed."
                    : $"Applied timing and styling to {clip.SubtitleRuns.Count} words.";
            }
        }
        finally
        {
            _titleDialogOpen = false;
        }
    }
}
