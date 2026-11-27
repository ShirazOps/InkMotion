using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;

namespace InkMotion;

public sealed partial class MainWindow
{
    private LanCollaborationHost? _lanHost;
    private LanCollaborationClient? _lanClient;
    private CancellationTokenSource? _lanCancellation;
    private DispatcherQueueTimer? _lanTimer;
    private Task<bool>? _lanPublishTask;
    private LanState? _lanQueuedState;
    private string _lanLocalBaseline = string.Empty;
    private long _lanRevision;
    private long _lanGeneration;
    private int _lanPeerCount;
    private bool _lanEditor;
    private bool _lanBusy;
    private bool _lanApplying;
    private bool _lanShareFailed;
    private bool _lanHasSessionProject;
    private bool _lanWindowClosed;
    private int _lanProjectFileOperations;

    private bool IsLanSessionActive => _lanClient is not null || _lanBusy;

    private bool LanLongOperationInProgress =>
        _compoundContexts.Count > 0 || _switchingCompound || _compoundPreparation is not null || _lanProjectFileOperations > 0 ||
        IsAudioRecordingInProgress || _screenRecorderWindow is not null ||
        _inkTrackingCancellation is not null || _selectInkTrackingBox ||
        _speechSubtitlesCancellation is not null || _subtitleTranslationCancellation is not null ||
        _colourImportCancellation is not null || _onDeviceModelBusy || _capturingAudioNoiseProfile || _isStabilisationRunning ||
        _isVideoProxyRunning || _isOpticalFlowProxyRunning || _isMaskTrackingRunning;

    private bool PrepareLanOperation()
    {
        if (LanLongOperationInProgress)
        {
            StatusText.Text = "Return to the main timeline and finish recording or background edits before changing collaboration.";
            return false;
        }
        PausePlayback();
        FinishActiveEdit();
        if (_quickStartGuide.IsActive) StopQuickStartGuide();
        return true;
    }

    private EditorProject CreateLanSnapshot()
    {
        EditorProject project = CreateProjectSnapshot();
        foreach (EditorProject timeline in CompoundTimelines.Projects(project))
        {
            timeline.SelectedFrameIndex = 0;
            timeline.ActiveInkLayerId = "ink-1";
        }
        return project;
    }

    private string CurrentLanSnapshot() => EditorProjectSerialiser.Serialise(CreateLanSnapshot());

    private static string NewLanMediaDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "InkMotion", "Collaboration", Guid.NewGuid().ToString("N"), "media");

    private string LanProjectDirectory() => _projectFile is null
        ? Environment.CurrentDirectory
        : Path.GetDirectoryName(_projectFile.Path)!;

    private async void HostLan_Click(object sender, RoutedEventArgs e)
    {
        if (IsLanSessionActive || !PrepareLanOperation()) return;
        IReadOnlyList<LanAddress> addresses;
        try { addresses = LanNetworks.Available(); }
        catch (NetworkInformationException ex)
        {
            StatusText.Text = $"Could not list local network addresses: {ex.Message}";
            return;
        }
        var selector = new ComboBox
        {
            Header = "Listen on a private IPv4 network",
            ItemsSource = addresses,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var panel = new StackPanel { Spacing = 12, MaxWidth = 500 };
        panel.Children.Add(new TextBlock
        {
            Text = "Anyone with the invitation can join and receive this project and all linked media directly. " +
                "Share it privately with people you trust. One person edits at a time; the others see updates. " +
                "Media copies remain on each device. VST3 inserts are not supported.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Choose the adapter connected to your collaborators. 'This computer only' cannot accept other devices.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(selector);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Host a local collaboration session",
            Content = panel,
            PrimaryButtonText = "Start sharing",
            IsPrimaryButtonEnabled = false,
            CloseButtonText = "Cancel",
        };
        selector.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = selector.SelectedItem is LanAddress;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
            selector.SelectedItem is not LanAddress address || !PrepareLanOperation())
            return;

        LanCollaborationHost? host = null;
        LanCollaborationClient? client = null;
        _lanCancellation = new CancellationTokenSource();
        CancellationToken token = _lanCancellation.Token;
        _lanBusy = true;
        UpdateLanControls();
        try
        {
            EditorProject project = CreateLanSnapshot();
            string before = EditorProjectSerialiser.Serialise(project);
            string baseDirectory = LanProjectDirectory();
            var media = new LanMediaStore(NewLanMediaDirectory());
            StatusText.Text = "Preparing linked media for local collaboration. This can take time for large files.";
            LanProjectSnapshot wire = await Task.Run(() => media.PrepareAsync(project, baseDirectory, token), token);
            if (before != CurrentLanSnapshot())
                throw new InvalidOperationException("The project changed while collaboration was starting; start again.");
            host = new LanCollaborationHost(address, media, wire.Json);
            client = await LanCollaborationClient.ConnectAsync(host.Invitation, media, token);
            if (before != CurrentLanSnapshot())
                throw new InvalidOperationException("The project changed while collaboration was starting; start again.");
            _lanHost = host;
            _lanClient = client;
            _lanLocalBaseline = before;
            _lanRevision = client.InitialState.Revision;
            _lanGeneration = client.InitialState.Generation;
            _lanPeerCount = client.InitialState.PeerCount;
            _lanEditor = client.InitialState.EditorId == client.PeerId;
            _lanHasSessionProject = true;
            AttachLanEvents(client, host);
            client.Start();
            StartLanTimer();
            host = null;
            client = null;
            StatusText.Text = "Local collaboration started. Share the invitation privately.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Starting collaboration was cancelled; the project was not changed.";
        }
        catch (Exception ex) when (IsLanFailure(ex))
        {
            StatusText.Text = $"Could not host collaboration: {ex.Message}";
        }
        finally
        {
            if (client is not null) await client.DisposeAsync();
            if (host is not null) await host.DisposeAsync();
            _lanBusy = false;
            if (_lanClient is null)
            {
                _lanCancellation?.Dispose();
                _lanCancellation = null;
            }
            UpdateLanControls();
            StartPendingLanStates();
        }
        if (_lanHost is not null) await ShowLanInvitationAsync();
    }

    private async void JoinLan_Click(object sender, RoutedEventArgs e)
    {
        if (IsLanSessionActive || !PrepareLanOperation()) return;
        var invitationBox = new TextBox
        {
            Header = "Private invitation",
            PlaceholderText = "Paste the invitation from the host",
            MaxLength = 2048,
            MinWidth = 430,
        };
        var panel = new StackPanel { Spacing = 12, MaxWidth = 500 };
        panel.Children.Add(new TextBlock
        {
            Text = "Joining replaces the current project. Save any changes first. " +
                "The host and other invited collaborators can see your project edits and linked media. " +
                "Files are transferred directly and retained on this device. One person edits at a time.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(invitationBox);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Join a local collaboration session",
            Content = panel,
            PrimaryButtonText = "Join and replace project",
            CloseButtonText = "Cancel",
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !PrepareLanOperation()) return;

        LanCollaborationClient? unownedClient = null;
        _lanCancellation = new CancellationTokenSource();
        CancellationToken token = _lanCancellation.Token;
        _lanBusy = true;
        UpdateLanControls();
        try
        {
            string before = CurrentLanSnapshot();
            LanInvitation invitation = LanInvitation.Decode(invitationBox.Text.Trim());
            var media = new LanMediaStore(NewLanMediaDirectory());
            StatusText.Text = "Joining the local session and receiving linked media.";
            unownedClient = await LanCollaborationClient.ConnectAsync(invitation, media, token);
            if (before != CurrentLanSnapshot())
                throw new InvalidOperationException("The current project changed while joining; it has been kept.");
            _lanClient = unownedClient;
            _lanLocalBaseline = before;
            _lanRevision = -1;
            _lanGeneration = -1;
            _lanEditor = false;
            AttachLanEvents(unownedClient, null);
            unownedClient.Start();
            LanState initial = unownedClient.InitialState;
            unownedClient = null;
            _lanApplying = true;
            await ApplyLanStateAsync(_lanClient, initial, replaceCurrentProject: true, token);
            _lanApplying = false;
            StartLanTimer();
        }
        catch (OperationCanceledException)
        {
            await StopLanSessionAsync(
                "Joining was cancelled. The project currently in the editor has been kept; review it before saving.");
        }
        catch (Exception ex) when (IsLanFailure(ex))
        {
            await StopLanSessionAsync(
                $"Could not join collaboration: {ex.Message} The project currently in the editor has been kept; review it before saving.");
        }
        finally
        {
            if (unownedClient is not null) await unownedClient.DisposeAsync();
            _lanApplying = false;
            _lanBusy = false;
            if (_lanClient is null)
            {
                _lanCancellation?.Dispose();
                _lanCancellation = null;
            }
            UpdateLanControls();
            StartPendingLanStates();
        }
    }

    private void AttachLanEvents(LanCollaborationClient client, LanCollaborationHost? host)
    {
        client.StateChanged += state => DispatcherQueue.TryEnqueue(() =>
        {
            if (_lanWindowClosed || !ReferenceEquals(_lanClient, client) || state.Generation < _lanGeneration)
                return;
            if (_lanQueuedState is null || state.Generation >= _lanQueuedState.Generation)
                _lanQueuedState = state;
            StartPendingLanStates();
        });
        client.Disconnected += reason => DispatcherQueue.TryEnqueue(async () =>
        {
            if (!_lanWindowClosed && ReferenceEquals(_lanClient, client))
                await StopLanSessionAsync($"{reason} Local edits remain in the editor; save them as a separate project.");
        });
        if (host is not null)
        {
            host.Faulted += reason => DispatcherQueue.TryEnqueue(() =>
            {
                if (!_lanWindowClosed && ReferenceEquals(_lanHost, host))
                    StatusText.Text = reason;
            });
        }
    }

    private void StartLanTimer()
    {
        _lanTimer ??= DispatcherQueue.CreateTimer();
        _lanTimer.Stop();
        _lanTimer.Interval = TimeSpan.FromMilliseconds(750);
        _lanTimer.Tick -= LanTimer_Tick;
        _lanTimer.Tick += LanTimer_Tick;
        _lanTimer.Start();
    }

    private void LanTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        LanCollaborationClient? client = _lanClient;
        if (client is null || _lanBusy || _lanApplying) return;
        try
        {
            if (!_lanEditor)
            {
                if (_pendingEdit is not null || CurrentLanSnapshot() != _lanLocalBaseline)
                    _ = StopLanSessionAsync("A local edit occurred while waiting. Collaboration stopped without overwriting it; save a separate project.");
                return;
            }
            if (_pendingEdit is not null || _switchingCompound || _compoundContexts.Count > 0 ||
                _lanShareFailed || _lanPublishTask is { IsCompleted: false })
                return;
            EditorProject snapshot = CreateLanSnapshot();
            string json = EditorProjectSerialiser.Serialise(snapshot);
            if (json != _lanLocalBaseline)
            {
                _lanPublishTask = PublishLanSnapshotAsync(client, snapshot, json, _lanCancellation!.Token);
                _ = ResumeLanStatesAfterPublishAsync(_lanPublishTask);
            }
        }
        catch (Exception ex) when (IsLanFailure(ex))
        {
            _lanShareFailed = true;
            StatusText.Text = $"Could not prepare collaboration changes: {ex.Message}";
            UpdateLanControls();
        }
    }

    private async Task<bool> PublishLanSnapshotAsync(
        LanCollaborationClient client, EditorProject snapshot, string json, CancellationToken token)
    {
        try
        {
            LanResult result = await client.PublishAsync(snapshot, LanProjectDirectory(), _lanRevision, token);
            if (!ReferenceEquals(_lanClient, client)) return false;
            if (result.EditorId != client.PeerId || result.Revision != _lanRevision + 1)
                throw new InvalidDataException("The host returned an unexpected project revision.");
            _lanRevision = result.Revision;
            _lanGeneration = Math.Max(_lanGeneration, result.Generation);
            _lanLocalBaseline = json;
            _lanShareFailed = false;
            UpdateLanControls();
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        catch (Exception ex) when (IsLanFailure(ex))
        {
            if (ReferenceEquals(_lanClient, client))
            {
                _lanShareFailed = true;
                StatusText.Text = $"Changes have not been shared: {ex.Message} They remain in this project.";
                UpdateLanControls();
            }
            return false;
        }
    }

    private void StartPendingLanStates()
    {
        if (_lanClient is not null && _lanQueuedState is not null && !_lanBusy && !_lanApplying &&
            _lanPublishTask is not { IsCompleted: false })
            _ = ApplyPendingLanStatesAsync(_lanClient);
    }

    private async Task ResumeLanStatesAfterPublishAsync(Task<bool> publish)
    {
        try { await publish; }
        finally
        {
            if (ReferenceEquals(_lanPublishTask, publish)) StartPendingLanStates();
        }
    }

    private async Task ApplyPendingLanStatesAsync(LanCollaborationClient client)
    {
        _lanApplying = true;
        UpdateLanControls();
        try
        {
            while (ReferenceEquals(_lanClient, client) && _lanQueuedState is LanState state)
            {
                _lanQueuedState = null;
                await ApplyLanStateAsync(client, state, replaceCurrentProject: false, _lanCancellation!.Token);
            }
        }
        catch (OperationCanceledException) when (_lanCancellation?.IsCancellationRequested != false) { }
        catch (Exception ex) when (IsLanFailure(ex))
        {
            await StopLanSessionAsync($"Collaboration stopped: {ex.Message} Local edits remain in the editor.");
        }
        finally
        {
            _lanApplying = false;
            UpdateLanControls();
            StartPendingLanStates();
        }
    }

    private async Task ApplyLanStateAsync(
        LanCollaborationClient client, LanState state, bool replaceCurrentProject, CancellationToken token)
    {
        if (state.Generation < _lanGeneration || state.Revision < _lanRevision) return;
        if (!replaceCurrentProject && state.Revision == _lanRevision)
        {
            if (!_lanEditor && state.EditorId == client.PeerId && CurrentLanSnapshot() != _lanLocalBaseline)
                throw new InvalidOperationException("The local project changed before the editing turn was granted.");
            _lanGeneration = state.Generation;
            _lanPeerCount = state.PeerCount;
            _lanEditor = state.EditorId == client.PeerId;
            UpdateLanControls();
            return;
        }
        if (!replaceCurrentProject && _lanEditor)
            throw new InvalidOperationException("An unexpected remote edit arrived during your editing turn.");
        if (_pendingEdit is not null || CurrentLanSnapshot() != _lanLocalBaseline)
            throw new InvalidOperationException("The local project changed before the remote update could be applied.");

        EditorProject project = await client.ResolveAsync(state.Project, token);
        if (!ReferenceEquals(_lanClient, client)) return;
        if (_pendingEdit is not null || CurrentLanSnapshot() != _lanLocalBaseline)
            throw new InvalidOperationException("The local project changed while remote media was being received.");
        if (!replaceCurrentProject)
        {
            project.SelectedFrameIndex = Math.Min(_selectedFrameIndex, project.Frames.Count - 1);
            if (project.InkLayers.Any(layer => layer.Id == _activeInkLayerId))
                project.ActiveInkLayerId = _activeInkLayerId;
        }
        ClearEditingClipboard();
        ClearHistory();
        if (replaceCurrentProject)
        {
            _projectFile = null;
            _lanHasSessionProject = true;
        }
        RestoreProjectState(project);
        UnsavedIndicator.Visibility = Visibility.Visible;
        _lanLocalBaseline = CurrentLanSnapshot();
        _lanRevision = state.Revision;
        _lanGeneration = state.Generation;
        _lanPeerCount = state.PeerCount;
        _lanEditor = state.EditorId == client.PeerId;
        string? warning = await LoadTimelineSourcesAsync(probeMissingDurations: false);
        if (!ReferenceEquals(_lanClient, client)) return;
        if (_pendingEdit is not null || CurrentLanSnapshot() != _lanLocalBaseline)
            throw new InvalidOperationException("A local edit completed while the remote project was loading.");
        foreach (AudioTrackRuntime track in _audioTracks.ToArray())
            if (track.HasSource && track.WaveformPeaks.Length == 0 && !track.WaveformLoading)
                _ = LoadAudioWaveformAsync(track);
        StatusText.Text = warning is null
            ? "Collaboration project updated. Save a separate project after leaving the session."
            : $"Collaboration project updated: {warning}.";
        UpdateLanControls();
    }

    private async void ClaimLanTurn_Click(object sender, RoutedEventArgs e)
    {
        LanCollaborationClient? client = _lanClient;
        if (client is null || _lanEditor || _lanBusy || _lanApplying || !PrepareLanOperation()) return;
        _lanBusy = true;
        UpdateLanControls();
        bool granted = false;
        try
        {
            if (CurrentLanSnapshot() != _lanLocalBaseline)
                throw new InvalidOperationException("The local project has changed; leave and save it before taking a turn.");
            LanResult result = await client.ClaimAsync(_lanRevision, _lanCancellation!.Token);
            if (!ReferenceEquals(_lanClient, client)) return;
            granted = result.EditorId == client.PeerId;
            if (result.Revision != _lanRevision || result.EditorId != client.PeerId ||
                CurrentLanSnapshot() != _lanLocalBaseline)
                throw new InvalidOperationException("The project changed while taking the editing turn.");
            _lanGeneration = Math.Max(_lanGeneration, result.Generation);
            _lanEditor = true;
            StatusText.Text = "You have the editing turn. Your changes will be shared automatically.";
        }
        catch (OperationCanceledException) when (_lanCancellation?.IsCancellationRequested != false) { }
        catch (Exception ex) when (IsLanFailure(ex))
        {
            if (granted)
                await StopLanSessionAsync($"Collaboration stopped: {ex.Message} Local edits remain in the editor.");
            else
                StatusText.Text = $"Could not take the editing turn: {ex.Message}";
        }
        finally
        {
            _lanBusy = false;
            UpdateLanControls();
            StartPendingLanStates();
        }
    }

    private async void ReleaseLanTurn_Click(object sender, RoutedEventArgs e)
    {
        LanCollaborationClient? client = _lanClient;
        if (client is null || !_lanEditor || _lanBusy || _lanApplying || !PrepareLanOperation()) return;
        CancellationToken token = _lanCancellation!.Token;
        _lanBusy = true;
        UpdateLanControls();
        try
        {
            if (_lanPublishTask is { IsCompleted: false } pending)
            {
                bool published = await pending;
                if (!ReferenceEquals(_lanClient, client)) return;
                if (!published)
                    throw new InvalidOperationException("Resolve the unshared changes before releasing the turn.");
            }
            _lanShareFailed = false;
            while (true)
            {
                EditorProject snapshot = CreateLanSnapshot();
                string json = EditorProjectSerialiser.Serialise(snapshot);
                if (json == _lanLocalBaseline) break;
                bool published = await PublishLanSnapshotAsync(client, snapshot, json, token);
                if (!ReferenceEquals(_lanClient, client)) return;
                if (!published)
                    throw new InvalidOperationException("Resolve the unshared changes before releasing the turn.");
                if (LanLongOperationInProgress)
                    throw new InvalidOperationException("Finish background edits before releasing the turn.");
            }
            LanResult result = await client.ReleaseAsync(_lanRevision, token);
            if (!ReferenceEquals(_lanClient, client)) return;
            if (result.Revision != _lanRevision || result.EditorId is not null)
                throw new InvalidDataException("The host returned an unexpected editing-turn state.");
            _lanGeneration = Math.Max(_lanGeneration, result.Generation);
            _lanEditor = false;
            if (CurrentLanSnapshot() != _lanLocalBaseline)
            {
                await StopLanSessionAsync("A local edit completed while releasing the turn. It has been kept in a separate project.");
                return;
            }
            StatusText.Text = "Editing turn released. Another collaborator can take it.";
        }
        catch (OperationCanceledException) when (_lanCancellation?.IsCancellationRequested != false) { }
        catch (Exception ex) when (IsLanFailure(ex))
        {
            if (ReferenceEquals(_lanClient, client))
                StatusText.Text = $"Could not release the editing turn: {ex.Message}";
        }
        finally
        {
            _lanBusy = false;
            UpdateLanControls();
            StartPendingLanStates();
        }
    }

    private void RetryLan_Click(object sender, RoutedEventArgs e)
    {
        if (_lanClient is null || !_lanEditor) return;
        _lanShareFailed = false;
        StatusText.Text = "Retrying unshared collaboration changes.";
        UpdateLanControls();
    }

    private async void CancelLan_Click(object sender, RoutedEventArgs e)
    {
        _lanCancellation?.Cancel();
        if (_lanClient is not null)
            await StopLanSessionAsync("Collaboration cancelled. Local edits remain in the editor.");
    }

    private async void LeaveLan_Click(object sender, RoutedEventArgs e)
    {
        if (_lanClient is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = _lanHost is null ? "Leave collaboration?" : "Stop hosting?",
            Content = "Unshared changes remain only in this editor. Other collaborators keep the last shared version. " +
                "Downloaded media stays on this device. Save the project separately after leaving.",
            PrimaryButtonText = _lanHost is null ? "Leave session" : "Stop hosting",
            CloseButtonText = "Keep collaborating",
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await StopLanSessionAsync("Collaboration ended. Local edits remain in the editor.");
    }

    private async void ShowLanInvitation_Click(object sender, RoutedEventArgs e) => await ShowLanInvitationAsync();

    private async Task ShowLanInvitationAsync()
    {
        if (_lanHost is null) return;
        string invitation = _lanHost.Invitation.Encode();
        var box = new TextBox
        {
            Header = "Private invitation",
            Text = invitation,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            MinWidth = 430,
            Height = 110,
        };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "This invitation grants access to the project and its linked media. Share it privately; stop hosting to revoke it.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 500,
        });
        panel.Children.Add(box);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Invite a collaborator",
            Content = panel,
            PrimaryButtonText = "Copy invitation",
            CloseButtonText = "Close",
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            try
            {
                var content = new DataPackage();
                content.SetText(invitation);
                Clipboard.SetContent(content);
                StatusText.Text = "Invitation copied. Share it privately with trusted collaborators.";
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                StatusText.Text = $"Could not copy the invitation: {ex.Message}";
            }
        }
    }

    private async Task StopLanSessionAsync(string message)
    {
        LanCollaborationClient? client = _lanClient;
        LanCollaborationHost? host = _lanHost;
        CancellationTokenSource? cancellation = _lanCancellation;
        _lanClient = null;
        _lanHost = null;
        _lanCancellation = null;
        _lanQueuedState = null;
        _lanPublishTask = null;
        _lanEditor = false;
        _lanShareFailed = false;
        _lanTimer?.Stop();
        cancellation?.Cancel();
        if (client is not null && _lanHasSessionProject && !_lanWindowClosed)
        {
            _projectFile = null;
            UnsavedIndicator.Visibility = Visibility.Visible;
        }
        _lanHasSessionProject = false;
        UpdateLanControls();
        try
        {
            if (client is not null) await client.DisposeAsync();
            if (host is not null) await host.DisposeAsync();
        }
        catch (Exception ex) when (IsLanFailure(ex))
        {
            message += $" Closing the connection reported: {ex.Message}";
        }
        finally { cancellation?.Dispose(); }
        if (!_lanWindowClosed) StatusText.Text = message;
    }

    private void CloseLanForWindow()
    {
        _lanWindowClosed = true;
        _ = StopLanSessionAsync(string.Empty);
    }

    private bool CanApplyLanBackgroundEdit()
    {
        if (!_lanBusy && !_lanApplying && (_lanClient is null || _lanEditor)) return true;
        StatusText.Text = "The background edit was not applied because the collaboration project is read-only.";
        return false;
    }

    private void UpdateLanControls()
    {
        if (_lanWindowClosed) return;
        bool active = _lanClient is not null;
        bool readOnly = _lanBusy || _lanApplying || (active && !_lanEditor);
        FileMenu.IsEnabled = !readOnly;
        EditMenu.IsEnabled = !readOnly;
        LanEditorGate.IsEnabled = !readOnly;
        EditorLayoutGrid.IsHitTestVisible = !readOnly;
        VideoActionBar.IsEnabled = !readOnly;
        AudioActionBar.IsEnabled = !readOnly;
        InkActionBar.IsEnabled = !readOnly;
        ExportActionBar.IsEnabled = !readOnly;
        HostLanMenu.IsEnabled = !active && !_lanBusy;
        JoinLanMenu.IsEnabled = !active && !_lanBusy;
        ShowLanInvitationMenu.IsEnabled = _lanHost is not null && !_lanBusy;
        ClaimLanTurnMenu.IsEnabled = active && !_lanEditor && !_lanBusy && !_lanApplying;
        ReleaseLanTurnMenu.IsEnabled = active && _lanEditor && !_lanBusy && !_lanApplying;
        RetryLanMenu.IsEnabled = active && _lanEditor && _lanShareFailed && !_lanBusy;
        CancelLanMenu.IsEnabled = _lanBusy || _lanApplying;
        LeaveLanMenu.IsEnabled = active && !_lanBusy && !_lanApplying;
        LanStatusText.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        LanStatusText.Text = _lanShareFailed ? "Local session: changes not shared" :
            _lanBusy || _lanApplying ? "Local session: synchronising" :
            _lanEditor ? $"Local session: your turn ({_lanPeerCount})" :
            $"Local session: viewing ({_lanPeerCount})";
    }

    private static bool IsLanFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or
        InvalidDataException or InvalidOperationException or ArgumentException or SocketException or AuthenticationException or
        CryptographicException or JsonException or NetworkInformationException or FormatException or OverflowException or
        NotSupportedException or TimeoutException or System.Runtime.InteropServices.COMException;
}
