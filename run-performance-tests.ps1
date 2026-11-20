param(
    [switch]$RegressionOnly
)

$ErrorActionPreference = 'Stop'
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compilerPath)) {
    throw '.NET Framework 4.x compiler was not found.'
}

$sourceFolder = Join-Path $PSScriptRoot 'Source'
$sourceFiles = @(Get-ChildItem -LiteralPath $sourceFolder -Filter '*.cs' |
    ForEach-Object { $_.FullName })
$testSource = Join-Path $PSScriptRoot 'Tests\QingJiePerf.cs'
$testBinary = Join-Path ([IO.Path]::GetTempPath()) (
    'QingJiePerf-' + [Guid]::NewGuid().ToString('N') + '.exe'
)

try {
    $compilerArgs = @(
        '/nologo', '/utf8output', '/target:exe', '/optimize+', '/main:QingJiePerf',
        '/reference:System.dll', '/reference:System.Core.dll',
        '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
        ('/out:' + $testBinary)
    ) + $sourceFiles + @($testSource)
    & $compilerPath @compilerArgs
    if ($LASTEXITCODE -ne 0) { throw "Performance test compilation failed: $LASTEXITCODE" }

    Write-Output 'Running synthetic image and redraw regressions...'
    & $testBinary regression
    if ($LASTEXITCODE -ne 0) { throw "A regression failed: $LASTEXITCODE" }

    if (-not $RegressionOnly) {
        Write-Output 'Running offscreen rendering benchmarks (not desktop FPS)...'
        & $testBinary all
        if ($LASTEXITCODE -ne 0) { throw "A performance benchmark failed: $LASTEXITCODE" }
    }
}
finally {
    if (Test-Path -LiteralPath $testBinary) {
        Remove-Item -LiteralPath $testBinary
    }
}
