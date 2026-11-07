using System.Runtime.InteropServices;

internal sealed class BackgroundPlayerWindow : IDisposable
{
    private const int ShowWithoutActivation = 4;
    private const int MinimizeAndActivateNext = 6;
    private const int MinimizeWithoutActivation = 7;
    private const int PreviousWindow = 3;
    private const uint LockForeground = 1;
    private const uint UnlockForeground = 2;
    private const uint NoSize = 0x0001;
    private const uint NoMove = 0x0002;
    private const uint NoActivate = 0x0010;
    private const uint NoOwnerZOrder = 0x0200;
    private static readonly nint Bottom = new(1);

    private readonly nint window;
    private readonly uint owner;
    private readonly nint previousForeground;
    private readonly nint above;
    private readonly bool minimized;
    private readonly uint? lastInput;
    private readonly bool focusLocked;

    private BackgroundPlayerWindow(nint window, uint owner, nint foreground, uint foregroundOwner)
    {
        this.window = window;
        this.owner = owner;
        previousForeground = foreground;
        minimized = IsIconic(window);
        above = minimized ? Bottom : GetWindow(window, PreviousWindow);
        lastInput = ReadLastInput();
        focusLocked = foregroundOwner == Environment.ProcessId && LockSetForegroundWindow(LockForeground);
    }

    public static BackgroundPlayerWindow? TryBegin(nint window)
    {
        nint foreground = GetForegroundWindow();
        GetWindowThreadProcessId(window, out uint owner);
        GetWindowThreadProcessId(foreground, out uint foregroundOwner);
        if (owner == 0 || owner == foregroundOwner || foreground == 0)
            return null;

        var background = new BackgroundPlayerWindow(window, owner, foreground, foregroundOwner);
        // The player explicitly activates a minimized window on receiving a link.
        if (background.minimized)
            background.ShowBehind();
        return background;
    }

    public void KeepBehind()
    {
        if (!CanReturnFocus())
            return;

        if (IsWindow(previousForeground) && SetForegroundWindow(previousForeground))
            SendBehind();
    }

    public void Dispose()
    {
        try
        {
            KeepBehind();
            if (!SameWindow())
                return;
            if (CanReturnFocus())
            {
                ShowWindow(window, MinimizeAndActivateNext);
                if (!minimized)
                {
                    // Give Windows time to activate the previous window before showing the player again.
                    for (int attempt = 0; attempt < 20 && GetForegroundWindow() == window; attempt++)
                        Thread.Sleep(10);
                    if (SameWindow() && GetForegroundWindow() != window && IsIconic(window))
                        ShowBehind();
                }
            }
            else if (minimized && GetForegroundWindow() != window && !IsIconic(window))
            {
                ShowWindow(window, MinimizeWithoutActivation);
            }
        }
        finally
        {
            if (focusLocked)
                LockSetForegroundWindow(UnlockForeground);
        }
    }

    private void ShowBehind()
    {
        SendBehind();
        ShowWindow(window, ShowWithoutActivation);
    }

    private void SendBehind()
    {
        nint insertAfter = above != 0 && IsWindow(above) ? above : Bottom;
        SetWindowPos(window, insertAfter, 0, 0, 0, 0, NoSize | NoMove | NoActivate | NoOwnerZOrder);
    }

    private bool SameWindow() =>
        GetWindowThreadProcessId(window, out uint currentOwner) != 0 && currentOwner == owner;

    private bool CanReturnFocus() =>
        GetForegroundWindow() == window && SameWindow() && lastInput is not null && ReadLastInput() == lastInput;

    private static uint? ReadLastInput()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? info.Tick : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Tick;
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool LockSetForegroundWindow(uint code);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(
        nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
