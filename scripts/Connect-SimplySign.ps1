<#
.SYNOPSIS
  Opens a Certum SimplySign session on this machine so signtool can use the cloud code-signing
  certificate, without a person typing the one-time code.

.DESCRIPTION
  Certum keeps the private key in its cloud HSM and hands it to Windows through SimplySign
  Desktop, which presents the certificate as a virtual smart card in the user's certificate
  store once a session is open. Opening a session means entering the account name and a TOTP
  code in the app's login dialog. The dialog is the only way in — there is no command-line or
  API login — so this script installs the app, pre-sets its registry options so the dialog
  appears on start and the CSP keeps the PIN, launches it, generates the TOTP code from the
  otpauth:// URI behind the SimplySign mobile app's QR code, pastes the credentials into the
  dialog and waits for the certificate to show up. Nothing about the account or the code is
  written to the log.

  Idempotent: exits at once when the certificate is already in the store.

  The approach follows Takuya Matsuyama's write-up and the MIT-licensed
  dismine/windows-app-signing-setup-action; kept in this repository so the TOTP seed, which is
  as sensitive as the signing key itself, never reaches third-party action code.

.PARAMETER UserName
  The SimplySign account (defaults to CERTUM_USERNAME).

.PARAMETER OtpUri
  The otpauth://totp/... URI from the SimplySign mobile app's Edit view (defaults to
  CERTUM_OTP_URI). Treat it as a long-lived secret.

.PARAMETER Thumbprint
  SHA-1 thumbprint of the code-signing certificate, as SimplySign registers it in
  Cert:\CurrentUser\My (defaults to CERTUM_KEY_ID). Not secret.

.PARAMETER InstallerUrl
  The SimplySign Desktop MSI to install when the app is missing. Pinned; bump deliberately.
#>
[CmdletBinding()]
param(
    [string]$UserName = $env:CERTUM_USERNAME,
    [string]$OtpUri = $env:CERTUM_OTP_URI,
    [string]$Thumbprint = $env:CERTUM_KEY_ID,
    [string]$InstallerUrl = "https://files.certum.eu/software/SimplySignDesktop/Windows/9.4.3.90/SimplySignDesktop-9.4.3.90-64-bit-en.msi"
)

$ErrorActionPreference = "Stop"
if (-not $IsWindows) { throw "SimplySign Desktop runs on Windows only." }
foreach ($required in @("UserName", "OtpUri", "Thumbprint")) {
    if ([string]::IsNullOrWhiteSpace((Get-Variable $required -ValueOnly))) { throw "$required is required (see the parameter help)." }
}
$Thumbprint = ($Thumbprint -replace "[^0-9A-Fa-f]", "").ToUpperInvariant()

function Get-SigningCertificate {
    Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint.ToUpperInvariant() -eq $Thumbprint } | Select-Object -First 1
}

$existing = Get-SigningCertificate
if ($existing) { Write-Host "SimplySign session already open: $($existing.Subject)"; exit 0 }

# --- Install ---------------------------------------------------------------------------------
$installDir = Join-Path $env:ProgramFiles "Certum\SimplySign Desktop"
$exe = Join-Path $installDir "SimplySignDesktop.exe"
if (-not (Test-Path $exe)) {
    $temp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
    $msi = Join-Path $temp "SimplySignDesktop.msi"
    $log = Join-Path $temp "SimplySignDesktop-install.log"
    Write-Host "== Installing SimplySign Desktop"
    Invoke-WebRequest -Uri $InstallerUrl -OutFile $msi -UseBasicParsing
    $install = Start-Process msiexec.exe -Wait -PassThru -ArgumentList @(
        "/i", "`"$msi`"", "/quiet", "/norestart", "/l*v", "`"$log`"", "ALLUSERS=1", "REBOOT=ReallySuppress")
    if ($install.ExitCode -notin @(0, 3010) -or -not (Test-Path $exe)) {
        if (Test-Path $log) { Get-Content $log -Tail 40 | Out-Host }
        throw "SimplySign Desktop did not install (msiexec exit code $($install.ExitCode))."
    }
}

# --- Configure -------------------------------------------------------------------------------
# Show the login dialog on start (so there is something to type into), keep the PIN in the CSP
# for the session (so signtool never prompts) and forget it on disconnect, do not autostart.
$registry = "HKCU:\Software\Certum\SimplySign"
New-Item -Path $registry -Force | Out-Null
$settings = @{
    ShowLoginDialogOnStart = 1; ShowLoginDialogOnAppRequest = 1; RememberLastUserName = 1
    Autostart = 0; UnregisterCertificatesOnDisconnect = 0; RememberPINinCSP = 1
    ForgetPINinCSPonDisconnect = 1; LangID = 9
}
foreach ($entry in $settings.GetEnumerator()) {
    New-ItemProperty -Path $registry -Name $entry.Key -PropertyType DWord -Value $entry.Value -Force | Out-Null
}

# --- TOTP ------------------------------------------------------------------------------------
$uri = [Uri]$OtpUri
if ($uri.Scheme -ne "otpauth") { throw "OtpUri must be an otpauth:// URI." }
$query = @{}
foreach ($part in $uri.Query.TrimStart("?") -split "&") {
    $kv = $part -split "=", 2
    if ($kv.Count -eq 2) { $query[$kv[0]] = [Uri]::UnescapeDataString($kv[1]) }
}
$secret = $query["secret"]
if (-not $secret) { throw "The otpauth:// URI carries no secret." }
$digits = if ($query["digits"]) { [int]$query["digits"] } else { 6 }
$period = if ($query["period"]) { [int]$query["period"] } else { 30 }
$algorithm = if ($query["algorithm"]) { $query["algorithm"].ToUpperInvariant() } else { "SHA1" }

Add-Type -Language CSharp @"
using System;
using System.Security.Cryptography;
public static class SimplySignTotp
{
    const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    static byte[] Base32(string s)
    {
        s = s.Trim().TrimEnd('=').ToUpperInvariant();
        var bytes = new byte[s.Length * 5 / 8]; int buffer = 0, bits = 0, i = 0;
        foreach (char c in s)
        {
            int v = Alphabet.IndexOf(c); if (v < 0) throw new ArgumentException("Invalid Base32.");
            buffer = (buffer << 5) | v; bits += 5;
            if (bits >= 8) { bytes[i++] = (byte)(buffer >> (bits - 8)); bits -= 8; }
        }
        return bytes;
    }
    public static string Code(string secret, int digits, int period, string algorithm)
    {
        return CodeAt(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), secret, digits, period, algorithm);
    }
    public static string CodeAt(long unixSeconds, string secret, int digits, int period, string algorithm)
    {
        var key = Base32(secret);
        var counter = BitConverter.GetBytes(unixSeconds / period);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);
        HMAC hmac = algorithm == "SHA256" ? new HMACSHA256(key) : algorithm == "SHA512" ? (HMAC)new HMACSHA512(key) : new HMACSHA1(key);
        byte[] hash; using (hmac) { hash = hmac.ComputeHash(counter); }
        int offset = hash[hash.Length - 1] & 0x0f;
        int binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % (int)Math.Pow(10, digits)).ToString(new string('0', digits));
    }
}
"@

function Get-TotpPeriod { [math]::Floor([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() / $period) }

# A code sent late in its window can be validated after it expired, and SimplySign refuses a
# second code from the window in which one already failed; so always submit early in a window
# later than the last attempt's.
function Get-FreshCode([long]$AfterPeriod = -1) {
    while ((Get-TotpPeriod) -le $AfterPeriod) { Start-Sleep -Seconds 2 }
    $left = $period - ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() % $period)
    if ($left -lt 20) { Start-Sleep -Seconds ($left + 1) }
    [SimplySignTotp]::Code($secret, $digits, $period, $algorithm)
}

# --- Drive the login dialog ------------------------------------------------------------------
Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class SimplySignWin32
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    public static List<IntPtr> VisibleWindows(uint pid)
    {
        var found = new List<IntPtr>();
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) found.Add(h); return true; }, IntPtr.Zero);
        return found;
    }
    public static long Area(IntPtr h) { RECT r; GetWindowRect(h, out r); return (long)(r.Right - r.Left) * (r.Bottom - r.Top); }
}
"@

Get-Process -Name SimplySignDesktop -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Write-Host "== Opening the SimplySign session"
$process = Start-Process -FilePath $exe -PassThru
$shell = New-Object -ComObject WScript.Shell

function Get-AppWindows { @([SimplySignWin32]::VisibleWindows([uint32]$process.Id)) }

function Focus-Window([IntPtr]$Handle) {
    for ($i = 0; $i -lt 12; $i++) {
        [SimplySignWin32]::SetForegroundWindow($Handle) | Out-Null
        Start-Sleep -Milliseconds 300
        if ([SimplySignWin32]::GetForegroundWindow() -eq $Handle) { return $true }
    }
    return $false
}

# SendKeys drops characters into Qt fields now and then; an atomic paste does not.
function Paste-Text([string]$Text) {
    Set-Clipboard -Value $Text
    $shell.SendKeys("^a"); Start-Sleep -Milliseconds 120
    $shell.SendKeys("{DEL}"); Start-Sleep -Milliseconds 120
    $shell.SendKeys("^v"); Start-Sleep -Milliseconds 250
    Set-Clipboard -Value " "
}

# Every window of the app carries the same title; the login dialog is the large one, and any
# other visible window is a modal — the "new version" prompt (Yes/No; never answer Yes, that
# starts a download) or the "invalid user name or token" error (OK). The key ladder tries "No"
# before "Enter", so Enter can only ever reach the OK-only box.
function Dismiss-Modals([IntPtr]$Login) {
    $dismissed = $false
    foreach ($modal in (Get-AppWindows | Where-Object { $_ -ne $Login })) {
        foreach ($keys in @("%n", "{TAB} ", "{ENTER}")) {
            Focus-Window $modal | Out-Null
            $shell.SendKeys($keys); Start-Sleep -Milliseconds 800
            if (-not (Get-AppWindows | Where-Object { $_ -eq $modal })) { $dismissed = $true; break }
        }
    }
    $dismissed
}

$windows = @()
for ($i = 0; $i -lt 30 -and $windows.Count -eq 0; $i++) {
    Start-Sleep -Seconds 1
    $process.Refresh()
    if ($process.HasExited) { throw "SimplySign Desktop exited during startup (code $($process.ExitCode))." }
    $windows = Get-AppWindows
}
if ($windows.Count -eq 0) { throw "SimplySign Desktop showed no login dialog within 30 seconds." }
$login = $windows | Sort-Object { [SimplySignWin32]::Area($_) } -Descending | Select-Object -First 1

# Let the asynchronous version check raise its prompt, and clear it, before typing.
for ($i = 0; $i -lt 5; $i++) { Dismiss-Modals $login | Out-Null; Start-Sleep -Seconds 1 }

if (-not (Focus-Window $login)) { throw "Could not focus the SimplySign login dialog." }
Start-Sleep -Milliseconds 400
Paste-Text $UserName
$shell.SendKeys("{TAB}"); Start-Sleep -Milliseconds 200
Paste-Text (Get-FreshCode)
$lastPeriod = Get-TotpPeriod
$shell.SendKeys("{ENTER}")

# After a rejection the dialog keeps the account name and focuses the empty token field, so a
# retry pastes only a fresh code. Bounded: repeated bad codes lock the Certum account.
$retries = 0
for ($elapsed = 0; $elapsed -lt 180; $elapsed += 5) {
    Start-Sleep -Seconds 5
    $certificate = Get-SigningCertificate
    if ($certificate) {
        Write-Host "SimplySign session open: $($certificate.Subject), expires $($certificate.NotAfter.ToString('u'))"
        exit 0
    }
    if (Get-AppWindows | Where-Object { $_ -ne $login }) {
        if ($retries -ge 3) { throw "SimplySign rejected the credentials $retries times; check the account name, the otpauth URI and the clock." }
        $retries++
        if (Dismiss-Modals $login) {
            Start-Sleep -Milliseconds 500
            Paste-Text (Get-FreshCode -AfterPeriod $lastPeriod)
            $lastPeriod = Get-TotpPeriod
            $shell.SendKeys("{ENTER}")
        }
    }
}
throw "Certificate $Thumbprint did not appear in Cert:\CurrentUser\My within 180 seconds."
