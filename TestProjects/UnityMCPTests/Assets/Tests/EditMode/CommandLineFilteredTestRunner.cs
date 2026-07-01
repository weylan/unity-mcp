using System;
using System.IO;
using System.Linq;
using System.Xml;
using NUnit.Framework.Interfaces;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.TestTools.TestRunner.GUI;

namespace MCPForUnityTests.Editor
{
    public static class CommandLineFilteredTestRunner
    {
        private static TestRunnerApi _api;
        private static ResultCallback _callback;

        public static void Run()
        {
            string[] args = Environment.GetCommandLineArgs();
            string resultPath = GetArgValue(args, "-filteredTestResults")
                ?? Path.GetFullPath("FilteredTestResults.xml");
            string pattern = GetArgValue(args, "-filteredTestPattern")
                ?? "MCPForUnityTests.Editor.Services";

            _callback = new ResultCallback(resultPath);
            _api = ScriptableObject.CreateInstance<TestRunnerApi>();
            _api.RegisterCallbacks(_callback);

            var filter = new Filter
            {
                testMode = TestMode.EditMode,
                assemblyNames = new[] { "MCPForUnityTests.EditMode" },
                groupNames = new[] { pattern }
            };
            var settings = new ExecutionSettings(filter);

            Debug.Log("[CommandLineFilteredTestRunner] Running EditMode tests matching: " + pattern);
            _api.Execute(settings);
        }

        private static string GetArgValue(string[] args, string name)
        {
            int index = Array.IndexOf(args, name);
            if (index < 0 || index + 1 >= args.Length)
            {
                return null;
            }

            return args[index + 1];
        }

        private sealed class ResultCallback : ICallbacks
        {
            private readonly string _resultPath;

            public ResultCallback(string resultPath)
            {
                _resultPath = resultPath;
            }

            public bool Completed { get; private set; }
            public int ExitCode { get; private set; } = 3;

            public void RunStarted(ITestAdaptor testsToRun)
            {
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                Completed = true;
                int total = result.PassCount + result.FailCount + result.SkipCount + result.InconclusiveCount;
                ExitCode = result.FailCount > 0 || total == 0 ? 1 : 0;
                WriteResult(result, total);
                Debug.LogFormat(
                    "[CommandLineFilteredTestRunner] Result={0} total={1} passed={2} failed={3} skipped={4} inconclusive={5}",
                    result.ResultState,
                    total,
                    result.PassCount,
                    result.FailCount,
                    result.SkipCount,
                    result.InconclusiveCount);
                EditorApplication.Exit(ExitCode);
            }

            public void TestStarted(ITestAdaptor test)
            {
            }

            public void TestFinished(ITestResultAdaptor result)
            {
            }

            private void WriteResult(ITestResultAdaptor result, int total)
            {
                string directory = Path.GetDirectoryName(_resultPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var settings = new XmlWriterSettings { Indent = true };
                using (var writer = XmlWriter.Create(_resultPath, settings))
                {
                    writer.WriteStartDocument();
                    writer.WriteStartElement("test-run");
                    writer.WriteAttributeString("result", result.ResultState);
                    writer.WriteAttributeString("total", total.ToString());
                    writer.WriteAttributeString("passed", result.PassCount.ToString());
                    writer.WriteAttributeString("failed", result.FailCount.ToString());
                    writer.WriteAttributeString("skipped", result.SkipCount.ToString());
                    writer.WriteAttributeString("inconclusive", result.InconclusiveCount.ToString());

                    TNode node = result.ToXml();
                    if (node != null)
                    {
                        node.WriteTo(writer);
                    }

                    writer.WriteEndElement();
                    writer.WriteEndDocument();
                }
            }
        }
    }
}
