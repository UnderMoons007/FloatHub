param([switch]$SkipInstaller)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $output = Join-Path $PSScriptRoot 'artifacts'
    $portable = Join-Path $output 'win-x64'
    dotnet publish src/FloatHub/FloatHub.csproj -c Release -r win-x64 --self-contained true -o $portable -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE) { throw 'dotnet publish failed' }
    if (!$SkipInstaller) {
        $toolDir = Join-Path $PSScriptRoot '.test-work/wix'
        $wix = Join-Path $toolDir 'wix.exe'
        if (!(Test-Path -LiteralPath $wix)) {
            dotnet tool install wix --version 4.0.6 --tool-path $toolDir
            if ($LASTEXITCODE) { throw 'WiX tool restore failed' }
        }
        $binary = Join-Path $portable 'FloatHub.exe'
        $icon = Join-Path $PSScriptRoot 'src/FloatHub/FloatHub.ico'
        $msi = Join-Path $output 'FloatHub-1.0.2-x64.msi'
        & $wix build installer/FloatHub.wxs -arch x64 -d "Exe=$binary" -d "Icon=$icon" -intermediateFolder .test-work/installer -pdbtype none -out $msi
        if ($LASTEXITCODE) { throw 'MSI build failed' }
    }
} finally { Pop-Location }
