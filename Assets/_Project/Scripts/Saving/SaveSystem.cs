using System;
using System.Collections;
using System.IO;
using System.Linq;
using BarOwner.Bar;
using BarOwner.Day;
using BarOwner.Economy;
using Unity.Netcode;
using UnityEngine;

namespace BarOwner.Saving
{
    /// <summary>
    /// One save slot, owned by the host. Saves automatically at the end of every day (when the pub
    /// is empty and closed, so there's no half-finished state to capture) and loads when the host
    /// starts. Clients don't touch the file; they receive the state through the network.
    /// File: %USERPROFILE%/AppData/LocalLow/&lt;company&gt;/&lt;product&gt;/save_slot1.json
    /// </summary>
    public class SaveSystem : NetworkBehaviour
    {
        public static SaveSystem Instance { get; private set; }

        private const int Version = 1;
        public static string SavePath => Path.Combine(Application.persistentDataPath, "save_slot1.json");
        public static bool HasSave => File.Exists(SavePath);

        public override void OnNetworkSpawn()
        {
            Instance = this;
            // Wait a frame so every in-scene NetworkObject has spawned and set its defaults first.
            if (IsServer && HasSave) StartCoroutine(LoadNextFrame());
        }

        public override void OnNetworkDespawn()
        {
            if (Instance == this) Instance = null;
        }

        // ---------------------------------------------------------------- static helpers (menu)

        /// <summary>One-line description of the save for the main menu, or null if there isn't one.</summary>
        public static string PeekSummary()
        {
            try
            {
                if (!HasSave) return null;
                var data = JsonUtility.FromJson<SaveData>(File.ReadAllText(SavePath));
                return $"Day {data.day} · {Money.Format(data.tillPence)} · reputation {data.reputation:0.0}/5";
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SaveSystem] Couldn't read save: {e.Message}");
                return "unreadable save";
            }
        }

        public static void DeleteSave()
        {
            if (HasSave) File.Delete(SavePath);
        }

        // ---------------------------------------------------------------- save

        public void ServerSave()
        {
            if (!IsServer) return;
            var ledger = PubLedger.Instance;
            var data = new SaveData
            {
                version = Version,
                day = DayClock.Instance != null ? DayClock.Instance.Day : 1,
                tillPence = BarTill.Instance != null ? BarTill.Instance.Pence : 0,
                reputation = ledger != null ? ledger.Reputation : 1f,
                priceLager = ledger != null ? ledger.PricePence(DrinkType.Lager) : Drinks.PricePence(DrinkType.Lager),
                priceBitter = ledger != null ? ledger.PricePence(DrinkType.Bitter) : Drinks.PricePence(DrinkType.Bitter),
                priceCider = ledger != null ? ledger.PricePence(DrinkType.Cider) : Drinks.PricePence(DrinkType.Cider),
                sockets = KegSocket.All.Select(s => new SocketData { line = (int)s.Line, connected = s.IsConnected, pints = s.Pints }).ToArray(),
                kegs = Keg.All.Where(k => k != null && k.IsSpawned)
                    .Select(k => new KegData { drink = (int)k.Drink, pints = k.Pints, position = k.transform.position, rotation = k.transform.rotation })
                    .ToArray(),
                pendingDeliveries = KegSupplier.Instance != null
                    ? KegSupplier.Instance.ServerPendingDrinks().Select(d => (int)d).ToArray()
                    : Array.Empty<int>(),
            };

            try
            {
                File.WriteAllText(SavePath, JsonUtility.ToJson(data, true));
                Debug.Log($"[SaveSystem] Saved day {data.day} to {SavePath}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveSystem] Save failed: {e.Message}");
                DayClock.Instance?.ServerAnnounce("Couldn't save the game! (see Console)", 4f);
            }
        }

        // ---------------------------------------------------------------- load

        private IEnumerator LoadNextFrame()
        {
            yield return null;

            SaveData data;
            try
            {
                data = JsonUtility.FromJson<SaveData>(File.ReadAllText(SavePath));
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveSystem] Couldn't load save, starting fresh: {e.Message}");
                yield break;
            }

            DayClock.Instance?.ServerSetDay(data.day);
            BarTill.Instance?.ServerSetBalance(data.tillPence);

            var ledger = PubLedger.Instance;
            if (ledger != null)
            {
                ledger.ServerSetReputation(data.reputation);
                ledger.ServerSetPrice(DrinkType.Lager, data.priceLager);
                ledger.ServerSetPrice(DrinkType.Bitter, data.priceBitter);
                ledger.ServerSetPrice(DrinkType.Cider, data.priceCider);
            }

            foreach (var saved in data.sockets ?? Array.Empty<SocketData>())
            {
                var socket = KegSocket.All.FirstOrDefault(s => (int)s.Line == saved.line);
                if (socket != null) socket.ServerSetState(saved.connected, saved.pints);
            }

            // Replace the scene's starting kegs with the saved ones.
            foreach (var keg in Keg.All.ToArray())
                if (keg != null && keg.IsSpawned) keg.NetworkObject.Despawn();

            var supplier = KegSupplier.Instance;
            if (supplier != null)
            {
                foreach (var k in data.kegs ?? Array.Empty<KegData>())
                    supplier.ServerSpawnKeg((DrinkType)k.drink, k.pints, k.position + Vector3.up * 0.05f, k.rotation);
                foreach (int drink in data.pendingDeliveries ?? Array.Empty<int>())
                    supplier.ServerQueueDelivery((DrinkType)drink, 5f);
            }

            Debug.Log($"[SaveSystem] Loaded day {data.day}");
            DayClock.Instance?.ServerAnnounce($"Welcome back! Day {data.day} · Till {Money.Format(data.tillPence)}", 4f);
        }

        // ---------------------------------------------------------------- data

        [Serializable]
        private class SaveData
        {
            public int version;
            public int day;
            public int tillPence;
            public float reputation;
            public int priceLager, priceBitter, priceCider;
            public SocketData[] sockets;
            public KegData[] kegs;
            public int[] pendingDeliveries;
        }

        [Serializable]
        private class SocketData
        {
            public int line;
            public bool connected;
            public float pints;
        }

        [Serializable]
        private class KegData
        {
            public int drink;
            public float pints;
            public Vector3 position;
            public Quaternion rotation;
        }
    }
}
