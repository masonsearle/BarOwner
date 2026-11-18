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
    /// ones lag behind). You can still use interactables while holding something (pour into the
    /// glass you're holding, hand a pint to a customer). Runs only on the owning client.
    /// </summary>
    [RequireComponent(typeof(PlayerMotor))]
    public class PlayerInteractor : NetworkBehaviour
    {
        /// <summary>The interactor of the player controlled on this machine.</summary>
        public static PlayerInteractor Local { get; private set; }

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
        [SerializeField, Tooltip("Degrees per second when turning a held object with Q/R.")] private float turnSpeed = 120f;
        [SerializeField, Tooltip("Drop the item if it gets stuck this far from the hold point.")]
        private float breakDistance = 2.0f;

        [Header("Weight")]
        [SerializeField, Tooltip("Max throw speed for very light items (m/s).")] private float maxThrowSpeed = 12f;
        [SerializeField, Tooltip("Mass (kg) at which throw speed is halved.")] private float throwMassReference = 3f;
        [SerializeField, Tooltip("Mass (kg) at which walking speed is halved while carrying.")] private float carryMassReference = 40f;

        private static readonly RaycastHit[] HitBuffer = new RaycastHit[16];

        private PlayerInputReader _input;
        private PlayerMotor _motor;
        private CharacterController _controller;

        private Grabbable _held;
        private Collider[] _heldColliders;
        private Quaternion _holdRotationOffset;
        private float _holdDistance;

        private Grabbable _lookGrabbable;
        private IInteractable _lookInteractable;
        private IHoldInteractable _activeHold;

        public Grabbable Held => _held;

        private void Awake()
        {
            _input = GetComponent<PlayerInputReader>();
            _motor = GetComponent<PlayerMotor>();
            _controller = GetComponent<CharacterController>();
        }

        public override void OnNetworkSpawn()
        {
            if (IsOwner) Local = this;
        }

        public override void OnNetworkDespawn()
        {
            EndActiveHold();
            if (_held != null) Release(Vector3.zero);
            if (Local == this) Local = null;
        }

        private void Update()
        {
            if (!IsSpawned || !IsOwner) return;

            if (!_input.GameplayActive)
            {
                EndActiveHold();
                PlayerHUD.SetPrompt(null);
                return;
            }

            if (_held != null && !UpdateHolding()) return; // threw/dropped this frame

            Scan();
            UpdateActiveHold();

            bool infoOnly = _lookInteractable != null && _lookInteractable.GetPrompt(this).StartsWith("~");
            if (_input.InteractPressed && _lookInteractable != null && !infoOnly)
            {
                if (_lookInteractable is IHoldInteractable hold)
                {
                    _activeHold = hold;
                    hold.HoldStart(this);
                }
                else
                {
                    _lookInteractable.Interact(this);
                }
            }
            else if (_held == null && _input.GrabPressed && _lookGrabbable != null && _lookGrabbable.CanBeGrabbed)
            {
                _lookGrabbable.RequestGrab(this);
            }

            UpdatePrompt();
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

        /// <summary>Returns false if the item was dropped/thrown this frame.</summary>
        private bool UpdateHolding()
        {
            // Lost it (despawned, or the server gave it to someone else)?
            if (_held == null || !_held.IsSpawned || !_held.IsOwner || !_held.IsHeldByLocalPlayer)
            {
                ForceDrop();
                return false;
            }

            if (Vector3.Distance(_held.Body.position, holdPoint.position) > breakDistance)
            {
                Release(Vector3.zero);
                return false;
            }

            float turn = _input.Rotate;
            if (turn != 0f)
            {
                // Spin the item's target rotation around the world up axis.
                Quaternion world = holdPoint.rotation * _holdRotationOffset;
                world = Quaternion.AngleAxis(turn * turnSpeed * Time.deltaTime, Vector3.up) * world;
                _holdRotationOffset = Quaternion.Inverse(holdPoint.rotation) * world;
            }

            float scroll = _input.Scroll;
            if (scroll != 0f)
                _holdDistance = Mathf.Clamp(_holdDistance + scroll * scrollStep, minHoldDistance, maxHoldDistance);

            if (_input.ThrowPressed)
            {
                float speed = maxThrowSpeed / (1f + _held.Body.mass / throwMassReference);
                Release(cameraTransform.forward * speed + _controller.velocity);
                return false;
            }

            if (_input.GrabPressed)
            {
                Release(_controller.velocity);
                return false;
            }

            return true;
        }

        private void Scan()
        {
            _lookGrabbable = null;
            _lookInteractable = null;

            int count = Physics.RaycastNonAlloc(cameraTransform.position, cameraTransform.forward, HitBuffer, reach,
                interactMask, QueryTriggerInteraction.Ignore);

            // Nearest hit that isn't the thing in our hands.
            RaycastHit? nearest = null;
            for (int i = 0; i < count; i++)
            {
                var hit = HitBuffer[i];
                if (_held != null && hit.collider.transform.IsChildOf(_held.transform)) continue;
                if (nearest == null || hit.distance < nearest.Value.distance) nearest = hit;
            }

            if (nearest == null) return;
            Collider col = nearest.Value.collider;
            _lookInteractable = col.GetComponentInParent<IInteractable>();
            _lookGrabbable = _held == null ? col.GetComponentInParent<Grabbable>() : null;

            if (_lookInteractable != null && string.IsNullOrEmpty(_lookInteractable.GetPrompt(this)))
                _lookInteractable = null;
        }

        private void UpdateActiveHold()
        {
            if (_activeHold == null) return;
            bool stillTargeted = ReferenceEquals(_lookInteractable, _activeHold);
            if (!_input.InteractHeld || !stillTargeted) EndActiveHold();
        }

        private void EndActiveHold()
        {
            if (_activeHold == null) return;
            var hold = _activeHold;
            _activeHold = null;
            if (hold is Object unityObject && unityObject == null) return; // destroyed
            hold.HoldEnd(this);
        }

        private void UpdatePrompt()
        {
            string look = null;
            if (_lookInteractable != null)
            {
                string prompt = _lookInteractable.GetPrompt(this);
                if (prompt.StartsWith("~")) look = prompt.Substring(1);
                else look = $"{(_lookInteractable is IHoldInteractable ? "Hold [E]" : "[E]")} {prompt}";
            }
            else if (_lookGrabbable != null && _lookGrabbable.CanBeGrabbed)
            {
                look = $"[LMB] Pick up {_lookGrabbable.DisplayName}{(_lookGrabbable.IsHeavy ? " (heavy)" : "")}";
            }

            if (_held == null)
            {
                PlayerHUD.SetPrompt(look);
                return;
            }

            string heldName = _held.TryGetComponent(out IHeldDescription description) ? description.Describe() : _held.DisplayName;
            string controls = "[LMB] Drop   [RMB] Throw   [Scroll] Distance   [Q/R] Turn";
            PlayerHUD.SetPrompt(look != null ? $"{look}\n<size=18>{heldName} · {controls}</size>" : $"{heldName}\n<size=18>{controls}</size>");
        }

        /// <summary>Called by Grabbable once the server has granted this player the item. False if hands are full.</summary>
        public bool BeginHold(Grabbable grabbable)
        {
            if (_held != null || grabbable == null) return false;

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
            return true;
        }

        /// <summary>Let go without telling the server (item was taken away or despawned).</summary>
        public void ForceDrop() => Release(Vector3.zero, notifyServer: false);

        private void Release(Vector3 velocity, bool notifyServer = true)
        {
            Grabbable item = _held;
            Collider[] colliders = _heldColliders;
            _held = null;
            _heldColliders = null;
            _motor.SpeedMultiplier = 1f;
            PlayerHUD.SetPrompt(null);

            if (item == null) return; // destroyed already

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
