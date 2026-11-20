$ErrorActionPreference = 'Stop'
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compilerPath)) { throw '.NET Framework 4.x compiler was not found.' }
$sourceFolder = Join-Path $PSScriptRoot 'Source'
$sourceFiles = @(Get-ChildItem -LiteralPath $sourceFolder -Filter '*.cs' | ForEach-Object { $_.FullName })
$compilerArgs = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/utf8output',
    '/reference:System.dll', '/reference:System.Core.dll',
    '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
    ('/win32manifest:' + (Join-Path $sourceFolder 'app.manifest')),
    ('/win32icon:' + (Join-Path $sourceFolder 'App.ico')),
    ('/out:' + (Join-Path $PSScriptRoot 'QingJie.exe'))
) + $sourceFiles
& $compilerPath @compilerArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
Write-Output ('Built: ' + (Join-Path $PSScriptRoot 'QingJie.exe'))
