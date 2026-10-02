using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Orbiters.Toolkit.Editor.Refit;
using UnityEditor;
using UnityEngine;

namespace Orbiters.ReFit.Editor
{
    /// <summary>
    /// ReFit as the refit engine of the other Orbiters tools (MCB, My Avatar), registered with Orbiters Toolkit when the
    /// editor loads: they ask Toolkit for a refit and never reference this package.
    /// </summary>
    internal sealed class ReFitToolkitEngine : IRefitEngine
    {
        // The last result of each renderer, so the commission window can show it.
        private static readonly Dictionary<int, (ReFitRequest request, ReFitResult result)> Last = new Dictionary<int, (ReFitRequest, ReFitResult)>();

        [InitializeOnLoadMethod]
        private static void Register() => RefitEngine.Register(new ReFitToolkitEngine());

        public string Name
        {
            get
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ReFitToolkitEngine).Assembly);
                return "ReFit " + (info != null ? info.version : "dev");
            }
        }

        /// <summary>The request a Toolkit job becomes: the armature is kept, shapes keep the body's names, no prefab.</summary>
        internal static ReFitRequest Request(RefitJob job)
        {
            var settings = new ReFitSettings { replaceArmature = false, transferWeights = false, prefixTransferredShapes = false, savePrefab = false };
            ReFitSettingsPresets.ApplyTightness(settings, job.Tightness);
            settings.coverDifferentBaseBody = job.Mode == RefitMode.Fit && job.CoverDifferentBaseBody;
            bool fit = job.Mode == RefitMode.Fit;
            return new ReFitRequest
            {
                mode = !fit ? ReFitMode.Blendshape : job.Shapes.Count > 0 ? ReFitMode.MeshAndBlendshape : ReFitMode.MeshToMesh,
                assetRenderer = job.Renderer,
                sourceAvatar = fit ? job.SourceAvatar : null,
                sourceBodyRenderer = fit ? job.SourceBody : null,
                targetAvatar = job.Avatar,
                targetBodyRenderer = job.Body,
                targetBlendshapes = job.Shapes.ToList(),
                settings = settings,
            };
        }

        public IEnumerator Run(RefitJob job, Action<float, string> progress, Action<RefitOutcome> done, CancellationToken cancellation)
        {
            if (job?.Renderer == null) throw new ArgumentException("A refit needs a mesh.", nameof(job));
            var request = Request(job);
            ReFitResult result = null;
            var routine = ReFitService.ExecuteCoroutine(request, (t, label) => progress?.Invoke(t, label), r => result = r, cancellation);
            return Drive(routine, () => result, request, job, done, cancellation);
        }

        private static IEnumerator Drive(IEnumerator routine, Func<ReFitResult> result, ReFitRequest request, RefitJob job,
            Action<RefitOutcome> done, CancellationToken cancellation)
        {
            using (routine as IDisposable)
                while (routine.MoveNext()) yield return routine.Current;
            var outcome = Outcome(result(), cancellation);
            if (job.Renderer != null) Last[job.Renderer.GetInstanceID()] = (request, result());
            done?.Invoke(outcome);
        }

        internal static RefitOutcome Outcome(ReFitResult result, CancellationToken cancellation)
        {
            var outcome = new RefitOutcome
            {
                Success = result != null && result.success,
                Mesh = result?.mesh,
                MeshPath = result?.meshAssetPath,
                PrimaryShape = result?.primaryShapeName,
                SourceShapes = result?.secondarySourceShapeNames ?? Array.Empty<string>(),
                GeneratedShapes = result?.secondaryShapeNames ?? Array.Empty<string>(),
            };
            if (result?.report != null)
                foreach (var message in result.report.messages)
                    outcome.Messages.Add(new RefitMessage { Severity = Severity(message.severity), Code = message.code, Text = message.text });
            outcome.Cancelled = !outcome.Success && (cancellation.IsCancellationRequested || outcome.Messages.Any(m => m.Code == "refit-cancelled"));
            return outcome;
        }

        private static RefitSeverity Severity(ReFitSeverity severity)
        {
            switch (severity)
            {
                case ReFitSeverity.Error: return RefitSeverity.Error;
                case ReFitSeverity.Warning: return RefitSeverity.Warning;
                default: return RefitSeverity.Info;
            }
        }

        public string SaveMetadata(SkinnedMeshRenderer renderer)
        {
            var metadata = renderer != null ? renderer.GetComponent<ReFitGeneratedAssetMetadata>() : null;
            return metadata != null && metadata.data != null ? JsonUtility.ToJson(metadata.data) : null;
        }

        public void LoadMetadata(SkinnedMeshRenderer renderer, string json)
        {
            if (renderer == null || string.IsNullOrEmpty(json)) return;
            var metadata = renderer.GetComponent<ReFitGeneratedAssetMetadata>();
            if (metadata == null) metadata = Undo.AddComponent<ReFitGeneratedAssetMetadata>(renderer.gameObject);
            else Undo.RecordObject(metadata, "ReFit metadata");
            metadata.data = JsonUtility.FromJson<ReFitGeneratedAssetMetadataData>(json);
            EditorUtility.SetDirty(metadata);
        }

        public void RemoveMetadata(SkinnedMeshRenderer renderer)
        {
            var metadata = renderer != null ? renderer.GetComponent<ReFitGeneratedAssetMetadata>() : null;
            if (metadata != null) Undo.DestroyObjectImmediate(metadata);
        }

        public void OpenCommission(RefitJob job, RefitOutcome outcome)
        {
            if (job?.Renderer == null) return;
            if (!Last.TryGetValue(job.Renderer.GetInstanceID(), out var last))
            {
                // Not refitted in this session (after a reload): show the mesh as it is now.
                var result = new ReFitResult { success = outcome != null && outcome.Success, sceneRenderer = job.Renderer, mesh = job.Renderer.sharedMesh };
                if (outcome != null)
                    foreach (var message in outcome.Messages.Where(m => m.Severity != RefitSeverity.Info))
                        result.report.messages.Add(new ReFitMessage
                        {
                            severity = message.Severity == RefitSeverity.Error ? ReFitSeverity.Error : ReFitSeverity.Warning,
                            code = message.Code, text = message.Text
                        });
                last = (Request(job), result);
            }
            ReFitWizard.OpenForCommission(last.request, last.result);
        }
    }
}
