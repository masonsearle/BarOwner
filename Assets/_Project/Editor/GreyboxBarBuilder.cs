using System.IO;
using System.Linq;
using BarOwner.Interaction;
using BarOwner.Networking;
using BarOwner.Player;
using BarOwner.UI;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace BarOwner.EditorTools
{
    /// <summary>
    /// Menu: BarOwner > Build Greybox Bar Scene.
    /// Generates the networked Player prefab and a blockout bar scene (room, counter, shelves,
    /// tables, grabbable bottles/glasses/crates/kegs, a light switch, spawn points, NetworkManager,
    /// HUD). Safe to re-run: it overwrites the generated scene, prefab and materials.
    /// </summary>
    public static class GreyboxBarBuilder
    {
        private const string Root = "Assets/_Project";
        private const string PrefabDir = Root + "/Prefabs";
        private const string SceneDir = Root + "/Scenes";
        private const string MaterialDir = Root + "/Materials/Greybox";
        private const string ScenePath = SceneDir + "/Bar_Greybox.unity";
        private const string PlayerPrefabPath = PrefabDir + "/Player.prefab";

        [MenuItem("BarOwner/Build Greybox Bar Scene", priority = 0)]
        public static void Build()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            if (File.Exists(ScenePath) && !EditorUtility.DisplayDialog("Rebuild greybox bar?",
                    $"This overwrites {ScenePath} and {PlayerPrefabPath}. Any manual edits to them will be lost.",
                    "Rebuild", "Cancel"))
                return;

            EnsureFolder(PrefabDir);
            EnsureFolder(SceneDir);
            EnsureFolder(MaterialDir);

            var mats = new Palette();
            GameObject playerPrefab = BuildPlayerPrefab(mats);
            NetworkPrefabsList prefabList = GetOrCreatePrefabList();
            if (!prefabList.Contains(playerPrefab))
            {
                prefabList.Add(new NetworkPrefab { Prefab = playerPrefab });
                EditorUtility.SetDirty(prefabList);
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.16f, 0.14f, 0.18f);

            Light[] barLights = BuildRoom(mats);
            BuildProps(mats);
            BuildLightSwitch(mats, barLights);
            BuildSpawnPoints();
            GameObject lobbyCamera = BuildLobbyCamera();
            BuildNetworkManager(playerPrefab, prefabList);
            BuildHud(lobbyCamera);

            EditorSceneManager.SaveScene(scene, ScenePath);

            // In-scene NetworkObjects get their GlobalObjectIdHash from the *saved* scene, so reopen
            // and re-save once to make sure every hash is generated and stored.
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            foreach (var no in Object.FindObjectsByType<NetworkObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                EditorUtility.SetDirty(no);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            AddSceneToBuildSettings(ScenePath);
            AssetDatabase.SaveAssets();

            Debug.Log("[BarOwner] Greybox bar built. Press Play, then H to host (or J to join).");
        }

        // ------------------------------------------------------------------ player

        private static GameObject BuildPlayerPrefab(Palette mats)
        {
            var root = new GameObject("Player");

            var controller = root.AddComponent<CharacterController>();
            controller.height = 1.8f;
            controller.radius = 0.35f;
            controller.center = new Vector3(0f, 0.9f, 0f);
            controller.stepOffset = 0.35f;
            controller.skinWidth = 0.04f;

            root.AddComponent<NetworkObject>();
            ConfigureNetworkTransform(root.AddComponent<NetworkTransform>(), syncPitchRoll: false);

            var input = root.AddComponent<PlayerInputReader>();
            input.enabled = false; // PlayerMotor enables it for the owner only
            var motor = root.AddComponent<PlayerMotor>();
            var interactor = root.AddComponent<PlayerInteractor>();

            // Body (hidden from your own camera, visible to everyone else).
            var body = Primitive(PrimitiveType.Capsule, "Body", root.transform, new Vector3(0f, 0.9f, 0f),
                new Vector3(0.7f, 0.9f, 0.7f), mats.Player, keepCollider: false);

            var pivot = new GameObject("CameraPivot").transform;
            pivot.SetParent(root.transform, false);
            pivot.localPosition = new Vector3(0f, 1.6f, 0f);

            var visor = Primitive(PrimitiveType.Cube, "Visor", pivot, new Vector3(0f, 0f, 0.3f),
                new Vector3(0.45f, 0.12f, 0.12f), mats.Dark, keepCollider: false);

            var camGo = new GameObject("PlayerCamera");
            camGo.tag = "MainCamera";
            camGo.transform.SetParent(pivot, false);
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 70f;
            cam.nearClipPlane = 0.05f;
            camGo.AddComponent<AudioListener>();

            var holdPoint = new GameObject("HoldPoint").transform;
            holdPoint.SetParent(camGo.transform, false);
            holdPoint.localPosition = new Vector3(0f, 0f, 1.2f);

            SetRef(motor, "cameraPivot", pivot);
            SetRefs(motor, "ownerHiddenRenderers", body.GetComponent<Renderer>(), visor.GetComponent<Renderer>());
            SetRef(interactor, "cameraTransform", camGo.transform);
            SetRef(interactor, "holdPoint", holdPoint);

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, PlayerPrefabPath);
            Object.DestroyImmediate(root);
            return prefab;
        }

        // ------------------------------------------------------------------ room

        private static Light[] BuildRoom(Palette m)
        {
            var room = new GameObject("Room").transform;

            // Shell: 12m x 10m, 3.2m high. Origin = floor centre. Bar along the back (+Z) wall.
            Block("Floor", room, new Vector3(0f, -0.05f, 0f), new Vector3(12f, 0.1f, 10f), m.Floor);
            Block("Ceiling", room, new Vector3(0f, 3.25f, 0f), new Vector3(12f, 0.1f, 10f), m.Ceiling);
            Block("Wall_Back", room, new Vector3(0f, 1.6f, 5.05f), new Vector3(12.2f, 3.2f, 0.1f), m.Wall);
            Block("Wall_Left", room, new Vector3(-6.05f, 1.6f, 0f), new Vector3(0.1f, 3.2f, 10f), m.Wall);
            Block("Wall_Right", room, new Vector3(6.05f, 1.6f, 0f), new Vector3(0.1f, 3.2f, 10f), m.Wall);
            // Front wall with a doorway between x = 4.0 and 5.4.
            Block("Wall_Front_A", room, new Vector3(-1.05f, 1.6f, -5.05f), new Vector3(10.1f, 3.2f, 0.1f), m.Wall);
            Block("Wall_Front_B", room, new Vector3(5.75f, 1.6f, -5.05f), new Vector3(0.7f, 3.2f, 0.1f), m.Wall);
            Block("Wall_Front_Lintel", room, new Vector3(4.7f, 2.75f, -5.05f), new Vector3(1.4f, 0.9f, 0.1f), m.Wall);
            Block("Street", room, new Vector3(4.7f, -0.06f, -7f), new Vector3(4f, 0.1f, 4f), m.Dark);

            // Bar counter (open end at x = 2 to walk behind it).
            var bar = new GameObject("Bar").transform;
            bar.SetParent(room, false);
            Block("Counter", bar, new Vector3(-1f, 0.525f, 2.8f), new Vector3(6f, 1.05f, 0.65f), m.Wood);
            Block("CounterTop", bar, new Vector3(-1f, 1.08f, 2.75f), new Vector3(6.2f, 0.06f, 0.85f), m.WoodDark);
            Block("BackCabinet", bar, new Vector3(-1f, 0.45f, 4.7f), new Vector3(6f, 0.9f, 0.5f), m.WoodDark);
            Block("Shelf_Low", bar, new Vector3(-1f, 1.3f, 4.8f), new Vector3(6f, 0.05f, 0.4f), m.Wood);
            Block("Shelf_High", bar, new Vector3(-1f, 1.85f, 4.8f), new Vector3(6f, 0.05f, 0.4f), m.Wood);
            Block("TapStand_Placeholder", bar, new Vector3(-1f, 1.35f, 2.75f), new Vector3(0.5f, 0.5f, 0.12f), m.Metal);

            // Tables.
            Vector3[] tables = { new(-3.5f, 0f, -2f), new(0f, 0f, -2.5f), new(-1.5f, 0f, 0.3f) };
            for (int i = 0; i < tables.Length; i++)
            {
                var t = new GameObject($"Table_{i + 1}").transform;
                t.SetParent(room, false);
                t.localPosition = tables[i];
                Block("Top", t, new Vector3(0f, 0.75f, 0f), new Vector3(1.1f, 0.05f, 1.1f), m.Wood);
                Block("Leg", t, new Vector3(0f, 0.36f, 0f), new Vector3(0.12f, 0.72f, 0.12f), m.WoodDark);
                Block("Foot", t, new Vector3(0f, 0.02f, 0f), new Vector3(0.6f, 0.04f, 0.6f), m.WoodDark);
            }

            // Keg store corner marker (the cellar comes in M4).
            Block("KegStore_Floor", room, new Vector3(4.5f, 0.005f, 4f), new Vector3(2.4f, 0.01f, 1.6f), m.Hazard);

            // Warm bar lighting.
            var lights = new[]
            {
                PointLight(room, "Light_Bar", new Vector3(-1f, 2.9f, 3.5f), 3.2f, true),
                PointLight(room, "Light_Floor_A", new Vector3(-3f, 2.9f, -1.5f), 2.6f, false),
                PointLight(room, "Light_Floor_B", new Vector3(2.5f, 2.9f, -1.5f), 2.6f, false),
                PointLight(room, "Light_KegStore", new Vector3(4.5f, 2.9f, 3.5f), 1.6f, false),
            };
            return lights;
        }

        private static void BuildProps(Palette m)
        {
            // Network objects live at the scene root (no plain-GameObject parents) to keep Netcode happy.
            for (int i = 0; i < 6; i++)
                SpawnGrabbable("Bottle", PrimitiveType.Cylinder, new Vector3(-3.5f + i * 0.3f, 1.47f, 4.8f),
                    new Vector3(0.08f, 0.14f, 0.08f), 0.6f, i % 2 == 0 ? m.BottleGreen : m.BottleBrown);

            for (int i = 0; i < 4; i++)
                SpawnGrabbable("Pint Glass", PrimitiveType.Cylinder, new Vector3(0.2f + i * 0.25f, 1.19f, 2.7f),
                    new Vector3(0.085f, 0.075f, 0.085f), 0.35f, m.Glass);

            SpawnGrabbable("Crate", PrimitiveType.Cube, new Vector3(3.2f, 0.16f, -4.2f), new Vector3(0.5f, 0.3f, 0.35f), 6f, m.Crate);
            SpawnGrabbable("Crate", PrimitiveType.Cube, new Vector3(3.2f, 0.47f, -4.2f), new Vector3(0.5f, 0.3f, 0.35f), 6f, m.Crate);

            SpawnGrabbable("Keg", PrimitiveType.Cylinder, new Vector3(4.0f, 0.31f, 4.2f), new Vector3(0.4f, 0.3f, 0.4f), 30f, m.Metal);
            SpawnGrabbable("Keg", PrimitiveType.Cylinder, new Vector3(4.6f, 0.31f, 4.2f), new Vector3(0.4f, 0.3f, 0.4f), 30f, m.Metal);
            SpawnGrabbable("Keg", PrimitiveType.Cylinder, new Vector3(5.2f, 0.31f, 4.2f), new Vector3(0.4f, 0.3f, 0.4f), 30f, m.Metal);
        }

        private static void BuildLightSwitch(Palette m, Light[] lights)
        {
            var plate = GameObject.CreatePrimitive(PrimitiveType.Cube);
            plate.name = "LightSwitch";
            plate.transform.position = new Vector3(3.6f, 1.3f, -4.97f);
            plate.transform.localScale = new Vector3(0.12f, 0.18f, 0.04f);
            plate.GetComponent<Renderer>().sharedMaterial = m.Ceiling;

            var toggle = Primitive(PrimitiveType.Cube, "Toggle", plate.transform, new Vector3(0f, 0f, 0.6f),
                new Vector3(0.3f, 0.4f, 0.8f), m.Dark, keepCollider: false);

            plate.AddComponent<NetworkObject>();
            var sw = plate.AddComponent<LightSwitch>();
            SetRefs(sw, "lights", lights.Cast<Object>().ToArray());
            SetRef(sw, "toggleVisual", toggle.transform);
        }

        private static void BuildSpawnPoints()
        {
            var parent = new GameObject("SpawnPoints").transform;
            Vector3[] spots = { new(4.7f, 0.05f, -3.8f), new(3.9f, 0.05f, -3.8f), new(4.7f, 0.05f, -3.0f), new(3.9f, 0.05f, -3.0f) };
            for (int i = 0; i < spots.Length; i++)
            {
                var go = new GameObject($"Spawn_{i}");
                go.transform.SetParent(parent, false);
                go.transform.SetPositionAndRotation(spots[i], Quaternion.Euler(0f, -40f, 0f));
                var sp = go.AddComponent<PlayerSpawnPoint>();
                var so = new SerializedObject(sp);
                so.FindProperty("order").intValue = i;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static GameObject BuildLobbyCamera()
        {
            var go = new GameObject("LobbyCamera");
            go.transform.position = new Vector3(5.2f, 2.6f, -4.4f);
            go.transform.LookAt(new Vector3(-1.5f, 0.8f, 2.5f));
            var cam = go.AddComponent<Camera>();
            cam.fieldOfView = 60f;
            go.AddComponent<AudioListener>();
            return go;
        }

        private static void BuildNetworkManager(GameObject playerPrefab, NetworkPrefabsList prefabList)
        {
            var go = new GameObject("NetworkManager");
            var nm = go.AddComponent<NetworkManager>();
            var transport = go.AddComponent<UnityTransport>();

            nm.NetworkConfig ??= new NetworkConfig();
            nm.NetworkConfig.NetworkTransport = transport;
            nm.NetworkConfig.PlayerPrefab = playerPrefab;
            nm.NetworkConfig.Prefabs ??= new NetworkPrefabs();
            if (!nm.NetworkConfig.Prefabs.NetworkPrefabsLists.Contains(prefabList))
                nm.NetworkConfig.Prefabs.NetworkPrefabsLists.Add(prefabList);
        }

        private static void BuildHud(GameObject lobbyCamera)
        {
            var canvasGo = new GameObject("HUD", typeof(Canvas), typeof(CanvasScaler));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            var crosshair = UiElement<Image>("Crosshair", canvasGo.transform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(6f, 6f));

            var prompt = UiElement<Text>("Prompt", canvasGo.transform, new Vector2(0.5f, 0.5f), new Vector2(0f, -70f), new Vector2(1000f, 100f));
            prompt.font = font;
            prompt.fontSize = 26;
            prompt.alignment = TextAnchor.UpperCenter;
            prompt.color = Color.white;
            prompt.gameObject.AddComponent<Outline>().effectColor = new Color(0f, 0f, 0f, 0.8f);

            var status = UiElement<Text>("NetStatus", canvasGo.transform, new Vector2(0f, 1f), new Vector2(24f, -20f), new Vector2(1100f, 100f));
            status.rectTransform.pivot = new Vector2(0f, 1f);
            status.font = font;
            status.fontSize = 22;
            status.alignment = TextAnchor.UpperLeft;
            status.color = new Color(1f, 0.9f, 0.7f);
            status.gameObject.AddComponent<Outline>().effectColor = new Color(0f, 0f, 0f, 0.8f);

            var hud = canvasGo.AddComponent<PlayerHUD>();
            SetRef(hud, "promptText", prompt);
            SetRef(hud, "crosshair", crosshair);

            var dev = canvasGo.AddComponent<DevNetworkHUD>();
            SetRef(dev, "statusText", status);
            SetRef(dev, "lobbyCamera", lobbyCamera);
        }

        // ------------------------------------------------------------------ helpers

        private static void SpawnGrabbable(string displayName, PrimitiveType shape, Vector3 position, Vector3 scale, float mass, Material mat)
        {
            var go = GameObject.CreatePrimitive(shape);
            go.name = displayName.Replace(" ", "");
            go.transform.position = position;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = mat;

            if (shape == PrimitiveType.Cylinder)
            {
                // Primitive cylinders come with a capsule collider; a convex mesh rolls like a real keg/bottle.
                Object.DestroyImmediate(go.GetComponent<Collider>());
                var mc = go.AddComponent<MeshCollider>();
                mc.sharedMesh = go.GetComponent<MeshFilter>().sharedMesh;
                mc.convex = true;
            }

            var rb = go.AddComponent<Rigidbody>();
            rb.mass = mass;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            rb.linearDamping = 0.05f;
            rb.angularDamping = 0.3f;

            var no = go.AddComponent<NetworkObject>();
            no.DontDestroyWithOwner = true;
            ConfigureNetworkTransform(go.AddComponent<NetworkTransform>(), syncPitchRoll: true);
            go.AddComponent<NetworkRigidbody>();

            var g = go.AddComponent<Grabbable>();
            var so = new SerializedObject(g);
            so.FindProperty("displayName").stringValue = displayName;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureNetworkTransform(NetworkTransform nt, bool syncPitchRoll)
        {
            nt.AuthorityMode = NetworkTransform.AuthorityModes.Owner;
            nt.SyncScaleX = nt.SyncScaleY = nt.SyncScaleZ = false;
            nt.SyncRotAngleX = syncPitchRoll;
            nt.SyncRotAngleZ = syncPitchRoll;
        }

        private static GameObject Block(string name, Transform parent, Vector3 localPos, Vector3 size, Material mat) =>
            Primitive(PrimitiveType.Cube, name, parent, localPos, size, mat, keepCollider: true);

        private static GameObject Primitive(PrimitiveType type, string name, Transform parent, Vector3 localPos,
            Vector3 scale, Material mat, bool keepCollider)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            if (!keepCollider) Object.DestroyImmediate(go.GetComponent<Collider>());
            return go;
        }

        private static Light PointLight(Transform parent, string name, Vector3 pos, float intensity, bool shadows)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.76f, 0.5f);
            light.intensity = intensity;
            light.range = 9f;
            light.shadows = shadows ? LightShadows.Soft : LightShadows.None;
            return light;
        }

        private static T UiElement<T>(string name, Transform parent, Vector2 anchor, Vector2 position, Vector2 size) where T : Graphic
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(T));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = anchor;
            rt.anchoredPosition = position;
            rt.sizeDelta = size;
            var graphic = go.GetComponent<T>();
            graphic.raycastTarget = false;
            return graphic;
        }

        private static void SetRef(Object target, string field, Object value)
        {
            var so = new SerializedObject(target);
            var prop = so.FindProperty(field);
            if (prop == null) { Debug.LogError($"[BarOwner] {target.GetType().Name} has no field '{field}'"); return; }
            prop.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetRefs(Object target, string field, params Object[] values)
        {
            var so = new SerializedObject(target);
            var prop = so.FindProperty(field);
            if (prop == null || !prop.isArray) { Debug.LogError($"[BarOwner] {target.GetType().Name} has no array '{field}'"); return; }
            prop.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++) prop.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static NetworkPrefabsList GetOrCreatePrefabList()
        {
            string guid = AssetDatabase.FindAssets("t:NetworkPrefabsList").FirstOrDefault();
            if (guid != null) return AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(AssetDatabase.GUIDToAssetPath(guid));

            var list = ScriptableObject.CreateInstance<NetworkPrefabsList>();
            AssetDatabase.CreateAsset(list, PrefabDir + "/NetworkPrefabs.asset");
            return list;
        }

        private static void AddSceneToBuildSettings(string path)
        {
            var scenes = EditorBuildSettings.scenes.Where(s => s.path != path).ToList();
            scenes.Insert(0, new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        /// <summary>Flat-colour URP materials, saved as assets so the scene can reference them.</summary>
        private class Palette
        {
            public readonly Material Floor = Make("Floor", new Color(0.36f, 0.27f, 0.2f));
            public readonly Material Wall = Make("Wall", new Color(0.55f, 0.24f, 0.2f));
            public readonly Material Ceiling = Make("Ceiling", new Color(0.85f, 0.8f, 0.7f));
            public readonly Material Wood = Make("Wood", new Color(0.55f, 0.36f, 0.2f));
            public readonly Material WoodDark = Make("WoodDark", new Color(0.3f, 0.19f, 0.11f));
            public readonly Material Metal = Make("Metal", new Color(0.7f, 0.72f, 0.75f), smoothness: 0.7f, metallic: 0.8f);
            public readonly Material Dark = Make("Dark", new Color(0.12f, 0.12f, 0.14f));
            public readonly Material Hazard = Make("Hazard", new Color(0.9f, 0.7f, 0.1f));
            public readonly Material Crate = Make("Crate", new Color(0.75f, 0.58f, 0.32f));
            public readonly Material BottleGreen = Make("BottleGreen", new Color(0.15f, 0.45f, 0.2f), smoothness: 0.8f);
            public readonly Material BottleBrown = Make("BottleBrown", new Color(0.4f, 0.2f, 0.08f), smoothness: 0.8f);
            public readonly Material Glass = Make("Glass", new Color(0.8f, 0.9f, 0.95f), smoothness: 0.9f);
            public readonly Material Player = Make("Player", new Color(0.25f, 0.5f, 0.85f));

            private static Material Make(string name, Color color, float smoothness = 0.2f, float metallic = 0f)
            {
                string path = $"{MaterialDir}/{name}.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null)
                {
                    Material template = GraphicsSettings.currentRenderPipeline != null
                        ? GraphicsSettings.currentRenderPipeline.defaultMaterial
                        : null;
                    mat = template != null ? new Material(template) : new Material(Shader.Find("Universal Render Pipeline/Lit"));
                    AssetDatabase.CreateAsset(mat, path);
                }

                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
                mat.color = color;
                if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
                if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metallic);
                EditorUtility.SetDirty(mat);
                return mat;
            }
        }
    }
}
