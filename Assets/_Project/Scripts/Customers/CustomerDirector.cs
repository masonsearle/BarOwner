using System.Collections.Generic;
using BarOwner.Bar;
using BarOwner.Day;
using Unity.AI.Navigation;
using Unity.Netcode;
using UnityEngine;

namespace BarOwner.Customers
{
    /// <summary>
    /// SERVER: spawns customers while the pub is open (busier in the evening) and hands out places:
    /// spots at the bar, a queue for when the bar is full, and seats at tables. Also builds the
    /// NavMesh at runtime on the server (clients don't need one).
    /// </summary>
    public class CustomerDirector : NetworkBehaviour
    {
        public static CustomerDirector Instance { get; private set; }

        [Header("Prefabs")]
        [SerializeField] private NetworkObject customerPrefab;
        [SerializeField] private NetworkObject glassPrefab;

        [Header("Places")]
        [SerializeField] private NavMeshSurface navMeshSurface;
        [SerializeField] private Transform entrance;
        [SerializeField] private Transform exit;
        [SerializeField] private Transform standingArea;
        [SerializeField, Tooltip("Where customers stand to order; forward = facing the bar.")] private Transform[] barSpots;
        [SerializeField, Tooltip("Queue line, first = next to be served.")] private Transform[] queueSpots;
        [SerializeField, Tooltip("Seats; forward = facing the table.")] private Transform[] seats;

        [Header("Crowd")]
        [SerializeField] private int maxCustomers = 12;
        [SerializeField, Tooltip("Seconds between arrivals when it's quiet.")] private float quietInterval = 24f;
        [SerializeField, Tooltip("Seconds between arrivals at the evening peak.")] private float busyInterval = 8f;
        [SerializeField] private int maxGlassesInPub = 24;

        private readonly List<Customer> _active = new();
        private readonly List<Customer> _queue = new();
        private Customer[] _barOccupants;
        private Customer[] _seatOccupants;
        private float _nextSpawnAt;
        private bool _wasAccepting;

        public int ActiveCustomers => _active.Count;
        public Vector3 ExitPosition => exit != null ? exit.position : transform.position;

        public override void OnNetworkSpawn()
        {
            Instance = this;
            if (!IsServer) return;

            _barOccupants = new Customer[barSpots.Length];
            _seatOccupants = new Customer[seats.Length];
            if (navMeshSurface != null) navMeshSurface.BuildNavMesh();
            else Debug.LogError("[CustomerDirector] No NavMeshSurface assigned; customers can't walk.");
        }

        public override void OnNetworkDespawn()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (!IsServer || !IsSpawned) return;
            var clock = DayClock.Instance;
            bool accepting = clock != null && clock.AcceptingNewCustomers;

            if (accepting && !_wasAccepting) _nextSpawnAt = Time.time + 2f; // first customer soon after opening
            _wasAccepting = accepting;

            if (!accepting || _active.Count >= maxCustomers || Time.time < _nextSpawnAt) return;

            SpawnCustomer();
            _nextSpawnAt = Time.time + ArrivalInterval(clock.Hour) * Random.Range(0.7f, 1.3f);
        }

        /// <summary>Quiet afternoon, lunchtime bump, busy evening.</summary>
        private float ArrivalInterval(float hour)
        {
            float busyness = hour switch
            {
                >= 17f and < 21.5f => 1f,
                >= 12f and < 14f => 0.45f,
                >= 21.5f => 0.6f,
                _ => 0.25f,
            };
            return Mathf.Lerp(quietInterval, busyInterval, busyness);
        }

        private void SpawnCustomer()
        {
            Vector3 at = entrance != null ? entrance.position : transform.position;
            NetworkObject obj = Instantiate(customerPrefab, at, entrance != null ? entrance.rotation : Quaternion.identity);
            obj.Spawn();
            var customer = obj.GetComponent<Customer>();
            _active.Add(customer);
            customer.ServerInit(this, Random.Range(int.MinValue, int.MaxValue));
        }

        // ---------------------------------------------------------------- places (server, called by Customer)

        /// <summary>Send a new arrival to a free bar spot, or the back of the queue. False = no room, go home.</summary>
        public bool ServerRequestPlace(Customer c)
        {
            int spot = FreeBarSpot();
            if (spot >= 0)
            {
                _barOccupants[spot] = c;
                c.ServerGoToBar(barSpots[spot]);
                return true;
            }

            if (_queue.Count >= queueSpots.Length) return false;
            _queue.Add(c);
            c.ServerQueueAt(queueSpots[_queue.Count - 1].position);
            return true;
        }

        public void ServerReleaseBarSpot(Customer c)
        {
            for (int i = 0; i < _barOccupants.Length; i++)
            {
                if (_barOccupants[i] != c) continue;
                _barOccupants[i] = null;

                // Next in the queue steps up.
                if (_queue.Count > 0)
                {
                    Customer next = _queue[0];
                    _queue.RemoveAt(0);
                    _barOccupants[i] = next;
                    next.ServerGoToBar(barSpots[i]);
                    ShuffleQueueForward();
                }
            }
        }

        public bool ServerTakeSeat(Customer c, out Vector3 position, out Vector3 facing, out Vector3 glassDrop)
        {
            position = facing = glassDrop = Vector3.zero;
            int free = 0;
            for (int i = 0; i < _seatOccupants.Length; i++) if (_seatOccupants[i] == null) free++;
            if (free == 0) return false;

            int pick = Random.Range(0, free);
            for (int i = 0; i < _seatOccupants.Length; i++)
            {
                if (_seatOccupants[i] != null) continue;
                if (pick-- > 0) continue;
                _seatOccupants[i] = c;
                Transform seat = seats[i];
                position = seat.position;
                facing = seat.forward;
                glassDrop = seat.position + seat.forward * 0.8f + Vector3.up * 0.86f; // on the table top
                return true;
            }
            return false;
        }

        public Vector3 StandingPosition()
        {
            Vector3 centre = standingArea != null ? standingArea.position : ExitPosition;
            Vector2 offset = Random.insideUnitCircle * 0.9f;
            return centre + new Vector3(offset.x, 0f, offset.y);
        }

        /// <summary>Free every place this customer held (bar spot, queue slot, seat).</summary>
        public void ServerReleaseEverything(Customer c)
        {
            ServerReleaseBarSpot(c);
            if (_queue.Remove(c)) ShuffleQueueForward();
            for (int i = 0; i < _seatOccupants.Length; i++)
                if (_seatOccupants[i] == c) _seatOccupants[i] = null;
        }

        public void ServerOnCustomerGone(Customer c)
        {
            ServerReleaseEverything(c);
            _active.Remove(c);
        }

        public void ServerSpawnEmptyGlass(Vector3 position)
        {
            if (glassPrefab == null || PintGlass.All.Count >= maxGlassesInPub) return; // they "took it home"
            NetworkObject glass = Instantiate(glassPrefab, position, Quaternion.identity);
            glass.DontDestroyWithOwner = true;
            glass.Spawn();
        }

        private int FreeBarSpot()
        {
            for (int i = 0; i < _barOccupants.Length; i++)
                if (_barOccupants[i] == null) return i;
            return -1;
        }

        private void ShuffleQueueForward()
        {
            for (int i = 0; i < _queue.Count; i++)
                _queue[i].ServerQueueAt(queueSpots[i].position);
        }
    }
}
