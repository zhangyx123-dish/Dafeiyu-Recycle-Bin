$ErrorActionPreference = 'Stop'
$script:SkillRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
function Assert-RecycleWindows {
    if ($env:OS -ne 'Windows_NT') { throw 'WINDOWS_REQUIRED: run on the Windows computer containing the files. Cloud/Linux cannot recycle files on another computer.' }
    if ($PSVersionTable.PSEdition -eq 'Core') { throw 'Use Windows PowerShell 5.1: %WINDIR%\System32\WindowsPowerShell\v1.0\powershell.exe -STA' }
    if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'STA_REQUIRED: start Windows PowerShell with -STA.' }
}
function Assert-RecycleAssets {
    $manifest = Get-Content -LiteralPath (Join-Path $script:SkillRoot 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($item in $manifest.files) {
        $file = Join-Path $script:SkillRoot $item.path
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing bundled file: $($item.path)" }
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $item.sha256) { throw "Bundle integrity check failed: $($item.path)" }
    }
}
function Import-RecycleRuntime {
    Assert-RecycleWindows
    Assert-RecycleAssets
    if (-not ('RecyclePet.PetWindow' -as [type])) {
        $runtime = [Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
        $references = @('System.dll','System.Core.dll','WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll','System.Xaml.dll') | ForEach-Object {
            $direct = Join-Path $runtime $_
            $wpf = Join-Path (Join-Path $runtime 'WPF') $_
            if (Test-Path -LiteralPath $direct) { $direct } elseif (Test-Path -LiteralPath $wpf) { $wpf } else { throw "Missing Windows framework assembly: $_" }
        }
        $sourceFiles = @((Join-Path $PSScriptRoot 'native\RecycleNative.cs'),(Join-Path $PSScriptRoot 'native\PetWindow.cs'))
        Add-Type -Path $sourceFiles -ReferencedAssemblies $references -ErrorAction Stop
    }
    [RecyclePet.Native]::ProtectedRoot = $script:SkillRoot
}
