[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Executable,
    [int]$GroupIconId = 101
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class CodexPeResources
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint SizeofResource(IntPtr module, IntPtr resource);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr LoadResource(IntPtr module, IntPtr resource);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr LockResource(IntPtr resource);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FreeLibrary(IntPtr module);
}
"@

function Resource-Pointer([int]$Id) {
    return [IntPtr]$Id
}

function Read-ResourceBytes([IntPtr]$Module, [IntPtr]$Type, [IntPtr]$Name) {
    $resource = [CodexPeResources]::FindResource($Module, $Name, $Type)
    if ($resource -eq [IntPtr]::Zero) {
        $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        throw "PE resource not found (type=$($Type.ToInt64()) name=$($Name.ToInt64()) error=$errorCode)"
    }
    $size = [CodexPeResources]::SizeofResource($Module, $resource)
    $loaded = [CodexPeResources]::LoadResource($Module, $resource)
    $locked = [CodexPeResources]::LockResource($loaded)
    if ($size -le 0 -or $locked -eq [IntPtr]::Zero) {
        throw "PE resource has no readable payload"
    }
    $bytes = [byte[]]::new([int]$size)
    [Runtime.InteropServices.Marshal]::Copy($locked, $bytes, 0, $bytes.Length)
    return $bytes
}

$path = [IO.Path]::GetFullPath($Executable)
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw "Executable not found: $path"
}

$module = [CodexPeResources]::LoadLibraryEx($path, [IntPtr]::Zero, 0x00000002)
if ($module -eq [IntPtr]::Zero) {
    $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
    throw "LoadLibraryEx failed for $path (error=$errorCode)"
}

try {
    $groupType = [IntPtr]14 # RT_GROUP_ICON
    $iconType = [IntPtr]3  # RT_ICON
    $groupBytes = Read-ResourceBytes $module $groupType (Resource-Pointer $GroupIconId)
    if ($groupBytes.Length -lt 6) { throw "RT_GROUP_ICON payload is truncated" }
    $reserved = [BitConverter]::ToUInt16($groupBytes, 0)
    $type = [BitConverter]::ToUInt16($groupBytes, 2)
    $count = [BitConverter]::ToUInt16($groupBytes, 4)
    if ($reserved -ne 0 -or $type -ne 1 -or $count -le 0) {
        throw "RT_GROUP_ICON header is invalid"
    }
    if ($groupBytes.Length -lt (6 + 14 * $count)) {
        throw "RT_GROUP_ICON entries are truncated"
    }

    $dimensions = [Collections.Generic.List[int]]::new()
    $childIds = [Collections.Generic.List[int]]::new()
    for ($index = 0; $index -lt $count; $index++) {
        $offset = 6 + 14 * $index
        $width = [int]$groupBytes[$offset]
        $height = [int]$groupBytes[$offset + 1]
        if ($width -eq 0) { $width = 256 }
        if ($height -eq 0) { $height = 256 }
        if ($width -ne $height) { throw "non-square icon frame ${width}x${height}" }
        $planes = [BitConverter]::ToUInt16($groupBytes, $offset + 4)
        $bits = [BitConverter]::ToUInt16($groupBytes, $offset + 6)
        $bytesInRes = [BitConverter]::ToUInt32($groupBytes, $offset + 8)
        $childId = [int][BitConverter]::ToUInt16($groupBytes, $offset + 12)
        if ($planes -ne 1 -or $bits -ne 32 -or $bytesInRes -eq 0) {
            throw "icon frame $width x $height has invalid metadata"
        }
        $dimensions.Add($width)
        $childIds.Add($childId)
        $childBytes = Read-ResourceBytes $module $iconType (Resource-Pointer $childId)
        if ($childBytes.Length -eq 0) { throw "RT_ICON child $childId is empty" }
    }

    $required = @(16, 20, 24, 32, 48, 256)
    foreach ($size in $required) {
        if (-not $dimensions.Contains($size)) {
            throw "RT_GROUP_ICON #$GroupIconId is missing ${size}x${size}"
        }
    }

    [pscustomobject]@{
        Executable = $path
        ResourceType = "RT_GROUP_ICON"
        ResourceId = $GroupIconId
        FrameCount = $count
        Dimensions = (($dimensions | Sort-Object -Unique) -join ",")
        ChildResourceCount = $childIds.Count
        Result = "ok"
    }
} finally {
    [CodexPeResources]::FreeLibrary($module) | Out-Null
}
