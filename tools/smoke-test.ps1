# End-to-end check: starts HanYeong.exe, types into a test window with SendInput and verifies the text.
# Takes keyboard focus for ~5 seconds. Usage: .\tools\smoke-test.ps1 -Exe src\HanYeong\bin\Debug\net10.0-windows\HanYeong.exe
param([string]$Exe)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class K {
  [StructLayout(LayoutKind.Sequential)] public struct KI { public ushort vk; public ushort scan; public uint flags; public uint time; public IntPtr extra; }
  [StructLayout(LayoutKind.Explicit, Size=40)] public struct IN { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KI ki; }
  [DllImport("user32.dll")] static extern uint SendInput(uint n, IN[] i, int size);
  public static void Key(ushort vk, bool up) { var a = new IN[1]; a[0].type = 1; a[0].ki.vk = vk; a[0].ki.flags = up ? 2u : 0u; SendInput(1, a, Marshal.SizeOf(typeof(IN))); }
  [DllImport("imm32.dll")] public static extern IntPtr ImmGetContext(IntPtr h);
  [DllImport("imm32.dll")] public static extern bool ImmSetConversionStatus(IntPtr c, int conv, int sent);
  [DllImport("imm32.dll")] public static extern bool ImmGetConversionStatus(IntPtr c, out int conv, out int sent);
  [DllImport("imm32.dll")] public static extern bool ImmReleaseContext(IntPtr h, IntPtr c);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@

function Pump($ms) { $end = [DateTime]::Now.AddMilliseconds($ms); while ([DateTime]::Now -lt $end) { [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 5 } }
function TypeKeys($s) { foreach ($ch in $s.ToUpper().ToCharArray()) { [K]::Key([uint16][char]$ch, $false); [K]::Key([uint16][char]$ch, $true); Pump 15 } }
function Hotkey { [K]::Key(0x11, $false); Pump 20; [K]::Key(0x20, $false); [K]::Key(0x20, $true); Pump 20; [K]::Key(0x11, $true); Pump 400 }
function Mode { $c = [K]::ImmGetContext($tb.Handle); $conv = 0; $sent = 0; [void][K]::ImmGetConversionStatus($c, [ref]$conv, [ref]$sent); [void][K]::ImmReleaseContext($tb.Handle, $c); if ($conv -band 1) { 'KO' } else { 'EN' } }

$app = Start-Process $Exe -PassThru
Start-Sleep -Milliseconds 800

$form = New-Object Windows.Forms.Form
$form.Text = 'HanYeong smoke test'; $form.Width = 500; $form.Height = 120; $form.TopMost = $true
$tb = New-Object Windows.Forms.TextBox; $tb.Dock = 'Fill'; $tb.Font = New-Object Drawing.Font('Malgun Gothic', 14)
$form.Controls.Add($tb); $form.Show(); Pump 300
[void][K]::SetForegroundWindow($form.Handle); $tb.Focus() | Out-Null; Pump 200
$c = [K]::ImmGetContext($tb.Handle); [void][K]::ImmSetConversionStatus($c, 0, 0); [void][K]::ImmReleaseContext($tb.Handle, $c); Pump 100

$results = @()
function Check($label, $expected) { $actual = $tb.Text; $script:results += [pscustomobject]@{ step = $label; expected = $expected; actual = $actual; mode = (Mode); ok = ($actual -eq $expected) } }

TypeKeys 'dkssud';             Check 'typed EN' 'dkssud'
Hotkey;                    Check 'convert -> KO' '안녕'
TypeKeys 'gktpdy'; Pump 100;   Check 'keep typing (KO)' '안녕하세요'
Hotkey;                    Check 'convert whole -> EN' 'dkssudgktpdy'
Hotkey;                    Check 'revert' '안녕하세요'
$tb.Clear(); Pump 50
$c = [K]::ImmGetContext($tb.Handle); [void][K]::ImmSetConversionStatus($c, 1, 0); [void][K]::ImmReleaseContext($tb.Handle, $c); Pump 100
[K]::Key(0x24,$false);[K]::Key(0x24,$true); Pump 50   # Home -> reset segment
TypeKeys 'hello';              Check 'typed KO' 'ㅗ디ㅣㅐ'
Hotkey;                    Check 'convert -> EN' 'hello'
$tb.Clear(); Pump 50
$c = [K]::ImmGetContext($tb.Handle); [void][K]::ImmSetConversionStatus($c, 1, 0); [void][K]::ImmReleaseContext($tb.Handle, $c); Pump 100
[K]::Key(0x24,$false);[K]::Key(0x24,$true); Pump 50
TypeKeys 'dkssudgk'; [K]::Key(0x08,$false);[K]::Key(0x08,$true); Pump 30; TypeKeys 'f'; Check 'KO with backspace (IME drops ㅏ, ㅎ+ㄹ stay apart)' '안녕ㅎㄹ'
Hotkey;                    Check 'convert -> EN' 'dkssudgf'

$form.Close(); Stop-Process -Id $app.Id
$results | Format-Table -AutoSize | Out-String -Width 200

