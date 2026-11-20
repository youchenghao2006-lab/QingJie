$ErrorActionPreference = 'Stop'
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
$testDirectory = if ([String]::IsNullOrWhiteSpace($env:QINGJIE_TEST_TEMP)) {
    [IO.Path]::GetTempPath()
} else {
    [IO.Path]::GetFullPath($env:QINGJIE_TEST_TEMP)
}
[IO.Directory]::CreateDirectory($testDirectory) | Out-Null
$testBinary = Join-Path $testDirectory ('QingJieQuickTests-' + [Guid]::NewGuid().ToString('N') + '.exe')
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Source') -Filter '*.cs' | ForEach-Object { $_.FullName })
$testSource = Join-Path $PSScriptRoot 'Tests\QuickCaptureTests.cs'
try {
    $compilerArgs = @('/nologo', '/target:exe', '/main:QuickCaptureTests',
        '/reference:System.dll', '/reference:System.Core.dll',
        '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
        ('/out:' + $testBinary)) + $sourceFiles + @($testSource)
    & $compilerPath @compilerArgs
    if ($LASTEXITCODE -ne 0) { throw 'Quick capture test compilation failed.' }
    & $testBinary
    if ($LASTEXITCODE -ne 0) { throw 'A quick capture test failed.' }
}
finally {
    if (Test-Path -LiteralPath $testBinary) { Remove-Item -LiteralPath $testBinary }
}
