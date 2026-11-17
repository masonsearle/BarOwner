using System;
using BarOwner.Bar;
using Unity.Netcode;
using UnityEngine;

namespace BarOwner.Economy
{
    /// <summary>
    /// The pub's money. One per scene. Only the server changes it (customers pay on the server);
    /// everyone reads it. Stored in pence to avoid floating-point money bugs.
    /// </summary>
    public class BarTill : NetworkBehaviour
    {
        public static BarTill Instance { get; private set; }

        /// <summary>Fires on every client when the balance changes (and once on spawn).</summary>
        public static event Action<int> BalanceChanged;

        /// <summary>Fires on every client when money moves: (amount, negative = spent; world position).</summary>
        public static event Action<int, Vector3> MoneyReceived;

        [SerializeField] private int startingPence = 15000;

        private readonly NetworkVariable<int> _pence = new(0);

        public int Pence => _pence.Value;

        public override void OnNetworkSpawn()
        {
            Instance = this;
            if (IsServer) _pence.Value = startingPence;
            _pence.OnValueChanged += OnChanged;
            BalanceChanged?.Invoke(_pence.Value);
        }

        public override void OnNetworkDespawn()
        {
            _pence.OnValueChanged -= OnChanged;
            if (Instance == this) Instance = null;
        }

        /// <summary>SERVER: take payment. Position is where the "+£4.50" pops up.</summary>
        public void ServerDeposit(int pence, Vector3 where)
        {
            if (!IsServer || pence == 0) return;
            _pence.Value += pence;
            MoneyReceivedRpc(pence, where);
        }

        /// <summary>SERVER (loading a save): set the balance outright.</summary>
        public void ServerSetBalance(int pence)
        {
            if (IsServer) _pence.Value = pence;
        }

        /// <summary>SERVER: pay a bill that can't be refused (wages). The till can go negative.</summary>
        public void ServerSpend(int pence, Vector3 where)
        {
            if (!IsServer || pence <= 0) return;
            _pence.Value -= pence;
            MoneyReceivedRpc(-pence, where);
        }

        /// <summary>SERVER: pay for something. False (and nothing spent) if the till can't cover it.</summary>
        public bool ServerTrySpend(int pence, Vector3 where)
        {
            if (!IsServer || pence < 0 || _pence.Value < pence) return false;
            _pence.Value -= pence;
            if (pence > 0) MoneyReceivedRpc(-pence, where);
            return true;
        }

        [Rpc(SendTo.Everyone)]
        private void MoneyReceivedRpc(int pence, Vector3 where) => MoneyReceived?.Invoke(pence, where);

        private void OnChanged(int previous, int current) => BalanceChanged?.Invoke(current);

        public override string ToString() => Money.Format(_pence.Value);
    }
}
