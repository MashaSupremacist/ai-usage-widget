param([switch]$Test)
$ErrorActionPreference = 'Stop'
$widgetCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $widgetCompiler)) { throw 'The Windows .NET Framework C# compiler was not found.' }
$widgetSources = @('Usage.cs', 'Widget.cs', 'BackgroundCrop.cs', 'UsageCache.cs', 'Accounts.cs', 'AccountDialog.cs', 'Antigravity.cs', 'Cursor.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$widgetReferences = @('/r:System.Web.Extensions.dll', '/r:System.Net.Http.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Management.dll', '/r:System.Security.dll')
& $widgetCompiler /nologo /warnaserror /target:winexe /main:UsageWidget.Program "/win32manifest:$PSScriptRoot\app.manifest" "/out:$PSScriptRoot\AIUsageWidget.exe" @widgetReferences @widgetSources
if ($LASTEXITCODE -ne 0) { throw 'App compilation failed.' }
if ($Test) {
    $widgetTestDirectory = Join-Path ([IO.Path]::GetTempPath()) ('AIUsageWidget-checks-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $widgetTestDirectory | Out-Null
    $widgetTestExecutable = Join-Path $widgetTestDirectory 'Tests.exe'
    try {
        $widgetChecks = @('Tests.cs', 'AccountChecks.cs', 'AntigravityChecks.cs', 'CursorChecks.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
        & $widgetCompiler /nologo /warnaserror /target:exe /main:UsageWidget.Tests "/out:$widgetTestExecutable" @widgetReferences @widgetSources @widgetChecks
        if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
        & $widgetTestExecutable
        if ($LASTEXITCODE -ne 0) { throw 'Checks failed.' }
    } finally {
        if (Test-Path -LiteralPath $widgetTestExecutable) { Remove-Item -LiteralPath $widgetTestExecutable }
        if ((Get-ChildItem -LiteralPath $widgetTestDirectory -Force | Measure-Object).Count -eq 0) { Remove-Item -LiteralPath $widgetTestDirectory }
    }
}
Write-Output "Built $PSScriptRoot\AIUsageWidget.exe"
