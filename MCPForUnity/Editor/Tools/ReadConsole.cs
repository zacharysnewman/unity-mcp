using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Handles reading and clearing Unity Editor console log entries.
    /// Returns deduplicated entries with occurrence counts.
    /// Uses reflection to access internal LogEntry methods/properties.
    /// </summary>
    [McpForUnityTool("read_console", AutoRegister = false)]
    public static class ReadConsole
    {
        private static MethodInfo _startGettingEntriesMethod;
        private static MethodInfo _endGettingEntriesMethod;
        private static MethodInfo _clearMethod;
        private static MethodInfo _getCountMethod;
        private static MethodInfo _getEntryMethod;
        private static FieldInfo _modeField;
        private static FieldInfo _messageField;
        private static FieldInfo _fileField;
        private static FieldInfo _lineField;

        static ReadConsole()
        {
            try
            {
                Type logEntriesType = typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntries");
                if (logEntriesType == null)
                    throw new Exception("Could not find internal type UnityEditor.LogEntries");

                BindingFlags staticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                _startGettingEntriesMethod = logEntriesType.GetMethod("StartGettingEntries", staticFlags);
                if (_startGettingEntriesMethod == null)
                    throw new Exception("Failed to reflect LogEntries.StartGettingEntries");

                _endGettingEntriesMethod = logEntriesType.GetMethod("EndGettingEntries", staticFlags);
                if (_endGettingEntriesMethod == null)
                    throw new Exception("Failed to reflect LogEntries.EndGettingEntries");

                _clearMethod = logEntriesType.GetMethod("Clear", staticFlags);
                if (_clearMethod == null)
                    throw new Exception("Failed to reflect LogEntries.Clear");

                _getCountMethod = logEntriesType.GetMethod("GetCount", staticFlags);
                if (_getCountMethod == null)
                    throw new Exception("Failed to reflect LogEntries.GetCount");

                _getEntryMethod = logEntriesType.GetMethod("GetEntryInternal", staticFlags);
                if (_getEntryMethod == null)
                    throw new Exception("Failed to reflect LogEntries.GetEntryInternal");

                Type logEntryType = typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntry");
                if (logEntryType == null)
                    throw new Exception("Could not find internal type UnityEditor.LogEntry");

                _modeField = logEntryType.GetField("mode", instanceFlags);
                if (_modeField == null)
                    throw new Exception("Failed to reflect LogEntry.mode");

                _messageField = logEntryType.GetField("message", instanceFlags);
                if (_messageField == null)
                    throw new Exception("Failed to reflect LogEntry.message");

                _fileField = logEntryType.GetField("file", instanceFlags);
                if (_fileField == null)
                    throw new Exception("Failed to reflect LogEntry.file");

                _lineField = logEntryType.GetField("line", instanceFlags);
                if (_lineField == null)
                    throw new Exception("Failed to reflect LogEntry.line");
            }
            catch (Exception e)
            {
                McpLog.Error($"[ReadConsole] Static Initialization Failed: {e.Message}");
                _startGettingEntriesMethod = _endGettingEntriesMethod = _clearMethod = _getCountMethod = _getEntryMethod = null;
                _modeField = _messageField = _fileField = _lineField = null;
            }
        }

        public static object HandleCommand(JObject @params)
        {
            if (!AreReflectionMembersInitialized())
            {
                McpLog.Error("[ReadConsole] HandleCommand called but reflection members are not initialized.");
                return new ErrorResponse("ReadConsole handler failed to initialize due to reflection errors. Cannot access console logs.");
            }

            if (@params == null)
                return new ErrorResponse("Parameters cannot be null.");

            var p = new ToolParams(@params);
            string action = p.Get("action", "get").ToLower();

            try
            {
                if (action == "clear")
                    return ClearConsole();

                if (action == "get")
                {
                    bool includeErrors = p.GetBool("includeErrors", true);
                    bool includeWarnings = p.GetBool("includeWarnings", false);
                    bool includeLogs = p.GetBool("includeLogs", false);
                    string filterText = p.Get("filterText");
                    var pagination = PaginationRequest.FromParams(@params, defaultPageSize: 50);
                    pagination.PageSize = Mathf.Clamp(pagination.PageSize, 1, 500);
                    return GetConsoleEntries(includeErrors, includeWarnings, includeLogs, filterText, pagination);
                }

                return new ErrorResponse($"Unknown action: '{action}'. Valid actions are 'get' or 'clear'.");
            }
            catch (Exception e)
            {
                McpLog.Error($"[ReadConsole] Action '{action}' failed: {e}");
                return new ErrorResponse($"Internal error processing action '{action}': {e.Message}");
            }
        }

        /// <summary>
        /// Returns full details for the first occurrence of the log entry with the given hash ID.
        /// Called by ConsoleLogResource to serve the mcpforunity://console/log/{id} resource.
        /// </summary>
        internal static object GetLogDetail(string id)
        {
            if (string.IsNullOrEmpty(id))
                return new ErrorResponse("'id' parameter is required.");

            if (!AreReflectionMembersInitialized())
                return new ErrorResponse("ReadConsole reflection members not initialized. Cannot access console logs.");

            Type logEntryType = typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntry");
            if (logEntryType == null)
                return new ErrorResponse("Could not find internal type UnityEditor.LogEntry.");

            object logEntryInstance = Activator.CreateInstance(logEntryType);
            string foundMessage = null;
            string foundType = null;
            string foundFile = null;
            int foundLine = -1;
            int occurrenceCount = 0;

            try
            {
                _startGettingEntriesMethod.Invoke(null, null);
                int total = (int)_getCountMethod.Invoke(null, null);

                for (int i = 0; i < total; i++)
                {
                    _getEntryMethod.Invoke(null, new object[] { i, logEntryInstance });
                    string message = (string)_messageField.GetValue(logEntryInstance);
                    if (string.IsNullOrEmpty(message)) continue;

                    string firstLine = GetFirstLine(message);
                    if (ComputeLogId(firstLine) != id) continue;

                    occurrenceCount++;
                    if (foundMessage != null) continue;

                    int mode = (int)_modeField.GetValue(logEntryInstance);
                    LogType unityType = InferTypeFromMessage(message);
                    if (!IsExplicitDebugLog(message) && unityType == LogType.Log)
                        unityType = GetLogTypeFromMode(mode);

                    foundMessage = message;
                    foundType = unityType.ToString();
                    foundFile = (string)_fileField.GetValue(logEntryInstance);
                    foundLine = (int)_lineField.GetValue(logEntryInstance);
                }
            }
            catch (Exception e)
            {
                McpLog.Error($"[ReadConsole] Error in GetLogDetail: {e}");
                return new ErrorResponse($"Error retrieving log detail: {e.Message}");
            }
            finally
            {
                try { _endGettingEntriesMethod.Invoke(null, null); }
                catch (Exception e) { McpLog.Error($"[ReadConsole] Failed to call EndGettingEntries: {e}"); }
            }

            if (foundMessage == null)
                return new ErrorResponse($"No log entry found with id '{id}'.");

            return new SuccessResponse("Found log entry.", new
            {
                id = id,
                type = foundType,
                message = foundMessage,
                file = foundFile,
                line = foundLine,
                occurrenceCount = occurrenceCount
            });
        }

        // --- Action Implementations ---

        private static object ClearConsole()
        {
            try
            {
                _clearMethod.Invoke(null, null);
                return new SuccessResponse("Console cleared successfully.");
            }
            catch (Exception e)
            {
                McpLog.Error($"[ReadConsole] Failed to clear console: {e}");
                return new ErrorResponse($"Failed to clear console: {e.Message}");
            }
        }

        private static object GetConsoleEntries(
            bool includeErrors,
            bool includeWarnings,
            bool includeLogs,
            string filterText,
            PaginationRequest pagination)
        {
            var entryById = new Dictionary<string, (string typeName, string message, int count)>();
            var entryOrder = new List<string>();

            Type logEntryType = typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntry");
            if (logEntryType == null)
                return new ErrorResponse("Could not find internal type UnityEditor.LogEntry.");

            object logEntryInstance = Activator.CreateInstance(logEntryType);

            try
            {
                _startGettingEntriesMethod.Invoke(null, null);
                int total = (int)_getCountMethod.Invoke(null, null);

                for (int i = 0; i < total; i++)
                {
                    _getEntryMethod.Invoke(null, new object[] { i, logEntryInstance });
                    string message = (string)_messageField.GetValue(logEntryInstance);
                    if (string.IsNullOrEmpty(message)) continue;

                    int mode = (int)_modeField.GetValue(logEntryInstance);
                    LogType unityType = InferTypeFromMessage(message);
                    if (!IsExplicitDebugLog(message) && unityType == LogType.Log)
                        unityType = GetLogTypeFromMode(mode);

                    bool wantEntry;
                    if (unityType == LogType.Exception || unityType == LogType.Assert || unityType == LogType.Error)
                        wantEntry = includeErrors;
                    else if (unityType == LogType.Warning)
                        wantEntry = includeWarnings;
                    else
                        wantEntry = includeLogs;

                    if (!wantEntry) continue;

                    if (!string.IsNullOrEmpty(filterText) &&
                        message.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    string firstLine = GetFirstLine(message);
                    string id = ComputeLogId(firstLine);

                    if (entryById.ContainsKey(id))
                    {
                        var existing = entryById[id];
                        entryById[id] = (existing.typeName, existing.message, existing.count + 1);
                    }
                    else
                    {
                        entryById[id] = (unityType.ToString(), firstLine, 1);
                        entryOrder.Add(id);
                    }
                }
            }
            catch (Exception e)
            {
                McpLog.Error($"[ReadConsole] Error while retrieving log entries: {e}");
                return new ErrorResponse($"Error retrieving log entries: {e.Message}");
            }
            finally
            {
                try { _endGettingEntriesMethod.Invoke(null, null); }
                catch (Exception e) { McpLog.Error($"[ReadConsole] Failed to call EndGettingEntries: {e}"); }
            }

            var dedupedItems = entryOrder.Select(id => (object)new
            {
                id = id,
                type = entryById[id].typeName,
                message = entryById[id].message,
                occurrenceCount = entryById[id].count
            }).ToList();

            var paginatedResult = PaginationResponse<object>.Create(dedupedItems, pagination);

            return new SuccessResponse(
                $"Retrieved {paginatedResult.Items.Count} log entries ({paginatedResult.TotalCount} unique).",
                new
                {
                    items = paginatedResult.Items,
                    cursor = paginatedResult.Cursor,
                    pageSize = paginatedResult.PageSize,
                    nextCursor = paginatedResult.NextCursor,
                    totalCount = paginatedResult.TotalCount,
                    hasMore = paginatedResult.HasMore
                }
            );
        }

        // --- Internal Helpers ---

        internal static string ComputeLogId(string firstLine)
        {
            using (var sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(firstLine ?? ""));
                return BitConverter.ToString(bytes, 0, 4).Replace("-", "").ToLowerInvariant();
            }
        }

        internal static string GetFirstLine(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;
            int idx = message.IndexOfAny(new[] { '\n', '\r' });
            return idx >= 0 ? message.Substring(0, idx) : message;
        }

        private static bool AreReflectionMembersInitialized()
        {
            return _startGettingEntriesMethod != null
                && _endGettingEntriesMethod != null
                && _clearMethod != null
                && _getCountMethod != null
                && _getEntryMethod != null
                && _modeField != null
                && _messageField != null
                && _fileField != null
                && _lineField != null;
        }

        // Mapping bits from LogEntry.mode. May vary by Unity version.
        private const int ModeBitError = 1 << 0;
        private const int ModeBitAssert = 1 << 1;
        private const int ModeBitWarning = 1 << 2;
        private const int ModeBitException = 1 << 4;
        private const int ModeBitScriptingError = 1 << 9;
        private const int ModeBitScriptingWarning = 1 << 10;
        private const int ModeBitScriptingException = 1 << 18;
        private const int ModeBitScriptingAssertion = 1 << 22;

        private static LogType GetLogTypeFromMode(int mode)
        {
            if ((mode & (ModeBitException | ModeBitScriptingException)) != 0) return LogType.Exception;
            if ((mode & (ModeBitError | ModeBitScriptingError)) != 0) return LogType.Error;
            if ((mode & (ModeBitAssert | ModeBitScriptingAssertion)) != 0) return LogType.Assert;
            if ((mode & (ModeBitWarning | ModeBitScriptingWarning)) != 0) return LogType.Warning;
            return LogType.Log;
        }

        private static LogType InferTypeFromMessage(string fullMessage)
        {
            if (string.IsNullOrEmpty(fullMessage)) return LogType.Log;

            if (fullMessage.IndexOf("LogError", StringComparison.OrdinalIgnoreCase) >= 0)
                return LogType.Error;
            if (fullMessage.IndexOf("LogWarning", StringComparison.OrdinalIgnoreCase) >= 0)
                return LogType.Warning;

            if (fullMessage.IndexOf(" warning CS", StringComparison.OrdinalIgnoreCase) >= 0
                || fullMessage.IndexOf(": warning CS", StringComparison.OrdinalIgnoreCase) >= 0)
                return LogType.Warning;
            if (fullMessage.IndexOf(" error CS", StringComparison.OrdinalIgnoreCase) >= 0
                || fullMessage.IndexOf(": error CS", StringComparison.OrdinalIgnoreCase) >= 0)
                return LogType.Error;

            if (fullMessage.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0)
                return LogType.Exception;
            if (fullMessage.IndexOf("Assertion", StringComparison.OrdinalIgnoreCase) >= 0)
                return LogType.Assert;

            return LogType.Log;
        }

        private static bool IsExplicitDebugLog(string fullMessage)
        {
            if (string.IsNullOrEmpty(fullMessage)) return false;
            if (fullMessage.IndexOf("Debug:Log (", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (fullMessage.IndexOf("UnityEngine.Debug:Log (", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
    }
}
