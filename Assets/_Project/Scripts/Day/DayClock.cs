using System;
using BarOwner.Bar;
using BarOwner.Customers;
using BarOwner.Economy;
using BarOwner.Staff;
using Unity.Netcode;
using UnityEngine;

namespace BarOwner.Day
{
    public enum PubPhase : byte
    {
        Closed = 0,     // between days: restock, tidy, then flip the sign
        Open = 1,       // customers arrive
        LastOrders = 2, // no new arrivals, still serving
        ClosingUp = 3,  // no more serving; waiting for the last customers to leave
    }

    /// <summary>
    /// The pub's day: opening hours run on a compressed clock (11 hours ≈ 12 real minutes by default).
    /// Server-authoritative; tracks the day's stats and announces last orders and the end-of-day summary.
    /// </summary>
    public class DayClock : NetworkBehaviour
    {
        public static DayClock Instance { get; private set; }

        /// <summary>Big centre-screen message on every client: (text, seconds to show).</summary>
        public static event Action<string, float> Announcement;

        [SerializeField] private int openHour = 12;
        [SerializeField] private int closeHour = 23;
        [SerializeField] private int lastOrdersMinutesBeforeClose = 15;
        [SerializeField, Tooltip("Real minutes for one full day of opening hours.")] private float realMinutesPerDay = 12f;

        private readonly NetworkVariable<int> _day = new(1);
        private readonly NetworkVariable<PubPhase> _phase = new(PubPhase.Closed);
        private readonly NetworkVariable<float> _minutes = new(12 * 60);

        public int Day => _day.Value;
        public PubPhase Phase => _phase.Value;
        public bool AcceptingNewCustomers => _phase.Value == PubPhase.Open;
        public bool IsServing => _phase.Value == PubPhase.Open || _phase.Value == PubPhase.LastOrders;
        public float Hour => _minutes.Value / 60f;

        public string ClockText
        {
            get
            {
                int total = Mathf.FloorToInt(_minutes.Value);
                return $"{total / 60 % 24:00}:{total % 60:00}";
            }
        }

        private float GameMinutesPerSecond => (closeHour - openHour) * 60f / Mathf.Max(0.1f, realMinutesPerDay * 60f);
        private float LastOrdersAt => closeHour * 60f - lastOrdersMinutesBeforeClose;

        public override void OnNetworkSpawn()
        {
            Instance = this;
            if (IsServer) _minutes.Value = openHour * 60f;
        }

        public override void OnNetworkDespawn()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (!IsServer || !IsSpawned) return;

            switch (_phase.Value)
            {
                case PubPhase.Open:
                case PubPhase.LastOrders:
                    _minutes.Value += GameMinutesPerSecond * Time.deltaTime;
                    if (_phase.Value == PubPhase.Open && _minutes.Value >= LastOrdersAt)
                    {
                        _phase.Value = PubPhase.LastOrders;
                        AnnounceRpc("*DING DING* Last orders!", 3f);
                    }
                    else if (_minutes.Value >= closeHour * 60f)
                    {
                        _phase.Value = PubPhase.ClosingUp;
                        AnnounceRpc("Time, please! Drink up.", 3f);
                    }
                    break;

                case PubPhase.ClosingUp:
                    var director = CustomerDirector.Instance;
                    if (director == null || director.ActiveCustomers == 0) EndDay();
                    break;
            }
        }

        // ---------------------------------------------------------------- requests (any client)

        [Rpc(SendTo.Server)]
        public void RequestOpenRpc()
        {
            if (_phase.Value != PubPhase.Closed) return;
            _minutes.Value = openHour * 60f;
            PubLedger.Instance?.ServerBeginDay();
            _phase.Value = PubPhase.Open;
            StaffManager.Instance?.ServerOnDayStarted(_day.Value);
            AnnounceRpc($"Day {_day.Value}: the pub is open!", 3f);
        }

        [Rpc(SendTo.Server)]
        public void RequestLastOrdersRpc()
        {
            if (_phase.Value == PubPhase.Open) _minutes.Value = Mathf.Max(_minutes.Value, LastOrdersAt);
        }

        // ---------------------------------------------------------------- server

        /// <summary>SERVER (loading a save): jump to a given day, closed, ready to open.</summary>
        public void ServerSetDay(int day)
        {
            if (!IsServer) return;
            _day.Value = Mathf.Max(1, day);
            _phase.Value = PubPhase.Closed;
            StaffManager.Instance?.ServerRollApplicants(_day.Value);
            _minutes.Value = openHour * 60f;
        }

        private void EndDay()
        {
            _phase.Value = PubPhase.Closed;
            StaffManager.Instance?.ServerOnDayEnded(); // pay wages first so they're in the report
            PubLedger.Instance?.ServerEndDay(_day.Value);
            _day.Value += 1;
            _minutes.Value = openHour * 60f;
            StaffManager.Instance?.ServerRollApplicants(_day.Value);
            Saving.SaveSystem.Instance?.ServerSave();
        }

        /// <summary>SERVER: show a centre-screen message to everyone.</summary>
        public void ServerAnnounce(string text, float seconds)
        {
            if (IsServer) AnnounceRpc(text, seconds);
        }

        [Rpc(SendTo.Everyone)]
        private void AnnounceRpc(string text, float seconds) => Announcement?.Invoke(text, seconds);
    }
}
