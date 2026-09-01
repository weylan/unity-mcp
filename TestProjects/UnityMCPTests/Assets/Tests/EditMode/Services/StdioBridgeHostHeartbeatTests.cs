using System;
using System.Diagnostics;
using System.Reflection;
using MCPForUnity.Editor.Services.Transport.Transports;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace MCPForUnityTests.Editor.Services
{
    [TestFixture]
    public class StdioBridgeHostHeartbeatTests
    {
        [Test]
        public void BuildHeartbeatPayload_IncludesAssetsPathAndCurrentProcessId()
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            MethodInfo method = typeof(StdioBridgeHost).GetMethod(
                "BuildHeartbeatPayload",
                flags,
                binder: null,
                types: new[] { typeof(bool), typeof(string) },
                modifiers: null);
            Assert.IsNotNull(method, "Heartbeat identity must be built without starting the host.");

            var payload = method.Invoke(null, new object[] { false, "ready" }) as JObject;

            Assert.IsNotNull(payload);
            Assert.AreEqual(Process.GetCurrentProcess().Id, payload.Value<int>("process_id"));
            Assert.AreEqual(Application.dataPath, payload.Value<string>("project_path"));
            Assert.AreEqual("ready", payload.Value<string>("reason"));
        }
    }
}
