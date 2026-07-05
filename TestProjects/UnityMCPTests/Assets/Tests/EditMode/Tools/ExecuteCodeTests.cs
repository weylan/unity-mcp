using Newtonsoft.Json.Linq;
using NUnit.Framework;
using MCPForUnity.Editor.Tools;
using static MCPForUnityTests.Editor.TestUtilities;

namespace MCPForUnityTests.Editor.Tools
{
    public class ExecuteCodeTests
    {
        [SetUp]
        public void SetUp()
        {
            ExecuteCode.HandleCommand(new JObject { ["action"] = "clear_history" });
            // The compile cache persists across calls within a domain; clear it between tests so
            // CompileCount deltas are deterministic and one test's cached snippet does not turn a
            // later test's first call into a cache hit.
            ExecuteCode.ClearCompileCacheForTests();
        }

        // ──────────────────── Compile cache ────────────────────

        [Test]
        public void Execute_IdenticalCode_CompilesOnce()
        {
            long before = ExecuteCode.CompileCount;

            var r1 = Execute("return 11 + 22;");
            var r2 = Execute("return 11 + 22;");

            Assert.IsTrue(r1.Value<bool>("success"), r1.ToString());
            Assert.IsTrue(r2.Value<bool>("success"), r2.ToString());
            Assert.AreEqual(33, r2["data"]["result"].Value<int>());
            Assert.AreEqual(1, ExecuteCode.CompileCount - before,
                "Identical source should compile once; the second call must hit the cache.");
        }

        [Test]
        public void Execute_SameCodeWithDifferentArgs_CompilesOnce()
        {
            long before = ExecuteCode.CompileCount;
            const string code = "return __mcpArgs[0];";

            var r1 = Execute(code, new JArray("first"));
            var r2 = Execute(code, new JArray("second"));

            Assert.IsTrue(r1.Value<bool>("success"), r1.ToString());
            Assert.IsTrue(r2.Value<bool>("success"), r2.ToString());
            Assert.AreEqual("second", r2["data"]["result"].Value<string>());
            Assert.AreEqual(1, ExecuteCode.CompileCount - before,
                "Args must not participate in the compile-cache key.");
        }

        [Test]
        public void Execute_DifferentCode_CompilesEachTime()
        {
            long before = ExecuteCode.CompileCount;

            Execute("return 101;");
            Execute("return 202;");

            Assert.AreEqual(2, ExecuteCode.CompileCount - before,
                "Distinct sources must each trigger a compile (no false cache hit).");
        }

        [Test]
        public void Execute_AfterCacheClear_Recompiles()
        {
            long before = ExecuteCode.CompileCount;

            Execute("return 303;");
            ExecuteCode.ClearCompileCacheForTests();
            Execute("return 303;");

            Assert.AreEqual(2, ExecuteCode.CompileCount - before,
                "Clearing the cache (domain-reload equivalent) must force a recompile of the same source.");
        }

        [Test]
        public void Execute_SafetyChecks_NotBypassedByCache()
        {
            // A method-group reference to a blocked API contains the blocked substring but is never
            // invoked, so it is harmless to run with safety off — yet it still caches an assembly.
            const string danger = "var d = (System.Action<string>)System.IO.File.Delete; return 1;";

            var off = Execute(danger, safetyChecks: false);
            Assert.IsTrue(off.Value<bool>("success"), off.ToString());

            // Same source with safety on must still be blocked: the safety gate runs before the
            // compile/cache lookup, so a cached assembly can never bypass it.
            var on = Execute(danger, safetyChecks: true);
            Assert.IsFalse(on.Value<bool>("success"), on.ToString());
            StringAssert.Contains("Blocked", on.Value<string>("error") ?? on["error"]?.ToString() ?? string.Empty);
        }

        // ──────────────────── MetadataReference cache (per-domain) ────────────────────

        // The reference set (MetadataReference.CreateFromFile over every loaded assembly) is the
        // dominant cost of a cache-miss compile, so it must be built once per domain and reused
        // across distinct snippets. RefBuildCount is a Roslyn-path-only seam.
        [Test]
        public void Execute_DistinctSources_ReuseMetadataReferenceSet()
        {
            if (!RoslynAvailable())
                Assert.Ignore("Roslyn backend not installed here; the MetadataReference cache is Roslyn-only.");

            ExecuteCode.ClearCompileCacheForTests();
            long beforeCompile = ExecuteCode.CompileCount;
            long beforeRefs = ExecuteCode.RefBuildCount;

            Assert.IsTrue(ExecuteWithCompiler("return 1001;", "roslyn").Value<bool>("success"));
            Assert.IsTrue(ExecuteWithCompiler("return 2002;", "roslyn").Value<bool>("success"));

            Assert.AreEqual(2, ExecuteCode.CompileCount - beforeCompile,
                "Two distinct sources each compile (no false cache hit).");
            Assert.AreEqual(1, ExecuteCode.RefBuildCount - beforeRefs,
                "The metadata reference set must be built once per domain and reused across distinct snippets.");
        }

        [Test]
        public void Execute_AfterCacheClear_RebuildsMetadataReferenceSet()
        {
            if (!RoslynAvailable())
                Assert.Ignore("Roslyn backend not installed here; the MetadataReference cache is Roslyn-only.");

            ExecuteCode.ClearCompileCacheForTests();
            long beforeRefs = ExecuteCode.RefBuildCount;

            ExecuteWithCompiler("return 7;", "roslyn");            // miss -> build refs
            ExecuteCode.ClearCompileCacheForTests();                // domain-reload equivalent
            ExecuteWithCompiler("return 7;", "roslyn");            // must rebuild the reference set

            Assert.AreEqual(2, ExecuteCode.RefBuildCount - beforeRefs,
                "Clearing the cache (domain-reload equivalent) must rebuild the reference set.");
        }

        // ──────────────────── Execute: success cases ────────────────────

        [Test]
        public void Execute_ReturnString_ReturnsSuccess()
        {
            var result = Execute("return \"hello\";");

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual("hello", result["data"]["result"].Value<string>());
        }

        [Test]
        public void Execute_WithArgs_PassesStringArrayToSnippet()
        {
            var result = Execute("return string.Join(\"|\", __mcpArgs);", new JArray("alpha", 7, "omega"));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual("alpha|7|omega", result["data"]["result"].Value<string>());
        }

        [Test]
        public void Execute_NoArgs_AllowsExistingLocalVariableNamedArgs()
        {
            var result = Execute("var args = new[] { \"local\" }; return args[0];");

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual("local", result["data"]["result"].Value<string>());
        }

        [Test]
        public void Execute_ReturnInt_ReturnsSuccess()
        {
            var result = Execute("return 42;");

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual(42, result["data"]["result"].Value<int>());
        }

        [Test]
        public void Execute_ReturnNull_NoResultValue()
        {
            var result = Execute("int x = 1; return null;");

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            // data may contain compiler info but should not have a "result" key
            var data = result["data"] as JObject;
            if (data != null)
                Assert.IsNull(data["result"], "Expected no 'result' key when code returns null");
        }

        [Test]
        public void Execute_VoidReturn_Succeeds()
        {
            var result = Execute("UnityEngine.Debug.Log(\"test\"); return null;");

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
        }

        [Test]
        public void Execute_UnityAPI_CanAccessSceneManager()
        {
            var result = Execute(
                "var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();\n" +
                "return scene.name;");

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.IsNotNull(result["data"]["result"]);
        }

        [Test]
        public void Execute_Generics_ListOfString()
        {
            var result = Execute(
                "var list = new System.Collections.Generic.List<string>();\n" +
                "list.Add(\"a\"); list.Add(\"b\");\n" +
                "return list;");

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            var arr = result["data"]["result"] as JArray;
            Assert.IsNotNull(arr, "Expected array result");
            Assert.AreEqual(2, arr.Count);
        }

        [Test]
        public void Execute_LINQ_SelectWorks()
        {
            var result = Execute(
                "var nums = new int[] { 1, 2, 3 };\n" +
                "return nums.Select(n => n * 2).ToList();");

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            var arr = result["data"]["result"] as JArray;
            Assert.IsNotNull(arr);
            Assert.AreEqual(3, arr.Count);
            Assert.AreEqual(2, arr[0].Value<int>());
            Assert.AreEqual(6, arr[2].Value<int>());
        }

        [Test]
        public void Execute_Dictionary_ReturnsStructured()
        {
            var result = Execute(
                "var dict = new Dictionary<string, int> { { \"a\", 1 }, { \"b\", 2 } };\n" +
                "return dict;");

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.IsNotNull(result["data"]["result"]);
        }

        // ──────────────────── Execute: error cases ────────────────────

        [Test]
        public void Execute_CompilationError_ReturnsErrors()
        {
            var result = Execute("int x = \"not an int\";");

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            StringAssert.Contains("Compilation failed", result.Value<string>("error"));
            Assert.IsNotNull(result["data"]["errors"]);
        }

        [Test]
        public void Execute_RuntimeException_ReturnsError()
        {
            var result = Execute("throw new System.Exception(\"boom\");");

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            StringAssert.Contains("boom", result.Value<string>("error"));
        }

        [Test]
        public void Execute_MissingCode_ReturnsError()
        {
            var result = ToJObject(ExecuteCode.HandleCommand(new JObject
            {
                ["action"] = "execute"
            }));

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            StringAssert.Contains("code", result.Value<string>("error").ToLowerInvariant());
        }

        [Test]
        public void Execute_EmptyCode_ReturnsError()
        {
            var result = ToJObject(ExecuteCode.HandleCommand(new JObject
            {
                ["action"] = "execute",
                ["code"] = "   "
            }));

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
        }

        // ──────────────────── Safety checks ────────────────────

        [Test]
        public void Execute_SafetyChecks_BlocksFileDelete()
        {
            var result = Execute("System.IO.File.Delete(\"x\");");

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            StringAssert.Contains("Blocked pattern", result.Value<string>("error"));
        }

        [Test]
        public void Execute_SafetyChecks_BlocksProcessStart()
        {
            var result = Execute("Process.Start(\"cmd\");");

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            StringAssert.Contains("Blocked pattern", result.Value<string>("error"));
        }

        [Test]
        public void Execute_SafetyChecks_BlocksInfiniteLoop()
        {
            var result = Execute("while (true) { }");

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            StringAssert.Contains("Blocked pattern", result.Value<string>("error"));
        }

        [Test]
        public void Execute_SafetyChecksDisabled_AllowsBlockedPattern()
        {
            var result = ToJObject(ExecuteCode.HandleCommand(new JObject
            {
                ["action"] = "execute",
                ["code"] = "while (true) { break; }  return null;",
                ["safety_checks"] = false
            }));

            if (!result.Value<bool>("success"))
            {
                var error = result.Value<string>("error") ?? "";
                Assert.IsFalse(error.Contains("Blocked pattern"),
                    "Safety checks should be disabled but still blocked");
            }
        }

        // ──────────────────── History ────────────────────

        [Test]
        public void GetHistory_Empty_ReturnsZero()
        {
            var result = ToJObject(ExecuteCode.HandleCommand(new JObject
            {
                ["action"] = "get_history"
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual(0, result["data"]["total"].Value<int>());
        }

        [Test]
        public void GetHistory_AfterExecution_RecordsEntry()
        {
            Execute("return 1;");

            var result = ToJObject(ExecuteCode.HandleCommand(new JObject
            {
                ["action"] = "get_history"
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual(1, result["data"]["total"].Value<int>());
            var entries = result["data"]["entries"] as JArray;
            Assert.IsNotNull(entries);
            Assert.AreEqual(1, entries.Count);
            Assert.IsTrue(entries[0]["success"].Value<bool>());
        }

        [Test]
        public void GetHistory_Limit_RespectsParameter()
        {
            Execute("return 1;");
            Execute("return 2;");
            Execute("return 3;");

            var result = ToJObject(ExecuteCode.HandleCommand(new JObject
            {
                ["action"] = "get_history",
                ["limit"] = 2
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual(3, result["data"]["total"].Value<int>());
            var entries = result["data"]["entries"] as JArray;
            Assert.AreEqual(2, entries.Count);
        }

        [Test]
        public void ClearHistory_RemovesAll()
        {
            Execute("return 1;");
            Execute("return 2;");

            var clearResult = ToJObject(ExecuteCode.HandleCommand(new JObject
            {
                ["action"] = "clear_history"
            }));
            Assert.IsTrue(clearResult.Value<bool>("success"), clearResult.ToString());

            var historyResult = ToJObject(ExecuteCode.HandleCommand(new JObject
            {
                ["action"] = "get_history"
            }));
            Assert.AreEqual(0, historyResult["data"]["total"].Value<int>());
        }

        // ──────────────────── Replay ────────────────────

        [Test]
        public void Replay_ValidIndex_ReExecutes()
        {
            Execute("return 42;");

            var result = ToJObject(ExecuteCode.HandleCommand(new JObject
            {
                ["action"] = "replay",
                ["index"] = 0
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual(42, result["data"]["result"].Value<int>());
        }

        [Test]
        public void Replay_WithArgs_ReExecutesWithOriginalArgs()
        {
            var initial = Execute("return __mcpArgs[0];", new JArray("from-history"));
            Assert.IsTrue(initial.Value<bool>("success"), initial.ToString());

            var result = ToJObject(ExecuteCode.HandleCommand(new JObject
            {
                ["action"] = "replay",
                ["index"] = 0
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual("from-history", result["data"]["result"].Value<string>());
        }

        [Test]
        public void Replay_AfterSnippetMutatesArgs_ReplaysOriginalValues()
        {
            const string code = "var v = __mcpArgs[0]; __mcpArgs[0] = \"polluted\"; return v;";
            var initial = Execute(code, new JArray("orig"));
            Assert.IsTrue(initial.Value<bool>("success"), initial.ToString());
            Assert.AreEqual("orig", initial["data"]["result"].Value<string>());

            var result = ToJObject(ExecuteCode.HandleCommand(new JObject
            {
                ["action"] = "replay",
                ["index"] = 0
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual("orig", result["data"]["result"].Value<string>());
        }

        [Test]
        public void Replay_InvalidIndex_ReturnsError()
        {
            Execute("return 1;");

            var result = ToJObject(ExecuteCode.HandleCommand(new JObject
            {
                ["action"] = "replay",
                ["index"] = 99
            }));

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            StringAssert.Contains("Invalid history index", result.Value<string>("error"));
        }

        [Test]
        public void Replay_EmptyHistory_ReturnsError()
        {
            var result = ToJObject(ExecuteCode.HandleCommand(new JObject
            {
                ["action"] = "replay",
                ["index"] = 0
            }));

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
        }

        // ──────────────────── Action validation ────────────────────

        [Test]
        public void UnknownAction_ReturnsError()
        {
            var result = ToJObject(ExecuteCode.HandleCommand(new JObject
            {
                ["action"] = "invalid_action"
            }));

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            StringAssert.Contains("Unknown action", result.Value<string>("error"));
        }

        [Test]
        public void NullParams_ReturnsError()
        {
            var result = ToJObject(ExecuteCode.HandleCommand(null));

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
        }

        // ──────────────────── CodeDom backend ────────────────────

        // Regression for CoplayDev/unity-mcp#1144: large projects (~100+ asmdefs) blew past the
        // Windows 32 KB CreateProcess limit because every reference became an inline /r: flag.
        // The fix routes references through a @responsefile, so this just verifies that the
        // codedom path still compiles and runs end-to-end.
        [Test]
        public void Execute_CodedomBackend_CompilesAndRuns()
        {
            var result = ToJObject(ExecuteCode.HandleCommand(new JObject
            {
                ["action"] = "execute",
                ["code"] = "return 1 + 1;",
                ["compiler"] = "codedom"
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual(2, result["data"]["result"].Value<int>());
            Assert.AreEqual("codedom", result["data"]["compiler"].Value<string>());
        }

        [Test]
        public void Execute_CodedomBackend_PassesArgs()
        {
            var result = ExecuteWithCompiler("return __mcpArgs[0] + \":\" + __mcpArgs.Length;", "codedom", new JArray("cd"));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.AreEqual("cd:1", result["data"]["result"].Value<string>());
            Assert.AreEqual("codedom", result["data"]["compiler"].Value<string>());
        }

        [Test]
        public void Execute_CodedomBackend_ResolvesUnityTypes()
        {
            var result = ToJObject(ExecuteCode.HandleCommand(new JObject
            {
                ["action"] = "execute",
                ["code"] = "return UnityEngine.Application.unityVersion;",
                ["compiler"] = "codedom"
            }));

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.IsNotNull(result["data"]["result"]);
        }

        // ──────────────────── Helpers ────────────────────

        private static JObject Execute(string code)
        {
            return ToJObject(ExecuteCode.HandleCommand(new JObject
            {
                ["action"] = "execute",
                ["code"] = code
            }));
        }

        private static JObject Execute(string code, JArray args)
        {
            return ToJObject(ExecuteCode.HandleCommand(new JObject
            {
                ["action"] = "execute",
                ["code"] = code,
                ["args"] = args
            }));
        }

        private static JObject Execute(string code, bool safetyChecks)
        {
            return ToJObject(ExecuteCode.HandleCommand(new JObject
            {
                ["action"] = "execute",
                ["code"] = code,
                ["safety_checks"] = safetyChecks
            }));
        }

        private static JObject ExecuteWithCompiler(string code, string compiler)
        {
            return ToJObject(ExecuteCode.HandleCommand(new JObject
            {
                ["action"] = "execute",
                ["code"] = code,
                ["compiler"] = compiler
            }));
        }

        private static JObject ExecuteWithCompiler(string code, string compiler, JArray args)
        {
            return ToJObject(ExecuteCode.HandleCommand(new JObject
            {
                ["action"] = "execute",
                ["code"] = code,
                ["compiler"] = compiler,
                ["args"] = args
            }));
        }

        // A roslyn execute that fails with a "Roslyn ... not available" error means the compiler DLLs
        // are absent in this test project; the RefBuildCount seam only fires on the Roslyn path.
        private static bool RoslynAvailable()
        {
            var r = ExecuteWithCompiler("return 0;", "roslyn");
            if (r.Value<bool>("success")) return true;
            return !(r.Value<string>("error") ?? "").Contains("Roslyn");
        }

    }
}
