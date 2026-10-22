using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace InkMotion;

public sealed partial class MainWindow
{
    private bool _isInkLasso;
    private readonly List<InkPoint> _inkLasso = [];
    private List<List<InkPoint>> _selectedInkStrokes = [];
    private List<InkStrokePartition> _partialInkSelection = [];
    private List<List<InkPoint>> _copiedInkSelection = [];
    private EditorFrame? _inkSelectionFrame;
    private string? _inkSelectionLayerId;

    private void ClearInkSelection()
    {
        _selectedInkStrokes.Clear();
        _partialInkSelection.Clear();
        _inkLasso.Clear();
        _inkSelectionFrame = null;
        _inkSelectionLayerId = null;
    }

    private bool HasCurrentInkSelection =>
        ReferenceEquals(CurrentFrame, _inkSelectionFrame) && _activeInkLayerId == _inkSelectionLayerId &&
        _selectedInkStrokes.Count > 0 && (_partialInkSelection.Count > 0
            ? _partialInkSelection.All(partition => GetLayerStrokes(CurrentFrame, _activeInkLayerId).Contains(partition.Original))
            : _selectedInkStrokes.All(stroke => GetLayerStrokes(CurrentFrame, _activeInkLayerId).Contains(stroke)));

    private void SelectInkLasso_Click(object sender, RoutedEventArgs e)
    {
        PausePlayback();
        FinishActiveEdit();
        ClearInkSelection();
        _isInkLasso = true;
        _isInkBucketFill = false;
        _isInkColourSampler = false;
        _isInkTextPicker = false;
        _inkShape = null;
        _isEraser = false;
        StatusText.Text = "Lasso selected. Draw a loop on the active cel and layer using the inspector's whole/partial stroke mode; text is excluded.";
        InkSurface.Invalidate();
    }

    private void AddLassoPoint(Point point)
    {
        if (InkSurface.ActualWidth <= 0 || InkSurface.ActualHeight <= 0)
        {
            return;
        }

        var next = new InkPoint
        {
            X = Math.Clamp(point.X / InkSurface.ActualWidth, 0, 1),
            Y = Math.Clamp(point.Y / InkSurface.ActualHeight, 0, 1),
        };
        if (_inkLasso.Count == 0 || Math.Abs(_inkLasso[^1].X - next.X) + Math.Abs(_inkLasso[^1].Y - next.Y) > 0.002)
        {
            _inkLasso.Add(next);
        }

        InkSurface.Invalidate();
    }

    private void FinishInkLasso()
    {
        _inkSelectionFrame = CurrentFrame;
        _inkSelectionLayerId = _activeInkLayerId;
        bool partial = InkLassoModeSelector.SelectedIndex == 1;
        _partialInkSelection = partial
            ? InkSelectionEditing.SelectPartial(GetLayerStrokes(CurrentFrame, _activeInkLayerId), _inkLasso)
            : [];
        _selectedInkStrokes = partial
            ? _partialInkSelection.SelectMany(partition => partition.Selected).ToList()
            : InkSelectionEditing.Select(GetLayerStrokes(CurrentFrame, _activeInkLayerId), _inkLasso);
        _inkLasso.Clear();
        StatusText.Text = $"{_selectedInkStrokes.Count} {(partial ? "stroke portion(s)" : "whole stroke(s)")} selected on the active layer. Copy does not alter drawings; splitting occurs only when transforming or deleting.";
        InkSurface.Invalidate();
    }

    private void InkSelectionAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action })
        {
            return;
        }

        PausePlayback();
        FinishActiveEdit();
        if (action == "all")
        {
            ClearInkSelection();
            _inkSelectionFrame = CurrentFrame;
            _inkSelectionLayerId = _activeInkLayerId;
            _selectedInkStrokes = GetLayerStrokes(CurrentFrame, _activeInkLayerId).ToList();
            StatusText.Text = $"{_selectedInkStrokes.Count} whole stroke(s) selected.";
            InkSurface.Invalidate();
            return;
        }

        if (action == "paste")
        {
            if (!EnsureInkLayerEditable())
            {
                return;
            }

            if (_copiedInkSelection.Count == 0)
            {
                StatusText.Text = "Copy an ink stroke selection first.";
                return;
            }

            BeginEdit();
            PromoteInterpolatedFrameForEditing();
            ClearInkSelection();
            List<List<InkPoint>> copies = InkEditing.CloneStrokes(_copiedInkSelection);
            CurrentStrokes.AddRange(copies);
            _selectedInkStrokes = copies;
            _inkSelectionFrame = CurrentFrame;
            _inkSelectionLayerId = _activeInkLayerId;
            MarkEditChanged();
            CommitEdit();
            RenderCurrentInk();
            StatusText.Text = "Stroke selection pasted onto the active cel and layer. Use offsets to move it.";
            return;
        }

        if (!HasCurrentInkSelection)
        {
            ClearInkSelection();
            StatusText.Text = "Select strokes on the current cel and layer first.";
            InkSurface.Invalidate();
            return;
        }

        if (action == "copy")
        {
            _copiedInkSelection = InkEditing.CloneStrokes(_selectedInkStrokes);
            StatusText.Text = "Stroke selection copied with its pressure and brush styling.";
            return;
        }

        if (!EnsureInkLayerEditable())
        {
            return;
        }

        if (action == "delete")
        {
            BeginEdit();
            InkSelectionEditing.ApplyPartitions(CurrentStrokes, _partialInkSelection);
            CurrentStrokes.RemoveAll(stroke => _selectedInkStrokes.Contains(stroke));
            ClearInkSelection();
        }
        else
        {
            double x = InkSelectionXBox.Value / 100;
            double y = InkSelectionYBox.Value / 100;
            double scale = InkSelectionScaleBox.Value / 100;
            double angle = InkSelectionAngleBox.Value;
            if (new[] { x, y, scale, angle }.Any(value => !double.IsFinite(value)) || scale <= 0 ||
                InkSurface.ActualWidth <= 0 || InkSurface.ActualHeight <= 0)
            {
                StatusText.Text = "Use finite selection offsets/rotation and a positive scale.";
                return;
            }

            if (x == 0 && y == 0 && scale == 1 && angle == 0)
            {
                StatusText.Text = "The selection transform is unchanged.";
                return;
            }

            List<List<InkPoint>> transformed = InkEditing.CloneStrokes(_selectedInkStrokes);
            if (!InkSelectionEditing.Transform(transformed, x, y, scale, angle,
                InkSurface.ActualWidth, InkSurface.ActualHeight))
            {
                StatusText.Text = "The transformed stroke paths would leave the canvas. Reduce the offset or scale.";
                return;
            }

            BeginEdit();
            InkSelectionEditing.ApplyPartitions(CurrentStrokes, _partialInkSelection);
            for (int stroke = 0; stroke < _selectedInkStrokes.Count; stroke++)
            {
                for (int point = 0; point < _selectedInkStrokes[stroke].Count; point++)
                {
                    _selectedInkStrokes[stroke][point].X = transformed[stroke][point].X;
                    _selectedInkStrokes[stroke][point].Y = transformed[stroke][point].Y;
                }
            }

            _partialInkSelection.Clear();
        }

        MarkEditChanged();
        CommitEdit();
        RenderCurrentInk();
        StatusText.Text = action == "delete" ? "Selected strokes deleted; other layers and text are unchanged." : "Stroke selection transformed.";
    }

    private void DrawInkSelection(CanvasDrawingSession session, float width, float height)
    {
        if (_workspace != "Ink" || _isPlaying || IsOriginalPreview)
        {
            return;
        }

        var colour = ToColour(0xFF64C8FF);
        for (int index = 1; index < _inkLasso.Count; index++)
        {
            session.DrawLine((float)_inkLasso[index - 1].X * width, (float)_inkLasso[index - 1].Y * height,
                (float)_inkLasso[index].X * width, (float)_inkLasso[index].Y * height, colour, 1);
        }

        if (HasCurrentInkSelection)
        {
            List<InkPoint> points = _selectedInkStrokes.SelectMany(stroke => stroke).ToList();
            if (points.Count > 0)
            {
                float x0 = (float)points.Min(point => point.X) * width;
                float y0 = (float)points.Min(point => point.Y) * height;
                float x1 = (float)points.Max(point => point.X) * width;
                float y1 = (float)points.Max(point => point.Y) * height;
                session.DrawRectangle(x0 - 2, y0 - 2, x1 - x0 + 4, y1 - y0 + 4, colour, 1);
            }
        }
    }
}
