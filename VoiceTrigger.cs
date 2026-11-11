using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace SapphireNotch.Windows;

/// <summary>
/// Hold-to-talk trigger. Windows cannot see the Fn key (keyboards handle it in firmware), so the
/// trigger is a modifier combination chosen in Settings. A low-level keyboard hook watches the
/// physical keys system-wide and never swallows them.
/// </summary>
internal sealed class VoiceTrigger : IDisposable
{
    private delegate nint HookProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHook
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint Extra;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int id, HookProc proc, nint module, uint thread);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);

    private const int WhKeyboardLl = 13;
    private const uint InjectedFlag = 0x10;
    private const int KeyDown = 0x0100, KeyUp = 0x0101, SysKeyDown = 0x0104, SysKeyUp = 0x0105;

    // A short hold, so brushing the keys while typing shortcuts never starts a note.
    private readonly DispatcherTimer holdTimer = new() { Interval = TimeSpan.FromMilliseconds(280) };
    private readonly HookProc proc;
    private nint hook;
    private bool leftCtrl, rightCtrl, leftAlt, rightAlt, leftWin, rightWin;
    private bool active;

    public event Action? Started;
    public event Action? Stopped;

    /// <summary>"CtrlRightAlt" (now Ctrl + either Alt), "CtrlWin" or "BothCtrl".</summary>
    public string Trigger { get; set; } = "CtrlRightAlt";

    public bool Enabled { get; set; } = true;

    public VoiceTrigger()
    {
        proc = OnKey; // keep the delegate alive for as long as the hook exists
        hook = SetWindowsHookEx(WhKeyboardLl, proc, GetModuleHandle(null), 0);
        holdTimer.Tick += (_, _) =>
        {
            holdTimer.Stop();
            if (Enabled && Satisfied() && !active)
            {
                active = true;
                Started?.Invoke();
            }
        };
    }

    public bool Installed => hook != 0;

    private bool Satisfied() => Trigger switch
    {
        "CtrlWin" => (leftCtrl || rightCtrl) && (leftWin || rightWin),
        "BothCtrl" => leftCtrl && rightCtrl,
        _ => (leftCtrl || rightCtrl) && (leftAlt || rightAlt) // Ctrl + Alt, either side
    };

    private nint OnKey(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            KeyboardHook data = Marshal.PtrToStructure<KeyboardHook>(lParam);
            // our own injected key presses (used to start voice typing) must not retrigger us
            if ((data.Flags & InjectedFlag) == 0)
            {
                int message = (int)wParam;
                bool down = message == KeyDown || message == SysKeyDown;
                bool up = message == KeyUp || message == SysKeyUp;
                if (down || up)
                {
                    switch (data.VirtualKey)
                    {
                        case 0xA2: leftCtrl = down; break;
                        case 0xA3: rightCtrl = down; break;
                        case 0xA4: leftAlt = down; break;
                        case 0xA5: rightAlt = down; break;
                        case 0x5B: leftWin = down; break;
                        case 0x5C: rightWin = down; break;
                        default: return CallNextHookEx(hook, code, wParam, lParam);
                    }
                    if (Satisfied())
                    {
                        if (!active && !holdTimer.IsEnabled)
                        {
                            holdTimer.Start();
                        }
                    }
                    else
                    {
                        holdTimer.Stop();
                        if (active)
                        {
                            active = false;
                            Stopped?.Invoke();
                        }
                    }
                }
            }
        }
        return CallNextHookEx(hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        holdTimer.Stop();
        if (hook != 0)
        {
            UnhookWindowsHookEx(hook);
            hook = 0;
        }
    }
}
