using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;
using MCS;
using MCS.COSTUMING;
using MCS.FOUNDATIONS;
using MCS.SERVICES;
using MCS_Utilities.Morph;

namespace RadboudVR.Avatar 
{
    public class MCSToolkit : EditorWindow
    {          
        static ContentProcessor _activeProcess = new ContentProcessor();
        static ManifestSelection _manifestSelectionMethod = ManifestSelection.AutoDetect;

        #region Window
        
        // Detect Unity version to only show relevant conversion tools. Original MCS files were reported to work up to 2018.3
        #if UNITY_2018_4_OR_NEWER 
            static bool _enableConvert = true;
        #else
            static bool _enableConvert = false;
        #endif

        static bool _showConversionGroup = true;
        static bool _showEditGroup = false;
        static bool _showUtilGroup = true;
        static bool _showAdvancedConvertSettings = false;
        static bool _useSelection = false;
        static string _editMorphFieldText = "";
        static Vector2 _windowScroll;
        static Vector2 _editMorphScroll;

        

        [MenuItem("MCS/MCS Toolkit", false, 100)]
        public static void OpenWindow() 
        {
            GetWindow<MCSToolkit>("MCS Toolkit").Show();
        }

        void Update() 
        {
            // Run active process and repaint to update any progress bars.
            if (_activeProcess.isActive) {
                _activeProcess.Update();
                Repaint();
            }
        }

        void OnGUI() 
        {
            _windowScroll = EditorGUILayout.BeginScrollView(_windowScroll, GUILayout.ExpandHeight(true));

            // Converter Group
            BeginFoldoutGroup("Morph Converter", ref _showConversionGroup);
            if (_showConversionGroup) {
                using (new EditorGUI.DisabledScope(_enableConvert == true && _showAdvancedConvertSettings == false)) {
                    if (GUILayout.Button(new GUIContent("Extract Vertex Maps", "Extract vertex maps from MCS content\n(intended for 2017.x)")) && !_activeProcess.isActive) {
                        ExtractVertexMaps(_useSelection);
                    }
                    if (ProcessIsActive("Extract")) {
                        ShowProgress();
                    }
                }
                using (new EditorGUI.DisabledScope(_enableConvert == false && _showAdvancedConvertSettings == false)) {
                    if (GUILayout.Button(new GUIContent("Fix Morph Data", "Convert morph data using extracted vertex maps\n(intended for 2018.4+)")) && !_activeProcess.isActive) {
                        ConvertMorphData(_useSelection);
                    }
                    if (ProcessIsActive("Convert")) {
                        ShowProgress();
                    }
                }

                // Advanced settings
                _showAdvancedConvertSettings = EditorGUILayout.Foldout(_showAdvancedConvertSettings, new GUIContent("Advanced", "Here be dragons!"));
                if (_showAdvancedConvertSettings) {
                    _useSelection = EditorGUILayout.Toggle(new GUIContent("Use Selection", "Only process selected content"), _useSelection);
                    _manifestSelectionMethod = (ManifestSelection)EditorGUILayout.EnumPopup(new GUIContent("Manifest:", "Manually set base character manifest\nUSE ONLY IN CASE AUTODETECT FAILS"), _manifestSelectionMethod);
                }
            }
            EndFoldoutGroup();
            EditorGUILayout.Space();

            // Edit Group            
            BeginFoldoutGroup("Morph Editor", ref _showEditGroup);
            if (_showEditGroup) {
                EditorGUILayout.LabelField("Enter Morph Names (separate by comma):");
                _editMorphScroll = EditorGUILayout.BeginScrollView(_editMorphScroll, GUILayout.Height(48));
                _editMorphFieldText = EditorGUILayout.TextArea(_editMorphFieldText, GUILayout.ExpandHeight(true));
                EditorGUILayout.EndScrollView();
                if (GUILayout.Button(new GUIContent("Remove Morphs From Selected Assets", "")) && !_activeProcess.isActive) {
                    HashSet<string> morphList = new HashSet<string>();
                    string[] input = _editMorphFieldText.Replace(" ", "").Split(',');
                    foreach(string morph in input) {
                        if (morph != "") {
                            morphList.Add(morph);
                        }
                    }
                    RemoveMorphsFromSelected(new List<string>(morphList));
                }
                if (ProcessIsActive("Edit")) {
                    ShowProgress();
                }
            }
            EndFoldoutGroup();
            EditorGUILayout.Space();

            // Utility Group
            BeginFoldoutGroup("Utilities", ref _showUtilGroup);
            if (_showUtilGroup) {
                if (GUILayout.Button(new GUIContent("Remove LOD Groups", "Removes LOD Group components on all selected MCS Character Managers and attached costume items in scene")) && !_activeProcess.isActive) {
                    RemoveLOD();
                }
            }
            EndFoldoutGroup();

            EditorGUILayout.EndScrollView();
        }

        bool ProcessIsActive(string id) 
        {
            return _activeProcess.id == id && _activeProcess.isActive;
        }

        void ShowProgress()
        {
            EditorGUI.ProgressBar(GUILayoutUtility.GetLastRect(), _activeProcess.GetProgress(), _activeProcess.status);
        }

        // Unity 2019+ has a nicer looking foldoutHeader :3
        void BeginFoldoutGroup(string header, ref bool state) 
        {   
            GUILayout.BeginVertical(EditorStyles.helpBox);         
            #if UNITY_2019_1_OR_NEWER
            state = EditorGUILayout.BeginFoldoutHeaderGroup(state, header);
            #else
            state = EditorGUILayout.Foldout(state, header);
            #endif
        }

        void EndFoldoutGroup() 
        {
            #if UNITY_2019_1_OR_NEWER
            EditorGUILayout.EndFoldoutHeaderGroup();
            #endif
            GUILayout.EndVertical();
        }

        #endregion

        #region Morph Conversion

        static string _contentPath = "Assets/MCS/Content"; 
        static string _conversionMapPath = "Assets/MCS/ConversionMaps";
        static ConversionData _conversionData;

        /// <summary>
		/// Extract vertex maps for all MCS conten, or only selected folders if useSelection == true.
		/// </summary>
        public static void ExtractVertexMaps(bool useSelection) 
        {
            List<GameObject> content = GetContent(useSelection);
            if (content.Count == 0)
                return;

            _activeProcess = new ContentProcessor("Extract", content, true);
            _activeProcess.Process = delegate() {
                GameObject obj = _activeProcess.GetObject();
                if (obj == null)
                    return;

                CoreMesh[] meshes = obj.GetComponentsInChildren<CoreMesh>();
                foreach(CoreMesh mesh in meshes) {
                    string collection = GetCollectionName(mesh.runtimeMorphPath);

                    //_activeProcess.status = collection + ":" + mesh.name;

                    SkinnedMeshRenderer smr = mesh.GetComponent<SkinnedMeshRenderer>();
                    if (smr != null) {
                        string collectionConversionMapPath = _conversionMapPath + ((string.IsNullOrEmpty(collection) ? "" : "/" + collection));
                        Directory.CreateDirectory(collectionConversionMapPath);

                        VertexMap vertexMap = new VertexMap(smr.sharedMesh.vertices);
                        vertexMap.WriteToDisk(collectionConversionMapPath + "/" + mesh.name + ".json");
                    }
                }

                if (_activeProcess.isLast) {
                    if (EditorUtility.DisplayDialog("Complete!", "Extracted vertex maps were saved in Assets/MCS/ConversionMaps.\nYou can copy the files to your new Unity project.", "Show in Explorer", "Close")) {
                        EditorUtility.RevealInFinder(_conversionMapPath);
                    }
                }
            };
        } 

        /// <summary>
		/// Remaps morph data for all MCS content, or only selected folders if useSelection == true.
        /// </summary>
        public static void ConvertMorphData(bool useSelection)
        {
            if (!useSelection) {
                if (!EditorUtility.DisplayDialog("Warning", "This will attempt to convert all MCS morph data in your project. This process is nonreversible.\nAre you sure?", "Yes", "Cancel"))
                return;
            }

            List<GameObject> content = GetContent(useSelection);
            if (content.Count == 0) 
                return;

            // Load common conversion tools and data
            _conversionData = new ConversionData();

            _activeProcess = new ContentProcessor("Convert", content, true);
            _activeProcess.Process = delegate() {
                GameObject obj = _activeProcess.GetObject();
                if (obj == null)
                    return;

                CoreMesh[] meshes = obj.GetComponentsInChildren<CoreMesh>();
                foreach(CoreMesh mesh in meshes) {
                    //_activeProcess.status = mesh.name + " : Checking...";
                    _conversionData.CreateReport(mesh.name);

                    // Check if already converted
                    if (_conversionData.GetMorphData(mesh.runtimeMorphPath, "_2019compatible") != null) {
                        _conversionData.CloseReport("Skipped (already converted)");
                        continue; 
                    }

                    // Check smr
                    SkinnedMeshRenderer smr = mesh.GetComponent<SkinnedMeshRenderer>();
                    if (smr == null) {
                        _conversionData.CloseReport("Skipped (no SkinnedMeshRenderer found)");
                        continue;
                    }

                    // Check for original vertex map
                    string vmPath = "";
                    foreach(string path in _conversionData.vertexMaps) {
                        if (path.Contains(mesh.name + ".json")) {
                            vmPath = path;
                            break;
                        }
                    }
                    if (vmPath == "") {
                        _conversionData.CloseReport("Skipped (no vertex map found)");
                        continue;
                    }

                    // Create temp directory for generated .morph files
                    string morphPath = Path.Combine(Application.streamingAssetsPath, mesh.runtimeMorphPath);
                    Directory.CreateDirectory(morphPath);

                    // Run process                    
                    try {
                        // Read vertex map
                        string mapData = File.ReadAllText(vmPath);
                        VertexMap vertexMap = JsonUtility.FromJson<VertexMap>(mapData);

                        // Generate retarget map
                        //_activeProcess.status = mesh.name + " : Generating Target Map...";
                        Dictionary<int, int> ttsMap = _conversionData.projectionMeshMap.GenerateTargetToSourceMap(vertexMap.vertices, smr.sharedMesh.vertices);
                        
                        // Get manifest
                        var manifest = _conversionData.GetManifestForCoreMesh(mesh, _manifestSelectionMethod);

                        // Process morphs
                        int n = 0;
                        int total = manifest.names.Length;
                        List<string> morphNames = new List<string>(manifest.names);    
                        morphNames.Add("base"); // Add "base" morph that's not in the manifest but is required for clothing and hair

                        foreach(string morph in morphNames) {
                            //_activeProcess.status = string.Format("{0} : Processing Morph {1}/{2}", mesh.name, n, total);
                            n++;

                            MorphData source = _conversionData.GetMorphData(morphPath, morph); // Not all assets will have all morphs
                            if (source != null) {
                                // Retarget morphs
                                MorphData target = RemapMorphData(smr, source, ttsMap);
                                // Save new .morph file
                                MCS_Utilities.MorphExtraction.MorphExtraction.WriteMorphDataToFile(target, morphPath + "/" + target.name + ".morph", false, false);
                            }
                        }

                        // Inject evidence of conversion so we don't accidentally remap again.
                        MorphData note = new MorphData();
                        note.name = "_2019compatible";
                        MCS_Utilities.MorphExtraction.MorphExtraction.WriteMorphDataToFile(note, morphPath + "/" + note.name + ".morph",false, false);

                        // Repack morphs into .morph.mr file
                        _activeProcess.status = mesh.name + " : Repacking Morphs...";
                        RepackMorphs(morphPath);

                        _conversionData.CloseReport("Success");
                    } catch {
                        _conversionData.CloseReport("Failed");
                    } finally {                        
                        MCS_Utilities.Paths.TryDirectoryDelete(morphPath);
                    }
                }

                if(_activeProcess.isLast) {
                    _conversionData.PrintSummary();
                }
            };
        }

        /// <summary>
		/// Removes morphs from selected assets. Useful in cases like hair moving unnaturally with eye blinks.
        /// </summary>
        public static void RemoveMorphsFromSelected(List<string> morphNamesToBeRemoved) 
        {            
            List<GameObject> content = GetContent(true);
            if (content.Count == 0 || morphNamesToBeRemoved == null || morphNamesToBeRemoved.Count == 0)
                return;
            
            if (!EditorUtility.DisplayDialog("Warning", "This process is nonreversible.\nAre you sure?", "Yes", "Cancel"))
                return;    

            // Load common conversion tools and data
            _conversionData = new ConversionData();

            _activeProcess = new ContentProcessor("Edit", content, true);
            _activeProcess.Process = delegate() {
                GameObject obj = _activeProcess.GetObject();
                if (obj == null)
                    return;

                CoreMesh[] meshes = obj.GetComponentsInChildren<CoreMesh>();
                foreach(CoreMesh mesh in meshes) {
                    //_activeProcess.status = mesh.name + " : Checking...";

                    // Check smr
                    SkinnedMeshRenderer smr = mesh.GetComponent<SkinnedMeshRenderer>();
                    if (smr == null) {
                        continue;
                    }
                    
                    // Create temp directory for generated .morph files
                    string morphPath = Path.Combine(Application.streamingAssetsPath, mesh.runtimeMorphPath);
                    Directory.CreateDirectory(morphPath);

                    try {     
                        // Get manifest
                        var manifest = _conversionData.GetManifestForCoreMesh(mesh, _manifestSelectionMethod);

                        // Process morphs
                        List<string> morphNames = new List<string>(manifest.names);    
                        morphNames.Add("base"); // Add "base" morph that's not in the manifest but is required for clothing and hair

                        foreach(string morph in morphNames) {
                            MorphData data = _conversionData.GetMorphData(morphPath, morph);
                            if (data != null) {
                                if (morphNamesToBeRemoved.Contains(morph)) { 
                                    _activeProcess.status = mesh.name + " : Removed Morph " + morph;
                                }  else {
                                     // Save .morph file for keeping
                                    MCS_Utilities.MorphExtraction.MorphExtraction.WriteMorphDataToFile(data, morphPath + "/" + data.name + ".morph", false, false);
                                }
                            }
                        }

                        // Repack morphs into .morph.mr file
                        //_activeProcess.status = mesh.name + " : Rebuilding MR...";
                        RepackMorphs(morphPath);

                        //_activeProcess.status = mesh.name + " : Success!";
                    } catch {
                        _activeProcess.status = mesh.name + " : FAILED";
                    } finally {                        
                        MCS_Utilities.Paths.TryDirectoryDelete(morphPath);
                    }
                }
            };
        }

        static void RepackMorphs(string root) 
        {
            root = root.Replace(@"\", @"/");
            string output = root + ".morphs.mr";
            MCS_Utilities.MorphExtraction.MorphExtraction.MergeMorphsIntoMR(root, output);
        }

        static MorphData RemapMorphData(SkinnedMeshRenderer skinnedMeshRenderer, MorphData morphData, Dictionary<int, int> targetToSourceMap)
        {
            // This is taken from MCS StreamingMorphs's built-in ConvertMorphDataFromMap, but without requiring a projectionmap.

            Mesh mesh = skinnedMeshRenderer.sharedMesh;
            Vector3[] targetVertices = mesh.vertices;

            MorphData morphDataNew = new MorphData();
            morphDataNew.name = morphData.name;
            morphDataNew.jctData = morphData.jctData;
            morphDataNew.blendshapeData = new BlendshapeData();
            morphDataNew.blendshapeData.frameIndex = morphData.blendshapeData.frameIndex;
            morphDataNew.blendshapeData.shapeIndex = morphData.blendshapeData.shapeIndex;

            morphDataNew.blendshapeData.deltaVertices = new Vector3[targetVertices.Length];
            morphDataNew.blendshapeData.deltaNormals = new Vector3[targetVertices.Length];
            morphDataNew.blendshapeData.deltaTangents = new Vector3[targetVertices.Length];
            
            foreach (var ts in targetToSourceMap) {
                if (morphData.blendshapeData.deltaNormals != null) {
                    morphDataNew.blendshapeData.deltaNormals[ts.Key] = morphData.blendshapeData.deltaNormals[ts.Value];
                }
                if (morphData.blendshapeData.deltaVertices != null) {
                    if (ts.Key >= morphDataNew.blendshapeData.deltaVertices.Length) {
                        throw new System.Exception("ts.key in: " + skinnedMeshRenderer.name + " is too large for deltas: " + ts.Key + " => " + ts.Value + " | " + morphDataNew.blendshapeData.deltaVertices.Length);
                    }
                    if (ts.Value >= morphData.blendshapeData.deltaVertices.Length) {
                        throw new System.Exception("ts.value in: " + skinnedMeshRenderer.name + " is too large for deltas: " + ts.Key + " => " + ts.Value + " | " + morphData.blendshapeData.deltaVertices.Length);
                    }
                    morphDataNew.blendshapeData.deltaVertices[ts.Key] = morphData.blendshapeData.deltaVertices[ts.Value];
                }
                if (morphData.blendshapeData.deltaTangents != null) {
                    morphDataNew.blendshapeData.deltaTangents[ts.Key] = morphData.blendshapeData.deltaTangents[ts.Value];
                }
            }
            return morphDataNew;
        }

        static List<GameObject> GetContent(bool useSelection) 
        { 
            List<string> collections = new List<string>();

            if (useSelection) {
                string[] guids = Selection.assetGUIDs;
                foreach(string guid in guids) {
                    string collectionPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (Directory.Exists(collectionPath)) {
                        collections.Add(collectionPath);
                    }
                }
            } else {
                collections = new List<string>(Directory.GetDirectories(_contentPath));
            }

            List<string> paths = new List<string>();

            foreach(string collection in collections) {
                if (collection.EndsWith("MCSFemale") || collection.EndsWith("MCSMale")) {
                    // Base models just have one object that we know where to find
                    paths.Add(_contentPath + string.Format("/{0}/Figure/{0}/{0}.fbx", new DirectoryInfo(collection).Name));
                } else {
                    // Content Packs have prefabs.
                    var prefabs = AssetDatabase.FindAssets("t: Prefab", new string[] {collection});
                    foreach(var prefab in prefabs) {
                        paths.Add(AssetDatabase.GUIDToAssetPath(prefab));
                    }
                }
            }
            
            List<GameObject> content = new List<GameObject>();

            foreach(string path in paths) {
                GameObject go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                content.Add(go);
            }

            if (content.Count == 0) {
                string errorMsg = (useSelection) ? "No content selected!" : "No content found!";
                EditorUtility.DisplayDialog("Error", errorMsg, "OK");
            }

            return content;
        }

        static string GetCollectionName(string runtimeMorphPath) 
        {
            string[] split = runtimeMorphPath.Split('/');
            if (split.Length < 2 || split[0] != "MCS") {
                return null;
            }
            return split[1];
        }

        [System.Serializable]
        public class VertexMap
        {
            public Vector3[] vertices;

            public VertexMap(Vector3[] vertices) {
                this.vertices = vertices;
            }

            public void WriteToDisk(string path)
            {
                string data = JsonUtility.ToJson(this);
                System.IO.File.WriteAllText(path, data);
                
            }
        }

        public enum ManifestSelection {AutoDetect, MCSFemale, MCSMale};

        // Content processor lets us update GUI while processing, to show progress bar
        class ContentProcessor
        {
            public string id;
            public bool isActive;
            public int index;
            public List<GameObject> content;
            public string status;
            public System.Action Process;

            public bool isLast {get {return (content == null || index + 1 >= content.Count);}}

            public ContentProcessor(string id = "", List<GameObject> content = null, bool setActive = false) 
            {
                this.id = id;
                isActive = setActive;
                index = 0;
                this.content = content;
                this.status = "Processing...";
            }

            public void Update() 
            {
                while (index < content.Count) {
                    Process();
                    index ++;
                    break;
                }
                if (index == content.Count) {
                    isActive = false;
                    status = "Completed";
                }
            }

            public GameObject GetObject() {
                if (index < content.Count) {
                    return content[index];
                }
                return null;
            }

            public float GetProgress() 
            { 
                return (content != null) ? (index + 1) / (float)content.Count : 0; 
            }
        }

        class ConversionData 
        {
            public ProjectionMeshMap projectionMeshMap;
            public StreamingMorphs streamingMorphs;
            public string[] vertexMaps;
            public List<ConversionReport> reports;
            ConversionReport _currentReport;

            public ConversionData() 
            {
                // Initialize class
                projectionMeshMap = new ProjectionMeshMap();
                streamingMorphs = new StreamingMorphs();
                StreamingMorphs.LoadMainThreadAssetPaths();
                vertexMaps = (Directory.Exists(_conversionMapPath)) ? Directory.GetFiles(_conversionMapPath, "*.json", SearchOption.AllDirectories) : new string[] {};
                reports = new List<ConversionReport>();    
            }

            public MorphManifest GetManifestForCoreMesh(CoreMesh coreMesh, ManifestSelection selectionMethod)
            {
                string meshKey = selectionMethod.ToString();
                if (selectionMethod == ManifestSelection.AutoDetect) {
                    // There is no easy way to detect to which base avatar (MCSFemale/MCSMale) our asset belongs. 
                    // The most reliable way I could find is to check their morphdata for blendshapes that are unique to one gender.
                    // Not all objects are affected by all blendshapes (e.g. shoes don't have face morphs), so we'll pick a couple that affect the whole body to cover our bases.
                    // If this still wrongly identifies a mesh, pick manual selectionMethod.
                    bool isFemale = (GetMorphData(coreMesh.runtimeMorphPath, "FBMGogoBody") != null || GetMorphData(coreMesh.runtimeMorphPath, "FHMGogoHead") != null );
                    meshKey = (isFemale) ? "MCSFemale" : "MCSMale";
                } 
                
                if (_currentReport != null && _currentReport.name == coreMesh.name) {
                    _currentReport.manifest = meshKey;
                }
                return streamingMorphs.GetManifest(meshKey, false);
            }

            public MorphData GetMorphData(string basePath, string morphName)
            {
                // This will try to find morph data even if basePath isn't correct
                return streamingMorphs.GetMorphDataFromResources(basePath, morphName);
            }

            public void CreateReport(string name) 
            {
                _currentReport = new ConversionReport();
                _currentReport.name = name;
                _currentReport.result = "Report Created";
            }

            public void CloseReport(string result) 
            {
                _currentReport.result = result;
                reports.Add(_currentReport);
                _currentReport = null;
            }

            public void PrintSummary() 
            {
                string summary = "Conversion Complete!\nReport Summary:\n";
                foreach(ConversionReport report in reports) {
                    summary += string.Format("{0}\nManifest= {1}\nResult= {2}\n", report.name, report.manifest, report.result);
                }
                Debug.Log(summary);
            }
        }

        class ConversionReport 
        {
            public string name;
            public string manifest;
            public string result;
        }

        #endregion

        #region Utilities

        /// <summary>
		/// Remove LOD Group components from MCS Characters and attached Costume Items.
        /// </summary>
        public static void RemoveLOD()
        {
            GameObject[] currentSelection = Selection.gameObjects;
            List<MCSCharacterManager> selectedManagers = new List<MCSCharacterManager>();

            foreach (GameObject obj in currentSelection) {
                MCSCharacterManager manager = obj.GetComponent<MCSCharacterManager>();
                if (manager != null) {
                    selectedManagers.Add(manager);
                }
            }

            if (selectedManagers.Count == 0) {
                EditorUtility.DisplayDialog("Error", "No MCS Character Manager selected!", "OK");
                return;
            }

            foreach (MCSCharacterManager manager in selectedManagers) {
                LODGroup[] groups = manager.GetComponentsInChildren<LODGroup>(true);
                for(int i = 0; i < groups.Length; i++) {
                    DestroyImmediate(groups[i]);
                }
            }
            Debug.Log("LOD Groups in selected characters have been removed.");
        }

        #endregion

        #region Shader Conversion        

        private static readonly Dictionary<string,string> _shaderConversionTable = new Dictionary<string, string>() {
            {"Standard", "Standard" },
            {"MCS/Standard-2pass-double sided", "Standard"},
            {"MCS/Standard - Double Sided", "Standard" },
            {"MCS/Standard (Specular setup) - Double Sided", "Standard" },
            {"MCS/EyeAndLash", "EyeAndLash" },
            {"MCS/Volund Variants/Standard Character (Specular, Surface)", "Skin" }
        };

        // URP (only supported for 2019 or newer)

        #if UNITY_2019_1_OR_NEWER

        [MenuItem("MCS/Render Pipeline/Upgrade Project MCS Materials to URP Materials", false, 110)]
        public static void UpgradeProjectMaterialsToURP() 
        {
            if (!EditorUtility.DisplayDialog("Warning", "This will overwrite all MCS materials in your project. Are you sure?", "Yes", "Cancel"))
                return;

            List<Material> materials = new List<Material>();

            // Find all materials in MCS folder
            var matAssets = AssetDatabase.FindAssets("t: Material", new string[] {"Assets/MCS"});
            foreach(var asset in matAssets) {
                Material mat = AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(asset)) as Material;
                if (mat != null) {
                    materials.Add(mat);
                }
            }

            UpgradeMaterials(materials, "URP");    
        }

        [MenuItem("MCS/Render Pipeline/Upgrade Selected MCS Materials to URP Materials", false, 111)]
        public static void UpgradeSelectedMaterialsToURP() 
        {
            var selection = Selection.objects;

            if (selection == null) {
                EditorUtility.DisplayDialog("Error", "Nothing selected!", "OK");
                return;
            }

            List<Material> materials = new List<Material>();

            for (int i = 0; i < selection.Length; i++) {
                Material mat = selection[i] as Material;
                if (mat != null) {
                    materials.Add(mat);
                }
            }

            if (materials.Count == 0) {
                EditorUtility.DisplayDialog("Error", "No materials selected!", "OK");
                return;
            }

            UpgradeMaterials(materials, "URP");
        }

        [MenuItem("MCS/Render Pipeline/Upgrade Project MCS Materials to URP Materials", true)]
        [MenuItem("MCS/Render Pipeline/Upgrade Selected MCS Materials to URP Materials", true)]
        static bool CheckRP() {
            // This will disable the shader conversion menu items if no render pipeline is selected. TODO: differentiate URP and HDRP.
            return GraphicsSettings.currentRenderPipeline != null;
        }

        #endif

        static void UpgradeMaterials(List<Material> materials, string renderPipeline) 
        {
            List<string> missingShaders = new List<string>();

            int numMaterials = materials.Count;
            for (int i = 0; i < numMaterials; i++) {
                if (UnityEditor.EditorUtility.DisplayCancelableProgressBar("Converting Materials...", string.Format("{0} of {1}", i, numMaterials), (float)i / (float)numMaterials))
                    break;

                Material mat = materials[i];
                if (mat != null) {
                    string currentShader = mat.shader.name;
                    if (_shaderConversionTable.ContainsKey(currentShader)) {
                        string shaderName = "MCS/" + renderPipeline + "/" + _shaderConversionTable[currentShader];
                        Shader newShader = Shader.Find(shaderName);
                        if (newShader != null) {
                            mat.shader = newShader;
                        } else {
                            // Only log error on first encounter to prevent flood of errors in console.
                            if (!missingShaders.Contains(shaderName)) {
                                Debug.LogError("Shader Conversion Failed: Could not find shader '" + shaderName + "'");
                                missingShaders.Add(shaderName);                                
                            }
                        }
                    }
                }
            }

            UnityEditor.EditorUtility.ClearProgressBar();
        }

        #endregion

    }
}
