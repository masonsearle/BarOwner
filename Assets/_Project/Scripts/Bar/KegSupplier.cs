using System.Collections.Generic;
using BarOwner.Day;
using BarOwner.Economy;
using Unity.Netcode;
using UnityEngine;

namespace BarOwner.Bar
{
    /// <summary>
    /// The brewery. Order kegs from the board in the cellar (paid from the till up front); the dray
    /// drops them outside the front door after a short wait, so someone has to lug them through
    /// the pub. Returning empties to the cage gets the deposit back.
    /// </summary>
    public class KegSupplier : NetworkBehaviour
    {
        public static KegSupplier Instance { get; private set; }

        [SerializeField] private NetworkObject kegPrefab;
        [SerializeField] private Transform deliveryPoint;
        [SerializeField, Tooltip("Real seconds from order to delivery.")] private float deliverySeconds = 30f;
        [SerializeField] private int emptyKegDepositPence = 500;

        private readonly List<(DrinkType drink, float arrivesAt)> _pending = new();
        private readonly NetworkVariable<int> _pendingCount = new(0);
        private int _dropIndex;

        public int PendingDeliveries => _pendingCount.Value;
        public int EmptyKegDepositPence => emptyKegDepositPence;

        /// <summary>Price of a full keg in pence (≈60% of what its 20 pints sell for).</summary>
        public static int KegPricePence(DrinkType drink) => drink switch
        {
            DrinkType.Lager => 5500,
            DrinkType.Bitter => 5000,
            DrinkType.Cider => 6000,
            _ => 0,
        };

        public override void OnNetworkSpawn() => Instance = this;

        public override void OnNetworkDespawn()
        {
            if (Instance == this) Instance = null;
        }

        [Rpc(SendTo.Server)]
        public void RequestOrderRpc(DrinkType drink)
        {
            int price = KegPricePence(drink);
            if (price <= 0 || BarTill.Instance == null) return;

            if (!BarTill.Instance.ServerTrySpend(price, deliveryPoint != null ? deliveryPoint.position : transform.position))
            {
                DayClock.Instance?.ServerAnnounce($"Can't afford a {Drinks.Name(drink)} keg ({Money.Format(price)})", 3f);
                return;
            }

            PubLedger.Instance?.ServerRecordKegPurchase(price);
            ServerQueueDelivery(drink, deliverySeconds);
            DayClock.Instance?.ServerAnnounce($"Ordered a {Drinks.Name(drink)} keg. Delivery in {deliverySeconds:0}s", 2.5f);
        }

        /// <summary>SERVER: a keg is on its way (already paid for).</summary>
        public void ServerQueueDelivery(DrinkType drink, float inSeconds)
        {
            if (!IsServer) return;
            _pending.Add((drink, Time.time + inSeconds));
            _pendingCount.Value = _pending.Count;
        }

        /// <summary>SERVER: drinks of every keg still on its way (for saving).</summary>
        public DrinkType[] ServerPendingDrinks()
        {
            var drinks = new DrinkType[_pending.Count];
            for (int i = 0; i < _pending.Count; i++) drinks[i] = _pending[i].drink;
            return drinks;
        }

        /// <summary>SERVER: spawn a loose keg (deliveries, loading a save).</summary>
        public Keg ServerSpawnKeg(DrinkType drink, float pints, Vector3 position, Quaternion rotation)
        {
            if (!IsServer || kegPrefab == null) return null;
            NetworkObject kegObject = Instantiate(kegPrefab, position, rotation);
            kegObject.DontDestroyWithOwner = true;
            kegObject.Spawn();
            var keg = kegObject.GetComponent<Keg>();
            keg.ServerSetContents(drink, pints);
            return keg;
        }

        private void Update()
        {
            if (!IsServer || _pending.Count == 0) return;

            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (Time.time < _pending[i].arrivesAt) continue;
                Deliver(_pending[i].drink);
                _pending.RemoveAt(i);
            }
            _pendingCount.Value = _pending.Count;
        }

        private void Deliver(DrinkType drink)
        {
            Vector3 basePos = deliveryPoint != null ? deliveryPoint.position : transform.position;
            // Spread drops out a little so kegs don't spawn inside each other.
            Vector3 offset = new((_dropIndex % 3 - 1) * 0.55f, 0.1f * (_dropIndex / 3 % 3), (_dropIndex / 3 % 2) * 0.55f);
            _dropIndex++;

            ServerSpawnKeg(drink, Keg.Capacity, basePos + offset, Quaternion.identity);
            DayClock.Instance?.ServerAnnounce($"Brewery delivery! A {Drinks.Name(drink)} keg is out front.", 3f);
        }
    }
}
