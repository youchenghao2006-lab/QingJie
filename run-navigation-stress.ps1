param(
    [ValidateSet('both', 'standalone', 'host')]
    [string]$Mode = 'both',
    # Optional directory containing an earlier complete Source snapshot.
    [string]$SourceDirectory = (Join-Path $PSScriptRoot 'Source'),
    [switch]$CompileOnly
)

$ErrorActionPreference = 'Stop'
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
$testBinary = Join-Path ([IO.Path]::GetTempPath()) ('QingJieStress-' + [Guid]::NewGuid().ToString('N') + '.exe')
$sourceFiles = @(Get-ChildItem -LiteralPath $SourceDirectory -Filter '*.cs' | ForEach-Object { $_.FullName })
$fixture = Join-Path (Join-Path $PSScriptRoot 'Tests') 'QingJieNavigationStress.cs'
try {
    $compilerArgs = @('/nologo', '/optimize+', '/target:exe', '/main:QingJieNavigationStress',
        '/reference:System.dll', '/reference:System.Core.dll',
        '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
        ('/out:' + $testBinary)) + $sourceFiles + @($fixture)
    & $compilerPath @compilerArgs
    if ($LASTEXITCODE -ne 0) { throw 'Navigation stress fixture compilation failed.' }
    if ($CompileOnly) {
        Write-Output 'Navigation stress fixture compiled successfully.'
    }
    else {
        & $testBinary $Mode
        if ($LASTEXITCODE -ne 0) { throw 'Navigation stress fixture failed.' }
    }
}
finally {
    if (Test-Path -LiteralPath $testBinary) { Remove-Item -LiteralPath $testBinary }
}
