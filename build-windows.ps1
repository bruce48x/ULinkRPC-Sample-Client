param(
    [string]$OutputDirectory = "Builds/Windows",
    [string]$ExecutableName = "ULinkRPC-Sample-Client.exe",
    [int]$TimeoutSeconds = 1800
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$unityPath = "C:\Program Files\Unity\Hub\Editor\2022.3.62f3c1\Editor\Unity.exe"
$requestDirectory = Join-Path $projectRoot "Temp\BuildRequests"
$requestPath = Join-Path $requestDirectory "windows-client-request.json"
$resultPath = Join-Path $requestDirectory "windows-client-result.json"
$statusPath = Join-Path $requestDirectory "windows-client-status.json"
$heartbeatPath = Join-Path $requestDirectory "editor-heartbeat.json"
$unityLockPath = Join-Path $projectRoot "Temp\\UnityLockfile"
$batchLogPath = Join-Path $projectRoot "Builds\windows-build.log"
$editorHeartbeatTimeoutSeconds = 10
$editorHeartbeatWaitSeconds = 15

function Read-JsonFile {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        return $null
    }

    return Get-Content $Path -Raw | ConvertFrom-Json
}

function Get-FreshEditorHeartbeat {
    $heartbeat = Read-JsonFile -Path $heartbeatPath
    if ($null -eq $heartbeat) {
        return $null
    }

    $updatedAtUtc = [DateTime]$heartbeat.UpdatedAtUtc
    if (([DateTime]::UtcNow - $updatedAtUtc).TotalSeconds -gt $editorHeartbeatTimeoutSeconds) {
        return $null
    }

    $expectedProjectPath = [IO.Path]::GetFullPath($projectRoot)
    if ([IO.Path]::GetFullPath([string]$heartbeat.ProjectPath) -ne $expectedProjectPath) {
        return $null
    }

    return $heartbeat
}

function Wait-ForEditorHeartbeat {
    $deadline = (Get-Date).AddSeconds($editorHeartbeatWaitSeconds)
    while ((Get-Date) -lt $deadline) {
        $heartbeat = Get-FreshEditorHeartbeat
        if ($null -ne $heartbeat) {
            return $heartbeat
        }

        Start-Sleep -Milliseconds 500
    }

    return $null
}

function Wait-ForResult {
    param([string]$RequestId)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastStatusKey = ""
    $editorAcked = $false

    while ((Get-Date) -lt $deadline) {
        $result = Read-JsonFile -Path $resultPath
        if ($null -ne $result -and $result.RequestId -eq $RequestId) {
            return $result
        }

        $status = Read-JsonFile -Path $statusPath
        if ($null -ne $status -and (($status.RequestId -eq $RequestId) -or [string]::IsNullOrWhiteSpace([string]$status.RequestId))) {
            $statusKey = "$($status.State)|$($status.Message)"
            if ($statusKey -ne $lastStatusKey) {
                Write-Host "[Editor] $($status.State): $($status.Message)"
                $lastStatusKey = $statusKey
            }

            if ($status.RequestId -eq $RequestId -and $status.State -ne "waiting") {
                $editorAcked = $true
            }
        }

        $heartbeat = Get-FreshEditorHeartbeat
        if ($null -eq $heartbeat -and -not $editorAcked) {
            throw "Open Unity Editor did not acknowledge the build request. Make sure the project is open, scripts compiled, and the Console is free of compile errors."
        }

        Start-Sleep -Milliseconds 500
    }

    throw "Timed out waiting for Unity Editor build result."
}

function Invoke-EditorBuild {
    $heartbeat = Get-FreshEditorHeartbeat
    if ($null -eq $heartbeat) {
        throw "Unity Editor heartbeat was not found. Open this project in Unity and wait for script compilation to finish, or close Unity and let the script use batchmode."
    }

    Write-Host "Using open Unity Editor build bridge..."
    Write-Host "Editor state: $($heartbeat.State)"

    $requestId = [guid]::NewGuid().ToString("N")
    New-Item -ItemType Directory -Force -Path $requestDirectory | Out-Null
    Remove-Item -Force $requestPath,$resultPath,$statusPath -ErrorAction SilentlyContinue

    $request = [pscustomobject]@{
        RequestId = $requestId
        OutputDirectory = $OutputDirectory
        ExecutableName = $ExecutableName
        RequestedAtUtc = [DateTime]::UtcNow.ToString("O")
    }

    $request | ConvertTo-Json | Set-Content -Path $requestPath -Encoding UTF8
    Write-Host "Build request written: $requestPath"

    $result = Wait-ForResult -RequestId $requestId
    if (-not $result.Succeeded) {
        throw "Unity Editor build failed: $($result.Message)"
    }

    return $result
}

function Invoke-BatchBuild {
    Write-Host "Unity Editor heartbeat not detected. Falling back to batchmode build..."
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $batchLogPath) | Out-Null
    $outputPath = Join-Path (Join-Path $projectRoot $OutputDirectory) $ExecutableName
    $startedAtUtc = [DateTime]::UtcNow
    & $unityPath -batchmode -nographics -quit -projectPath $projectRoot -executeMethod SampleClient.Editor.ClientBuild.BuildWindowsClient -logFile $batchLogPath
    if ($LASTEXITCODE -ne 0) {
        throw "Batch Unity exited with code $LASTEXITCODE. See log: $batchLogPath"
    }

    if (-not (Test-Path $outputPath)) {
        throw "Batch build completed without output: $outputPath"
    }

    $outputWriteTimeUtc = (Get-Item $outputPath).LastWriteTimeUtc
    if ($outputWriteTimeUtc -lt $startedAtUtc.AddSeconds(-1)) {
        throw "Batch Unity did not produce a fresh build output. Existing file appears stale: $outputPath"
    }

    return [pscustomobject]@{
        OutputPath = $outputPath
        Message = "Build completed through batch Unity."
    }
}

$heartbeat = Get-FreshEditorHeartbeat
if ($null -ne $heartbeat) {
    $result = Invoke-EditorBuild
}
else {
    Write-Host "Unity Editor heartbeat not detected. Waiting briefly for an open Editor to publish readiness..."
    $heartbeat = Wait-ForEditorHeartbeat
    if ($null -ne $heartbeat) {
        $result = Invoke-EditorBuild
    }
    elseif (Test-Path $unityLockPath) {
        throw "This project appears to be open in Unity, but the build bridge heartbeat is unavailable. Focus the Editor, wait for script compilation to finish, confirm Unity Console has no compile errors, then rerun build-windows.ps1."
    }
    else {
        $result = Invoke-BatchBuild
    }
}

Write-Host $result.Message
Write-Host "Output: $($result.OutputPath)"
