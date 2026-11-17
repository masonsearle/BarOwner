using System.Collections;
using BarOwner.Interaction;
using BarOwner.UI;
using Unity.Netcode;
using UnityEngine;

namespace BarOwner.Player
{
    /// <summary>
    /// Looks through the camera to find things to grab or use, and carries the held object with a
    /// springy physics hold (velocity-driven, so held items still collide with the world and heavy
    /// ones lag behind). Runs only on the owning client.
    /// </summary>
    [RequireComponent(typeof(PlayerMotor))]
    public class PlayerInteractor : NetworkBehaviour
    {
        [Header("References")]
        [SerializeField] private Transform cameraTransform;
        [SerializeField] private Transform holdPoint;

        [Header("Looking")]
        [SerializeField] private float reach = 2.6f;
        [SerializeField] private LayerMask interactMask = ~0;

        [Header("Holding")]
        [SerializeField] private float followStrength = 18f;
        [SerializeField] private float maxFollowSpeed = 14f;
        [SerializeField] private float rotationStrength = 12f;
        [SerializeField] private float minHoldDistance = 0.7f;
        [SerializeField] private float maxHoldDistance = 2.2f;
        [SerializeField] private float scrollStep = 0.15f;
        [SerializeField, Tooltip("Drop the item if it gets stuck this far from the hold point.")]
        private float breakDistance = 2.0f;

        [Header("Weight")]
        [SerializeField, Tooltip("Max throw speed for very light items (m/s).")] private float maxThrowSpeed = 12f;
        [SerializeField, Tooltip("Mass (kg) at which throw speed is halved.")] private float throwMassReference = 3f;
        [SerializeField, Tooltip("Mass (kg) at which walking speed is halved while carrying.")] private float carryMassReference = 40f;

        private PlayerInputReader _input;
        private PlayerMotor _motor;
        private CharacterController _controller;

        private Grabbable _held;
        private Collider[] _heldColliders;
        private Quaternion _holdRotationOffset;
        private float _holdDistance;

        private Grabbable _lookGrabbable;
        private IInteractable _lookInteractable;

        public Grabbable Held => _held;

        private void Awake()
        {
            _input = GetComponent<PlayerInputReader>();
            _motor = GetComponent<PlayerMotor>();
            _controller = GetComponent<CharacterController>();
        }

        public override void OnNetworkDespawn()
        {
            if (_held != null) Release(Vector3.zero);
        }

        private void Update()
        {
            if (!IsSpawned || !IsOwner) return;

            if (!_input.GameplayActive)
            {
                PlayerHUD.SetPrompt(null);
                return;
            }

            if (_held != null)
            {
                UpdateHolding();
                return;
            }

            Scan();

            if (_input.GrabPressed && _lookGrabbable != null && _lookGrabbable.CanBeGrabbed)
                _lookGrabbable.RequestGrab(this);
            else if (_input.InteractPressed && _lookInteractable != null)
                _lookInteractable.Interact(this);
        }

        private void FixedUpdate()
        {
            if (_held == null || !IsOwner) return;

            Rigidbody body = _held.Body;
            holdPoint.localPosition = new Vector3(0f, 0f, _holdDistance);

            // Heavier items follow more sluggishly.
            float heaviness = body.mass / (body.mass + carryMassReference);
            float strength = followStrength * (1f - heaviness * 0.7f);

            Vector3 toTarget = holdPoint.position - body.position;
            body.linearVelocity = Vector3.ClampMagnitude(toTarget * strength, maxFollowSpeed);

            Quaternion targetRotation = holdPoint.rotation * _holdRotationOffset;
            Quaternion delta = targetRotation * Quaternion.Inverse(body.rotation);
            delta.ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f) angle -= 360f;
            if (Mathf.Abs(angle) > 0.1f && !float.IsNaN(axis.x) && !float.IsInfinity(axis.x))
                body.angularVelocity = axis.normalized * (angle * Mathf.Deg2Rad * rotationStrength * (1f - heaviness * 0.5f));
            else
                body.angularVelocity = Vector3.zero;
        }

        private void UpdateHolding()
        {
            // Lost it (despawned, or the server gave it to someone else)?
            if (!_held.IsSpawned || !_held.IsOwner || !_held.IsHeldByLocalPlayer)
            {
                Release(Vector3.zero, notifyServer: false);
                return;
            }

            if (Vector3.Distance(_held.Body.position, holdPoint.position) > breakDistance)
            {
                Release(Vector3.zero);
                return;
            }

            float scroll = _input.Scroll;
            if (scroll != 0f)
                _holdDistance = Mathf.Clamp(_holdDistance + scroll * scrollStep, minHoldDistance, maxHoldDistance);

            if (_input.ThrowPressed)
            {
                float speed = maxThrowSpeed / (1f + _held.Body.mass / throwMassReference);
                Release(cameraTransform.forward * speed + _controller.velocity);
                return;
            }

            if (_input.GrabPressed)
            {
                Release(_controller.velocity);
                return;
            }

            PlayerHUD.SetPrompt($"{_held.DisplayName}\n[LMB] Drop   [RMB] Throw   [Scroll] Distance");
        }

        private void Scan()
        {
            _lookGrabbable = null;
            _lookInteractable = null;

            if (Physics.Raycast(cameraTransform.position, cameraTransform.forward, out RaycastHit hit, reach,
                    interactMask, QueryTriggerInteraction.Ignore))
            {
                _lookGrabbable = hit.collider.GetComponentInParent<Grabbable>();
                _lookInteractable = hit.collider.GetComponentInParent<IInteractable>();
            }

            string prompt = null;
            if (_lookInteractable != null)
            {
                string action = _lookInteractable.GetPrompt(this);
                if (!string.IsNullOrEmpty(action)) prompt = $"[E] {action}";
                else _lookInteractable = null;
            }
            else if (_lookGrabbable != null && _lookGrabbable.CanBeGrabbed)
            {
                prompt = $"[LMB] Pick up {_lookGrabbable.DisplayName}{(_lookGrabbable.IsHeavy ? " (heavy)" : "")}";
            }

            PlayerHUD.SetPrompt(prompt);
        }

        /// <summary>Called by Grabbable once the server has granted this player the item.</summary>
        public void BeginHold(Grabbable grabbable)
        {
            if (_held != null || grabbable == null) return;

            _held = grabbable;
            _heldColliders = grabbable.GetComponentsInChildren<Collider>();
            foreach (var c in _heldColliders) Physics.IgnoreCollision(c, _controller, true);

            Rigidbody body = grabbable.Body;
            body.useGravity = false; // NetworkRigidbody already made it non-kinematic for us (the owner)

            _holdDistance = Mathf.Clamp(Vector3.Distance(cameraTransform.position, body.position),
                minHoldDistance, maxHoldDistance);
            holdPoint.localPosition = new Vector3(0f, 0f, _holdDistance);
            _holdRotationOffset = Quaternion.Inverse(holdPoint.rotation) * body.rotation;

            _motor.SpeedMultiplier = 1f / (1f + body.mass / carryMassReference);
        }

        private void Release(Vector3 velocity, bool notifyServer = true)
        {
            Grabbable item = _held;
            Collider[] colliders = _heldColliders;
            _held = null;
            _heldColliders = null;
            _motor.SpeedMultiplier = 1f;
            PlayerHUD.SetPrompt(null);

            if (item == null) return;

            if (item.Body != null)
            {
                item.Body.useGravity = true;
                if (!item.Body.isKinematic)
                {
                    item.Body.linearVelocity = velocity;
                    item.Body.angularVelocity *= 0.5f;
                }
            }

            if (colliders != null && isActiveAndEnabled) StartCoroutine(RestoreCollisions(colliders));
            if (notifyServer) item.NotifyReleased();
        }

        private IEnumerator RestoreCollisions(Collider[] colliders)
        {
            // Give the item a moment to clear the player's capsule so it doesn't pop.
            yield return new WaitForSeconds(0.3f);
            foreach (var c in colliders)
                if (c != null && _controller != null) Physics.IgnoreCollision(c, _controller, false);
        }
    }
}
