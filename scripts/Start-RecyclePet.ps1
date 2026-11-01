param([ValidateSet('frog','maid')][string]$Skin = 'maid',[switch]$CheckOnly)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { Write-Output 'WINDOWS_REQUIRED: run this skill on your Windows computer. No files were processed.'; exit 2 }
$mutex = $null
try {
    . (Join-Path $PSScriptRoot 'Runtime.ps1')
    Import-RecycleRuntime
    if ($CheckOnly) { Write-Output '{"compile":"PASS","recycle_test":"NOT_RUN","files_processed":0}'; exit 0 }
    $created = $false
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $mutex = [Threading.Mutex]::new($true, ('Local\RecyclePet-' + $sid), [ref]$created)
    if (-not $created) { [Windows.MessageBox]::Show('桌宠已经在运行，请查看桌面右下角。','回收站桌宠') | Out-Null; exit 0 }
    [RecyclePet.PetWindow]::Run((Join-Path $script:SkillRoot 'assets'),$Skin)
} catch {
    $state = Join-Path $env:LOCALAPPDATA 'RecyclePet'
    [IO.Directory]::CreateDirectory($state) | Out-Null
    ($_ | Out-String) | Out-File -LiteralPath (Join-Path $state 'startup-error.txt') -Encoding utf8
    try { Add-Type -AssemblyName PresentationFramework; [Windows.MessageBox]::Show(('启动失败。错误已保存到：' + $state + '\startup-error.txt' + [Environment]::NewLine + $_.Exception.Message),'回收站桌宠') | Out-Null } catch {}
    Write-Error $_ -ErrorAction Continue
    exit 1
} finally {
    if ($mutex) { try { $mutex.ReleaseMutex() } catch {}; $mutex.Dispose() }
}
