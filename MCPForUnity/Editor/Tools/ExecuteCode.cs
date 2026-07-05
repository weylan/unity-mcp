using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Runtime.Helpers;
using Microsoft.CSharp;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MCPForUnity.Editor.Tools
{
    [McpForUnityTool("execute_code", AutoRegister = false, Group = "scripting_ext")]
    public static class ExecuteCode
    {
        private const int MaxCodeLength = 50000;
        private const int MaxHistoryEntries = 50;
        private const int MaxHistoryCodePreview = 500;
        internal const int WrapperLineOffset = 10;
        private const string WrapperClassName = "MCPDynamicCode";
        private const string WrapperMethodName = "Execute";

        private const string ActionExecute = "execute";
        private const string ActionGetHistory = "get_history";
        private const string ActionClearHistory = "clear_history";
        private const string ActionReplay = "replay";

        private static readonly List<HistoryEntry> _history = new List<HistoryEntry>();
        private static string[] _cachedAssemblyPaths;

        // Compiled-assembly cache: an identical wrapped source within a domain reuses its compiled
        // Assembly instead of recompiling (Roslyn in-memory, or CodeDom shelling out to mcs). Keyed
        // by resolved compiler + assembly-reference fingerprint + SHA-256 of the wrapped source.
        // Bookkeeping is guarded by _cacheLock: the production transport path is single-threaded
        // (TransportCommandDispatcher marshals every command to the Unity main thread), but
        // HandleCommand is public and may be invoked by tests or future non-transport callers.
        // Invalidated on every domain reload because cached assemblies are compiled against
        // MetadataReference.CreateFromFile(_cachedAssemblyPaths), which a project recompile rewrites.
        private static readonly Dictionary<string, (Assembly assembly, string usedCompiler)> _assemblyCache
            = new Dictionary<string, (Assembly, string)>();
        private static readonly object _cacheLock = new object();
        private static string _assemblyPathsFingerprint;
        private static long _compileCount;

        /// <summary>Count of real compilations performed (cache misses). Test seam.</summary>
        internal static long CompileCount => System.Threading.Interlocked.Read(ref _compileCount);

        /// <summary>Count of real MetadataReference-set builds (disk read + metadata parse). Test seam
        /// for the per-domain reference cache: distinct snippets within a domain reuse one built set.</summary>
        internal static long RefBuildCount => RoslynCompiler.RefBuildCount;

        [UnityEditor.InitializeOnLoadMethod]
        private static void OnDomainReload()
        {
            _cachedAssemblyPaths = null;
            RoslynCompiler.ResetCache();
            ClearCompileCache();
        }

        private static void ClearCompileCache()
        {
            lock (_cacheLock)
            {
                _assemblyCache.Clear();
                _assemblyPathsFingerprint = null;
            }
        }

        /// <summary>Test seam: clear the compile cache and reset compiler availability + assembly
        /// paths, mirroring a domain reload so tests can assert recompilation deterministically.</summary>
        internal static void ClearCompileCacheForTests()
        {
            _cachedAssemblyPaths = null;
            RoslynCompiler.ResetCache();
            ClearCompileCache();
        }

        private static readonly HashSet<string> _blockedPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System.IO.File.Delete",
            "System.IO.Directory.Delete",
            "FileUtil.DeleteFileOrDirectory",
            "AssetDatabase.DeleteAsset",
            "AssetDatabase.MoveAssetToTrash",
            "EditorApplication.Exit",
            "Process.Start",
            "Process.Kill",
            "while(true)",
            "while (true)",
            "for(;;)",
            "for (;;)",
        };

        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
                return new ErrorResponse("Parameters cannot be null.");

            var p = new ToolParams(@params);
            var actionResult = p.GetRequired("action");
            if (!actionResult.IsSuccess)
                return new ErrorResponse(actionResult.ErrorMessage);

            string action = actionResult.Value.ToLowerInvariant();

            switch (action)
            {
                case ActionExecute:
                    return HandleExecute(@params);
                case ActionGetHistory:
                    return HandleGetHistory(@params);
                case ActionClearHistory:
                    return HandleClearHistory();
                case ActionReplay:
                    return HandleReplay(@params);
                default:
                    return new ErrorResponse(
                        $"Unknown action: '{action}'. Valid actions: {ActionExecute}, {ActionGetHistory}, {ActionClearHistory}, {ActionReplay}");
            }
        }

        private static object HandleExecute(JObject @params)
        {
            string code = @params["code"]?.ToString();
            if (string.IsNullOrWhiteSpace(code))
                return new ErrorResponse("Required parameter 'code' is missing or empty.");

            if (code.Length > MaxCodeLength)
                return new ErrorResponse($"Code exceeds maximum length of {MaxCodeLength} characters.");

            bool safetyChecks = @params["safety_checks"]?.Value<bool>() ?? true;
            string compiler = @params["compiler"]?.ToString()?.ToLowerInvariant() ?? "auto";

            if (safetyChecks)
            {
                var violation = CheckBlockedPatterns(code);
                if (violation != null)
                    return new ErrorResponse($"Blocked pattern detected: {violation}");
            }

            var args = Array.Empty<string>();
            try
            {
                var startTime = DateTime.UtcNow;
                if (!TryReadArgs(@params, out args, out var argsError))
                    return argsError;

                var result = CompileAndExecute(code, compiler, args);
                var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;

                AddToHistory(code, args, result, elapsed, safetyChecks, compiler);
                return result;
            }
            catch (Exception e)
            {
                McpLog.Error($"[ExecuteCode] Execution failed: {e}");
                var errorResult = new ErrorResponse($"Execution failed: {e.Message}");
                AddToHistory(code, args, errorResult, 0, safetyChecks, compiler);
                return errorResult;
            }
        }

        private static object HandleGetHistory(JObject @params)
        {
            int limit = @params["limit"]?.Value<int>() ?? 10;
            limit = Math.Clamp(limit, 1, MaxHistoryEntries);

            if (_history.Count == 0)
                return new SuccessResponse("No execution history.", new { total = 0, entries = new object[0] });

            var entries = _history.Skip(Math.Max(0, _history.Count - limit)).ToList();
            return new SuccessResponse($"Returning {entries.Count} of {_history.Count} history entries.", new
            {
                total = _history.Count,
                entries = entries.Select((e, i) => new
                {
                    index = _history.Count - entries.Count + i,
                    codePreview = e.code.Length > MaxHistoryCodePreview
                        ? e.code.Substring(0, MaxHistoryCodePreview) + "..."
                        : e.code,
                    e.success,
                    e.resultPreview,
                    e.elapsedMs,
                    e.timestamp,
                    e.safetyChecksEnabled,
                    e.compiler,
                }).ToList(),
            });
        }

        private static object HandleClearHistory()
        {
            int count = _history.Count;
            _history.Clear();
            return new SuccessResponse($"Cleared {count} history entries.");
        }

        private static object HandleReplay(JObject @params)
        {
            if (_history.Count == 0)
                return new ErrorResponse("No execution history to replay.");

            int? index = @params["index"]?.Value<int>();
            if (index == null || index < 0 || index >= _history.Count)
                return new ErrorResponse($"Invalid history index. Valid range: 0-{_history.Count - 1}");

            var entry = _history[index.Value];
            var replayParams = JObject.FromObject(new
            {
                action = ActionExecute,
                code = entry.code,
                args = new JArray(entry.args ?? Array.Empty<string>()),
                safety_checks = entry.safetyChecksEnabled,
                compiler = entry.compiler ?? "auto",
            });
            return HandleExecute(replayParams);
        }

        // ──────────────────── Compilation ────────────────────

        private static object CompileAndExecute(string code, string compiler, string[] args)
        {
            string wrappedSource = WrapUserCode(code);
            string[] assemblyPaths = GetAssemblyPaths();

            // Resolve "auto"/empty/unknown to the backend the switch below will actually use, so the
            // cache key reflects the real compiler. Availability is stable within a domain (reset on
            // domain reload, which also clears the cache), keeping it consistent for a cache entry.
            string effectiveCompiler = (compiler == "roslyn" || compiler == "codedom")
                ? compiler
                : (RoslynCompiler.IsAvailable ? "roslyn" : "codedom");
            string cacheKey = effectiveCompiler + "|" + GetAssemblyPathsFingerprint() + "|" + Sha256Hex(wrappedSource);

            lock (_cacheLock)
            {
                if (_assemblyCache.TryGetValue(cacheKey, out var cached))
                {
                    // Re-invoke the cached assembly's Execute(__mcpArgs) against current Unity state — intended:
                    // repeated probes re-run. Note the snippet's compiler-generated statics persist
                    // across cache hits (fresh-compile semantics only on first call / after a reload).
                    return InvokeCompiled(cached.assembly, cached.usedCompiler, args);
                }
            }

            // Cache miss → real compilation.
            System.Threading.Interlocked.Increment(ref _compileCount);

            Assembly compiled;
            string usedCompiler;

            switch (compiler)
            {
                case "roslyn":
                    if (!RoslynCompiler.IsAvailable)
                        return new ErrorResponse("Roslyn (Microsoft.CodeAnalysis) is not available. Install it via NuGet or use compiler='codedom'.");
                    compiled = RoslynCompiler.Compile(wrappedSource, assemblyPaths, out var roslynErrors);
                    if (compiled == null)
                        return new ErrorResponse("Compilation failed", new { errors = OffsetErrors(roslynErrors), compiler = "roslyn" });
                    usedCompiler = "roslyn";
                    break;

                case "codedom":
                    compiled = CodeDomCompile(wrappedSource, assemblyPaths, out var codedomErrors);
                    if (compiled == null)
                        return new ErrorResponse("Compilation failed", new { errors = OffsetErrors(codedomErrors), compiler = "codedom" });
                    usedCompiler = "codedom";
                    break;

                default: // "auto"
                    if (RoslynCompiler.IsAvailable)
                    {
                        compiled = RoslynCompiler.Compile(wrappedSource, assemblyPaths, out var autoErrors);
                        if (compiled == null)
                            return new ErrorResponse("Compilation failed", new { errors = OffsetErrors(autoErrors), compiler = "roslyn" });
                        usedCompiler = "roslyn";
                    }
                    else
                    {
                        compiled = CodeDomCompile(wrappedSource, assemblyPaths, out var autoFallbackErrors);
                        if (compiled == null)
                            return new ErrorResponse("Compilation failed", new { errors = OffsetErrors(autoFallbackErrors), compiler = "codedom" });
                        usedCompiler = "codedom";
                    }
                    break;
            }

            lock (_cacheLock)
            {
                _assemblyCache[cacheKey] = (compiled, usedCompiler);
            }

            return InvokeCompiled(compiled, usedCompiler, args);
        }

        private static object InvokeCompiled(Assembly assembly, string compilerUsed, string[] args)
        {
            var type = assembly.GetType(WrapperClassName);
            if (type == null)
                return new ErrorResponse("Internal error: failed to find compiled type.");

            var method = type.GetMethod(WrapperMethodName, BindingFlags.Public | BindingFlags.Static);
            if (method == null)
                return new ErrorResponse("Internal error: failed to find Execute method.");

            object result = null;
            Exception executionError = null;

            try
            {
                result = method.Invoke(null, new object[] { args ?? Array.Empty<string>() });
            }
            catch (TargetInvocationException tie)
            {
                executionError = tie.InnerException ?? tie;
            }
            catch (Exception e)
            {
                executionError = e;
            }

            if (executionError != null)
                return new ErrorResponse($"Runtime error: {executionError.Message}",
                    new { exceptionType = executionError.GetType().Name, stackTrace = executionError.StackTrace, compiler = compilerUsed });

            if (result != null)
                return new SuccessResponse("Code executed successfully.",
                    new { result = SerializeResult(result), compiler = compilerUsed });

            return new SuccessResponse("Code executed successfully.", new { compiler = compilerUsed });
        }

        private static List<string> OffsetErrors(List<string> errors)
        {
            // Errors already have line numbers adjusted by the compiler-specific code
            return errors;
        }

        // ──────────────────── CodeDom compiler ────────────────────

        private static Assembly CodeDomCompile(string source, string[] assemblyPaths, out List<string> errors)
        {
            errors = new List<string>();

            // CodeDom needs the netstandard-aware filtered paths
            var filtered = FilterAssemblyPathsForCodeDom(assemblyPaths);

            // CSharpCodeProvider turns every ReferencedAssemblies entry into a literal /r:"..." flag
            // on the csc command line. Projects with ~100+ asmdefs blow past Windows' 32 KB
            // CreateProcess argument limit and fail with "The filename or extension is too long."
            // Route references through a response file (@responsefile is supported by both mcs and
            // Roslyn csc) so we pass exactly one short argument regardless of reference count.
            string responseFilePath = Path.Combine(Path.GetTempPath(), $"mcp-codedom-{Guid.NewGuid():N}.rsp");

            try
            {
                using (var writer = new StreamWriter(responseFilePath, append: false, Encoding.UTF8))
                {
                    foreach (var path in filtered)
                    {
                        writer.Write("/r:\"");
                        writer.Write(path);
                        writer.WriteLine("\"");
                    }
                }

                using (var provider = new CSharpCodeProvider())
                {
                    var parameters = new CompilerParameters
                    {
                        GenerateInMemory = true,
                        GenerateExecutable = false,
                        TreatWarningsAsErrors = false,
                        CompilerOptions = "@\"" + responseFilePath + "\"",
                    };

                    var results = provider.CompileAssemblyFromSource(parameters, source);

                    if (results.Errors.HasErrors)
                    {
                        foreach (CompilerError error in results.Errors)
                        {
                            if (!error.IsWarning)
                            {
                                int userLine = Math.Max(1, error.Line - WrapperLineOffset);
                                errors.Add($"Line {userLine}: {error.ErrorText}");
                            }
                        }
                        return null;
                    }

                    return results.CompiledAssembly;
                }
            }
            finally
            {
                try { if (File.Exists(responseFilePath)) File.Delete(responseFilePath); }
                catch { /* best effort */ }
            }
        }

        // CSharpCodeProvider can't resolve type-forwarding, so when netstandard.dll is loaded
        // alongside mscorlib/System.Runtime/System.Collections, types like List<T> appear in
        // multiple assemblies causing "type defined multiple times" errors.
        private static readonly HashSet<string> _codedomDuplicateAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mscorlib",
            "System.Runtime",
            "System.Private.CoreLib",
            "System.Collections",
        };

        private static string[] FilterAssemblyPathsForCodeDom(string[] allPaths)
        {
            bool hasNetstandard = allPaths.Any(p =>
                string.Equals(Path.GetFileNameWithoutExtension(p), "netstandard", StringComparison.OrdinalIgnoreCase));

            if (!hasNetstandard)
                return allPaths;

            return allPaths.Where(p =>
                !_codedomDuplicateAssemblies.Contains(Path.GetFileNameWithoutExtension(p))).ToArray();
        }

        // ──────────────────── Shared helpers ────────────────────

        private static string WrapUserCode(string code)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using System.Reflection;");
            sb.AppendLine("using UnityEngine;");
            sb.AppendLine("using UnityEditor;");
            sb.AppendLine($"public static class {WrapperClassName}");
            sb.AppendLine("{");
            sb.AppendLine($"    public static object {WrapperMethodName}(string[] __mcpArgs)");
            sb.AppendLine("    {");
            sb.AppendLine(code);
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string[] GetAssemblyPaths()
        {
            if (_cachedAssemblyPaths == null)
                _cachedAssemblyPaths = ResolveAssemblyPaths();
            return _cachedAssemblyPaths;
        }

        // Fingerprint of the reference set, folded into the cache key so a cached assembly is never
        // reused against a different set of assembly references. Computed once per domain (reset
        // alongside _cachedAssemblyPaths on domain reload / ClearCompileCacheForTests).
        private static string GetAssemblyPathsFingerprint()
        {
            if (_assemblyPathsFingerprint == null)
            {
                var paths = GetAssemblyPaths();
                var sorted = (string[])paths.Clone();
                Array.Sort(sorted, StringComparer.Ordinal);
                _assemblyPathsFingerprint = Sha256Hex(string.Join("\n", sorted));
            }
            return _assemblyPathsFingerprint;
        }

        private static string Sha256Hex(string s)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static string[] ResolveAssemblyPaths()
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var assembly in UnityAssembliesCompat.GetLoadedAssemblies())
            {
                try
                {
                    if (assembly.IsDynamic) continue;
                    var location = assembly.Location;
                    if (string.IsNullOrEmpty(location)) continue;
                    if (!File.Exists(location)) continue;
                    paths.Add(location);
                }
                catch (NotSupportedException)
                {
                    // Some assemblies don't support Location property
                }
            }

            var result = new string[paths.Count];
            paths.CopyTo(result);
            return result;
        }

        private static string CheckBlockedPatterns(string code)
        {
            foreach (var pattern in _blockedPatterns)
            {
                if (code.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    return $"Code contains blocked pattern: '{pattern}'. Disable safety checks with safety_checks=false if this is intentional.";
            }
            return null;
        }

        private static bool TryReadArgs(JObject @params, out string[] args, out ErrorResponse error)
        {
            args = Array.Empty<string>();
            error = null;

            var token = @params["args"];
            if (token == null || token.Type == JTokenType.Null)
                return true;

            if (token.Type != JTokenType.Array)
            {
                error = new ErrorResponse("Optional parameter 'args' must be a JSON array.");
                return false;
            }

            args = ((JArray)token)
                .Select(item => item.Type == JTokenType.Null
                    ? null
                    : item is JValue value
                        ? Convert.ToString(value.Value)
                        : item.ToString(Newtonsoft.Json.Formatting.None))
                .ToArray();
            return true;
        }

        private static void AddToHistory(string code, string[] args, object result, double elapsedMs, bool safetyChecks, string compiler = "auto")
        {
            string preview;
            if (result is SuccessResponse sr)
                preview = sr.Data?.ToString() ?? sr.Message;
            else if (result is ErrorResponse er)
                preview = er.Error;
            else
                preview = result?.ToString() ?? "null";

            if (preview != null && preview.Length > 200)
                preview = preview.Substring(0, 200) + "...";

            _history.Add(new HistoryEntry
            {
                code = code,
                args = args ?? Array.Empty<string>(),
                success = result is SuccessResponse,
                resultPreview = preview,
                elapsedMs = Math.Round(elapsedMs, 1),
                timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                safetyChecksEnabled = safetyChecks,
                compiler = compiler,
            });

            while (_history.Count > MaxHistoryEntries)
                _history.RemoveAt(0);
        }

        private static object SerializeResult(object result)
        {
            if (result == null) return null;

            var type = result.GetType();
            if (type.IsPrimitive || result is string || result is decimal)
                return result;

            try
            {
                return JToken.FromObject(result);
            }
            catch
            {
                return result.ToString();
            }
        }

        private class HistoryEntry
        {
            public string code;
            public string[] args;
            public bool success;
            public string resultPreview;
            public double elapsedMs;
            public string timestamp;
            public bool safetyChecksEnabled;
            public string compiler;
        }
    }

    /// <summary>
    /// Roslyn compiler backend accessed entirely via reflection.
    /// No compile-time dependency on Microsoft.CodeAnalysis — works only if the package is installed.
    /// </summary>
    internal static class RoslynCompiler
    {
        private static bool? _isAvailable;
        private static Type _syntaxTreeType;
        private static Type _compilationType;
        private static Type _compilationOptionsType;
        private static Type _parseOptionsType;
        private static Type _metadataReferenceType;
        private static Type _outputKindEnum;
        private static Type _languageVersionEnum;
        private static MethodInfo _parseText;
        private static MethodInfo _createCompilation;
        private static MethodInfo _createFromFile;
        private static MethodInfo _emit;
        private static object _parseOptions;
        private static object _compilationOptions;

        // Per-domain MetadataReference cache. MetadataReference.CreateFromFile reads and parses each
        // assembly's PE metadata; for a large project that dominates a cache-miss compile (measured
        // ~70-85% of it). PortableExecutableReference is immutable and safe to reuse across
        // compilations, so build the set once per domain and reuse it for every distinct snippet.
        // Keyed by the assembly-paths array *instance*, which ExecuteCode recreates on domain reload
        // (GetAssemblyPaths caches _cachedAssemblyPaths, nulled in OnDomainReload) — same validity
        // envelope as that existing per-domain path cache, so no new invalidation risk.
        private static object _cachedMetadataRefs;   // List<MetadataReference> as IList
        private static string[] _cachedRefsPaths;
        private static long _refBuildCount;
        private static readonly object _refsLock = new object();

        /// <summary>Count of real MetadataReference-set builds (disk read + metadata parse). Test seam.</summary>
        internal static long RefBuildCount => System.Threading.Interlocked.Read(ref _refBuildCount);

        // Build the metadata reference set for these paths, or reuse the per-domain cached set when the
        // same (per-domain-stable) assembly-paths array instance is passed again. Cleared on domain
        // reload / ClearCompileCacheForTests via ResetCache().
        private static System.Collections.IList GetOrBuildMetadataReferences(string[] assemblyPaths)
        {
            lock (_refsLock)
            {
                if (_cachedMetadataRefs != null && ReferenceEquals(_cachedRefsPaths, assemblyPaths))
                    return (System.Collections.IList)_cachedMetadataRefs;

                var listType = typeof(List<>).MakeGenericType(_metadataReferenceType);
                var refs = (System.Collections.IList)Activator.CreateInstance(listType);
                foreach (var path in assemblyPaths)
                {
                    try
                    {
                        var cfParams = _createFromFile.GetParameters();
                        var cfArgs = new object[cfParams.Length];
                        cfArgs[0] = path; // string path
                        for (int i = 1; i < cfParams.Length; i++)
                            cfArgs[i] = cfParams[i].HasDefaultValue ? cfParams[i].DefaultValue : null;
                        refs.Add(_createFromFile.Invoke(null, cfArgs));
                    }
                    catch
                    {
                        // Skip assemblies that can't be loaded as metadata
                    }
                }

                _cachedMetadataRefs = refs;
                _cachedRefsPaths = assemblyPaths;
                System.Threading.Interlocked.Increment(ref _refBuildCount);
                return refs;
            }
        }

        public static bool IsAvailable
        {
            get
            {
                if (_isAvailable == null)
                    _isAvailable = Initialize();
                return _isAvailable.Value;
            }
        }

        public static void ResetCache()
        {
            _isAvailable = null;
            lock (_refsLock)
            {
                _cachedMetadataRefs = null;
                _cachedRefsPaths = null;
            }
        }

        private static bool Initialize()
        {
            try
            {
                _syntaxTreeType = Type.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree, Microsoft.CodeAnalysis.CSharp");
                _compilationType = Type.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation, Microsoft.CodeAnalysis.CSharp");
                _compilationOptionsType = Type.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions, Microsoft.CodeAnalysis.CSharp");
                _parseOptionsType = Type.GetType("Microsoft.CodeAnalysis.CSharp.CSharpParseOptions, Microsoft.CodeAnalysis.CSharp");
                _metadataReferenceType = Type.GetType("Microsoft.CodeAnalysis.MetadataReference, Microsoft.CodeAnalysis");
                _outputKindEnum = Type.GetType("Microsoft.CodeAnalysis.OutputKind, Microsoft.CodeAnalysis");
                _languageVersionEnum = Type.GetType("Microsoft.CodeAnalysis.CSharp.LanguageVersion, Microsoft.CodeAnalysis.CSharp");

                if (_syntaxTreeType == null || _compilationType == null || _compilationOptionsType == null ||
                    _parseOptionsType == null || _metadataReferenceType == null || _outputKindEnum == null ||
                    _languageVersionEnum == null)
                    return false;

                // CSharpSyntaxTree.ParseText(string, CSharpParseOptions, string, Encoding, CancellationToken)
                var syntaxTreeBase = Type.GetType("Microsoft.CodeAnalysis.SyntaxTree, Microsoft.CodeAnalysis");
                _parseText = _syntaxTreeType.GetMethod("ParseText", new[] { typeof(string), _parseOptionsType, typeof(string), typeof(Encoding), typeof(System.Threading.CancellationToken) });
                if (_parseText == null)
                    return false;

                // CSharpCompilation.Create(string, IEnumerable<SyntaxTree>, IEnumerable<MetadataReference>, CSharpCompilationOptions)
                var metadataRefBase = _metadataReferenceType;
                var syntaxTreeEnumerable = typeof(IEnumerable<>).MakeGenericType(syntaxTreeBase);
                var metadataRefEnumerable = typeof(IEnumerable<>).MakeGenericType(metadataRefBase);
                _createCompilation = _compilationType.GetMethod("Create", new[] { typeof(string), syntaxTreeEnumerable, metadataRefEnumerable, _compilationOptionsType });
                if (_createCompilation == null)
                    return false;

                // MetadataReference.CreateFromFile(string, MetadataReferenceProperties, DocumentationProvider)
                _createFromFile = _metadataReferenceType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "CreateFromFile");
                if (_createFromFile == null)
                    return false;

                // Emit has no single-param overload; the simplest is
                // Emit(Stream, Stream, Stream, Stream, IEnumerable<ResourceDescription>, EmitOptions, CancellationToken)
                var compilationBase = Type.GetType("Microsoft.CodeAnalysis.Compilation, Microsoft.CodeAnalysis");
                if (compilationBase == null) return false;
                _emit = compilationBase.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "Emit")
                    .OrderBy(m => m.GetParameters().Length)
                    .FirstOrDefault();
                if (_emit == null)
                    return false;

                // Build CSharpParseOptions — constructor has optional params, use reflection
                var latestValue = Enum.Parse(_languageVersionEnum, "Latest");
                var parseOptionsCtor = _parseOptionsType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)[0];
                var parseCtorParams = parseOptionsCtor.GetParameters();
                var parseArgs = new object[parseCtorParams.Length];
                for (int i = 0; i < parseCtorParams.Length; i++)
                {
                    if (parseCtorParams[i].Name == "languageVersion")
                        parseArgs[i] = latestValue;
                    else if (parseCtorParams[i].HasDefaultValue)
                        parseArgs[i] = parseCtorParams[i].DefaultValue;
                    else
                        parseArgs[i] = null;
                }
                _parseOptions = parseOptionsCtor.Invoke(parseArgs);

                // Build CSharpCompilationOptions — use the first constructor (has most defaults)
                var dllKind = Enum.Parse(_outputKindEnum, "DynamicallyLinkedLibrary");
                var compOptionsCtor = _compilationOptionsType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)[0];
                var compCtorParams = compOptionsCtor.GetParameters();
                var compArgs = new object[compCtorParams.Length];
                for (int i = 0; i < compCtorParams.Length; i++)
                {
                    if (compCtorParams[i].Name == "outputKind")
                        compArgs[i] = dllKind;
                    else if (compCtorParams[i].HasDefaultValue)
                        compArgs[i] = compCtorParams[i].DefaultValue;
                    else
                        compArgs[i] = null;
                }
                _compilationOptions = compOptionsCtor.Invoke(compArgs);

                return true;
            }
            catch (Exception e)
            {
                McpLog.Warn($"[ExecuteCode] Roslyn initialization failed: {e.Message}");
                return false;
            }
        }

        public static Assembly Compile(string source, string[] assemblyPaths, out List<string> errors)
        {
            errors = new List<string>();

            try
            {
                // Parse source
                var syntaxTree = _parseText.Invoke(null, new object[] { source, _parseOptions, null, null, default(System.Threading.CancellationToken) });

                // Build (or reuse the per-domain cached) metadata reference set.
                var refs = GetOrBuildMetadataReferences(assemblyPaths);

                // Build syntax tree array
                var syntaxTreeBase = Type.GetType("Microsoft.CodeAnalysis.SyntaxTree, Microsoft.CodeAnalysis");
                var treeArray = Array.CreateInstance(syntaxTreeBase, 1);
                treeArray.SetValue(syntaxTree, 0);

                // Create compilation
                var compilation = _createCompilation.Invoke(null, new object[] { "MCPDynamic", treeArray, refs, _compilationOptions });

                // Emit to memory
                using (var ms = new MemoryStream())
                {
                    // Build args for the Emit overload (fill non-stream params with defaults)
                    var emitParams = _emit.GetParameters();
                    var emitArgs = new object[emitParams.Length];
                    emitArgs[0] = ms; // peStream
                    for (int i = 1; i < emitParams.Length; i++)
                    {
                        if (emitParams[i].HasDefaultValue)
                            emitArgs[i] = emitParams[i].DefaultValue;
                        else
                            emitArgs[i] = null;
                    }
                    var emitResult = _emit.Invoke(compilation, emitArgs);

                    // Check emitResult.Success
                    var successProp = emitResult.GetType().GetProperty("Success");
                    bool success = (bool)successProp.GetValue(emitResult);

                    if (!success)
                    {
                        // Read emitResult.Diagnostics
                        var diagProp = emitResult.GetType().GetProperty("Diagnostics");
                        var diagnostics = (System.Collections.IEnumerable)diagProp.GetValue(emitResult);
                        var severityError = Enum.Parse(Type.GetType("Microsoft.CodeAnalysis.DiagnosticSeverity, Microsoft.CodeAnalysis"), "Error");

                        foreach (var diag in diagnostics)
                        {
                            var sevProp = diag.GetType().GetProperty("Severity");
                            var severity = sevProp.GetValue(diag);
                            if (!severity.Equals(severityError)) continue;

                            var locProp = diag.GetType().GetProperty("Location");
                            var loc = locProp.GetValue(diag);
                            var spanProp = loc.GetType().GetMethod("GetLineSpan");
                            var lineSpan = spanProp.Invoke(loc, null);
                            var startProp = lineSpan.GetType().GetProperty("StartLinePosition");
                            var startPos = startProp.GetValue(lineSpan);
                            var lineProp = startPos.GetType().GetProperty("Line");
                            int line = (int)lineProp.GetValue(startPos);

                            var msgProp = diag.GetType().GetMethod("GetMessage", new[] { typeof(System.Globalization.CultureInfo) });
                            string msg = (string)msgProp.Invoke(diag, new object[] { null });

                            int userLine = Math.Max(1, line + 1 - ExecuteCode.WrapperLineOffset);
                            errors.Add($"Line {userLine}: {msg}");
                        }
                        return null;
                    }

                    ms.Seek(0, SeekOrigin.Begin);
                    return Assembly.Load(ms.ToArray());
                }
            }
            catch (Exception e)
            {
                // Walk to the deepest cause: TargetInvocationException (and friends) wrap the real
                // failure inside .InnerException, and reporting only e.Message hides everything
                // useful (e.g. a missing transitive dep manifests as the generic "Exception has been
                // thrown by the target of an invocation.").
                Exception root = e;
                while (root.InnerException != null) root = root.InnerException;
                errors.Add($"Roslyn compilation error: {root.GetType().Name}: {root.Message}");
                return null;
            }
        }
    }
}
