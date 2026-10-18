using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Tzar2.Tools.Cli.Fidelity.Imaging;
using Tzar2.Tools.Cli.Fidelity.Scenarios;
using static Tzar2.Tools.Cli.Fidelity.Capture.Windows.NativeMethods;

namespace Tzar2.Tools.Cli.Fidelity.Capture.Windows;

/// <summary>
/// <c>t2 fidelity selftest</c>: exercises the Windows half of the harness without the game. It opens its own window
/// painted with known colours, captures it (PrintWindow and BitBlt), sends mouse and keyboard input to it and checks
/// what the window received, and scans its own memory for a planted value. Run by CI on windows-latest and by
/// <c>run-capture.ps1</c> before every session.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class SelfTest
{
    // Colours that RGB555 represents exactly (each channel a multiple of 8 plus its bit-replicated tail).
    private static readonly (byte R, byte G, byte B)[] Quadrants = [(0xFF, 0x00, 0x00), (0x00, 0xFF, 0x00), (0x00, 0x00, 0xFF), (0x84, 0x42, 0x21)];

    private static readonly object Gate = new();
    private static readonly List<(uint Message, nuint WParam, nint LParam)> Received = [];

    public static int Run(TextWriter output, bool requireInput)
    {
        int failures = 0;
        void Report(string status, string check, string detail)
        {
            output.WriteLine($"{status,-4} {check}: {detail}");
            if (status == "FAIL")
            {
                failures++;
            }
        }

        if (!Environment.Is64BitProcess)
        {
            Report("FAIL", "process", "t2 must run as a 64-bit process");
            return 1;
        }

        Report(sizeof(Input) == 40 ? "PASS" : "FAIL", "INPUT layout", $"{sizeof(Input)} bytes (expected 40)");
        Report(sizeof(MemoryBasicInformation) == 48 ? "PASS" : "FAIL", "MEMORY_BASIC_INFORMATION layout", $"{sizeof(MemoryBasicInformation)} bytes (expected 48)");
        IReadOnlyDictionary<string, string> devices = MachineSettings.IniFor(DeviceProbe.Detect());
        Report("INFO", "device counts for tzar.ini", devices.Count == 0 ? "none detected" : string.Join(", ", devices.Select(kv => $"{kv.Key}={kv.Value}")));
        IReadOnlyList<string> shims = DeviceProbe.CompatDatabases("Tzar.exe");
        Report(shims.Count == 0 ? "PASS" : "WARN", "compatibility databases for Tzar.exe", shims.Count == 0 ? "none" : $"{string.Join(", ", shims)} (the capture prints the workaround)");

        Win32GameDriver.EnsureDpiAware();
        nint hwnd = 0;
        var ready = new ManualResetEventSlim();
        var thread = new Thread(() => WindowThread(ref hwnd, ready)) { IsBackground = true, Name = "selftest-window" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(10)) || hwnd == 0)
        {
            Report("FAIL", "window", "could not create the test window");
            return 1;
        }

        try
        {
            using var driver = new Win32GameDriver(Process.GetCurrentProcess(), hwnd);
            Thread.Sleep(300);
            (int w, int h) = driver.ClientSize;
            Report(w > 0 && h > 0 ? "PASS" : "FAIL", "client area", $"{w}×{h}");

            var watch = Stopwatch.StartNew();
            CaptureImage? image = driver.CaptureWindow();
            double ms = watch.Elapsed.TotalMilliseconds;
            if (image is null)
            {
                Report("FAIL", "window capture", "no image");
            }
            else
            {
                (int x, int y)[] centres = [(w / 4, h / 4), (3 * w / 4, h / 4), (w / 4, 3 * h / 4), (3 * w / 4, 3 * h / 4)];
                int wrong = 0;
                for (int i = 0; i < 4; i++)
                {
                    uint v = image.NativeAt(centres[i].x, centres[i].y);
                    uint expected = (uint)((Quadrants[i].R << 16) | (Quadrants[i].G << 8) | Quadrants[i].B);
                    if (v != expected)
                    {
                        wrong++;
                        output.WriteLine($"     quadrant {i}: 0x{v:X6}, expected 0x{expected:X6}");
                    }
                }

                Report(wrong == 0 ? "PASS" : "FAIL", "window capture", $"{driver.LastCaptureMethod}, {ms:F1} ms, {4 - wrong}/4 colours exact");
            }

            bool foreground = driver.Focus();
            string inputStatus = requireInput ? "FAIL" : "WARN";
            if (!foreground)
            {
                Report(inputStatus, "foreground", "the test window could not be brought to the foreground (locked session or another app holds focus?)");
            }

            lock (Gate)
            {
                Received.Clear();
            }

            try
            {
                driver.MouseMove(new ScreenPoint(31, 47));
                Thread.Sleep(50);
                driver.MouseButton(MouseButton.Left, true);
                Thread.Sleep(50);
                driver.MouseButton(MouseButton.Left, false);
                driver.Key(0x4B, true);
                Thread.Sleep(30);
                driver.Key(0x4B, false);
                driver.Type("a1");
                Thread.Sleep(300);
                (uint Message, nuint WParam, nint LParam)[] got;
                lock (Gate)
                {
                    got = [.. Received];
                }

                var click = got.FirstOrDefault(m => m.Message == WmLButtonDown);
                int cx = (short)(click.LParam & 0xFFFF), cy = (short)((click.LParam >> 16) & 0xFFFF);
                Report(click.Message != 0 && cx == 31 && cy == 47 ? "PASS" : inputStatus, "mouse input",
                    click.Message != 0 ? $"click received at ({cx}, {cy}), sent at (31, 47)" : "no click received");
                bool key = got.Any(m => m.Message == WmKeyDown && m.WParam == 0x4B);
                Report(key ? "PASS" : inputStatus, "keyboard input", key ? "key K received" : "no key received");
                string chars = new(got.Where(m => m.Message == WmChar).Select(m => (char)m.WParam).ToArray());
                Report(chars.Contains("a1", StringComparison.Ordinal) ? "PASS" : inputStatus, "typed text", $"received \"{chars}\"");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Report(inputStatus, "input", ex.Message);
            }

            MemoryCheck(driver, Report);
        }
        finally
        {
            PostMessageW(hwnd, WmClose, 0, 0);
            thread.Join(TimeSpan.FromSeconds(5));
        }

        output.WriteLine(failures == 0 ? "Self-test passed." : $"Self-test FAILED ({failures} checks).");
        return failures == 0 ? 0 : 1;
    }

    private static void MemoryCheck(Win32GameDriver driver, Action<string, string, string> report)
    {
        IMemoryReader? reader = driver.Memory;
        if (reader is null)
        {
            report("FAIL", "memory", "OpenProcess failed");
            return;
        }

        // A value that is unlikely to occur by chance, kept alive in a pinned array.
        int marker = 0x5A3C0000 | Random.Shared.Next(0x10000);
        int[] planted = GC.AllocateArray<int>(4, pinned: true);
        planted[0] = marker;
        var watch = Stopwatch.StartNew();
        MemoryScanResult scan = MemoryProbe.Scan(reader, "selftest", marker, 4, 10000);
        nint expected;
        fixed (int* p = planted)
        {
            expected = (nint)p;
        }

        bool found = scan.Addresses.Contains((ulong)expected);
        report(found ? "PASS" : "FAIL", "memory scan", $"{scan.Addresses.Count} hits in {scan.Regions} regions, {scan.BytesScanned / (1 << 20)} MiB, {watch.ElapsedMilliseconds} ms");
        planted[0] = marker - 7;
        MemoryScanResult narrowed = MemoryProbe.Filter(reader, "selftest2", scan, ScanMode.Decreased, null, 1000);
        report(narrowed.Addresses.Contains((ulong)expected) ? "PASS" : "FAIL", "memory narrowing", $"{narrowed.Addresses.Count} decreased");
        GC.KeepAlive(planted);
    }

    private static void WindowThread(ref nint hwnd, ManualResetEventSlim ready)
    {
        nint instance = GetModuleHandleW(null);
        fixed (char* name = "Tzar2CaptureSelfTest")
        {
            var cls = new WndClassEx
            {
                Size = (uint)sizeof(WndClassEx),
                WndProc = &WndProc,
                Instance = instance,
                ClassName = name,
            };
            RegisterClassExW(&cls);
        }

        // WS_OVERLAPPEDWINDOW | WS_VISIBLE
        hwnd = CreateWindowExW(0, "Tzar2CaptureSelfTest", "Tzar2 capture self-test", 0x00CF0000 | 0x10000000, 120, 120, 480, 360, 0, 0, instance, 0);
        ready.Set();
        if (hwnd == 0)
        {
            return;
        }

        UpdateWindow(hwnd);
        while (GetMessageW(out Msg msg, 0, 0, 0) > 0)
        {
            TranslateMessage(msg);
            DispatchMessageW(msg);
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case WmPaint:
            {
                nint hdc = BeginPaint(hwnd, out PaintStruct ps);
                Paint(hwnd, hdc);
                EndPaint(hwnd, ps);
                return 0;
            }

            case WmPrintClient:
                Paint(hwnd, (nint)wParam);
                return 0;
            case 0x0014: // WM_ERASEBKGND: everything is painted in WM_PAINT
                return 1;
            case WmLButtonDown or WmKeyDown or WmChar:
                lock (Gate)
                {
                    Received.Add((message, wParam, lParam));
                }

                break;
            case WmDestroy:
                PostQuitMessage(0);
                return 0;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private static void Paint(nint hwnd, nint hdc)
    {
        GetClientRect(hwnd, out Rect client);
        int w = client.Right, h = client.Bottom;
        Rect[] rects =
        [
            new() { Left = 0, Top = 0, Right = w / 2, Bottom = h / 2 },
            new() { Left = w / 2, Top = 0, Right = w, Bottom = h / 2 },
            new() { Left = 0, Top = h / 2, Right = w / 2, Bottom = h },
            new() { Left = w / 2, Top = h / 2, Right = w, Bottom = h },
        ];
        for (int i = 0; i < 4; i++)
        {
            nint brush = CreateSolidBrush((uint)(Quadrants[i].R | (Quadrants[i].G << 8) | (Quadrants[i].B << 16)));
            FillRect(hdc, rects[i], brush);
            DeleteObject(brush);
        }
    }
}
