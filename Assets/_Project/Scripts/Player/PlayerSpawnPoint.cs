using System.Collections.Generic;
using UnityEngine;

namespace BarOwner.Player
{
    /// <summary>Marks where players appear. Player N uses spawn point N (wrapping around).</summary>
    public class PlayerSpawnPoint : MonoBehaviour
    {
        private static readonly List<PlayerSpawnPoint> All = new();

        [SerializeField] private int order;

        private void OnEnable()
        {
            All.Add(this);
            All.Sort((a, b) => a.order.CompareTo(b.order));
        }

        private void OnDisable() => All.Remove(this);

        public static Transform Get(int index)
        {
            if (All.Count == 0) return null;
            return All[Mathf.Abs(index) % All.Count].transform;
        }

        private void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.6f);
            Gizmos.DrawWireCube(transform.position + Vector3.up * 0.9f, new Vector3(0.7f, 1.8f, 0.7f));
            Gizmos.DrawRay(transform.position + Vector3.up * 1.6f, transform.forward * 0.6f);
        }
    }
}
