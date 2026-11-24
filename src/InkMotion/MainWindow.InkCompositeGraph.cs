using Microsoft.UI.Xaml;

namespace InkMotion;

public sealed partial class MainWindow
{
    private InkCompositeGraph? _compositeGraph;
    private InkCompositeGraphWindow? _compositeGraphWindow;

    private void InkCompositeGraph_Click(object sender, RoutedEventArgs e)
    {
        if (_compositeGraphWindow is null)
        {
            _compositeGraphWindow = new InkCompositeGraphWindow(
                () => _compositeGraph, () => _inkLayers, ApplyCompositeGraph, CommitCompositeGraphEdit);
            _compositeGraphWindow.Closed += (_, _) =>
            {
                CommitCompositeGraphEdit();
                _compositeGraphWindow = null;
            };
            Closed += (_, _) => _compositeGraphWindow?.Close();
        }

        _compositeGraphWindow.Activate();
    }

    private void ApplyCompositeGraph(InkCompositeGraph? graph, bool commit, string status)
    {
        if (_isRestoringProject || _isInitialising) return;
        if (commit) FinishActiveEdit();
        BeginEdit();
        _compositeGraph = graph;
        MarkEditChanged();
        if (commit) CommitEdit();
        UpdateCompositeGraphStatus();
        RenderCurrentInk();
        StatusText.Text = status;
    }

    private void CommitCompositeGraphEdit()
    {
        if (_pendingEdit is not null) CommitEdit();
    }

    private void RefreshCompositeGraphEditor()
    {
        UpdateCompositeGraphStatus();
        _compositeGraphWindow?.Reload();
    }

    private void UpdateCompositeGraphStatus()
    {
        InkCompositeGraphStatusText.Text = _compositeGraph is { Enabled: true }
            ? "Node graph on: it replaces the layer stack in the preview and in exports. Layer blend modes, clipping and occlusion mattes are ignored; edit the graph instead."
            : "Node graph off: the layer stack above decides how ink is composited.";
    }
}
