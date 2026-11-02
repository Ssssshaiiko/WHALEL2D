# 大肥鱼 Live2D 桌宠插件：检查、编译、自检。在插件文件夹里运行：
#   powershell -ExecutionPolicy Bypass -File setup.ps1          检查并编译
#   powershell -ExecutionPolicy Bypass -File setup.ps1 -Check   编译完再开一个看不见的桌宠试一遍（推荐）
# 缺什么就停下，告诉你缺什么、去哪里拿；不会自己下载 Live2D 的 SDK 或模型（它们要你自己同意各自的许可）。
param([switch]$Check)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$root = $PSScriptRoot
function Ok($text) { Write-Host ('✓ ' + $text) -ForegroundColor Green }
function Missing($text) { Write-Host ('✗ ' + $text) -ForegroundColor Red; exit 1 }

if (![Environment]::Is64BitOperatingSystem) { Missing '只支持 64 位 Windows。' }

# Cubism SDK for Native：用户自己从 Live2D 官网下载，解压进 sdk\
# 找文件用 .NET 的枚举：解压出来的文件夹和链接过来的文件夹都找得到。
function Find($folder, $name) { if (Test-Path -LiteralPath $folder) { [IO.Directory]::EnumerateFiles($folder, $name, [IO.SearchOption]::AllDirectories) } }
$header = Find (Join-Path $root 'sdk') 'Live2DCubismCore.h' | Where-Object { (Split-Path (Split-Path $_ -Parent) -Leaf) -eq 'include' -and (Split-Path (Split-Path (Split-Path $_ -Parent) -Parent) -Leaf) -eq 'Core' } | Select-Object -First 1
if (!$header) { Missing '没找到 Cubism SDK for Native。到 https://www.live2d.com/sdk/download/native/ 下载（要同意它的许可），把压缩包解压到插件的 sdk 文件夹里。' }
$sdk = Split-Path (Split-Path (Split-Path $header -Parent) -Parent) -Parent
Ok ('Cubism SDK：' + $sdk)

# 模型：用户自己从作者那里获取，放进 model\
$model = Find (Join-Path $root 'model') '大肥鱼.model3.json' | Select-Object -First 1
if (!$model) { Missing '没找到模型。按 model\获取模型.md 去作者的视频里获取，解压到插件的 model 文件夹里（里面要有 大肥鱼.model3.json）。' }
Ok ('模型：' + (Split-Path $model -Parent))

# Visual Studio 生成工具（C++）里的编译器和 CMake
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$studio = if (Test-Path -LiteralPath $vswhere) { & $vswhere -products * -latest -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath }
if (!$studio) { Missing '没装 Visual Studio 生成工具的 C++ 部分。可以运行：winget install Microsoft.VisualStudio.2022.BuildTools --override "--quiet --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"' }
$cmake = Join-Path $studio 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
if (!(Test-Path -LiteralPath $cmake)) { $cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source }
if (!$cmake) { Missing '没找到 CMake。在 Visual Studio Installer 里给生成工具勾上“用于 Windows 的 C++ CMake 工具”，或者 winget install Kitware.CMake。' }
$line = & $vswhere -products * -latest -property catalog_productLineVersion
$generator = switch ($line) { '2026' { 'Visual Studio 18 2026' } '2019' { 'Visual Studio 16 2019' } default { 'Visual Studio 17 2022' } }
Ok ('编译工具：' + $generator)

# 渲染器（链接 Cubism Core，只在你自己电脑上编译，不要把 renderer\build 分享给别人）
$renderer = Join-Path $root 'renderer'
& $cmake -S $renderer -B (Join-Path $renderer 'build') -G $generator -A x64 "-DCUBISM_SDK=$($sdk -replace '\\', '/')" | Out-Null
if ($LASTEXITCODE -ne 0) { Missing '渲染器配置失败，看上面的输出。' }
& $cmake --build (Join-Path $renderer 'build') --config Release --parallel --target l2d-render | Out-Null
if ($LASTEXITCODE -ne 0) { Missing '渲染器编译失败，看上面的输出。' }
Ok '渲染器编译好了'

# .NET 10 SDK
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (!$dotnet -or !(& dotnet --list-sdks | Where-Object { $_ -match '^(1[0-9])\.' })) { Missing '没装 .NET 10 SDK。可以运行：winget install Microsoft.DotNet.SDK.10' }
foreach ($project in 'FishPet', 'FishPet.Mcp') {
    & dotnet publish (Join-Path $root "src\$project\$project.csproj") -c Release -o (Join-Path $root 'bin') --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { Missing "$project 编译失败。再运行一次 dotnet publish src\$project\$project.csproj -c Release -o bin 看输出。" }
}
Ok '桌宠和 MCP 接口编译好了'
& dotnet run --project (Join-Path $root 'src\FishPet.Tests\FishPet.Tests.csproj') -c Release | Out-Host
if ($LASTEXITCODE -ne 0) { Missing '动作逻辑测试没通过。' }

$mcp = Join-Path $root 'bin\FishPet.Mcp.exe'
if ($Check) {
    & $mcp --check | Out-Host
    if ($LASTEXITCODE -ne 0) { Missing '自检没通过，看上面的输出；还有问题看插件里的 errors.log。' }
}

Write-Host ''
Write-Host '装好了。把下面这个 MCP 服务接进你的 Agent（stdio，没有参数）：' -ForegroundColor Cyan
Write-Host ('  ' + $mcp)
Write-Host '  Claude Code： claude mcp add dafeiyu -- "' -NoNewline; Write-Host ($mcp + '"')
Write-Host '  其他 Agent：在它的 MCP 配置里加 { "command": "' -NoNewline; Write-Host (($mcp -replace '\\', '\\') + '" }')
Write-Host '用模型 API 自己搭的程序（没有 MCP）：开着 bin\FishPet.exe，按 api.json 里的地址和口令发 HTTP 请求。'
Write-Host '然后把 prompt\身体说明.md 填好称呼和名字，放进 Agent 的设定里。详见 AGENTS.md。'
