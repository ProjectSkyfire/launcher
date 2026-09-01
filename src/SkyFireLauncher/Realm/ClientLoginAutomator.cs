using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SkyFireLauncher.Realm;

public static class ClientLoginAutomator
{
    private const int MaxWindowWaitMs = 20000;
    private const int PollDelayMs = 100;
    private const int LoginSubmitDelayMs = 3000;
    private const double PasswordBoxClientX = 0.50;
    private const double PasswordBoxClientY = 0.625;
    private const ushort EnterKey = 0x0D;
    private const ushort ControlKey = 0x11;
    private const ushort AKey = 0x41;
    private const uint MouseEventAbsolute = 0x8000;
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const int SwRestore = 9;

    public static void SubmitPasswordWhenReady(int processId, string password)
    {
        if (string.IsNullOrEmpty(password))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                var windowHandle = await WaitForMainWindowAsync(process);
                if (windowHandle == IntPtr.Zero)
                    return;

                await Task.Delay(LoginSubmitDelayMs);
                ActivateWindow(windowHandle);
                await Task.Delay(150);
                ClickPasswordBox(windowHandle);
                await Task.Delay(100);
                SelectExistingText();
                SendText(password);
                SendKey(EnterKey);
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (Win32Exception)
            {
            }
        });
    }

    private static async Task<IntPtr> WaitForMainWindowAsync(Process process)
    {
        var waitedMs = 0;
        while (waitedMs < MaxWindowWaitMs)
        {
            process.Refresh();
            if (process.HasExited)
                return IntPtr.Zero;

            if (process.MainWindowHandle != IntPtr.Zero)
                return process.MainWindowHandle;

            await Task.Delay(PollDelayMs);
            waitedMs += PollDelayMs;
        }

        return IntPtr.Zero;
    }

    private static void ActivateWindow(IntPtr windowHandle)
    {
        ShowWindow(windowHandle, SwRestore);
        AllowSetForegroundWindow((uint)GetWindowProcessId(windowHandle));

        var foregroundWindow = GetForegroundWindow();
        var foregroundThreadId = foregroundWindow == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foregroundWindow, out _);
        var targetThreadId = GetWindowThreadProcessId(windowHandle, out _);
        var currentThreadId = GetCurrentThreadId();

        if (foregroundThreadId != 0)
            AttachThreadInput(currentThreadId, foregroundThreadId, true);

        AttachThreadInput(currentThreadId, targetThreadId, true);
        try
        {
            SetForegroundWindow(windowHandle);
            BringWindowToTop(windowHandle);
            SetFocus(windowHandle);
        }
        finally
        {
            AttachThreadInput(currentThreadId, targetThreadId, false);
            if (foregroundThreadId != 0)
                AttachThreadInput(currentThreadId, foregroundThreadId, false);
        }
    }

    private static void ClickPasswordBox(IntPtr windowHandle)
    {
        if (!GetClientRect(windowHandle, out var rect))
            return;

        var point = new POINT
        {
            X = rect.Left + (int)((rect.Right - rect.Left) * PasswordBoxClientX),
            Y = rect.Top + (int)((rect.Bottom - rect.Top) * PasswordBoxClientY)
        };

        if (!ClientToScreen(windowHandle, ref point))
            return;

        SendMouseClick(point.X, point.Y);
    }

    private static void SendMouseClick(int x, int y)
    {
        var normalizedX = x * 65535 / Math.Max(GetSystemMetrics(SystemMetric.SM_CXSCREEN) - 1, 1);
        var normalizedY = y * 65535 / Math.Max(GetSystemMetrics(SystemMetric.SM_CYSCREEN) - 1, 1);

        SendInputs(
        [
            CreateMouseInput(normalizedX, normalizedY, MouseEventAbsolute | MouseEventMove),
            CreateMouseInput(normalizedX, normalizedY, MouseEventAbsolute | MouseEventLeftDown),
            CreateMouseInput(normalizedX, normalizedY, MouseEventAbsolute | MouseEventLeftUp)
        ]);
    }

    private static void SelectExistingText()
    {
        SendInputs(
        [
            CreateVirtualKeyInput(ControlKey, keyUp: false),
            CreateVirtualKeyInput(AKey, keyUp: false),
            CreateVirtualKeyInput(AKey, keyUp: true),
            CreateVirtualKeyInput(ControlKey, keyUp: true)
        ]);
    }

    private static void SendText(string text)
    {
        foreach (var character in text)
            SendCharacter(character);
    }

    private static void SendKey(ushort virtualKey)
    {
        SendInputs(
        [
            CreateVirtualKeyInput(virtualKey, keyUp: false),
            CreateVirtualKeyInput(virtualKey, keyUp: true)
        ]);
    }

    private static void SendCharacter(char character)
    {
        var key = VkKeyScan(character);
        if (key == -1)
        {
            SendInputs(
            [
                CreateUnicodeInput(character, keyUp: false),
                CreateUnicodeInput(character, keyUp: true)
            ]);
            return;
        }

        var virtualKey = (ushort)(key & 0xFF);
        var shiftState = (key >> 8) & 0xFF;
        var inputs = new List<INPUT>();

        AddModifierInputs(inputs, shiftState, keyUp: false);
        inputs.Add(CreateVirtualKeyInput(virtualKey, keyUp: false));
        inputs.Add(CreateVirtualKeyInput(virtualKey, keyUp: true));
        AddModifierInputs(inputs, shiftState, keyUp: true);

        SendInputs(inputs.ToArray());
    }

    private static void AddModifierInputs(List<INPUT> inputs, int shiftState, bool keyUp)
    {
        if ((shiftState & 1) != 0)
            inputs.Add(CreateVirtualKeyInput(0x10, keyUp));

        if ((shiftState & 2) != 0)
            inputs.Add(CreateVirtualKeyInput(ControlKey, keyUp));

        if ((shiftState & 4) != 0)
            inputs.Add(CreateVirtualKeyInput(0x12, keyUp));
    }

    private static INPUT CreateUnicodeInput(char character, bool keyUp) => new()
    {
        Type = InputType.Keyboard,
        Data = new INPUTUNION
        {
            Keyboard = new KEYBDINPUT
            {
                Scan = character,
                Flags = KEYEVENTF.Unicode | (keyUp ? KEYEVENTF.KeyUp : 0)
            }
        }
    };

    private static INPUT CreateVirtualKeyInput(ushort virtualKey, bool keyUp) => new()
    {
        Type = InputType.Keyboard,
        Data = new INPUTUNION
        {
            Keyboard = new KEYBDINPUT
            {
                Vk = virtualKey,
                Flags = keyUp ? KEYEVENTF.KeyUp : 0
            }
        }
    };

    private static INPUT CreateMouseInput(int x, int y, uint flags) => new()
    {
        Type = InputType.Mouse,
        Data = new INPUTUNION
        {
            Mouse = new MOUSEINPUT
            {
                X = x,
                Y = y,
                Flags = flags
            }
        }
    };

    private static void SendInputs(INPUT[] inputs)
    {
        if (inputs.Length == 0)
            return;

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern short VkKeyScan(char ch);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(SystemMetric nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private static uint GetWindowProcessId(IntPtr windowHandle)
    {
        GetWindowThreadProcessId(windowHandle, out var processId);
        return processId;
    }

    private enum InputType : uint
    {
        Mouse = 0,
        Keyboard = 1
    }

    [Flags]
    private enum KEYEVENTF : uint
    {
        KeyUp = 0x0002,
        Unicode = 0x0004
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public InputType Type;
        public INPUTUNION Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)]
        public MOUSEINPUT Mouse;

        [FieldOffset(0)]
        public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort Vk;
        public ushort Scan;
        public KEYEVENTF Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private enum SystemMetric
    {
        SM_CXSCREEN = 0,
        SM_CYSCREEN = 1
    }
}
