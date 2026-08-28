using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Resources.PlayMode;
using MCPForUnity.Editor.Services.PlayMode;
using MCPForUnity.Editor.Tools.PlayMode;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace MCPForUnityTests.Editor.Tools
{
    public class PlayModeTestingTests
    {
        [Test]
        public void StateResource_OutsidePlayMode_ReturnsStructuredError()
        {
            var response = GetPlayModeState.HandleCommand(new JObject()) as ErrorResponse;

            Assert.IsNotNull(response);
            Assert.AreEqual("play_mode_required", response.Code);
        }

        [Test]
        public void UiResource_OutsidePlayMode_ReturnsStructuredError()
        {
            var response = GetPlayModeUI.HandleCommand(new JObject()) as ErrorResponse;

            Assert.IsNotNull(response);
            Assert.AreEqual("play_mode_required", response.Code);
        }

        [Test]
        public void SimulateInput_OutsidePlayMode_ReturnsStructuredError()
        {
            var response = SimulateInput.HandleCommand(
                new JObject { ["action"] = "release_all" }) as ErrorResponse;

            Assert.IsNotNull(response);
            Assert.AreEqual("play_mode_required", response.Code);
        }

        [TestCase(10.0, "equals", 10.0005, 0.001, true)]
        [TestCase(10.0, "equals", 10.1, 0.001, false)]
        [TestCase(10.0, "gt", 9.0, 0.0, true)]
        [TestCase(10.0, "lte", 10.0, 0.0, true)]
        public void ConditionComparison_NumericOperators(
            double actual,
            string op,
            double expected,
            double tolerance,
            bool matches)
        {
            var result = PlayModeConditionEvaluator.Compare(
                new JValue(actual),
                op,
                new JValue(expected),
                tolerance);

            Assert.AreEqual(matches, result.Matched);
        }

        [Test]
        public void ConditionComparison_StringContains_IsCaseInsensitive()
        {
            var result = PlayModeConditionEvaluator.Compare(
                new JValue("Mission READY"),
                "contains",
                new JValue("ready"),
                0.0);

            Assert.IsTrue(result.Matched);
        }

        [Test]
        public void ConditionComparison_InvalidOperator_ReturnsError()
        {
            var result = PlayModeConditionEvaluator.Compare(
                new JValue(1),
                "approximately_magic",
                new JValue(1),
                0.0);

            Assert.IsFalse(result.Matched);
            Assert.That(result.Error, Does.Contain("operator"));
        }

        [Test]
        public void SequenceValidation_RejectsMoreThanFiftySteps()
        {
            var steps = new JArray();
            for (int i = 0; i < 51; i++)
                steps.Add(new JObject { ["type"] = "delay", ["frames"] = 1 });

            string error = PlayModeTestJobManager.ValidateSequence(steps);

            Assert.That(error, Does.Contain("50"));
        }

        [Test]
        public void SequenceValidation_RejectsUnknownStep()
        {
            var steps = new JArray
            {
                new JObject { ["type"] = "arbitrary_code" }
            };

            string error = PlayModeTestJobManager.ValidateSequence(steps);

            Assert.That(error, Does.Contain("Unknown step type"));
        }

        [Test]
        public void TargetResolver_InfersHierarchyPath()
        {
            var root = new GameObject("PlayModeResolverRoot");
            var child = new GameObject("PlayModeResolverChild");
            child.transform.SetParent(root.transform);
            try
            {
                GameObject resolved = PlayModeTargetResolver.Resolve(
                    new JValue("PlayModeResolverRoot/PlayModeResolverChild"));

                Assert.AreSame(child, resolved);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

    }
}
