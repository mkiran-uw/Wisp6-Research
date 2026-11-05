$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'MSVC is required for the portable firmware tests. Install Visual Studio C++ build tools.'
}
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) {
    throw 'No Visual Studio installation with C++ build tools was found.'
}
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$output = Join-Path ([IO.Path]::GetTempPath()) ('wisp-camera-tests-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $output
try {
    Push-Location $output
    try {
        $command = "call `"$vcvars`" >nul && cl /nologo /W4 /WX /std:c11 /D_CRT_SECURE_NO_WARNINGS /Fe:camera_protocol_test.exe `"$root\tests\firmware\camera_protocol_test.c`" `"$root\HimaxCamera\camera_protocol.c`" && camera_protocol_test.exe camera-windows.bin"
        & $env:ComSpec /d /c $command
        if ($LASTEXITCODE -ne 0) {
            throw "Native firmware tests failed (exit $LASTEXITCODE)."
        }
    }
    finally {
        Pop-Location
    }
    & dotnet run --project "$PSScriptRoot\camera-reader\CameraReader.Tests.csproj" -- (Join-Path $output 'camera-windows.bin')
    if ($LASTEXITCODE -ne 0) {
        throw "Receiver tests failed (exit $LASTEXITCODE)."
    }
}
finally {
    foreach ($name in 'camera_protocol.obj', 'camera_protocol_test.obj', 'camera_protocol_test.exe', 'camera-windows.bin') {
        $path = Join-Path $output $name
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path
        }
    }
    Remove-Item -LiteralPath $output
}
