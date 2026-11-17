using System;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.Rendering;

namespace BarOwner.Player
{
    /// <summary>
    /// First-person movement + mouse look. Owner-authoritative: the owning client moves its own
    /// CharacterController and the (Owner-mode) NetworkTransform replicates it to everyone else.
    /// Head pitch is shared via an owner-written NetworkVariable so others can see where you look.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    [RequireComponent(typeof(PlayerInputReader))]
    public class PlayerMotor : NetworkBehaviour
    {
        /// <summary>The player controlled on this machine (null until spawned).</summary>
        public static PlayerMotor LocalPlayer { get; private set; }
        public static event Action<PlayerMotor> LocalPlayerSpawned;
        public static event Action LocalPlayerDespawned;

        [Header("References")]
        [SerializeField] private Transform cameraPivot;
        [SerializeField, Tooltip("Body meshes the local player shouldn't see (they still cast shadows).")]
        private Renderer[] ownerHiddenRenderers;

        [Header("Movement")]
        [SerializeField] private float walkSpeed = 3.5f;
        [SerializeField] private float sprintSpeed = 6f;
        [SerializeField] private float acceleration = 14f;
        [SerializeField] private float airControl = 0.3f;
        [SerializeField] private float jumpHeight = 0.9f;
        [SerializeField] private float gravity = -20f;

        [Header("Look")]
        [SerializeField, Tooltip("Degrees per pixel of mouse movement")] private float lookSensitivity = 0.1f;
        [SerializeField] private float maxPitch = 85f;

        [Header("Head bob")]
        [SerializeField] private float bobAmount = 0.035f;
        [SerializeField] private float bobFrequency = 1.8f;

        private readonly NetworkVariable<float> _pitch = new(0f,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        private CharacterController _controller;
        private PlayerInputReader _input;
        private Camera _camera;
        private AudioListener _listener;
        private Vector3 _velocity;
        private float _localPitch;
        private float _bobPhase;
        private Vector3 _pivotBasePosition;
        private bool _needsSpawnPlacement;

        /// <summary>Set by whatever slows the player (e.g. carrying a keg). 1 = normal.</summary>
        public float SpeedMultiplier { get; set; } = 1f;

        public Transform CameraPivot => cameraPivot;
        public Camera Camera => _camera;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            _input = GetComponent<PlayerInputReader>();
            _camera = cameraPivot != null ? cameraPivot.GetComponentInChildren<Camera>(true) : null;
            _listener = cameraPivot != null ? cameraPivot.GetComponentInChildren<AudioListener>(true) : null;
            if (cameraPivot != null) _pivotBasePosition = cameraPivot.localPosition;
        }

        public override void OnNetworkSpawn()
        {
            bool local = IsOwner;
            _input.enabled = local;
            if (_camera != null) _camera.enabled = local;
            if (_listener != null) _listener.enabled = local;

            if (local)
            {
                LocalPlayer = this;
                _needsSpawnPlacement = true;
                if (ownerHiddenRenderers != null)
                    foreach (var r in ownerHiddenRenderers)
                        if (r != null) r.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                LocalPlayerSpawned?.Invoke(this);
            }
        }

        public override void OnNetworkDespawn()
        {
            if (LocalPlayer == this)
            {
                LocalPlayer = null;
                _input.enabled = false;
                LocalPlayerDespawned?.Invoke();
            }
        }

        private void Update()
        {
            if (!IsSpawned) return;

            if (!IsOwner)
            {
                // Remote player: just show where they're looking.
                if (cameraPivot != null)
                    cameraPivot.localRotation = Quaternion.Euler(_pitch.Value, 0f, 0f);
                return;
            }

            if (_needsSpawnPlacement)
            {
                _needsSpawnPlacement = false;
                PlaceAtSpawnPoint();
            }

            UpdateLook();
            UpdateMovement();
            UpdateHeadBob();
        }

        private void PlaceAtSpawnPoint()
        {
            var spawn = PlayerSpawnPoint.Get((int)OwnerClientId);
            if (spawn == null) return;
            Teleport(spawn.position, spawn.rotation);
        }

        /// <summary>Instantly move the local player (no interpolation for other clients).</summary>
        public void Teleport(Vector3 position, Quaternion rotation)
        {
            if (!IsOwner) return;
            _controller.enabled = false;
            var networkTransform = GetComponent<NetworkTransform>();
            if (networkTransform != null && networkTransform.CanCommitToTransform)
                networkTransform.Teleport(position, Quaternion.Euler(0f, rotation.eulerAngles.y, 0f), transform.localScale);
            else
                transform.SetPositionAndRotation(position, Quaternion.Euler(0f, rotation.eulerAngles.y, 0f));
            _controller.enabled = true;
            _velocity = Vector3.zero;
        }

        private void UpdateLook()
        {
            Vector2 look = _input.Look * lookSensitivity;
            transform.Rotate(0f, look.x, 0f, Space.Self);
            _localPitch = Mathf.Clamp(_localPitch - look.y, -maxPitch, maxPitch);
            if (cameraPivot != null) cameraPivot.localRotation = Quaternion.Euler(_localPitch, 0f, 0f);

            if (Mathf.Abs(_pitch.Value - _localPitch) > 0.5f) _pitch.Value = _localPitch;
        }

        private void UpdateMovement()
        {
            float dt = Time.deltaTime;
            bool grounded = _controller.isGrounded;

            Vector2 moveInput = _input.Move;
            Vector3 wishDir = transform.right * moveInput.x + transform.forward * moveInput.y;
            wishDir = Vector3.ClampMagnitude(wishDir, 1f);
            float speed = (_input.Sprint ? sprintSpeed : walkSpeed) * SpeedMultiplier;

            Vector3 horizontal = new(_velocity.x, 0f, _velocity.z);
            float accel = acceleration * (grounded ? 1f : airControl);
            horizontal = Vector3.MoveTowards(horizontal, wishDir * speed, accel * dt);

            if (grounded && _velocity.y < 0f) _velocity.y = -2f; // keep snapped to the floor
            if (grounded && _input.JumpPressed) _velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
            _velocity.y += gravity * dt;

            _velocity = new Vector3(horizontal.x, _velocity.y, horizontal.z);
            _controller.Move(_velocity * dt);
        }

        private void UpdateHeadBob()
        {
            if (cameraPivot == null) return;
            float planarSpeed = new Vector2(_velocity.x, _velocity.z).magnitude;
            Vector3 target = _pivotBasePosition;

            if (_controller.isGrounded && planarSpeed > 0.2f)
            {
                _bobPhase += planarSpeed * bobFrequency * Time.deltaTime;
                target.y += Mathf.Sin(_bobPhase * Mathf.PI * 2f) * bobAmount;
            }

            cameraPivot.localPosition = Vector3.Lerp(cameraPivot.localPosition, target, 12f * Time.deltaTime);
        }
    }
}
