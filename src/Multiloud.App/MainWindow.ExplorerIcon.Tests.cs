using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Multiloud.App;

public partial class MainWindow
{
    private void VerifyExplorerIconSmoke()
    {
        static IconBitmapDecoder Decode(string asset)
        {
            using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/" + asset))!.Stream;
            return new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        }
        static byte[] Pixels(BitmapSource bitmap)
        {
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
            converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
            return pixels;
        }
        static bool Contrast(byte[] pixels)
        {
            int white = 0, black = 0, transparent = 0;
            for (int index = 0; index < pixels.Length; index += 4)
            {
                if (pixels[index + 3] == 0) transparent++;
                if (pixels[index + 3] < 128) continue;
                if (pixels[index] > 235 && pixels[index + 1] > 235 && pixels[index + 2] > 235) white++;
                if (pixels[index] < 45 && pixels[index + 1] < 45 && pixels[index + 2] < 45) black++;
            }
            return white >= 4 && black >= 8 && transparent >= 4;
        }

        var outlined = Decode("ExplorerIcon.ico");
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
        if (!outlined.Frames.Select(frame => frame.PixelWidth).Order().SequenceEqual(sizes))
            throw new InvalidOperationException("Explorer icon smoke: the executable icon lost its Windows size variants.");
        foreach (var frame in outlined.Frames)
            if (frame.PixelWidth != frame.PixelHeight || !Contrast(Pixels(frame)))
                throw new InvalidOperationException($"Explorer icon smoke: {frame.PixelWidth}px lost the black symbol, white outline or transparency.");

        var original = Decode("AppIcon.ico").Frames.Single(frame => frame.PixelWidth == 256);
        var source = Pixels(original);
        var result = Pixels(outlined.Frames.Single(frame => frame.PixelWidth == 256));
        int checkedPixels = 0;
        for (int y = 3; y < 253; y++)
            for (int x = 3; x < 253; x++)
            {
                int index = (y * 256 + x) * 4;
                bool interior = true;
                for (int dy = -3; dy <= 3 && interior; dy++)
                    for (int dx = -3; dx <= 3; dx++)
                        if (source[((y + dy) * 256 + x + dx) * 4 + 3] < 254) { interior = false; break; }
                if (!interior) continue;
                checkedPixels++;
                if (result[index + 3] < 254 || Enumerable.Range(0, 3).Any(channel => Math.Abs(source[index + channel] - result[index + channel]) > 2))
                    throw new InvalidOperationException("Explorer icon smoke: outlining altered the original symbol interior.");
            }
        if (checkedPixels < 1000)
            throw new InvalidOperationException("Explorer icon smoke: the original symbol disappeared or its geometry changed.");

        // Test the actual PE resource Explorer extracts, not just the WPF copy.
        var handles = new IntPtr[1];
        if (ExtractExplorerIcon(Environment.ProcessPath!, 0, handles, null, 1) != 1 || handles[0] == IntPtr.Zero)
            throw new InvalidOperationException("Explorer icon smoke: the published executable contains no extractable icon.");
        try
        {
            var icon = Imaging.CreateBitmapSourceFromHIcon(handles[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            if (!Contrast(Pixels(icon)))
                throw new InvalidOperationException("Explorer icon smoke: the executable still embeds the unoutlined symbol.");
        }
        finally { DestroyExplorerIcon(handles[0]); }
    }

    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)]
    private static extern uint ExtractExplorerIcon(string file, int index, [Out] IntPtr[] large, [Out] IntPtr[]? small, uint count);

    [DllImport("user32.dll", EntryPoint = "DestroyIcon")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyExplorerIcon(IntPtr icon);
}
