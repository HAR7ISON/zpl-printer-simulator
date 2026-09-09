[CmdletBinding()]
param([Parameter(Mandatory)][string]$Path, [string]$PrinterName = 'ZPL Simulator')
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class RawZplSender {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    public struct Info { public string Name; public string Output; public string Type; }
    [DllImport("winspool.drv", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool OpenPrinter(string name, out IntPtr printer, IntPtr defaults);
    [DllImport("winspool.drv", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern int StartDocPrinter(IntPtr printer, int level, ref Info info);
    [DllImport("winspool.drv", SetLastError=true)]
    static extern bool WritePrinter(IntPtr printer, IntPtr bytes, int length, out int written);
    [DllImport("winspool.drv", SetLastError=true)] static extern bool EndDocPrinter(IntPtr printer);
    [DllImport("winspool.drv")] static extern bool AbortPrinter(IntPtr printer);
    [DllImport("winspool.drv")] static extern bool ClosePrinter(IntPtr printer);
    public static void Send(string name, byte[] bytes) {
        IntPtr printer;
        if (!OpenPrinter(name, out printer, IntPtr.Zero)) throw new Win32Exception();
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        bool started = false;
        try {
            var info = new Info { Name="ZPL test", Type="RAW" };
            if (StartDocPrinter(printer, 1, ref info) == 0) throw new Win32Exception();
            started = true;
            int offset = 0;
            while (offset < bytes.Length) {
                int written;
                if (!WritePrinter(printer, IntPtr.Add(pinned.AddrOfPinnedObject(), offset), bytes.Length-offset, out written) || written == 0) throw new Win32Exception();
                offset += written;
            }
            if (!EndDocPrinter(printer)) throw new Win32Exception();
            started = false;
        } finally { if (started) AbortPrinter(printer); pinned.Free(); ClosePrinter(printer); }
    }
}
'@
[RawZplSender]::Send($PrinterName, [IO.File]::ReadAllBytes((Resolve-Path $Path)))
Write-Host "Submitted raw ZPL to $PrinterName."
