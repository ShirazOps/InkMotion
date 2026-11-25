using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace InkMotion;

/// <summary>Automatic occlusion mattes and the on-device AI model manager.</summary>
public sealed partial class MainWindow
{
    private bool _onDeviceModelListReady;
    private bool _onDeviceModelBusy;

    private OnDeviceModelDescriptor? SelectedOnDeviceModel =>
        OnDeviceModelSelector?.SelectedItem is ComboBoxItem { Tag: OnDeviceModelDescriptor model } ? model : null;

    private void EnsureOnDeviceModelList()
    {
        if (_onDeviceModelListReady || OnDeviceModelSelector is null) return;
        _onDeviceModelListReady = true;
        foreach (OnDeviceModelDescriptor model in OnDeviceModels.All)
            OnDeviceModelSelector.Items.Add(new ComboBoxItem { Content = model.DisplayName, Tag = model });
        OnDeviceModelSelector.SelectedIndex = 0;
        if (OnDeviceModels.UnsupportedReason() is string reason)
        {
            InkTrackingPoseModeItem.IsEnabled = false;
            ToolTipService.SetToolTip(InkTrackingPoseModeItem, reason);
        }
        UpdateOnDeviceModelControls();
    }

    private void UpdateOnDeviceModelControls()
    {
        if (OnDeviceModelStatusText is null) return;
        EnsureOnDeviceModelList();
        string? unsupported = OnDeviceModels.UnsupportedReason();
        OnDeviceModelDescriptor? model = SelectedOnDeviceModel;
        bool installed = model is not null && OnDeviceModelStore.Default.IsInstalled(model);
        bool idle = !_onDeviceModelBusy && _inkTrackingCancellation is null;
        OnDeviceModelSelector.IsEnabled = idle;
        ImportOnDeviceModelButton.IsEnabled = idle && unsupported is null && model is not null;
        DownloadOnDeviceModelButton.IsEnabled = idle && unsupported is null && model is { IsPublished: true };
        RemoveOnDeviceModelButton.IsEnabled = idle && installed;
        OnDeviceModelStatusText.Text = unsupported ?? (model is null ? "" :
            $"{(installed ? "Installed" : "Not installed")} · {model.Purpose}\nLicence: {model.Licence}\nSource: {model.SourceProject}\n" +
            (model.IsPublished ? "A verified download is available on request." :
                "Download: not yet published. Export the file with the scripts in build/models, then import it.") +
            $"\nStored in {OnDeviceModelStore.Default.Directory}");
    }

    private void OnDeviceModel_Changed(object sender, SelectionChangedEventArgs e) => UpdateOnDeviceModelControls();

    private async void ImportOnDeviceModel_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedOnDeviceModel is not { } model || _onDeviceModelBusy || OnDeviceModels.UnsupportedReason() is not null) return;
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".onnx");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null) return;
        _onDeviceModelBusy = true;
        UpdateOnDeviceModelControls();
        try
        {
            string hash = await OnDeviceModelStore.Default.ImportAsync(model, file.Path, CancellationToken.None);
            StatusText.Text = $"Imported {model.DisplayName}. SHA-256 {hash}; compare it with the exporter's output.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = $"Could not import the model: {ex.Message}";
        }
        finally
        {
            _onDeviceModelBusy = false;
            UpdateOnDeviceModelControls();
        }
    }

    private async void DownloadOnDeviceModel_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedOnDeviceModel is not { } model || _onDeviceModelBusy || OnDeviceModels.UnsupportedReason() is not null) return;
        if (!model.IsPublished)
        {
            StatusText.Text = $"{model.DisplayName} has not been published for download yet. Export and import it instead.";
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Download this AI model?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = $"{model.DisplayName}\n\nFrom: {model.DownloadUrl}\nLicence: {model.Licence}\n" +
                    $"Up to {model.MaximumBytes / (1024 * 1024)} MB. The file is checked against its published SHA-256 and " +
                    "stored only on this device. Your videos are never uploaded.",
            },
            PrimaryButtonText = "Download",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        OnDeviceModelDownloadConsent consent = OnDeviceModelDownloadConsent.GrantedByUser(model);
        _onDeviceModelBusy = true;
        UpdateOnDeviceModelControls();
        try
        {
            using var client = new HttpClient();
            var progress = new Progress<double>(value => OnDeviceModelStatusText.Text = $"Downloading · {value:P0}");
            await OnDeviceModelStore.Default.DownloadAsync(model, consent, client, progress, CancellationToken.None);
            StatusText.Text = $"Downloaded and verified {model.DisplayName}.";
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException or
            InvalidOperationException or TaskCanceledException)
        {
            StatusText.Text = $"Could not download the model: {ex.Message}";
        }
        finally
        {
            _onDeviceModelBusy = false;
            UpdateOnDeviceModelControls();
        }
    }

    private void RemoveOnDeviceModel_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedOnDeviceModel is not { } model || _onDeviceModelBusy) return;
        try
        {
            OnDeviceModelStore.Default.Remove(model);
            StatusText.Text = $"Removed {model.DisplayName} from this device.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not remove the model: {ex.Message}";
        }
        UpdateOnDeviceModelControls();
    }

    private void UpdateInkAutoMatteControls()
    {
        if (InkAutoMatteStatusText is null) return;
        InkLayerProject layer = CurrentInkLayer;
        bool idle = _inkTrackingCancellation is null && !_selectInkTrackingBox;
        string? unsupported = OnDeviceModels.UnsupportedReason();
        InkAutoMatteButton.IsEnabled = idle && layer.IsHoldout && !layer.IsLocked && unsupported is null;
        ClearInkAutoMatteButton.IsEnabled = idle && !layer.IsLocked && layer.AutoMatte is not null;
        InkAutoMatteStatusText.Text = layer.AutoMatte is { } matte
            ? $"Automatic matte · {matte.StartSeconds:0.###}–{matte.EndSeconds:0.###} s · {matte.Keys.Count} frames" +
              (InkAutoMattes.Evaluate(matte, _videoTracks, matte.Keys[0].Seconds) is null ? " · source clip changed; clear and generate again" : "") +
              (matte.LostSeconds is double lost ? $" · object lost at {lost:0.###} s; paint the rest or generate again from there" : "")
            : unsupported ?? (layer.IsHoldout
                ? "Automatic matte: select a box around the foreground object, then generate."
                : "Automatic matte: make this layer an occlusion matte first.");
        UpdateOnDeviceModelControls();
    }

    private async void InkAutoMatte_Click(object sender, RoutedEventArgs e)
    {
        if (_inkTrackingCancellation is not null) return;
        InkLayerProject layer = CurrentInkLayer;
        if (OnDeviceModels.UnsupportedReason() is string unsupported) { StatusText.Text = unsupported; return; }
        if (!layer.IsHoldout || layer.IsLocked)
        {
            StatusText.Text = "Choose an unlocked occlusion matte layer first.";
            return;
        }
        if (!OnDeviceModelStore.Default.AreInstalled(OnDeviceModels.SegmentationEncoder, OnDeviceModels.SegmentationDecoder))
        {
            StatusText.Text = "Import both MobileSAM files (encoder and decoder) under AI models first.";
            return;
        }
        if (IsOriginalPreview)
        {
            StatusText.Text = "Switch to Modified preview before generating a matte.";
            return;
        }
        PausePlayback();
        FinishActiveEdit();
        double start = _playheadFramePosition / _frameRate;
        if (_inkTrackingBox is not { } box || _inkTrackingLine is not null || !ReferenceEquals(_inkTrackingBoxLayer, layer) ||
            Math.Abs(start - _inkTrackingBoxSeconds) > 0.5 / _frameRate)
        {
            StatusText.Text = "Select a box around the foreground object on this layer at the playhead first (use a box tracking mode).";
            return;
        }
        var visible = VideoTimelineEditing.GetVisibleLayers(_videoTracks, start, _frameRate, _videoMulticamGroups)
            .LastOrDefault(location => location.Track.Kind == TimelineTrackKind.Video);
        if (visible is null)
        {
            StatusText.Text = "Place the playhead inside a visible video clip.";
            return;
        }
        double clipEnd = VideoTimelineEditing.GetStartSeconds(visible.Clip, _frameRate) +
            VideoTimelineEditing.GetDurationSeconds(visible.Clip, TimelineTrackKind.Video);
        double end = InkTrackingEndBox.Value == 0 || double.IsNaN(InkTrackingEndBox.Value) ? clipEnd : InkTrackingEndBox.Value;
        if (!double.IsFinite(end) || end > clipEnd + 0.0001 || end <= start + 1 / _frameRate)
        {
            StatusText.Text = "Set the end time after the playhead and within this video clip, or use 0 for the clip end.";
            return;
        }
        EditorProject snapshot = CreateProjectSnapshot();
        string sourceSignature = JsonSerializer.Serialize(snapshot.VideoTracks);
        string clipSignature = JsonSerializer.Serialize(visible.Clip);
        string clipId = visible.Clip.Id;
        double aspect = InkSurface.ActualWidth / Math.Max(1, InkSurface.ActualHeight);
        double frameRate = _frameRate;
        string directory = OnDeviceModelStore.Default.Directory;
        using var cancellation = new CancellationTokenSource();
        _inkTrackingCancellation = cancellation;
        UpdateInkTrackingControls();
        var progress = new Progress<double>(value =>
        {
            if (_inkTrackingCancellation == cancellation)
                InkAutoMatteStatusText.Text = $"Outlining the object on this device · {value:P0}";
        });
        try
        {
            InkAutoMatteResult result = await Task.Run(() => InkAutoMattes.Analyse(snapshot, clipId, start, end, box,
                aspect, directory, progress, cancellation.Token));
            if (!_inkLayers.Contains(layer) || layer.IsLocked || !layer.IsHoldout || frameRate != _frameRate ||
                JsonSerializer.Serialize(CreateProjectSnapshot().VideoTracks) != sourceSignature)
            {
                StatusText.Text = "The layer or video changed while the matte was generated; nothing was applied. Generate it again.";
                return;
            }
            if (result.Keys.Count == 0)
            {
                StatusText.Text = result.Message;
                return;
            }
            BeginEdit();
            layer.AutoMatte = new InkAutoMatte
            {
                SourceClipId = clipId, SourceSignature = clipSignature, CanvasAspectRatio = aspect,
                StartSeconds = start, EndSeconds = end, LostSeconds = result.LostSeconds, Keys = [.. result.Keys],
            };
            MarkEditChanged();
            CommitEdit();
            StatusText.Text = result.Message;
            CancelInkTrackingSelection();
            RenderCurrentInk();
            CompositionSurface.Invalidate();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            StatusText.Text = "Automatic matte cancelled; the previous matte was kept.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or
            System.Runtime.InteropServices.COMException or UnauthorizedAccessException or
            PlatformNotSupportedException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException)
        {
            StatusText.Text = $"Could not generate the matte: {ex.Message}";
        }
        finally
        {
            _inkTrackingCancellation = null;
            UpdateInkTrackingControls();
        }
    }

    private void ClearInkAutoMatte_Click(object sender, RoutedEventArgs e)
    {
        if (_inkTrackingCancellation is not null || CurrentInkLayer.IsLocked || CurrentInkLayer.AutoMatte is null) return;
        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        CurrentInkLayer.AutoMatte = null;
        MarkEditChanged();
        CommitEdit();
        UpdateInkTrackingControls();
        RenderCurrentInk();
        CompositionSurface.Invalidate();
        StatusText.Text = "Automatic matte cleared. Painted matte strokes are unchanged.";
    }
}
