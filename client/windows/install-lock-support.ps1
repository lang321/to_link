# REQ-REMOTE-20261110-01. Run elevated on the same account that runs ToLink.
param([switch]$Uninstall)
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '"'
    if ($Uninstall) { $arguments += ' -Uninstall' }
    $elevated = Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments -Wait -PassThru
    exit $elevated.ExitCode
}
$serviceName = 'ToLinkDesktop'
$target = Join-Path $env:ProgramFiles 'ToLinkDesktop'
$existing = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($existing) { Stop-Service $serviceName -Force; $existing.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(15)) }
if ($Uninstall) {
    if ($existing) { & sc.exe delete $serviceName; if ($LASTEXITCODE -ne 0) { throw 'Service removal failed' } }
    Write-Host 'Lock support service removed. ToLink device settings are preserved.'
    exit
}
$source = Join-Path $PSScriptRoot 'ToLink.DesktopHost.exe'
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw 'ToLink.DesktopHost.exe is missing.' }
# Protect both the executable and its directory from replacement by ordinary users.
if (Test-Path -LiteralPath $target) {
    if ((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Installation directory must not be a reparse point.' }
} else { New-Item -ItemType Directory -Path $target | Out-Null }
$acl = New-Object Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true,$false)
foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
    $rule = New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)),'FullControl','ContainerInherit,ObjectInherit','None','Allow')
    $acl.AddAccessRule($rule)
}
Set-Acl -LiteralPath $target -AclObject $acl
$binary = Join-Path $target 'ToLink.DesktopHost.exe'
Copy-Item -LiteralPath $source -Destination $binary -Force
$binPath = '"' + $binary + '" --service ' + $identity.User.Value
if ($existing) {
    $change = Invoke-CimMethod -InputObject (Get-CimInstance Win32_Service -Filter "Name='$serviceName'") -MethodName Change -Arguments @{PathName=$binPath; StartMode='Automatic'; StartName='LocalSystem'}
    if ($change.ReturnValue -ne 0) { throw 'Service update failed' }
} else { New-Service -Name $serviceName -BinaryPathName $binPath -StartupType Automatic -DisplayName 'ToLink lock-screen support' | Out-Null }
& sc.exe description $serviceName 'REQ-REMOTE-20261110-01: local console screen and input helper for the installing user.'
Start-Service $serviceName
(Get-Service $serviceName).WaitForStatus('Running',[TimeSpan]::FromSeconds(15))
Write-Host 'Lock support installed for the current account. Start ToLink normally; no system password is stored.'
