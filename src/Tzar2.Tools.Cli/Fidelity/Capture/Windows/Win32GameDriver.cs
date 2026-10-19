using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Tzar2.Tools.Cli.Fidelity.Imaging;
using Tzar2.Tools.Cli.Fidelity.Scenarios;
using static Tzar2.Tools.Cli.Fidelity.Capture.Windows.NativeMethods;

namespace Tzar2.Tools.Cli.Fidelity.Capture.Windows;

/// <summary>
/// <see cref="IGameDriver"/> on Win32: Process.Start, EnumWindows, SendInput, PrintWindow/BitBlt and
/// ReadProcessMemory (black-box only; nothing is written into the game process).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class Win32GameDriver : IGameDriver
{
    private static int s_dpiSet;
    private Process? _process;
    private readonly bool _ownsProcess;
    private nint _hwnd;
    private Win32MemoryReader? _memory;

    public Win32GameDriver()
    {
        EnsureDpiAware();
        _ownsProcess = true;
    }

    /// <summary>Physical pixels for every coordinate and capture (a DPI-unaware harness would see scaled values).</summary>
    public static void EnsureDpiAware()
    {
        if (Interlocked.Exchange(ref s_dpiSet, 1) == 0)
        {
            SetProcessDpiAwarenessContext(DpiAwarenessPerMonitorV2);
        }
    }

    /// <summary>Attaches to an existing window of an existing process (self-test).</summary>
    internal Win32GameDriver(Process process, nint hwnd)
        : this()
    {
        _process = process;
        _hwnd = hwnd;
        _ownsProcess = false;
    }

    /// <summary>How the last window capture was taken (PrintWindow or screen BitBlt), for the log.</summary>
    public string LastCaptureMethod { get; private set; } = "none";

    public void Launch(string exePath, string workingDirectory, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(exePath) { WorkingDirectory = workingDirectory, UseShellExecute = false };
        foreach (string a in arguments)
        {
            info.ArgumentList.Add(a);
        }

        _process = Process.Start(info) ?? throw new InvalidOperationException($"could not start {exePath}");
    }

    public bool HasExited => _process is null || _process.HasExited;

    public int? ExitCode => _process is { HasExited: true } p ? p.ExitCode : null;

    public bool WaitForWindow(TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (HasExited)
            {
                return false;
            }

            nint found = FindLargestWindow((uint)_process!.Id);
            if (found != 0)
            {
                _hwnd = found;
                return true;
            }

            Thread.Sleep(250);
        }

        return false;
    }

    public (int Width, int Height) ClientSize
    {
        get
        {
            RefreshWindow();
            return _hwnd != 0 && GetClientRect(_hwnd, out Rect r) ? (r.Right - r.Left, r.Bottom - r.Top) : (0, 0);
        }
    }

    public bool Focus()
    {
        RefreshWindow();
        if (_hwnd == 0)
        {
            return false;
        }

        if (GetForegroundWindow() == _hwnd)
        {
            return true;
        }

        if (IsIconic(_hwnd))
        {
            ShowWindow(_hwnd, SwRestore);
        }

        // The launcher may normally hand the foreground to the game it started; when Windows refuses, an Alt tap (sent
        // to the current foreground window, not the game) and a thread-input attach are the documented fallbacks.
        BringWindowToTop(_hwnd);
        if (SetForegroundWindow(_hwnd) && GetForegroundWindow() == _hwnd)
        {
            return true;
        }

        SendKey(0x12, true);
        SendKey(0x12, false);
        nint foreground = GetForegroundWindow();
        uint foregroundThread = foreground != 0 ? GetWindowThreadProcessId(foreground, out _) : 0;
        uint self = GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != self && AttachThreadInput(self, foregroundThread, true);
        BringWindowToTop(_hwnd);
        SetForegroundWindow(_hwnd);
        if (attached)
        {
            AttachThreadInput(self, foregroundThread, false);
        }

        Thread.Sleep(100);
        return GetForegroundWindow() == _hwnd;
    }

    public void Key(ushort virtualKey, bool down)
    {
        EnsureForeground();
        SendKey(virtualKey, down);
    }

    public void Type(string text)
    {
        EnsureForeground();
        nint layout = UseUsLayout();
        foreach (char c in text)
        {
            short scan = VkKeyScanExW(c, layout);
            if (scan == -1)
            {
                throw new ArgumentException($"character '{c}' cannot be typed on the US keyboard layout");
            }

            ushort vk = (ushort)(scan & 0xFF);
            bool shift = (scan & 0x100) != 0;
            if (shift)
            {
                SendKey(0x10, true);
            }

            SendKey(vk, true);
            Thread.Sleep(25);
            SendKey(vk, false);
            if (shift)
            {
                SendKey(0x10, false);
            }

            Thread.Sleep(25);
        }
    }

    /// <summary>
    /// Switches the window's thread to the US layout and returns it. The game turns the virtual keys it receives into
    /// characters with its own thread's layout, and the operator's may not be Latin at all: on a Russian layout the
    /// chat cheats arrived as Cyrillic and VkKeyScanW could not even map 'a' (operator run, 2026-10-18). With Windows'
    /// default per-user layout switching this also switches the desktop's layout for the rest of the session.
    /// </summary>
    private nint UseUsLayout()
    {
        nint us = LoadKeyboardLayoutW("00000409", KlfNoTellShell);
        if (us == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "the US keyboard layout (00000409) could not be loaded");
        }

        uint thread = GetWindowThreadProcessId(_hwnd, out _);
        if (IsUs(GetKeyboardLayout(thread)))
        {
            return us;
        }

        PostMessageW(_hwnd, WmInputLangChangeRequest, 0, us);
        for (int i = 0; i < 20 && !IsUs(GetKeyboardLayout(thread)); i++)
        {
            Thread.Sleep(25);
        }

        return IsUs(GetKeyboardLayout(thread))
            ? us
            : throw new InvalidOperationException(
                "the game window did not switch to the English (US) keyboard layout; switch the layout to ENG and rerun");
    }

    private static bool IsUs(nint layout) => (layout & 0xFFFF) == 0x0409;

    public void MouseMove(ScreenPoint point)
    {
        EnsureForeground();
        var p = new NativeMethods.Point { X = point.X, Y = point.Y };
        ClientToScreen(_hwnd, ref p);
        // SetCursorPos is pixel-exact; the zero relative move makes the game see a WM_MOUSEMOVE at the new spot.
        SetCursorPos(p.X, p.Y);
        SendMouse(MouseEventMove);
    }

    public void MouseButton(MouseButton button, bool down)
    {
        EnsureForeground();
        SendMouse(button == Scenarios.MouseButton.Left
            ? (down ? MouseEventLeftDown : MouseEventLeftUp)
            : (down ? MouseEventRightDown : MouseEventRightUp));
    }

    public CaptureImage? CaptureWindow()
    {
        RefreshWindow();
        (int w, int h) = ClientSize;
        if (_hwnd == 0 || w <= 0 || h <= 0)
        {
            return null;
        }

        CaptureImage? printed = Capture(w, h, usePrintWindow: true);
        if (printed is not null && !IsUniform(printed))
        {
            LastCaptureMethod = "PrintWindow";
            return printed;
        }

        CaptureImage? blitted = Capture(w, h, usePrintWindow: false);
        if (blitted is not null && !IsUniform(blitted))
        {
            LastCaptureMethod = "BitBlt";
            return blitted;
        }

        LastCaptureMethod = "uniform";
        return printed ?? blitted;
    }

    public IMemoryReader? Memory
    {
        get
        {
            if (_memory is null && _process is { HasExited: false } p)
            {
                nint handle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, (uint)p.Id);
                _memory = handle != 0 ? new Win32MemoryReader(handle) : null;
            }

            return _memory;
        }
    }

    public void Close(TimeSpan timeout)
    {
        if (!_ownsProcess || _process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                if (_hwnd != 0)
                {
                    PostMessageW(_hwnd, WmClose, 0, 0);
                }

                if (!_process.WaitForExit(timeout))
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(5000);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }
    }

    public void Dispose()
    {
        _memory?.Dispose();
        _memory = null;
        if (_ownsProcess)
        {
            Close(TimeSpan.FromSeconds(2));
            _process?.Dispose();
        }
    }

    private void RefreshWindow()
    {
        if ((_hwnd == 0 || !IsWindow(_hwnd)) && _process is { HasExited: false } p)
        {
            // Games may recreate their window (e.g. after a mode change).
            _hwnd = FindLargestWindow((uint)p.Id);
        }
    }

    private void EnsureForeground()
    {
        RefreshWindow();
        if (_hwnd != 0 && GetForegroundWindow() != _hwnd)
        {
            Focus();
        }
    }

    private CaptureImage? Capture(int w, int h, bool usePrintWindow)
    {
        var header = new BitmapInfoHeader
        {
            Size = (uint)sizeof(BitmapInfoHeader),
            Width = w,
            Height = -h,
            Planes = 1,
            BitCount = 32,
        };
        nint screen = GetDC(0);
        nint memory = CreateCompatibleDC(screen);
        nint bitmap = CreateDIBSection(screen, &header, 0, out nint bits, 0, 0);
        try
        {
            if (memory == 0 || bitmap == 0 || bits == 0)
            {
                return null;
            }

            nint old = SelectObject(memory, bitmap);
            bool ok;
            if (usePrintWindow)
            {
                ok = PrintWindow(_hwnd, memory, PrintWindowClientOnly | PrintWindowRenderFullContent);
            }
            else
            {
                var origin = new NativeMethods.Point();
                ClientToScreen(_hwnd, ref origin);
                ok = BitBlt(memory, 0, 0, w, h, screen, origin.X, origin.Y, Srccopy);
            }

            GdiFlush();
            SelectObject(memory, old);
            if (!ok)
            {
                return null;
            }

            uint[] pixels = new uint[w * h];
            var source = new ReadOnlySpan<uint>((void*)bits, w * h);
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = source[i] & 0xFFFFFF;
            }

            return new CaptureImage(w, h, PixelFormat.Rgb888, pixels);
        }
        finally
        {
            if (bitmap != 0)
            {
                DeleteObject(bitmap);
            }

            if (memory != 0)
            {
                DeleteDC(memory);
            }

            ReleaseDC(0, screen);
        }
    }

    private static bool IsUniform(CaptureImage image)
    {
        uint first = image.Native[0];
        foreach (uint v in image.Native)
        {
            if (v != first)
            {
                return false;
            }
        }

        return true;
    }

    internal static void SendKey(ushort vk, bool down)
    {
        Input input = default;
        input.Type = InputKeyboard;
        input.Data.Keyboard.VirtualKey = vk;
        input.Data.Keyboard.ScanCode = (ushort)MapVirtualKeyW(vk, 0);
        input.Data.Keyboard.Flags = (KeyNames.IsExtended(vk) ? KeyEventExtendedKey : 0) | (down ? 0 : KeyEventKeyUp);
        if (SendInput(1, &input, sizeof(Input)) != 1)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SendInput (keyboard) was blocked");
        }
    }

    private static void SendMouse(uint flags)
    {
        Input input = default;
        input.Type = InputMouse;
        input.Data.Mouse.Flags = flags;
        if (SendInput(1, &input, sizeof(Input)) != 1)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SendInput (mouse) was blocked");
        }
    }

    private static nint FindLargestWindow(uint processId)
    {
        var search = new WindowSearch { ProcessId = processId };
        GCHandle handle = GCHandle.Alloc(search);
        try
        {
            EnumWindows(&EnumCallback, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return search.Best;
    }

    [UnmanagedCallersOnly]
    private static int EnumCallback(nint hwnd, nint param)
    {
        var search = (WindowSearch)GCHandle.FromIntPtr(param).Target!;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == search.ProcessId && IsWindowVisible(hwnd) && GetClientRect(hwnd, out Rect r))
        {
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > search.BestArea)
            {
                search.BestArea = area;
                search.Best = hwnd;
            }
        }

        return 1;
    }

    private sealed class WindowSearch
    {
        public uint ProcessId;
        public nint Best;
        public long BestArea;
    }
}

/// <summary>ReadProcessMemory over the committed writable regions (VirtualQueryEx).</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class Win32MemoryReader(nint handle) : IMemoryReader, IDisposable
{
    private nint _handle = handle;

    public IReadOnlyList<MemoryRegion> WritableRegions()
    {
        var regions = new List<MemoryRegion>();
        nuint address = 0;
        while (VirtualQueryEx(_handle, address, out MemoryBasicInformation info, (nuint)sizeof(MemoryBasicInformation)) != 0)
        {
            long size = info.RegionSize;
            if (size <= 0)
            {
                break;
            }

            if (info.State == MemCommit && (info.Protect & PageWritableMask) != 0 && (info.Protect & (PageGuard | PageNoAccess)) == 0)
            {
                regions.Add(new MemoryRegion((ulong)info.BaseAddress, size));
            }

            nuint next = (nuint)info.BaseAddress + (nuint)size;
            if (next <= address)
            {
                break;
            }

            address = next;
        }

        return regions;
    }

    public int Read(ulong address, Span<byte> buffer)
    {
        fixed (byte* p = buffer)
        {
            return ReadProcessMemory(_handle, (nuint)address, p, (nuint)buffer.Length, out nuint read) ? (int)read : 0;
        }
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            CloseHandle(_handle);
            _handle = 0;
        }
    }
}
