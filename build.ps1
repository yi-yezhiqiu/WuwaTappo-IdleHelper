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
$uiaRoot = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL'
$uiaReferences = foreach ($assemblyName in @('UIAutomationClient', 'UIAutomationTypes', 'WindowsBase')) {
    $assemblyPath = Get-ChildItem -LiteralPath (Join-Path $uiaRoot $assemblyName) -Filter ($assemblyName + '.dll') -Recurse -File | Select-Object -First 1 -ExpandProperty FullName
    if (-not $assemblyPath) { throw ('找不到系统 UI Automation 程序集：' + $assemblyName) }
    '/r:' + $assemblyPath
}
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
    & $compilerPath /nologo /utf8output /warnaserror+ /target:exe /out:tests\ChestEngineTests.exe src\ChestEngine.cs tests\ChestEngineTests.cs
    if ($LASTEXITCODE -ne 0) { throw '开箱策略测试编译失败。' }
    & (Join-Path $PSScriptRoot 'tests\ChestEngineTests.exe')
    if ($LASTEXITCODE -ne 0) { throw '开箱策略测试失败。' }
    & $compilerPath /nologo /utf8output /warnaserror+ /target:exe /out:tests\HeartbeatLeaseTests.exe src\HeartbeatLease.cs tests\HeartbeatLeaseTests.cs
    if ($LASTEXITCODE -ne 0) { throw '后台安全心跳测试编译失败。' }
    & (Join-Path $PSScriptRoot 'tests\HeartbeatLeaseTests.exe')
    if ($LASTEXITCODE -ne 0) { throw '后台安全心跳测试失败。' }
    & $compilerPath /nologo /utf8output /warnaserror+ /target:exe /main:UiIntegrationTests /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:tests\UiIntegrationTests.exe src\Program.cs src\IdleEngine.cs src\LoopGuard.cs tests\UiIntegrationStubs.cs tests\UiIntegrationTests.cs
    if ($LASTEXITCODE -ne 0) { throw '界面集成测试编译失败。' }
    & (Join-Path $PSScriptRoot 'tests\UiIntegrationTests.exe')
    if ($LASTEXITCODE -ne 0) { throw '界面集成测试失败。' }
    & $compilerPath /nologo /utf8output /warnaserror+ /optimize+ /target:winexe /platform:anycpu /win32manifest:src\app.manifest /r:System.Windows.Forms.dll /r:System.Drawing.dll $uiaReferences "/out:$OutputPath" src\AssemblyInfo.cs src\IdleEngine.cs src\LoopGuard.cs src\HeartbeatLease.cs src\NativeInput.cs src\ChestEngine.cs src\ChestService.cs src\Program.cs
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
