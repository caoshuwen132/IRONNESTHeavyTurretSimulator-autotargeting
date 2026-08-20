param(
    [string]$GameDir = "",
    [switch]$VerifyOnly,
    [switch]$Install,
    [switch]$Uninstall,
    [switch]$UninstallNow,
    [switch]$NoElevation
)

$ErrorActionPreference = "Stop"
$scriptPath = $PSCommandPath
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$payloadRoot = Join-Path $scriptRoot "payload"
if (-not (Test-Path -LiteralPath $payloadRoot -PathType Container)) {
    $groupedPayloadRoot = Join-Path $scriptRoot "离线安装文件\payload"
    if (Test-Path -LiteralPath $groupedPayloadRoot -PathType Container) {
        $payloadRoot = $groupedPayloadRoot
    }
}
$manifestPath = Join-Path $scriptRoot "payload-manifest.json"

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

function Show-Error([string]$message) {
    [System.Windows.Forms.MessageBox]::Show(
        $message,
        $(if ($Uninstall -or $UninstallNow) { "IronNestFCS 一键卸载" } else { "IronNestFCS 一键安装" }),
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Error
    ) | Out-Null
}

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Start-ElevatedInstaller {
    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-STA",
        "-WindowStyle", "Hidden",
        "-File", ('"{0}"' -f $scriptPath)
    )
    if ($GameDir) {
        $arguments += @("-GameDir", ('"{0}"' -f $GameDir))
    }
    if ($Uninstall) { $arguments += "-Uninstall" }
    if ($UninstallNow) { $arguments += "-UninstallNow" }
    Start-Process -FilePath "powershell.exe" -Verb RunAs -ArgumentList ($arguments -join " ") | Out-Null
}

function Get-Manifest {
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "安装包不完整：缺少 payload-manifest.json。请重新解压完整安装包。"
    }
    return Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
}

function Test-Payload {
    $manifest = Get-Manifest
    foreach ($property in $manifest.PSObject.Properties) {
        $relativePath = $property.Name -replace '/', '\'
        $path = Join-Path $payloadRoot $relativePath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "安装包不完整：缺少 $($property.Name)。请重新解压完整安装包。"
        }
        $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($actual -ne $property.Value) {
            throw "文件校验失败：$($property.Name)。文件可能下载不完整或已损坏。"
        }
    }
    return $true
}

function Test-GameDirectory([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return $false }
    if (-not (Test-Path -LiteralPath $path -PathType Container)) { return $false }
    if ((Split-Path -Leaf $path) -like "*_Data") { return $false }
    $exe = Get-ChildItem -LiteralPath $path -File -Filter "*.exe" -ErrorAction SilentlyContinue |
        Where-Object { $_.BaseName -like "Iron Nest Heavy Turret Simulator*" } |
        Select-Object -First 1
    return $null -ne $exe
}

function Add-Candidate([System.Collections.Generic.List[string]]$list, [string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return }
    try { $fullPath = [IO.Path]::GetFullPath($path) } catch { return }
    $alreadyAdded = @($list | Where-Object { $_.Equals($fullPath, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
    if ((Test-GameDirectory $fullPath) -and -not $alreadyAdded) {
        $list.Add($fullPath)
    }
}

function Get-GameCandidates {
    $result = New-Object 'System.Collections.Generic.List[string]'
    if ($GameDir) { Add-Candidate $result $GameDir }

    $steamRoots = New-Object 'System.Collections.Generic.List[string]'
    foreach ($registryPath in @(
        "HKCU:\Software\Valve\Steam",
        "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam",
        "HKLM:\SOFTWARE\Valve\Steam"
    )) {
        try {
            $properties = Get-ItemProperty -LiteralPath $registryPath -ErrorAction Stop
            foreach ($name in @("SteamPath", "InstallPath")) {
                $value = $properties.$name
                if ($value -and -not $steamRoots.Contains($value)) { $steamRoots.Add($value) }
            }
        } catch { }
    }

    foreach ($driveName in @("C", "D", "E", "F", "G")) {
        if (-not (Test-Path -LiteralPath "${driveName}:\" -PathType Container)) { continue }
        foreach ($folderName in @(
            "Iron Nest Heavy Turret Simulator",
            "IRON NEST Heavy Turret Simulator Demo",
            "Iron Nest Heavy Turret Simulator Demo"
        )) {
            Add-Candidate $result "${driveName}:\Games\$folderName"
        }
        foreach ($relative in @("SteamLibrary", "Steam")) {
            $root = "${driveName}:\$relative"
            if ((Test-Path -LiteralPath $root -PathType Container) -and -not $steamRoots.Contains($root)) {
                $steamRoots.Add($root)
            }
        }
    }

    $extraRoots = @($steamRoots)
    foreach ($steamRoot in $extraRoots) {
        $libraryFile = Join-Path $steamRoot "steamapps\libraryfolders.vdf"
        if (-not (Test-Path -LiteralPath $libraryFile -PathType Leaf)) { continue }
        foreach ($line in Get-Content -LiteralPath $libraryFile -ErrorAction SilentlyContinue) {
            if ($line -match '"path"\s+"([^"]+)"') {
                $libraryRoot = $matches[1] -replace '\\\\', '\'
                if (-not $steamRoots.Contains($libraryRoot)) { $steamRoots.Add($libraryRoot) }
            }
        }
    }

    foreach ($steamRoot in $steamRoots) {
        $common = Join-Path $steamRoot "steamapps\common"
        foreach ($folderName in @(
            "Iron Nest Heavy Turret Simulator",
            "IRON NEST Heavy Turret Simulator Demo",
            "Iron Nest Heavy Turret Simulator Demo"
        )) {
            Add-Candidate $result (Join-Path $common $folderName)
        }
    }

    return @($result | Sort-Object @{ Expression = { if ($_ -match "Demo") { 1 } else { 0 } } }, @{ Expression = { $_ } })
}

function Copy-WithBackup([string]$source, [string]$destination, [string]$backupRoot, [System.Collections.Generic.List[string]]$log) {
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        $relative = $destination.Substring($script:SelectedGameDir.Length).TrimStart('\')
        $backupPath = Join-Path $backupRoot $relative
        $backupParent = Split-Path -Parent $backupPath
        New-Item -ItemType Directory -Path $backupParent -Force | Out-Null
        Copy-Item -LiteralPath $destination -Destination $backupPath -Force
        $log.Add("已备份：$relative")
    }
    $parent = Split-Path -Parent $destination
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination -Force
}

function Install-IronNestFCS([string]$target, [System.Windows.Forms.Label]$statusLabel) {
    $script:SelectedGameDir = [IO.Path]::GetFullPath($target).TrimEnd('\')
    if (-not (Test-GameDirectory $script:SelectedGameDir)) {
        throw "所选目录不是游戏根目录。请选择能够看到游戏 exe 的目录。"
    }

    $running = Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -like "Iron Nest Heavy Turret Simulator*" }
    if ($running) { throw "游戏正在运行。请先完全退出游戏，再重新安装。" }

    $statusLabel.Text = "正在校验离线安装文件……"
    [System.Windows.Forms.Application]::DoEvents()
    Test-Payload | Out-Null

    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $backupRoot = Join-Path $script:SelectedGameDir "IronNestFCS_Backups\$stamp"
    $log = New-Object 'System.Collections.Generic.List[string]'
    $log.Add("IronNestFCS 安装时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    $log.Add("游戏目录：$script:SelectedGameDir")

    $statusLabel.Text = "正在备份原文件……"
    [System.Windows.Forms.Application]::DoEvents()
    $backupTargets = @(
        "version.dll",
        "Mods\IronNestFCS.dll",
        "UserLibs\IronNestFCS.Abstractions.dll",
        "UserData\IronNestFCS\IronNestFCS.Logic.dll"
    )
    foreach ($relative in $backupTargets) {
        $existing = Join-Path $script:SelectedGameDir $relative
        if (Test-Path -LiteralPath $existing -PathType Leaf) {
            $backupPath = Join-Path $backupRoot $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $backupPath) -Force | Out-Null
            Copy-Item -LiteralPath $existing -Destination $backupPath -Force
            $log.Add("已备份：$relative")
        }
    }

    $existingMelonLoader = Join-Path $script:SelectedGameDir "MelonLoader"
    if (Test-Path -LiteralPath $existingMelonLoader -PathType Container) {
        New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
        $melonBackup = Join-Path $backupRoot "MelonLoader"
        Move-Item -LiteralPath $existingMelonLoader -Destination $melonBackup
        $log.Add("已备份并移出旧 MelonLoader 文件夹。")
    }

    $statusLabel.Text = "正在安装 MelonLoader……"
    [System.Windows.Forms.Application]::DoEvents()
    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("IronNestFCS-Installer-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    try {
        Expand-Archive -LiteralPath (Join-Path $payloadRoot "MelonLoader.x64.zip") -DestinationPath $tempRoot -Force
        Get-ChildItem -LiteralPath $tempRoot -Force | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $script:SelectedGameDir -Recurse -Force
        }
    } finally {
        $resolvedTemp = [IO.Path]::GetFullPath($tempRoot)
        $systemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if ($resolvedTemp.StartsWith($systemTemp, [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Directory]::Exists($resolvedTemp)) {
            [IO.Directory]::Delete($resolvedTemp, $true)
        }
    }
    $log.Add("已安装或修复 MelonLoader x64。")

    $statusLabel.Text = "正在补齐离线依赖……"
    [System.Windows.Forms.Application]::DoEvents()
    $generatorDir = Join-Path $script:SelectedGameDir "MelonLoader\Dependencies\Il2CppAssemblyGenerator"
    $cppDir = Join-Path $generatorDir "Cpp2IL"
    $cppPluginDir = Join-Path $cppDir "Plugins"
    New-Item -ItemType Directory -Path $cppPluginDir -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $payloadRoot "UnityDependencies_6000.3.9.zip") -Destination (Join-Path $generatorDir "UnityDependencies_6000.3.9.zip") -Force
    Copy-Item -LiteralPath (Join-Path $payloadRoot "Cpp2IL.exe") -Destination (Join-Path $cppDir "Cpp2IL.exe") -Force
    Copy-Item -LiteralPath (Join-Path $payloadRoot "Cpp2IL.Plugin.StrippedCodeRegSupport.dll") -Destination (Join-Path $cppPluginDir "Cpp2IL.Plugin.StrippedCodeRegSupport.dll") -Force
    $log.Add("已安装 UnityDependencies 6000.3.9、Cpp2IL 与配套 StrippedCodeRegSupport 插件。")

    $statusLabel.Text = "正在安装自动索敌 Mod……"
    [System.Windows.Forms.Application]::DoEvents()
    foreach ($relative in @(
        "Mods\IronNestFCS.dll",
        "UserLibs\IronNestFCS.Abstractions.dll",
        "UserData\IronNestFCS\IronNestFCS.Logic.dll"
    )) {
        $source = Join-Path $payloadRoot $relative
        $destination = Join-Path $script:SelectedGameDir $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Force
        $log.Add("已安装：$relative")
    }

    $installedLog = Join-Path $script:SelectedGameDir "UserData\IronNestFCS\Installer.log"
    $log.Add("安装完成。首次启动时请等待 MelonLoader 控制台生成依赖。")
    $log | Set-Content -LiteralPath $installedLog -Encoding UTF8
    return $backupRoot
}

function Uninstall-IronNestFCS([string]$target, [System.Windows.Forms.Label]$statusLabel) {
    $script:SelectedGameDir = [IO.Path]::GetFullPath($target).TrimEnd('\')
    if (-not (Test-GameDirectory $script:SelectedGameDir)) {
        throw "所选目录不是游戏根目录。请选择能够看到游戏 exe 的目录。"
    }

    $running = Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -like "Iron Nest Heavy Turret Simulator*" }
    if ($running) { throw "游戏正在运行。请先完全退出游戏，再重新卸载。" }

    $statusLabel.Text = "正在移除 IronNestFCS……"
    [System.Windows.Forms.Application]::DoEvents()

    $removed = New-Object 'System.Collections.Generic.List[string]'
    foreach ($relative in @(
        "Mods\IronNestFCS.dll",
        "UserLibs\IronNestFCS.Abstractions.dll",
        "UserData\IronNestFCS\IronNestFCS.Logic.dll",
        "UserData\IronNestFCS\IronNestFCS.Logic.AutoTarget.dll",
        "UserData\IronNestFCS\Installer.log"
    )) {
        $path = Join-Path $script:SelectedGameDir $relative
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            Remove-Item -LiteralPath $path -Force
            $removed.Add($relative)
        }
    }

    $dataDirectory = Join-Path $script:SelectedGameDir "UserData\IronNestFCS"
    if ((Test-Path -LiteralPath $dataDirectory -PathType Container) -and
        @(Get-ChildItem -LiteralPath $dataDirectory -Force).Count -eq 0) {
        Remove-Item -LiteralPath $dataDirectory -Force
    }

    return @($removed)
}

if ($VerifyOnly) {
    try {
        Test-Payload | Out-Null
        $candidates = Get-GameCandidates
        Write-Output "PAYLOAD_OK"
        foreach ($candidate in $candidates) { Write-Output "GAME=$candidate" }
        exit 0
    } catch {
        Write-Error $_.Exception.Message
        exit 1
    }
}

if ($Install) {
    try {
        $silentStatus = New-Object System.Windows.Forms.Label
        $backup = Install-IronNestFCS $GameDir $silentStatus
        Write-Output "INSTALL_OK"
        Write-Output "GAME=$GameDir"
        if (Test-Path -LiteralPath $backup -PathType Container) { Write-Output "BACKUP=$backup" }
        exit 0
    } catch {
        Write-Error $_.Exception.Message
        exit 1
    }
}

if ($UninstallNow) {
    try {
        $silentStatus = New-Object System.Windows.Forms.Label
        $removed = @(Uninstall-IronNestFCS $GameDir $silentStatus)
        Write-Output "UNINSTALL_OK"
        Write-Output "GAME=$GameDir"
        foreach ($relative in $removed) { Write-Output "REMOVED=$relative" }
        exit 0
    } catch {
        Write-Error $_.Exception.Message
        exit 1
    }
}

if (-not $NoElevation -and -not (Test-Administrator)) {
    try {
        Start-ElevatedInstaller
        exit 0
    } catch {
        Show-Error $(if ($Uninstall) { "卸载需要管理员权限，但权限请求被取消。" } else { "安装需要管理员权限，但权限请求被取消。" })
        exit 1
    }
}

$isUninstallMode = [bool]$Uninstall
$form = New-Object System.Windows.Forms.Form
$form.Text = if ($isUninstallMode) { "IronNestFCS 一键卸载" } else { "IronNestFCS 自动索敌一键安装" }
$form.StartPosition = "CenterScreen"
$form.Size = New-Object System.Drawing.Size(700, 300)
$form.MinimumSize = New-Object System.Drawing.Size(700, 300)
$form.MaximizeBox = $false
$form.FormBorderStyle = "FixedDialog"
$form.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 10)

$title = New-Object System.Windows.Forms.Label
$title.Text = if ($isUninstallMode) { "IronNestFCS 一键卸载" } else { "IronNestFCS 自动索敌离线安装器" }
$title.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 16, [System.Drawing.FontStyle]::Bold)
$title.AutoSize = $true
$title.Location = New-Object System.Drawing.Point(24, 20)
$form.Controls.Add($title)

$description = New-Object System.Windows.Forms.Label
$description.Text = if ($isUninstallMode) {
    "仅移除 IronNestFCS 文件；保留游戏、MelonLoader、其他 Mod 和安装备份。"
} else {
    "自动安装 MelonLoader、离线依赖和 Mod；兼容 Demo 与正式版。覆盖前会备份旧 DLL。"
}
$description.AutoSize = $true
$description.Location = New-Object System.Drawing.Point(27, 60)
$form.Controls.Add($description)

$pathLabel = New-Object System.Windows.Forms.Label
$pathLabel.Text = "游戏目录："
$pathLabel.AutoSize = $true
$pathLabel.Location = New-Object System.Drawing.Point(27, 101)
$form.Controls.Add($pathLabel)

$pathBox = New-Object System.Windows.Forms.ComboBox
$pathBox.Location = New-Object System.Drawing.Point(110, 96)
$pathBox.Size = New-Object System.Drawing.Size(465, 30)
$pathBox.DropDownStyle = "DropDown"
$candidates = Get-GameCandidates
foreach ($candidate in $candidates) { [void]$pathBox.Items.Add($candidate) }
if ($GameDir) { $pathBox.Text = $GameDir }
elseif ($pathBox.Items.Count -gt 0) { $pathBox.SelectedIndex = 0 }
$form.Controls.Add($pathBox)

$browseButton = New-Object System.Windows.Forms.Button
$browseButton.Text = "浏览…"
$browseButton.Location = New-Object System.Drawing.Point(585, 94)
$browseButton.Size = New-Object System.Drawing.Size(82, 32)
$browseButton.Add_Click({
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = "选择包含游戏 exe 的根目录"
    if (Test-Path -LiteralPath $pathBox.Text -PathType Container) { $dialog.SelectedPath = $pathBox.Text }
    if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { $pathBox.Text = $dialog.SelectedPath }
})
$form.Controls.Add($browseButton)

$status = New-Object System.Windows.Forms.Label
if ($candidates.Count -gt 0) {
    $status.Text = if ($isUninstallMode) { "已检测到游戏，可以开始卸载。" } else { "已检测到游戏，可以开始安装。" }
} else {
    $status.Text = "未自动找到游戏，请点击浏览按钮选择游戏目录。"
}
$status.AutoSize = $true
$status.Location = New-Object System.Drawing.Point(27, 146)
$form.Controls.Add($status)

$installButton = New-Object System.Windows.Forms.Button
$installButton.Text = if ($isUninstallMode) { "开始卸载" } else { "开始安装" }
$installButton.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 11, [System.Drawing.FontStyle]::Bold)
$installButton.Location = New-Object System.Drawing.Point(505, 186)
$installButton.Size = New-Object System.Drawing.Size(162, 45)
$installButton.Add_Click({
    $installButton.Enabled = $false
    $browseButton.Enabled = $false
    $pathBox.Enabled = $false
    try {
        if ($isUninstallMode) {
            $confirmation = [System.Windows.Forms.MessageBox]::Show(
                "将移除 IronNestFCS 的宿主、契约和逻辑 DLL。`r`n`r`n不会删除 MelonLoader、其他 Mod、游戏文件或 IronNestFCS_Backups。是否继续？",
                "确认卸载 IronNestFCS",
                [System.Windows.Forms.MessageBoxButtons]::YesNo,
                [System.Windows.Forms.MessageBoxIcon]::Warning
            )
            if ($confirmation -ne [System.Windows.Forms.DialogResult]::Yes) {
                $status.Text = "已取消卸载。"
                $installButton.Enabled = $true
                $browseButton.Enabled = $true
                $pathBox.Enabled = $true
                return
            }
            $removed = @(Uninstall-IronNestFCS $pathBox.Text $status)
            $status.Text = "卸载完成。"
            $message = if ($removed.Count -gt 0) {
                "卸载成功，共移除 $($removed.Count) 个 IronNestFCS 文件。`r`n`r`nMelonLoader、其他 Mod 和安装备份均已保留。"
            } else {
                "未找到已安装的 IronNestFCS 文件，无需卸载。"
            }
            [System.Windows.Forms.MessageBox]::Show($message, "IronNestFCS 一键卸载", "OK", "Information") | Out-Null
        } else {
            $backup = Install-IronNestFCS $pathBox.Text $status
            $status.Text = "安装完成。现在可以启动游戏。"
            $message = "安装成功！`r`n`r`n首次启动时请等待 MelonLoader 黑色控制台完成依赖生成。"
            if (Test-Path -LiteralPath $backup -PathType Container) {
                $message += "`r`n`r`n原文件备份位置：`r`n$backup"
            }
            [System.Windows.Forms.MessageBox]::Show($message, "IronNestFCS 一键安装", "OK", "Information") | Out-Null
        }
        $form.Close()
    } catch {
        $status.Text = if ($isUninstallMode) { "卸载未完成。" } else { "安装未完成。" }
        Show-Error $_.Exception.Message
        $installButton.Enabled = $true
        $browseButton.Enabled = $true
        $pathBox.Enabled = $true
    }
})
$form.Controls.Add($installButton)

$cancelButton = New-Object System.Windows.Forms.Button
$cancelButton.Text = "取消"
$cancelButton.Location = New-Object System.Drawing.Point(397, 193)
$cancelButton.Size = New-Object System.Drawing.Size(92, 34)
$cancelButton.Add_Click({ $form.Close() })
$form.Controls.Add($cancelButton)

[void]$form.ShowDialog()
