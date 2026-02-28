using System;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;


namespace MCPForUnity.Editor.Tools
{
    [McpForUnityTool("manage_script", AutoRegister = false)]
    public static class ManageScript
    {
        /// <summary>
        /// Resolves a directory under Assets/, preventing traversal and escaping.
        /// Returns fullPathDir on disk and canonical 'Assets/...' relative path.
        /// </summary>
        private static bool TryResolveUnderAssets(string relDir, out string fullPathDir, out string relPathSafe)
        {
            string assets = AssetPathUtility.NormalizeSeparators(Application.dataPath);

            // Normalize caller path: allow both "Scripts/..." and "Assets/Scripts/..."
            string rel = AssetPathUtility.NormalizeSeparators(relDir ?? "Scripts").Trim();
            if (string.IsNullOrEmpty(rel)) rel = "Scripts";

            // Handle both "Assets" and "Assets/" prefixes
            if (rel.Equals("Assets", StringComparison.OrdinalIgnoreCase))
            {
                rel = string.Empty;
            }
            else if (rel.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                rel = rel.Substring(7);
            }

            rel = rel.TrimStart('/');

            string targetDir = AssetPathUtility.NormalizeSeparators(Path.Combine(assets, rel));
            string full = AssetPathUtility.NormalizeSeparators(Path.GetFullPath(targetDir));

            bool underAssets = full.StartsWith(assets + "/", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(full, assets, StringComparison.OrdinalIgnoreCase);
            if (!underAssets)
            {
                fullPathDir = null;
                relPathSafe = null;
                return false;
            }

            // Best-effort symlink guard: if the directory OR ANY ANCESTOR (up to Assets/) is a reparse point/symlink, reject
            try
            {
                var di = new DirectoryInfo(full);
                while (di != null)
                {
                    if (di.Exists && (di.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        fullPathDir = null;
                        relPathSafe = null;
                        return false;
                    }
                    var atAssets = string.Equals(
                        di.FullName.Replace('\\', '/'),
                        assets,
                        StringComparison.OrdinalIgnoreCase
                    );
                    if (atAssets) break;
                    di = di.Parent;
                }
            }
            catch { /* best effort; proceed */ }

            fullPathDir = full;
            string tail = full.Length > assets.Length ? full.Substring(assets.Length).TrimStart('/') : string.Empty;
            relPathSafe = ("Assets/" + tail).TrimEnd('/');
            return true;
        }

        /// <summary>
        /// Main handler for script management actions.
        /// </summary>
        public static object HandleCommand(JObject @params)
        {
            // Handle null parameters
            if (@params == null)
            {
                return new ErrorResponse("invalid_params", "Parameters cannot be null.");
            }

            var p = new ToolParams(@params);

            // Extract and validate required parameters
            var actionResult = p.GetRequired("action");
            if (!actionResult.IsSuccess)
            {
                return new ErrorResponse(actionResult.ErrorMessage);
            }
            string action = actionResult.Value.ToLowerInvariant();

            var nameResult = p.GetRequired("name");
            if (!nameResult.IsSuccess)
            {
                return new ErrorResponse(nameResult.ErrorMessage);
            }
            string name = nameResult.Value;

            // Optional parameters
            string path = p.Get("path"); // Relative to Assets/

            // Basic name validation (alphanumeric, underscores, cannot start with number)
            if (!Regex.IsMatch(name, @"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)))
            {
                return new ErrorResponse(
                    $"Invalid script name: '{name}'. Use only letters, numbers, underscores, and don't start with a number."
                );
            }

            // Resolve and harden target directory under Assets/
            if (!TryResolveUnderAssets(path, out string fullPathDir, out string relPathSafeDir))
            {
                return new ErrorResponse($"Invalid path. Target directory must be within 'Assets/'. Provided: '{(path ?? "(null)")}'");
            }

            // Construct file paths
            string scriptFileName = $"{name}.cs";
            string fullPath = Path.Combine(fullPathDir, scriptFileName);
            string relativePath = AssetPathUtility.NormalizeSeparators(Path.Combine(relPathSafeDir, scriptFileName));

            // Route to specific action handlers
            switch (action)
            {
                case "read":
                    return ReadScript(fullPath, relativePath);
                default:
                    return new ErrorResponse(
                        $"Unknown action: '{action}'. Valid actions are: read."
                    );
            }
        }

        /// <summary>
        /// Encode text to base64 string
        /// </summary>
        private static string EncodeBase64(string text)
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes(text);
            return Convert.ToBase64String(data);
        }

        private static object ReadScript(string fullPath, string relativePath)
        {
            if (!File.Exists(fullPath))
            {
                return new ErrorResponse($"Script not found at '{relativePath}'.");
            }

            try
            {
                string contents = File.ReadAllText(fullPath);

                // Return both normal and encoded contents for larger files
                bool isLarge = contents.Length > 10000; // If content is large, include encoded version
                var uri = $"mcpforunity://path/{relativePath}";
                var responseData = new
                {
                    uri,
                    path = relativePath,
                    contents = contents,
                    // For large files, also include base64-encoded version
                    encodedContents = isLarge ? EncodeBase64(contents) : null,
                    contentsEncoded = isLarge,
                };

                return new SuccessResponse(
                    $"Script '{Path.GetFileName(relativePath)}' read successfully.",
                    responseData
                );
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Failed to read script '{relativePath}': {e.Message}");
            }
        }
    }
}
