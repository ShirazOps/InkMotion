using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;

namespace InkMotion;

public sealed partial class MainWindow
{
    private readonly QuickStartGuide _quickStartGuide = new();
    private QuickStartPreferences _quickStartPreferences = new();
    private readonly string _quickStartPreferencesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InkMotion", "quick-start.json");
    private bool _quickStartWelcomeOpen;
    private bool _quickStartTipClosing;
    private bool _quickStartReopenTip;
    private bool _quickStartClosed;

    private void InitialiseQuickStartGuide()
    {
        try
        {
            _quickStartPreferences = QuickStartPreferences.Load(_quickStartPreferencesPath);
        }
        catch (Exception ex) when (IsQuickStartPreferenceException(ex))
        {
            StatusText.Text = $"Could not restore beginner guide preferences: {ex.Message}";
            _quickStartPreferences.WelcomeDismissed = true;
        }
        RootGrid.Loaded += QuickStartRoot_Loaded;
        RootGrid.LayoutUpdated += QuickStartLayout_Updated;
    }

    private static bool IsQuickStartPreferenceException(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException;

    private async void QuickStartRoot_Loaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= QuickStartRoot_Loaded;
        if (!_quickStartPreferences.WelcomeDismissed && !_quickStartPreferences.Completed)
        {
            await OfferQuickStartGuideAsync();
        }
    }

    private async Task OfferQuickStartGuideAsync()
    {
        if (_quickStartWelcomeOpen || _quickStartClosed) return;
        StopQuickStartGuide();
        _quickStartWelcomeOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Welcome to InkMotion",
                Content = new TextBlock
                {
                    Text = "Learn by doing: add media, create an ink layer, draw, preview and export. The guide highlights what to click and waits for your action. Your edits are real and undoable; existing work is not reset. You can skip at any time and restart from Help > Beginner quick-start guide.",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Start guide",
                CloseButtonText = "Not now",
                DefaultButton = ContentDialogButton.Primary,
            };
            ContentDialogResult result = await dialog.ShowAsync();
            if (_quickStartClosed) return;
            _quickStartPreferences.WelcomeDismissed = true;
            SaveQuickStartPreferences();
            if (result == ContentDialogResult.Primary)
            {
                PausePlayback();
                FinishActiveEdit();
                _quickStartGuide.Start();
                RevealQuickStartStep();
            }
        }
        finally { _quickStartWelcomeOpen = false; }
    }

    private async void StartQuickStartGuide_Click(object sender, RoutedEventArgs e) =>
        await OfferQuickStartGuideAsync();

    private void ObserveQuickStartAction(QuickStartAction action)
    {
        if (!_quickStartGuide.Observe(action)) return;
        if (_quickStartGuide.IsComplete)
        {
            _quickStartPreferences.Completed = true;
            SaveQuickStartPreferences();
        }
        RootGrid.UpdateLayout();
        GetQuickStartTarget()?.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        RefreshQuickStartPresentation();
    }

    private void RevealQuickStartStep()
    {
        if (!_quickStartGuide.IsActive) return;
        string workspace = _quickStartGuide.CurrentStep?.Workspace ?? "Export";
        ShowWorkspace(workspace);
        if (IsOriginalPreview) OriginalPreviewToggle.IsOn = false;
        ApplyEditorLayout();
        RootGrid.UpdateLayout();
        FrameworkElement? target = GetQuickStartTarget();
        target?.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        RefreshQuickStartPresentation();
    }

    private void QuickStartShowMe_Click(object sender, RoutedEventArgs e) => RevealQuickStartStep();

    private FrameworkElement? GetQuickStartTarget() => _quickStartGuide.IsComplete
        ? ExportJobsSelector
        : _quickStartGuide.CurrentStep?.Action switch
        {
            QuickStartAction.MediaAdded => QuickStartAddVideoButton,
            QuickStartAction.InkWorkspaceOpened => InkWorkspaceButton,
            QuickStartAction.LayerAdded => QuickStartAddInkLayerButton,
            QuickStartAction.PenSelected => QuickStartPenButton,
            QuickStartAction.InkDrawn => PreviewViewport,
            QuickStartAction.PlaybackStarted => PlayButton,
            QuickStartAction.ExportWorkspaceOpened => ExportWorkspaceButton,
            QuickStartAction.VideoExportSubmitted => ToolbarExportButton,
            _ => null,
        };

    private string QuickStartInstruction => _quickStartGuide.CurrentStep?.Instruction ??
        "Your video export has been accepted. Check Export jobs for completion or errors; it can continue after the editor closes. Save your project from File > Save project if you want to edit it later.";

    private void RefreshQuickStartPresentation()
    {
        if (!_quickStartGuide.IsActive || _quickStartClosed) return;
        QuickStartBanner.Visibility = Visibility.Visible;
        QuickStartProgressText.Text = _quickStartGuide.IsComplete
            ? "Beginner guide complete"
            : $"Step {_quickStartGuide.StepIndex + 1} of {QuickStartGuide.Steps.Count}: {_quickStartGuide.CurrentStep!.Title}";
        QuickStartInstructionText.Text = QuickStartInstruction;
        QuickStartSkipButton.Content = _quickStartGuide.IsComplete ? "Finish" : "Skip guide";
        _quickStartReopenTip = true;
        if (QuickStartTip.IsOpen)
        {
            QuickStartTip.IsOpen = false;
        }
        else if (!_quickStartTipClosing)
        {
            DispatcherQueue.TryEnqueue(ShowQuickStartTip);
        }
    }

    private void ShowQuickStartTip()
    {
        if (!_quickStartGuide.IsActive || _quickStartClosed || _quickStartTipClosing) return;
        _quickStartReopenTip = false;
        RootGrid.UpdateLayout();
        FrameworkElement? target = GetVisibleQuickStartTarget();
        QuickStartTip.Target = target ?? QuickStartBanner;
        QuickStartTip.Title = QuickStartProgressText.Text;
        QuickStartTip.Subtitle = QuickStartInstruction;
        QuickStartTip.CloseButtonContent = _quickStartGuide.IsComplete ? "Finish" : "Skip guide";
        QuickStartTip.PreferredPlacement = target == PreviewViewport
            ? TeachingTipPlacementMode.Top : TeachingTipPlacementMode.Bottom;
        UpdateQuickStartHighlight();
        QuickStartTip.IsOpen = true;
    }

    private FrameworkElement? GetVisibleQuickStartTarget()
    {
        string workspace = _quickStartGuide.CurrentStep?.Workspace ?? "Export";
        if (_workspace != workspace) return null;
        FrameworkElement? target = GetQuickStartTarget();
        if (target is null || target.Visibility != Visibility.Visible || target.ActualWidth <= 0 || target.ActualHeight <= 0)
            return null;
        Rect bounds = target.TransformToVisual(RootGrid).TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
        FrameworkElement container = target == QuickStartAddInkLayerButton ? InkInspectorPanel :
            target == ExportJobsSelector ? ExportInspectorPanel : RootGrid;
        Rect visible = container.TransformToVisual(RootGrid).TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
        return bounds.Left >= visible.Left && bounds.Right <= visible.Right &&
            bounds.Top >= visible.Top && bounds.Bottom <= visible.Bottom ? target : null;
    }

    private void QuickStartLayout_Updated(object? sender, object e) => UpdateQuickStartHighlight();

    private void UpdateQuickStartHighlight()
    {
        FrameworkElement? target = _quickStartGuide.IsActive ? GetVisibleQuickStartTarget() : null;
        QuickStartHighlight.Visibility = target is null ? Visibility.Collapsed : Visibility.Visible;
        if (target is null) return;
        Rect bounds = target.TransformToVisual(RootGrid).TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
        Canvas.SetLeft(QuickStartHighlight, bounds.X);
        Canvas.SetTop(QuickStartHighlight, bounds.Y);
        QuickStartHighlight.Width = bounds.Width;
        QuickStartHighlight.Height = bounds.Height;
    }

    private void QuickStartTip_Closing(TeachingTip sender, TeachingTipClosingEventArgs args) =>
        _quickStartTipClosing = true;

    private void QuickStartTip_Closed(TeachingTip sender, TeachingTipClosedEventArgs args)
    {
        _quickStartTipClosing = false;
        if (args.Reason == TeachingTipCloseReason.CloseButton)
        {
            StopQuickStartGuide();
        }
        else if (_quickStartReopenTip && _quickStartGuide.IsActive)
        {
            DispatcherQueue.TryEnqueue(ShowQuickStartTip);
        }
    }

    private void QuickStartSkip_Click(object sender, RoutedEventArgs e) => StopQuickStartGuide();

    private void QuickStart_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_quickStartGuide.IsActive && e.Key == VirtualKey.Escape)
        {
            StopQuickStartGuide();
            e.Handled = true;
        }
        if (!_quickStartGuide.IsActive)
            HandleVideoEditingShortcut(e);
    }

    private void StopQuickStartGuide()
    {
        _quickStartGuide.Stop();
        _quickStartReopenTip = false;
        QuickStartTip.IsOpen = false;
        QuickStartBanner.Visibility = Visibility.Collapsed;
        QuickStartHighlight.Visibility = Visibility.Collapsed;
        ApplyEditorLayout();
    }

    private void SaveQuickStartPreferences()
    {
        try { _quickStartPreferences.Save(_quickStartPreferencesPath); }
        catch (Exception ex) when (IsQuickStartPreferenceException(ex))
        {
            StatusText.Text = $"Could not save beginner guide preferences: {ex.Message}";
        }
    }

    private void CloseQuickStartGuide()
    {
        _quickStartClosed = true;
        StopQuickStartGuide();
        RootGrid.Loaded -= QuickStartRoot_Loaded;
        RootGrid.LayoutUpdated -= QuickStartLayout_Updated;
    }
}
