using BarOwner.Economy;
using Unity.Netcode;
using UnityEngine;

namespace BarOwner.Bar
{
    /// <summary>
    /// Trigger zone in the cellar. Drop an empty keg in (let go of it) and the brewery takes it back
    /// for the deposit. Runs on the server only; needs a trigger collider on this object.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class EmptiesCage : MonoBehaviour
    {
        private void OnTriggerStay(Collider other)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;

            var keg = other.GetComponentInParent<Keg>();
            if (keg == null || !keg.IsSpawned || !keg.IsEmpty || keg.Grabbable.IsHeld) return;

            int deposit = KegSupplier.Instance != null ? KegSupplier.Instance.EmptyKegDepositPence : 0;
            BarTill.Instance?.ServerDeposit(deposit, keg.transform.position + Vector3.up);
            PubLedger.Instance?.ServerRecordDeposit(deposit);
            keg.NetworkObject.Despawn();
        }
    }
}
