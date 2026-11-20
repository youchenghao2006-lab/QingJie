param(
    [switch]$Benchmark,
    [switch]$BenchmarkOnly,
    [string]$CaptureSource
)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
$temporaryFolder = if ($env:QINGJIE_TEST_TEMP) { $env:QINGJIE_TEST_TEMP } else { [IO.Path]::GetTempPath() }
$binary = Join-Path $temporaryFolder ('QingJieOverlayTests-' + [Guid]::NewGuid().ToString('N') + '.exe')
if (-not $CaptureSource) { $CaptureSource = Join-Path $PSScriptRoot 'Source\Capture.cs' }
$CaptureSource = (Resolve-Path -LiteralPath $CaptureSource).Path
try {
    $commonArguments = @('/nologo', '/utf8output', '/target:exe', '/optimize+',
        '/reference:System.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
        ('/out:' + $binary), $CaptureSource)
    if (-not $BenchmarkOnly) {
        $arguments = $commonArguments + @('/main:RegionOverlayTests',
            (Join-Path $PSScriptRoot 'Tests\RegionOverlayTests.cs'))
        & $compiler @arguments
        if ($LASTEXITCODE -ne 0) { throw 'Overlay test compilation failed.' }
        & $binary
        if ($LASTEXITCODE -ne 0) { throw 'Overlay regression failed.' }
    }
    if ($Benchmark -or $BenchmarkOnly) {
        $arguments = $commonArguments + @('/main:RegionOverlayPerf',
            (Join-Path $PSScriptRoot 'Tests\RegionOverlayPerf.cs'))
        & $compiler @arguments
        if ($LASTEXITCODE -ne 0) { throw 'Overlay benchmark compilation failed.' }
        Write-Output 'Synthetic 4K/dual-4K native-memory rendering; 16 input events/frame. Excludes desktop capture and compositor presentation.'
        & $binary burst
        if ($LASTEXITCODE -ne 0) { throw 'Overlay benchmark failed.' }
    }
}
finally {
    if (Test-Path -LiteralPath $binary) { Remove-Item -LiteralPath $binary }
}
