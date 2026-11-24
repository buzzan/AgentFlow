using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class AgentFlowWindowEvidence
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr window, out Rect bounds);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr above, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);

    public static void Resize(IntPtr window, int width, int height)
    {
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            const uint noMoveNoZOrderNoActivate = 0x16;
            if (!SetWindowPos(window, IntPtr.Zero, 0, 0, width, height, noMoveNoZOrderNoActivate))
                throw new Win32Exception();
        }
        finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }

    public static int Width(IntPtr window)
    {
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            Rect bounds;
            if (!GetWindowRect(window, out bounds)) throw new Win32Exception();
            return bounds.Right - bounds.Left;
        }
        finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }

    public static void Capture(IntPtr window, string file)
    {
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            Rect bounds;
            if (!GetWindowRect(window, out bounds)) throw new Win32Exception();
            using (var image = new Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top))
            using (var graphics = Graphics.FromImage(image))
            {
                var dc = graphics.GetHdc();
                try { if (!PrintWindow(window, dc, 2)) throw new Win32Exception(); }
                finally { graphics.ReleaseHdc(dc); }
                var first = image.GetPixel(0, 0);
                bool hasContent = false;
                for (int y = 0; y < image.Height && !hasContent; y += 8)
                    for (int x = 0; x < image.Width && !hasContent; x += 8)
                        hasContent = image.GetPixel(x, y) != first;
                if (!hasContent) throw new InvalidOperationException("The window capture is blank.");
                image.Save(file, ImageFormat.Png);
            }
        }
        finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }
}
