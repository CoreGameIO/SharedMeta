#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace SharedMeta.Editor
{
    /// <summary>
    /// Unity EditorWindow for scaffolding SharedMeta projects.
    /// Shared: creates game state/service files in Unity + .NET mirror project.
    /// Server: creates a .NET project (outside Unity).
    /// Client: generates MonoBehaviour scripts (inside Unity project).
    /// Accessible via Tools > SharedMeta > Project Wizard menu.
    /// </summary>
    public class SharedMetaProjectWizard : EditorWindow
    {
        // Common settings
        private string _sharedMetaVersion = "";
        private string _sharedProjectName = "Meta.Shared";
        private string _sharedStateName = "PlayerProfile";
        private int _transportIndex; // 0 = SignalR, 1 = HTTP Polling
        private int _serializerIndex; // 0 = MemoryPack, 1 = MessagePack
        private int _serverPort = 5000;
        private bool _enableAuth = true;
        private bool _enableNullable = true;
        private bool _useLocalNuget;
        private string _localNugetPath = "";

        // Shared project settings
        private string _sharedOutputDir = "Assets/Scripts/Meta.Shared";

        // Solution directory (root for .NET projects)
        private string _solutionDir = "../";

        // Server settings
        private string _serverProjectName = "Meta.Server";

        // Derived paths (from solutionDir + project names)
        private string SharedDotnetDir => _solutionDir.TrimEnd('/', '\\') + "/" + _sharedProjectName;
        private string ServerOutputDir => _solutionDir.TrimEnd('/', '\\') + "/" + _serverProjectName;

        // Client settings
        private string _clientOutputDir = "Assets/Scripts/Meta.Client";

        private string ServerUrl => $"http://localhost:{_serverPort}/meta";

        // Package management (transient — must not survive domain reload)
        private enum PackageStatus { Unknown, Checking, Installed, NotInstalled, Installing, Error }
        [NonSerialized] private PackageStatus _serializerPkgStatus = PackageStatus.Unknown;
        [NonSerialized] private string _serializerPkgError = "";
        [NonSerialized] private int _lastCheckedSerializerIndex = -1;
        [NonSerialized] private ListRequest? _listRequest;
        [NonSerialized] private AddRequest? _addRequest;

        // Transport dependency management
        [NonSerialized] private PackageStatus _transportPkgStatus = PackageStatus.Unknown;
        [NonSerialized] private string _transportPkgError = "";
        [NonSerialized] private int _lastCheckedTransportIndex = -1;
        [NonSerialized] private AddRequest? _transportAddRequest;

        // UI state
        private Vector2 _scrollPos;
        private bool _depsFoldout = true;
        private bool _sharedFoldout = true;
        private bool _serverFoldout = true;
        private bool _clientFoldout = true;
        private bool _sceneFoldout = true;

        private int _templateIndex; // 0 = Simple Profile, 1 = Othello, 2 = Expedition

        // Wizard mode
        private enum WizardMode { Interactive, Classic }
        private WizardMode _wizardMode = WizardMode.Interactive;
        private int _currentStep; // 0-5

        private static readonly string[] TransportOptions = { "SignalR", "HTTP Polling", "BestHttp SignalR", "BestHttp HTTP" };

        // Helper: is the server using SignalR (indices 0, 2) or HTTP Polling (indices 1, 3)?
        private bool IsServerSignalR => _transportIndex == 0 || _transportIndex == 2;
        private bool IsBestHttpTransport => _transportIndex >= 2;
        private bool IsBestHttpSignalRMessagePack => _transportIndex == 2 && _serializerIndex == 1;
        private static readonly string[] SerializerOptions = { "MemoryPack", "MessagePack" };
        private static readonly string[] TemplateOptions = {
            "Simple Profile",
            "Othello (2-player with matchmaking)",
            "Expedition (single-player with energy)"
        };
        private static readonly string[] StepLabels = {
            "Settings", "Dependencies", "Create Shared Project",
            "Create Server Project", "Generate Client Scripts", "Setup Scene"
        };

        [MenuItem("Tools/SharedMeta/Project Wizard")]
        public static void ShowWindow()
        {
            var window = GetWindow<SharedMetaProjectWizard>("SharedMeta Project Wizard");
            window.minSize = new Vector2(450, 600);
        }

        private void OnEnable()
        {
            // Reset transient state (may be stale after domain reload)
            _listRequest = null;
            _addRequest = null;
            _serializerPkgStatus = PackageStatus.Unknown;
            _serializerPkgError = "";
            _lastCheckedSerializerIndex = -1;
            _transportAddRequest = null;
            _transportPkgStatus = PackageStatus.Unknown;
            _transportPkgError = "";
            _lastCheckedTransportIndex = -1;

            LoadSettings();
            if (string.IsNullOrEmpty(_localNugetPath))
                _localNugetPath = DetectLocalNugetPath();
            if (string.IsNullOrEmpty(_sharedMetaVersion))
                _sharedMetaVersion = DetectVersionFromLocalNupkg();
            if (string.IsNullOrEmpty(_sharedMetaVersion))
                _sharedMetaVersion = DetectVersionFromPackageJson();
            CheckSerializerPackage();
            CheckTransportDependency();
        }

        private void OnDisable()
        {
            SaveSettings();
        }

        private void OnGUI()
        {
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            // Title + mode toggle
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("SharedMeta Project Wizard", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(_wizardMode == WizardMode.Interactive ? "Classic View" : "Wizard View",
                GUILayout.Width(100)))
                _wizardMode = _wizardMode == WizardMode.Interactive ? WizardMode.Classic : WizardMode.Interactive;
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(8);

            // Polling (runs in both modes)
            if (_serializerIndex != _lastCheckedSerializerIndex) CheckSerializerPackage();
            if (_transportIndex != _lastCheckedTransportIndex) CheckTransportDependency();
            PollPackageRequests();

            if (_wizardMode == WizardMode.Interactive)
                DrawInteractiveMode();
            else
                DrawClassicMode();

            EditorGUILayout.EndScrollView();
        }

        // ─── Interactive Mode ─────────────────────────────────────────

        private void DrawInteractiveMode()
        {
            // Clamp step to valid range
            _currentStep = Math.Max(0, Math.Min(_currentStep, StepLabels.Length - 1));

            // Step indicator
            EditorGUILayout.LabelField(
                $"Step {_currentStep + 1} of {StepLabels.Length}: {StepLabels[_currentStep]}",
                EditorStyles.boldLabel);
            var rect = EditorGUILayout.GetControlRect(false, 18);
            EditorGUI.ProgressBar(rect, (_currentStep + 1f) / StepLabels.Length, "");
            EditorGUILayout.Space(12);

            // Current step content
            switch (_currentStep)
            {
                case 0: DrawSettingsStep(); break;
                case 1: DrawDependenciesStep(); break;
                case 2: DrawSharedProjectStep(); break;
                case 3: DrawServerProjectStep(); break;
                case 4: DrawClientScriptsStep(); break;
                case 5: DrawSetupSceneStep(); break;
            }

            // Navigation bar
            EditorGUILayout.Space(16);
            EditorGUILayout.BeginHorizontal();

            EditorGUI.BeginDisabledGroup(_currentStep == 0);
            if (GUILayout.Button("< Back", GUILayout.Height(28), GUILayout.Width(80)))
            {
                _currentStep--;
                GUIUtility.keyboardControl = 0; // clear focus to prevent value leak
            }
            EditorGUI.EndDisabledGroup();

            GUILayout.FlexibleSpace();

            if (_currentStep < StepLabels.Length - 1)
            {
                if (GUILayout.Button("Skip >", GUILayout.Height(28), GUILayout.Width(80)))
                {
                    _currentStep++;
                    GUIUtility.keyboardControl = 0;
                }
                if (GUILayout.Button("Next >", GUILayout.Height(28), GUILayout.Width(80)))
                {
                    _currentStep++;
                    GUIUtility.keyboardControl = 0;
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        // ─── Classic Mode ─────────────────────────────────────────────

        private void DrawClassicMode()
        {
            EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
            DrawSettingsStep();

            EditorGUILayout.Space(16);
            _depsFoldout = EditorGUILayout.Foldout(_depsFoldout, "Dependencies", true, EditorStyles.foldoutHeader);
            if (_depsFoldout)
            {
                EditorGUI.indentLevel++;
                DrawDependenciesStep();
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(16);
            _sharedFoldout = EditorGUILayout.Foldout(_sharedFoldout, "Shared Project", true, EditorStyles.foldoutHeader);
            if (_sharedFoldout)
            {
                EditorGUI.indentLevel++;
                DrawSharedProjectStep();
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(16);
            _serverFoldout = EditorGUILayout.Foldout(_serverFoldout, "Server Project", true, EditorStyles.foldoutHeader);
            if (_serverFoldout)
            {
                EditorGUI.indentLevel++;
                DrawServerProjectStep();
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(16);
            _clientFoldout = EditorGUILayout.Foldout(_clientFoldout, "Client Scripts", true, EditorStyles.foldoutHeader);
            if (_clientFoldout)
            {
                EditorGUI.indentLevel++;
                DrawClientScriptsStep();
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(16);
            _sceneFoldout = EditorGUILayout.Foldout(_sceneFoldout, "Setup Scene", true, EditorStyles.foldoutHeader);
            if (_sceneFoldout)
            {
                EditorGUI.indentLevel++;
                DrawSetupSceneStep();
                EditorGUI.indentLevel--;
            }
        }

        // ─── Step Drawing Methods ─────────────────────────────────────

        private void DrawSettingsStep()
        {
            _sharedMetaVersion = EditorGUILayout.TextField("SharedMeta Version", _sharedMetaVersion);
            _sharedProjectName = EditorGUILayout.TextField("Shared Project Name", _sharedProjectName);
            _serverProjectName = EditorGUILayout.TextField("Server Project Name", _serverProjectName);
            _sharedStateName = EditorGUILayout.TextField("Shared State Name", _sharedStateName);
            _templateIndex = EditorGUILayout.Popup("Template", _templateIndex, TemplateOptions);

            // Solution directory (root for .NET projects)
            EditorGUILayout.BeginHorizontal();
            _solutionDir = EditorGUILayout.TextField("Solution Directory", _solutionDir);
            if (GUILayout.Button("...", GUILayout.Width(30)))
            {
                var selected = EditorUtility.OpenFolderPanel("Solution Root Directory", _solutionDir, "");
                if (!string.IsNullOrEmpty(selected))
                {
                    var projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
                    _solutionDir = ComputeRelativeDir(projectRoot, selected);
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField("Shared .NET", SharedDotnetDir);
            EditorGUILayout.LabelField("Server", ServerOutputDir);
            EditorGUI.indentLevel--;

            _transportIndex = EditorGUILayout.Popup("Transport", _transportIndex, TransportOptions);
            _serializerIndex = EditorGUILayout.Popup("Serializer", _serializerIndex, SerializerOptions);
            _serverPort = EditorGUILayout.IntField("Server Port", _serverPort);
            _enableAuth = EditorGUILayout.Toggle("Enable Auth (JWT)", _enableAuth);
            _enableNullable = EditorGUILayout.Toggle("Enable Nullable", _enableNullable);
            _useLocalNuget = EditorGUILayout.Toggle("Local NuGet (dev)", _useLocalNuget);
            if (_useLocalNuget)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.BeginHorizontal();
                _localNugetPath = EditorGUILayout.TextField("NuGet Path", _localNugetPath);
                if (GUILayout.Button("...", GUILayout.Width(30)))
                {
                    var selected = EditorUtility.OpenFolderPanel("Local NuGet Packages", _localNugetPath, "");
                    if (!string.IsNullOrEmpty(selected))
                    {
                        var projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
                        _localNugetPath = ComputeRelativeDir(projectRoot, selected);
                    }
                }
                EditorGUILayout.EndHorizontal();
                EditorGUI.indentLevel--;
            }
        }

        private void DrawDependenciesStep()
        {
            // Serializer package row
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"{GetSerializerPackageName()} ({GetSerializerPackageId()})");
            var prevColor = GUI.color;
            switch (_serializerPkgStatus)
            {
                case PackageStatus.Installed:
                    GUI.color = Color.green;
                    GUILayout.Label("Installed", EditorStyles.boldLabel, GUILayout.Width(80));
                    break;
                case PackageStatus.NotInstalled:
                    if (GUILayout.Button("Install", GUILayout.Width(80)))
                        InstallSerializerPackage();
                    break;
                case PackageStatus.Checking:
                    GUILayout.Label("Checking...", GUILayout.Width(80));
                    break;
                case PackageStatus.Installing:
                    GUILayout.Label("Installing...", GUILayout.Width(80));
                    break;
                case PackageStatus.Error:
                    GUI.color = Color.red;
                    GUILayout.Label("Error", GUILayout.Width(80));
                    break;
                default:
                    GUILayout.Label("...", GUILayout.Width(80));
                    break;
            }
            GUI.color = prevColor;
            EditorGUILayout.EndHorizontal();

            if (_serializerPkgStatus == PackageStatus.Error && !string.IsNullOrEmpty(_serializerPkgError))
                EditorGUILayout.HelpBox(_serializerPkgError, MessageType.Error);

            // Transport dependency row
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(GetTransportDependencyLabel());
            prevColor = GUI.color;
            switch (_transportPkgStatus)
            {
                case PackageStatus.Installed:
                    GUI.color = Color.green;
                    GUILayout.Label("Installed", EditorStyles.boldLabel, GUILayout.Width(80));
                    break;
                case PackageStatus.NotInstalled:
                    if (_transportIndex == 1) // HTTP Polling — auto-install Newtonsoft.Json
                    {
                        if (GUILayout.Button("Install", GUILayout.Width(80)))
                            InstallTransportPackage();
                    }
                    else if (_transportIndex == 0) // SignalR — manual install
                    {
                        if (GUILayout.Button("Install", GUILayout.Width(80)))
                            ShowSignalRInstallDialog();
                    }
                    else // BestHttp — manual install from Asset Store
                    {
                        GUILayout.Label("Not Found", GUILayout.Width(80));
                    }
                    break;
                case PackageStatus.Checking:
                    GUILayout.Label("Checking...", GUILayout.Width(80));
                    break;
                case PackageStatus.Installing:
                    GUILayout.Label("Installing...", GUILayout.Width(80));
                    break;
                case PackageStatus.Error:
                    GUI.color = Color.red;
                    GUILayout.Label("Error", GUILayout.Width(80));
                    break;
                default:
                    GUILayout.Label("...", GUILayout.Width(80));
                    break;
            }
            GUI.color = prevColor;
            EditorGUILayout.EndHorizontal();

            if (_transportPkgStatus == PackageStatus.NotInstalled)
            {
                if (_transportIndex == 0)
                    EditorGUILayout.HelpBox(
                        "Install Microsoft.AspNetCore.SignalR.Client via NuGetForUnity or place DLLs in Assets/Plugins/.\n" +
                        "The HAS_SIGNALR scripting define will be added automatically when detected.",
                        MessageType.Info);
                else if (_transportIndex >= 2)
                    EditorGUILayout.HelpBox(
                        "BestHTTP2 not detected. Import the BestHTTP2 .unitypackage into your project.\n" +
                        "The HAS_BESTHTTP scripting define will be added automatically when the BestHTTP assembly is detected.",
                        MessageType.Info);
            }

            if (_transportPkgStatus == PackageStatus.Error && !string.IsNullOrEmpty(_transportPkgError))
                EditorGUILayout.HelpBox(_transportPkgError, MessageType.Error);
        }

        private void DrawSharedProjectStep()
        {
            EditorGUILayout.BeginHorizontal();
            _sharedOutputDir = EditorGUILayout.TextField("Unity Folder", _sharedOutputDir);
            if (GUILayout.Button("...", GUILayout.Width(30)))
            {
                var selected = EditorUtility.OpenFolderPanel("Shared Unity Folder", _sharedOutputDir, "");
                if (!string.IsNullOrEmpty(selected))
                    _sharedOutputDir = selected;
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(".NET Project Dir", SharedDotnetDir);

            // Status: detect existing project parts
            var unityDir = ResolveOutputDir(_sharedOutputDir);
            var dotnetDir = ResolveOutputDir(SharedDotnetDir);
            bool unityExists = File.Exists(Path.Combine(unityDir, $"{_sharedProjectName}.asmdef"));
            bool dotnetExists = File.Exists(Path.Combine(dotnetDir, $"{_sharedProjectName}.csproj"));

            if (dotnetExists && !unityExists)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(
                    ".NET shared project found but Unity files are missing.\n" +
                    "Click the button below to generate Unity-side files (asmdef, source code, docs).",
                    MessageType.Warning);
            }
            else if (unityExists && dotnetExists)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(
                    "Shared project already exists (Unity + .NET). You can recreate to update files.",
                    MessageType.Info);
            }

            EditorGUILayout.Space(4);
            if (GUILayout.Button("Create Shared Project", GUILayout.Height(30)))
                CreateSharedProject();
        }

        private void DrawServerProjectStep()
        {
            EditorGUILayout.LabelField("Output Directory", ServerOutputDir);
            EditorGUILayout.Space(4);
            if (GUILayout.Button("Create Server Project", GUILayout.Height(30)))
                CreateServerProject();
        }

        private void DrawClientScriptsStep()
        {
            // Check if shared Unity project exists
            var sharedUnityDir = ResolveOutputDir(_sharedOutputDir);
            bool sharedExists = File.Exists(Path.Combine(sharedUnityDir, $"{_sharedProjectName}.asmdef"));
            if (!sharedExists)
            {
                EditorGUILayout.HelpBox(
                    $"Shared Unity project not found at:\n{sharedUnityDir}\n\n" +
                    "Please create the Shared Project first (Step 2).",
                    MessageType.Warning);
                EditorGUILayout.Space(4);
            }

            EditorGUILayout.BeginHorizontal();
            _clientOutputDir = EditorGUILayout.TextField("Output Folder", _clientOutputDir);
            if (GUILayout.Button("...", GUILayout.Width(30)))
            {
                var selected = EditorUtility.OpenFolderPanel("Client Output Folder", _clientOutputDir, "");
                if (!string.IsNullOrEmpty(selected))
                    _clientOutputDir = selected;
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);

            EditorGUI.BeginDisabledGroup(!sharedExists);
            if (GUILayout.Button("Generate Client Scripts", GUILayout.Height(30)))
                CreateClientProject();
            EditorGUI.EndDisabledGroup();
        }

        private void DrawSetupSceneStep()
        {
            EditorGUILayout.LabelField(
                "Create a GameObject with MetaGameClient component to bootstrap the connection.",
                EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space(8);

            // Check if MetaGameClient type exists (scripts must be compiled)
            var clientType = FindMetaGameClientType();
            if (clientType == null)
            {
                EditorGUILayout.HelpBox(
                    "MetaGameClient script not found.\n\n" +
                    "Please generate client scripts first (Step 5) and wait for Unity to finish compiling.",
                    MessageType.Warning);
            }

            EditorGUI.BeginDisabledGroup(clientType == null);
            if (GUILayout.Button("Create MetaGameClient GameObject", GUILayout.Height(30)))
            {
                CreateMetaGameClientObject(clientType!);
            }
            EditorGUI.EndDisabledGroup();
        }

        private static Type? FindMetaGameClientType()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = asm.GetType("MetaGameClient");
                if (type != null && typeof(MonoBehaviour).IsAssignableFrom(type))
                    return type;
            }
            return null;
        }

        private static void CreateMetaGameClientObject(Type clientType)
        {
            // Check if one already exists in the scene
            var existing = UnityEngine.Object.FindAnyObjectByType(clientType) as Component;
            if (existing != null)
            {
                if (!EditorUtility.DisplayDialog("Already Exists",
                    "A MetaGameClient already exists in the scene.\n\nSelect the existing one instead?",
                    "Select Existing", "Create New"))
                {
                    var go = new GameObject("MetaGameClient");
                    Undo.RegisterCreatedObjectUndo(go, "Create MetaGameClient");
                    go.AddComponent(clientType);
                    Selection.activeGameObject = go;
                    return;
                }
                Selection.activeGameObject = existing.gameObject;
                EditorGUIUtility.PingObject(existing.gameObject);
                return;
            }

            var newGo = new GameObject("MetaGameClient");
            Undo.RegisterCreatedObjectUndo(newGo, "Create MetaGameClient");
            newGo.AddComponent(clientType);
            Selection.activeGameObject = newGo;

            EditorUtility.DisplayDialog("Success",
                "MetaGameClient GameObject created!\n\nConfigure the server URL in the Inspector.",
                "OK");
        }

        // ─── Shared Project ────────────────────────────────────────────────

        private void CreateSharedProject()
        {
            var unityDir = ResolveOutputDir(_sharedOutputDir);
            var dotnetDir = ResolveOutputDir(SharedDotnetDir);
            var ns = _sharedProjectName;
            var stateName = _sharedStateName;

            // Check for existing files
            if (Directory.Exists(unityDir) && Directory.GetFiles(unityDir, "*.cs").Length > 0)
            {
                if (!EditorUtility.DisplayDialog("Overwrite?",
                    $"Shared project files already exist at:\n{unityDir}\n\nOverwrite existing files?",
                    "Overwrite", "Cancel"))
                    return;
            }

            Directory.CreateDirectory(unityDir);
            Directory.CreateDirectory(dotnetDir);

            // Unity side: asmdef + source files
            File.WriteAllText(
                Path.Combine(unityDir, $"{_sharedProjectName}.asmdef"),
                GenerateSharedAsmdef(),
                new UTF8Encoding(false));

            if (_enableNullable)
            {
                File.WriteAllText(
                    Path.Combine(unityDir, "csc.rsp"),
                    "-nullable+\n",
                    new UTF8Encoding(false));
            }

            // Generate template-specific files
            switch (_templateIndex)
            {
                case 0: GenerateSimpleProfileFiles(unityDir, ns, stateName); break;
                case 1: GenerateOthelloFiles(unityDir, ns); break;
                case 2: GenerateExpeditionFiles(unityDir, ns); break;
            }

            File.WriteAllText(
                Path.Combine(unityDir, "AssemblyInfo.cs"),
                GenerateAssemblyInfo(),
                Encoding.UTF8);

            // .NET side: .csproj that links Unity source files
            var unityRelPath = ComputeRelativeDir(dotnetDir, unityDir);
            File.WriteAllText(
                Path.Combine(dotnetDir, $"{_sharedProjectName}.csproj"),
                GenerateSharedCsproj(unityRelPath),
                Encoding.UTF8);

            File.WriteAllText(
                Path.Combine(dotnetDir, ".gitignore"),
                "bin/\nobj/\n",
                Encoding.UTF8);

            // Copy framework documentation to shared project folder
            WriteDocFiles(unityDir);

            // Solution-level files (NuGet.Config, Directory.Packages.props, .sln)
            // are generated when the Server project is created, since they belong
            // at the solution root (common parent of shared and server).

            AssetDatabase.Refresh();

            var templateLabel = TemplateOptions[_templateIndex];
            EditorUtility.DisplayDialog("Success",
                $"Shared project created!\n\nTemplate: {templateLabel}\nUnity: {unityDir}\n.NET: {dotnetDir}\n\n" +
                "Next: create the Server project to generate the .sln solution.",
                "OK");
        }

        private string GenerateSharedAsmdef()
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"    \"name\": \"{_sharedProjectName}\",");
            sb.AppendLine($"    \"rootNamespace\": \"{_sharedProjectName}\",");
            sb.AppendLine("    \"references\": [\"SharedMeta.Runtime\"],");
            sb.AppendLine("    \"includePlatforms\": [],");
            sb.AppendLine("    \"excludePlatforms\": [],");
            sb.AppendLine("    \"allowUnsafeCode\": false,");
            sb.AppendLine("    \"overrideReferences\": false,");
            sb.AppendLine("    \"precompiledReferences\": [],");
            sb.AppendLine("    \"autoReferenced\": true,");
            sb.AppendLine("    \"defineConstraints\": [],");
            sb.AppendLine("    \"versionDefines\": [],");
            sb.AppendLine("    \"noEngineReferences\": true");
            sb.AppendLine("}");
            return sb.ToString();
        }

        // ─── Template: Simple Profile ───────────────────────────────────

        private void GenerateSimpleProfileFiles(string dir, string ns, string stateName)
        {
            WriteFile(Path.Combine(dir, "State"), $"{stateName}State.cs", GenSimpleProfileState(ns, stateName));
            WriteFile(Path.Combine(dir, "Services"), $"I{stateName}Service.cs", GenSimpleProfileInterface(ns, stateName));
            WriteFile(Path.Combine(dir, "Impl"), $"{stateName}Service.cs", GenSimpleProfileImpl(ns, stateName));
        }

        private string GenSimpleProfileState(string ns, string stateName)
        {
            var sb = new StringBuilder();
            AppendSerializerUsing(sb);
            sb.AppendLine("using SharedMeta.Core;");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            AppendSerializerAttr(sb);

            sb.AppendLine($"    public partial class {stateName}State : ISharedState");
            sb.AppendLine("    {");
            AppendProp(sb, 0, "string", "PlayerId", "\"\"");
            AppendProp(sb, 1, "string", "DisplayName", "\"\"");
            AppendProp(sb, 2, "int", "Level", "1");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private string GenSimpleProfileInterface(string ns, string stateName)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using SharedMeta.Core;");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            sb.AppendLine($"    [MetaService(StateType = typeof({stateName}State), AccessPolicy = EntityAccessPolicy.UserOwned)]");
            sb.AppendLine($"    public interface I{stateName}Service : IMetaService");
            sb.AppendLine("    {");
            sb.AppendLine("        [MetaMethod(Alias = \"Init\", Mode = ExecutionMode.Server)]");
            sb.AppendLine("        void Init(string playerId);");
            sb.AppendLine();
            sb.AppendLine("        [MetaMethod(Alias = \"SetName\", Mode = ExecutionMode.Server)]");
            sb.AppendLine("        void SetName(string name);");
            sb.AppendLine();
            sb.AppendLine("        [MetaMethod(Alias = \"AddExperience\")]");
            sb.AppendLine("        int AddExperience(int amount);");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private string GenSimpleProfileImpl(string ns, string stateName)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using SharedMeta.Core;");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            sb.AppendLine($"    [MetaServiceImpl(typeof(I{stateName}Service), typeof({stateName}State))]");
            sb.AppendLine($"    public partial class {stateName}Service : I{stateName}Service");
            sb.AppendLine("    {");
            sb.AppendLine("        public void Init(string playerId)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (State.PlayerId == playerId) return;");
            sb.AppendLine("            State.PlayerId = playerId;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public void SetName(string name)");
            sb.AppendLine("        {");
            sb.AppendLine("            State.DisplayName = name;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public int AddExperience(int amount)");
            sb.AppendLine("        {");
            sb.AppendLine("            State.Level += amount / 100;");
            sb.AppendLine("            return State.Level;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        // ─── Template: Othello ──────────────────────────────────────────

        private void GenerateOthelloFiles(string dir, string ns)
        {
            WriteFile(Path.Combine(dir, "State"), "ProfileState.cs", GenOthelloProfileState(ns));
            WriteFile(Path.Combine(dir, "Services"), "IProfileService.cs", GenOthelloProfileInterface(ns));
            WriteFile(Path.Combine(dir, "Impl"), "ProfileService.cs", GenOthelloProfileImpl(ns));
            WriteFile(Path.Combine(dir, "State"), "OthelloState.cs", GenOthelloGameState(ns));
            WriteFile(Path.Combine(dir, "Services"), "IOthelloService.cs", GenOthelloGameInterface(ns));
            WriteFile(Path.Combine(dir, "Impl"), "OthelloService.cs", GenOthelloGameImpl(ns));
        }

        private string GenOthelloProfileState(string ns)
        {
            var sb = new StringBuilder();
            AppendSerializerUsing(sb);
            sb.AppendLine("using SharedMeta.Core;");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            AppendSerializerAttr(sb);

            sb.AppendLine("    public partial class ProfileState : ISharedState");
            sb.AppendLine("    {");
            AppendProp(sb, 0, "string", "PlayerId", "\"\"");
            AppendProp(sb, 1, "string", "DisplayName", "\"\"");
            AppendProp(sb, 2, "int", "Wins");
            AppendProp(sb, 3, "int", "Losses");
            AppendProp(sb, 4, "bool", "IsSearching");
            AppendProp(sb, 5, "string?", "CurrentGameId");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private string GenOthelloProfileInterface(string ns)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System.Threading.Tasks;");
            sb.AppendLine("using SharedMeta.Core;");
            sb.AppendLine("using SharedMeta.Core.Framework;");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            sb.AppendLine("    [MetaService(StateType = typeof(ProfileState), AccessPolicy = EntityAccessPolicy.UserOwned,");
            sb.AppendLine("        SubscriberInterfaces = new[] { typeof(ILobbySubscriber) })]");
            sb.AppendLine("    public interface IProfileService : IMetaService");
            sb.AppendLine("    {");
            sb.AppendLine("        [MetaMethod(Alias = \"Init\", Mode = ExecutionMode.Server)]");
            sb.AppendLine("        void Init(string playerId);");
            sb.AppendLine();
            sb.AppendLine("        [MetaMethod(Alias = \"SetName\", Mode = ExecutionMode.Server)]");
            sb.AppendLine("        void SetName(string name);");
            sb.AppendLine();
            sb.AppendLine("        [MetaMethod(Alias = \"RequestMatch\", Mode = ExecutionMode.Server)]");
            sb.AppendLine("        Task<bool> RequestMatch();");
            sb.AppendLine();
            sb.AppendLine("        [MetaMethod(Alias = \"CancelMatch\", Mode = ExecutionMode.Server)]");
            sb.AppendLine("        Task CancelMatch();");
            sb.AppendLine();
            sb.AppendLine("        [MetaMethod(Alias = \"OnGameResult\", Mode = ExecutionMode.Server, GenerateClientApi = false)]");
            sb.AppendLine("        void OnGameResult(int result);");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private string GenOthelloProfileImpl(string ns)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System.Threading.Tasks;");
            sb.AppendLine("using SharedMeta.Core;");
            sb.AppendLine("using SharedMeta.Core.Framework;");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            sb.AppendLine("    [MetaServiceImpl(typeof(IProfileService), typeof(ProfileState), typeof(ILobbyRequester))]");
            sb.AppendLine("    public partial class ProfileService : IProfileService, ILobbySubscriber");
            sb.AppendLine("    {");
            sb.AppendLine("        public void Init(string playerId)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (State.PlayerId == playerId) return;");
            sb.AppendLine("            State.PlayerId = playerId;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public void SetName(string name) => State.DisplayName = name;");
            sb.AppendLine();
            sb.AppendLine("        public async Task<bool> RequestMatch()");
            sb.AppendLine("        {");
            sb.AppendLine("            var ok = await LobbyRequester.RequestMatchAsync(new MatchRequest");
            sb.AppendLine("            {");
            sb.AppendLine("                GameMode = \"othello\",");
            sb.AppendLine("                PlayerCount = 2,");
            sb.AppendLine("                MaxWaitSeconds = 60");
            sb.AppendLine("            });");
            sb.AppendLine("            if (ok) State.IsSearching = true;");
            sb.AppendLine("            return ok;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public async Task CancelMatch()");
            sb.AppendLine("        {");
            sb.AppendLine("            await LobbyRequester.CancelMatchAsync(\"othello\");");
            sb.AppendLine("            State.IsSearching = false;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public void OnGameResult(int result)");
            sb.AppendLine("        {");
            sb.AppendLine("            State.CurrentGameId = null;");
            sb.AppendLine("            if (result == 1) State.Wins++;");
            sb.AppendLine("            else if (result == -1) State.Losses++;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        // ILobbySubscriber");
            sb.AppendLine("        public void OnMatchFound(MatchFoundEvent e)");
            sb.AppendLine("        {");
            sb.AppendLine("            State.IsSearching = false;");
            sb.AppendLine("            State.CurrentGameId = e.MatchId;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public void OnMatchCancelled(MatchCancelledEvent e) => State.IsSearching = false;");
            sb.AppendLine("        public void OnMatchmakingUpdate(MatchmakingUpdateEvent e) { }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private string GenOthelloGameState(string ns)
        {
            var sb = new StringBuilder();
            AppendSerializerUsing(sb);
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using SharedMeta.Core;");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            sb.AppendLine("    public enum GamePhase { WaitingForPlayers, Playing, GameOver }");
            sb.AppendLine();
            AppendSerializerAttr(sb);

            sb.AppendLine("    public partial class OthelloState : ISharedState");
            sb.AppendLine("    {");
            sb.AppendLine("        public const int BoardSize = 8;");
            sb.AppendLine();
            AppendProp(sb, 0, "int[]", "Board", "new int[BoardSize * BoardSize]");
            AppendProp(sb, 1, "int", "CurrentPlayer", "1");
            AppendProp(sb, 2, "GamePhase", "Phase");
            AppendProp(sb, 3, "List<string>", "Players", "new()");
            AppendProp(sb, 4, "int", "ScoreBlack");
            AppendProp(sb, 5, "int", "ScoreWhite");
            AppendProp(sb, 6, "string?", "WinnerId");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private string GenOthelloGameInterface(string ns)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using SharedMeta.Core;");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            sb.AppendLine("    [MetaService(StateType = typeof(OthelloState))]");
            sb.AppendLine("    public interface IOthelloService : IMetaService");
            sb.AppendLine("    {");
            sb.AppendLine("        [MetaMethod(Alias = \"RegisterPlayer\", Mode = ExecutionMode.Server, GenerateClientApi = false)]");
            sb.AppendLine("        void RegisterPlayer(string playerId);");
            sb.AppendLine();
            sb.AppendLine("        [MetaMethod(Alias = \"NewGame\", Mode = ExecutionMode.Server)]");
            sb.AppendLine("        void NewGame();");
            sb.AppendLine();
            sb.AppendLine("        [MetaMethod(Alias = \"PlacePiece\")]");
            sb.AppendLine("        bool PlacePiece(int row, int col);");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private string GenOthelloGameImpl(string ns)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using SharedMeta.Core;");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            sb.AppendLine("    [MetaServiceImpl(typeof(IOthelloService), typeof(OthelloState))]");
            sb.AppendLine("    public partial class OthelloService : IOthelloService");
            sb.AppendLine("    {");
            sb.AppendLine("        public void RegisterPlayer(string playerId)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (!State.Players.Contains(playerId))");
            sb.AppendLine("                State.Players.Add(playerId);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public void NewGame()");
            sb.AppendLine("        {");
            sb.AppendLine("            State.Board = new int[OthelloState.BoardSize * OthelloState.BoardSize];");
            sb.AppendLine("            // Initial 4 pieces in the center");
            sb.AppendLine("            State.Board[27] = 2; State.Board[28] = 1;");
            sb.AppendLine("            State.Board[35] = 1; State.Board[36] = 2;");
            sb.AppendLine("            State.CurrentPlayer = 1;");
            sb.AppendLine("            State.Phase = GamePhase.Playing;");
            sb.AppendLine("            State.WinnerId = null;");
            sb.AppendLine("            UpdateScore();");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public bool PlacePiece(int row, int col)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (State.Phase != GamePhase.Playing) return false;");
            sb.AppendLine("            int idx = row * OthelloState.BoardSize + col;");
            sb.AppendLine("            if (State.Board[idx] != 0) return false;");
            sb.AppendLine();
            sb.AppendLine("            int flipped = FlipPieces(row, col, State.CurrentPlayer, apply: true);");
            sb.AppendLine("            if (flipped == 0) return false;");
            sb.AppendLine();
            sb.AppendLine("            State.Board[idx] = State.CurrentPlayer;");
            sb.AppendLine("            State.CurrentPlayer = 3 - State.CurrentPlayer; // Toggle 1 <-> 2");
            sb.AppendLine();
            sb.AppendLine("            // Skip turn if no valid moves");
            sb.AppendLine("            if (!HasValidMoves(State.CurrentPlayer))");
            sb.AppendLine("            {");
            sb.AppendLine("                State.CurrentPlayer = 3 - State.CurrentPlayer;");
            sb.AppendLine("                if (!HasValidMoves(State.CurrentPlayer))");
            sb.AppendLine("                    EndGame();");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine("            UpdateScore();");
            sb.AppendLine("            return true;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private int FlipPieces(int row, int col, int player, bool apply)");
            sb.AppendLine("        {");
            sb.AppendLine("            int total = 0;");
            sb.AppendLine("            int opponent = 3 - player;");
            sb.AppendLine("            int[] dr = { -1, -1, -1, 0, 0, 1, 1, 1 };");
            sb.AppendLine("            int[] dc = { -1, 0, 1, -1, 1, -1, 0, 1 };");
            sb.AppendLine();
            sb.AppendLine("            for (int d = 0; d < 8; d++)");
            sb.AppendLine("            {");
            sb.AppendLine("                int r = row + dr[d], c = col + dc[d], count = 0;");
            sb.AppendLine("                while (r >= 0 && r < 8 && c >= 0 && c < 8 && State.Board[r * 8 + c] == opponent)");
            sb.AppendLine("                { r += dr[d]; c += dc[d]; count++; }");
            sb.AppendLine();
            sb.AppendLine("                if (count > 0 && r >= 0 && r < 8 && c >= 0 && c < 8 && State.Board[r * 8 + c] == player)");
            sb.AppendLine("                {");
            sb.AppendLine("                    total += count;");
            sb.AppendLine("                    if (apply)");
            sb.AppendLine("                    {");
            sb.AppendLine("                        r = row + dr[d]; c = col + dc[d];");
            sb.AppendLine("                        for (int i = 0; i < count; i++)");
            sb.AppendLine("                        { State.Board[r * 8 + c] = player; r += dr[d]; c += dc[d]; }");
            sb.AppendLine("                    }");
            sb.AppendLine("                }");
            sb.AppendLine("            }");
            sb.AppendLine("            return total;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private bool HasValidMoves(int player)");
            sb.AppendLine("        {");
            sb.AppendLine("            for (int r = 0; r < 8; r++)");
            sb.AppendLine("                for (int c = 0; c < 8; c++)");
            sb.AppendLine("                    if (State.Board[r * 8 + c] == 0 && FlipPieces(r, c, player, apply: false) > 0)");
            sb.AppendLine("                        return true;");
            sb.AppendLine("            return false;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private void UpdateScore()");
            sb.AppendLine("        {");
            sb.AppendLine("            int b = 0, w = 0;");
            sb.AppendLine("            foreach (var cell in State.Board) { if (cell == 1) b++; else if (cell == 2) w++; }");
            sb.AppendLine("            State.ScoreBlack = b;");
            sb.AppendLine("            State.ScoreWhite = w;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private void EndGame()");
            sb.AppendLine("        {");
            sb.AppendLine("            State.Phase = GamePhase.GameOver;");
            sb.AppendLine("            if (State.ScoreBlack > State.ScoreWhite && State.Players.Count > 0)");
            sb.AppendLine("                State.WinnerId = State.Players[0];");
            sb.AppendLine("            else if (State.ScoreWhite > State.ScoreBlack && State.Players.Count > 1)");
            sb.AppendLine("                State.WinnerId = State.Players[1];");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        // ─── Template: Expedition ───────────────────────────────────────

        private void GenerateExpeditionFiles(string dir, string ns)
        {
            WriteFile(Path.Combine(dir, "State"), "ProfileState.cs", GenExpeditionProfileState(ns));
            WriteFile(Path.Combine(dir, "Services"), "IProfileService.cs", GenExpeditionProfileInterface(ns));
            WriteFile(Path.Combine(dir, "Impl"), "ProfileService.cs", GenExpeditionProfileImpl(ns));
            WriteFile(Path.Combine(dir, "State"), "ExpeditionState.cs", GenExpeditionState(ns));
            WriteFile(Path.Combine(dir, "Services"), "IExpeditionService.cs", GenExpeditionInterface(ns));
            WriteFile(Path.Combine(dir, "Impl"), "ExpeditionService.cs", GenExpeditionImpl(ns));
        }

        private string GenExpeditionProfileState(string ns)
        {
            var sb = new StringBuilder();
            AppendSerializerUsing(sb);
            sb.AppendLine("using SharedMeta.Core;");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            AppendSerializerAttr(sb);

            sb.AppendLine("    public partial class ProfileState : ISharedState");
            sb.AppendLine("    {");
            AppendProp(sb, 0, "string", "PlayerId", "\"\"");
            AppendProp(sb, 1, "string", "DisplayName", "\"\"");
            AppendProp(sb, 2, "int", "Energy", "50");
            AppendProp(sb, 3, "int", "MaxEnergy", "50");
            AppendProp(sb, 4, "int", "Money", "100");
            AppendProp(sb, 5, "long", "LastEnergyUpdateTicks");
            AppendProp(sb, 6, "int", "EnergyRegenSeconds", "10");
            AppendProp(sb, 7, "string?", "ActiveExpeditionId");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private string GenExpeditionProfileInterface(string ns)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System.Threading.Tasks;");
            sb.AppendLine("using SharedMeta.Core;");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            sb.AppendLine("    [MetaService(StateType = typeof(ProfileState), AccessPolicy = EntityAccessPolicy.UserOwned)]");
            sb.AppendLine("    public interface IProfileService : IMetaService");
            sb.AppendLine("    {");
            sb.AppendLine("        [MetaMethod(Alias = \"InitProfile\", Mode = ExecutionMode.Server)]");
            sb.AppendLine("        void InitProfile(string playerId);");
            sb.AppendLine();
            sb.AppendLine("        [MetaMethod(Alias = \"UpdateEnergy\")]");
            sb.AppendLine("        int UpdateEnergy();");
            sb.AppendLine();
            sb.AppendLine("        [MetaMethod(Alias = \"BuyEnergy\")]");
            sb.AppendLine("        bool BuyEnergy(int energyAmount, int moneyCost);");
            sb.AppendLine();
            sb.AppendLine("        [MetaMethod(Alias = \"StartExpedition\", Mode = ExecutionMode.Server)]");
            sb.AppendLine("        Task<string> StartExpedition();");
            sb.AppendLine();
            sb.AppendLine("        [MetaMethod(Alias = \"SpendEnergy\", Mode = ExecutionMode.Server, GenerateClientApi = false)]");
            sb.AppendLine("        bool SpendEnergy(int amount);");
            sb.AppendLine();
            sb.AppendLine("        [MetaMethod(Alias = \"AddMoney\", Mode = ExecutionMode.Server, GenerateClientApi = false)]");
            sb.AppendLine("        void AddMoney(int amount);");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private string GenExpeditionProfileImpl(string ns)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Threading.Tasks;");
            sb.AppendLine("using SharedMeta.Core;");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            sb.AppendLine($"    [MetaServiceImpl(typeof(IProfileService), typeof(ProfileState), typeof(IExpeditionService))]");
            sb.AppendLine("    public partial class ProfileService : IProfileService");
            sb.AppendLine("    {");
            sb.AppendLine("        public void InitProfile(string playerId)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (State.PlayerId == playerId) return;");
            sb.AppendLine("            State.PlayerId = playerId;");
            sb.AppendLine("            State.LastEnergyUpdateTicks = Context.ServerTimeTicks;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public int UpdateEnergy()");
            sb.AppendLine("        {");
            sb.AppendLine("            if (State.Energy >= State.MaxEnergy) return State.Energy;");
            sb.AppendLine("            long now = Context.ServerTimeTicks;");
            sb.AppendLine("            long elapsed = now - State.LastEnergyUpdateTicks;");
            sb.AppendLine("            int regenTicks = State.EnergyRegenSeconds * (int)TimeSpan.TicksPerSecond;");
            sb.AppendLine("            int gained = (int)(elapsed / regenTicks);");
            sb.AppendLine("            if (gained > 0)");
            sb.AppendLine("            {");
            sb.AppendLine("                State.Energy = Math.Min(State.Energy + gained, State.MaxEnergy);");
            sb.AppendLine("                State.LastEnergyUpdateTicks = now;");
            sb.AppendLine("            }");
            sb.AppendLine("            return State.Energy;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public bool BuyEnergy(int energyAmount, int moneyCost)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (State.Money < moneyCost) return false;");
            sb.AppendLine("            State.Money -= moneyCost;");
            sb.AppendLine("            State.Energy = Math.Min(State.Energy + energyAmount, State.MaxEnergy);");
            sb.AppendLine("            return true;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public async Task<string> StartExpedition()");
            sb.AppendLine("        {");
            sb.AppendLine("            if (!SpendEnergy(10)) return \"\";");
            sb.AppendLine("            var expeditionId = $\"expedition:{State.PlayerId}:{Context.ServerTimeTicks}\";");
            sb.AppendLine("            var expeditionService = GetIExpeditionService(expeditionId);");
            sb.AppendLine("            await expeditionService.InitAsync(State.PlayerId);");
            sb.AppendLine("            State.ActiveExpeditionId = expeditionId;");
            sb.AppendLine("            return expeditionId;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public bool SpendEnergy(int amount)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (State.Energy < amount) return false;");
            sb.AppendLine("            State.Energy -= amount;");
            sb.AppendLine("            return true;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public void AddMoney(int amount) => State.Money += amount;");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private string GenExpeditionState(string ns)
        {
            var sb = new StringBuilder();
            AppendSerializerUsing(sb);
            sb.AppendLine("using SharedMeta.Core;");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            sb.AppendLine("    public enum CellType { Empty, Wall, Treasure }");
            sb.AppendLine();
            AppendSerializerAttr(sb);

            // Named random streams — independent scroll per mechanic so adding a Loot roll
            // doesn't shift the Map generation sequence (and vice versa).
            sb.AppendLine("    [NamedRandom(\"Map\")]");
            sb.AppendLine("    [NamedRandom(\"Loot\")]");
            sb.AppendLine("    public partial class ExpeditionState : ISharedState");
            sb.AppendLine("    {");
            AppendProp(sb, 0, "string", "OwnerId", "\"\"");
            AppendProp(sb, 1, "int", "Width", "10");
            AppendProp(sb, 2, "int", "Height", "10");
            AppendProp(sb, 3, "int[]", "Cells", "new int[100]");
            AppendProp(sb, 4, "int", "PlayerX");
            AppendProp(sb, 5, "int", "PlayerY");
            AppendProp(sb, 6, "int", "TreasuresFound");
            AppendProp(sb, 7, "bool", "IsComplete");
            AppendProp(sb, 8, "string", "ProfileEntityId", "\"\"");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private string GenExpeditionInterface(string ns)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System.Threading.Tasks;");
            sb.AppendLine("using SharedMeta.Core;");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            sb.AppendLine("    [MetaService(StateType = typeof(ExpeditionState), AccessPolicy = EntityAccessPolicy.Authorized)]");
            sb.AppendLine("    public interface IExpeditionService : IMetaService");
            sb.AppendLine("    {");
            sb.AppendLine("        [MetaMethod(Alias = \"Init\", Mode = ExecutionMode.Server, GenerateClientApi = false)]");
            sb.AppendLine("        void Init(string profileEntityId);");
            sb.AppendLine();
            sb.AppendLine("        [MetaMethod(Alias = \"GenerateMap\")]");
            sb.AppendLine("        void GenerateMap();");
            sb.AppendLine();
            sb.AppendLine("        [MetaMethod(Alias = \"Move\", Mode = ExecutionMode.CrossOptimistic)]");
            sb.AppendLine("        Task<bool> Move(int dx, int dy);");
            sb.AppendLine();
            sb.AppendLine("        // Query — read-only, no subscription required on the client.");
            sb.AppendLine("        // Generated ExpeditionServiceQueryApi lets the client poll entity state");
            sb.AppendLine("        // (e.g. \"is this expedition still active?\") without subscribing.");
            sb.AppendLine("        [MetaMethod(Alias = \"IsActive\", Mode = ExecutionMode.Query)]");
            sb.AppendLine("        bool IsActive();");
            sb.AppendLine();
            sb.AppendLine("        // Signal — fire-and-forget, no response, no auto-retry, no broadcast side-effects.");
            sb.AppendLine("        // Server errors are swallowed. Good fit for heartbeat / telemetry / presence pings.");
            sb.AppendLine("        [MetaMethod(Alias = \"Ping\", Mode = ExecutionMode.Signal)]");
            sb.AppendLine("        void Ping(string clientTime);");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private string GenExpeditionImpl(string ns)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System.Threading.Tasks;");
            sb.AppendLine("using SharedMeta.Core;");
            sb.AppendLine();
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            sb.AppendLine($"    [MetaServiceImpl(typeof(IExpeditionService), typeof(ExpeditionState), typeof(IProfileService))]");
            sb.AppendLine("    public partial class ExpeditionService : IExpeditionService");
            sb.AppendLine("    {");
            sb.AppendLine("        public void Init(string profileEntityId)");
            sb.AppendLine("        {");
            sb.AppendLine("            State.ProfileEntityId = profileEntityId;");
            sb.AppendLine("            State.OwnerId = profileEntityId;");
            sb.AppendLine("            GenerateMap();");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public void GenerateMap()");
            sb.AppendLine("        {");
            sb.AppendLine("            State.Cells = new int[State.Width * State.Height];");
            sb.AppendLine("            State.PlayerX = 0;");
            sb.AppendLine("            State.PlayerY = 0;");
            sb.AppendLine("            State.TreasuresFound = 0;");
            sb.AppendLine("            State.IsComplete = false;");
            sb.AppendLine();
            sb.AppendLine("            // Use the [NamedRandom(\"Map\")] stream declared on ExpeditionState —");
            sb.AppendLine("            // the generator emits MapRandom on the Context so Map generation's scroll");
            sb.AppendLine("            // position is independent of other mechanics' random rolls.");
            sb.AppendLine("            for (int i = 0; i < State.Cells.Length; i++)");
            sb.AppendLine("            {");
            sb.AppendLine("                if (i == 0) continue; // Start cell always empty");
            sb.AppendLine("                int roll = MapRandom.Next(100);");
            sb.AppendLine("                if (roll < 15) State.Cells[i] = (int)CellType.Wall;");
            sb.AppendLine("                else if (roll < 25) State.Cells[i] = (int)CellType.Treasure;");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        // Query — read-only view into the entity. The framework wires a transient");
            sb.AppendLine("        // ServerMetaContext where State is a snapshot; mutations here are discarded.");
            sb.AppendLine("        public bool IsActive() => !State.IsComplete;");
            sb.AppendLine();
            sb.AppendLine("        // Signal — no response, no state mutation. For heartbeat / presence / telemetry.");
            sb.AppendLine("        // State is read-only inside a signal body; the framework throws if you mutate it.");
            sb.AppendLine("        public void Ping(string clientTime)");
            sb.AppendLine("        {");
            sb.AppendLine("            // Example: forward to logging/metrics here. No persistence or broadcasts.");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public async Task<bool> Move(int dx, int dy)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (State.IsComplete) return false;");
            sb.AppendLine("            int nx = State.PlayerX + dx, ny = State.PlayerY + dy;");
            sb.AppendLine("            if (nx < 0 || nx >= State.Width || ny < 0 || ny >= State.Height) return false;");
            sb.AppendLine();
            sb.AppendLine("            int idx = ny * State.Width + nx;");
            sb.AppendLine("            if (State.Cells[idx] == (int)CellType.Wall) return false;");
            sb.AppendLine();
            sb.AppendLine("            // Spend energy via cross-entity call");
            sb.AppendLine("            var profileService = GetIProfileService(State.ProfileEntityId);");
            sb.AppendLine("            var hasEnergy = await profileService.SpendEnergyAsync(1);");
            sb.AppendLine("            if (!hasEnergy) return false;");
            sb.AppendLine();
            sb.AppendLine("            State.PlayerX = nx;");
            sb.AppendLine("            State.PlayerY = ny;");
            sb.AppendLine();
            sb.AppendLine("            if (State.Cells[idx] == (int)CellType.Treasure)");
            sb.AppendLine("            {");
            sb.AppendLine("                State.Cells[idx] = (int)CellType.Empty;");
            sb.AppendLine("                State.TreasuresFound++;");
            sb.AppendLine("                // Independent [NamedRandom(\"Loot\")] stream — reward size doesn't");
            sb.AppendLine("                // shift the Map generator's scroll when you change the loot table.");
            sb.AppendLine("                int bonus = LootRandom.Next(5, 16);");
            sb.AppendLine("                await profileService.AddMoneyAsync(bonus);");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine("            return true;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        // ─── Template helpers ───────────────────────────────────────────

        private void AppendSerializerUsing(StringBuilder sb)
        {
            sb.AppendLine(_serializerIndex == 0 ? "using MemoryPack;" : "using MessagePack;");
        }

        private void AppendSerializerAttr(StringBuilder sb)
        {
            sb.AppendLine(_serializerIndex == 0
                ? "    [MemoryPackable(GenerateType.VersionTolerant)]"
                : "    [MessagePackObject]");
        }

        private void AppendProp(StringBuilder sb, int id, string type, string name, string? defaultValue = null)
        {
            var attr = _serializerIndex == 0
                ? $"[MemoryPackOrder({id})]"
                : $"[Key({id})]";
            var def = defaultValue != null ? $" = {defaultValue};" : "";
            sb.AppendLine($"        {attr} public {type} {name} {{ get; set; }}{def}");
        }

        private static void WriteFile(string dir, string fileName, string content)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, fileName), content, Encoding.UTF8);
        }

        private static string GenerateAssemblyInfo()
        {
            var sb = new StringBuilder();
            sb.AppendLine("using SharedMeta.Core;");
            sb.AppendLine();
            sb.AppendLine("[assembly: MetaSerializer(SerializerType.Generic)]");
            return sb.ToString();
        }

        private string GenerateSharedCsproj(string unityRelPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
            sb.AppendLine();
            sb.AppendLine("  <PropertyGroup>");
            sb.AppendLine("    <TargetFramework>net10.0</TargetFramework>");
            sb.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
            sb.AppendLine("    <Nullable>enable</Nullable>");
            sb.AppendLine("    <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>");
            sb.AppendLine("    <CompilerGeneratedFilesOutputPath>$(BaseIntermediateOutputPath)Generated</CompilerGeneratedFilesOutputPath>");
            sb.AppendLine("    <IsPackable>false</IsPackable>");
            sb.AppendLine("    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>");
            sb.AppendLine("  </PropertyGroup>");
            sb.AppendLine();
            sb.AppendLine("  <ItemGroup>");
            sb.AppendLine($"    <Compile Include=\"{unityRelPath}/**/*.cs\" Link=\"%(RecursiveDir)%(Filename)%(Extension)\" />");
            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine();
            sb.AppendLine("  <ItemGroup>");
            sb.AppendLine("    <PackageReference Include=\"CoreGame.SharedMeta.Core\" />");
            sb.AppendLine("    <PackageReference Include=\"CoreGame.SharedMeta.Client\" />");
            sb.AppendLine("    <PackageReference Include=\"CoreGame.SharedMeta.Generator\" OutputItemType=\"Analyzer\" ReferenceOutputAssembly=\"false\" />");
            if (_serializerIndex == 0)
                sb.AppendLine("    <PackageReference Include=\"MemoryPack\" />");
            else
                sb.AppendLine("    <PackageReference Include=\"MessagePack\" />");
            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine();
            sb.AppendLine("</Project>");
            return sb.ToString();
        }

        // ─── Server Project ─────────────────────────────────────────────

        private void CreateServerProject()
        {
            var outputDir = ResolveOutputDir(ServerOutputDir);
            if (!ConfirmOverwrite(outputDir, _serverProjectName))
                return;

            Directory.CreateDirectory(outputDir);

            var sharedDotnetDir = ResolveOutputDir(SharedDotnetDir);
            var sharedCsprojPath = Path.Combine(sharedDotnetDir, $"{_sharedProjectName}.csproj");
            var sharedRelPath = ComputeRelativePath(outputDir, sharedCsprojPath);

            // Determine solution root (common parent of server and shared .NET dirs)
            var solutionDir = FindCommonParent(outputDir, sharedDotnetDir);
            var solutionName = _sharedProjectName.Replace(".Shared", "");

            // .csproj — project reference path is relative to server dir
            File.WriteAllText(
                Path.Combine(outputDir, $"{_serverProjectName}.csproj"),
                GenerateServerCsproj(sharedRelPath),
                Encoding.UTF8);

            // Program.cs
            File.WriteAllText(
                Path.Combine(outputDir, "Program.cs"),
                GenerateServerProgram(),
                Encoding.UTF8);

            // .gitignore
            File.WriteAllText(
                Path.Combine(outputDir, ".gitignore"),
                "bin/\nobj/\ndata/\nlogs/\n",
                Encoding.UTF8);

            // Solution-level files at common root
            // CPM: Directory.Packages.props
            WriteDirectoryPackagesProps(solutionDir, isServer: true);

            // NuGet.Config if local NuGet is enabled
            if (_useLocalNuget && !string.IsNullOrEmpty(_localNugetPath))
                WriteNugetConfig(solutionDir);

            // .sln file
            WriteSolution(solutionDir, solutionName, sharedDotnetDir, outputDir);

            // Documentation files
            WriteDocFiles(outputDir);

            EditorUtility.DisplayDialog("Success",
                $"Server project created at:\n{outputDir}\n\n" +
                $"Solution: {Path.Combine(solutionDir, solutionName + ".sln")}\n\n" +
                $"Open the .sln in your IDE, or run:\ndotnet run --project {_serverProjectName}/{_serverProjectName}.csproj",
                "OK");
        }

        private string GenerateServerCsproj(string sharedRelPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk.Web\">");
            sb.AppendLine();
            sb.AppendLine("  <PropertyGroup>");
            sb.AppendLine("    <OutputType>Exe</OutputType>");
            sb.AppendLine("    <TargetFramework>net10.0</TargetFramework>");
            sb.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
            sb.AppendLine("    <Nullable>enable</Nullable>");
            sb.AppendLine("    <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>");
            sb.AppendLine("    <CompilerGeneratedFilesOutputPath>$(BaseIntermediateOutputPath)Generated</CompilerGeneratedFilesOutputPath>");
            sb.AppendLine("  </PropertyGroup>");
            sb.AppendLine();
            sb.AppendLine("  <ItemGroup>");
            sb.AppendLine("    <PackageReference Include=\"CoreGame.SharedMeta.Generator\" OutputItemType=\"Analyzer\" ReferenceOutputAssembly=\"false\" />");
            sb.AppendLine("    <PackageReference Include=\"CoreGame.SharedMeta.Server\" />");
            sb.AppendLine("    <PackageReference Include=\"CoreGame.SharedMeta.Server.Core\" />");
            sb.AppendLine("    <PackageReference Include=\"CoreGame.SharedMeta.Orleans\" />");

            if (IsServerSignalR)
            {
                sb.AppendLine("    <PackageReference Include=\"CoreGame.SharedMeta.Transport.SignalR\" />");
                // MessagePack protocol for SignalR lives in a separate optional package —
                // pulls in AddMetaMessagePackProtocol() used by Program.cs below.
                if (_serializerIndex == 1)
                    sb.AppendLine("    <PackageReference Include=\"CoreGame.SharedMeta.Transport.SignalR.MessagePack\" />");
            }
            else
                sb.AppendLine("    <PackageReference Include=\"CoreGame.SharedMeta.Transport.HttpPolling\" />");

            if (_serializerIndex == 0)
                sb.AppendLine("    <PackageReference Include=\"CoreGame.SharedMeta.Serialization.MemoryPack\" />");
            else
                sb.AppendLine("    <PackageReference Include=\"CoreGame.SharedMeta.Serialization.MessagePack\" />");

            if (_enableAuth)
                sb.AppendLine("    <PackageReference Include=\"CoreGame.SharedMeta.Auth\" />");

            sb.AppendLine("    <PackageReference Include=\"Microsoft.Orleans.Server\" />");
            sb.AppendLine("    <PackageReference Include=\"Serilog.AspNetCore\" />");
            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine();
            sb.AppendLine("  <ItemGroup>");
            sb.AppendLine($"    <ProjectReference Include=\"{sharedRelPath}\" />");
            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine();
            sb.AppendLine("</Project>");
            return sb.ToString();
        }

        private string GenerateServerProgram()
        {
            var sb = new StringBuilder();
            sb.AppendLine("using Microsoft.AspNetCore.Builder;");
            sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
            sb.AppendLine("using Microsoft.Extensions.Hosting;");
            sb.AppendLine("using Microsoft.Extensions.Logging;");
            sb.AppendLine("using Orleans;");
            sb.AppendLine("using Orleans.Configuration;");
            sb.AppendLine("using Orleans.Hosting;");
            sb.AppendLine("using SharedMeta.Core;");
            sb.AppendLine("using SharedMeta.Server.Core;");
            sb.AppendLine("using SharedMeta.Server.Core.Grains;");
            sb.AppendLine("using SharedMeta.Server.Core.Transport;");
            sb.AppendLine("using SharedMeta.Server.Core.Storage;");
            sb.AppendLine("using SharedMeta.Core.Framework;");
            sb.AppendLine("using SharedMeta.Orleans.Framework;");

            if (_serializerIndex == 0)
                sb.AppendLine("using SharedMeta.Serialization.MemoryPack;");
            else
                sb.AppendLine("using SharedMeta.Serialization.MessagePack;");

            if (IsServerSignalR)
                sb.AppendLine("using SharedMeta.Transport.SignalR;");
            else
                sb.AppendLine("using SharedMeta.Transport.HttpPolling;");

            if (_enableAuth)
                sb.AppendLine("using SharedMeta.Auth;");

            sb.AppendLine($"using {_sharedProjectName}.Server;");
            sb.AppendLine("using Serilog;");
            sb.AppendLine();

            // Port config
            sb.AppendLine("// Port configuration: pass as first arg, e.g. `dotnet run -- 5000`");
            sb.AppendLine($"var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : {_serverPort};");
            sb.AppendLine("var siloPort = 11111 + (port - 5000);");
            sb.AppendLine("var gatewayPort = 30000 + (port - 5000);");
            sb.AppendLine();

            sb.AppendLine("var builder = WebApplication.CreateBuilder(args);");
            sb.AppendLine($"builder.WebHost.UseUrls($\"http://localhost:{{port}}\");");
            sb.AppendLine();

            sb.AppendLine("builder.Host.UseSerilog((ctx, config) => config");
            sb.AppendLine("    // Surface SharedMeta diagnostic logs ([Desync], [Handler], transport-level info)");
            sb.AppendLine("    // that would otherwise be filtered by Serilog's default Information threshold.");
            sb.AppendLine("    .MinimumLevel.Override(\"SharedMeta\", Serilog.Events.LogEventLevel.Debug)");
            sb.AppendLine("    .WriteTo.Console());");
            sb.AppendLine();

            // Serializer
            if (_serializerIndex == 1)
            {
                sb.AppendLine("// MessagePack: configure composite resolver with generated resolvers from all assemblies");
                sb.AppendLine("GeneratedMetaMessagePackConfiguration.Configure();");
            }
            if (_serializerIndex == 0)
                sb.AppendLine("var serializer = new MemoryPackMetaSerializer();");
            else
                sb.AppendLine("var serializer = new MessagePackMetaSerializer();");
            sb.AppendLine("builder.Services.AddSingleton<IMetaSerializer>(serializer);");
            sb.AppendLine();

            // Orleans silo
            sb.AppendLine("// Orleans Silo");
            sb.AppendLine("builder.Host.UseOrleans(siloBuilder =>");
            sb.AppendLine("{");
            sb.AppendLine("    siloBuilder");
            sb.AppendLine("        .UseLocalhostClustering(siloPort, gatewayPort)");
            sb.AppendLine("        .Configure<ClusterOptions>(options =>");
            sb.AppendLine("        {");
            sb.AppendLine($"            options.ClusterId = \"{_serverProjectName.ToLower()}-cluster\";");
            sb.AppendLine($"            options.ServiceId = \"{_serverProjectName.ToLower()}-server\";");
            sb.AppendLine("        })");
            sb.AppendLine("        .AddFileGrainStorage(\"Default\", o => o.RootDirectory = \"./data\")");
            sb.AppendLine("        .ConfigureServices(services =>");
            sb.AppendLine("        {");
            sb.AppendLine("            services.AddSingleton<IMetaSerializer>(serializer);");
            sb.AppendLine("            services.Configure<EntityGrainOptions>(o => o.SubscriberTtl = TimeSpan.FromMinutes(10));");
            sb.AppendLine();
            sb.AppendLine("            services.ConfigureMeta(svc =>");
            sb.AppendLine("            {");
            sb.AppendLine("                // Register your server-side services here");
            sb.AppendLine("                // svc.AddTransient<IRandomService, RandomServiceImpl>();");
            sb.AppendLine("                svc.AddTransient<ILobbyRequester>(sp => new OrleansLobbyRequester(sp.GetRequiredService<IGrainFactory>()));");
            sb.AppendLine("            });");
            sb.AppendLine("        });");
            sb.AppendLine("});");
            sb.AppendLine();

            // Transport
            if (IsServerSignalR)
            {
                sb.AppendLine("// SignalR with MessagePack binary protocol");
                sb.AppendLine("builder.Services.AddSignalR(hubOptions =>");
                sb.AppendLine("{");
                sb.AppendLine("    if (builder.Environment.IsDevelopment())");
                sb.AppendLine("    {");
                sb.AppendLine("        hubOptions.EnableDetailedErrors = true;");
                sb.AppendLine("        hubOptions.ClientTimeoutInterval = TimeSpan.FromMinutes(30);");
                sb.AppendLine("        hubOptions.KeepAliveInterval = TimeSpan.FromMinutes(15);");
                sb.AppendLine("    }");
                sb.AppendLine("}).AddMetaMessagePackProtocol();");
            }
            else
            {
                sb.AppendLine("// HTTP Polling connection manager");
                sb.AppendLine("builder.Services.AddSingleton<HttpPollingConnectionManager>(sp =>");
                sb.AppendLine("    new HttpPollingConnectionManager(");
                sb.AppendLine("        sp.GetRequiredService<IMetaConnectionHandlerFactory>(),");
                sb.AppendLine("        sp.GetRequiredService<ILoggerFactory>()));");
            }
            sb.AppendLine();

            // MetaConnectionHandler factory
            sb.AppendLine("// MetaConnectionHandler factory");
            sb.AppendLine("builder.Services.AddSingleton<IMetaConnectionHandlerFactory>(sp =>");
            sb.AppendLine("{");
            sb.AppendLine("    var grainFactory = sp.GetRequiredService<IGrainFactory>();");
            sb.AppendLine("    var entityGrainResolver = sp.GetRequiredService<IEntityGrainResolver>();");
            sb.AppendLine("    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();");
            sb.AppendLine("    return new MetaConnectionHandlerFactory(grainFactory, entityGrainResolver, loggerFactory);");
            sb.AppendLine("});");
            sb.AppendLine();

            // Auth
            if (_enableAuth)
            {
                sb.AppendLine("// Authentication");
                sb.AppendLine("builder.Services.AddMetaAuth(options =>");
                sb.AppendLine("{");
                sb.AppendLine($"    options.SecretKey = \"{_serverProjectName.ToLower()}-secret-key-at-least-32-characters!\";");
                sb.AppendLine($"    options.Issuer = \"{_serverProjectName.ToLower()}-server\";");
                sb.AppendLine("});");
                sb.AppendLine("builder.Services.AddSingleton(new MetaTransportOptions { RequireAuthentication = true });");
                sb.AppendLine();
            }

            // CORS
            sb.AppendLine("// CORS");
            sb.AppendLine("builder.Services.AddCors(options =>");
            sb.AppendLine("{");
            sb.AppendLine("    options.AddDefaultPolicy(policy =>");
            sb.AppendLine("    {");
            sb.AppendLine("        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();");
            sb.AppendLine("    });");
            sb.AppendLine("});");
            sb.AppendLine();

            sb.AppendLine("var app = builder.Build();");
            sb.AppendLine();
            sb.AppendLine("app.UseCors();");

            if (_enableAuth)
            {
                sb.AppendLine("app.UseAuthentication();");
                sb.AppendLine("app.UseAuthorization();");
                sb.AppendLine("app.MapMetaAuth(\"/meta/auth\");");
            }
            sb.AppendLine();

            // Map transport endpoints
            if (IsServerSignalR)
            {
                sb.AppendLine("app.MapHub<MetaHub>(\"/meta\");");
            }
            else
            {
                sb.AppendLine("app.MapMetaHttpPolling(\"/meta\");");
            }

            sb.AppendLine($"app.MapGet(\"/\", () => \"{_serverProjectName} is running\");");
            sb.AppendLine();

            // Config download endpoint
            sb.AppendLine("// Config download endpoint — serves serialized config bytes for client-side caching.");
            sb.AppendLine("// Register your IMetaConfigProvider<TConfig> and uncomment:");
            sb.AppendLine("// app.MapGet(\"/meta/config/{major:int}/{minor:int}\", (int major, int minor, IMetaSerializer ser, IMetaConfigProvider<YourConfig> provider) =>");
            sb.AppendLine("// {");
            sb.AppendLine("//     var config = provider.GetConfig(\"\");");
            sb.AppendLine("//     return Results.Bytes(ser.Pack(config), \"application/octet-stream\");");
            sb.AppendLine("// });");
            sb.AppendLine();

            sb.AppendLine($"app.Logger.LogInformation(\"=== {_serverProjectName} ===\");");
            sb.AppendLine("app.Logger.LogInformation(\"Listening on http://localhost:{Port}\", port);");
            sb.AppendLine();
            sb.AppendLine("await app.RunAsync();");
            return sb.ToString();
        }

        // ─── Client Scripts ──────────────────────────────────────────────

        private void CreateClientProject()
        {
            var outputDir = ResolveOutputDir(_clientOutputDir);

            // Check for existing scripts
            var mainScript = Path.Combine(outputDir, "MetaGameClient.cs");
            if (File.Exists(mainScript))
            {
                if (!EditorUtility.DisplayDialog("Overwrite?",
                    $"Client scripts already exist at:\n{outputDir}\n\nOverwrite existing files?",
                    "Overwrite", "Cancel"))
                    return;
            }

            Directory.CreateDirectory(outputDir);

            // asmdef
            var clientAsmdefName = _sharedProjectName.Replace(".Shared", ".Client");
            File.WriteAllText(
                Path.Combine(outputDir, $"{clientAsmdefName}.asmdef"),
                GenerateClientAsmdef(clientAsmdefName),
                new UTF8Encoding(false));

            // MetaGameClient.cs
            File.WriteAllText(
                Path.Combine(outputDir, "MetaGameClient.cs"),
                GenerateMetaGameClient(),
                Encoding.UTF8);

            // UnityMetaLogger.cs
            File.WriteAllText(
                Path.Combine(outputDir, "UnityMetaLogger.cs"),
                GenerateUnityMetaLogger(),
                Encoding.UTF8);

            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog("Success",
                $"Client scripts generated at:\n{outputDir}\n\n- {clientAsmdefName}.asmdef\n- MetaGameClient.cs (MonoBehaviour)\n- UnityMetaLogger.cs (IMetaLogger)\n\n" +
                "Next: after Unity finishes compiling, use Setup Scene (Step 6) to create the MetaGameClient GameObject.",
                "OK");

            // Auto-advance to Setup Scene step in interactive mode
            if (_wizardMode == WizardMode.Interactive)
                _currentStep = 5;
        }

        private string GenerateMetaGameClient()
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using UnityEngine;");
            sb.AppendLine("using SharedMeta.Client;");
            if (_enableAuth)
            {
                sb.AppendLine("using SharedMeta.Client.Auth;");
                sb.AppendLine("using SharedMeta.Core.Auth;");
            }
            if (IsBestHttpTransport)
            {
                sb.AppendLine("using SharedMeta.Transport.BestHttp;");
                if (IsBestHttpSignalRMessagePack)
                    sb.AppendLine("using BestHTTP.SignalRCore.Encoders;");
            }
            else
                sb.AppendLine("using SharedMeta.Client.Network;");
            sb.AppendLine("using SharedMeta.Core;");
            sb.AppendLine("using SharedMeta.Core.Logging;");
            sb.AppendLine("using SharedMeta.Core.Transport;");

            if (_serializerIndex == 0)
                sb.AppendLine("using SharedMeta.Serialization.MemoryPack;");
            else
            {
                sb.AppendLine("using SharedMeta.Serialization.MessagePack;");
                if (IsBestHttpSignalRMessagePack)
                    sb.AppendLine("using MessagePack;");
            }

            sb.AppendLine($"using {_sharedProjectName}.Client;");

            sb.AppendLine();
            sb.AppendLine("/// <summary>");
            sb.AppendLine("/// SharedMeta client MonoBehaviour.");
            sb.AppendLine("/// Manages MetaClient lifecycle: connection, broadcast processing, cleanup.");
            sb.AppendLine("/// </summary>");
            sb.AppendLine("public class MetaGameClient : MonoBehaviour");
            sb.AppendLine("{");
            sb.AppendLine($"    [Header(\"Connection\")]");
            sb.AppendLine($"    public string serverUrl = \"{ServerUrl}\";");
            sb.AppendLine();
            sb.AppendLine("    public MetaClient Client { get; private set; }");
            sb.AppendLine();
            sb.AppendLine("    private async void Start()");
            sb.AppendLine("    {");
            sb.AppendLine("        MetaLog.SetLogger(new UnityMetaLogger());");
            sb.AppendLine();
            sb.AppendLine("        try");
            sb.AppendLine("        {");

            // Authentication
            if (_enableAuth)
            {
                sb.AppendLine("        // Authenticate — reuses cached token if still valid");
                sb.AppendLine("        // Auth URL uses server root (serverUrl points to hub path /meta)");
                sb.AppendLine("        var baseUrl = serverUrl.EndsWith(\"/meta\") ? serverUrl.Substring(0, serverUrl.Length - 5) : serverUrl;");
                sb.AppendLine("        var deviceId = SystemInfo.deviceUniqueIdentifier;");
                sb.AppendLine("        // Scope token storage by deviceId so multi-instance / random-deviceId dev builds");
                sb.AppendLine("        // don't share a JWT cached for a different PlayerId.");
                sb.AppendLine("        var tokenStorage = new PlayerPrefsTokenStorage(deviceId);");
                sb.AppendLine("        var login = await MetaAuth.EnsureAuthenticatedAsync(");
                sb.AppendLine("            baseUrl.TrimEnd('/') + \"/meta/auth\",");
                sb.AppendLine("            deviceId,");
                sb.AppendLine("            tokenStorage);");
                sb.AppendLine("        var accessToken = login.Token;");
                sb.AppendLine("        var playerId = login.PlayerId;");
                sb.AppendLine();
            }

            // Create connection
            switch (_transportIndex)
            {
                case 0: // SignalR
                    if (_enableAuth)
                        sb.AppendLine("        var connection = new SignalRConnection(serverUrl, accessToken);");
                    else
                        sb.AppendLine("        var connection = new SignalRConnection(serverUrl);");
                    break;
                case 1: // HTTP Polling (UnityWebRequest)
                    sb.AppendLine("        var connection = new UnityHttpConnection(new UnityHttpConnectionOptions");
                    sb.AppendLine("        {");
                    if (_enableAuth)
                        sb.AppendLine("            ServerUrl = serverUrl, AccessToken = accessToken");
                    else
                        sb.AppendLine("            ServerUrl = serverUrl");
                    sb.AppendLine("        });");
                    break;
                case 2: // BestHttp SignalR
                    if (_serializerIndex == 1) // MessagePack — use binary SignalR protocol
                    {
                        sb.AppendLine("        var connection = new BestHttpSignalRConnection(new BestHttpSignalRConnectionOptions");
                        sb.AppendLine("        {");
                        if (_enableAuth)
                        {
                            sb.AppendLine("            ServerUrl = serverUrl,");
                            sb.AppendLine("            AccessToken = accessToken,");
                        }
                        else
                        {
                            sb.AppendLine("            ServerUrl = serverUrl,");
                        }
                        sb.AppendLine("            Protocol = new MessagePackCSharpProtocol()");
                        sb.AppendLine("        });");
                    }
                    else
                    {
                        if (_enableAuth)
                        {
                            sb.AppendLine("        var connection = new BestHttpSignalRConnection(new BestHttpSignalRConnectionOptions");
                            sb.AppendLine("        {");
                            sb.AppendLine("            ServerUrl = serverUrl, AccessToken = accessToken");
                            sb.AppendLine("        });");
                        }
                        else
                        {
                            sb.AppendLine("        var connection = new BestHttpSignalRConnection(serverUrl);");
                        }
                    }
                    break;
                case 3: // BestHttp HTTP
                    sb.AppendLine("        var connection = new BestHttpPollingConnection(new BestHttpPollingConnectionOptions");
                    sb.AppendLine("        {");
                    if (_enableAuth)
                        sb.AppendLine("            ServerUrl = serverUrl, AccessToken = accessToken");
                    else
                        sb.AppendLine("            ServerUrl = serverUrl");
                    sb.AppendLine("        });");
                    break;
            }
            sb.AppendLine();

            // Create serializer
            if (_serializerIndex == 1)
            {
                sb.AppendLine("        // Configure MessagePack composite resolver (auto-generated)");
                sb.AppendLine("        GeneratedMetaMessagePackConfiguration.Configure();");
                if (IsBestHttpSignalRMessagePack)
                {
                    sb.AppendLine("        // BestHTTP MessagePackCSharpProtocol uses MessagePackSerializer.DefaultOptions");
                    sb.AppendLine("        MessagePackSerializer.DefaultOptions = MetaMessagePackOptions.Instance;");
                }
            }
            if (_serializerIndex == 0)
                sb.AppendLine("        var serializer = new MemoryPackMetaSerializer();");
            else
                sb.AppendLine("        var serializer = new MessagePackMetaSerializer();");
            sb.AppendLine();

            // Create MetaClient
            sb.AppendLine("        Client = new MetaClient(connection, serializer, new MetaClientOptions");
            sb.AppendLine("        {");
            if (_enableAuth)
                sb.AppendLine("            PlayerId = playerId,");
            else
                sb.AppendLine("            PlayerId = SystemInfo.deviceUniqueIdentifier,");
            sb.AppendLine("            // Optional hooks — implement and uncomment when needed:");
            sb.AppendLine("            // Diagnostics     = new MyDesyncDiagnostics(),     // IDesyncDiagnostics: OnRandomDesync/OnPatchDesync/OnResultMismatch callbacks");
            sb.AppendLine("            // ConnectionHealth = new MyConnectionHealth(),     // IConnectionHealth: Healthy ↔ Slow ↔ Unresponsive transitions for UI overlays");
            sb.AppendLine("        });");
            sb.AppendLine();
            sb.AppendLine("        // For deep request-lifecycle tracing, assign Client.Dispatcher.DiagnosticsLog");
            sb.AppendLine("        // to a file writer — it emits SEND/RECV/BATCH/CONFIRMED per request.");
            sb.AppendLine();
            sb.AppendLine("        Client.Resolver.RegisterAllServices();");
            sb.AppendLine();
            sb.AppendLine("        // Config download: enable file caching and download for server-provided configs.");
            sb.AppendLine("        // var resolver = (MetaServiceResolver)Client.Resolver;");
            sb.AppendLine("        // resolver.ConfigCache = new FileConfigCache(Application.persistentDataPath + \"/config-cache\", serializer);");
            sb.AppendLine("        // resolver.ConfigDownloader = new UnityConfigDownloader();");
            sb.AppendLine();
            sb.AppendLine("        Debug.Log(\"[SharedMeta] Connecting...\");");
            sb.AppendLine("        await Client.ConnectAsync();");
            sb.AppendLine("        Debug.Log($\"[SharedMeta] Connected! PlayerId: {Client.PlayerId}\");");
            sb.AppendLine();
            // Per-template usage example
            switch (_templateIndex)
            {
                case 0: // Simple Profile
                    sb.AppendLine("        // UserOwned service — convenience method (no entityId needed)");
                    sb.AppendLine($"        var profile = await Client.Get{_sharedStateName}ServiceAsync();");
                    sb.AppendLine("        await profile.SetNameAsync(\"Player\");");
                    sb.AppendLine($"        Debug.Log($\"Profile: {{profile.State.DisplayName}}, Level: {{profile.State.Level}}\");");
                    break;
                case 1: // Othello
                    sb.AppendLine("        // UserOwned service — convenience method (no entityId needed)");
                    sb.AppendLine("        var profile = await Client.GetProfileServiceAsync();");
                    sb.AppendLine("        await profile.SetNameAsync(\"Player\");");
                    sb.AppendLine("        Debug.Log($\"Profile: {profile.State.DisplayName}\");");
                    sb.AppendLine();
                    sb.AppendLine("        // To start matchmaking:");
                    sb.AppendLine("        // await profile.RequestMatchAsync();");
                    sb.AppendLine("        // After match found, access the game entity (Authorized — requires entityId):");
                    sb.AppendLine("        // var gameApi = await Client.GetServiceAsync<OthelloServiceApiClient>(gameEntityId);");
                    sb.AppendLine("        // var gameState = Client.GetState<OthelloState>(gameEntityId);");
                    break;
                case 2: // Expedition
                    sb.AppendLine("        // UserOwned service — convenience method (no entityId needed)");
                    sb.AppendLine("        var profile = await Client.GetProfileServiceAsync();");
                    sb.AppendLine("        await profile.InitProfileAsync(Client.PlayerId);");
                    sb.AppendLine("        await profile.UpdateEnergyAsync();");
                    sb.AppendLine("        Debug.Log($\"Energy: {profile.State.Energy}/{profile.State.MaxEnergy}, Money: {profile.State.Money}\");");
                    sb.AppendLine();
                    sb.AppendLine("        // Start expedition (returns entity ID for the new expedition)");
                    sb.AppendLine("        // var entityId = await profile.StartExpeditionAsync();");
                    sb.AppendLine();
                    sb.AppendLine("        // Authorized service — requires explicit entityId");
                    sb.AppendLine("        // var expApi = await Client.GetServiceAsync<ExpeditionServiceApiClient>(entityId);");
                    sb.AppendLine("        // var expState = Client.GetState<ExpeditionState>(entityId);");
                    sb.AppendLine();
                    sb.AppendLine("        // Signal (fire-and-forget heartbeat, no response awaited):");
                    sb.AppendLine("        //   expApi.PingSignal(System.DateTime.UtcNow.ToString(\"O\"));");
                    sb.AppendLine();
                    sb.AppendLine("        // Query (read-only, no subscription — useful to check state before subscribing):");
                    sb.AppendLine("        //   var query = new ExpeditionServiceQueryApi(connection, serializer).EntityApi(entityId);");
                    sb.AppendLine("        //   bool isActive = await query.IsActiveAsync();");
                    break;
            }
            sb.AppendLine("        }");
            sb.AppendLine("        catch (Exception ex)");
            sb.AppendLine("        {");
            sb.AppendLine("            Debug.LogError($\"[SharedMeta] Connection failed: {ex.Message}\");");
            sb.AppendLine("            Debug.LogException(ex);");
            sb.AppendLine("            // In a production UI: show a modal with a Reconnect button that calls Start()");
            sb.AppendLine("            // (or a dedicated Reconnect() method) after the player confirms. The");
            sb.AppendLine("            // Expedition sample under examples/Unity/Expedition demonstrates this pattern.");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    private void Update()");
            sb.AppendLine("    {");
            sb.AppendLine("        Client?.Dispatcher?.ProcessPendingBroadcasts();");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    private void OnDestroy()");
            sb.AppendLine("    {");
            sb.AppendLine("        Client?.Dispose();");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private string GenerateUnityMetaLogger()
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using SharedMeta.Core.Logging;");
            sb.AppendLine("using UnityEngine;");
            sb.AppendLine();
            sb.AppendLine("/// <summary>");
            sb.AppendLine("/// IMetaLogger implementation that routes SharedMeta logs to Unity console.");
            sb.AppendLine("/// </summary>");
            sb.AppendLine("public class UnityMetaLogger : IMetaLogger");
            sb.AppendLine("{");
            sb.AppendLine("    public bool IsEnabled(MetaLogLevel level) => true;");
            sb.AppendLine();
            sb.AppendLine("    public void Log(MetaLogLevel level, string message)");
            sb.AppendLine("    {");
            sb.AppendLine("        switch (level)");
            sb.AppendLine("        {");
            sb.AppendLine("            case MetaLogLevel.Error:");
            sb.AppendLine("                Debug.LogError($\"[SharedMeta] {message}\");");
            sb.AppendLine("                break;");
            sb.AppendLine("            case MetaLogLevel.Warning:");
            sb.AppendLine("                Debug.LogWarning($\"[SharedMeta] {message}\");");
            sb.AppendLine("                break;");
            sb.AppendLine("            default:");
            sb.AppendLine("                Debug.Log($\"[SharedMeta] {message}\");");
            sb.AppendLine("                break;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    public void Log(MetaLogLevel level, string message, Exception exception)");
            sb.AppendLine("    {");
            sb.AppendLine("        switch (level)");
            sb.AppendLine("        {");
            sb.AppendLine("            case MetaLogLevel.Error:");
            sb.AppendLine("                Debug.LogError($\"[SharedMeta] {message}\\n{exception}\");");
            sb.AppendLine("                break;");
            sb.AppendLine("            case MetaLogLevel.Warning:");
            sb.AppendLine("                Debug.LogWarning($\"[SharedMeta] {message}\\n{exception}\");");
            sb.AppendLine("                break;");
            sb.AppendLine("            default:");
            sb.AppendLine("                Debug.Log($\"[SharedMeta] {message}\\n{exception}\");");
            sb.AppendLine("                break;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private string GenerateClientAsmdef(string asmdefName)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"    \"name\": \"{asmdefName}\",");
            sb.AppendLine($"    \"rootNamespace\": \"\",");
            sb.AppendLine("    \"references\": [");
            sb.Append("        \"SharedMeta.Runtime\"");

            // Transport
            switch (_transportIndex)
            {
                case 0: // SignalR
                    sb.AppendLine(",");
                    sb.Append("        \"SharedMeta.Transport.SignalR.Client\"");
                    break;
                case 1: // HTTP Polling
                    sb.AppendLine(",");
                    sb.Append("        \"SharedMeta.Transport.Http\"");
                    break;
                case 2: // BestHttp SignalR
                    sb.AppendLine(",");
                    sb.Append("        \"SharedMeta.Transport.BestHttp.SignalR\"");
                    if (IsBestHttpSignalRMessagePack)
                    {
                        sb.AppendLine(",");
                        sb.Append("        \"BestHTTP\""); // for MessagePackCSharpProtocol
                    }
                    break;
                case 3: // BestHttp HTTP
                    sb.AppendLine(",");
                    sb.Append("        \"SharedMeta.Transport.BestHttp\"");
                    break;
            }

            // Serializer
            if (_serializerIndex == 0)
            {
                sb.AppendLine(",");
                sb.Append("        \"SharedMeta.Serialization.MemoryPack\"");
            }
            else
            {
                sb.AppendLine(",");
                sb.Append("        \"SharedMeta.Serialization.MessagePack\"");
            }

            sb.AppendLine(",");
            sb.Append($"        \"{_sharedProjectName}\"");

            // Auth
            if (_enableAuth)
            {
                sb.AppendLine(",");
                sb.Append("        \"SharedMeta.Auth.Client\"");
            }

            sb.AppendLine();
            sb.AppendLine("    ],");
            sb.AppendLine("    \"includePlatforms\": [],");
            sb.AppendLine("    \"excludePlatforms\": [],");
            sb.AppendLine("    \"allowUnsafeCode\": false,");
            sb.AppendLine("    \"overrideReferences\": false,");
            sb.AppendLine("    \"precompiledReferences\": [],");
            sb.AppendLine("    \"autoReferenced\": true,");
            sb.AppendLine("    \"defineConstraints\": [],");
            sb.AppendLine("    \"versionDefines\": [],");
            sb.AppendLine("    \"noEngineReferences\": false");
            sb.AppendLine("}");
            return sb.ToString();
        }

        // ─── Package Management ──────────────────────────────────────────

        /// <summary>UPM package name used by Client.List() for detection.</summary>
        private string GetSerializerPackageId() =>
            _serializerIndex == 0 ? "com.cysharp.memorypack" : "com.github.messagepack-csharp";

        private string GetSerializerPackageName() =>
            _serializerIndex == 0 ? "MemoryPack" : "MessagePack";

        /// <summary>Git URL for Client.Add() installation.</summary>
        private string GetSerializerGitUrl() => _serializerIndex == 0
            ? "https://github.com/Cysharp/MemoryPack.git?path=src/MemoryPack.Unity/Assets/MemoryPack.Unity"
            : "https://github.com/MessagePack-CSharp/MessagePack-CSharp.git?path=src/MessagePack.UnityClient/Assets/Scripts/MessagePack";

        private void CheckSerializerPackage()
        {
            _lastCheckedSerializerIndex = _serializerIndex;
            _serializerPkgStatus = PackageStatus.Checking;
            _serializerPkgError = "";
            _listRequest = Client.List();
        }

        private void PollPackageRequests()
        {
            // Poll list request (shared by serializer + HTTP transport checks)
            if (_listRequest != null && _listRequest.IsCompleted)
            {
                if (_listRequest.Status == StatusCode.Success)
                {
                    var targetId = GetSerializerPackageId();
                    _serializerPkgStatus = _listRequest.Result.Any(p => p.name == targetId)
                        ? PackageStatus.Installed
                        : PackageStatus.NotInstalled;

                    // Fallback: check loaded assemblies (NuGetForUnity installs don't show in UPM list)
                    if (_serializerPkgStatus == PackageStatus.NotInstalled)
                    {
                        var asmName = _serializerIndex == 0 ? "MemoryPack.Core" : "MessagePack";
                        if (IsAssemblyLoaded(asmName))
                            _serializerPkgStatus = PackageStatus.Installed;
                    }

                    // Ensure scripting define is set so stubs are excluded
                    if (_serializerPkgStatus == PackageStatus.Installed)
                        EnsureSerializerDefine();

                    // Also check HTTP transport dependency if pending (only index 1 uses UPM check)
                    if (_transportIndex == 1 && _transportPkgStatus == PackageStatus.Checking)
                    {
                        _transportPkgStatus = _listRequest.Result.Any(p => p.name == "com.unity.nuget.newtonsoft-json")
                            ? PackageStatus.Installed
                            : PackageStatus.NotInstalled;
                    }
                }
                else
                {
                    _serializerPkgStatus = PackageStatus.Error;
                    _serializerPkgError = _listRequest.Error?.message ?? "Failed to list packages";

                    if (_transportPkgStatus == PackageStatus.Checking)
                    {
                        _transportPkgStatus = PackageStatus.Error;
                        _transportPkgError = _listRequest.Error?.message ?? "Failed to list packages";
                    }
                }
                _listRequest = null;
                Repaint();
            }

            // Poll serializer add request
            if (_addRequest != null && _addRequest.IsCompleted)
            {
                var pkgName = GetSerializerPackageName();
                if (_addRequest.Status == StatusCode.Success)
                {
                    _serializerPkgStatus = PackageStatus.Installed;
                    EnsureSerializerDefine();
                    Debug.Log($"[SharedMeta] {pkgName} installed successfully");
                }
                else
                {
                    _serializerPkgStatus = PackageStatus.Error;
                    var errorCode = _addRequest.Error?.errorCode.ToString() ?? "unknown";
                    var errorMsg = _addRequest.Error?.message ?? "(no message)";
                    _serializerPkgError = $"{pkgName}: [{errorCode}] {errorMsg}";
                    Debug.LogError($"[SharedMeta] Package installation failed: {_serializerPkgError}");
                }
                _addRequest = null;
                Repaint();
            }

            // Poll transport add request (HTTP Newtonsoft or NuGetForUnity)
            if (_transportAddRequest != null && _transportAddRequest.IsCompleted)
            {
                var installedName = _transportAddRequest.Result?.displayName ?? "Package";
                if (_transportAddRequest.Status == StatusCode.Success)
                {
                    Debug.Log($"[SharedMeta] {installedName} installed successfully");
                    // Re-check dependencies (NuGetForUnity install → domain reload will also re-check)
                    CheckSerializerPackage();
                    CheckTransportDependency();
                }
                else
                {
                    _transportPkgStatus = PackageStatus.Error;
                    var errorCode = _transportAddRequest.Error?.errorCode.ToString() ?? "unknown";
                    var errorMsg = _transportAddRequest.Error?.message ?? "(no message)";
                    _transportPkgError = $"{installedName}: [{errorCode}] {errorMsg}";
                    Debug.LogError($"[SharedMeta] Package installation failed: {_transportPkgError}");
                }
                _transportAddRequest = null;
                Repaint();
            }
        }

        private void InstallSerializerPackage()
        {
            var packageName = GetSerializerPackageName();
            bool hasNuGetForUnity = IsNuGetForUnityInstalled();

            if (hasNuGetForUnity)
            {
                int choice = EditorUtility.DisplayDialogComplex(
                    $"Install {packageName}",
                    $"{packageName} can be installed via NuGetForUnity (recommended, resolves all dependencies)\n" +
                    "or via UPM git URL (may require manual dependency setup).",
                    "Install via NuGetForUnity",   // 0
                    "Cancel",                       // 1
                    "Install via UPM");             // 2

                switch (choice)
                {
                    case 0:
                        InstallSerializerViaNuGetForUnity();
                        break;
                    case 2:
                        InstallSerializerViaUpm();
                        break;
                }
            }
            else
            {
                int choice = EditorUtility.DisplayDialogComplex(
                    $"Install {packageName}",
                    $"NuGetForUnity is NOT installed.\n\n" +
                    "NuGetForUnity (recommended) resolves all dependencies automatically.\n" +
                    "UPM git URL may require manual dependency setup (e.g., Unsafe.dll for MemoryPack).",
                    "Install NuGetForUnity first",   // 0
                    "Cancel",                         // 1
                    "Install via UPM");               // 2

                switch (choice)
                {
                    case 0:
                        InstallNuGetForUnity();
                        break;
                    case 2:
                        InstallSerializerViaUpm();
                        break;
                }
            }
        }

        private void InstallSerializerViaUpm()
        {
            var gitUrl = GetSerializerGitUrl();
            _serializerPkgStatus = PackageStatus.Installing;
            _addRequest = Client.Add(gitUrl);
        }

        private void InstallSerializerViaNuGetForUnity()
        {
            var packageName = GetSerializerPackageName();
            if (!TryInvokeNuGetForUnityInstall(packageName, out var error))
            {
                Debug.LogError($"[SharedMeta] NuGetForUnity install failed: {error}");
                _serializerPkgStatus = PackageStatus.Error;
                _serializerPkgError = error ?? "Unknown error";
                return;
            }
            _serializerPkgStatus = PackageStatus.Installing;
            EnsureSerializerDefine();
            Debug.Log($"[SharedMeta] {packageName} installation initiated via NuGetForUnity");
        }

        /// <summary>
        /// Invokes NuGetForUnity to install a package by name. Returns false with error message on failure.
        /// </summary>
        private static bool TryInvokeNuGetForUnityInstall(string packageName, out string? error, string version = "")
        {
            error = null;
            var nugetAsm = FindNuGetForUnityAssembly();
            if (nugetAsm == null)
            {
                error = "NuGetForUnity assembly not loaded";
                return false;
            }

            // Find package identifier type
            var idType = FindType(nugetAsm, "NugetPackageIdentifier");
            if (idType == null)
            {
                error = $"NugetPackageIdentifier type not found in {nugetAsm.GetName().Name}";
                return false;
            }

            // Create package identifier instance
            var idCtor = idType.GetConstructor(new[] { typeof(string), typeof(string) })
                      ?? idType.GetConstructor(new[] { typeof(string) });
            if (idCtor == null)
            {
                error = $"No suitable constructor found on {idType.FullName}";
                return false;
            }

            var ctorParams = idCtor.GetParameters();
            var packageId = ctorParams.Length == 2
                ? idCtor.Invoke(new object[] { packageName, version })
                : idCtor.Invoke(new object[] { packageName });

            // Find install method across all types in the assembly.
            // Method signature: InstallIdentifier(INugetPackageIdentifier) — parameter is an interface,
            // so we search for methods where the first param is assignable from the concrete identifier type.
            MethodInfo? installMethod = null;
            foreach (var t in nugetAsm.GetExportedTypes())
            {
                installMethod = FindInstallMethod(t, idType);
                if (installMethod != null) break;
            }

            if (installMethod == null)
            {
                error = $"Install method not found in {nugetAsm.GetName().Name}";
                return false;
            }

            // Fill optional parameters with their default values
            var methodParams = installMethod.GetParameters();
            var invokeArgs = new object[methodParams.Length];
            invokeArgs[0] = packageId;
            for (int i = 1; i < methodParams.Length; i++)
                invokeArgs[i] = methodParams[i].HasDefaultValue ? methodParams[i].DefaultValue : Type.Missing;

            installMethod.Invoke(null, invokeArgs);
            return true;
        }

        private static Type? FindType(Assembly asm, string shortName)
        {
            return asm.GetExportedTypes().FirstOrDefault(t =>
                t.Name.Equals(shortName, StringComparison.OrdinalIgnoreCase));
        }

        private static MethodInfo? FindInstallMethod(Type type, Type concreteParamType)
        {
            string[] methodNames = { "InstallIdentifier", "Install", "InstallPackage" };
            foreach (var name in methodNames)
            {
                // Exact match first
                var m = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, new[] { concreteParamType }, null);
                if (m != null) return m;

                // Interface/base match: method may accept an interface (e.g. INugetPackageIdentifier)
                // that the concrete type implements
                foreach (var candidate in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (!string.Equals(candidate.Name, name, StringComparison.Ordinal)) continue;
                    var ps = candidate.GetParameters();
                    if (ps.Length >= 1 && ps[0].ParameterType.IsAssignableFrom(concreteParamType))
                        return candidate;
                }
            }
            return null;
        }

        // ─── Transport Dependency ────────────────────────────────────────

        private string GetTransportDependencyLabel() => _transportIndex switch
        {
            0 => "SignalR Client",
            1 => "Newtonsoft.Json (com.unity.nuget.newtonsoft-json)",
            2 or 3 => "BestHTTP2",
            _ => "Transport"
        };

        private void CheckTransportDependency()
        {
            _lastCheckedTransportIndex = _transportIndex;
            _transportPkgError = "";

            switch (_transportIndex)
            {
                case 0: // SignalR — detect DLLs via loaded assemblies
                {
                    var found = IsAssemblyLoaded("Microsoft.AspNetCore.SignalR.Client");
                    _transportPkgStatus = found ? PackageStatus.Installed : PackageStatus.NotInstalled;
                    if (found)
                        EnsureSignalRDefine();
                    break;
                }
                case 1: // HTTP Polling — check UPM package (Newtonsoft.Json)
                    _transportPkgStatus = PackageStatus.Checking;
                    if (_listRequest == null)
                        _listRequest = Client.List();
                    break;
                case 2: // BestHttp SignalR — detect via loaded assemblies (.unitypackage)
                case 3: // BestHttp HTTP — same assembly
                {
                    var found = IsBestHttpDetected();
                    _transportPkgStatus = found ? PackageStatus.Installed : PackageStatus.NotInstalled;
                    if (found)
                    {
                        EnsureScriptingDefine("HAS_BESTHTTP");
                        if (IsBestHttpSignalRMessagePack)
                            EnsureScriptingDefine("BESTHTTP_SIGNALR_CORE_ENABLE_MESSAGEPACK_CSHARP");
                    }
                    break;
                }
            }
        }

        private void InstallTransportPackage()
        {
            if (_transportIndex != 1) return; // Only HTTP (Newtonsoft) can auto-install

            if (!EditorUtility.DisplayDialog("Install Package",
                "Install Newtonsoft.Json (com.unity.nuget.newtonsoft-json)?\n\nRequired for HTTP Polling transport.",
                "Install", "Cancel"))
                return;

            _transportPkgStatus = PackageStatus.Installing;
            _transportAddRequest = Client.Add("com.unity.nuget.newtonsoft-json@3.2.1");
        }

        // ─── Dependency Installation Helpers ─────────────────────────────

        private static Assembly? FindNuGetForUnityAssembly()
        {
            // Type.GetType with assembly-qualified name is fragile — assembly name may differ between versions.
            // Search all loaded assemblies instead.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = asm.GetName().Name;
                if (name != null && name.IndexOf("NuGetForUnity", StringComparison.OrdinalIgnoreCase) >= 0)
                    return asm;
            }
            return null;
        }

        private static bool IsNuGetForUnityInstalled()
        {
            return FindNuGetForUnityAssembly() != null;
        }

        /// <summary>
        /// Checks if an assembly with the given simple name is loaded.
        /// More reliable than Type.GetType(assemblyQualifiedName) in Unity,
        /// because Unity's assembly resolver may not find NuGetForUnity-installed DLLs.
        /// </summary>
        private static bool IsAssemblyLoaded(string assemblySimpleName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(asm.GetName().Name, assemblySimpleName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private void ShowSignalRInstallDialog()
        {
            bool hasNuGetForUnity = IsNuGetForUnityInstalled();

            if (hasNuGetForUnity)
            {
                int choice = EditorUtility.DisplayDialogComplex(
                    "Install SignalR Client",
                    "SignalR requires multiple DLLs with transitive dependencies.\n\n" +
                    "NuGetForUnity detected — recommended for automatic installation.",
                    "Install via NuGetForUnity",   // 0
                    "Cancel",                       // 1
                    "I'll install manually");       // 2

                switch (choice)
                {
                    case 0:
                        InstallSignalRViaNuGetForUnity();
                        break;
                    case 2:
                        Debug.Log("[SharedMeta] Manual SignalR installation: place SignalR DLLs in Assets/Plugins/");
                        break;
                }
            }
            else
            {
                int choice = EditorUtility.DisplayDialogComplex(
                    "Install SignalR Client",
                    "SignalR requires multiple DLLs with transitive dependencies.\n\n" +
                    "NuGetForUnity is NOT installed. You can:\n" +
                    "• Install NuGetForUnity first (recommended), then retry\n" +
                    "• Place DLLs manually in Assets/Plugins/",
                    "Install NuGetForUnity",   // 0
                    "Cancel",                   // 1
                    "I'll install manually");   // 2

                switch (choice)
                {
                    case 0:
                        InstallNuGetForUnity();
                        break;
                    case 2:
                        Debug.Log("[SharedMeta] Manual SignalR installation: place SignalR DLLs in Assets/Plugins/");
                        break;
                }
            }
        }

        private void InstallSignalRViaNuGetForUnity()
        {
            if (!TryInvokeNuGetForUnityInstall("Microsoft.AspNetCore.SignalR.Client", out var error, "8.0.24"))
            {
                Debug.LogError($"[SharedMeta] NuGetForUnity install failed: {error}");
                _transportPkgStatus = PackageStatus.Error;
                _transportPkgError = error ?? "Unknown error";
                return;
            }
            _transportPkgStatus = PackageStatus.Installing;
            Debug.Log("[SharedMeta] SignalR installation initiated via NuGetForUnity");
            // After NuGetForUnity installs, domain reload will trigger OnEnable → CheckTransportDependency
        }

        private void InstallNuGetForUnity()
        {
            // NuGetForUnity installation triggers domain reload → OnEnable re-checks everything
            _serializerPkgStatus = PackageStatus.Installing;
            _transportPkgStatus = PackageStatus.Installing;
            _transportAddRequest = Client.Add("https://github.com/GlitchEnzo/NuGetForUnity.git?path=/src/NuGetForUnity");
            Debug.Log("[SharedMeta] Installing NuGetForUnity via UPM...");
        }

        private void EnsureSignalRDefine()
        {
            EnsureScriptingDefine("HAS_SIGNALR");
        }

        /// <summary>
        /// Detects BestHTTP2 core (com.tivadar.best.http) — installed as .unitypackage or UPM.
        /// Checks multiple known assembly names to handle different BestHTTP2 versions.
        /// </summary>
        private static bool IsBestHttpDetected()
        {
            return IsAssemblyLoaded("BestHTTP");
        }

        // BestHTTP bundles SignalR in the same assembly, so detection is identical.
        private static bool IsBestHttpSignalRDetected() => IsBestHttpDetected();

        private void EnsureSerializerDefine()
        {
            var symbol = _serializerIndex == 0 ? "HAS_MEMORYPACK" : "HAS_MESSAGEPACK";
            EnsureScriptingDefine(symbol);
        }

        private static void EnsureScriptingDefine(string symbol)
        {
            var namedTarget = UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(
                EditorUserBuildSettings.selectedBuildTargetGroup);
            PlayerSettings.GetScriptingDefineSymbols(namedTarget, out var defines);
            var defineList = new System.Collections.Generic.List<string>(defines);
            if (!defineList.Contains(symbol))
            {
                defineList.Add(symbol);
                PlayerSettings.SetScriptingDefineSymbols(namedTarget, defineList.ToArray());
                Debug.Log($"[SharedMeta] Added {symbol} scripting define");
            }
        }

        // ─── Documentation ───────────────────────────────────────────────

        private void WriteDocFiles(string outputDir)
        {
            // Try to copy docs from the package directory first
            var packageRoot = FindPackageRootDir();
            if (packageRoot != null)
            {
                CopyDocFile(packageRoot, outputDir, "SharedMeta-UserGuide.md");
                CopyDocFile(packageRoot, outputDir, "SharedMeta-AI.md");
            }
            else
            {
                // Fallback: generate embedded versions
                File.WriteAllText(
                    Path.Combine(outputDir, "SharedMeta-AI.md"),
                    GenerateAIInstructions(),
                    Encoding.UTF8);
            }
        }

        private static string? FindPackageRootDir()
        {
            // Check UPM package path (docs are at package root, not in docs/ subfolder)
            var candidates = new[]
            {
                "Packages/com.coregame.sharedmeta",
                Path.Combine(Application.dataPath, "../Packages/com.coregame.sharedmeta"),
            };
            foreach (var candidate in candidates)
            {
                var fullPath = Path.GetFullPath(candidate);
                if (Directory.Exists(fullPath))
                    return fullPath;
            }
            return null;
        }

        private static void CopyDocFile(string sourceDir, string destDir, string fileName)
        {
            var src = Path.Combine(sourceDir, fileName);
            if (File.Exists(src))
                File.Copy(src, Path.Combine(destDir, fileName), overwrite: true);
        }

        private static string GenerateAIInstructions()
        {
            return @"# SharedMeta Framework — AI Assistant Instructions

This file provides context for AI code assistants (Claude, Copilot, Cursor, etc.) working on projects that use the SharedMeta framework.

## What is SharedMeta

SharedMeta is a framework for shared game meta-logic between Client and Server. Game logic is written once in C# and runs on both the server (Orleans grains) and the client (Unity/.NET) with optimistic execution, automatic replay, and desync detection.

## Architecture

```
Client (Unity/.NET)                           Server (.NET + Orleans)
+----------------------+                     +-----------------------------+
| Game Code            |                     | MetaConnectionHandler       |
|   v                  |                     |   v                         |
| API Client (gen)     |                     | SessionManagerGrain         |
|   v                  |                     |   (per player)              |
| MetaClient           |   SignalR/HTTP      |   v                         |
|   v                  | <------------------>| EntityGrain<TState>         |
| ClientDispatcher     |                     |   (per entity)              |
|   v                  |                     |   v                         |
| IConnection          |                     | MetaProviderBase<TState>    |
| (SignalR/HTTP/       |                     |   v                         |
|  InProcess)          |                     | Service Dispatcher (gen)    |
+----------------------+                     |   v                         |
                                             | Service Implementation      |
                                             |   (your game logic)         |
                                             +-----------------------------+
```

**RPC call flow:**
1. Client calls `api.PlayCardAsync(card)` (generated API client)
2. Args serialized -> `IConnection.RpcCallAsync()`
3. Server `SessionManagerGrain` routes to `EntityGrain`
4. `EntityGrain` increments sequence -> `MetaProvider.HandleCallAsync()`
5. `MetaProvider` sets up context (Random, ServerRandom, Replay recording)
6. Generated dispatcher routes to `CardGameService.PlayCard(card)`
7. Result + replay payload returned up the chain
8. Client receives response, replays locally, returns result to game code

---

## Critical Rules

### 1. Serialization Attributes (REQUIRED)

All state and DTO classes need a **transport serializer** attribute (choose one based on project setup):

**With MemoryPack (use VersionTolerant for persisted state classes):**
```csharp
[MemoryPackable(GenerateType.VersionTolerant)]
public partial class MyState : ISharedState
{
    [MemoryPackOrder(0)] public string Name { get; set; }
    [MemoryPackOrder(1)] public int Value { get; set; }
}
```

**With MessagePack:**
```csharp
[MessagePackObject]
public partial class MyState : ISharedState
{
    [Key(0)] public string Name { get; set; }
    [Key(1)] public int Value { get; set; }
}
```

**With both (for cross-serializer compatibility):**
```csharp
[MemoryPackable(GenerateType.VersionTolerant), MessagePackObject]
public partial class MyState : ISharedState
{
    [Key(0), MemoryPackOrder(0)] public string Name { get; set; }
    [Key(1), MemoryPackOrder(1)] public int Value { get; set; }
}
```

**Attribute roles:**
- `[MemoryPackOrder(n)]` — MemoryPack field ordering. Without it, reordering/inserting fields breaks deserialization.
- `[Key(n)]` — MessagePack field ordering.
- States are persisted and transmitted as bytes via the chosen serializer. Orleans `[GenerateSerializer]`/`[Id(n)]` are NOT needed on game state/DTO classes.

### 2. Classes Must Be Partial

All state classes, service implementations, and DTO types must be `partial` — the source generator extends them.

### 3. Never Use System.Random

Use `Context.Random` (optimistic, identical xoshiro128** on both sides) or `Context.ServerRandom` (server-only, replayed on client).

### 4. Never Use DateTime.Now

Use `Context.ServerTimeTicks` (synchronized UTC ticks) instead.

### 5. Floating Point Is Not Deterministic

`float` and `double` arithmetic is NOT portable across platforms (x86 SSE vs ARM NEON, RyuJIT vs Mono).

**Safe in shared logic:** `int`, `long`, `decimal`, `Context.Random!.Next(int max)`
**NOT safe (Optimistic/CrossOptimistic):** `float`/`double` arithmetic, `Math.Sin`, `MathF.*`, `Context.Random!.NextFloat()`
**Fix:** Use fixed-point arithmetic or move to `ExecutionMode.Server`.

---

## Execution Modes

| Mode | When to Use |
|------|-------------|
| `ExecutionMode.Optimistic` | UI-responsive actions where client can predict result (move, play card) |
| `ExecutionMode.Server` | Actions needing `ServerRandom`, hidden state, or server-only data |
| `ExecutionMode.Local` | Client-only state changes (UI state), no server communication |
| `ExecutionMode.CrossOptimistic` | Cross-entity interactions (trading, multiplayer moves) |
| `ExecutionMode.ServerPatch` | Large state where sending diffs is more efficient than full state |

**Optimistic:** Client executes immediately, sends RPC, server executes authoritatively, client replays and validates.
**Server:** Client waits for server result, then replays with recorded ServerRandom values.
**CrossOptimistic:** Client executes on cached local state for both entities, server validates with real grain calls.

### Runtime Mode Override
```csharp
var modeProvider = client.ModeProvider as ExecutionModeProvider;
modeProvider.SetMode(""IProfileService"", ""SetName"", ExecutionMode.Server);
modeProvider.SetServiceMode(""IProfileService"", ExecutionMode.Server);
modeProvider.Clear(); // Reset to attribute defaults
```

---

## Shared State & Services

### State Definition
```csharp
[MemoryPackable(GenerateType.VersionTolerant)]  // or [MessagePackObject], or both
public partial class GameState : ISharedState
{
    [MemoryPackOrder(0)] public int Score { get; set; }
    [MemoryPackOrder(1)] public List<Player> Players { get; set; } = new();
    [MemoryPackOrder(2)] public GamePhase Phase { get; set; }
}
```

### Service Interface
```csharp
[MetaService(StateType = typeof(GameState), AccessPolicy = EntityAccessPolicy.Open)]
public interface ICardGameService : IMetaService
{
    [MetaMethod(Mode = ExecutionMode.Optimistic)]
    bool PlayCard(Card card);

    [MetaMethod(Mode = ExecutionMode.Server)]
    void DealCards();

    [MetaMethod(Mode = ExecutionMode.Local)]
    void SelectCardInHand(int index);

    [MetaMethod(Mode = ExecutionMode.CrossOptimistic)]
    Task<bool> TradeWith(string targetEntityId, Item item);
}
```

### Service Implementation
```csharp
[MetaServiceImpl(typeof(ICardGameService), typeof(GameState), typeof(IRandomService))]
public partial class CardGameServiceImpl : ICardGameService
{
    // Injected by source generator:
    // public MetaContext<GameState> Context { get; set; }
    // public GameState State => Context.State;
    // public string CallerId => Context.CallerId;
    // public IRandomService RandomService { get; set; }  // dependency

    public bool PlayCard(Card card)
    {
        if (!State.CurrentPlayer.Hand.Contains(card)) return false;
        State.CurrentPlayer.Hand.Remove(card);
        State.Table.Add(card);
        return true;
    }

    public void DealCards()
    {
        foreach (var player in State.Players)
        {
            for (int i = 0; i < 6; i++)
            {
                int idx = Context.ServerRandom!.Next(State.Deck.Count);
                player.Hand.Add(State.Deck[idx]);
                State.Deck.RemoveAt(idx);
            }
        }
    }
}
```

### Context Properties (auto-injected in [MetaServiceImpl])
- `Context.Random` — optimistic deterministic random (xoshiro128**)
- `Context.ServerRandom` — server-only random (null on client in Optimistic mode)
- `Context.ServerTimeTicks` — synchronized UTC ticks
- `Context.IsServer` / `Context.IsClient` — execution side
- `Context.ExecutionMode` — current execution mode
- `Context.EntityId` — current entity ID
- `State` — shortcut to `Context.State`
- `CallerId` — shortcut to `Context.CallerId`

---

## Cross-Entity Calls

Declare the target service as a dependency in [MetaServiceImpl] — the generator
injects a typed GetI{Service}(entityId) accessor into the partial impl:

```csharp
[MetaServiceImpl(typeof(IExpeditionService), typeof(ExpeditionState), typeof(IProfileService))]
public partial class ExpeditionService : IExpeditionService
{
    // Generator injects: GetIProfileService(string entityId).

    public async Task<MoveResult> Move(int dx, int dy)
    {
        var profile = GetIProfileService(State.ProfileEntityId!);
        bool spent = await profile.SpendEnergyAsync(Config.MoveCost);
        // ...
    }
}
```

Do NOT use Context.GetEntityApi<T>(id) — removed in 0.12.4. The generated
GetI{Service} method is the only supported entry point.

On server: resolves target grain, calls HandleCallFromEntityAsync.
On client (CrossOptimistic): executes on cached local state.

---

## Triggers & Subscribers

### Triggers — auto-execute after another method:
```csharp
[MetaMethod(Mode = ExecutionMode.Optimistic)]
void Defend(Card card);

[Trigger(On = ""Defend"", Condition = ""ShouldAutoEndAttack"")]
void OnDefendComplete();
```

### Subscriber Interfaces — framework events (e.g. matchmaking):
```csharp
[MetaService(StateType = typeof(ProfileState),
    SubscriberInterfaces = new[] { typeof(ILobbySubscriber) })]
public interface IProfileService : IMetaService
{
    [ServiceTrigger(Service = typeof(ILobbySubscriber), Method = ""OnMatchFound"")]
    void HandleMatchFound();
}
```

### Client-side subscriptions:
```csharp
var sub = resolver.OnMethodReplayed<MatchFoundArgs>(
    entityId, ""ILobbySubscriber"", ""OnMatchFound"",
    args => Console.WriteLine($""Match found: {args.MatchId}""));
sub.Dispose(); // when done
```

---

## Argument Transformers

Transform complex types into simple serializable types for RPC:
```csharp
[Transformer]
public class Vector3Transformer : IArgumentTransformer<Vector3, int[]>
{
    public int[] Box(Vector3 v) => new[] { v.X, v.Y, v.Z };
    public Vector3 Unbox(int[] a) => new Vector3(a[0], a[1], a[2]);
}

// State-aware:
[Transformer]
public class PlayerTransformer : IStateArgumentTransformer<Player, int, GameState>
{
    public int Box(Player player, GameState state) => player.Id;
    public Player Unbox(int id, GameState state) =>
        state.Players.FirstOrDefault(p => p.Id == id);
}
```

Usage: `[Transform(typeof(Vector3Transformer))]` or `[SkipTransform]` on parameters.

---

## Matchmaking (Lobby)

```csharp
// In IProfileService implementation:
public async Task RequestMatch(int playerCount)
{
    var lobbyRequester = Context.ResolveService<ILobbyRequester>();
    await lobbyRequester.RequestMatchAsync(
        Context.EntityId, Context.CallerId!, playerCount);
}
```

Flow: RequestMatch -> LobbyGrain queue -> match forms -> HandleExternalEventAsync -> [ServiceTrigger] fires -> broadcast to subscribers.

---

## Entity Access Policy

| Policy | Behavior |
|--------|----------|
| `Open` | Anyone can subscribe |
| `OwnerOnly` / `UserOwned` | Only if entityId == playerId |
| `Authorized` | Custom `CheckAccessAsync()` in MetaProvider |

---

## Persistence

| Policy | Behavior |
|--------|----------|
| `EveryCall` | Save after every RPC (default, safest) |
| `EveryNRequests(N)` | Save every N requests |
| `EveryNMinutes(M)` | Save when M minutes passed |
| `RequestsOrTime(N, M)` | N requests OR M minutes |
| `OnDeactivationOnly` | Max performance, risk of data loss |

ForcePersist: `[MetaMethod(ForcePersist = true)]` — always persist after this method (for purchases, currency).

---

## Code Generation

The source generator (`CoreGame.SharedMeta.Generator`) produces:
- `*Dispatcher.g.cs` — server-side method routing (switch-based)
- `*ApiClient.g.cs` — typed client API with async methods
- `*ServiceExtensions.g.cs` — DI registration helpers
- `*.Context.g.cs` — Context/State/dependency injection
- `ServerMetaConfiguration.g.cs` — MetaProvider + service registration
- `TransformerRegistrations.g.cs` — auto-registration of [Transformer] classes

**Do not write** dispatcher, API client, or context injection code manually.

---

## Attribute Reference

| Attribute | Target | Description |
|-----------|--------|-------------|
| `[MetaService]` | Interface | Marks shared service (StateType, AccessPolicy, SubscriberInterfaces) |
| `[MetaMethod]` | Method | Execution mode, Alias, Version, GenerateClientApi, SkipServerOnFalse, ForcePersist |
| `[MetaServiceImpl]` | Class | Marks implementation for context injection |
| `[Trigger]` | Method | Auto-execute after condition on another method |
| `[ServiceTrigger]` | Method | Trigger on framework service event |
| `[Transformer]` | Class | Register argument transformer |
| `[Transform]` | Parameter | Explicit transformer for parameter |
| `[SkipTransform]` | Parameter | Disable auto-transformation |
| `[MemoryPackable]` | Class | MemoryPack transport serialization |
| `[MessagePackObject]` | Class | MessagePack transport serialization |
| `[MemoryPackOrder(n)]` | Property | MemoryPack field ordering for version tolerance |
| `[Key(n)]` | Property | MessagePack field ordering for version tolerance |

---

## Common Patterns

### Adding a Method
1. Add to interface with `[MetaMethod(Mode = ...)]`
2. Implement in `[MetaServiceImpl]` class
3. New arg/return types need serializer attr (`[MemoryPackable]`/`[MessagePackObject]`) with `[MemoryPackOrder(n)]`/`[Key(n)]` on properties
4. Build — generator updates dispatchers and API clients

### Adding a Service
1. State class: serializer attr (`[MemoryPackable]`/`[MessagePackObject]`), `ISharedState`, `[MemoryPackOrder(n)]`/`[Key(n)]` on every property
2. Interface: `[MetaService(StateType = typeof(TState))]` extending `IMetaService`
3. Implementation: `[MetaServiceImpl(typeof(IService), typeof(TState))]`, must be `partial`
4. Server: `services.ConfigureMeta(svc => svc.AddTransient<IService, ServiceImpl>());`
5. Build — generator creates everything

### Adding New Fields (Version Tolerance)
- Never reuse or change existing `[MemoryPackOrder(n)]` / `[Key(n)]` values
- Always append with next sequential ID
- Use nullable types or defaults for new fields

### Server Setup
```csharp
builder.Host.UseOrleans(silo =>
{
    silo.UseLocalhostClustering()
        .AddFileGrainStorage(""Default"", o => o.RootDirectory = ""./data"")
        .ConfigureServices(services =>
        {
            services.AddSingleton<IMetaSerializer>(new MemoryPackMetaSerializer());
            services.ConfigureMeta(svc => { svc.AddTransient<IMyService, MyServiceImpl>(); });
        });
});
builder.Services.AddSignalR().AddMetaMessagePackProtocol();
app.MapHub<MetaHub>(""/meta"");
```

### Client Setup
```csharp
var connection = new SignalRConnection(serverUrl);
var serializer = new MemoryPackMetaSerializer();
var client = new MetaClient(connection, serializer, new MetaClientOptions { PlayerId = playerId });
client.Resolver.RegisterAllServices();
await client.ConnectAsync();

var api = await client.GetServiceAsync<IMyServiceApiClient>(entityId);
api.DoSomething(args);

// Main loop — required for game engines
while (true) { client.Dispatcher.ProcessPendingBroadcasts(); await Task.Delay(33); }
```

Unity: Call `ProcessPendingBroadcasts()` from `MonoBehaviour.Update()`.

---

## Pitfalls

1. Missing `partial` — build fails
2. Missing `[MemoryPackOrder(n)]`/`[Key(n)]` — serializer uses declaration order, reordering/inserting fields breaks deserialization
3. `System.Random` — guaranteed desync
4. Missing serializer attribute on nested types (`[MemoryPackable]`/`[MessagePackObject]`) — runtime exception
5. Async I/O in service methods — services are synchronous
6. Modifying state outside service methods — bypasses replay tracking
7. Reusing `[MemoryPackOrder(n)]`/`[Key(n)]` after removing fields — data corruption
8. `float`/`double` in Optimistic logic — platform-dependent desync
9. `DateTime.Now` instead of `Context.ServerTimeTicks` — clock difference
10. Dictionary iteration in deterministic logic — order not guaranteed

For full documentation see: https://github.com/CoreGameIO/SharedMeta
";
        }

        // ─── Helpers ─────────────────────────────────────────────────────

        private string ResolveOutputDir(string outputDir)
        {
            if (Path.IsPathRooted(outputDir))
                return outputDir;
            // Relative to Unity project root (parent of Assets/)
            var projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            return Path.GetFullPath(Path.Combine(projectRoot, outputDir));
        }

        private static string ComputeRelativePath(string fromDir, string toFile)
        {
            var fromUri = new Uri(fromDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar);
            var toUri = new Uri(toFile);
            var relativeUri = fromUri.MakeRelativeUri(toUri);
            return Uri.UnescapeDataString(relativeUri.ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        private static string ComputeRelativeDir(string fromDir, string toDir)
        {
            var fromUri = new Uri(fromDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar);
            var toUri = new Uri(toDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar);
            return Uri.UnescapeDataString(fromUri.MakeRelativeUri(toUri).ToString()).TrimEnd('/');
        }

        private static string FindCommonParent(string path1, string path2)
        {
            var dir1 = Path.GetFullPath(path1).Replace('\\', '/').TrimEnd('/');
            var dir2 = Path.GetFullPath(path2).Replace('\\', '/').TrimEnd('/');
            var parts1 = dir1.Split('/');
            var parts2 = dir2.Split('/');
            var common = new StringBuilder();
            for (int i = 0; i < Math.Min(parts1.Length, parts2.Length); i++)
            {
                if (!string.Equals(parts1[i], parts2[i], StringComparison.OrdinalIgnoreCase))
                    break;
                if (i > 0) common.Append(Path.DirectorySeparatorChar);
                common.Append(parts1[i]);
            }
            var result = common.ToString();
            return string.IsNullOrEmpty(result) ? path1 : result;
        }

        private void WriteSolution(string solutionDir, string solutionName,
            string sharedDotnetDir, string serverDir)
        {
            Directory.CreateDirectory(solutionDir);

            var sharedCsprojRel = ComputeRelativePath(solutionDir,
                Path.Combine(sharedDotnetDir, $"{_sharedProjectName}.csproj"));
            var serverCsprojRel = ComputeRelativePath(solutionDir,
                Path.Combine(serverDir, $"{_serverProjectName}.csproj"));

            var sharedGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
            var serverGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
            const string csharpProjectType = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00");
            sb.AppendLine("# Visual Studio Version 17");
            sb.AppendLine("VisualStudioVersion = 17.0.31903.59");
            sb.AppendLine("MinimumVisualStudioVersion = 10.0.40219.1");
            sb.AppendLine($"Project(\"{csharpProjectType}\") = \"{_sharedProjectName}\", \"{sharedCsprojRel}\", \"{sharedGuid}\"");
            sb.AppendLine("EndProject");
            sb.AppendLine($"Project(\"{csharpProjectType}\") = \"{_serverProjectName}\", \"{serverCsprojRel}\", \"{serverGuid}\"");
            sb.AppendLine("EndProject");
            sb.AppendLine("Global");
            sb.AppendLine("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution");
            sb.AppendLine("\t\tDebug|Any CPU = Debug|Any CPU");
            sb.AppendLine("\t\tRelease|Any CPU = Release|Any CPU");
            sb.AppendLine("\tEndGlobalSection");
            sb.AppendLine("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");
            foreach (var guid in new[] { sharedGuid, serverGuid })
            {
                sb.AppendLine($"\t\t{guid}.Debug|Any CPU.ActiveCfg = Debug|Any CPU");
                sb.AppendLine($"\t\t{guid}.Debug|Any CPU.Build.0 = Debug|Any CPU");
                sb.AppendLine($"\t\t{guid}.Release|Any CPU.ActiveCfg = Release|Any CPU");
                sb.AppendLine($"\t\t{guid}.Release|Any CPU.Build.0 = Release|Any CPU");
            }
            sb.AppendLine("\tEndGlobalSection");
            sb.AppendLine("EndGlobal");

            var slnPath = Path.Combine(solutionDir, solutionName + ".sln");
            File.WriteAllText(slnPath, sb.ToString(), Encoding.UTF8);
            Debug.Log($"[SharedMeta] Solution created at: {slnPath}");
        }

        private bool ConfirmOverwrite(string outputDir, string projectName)
        {
            var csprojPath = Path.Combine(outputDir, $"{projectName}.csproj");
            if (File.Exists(csprojPath))
            {
                return EditorUtility.DisplayDialog("Overwrite?",
                    $"A project already exists at:\n{outputDir}\n\nOverwrite existing files?",
                    "Overwrite", "Cancel");
            }
            return true;
        }

        /// <summary>
        /// Resolves the actual SharedMeta NuGet package version.
        /// When using local nupkgs, scans the folder for the real version (e.g., "0.1.0-local").
        /// Otherwise returns _sharedMetaVersion from package.json.
        /// </summary>
        private string ResolveSharedMetaPackageVersion()
        {
            // _sharedMetaVersion is the user-visible version field (auto-detected on init, editable in UI).
            // Always use it — it's the authoritative source for package version.
            return _sharedMetaVersion;
        }

        /// <summary>
        /// Writes Directory.Packages.props at <paramref name="outputDir"/> if none exists there.
        /// If the solution root already has a Directory.Packages.props, adds missing
        /// PackageVersion entries to it instead. Search is capped at <paramref name="outputDir"/>
        /// — we never walk above the solution boundary, so wizard runs inside unrelated
        /// repos (e.g. testing from within this SharedMeta repo) cannot accidentally
        /// mutate the enclosing repo's CPM file.
        /// </summary>
        private void WriteDirectoryPackagesProps(string outputDir, bool isServer)
        {
            // Check only the solution root itself — do not walk above.
            var existingPath = Path.Combine(outputDir, "Directory.Packages.props");
            if (File.Exists(existingPath))
            {
                AppendMissingPackageVersions(existingPath, isServer);
                return;
            }

            // No parent CPM — generate a standalone one
            var ver = ResolveSharedMetaPackageVersion();
            var sb = new StringBuilder();
            sb.AppendLine("<Project>");
            sb.AppendLine("  <PropertyGroup>");
            sb.AppendLine("    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>");
            sb.AppendLine("  </PropertyGroup>");
            sb.AppendLine();
            sb.AppendLine("  <ItemGroup>");
            sb.AppendLine($"    <PackageVersion Include=\"CoreGame.SharedMeta.Core\" Version=\"{ver}\" />");
            sb.AppendLine($"    <PackageVersion Include=\"CoreGame.SharedMeta.Client\" Version=\"{ver}\" />");
            sb.AppendLine($"    <PackageVersion Include=\"CoreGame.SharedMeta.Generator\" Version=\"{ver}\" />");

            if (isServer)
            {
                sb.AppendLine($"    <PackageVersion Include=\"CoreGame.SharedMeta.Server\" Version=\"{ver}\" />");
                sb.AppendLine($"    <PackageVersion Include=\"CoreGame.SharedMeta.Server.Core\" Version=\"{ver}\" />");
                sb.AppendLine($"    <PackageVersion Include=\"CoreGame.SharedMeta.Orleans\" Version=\"{ver}\" />");

                if (IsServerSignalR)
                {
                    sb.AppendLine($"    <PackageVersion Include=\"CoreGame.SharedMeta.Transport.SignalR\" Version=\"{ver}\" />");
                    if (_serializerIndex == 1)
                        sb.AppendLine($"    <PackageVersion Include=\"CoreGame.SharedMeta.Transport.SignalR.MessagePack\" Version=\"{ver}\" />");
                }
                else
                    sb.AppendLine($"    <PackageVersion Include=\"CoreGame.SharedMeta.Transport.HttpPolling\" Version=\"{ver}\" />");

                if (_serializerIndex == 0)
                    sb.AppendLine($"    <PackageVersion Include=\"CoreGame.SharedMeta.Serialization.MemoryPack\" Version=\"{ver}\" />");
                else
                    sb.AppendLine($"    <PackageVersion Include=\"CoreGame.SharedMeta.Serialization.MessagePack\" Version=\"{ver}\" />");

                if (_enableAuth)
                    sb.AppendLine($"    <PackageVersion Include=\"CoreGame.SharedMeta.Auth\" Version=\"{ver}\" />");

                sb.AppendLine("    <PackageVersion Include=\"Microsoft.Orleans.Server\" Version=\"10.0.0\" />");
                sb.AppendLine("    <PackageVersion Include=\"Serilog.AspNetCore\" Version=\"10.0.0\" />");
            }

            if (_serializerIndex == 0)
                sb.AppendLine("    <PackageVersion Include=\"MemoryPack\" Version=\"1.21.4\" />");
            else
                sb.AppendLine("    <PackageVersion Include=\"MessagePack\" Version=\"3.1.4\" />");

            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine("</Project>");

            File.WriteAllText(
                Path.Combine(outputDir, "Directory.Packages.props"),
                sb.ToString(),
                Encoding.UTF8);
        }

        private void AppendMissingPackageVersions(string propsPath, bool isServer)
        {
            var content = File.ReadAllText(propsPath);
            var ver = ResolveSharedMetaPackageVersion();
            var modified = false;

            var packages = new System.Collections.Generic.List<string>
            {
                "CoreGame.SharedMeta.Core",
                "CoreGame.SharedMeta.Client",
                "CoreGame.SharedMeta.Generator",
            };

            if (isServer)
            {
                packages.Add("CoreGame.SharedMeta.Server");
                packages.Add("CoreGame.SharedMeta.Server.Core");
                packages.Add("CoreGame.SharedMeta.Orleans");

                if (IsServerSignalR)
                {
                    packages.Add("CoreGame.SharedMeta.Transport.SignalR");
                    if (_serializerIndex == 1)
                        packages.Add("CoreGame.SharedMeta.Transport.SignalR.MessagePack");
                }
                else
                {
                    packages.Add("CoreGame.SharedMeta.Transport.HttpPolling");
                }

                packages.Add(_serializerIndex == 0
                    ? "CoreGame.SharedMeta.Serialization.MemoryPack"
                    : "CoreGame.SharedMeta.Serialization.MessagePack");

                if (_enableAuth)
                    packages.Add("CoreGame.SharedMeta.Auth");
            }

            // Update existing SharedMeta entries to the current version
            foreach (var pkg in packages)
            {
                var pattern = $"Include=\"{pkg}\" Version=\"";
                var idx = content.IndexOf(pattern, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    var verStart = idx + pattern.Length;
                    var verEnd = content.IndexOf('"', verStart);
                    if (verEnd > verStart)
                    {
                        var existingVer = content.Substring(verStart, verEnd - verStart);
                        if (existingVer != ver)
                        {
                            content = content.Substring(0, verStart) + ver + content.Substring(verEnd);
                            modified = true;
                        }
                    }
                }
            }

            // Also ensure the raw serializer NuGet package is present
            var serializerPkg = _serializerIndex == 0 ? "MemoryPack" : "MessagePack";
            var serializerVer = _serializerIndex == 0 ? "1.21.4" : "3.1.4";

            var linesToAdd = new StringBuilder();
            foreach (var pkg in packages)
            {
                if (!content.Contains($"\"{pkg}\""))
                    linesToAdd.AppendLine($"    <PackageVersion Include=\"{pkg}\" Version=\"{ver}\" />");
            }
            if (!content.Contains($"\"{serializerPkg}\""))
                linesToAdd.AppendLine($"    <PackageVersion Include=\"{serializerPkg}\" Version=\"{serializerVer}\" />");

            if (linesToAdd.Length > 0)
            {
                // Insert before the last </ItemGroup>
                var insertIndex = content.LastIndexOf("</ItemGroup>", StringComparison.Ordinal);
                if (insertIndex >= 0)
                {
                    content = content.Insert(insertIndex,
                        "\n    <!-- SharedMeta (auto-added by Wizard) -->\n" + linesToAdd.ToString());
                    modified = true;
                }
            }

            if (!modified) return;

            File.WriteAllText(propsPath, content, Encoding.UTF8);
            Debug.Log($"[SharedMeta] Updated {propsPath} with SharedMeta package versions");
        }

        private void WriteNugetConfig(string outputDir)
        {
            var nugetAbsPath = ResolveOutputDir(_localNugetPath);
            var relPath = ComputeRelativeDir(outputDir, nugetAbsPath);

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<configuration>");
            sb.AppendLine("  <packageSources>");
            sb.AppendLine("    <clear />");
            sb.AppendLine("    <add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" />");
            sb.AppendLine($"    <add key=\"SharedMeta-Local\" value=\"{relPath}\" />");
            sb.AppendLine("  </packageSources>");
            sb.AppendLine("</configuration>");

            File.WriteAllText(
                Path.Combine(outputDir, "NuGet.Config"),
                sb.ToString(),
                Encoding.UTF8);
        }

        private static string DetectLocalNugetPath()
        {
            // Resolve actual path of UPM package
            var projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            var packagePath = Path.GetFullPath(Path.Combine(projectRoot, "Packages/com.coregame.sharedmeta"));
            if (!Directory.Exists(packagePath)) return "";

            // nupkgs/ is at the repo root, package is in com.coregame.sharedmeta/ subfolder
            var parentDir = Directory.GetParent(packagePath)?.FullName;
            if (parentDir == null) return "";

            var nupkgsPath = Path.Combine(parentDir, "nupkgs");
            if (!Directory.Exists(nupkgsPath)) return "";

            // Make relative to Unity project root
            return ComputeRelativeDir(projectRoot, nupkgsPath);
        }

        private string DetectVersionFromLocalNupkg()
        {
            if (string.IsNullOrEmpty(_localNugetPath)) return "";

            var nugetDir = ResolveOutputDir(_localNugetPath);
            if (!Directory.Exists(nugetDir)) return "";

            var files = Directory.GetFiles(nugetDir, "CoreGame.SharedMeta.Core.*.nupkg");
            if (files.Length == 0) return "";

            var fileName = Path.GetFileNameWithoutExtension(files[0]);
            const string prefix = "CoreGame.SharedMeta.Core.";
            if (fileName.StartsWith(prefix))
                return fileName.Substring(prefix.Length);

            return "";
        }

        private static string DetectVersionFromPackageJson()
        {
            // Look for package.json in the package directory
            var guids = AssetDatabase.FindAssets("package t:TextAsset");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith("package.json") && path.Contains("SharedMeta") || path.Contains("sharedmeta"))
                {
                    var json = File.ReadAllText(path);
                    // Simple version extraction without JSON parser
                    var versionKey = "\"version\"";
                    var idx = json.IndexOf(versionKey, StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        var colonIdx = json.IndexOf(':', idx + versionKey.Length);
                        var quoteStart = json.IndexOf('"', colonIdx + 1);
                        var quoteEnd = json.IndexOf('"', quoteStart + 1);
                        if (quoteStart >= 0 && quoteEnd > quoteStart)
                            return json.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
                    }
                }
            }

            // Fallback: try Packages/com.coregame.sharedmeta/package.json
            var packageJsonPath = "Packages/com.coregame.sharedmeta/package.json";
            if (File.Exists(packageJsonPath))
            {
                var json2 = File.ReadAllText(packageJsonPath);
                var versionKey2 = "\"version\"";
                var idx2 = json2.IndexOf(versionKey2, StringComparison.Ordinal);
                if (idx2 >= 0)
                {
                    var colonIdx = json2.IndexOf(':', idx2 + versionKey2.Length);
                    var quoteStart = json2.IndexOf('"', colonIdx + 1);
                    var quoteEnd = json2.IndexOf('"', quoteStart + 1);
                    if (quoteStart >= 0 && quoteEnd > quoteStart)
                        return json2.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
                }
            }

            return "0.2.0";
        }

        // ─── Settings persistence (ProjectSettings/SharedMetaSettings.asset) ───

        private void SaveSettings()
        {
            var s = SharedMetaEditorSettings.instance;
            s.wizardVersion = _sharedMetaVersion;
            s.sharedProjectName = _sharedProjectName;
            s.sharedStateName = _sharedStateName;
            s.transportIndex = _transportIndex;
            s.serializerIndex = _serializerIndex;
            s.serverPort = _serverPort;
            s.enableAuth = _enableAuth;
            s.enableNullable = _enableNullable;
            s.useLocalNuget = _useLocalNuget;
            s.localNugetPath = _localNugetPath;
            s.sharedOutputDir = _sharedOutputDir;
            s.solutionDir = _solutionDir;
            s.serverProjectName = _serverProjectName;
            s.clientOutputDir = _clientOutputDir;
            s.templateIndex = _templateIndex;
            s.wizardMode = (int)_wizardMode;
            s.currentStep = _currentStep;
            s.Save();
        }

        private void LoadSettings()
        {
            var s = SharedMetaEditorSettings.instance;
            s.MigrateFromEditorPrefsIfNeeded();
            _sharedMetaVersion = s.wizardVersion;
            _sharedProjectName = s.sharedProjectName;
            _sharedStateName = s.sharedStateName;
            _transportIndex = s.transportIndex;
            _serializerIndex = s.serializerIndex;
            _serverPort = s.serverPort;
            _enableAuth = s.enableAuth;
            _enableNullable = s.enableNullable;
            _useLocalNuget = s.useLocalNuget;
            _localNugetPath = s.localNugetPath;
            _sharedOutputDir = string.IsNullOrEmpty(s.sharedOutputDir) ? $"Assets/Scripts/{_sharedProjectName}" : s.sharedOutputDir;
            _serverProjectName = s.serverProjectName;

            // Load solutionDir with migration from legacy fields
            if (!string.IsNullOrEmpty(s.solutionDir) && s.solutionDir != "../")
            {
                _solutionDir = s.solutionDir;
            }
            else if (!string.IsNullOrEmpty(s.serverOutputDir) && s.serverOutputDir != "../Server")
            {
                // Legacy migration: derive solutionDir from old serverOutputDir
                var parent = Path.GetDirectoryName(s.serverOutputDir)?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(parent))
                    _solutionDir = parent!;
            }
            else if (!string.IsNullOrEmpty(s.sharedDotnetDir) && s.sharedDotnetDir != $"../{_sharedProjectName}")
            {
                // Legacy migration: derive from old sharedDotnetDir
                var parent = Path.GetDirectoryName(s.sharedDotnetDir)?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(parent))
                    _solutionDir = parent!;
            }
            _clientOutputDir = s.clientOutputDir;
            _templateIndex = s.templateIndex;
            _wizardMode = (WizardMode)s.wizardMode;
            _currentStep = s.currentStep;
        }
    }
}
#endif
