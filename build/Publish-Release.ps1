param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'linux-x64', 'linux-arm64', 'osx-arm64')][string]$Runtime,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$SkipNativeSmoke
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$repo = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputDirectory)
$work = Join-Path ([IO.Path]::GetTempPath()) ('driveapi-release-' + [guid]::NewGuid().ToString('N'))
$source = Join-Path $work 'source'
$publish = Join-Path $work 'publish'
$package = Join-Path $work 'package'
New-Item -ItemType Directory -Path $source, $publish, $package, $output -Force | Out-Null
Write-Host "Isolated build directory: $work"

# 只复制源码；运行数据、数据库、模型、网页及开发配置均不进入编译副本。
Get-ChildItem -LiteralPath $repo -File | Where-Object { $_.Extension -in '.cs', '.csproj' } | Copy-Item -Destination $source
foreach ($folder in 'Controllers', 'Models', 'Services') {
    Copy-Item -LiteralPath (Join-Path $repo $folder) -Destination $source -Recurse
}
Copy-Item -LiteralPath (Join-Path $repo 'global.json') -Destination $work
Copy-Item -LiteralPath (Join-Path $repo 'EmailCodeTemplate.html') -Destination $source
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'appsettings.release.json') -Destination (Join-Path $source 'appsettings.json')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Release.targets') -Destination (Join-Path $work 'Directory.Build.targets')

$publishArgs = @('-c', 'Release', '-r', $Runtime, '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:PublishTrimmed=false', '-p:PublishAot=false', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableUnsupportedPlatformTargetCheck=false', '-p:DebugType=None', '-p:DebugSymbols=false', '-v:minimal')
$log = Join-Path $output "publish-$Runtime.log"
Push-Location $work
try {
    & dotnet publish (Join-Path $source 'driveApi.csproj') @publishArgs -o $publish 2>&1 | Tee-Object -FilePath $log
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }

    # 原生动态库必须已装入单文件；遗漏时失败，不能静默删除依赖后发布。
    $looseLibraries = @(Get-ChildItem -LiteralPath $publish -Recurse -File | Where-Object { $_.Name -match '\.(dll|dylib|so)(\..*)?$' })
    if ($looseLibraries.Count -gt 0) { throw "Unbundled runtime libraries: $($looseLibraries.FullName -join ', ')" }

    if (!$SkipNativeSmoke) {
        $probe = Join-Path $work 'probe'
        $probePublish = Join-Path $work 'probe-publish'
        $probeRun = Join-Path $work 'probe-run'
        New-Item -ItemType Directory -Path $probe, $probeRun | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'NativeSmoke.csproj.template') -Destination (Join-Path $probe 'NativeSmoke.csproj')
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'NativeSmoke.cs.template') -Destination (Join-Path $probe 'NativeSmoke.cs')

        # 使用主项目实际还原的版本，而不是在测试模板里再硬编码一组版本号。
        $assets = Get-Content -LiteralPath (Join-Path $source 'obj/project.assets.json') -Raw | ConvertFrom-Json
        $probeArgs = @($publishArgs)
        $probePackages = [ordered]@{
            'Microsoft.ML.OnnxRuntime' = 'NativeSmokeOnnxVersion'
            'Microsoft.ML.OnnxRuntime.Managed' = 'NativeSmokeOnnxManagedVersion'
        }
        foreach ($packageName in $probePackages.Keys) {
            $matches = @($assets.libraries.PSObject.Properties.Name | Where-Object { $_ -like "$packageName/*" })
            if ($matches.Count -ne 1) { throw "Cannot resolve $packageName from the main project's assets file." }
            $version = $matches[0].Split('/')[1]
            $probeArgs += "-p:$($probePackages[$packageName])=$version"
        }
        & dotnet publish (Join-Path $probe 'NativeSmoke.csproj') @probeArgs -o $probePublish 2>&1 | Tee-Object -FilePath (Join-Path $output "native-$Runtime.log")
        if ($LASTEXITCODE -ne 0) { throw 'Native dependency probe failed to publish.' }
        $probeName = if ($Runtime.StartsWith('win-')) { 'NativeSmoke.exe' } else { 'NativeSmoke' }
        Copy-Item -LiteralPath (Join-Path $probePublish $probeName) -Destination $probeRun
        Push-Location $probeRun
        try {
            & (Join-Path $probeRun $probeName) 2>&1 | Tee-Object -FilePath (Join-Path $output "native-$Runtime.log") -Append
            if ($LASTEXITCODE -ne 0) { throw 'Native dependency probe failed to run.' }
        } finally { Pop-Location }
    }
} finally { Pop-Location }

$executable = if ($Runtime.StartsWith('win-')) { 'driveApi.exe' } else { 'driveApi' }
Copy-Item -LiteralPath (Join-Path $publish $executable) -Destination $package
Copy-Item -LiteralPath (Join-Path $source 'appsettings.json'), (Join-Path $source 'EmailCodeTemplate.html') -Destination $package
New-Item -ItemType Directory -Path (Join-Path $package 'wwwroot'), (Join-Path $package 'Onnx') | Out-Null
$expected = @($executable, 'appsettings.json', 'EmailCodeTemplate.html', 'wwwroot', 'Onnx') | Sort-Object
$actual = @(Get-ChildItem -LiteralPath $package -Force | Select-Object -ExpandProperty Name | Sort-Object)
if (Compare-Object $expected $actual) { throw 'Unexpected release package contents.' }

if ($Runtime.StartsWith('win-')) {
    $archive = Join-Path $output "driveApi-$Runtime.zip"
    if (Test-Path -LiteralPath $archive) { throw "Archive already exists: $archive" }
    # ZipFile 保留空目录；压缩的是目录内容，没有额外的 package 外层目录。
    [IO.Compression.ZipFile]::CreateFromDirectory($package, $archive)
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try { $entries = @($zip.Entries.FullName | ForEach-Object { $_.TrimEnd('/') } | Sort-Object) } finally { $zip.Dispose() }
} else {
    & chmod 755 (Join-Path $package $executable)
    if ($LASTEXITCODE -ne 0) { throw 'chmod failed.' }
    $archive = Join-Path $output "driveApi-$Runtime.tar.gz"
    if (Test-Path -LiteralPath $archive) { throw "Archive already exists: $archive" }
    & tar -czf $archive -C $package $executable appsettings.json EmailCodeTemplate.html wwwroot Onnx
    if ($LASTEXITCODE -ne 0) { throw 'tar failed.' }
    $entries = @(& tar -tzf $archive | ForEach-Object { $_.TrimEnd('/') } | Sort-Object)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect archive.' }
}
if (Compare-Object $expected $entries) { throw 'Archive does not contain exactly the five required entries.' }
Write-Host "Verified release archive: $archive"
