using UnityEngine;

namespace BarOwner.UI
{
    /// <summary>Keeps world-space UI (speech bubbles, labels) facing the active camera.</summary>
    public class Billboard : MonoBehaviour
    {
        private void LateUpdate()
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position, Vector3.up);
        }
    }
}
