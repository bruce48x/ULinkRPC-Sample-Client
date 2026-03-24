using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace SampleClient.Editor
{
    internal static class SharedPackageSync
    {
        private const string PackageName = "com.ulinkrpc-sample-server.shared";
        private const string PackageAssetRoot = "Packages/com.ulinkrpc-sample-server.shared";
        private const string SharedAssemblyRelativePath = "Library/ScriptAssemblies/Shared.dll";

        [InitializeOnLoadMethod]
        private static void WarnWhenSharedPackageIsStale()
        {
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isCompiling || BuildPipeline.isBuildingPlayer)
                {
                    return;
                }

                if (!TryGetStaleReason(out var reason))
                {
                    return;
                }

                Debug.LogWarning($"[SharedPackageSync] {reason} Use Tools/Build/Reimport Shared Package, wait for Unity to finish recompiling, then run the client again.");
            };
        }

        [MenuItem("Tools/Build/Reimport Shared Package")]
        public static void ReimportSharedPackage()
        {
            AssetDatabase.ImportAsset($"{PackageAssetRoot}/package.json", ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset($"{PackageAssetRoot}/Shared.asmdef", ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            CompilationPipeline.RequestScriptCompilation();
            Debug.Log("[SharedPackageSync] Requested shared package reimport and script recompilation.");
        }

        public static void EnsureFreshForBuild()
        {
            if (!TryGetStaleReason(out var reason))
            {
                return;
            }

            ReimportSharedPackage();
            throw new InvalidOperationException(
                $"{reason} Unity has been asked to reimport the local shared package. Wait for compilation to finish, then build again.");
        }

        private static bool TryGetStaleReason(out string reason)
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                reason = "Unable to determine the Unity project root.";
                return false;
            }

            var packageSourceRoot = Path.Combine(projectRoot, "ulinkrpc-sample-server", "Shared");
            if (!Directory.Exists(packageSourceRoot))
            {
                reason = $"Shared package source folder was not found: {packageSourceRoot}";
                return false;
            }

            var sharedAssemblyPath = Path.Combine(projectRoot, SharedAssemblyRelativePath);
            if (!File.Exists(sharedAssemblyPath))
            {
                reason = $"Compiled Shared assembly was not found: {sharedAssemblyPath}";
                return true;
            }

            var latestSourceWriteTimeUtc = Directory
                .EnumerateFiles(packageSourceRoot, "*", SearchOption.AllDirectories)
                .Where(static path =>
                    path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith("package.json", StringComparison.OrdinalIgnoreCase))
                .Select(File.GetLastWriteTimeUtc)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();

            var assemblyWriteTimeUtc = File.GetLastWriteTimeUtc(sharedAssemblyPath);
            if (latestSourceWriteTimeUtc <= assemblyWriteTimeUtc)
            {
                reason = string.Empty;
                return false;
            }

            reason =
                $"{PackageName} was modified at {latestSourceWriteTimeUtc:O}, but Shared.dll was last compiled at {assemblyWriteTimeUtc:O}.";
            return true;
        }
    }
}
