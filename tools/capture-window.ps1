# 截取 Piable 主窗口，用于人工确认界面渲染是否正常。
#
# 注意：必须先声明 DPI 感知。否则 GetWindowRect 返回的是被缩放过的坐标，
# 与屏幕上真实的物理像素对不上，截出来的图会缺掉底部一块。
#
# 用法: powershell -NoProfile -File tools\capture-window.ps1 [输出路径]
param(
    [string]$Out = "shot.png"
)

Add-Type -AssemblyName System.Windows.Forms, System.Drawing

Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class Win {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int m);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@

[Win]::SetProcessDPIAware() | Out-Null

$procs = Get-Process -Name Piable -ErrorAction SilentlyContinue
if (-not $procs) { Write-Output "NO_PIABLE_PROCESS"; exit 1 }
$ids = @($procs | ForEach-Object { $_.Id })

# 取该进程中面积最大的可见窗口，而不是第一个。
# Avalonia 会额外创建一些不可见或很小的辅助窗口，取"第一个"很容易挑错，
# 截出来的区域跟着偏，看起来就像界面缺了一块。
$script:best = [IntPtr]::Zero
$script:bestArea = 0
$script:found = @()
$cb = [Win+EnumProc] {
    param($h, $l)
    $owner = 0
    [Win]::GetWindowThreadProcessId($h, [ref]$owner) | Out-Null
    if ($ids -contains $owner -and [Win]::IsWindowVisible($h)) {
        $sb = New-Object System.Text.StringBuilder 512
        [Win]::GetWindowText($h, $sb, 512) | Out-Null
        $rect = New-Object Win+RECT
        [Win]::GetWindowRect($h, [ref]$rect) | Out-Null
        $area = ($rect.R - $rect.L) * ($rect.B - $rect.T)
        $script:found += "'$($sb.ToString())' $($rect.R - $rect.L)x$($rect.B - $rect.T)"
        if ($area -gt $script:bestArea) {
            $script:bestArea = $area
            $script:best = $h
        }
    }
    return $true
}
[Win]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null

Write-Output "候选窗口: $($script:found -join ' | ')"
$script:handle = $script:best

if ($script:handle -eq [IntPtr]::Zero) { Write-Output "NO_VISIBLE_WINDOW"; exit 1 }

[Win]::ShowWindow($script:handle, 9) | Out-Null   # SW_RESTORE
[Win]::SetForegroundWindow($script:handle) | Out-Null
Start-Sleep -Milliseconds 1200

$r = New-Object Win+RECT
[Win]::GetWindowRect($script:handle, [ref]$r) | Out-Null

# 多截一圈：宁可带上一点桌面背景，也不要因为坐标误差漏掉窗口底部
$x = [int][Math]::Max(0, $r.L - 30)
$y = [int][Math]::Max(0, $r.T - 30)
$w = [int]($r.R - $r.L) + 60
$h2 = [int]($r.B - $r.T) + 90

Write-Output "WindowRect L=$($r.L) T=$($r.T) R=$($r.R) B=$($r.B)  capture=${w}x${h2} at ($x,$y)"

$bmp = New-Object System.Drawing.Bitmap($w, $h2)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

Write-Output "OK -> $Out"
