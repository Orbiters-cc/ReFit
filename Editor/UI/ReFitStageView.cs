using System;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Orbiters.ReFit.Editor
{
    /// <summary>
    /// The window's stage: the avatar wearing the clothing, in 3D. Drag to turn, right-drag (or Shift-drag) to move, the
    /// wheel zooms and a double-click frames the clothing again; the view presets glide to the front, three-quarter, side or
    /// back. After a refit one camera shows before and after, split by a handle the pointer drags (it sweeps open when the
    /// result arrives). Drawn with the Orbiters tools' shared preview camera (<see cref="OrbitCamera"/>,
    /// <see cref="OffscreenPreview"/>, which gives the editor its lighting back after every render). The scenes shown are
    /// the window's: it disposes them, this element only draws them.
    /// </summary>
    internal sealed class ReFitStageView : VisualElement
    {
        private const float HandleGrab = 18f;
        private const float FrameMargin = 1.25f;
        private static readonly Color Background = new Color(0.102f, 0.102f, 0.106f, 1f);

        private readonly VisualElement beforePane, afterPane, handle, empty;
        private readonly Image beforeImage, afterImage;
        private readonly Label beforeTag, afterTag, emptyTitle, emptyHint, busy, hint;
        private readonly OrbitCamera orbit = new OrbitCamera { FieldOfView = 26f, MinPitch = -80f, MaxPitch = 80f };

        private PreviewRenderUtility preview;
        private RenderTexture beforeTexture, afterTexture;
        private Material fallback;
        private ReFitScenePicture.Scene beforeScene, afterScene;
        private int framedKey;
        private Bounds frame = new Bounds(Vector3.up, Vector3.one * 0.3f), everything = new Bounds(Vector3.up, Vector3.one);
        private Quaternion facing = Quaternion.identity;
        private Vector3 viewDirection = ReFitScenePicture.Direction(ReFitScenePicture.View.ThreeQuarter);
        private float split = 1f, targetSplit = 1f;
        private bool dirty;
        private double lastTick;
        private int dragPointer = -1;
        private bool dragSplit, dragPans;
        private Vector2 lastPointer;

        /// <summary>Controls over the picture (the view presets); presses on them do not turn the camera.</summary>
        public VisualElement Overlay { get; }
        /// <summary>Raised when objects are dropped on the stage.</summary>
        public event Action<Object[]> Dropped;

        public bool Comparing => beforeScene != null && afterScene != null;
        public bool HasScene => beforeScene != null || afterScene != null;

        public ReFitStageView()
        {
            AddToClassList("refit-stage");
            focusable = true;

            afterPane = Pane("refit-stage__pane--after", out afterImage);
            beforePane = Pane("refit-stage__pane--before", out beforeImage);

            handle = new VisualElement { pickingMode = PickingMode.Ignore };
            handle.AddToClassList("refit-stage__handle");
            var line = new VisualElement { pickingMode = PickingMode.Ignore };
            line.AddToClassList("refit-stage__line");
            handle.Add(line);
            var knob = new VisualElement { pickingMode = PickingMode.Ignore };
            knob.AddToClassList("refit-stage__knob");
            var arrows = new Label("‹ ›") { pickingMode = PickingMode.Ignore };
            arrows.AddToClassList("refit-stage__knob-arrows");
            knob.Add(arrows);
            handle.Add(knob);
            Add(handle);

            beforeTag = Tag("BEFORE", "refit-stage__tag--before");
            afterTag = Tag("AFTER", "refit-stage__tag--after");

            empty = new VisualElement { pickingMode = PickingMode.Ignore };
            empty.AddToClassList("refit-stage__empty");
            var icon = new VectorIcon(IconGlyph.Clothes);
            icon.AddToClassList("refit-stage__empty-icon");
            empty.Add(icon);
            emptyTitle = new Label { pickingMode = PickingMode.Ignore };
            emptyTitle.AddToClassList("refit-stage__empty-title");
            empty.Add(emptyTitle);
            emptyHint = new Label { pickingMode = PickingMode.Ignore };
            emptyHint.AddToClassList("refit-stage__empty-hint");
            empty.Add(emptyHint);
            Add(empty);

            busy = new Label { pickingMode = PickingMode.Ignore };
            busy.AddToClassList("refit-stage__busy");
            Add(busy);

            hint = new Label("Drag to turn  ·  Scroll to zoom  ·  Right-drag to move  ·  Double-click to frame") { pickingMode = PickingMode.Ignore };
            hint.AddToClassList("refit-stage__hint");
            Add(hint);

            Overlay = new VisualElement { pickingMode = PickingMode.Ignore };
            Overlay.AddToClassList("refit-stage__overlay");
            Add(Overlay);

            RegisterCallback<GeometryChangedEvent>(_ => { PlacePanes(); dirty = true; });
            // Off a panel (the window closing, or the stage moving between columns) the renderer is freed; it is made
            // again on the next render.
            RegisterCallback<DetachFromPanelEvent>(_ => ReleaseRenderer());
            RegisterCallback<AttachToPanelEvent>(_ => dirty = true);
            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            RegisterCallback<PointerCaptureOutEvent>(_ => EndDrag());
            RegisterCallback<WheelEvent>(OnWheel);
            RegisterCallback<DragUpdatedEvent>(OnDragUpdated);
            RegisterCallback<DragPerformEvent>(OnDragPerform);
            RegisterCallback<DragLeaveEvent>(_ => RemoveFromClassList("refit-stage--drop"));
            schedule.Execute(Tick).Every(15);
            ShowEmpty("Pick the clothing to refit", "Select it in the Hierarchy, click it, or drop it here.");
        }

        private VisualElement Pane(string modifier, out Image image)
        {
            var pane = new VisualElement { pickingMode = PickingMode.Ignore };
            pane.AddToClassList("refit-stage__pane");
            pane.AddToClassList(modifier);
            image = new Image { scaleMode = ScaleMode.StretchToFill, pickingMode = PickingMode.Ignore };
            image.AddToClassList("refit-stage__image");
            pane.Add(image);
            Add(pane);
            return pane;
        }

        private Label Tag(string text, string modifier)
        {
            var tag = new Label(text) { pickingMode = PickingMode.Ignore };
            tag.AddToClassList("refit-stage__tag");
            tag.AddToClassList(modifier);
            Add(tag);
            return tag;
        }

        /// <summary>Nothing to show: a title and a hint.</summary>
        public void ShowEmpty(string title, string hintText)
        {
            beforeScene = afterScene = null;
            framedKey = 0;
            emptyTitle.text = title;
            emptyHint.text = hintText;
            Refresh(true);
        }

        /// <summary>The scene is being made: the stage stays plain.</summary>
        public void ShowBlank()
        {
            beforeScene = afterScene = null;
            Refresh(false);
        }

        /// <summary>
        /// The avatar wearing the piece; with <paramref name="after"/> the comparison, swept open when new. A scene of
        /// another piece (<paramref name="key"/>) frames the camera on it again; the same piece keeps the camera.
        /// </summary>
        public void Show(ReFitScenePicture.Scene before, ReFitScenePicture.Scene after, int key)
        {
            bool reveal = after != null && afterScene == null && before != null;
            beforeScene = before;
            afterScene = after;
            var main = before ?? after;
            if (main != null)
            {
                everything = main.bounds;
                if (after != null) everything.Encapsulate(after.bounds);
                if (key != framedKey)
                {
                    framedKey = key;
                    frame = main.accessoryBounds;
                    facing = main.facing;
                    Aim(true);
                }
            }
            if (!Comparing) split = targetSplit = 1f;
            else if (reveal)
            {
                // The result wipes in from the right, then rests at the middle.
                split = 1f;
                targetSplit = 0.5f;
            }
            Refresh(false);
        }

        /// <summary>Glides to a view preset: <paramref name="localDirection"/> relative to the avatar's facing.</summary>
        public void SetView(Vector3 localDirection)
        {
            viewDirection = localDirection;
            Aim(false);
        }

        /// <summary>A short line over the stage while the scene is made or a refit runs; null hides it.</summary>
        public void SetBusy(string text)
        {
            busy.text = text ?? string.Empty;
            busy.EnableInClassList("refit-stage__busy--shown", !string.IsNullOrEmpty(text));
        }

        /// <summary>Frees the renderer and lets go of the scenes (the window closing or reloading).</summary>
        public void Release()
        {
            ReleaseRenderer();
            beforeScene = afterScene = null;
        }

        // The preview renderer (and its preview scene), the textures and the fallback material.
        private void ReleaseRenderer()
        {
            if (preview != null) { preview.Cleanup(); preview = null; }
            OffscreenPreview.Release(ref beforeTexture);
            OffscreenPreview.Release(ref afterTexture);
            if (fallback != null) { Object.DestroyImmediate(fallback); fallback = null; }
            beforeImage.image = afterImage.image = null;
            dirty = true;
        }

        // The camera on the piece, from the preset direction, at the distance that frames it.
        private void Aim(bool snap)
        {
            orbit.TargetPivot = frame.center;
            orbit.TargetDistance = orbit.Fit(frame) * FrameMargin;
            orbit.LookFrom(facing * viewDirection);
            if (snap) orbit.Snap();
            dirty = true;
        }

        private void Refresh(bool showEmpty)
        {
            bool any = HasScene;
            empty.style.display = showEmpty && !any ? DisplayStyle.Flex : DisplayStyle.None;
            afterPane.style.display = any ? DisplayStyle.Flex : DisplayStyle.None;
            hint.style.display = any ? DisplayStyle.Flex : DisplayStyle.None;
            Overlay.style.display = any ? DisplayStyle.Flex : DisplayStyle.None;
            EnableInClassList("refit-stage--empty", !any);
            EnableInClassList("refit-stage--comparing", Comparing);
            if (!any) { beforeImage.image = afterImage.image = null; }
            PlacePanes();
            dirty = true;
        }

        private void PlacePanes()
        {
            float width = contentRect.width;
            if (float.IsNaN(width) || width <= 0f) return;
            bool comparing = Comparing;
            float x = Mathf.Round(width * Mathf.Clamp01(split));
            beforePane.style.display = comparing ? DisplayStyle.Flex : DisplayStyle.None;
            beforePane.style.width = x;
            beforeImage.style.width = width;
            handle.style.display = comparing ? DisplayStyle.Flex : DisplayStyle.None;
            handle.style.left = x;
            beforeTag.style.display = afterTag.style.display = comparing ? DisplayStyle.Flex : DisplayStyle.None;
            beforeTag.style.opacity = Mathf.Clamp01((x - 70f) / 40f);
            afterTag.style.opacity = Mathf.Clamp01((width - x - 70f) / 40f);
        }

        // ---- Input -------------------------------------------------------------------------------------

        private void OnPointerDown(PointerDownEvent evt)
        {
            if (!HasScene) return;
            if (evt.target is VisualElement pressed && pressed != this && Overlay.Contains(pressed)) return;
            Focus();
            if (evt.button == 0 && evt.clickCount == 2) { Aim(false); evt.StopPropagation(); return; }
            float localX = this.WorldToLocal(evt.position).x;
            dragSplit = Comparing && evt.button == 0 && !evt.shiftKey && Mathf.Abs(localX - contentRect.width * split) <= HandleGrab;
            dragPans = !dragSplit && (evt.button == 1 || evt.button == 2 || evt.button == 0 && evt.shiftKey);
            dragPointer = evt.pointerId;
            lastPointer = evt.position;
            this.CapturePointer(dragPointer);
            AddToClassList(dragSplit ? "refit-stage--splitting" : "refit-stage--dragging");
            if (dragSplit) MoveSplit(localX);
            evt.StopPropagation();
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (evt.pointerId != dragPointer || !this.HasPointerCapture(dragPointer)) return;
            Vector2 delta = (Vector2)evt.position - lastPointer;
            lastPointer = evt.position;
            if (dragSplit) MoveSplit(this.WorldToLocal(evt.position).x);
            else if (dragPans) orbit.Pan(delta, contentRect.height);
            else orbit.Turn(delta);
            dirty = true;
            evt.StopPropagation();
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            if (evt.pointerId != dragPointer) return;
            if (this.HasPointerCapture(dragPointer)) this.ReleasePointer(dragPointer);
            EndDrag();
        }

        private void EndDrag()
        {
            dragPointer = -1;
            dragSplit = dragPans = false;
            RemoveFromClassList("refit-stage--dragging");
            RemoveFromClassList("refit-stage--splitting");
        }

        private void MoveSplit(float x)
        {
            float width = contentRect.width;
            if (float.IsNaN(width) || width <= 0f) return;
            split = targetSplit = Mathf.Clamp(x / width, 0.02f, 0.98f);
            PlacePanes();
        }

        private void OnWheel(WheelEvent evt)
        {
            if (!HasScene) return;
            float fit = orbit.Fit(frame);
            orbit.Zoom(evt.delta.y, fit * 0.15f, Mathf.Max(orbit.Fit(everything) * 1.6f, fit * 3f));
            dirty = true;
            evt.StopPropagation();
            evt.PreventDefault();
        }

        private void OnDragUpdated(DragUpdatedEvent evt)
        {
            if (DragAndDrop.objectReferences == null || DragAndDrop.objectReferences.Length == 0) return;
            DragAndDrop.visualMode = DragAndDropVisualMode.Link;
            AddToClassList("refit-stage--drop");
        }

        private void OnDragPerform(DragPerformEvent evt)
        {
            RemoveFromClassList("refit-stage--drop");
            var objects = DragAndDrop.objectReferences;
            if (objects == null || objects.Length == 0) return;
            DragAndDrop.AcceptDrag();
            Dropped?.Invoke(objects);
        }

        // ---- Rendering ---------------------------------------------------------------------------------

        private void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            float dt = Mathf.Clamp((float)(now - lastTick), 0f, 0.1f);
            lastTick = now;
            if (panel == null || !HasScene) return;
            bool moving = orbit.Glide(dt, everything.size.magnitude * 1e-4f);
            if (Mathf.Abs(split - targetSplit) > 0.001f)
            {
                split = Mathf.Lerp(split, targetSplit, 1f - Mathf.Exp(-dt * 6f));
                PlacePanes();
                moving = true;
            }
            if (moving || dirty) Render();
        }

        private void Render()
        {
            dirty = false;
            var rect = contentRect;
            if (!HasScene || float.IsNaN(rect.width) || rect.width < 8f || rect.height < 8f) return;
            EnsureResources();
            float scale = EditorGUIUtility.pixelsPerPoint;
            int width = Mathf.Clamp(Mathf.RoundToInt(rect.width * scale), 8, 2048);
            int height = Mathf.Clamp(Mathf.RoundToInt(rect.height * scale), 8, 2048);
            OffscreenPreview.EnsureTexture(ref afterTexture, width, height, "ReFit Stage");
            Draw(afterScene ?? beforeScene, afterTexture);
            afterImage.image = afterTexture;
            afterImage.MarkDirtyRepaint();
            if (Comparing)
            {
                OffscreenPreview.EnsureTexture(ref beforeTexture, width, height, "ReFit Stage Before");
                Draw(beforeScene, beforeTexture);
                beforeImage.image = beforeTexture;
                beforeImage.MarkDirtyRepaint();
            }
        }

        private void Draw(ReFitScenePicture.Scene scene, RenderTexture texture)
        {
            var camera = preview.camera;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Background;
            orbit.Apply(camera, (float)texture.width / texture.height, everything.size.magnitude);
            var rotation = orbit.Rotation;
            preview.ambientColor = new Color(0.62f, 0.62f, 0.64f);
            preview.lights[0].intensity = 1.25f;
            preview.lights[0].transform.rotation = rotation * Quaternion.Euler(25f, -30f, 0f);
            preview.lights[1].intensity = 0.75f;
            preview.lights[1].transform.rotation = rotation * Quaternion.Euler(0f, 140f, 0f);
            foreach (var part in scene.parts)
            {
                if (part.mesh == null) continue;
                for (int submesh = 0; submesh < part.mesh.subMeshCount; submesh++)
                {
                    var material = part.materials != null && part.materials.Length > 0
                        ? part.materials[Mathf.Min(submesh, part.materials.Length - 1)] : null;
                    // A renderer without its material still shows, in plain grey.
                    preview.DrawMesh(part.mesh, part.matrix, material != null ? material : fallback, submesh);
                }
            }
            OffscreenPreview.Render(preview, texture);
        }

        private void EnsureResources()
        {
            if (preview != null) return;
            preview = new PreviewRenderUtility();
            preview.camera.allowHDR = false;
            var shader = Shader.Find("Standard");
            fallback = new Material(shader != null ? shader : Shader.Find("Hidden/InternalErrorShader"))
                { hideFlags = HideFlags.HideAndDontSave, color = new Color(0.62f, 0.63f, 0.66f) };
        }
    }
}
