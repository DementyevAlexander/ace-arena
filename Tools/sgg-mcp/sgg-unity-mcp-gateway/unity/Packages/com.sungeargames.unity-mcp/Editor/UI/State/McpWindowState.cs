using System;
using UnityEngine;

namespace SGG.UnityMCP.Editor.UI.State
{
    [Serializable]
    internal sealed class McpWindowState
    {
        public const string DefaultTab = "Setup";

        [SerializeField]
        string m_selectedTab = string.Empty;

        internal bool HasExplicitSelection
        {
            get { return IsSupportedTab(m_selectedTab); }
        }

        public string SelectedTab
        {
            get
            {
                m_selectedTab = NormalizeTab(m_selectedTab);
                return m_selectedTab;
            }
        }

        public string SelectTab(string tabName)
        {
            m_selectedTab = NormalizeTab(tabName);
            return m_selectedTab;
        }

        public static bool IsSupportedTab(string tabName)
        {
            switch (tabName)
            {
                case "Setup":
                case "FTUE":
                case "Status":
                case "Permissions":
                case "Commands":
                case "Plans":
                case "Skills":
                case "Unity AI Assistant":
                case "Diagnostics":
                    return true;
                default:
                    return false;
            }
        }

        public static string NormalizeTab(string tabName)
        {
            return IsSupportedTab(tabName) ? tabName : DefaultTab;
        }
    }
}
