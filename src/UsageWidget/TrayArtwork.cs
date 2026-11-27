using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace UsageWidget;

internal readonly record struct TrayAppearance(int Pixels, bool Light, bool HighContrast, Color TextColor)
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string name, string? title);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    internal static TrayAppearance Read()
    {
        uint dpi = GetDpiForWindow(FindWindow("Shell_TrayWnd", null));
        int pixels = GetSystemMetricsForDpi(49, dpi == 0 ? 96 : dpi); // SM_CXSMICON, at the taskbar's scale.
        using var theme = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return new(Math.Clamp(pixels, 16, 128), theme?.GetValue("SystemUsesLightTheme") is int value && value != 0,
            SystemParameters.HighContrast, SystemColors.WindowTextColor);
    }
}

internal sealed class TrayIconHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal TrayIconHandle(IntPtr handle) : base(true) => SetHandle(handle);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    protected override bool ReleaseHandle() => DestroyIcon(handle);
}

internal static class TrayArtwork
{
    private static readonly Typeface Digits = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    internal static BitmapSource Render(UsageProvider provider, string? number, TrayAppearance appearance)
    {
        Color color = appearance.HighContrast ? appearance.TextColor : provider switch {
            UsageProvider.Claude => appearance.Light ? Color.FromRgb(0x9B, 0x48, 0x25) : Color.FromRgb(0xE8, 0xA2, 0x80),
            UsageProvider.Codex => appearance.Light ? Color.FromRgb(0x17, 0x65, 0x4A) : Color.FromRgb(0x9B, 0xDC, 0xBF),
            _ => appearance.Light ? Color.FromRgb(0x34, 0x58, 0xC4) : Color.FromRgb(0xA0, 0xB8, 0xF6),
        };
        var brush = new SolidColorBrush(color);
        var visual = new DrawingVisual();
        TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Grayscale);
        TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Display);
        double scale = appearance.Pixels / 16.0;
        using (var drawing = visual.RenderOpen())
        {
            if (number is null)
            {
                drawing.PushTransform(new ScaleTransform(16.0 / 24, 16.0 / 24));
                drawing.DrawGeometry(brush, null, BrandMarks.For(provider));
                drawing.Pop();
            }
            else
            {
                var text = new FormattedText(number, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Digits, 13, brush, scale);
                // Three digits (100) must fit without clipping, including at 100% display scaling.
                if (text.Width > 16) text.SetFontSize(13 * 16 / text.Width);
                drawing.DrawText(text, new Point((16 - text.Width) / 2, (16 - text.Height) / 2));
            }
        }
        var bitmap = new RenderTargetBitmap(appearance.Pixels, appearance.Pixels, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, ImageSize; public int XPels, YPels; public uint UsedColors, ImportantColors;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo { public int IsIcon; public uint X, Y; public IntPtr Mask, Color; }
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[] bits);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreateIconIndirect(ref IconInfo info);

    internal static TrayIconHandle Create(UsageProvider provider, string? number, TrayAppearance appearance)
    {
        var bitmap = Render(provider, number, appearance);
        int size = appearance.Pixels, stride = size * 4;
        var pixels = new byte[stride * size];
        bitmap.CopyPixels(pixels, stride, 0);
        var info = new BitmapInfo { Size = (uint)Marshal.SizeOf<BitmapInfo>(), Width = size, Height = -size, Planes = 1, BitCount = 32 };
        IntPtr color = IntPtr.Zero, mask = IntPtr.Zero;
        try
        {
            color = CreateDIBSection(IntPtr.Zero, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (color == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            mask = CreateBitmap(size, size, 1, 1, new byte[((size + 15) / 16) * 2 * size]);
            if (mask == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var iconInfo = new IconInfo { IsIcon = 1, Color = color, Mask = mask };
            var icon = CreateIconIndirect(ref iconInfo);
            if (icon == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            return new TrayIconHandle(icon);
        }
        finally
        {
            // CreateIconIndirect copies both bitmaps; only the returned HICON lives with the tray entry.
            if (mask != IntPtr.Zero) DeleteObject(mask);
            if (color != IntPtr.Zero) DeleteObject(color);
        }
    }
}
