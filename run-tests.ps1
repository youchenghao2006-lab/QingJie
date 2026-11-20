$ErrorActionPreference = 'Stop'
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
$testFolder = if ($env:QINGJIE_TEST_TEMP) { $env:QINGJIE_TEST_TEMP } else { [IO.Path]::GetTempPath() }
$testBinary = Join-Path $testFolder ('QingJieTests-' + [Guid]::NewGuid().ToString('N') + '.exe')
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Source') -Filter '*.cs' | ForEach-Object { $_.FullName })
$testSources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Tests') -Filter 'QingJieTests*.cs' | ForEach-Object { $_.FullName })
try {
    $compilerArgs = @('/nologo', '/target:exe', '/main:QingJieTests',
        '/reference:System.dll', '/reference:System.Core.dll',
        '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
        ('/out:' + $testBinary)) + $sourceFiles + $testSources
    & $compilerPath @compilerArgs
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    & $testBinary
    if ($LASTEXITCODE -ne 0) { throw 'A test failed.' }
}
finally {
    if (Test-Path -LiteralPath $testBinary) { Remove-Item -LiteralPath $testBinary }
}
