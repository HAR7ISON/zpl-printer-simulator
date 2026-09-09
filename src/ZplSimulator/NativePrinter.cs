using SkiaSharp;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ZplSimulator;

// GDI printing is usable in a Windows service; System.Drawing.Printing is not supported there.
internal static class NativePrinter
{
    public static void Print(Config config, IReadOnlyList<Page> pages, string jobName, string? outputPath)
    {
        if (!OpenPrinter(config.TargetPrinter, out var printer, IntPtr.Zero)) Fail("OpenPrinter");
        IntPtr mode = IntPtr.Zero, dc = IntPtr.Zero;
        bool started = false;
        try
        {
            int size = DocumentProperties(IntPtr.Zero, printer, config.TargetPrinter, IntPtr.Zero, IntPtr.Zero, 0);
            if (size < 220) throw new InvalidOperationException("Printer returned an invalid DEVMODE.");
            mode = Marshal.AllocHGlobal(size);
            if (DocumentProperties(IntPtr.Zero, printer, config.TargetPrinter, mode, IntPtr.Zero, 2) < 0) Fail("Read printer settings");
            Marshal.WriteInt32(mode, 72, Marshal.ReadInt32(mode, 72) | 0x1110);
            Marshal.WriteInt16(mode, 84, 100); // scaling
            Marshal.WriteInt16(mode, 86, 1);   // quantity handled explicitly per label
            Marshal.WriteInt16(mode, 94, 1);   // simplex
            if (config.PaperMode == "Label") SetPaper(mode, pages[0]);
            dc = CreateDC("WINSPOOL", config.TargetPrinter, null, mode);
            if (dc == IntPtr.Zero) Fail("CreateDC");
            var info = new DocInfo { Size = Marshal.SizeOf<DocInfo>(), Name = jobName, Output = outputPath };
            if (StartDoc(dc, ref info) <= 0) Fail("StartDoc");
            started = true;
            foreach (var page in pages)
            {
                if (config.PaperMode == "Label")
                {
                    SetPaper(mode, page);
                    var reset = ResetDC(dc, mode);
                    if (reset == IntPtr.Zero) Fail("ResetDC");
                    dc = reset;
                }
                int dpiX = GetDeviceCaps(dc, 88), dpiY = GetDeviceCaps(dc, 90);
                int w = (int)Math.Round(page.WidthMm / 25.4 * dpiX), h = (int)Math.Round(page.HeightMm / 25.4 * dpiY);
                // Some drivers silently replace custom sizes. Fail instead of clipping the label.
                if (config.PaperMode == "Label" && (Math.Abs(GetDeviceCaps(dc, 110) - w) > dpiX / 25.4 * 2
                    || Math.Abs(GetDeviceCaps(dc, 111) - h) > dpiY / 25.4 * 2))
                    throw new InvalidOperationException("Target driver did not accept the label's custom paper size. Configure a matching supported form on the target printer.");
                int x = -GetDeviceCaps(dc, 112), y = -GetDeviceCaps(dc, 113);
                if (config.PaperMode == "PrinterDefault")
                {
                    int availableWidth = GetDeviceCaps(dc, 8), availableHeight = GetDeviceCaps(dc, 10);
                    double scale = Math.Min(1, Math.Min((double)availableWidth / w, (double)availableHeight / h));
                    w = (int)Math.Round(w * scale); h = (int)Math.Round(h * scale);
                    x = (availableWidth - w) / 2; y = (availableHeight - h) / 2;
                }
                using var decoded = SKBitmap.Decode(page.Png) ?? throw new InvalidDataException("Invalid rendered PNG.");
                using var bitmap = new SKBitmap(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
                using (var canvas = new SKCanvas(bitmap)) { canvas.Clear(SKColors.White); canvas.DrawBitmap(decoded, 0, 0); }
                var header = new BitmapInfo { Size = 40, Width = bitmap.Width, Height = -bitmap.Height, Planes = 1, BitCount = 32 };
                for (int copy = 0; copy < page.Copies; copy++)
                {
                    if (StartPage(dc) <= 0) Fail("StartPage");
                    // Coordinates are relative to printable area; compensate for physical margins.
                    int result = StretchDIBits(dc, x, y, w, h,
                        0, 0, bitmap.Width, bitmap.Height, bitmap.GetPixels(), ref header, 0, 0x00CC0020);
                    if (result is 0 or -1) Fail("StretchDIBits");
                    if (EndPage(dc) <= 0) Fail("EndPage");
                }
            }
            if (EndDoc(dc) <= 0) Fail("EndDoc");
            started = false;
        }
        finally
        {
            if (started && dc != IntPtr.Zero) AbortDoc(dc);
            if (dc != IntPtr.Zero) DeleteDC(dc);
            if (mode != IntPtr.Zero) Marshal.FreeHGlobal(mode);
            ClosePrinter(printer);
        }
    }

    private static void SetPaper(IntPtr mode, Page page)
    {
        // Public DEVMODEW layout: dmFields=72, orientation=76, size=78, length=80, width=82.
        Marshal.WriteInt32(mode, 72, (Marshal.ReadInt32(mode, 72) & ~0x10000) | 0x11F); // clear form name; orientation, paper, scale, copies
        Marshal.WriteInt16(mode, 76, 1);
        Marshal.WriteInt16(mode, 78, 256); // DMPAPER_USER
        Marshal.WriteInt16(mode, 80, checked((short)Math.Round(page.HeightMm * 10)));
        Marshal.WriteInt16(mode, 82, checked((short)Math.Round(page.WidthMm * 10)));
        Marshal.WriteInt16(mode, 84, 100);
        Marshal.WriteInt16(mode, 86, 1);
    }

    private static void Fail(string operation) => throw new Win32Exception(Marshal.GetLastWin32Error(), operation + " failed");
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo { public int Size; public string Name; public string? Output; public string? DataType; public int Type; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo { public int Size, Width, Height; public short Planes, BitCount; public int Compression, ImageSize, XPels, YPels, Colors, Important; }
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool OpenPrinter(string name, out IntPtr printer, IntPtr defaults);
    [DllImport("winspool.drv")] private static extern bool ClosePrinter(IntPtr printer);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int DocumentProperties(IntPtr window, IntPtr printer, string name, IntPtr output, IntPtr input, int mode);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr mode);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr ResetDC(IntPtr dc, IntPtr mode);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int StartDoc(IntPtr dc, ref DocInfo info);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int StartPage(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int EndPage(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int EndDoc(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int AbortDoc(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr dc, int index);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int StretchDIBits(IntPtr dc, int x, int y, int width, int height, int sx, int sy, int sw, int sh, IntPtr bits, ref BitmapInfo info, uint usage, uint rop);
}
