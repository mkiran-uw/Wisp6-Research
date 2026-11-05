param(
    [string]$CompilerRoot = 'C:\ti\ccs1210\ccs\tools\compiler\ti-cgt-msp430_21.6.0.LTS',
    [string]$DeviceInclude = 'C:\ti\ccs1210\ccs\ccs_base\msp430\include'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $CompilerRoot 'bin\cl430.exe'
if (-not (Test-Path -LiteralPath $compiler) -or -not (Test-Path -LiteralPath (Join-Path $DeviceInclude 'msp430.h'))) {
    throw 'TI MSP430 compiler or device headers were not found. Supply -CompilerRoot and -DeviceInclude.'
}
$output = Join-Path $PSScriptRoot 'Debug'
$objectsDirectory = Join-Path $output 'objects'
$null = New-Item -ItemType Directory -Path $objectsDirectory -Force
$options = @(
    '--silicon_version=mspx', '--code_model=large', '--data_model=restricted', '--abi=eabi',
    '--define=__MSP430FR5969__', "--include_path=$DeviceInclude",
    "--include_path=$CompilerRoot\include", "--include_path=$root\wisp-base",
    '--display_error_number', '--diag_wrap=off', '--compile_only', "--obj_directory=$objectsDirectory"
)
$sources = @(Get-ChildItem -LiteralPath "$root\wisp-base" -Recurse -File -Filter '*.c')
$sources += @(Get-ChildItem -LiteralPath "$root\wisp-base\RFID" -File -Filter '*.asm')
$sources += @(Get-ChildItem -LiteralPath "$root\wisp-base\Math" -File -Filter '*.asm')
$sources += @(Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -File -Filter '*.c' |
    Where-Object { $_.DirectoryName -eq $PSScriptRoot -or $_.DirectoryName -eq "$PSScriptRoot\Camera" })
$sources += Get-Item -LiteralPath "$PSScriptRoot\isr-link.asm"
$objects = @()
foreach ($source in $sources) {
    Push-Location $source.DirectoryName
    try {
        & $compiler @options $source.Name
        if ($LASTEXITCODE -ne 0) {
            throw "TI compilation failed for $($source.FullName)."
        }
    }
    finally {
        Pop-Location
    }
    $objects += Join-Path $objectsDirectory ($source.BaseName + '.obj')
}
& $compiler --silicon_version=mspx --code_model=large --data_model=restricted --abi=eabi --run_linker `
    "--output_file=$output\HimaxCamera.out" "--map_file=$output\HimaxCamera.map" `
    --stack_size=320 --heap_size=0 --rom_model "--search_path=$DeviceInclude" `
    "--search_path=$CompilerRoot\lib" @objects "$PSScriptRoot\lnk_msp430fr5969.cmd" --library=libc.a
if ($LASTEXITCODE -ne 0) {
    throw 'TI link failed.'
}
$map = Get-Content -LiteralPath "$output\HimaxCamera.map" -Raw
if ($map -notmatch '(?m)^\.camera_frame\s+\*?\s*\d+\s+00007000\s+00004d34\s+UNINITIALIZED' -or
    $map -notmatch '(?m)^\s+INT41\s+0000ffe2\s+00000002\s+00000002') {
    throw 'Linked frame reservation or RFID timeout vector did not match the required layout.'
}
Write-Output "Built and checked $output\HimaxCamera.out"
