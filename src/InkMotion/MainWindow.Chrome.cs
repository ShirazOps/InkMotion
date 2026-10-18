using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace InkMotion;

public sealed partial class MainWindow
{
    private void ConfigureTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }

        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        AppWindowTitleBar titleBar = AppWindow.TitleBar;
        titleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(0xFF, 0xEC, 0xEF, 0xF4);
        titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(0xFF, 0x6E, 0x76, 0x86);
        titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0xFF, 0x26, 0x2B, 0x33);
        titleBar.ButtonHoverForegroundColor = Colors.White;
        titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0xFF, 0x30, 0x36, 0x40);
    }

    private void TitleBar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTitleBarPassthrough();

    // The whole title bar drags the window, so the menu and page buttons must be marked as passthrough to stay clickable.
    private void UpdateTitleBarPassthrough()
    {
        if (AppTitleBar.XamlRoot is null || !AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        double scale = AppTitleBar.XamlRoot.RasterizationScale;
        CaptionButtonsColumn.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);

        RectInt32[] passthrough =
        [
            GetPhysicalBounds(AppMenuBar, scale),
            GetPhysicalBounds(PageSwitcher, scale),
        ];
        InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
            .SetRegionRects(NonClientRegionKind.Passthrough, passthrough);
    }

    private static RectInt32 GetPhysicalBounds(FrameworkElement element, double scale)
    {
        Windows.Foundation.Rect bounds = element.TransformToVisual(null)
            .TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return new RectInt32(
            (int)Math.Round(bounds.X * scale),
            (int)Math.Round(bounds.Y * scale),
            (int)Math.Round(bounds.Width * scale),
            (int)Math.Round(bounds.Height * scale));
    }

    private void SetPlayButtonState(bool isPlaying)
    {
        string label = isPlaying ? "Pause" : "Play";
        PlayButtonIcon.Glyph = isPlaying ? "\uE769" : "\uE768";
        ToolTipService.SetToolTip(PlayButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PlayButton, label);
    }
}
