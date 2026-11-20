param(
    [ValidateRange(1, 100)]
    [int]$SwitchIterations = 10,
    [switch]$CompileOnly
)

$ErrorActionPreference = 'Stop'
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
$temporaryPrefix = Join-Path ([IO.Path]::GetTempPath()) ('QingJieNavigation-' + [Guid]::NewGuid().ToString('N'))
$switchBinary = $temporaryPrefix + '-switch.exe'
$viewBinary = $temporaryPrefix + '-view.exe'
$sourceDirectory = Join-Path $PSScriptRoot 'Source'
$testDirectory = Join-Path $PSScriptRoot 'Tests'
$references = @('/nologo', '/target:exe', '/optimize+',
    '/reference:System.dll', '/reference:System.Core.dll',
    '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll')

try {
    $sourceFiles = @(Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' | ForEach-Object { $_.FullName })
    $switchArgs = $references + @('/main:QingJieSwitchBench', ('/out:' + $switchBinary)) +
        $sourceFiles + @((Join-Path $testDirectory 'QingJieSwitchBench.cs'))
    & $compilerPath @switchArgs
    if ($LASTEXITCODE -ne 0) { throw 'Workspace-switch benchmark compilation failed.' }

    # The isolated board fixture supplies its own DrawingTool enum.
    $viewArgs = $references + @('/main:BoardViewPerf', ('/out:' + $viewBinary),
        (Join-Path $sourceDirectory 'BoardCanvas.cs'),
        (Join-Path $sourceDirectory 'BoardViewSnapshot.cs'),
        (Join-Path $sourceDirectory 'SmoothStroke.cs'),
        (Join-Path $testDirectory 'BoardViewPerf.cs'))
    & $compilerPath @viewArgs
    if ($LASTEXITCODE -ne 0) { throw 'Board-navigation benchmark compilation failed.' }

    if ($CompileOnly) {
        Write-Output 'Both navigation benchmark fixtures compiled successfully.'
    }
    else {
        Write-Output 'Synthetic offscreen measurements; these are not desktop frame-rate measurements.'
        & $switchBinary $SwitchIterations
        if ($LASTEXITCODE -ne 0) { throw 'Workspace-switch benchmark failed.' }
        & $viewBinary
        if ($LASTEXITCODE -ne 0) { throw 'Board-navigation benchmark failed.' }
    }
}
finally {
    foreach ($temporaryBinary in @($switchBinary, $viewBinary)) {
        if (Test-Path -LiteralPath $temporaryBinary) { Remove-Item -LiteralPath $temporaryBinary }
    }
}
