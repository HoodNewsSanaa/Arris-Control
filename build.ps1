[CmdletBinding()]
param([switch]$Offline)
$ErrorActionPreference='Stop'
$project=Join-Path $PSScriptRoot 'Arris-Control.csproj'
$arguments=@('publish',$project,'-c','Release','-r','win-x64','--self-contained','true',
    '-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true','-p:EnableSingleFileAnalyzer=false',
    '--ignore-failed-sources','-o',(Join-Path $PSScriptRoot 'dist'))
if ($Offline) {$arguments+='-p:RestoreSources='}
& dotnet @arguments
if ($LASTEXITCODE -ne 0) {throw 'Publish failed.'}
$directory=Join-Path $PSScriptRoot 'dist'
$built=Join-Path $directory 'Arris-Control.exe'
# Exercise the packaged single-file executable, not only the development DLL.
foreach ($test in '--self-test','--smoke-test') {
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=$built;$start.Arguments=$test;$start.WorkingDirectory=$directory
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $process=[Diagnostics.Process]::Start($start)
    $stdout=$process.StandardOutput.ReadToEnd();$stderr=$process.StandardError.ReadToEnd()
    $process.WaitForExit()
    if ($stdout) {Write-Output $stdout.Trim()}
    if ($process.ExitCode -ne 0) {throw ('Packaged executable check failed: '+$stderr)}
    $process.Dispose()
}
$asset=Join-Path $directory 'Arris-Control-win-x64.exe'
[IO.File]::Copy($built,$asset,$true)
$checksum=(Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $directory 'SHA256SUMS.txt'),($checksum+'  Arris-Control-win-x64.exe'+[Environment]::NewLine),(New-Object Text.UTF8Encoding($false)))
Get-Item -LiteralPath $asset | Select-Object Name,Length,FullName
