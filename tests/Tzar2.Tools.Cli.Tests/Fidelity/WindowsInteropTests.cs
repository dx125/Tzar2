using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Tzar2.Tools.Cli.Fidelity.Capture;
using Tzar2.Tools.Cli.Fidelity.Capture.Windows;
using Xunit;

namespace Tzar2.Tools.Cli.Tests.Fidelity;

/// <summary>Win32 interop checks; they pass trivially on other systems (the Windows CI job runs them for real).</summary>
public class WindowsInteropTests
{
    [Fact]
    public void Native_structures_have_the_64_bit_windows_layout()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(40, Marshal.SizeOf<NativeMethods.Input>());
        Assert.Equal(48, Marshal.SizeOf<NativeMethods.MemoryBasicInformation>());
        Assert.Equal(40, Marshal.SizeOf<NativeMethods.BitmapInfoHeader>());
    }

    [Fact]
    public void Reads_this_process_memory_read_only()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        int[] planted = GC.AllocateArray<int>(4, pinned: true);
        planted[0] = 0x5A3C0000 | Environment.ProcessId % 0x10000;
        nint handle = NativeMethods.OpenProcess(NativeMethods.ProcessVmRead | NativeMethods.ProcessQueryInformation, false, (uint)Environment.ProcessId);
        Assert.NotEqual(0, handle);
        using var reader = new Win32MemoryReader(handle);
        Assert.NotEmpty(reader.WritableRegions());
        MemoryScanResult scan = MemoryProbe.Scan(reader, "self", planted[0], 4, 10000);
        ulong address = (ulong)Marshal.UnsafeAddrOfPinnedArrayElement(planted, 0);
        Assert.Contains(address, scan.Addresses);
        GC.KeepAlive(planted);
        Assert.True(Process.GetCurrentProcess().Id > 0);
    }
}
