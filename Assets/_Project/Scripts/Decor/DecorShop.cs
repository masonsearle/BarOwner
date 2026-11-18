using BarOwner.Bar;
using BarOwner.Day;
using BarOwner.Economy;
using Unity.Netcode;
using UnityEngine;

namespace BarOwner.Decor
{
    /// <summary>
    /// The furniture catalogue in the office. Paid from the till; the item turns up at the back door
    /// straight away (flat-pack-free!) and someone has to carry it out and place it.
    /// </summary>
    public class DecorShop : NetworkBehaviour
    {
        public static DecorShop Instance { get; private set; }

        [SerializeField, Tooltip("One prefab per DecorType, in enum order.")] private NetworkObject[] prefabs;
        [SerializeField] private Transform deliveryPoint;

        private int _drops;

        public override void OnNetworkSpawn() => Instance = this;

        public override void OnNetworkDespawn()
        {
            if (Instance == this) Instance = null;
        }

        [Rpc(SendTo.Server)]
        public void RequestBuyRpc(DecorType type)
        {
            int price = DecorCatalog.PricePence(type);
            Vector3 at = DropPosition();
            if (BarTill.Instance == null || !BarTill.Instance.ServerTrySpend(price, at))
            {
                DayClock.Instance?.ServerAnnounce($"Can't afford the {DecorCatalog.Name(type)} ({Money.Format(price)})", 3f);
                return;
            }

            PubLedger.Instance?.ServerRecordDecorPurchase(price);
            ServerSpawn(type, at, Quaternion.identity);
            DayClock.Instance?.ServerAnnounce($"{DecorCatalog.Name(type)} delivered to the back door", 2.5f);
        }

        /// <summary>SERVER: spawn a decor item (purchases and loading saves).</summary>
        public DecorItem ServerSpawn(DecorType type, Vector3 position, Quaternion rotation)
        {
            int index = (int)type;
            if (!IsServer || prefabs == null || index >= prefabs.Length || prefabs[index] == null) return null;
            NetworkObject obj = Instantiate(prefabs[index], position, rotation);
            obj.DontDestroyWithOwner = true;
            obj.Spawn();
            return obj.GetComponent<DecorItem>();
        }

        private Vector3 DropPosition()
        {
            Vector3 basePos = deliveryPoint != null ? deliveryPoint.position : transform.position;
            Vector3 offset = new(0f, 0f, (_drops++ % 3 - 1) * 0.7f);
            return basePos + offset;
        }
    }
}
