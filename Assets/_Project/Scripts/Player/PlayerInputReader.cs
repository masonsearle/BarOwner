using UnityEngine;
using UnityEngine.InputSystem;

namespace BarOwner.Player
{
    /// <summary>
    /// Reads the project-wide input actions (Assets/InputSystem_Actions) for the LOCAL player only.
    /// Starts disabled; PlayerMotor enables it on the owning client when the player spawns.
    /// Also owns cursor locking: Esc frees the cursor, clicking the game window locks it again.
    /// </summary>
    public class PlayerInputReader : MonoBehaviour
    {
        private InputAction _move, _look, _jump, _sprint, _grab, _interact;

        /// <summary>True when the local player should respond to gameplay input (cursor captured).</summary>
        public bool GameplayActive => isActiveAndEnabled && Cursor.lockState == CursorLockMode.Locked;

        public Vector2 Move => GameplayActive && _move != null ? _move.ReadValue<Vector2>() : Vector2.zero;
        public Vector2 Look => GameplayActive && _look != null ? _look.ReadValue<Vector2>() : Vector2.zero;
        public bool Sprint => GameplayActive && _sprint != null && _sprint.IsPressed();
        public bool JumpPressed => GameplayActive && _jump != null && _jump.WasPressedThisFrame();

        /// <summary>Left mouse / right trigger: pick up, or drop what you're holding.</summary>
        public bool GrabPressed => GameplayActive && _grab != null && _grab.WasPressedThisFrame();

        /// <summary>E / gamepad north: use the thing you're looking at.</summary>
        public bool InteractPressed => GameplayActive && _interact != null && _interact.WasPressedThisFrame();

        /// <summary>Right mouse / left trigger: throw the held object.</summary>
        public bool ThrowPressed
        {
            get
            {
                if (!GameplayActive) return false;
                bool mouse = Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame;
                bool pad = Gamepad.current != null && Gamepad.current.leftTrigger.wasPressedThisFrame;
                return mouse || pad;
            }
        }

        /// <summary>Mouse wheel in notches (+1 / -1), used to push/pull the held object.</summary>
        public float Scroll
        {
            get
            {
                if (!GameplayActive || Mouse.current == null) return 0f;
                float y = Mouse.current.scroll.ReadValue().y;
                return Mathf.Abs(y) < 0.01f ? 0f : Mathf.Sign(y);
            }
        }

        private void Awake()
        {
            var actions = InputSystem.actions;
            if (actions == null)
            {
                Debug.LogError("[PlayerInputReader] No project-wide input actions assigned. " +
                               "Set them in Edit > Project Settings > Input System Package.");
                return;
            }

            _move = actions.FindAction("Player/Move");
            _look = actions.FindAction("Player/Look");
            _jump = actions.FindAction("Player/Jump");
            _sprint = actions.FindAction("Player/Sprint");
            _grab = actions.FindAction("Player/Attack");
            _interact = actions.FindAction("Player/Interact");
            actions.FindActionMap("Player")?.Enable();
        }

        private void OnEnable() => SetCursorLocked(true);
        private void OnDisable() => SetCursorLocked(false);

        private void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
                SetCursorLocked(false);

            // Re-capture on mouse *release* so the click that focuses the window never counts as a grab.
            if (Cursor.lockState != CursorLockMode.Locked && Mouse.current != null &&
                Mouse.current.leftButton.wasReleasedThisFrame)
                SetCursorLocked(true);
        }

        private static void SetCursorLocked(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }
    }
}
