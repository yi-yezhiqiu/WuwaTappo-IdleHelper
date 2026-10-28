param(
    [switch]$CheckWindowsProtection,
    [string]$OutputPath = 'TappoIdleHelper.exe'
)

$ErrorActionPreference = 'Stop'
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compilerPath)) { throw '需要 Windows .NET Framework 4 编译器。' }
Push-Location -LiteralPath $PSScriptRoot
try {
    & $compilerPath /nologo /utf8output /warnaserror+ /target:exe /out:tests\EngineTests.exe src\IdleEngine.cs tests\EngineTests.cs
    if ($LASTEXITCODE -ne 0) { throw '测试编译失败。' }
    & (Join-Path $PSScriptRoot 'tests\EngineTests.exe')
    if ($LASTEXITCODE -ne 0) { throw '测试失败。' }
    & $compilerPath /nologo /utf8output /warnaserror+ /target:exe /out:tests\LoopGuardTests.exe src\LoopGuard.cs tests\LoopGuardTests.cs
    if ($LASTEXITCODE -ne 0) { throw '卡顿保护测试编译失败。' }
    & (Join-Path $PSScriptRoot 'tests\LoopGuardTests.exe')
    if ($LASTEXITCODE -ne 0) { throw '卡顿保护测试失败。' }
    & $compilerPath /nologo /utf8output /warnaserror+ /optimize+ /target:winexe /platform:anycpu /win32manifest:src\app.manifest /r:System.Windows.Forms.dll /r:System.Drawing.dll "/out:$OutputPath" src\AssemblyInfo.cs src\IdleEngine.cs src\LoopGuard.cs src\NativeInput.cs src\Program.cs
    if ($LASTEXITCODE -ne 0) { throw '程序编译失败。' }
    if ($CheckWindowsProtection) {
        & $compilerPath /nologo /utf8output /warnaserror+ /target:exe /r:System.Windows.Forms.dll /out:tests\WindowsProtectionSmoke.exe src\NativeInput.cs tests\WindowsProtectionSmoke.cs
        if ($LASTEXITCODE -ne 0) { throw 'Windows 保护检查编译失败。' }
        & (Join-Path $PSScriptRoot 'tests\WindowsProtectionSmoke.exe')
        if ($LASTEXITCODE -ne 0) { throw 'Windows 当前会话不允许安装输入监听或急停快捷键。' }
    }
    Write-Output "Build OK: $OutputPath"
}
finally { Pop-Location }
