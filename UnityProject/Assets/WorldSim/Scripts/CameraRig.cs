using UnityEngine;
using UnityEngine.InputSystem;
using WorldSim.Core.World;
using ETouch = UnityEngine.InputSystem.EnhancedTouch.Touch;
using EnhancedTouchSupport = UnityEngine.InputSystem.EnhancedTouch.EnhancedTouchSupport;

namespace WorldSim.Presentation
{
    public enum CameraMode
    {
        Overhead,
        Free,
        FirstPerson,
    }

    /// <summary>
    /// Three camera modes over the same world (spec 3.3):
    ///  Overhead    – WASD/arrows pan, wheel zoom, Q/E or right-drag rotate; touch: 2-finger pan/pinch/twist
    ///  Free        – WASD move, Space/Ctrl up/down, right-drag look, Shift faster
    ///  FirstPerson – WASD walk on the ground, right-drag look
    /// </summary>
    public sealed class CameraRig : MonoBehaviour
    {
        public CameraMode Mode { get; private set; } = CameraMode.Overhead;
        /// <summary>Set by the tool controller: one-finger drag pans only when no tool needs the finger.</summary>
        public bool AllowSingleTouchPan { get; set; } = true;

        private WorldState _world;
        private Vector3 _focus;
        private float _distance = 450f;
        private float _yaw;
        private float _pitch = 55f;
        private float _lookYaw, _lookPitch;

        public void Init(WorldState world)
        {
            _world = world;
            Terrain t = world.Terrain;
            _focus = new Vector3(t.Width * t.CellSize * 0.5f, 0, t.Depth * t.CellSize * 0.5f);
            if (!EnhancedTouchSupport.enabled) EnhancedTouchSupport.Enable();
            ApplyOverhead();
        }

        public void SetMode(CameraMode mode)
        {
            if (mode == Mode) return;
            if (mode != CameraMode.Overhead)
            {
                Vector3 e = transform.eulerAngles;
                _lookYaw = e.y;
                _lookPitch = mode == CameraMode.FirstPerson ? 0f : NormalizePitch(e.x);
                if (mode == CameraMode.FirstPerson)
                {
                    Vector3 p = Mode == CameraMode.Overhead ? _focus : transform.position;
                    transform.position = new Vector3(p.x, GroundHeight(p) + 1.7f, p.z);
                }
            }
            else
            {
                Vector3 p = transform.position;
                _focus = new Vector3(p.x, 0, p.z);
                _yaw = transform.eulerAngles.y;
            }
            Mode = mode;
        }

        public void CycleMode() => SetMode((CameraMode)(((int)Mode + 1) % 3));

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            Keyboard kb = Keyboard.current;
            Mouse mouse = Mouse.current;
            if (kb != null && kb.cKey.wasPressedThisFrame) CycleMode();

            switch (Mode)
            {
                case CameraMode.Overhead: UpdateOverhead(kb, mouse, dt); break;
                case CameraMode.Free: UpdateFree(kb, mouse, dt, false); break;
                case CameraMode.FirstPerson: UpdateFree(kb, mouse, dt, true); break;
            }
        }

        private void UpdateOverhead(Keyboard kb, Mouse mouse, float dt)
        {
            Vector2 move = ReadMove(kb);
            float panSpeed = _distance * 1.2f;
            Quaternion yawRot = Quaternion.Euler(0, _yaw, 0);
            _focus += yawRot * new Vector3(move.x, 0, move.y) * panSpeed * dt;

            if (kb != null)
            {
                if (kb.qKey.isPressed) _yaw -= 90f * dt;
                if (kb.eKey.isPressed) _yaw += 90f * dt;
            }
            if (mouse != null)
            {
                float scroll = mouse.scroll.ReadValue().y;
                if (scroll != 0) _distance *= Mathf.Pow(0.999f, scroll);
                if (mouse.rightButton.isPressed)
                {
                    Vector2 d = mouse.delta.ReadValue();
                    _yaw += d.x * 0.25f;
                    _pitch = Mathf.Clamp(_pitch - d.y * 0.25f, 20f, 89f);
                }
                if (mouse.middleButton.isPressed)
                {
                    Vector2 d = mouse.delta.ReadValue();
                    _focus -= yawRot * new Vector3(d.x, 0, d.y) * _distance * 0.0015f;
                }
            }
            UpdateTouch(yawRot);

            Terrain t = _world.Terrain;
            _focus.x = Mathf.Clamp(_focus.x, 0, t.Width * t.CellSize);
            _focus.z = Mathf.Clamp(_focus.z, 0, t.Depth * t.CellSize);
            _focus.y = GroundHeight(_focus);
            _distance = Mathf.Clamp(_distance, 30f, 2500f);
            ApplyOverhead();
        }

        private void UpdateTouch(Quaternion yawRot)
        {
            var touches = ETouch.activeTouches;
            if (touches.Count == 1 && AllowSingleTouchPan)
            {
                Vector2 d = touches[0].delta;
                _focus -= yawRot * new Vector3(d.x, 0, d.y) * _distance * 0.0015f;
            }
            else if (touches.Count >= 2)
            {
                ETouch a = touches[0], b = touches[1];
                Vector2 prevA = a.screenPosition - a.delta, prevB = b.screenPosition - b.delta;
                float prevDist = Vector2.Distance(prevA, prevB), dist = Vector2.Distance(a.screenPosition, b.screenPosition);
                if (prevDist > 1f && dist > 1f) _distance *= prevDist / dist;

                float prevAngle = Mathf.Atan2(prevB.y - prevA.y, prevB.x - prevA.x) * Mathf.Rad2Deg;
                float angle = Mathf.Atan2(b.screenPosition.y - a.screenPosition.y, b.screenPosition.x - a.screenPosition.x) * Mathf.Rad2Deg;
                _yaw -= Mathf.DeltaAngle(prevAngle, angle);

                Vector2 mid = (a.delta + b.delta) * 0.5f;
                _focus -= yawRot * new Vector3(mid.x, 0, mid.y) * _distance * 0.0015f;
            }
        }

        private void ApplyOverhead()
        {
            Quaternion rot = Quaternion.Euler(_pitch, _yaw, 0);
            transform.rotation = rot;
            transform.position = _focus - rot * Vector3.forward * _distance;
        }

        private void UpdateFree(Keyboard kb, Mouse mouse, float dt, bool walk)
        {
            if (mouse != null && mouse.rightButton.isPressed)
            {
                Vector2 d = mouse.delta.ReadValue();
                _lookYaw += d.x * 0.15f;
                _lookPitch = Mathf.Clamp(_lookPitch - d.y * 0.15f, -85f, 85f);
            }
            transform.rotation = Quaternion.Euler(_lookPitch, _lookYaw, 0);

            Vector2 move = ReadMove(kb);
            bool fast = kb != null && kb.leftShiftKey.isPressed;
            float speed = walk ? (fast ? 15f : 5f) : (fast ? 300f : 80f);
            Vector3 pos = transform.position;
            if (walk)
            {
                Quaternion yawRot = Quaternion.Euler(0, _lookYaw, 0);
                pos += yawRot * new Vector3(move.x, 0, move.y) * speed * dt;
            }
            else
            {
                pos += transform.rotation * new Vector3(move.x, 0, move.y) * speed * dt;
                if (kb != null && kb.spaceKey.isPressed) pos.y += speed * dt;
                if (kb != null && kb.leftCtrlKey.isPressed) pos.y -= speed * dt;
            }

            Terrain t = _world.Terrain;
            pos.x = Mathf.Clamp(pos.x, 0, t.Width * t.CellSize);
            pos.z = Mathf.Clamp(pos.z, 0, t.Depth * t.CellSize);
            float ground = GroundHeight(pos);
            pos.y = walk ? ground + 1.7f : Mathf.Max(pos.y, ground + 2f);
            transform.position = pos;
        }

        private static Vector2 ReadMove(Keyboard kb)
        {
            if (kb == null) return Vector2.zero;
            var v = Vector2.zero;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) v.y += 1;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) v.y -= 1;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) v.x += 1;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) v.x -= 1;
            return v.sqrMagnitude > 1 ? v.normalized : v;
        }

        /// <summary>Terrain height, or sea surface when the ground is under water.</summary>
        private float GroundHeight(Vector3 p) =>
            Mathf.Max(_world.Terrain.SampleHeight(p.x, p.z), _world.Terrain.SeaLevel);

        private static float NormalizePitch(float x) => x > 180f ? x - 360f : x;
    }
}
