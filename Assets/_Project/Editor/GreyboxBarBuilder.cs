using System.IO;
using System.Linq;
using BarOwner.Bar;
using BarOwner.Customers;
using BarOwner.Day;
using BarOwner.Economy;
using BarOwner.Interaction;
using BarOwner.Networking;
using BarOwner.Player;
using BarOwner.UI;
using Unity.AI.Navigation;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
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
        private const string GlassPrefabPath = PrefabDir + "/PintGlass.prefab";
        private const string CustomerPrefabPath = PrefabDir + "/Customer.prefab";
        private const string KegPrefabPath = PrefabDir + "/Keg.prefab";

        [MenuItem("BarOwner/Build Greybox Bar Scene", priority = 0)]
        public static void Build()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            if (File.Exists(ScenePath) && !EditorUtility.DisplayDialog("Rebuild greybox bar?",
                    $"This overwrites {ScenePath}, the Player, PintGlass, Customer and Keg prefabs and the greybox materials. Manual edits to them will be lost.",
                    "Rebuild", "Cancel"))
                return;

            EnsureFolder(PrefabDir);
            EnsureFolder(SceneDir);
            EnsureFolder(MaterialDir);

            var mats = new Palette();
            GameObject playerPrefab = BuildPlayerPrefab(mats);
            GameObject glassPrefab = BuildGlassPrefab(mats);
            GameObject customerPrefab = BuildCustomerPrefab(mats);
            GameObject kegPrefab = BuildKegPrefab(mats);
            NetworkPrefabsList prefabList = GetOrCreatePrefabList();
            foreach (var prefab in new[] { playerPrefab, glassPrefab, customerPrefab, kegPrefab })
            {
                if (prefabList.Contains(prefab)) continue;
                prefabList.Add(new NetworkPrefab { Prefab = prefab });
                EditorUtility.SetDirty(prefabList);
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.16f, 0.14f, 0.18f);

            Light[] barLights = BuildRoom(mats);
            BuildProps(mats, glassPrefab, kegPrefab);
            var sockets = BuildCellar(mats, kegPrefab);
            BuildTaps(mats, sockets);
            BuildGlassRack(mats, glassPrefab);
            BuildTill(mats);
            BuildDayClock();
            BuildOpenSign(mats);
            BuildCustomerDirector(customerPrefab, glassPrefab);
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

            // Customers path on a NavMesh built at runtime (server only) from the room's colliders.
            var surface = room.gameObject.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Children;
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;

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

        private static void BuildProps(Palette m, GameObject glassPrefab, GameObject kegPrefab)
        {
            // Network objects live at the scene root (no plain-GameObject parents) to keep Netcode happy.
            for (int i = 0; i < 6; i++)
                SpawnGrabbable("Bottle", PrimitiveType.Cylinder, new Vector3(-3.5f + i * 0.3f, 1.47f, 4.8f),
                    new Vector3(0.08f, 0.14f, 0.08f), 0.6f, i % 2 == 0 ? m.BottleGreen : m.BottleBrown);

            for (int i = 0; i < 2; i++)
            {
                var glass = (GameObject)PrefabUtility.InstantiatePrefab(glassPrefab);
                glass.transform.position = new Vector3(0.2f + i * 0.25f, 1.19f, 2.9f);
            }

            SpawnGrabbable("Crate", PrimitiveType.Cube, new Vector3(3.2f, 0.16f, -4.2f), new Vector3(0.5f, 0.3f, 0.35f), 6f, m.Crate);
            SpawnGrabbable("Crate", PrimitiveType.Cube, new Vector3(3.2f, 0.47f, -4.2f), new Vector3(0.5f, 0.3f, 0.35f), 6f, m.Crate);

            // One full spare keg of each in the cellar to start with.
            DrinkType[] spares = { DrinkType.Lager, DrinkType.Bitter, DrinkType.Cider };
            for (int i = 0; i < spares.Length; i++)
            {
                var keg = (GameObject)PrefabUtility.InstantiatePrefab(kegPrefab);
                keg.transform.position = new Vector3(3.9f + i * 0.6f, 0.31f, 3.55f);
                var so = new SerializedObject(keg.GetComponent<Keg>());
                so.FindProperty("startingDrink").intValue = (int)spares[i];
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        // ------------------------------------------------------------------ M2: pint glass, taps, rack, till, customer

        private static GameObject BuildGlassPrefab(Palette m)
        {
            var root = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            root.name = "PintGlass";
            root.transform.localScale = new Vector3(0.085f, 0.075f, 0.085f);
            root.GetComponent<Renderer>().sharedMaterial = m.Glass;
            Object.DestroyImmediate(root.GetComponent<Collider>());
            var mc = root.AddComponent<MeshCollider>();
            mc.sharedMesh = root.GetComponent<MeshFilter>().sharedMesh;
            mc.convex = true;

            var liquid = Primitive(PrimitiveType.Cylinder, "Liquid", root.transform, new Vector3(0f, -0.5f, 0f),
                new Vector3(0.86f, 0.5f, 0.86f), m.Liquid, keepCollider: false);
            var head = Primitive(PrimitiveType.Cylinder, "Head", root.transform, new Vector3(0f, 0.5f, 0f),
                new Vector3(0.87f, 0.05f, 0.87f), m.Foam, keepCollider: false);
            foreach (var r in new[] { liquid.GetComponent<Renderer>(), head.GetComponent<Renderer>() })
                r.shadowCastingMode = ShadowCastingMode.Off;
            liquid.SetActive(false);
            head.SetActive(false);

            AddNetworkPhysics(root, 0.35f, "Pint Glass");
            var glass = root.AddComponent<PintGlass>();
            SetRef(glass, "liquid", liquid.transform);
            SetRef(glass, "head", head.transform);

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, GlassPrefabPath);
            Object.DestroyImmediate(root);
            return prefab;
        }

        private static void BuildTaps(Palette m, System.Collections.Generic.Dictionary<DrinkType, KegSocket> sockets)
        {
            // The "font": a crossbar on two posts in the middle of the counter. Taps hang off it,
            // spouts face the staff side (+Z) over a drip tray.
            var font = new GameObject("TapFont").transform;
            Primitive(PrimitiveType.Cube, "Post_L", font, new Vector3(-1.55f, 1.29f, 2.75f), new Vector3(0.05f, 0.36f, 0.05f), m.Metal, keepCollider: true);
            Primitive(PrimitiveType.Cube, "Post_R", font, new Vector3(-0.45f, 1.29f, 2.75f), new Vector3(0.05f, 0.36f, 0.05f), m.Metal, keepCollider: true);
            Primitive(PrimitiveType.Cube, "Crossbar", font, new Vector3(-1f, 1.47f, 2.75f), new Vector3(1.15f, 0.07f, 0.09f), m.Metal, keepCollider: false);
            Primitive(PrimitiveType.Cube, "DripTray", font, new Vector3(-1f, 1.12f, 2.87f), new Vector3(1.05f, 0.02f, 0.2f), m.Dark, keepCollider: true);

            (DrinkType drink, float x, Material handle, Material stream)[] taps =
            {
                (DrinkType.Lager, -1.35f, m.HandleLager, m.StreamLager),
                (DrinkType.Bitter, -1.0f, m.HandleBitter, m.StreamBitter),
                (DrinkType.Cider, -0.65f, m.HandleCider, m.StreamCider),
            };

            foreach (var t in taps)
            {
                // Network objects stay at the scene root.
                var tap = new GameObject($"Tap_{t.drink}");
                tap.transform.position = new Vector3(t.x, 1.47f, 2.75f);
                var box = tap.AddComponent<BoxCollider>();
                box.center = new Vector3(0f, 0.04f, 0.06f);
                box.size = new Vector3(0.12f, 0.3f, 0.2f);

                var pivot = new GameObject("HandlePivot").transform;
                pivot.SetParent(tap.transform, false);
                pivot.localPosition = new Vector3(0f, 0.035f, 0f);
                Primitive(PrimitiveType.Cube, "Handle", pivot, new Vector3(0f, 0.09f, 0f), new Vector3(0.035f, 0.16f, 0.035f), t.handle, keepCollider: false);

                Primitive(PrimitiveType.Cube, "SpoutVisual", tap.transform, new Vector3(0f, -0.03f, 0.1f), new Vector3(0.03f, 0.08f, 0.03f), m.Metal, keepCollider: false);
                var spout = new GameObject("Spout").transform;
                spout.SetParent(tap.transform, false);
                spout.localPosition = new Vector3(0f, -0.07f, 0.12f);

                var stream = Primitive(PrimitiveType.Cylinder, "Stream", tap.transform, new Vector3(0f, -0.205f, 0.12f),
                    new Vector3(0.012f, 0.135f, 0.012f), t.stream, keepCollider: false);
                stream.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
                stream.SetActive(false);

                tap.AddComponent<NetworkObject>();
                var beerTap = tap.AddComponent<BeerTap>();
                var so = new SerializedObject(beerTap);
                so.FindProperty("drink").intValue = (int)t.drink;
                so.FindProperty("spout").objectReferenceValue = spout;
                so.FindProperty("handle").objectReferenceValue = pivot;
                so.FindProperty("stream").objectReferenceValue = stream;
                so.FindProperty("keg").objectReferenceValue = sockets.TryGetValue(t.drink, out var socket) ? socket : null;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static void BuildGlassRack(Palette m, GameObject glassPrefab)
        {
            var rack = GameObject.CreatePrimitive(PrimitiveType.Cube);
            rack.name = "GlassRack";
            rack.transform.position = new Vector3(1.2f, 0.95f, 4.6f);
            rack.transform.localScale = new Vector3(0.6f, 0.1f, 0.4f);
            rack.GetComponent<Renderer>().sharedMaterial = m.Metal;

            // Some decorative upside-down glasses so it reads as a glass rack.
            for (int i = 0; i < 4; i++)
                Primitive(PrimitiveType.Cylinder, $"Deco_{i}", rack.transform, new Vector3(-0.35f + i * 0.23f, 1.25f, 0f),
                    new Vector3(0.14f, 0.75f, 0.2f), m.Glass, keepCollider: false);

            var spawn = new GameObject("SpawnPoint").transform;
            spawn.SetParent(rack.transform, false);
            spawn.position = new Vector3(1.2f, 1.15f, 4.4f);

            rack.AddComponent<NetworkObject>();
            var glassRack = rack.AddComponent<GlassRack>();
            SetRef(glassRack, "glassPrefab", glassPrefab.GetComponent<NetworkObject>());
            SetRef(glassRack, "spawnPoint", spawn);
        }

        private static void BuildTill(Palette m)
        {
            var till = GameObject.CreatePrimitive(PrimitiveType.Cube);
            till.name = "Till";
            till.transform.position = new Vector3(1.45f, 1.24f, 2.85f);
            till.transform.localScale = new Vector3(0.38f, 0.25f, 0.3f);
            till.GetComponent<Renderer>().sharedMaterial = m.Dark;
            Primitive(PrimitiveType.Cube, "Screen", till.transform, new Vector3(0f, 0.75f, 0.2f), new Vector3(0.6f, 0.5f, 0.1f), m.Hazard, keepCollider: false);
            till.AddComponent<NetworkObject>();
            till.AddComponent<BarTill>();
        }

        // ------------------------------------------------------------------ M4: kegs + cellar

        private static GameObject BuildKegPrefab(Palette m)
        {
            var root = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            root.name = "Keg";
            root.transform.localScale = new Vector3(0.4f, 0.3f, 0.4f);
            root.GetComponent<Renderer>().sharedMaterial = m.Metal;
            Object.DestroyImmediate(root.GetComponent<Collider>());
            var mc = root.AddComponent<MeshCollider>();
            mc.sharedMesh = root.GetComponent<MeshFilter>().sharedMesh;
            mc.convex = true;

            var band = KegBand(root.transform, m);
            AddNetworkPhysics(root, 30f, "Keg");
            var keg = root.AddComponent<Keg>();
            SetRef(keg, "band", band);

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, KegPrefabPath);
            Object.DestroyImmediate(root);
            return prefab;
        }

        private static Renderer KegBand(Transform keg, Palette m)
        {
            Primitive(PrimitiveType.Cylinder, "RimTop", keg, new Vector3(0f, 0.92f, 0f), new Vector3(1.04f, 0.06f, 1.04f), m.Dark, keepCollider: false);
            var band = Primitive(PrimitiveType.Cylinder, "Band", keg, new Vector3(0f, 0.35f, 0f), new Vector3(1.03f, 0.14f, 1.03f), m.Liquid, keepCollider: false);
            return band.GetComponent<Renderer>();
        }

        private static System.Collections.Generic.Dictionary<DrinkType, KegSocket> BuildCellar(Palette m, GameObject kegPrefab)
        {
            var sockets = new System.Collections.Generic.Dictionary<DrinkType, KegSocket>();
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            DrinkType[] lines = { DrinkType.Lager, DrinkType.Bitter, DrinkType.Cider };

            for (int i = 0; i < lines.Length; i++)
            {
                float x = 3.7f + i * 0.8f;
                var root = new GameObject($"KegSocket_{lines[i]}");
                root.transform.position = new Vector3(x, 0f, 4.6f);
                var plateCollider = root.AddComponent<BoxCollider>();
                plateCollider.center = new Vector3(0f, 0.04f, 0f);
                plateCollider.size = new Vector3(0.55f, 0.08f, 0.55f);
                Primitive(PrimitiveType.Cube, "Plate", root.transform, new Vector3(0f, 0.04f, 0f), new Vector3(0.55f, 0.08f, 0.55f), m.Dark, keepCollider: false);
                Primitive(PrimitiveType.Cube, "Pipe", root.transform, new Vector3(0f, 1.65f, 0.35f), new Vector3(0.04f, 3.2f, 0.04f), m.Metal, keepCollider: false);

                // The plugged-in keg (visual + collider so you can bump into it and look at it).
                var connected = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                connected.name = "ConnectedKeg";
                connected.transform.SetParent(root.transform, false);
                connected.transform.localPosition = new Vector3(0f, 0.38f, 0f);
                connected.transform.localScale = new Vector3(0.4f, 0.3f, 0.4f);
                connected.GetComponent<Renderer>().sharedMaterial = m.Metal;
                var band = KegBand(connected.transform, m);
                Primitive(PrimitiveType.Cube, "Coupler", connected.transform, new Vector3(0f, 1.05f, 0f), new Vector3(0.3f, 0.15f, 0.3f), m.Dark, keepCollider: false);

                var popOut = new GameObject("PopOutPoint").transform;
                popOut.SetParent(root.transform, false);
                popOut.localPosition = new Vector3(0f, 0.45f, -0.6f);

                // Label on the back wall.
                var canvas = WorldCanvas($"Label", root.transform, new Vector3(0f, 1.45f, 0.38f), new Vector2(300f, 70f), 0.0025f);
                Object.DestroyImmediate(canvas.GetComponent<Billboard>());
                canvas.rotation = Quaternion.identity;
                var label = UiElement<Text>("Text", canvas, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(300f, 70f));
                label.font = font;
                label.fontSize = 44;
                label.fontStyle = FontStyle.Bold;
                label.alignment = TextAnchor.MiddleCenter;
                label.color = Color.white;
                label.text = Drinks.Name(lines[i]).ToUpperInvariant();

                root.AddComponent<NetworkObject>();
                var socket = root.AddComponent<KegSocket>();
                var so = new SerializedObject(socket);
                so.FindProperty("line").intValue = (int)lines[i];
                so.FindProperty("kegPrefab").objectReferenceValue = kegPrefab.GetComponent<NetworkObject>();
                so.FindProperty("connectedVisual").objectReferenceValue = connected;
                so.FindProperty("connectedBand").objectReferenceValue = band;
                so.FindProperty("popOutPoint").objectReferenceValue = popOut;
                so.ApplyModifiedPropertiesWithoutUndo();
                sockets[lines[i]] = socket;
            }

            BuildOrderBoard(m, font);
            BuildEmptiesCage(m, font);

            var supplierGo = new GameObject("KegSupplier");
            supplierGo.AddComponent<NetworkObject>();
            var supplier = supplierGo.AddComponent<KegSupplier>();
            var drop = new GameObject("DeliveryPoint").transform;
            drop.SetParent(supplierGo.transform, false);
            drop.position = new Vector3(3.7f, 0.4f, -6.4f); // outside, just left of the front door
            SetRef(supplier, "kegPrefab", kegPrefab.GetComponent<NetworkObject>());
            SetRef(supplier, "deliveryPoint", drop);

            return sockets;
        }

        private static void BuildOrderBoard(Palette m, Font font)
        {
            // On the right-hand wall of the cellar corner; text faces into the room (-X).
            var board = new GameObject("OrderBoard").transform;
            Primitive(PrimitiveType.Cube, "Backing", board, new Vector3(5.97f, 1.45f, 3.4f), new Vector3(0.04f, 1.0f, 1.0f), m.WoodDark, keepCollider: false);
            var title = WorldCanvas("Title", board, new Vector3(5.94f, 1.85f, 3.4f), new Vector2(400f, 60f), 0.0022f);
            Object.DestroyImmediate(title.GetComponent<Billboard>());
            title.rotation = Quaternion.Euler(0f, 90f, 0f);
            var titleText = UiElement<Text>("Text", title, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(400f, 60f));
            titleText.font = font;
            titleText.fontSize = 40;
            titleText.fontStyle = FontStyle.Bold;
            titleText.alignment = TextAnchor.MiddleCenter;
            titleText.text = "BREWERY ORDERS";

            DrinkType[] drinks = { DrinkType.Lager, DrinkType.Bitter, DrinkType.Cider };
            for (int i = 0; i < drinks.Length; i++)
            {
                float y = 1.62f - i * 0.25f;
                var button = Primitive(PrimitiveType.Cube, $"Order_{drinks[i]}", board, new Vector3(5.94f, y, 3.4f),
                    new Vector3(0.04f, 0.2f, 0.85f), m.Ceiling, keepCollider: true);
                var b = button.AddComponent<KegOrderButton>();
                var so = new SerializedObject(b);
                so.FindProperty("drink").intValue = (int)drinks[i];
                so.ApplyModifiedPropertiesWithoutUndo();

                var canvas = WorldCanvas("Label", board, new Vector3(5.915f, y, 3.4f), new Vector2(380f, 80f), 0.0022f);
                Object.DestroyImmediate(canvas.GetComponent<Billboard>());
                canvas.rotation = Quaternion.Euler(0f, 90f, 0f);
                var text = UiElement<Text>("Text", canvas, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(380f, 80f));
                text.font = font;
                text.fontSize = 36;
                text.alignment = TextAnchor.MiddleCenter;
                text.color = new Color(0.15f, 0.1f, 0.05f);
                text.text = $"{Drinks.Name(drinks[i])} keg  {Money.Format(KegSupplier.KegPricePence(drinks[i]))}";
            }
        }

        private static void BuildEmptiesCage(Palette m, Font font)
        {
            var cage = new GameObject("EmptiesCage");
            cage.transform.position = new Vector3(5.3f, 0f, 2.3f);
            var trigger = cage.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            trigger.center = new Vector3(0f, 0.5f, 0f);
            trigger.size = new Vector3(1f, 1f, 1f);
            cage.AddComponent<EmptiesCage>();

            // Rails only (no colliders) so kegs can be rolled/dropped in from any side.
            Primitive(PrimitiveType.Cube, "Floor", cage.transform, new Vector3(0f, 0.005f, 0f), new Vector3(1f, 0.01f, 1f), m.Hazard, keepCollider: false);
            foreach (var (pos, size) in new[]
                     {
                         (new Vector3(0f, 0.5f, 0.5f), new Vector3(1f, 0.04f, 0.04f)), (new Vector3(0f, 0.5f, -0.5f), new Vector3(1f, 0.04f, 0.04f)),
                         (new Vector3(0.5f, 0.5f, 0f), new Vector3(0.04f, 0.04f, 1f)), (new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.04f, 0.04f, 1f)),
                     })
                Primitive(PrimitiveType.Cube, "Rail", cage.transform, pos, size, m.Metal, keepCollider: false);

            var canvas = WorldCanvas("Label", cage.transform, new Vector3(0f, 1.05f, 0f), new Vector2(420f, 60f), 0.0025f);
            var text = UiElement<Text>("Text", canvas, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(420f, 60f));
            text.font = font;
            text.fontSize = 36;
            text.alignment = TextAnchor.MiddleCenter;
            text.text = "EMPTY KEGS (£5 back)";
        }

        // ------------------------------------------------------------------ M3: customers + day

        private static GameObject BuildCustomerPrefab(Palette m)
        {
            var root = new GameObject("Customer");

            var capsule = root.AddComponent<CapsuleCollider>();
            capsule.center = new Vector3(0f, 0.85f, 0f);
            capsule.radius = 0.3f;
            capsule.height = 1.7f;
            var rb = root.AddComponent<Rigidbody>();
            rb.isKinematic = true; // moved by the NavMeshAgent; still shoves loose glasses about

            var agent = root.AddComponent<NavMeshAgent>();
            agent.radius = 0.3f;
            agent.height = 1.8f;
            agent.speed = 1.5f;
            agent.angularSpeed = 360f;
            agent.acceleration = 8f;
            agent.stoppingDistance = 0.1f;
            agent.enabled = false; // the server enables it

            root.AddComponent<NetworkObject>();
            var nt = root.AddComponent<NetworkTransform>();
            ConfigureNetworkTransform(nt, syncPitchRoll: false);
            nt.AuthorityMode = NetworkTransform.AuthorityModes.Server;

            var body = Primitive(PrimitiveType.Capsule, "Body", root.transform, new Vector3(0f, 0.85f, 0f),
                new Vector3(0.6f, 0.85f, 0.6f), m.Customer, keepCollider: false);
            Primitive(PrimitiveType.Sphere, "Head", root.transform, new Vector3(0f, 1.78f, 0f),
                new Vector3(0.32f, 0.32f, 0.32f), m.Skin, keepCollider: false);
            Primitive(PrimitiveType.Cube, "Eyes", root.transform, new Vector3(0f, 1.82f, 0.15f),
                new Vector3(0.18f, 0.04f, 0.04f), m.Dark, keepCollider: false);
            var pint = Primitive(PrimitiveType.Cylinder, "HeldPint", root.transform, new Vector3(0.33f, 1.1f, 0.18f),
                new Vector3(0.085f, 0.075f, 0.085f), m.Liquid, keepCollider: false);
            pint.SetActive(false);

            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            // Speech bubble.
            var speechCanvas = WorldCanvas("SpeechBubble", root.transform, new Vector3(0f, 2.35f, 0f), new Vector2(560f, 90f), 0.004f);
            var bg = UiElement<Image>("Background", speechCanvas, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(560f, 90f));
            bg.color = new Color(0f, 0f, 0f, 0.6f);
            var text = UiElement<Text>("Text", speechCanvas, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(540f, 80f));
            text.font = font;
            text.fontSize = 34;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            speechCanvas.gameObject.SetActive(false);

            // Patience bar.
            var patienceCanvas = WorldCanvas("PatienceBar", root.transform, new Vector3(0f, 2.08f, 0f), new Vector2(200f, 18f), 0.004f);
            var barBg = UiElement<Image>("Background", patienceCanvas, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(200f, 18f));
            barBg.color = new Color(0f, 0f, 0f, 0.7f);
            var fill = UiElement<Image>("Fill", patienceCanvas, new Vector2(0f, 0.5f), new Vector2(2f, 0f), new Vector2(196f, 14f));
            fill.rectTransform.pivot = new Vector2(0f, 0.5f);
            fill.color = Color.green;
            patienceCanvas.gameObject.SetActive(false);

            var customer = root.AddComponent<Customer>();
            SetRef(customer, "bodyRenderer", body.GetComponent<Renderer>());
            SetRef(customer, "speechRoot", speechCanvas.gameObject);
            SetRef(customer, "speech", text);
            SetRef(customer, "patienceRoot", patienceCanvas.gameObject);
            SetRef(customer, "patienceFill", fill.rectTransform);
            SetRef(customer, "patienceImage", fill);
            SetRef(customer, "heldPint", pint);
            SetRef(customer, "heldPintRenderer", pint.GetComponent<Renderer>());

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, CustomerPrefabPath);
            Object.DestroyImmediate(root);
            return prefab;
        }

        private static RectTransform WorldCanvas(string name, Transform parent, Vector3 localPos, Vector2 size, float scale)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(Billboard));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.localPosition = localPos;
            rt.sizeDelta = size;
            rt.localScale = Vector3.one * scale;
            go.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            return rt;
        }

        private static void BuildDayClock()
        {
            var go = new GameObject("DayClock");
            go.AddComponent<NetworkObject>();
            go.AddComponent<DayClock>();
        }

        private static void BuildOpenSign(Palette m)
        {
            var board = GameObject.CreatePrimitive(PrimitiveType.Cube);
            board.name = "OpenSign";
            board.transform.position = new Vector3(2.9f, 1.6f, -4.97f);
            board.transform.localScale = new Vector3(0.55f, 0.26f, 0.04f);
            board.GetComponent<Renderer>().sharedMaterial = m.Ceiling;

            // Text faces into the room (-Z forward so it reads from inside).
            var canvasGo = new GameObject("Label", typeof(RectTransform), typeof(Canvas));
            var rt = (RectTransform)canvasGo.transform;
            rt.position = new Vector3(2.9f, 1.6f, -4.945f);
            rt.rotation = Quaternion.Euler(0f, 180f, 0f);
            rt.sizeDelta = new Vector2(260f, 120f);
            rt.localScale = Vector3.one * 0.002f;
            rt.SetParent(board.transform, true);
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            var label = UiElement<Text>("Text", rt, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(260f, 120f));
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = 70;
            label.fontStyle = FontStyle.Bold;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = Color.white;
            label.text = "CLOSED";

            var sign = board.AddComponent<OpenSign>();
            SetRef(sign, "label", label);
            SetRef(sign, "board", board.GetComponent<Renderer>());
        }

        private static void BuildCustomerDirector(GameObject customerPrefab, GameObject glassPrefab)
        {
            var go = new GameObject("CustomerDirector");
            go.AddComponent<NetworkObject>();
            var director = go.AddComponent<CustomerDirector>();

            Transform Marker(string name, Vector3 pos, Quaternion rot)
            {
                var t = new GameObject(name).transform;
                t.SetParent(go.transform, false);
                t.SetPositionAndRotation(pos, rot);
                return t;
            }

            var entrance = Marker("Entrance", new Vector3(4.7f, 0f, -7.6f), Quaternion.identity);
            var exit = Marker("Exit", new Vector3(4.7f, 0f, -7.9f), Quaternion.Euler(0f, 180f, 0f));
            var standing = Marker("StandingArea", new Vector3(5.1f, 0f, -1.2f), Quaternion.Euler(0f, -90f, 0f));

            float[] barX = { -3.3f, -2.2f, -1.0f, 0.3f };
            var barSpots = barX.Select((x, i) => Marker($"BarSpot_{i}", new Vector3(x, 0f, 1.9f), Quaternion.identity)).ToArray();

            Vector3[] queue = { new(1.2f, 0f, 1.1f), new(1.5f, 0f, 0.3f), new(2.0f, 0f, -0.4f), new(2.6f, 0f, -1.0f), new(3.2f, 0f, -1.6f) };
            var queueSpots = queue.Select((p, i) => Marker($"Queue_{i}", p, Quaternion.identity)).ToArray();

            // Four seats around each table, facing it.
            Vector3[] tables = { new(-3.5f, 0f, -2f), new(0f, 0f, -2.5f), new(-1.5f, 0f, 0.3f) };
            Vector3[] sides = { Vector3.left, Vector3.right, Vector3.forward, Vector3.back };
            var seatList = new System.Collections.Generic.List<Transform>();
            foreach (var table in tables)
                foreach (var side in sides)
                {
                    Vector3 pos = table + side * 1.05f;
                    seatList.Add(Marker($"Seat_{seatList.Count}", pos, Quaternion.LookRotation(-side)));
                }

            var so = new SerializedObject(director);
            so.FindProperty("customerPrefab").objectReferenceValue = customerPrefab.GetComponent<NetworkObject>();
            so.FindProperty("glassPrefab").objectReferenceValue = glassPrefab.GetComponent<NetworkObject>();
            so.FindProperty("navMeshSurface").objectReferenceValue = Object.FindFirstObjectByType<NavMeshSurface>();
            so.FindProperty("entrance").objectReferenceValue = entrance;
            so.FindProperty("exit").objectReferenceValue = exit;
            so.FindProperty("standingArea").objectReferenceValue = standing;
            so.ApplyModifiedPropertiesWithoutUndo();
            SetRefs(director, "barSpots", barSpots.Cast<Object>().ToArray());
            SetRefs(director, "queueSpots", queueSpots.Cast<Object>().ToArray());
            SetRefs(director, "seats", seatList.Cast<Object>().ToArray());
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
            var go = new GameObject("LobbyCamera") { tag = "MainCamera" };
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

            var money = UiElement<Text>("Money", canvasGo.transform, new Vector2(1f, 1f), new Vector2(-24f, -20f), new Vector2(400f, 60f));
            money.rectTransform.pivot = new Vector2(1f, 1f);
            money.font = font;
            money.fontSize = 40;
            money.fontStyle = FontStyle.Bold;
            money.alignment = TextAnchor.UpperRight;
            money.color = new Color(0.55f, 1f, 0.55f);
            money.gameObject.AddComponent<Outline>().effectColor = new Color(0f, 0f, 0f, 0.8f);

            var moneyGain = UiElement<Text>("MoneyGain", canvasGo.transform, new Vector2(1f, 1f), new Vector2(-24f, -72f), new Vector2(400f, 50f));
            moneyGain.rectTransform.pivot = new Vector2(1f, 1f);
            moneyGain.font = font;
            moneyGain.fontSize = 30;
            moneyGain.alignment = TextAnchor.UpperRight;
            moneyGain.color = new Color(1f, 0.9f, 0.4f);
            moneyGain.gameObject.AddComponent<Outline>().effectColor = new Color(0f, 0f, 0f, 0.8f);

            var clockText = UiElement<Text>("Clock", canvasGo.transform, new Vector2(0.5f, 1f), new Vector2(0f, -22f), new Vector2(1200f, 44f));
            clockText.rectTransform.pivot = new Vector2(0.5f, 1f);
            clockText.font = font;
            clockText.fontSize = 28;
            clockText.alignment = TextAnchor.UpperCenter;
            clockText.color = Color.white;
            clockText.gameObject.AddComponent<Outline>().effectColor = new Color(0f, 0f, 0f, 0.8f);

            var announcement = UiElement<Text>("Announcement", canvasGo.transform, new Vector2(0.5f, 0.5f), new Vector2(0f, 230f), new Vector2(1600f, 160f));
            announcement.font = font;
            announcement.fontSize = 46;
            announcement.fontStyle = FontStyle.Bold;
            announcement.alignment = TextAnchor.MiddleCenter;
            announcement.color = new Color(1f, 0.92f, 0.6f);
            announcement.gameObject.AddComponent<Outline>().effectColor = new Color(0f, 0f, 0f, 0.9f);

            var hud = canvasGo.AddComponent<PlayerHUD>();
            SetRef(hud, "clockText", clockText);
            SetRef(hud, "announcementText", announcement);
            SetRef(hud, "promptText", prompt);
            SetRef(hud, "crosshair", crosshair);
            SetRef(hud, "moneyText", money);
            SetRef(hud, "moneyGainText", moneyGain);

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

            AddNetworkPhysics(go, mass, displayName);
        }

        /// <summary>Rigidbody + NetworkObject + owner-authority NetworkTransform + NetworkRigidbody + Grabbable.</summary>
        private static void AddNetworkPhysics(GameObject go, float mass, string displayName)
        {
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
            public readonly Material Glass = MakeTransparent("Glass", new Color(0.85f, 0.93f, 1f, 0.28f));
            public readonly Material Liquid = Make("Liquid", Color.white, smoothness: 0.6f);
            public readonly Material Foam = Make("Foam", new Color(0.98f, 0.95f, 0.85f));
            public readonly Material HandleLager = Make("Handle_Lager", new Color(0.95f, 0.75f, 0.15f));
            public readonly Material HandleBitter = Make("Handle_Bitter", new Color(0.45f, 0.2f, 0.08f));
            public readonly Material HandleCider = Make("Handle_Cider", new Color(0.35f, 0.65f, 0.2f));
            public readonly Material StreamLager = Make("Stream_Lager", Drinks.LiquidColour(DrinkType.Lager), smoothness: 0.8f);
            public readonly Material StreamBitter = Make("Stream_Bitter", Drinks.LiquidColour(DrinkType.Bitter), smoothness: 0.8f);
            public readonly Material StreamCider = Make("Stream_Cider", Drinks.LiquidColour(DrinkType.Cider), smoothness: 0.8f);
            public readonly Material Customer = Make("Customer", new Color(0.75f, 0.3f, 0.25f));
            public readonly Material Skin = Make("Skin", new Color(0.93f, 0.75f, 0.6f));
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

            /// <summary>URP Lit set to Transparent / Alpha blend.</summary>
            private static Material MakeTransparent(string name, Color color)
            {
                var mat = Make(name, color, smoothness: 0.95f);
                mat.SetFloat("_Surface", 1f); // Transparent
                mat.SetFloat("_Blend", 0f);   // Alpha
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_SrcBlendAlpha", (int)BlendMode.One);
                mat.SetInt("_DstBlendAlpha", (int)BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.renderQueue = (int)RenderQueue.Transparent;
                EditorUtility.SetDirty(mat);
                return mat;
            }
        }
    }
}
