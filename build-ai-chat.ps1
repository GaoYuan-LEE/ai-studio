param(
    [switch]$Test
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location -LiteralPath $root

$webViewVersion = "1.0.4314.35"
$coreDll = Join-Path $root "Microsoft.Web.WebView2.Core.dll"
$winFormsDll = Join-Path $root "Microsoft.Web.WebView2.WinForms.dll"
$loaderDll = Join-Path $root "WebView2Loader.dll"

function Find-CSharpCompiler {
    $candidates = @(
        "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
        "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    throw "没有找到 .NET Framework 4.x 自带的 C# 编译器（csc.exe）。"
}

function Ensure-WebView2Sdk {
    if ((Test-Path -LiteralPath $coreDll) -and
        (Test-Path -LiteralPath $winFormsDll) -and
        (Test-Path -LiteralPath $loaderDll)) {
        return
    }

    $cache = Join-Path $env:TEMP ("webview2-sdk-" + $webViewVersion)
    $package = Join-Path $env:TEMP ("webview2-" + $webViewVersion + ".nupkg")
    $zip = Join-Path $env:TEMP ("webview2-" + $webViewVersion + ".zip")

    if (-not (Test-Path -LiteralPath $cache)) {
        if (-not (Test-Path -LiteralPath $package)) {
            Write-Host "正在下载 WebView2 SDK ..."
            Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$webViewVersion/microsoft.web.webview2.$webViewVersion.nupkg" -OutFile $package -UseBasicParsing -TimeoutSec 120
        }
        Copy-Item -LiteralPath $package -Destination $zip -Force
        Expand-Archive -LiteralPath $zip -DestinationPath $cache -Force
    }

    Copy-Item -LiteralPath (Join-Path $cache "lib\net462\Microsoft.Web.WebView2.Core.dll") -Destination $coreDll -Force
    Copy-Item -LiteralPath (Join-Path $cache "lib\net462\Microsoft.Web.WebView2.WinForms.dll") -Destination $winFormsDll -Force
    Copy-Item -LiteralPath (Join-Path $cache "runtimes\win-x64\native\WebView2Loader.dll") -Destination $loaderDll -Force
    Copy-Item -LiteralPath (Join-Path $cache "LICENSE.txt") -Destination (Join-Path $root "WebView2-LICENSE.txt") -Force
}

$csc = Find-CSharpCompiler
Ensure-WebView2Sdk

$iconPath = Join-Path $root "ai.ico"
if (-not (Test-Path -LiteralPath $iconPath)) {
    $iconPath = Join-Path $root "web.ico"
}
$manifestPath = Join-Path $root "app.manifest"
$uiFolder = Join-Path $root "ai-ui"

if (-not (Test-Path -LiteralPath (Join-Path $uiFolder "index.html"))) {
    throw "缺少 ai-ui\index.html。"
}
if (-not (Test-Path -LiteralPath (Join-Path $uiFolder "app.js"))) {
    throw "缺少 ai-ui\app.js。"
}
if (-not (Test-Path -LiteralPath (Join-Path $uiFolder "marked.min.js"))) {
    throw "缺少 ai-ui\marked.min.js。"
}
if (-not (Test-Path -LiteralPath (Join-Path $uiFolder "purify.min.js"))) {
    throw "缺少 ai-ui\purify.min.js。"
}

Write-Host "编译器：$csc"
Write-Host "正在编译 AI聊天.exe ..."

& $csc `
    /nologo `
    /target:winexe `
    /platform:x64 `
    /optimize+ `
    /codepage:65001 `
    "/win32icon:$iconPath" `
    "/win32manifest:$manifestPath" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Web.Extensions.dll `
    /reference:System.Net.Http.dll `
    /reference:System.Security.dll `
    "/reference:$coreDll" `
    "/reference:$winFormsDll" `
    "/out:$root\AI聊天.exe" `
    "$root\AI聊天.cs"

if ($LASTEXITCODE -ne 0) {
    throw "AI聊天编译失败。"
}

$exe = Join-Path $root "AI聊天.exe"
Write-Host ""
Write-Host "构建完成："
Get-Item -LiteralPath $exe, $coreDll, $winFormsDll, $loaderDll |
    Select-Object Name, @{Name = "大小"; Expression = { "{0:N0} KB" -f ($_.Length / 1KB) } } |
    Format-Table -AutoSize

Write-Host "界面文件：$uiFolder"
Write-Host "设置文件：$env:LOCALAPPDATA\AIWorkbench\settings.json"

if ($Test) {
    Write-Host "正在检查程序能否启动..."
    $test = Start-Process -FilePath $exe -ArgumentList "--self-test", "http://127.0.0.1:1/v1", (Join-Path $env:TEMP "ai-chat-self-test.txt") -Wait -PassThru
    if ($test.ExitCode -ne 1) {
        throw "自检结果异常：连接失败时应当返回非零退出码。"
    }
    Write-Host "自检通过：程序可运行，网络失败能被正确捕获。"
}
