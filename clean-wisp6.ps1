$root = "C:\UW-PhD\WISP6\wisp6-firmware"

Write-Host "Cleaning CCS build artifacts in $root ..." -ForegroundColor Cyan

# 1. Remove build artifacts
$extensions = @("*.out", "*.obj", "*.d", "*.lib", "*.map", "*.d_raw")

foreach ($ext in $extensions) {
    Get-ChildItem -Path $root -Recurse -Filter $ext -ErrorAction SilentlyContinue |
        ForEach-Object {
            Write-Host "Deleting file: $($_.FullName)"
            Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
        }
}

# 2. Remove .settings folders (CCS/Eclipse metadata)
Get-ChildItem -Path $root -Recurse -Directory -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq ".settings" } |
    ForEach-Object {
        Write-Host "Deleting folder: $($_.FullName)"
        Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
    }

# 3. Optional: remove CCS debug metadata (safe if regenerated)
Get-ChildItem -Path $root -Recurse -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like ".metadata" -or $_.Name -eq ".theia" } |
    ForEach-Object {
        Write-Host "Deleting workspace metadata: $($_.FullName)"
        Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
    }

Write-Host "`nCleanup complete. Now reopen CCS and rebuild wisp-base first." -ForegroundColor Green