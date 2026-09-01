using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SkyFireLauncher.Realm;

public static class ClientLoginAutomator
{
    private const int MaxWindowWaitMs = 20000;
    private const int PollDelayMs = 100;
    private const int LoginSubmitDelayMs = 3000;
    private const ushort EnterKey = 0x0D;

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
                SetForegroundWindow(windowHandle);
                await Task.Delay(150);
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

    private static void SendText(string text)
    {
        var inputs = new INPUT[text.Length * 2];
        var index = 0;

        foreach (var character in text)
        {
            inputs[index++] = CreateUnicodeInput(character, keyUp: false);
            inputs[index++] = CreateUnicodeInput(character, keyUp: true);
        }

        SendInputs(inputs);
    }

    private static void SendKey(ushort virtualKey)
    {
        SendInputs(
        [
            CreateVirtualKeyInput(virtualKey, keyUp: false),
            CreateVirtualKeyInput(virtualKey, keyUp: true)
        ]);
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private enum InputType : uint
    {
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
        public KEYBDINPUT Keyboard;
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
}
