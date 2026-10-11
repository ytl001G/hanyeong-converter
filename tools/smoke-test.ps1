# End-to-end check of what unit tests cannot cover: the keyboard hook, SendInput and the real IME,
# in a classic edit box and in Windows Notepad (TSF). Judgement rules are covered by the unit tests.
# Usage: .\tools\smoke-test.ps1 -Exe <HanYeong.exe>
# Takes keyboard focus for about 25 seconds and aborts if its window loses focus.
param([Parameter(Mandatory)][string]$Exe)
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
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [StructLayout(LayoutKind.Sequential)] public struct GTI { public int cb; public uint flags; public IntPtr a, focus, c, m, ms, caret; public int l, t, r, b; }
  [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint tid, ref GTI g);
  [DllImport("imm32.dll")] static extern IntPtr ImmGetDefaultIMEWnd(IntPtr h);
  [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, int m, IntPtr w, IntPtr l, uint f, uint t, out IntPtr r);
  public static bool IsKorean() {
    IntPtr fg = GetForegroundWindow(); uint pid; uint tid = GetWindowThreadProcessId(fg, out pid);
    var g = new GTI(); g.cb = Marshal.SizeOf(typeof(GTI)); IntPtr focus = GetGUIThreadInfo(tid, ref g) && g.focus != IntPtr.Zero ? g.focus : fg;
    IntPtr r; SendMessageTimeout(ImmGetDefaultIMEWnd(focus), 0x283, (IntPtr)1, IntPtr.Zero, 2, 200, out r); return ((long)r & 1) != 0;
  }
}
"@

$script:target = [IntPtr]::Zero
$script:results = @()
$script:pumpForm = $false

function Pump($ms) { $end = [DateTime]::Now.AddMilliseconds($ms); while ([DateTime]::Now -lt $end) { if ($script:pumpForm) { [Windows.Forms.Application]::DoEvents() }; Start-Sleep -Milliseconds 5 } }
function Guard { if ([K]::GetForegroundWindow() -ne $script:target) { throw 'Test window lost focus - aborted before sending keys to another window.' } }
function Tap($vk) { Guard; [K]::Key($vk, $false); [K]::Key($vk, $true); Pump 20 }
# Letters (capitals with Shift), space, Enter (`n) and . only — all the cases need.
function Type-Text($s) {
  foreach ($ch in $s.ToCharArray()) {
    Guard
    if ($ch -eq ' ') { Tap 0x20; Pump 100; continue }
    if ($ch -eq [char]10) { Tap 0x0D; Pump 100; continue }
    if ($ch -eq '.') { Tap 0xBE; Pump 100; continue }
    $isUpper = [char]::IsUpper($ch)
    if ($isUpper) { [K]::Key(0x10, $false) }
    [K]::Key([uint16][char]::ToUpper($ch), $false); [K]::Key([uint16][char]::ToUpper($ch), $true)
    if ($isUpper) { [K]::Key(0x10, $true) }
    Pump 20
  }
}
function Hotkey { Guard; [K]::Key(0x11, $false); Pump 20; [K]::Key(0x20, $false); [K]::Key(0x20, $true); Pump 20; [K]::Key(0x11, $true); Pump 300 }
function Mode { if ([K]::IsKorean()) { 'KO' } else { 'EN' } }
# Real 한/영 key presses, so HanYeong sees the switch like a user's.
function Set-Mode($m) { if ((Mode) -ne $m) { Tap 0x15; Pump 120 } }
function Record($label, $expected, $actual) { $script:results += [pscustomobject]@{ where = $script:where; step = $label; expected = $expected; actual = $actual; ok = ($actual -ceq $expected) } }

# ---- start the app with throwaway settings (one exception word) ----
$exePath = (Resolve-Path $Exe).Path
$installed = Get-Process HanYeong -ErrorAction SilentlyContinue | Where-Object { $_.Path -ne $exePath } | Select-Object -First 1 -ExpandProperty Path
Get-Process HanYeong -ErrorAction SilentlyContinue | Stop-Process -Force
$env:HANYEONG_SETTINGS = Join-Path $env:TEMP "hanyeong-smoke-$PID.json"
'{ "AutoMode": true, "HasShownTrayHint": true, "ExceptionWords": [ "qkqh" ] }' | Set-Content $env:HANYEONG_SETTINGS -Encoding UTF8
$app = Start-Process $exePath -PassThru
Start-Sleep -Milliseconds 1500   # word tables load in the background
$savedClip = Get-Clipboard -Raw

try {
  # ================= classic edit box =================
  $script:where = 'form'; $script:pumpForm = $true
  $form = New-Object Windows.Forms.Form
  $form.Text = 'HanYeong smoke test'; $form.Width = 520; $form.Height = 120; $form.TopMost = $true
  $tb = New-Object Windows.Forms.TextBox; $tb.Dock = 'Fill'; $tb.Font = New-Object Drawing.Font('Malgun Gothic', 14)
  $form.Controls.Add($tb); $form.Show(); Pump 300
  [void][K]::SetForegroundWindow($form.Handle); $tb.Focus() | Out-Null; Pump 200
  $script:target = $form.Handle
  function Fresh($m) { $tb.Clear(); Pump 30; Set-Mode $m; Tap 0x23; Pump 40 }   # End = reset point

  Fresh 'EN'; Type-Text 'dkssud'; Hotkey; Record 'Ctrl+Space (IME mode untouched)' '안녕' $tb.Text
  Hotkey; Record 'Ctrl+Space again reverts' 'dkssud' $tb.Text
  Fresh 'KO'; Type-Text 'Hello'; Hotkey; Record 'composing syllable erased, capital kept' 'Hello' $tb.Text
  Set-Clipboard -Value 'CLIP-KEEP'
  $tb.Text = 'dkssud gktpdy'; Pump 30; Tap 0x23; $tb.SelectAll(); Pump 30
  Hotkey; Pump 150; Record 'selection converted' '안녕 하세요' $tb.Text
  Record 'clipboard restored' 'CLIP-KEEP' (Get-Clipboard -Raw).TrimEnd()
  Fresh 'EN'; Type-Text 'dkssud. gktpdy '; Record 'auto on . and Space' '안녕. 하세요 ' $tb.Text
  Fresh 'KO'; Type-Text 'want to go '; Record 'auto KO->EN + sentence context' 'want to go ' $tb.Text
  Fresh 'EN'; Type-Text 'dkssud'; Tap 0x25; Pump 120; Type-Text 'x'; Record 'auto on arrow, cursor moved' '안x녕' $tb.Text
  Fresh 'EN'; Type-Text 'qkqh '; Record 'exception word left alone' 'qkqh ' $tb.Text
  $form.Close(); $script:pumpForm = $false

  # ================= Windows Notepad (TSF) =================
  $script:where = 'notepad'
  $np = Start-Process notepad -PassThru
  Start-Sleep -Milliseconds 1500
  $script:target = [K]::GetForegroundWindow()
  $fgPid = 0; [void][K]::GetWindowThreadProcessId($script:target, [ref]$fgPid)
  if ((Get-Process -Id $fgPid).ProcessName -ne 'Notepad') { throw 'Notepad did not come to the front - aborted before typing.' }
  function Read-Doc { Guard; [K]::Key(0x11, $false); Tap 0x41; Tap 0x43; [K]::Key(0x11, $true); Pump 200; $t = Get-Clipboard -Raw; Tap 0x23; if ($null -eq $t) { '' } else { $t } }
  function Clear-Doc { Guard; [K]::Key(0x11, $false); Tap 0x41; [K]::Key(0x11, $true); Tap 0x2E; Pump 80 }
  function Case($label, $mode, $keys, $action, $expected) {
    Clear-Doc; Set-Mode $mode; Tap 0x23; Type-Text $keys; Pump 80
    if ($action) { & $action }
    Record $label $expected (Read-Doc)
  }
  Case 'Ctrl+Space on composing ㅑ (reported bug)' 'KO' 'hi' { Hotkey } 'hi'
  Case 'auto KO->EN, English + Korean noun' 'KO' 'engelrhrtjs ' $null 'engel곡선 '
  Case 'auto on Enter after other words' 'EN' ("dkssud gktpdy" + [char]10) $null ("안녕 하세요" + [char]13 + [char]10)
  Case 'one word + Enter stays (ID fields)' 'EN' ("rlaalswn" + [char]10) $null ("rlaalswn" + [char]13 + [char]10)
  Clear-Doc
  $np.CloseMainWindow() | Out-Null
} finally {
  if ($null -ne $savedClip) { Set-Clipboard -Value $savedClip }
  Stop-Process -Id $app.Id -ErrorAction SilentlyContinue
  Remove-Item $env:HANYEONG_SETTINGS -ErrorAction SilentlyContinue
  if ($installed) { Start-Process $installed -ArgumentList '--startup' }
}
$results | Format-Table -AutoSize | Out-String -Width 200
