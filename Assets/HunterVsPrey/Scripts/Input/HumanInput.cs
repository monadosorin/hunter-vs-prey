using UnityEngine;
using UnityEngine.InputSystem;

namespace HunterVsPrey
{
    /// <summary>
    /// WASD move · Shift run · Space jump · Ctrl/C slide (hold to crouch) · LMB tag/shoot ·
    /// Q X-Ray (shared by both Hunters) · RMB mark a ball / F find my ball (Prey) ·
    /// Esc releases the mouse · K instant death (editor debug).
    /// </summary>
    public class HumanInput : InputProvider
    {
        public float mouseSensitivity = 0.12f;

        public override ActorInput Read(float dt)
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            var i = new ActorInput();
            if (kb == null || mouse == null) return i;

            if (kb.escapeKey.wasPressedThisFrame) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            if (mouse.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                return i; // the click that grabs the mouse doesn't also fire
            }
            bool locked = Cursor.lockState == CursorLockMode.Locked;

            i.move = new Vector2((kb.dKey.isPressed ? 1 : 0) - (kb.aKey.isPressed ? 1 : 0),
                                 (kb.wKey.isPressed ? 1 : 0) - (kb.sKey.isPressed ? 1 : 0));
            if (locked)
            {
                Vector2 d = mouse.delta.ReadValue() * mouseSensitivity;
                i.yawDelta = d.x;
                i.pitchDelta = -d.y;
                i.primary = mouse.leftButton.wasPressedThisFrame;
                i.mark = mouse.rightButton.wasPressedThisFrame;
            }
            i.run = kb.leftShiftKey.isPressed;
            i.jump = kb.spaceKey.wasPressedThisFrame;
            i.slide = kb.leftCtrlKey.isPressed || kb.cKey.isPressed;
            i.xray = kb.qKey.wasPressedThisFrame;
            i.locate = kb.fKey.wasPressedThisFrame;
            return i;
        }
    }
}
