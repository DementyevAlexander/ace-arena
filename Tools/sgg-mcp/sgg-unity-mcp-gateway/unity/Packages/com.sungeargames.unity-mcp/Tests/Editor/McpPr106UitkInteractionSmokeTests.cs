using System.Collections.Generic;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Commands;
using UnityEditor;
using UnityEngine.UIElements;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class McpPr106UitkInteractionSmokeTests
    {
        [Test]
        public void ExactNameResolutionRejectsDuplicatesAndAcceptsUniqueNames()
        {
            var root = new VisualElement { name = "Root" };
            var first = new Button { name = "Play" };
            var duplicate = new Toggle { name = "Play" };
            var unique = new TextField { name = "Name" };
            root.Add(first);
            root.Add(duplicate);
            root.Add(unique);

            Assert.That(UitkInteractionCommands.TryResolveNamedControlForTests(root, "Play", out _, out var duplicateReason), Is.False);
            Assert.That(duplicateReason, Does.Contain("ambiguous"));
            Assert.That(UitkInteractionCommands.TryResolveNamedControlForTests(root, "Name", out var resolved, out var uniqueReason), Is.True);
            Assert.That(resolved, Is.SameAs(unique));
            Assert.That(uniqueReason, Is.Empty);
            Assert.That(UitkInteractionCommands.VisualHierarchyPathForTests(root, unique), Does.Contain("Name"));
        }

        [Test]
        public void ActionValidationAllowsOnlyTypedCompatibleControls()
        {
            var button = new Button { name = "Button" };
            var toggle = new Toggle { name = "Toggle" };
            var text = new TextField { name = "Text" };
            var dropdown = new DropdownField("Dropdown", new List<string> { "One", "Two" }, 0) { name = "Dropdown" };
            var unsupported = new VisualElement { name = "Unsupported" };

            Assert.That(UitkInteractionCommands.ValidateActionCompatibilityForTests(button, "click", "Button", -1), Is.Empty);
            Assert.That(UitkInteractionCommands.ValidateActionCompatibilityForTests(toggle, "click", string.Empty, -1), Does.Contain("only with Button"));
            Assert.That(UitkInteractionCommands.ValidateActionCompatibilityForTests(text, "set_text", "Toggle", -1), Does.Contain("expected_control_type"));
            Assert.That(UitkInteractionCommands.ValidateActionCompatibilityForTests(dropdown, "set_dropdown_index", "DropdownField", 1), Is.Empty);
            var requestedChoice = UitkInteractionCommands.RequestedDropdownValueJsonForTests(dropdown, 1);
            Assert.That(UitkInteractionCommands.RevalidateRequestedDropdownValueForTests(dropdown, 1, requestedChoice, out var unchangedReason), Is.True);
            Assert.That(unchangedReason, Is.Empty);
            dropdown.choices[1] = "Renamed";
            Assert.That(UitkInteractionCommands.RevalidateRequestedDropdownValueForTests(dropdown, 1, requestedChoice, out var driftReason), Is.False);
            Assert.That(driftReason, Does.Contain("drifted after preflight"));
            Assert.That(UitkInteractionCommands.ValidateActionCompatibilityForTests(dropdown, "set_dropdown_index", string.Empty, 2), Does.Contain("existing"));
            dropdown.choices.RemoveAt(1);
            Assert.That(UitkInteractionCommands.ValidateActionCompatibilityForTests(dropdown, "set_dropdown_index", "DropdownField", 1), Does.Contain("existing"));
            Assert.That(UitkInteractionCommands.ValidateActionCompatibilityForTests(unsupported, "focus", string.Empty, -1), Does.Contain("not allowlisted"));
        }

        [Test]
        public void ButtonSubmitUsesTypedNavigationEventAndInvokesClickedCallback()
        {
            var button = new Button { name = "Submit" };
            var clicked = 0;
            button.clicked += () => clicked++;
            var window = EditorWindow.CreateWindow<EditorWindow>();
            try
            {
                window.rootVisualElement.Add(button);
                window.Show();

                Assert.That(UitkInteractionCommands.ExecuteTypedValueForTests(button, "click", false, string.Empty, -1, out var exceptionType, out var exceptionMessage), Is.True);
                Assert.That(clicked, Is.EqualTo(1));
                Assert.That(exceptionType, Is.Empty);
                Assert.That(exceptionMessage, Is.Empty);
            }
            finally
            {
                window.Close();
            }
        }

        [Test]
        public void CallbackExceptionsAreBoundedAndReportedAsPartialSideEffects()
        {
            var button = new Button { name = "Throws" };
            button.clicked += () => throw new System.InvalidOperationException("bounded callback failure");
            var window = EditorWindow.CreateWindow<EditorWindow>();
            try
            {
                window.rootVisualElement.Add(button);
                window.Show();

                Assert.That(UitkInteractionCommands.ExecuteTypedValueForTests(button, "click", false, string.Empty, -1, out var exceptionType, out var exceptionMessage), Is.False);
                Assert.That(exceptionType, Does.Contain("InvalidOperationException"));
                Assert.That(exceptionMessage, Is.EqualTo("bounded callback failure"));
            }
            finally
            {
                window.Close();
            }
        }

        [Test]
        public void ToggleTextAndDropdownUseTypedMutationAndExposeRestorationValues()
        {
            var toggle = new Toggle { name = "Enabled" };
            toggle.value = false;
            var text = new TextField { name = "Title" };
            text.value = "before";
            var dropdown = new DropdownField("Choice", new List<string> { "A", "B" }, 0) { name = "Choice" };

            var toggleBefore = UitkInteractionCommands.PrimitiveValueJsonForTests(toggle);
            var textBefore = UitkInteractionCommands.PrimitiveValueJsonForTests(text);
            var dropdownBefore = UitkInteractionCommands.PrimitiveValueJsonForTests(dropdown);
            Assert.That(UitkInteractionCommands.ExecuteTypedValueForTests(toggle, "set_toggle", true, string.Empty, -1, out _, out _), Is.True);
            Assert.That(UitkInteractionCommands.ExecuteTypedValueForTests(text, "set_text", false, "after", -1, out _, out _), Is.True);
            Assert.That(UitkInteractionCommands.ExecuteTypedValueForTests(dropdown, "set_dropdown_index", false, string.Empty, 1, out _, out _), Is.True);
            Assert.That(toggle.value, Is.True);
            Assert.That(text.value, Is.EqualTo("after"));
            Assert.That(dropdown.index, Is.EqualTo(1));
            Assert.That(UitkInteractionCommands.RestorationStepJsonForTests(2, dropdown.name, "set_dropdown_index", "/Root/DropdownField[0]:Choice", dropdownBefore), Does.Contain("\"reverse_order\":true"));
            Assert.That(UitkInteractionCommands.RestorationStepJsonForTests(2, dropdown.name, "set_dropdown_index", "/Root/DropdownField[0]:Choice", dropdownBefore), Does.Contain("\"dropdown_index\":0"));
            Assert.That(toggleBefore, Is.EqualTo("false"));
            Assert.That(textBefore, Is.EqualTo("\"before\""));
            Assert.That(dropdownBefore, Is.EqualTo("0"));
        }

        [Test]
        public void SettlePolicyDistinguishesRunningAndPausedPlayMode()
        {
            Assert.That(UitkInteractionCommands.SettlePolicyForTests(false, false, 0), Is.EqualTo("play_mode_required"));
            Assert.That(UitkInteractionCommands.SettlePolicyForTests(true, false, 0), Is.EqualTo("allowed"));
            Assert.That(UitkInteractionCommands.SettlePolicyForTests(true, false, 1), Is.EqualTo("paused_playmode_required"));
            Assert.That(UitkInteractionCommands.SettlePolicyForTests(true, true, 120), Is.EqualTo("allowed"));
            Assert.That(UitkInteractionCommands.SettlePolicyForTests(true, true, 121), Is.EqualTo("settle_frames_out_of_range"));
        }

        [Test]
        public void FullCommandRejectsEditModeBeforeDocumentResolutionOrMutation()
        {
            var args = "{\"scene_path\":\"Assets/Smoke.unity\",\"document_object_path\":\"/UI/Document\",\"component_index\":0,\"steps\":[{\"control_name\":\"Submit\",\"action\":\"focus\"}],\"dry_run\":true}";
            var result = UitkInteractionCommands.InteractionSmoke(new McpCommandContext(null, "test", "test", args, "{}"));

            Assert.That(result.ErrorCode, Is.EqualTo("unity_busy"));
        }

        [Test]
        public void PreconditionFingerprintChangesForCurrentRequestAndRuntimeIdentityDrift()
        {
            var baseline = UitkInteractionCommands.ComputePreconditionHashForTests(
                "Assets/Smoke.unity", 7, "/UI/Document", 10, 11, 2, "Enabled", "set_toggle", "/Root/Toggle[0]:Enabled", "Toggle", 20, "false", "true");
            var currentValueChanged = UitkInteractionCommands.ComputePreconditionHashForTests(
                "Assets/Smoke.unity", 7, "/UI/Document", 10, 11, 2, "Enabled", "set_toggle", "/Root/Toggle[0]:Enabled", "Toggle", 20, "true", "true");
            var requestChanged = UitkInteractionCommands.ComputePreconditionHashForTests(
                "Assets/Smoke.unity", 7, "/UI/Document", 10, 11, 2, "Enabled", "set_toggle", "/Root/Toggle[0]:Enabled", "Toggle", 20, "false", "false");
            var documentChanged = UitkInteractionCommands.ComputePreconditionHashForTests(
                "Assets/Smoke.unity", 7, "/UI/Document", 99, 11, 2, "Enabled", "set_toggle", "/Root/Toggle[0]:Enabled", "Toggle", 20, "false", "true");
            var settleChanged = UitkInteractionCommands.ComputePreconditionHashForTests(
                "Assets/Smoke.unity", 7, "/UI/Document", 10, 11, 2, "Enabled", "set_toggle", "/Root/Toggle[0]:Enabled", "Toggle", 20, "false", "true", 1);
            var enabledChanged = UitkInteractionCommands.ComputePreconditionHashForTests(
                "Assets/Smoke.unity", 7, "/UI/Document", 10, 11, 2, "Enabled", "set_toggle", "/Root/Toggle[0]:Enabled", "Toggle", 20, "false", "true", 0, false);

            Assert.That(baseline, Does.StartWith("sha256:"));
            Assert.That(baseline, Has.Length.EqualTo(71));
            Assert.That(currentValueChanged, Is.Not.EqualTo(baseline));
            Assert.That(requestChanged, Is.Not.EqualTo(baseline));
            Assert.That(documentChanged, Is.Not.EqualTo(baseline));
            Assert.That(settleChanged, Is.Not.EqualTo(baseline));
            Assert.That(enabledChanged, Is.Not.EqualTo(baseline));
        }
    }
}
