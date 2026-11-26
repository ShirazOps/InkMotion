using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace InkMotion;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, IReadOnlyList<Vst3Parameter>> _vst3ParameterCache =
        new(StringComparer.OrdinalIgnoreCase);
    private string? _selectedVst3PluginId;
    private bool _updatingVst3ParameterControls;

    private void UpdateVst3PluginInspector(AudioTrackProject? track)
    {
        Vst3PluginTarget? target = GetVst3PluginTarget(track);
        Vst3PluginsPanel.Children.Clear();
        Vst3PluginControlsPanel.Children.Clear();
        AddVst3PluginButton.IsEnabled = target is not null;
        OpenVst3PluginEditorButton.IsEnabled = false;
        if (target is null)
        {
            Vst3PluginStatusText.Text = "Select an audio clip or bus to manage VST3 inserts.";
            return;
        }

        AudioVst3PluginProject[] plugins = target.GetPlugins();
        if (plugins.Length == 0)
        {
            _selectedVst3PluginId = null;
            Vst3PluginStatusText.Text = "No VST3 inserts on this target.";
            return;
        }

        if (!plugins.Any(plugin => plugin.Id == _selectedVst3PluginId))
        {
            _selectedVst3PluginId = plugins[0].Id;
        }

        foreach (AudioVst3PluginProject plugin in plugins)
        {
            var name = new Button
            {
                Content = plugin.Name,
                Tag = new Vst3PluginActionContext(target, plugin),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            name.Click += SelectVst3Plugin_Click;
            var enabled = new ToggleButton
            {
                Content = plugin.Enabled ? "Enabled" : "Bypassed",
                IsChecked = plugin.Enabled,
                Tag = new Vst3PluginActionContext(target, plugin),
                VerticalAlignment = VerticalAlignment.Center,
            };
            enabled.Click += Vst3PluginEnabled_Click;
            var remove = new Button
            {
                Content = "Remove",
                Tag = new Vst3PluginActionContext(target, plugin),
                VerticalAlignment = VerticalAlignment.Center,
            };
            remove.Click += RemoveVst3Plugin_Click;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(name);
            row.Children.Add(enabled);
            row.Children.Add(remove);
            Vst3PluginsPanel.Children.Add(row);
        }

        AudioVst3PluginProject selected = plugins.First(plugin => plugin.Id == _selectedVst3PluginId);
        RenderVst3PluginControls(target, selected);
    }

    private async void AddVst3Plugin_Click(object sender, RoutedEventArgs e)
    {
        Vst3PluginTarget? target = GetVst3PluginTarget(
            TryGetSelectedAudioClip(out AudioTrackRuntime? track) ? track.Project : null);
        if (target is null)
        {
            return;
        }

        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        string modulePath;
        try
        {
            modulePath = Vst3Host.ResolveModulePath(folder.Path);
        }
        catch (Exception exception) when (IsVst3UiFailure(exception))
        {
            Vst3PluginStatusText.Text = $"Could not locate a compatible VST3 module: {exception.Message}";
            return;
        }

        ContentDialog scanConsent = CreateVst3ConsentDialog(
            "VST3 plug-in code will be loaded to scan this bundle. The worker runs out of process but is not a security sandbox; the plug-in has your Windows account's permissions. Only continue if you trust this plug-in.");
        if (await scanConsent.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        Vst3PluginStatusText.Text = $"Scanning {Path.GetFileName(Path.GetDirectoryName(modulePath))}...";
        AddVst3PluginButton.IsEnabled = false;
        try
        {
            IReadOnlyList<Vst3PluginDescription> effects =
                await Task.Run(() => Vst3Host.ScanModule(modulePath));
            if (effects.Count == 0)
            {
                Vst3PluginStatusText.Text = "The selected VST3 bundle contains no audio effects.";
                return;
            }

            Vst3PluginDescription? selectedEffect = await ChooseVst3EffectAsync(effects);
            if (selectedEffect is null)
            {
                return;
            }

            ContentDialog loadConsent = CreateVst3ConsentDialog(
                $"Enable {selectedEffect.Name} for this project? Its code will run as your Windows account permits, in a separate process that is not a security sandbox. This approval is stored on this device for this plug-in.");
            if (await loadConsent.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            Vst3PluginTrustStore.Trust(selectedEffect);
            var insert = new AudioVst3PluginProject
            {
                ModulePath = selectedEffect.ModulePath,
                ClassId = selectedEffect.ClassId,
                Name = selectedEffect.Name,
                Enabled = true,
            };
            ApplyVst3PluginListEdit(target, () =>
            {
                target.GetPluginList().Add(insert);
                return true;
            },
                $"{insert.Name} added to the VST3 insert chain.",
                $"Could not add {insert.Name}.");
            _selectedVst3PluginId = insert.Id;
            UpdateVst3PluginInspector(target.Track);
        }
        catch (Exception exception) when (IsVst3UiFailure(exception))
        {
            Vst3PluginStatusText.Text = $"VST3 plug-in could not be added: {exception.Message}";
        }
        finally
        {
            AddVst3PluginButton.IsEnabled = GetVst3PluginTarget(
                TryGetSelectedAudioClip(out AudioTrackRuntime? selectedTrack) ? selectedTrack.Project : null) is not null;
        }
    }

    private void OpenVst3PluginEditor_Click(object sender, RoutedEventArgs e)
    {
        Vst3PluginTarget? target = GetVst3PluginTarget(
            TryGetSelectedAudioClip(out AudioTrackRuntime? track) ? track.Project : null);
        AudioVst3PluginProject? plugin = target?.GetPlugins()
            .FirstOrDefault(candidate => candidate.Id == _selectedVst3PluginId);
        if (target is null || plugin is null)
        {
            return;
        }

        try
        {
            if (!Vst3PluginTrustStore.TryIsTrusted(
                plugin.GetDescription(), out bool trusted, out string? trustFailure))
            {
                Vst3PluginStatusText.Text =
                    $"{plugin.Name} approval could not be checked; its editor remains closed. {trustFailure}";
                return;
            }

            if (!trusted)
            {
                Vst3PluginStatusText.Text = $"{plugin.Name} must be approved on this device before its editor can be opened.";
                return;
            }

            if (_vst3Editors.ContainsKey(plugin))
            {
                Vst3PluginStatusText.Text = $"{plugin.Name} editor is already open.";
                return;
            }

            var editor = new Vst3PluginEditor(plugin.GetDescription(), plugin.ParameterValues);
            editor.ParameterChanged += (id, value) =>
                DispatcherQueue.TryEnqueue(() => ApplyNativeVst3Parameter(target, plugin.Id, id, value));
            editor.StatusChanged += message => DispatcherQueue.TryEnqueue(() =>
                Vst3PluginStatusText.Text = $"{plugin.Name}: {message}");
            editor.Exited += () => DispatcherQueue.TryEnqueue(() =>
            {
                if (_vst3Editors.TryGetValue(plugin, out Vst3PluginEditor? activeEditor) &&
                    ReferenceEquals(activeEditor, editor))
                {
                    _vst3Editors.Remove(plugin);
                    editor.Dispose();
                    UpdateVst3PluginInspector(target.Track);
                }
            });
            _vst3Editors.Add(plugin, editor);
            try
            {
                editor.Start();
            }
            catch
            {
                _vst3Editors.Remove(plugin);
                editor.Dispose();
                throw;
            }
            Vst3PluginStatusText.Text = $"Opening the native editor for {plugin.Name}...";
        }
        catch (Exception exception) when (IsVst3UiFailure(exception))
        {
            Vst3PluginStatusText.Text = $"{plugin.Name} native editor could not be opened: {exception.Message}";
        }
    }

    private async Task<Vst3PluginDescription?> ChooseVst3EffectAsync(
        IReadOnlyList<Vst3PluginDescription> effects)
    {
        if (effects.Count == 1)
        {
            return effects[0];
        }

        var selector = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (Vst3PluginDescription effect in effects)
        {
            selector.Items.Add(new ComboBoxItem
            {
                Content = $"{effect.Name} — {effect.Vendor}",
                Tag = effect,
            });
        }

        selector.SelectedIndex = 0;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Choose a VST3 effect",
            Content = selector,
            PrimaryButtonText = "Add effect",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary &&
            selector.SelectedItem is ComboBoxItem { Tag: Vst3PluginDescription selected }
                ? selected
                : null;
    }

    private async void Vst3PluginEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: Vst3PluginActionContext context } toggle)
        {
            return;
        }

        bool enable = toggle.IsChecked == true;
        Vst3PluginDescription description = context.Plugin.GetDescription();
        bool trusted = false;
        if (enable)
        {
            if (!Vst3PluginTrustStore.TryIsTrusted(
                description, out trusted, out string? trustFailure))
            {
                toggle.IsChecked = false;
                toggle.Content = "Bypassed";
                Vst3PluginStatusText.Text =
                    $"{context.Plugin.Name} approval could not be checked; the insert remains bypassed. {trustFailure}";
                return;
            }

            if (!trusted)
            {
                ContentDialog consent = CreateVst3ConsentDialog(
                    $"Enable {context.Plugin.Name}? Its code runs with your Windows account's permissions. The separate worker process is not a security sandbox. Approval is stored only on this device.");
                if (await consent.ShowAsync() != ContentDialogResult.Primary)
                {
                    toggle.IsChecked = false;
                    toggle.Content = "Bypassed";
                    return;
                }

                try
                {
                    Vst3PluginTrustStore.Trust(description);
                }
                catch (Exception exception) when (IsVst3UiFailure(exception))
                {
                    toggle.IsChecked = false;
                    toggle.Content = "Bypassed";
                    Vst3PluginStatusText.Text = $"Could not save plug-in approval: {exception.Message}";
                    return;
                }
            }
        }

        toggle.Content = enable ? "Enabled" : "Bypassed";
        ApplyVst3PluginListEdit(context.Target, () => context.Plugin.Enabled = enable,
            enable ? $"{context.Plugin.Name} enabled." : $"{context.Plugin.Name} bypassed.",
            $"Could not change {context.Plugin.Name}.");
    }

    private void RemoveVst3Plugin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Vst3PluginActionContext context })
        {
            return;
        }

        ApplyVst3PluginListEdit(context.Target,
            () =>
            {
                bool removed = context.Target.GetPluginList().Remove(context.Plugin);
                if (removed && _vst3Editors.Remove(context.Plugin, out Vst3PluginEditor? editor))
                {
                    editor.Dispose();
                }
                if (removed && _selectedVst3PluginId == context.Plugin.Id)
                {
                    _selectedVst3PluginId = null;
                }

                return removed;
            }, $"{context.Plugin.Name} removed from the VST3 insert chain.",
            $"Could not remove {context.Plugin.Name}.");
    }

    private void SelectVst3Plugin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Vst3PluginActionContext context })
        {
            _selectedVst3PluginId = context.Plugin.Id;
            UpdateVst3PluginInspector(context.Target.Track);
        }
    }

    private void RenderVst3PluginControls(Vst3PluginTarget target, AudioVst3PluginProject plugin)
    {
        Vst3PluginControlsPanel.Children.Clear();
        Vst3PluginControlsPanel.Children.Add(new TextBlock
        {
            Text = $"{plugin.Name} · generic parameter controls",
        });

        if (!Vst3PluginTrustStore.TryIsTrusted(
            plugin.GetDescription(), out bool isTrusted, out string? trustFailure))
        {
            Vst3PluginStatusText.Text = $"Could not read local plug-in approvals: {trustFailure}";
            return;
        }

        OpenVst3PluginEditorButton.IsEnabled = isTrusted;
        if (!isTrusted)
        {
            Vst3PluginStatusText.Text = $"{plugin.Name} is bypassed until you approve it locally.";
            return;
        }

        IReadOnlyList<Vst3Parameter> parameters;
        try
        {
            string key = $"{plugin.ModulePath}|{plugin.ClassId}";
            if (!_vst3ParameterCache.TryGetValue(key, out IReadOnlyList<Vst3Parameter>? cachedParameters))
            {
                parameters = FindAudioTrackRuntime(target.Track)?.Player is AudioClipPlayer player
                    ? player.GetVst3Parameters(plugin.Id)
                    : target.Bus is AudioBusProject bus &&
                      GetPreviewSubmix(bus.Id) is AudioPreviewSubmix submix
                        ? submix.GetVst3Parameters(plugin.Id)
                        : Vst3Host.GetParameters(plugin.GetDescription());
                _vst3ParameterCache[key] = parameters;
            }
            else
            {
                parameters = cachedParameters;
            }
        }
        catch (Exception exception) when (IsVst3UiFailure(exception))
        {
            Vst3PluginStatusText.Text = $"{plugin.Name} controls are unavailable: {exception.Message}";
            return;
        }

        foreach (Vst3Parameter parameter in parameters.Where(parameter => (parameter.Flags & (1u << 4)) == 0))
        {
            double value = plugin.ParameterValues.GetValueOrDefault(parameter.Id, parameter.NormalizedValue);
            var label = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(parameter.Units)
                    ? parameter.Title
                    : $"{parameter.Title} ({parameter.Units})",
                TextWrapping = TextWrapping.Wrap,
            };
            var readout = new TextBlock
            {
                Text = $"{value:P0}",
                VerticalAlignment = VerticalAlignment.Center,
                Width = 48,
            };
            var slider = new Slider
            {
                Minimum = 0,
                Maximum = 1,
                Value = value,
                StepFrequency = parameter.StepCount > 0 ? 1d / parameter.StepCount : 0.01,
                IsEnabled = plugin.Enabled && (parameter.Flags & (1u << 1)) == 0,
                Tag = new Vst3ParameterActionContext(target, plugin, parameter, readout),
            };
            slider.ValueChanged += Vst3Parameter_ValueChanged;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(slider);
            row.Children.Add(readout);
            Vst3PluginControlsPanel.Children.Add(label);
            Vst3PluginControlsPanel.Children.Add(row);
        }

        Vst3PluginStatusText.Text = FindAudioTrackRuntime(target.Track)?.Player?.Vst3FailureMessage ??
            (target.Bus is AudioBusProject selectedBus
                ? GetPreviewSubmix(selectedBus.Id)?.Vst3FailureMessage
                : null) ??
            (parameters.Count == 0 ? $"{plugin.Name} exposes no generic parameters." : $"{plugin.Name}: {parameters.Count} parameter(s).");
    }

    private void Vst3Parameter_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingVst3ParameterControls ||
            sender is not Slider { Tag: Vst3ParameterActionContext context })
        {
            return;
        }

        double value = e.NewValue;
        context.Readout.Text = $"{value:P0}";
        if (context.Target.Track is AudioTrackProject track)
        {
            AudioTrackRuntime? runtime = FindAudioTrackRuntime(track);
            if (runtime is null)
            {
                return;
            }

            FinishActiveEdit();
            BeginEdit();
            context.Plugin.ParameterValues[context.Parameter.Id] = value;
            SetNativeVst3EditorParameter(context.Plugin, context.Parameter.Id, value);
            UpdateAudioTrackPlayers(runtime);
            MarkEditChanged();
            CommitEdit();
            return;
        }

        if (context.Target.Bus is AudioBusProject bus)
        {
            FinishActiveEdit();
            BeginEdit();
            context.Plugin.ParameterValues[context.Parameter.Id] = value;
            SetNativeVst3EditorParameter(context.Plugin, context.Parameter.Id, value);
            if (GetPreviewSubmix(bus.Id) is AudioPreviewSubmix submix)
            {
                submix.Update(bus.Effects, bus.GainDecibels, bus.Vst3Plugins);
            }

            MarkEditChanged();
            CommitEdit();
        }
    }

    private void ApplyVst3PluginListEdit(
        Vst3PluginTarget target,
        Func<bool> edit,
        string successMessage,
        string failureMessage)
    {
        if (target.Bus is AudioBusProject bus)
        {
            ApplyAudioBusEdit(edit, successMessage, failureMessage);
            UpdateVst3PluginInspector(target.Track);
            return;
        }

        if (target.Track is AudioTrackProject track)
        {
            ApplyAudioClipEdit(runtime =>
                ReferenceEquals(runtime.Project, track) && edit(), successMessage, failureMessage);
            return;
        }

        throw new InvalidOperationException("The selected VST3 insert target is unavailable.");
    }

    private void ApplyNativeVst3Parameter(
        Vst3PluginTarget target,
        string pluginId,
        uint parameterId,
        double value)
    {
        if (!target.GetPluginList().Any(plugin => plugin.Id == pluginId))
        {
            return;
        }

        bool UpdatePlugin()
        {
            AudioVst3PluginProject? plugin = target.GetPluginList()
                .FirstOrDefault(candidate => candidate.Id == pluginId);
            if (plugin is null)
            {
                return false;
            }

            plugin.ParameterValues[parameterId] = value;
            return true;
        }

        FinishActiveEdit();
        BeginEdit();
        if (!UpdatePlugin())
        {
            CommitEdit();
            return;
        }

        if (target.Track is AudioTrackProject track)
        {
            AudioTrackRuntime? runtime = FindAudioTrackRuntime(track);
            if (runtime is null)
            {
                CommitEdit();
                return;
            }

            UpdateAudioTrackPlayers(runtime);
        }
        else if (target.Bus is AudioBusProject bus &&
            GetPreviewSubmix(bus.Id) is AudioPreviewSubmix submix)
        {
            submix.Update(bus.Effects, bus.GainDecibels, bus.Vst3Plugins);
        }

        MarkEditChanged();
        CommitEdit();
        UpdateNativeVst3ParameterControl(target, pluginId, parameterId, value);
    }

    private void UpdateNativeVst3ParameterControl(
        Vst3PluginTarget target,
        string pluginId,
        uint parameterId,
        double value)
    {
        foreach (Slider slider in Vst3PluginControlsPanel.Children
            .OfType<StackPanel>()
            .SelectMany(panel => panel.Children.OfType<Slider>()))
        {
            if (slider.Tag is not Vst3ParameterActionContext context ||
                context.Plugin.Id != pluginId ||
                !ReferenceEquals(context.Target.Track, target.Track) ||
                !ReferenceEquals(context.Target.Bus, target.Bus) ||
                context.Parameter.Id != parameterId)
            {
                continue;
            }

            _updatingVst3ParameterControls = true;
            slider.Value = value;
            context.Readout.Text = $"{value:P0}";
            _updatingVst3ParameterControls = false;
        }
    }

    private void SetNativeVst3EditorParameter(
        AudioVst3PluginProject plugin,
        uint parameterId,
        double value)
    {
        if (_vst3Editors.TryGetValue(plugin, out Vst3PluginEditor? editor))
        {
            try
            {
                editor.SetParameter(parameterId, value);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or
                System.ComponentModel.Win32Exception or ObjectDisposedException)
            {
                Vst3PluginStatusText.Text = $"Could not update the native VST3 editor: {exception.Message}";
            }
        }
    }

    private Vst3PluginTarget? GetVst3PluginTarget(AudioTrackProject? track)
    {
        AudioBusProject? bus = AudioBuses.Find(_audioBuses, _audioEffectsBusId);
        return bus is not null
            ? new Vst3PluginTarget(null, bus)
            : track is null ? null : new Vst3PluginTarget(track, null);
    }

    private AudioTrackRuntime? FindAudioTrackRuntime(AudioTrackProject? project) =>
        project is null ? null : _audioTracks.FirstOrDefault(track => ReferenceEquals(track.Project, project));

    private ContentDialog CreateVst3ConsentDialog(string message) => new()
    {
        XamlRoot = Content.XamlRoot,
        Title = "VST3 plug-in security",
        Content = message,
        PrimaryButtonText = "I trust this plug-in",
        CloseButtonText = "Cancel",
        DefaultButton = ContentDialogButton.Close,
    };

    private void DisposeVst3Editors()
    {
        foreach (Vst3PluginEditor editor in _vst3Editors.Values)
        {
            editor.Dispose();
        }
        _vst3Editors.Clear();
    }

    private static bool IsVst3UiFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidDataException or
            InvalidOperationException or NotSupportedException or PlatformNotSupportedException or
            ArgumentException or TimeoutException or OperationCanceledException or
            Win32Exception or COMException or JsonException;

    private sealed record Vst3PluginTarget(AudioTrackProject? Track, AudioBusProject? Bus)
    {
        public AudioVst3PluginProject[] GetPlugins() =>
            (Track?.Vst3Plugins ?? Bus?.Vst3Plugins ?? []).ToArray();

        public List<AudioVst3PluginProject> GetPluginList()
        {
            if (Track is not null)
            {
                return Track.Vst3Plugins ??= [];
            }

            if (Bus is not null)
            {
                return Bus.Vst3Plugins ??= [];
            }

            throw new InvalidOperationException("The selected VST3 insert target is unavailable.");
        }
    }

    private sealed record Vst3PluginActionContext(Vst3PluginTarget Target, AudioVst3PluginProject Plugin);

    private sealed record Vst3ParameterActionContext(
        Vst3PluginTarget Target,
        AudioVst3PluginProject Plugin,
        Vst3Parameter Parameter,
        TextBlock Readout);
}
