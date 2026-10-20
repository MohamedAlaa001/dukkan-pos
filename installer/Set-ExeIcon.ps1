<#
 Replaces the icon of an .exe with an .ico file (used on dist\Dukkan-Setup.exe, which IExpress builds with a generic icon).
 Usage: Set-ExeIcon.ps1 -Exe <path> -Icon <path>
#>
param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$Icon)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class ResIcon
{
    delegate bool EnumNameProc(IntPtr h, IntPtr type, IntPtr name, IntPtr param);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryEx(string f, IntPtr h, uint flags);
    [DllImport("kernel32", SetLastError = true)] static extern bool FreeLibrary(IntPtr h);
    [DllImport("kernel32", SetLastError = true)] static extern bool EnumResourceNames(IntPtr h, IntPtr type, EnumNameProc proc, IntPtr param);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr BeginUpdateResource(string f, bool del);
    [DllImport("kernel32", SetLastError = true)] static extern bool UpdateResource(IntPtr h, IntPtr type, IntPtr name, ushort lang, byte[] data, uint size);
    [DllImport("kernel32", SetLastError = true)] static extern bool EndUpdateResource(IntPtr h, bool discard);

    static IntPtr RtIcon = (IntPtr)3, RtGroup = (IntPtr)14;

    public static void Apply(string exe, byte[] ico)
    {
        // existing icon-group ids (numeric ids only; IExpress uses numeric ones)
        var ids = new List<int>();
        var lib = LoadLibraryEx(exe, IntPtr.Zero, 0x2 /* LOAD_LIBRARY_AS_DATAFILE */);
        if (lib == IntPtr.Zero) throw new Exception("Cannot open " + exe);
        EnumResourceNames(lib, RtGroup, (h, t, n, p) => { if (((long)n >> 16) == 0) ids.Add((int)(long)n); return true; }, IntPtr.Zero);
        FreeLibrary(lib);
        if (ids.Count == 0) ids.Add(1);

        int count = BitConverter.ToUInt16(ico, 4);
        var group = new byte[6 + 14 * count];
        Array.Copy(ico, 0, group, 0, 6);
        var upd = BeginUpdateResource(exe, false);
        if (upd == IntPtr.Zero) throw new Exception("BeginUpdateResource failed: " + Marshal.GetLastWin32Error());
        for (int i = 0; i < count; i++)
        {
            int e = 6 + 16 * i;
            uint size = BitConverter.ToUInt32(ico, e + 8), off = BitConverter.ToUInt32(ico, e + 12);
            var frame = new byte[size]; Array.Copy(ico, off, frame, 0, size);
            ushort frameId = (ushort)(300 + i);
            if (!UpdateResource(upd, RtIcon, (IntPtr)frameId, 1033, frame, size)) throw new Exception("UpdateResource(icon) failed");
            Array.Copy(ico, e, group, 6 + 14 * i, 12);                 // width,height,colors,reserved,planes,bitcount,bytes
            BitConverter.GetBytes(frameId).CopyTo(group, 6 + 14 * i + 12);
        }
        foreach (var id in ids)
            if (!UpdateResource(upd, RtGroup, (IntPtr)id, 1033, group, (uint)group.Length)) throw new Exception("UpdateResource(group) failed");
        if (!EndUpdateResource(upd, false)) throw new Exception("EndUpdateResource failed: " + Marshal.GetLastWin32Error());
    }
}
'@
[ResIcon]::Apply((Resolve-Path $Exe).Path, [System.IO.File]::ReadAllBytes((Resolve-Path $Icon).Path))
Write-Host "Icon applied to $Exe"
