#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

// Ensure we can use ZipFile without extra fuss.
using System.IO.Compression;

public static class ExportProjectToZip
{
    [MenuItem("Tools/Export Project to Zip…", priority = 1000)]
    public static void Export()
    {
        try
        {
            // Ask user for output path
            var defaultName = $"{Application.productName}_ProjectExport_{DateTime.Now:yyyyMMdd_HHmm}.zip";
            var zipPath = EditorUtility.SaveFilePanel("Export Project to Zip", "", defaultName, "zip");
            if (string.IsNullOrEmpty(zipPath))
                return;

            // Prepare a clean temp directory
            var tempRoot = Path.Combine(Path.GetTempPath(), "UnityProjectExport_" + Guid.NewGuid().ToString("N"));
            var bundleRoot = Path.Combine(tempRoot, "bundle"); // where we stage the content
            Directory.CreateDirectory(bundleRoot);

            // Always include Assets and ProjectSettings
            var projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            var assetsSrc = Path.Combine(projectRoot, "Assets");
            var projSettingsSrc = Path.Combine(projectRoot, "ProjectSettings");

            if (!Directory.Exists(assetsSrc))
                throw new DirectoryNotFoundException("Assets folder not found at: " + assetsSrc);
            if (!Directory.Exists(projSettingsSrc))
                throw new DirectoryNotFoundException("ProjectSettings folder not found at: " + projSettingsSrc);

            EditorUtility.DisplayProgressBar("Exporting Project", "Copying Assets…", 0.2f);
            CopyDirectory(assetsSrc, Path.Combine(bundleRoot, "Assets"));

            EditorUtility.DisplayProgressBar("Exporting Project", "Copying ProjectSettings…", 0.4f);
            CopyDirectory(projSettingsSrc, Path.Combine(bundleRoot, "ProjectSettings"));

            // Include Packages/manifest.json if present (helps restore packages)
            var packagesSrc = Path.Combine(projectRoot, "Packages");
            var manifestSrc = Path.Combine(packagesSrc, "manifest.json");
            if (File.Exists(manifestSrc))
            {
                EditorUtility.DisplayProgressBar("Exporting Project", "Including Packages/manifest.json…", 0.55f);
                var packagesDst = Path.Combine(bundleRoot, "Packages");
                Directory.CreateDirectory(packagesDst);
                File.Copy(manifestSrc, Path.Combine(packagesDst, "manifest.json"), overwrite: true);

                // Optional: include packages-lock.json if present
                var lockSrc = Path.Combine(packagesSrc, "packages-lock.json");
                if (File.Exists(lockSrc))
                    File.Copy(lockSrc, Path.Combine(packagesDst, "packages-lock.json"), overwrite: true);
            }

            // Create the ZIP
            EditorUtility.DisplayProgressBar("Exporting Project", "Creating ZIP…", 0.75f);
            if (File.Exists(zipPath)) File.Delete(zipPath); // overwrite cleanly

            // Use fully-qualified CompressionLevel to avoid UnityEngine.CompressionLevel ambiguity.
            ZipFile.CreateFromDirectory(
                bundleRoot,
                zipPath,
                System.IO.Compression.CompressionLevel.Optimal,
                includeBaseDirectory: false
            );

            EditorUtility.ClearProgressBar();
            EditorUtility.RevealInFinder(zipPath);
            EditorUtility.DisplayDialog("Export Complete",
                "Project exported successfully:\n" + zipPath, "OK");
        }
        catch (Exception ex)
        {
            EditorUtility.ClearProgressBar();
            Debug.LogError("[ExportProjectToZip] " + ex);
            EditorUtility.DisplayDialog("Export Failed", ex.Message, "OK");
        }
    }

    /// <summary>
    /// Recursively copies a directory, preserving .meta files and timestamps.
    /// Excludes common non-source folders if encountered (not expected under Assets/ or ProjectSettings/).
    /// </summary>
    private static void CopyDirectory(string sourceDir, string destDir)
    {
        // Ensure destination exists
        Directory.CreateDirectory(destDir);

        // Copy files
        foreach (var filePath in Directory.GetFiles(sourceDir))
        {
            var fileName = Path.GetFileName(filePath);
            var destFile = Path.Combine(destDir, fileName);
            File.Copy(filePath, destFile, overwrite: true);
            // Preserve timestamps (optional)
            var infoSrc = new FileInfo(filePath);
            File.SetCreationTimeUtc(destFile, infoSrc.CreationTimeUtc);
            File.SetLastWriteTimeUtc(destFile, infoSrc.LastWriteTimeUtc);
        }

        // Copy subdirectories
        foreach (var dirPath in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(dirPath);
            // Skip typical transient folders if ever encountered
            if (IsBlacklistedDir(dirName)) continue;

            var newDest = Path.Combine(destDir, dirName);
            CopyDirectory(dirPath, newDest);
        }
    }

    private static bool IsBlacklistedDir(string name)
    {
        // These usually live at project root, but we keep the guard anyway.
        switch (name)
        {
            case "Library":
            case "Logs":
            case "Obj":
            case "Temp":
            case "UserSettings":
                return true;
            default:
                return false;
        }
    }
}
#endif
