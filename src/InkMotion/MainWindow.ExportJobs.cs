using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkMotion;

public sealed partial class MainWindow
{
    private readonly ExportJobStore _exportJobStore = new();
    private DispatcherQueueTimer? _exportJobsTimer;
    private bool _refreshingExportJobs;
    private bool _exportJobsClosed;
    private bool _selectingExportJob;
    private string? _selectedExportJobId;
    private string? _exportSubmissionError;
    private bool _exportJobsInitialScan = true;

    private void InitialiseExportJobs()
    {
        _exportJobsTimer = DispatcherQueue.CreateTimer();
        _exportJobsTimer.Interval = TimeSpan.FromSeconds(1);
        _exportJobsTimer.Tick += async (_, _) => await RefreshExportJobsAsync();
        _exportJobsTimer.Start();
        _ = RefreshExportJobsAsync();
    }

    private async Task SubmitExportJobAsync(EditorProject project, string outputPath,
        AudioExportSettings? audio, ExportVideoSettings? video)
    {
        _submittingExport = true;
        _exportSubmissionError = null;
        SetExportRunning(true);
        try
        {
            string executable = Path.Combine(AppContext.BaseDirectory, "InkMotion.exe");
            ExportJobStatus job = await Task.Run(() => _exportJobStore.Launch(project, outputPath, audio, video, executable));
            _selectedExportJobId = job.Id;
            StatusText.Text = "Export worker started. Export continues if the editor closes.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            _exportSubmissionError = $"Could not submit export: {ex.Message}";
            ExportStatusText.Text = _exportSubmissionError;
            StatusText.Text = "Export submission failed.";
        }
        finally
        {
            _submittingExport = false;
            SetExportRunning(_hasActiveExports);
        }

        await RefreshExportJobsAsync();
    }

    private async Task RefreshExportJobsAsync()
    {
        if (_refreshingExportJobs || _submittingExport || _exportJobsClosed)
        {
            return;
        }

        _refreshingExportJobs = true;
        try
        {
            ExportJobScan scan = await Task.Run(_exportJobStore.Scan);
            if (_exportJobsClosed)
            {
                return;
            }

            _hasActiveExports = scan.Jobs.Any(job => job.IsActive);
            if (_exportJobsInitialScan && _hasActiveExports)
            {
                StatusText.Text = $"Reconnected to {scan.Jobs.Count(job => job.IsActive)} active export job(s). Open Export to monitor or cancel.";
            }
            _exportJobsInitialScan = false;
            SetExportRunning(_hasActiveExports);
            _selectingExportJob = true;
            try
            {
                ExportJobsSelector.ItemsSource = scan.Jobs;
                ExportJobsSelector.SelectedItem = scan.Jobs.FirstOrDefault(job => job.Id == _selectedExportJobId)
                    ?? scan.Jobs.FirstOrDefault();
            }
            finally
            {
                _selectingExportJob = false;
            }

            ExportJobsSummaryText.Text = $"{scan.Jobs.Count(job => job.IsActive)} active · {scan.Jobs.Count} saved jobs." +
                (scan.Errors.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, scan.Errors)) +
                (_exportSubmissionError is null ? "" : Environment.NewLine + _exportSubmissionError);
            UpdateExportJobDisplay();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ExportJobsSummaryText.Text = $"Could not scan export jobs: {ex.Message}";
        }
        finally
        {
            _refreshingExportJobs = false;
        }
    }

    private void ExportJob_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_selectingExportJob)
        {
            UpdateExportJobDisplay();
        }
    }

    private void UpdateExportJobDisplay()
    {
        if (ExportJobsSelector.SelectedItem is not ExportJobStatus job)
        {
            return;
        }

        _selectedExportJobId = job.Id;
        ExportStatusText.Visibility = Visibility.Visible;
        bool cancelling = job.IsActive && _exportJobStore.IsCancellationRequested(job.Id);
        ExportStatusText.Text = $"{job.OutputPath}{Environment.NewLine}{job.Phase}: {job.Message}" +
            (cancelling ? Environment.NewLine + "Cancellation requested; waiting for the worker." : "");
        ExportProgressBar.Visibility = job.IsActive ? Visibility.Visible : Visibility.Collapsed;
        ExportProgressBar.IsIndeterminate = job.Phase == ExportJobPhase.Queued;
        ExportProgressBar.Value = job.Fraction;
        CancelExportButton.Visibility = job.IsActive ? Visibility.Visible : Visibility.Collapsed;
        CancelExportButton.IsEnabled = job.IsActive && !cancelling;
        RetryExportJobButton.IsEnabled = !_submittingExport && !_hasActiveExports &&
            job.Phase is ExportJobPhase.Failed or ExportJobPhase.Cancelled or ExportJobPhase.Interrupted;
    }

    private async void RetryExportJob_Click(object sender, RoutedEventArgs e)
    {
        if (_submittingExport || _hasActiveExports || ExportJobsSelector.SelectedItem is not ExportJobStatus job)
        {
            return;
        }

        _submittingExport = true;
        _exportSubmissionError = null;
        RetryExportJobButton.IsEnabled = false;
        SetExportRunning(true);
        try
        {
            ExportJobStatus retry = await Task.Run(() => _exportJobStore.Retry(job.Id,
                Path.Combine(AppContext.BaseDirectory, "InkMotion.exe")));
            _selectedExportJobId = retry.Id;
            StatusText.Text = "Retry started from the saved export snapshot, not the current editor.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        {
            _exportSubmissionError = $"Could not retry export: {ex.Message}";
        }
        finally
        {
            _submittingExport = false;
            SetExportRunning(_hasActiveExports);
        }

        await RefreshExportJobsAsync();
    }
}
