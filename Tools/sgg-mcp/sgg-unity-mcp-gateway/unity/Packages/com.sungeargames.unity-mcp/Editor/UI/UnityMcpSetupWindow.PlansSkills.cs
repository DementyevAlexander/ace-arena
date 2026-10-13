using System;
using System.IO;
using System.Text.RegularExpressions;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Setup;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace SGG.UnityMCP.Editor.UI
{
    public sealed partial class UnityMcpSetupWindow
    {
        Label m_plansLocalRoot;
        Label m_plansVersionedRoot;
        Label m_plansLatestFiles;
        Label m_skillsLocalRoot;
        Label m_skillsExamplesRoot;
        Label m_skillsValidationStatus;
        Label m_skillsReviewStatus;
        Label m_skillsKnowledgeRoot;

        void BuildPlansPanel()
        {
            HelpRow(m_plansPanel, "Plans is a non-applying scaffold. Open, refresh and copy helpers are safe local file visibility actions; this tab never applies plans automatically.");

            var storageSection = AddSection(m_plansPanel, "Plans");
            AddRow(storageSection, "Status", "🚧 Not implemented: automatic plan apply. This tab only previews and copies plan files.");
            AddRow(storageSection, "Optional local folder", "Empty/missing local plans folder is optional.");
            AddRow(storageSection, "Execution", "Plans remain explicit MCP command workflows with project-scoped calls; this UI never applies plans automatically.");
            m_plansLocalRoot = AddRow(storageSection, "Local plans root");
            m_plansVersionedRoot = AddRow(storageSection, "Versioned plans root");
            m_plansLatestFiles = AddRow(storageSection, "Latest plan files");
            var actions = AddActions(storageSection);
            AddButton(actions, "Open Plans Folder", OpenPlansFolder);
            AddButton(actions, "Copy Plan Template", CopyPlanTemplate);
            AddButton(actions, "Refresh Plans", RefreshPlansAndLocalize);
            AddButton(actions, "Copy Latest Plan", CopyLatestPlan);
        }

        void BuildSkillsPanel()
        {
            HelpRow(m_skillsPanel, "Skills shows local skill locations and review status. Open, refresh and copy-style actions are safe; this tab does not execute or auto-enable skills.");

            var section = AddSection(m_skillsPanel, "Skills");
            AddRow(section, "Status", "Read-only skills workflow scaffold. Skills are not executed or auto-enabled from this UI.");
            AddRow(section, "Source review", "Missing .mcp/skills folder is optional. Review local SKILL.md sources before enabling related Gateway skills elsewhere.");
            AddRow(section, "Activation", "Review, feature flags and permissions remain configured outside this tab; skills cannot change policy or bypass PathGuard/CanonicalPath.");
            m_skillsLocalRoot = AddRow(section, "Local skills folder");
            m_skillsExamplesRoot = AddRow(section, "Built-in examples");
            m_skillsValidationStatus = AddRow(section, "Validation status");
            m_skillsReviewStatus = AddRow(section, "Review status");
            m_skillsKnowledgeRoot = AddRow(section, "Project knowledge");
            var actions = AddActions(section);
            AddButton(actions, "Open Skills Folder", OpenSkillsFolder);
            AddButton(actions, "Refresh Skills", RefreshSkillsAndLocalize);
            BuildUnityCliSkillSection(m_skillsPanel);
        }

        void OpenPlansFolder()
        {
            try
            {
                var root = LocalPlansRoot();
                if (!Directory.Exists(root))
                {
                    m_lastActionLabel.text = "Open Plans Folder: missing local plans folder is optional: " + root;
                    return;
                }

                EditorUtility.RevealInFinder(root);
                m_lastActionLabel.text = "Plans folder opened: " + root;
                RefreshPlans();
            }
            catch (Exception exception)
            {
                m_lastActionLabel.text = "Open Plans Folder: " + exception.Message;
                EditorUtility.DisplayDialog("Open Plans Folder Failed", exception.Message, "OK");
            }
        }

        void CopyPlanTemplate()
        {
            GUIUtility.systemCopyBuffer = PlanTemplate();
            m_lastActionLabel.text = "Plan template copied to clipboard.";
        }

        void CopyLatestPlan()
        {
            try
            {
                var latest = LatestPlanFile();
                if (string.IsNullOrEmpty(latest))
                {
                    m_lastActionLabel.text = "Copy Latest Plan: no local plan files found.";
                    return;
                }

                GUIUtility.systemCopyBuffer = File.ReadAllText(latest);
                m_lastActionLabel.text = "Latest plan copied to clipboard: " + Path.GetFileName(latest);
            }
            catch (Exception exception)
            {
                m_lastActionLabel.text = "Copy Latest Plan: " + exception.Message;
                EditorUtility.DisplayDialog("Copy Latest Plan Failed", exception.Message, "OK");
            }
        }

        void RefreshPlans()
        {
            if (m_plansLocalRoot == null)
            {
                return;
            }

            m_plansLocalRoot.text = LocalPlansRoot() + " (local runtime/debug; not committed)";
            m_plansVersionedRoot.text = VersionedPlansRoot() + " (only with explicit versioned=true and policy approval)";
            m_plansLatestFiles.text = LatestPlanFilesSummary();
        }

        void OpenSkillsFolder()
        {
            var root = LocalSkillsRoot();
            if (!Directory.Exists(root))
            {
                m_lastActionLabel.text = "Open Skills Folder: folder does not exist yet: " + root;
                return;
            }

            EditorUtility.RevealInFinder(root);
            m_lastActionLabel.text = "Skills folder opened: " + root;
            RefreshSkills();
        }

        void RefreshSkills()
        {
            if (m_skillsLocalRoot == null)
            {
                return;
            }

            m_skillsLocalRoot.text = CanonicalizePath(LocalSkillsRoot()) + " (.mcp/skills/<skill>/SKILL.md agent workflows; missing folder is optional)";
            m_skillsExamplesRoot.text = CanonicalizePath(BuiltInSkillsRoot()) + " (generic examples only)";
            m_skillsValidationStatus.text = SkillsValidationSummary();
            m_skillsReviewStatus.text = SkillsReviewSummary();
            m_skillsKnowledgeRoot.text = CanonicalizePath(Path.Combine(ProjectPaths.ProjectRoot, "Assets", "UnityMcpKnowledge")) + " (project knowledge/resources, not skill workflows)";
            RefreshUnityCliProjectSkill();
        }

        void RefreshPlansAndLocalize()
        {
            RefreshPlans();
            ApplyLocalization();
        }

        void RefreshSkillsAndLocalize()
        {
            RefreshSkills();
            ApplyLocalization();
        }

        string LocalPlansRoot()
        {
            return Path.Combine(ProjectPaths.ProjectRoot, "Library", "SGGUnityMcp", "plans");
        }

        string LocalSkillsRoot()
        {
            return Path.Combine(ProjectPaths.ProjectRoot, ".mcp", "skills");
        }

        string BuiltInSkillsRoot()
        {
            var gatewayStatus = GatewayInstaller.GetStatus();
            return string.IsNullOrEmpty(gatewayStatus.RepoRoot)
                ? "examples/skills in the Gateway repository"
                : Path.Combine(gatewayStatus.RepoRoot, "examples", "skills");
        }

        string SkillReviewMetadataPath()
        {
            return Path.Combine(ProjectPaths.ProjectRoot, "Library", "SGGUnityMcp", "skills", "reviewed-skills.json");
        }

        string SkillsValidationSummary()
        {
            var root = LocalSkillsRoot();
            if (!Directory.Exists(root))
            {
                return "Missing .mcp/skills folder is optional; skill.list will return built-in examples when enabled.";
            }

            var folders = Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly);
            var valid = 0;
            var invalid = 0;
            foreach (var folder in folders)
            {
                var skillPath = Path.Combine(folder, "SKILL.md");
                if (!File.Exists(skillPath))
                {
                    invalid++;
                    continue;
                }

                var text = File.ReadAllText(skillPath);
                if (Regex.IsMatch(text, "^#\\s+.+$", RegexOptions.Multiline) && text.Length <= 131072)
                {
                    valid++;
                }
                else
                {
                    invalid++;
                }
            }

            return valid + " valid, " + invalid + " invalid; validation is read-only and never executes SKILL.md.";
        }

        string SkillsReviewSummary()
        {
            var metadataPath = SkillReviewMetadataPath();
            if (!File.Exists(metadataPath))
            {
                return "Not reviewed yet; metadata not found at " + CanonicalizePath(metadataPath) + "; unknown skills are not auto-enabled.";
            }

            return "Review metadata present at " + CanonicalizePath(metadataPath) + " (read-only; no write command is implemented).";
        }

        string VersionedPlansRoot()
        {
            return Path.Combine(ProjectPaths.ProjectRoot, ".mcp", "plans");
        }

        string LatestPlanFilesSummary()
        {
            var root = LocalPlansRoot();
            if (!Directory.Exists(root))
            {
                return "No local plans folder yet";
            }

            var files = Directory.GetFiles(root, "*.md", SearchOption.TopDirectoryOnly);
            Array.Sort(files, (left, right) => File.GetLastWriteTimeUtc(right).CompareTo(File.GetLastWriteTimeUtc(left)));
            if (files.Length == 0)
            {
                return "No local plan files found";
            }

            var count = Math.Min(files.Length, 5);
            var result = string.Empty;
            for (var index = 0; index < count; index++)
            {
                result += (index == 0 ? string.Empty : "\n") + Path.GetFileName(files[index]) + " - " + File.GetLastWriteTime(files[index]).ToString("u");
            }

            return result;
        }

        string LatestPlanFile()
        {
            var root = LocalPlansRoot();
            if (!Directory.Exists(root))
            {
                return string.Empty;
            }

            var files = Directory.GetFiles(root, "*.md", SearchOption.TopDirectoryOnly);
            Array.Sort(files, (left, right) => File.GetLastWriteTimeUtc(right).CompareTo(File.GetLastWriteTimeUtc(left)));
            return files.Length == 0 ? string.Empty : files[0];
        }

        string PlanTemplate()
        {
            return "# SGGMcp Plan\n\n"
                + "## Goal\n\n"
                + "## Project\n\n"
                + "## Read-only discovery\n\n"
                + "## Proposed changes\n\n"
                + "## Commands to run\n\n"
                + "## Files to touch\n\n"
                + "## Risks\n\n"
                + "## Approval required\n\n"
                + "## Rollback\n";
        }
    }
}
