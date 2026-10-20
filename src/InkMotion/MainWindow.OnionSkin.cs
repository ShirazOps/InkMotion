using Microsoft.UI.Xaml.Controls;

namespace InkMotion;

public sealed partial class MainWindow
{
    private int _onionPreviousCount = 1;
    private int _onionNextCount = 1;

    private void OnionCount_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_isInitialising)
        {
            return;
        }

        bool previous = ReferenceEquals(sender, OnionPreviousCountBox);
        if (!double.IsFinite(e.NewValue) || e.NewValue != Math.Truncate(e.NewValue) || e.NewValue is < 0 or > 8)
        {
            sender.Value = previous ? _onionPreviousCount : _onionNextCount;
            StatusText.Text = "Use 0-8 whole cels for each onion-skin direction.";
            return;
        }

        if (previous)
        {
            _onionPreviousCount = (int)e.NewValue;
        }
        else
        {
            _onionNextCount = (int)e.NewValue;
        }

        RenderCurrentInk();
    }
}
