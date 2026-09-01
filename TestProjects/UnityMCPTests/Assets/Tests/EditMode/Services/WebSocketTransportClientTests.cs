using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using MCPForUnity.Editor.Services.Transport.Transports;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services
{
    [TestFixture]
    public class WebSocketTransportClientTests
    {
        private const string CandidateBuilderMethodName = "BuildConnectionCandidateUris";
        private const string MarkConnectedAfterStartMethodName = "MarkConnectedAfterStart";
        private const string WebSocketTransportClientTypeName = "MCPForUnity.Editor.Services.Transport.Transports.WebSocketTransportClient";
        private static readonly MethodInfo BuildConnectionCandidateUrisMethod = ResolveCandidateBuilderMethod();

        [Test]
        public void BuildConnectionCandidateUris_NullEndpoint_ReturnsEmptyList()
        {
            // Act
            List<Uri> candidates = InvokeBuildConnectionCandidateUris(null);

            // Assert
            Assert.IsNotNull(candidates);
            Assert.AreEqual(0, candidates.Count);
        }

        [Test]
        public void BuildConnectionCandidateUris_NonLocalhost_ReturnsOriginalOnly()
        {
            // Arrange
            var endpoint = new Uri("ws://127.0.0.1:8080/hub/plugin");

            // Act
            List<Uri> candidates = InvokeBuildConnectionCandidateUris(endpoint);

            // Assert
            Assert.AreEqual(1, candidates.Count);
            Assert.AreEqual(endpoint, candidates[0]);
        }

        [Test]
        public void BuildConnectionCandidateUris_Localhost_AddsIPv4AndIPv6Fallbacks()
        {
            // Arrange
            var endpoint = new Uri("ws://localhost:8080/hub/plugin");

            // Act
            List<Uri> candidates = InvokeBuildConnectionCandidateUris(endpoint);

            // Assert
            Assert.AreEqual(3, candidates.Count);
            CollectionAssert.AreEqual(
                new[] { "localhost", "127.0.0.1", "::1" },
                candidates.Select(uri => NormalizeHostForComparison(uri.Host)).ToArray());

            int uniqueCount = candidates
                .Select(uri => uri.AbsoluteUri)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            Assert.AreEqual(candidates.Count, uniqueCount, "Fallback list should not contain duplicate endpoints.");
        }

        [Test]
        public void BuildConnectionCandidateUris_LocalhostFallbacks_PreserveSchemePortPathAndQuery()
        {
            // Arrange
            var endpoint = new Uri("wss://localhost:9443/custom/path?mode=test");

            // Act
            List<Uri> candidates = InvokeBuildConnectionCandidateUris(endpoint);

            // Assert
            Assert.AreEqual(3, candidates.Count);
            foreach (Uri candidate in candidates)
            {
                Assert.AreEqual("wss", candidate.Scheme);
                Assert.AreEqual(9443, candidate.Port);
                Assert.AreEqual("/custom/path", candidate.AbsolutePath);
                Assert.AreEqual("?mode=test", candidate.Query);
            }
        }

        [Test]
        public void MarkConnectedAfterStart_RegisteredBeforeStartResumes_PreservesAssignedSessionId()
        {
            // Arrange
            var client = new WebSocketTransportClient();
            SetPrivateField(client, "_endpointUri", new Uri("ws://127.0.0.1:8080/hub/plugin"));
            SetPrivateField(client, "_sessionId", "session-from-register");

            // Act
            InvokeMarkConnectedAfterStart(client);

            // Assert
            Assert.IsTrue(client.State.IsConnected);
            Assert.AreEqual("session-from-register", client.State.SessionId);
            Assert.AreEqual("ws://127.0.0.1:8080/hub/plugin", client.State.Details);
        }

        [Test]
        public void MarkConnectedAfterStart_NoRegisteredSession_UsesPendingMarker()
        {
            // Arrange
            var client = new WebSocketTransportClient();
            SetPrivateField(client, "_endpointUri", new Uri("ws://127.0.0.1:8080/hub/plugin"));

            // Act
            InvokeMarkConnectedAfterStart(client);

            // Assert
            Assert.IsTrue(client.State.IsConnected);
            Assert.AreEqual("pending", client.State.SessionId);
        }

        [Test]
        public void BuildRegisterPayload_IncludesExactProjectRootAndCurrentProcessId()
        {
            var client = new WebSocketTransportClient();
            string projectRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ExactProject"));
            SetPrivateField(client, "_projectName", "ExactProject");
            SetPrivateField(client, "_projectHash", "hash-exact");
            SetPrivateField(client, "_unityVersion", "2022.3");
            SetPrivateField(client, "_projectPath", projectRoot);

            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            MethodInfo method = typeof(WebSocketTransportClient).GetMethod(
                "BuildRegisterPayload",
                flags,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            Assert.IsNotNull(method, "Registration identity must be built in one testable production method.");
            var payload = method.Invoke(client, Array.Empty<object>()) as JObject;

            Assert.IsNotNull(payload);
            Assert.AreEqual(Process.GetCurrentProcess().Id, payload.Value<int>("process_id"));
            Assert.AreEqual(projectRoot, payload.Value<string>("project_path"));
            Assert.AreEqual("hash-exact", payload.Value<string>("project_hash"));
        }

        private static List<Uri> InvokeBuildConnectionCandidateUris(Uri endpoint)
        {
            if (BuildConnectionCandidateUrisMethod == null)
            {
                Assert.Fail(BuildMissingMethodDiagnostic());
            }
            var result = BuildConnectionCandidateUrisMethod.Invoke(null, new object[] { endpoint });
            Assert.IsNotNull(result);
            Assert.IsInstanceOf<List<Uri>>(result);
            return (List<Uri>)result;
        }

        private static void InvokeMarkConnectedAfterStart(WebSocketTransportClient client)
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            MethodInfo method = typeof(WebSocketTransportClient).GetMethod(MarkConnectedAfterStartMethodName, flags);
            Assert.IsNotNull(method, $"Expected private method {MarkConnectedAfterStartMethodName} to exist.");
            method.Invoke(client, Array.Empty<object>());
        }

        private static void SetPrivateField(WebSocketTransportClient client, string fieldName, object value)
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            FieldInfo field = typeof(WebSocketTransportClient).GetField(fieldName, flags);
            Assert.IsNotNull(field, $"Expected private field {fieldName} to exist.");
            field.SetValue(client, value);
        }

        private static MethodInfo ResolveCandidateBuilderMethod()
        {
            MethodInfo direct = GetCandidateBuilderMethod(typeof(WebSocketTransportClient));
            if (direct != null)
            {
                return direct;
            }

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type candidateType = assembly.GetType(WebSocketTransportClientTypeName);
                if (candidateType == null)
                {
                    continue;
                }

                MethodInfo method = GetCandidateBuilderMethod(candidateType);
                if (method != null)
                {
                    return method;
                }
            }

            return null;
        }

        private static MethodInfo GetCandidateBuilderMethod(Type type)
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
            MethodInfo direct = type.GetMethod(
                CandidateBuilderMethodName,
                flags,
                binder: null,
                types: new[] { typeof(Uri) },
                modifiers: null);
            if (direct != null)
            {
                return direct;
            }

            // Fallback for environments where signature binding can differ between loaded copies.
            return type.GetMethods(flags).FirstOrDefault(method =>
            {
                if (!string.Equals(method.Name, CandidateBuilderMethodName, StringComparison.Ordinal))
                {
                    return false;
                }

                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == typeof(Uri);
            });
        }

        private static string BuildMissingMethodDiagnostic()
        {
            var sb = new StringBuilder();
            sb.Append("Expected private candidate builder method to exist. Searched loaded assemblies for ")
              .Append(WebSocketTransportClientTypeName)
              .Append('.')
              .Append(CandidateBuilderMethodName)
              .Append(". Loaded candidate types:");

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type candidateType = assembly.GetType(WebSocketTransportClientTypeName);
                if (candidateType == null)
                {
                    continue;
                }

                sb.Append("\n- ")
                  .Append(assembly.FullName)
                  .Append(" @ ")
                  .Append(string.IsNullOrEmpty(assembly.Location) ? "<dynamic>" : assembly.Location);
            }

            return sb.ToString();
        }

        private static string NormalizeHostForComparison(string host)
        {
            if (string.IsNullOrEmpty(host))
            {
                return host;
            }

            return host.Trim('[', ']');
        }
    }
}
