using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

public sealed class MurekaBackendBuildPostprocessor : IPostprocessBuildWithReport
{
    private const string BackendFolderName = "MurekaBackend";

    private static readonly string[] ExcludedDirectories =
    {
        "browser_profile",
        "downloads",
        "jobs",
        "output",
        ".venv_mureka",
        "__pycache__"
    };

    private static readonly string[] ExcludedExtensions =
    {
        ".log",
        ".pyc"
    };

    public int callbackOrder => 0;

    public void OnPostprocessBuild(BuildReport report)
    {
        string projectRoot = Directory.GetParent(UnityEngine.Application.dataPath).FullName;
        string sourceRoot = Path.Combine(projectRoot, BackendFolderName);
        if (!Directory.Exists(sourceRoot))
        {
            UnityEngine.Debug.LogWarning("MUREKA backend folder was not found: " + sourceRoot);
            return;
        }

        string buildRoot = GetBuildRoot(report.summary.outputPath);
        string destinationRoot = Path.Combine(buildRoot, BackendFolderName);

        if (Directory.Exists(destinationRoot))
        {
            TryDeleteDirectory(destinationRoot);
        }

        CopyDirectory(sourceRoot, destinationRoot);
        UnityEngine.Debug.Log("MUREKA backend copied to build output: " + destinationRoot);
    }

    private static string GetBuildRoot(string outputPath)
    {
        if (Directory.Exists(outputPath))
        {
            return outputPath;
        }

        string directory = Path.GetDirectoryName(outputPath);
        return string.IsNullOrEmpty(directory) ? Directory.GetCurrentDirectory() : directory;
    }

    private static void TryDeleteDirectory(string directoryPath)
    {
        try
        {
            Directory.Delete(directoryPath, true);
        }
        catch (IOException ex)
        {
            UnityEngine.Debug.LogWarning("MUREKA backend cleanup skipped: " + ex.Message);
        }
        catch (System.UnauthorizedAccessException ex)
        {
            UnityEngine.Debug.LogWarning("MUREKA backend cleanup skipped: " + ex.Message);
        }
    }

    private static void CopyDirectory(string sourceRoot, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);

        foreach (string filePath in Directory.GetFiles(sourceRoot))
        {
            if (ShouldSkipFile(filePath))
            {
                continue;
            }

            File.Copy(filePath, Path.Combine(destinationRoot, Path.GetFileName(filePath)), true);
        }

        foreach (string directoryPath in Directory.GetDirectories(sourceRoot))
        {
            if (ShouldSkipDirectory(directoryPath))
            {
                continue;
            }

            CopyDirectory(directoryPath, Path.Combine(destinationRoot, Path.GetFileName(directoryPath)));
        }
    }

    private static bool ShouldSkipDirectory(string directoryPath)
    {
        string directoryName = Path.GetFileName(directoryPath);
        for (int i = 0; i < ExcludedDirectories.Length; i++)
        {
            if (string.Equals(directoryName, ExcludedDirectories[i], System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ShouldSkipFile(string filePath)
    {
        string extension = Path.GetExtension(filePath);
        for (int i = 0; i < ExcludedExtensions.Length; i++)
        {
            if (string.Equals(extension, ExcludedExtensions[i], System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
