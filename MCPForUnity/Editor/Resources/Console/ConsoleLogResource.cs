using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Resources.Console
{
    /// <summary>
    /// Resource handler for reading full details of a console log entry.
    /// Returns the complete message (including stack trace), source file, line, and occurrence count.
    ///
    /// URI: mcpforunity://console/log/{id}
    /// </summary>
    [McpForUnityResource("get_console_log")]
    public static class ConsoleLogResource
    {
        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
                return new ErrorResponse("Parameters cannot be null.");

            var p = new ToolParams(@params);
            string id = p.Get("id");
            if (string.IsNullOrEmpty(id))
                return new ErrorResponse("'id' parameter is required.");

            return ReadConsole.GetLogDetail(id);
        }
    }
}
