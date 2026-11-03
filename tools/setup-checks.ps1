# Checks for Setup.exe and the uninstaller on CI's clean runner: install, look at what's there, uninstall, look at
# what's left. They install for real, changing the PATH, the Start menu and the Apps list, so they're only for a
# throwaway machine such as CI's: never run them on your own PC.
# usage: . tools/setup-checks.ps1, then call the functions (see the installer job in .github/workflows/build.yml)

$ErrorActionPreference = 'Stop'

$UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\Tanjelo'

# Runs a program and waits for it alone (not for programs it starts), failing on a non-zero exit code.
function Invoke-AndWait([string]$Exe, [string[]]$Arguments, [int]$Seconds = 300) {
    $run = Start-Process $Exe -ArgumentList $Arguments -PassThru
    $null = $run.Handle
    if (-not $run.WaitForExit($Seconds * 1000)) { throw "$Exe $Arguments took more than $Seconds seconds" }
    if ($run.ExitCode -ne 0) {
        $log = Join-Path $env:TEMP 'Tanjelo-Setup.log'
        if (Test-Path $log) { Get-Content $log -Tail 20 | Write-Host }
        throw "$Exe $Arguments failed with exit code $($run.ExitCode)"
    }
}

function Get-Setup { (Get-ChildItem release -Filter *-Setup.exe | Select-Object -First 1).FullName }

function Get-StartMenu([string]$Scope) {
    if ($Scope -eq 'machine') { "$env:ProgramData\Microsoft\Windows\Start Menu\Programs" } else { "$env:APPDATA\Microsoft\Windows\Start Menu\Programs" }
}

function Get-AppsEntry([string]$Scope) {
    $hive = if ($Scope -eq 'machine') { 'HKLM:' } else { 'HKCU:' }
    Get-ItemProperty "$hive\$UninstallKey" -ErrorAction SilentlyContinue
}

function Get-SavedPath([string]$Scope) {
    [Environment]::GetEnvironmentVariable('PATH', $(if ($Scope -eq 'machine') { 'Machine' } else { 'User' }))
}

function Get-ShortcutTarget([string]$Path) {
    if (-not (Test-Path $Path)) { return $null }
    (New-Object -ComObject WScript.Shell).CreateShortcut($Path).TargetPath
}

function New-Shortcut([string]$Path, [string]$Target) {
    $link = (New-Object -ComObject WScript.Shell).CreateShortcut($Path)
    $link.TargetPath = $Target
    $link.Save()
}

# Everything an install should have put in place, for the scope, in the folder.
function Test-Installed([string]$Folder, [string]$Scope) {
    foreach ($file in 'Tanjelo.exe', 'tanj.exe', 'Uninstall Tanjelo.exe', 'installed-files.txt', 'LICENSE.txt', 'licenses\dotnet-LICENSE.txt') {
        if (-not (Test-Path (Join-Path $Folder $file))) { throw "missing $Folder\$file" }
    }
    $listed = Get-Content (Join-Path $Folder 'installed-files.txt') | Where-Object { $_ -and -not $_.StartsWith('#') }
    $present = Get-ChildItem $Folder -Recurse -File | Where-Object Name -ne 'installed-files.txt'
    if ($listed.Count -ne $present.Count) { throw "the list has $($listed.Count) files, the folder $($present.Count)" }
    $shortcut = Join-Path (Get-StartMenu $Scope) 'Tanjelo.lnk'
    if ((Get-ShortcutTarget $shortcut) -ne (Join-Path $Folder 'Tanjelo.exe')) { throw "the Start menu entry doesn't open $Folder\Tanjelo.exe" }
    $entry = Get-AppsEntry $Scope
    if (-not $entry) { throw "no Apps entry for $Scope" }
    if ($entry.QuietUninstallString -ne "`"$Folder\Uninstall Tanjelo.exe`" --silent") { throw "unexpected QuietUninstallString: $($entry.QuietUninstallString)" }
    Write-Host "Apps: $($entry.DisplayName) $($entry.DisplayVersion) by $($entry.Publisher), in $($entry.InstallLocation)"
    if ((Get-SavedPath $Scope) -split ';' -notcontains $Folder) { throw "$Folder isn't on the $Scope PATH" }
}

# Uses the installed copy as a terminal and a person would: tanj from the PATH, then the app itself, then --quit.
function Test-InstalledCopy([string]$Folder) {
    $env:PATH = [Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('PATH', 'User')
    $tanj = (Get-Command tanj -ErrorAction Stop).Source
    if ($tanj -ne (Join-Path $Folder 'tanj.exe')) { throw "tanj on the PATH is $tanj" }
    tanj tools
    if ($LASTEXITCODE -ne 0) { throw "tanj tools failed" }
    Add-Type -AssemblyName System.Drawing
    $bitmap = [System.Drawing.Bitmap]::new(64, 48)
    [System.Drawing.Graphics]::FromImage($bitmap).Clear([System.Drawing.Color]::Orange)
    $bitmap.Save("$env:RUNNER_TEMP\smoke.png")
    $bitmap.Dispose()
    foreach ($step in @(@('jpg', "$env:RUNNER_TEMP\smoke.png"), @('pdf', "$env:RUNNER_TEMP\smoke.png"), @('png', "$env:RUNNER_TEMP\smoke.pdf"))) {
        tanj convert $step[0] $step[1]
        if ($LASTEXITCODE -ne 0) { throw "tanj convert $($step -join ' ') failed" }
    }
    # The app starts with only the .NET it carries, is still running a few seconds later, and quits when asked.
    $app = Start-Process (Join-Path $Folder 'Tanjelo.exe') -PassThru
    Start-Sleep 8
    if ($app.HasExited) { throw "Tanjelo.exe exited with code $($app.ExitCode)" }
    Invoke-AndWait (Join-Path $Folder 'Tanjelo.exe') '--quit' 30
    if (-not $app.WaitForExit(15000)) { throw "Tanjelo.exe --quit didn't make it quit" }
    Remove-Item "$env:RUNNER_TEMP\smoke.*"
}

# What Tanjelo adds to Windows for its user from its settings, as if they were turned on, for the uninstaller to take away.
function Set-TanjeloExtras([string]$Exe) {
    New-Item -Force 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' | Out-Null
    Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' Tanjelo "`"$Exe`" --autostart"
    New-Shortcut "$env:APPDATA\Microsoft\Windows\SendTo\Tanjelo.lnk" $Exe
}

# The install folder goes just after the uninstaller's own file, once nothing else is left in it.
function Assert-FolderGone([string]$Folder) {
    for ($i = 0; $i -lt 15 -and (Test-Path $Folder); $i++) { Start-Sleep 1 }
    if (Test-Path $Folder) { throw "the install folder is still there: $((Get-ChildItem $Folder -Recurse -Name) -join ', ')" }
}

# Runs the uninstaller as Apps would, silently, and checks that what setup added is gone.
function Invoke-Uninstall([string]$Folder, [string]$Scope) {
    $entry = Get-AppsEntry $Scope
    $exe = Join-Path $Folder 'Uninstall Tanjelo.exe'
    if ($entry.QuietUninstallString -ne "`"$exe`" --silent") { throw "unexpected QuietUninstallString: $($entry.QuietUninstallString)" }
    Invoke-AndWait $exe '--silent'
    # The uninstaller removes itself just after it exits.
    for ($i = 0; $i -lt 60 -and (Test-Path $exe); $i++) { Start-Sleep 1 }
    $left = @()
    if (Test-Path $exe) { $left += 'the uninstaller' }
    if (Test-Path (Join-Path $Folder 'Tanjelo.exe')) { $left += 'Tanjelo.exe' }
    if (Test-Path (Join-Path $Folder 'installed-files.txt')) { $left += 'the list of files' }
    if (Test-Path (Join-Path (Get-StartMenu $Scope) 'Tanjelo.lnk')) { $left += 'the Start menu entry' }
    if (Get-AppsEntry $Scope) { $left += 'the Apps entry' }
    if ((Get-SavedPath $Scope) -split ';' -contains $Folder) { $left += 'the PATH entry' }
    if ((Get-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Run -ErrorAction SilentlyContinue).Tanjelo) { $left += 'Start with Windows' }
    if (Test-Path "$env:APPDATA\Microsoft\Windows\SendTo\Tanjelo.lnk") { $left += 'the Send to entry' }
    foreach ($data in 'log.txt', 'update.json', 'Clipboard', 'WebView2') {
        if (Test-Path "$env:LOCALAPPDATA\Tanjelo\$data") { $left += "%LOCALAPPDATA%\Tanjelo\$data" }
    }
    if ($left) { throw "left behind: $($left -join '; ')" }
}
