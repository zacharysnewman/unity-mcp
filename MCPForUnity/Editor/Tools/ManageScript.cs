using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using System.Threading;

#if USE_ROSLYN
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;
#endif

#if UNITY_EDITOR
using UnityEditor.Compilation;
#endif


namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Handles CRUD operations for C# scripts within the Unity project.
    /// </summary>
    /// <remarks>
    /// ROSLYN INSTALLATION GUIDE:
    /// To enable advanced syntax validation with Roslyn compiler services:
    /// 
    /// 1. Install Microsoft.CodeAnalysis.CSharp NuGet package:
    ///    - Open Package Manager in Unity
    ///    - Follow the instruction on https://github.com/GlitchEnzo/NuGetForUnity
    ///    
    /// 2. Open NuGet Package Manager and Install Microsoft.CodeAnalysis.CSharp:
    ///    
    /// 3. Alternative: Manual DLL installation:
    ///    - Download Microsoft.CodeAnalysis.CSharp.dll and dependencies
    ///    - Place in Assets/Plugins/ folder
    ///    - Ensure .NET compatibility settings are correct
    ///    
    /// 4. Define USE_ROSLYN symbol:
    ///    - Go to Player Settings > Scripting Define Symbols
    ///    - Add "USE_ROSLYN" to enable Roslyn-based validation
    ///    
    /// 5. Restart Unity after installation
    /// 
    /// Note: Without Roslyn, the system falls back to basic structural validation.
    /// Roslyn provides full C# compiler diagnostics with line numbers and detailed error messages.
    /// </remarks>
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
                case "validate":
                    {
                        string level = p.Get("level", "standard").ToLowerInvariant();
                        var chosen = level switch
                        {
                            "basic" => ValidationLevel.Basic,
                            "standard" => ValidationLevel.Standard,
                            "strict" => ValidationLevel.Strict,
                            "comprehensive" => ValidationLevel.Comprehensive,
                            _ => ValidationLevel.Standard
                        };
                        string fileText;
                        try { fileText = File.ReadAllText(fullPath); }
                        catch (Exception ex) { return new ErrorResponse($"Failed to read script: {ex.Message}"); }

                        bool ok = ValidateScriptSyntax(fileText, chosen, out string[] diagsRaw);
                        var diags = (diagsRaw ?? Array.Empty<string>()).Select(s =>
                        {
                            var m = Regex.Match(
                                s,
                                @"^(ERROR|WARNING|INFO): (.*?)(?: \(Line (\d+)\))?$",
                                RegexOptions.CultureInvariant | RegexOptions.Multiline,
                                TimeSpan.FromMilliseconds(250)
                            );
                            string severity = m.Success ? m.Groups[1].Value.ToLowerInvariant() : "info";
                            string message = m.Success ? m.Groups[2].Value : s;
                            int lineNum = m.Success && int.TryParse(m.Groups[3].Value, out var l) ? l : 0;
                            return new { line = lineNum, col = 0, severity, message };
                        }).ToArray();

                        var result = new { diagnostics = diags };
                        return ok ? new SuccessResponse("Validation completed.", result)
                                   : new ErrorResponse("Validation failed.", result);
                    }
                default:
                    return new ErrorResponse(
                        $"Unknown action: '{action}'. Valid actions are: read, validate."
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

        private struct CSharpLexer
        {
            private readonly string _text;
            private int _pos;
            private readonly int _end;
            private int _line;

            // String/comment state
            private bool _inSingleComment;
            private bool _inMultiComment;

            public CSharpLexer(string text, int start = 0, int end = -1)
            {
                _text = text;
                _pos = start;
                _end = end < 0 ? text.Length : end;
                _line = 1;
                // count newlines before start
                for (int i = 0; i < start && i < text.Length; i++)
                    if (text[i] == '\n') _line++;
                _inSingleComment = false;
                _inMultiComment = false;
                InNonCode = false;
            }

            public bool InNonCode { get; private set; }
            public int Position => _pos;
            public int Line => _line;

            /// <summary>
            /// Advance to the next character, updating all state.
            /// Returns false at end of range.
            /// </summary>
            public bool Advance(out char c)
            {
                if (_pos >= _end) { c = '\0'; return false; }

                c = _text[_pos];
                char next = _pos + 1 < _end ? _text[_pos + 1] : '\0';

                if (c == '\n')
                {
                    _line++;
                    if (_inSingleComment) _inSingleComment = false;
                }

                // Inside single-line comment
                if (_inSingleComment) { InNonCode = true; _pos++; return true; }

                // Inside multi-line comment
                if (_inMultiComment)
                {
                    if (c == '*' && next == '/') { _inMultiComment = false; InNonCode = true; _pos += 2; c = '/'; return true; }
                    InNonCode = true; _pos++; return true;
                }

                // Start of comment
                if (c == '/' && next == '/') { _inSingleComment = true; InNonCode = true; _pos += 2; return true; }
                if (c == '/' && next == '*') { _inMultiComment = true; InNonCode = true; _pos += 2; return true; }

                // Interpolated raw string: $"""...""" or $$"""...""" etc. (C# 11)
                // Must check BEFORE regular $" and BEFORE plain """
                if (c == '$')
                {
                    int dollarCount = 1;
                    while (_pos + dollarCount < _end && _text[_pos + dollarCount] == '$') dollarCount++;
                    int afterDollars = _pos + dollarCount;
                    if (afterDollars + 2 < _end && _text[afterDollars] == '"' && _text[afterDollars + 1] == '"' && _text[afterDollars + 2] == '"')
                    {
                        int q = 3;
                        while (afterDollars + q < _end && _text[afterDollars + q] == '"') q++;
                        _pos = afterDollars + q; // past all opening quotes
                        SkipInterpolatedRawStringBody(dollarCount, q);
                        InNonCode = true; return true;
                    }
                }

                // Raw string literal: """...""" (C# 11, non-interpolated)
                if (c == '"' && next == '"' && _pos + 2 < _end && _text[_pos + 2] == '"')
                {
                    int q = 3;
                    while (_pos + q < _end && _text[_pos + q] == '"') q++;
                    _pos += q; // past opening quotes
                    int closeCount = 0;
                    while (_pos < _end)
                    {
                        if (_text[_pos] == '\n') _line++;
                        if (_text[_pos] == '"') { closeCount++; if (closeCount >= q) { _pos++; break; } }
                        else closeCount = 0;
                        _pos++;
                    }
                    InNonCode = true; return true;
                }

                // Interpolated string: $"..." or $@"..." or @$"..."
                if ((c == '$' && next == '"') ||
                    (c == '$' && next == '@' && _pos + 2 < _end && _text[_pos + 2] == '"') ||
                    (c == '@' && next == '$' && _pos + 2 < _end && _text[_pos + 2] == '"'))
                {
                    bool isVerbatim = (next == '@') || (c == '@');
                    _pos += (c == '$' && next == '"') ? 2 : 3;
                    SkipInterpolatedStringBody(isVerbatim);
                    InNonCode = true; return true;
                }

                // Verbatim string: @"..."
                if (c == '@' && next == '"')
                {
                    _pos += 2;
                    while (_pos < _end)
                    {
                        if (_text[_pos] == '\n') _line++;
                        if (_text[_pos] == '"')
                        {
                            if (_pos + 1 < _end && _text[_pos + 1] == '"') { _pos += 2; continue; }
                            _pos++; break;
                        }
                        _pos++;
                    }
                    InNonCode = true; return true;
                }

                // Regular string: "..."
                if (c == '"')
                {
                    _pos++;
                    while (_pos < _end)
                    {
                        if (_text[_pos] == '\\') { _pos += 2; continue; }
                        if (_text[_pos] == '"') { _pos++; break; }
                        if (_text[_pos] == '\n') _line++;
                        _pos++;
                    }
                    InNonCode = true; return true;
                }

                // Char literal: '...'
                if (c == '\'')
                {
                    _pos++;
                    while (_pos < _end)
                    {
                        if (_text[_pos] == '\\') { _pos += 2; continue; }
                        if (_text[_pos] == '\'') { _pos++; break; }
                        _pos++;
                    }
                    InNonCode = true; return true;
                }

                InNonCode = false;
                _pos++;
                return true;
            }

            /// <summary>
            /// Skip the body of an interpolated string, handling nested interpolation holes.
            /// _pos should be right after the opening quote.
            /// </summary>
            private void SkipInterpolatedStringBody(bool isVerbatim)
            {
                int interpDepth = 0;
                while (_pos < _end)
                {
                    char ch = _text[_pos];
                    if (ch == '\n') _line++;

                    if (interpDepth > 0)
                    {
                        // Inside interpolation hole — this is code, scan for nested strings/braces
                        if (ch == '{') { interpDepth++; _pos++; continue; }
                        if (ch == '}') { interpDepth--; _pos++; continue; }
                        if (ch == '"')
                        {
                            // Nested string inside interpolation hole
                            _pos++;
                            while (_pos < _end)
                            {
                                if (_text[_pos] == '\\') { _pos += 2; continue; }
                                if (_text[_pos] == '"') { _pos++; break; }
                                if (_text[_pos] == '\n') _line++;
                                _pos++;
                            }
                            continue;
                        }
                        if (ch == '/' && _pos + 1 < _end)
                        {
                            if (_text[_pos + 1] == '/') { _pos += 2; while (_pos < _end && _text[_pos] != '\n') _pos++; continue; }
                            if (_text[_pos + 1] == '*') { _pos += 2; while (_pos + 1 < _end && !(_text[_pos] == '*' && _text[_pos + 1] == '/')) { if (_text[_pos] == '\n') _line++; _pos++; } if (_pos + 1 < _end) _pos += 2; continue; }
                        }
                        if (ch == '\'') { _pos++; while (_pos < _end) { if (_text[_pos] == '\\') { _pos += 2; continue; } if (_text[_pos] == '\'') { _pos++; break; } _pos++; } continue; }
                        _pos++;
                        continue;
                    }

                    // interpDepth == 0: inside string content
                    if (ch == '{')
                    {
                        if (_pos + 1 < _end && _text[_pos + 1] == '{') { _pos += 2; continue; } // escaped {{
                        interpDepth = 1; _pos++; continue;
                    }
                    if (ch == '}')
                    {
                        if (_pos + 1 < _end && _text[_pos + 1] == '}') { _pos += 2; continue; } // escaped }}
                        // Stray } at depth 0 — shouldn't happen in valid code, just advance
                        _pos++; continue;
                    }
                    if (ch == '"')
                    {
                        if (isVerbatim && _pos + 1 < _end && _text[_pos + 1] == '"') { _pos += 2; continue; } // doubled quote
                        _pos++; return; // closing quote
                    }
                    if (!isVerbatim && ch == '\\') { _pos += 2; continue; } // escape in regular interpolated
                    _pos++;
                }
            }

            /// <summary>
            /// Skip the body of an interpolated raw string ($"""...""", $$"""...""", etc.).
            /// dollarCount determines how many consecutive { start an interpolation hole.
            /// quoteCount is the number of " that close the string.
            /// _pos should be right after the opening quotes.
            /// </summary>
            private void SkipInterpolatedRawStringBody(int dollarCount, int quoteCount)
            {
                int interpDepth = 0;
                while (_pos < _end)
                {
                    char ch = _text[_pos];
                    if (ch == '\n') _line++;

                    if (interpDepth > 0)
                    {
                        // Inside interpolation hole — code context
                        if (ch == '{') { interpDepth++; _pos++; continue; }
                        if (ch == '}') { interpDepth--; _pos++; continue; }
                        if (ch == '"')
                        {
                            _pos++;
                            while (_pos < _end)
                            {
                                if (_text[_pos] == '\\') { _pos += 2; continue; }
                                if (_text[_pos] == '"') { _pos++; break; }
                                if (_text[_pos] == '\n') _line++;
                                _pos++;
                            }
                            continue;
                        }
                        if (ch == '/' && _pos + 1 < _end)
                        {
                            if (_text[_pos + 1] == '/') { _pos += 2; while (_pos < _end && _text[_pos] != '\n') _pos++; continue; }
                            if (_text[_pos + 1] == '*') { _pos += 2; while (_pos + 1 < _end && !(_text[_pos] == '*' && _text[_pos + 1] == '/')) { if (_text[_pos] == '\n') _line++; _pos++; } if (_pos + 1 < _end) _pos += 2; continue; }
                        }
                        if (ch == '\'') { _pos++; while (_pos < _end) { if (_text[_pos] == '\\') { _pos += 2; continue; } if (_text[_pos] == '\'') { _pos++; break; } _pos++; } continue; }
                        _pos++;
                        continue;
                    }

                    // String content (interpDepth == 0)
                    // Check for closing quote sequence
                    if (ch == '"')
                    {
                        int qc = 1;
                        while (_pos + qc < _end && _text[_pos + qc] == '"') qc++;
                        if (qc >= quoteCount) { _pos += quoteCount; return; }
                        // Fewer quotes than needed — literal content
                        _pos += qc;
                        continue;
                    }

                    // Check for interpolation hole: dollarCount consecutive {'s
                    if (ch == '{')
                    {
                        int bc = 1;
                        while (_pos + bc < _end && _text[_pos + bc] == '{') bc++;
                        if (bc >= dollarCount)
                        {
                            // Exactly dollarCount opens an interpolation hole; extras are literal
                            _pos += dollarCount;
                            interpDepth = 1;
                        }
                        else
                        {
                            // Fewer than dollarCount — literal braces
                            _pos += bc;
                        }
                        continue;
                    }

                    // Closing braces with dollarCount threshold — literal if fewer
                    if (ch == '}')
                    {
                        int bc = 1;
                        while (_pos + bc < _end && _text[_pos + bc] == '}') bc++;
                        _pos += bc; // all literal at depth 0
                        continue;
                    }

                    _pos++;
                }
            }
        }

        private static bool CheckBalancedDelimiters(string text, out int line, out char expected)
        {
            var braceStack = new Stack<int>();
            var parenStack = new Stack<int>();
            var bracketStack = new Stack<int>();
            line = 1; expected = '\0';

            var lexer = new CSharpLexer(text);
            while (lexer.Advance(out char c))
            {
                if (lexer.InNonCode) continue;

                switch (c)
                {
                    case '{': braceStack.Push(lexer.Line); break;
                    case '}':
                        if (braceStack.Count == 0) { line = lexer.Line; expected = '{'; return false; }
                        braceStack.Pop();
                        break;
                    case '(': parenStack.Push(lexer.Line); break;
                    case ')':
                        if (parenStack.Count == 0) { line = lexer.Line; expected = '('; return false; }
                        parenStack.Pop();
                        break;
                    case '[': bracketStack.Push(lexer.Line); break;
                    case ']':
                        if (bracketStack.Count == 0) { line = lexer.Line; expected = '['; return false; }
                        bracketStack.Pop();
                        break;
                }
            }

            if (braceStack.Count > 0) { line = braceStack.Peek(); expected = '}'; return false; }
            if (parenStack.Count > 0) { line = parenStack.Peek(); expected = ')'; return false; }
            if (bracketStack.Count > 0) { line = bracketStack.Peek(); expected = ']'; return false; }

            return true;
        }

        private static bool ValidateScriptSyntax(string contents, ValidationLevel level, out string[] errors)
        {
            var errorList = new System.Collections.Generic.List<string>();
            errors = null;

            if (string.IsNullOrEmpty(contents))
            {
                return true; // Empty content is valid
            }

            // Basic structural validation: check balanced delimiters
            if (!CheckBalancedDelimiters(contents, out int errLine, out char errExpected))
            {
                errorList.Add($"ERROR: Unbalanced delimiter at line {errLine} (expected '{errExpected}')");
                errors = errorList.ToArray();
                return false;
            }

#if USE_ROSLYN
            // Advanced Roslyn-based validation: only run for Standard+; fail on Roslyn errors
            if (level >= ValidationLevel.Standard)
            {
                if (!ValidateScriptSyntaxRoslyn(contents, level, errorList))
                {
                    errors = errorList.ToArray();
                    return false;
                }
            }
#endif

            // Unity-specific validation
            if (level >= ValidationLevel.Standard)
            {
                ValidateScriptSyntaxUnity(contents, errorList);
            }

            // Semantic analysis for common issues
            if (level >= ValidationLevel.Comprehensive)
            {
                ValidateSemanticRules(contents, errorList);
            }

#if USE_ROSLYN
            // Full semantic compilation validation for Strict level
            if (level == ValidationLevel.Strict)
            {
                if (!ValidateScriptSemantics(contents, errorList))
                {
                    errors = errorList.ToArray();
                    return false; // Strict level fails on any semantic errors
                }
            }
#endif

            errors = errorList.ToArray();
            return errorList.Count == 0 || (level != ValidationLevel.Strict && !errorList.Any(e => e.StartsWith("ERROR:")));
        }

        /// <summary>
        /// Validation strictness levels
        /// </summary>
        private enum ValidationLevel
        {
            Basic,        // Only syntax errors
            Standard,     // Syntax + Unity best practices
            Comprehensive, // All checks + semantic analysis
            Strict        // Treat all issues as errors
        }

#if USE_ROSLYN
        /// <summary>
        /// Cached compilation references for performance
        /// </summary>
        private static System.Collections.Generic.List<MetadataReference> _cachedReferences = null;
        private static DateTime _cacheTime = DateTime.MinValue;
        private static readonly TimeSpan CacheExpiry = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Validates syntax using Roslyn compiler services
        /// </summary>
        private static bool ValidateScriptSyntaxRoslyn(string contents, ValidationLevel level, System.Collections.Generic.List<string> errors)
        {
            try
            {
                var syntaxTree = CSharpSyntaxTree.ParseText(contents);
                var diagnostics = syntaxTree.GetDiagnostics();
                
                bool hasErrors = false;
                foreach (var diagnostic in diagnostics)
                {
                    string severity = diagnostic.Severity.ToString().ToUpper();
                    string message = $"{severity}: {diagnostic.GetMessage()}";
                    
                    if (diagnostic.Severity == DiagnosticSeverity.Error)
                    {
                        hasErrors = true;
                    }
                    
                    // Include warnings in comprehensive mode
                    if (level >= ValidationLevel.Standard || diagnostic.Severity == DiagnosticSeverity.Error) //Also use Standard for now
                    {
                        var location = diagnostic.Location.GetLineSpan();
                        if (location.IsValid)
                        {
                            message += $" (Line {location.StartLinePosition.Line + 1})";
                        }
                        errors.Add(message);
                    }
                }
                
                return !hasErrors;
            }
            catch (Exception ex)
            {
                errors.Add($"ERROR: Roslyn validation failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Validates script semantics using full compilation context to catch namespace, type, and method resolution errors
        /// </summary>
        private static bool ValidateScriptSemantics(string contents, System.Collections.Generic.List<string> errors)
        {
            try
            {
                // Get compilation references with caching
                var references = GetCompilationReferences();
                if (references == null || references.Count == 0)
                {
                    errors.Add("WARNING: Could not load compilation references for semantic validation");
                    return true; // Don't fail if we can't get references
                }

                // Create syntax tree
                var syntaxTree = CSharpSyntaxTree.ParseText(contents);

                // Create compilation with full context
                var compilation = CSharpCompilation.Create(
                    "TempValidation",
                    new[] { syntaxTree },
                    references,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                );

                // Get semantic diagnostics - this catches all the issues you mentioned!
                var diagnostics = compilation.GetDiagnostics();
                
                bool hasErrors = false;
                foreach (var diagnostic in diagnostics)
                {
                    if (diagnostic.Severity == DiagnosticSeverity.Error)
                    {
                        hasErrors = true;
                        var location = diagnostic.Location.GetLineSpan();
                        string locationInfo = location.IsValid ? 
                            $" (Line {location.StartLinePosition.Line + 1}, Column {location.StartLinePosition.Character + 1})" : "";
                        
                        // Include diagnostic ID for better error identification
                        string diagnosticId = !string.IsNullOrEmpty(diagnostic.Id) ? $" [{diagnostic.Id}]" : "";
                        errors.Add($"ERROR: {diagnostic.GetMessage()}{diagnosticId}{locationInfo}");
                    }
                    else if (diagnostic.Severity == DiagnosticSeverity.Warning)
                    {
                        var location = diagnostic.Location.GetLineSpan();
                        string locationInfo = location.IsValid ? 
                            $" (Line {location.StartLinePosition.Line + 1}, Column {location.StartLinePosition.Character + 1})" : "";
                        
                        string diagnosticId = !string.IsNullOrEmpty(diagnostic.Id) ? $" [{diagnostic.Id}]" : "";
                        errors.Add($"WARNING: {diagnostic.GetMessage()}{diagnosticId}{locationInfo}");
                    }
                }
                
                return !hasErrors;
            }
            catch (Exception ex)
            {
                errors.Add($"ERROR: Semantic validation failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Gets compilation references with caching for performance
        /// </summary>
        private static System.Collections.Generic.List<MetadataReference> GetCompilationReferences()
        {
            // Check cache validity
            if (_cachedReferences != null && DateTime.Now - _cacheTime < CacheExpiry)
            {
                return _cachedReferences;
            }

            try
            {
                var references = new System.Collections.Generic.List<MetadataReference>();

                // Core .NET assemblies
                references.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location)); // mscorlib/System.Private.CoreLib
                references.Add(MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location)); // System.Linq
                references.Add(MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location)); // System.Collections

                // Unity assemblies
                try
                {
                    references.Add(MetadataReference.CreateFromFile(typeof(UnityEngine.Debug).Assembly.Location)); // UnityEngine
                }
                catch (Exception ex)
                {
                    McpLog.Warn($"Could not load UnityEngine assembly: {ex.Message}");
                }

#if UNITY_EDITOR
                try
                {
                    references.Add(MetadataReference.CreateFromFile(typeof(UnityEditor.Editor).Assembly.Location)); // UnityEditor
                }
                catch (Exception ex)
                {
                    McpLog.Warn($"Could not load UnityEditor assembly: {ex.Message}");
                }

                // Get Unity project assemblies
                try
                {
                    var assemblies = CompilationPipeline.GetAssemblies();
                    foreach (var assembly in assemblies)
                    {
                        if (File.Exists(assembly.outputPath))
                        {
                            references.Add(MetadataReference.CreateFromFile(assembly.outputPath));
                        }
                    }
                }
                catch (Exception ex)
                {
                    McpLog.Warn($"Could not load Unity project assemblies: {ex.Message}");
                }
#endif

                // Cache the results
                _cachedReferences = references;
                _cacheTime = DateTime.Now;

                return references;
            }
            catch (Exception ex)
            {
                McpLog.Error($"Failed to get compilation references: {ex.Message}");
                return new System.Collections.Generic.List<MetadataReference>();
            }
        }
#else
        private static bool ValidateScriptSyntaxRoslyn(string contents, ValidationLevel level, System.Collections.Generic.List<string> errors)
        {
            // Fallback when Roslyn is not available
            return true;
        }
#endif

        /// <summary>
        /// Validates Unity-specific coding rules and best practices
        /// //TODO: Naive Unity Checks and not really yield any results, need to be improved
        /// </summary>
        private static void ValidateScriptSyntaxUnity(string contents, System.Collections.Generic.List<string> errors)
        {
            // Check for common Unity anti-patterns
            if (contents.Contains("FindObjectOfType") && contents.Contains("Update()"))
            {
                errors.Add("WARNING: FindObjectOfType in Update() can cause performance issues");
            }

            if (contents.Contains("GameObject.Find") && contents.Contains("Update()"))
            {
                errors.Add("WARNING: GameObject.Find in Update() can cause performance issues");
            }

            // Check for proper MonoBehaviour usage
            if (contents.Contains(": MonoBehaviour") && !contents.Contains("using UnityEngine"))
            {
                errors.Add("WARNING: MonoBehaviour requires 'using UnityEngine;'");
            }

            // Check for SerializeField usage
            if (contents.Contains("[SerializeField]") && !contents.Contains("using UnityEngine"))
            {
                errors.Add("WARNING: SerializeField requires 'using UnityEngine;'");
            }

            // Check for proper coroutine usage
            if (contents.Contains("StartCoroutine") && !contents.Contains("IEnumerator"))
            {
                errors.Add("WARNING: StartCoroutine typically requires IEnumerator methods");
            }

            // Check for Update without FixedUpdate for physics
            if (contents.Contains("Rigidbody") && contents.Contains("Update()") && !contents.Contains("FixedUpdate()"))
            {
                errors.Add("WARNING: Consider using FixedUpdate() for Rigidbody operations");
            }

            // Check for missing null checks on Unity objects
            if (contents.Contains("GetComponent<") && !contents.Contains("!= null"))
            {
                errors.Add("WARNING: Consider null checking GetComponent results");
            }

            // Check for proper event function signatures
            if (contents.Contains("void Start(") && !contents.Contains("void Start()"))
            {
                errors.Add("WARNING: Start() should not have parameters");
            }

            if (contents.Contains("void Update(") && !contents.Contains("void Update()"))
            {
                errors.Add("WARNING: Update() should not have parameters");
            }

            // Check for inefficient string operations
            if (contents.Contains("Update()") && contents.Contains("\"") && contents.Contains("+"))
            {
                errors.Add("WARNING: String concatenation in Update() can cause garbage collection issues");
            }
        }

        /// <summary>
        /// Validates semantic rules and common coding issues
        /// </summary>
        private static void ValidateSemanticRules(string contents, System.Collections.Generic.List<string> errors)
        {
            // Check for potential memory leaks
            if (contents.Contains("new ") && contents.Contains("Update()"))
            {
                errors.Add("WARNING: Creating objects in Update() may cause memory issues");
            }

            // Check for magic numbers
            var magicNumberPattern = new Regex(@"\b\d+\.?\d*f?\b(?!\s*[;})\]])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
            var matches = magicNumberPattern.Matches(contents);
            if (matches.Count > 5)
            {
                errors.Add("WARNING: Consider using named constants instead of magic numbers");
            }

            // Check for long methods (simple line count check)
            var methodPattern = new Regex(@"(public|private|protected|internal)?\s*(static)?\s*\w+\s+\w+\s*\([^)]*\)\s*{", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
            var methodMatches = methodPattern.Matches(contents);
            foreach (Match match in methodMatches)
            {
                int startIndex = match.Index;
                int braceCount = 0;
                int lineCount = 0;
                bool inMethod = false;

                for (int i = startIndex; i < contents.Length; i++)
                {
                    if (contents[i] == '{')
                    {
                        braceCount++;
                        inMethod = true;
                    }
                    else if (contents[i] == '}')
                    {
                        braceCount--;
                        if (braceCount == 0 && inMethod)
                            break;
                    }
                    else if (contents[i] == '\n' && inMethod)
                    {
                        lineCount++;
                    }
                }

                if (lineCount > 50)
                {
                    errors.Add("WARNING: Method is very long, consider breaking it into smaller methods");
                    break; // Only report once
                }
            }

            // Check for proper exception handling
            if (contents.Contains("catch") && contents.Contains("catch()"))
            {
                errors.Add("WARNING: Empty catch blocks should be avoided");
            }

            // Check for proper async/await usage
            if (contents.Contains("async ") && !contents.Contains("await"))
            {
                errors.Add("WARNING: Async method should contain await or return Task");
            }

            // Check for hardcoded tags and layers
            if (contents.Contains("\"Player\"") || contents.Contains("\"Enemy\""))
            {
                errors.Add("WARNING: Consider using constants for tags instead of hardcoded strings");
            }
        }

        //TODO: A easier way for users to update incorrect scripts (now duplicated with the updateScript method and need to also update server side, put aside for now)
        /// <summary>
        /// Public method to validate script syntax with configurable validation level
        /// Returns detailed validation results including errors and warnings
        /// </summary>
