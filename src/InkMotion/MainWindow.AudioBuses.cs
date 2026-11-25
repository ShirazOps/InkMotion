using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkMotion;

public sealed partial class MainWindow
{
    private const int MaximumAudioBusNameLength = 64;
    private readonly List<AudioBusProject> _audioBuses = [];
    private readonly Dictionary<string, AudioPreviewSubmix> _previewSubmixes = new(StringComparer.Ordinal);
    private string? _selectedAudioBusId;
    private string? _audioEffectsBusId;

    private AudioPreviewSubmix? GetPreviewSubmix(string? busId) =>
        busId is not null && _previewSubmixes.TryGetValue(busId, out AudioPreviewSubmix? submix) ? submix : null;

    /// <summary>
    /// Rebuilds the bus tree and reroutes preview players. Bus edits pause playback before this is called.
    /// </summary>
    private void SyncPreviewSubmixes()
    {
        DisposePreviewSubmixes();
        AudioPreviewSubmix? CreateSubmix(string? busId)
        {
            AudioBusProject? bus = AudioBuses.Find(_audioBuses, busId);
            if (bus is null)
            {
                return null;
            }

            if (_previewSubmixes.TryGetValue(bus.Id, out AudioPreviewSubmix? existing))
            {
                return existing;
            }

            AudioPreviewSubmix? parent = CreateSubmix(bus.ParentBusId);
            var submix = _audioPreviewBus.CreateSubmix(_audioMixLayout, bus.Effects, bus.GainDecibels, parent);
            _previewSubmixes.Add(bus.Id, submix);
            return submix;
        }

        foreach (AudioBusProject bus in _audioBuses)
        {
            CreateSubmix(bus.Id);
        }

        foreach (AudioTrackRuntime track in _audioTracks)
        {
            track.Player?.SetRoute(GetPreviewSubmix(track.Project.BusId), track.Project, _audioMixLayout);
        }

        foreach (VideoAudioPreviewState state in new[] { _videoAudioPreview, _transitionVideoAudioPreview })
        {
            if (state.Player is AudioClipPlayer player && state.Settings is AudioTrackProject settings)
            {
                player.SetRoute(GetPreviewSubmix(settings.BusId), settings, _audioMixLayout);
            }
        }

    }

    private void DisposePreviewSubmixes()
    {
        foreach (AudioPreviewSubmix submix in _previewSubmixes.Values)
        {
            submix.Dispose();
        }

        _previewSubmixes.Clear();
    }

    private void ApplyAudioBusEdit(Func<bool> edit, string successMessage, string failureMessage)
    {
        PausePlayback();
        FinishActiveEdit();
        BeginEdit();
        if (edit())
        {
            MarkEditChanged();
            StatusText.Text = successMessage;
        }
        else
        {
            StatusText.Text = failureMessage;
        }

        CommitEdit();
        SyncPreviewSubmixes();
        SyncAudioTracks(TimeSpan.FromSeconds(_playheadFramePosition / _frameRate), startPlayback: false);
        SyncVideoPreview(CurrentPlaybackSeconds, forceSeek: true);
        UpdateAudioBusControls();
        UpdateSelectedAudioTrack();
    }

    private void UpdateAudioBusControls()
    {
        bool wasRestoringProject = _isRestoringProject;
        _isRestoringProject = true;
        if (AudioBuses.Find(_audioBuses, _selectedAudioBusId) is null)
        {
            _selectedAudioBusId = _audioBuses.FirstOrDefault()?.Id;
        }

        if (AudioBuses.Find(_audioBuses, _audioEffectsBusId) is null)
        {
            _audioEffectsBusId = null;
        }

        AudioBusSelector.Items.Clear();
        foreach (AudioBusProject bus in _audioBuses)
        {
            AudioBusSelector.Items.Add(new ComboBoxItem { Content = bus.Name, Tag = bus.Id });
        }

        AudioBusSelector.SelectedItem = FindBusItem(AudioBusSelector, _selectedAudioBusId ?? string.Empty);
        AudioBusSelector.IsEnabled = _audioBuses.Count > 0;
        AudioBusProject? selected = AudioBuses.Find(_audioBuses, _selectedAudioBusId);
        AudioBusControls.IsEnabled = selected is not null;
        RemoveAudioBusButton.IsEnabled = selected is not null;
        AudioBusNameBox.Text = selected?.Name ?? string.Empty;
        AudioBusGainBox.Value = selected?.GainDecibels ?? 0;
        AudioBusMuteButton.IsChecked = selected?.Muted == true;
        AudioBusSoloButton.IsChecked = selected?.Solo == true;
        PopulateParentBusSelector(selected);

        bool hasTrack = _selectedAudioTrackIndex >= 0 && _selectedAudioTrackIndex < _audioTracks.Count;
        PopulateBusOutputSelector(AudioClipBusSelector, "Master (direct)");
        AudioClipBusSelector.SelectedItem = FindBusItem(
            AudioClipBusSelector, hasTrack ? _audioTracks[_selectedAudioTrackIndex].Project.BusId ?? string.Empty : string.Empty);
        AudioClipBusSelector.IsEnabled = hasTrack && _audioBuses.Count > 0;

        AudioEffectsTargetSelector.Items.Clear();
        AudioEffectsTargetSelector.Items.Add(new ComboBoxItem { Content = "Selected audio clip", Tag = string.Empty });
        foreach (AudioBusProject bus in _audioBuses)
        {
            AudioEffectsTargetSelector.Items.Add(new ComboBoxItem { Content = $"Bus: {bus.Name}", Tag = bus.Id });
        }

        AudioEffectsTargetSelector.SelectedItem = FindBusItem(AudioEffectsTargetSelector, _audioEffectsBusId ?? string.Empty);
        AudioEffectsTargetSelector.IsEnabled = _audioBuses.Count > 0;
        _isRestoringProject = wasRestoringProject;
    }

    private void PopulateBusOutputSelector(ComboBox selector, string masterLabel)
    {
        selector.Items.Clear();
        selector.Items.Add(new ComboBoxItem { Content = masterLabel, Tag = string.Empty });
        foreach (AudioBusProject bus in _audioBuses)
        {
            selector.Items.Add(new ComboBoxItem { Content = bus.Name, Tag = bus.Id });
        }
    }

    private void PopulateParentBusSelector(AudioBusProject? selected)
    {
        AudioBusParentSelector.Items.Clear();
        AudioBusParentSelector.Items.Add(new ComboBoxItem { Content = "Master", Tag = string.Empty });
        foreach (AudioBusProject bus in _audioBuses.Where(bus => selected is not null &&
            !AudioBuses.WouldCreateCycle(_audioBuses, selected.Id, bus.Id)))
        {
            AudioBusParentSelector.Items.Add(new ComboBoxItem { Content = bus.Name, Tag = bus.Id });
        }

        AudioBusParentSelector.SelectedItem = FindBusItem(AudioBusParentSelector, selected?.ParentBusId ?? string.Empty);
        AudioBusParentSelector.IsEnabled = selected is not null;
    }

    private static ComboBoxItem? FindBusItem(ComboBox selector, string tag) =>
        selector.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.Ordinal));

    private AudioBusProject? SelectedAudioBus => AudioBuses.Find(_audioBuses, _selectedAudioBusId);

    private void AddAudioBus_Click(object sender, RoutedEventArgs e)
    {
        var bus = new AudioBusProject { Name = AudioBuses.CreateName(_audioBuses) };
        ApplyAudioBusEdit(() =>
        {
            _audioBuses.Add(bus);
            _selectedAudioBusId = bus.Id;
            return true;
        }, $"{bus.Name} added. Route clips or video tracks to it, then set its gain and effects.", "Could not add a bus.");
    }

    private void RemoveAudioBus_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAudioBus is not AudioBusProject bus)
        {
            return;
        }

        ApplyAudioBusEdit(() =>
        {
            AudioBuses.ClearRoutes(_audioTracks.Select(track => track.Project), _videoTracks, bus.Id);
            AudioBuses.ReparentChildren(_audioBuses, bus.Id, bus.ParentBusId);
            _audioBuses.Remove(bus);
            return true;
        }, $"{bus.Name} removed; its sources and child buses now feed the previous output.", "Could not remove the bus.");
    }

    private void AudioBusSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitialising || _isRestoringProject || AudioBusSelector.SelectedItem is not ComboBoxItem { Tag: string id })
        {
            return;
        }

        _selectedAudioBusId = id;
        UpdateAudioBusControls();
    }

    private void AudioBusParent_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitialising || _isRestoringProject ||
            AudioBusParentSelector.SelectedItem is not ComboBoxItem { Tag: string parentId } ||
            SelectedAudioBus is not AudioBusProject bus)
        {
            return;
        }

        string? newParentId = string.IsNullOrEmpty(parentId) ? null : parentId;
        if (AudioBuses.WouldCreateCycle(_audioBuses, bus.Id, newParentId))
        {
            StatusText.Text = "A bus cannot be routed to itself or one of its child buses.";
            UpdateAudioBusControls();
            return;
        }

        if (bus.ParentBusId == newParentId)
        {
            return;
        }

        ApplyAudioBusEdit(() =>
        {
            bus.ParentBusId = newParentId;
            return true;
        }, newParentId is null ? $"{bus.Name} now feeds the master." :
            $"{bus.Name} now feeds {AudioBuses.Find(_audioBuses, newParentId)?.Name}.",
            "Could not change the bus parent.");
    }

    private void AudioBusName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isInitialising || _isRestoringProject || SelectedAudioBus is not AudioBusProject bus)
        {
            return;
        }

        string name = AudioBusNameBox.Text.Trim();
        if (name.Length == 0 || name.Length > MaximumAudioBusNameLength)
        {
            AudioBusNameBox.Text = bus.Name;
            StatusText.Text = $"Bus names must contain 1 to {MaximumAudioBusNameLength} characters.";
            return;
        }

        if (name == bus.Name)
        {
            return;
        }

        ApplyAudioBusEdit(() =>
        {
            bus.Name = name;
            return true;
        }, $"Bus renamed to {name}.", "Could not rename the bus.");
    }

    private void AudioBusGain_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInitialising || _isRestoringProject || SelectedAudioBus is not AudioBusProject bus)
        {
            return;
        }

        double gain = double.IsFinite(args.NewValue)
            ? Math.Clamp(args.NewValue, AudioBuses.MinimumGainDecibels, AudioBuses.MaximumGainDecibels)
            : 0;
        if (gain == bus.GainDecibels)
        {
            return;
        }

        ApplyAudioBusEdit(() =>
        {
            bus.GainDecibels = gain;
            return true;
        }, $"{bus.Name} gain set to {gain:+0.0;-0.0;0.0} dB.", "Could not change the bus gain.");
    }

    private void AudioBusMute_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAudioBus is not AudioBusProject bus)
        {
            return;
        }

        bool muted = AudioBusMuteButton.IsChecked == true;
        ApplyAudioBusEdit(() =>
        {
            bus.Muted = muted;
            return true;
        }, muted ? $"{bus.Name} muted." : $"{bus.Name} unmuted.", "Could not change the bus mute.");
    }

    private void AudioBusSolo_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAudioBus is not AudioBusProject bus)
        {
            return;
        }

        bool solo = AudioBusSoloButton.IsChecked == true;
        ApplyAudioBusEdit(() =>
        {
            bus.Solo = solo;
            return true;
        }, solo
            ? $"{bus.Name} soloed; sources on other buses and the master are silent."
            : $"{bus.Name} solo cleared.", "Could not change the bus solo.");
    }

    private void AudioClipBus_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitialising || _isRestoringProject ||
            AudioClipBusSelector.SelectedItem is not ComboBoxItem { Tag: string tag } ||
            _selectedAudioTrackIndex < 0 || _selectedAudioTrackIndex >= _audioTracks.Count)
        {
            return;
        }

        AudioTrackRuntime track = _audioTracks[_selectedAudioTrackIndex];
        AudioBusProject? bus = AudioBuses.Find(_audioBuses, tag);
        if (track.Project.BusId == bus?.Id)
        {
            return;
        }

        ApplyAudioBusEdit(() =>
        {
            track.Project.BusId = bus?.Id;
            return true;
        }, bus is null ? "Audio clip now feeds the master directly." : $"Audio clip routed to {bus.Name}.",
            "Could not change the audio clip output.");
        RefreshAudioTracks();
    }

    private void AudioEffectsTarget_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitialising || _isRestoringProject || AudioEffectsTargetSelector.SelectedItem is not ComboBoxItem { Tag: string tag })
        {
            return;
        }

        _audioEffectsBusId = AudioBuses.Find(_audioBuses, tag)?.Id;
        UpdateSelectedAudioTrack();
    }
}
