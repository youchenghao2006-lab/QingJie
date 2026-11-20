param()
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
$temporaryFolder = if ($env:QINGJIE_TEST_TEMP) { $env:QINGJIE_TEST_TEMP } else { [IO.Path]::GetTempPath() }
$binary = Join-Path $temporaryFolder ('QingJieScrollTests-' + [Guid]::NewGuid().ToString('N') + '.exe')
try {
    $arguments = @('/nologo', '/utf8output', '/target:exe', '/optimize+', '/main:ScrollStitchTests',
        '/reference:System.dll', '/reference:System.Drawing.dll', ('/out:' + $binary),
        (Join-Path $PSScriptRoot 'Source\ScrollStitcher.cs'),
        (Join-Path $PSScriptRoot 'Tests\ScrollStitchTests.cs'))
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Scroll test compilation failed.' }
    & $binary
    if ($LASTEXITCODE -ne 0) { throw 'Scroll regression failed.' }
}
finally {
    if (Test-Path -LiteralPath $binary) { Remove-Item -LiteralPath $binary }
}
