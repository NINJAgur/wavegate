# Builds dist\WaveGate-Setup.exe: the APO, both .NET apps (self-contained, so no runtime is
# needed on the target PC) and the Inno Setup installer around them.
param([string]$Version = "1.0.0")
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

function Find-Tool([string[]]$candidates) {
    foreach ($c in $candidates) { if ($c -and (Test-Path $c)) { return $c } }
    return $null
}

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Find-Tool @("$env:ProgramFiles\dotnet\dotnet.exe") }
if (-not $dotnet) { throw "dotnet SDK not found." }

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = if (Test-Path $vswhere) {
    & $vswhere -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
}
if (-not $msbuild) { throw "MSBuild (Visual Studio with C++) not found; it is needed to build the APO DLL." }

$iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $iscc = Find-Tool @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
                        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")
}
if (-not $iscc) { throw "Inno Setup not found. Install it with: winget install JRSoftware.InnoSetup" }

$app = Join-Path $PSScriptRoot "dist\app"
if (Test-Path $app) { Remove-Item $app -Recurse -Force } # our own previous output
New-Item -ItemType Directory -Force $app | Out-Null

Write-Host "Building APO..."
& $msbuild src\WaveGate.Apo\WaveGate.Apo.vcxproj /p:Configuration=Release /p:Platform=x64 /v:minimal /nologo
if ($LASTEXITCODE) { throw "APO build failed." }
Copy-Item src\WaveGate.Apo\x64\Release\WaveGateApo.dll $app

# Both apps go into one folder so they share a single copy of the .NET runtime.
foreach ($project in "src\WaveGate.App", "src\WaveGate.Installer") {
    Write-Host "Publishing $project..."
    & $dotnet publish $project -c Release -r win-x64 --self-contained true -o $app -p:DebugType=none -p:Version=$Version --nologo -v:minimal
    if ($LASTEXITCODE) { throw "Publishing $project failed." }
}

Write-Host "Packaging..."
& $iscc /Q "/DAppVersion=$Version" installer\WaveGate.iss
if ($LASTEXITCODE) { throw "Inno Setup failed." }

Get-Item "dist\WaveGate-Setup.exe" | ForEach-Object { "Built $($_.FullName) ($([math]::Round($_.Length / 1MB, 1)) MB)" }
