using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace HunterVsPrey
{
    /// <summary>
    /// Local player's first-person camera, or spectator after death / in AI-vs-AI:
    /// V cycles First-person → Third-person → Free-roam ghost, LMB/RMB switch the spectated player.
    /// </summary>
    public class PlayerCamera : MonoBehaviour
    {
        public enum Mode { Local, FirstPerson, ThirdPerson, Ghost }

        public Mode CurrentMode { get; private set; } = Mode.Ghost;
        public Actor Target { get; private set; }

        MatchManager match;
        Camera cam;
        float orbitYaw, orbitPitch = 15f, ghostYaw, ghostPitch = 30f;
        Actor hiddenActor;

        public void Init(MatchManager m)
        {
            match = m;
            cam = GetComponent<Camera>();
            cam.nearClipPlane = 0.05f;
            cam.fieldOfView = 80f;
            transform.SetPositionAndRotation(new Vector3(0, 70, -70), Quaternion.Euler(48, 0, 0));
            ghostYaw = 0f;
            ghostPitch = 45f;
        }

        public void FollowLocal(Actor a)
        {
            CurrentMode = Mode.Local;
            SetTarget(a);
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        public void StartSpectating()
        {
            var next = match.AllActors.FirstOrDefault(a => a.IsActive && a != match.LocalActor);
            CurrentMode = next ? Mode.FirstPerson : Mode.Ghost;
            SetTarget(next);
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        void SetTarget(Actor a)
        {
            if (hiddenActor) hiddenActor.SetFirstPersonHidden(false);
            hiddenActor = null;
            Target = a;
            if (a && (CurrentMode == Mode.Local || CurrentMode == Mode.FirstPerson))
            {
                a.SetFirstPersonHidden(true);
                hiddenActor = a;
            }
            if (a) orbitYaw = a.Motor.Yaw;
        }

        void LateUpdate()
        {
            if (match == null || match.State == MatchManager.Phase.Menu) return;
            var kb = Keyboard.current;
            var mouse = Mouse.current;

            if (CurrentMode == Mode.Local)
            {
                if (Target && Target.IsActive) Snap(Target.Eye);
                return; // dead Hunter waiting to respawn: camera holds still
            }

            // spectator controls
            if (Cursor.lockState != CursorLockMode.Locked && mouse.leftButton.wasPressedThisFrame)
            { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; return; }
            if (kb.escapeKey.wasPressedThisFrame) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            if (kb.vKey.wasPressedThisFrame)
            {
                CurrentMode = CurrentMode switch { Mode.FirstPerson => Mode.ThirdPerson, Mode.ThirdPerson => Mode.Ghost, _ => Mode.FirstPerson };
                if (CurrentMode == Mode.Ghost) { ghostYaw = transform.eulerAngles.y; ghostPitch = transform.eulerAngles.x; }
                SetTarget(Target && Target.IsActive ? Target : NextTarget(1));
            }
            if (CurrentMode != Mode.Ghost && Cursor.lockState == CursorLockMode.Locked)
            {
                if (mouse.leftButton.wasPressedThisFrame) SetTarget(NextTarget(1));
                if (mouse.rightButton.wasPressedThisFrame) SetTarget(NextTarget(-1));
            }
            if (CurrentMode != Mode.Ghost && (!Target || !Target.IsActive))
            {
                var n = NextTarget(1);
                if (n) SetTarget(n); else CurrentMode = Mode.Ghost;
            }

            Vector2 look = Cursor.lockState == CursorLockMode.Locked ? mouse.delta.ReadValue() * 0.12f : Vector2.zero;
            switch (CurrentMode)
            {
                case Mode.FirstPerson:
                    Snap(Target.Eye);
                    break;
                case Mode.ThirdPerson:
                    orbitYaw += look.x;
                    orbitPitch = Mathf.Clamp(orbitPitch - look.y, -20f, 70f);
                    var rot = Quaternion.Euler(orbitPitch, orbitYaw, 0);
                    Vector3 pivot = Target.transform.position + Vector3.up * 1.6f;
                    float dist = 4.5f;
                    if (Physics.SphereCast(pivot, 0.25f, rot * Vector3.back, out var hit, dist, Layers.EnvMask, QueryTriggerInteraction.Ignore))
                        dist = hit.distance;
                    transform.SetPositionAndRotation(pivot + rot * Vector3.back * dist, rot);
                    break;
                case Mode.Ghost:
                    ghostYaw += look.x;
                    ghostPitch = Mathf.Clamp(ghostPitch - look.y, -89f, 89f);
                    transform.rotation = Quaternion.Euler(ghostPitch, ghostYaw, 0);
                    Vector3 v = transform.right * ((kb.dKey.isPressed ? 1 : 0) - (kb.aKey.isPressed ? 1 : 0))
                              + transform.forward * ((kb.wKey.isPressed ? 1 : 0) - (kb.sKey.isPressed ? 1 : 0))
                              + Vector3.up * ((kb.spaceKey.isPressed ? 1 : 0) - (kb.leftCtrlKey.isPressed ? 1 : 0));
                    transform.position += v * ((kb.leftShiftKey.isPressed ? 22f : 10f) * Time.unscaledDeltaTime);
                    break;
            }
        }

        void Snap(Transform eye) => transform.SetPositionAndRotation(eye.position, eye.rotation);

        Actor NextTarget(int dir)
        {
            var list = match.AllActors.Where(a => a.IsActive && a != match.LocalActor).ToList();
            if (list.Count == 0) return null;
            int idx = Target ? list.IndexOf(Target) : -1;
            idx = ((idx + dir) % list.Count + list.Count) % list.Count;
            return list[idx];
        }
    }
}
