using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace SampleClient.Editor
{
    [InitializeOnLoad]
    public static class ClientBuildRequestWatcher
    {
        private const double PollIntervalSeconds = 1d;
        private static readonly string RequestPath = GetProjectPath("Temp/BuildRequests/windows-client-request.json");
        private static readonly string PendingRequestPath = GetProjectPath("Temp/BuildRequests/windows-client-pending.json");
        private static readonly string ResultPath = GetProjectPath("Temp/BuildRequests/windows-client-result.json");
        private static readonly string StatusPath = GetProjectPath("Temp/BuildRequests/windows-client-status.json");
        private static readonly string HeartbeatPath = GetProjectPath("Temp/BuildRequests/editor-heartbeat.json");

        private static bool _isProcessing;
        private static double _nextPollAt;
        private static double _nextHeartbeatAt;

        static ClientBuildRequestWatcher()
        {
            EditorApplication.update += PollForBuildRequests;
            WriteHeartbeat("watching");
            Debug.Log("[ClientBuild] Request watcher is active.");
        }

        private static void PollForBuildRequests()
        {
            if (EditorApplication.timeSinceStartup >= _nextHeartbeatAt)
            {
                WriteHeartbeat(GetEditorState());
                _nextHeartbeatAt = EditorApplication.timeSinceStartup + PollIntervalSeconds;
            }

            if (_isProcessing || EditorApplication.timeSinceStartup < _nextPollAt)
            {
                return;
            }

            _nextPollAt = EditorApplication.timeSinceStartup + PollIntervalSeconds;

            if (EditorApplication.isCompiling || EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer)
            {
                if (File.Exists(RequestPath))
                {
                    WriteStatus(string.Empty, "waiting",
                        $"Editor is busy: {GetEditorState()}");
                }
                return;
            }

            var request = TryLoadPendingRequest();
            if (request == null && !File.Exists(RequestPath))
            {
                return;
            }

            if (request == null)
            {
                try
                {
                    request = JsonUtility.FromJson<BuildRequestData>(File.ReadAllText(RequestPath));
                    File.WriteAllText(PendingRequestPath, JsonUtility.ToJson(request, true));
                    File.Delete(RequestPath);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ClientBuild] Invalid build request: {ex.Message}");
                    WriteFailureResult(string.Empty, $"Build request is invalid: {ex.Message}");
                    SafeDelete(RequestPath);
                    SafeDelete(PendingRequestPath);
                    return;
                }
            }

            Debug.Log($"[ClientBuild] Accepted build request {request.RequestId} -> {request.OutputDirectory}/{request.ExecutableName}");
            WriteStatus(request.RequestId, "accepted", "Build request accepted by Unity Editor.");
            _isProcessing = true;
            EditorApplication.delayCall += () => ExecuteRequest(request);
        }

        private static void ExecuteRequest(BuildRequestData request)
        {
            try
            {
                Debug.Log($"[ClientBuild] Starting Windows build for request {request.RequestId}.");
                WriteStatus(request.RequestId, "building", "Unity Editor is building the Windows client.");
                var result = ClientBuild.BuildWindowsClient(request.OutputDirectory, request.ExecutableName);
                result.RequestId = request.RequestId;
                WriteResult(result);
                WriteStatus(request.RequestId, "completed", result.Message);
                Debug.Log($"[ClientBuild] {result.Message}");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                WriteFailureResult(request.RequestId, ex.Message);
            }
            finally
            {
                SafeDelete(PendingRequestPath);
                _isProcessing = false;
            }
        }

        private static BuildRequestData TryLoadPendingRequest()
        {
            if (!File.Exists(PendingRequestPath))
            {
                return null;
            }

            try
            {
                var request = JsonUtility.FromJson<BuildRequestData>(File.ReadAllText(PendingRequestPath));
                if (request == null || string.IsNullOrWhiteSpace(request.RequestId))
                {
                    throw new InvalidOperationException("Pending build request is empty.");
                }

                return request;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ClientBuild] Invalid pending build request: {ex.Message}");
                WriteFailureResult(string.Empty, $"Pending build request is invalid: {ex.Message}");
                SafeDelete(PendingRequestPath);
                return null;
            }
        }

        private static void WriteFailureResult(string requestId, string message)
        {
            WriteResult(new BuildResultData
            {
                RequestId = requestId,
                Succeeded = false,
                Message = message,
                FinishedAtUtc = DateTime.UtcNow.ToString("O")
            });
            WriteStatus(requestId, "failed", message);
        }

        private static void WriteResult(BuildResultData result)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ResultPath)!);
            File.WriteAllText(ResultPath, JsonUtility.ToJson(result, true));
        }

        private static void WriteStatus(string requestId, string state, string message)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatusPath)!);
            File.WriteAllText(StatusPath, JsonUtility.ToJson(new BuildStatusData
            {
                RequestId = requestId,
                State = state,
                Message = message,
                UpdatedAtUtc = DateTime.UtcNow.ToString("O")
            }, true));
        }

        private static void WriteHeartbeat(string state)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HeartbeatPath)!);
            File.WriteAllText(HeartbeatPath, JsonUtility.ToJson(new EditorHeartbeatData
            {
                ProjectPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                UpdatedAtUtc = DateTime.UtcNow.ToString("O"),
                ReadyForBuild = !(EditorApplication.isCompiling || EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer),
                State = state
            }, true));
        }

        private static string GetEditorState()
        {
            if (BuildPipeline.isBuildingPlayer)
            {
                return "building-player";
            }

            if (EditorApplication.isCompiling)
            {
                return "compiling";
            }

            if (EditorApplication.isUpdating)
            {
                return "updating";
            }

            return "idle";
        }

        private static string GetProjectPath(string relativePath)
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        private static void SafeDelete(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
