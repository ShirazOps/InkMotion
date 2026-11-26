using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.Foundation;
using WinRT.Interop;

namespace InkMotion;

public sealed partial class MainWindow
{
    private const long MaximumCutOutPixels = 33_500_000;

    private async void CutOutImage_Click(object sender, RoutedEventArgs e)
    {
        PausePlayback();
        FinishActiveEdit();
        if (OnDeviceModels.UnsupportedReason() is string unsupported)
        {
            StatusText.Text = unsupported;
            return;
        }

        OnDeviceModelStore models = OnDeviceModelStore.Default;
        if (!models.AreInstalled(OnDeviceModels.SegmentationEncoder, OnDeviceModels.SegmentationDecoder))
        {
            StatusText.Text = "Import the MobileSAM encoder and decoder in Ink workspace → AI MODELS first. No model is downloaded automatically.";
            return;
        }

        var imagePicker = new FileOpenPicker();
        foreach (string extension in VideoTimelineEditing.ImageExtensions)
        {
            imagePicker.FileTypeFilter.Add(extension);
        }

        InitializeWithWindow.Initialize(imagePicker, WindowNative.GetWindowHandle(this));
        StorageFile? source = await imagePicker.PickSingleFileAsync();
        if (source is null)
        {
            return;
        }

        try
        {
            byte[] pixels;
            uint width;
            uint height;
            BitmapImage preview = new();
            using (IRandomAccessStream input = await source.OpenAsync(FileAccessMode.Read))
            {
                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);
                width = decoder.OrientedPixelWidth;
                height = decoder.OrientedPixelHeight;
                if (width == 0 || height == 0 || (long)width * height > MaximumCutOutPixels)
                {
                    StatusText.Text = "AI image cut-out supports images up to 33.5 megapixels.";
                    return;
                }

                pixels = (await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Straight,
                    new BitmapTransform(),
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage)).DetachPixelData();
                input.Seek(0);
                await preview.SetSourceAsync(input);
            }

            (Vector2 TopLeft, Vector2 BottomRight)? box = await SelectCutOutBoxAsync(preview, width, height);
            if (box is null)
            {
                return;
            }

            var savePicker = new FileSavePicker
            {
                SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(source.Name) + "-cut-out",
            };
            savePicker.FileTypeChoices.Add("PNG image with transparency", [".png"]);
            InitializeWithWindow.Initialize(savePicker, WindowNative.GetWindowHandle(this));
            StorageFile? destination = await savePicker.PickSaveFileAsync();
            if (destination is null)
            {
                return;
            }

            if (string.Equals(System.IO.Path.GetFullPath(source.Path), System.IO.Path.GetFullPath(destination.Path),
                StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = "Choose a different file so the original image stays unchanged.";
                return;
            }

            StatusText.Text = "Finding the foreground locally…";
            bool[] foreground = await Task.Run(() =>
            {
                using var model = new MobileSamModel(
                    models.GetPath(OnDeviceModels.SegmentationEncoder),
                    models.GetPath(OnDeviceModels.SegmentationDecoder));
                return model.Segment(pixels, checked((int)width), checked((int)height),
                    box.Value.TopLeft, box.Value.BottomRight, previousLowResolution: null).Mask;
            });

            byte[] cutOut = InkImageCutOut.ApplyMaskToAlpha(pixels, checked((int)width), checked((int)height), foreground);
            using IRandomAccessStream output = await destination.OpenAsync(FileAccessMode.ReadWrite);
            output.Size = 0;
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                width,
                height,
                96,
                96,
                cutOut);
            await encoder.FlushAsync();
            StatusText.Text = $"Cut out the foreground object on this device and saved {destination.Name}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or InvalidDataException or NotSupportedException or OverflowException or
            COMException or OnnxRuntimeException)
        {
            StatusText.Text = $"Could not cut out the image: {ex.Message}";
        }
    }

    private async Task<(Vector2 TopLeft, Vector2 BottomRight)?> SelectCutOutBoxAsync(
        BitmapImage preview, uint sourceWidth, uint sourceHeight)
    {
        const double maximumWidth = 640;
        const double maximumHeight = 420;
        double scale = Math.Min(maximumWidth / sourceWidth, maximumHeight / sourceHeight);
        double width = sourceWidth * scale;
        double height = sourceHeight * scale;
        var image = new Image
        {
            Source = preview,
            Width = width,
            Height = height,
            Stretch = Stretch.Fill,
        };
        var overlay = new Canvas
        {
            Width = width,
            Height = height,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        var selection = new Rectangle
        {
            Stroke = new SolidColorBrush(Microsoft.UI.Colors.Orange),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(36, 255, 165, 0)),
            Visibility = Visibility.Collapsed,
        };
        overlay.Children.Add(selection);
        var content = new Grid
        {
            Width = width,
            Height = height,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(image);
        content.Children.Add(overlay);
        Point? start = null;
        Rect? selected = null;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Select the foreground object",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Drag a box around the object to keep. Everything outside the box will be made transparent." },
                    content,
                },
            },
            PrimaryButtonText = "Cut out",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };

        overlay.PointerPressed += (_, args) =>
        {
            start = Clamp(args.GetCurrentPoint(overlay).Position, width, height);
            selected = null;
            selection.Visibility = Visibility.Visible;
            UpdateSelection(selection, start.Value, start.Value);
            overlay.CapturePointer(args.Pointer);
            args.Handled = true;
        };
        overlay.PointerMoved += (_, args) =>
        {
            if (start is not Point origin)
            {
                return;
            }

            Point current = Clamp(args.GetCurrentPoint(overlay).Position, width, height);
            UpdateSelection(selection, origin, current);
            args.Handled = true;
        };
        overlay.PointerReleased += (_, args) =>
        {
            if (start is not Point origin)
            {
                return;
            }

            Point current = Clamp(args.GetCurrentPoint(overlay).Position, width, height);
            UpdateSelection(selection, origin, current);
            double left = Math.Min(origin.X, current.X);
            double top = Math.Min(origin.Y, current.Y);
            double boxWidth = Math.Abs(current.X - origin.X);
            double boxHeight = Math.Abs(current.Y - origin.Y);
            if (boxWidth >= 6 && boxHeight >= 6)
            {
                selected = new Rect(left, top, boxWidth, boxHeight);
                dialog.IsPrimaryButtonEnabled = true;
            }
            else
            {
                selected = null;
                dialog.IsPrimaryButtonEnabled = false;
            }

            start = null;
            overlay.ReleasePointerCapture(args.Pointer);
            args.Handled = true;
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || selected is not Rect box)
        {
            return null;
        }

        return (
            new Vector2((float)(box.Left / width * sourceWidth), (float)(box.Top / height * sourceHeight)),
            new Vector2((float)(box.Right / width * sourceWidth), (float)(box.Bottom / height * sourceHeight)));
    }

    private static Point Clamp(Point point, double width, double height) =>
        new(Math.Clamp(point.X, 0, width), Math.Clamp(point.Y, 0, height));

    private static void UpdateSelection(Rectangle rectangle, Point first, Point second)
    {
        Canvas.SetLeft(rectangle, Math.Min(first.X, second.X));
        Canvas.SetTop(rectangle, Math.Min(first.Y, second.Y));
        rectangle.Width = Math.Abs(second.X - first.X);
        rectangle.Height = Math.Abs(second.Y - first.Y);
    }
}
