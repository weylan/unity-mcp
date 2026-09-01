using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Resources.PlayMode;
using MCPForUnity.Editor.Services.PlayMode;
using MCPForUnity.Editor.Tools.PlayMode;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

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

        [Test]
        public void UiScanner_DiscoversPointerClickHandlerWithoutSelectable()
        {
            var target = new GameObject(
                "PointerOnlyTarget",
                typeof(RectTransform),
                typeof(PlayModePointerClickProbe));
            try
            {
                PlayModeUiItem item = PlayModeUiScanner.Scan("ugui")
                    .Single(candidate => candidate.Name == target.name);

                Assert.AreEqual("pointer_handler", item.InteractionSource);
                CollectionAssert.Contains(item.PointerHandlers, "IPointerClickHandler");
                Assert.IsTrue(item.Interactable);
            }
            finally
            {
                Object.DestroyImmediate(target);
            }
        }

        [Test]
        public void UiTextNotContains_RequiresTargetTextToBeAbsentFromAllItems()
        {
            var other = new GameObject("AAA_OtherText", typeof(RectTransform), typeof(Text));
            var target = new GameObject("ZZZ_TargetText", typeof(RectTransform), typeof(Text));
            other.GetComponent<Text>().text = "Settings";
            target.GetComponent<Text>().text = "New Game";
            var condition = new JObject
            {
                ["type"] = "ui_text",
                ["operator"] = "not_contains",
                ["value"] = "New Game",
            };
            try
            {
                Assert.IsFalse(PlayModeConditionEvaluator.Evaluate(condition).Matched);

                target.SetActive(false);

                Assert.IsTrue(PlayModeConditionEvaluator.Evaluate(condition).Matched);
            }
            finally
            {
                Object.DestroyImmediate(other);
                Object.DestroyImmediate(target);
            }
        }

        [Test]
        public void StrictUiClick_RejectsOverlayAndAcceptsOrdinaryTargetChild()
        {
            var eventSystemObject = new GameObject("StrictClickEventSystem", typeof(EventSystem));
            var eventSystem = eventSystemObject.GetComponent<EventSystem>();
            var eventSystems = (List<EventSystem>)typeof(EventSystem)
                .GetField("m_EventSystems", BindingFlags.Static | BindingFlags.NonPublic)
                ?.GetValue(null);
            Assert.IsNotNull(eventSystems);
            eventSystems.Remove(eventSystem);
            eventSystems.Insert(0, eventSystem);
            var raycaster = eventSystemObject.AddComponent<PlayModeDeterministicRaycaster>();
            typeof(BaseRaycaster).GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(raycaster, null);
            Assert.AreSame(eventSystem, EventSystem.current);
            var target = new GameObject(
                "StrictClickTarget",
                typeof(RectTransform),
                typeof(PlayModePointerClickProbe));
            var child = new GameObject("StrictClickChild", typeof(RectTransform));
            child.transform.SetParent(target.transform);
            var nested = new GameObject(
                "StrictClickNestedHandler",
                typeof(RectTransform),
                typeof(PlayModePointerClickProbe));
            nested.transform.SetParent(target.transform);
            var overlay = new GameObject(
                "StrictClickOverlay",
                typeof(RectTransform),
                typeof(PlayModePointerClickProbe));
            var disabled = new GameObject(
                "StrictClickDisabledButton",
                typeof(RectTransform),
                typeof(Image),
                typeof(Button));
            disabled.GetComponent<Button>().interactable = false;
            var targetProbe = target.GetComponent<PlayModePointerClickProbe>();
            var nestedProbe = nested.GetComponent<PlayModePointerClickProbe>();
            var overlayProbe = overlay.GetComponent<PlayModePointerClickProbe>();
            var parameters = new JObject
            {
                ["action"] = "ui_click",
                ["target"] = target.GetInstanceID(),
                ["backend"] = "event_system",
                ["hitTest"] = "strict",
            };
            try
            {
                raycaster.SetHits(overlay, target);
                PlayModeInputResult blocked = PlayModeInputService.Execute(parameters, allowDuringJob: false);

                Assert.IsFalse(blocked.Success);
                Assert.AreEqual("blocked_by_overlay", blocked.Code);
                Assert.AreEqual(0, targetProbe.ClickCount);
                Assert.AreEqual(0, overlayProbe.ClickCount);

                raycaster.SetHits(child);
                PlayModeInputResult clicked = PlayModeInputService.Execute(parameters, allowDuringJob: false);

                Assert.IsTrue(clicked.Success, clicked.Message);
                Assert.AreEqual(1, targetProbe.ClickCount);
                Assert.AreEqual(0, overlayProbe.ClickCount);

                raycaster.SetHits(nested);
                PlayModeInputResult nestedBlocked = PlayModeInputService.Execute(parameters, allowDuringJob: false);

                Assert.IsFalse(nestedBlocked.Success);
                Assert.AreEqual("blocked_by_overlay", nestedBlocked.Code);
                Assert.AreEqual(1, targetProbe.ClickCount);
                Assert.AreEqual(0, nestedProbe.ClickCount);

                parameters["target"] = disabled.GetInstanceID();
                raycaster.SetHits(disabled);
                PlayModeInputResult notInteractable = PlayModeInputService.Execute(parameters, allowDuringJob: false);

                Assert.IsFalse(notInteractable.Success);
                Assert.AreEqual("ui_target_not_interactable", notInteractable.Code);
            }
            finally
            {
                eventSystems.Remove(eventSystem);
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(overlay);
                Object.DestroyImmediate(disabled);
                Object.DestroyImmediate(eventSystemObject);
            }
        }

    }

    public sealed class PlayModePointerClickProbe : MonoBehaviour, IPointerClickHandler
    {
        public int ClickCount { get; private set; }

        public void OnPointerClick(PointerEventData eventData)
        {
            ClickCount++;
        }
    }

    [ExecuteAlways]
    public sealed class PlayModeDeterministicRaycaster : BaseRaycaster
    {
        private readonly List<GameObject> _hits = new();

        public override Camera eventCamera => null;
        public override int sortOrderPriority => 1000;
        public override int renderOrderPriority => 1000;
        public override bool IsActive() => true;

        public void SetHits(params GameObject[] hits)
        {
            _hits.Clear();
            _hits.AddRange(hits);
        }

        public override void Raycast(PointerEventData eventData, List<RaycastResult> resultAppendList)
        {
            for (int index = 0; index < _hits.Count; index++)
            {
                GameObject hit = _hits[index];
                if (hit == null || !hit.activeInHierarchy) continue;
                resultAppendList.Add(new RaycastResult
                {
                    gameObject = hit,
                    module = this,
                    distance = index,
                    index = resultAppendList.Count,
                    sortingOrder = 1000 - index,
                    depth = 1000 - index,
                    screenPosition = eventData.position,
                });
            }
        }
    }
}
