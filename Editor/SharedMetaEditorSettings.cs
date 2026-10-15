#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace SharedMeta.Editor
{
    /// <summary>
    /// Per-project editor settings for SharedMeta tools (Wizard, Server Runner).
    /// Stored in ProjectSettings/SharedMetaSettings.asset — not per-user, committed to VCS.
    /// </summary>
    [FilePath("ProjectSettings/SharedMetaSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public class SharedMetaEditorSettings : ScriptableSingleton<SharedMetaEditorSettings>
    {
        // ─── Wizard Settings ───────────────────────────────────────────

        public string wizardVersion = "";
        public string sharedProjectName = "MyGame.Shared";
        public string sharedStateName = "PlayerProfile";
        public int transportIndex; // 0 = SignalR, 1 = HTTP Polling
        public int serializerIndex; // 0 = MemoryPack, 1 = MessagePack
        public int serverPort = 5000;
        public bool enableAuth = true;
        public bool enableNullable = true;
        public bool useLocalNuget;
        public string localNugetPath = "";
        public string sharedOutputDir = "Assets/Scripts/MyGame.Shared";
        public string sharedDotnetDir = "../MyGame.Shared";  // legacy, migrated to solutionDir
        public string solutionDir = "../ServerSolution";
        public string serverProjectName = "MyGame.Server";
        public string serverOutputDir = "../Server";  // legacy, migrated to solutionDir
        public string clientOutputDir = "Assets/Scripts/Meta";
        public int templateIndex;
        public int wizardMode; // 0 = Interactive, 1 = Classic
        public int currentStep;

        // ─── Server Runner Settings ────────────────────────────────────

        public string serverCsprojPath = "";
        public string serverDotnetArgs = "";
        public string serverUrl = "http://localhost:5000";
        public bool serverAutoScroll = true;

        // ─── API ───────────────────────────────────────────────────────

        public void Save()
        {
            Save(true);
        }

        /// <summary>
        /// Migrate settings from EditorPrefs (legacy) to ScriptableSingleton on first use.
        /// Call once from each tool's OnEnable.
        /// </summary>
        public void MigrateFromEditorPrefsIfNeeded()
        {
            // If sharedProjectName is still default and EditorPrefs has a value, migrate
            if (!EditorPrefs.HasKey("SharedMeta_Wizard_SharedName")) return;

            wizardVersion = EditorPrefs.GetString("SharedMeta_Wizard_Version", wizardVersion);
            sharedProjectName = EditorPrefs.GetString("SharedMeta_Wizard_SharedName", sharedProjectName);
            sharedStateName = EditorPrefs.GetString("SharedMeta_Wizard_StateName", sharedStateName);
            transportIndex = EditorPrefs.GetInt("SharedMeta_Wizard_Transport", transportIndex);
            serializerIndex = EditorPrefs.GetInt("SharedMeta_Wizard_Serializer", serializerIndex);
            serverPort = EditorPrefs.GetInt("SharedMeta_Wizard_ServerPort", serverPort);
            enableAuth = EditorPrefs.GetBool("SharedMeta_Wizard_Auth", enableAuth);
            enableNullable = EditorPrefs.GetBool("SharedMeta_Wizard_Nullable", enableNullable);
            useLocalNuget = EditorPrefs.GetBool("SharedMeta_Wizard_LocalNuget", useLocalNuget);
            localNugetPath = EditorPrefs.GetString("SharedMeta_Wizard_LocalNugetPath", localNugetPath);
            sharedOutputDir = EditorPrefs.GetString("SharedMeta_Wizard_SharedOutputDir", sharedOutputDir);
            sharedDotnetDir = EditorPrefs.GetString("SharedMeta_Wizard_SharedDotnetDir", sharedDotnetDir);
            serverProjectName = EditorPrefs.GetString("SharedMeta_Wizard_ServerName", serverProjectName);
            serverOutputDir = EditorPrefs.GetString("SharedMeta_Wizard_ServerDir", serverOutputDir);
            clientOutputDir = EditorPrefs.GetString("SharedMeta_Wizard_ClientDir", clientOutputDir);
            templateIndex = EditorPrefs.GetInt("SharedMeta_Wizard_Template", templateIndex);
            wizardMode = EditorPrefs.GetInt("SharedMeta_Wizard_Mode", wizardMode);
            currentStep = EditorPrefs.GetInt("SharedMeta_Wizard_CurrentStep", currentStep);

            serverCsprojPath = EditorPrefs.GetString("SharedMeta_Server_CsprojPath", serverCsprojPath);
            serverDotnetArgs = EditorPrefs.GetString("SharedMeta_Server_DotnetArgs", serverDotnetArgs);
            serverUrl = EditorPrefs.GetString("SharedMeta_Server_Url", serverUrl);
            serverAutoScroll = EditorPrefs.GetBool("SharedMeta_Server_AutoScroll", serverAutoScroll);

            Save();

            // Clean up legacy keys
            EditorPrefs.DeleteKey("SharedMeta_Wizard_Version");
            EditorPrefs.DeleteKey("SharedMeta_Wizard_SharedName");
            EditorPrefs.DeleteKey("SharedMeta_Wizard_StateName");
            EditorPrefs.DeleteKey("SharedMeta_Wizard_Transport");
            EditorPrefs.DeleteKey("SharedMeta_Wizard_Serializer");
            EditorPrefs.DeleteKey("SharedMeta_Wizard_ServerPort");
            EditorPrefs.DeleteKey("SharedMeta_Wizard_Auth");
            EditorPrefs.DeleteKey("SharedMeta_Wizard_Nullable");
            EditorPrefs.DeleteKey("SharedMeta_Wizard_LocalNuget");
            EditorPrefs.DeleteKey("SharedMeta_Wizard_LocalNugetPath");
            EditorPrefs.DeleteKey("SharedMeta_Wizard_SharedOutputDir");
            EditorPrefs.DeleteKey("SharedMeta_Wizard_SharedDotnetDir");
            EditorPrefs.DeleteKey("SharedMeta_Wizard_ServerName");
            EditorPrefs.DeleteKey("SharedMeta_Wizard_ServerDir");
            EditorPrefs.DeleteKey("SharedMeta_Wizard_ClientDir");
            EditorPrefs.DeleteKey("SharedMeta_Wizard_Template");
            EditorPrefs.DeleteKey("SharedMeta_Wizard_Mode");
            EditorPrefs.DeleteKey("SharedMeta_Wizard_CurrentStep");
            EditorPrefs.DeleteKey("SharedMeta_Server_CsprojPath");
            EditorPrefs.DeleteKey("SharedMeta_Server_DotnetArgs");
            EditorPrefs.DeleteKey("SharedMeta_Server_Url");
            EditorPrefs.DeleteKey("SharedMeta_Server_AutoScroll");
        }
    }
}
#endif
