using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace SampleClient.Editor
{
    public static class ClientBuild
    {
        public const string DefaultOutputDirectory = "Builds/Windows";
        public const string DefaultExecutableName = "ULinkRPC-Sample-Client.exe";
        private const string ScenePath = "Assets/Scenes/SampleScene.unity";

        [MenuItem("Tools/Build/Windows Client")]
        public static void BuildWindowsClient()
        {
            BuildWindowsClient(DefaultOutputDirectory, DefaultExecutableName);
        }

        public static BuildResultData BuildWindowsClient(string outputDirectory, string executableName)
        {
            SharedPackageSync.EnsureFreshForBuild();
            ConfigureWindowsPlayerSettings();

            var fullOutputDirectory = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(fullOutputDirectory);
            var outputPath = Path.Combine(fullOutputDirectory, executableName);

            var buildPlayerOptions = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = outputPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None
            };

            var report = BuildPipeline.BuildPlayer(buildPlayerOptions);
            var result = new BuildResultData
            {
                Succeeded = report.summary.result == BuildResult.Succeeded,
                Message = report.summary.result == BuildResult.Succeeded
                    ? $"Build completed: {outputPath}"
                    : $"Windows build failed: {report.summary.result}",
                OutputPath = outputPath,
                TotalErrors = report.summary.totalErrors,
                TotalWarnings = report.summary.totalWarnings,
                FinishedAtUtc = DateTime.UtcNow.ToString("O")
            };

            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Windows build failed: {report.summary.result}, errors={report.summary.totalErrors}, warnings={report.summary.totalWarnings}");
            }

            Console.WriteLine(result.Message);
            return result;
        }

        private static void ConfigureWindowsPlayerSettings()
        {
            PlayerSettings.defaultScreenWidth = 1600;
            PlayerSettings.defaultScreenHeight = 800;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        }
    }

    [Serializable]
    public sealed class BuildRequestData
    {
        public string RequestId = string.Empty;
        public string OutputDirectory = ClientBuild.DefaultOutputDirectory;
        public string ExecutableName = ClientBuild.DefaultExecutableName;
        public string RequestedAtUtc = string.Empty;
    }

    [Serializable]
    public sealed class BuildResultData
    {
        public string RequestId = string.Empty;
        public bool Succeeded;
        public string Message = string.Empty;
        public string OutputPath = string.Empty;
        public int TotalErrors;
        public int TotalWarnings;
        public string FinishedAtUtc = string.Empty;
    }

    [Serializable]
    public sealed class BuildStatusData
    {
        public string RequestId = string.Empty;
        public string State = string.Empty;
        public string Message = string.Empty;
        public string UpdatedAtUtc = string.Empty;
    }

    [Serializable]
    public sealed class EditorHeartbeatData
    {
        public string ProjectPath = string.Empty;
        public string UpdatedAtUtc = string.Empty;
        public bool ReadyForBuild;
        public string State = string.Empty;
    }
}
