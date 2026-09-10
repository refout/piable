# 截取整个虚拟桌面，用于判断窗口是否被屏幕边界裁剪。
param([string]$Out = "screen.png")

Add-Type -AssemblyName System.Windows.Forms, System.Drawing

$bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
Write-Output "VirtualScreen: X=$($bounds.X) Y=$($bounds.Y) W=$($bounds.Width) H=$($bounds.Height)"

$bmp = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($bounds.X, $bounds.Y, 0, 0, $bmp.Size)
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

Write-Output "OK -> $Out"
