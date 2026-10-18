# Builds the Digital Modes module and packages it for Kainos (Setup > Modules > Install from file, or a GitHub
# release listed in Kainos's modules.json).
#
#   .\build.ps1 [-Wsjtx C:\WSJT\wsjtx] [-KainosBin "<Kainos>\Project Files\bin\x64\Release\"] [-SkipTests]
#
# Writes dist\kainos-digimodes-<version>.zip and dist\registry-entry.json (the entry for modules.json, with the
# zip's SHA-256; fill in the release URL).

param(
    [string]$Wsjtx = "C:\WSJT\wsjtx",
    [string]$KainosBin = "",
    [switch]$SkipTests
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -prerelease -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
if (-not $msbuild) { throw "MSBuild not found (install Visual Studio with .NET desktop development)" }
$props = @("/p:Configuration=Release", "/v:minimal", "/nologo")
if ($KainosBin) { $props += "/p:KainosBin=$KainosBin" }

$meta = Get-Content "$root\module.json" -Raw | ConvertFrom-Json
$ver = $meta.version
Write-Host "Building $($meta.name) $ver"
& $msbuild "$root\tests\Kainos.DigiModes.SelfTest\Kainos.DigiModes.SelfTest.csproj" @props
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$bin = Join-Path $Wsjtx "bin"
if (-not $SkipTests) {
    & "$root\tests\Kainos.DigiModes.SelfTest\bin\Release\Kainos.DigiModes.SelfTest.exe" $bin
    if ($LASTEXITCODE -ne 0) { throw "self-test failed" }
}

# stage: the module, its documents, and the decoder (jt9, ft8code and the DLLs they need, from WSJT-X)
$stage = Join-Path $root "dist\stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force "$stage\decoder" | Out-Null
Copy-Item "$root\module.json", "$root\LICENSE", "$root\README.md", "$root\THIRD-PARTY.md" $stage
Copy-Item "$root\src\Kainos.DigiModes\bin\Release\Kainos.DigiModes.dll" $stage
$decoder = @("jt9.exe", "ft8code.exe", "jt65code.exe", "jt9code.exe", "wsprd.exe", "libfftw3f-3.dll", "libgcc_s_seh-1.dll", "libgfortran-4.dll", "libgomp-1.dll",
             "libquadmath-0.dll", "libstdc++-6.dll", "libwinpthread-1.dll", "Qt5Core.dll")
foreach ($f in $decoder) {
    $p = Join-Path $bin $f
    if (-not (Test-Path $p)) { throw "$p not found: point -Wsjtx at a WSJT-X install" }
    Copy-Item $p "$stage\decoder"
}
# cty.dat (AD1C's country file, MIT-style licence) for the DXCC lookups
$share = Join-Path $Wsjtx "share\wsjtx"
if (Test-Path "$share\cty.dat") { Copy-Item "$share\cty.dat" $stage; if (Test-Path "$share\cty.dat_copyright.txt") { Copy-Item "$share\cty.dat_copyright.txt" $stage } }
else { Write-Warning "cty.dat not found in ${share}: decodes won't show countries" }
$wsjtxDoc = Join-Path $Wsjtx "share\doc\wsjtx"
foreach ($f in "COPYING", "AUTHORS") { if (Test-Path "$wsjtxDoc\$f") { Copy-Item "$wsjtxDoc\$f" "$stage\decoder\WSJT-X-$f.txt" } }

# zip it (entries with forward slashes, module.json at the top)
$zip = Join-Path $root "dist\kainos-digimodes-$ver.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$z = [System.IO.Compression.ZipFile]::Open($zip, "Create")
try {
    Get-ChildItem $stage -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($stage.Length + 1).Replace("\", "/")
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $_.FullName, $rel, "Optimal") | Out-Null
    }
} finally { $z.Dispose() }

$sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item $zip).Length
$entry = [ordered]@{
    id = $meta.id; name = $meta.name; description = $meta.description; version = $ver; api = $meta.api
    minKainos = $meta.minKainos; entry = $meta.entry; author = $meta.author; license = $meta.license; homepage = $meta.homepage
    url = "https://github.com/PNWHam-K7JUS/kainos-module-digimodes/releases/download/v$ver/kainos-digimodes-$ver.zip"
    sha256 = $sha; size = $size
}
$entry | ConvertTo-Json | Set-Content (Join-Path $root "dist\registry-entry.json") -Encoding utf8
Write-Host ""
Write-Host "Packaged $zip ($([math]::Round($size / 1MB, 1)) MB)"
Write-Host "SHA-256 $sha"
