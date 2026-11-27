#Requires -Version 5.1
<#
.SYNOPSIS
    Windows 真机一键验证脚本（BatchCompress.Avalonia）。
.DESCRIPTION
    1) Release 构建主工程与测试工程，运行全部单元测试；
    2) 验证“文件被其他程序独占打开”时，7-Zip 返回码 1 被直接判失败、保留源文件且不残留破损归档；
    3) 验证正常压缩与 --strict-extract 严格解压往返闭环。
#>
$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $RepoRoot

$TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("bc-win-verify-" + [System.Guid]::NewGuid().ToString("N"))
$LockStream = $null

try {
    Write-Host "== 1. Release 构建与全部单元测试 =="
    dotnet build BatchCompress.Avalonia.sln -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "主工程构建失败" }

    dotnet build BatchCompress.Avalonia.Tests\BatchCompress.Avalonia.Tests.csproj -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "测试工程构建失败" }

    dotnet run -c Release --no-build --project BatchCompress.Avalonia.Tests\BatchCompress.Avalonia.Tests.csproj
    if ($LASTEXITCODE -ne 0) { throw "单元测试未全部通过" }

    Write-Host "== 2. 验证文件被独占占用导致返回码 1 时直接判失败 =="
    $SrcDir = Join-Path $TempRoot "src"
    $OutDir = Join-Path $TempRoot "out"
    New-Item -ItemType Directory -Path $SrcDir, $OutDir -Force | Out-Null

    $OkFile = Join-Path $SrcDir "ok.txt"
    $LockedFile = Join-Path $SrcDir "locked.txt"
    Set-Content -Path $OkFile -Value "ok payload" -Encoding UTF8
    Set-Content -Path $LockedFile -Value "locked payload" -Encoding UTF8

    $LockStream = [System.IO.File]::Open(
        $LockedFile,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::None)

    & dotnet run -c Release --no-build --project BatchCompress.Avalonia.csproj -- `
        compress --source $SrcDir --output $OutDir --format 7z --no-random-password --delete-source --test
    $CompressExit = $LASTEXITCODE

    if ($CompressExit -eq 0) {
        throw "预期文件被占用时压缩退出码非 0，实际为 0"
    }
    if (-not (Test-Path $LockedFile)) {
        throw "压缩失败后被占用的源文件不应被删除"
    }
    $PartialArchive = Join-Path $OutDir "locked.txt.7z"
    if (Test-Path $PartialArchive) {
        throw "压缩返回码 1 判失败后不应残留不完整归档: $PartialArchive"
    }
    Write-Host "PASS: 文件被占用时压缩直接判失败、保留源文件且清理不完整归档"

    $LockStream.Dispose()
    $LockStream = $null

    Write-Host "== 3. 验证正常压缩与 --strict-extract 解压闭环 =="
    $CleanOut = Join-Path $TempRoot "clean-out"
    $ExtractOut = Join-Path $TempRoot "extract-out"
    & dotnet run -c Release --no-build --project BatchCompress.Avalonia.csproj -- `
        compress --input $OkFile --output $CleanOut --format 7z --no-random-password --test
    if ($LASTEXITCODE -ne 0) { throw "正常压缩失败" }

    & dotnet run -c Release --no-build --project BatchCompress.Avalonia.csproj -- `
        extract --input (Join-Path $CleanOut "ok.txt.7z") --output $ExtractOut --format 7z --no-random-password --strict-extract
    if ($LASTEXITCODE -ne 0) { throw "严格解压失败" }

    if (-not (Test-Path (Join-Path $ExtractOut "ok.txt"))) {
        throw "严格解压后未找到 ok.txt"
    }

    Write-Host "PASS: Windows 真机全部验证通过！"
}
finally {
    if ($null -ne $LockStream) {
        $LockStream.Dispose()
    }
    if (Test-Path $TempRoot) {
        Remove-Item -Path $TempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    Pop-Location
}
