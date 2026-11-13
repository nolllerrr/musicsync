using System.Runtime.InteropServices;
using System.Windows.Interop;

internal partial class PatchWindow
{
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        const int extendedStyle = -20;
        const int dialogModalFrame = 0x00000001;
        const uint setIcon = 0x0080;
        const uint frameChanged = 0x0020 | 0x0001 | 0x0002 | 0x0004; // Keep size, position, and z-order.

        // Suppress the caption icon and its fallback without replacing the native title bar.
        SetWindowLongW(handle, extendedStyle, GetWindowLongW(handle, extendedStyle) | dialogModalFrame);
        SendMessageW(handle, setIcon, IntPtr.Zero, IntPtr.Zero);
        SendMessageW(handle, setIcon, new IntPtr(1), IntPtr.Zero);
        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, frameChanged);
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetWindowLongW(IntPtr window, int index);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int SetWindowLongW(IntPtr window, int index, int value);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
