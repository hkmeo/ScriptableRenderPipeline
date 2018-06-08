//#define USE_SHADER_AS_SUBASSET
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.Experimental.VFX;
using UnityEngine;
using UnityEngine.Experimental.VFX;
using UnityEngine.Profiling;
using System.Reflection;

using Object = UnityEngine.Object;

namespace UnityEditor.VFX
{
#if !USE_SHADER_AS_SUBASSET
    public class VFXCacheManager : EditorWindow
    {
        //[MenuItem("VFX Editor/Build All VFXs")]
        public static void Build()
        {
            var vfxAssets = new List<VisualEffectAsset>();
            var vfxAssetsGuid = AssetDatabase.FindAssets("t:VisualEffectAsset");
            foreach (var guid in vfxAssetsGuid)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                var vfxAsset = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(assetPath);
                if (vfxAsset != null)
                {
                    vfxAssets.Add(vfxAsset);
                }
            }

            foreach (var vfxAsset in vfxAssets)
            {
                Debug.Log(string.Format("Recompile VFX asset: {0} ({1})", vfxAsset, AssetDatabase.GetAssetPath(vfxAsset)));
                vfxAsset.GetResource().GetOrCreateGraph().SetExpressionGraphDirty();
                vfxAsset.GetResource().GetOrCreateGraph().OnSaved();
            }
            AssetDatabase.SaveAssets();
        }
    }
#endif

    public class VisualEffectAssetModicationProcessor : UnityEditor.AssetModificationProcessor
    {
        static string[] OnWillSaveAssets(string[] paths)
        {
            Profiler.BeginSample("VisualEffectAssetModicationProcessor.OnWillSaveAssets");
            foreach (string path in paths.Where(t => t.EndsWith(".vfx")))
            {
                var vfxResource = VisualEffectResource.GetResourceAtPath(path);
                if (vfxResource != null)
                {
                    var graph = vfxResource.GetOrCreateGraph();
                    graph.OnSaved();
                    vfxResource.WriteAsset(); // write asset as the AssetDatabase won't do it.
                }
            }
            Profiler.EndSample();
            return paths;
        }

        static AssetDeleteResult OnWillDeleteAsset(string assetPath, RemoveAssetOptions option)
        {
            if (assetPath.EndsWith(".vfx"))
            {
                VisualEffectResource.DeleteAtPath(assetPath);
            }

            return AssetDeleteResult.DidNotDelete;
        }
    }

    static class VisualEffectAssetExtensions
    {
        public static VFXGraph GetOrCreateGraph(this VisualEffectResource resource)
        {
            VFXGraph graph = resource.graph as VFXGraph;

            if (graph == null)
            {
                string assetPath = AssetDatabase.GetAssetPath(resource);
                AssetDatabase.ImportAsset(assetPath);

                graph = resource.GetContents().OfType<VFXGraph>().FirstOrDefault();
            }

            if (graph == null)
            {
                graph = ScriptableObject.CreateInstance<VFXGraph>();
                resource.graph = graph;
                graph.hideFlags |= HideFlags.HideInHierarchy;
                graph.visualEffectResource = resource;
                // in this case we must update the subassets so that the graph is added to the resource dependencies
                graph.UpdateSubAssets();
            }

            graph.visualEffectResource = resource;
            return graph;
        }

        public static void UpdateSubAssets(this VisualEffectResource resource)
        {
            resource.GetOrCreateGraph().UpdateSubAssets();
        }

        public static VisualEffectResource GetResource(this VisualEffectAsset asset)
        {
            VisualEffectResource resource = VisualEffectResource.GetResourceAtPath(AssetDatabase.GetAssetPath(asset));

            if (resource == null)
            {
                string assetPath = AssetDatabase.GetAssetPath(asset);
                resource = VisualEffectResource.GetResourceAtPath(assetPath);
                if (resource == null)
                {
                    resource = new VisualEffectResource();
                    resource.SetAssetPath(assetPath);
                }
            }
            return resource;
        }
    }

    class VFXGraph : VFXModel
    {
        public VisualEffectResource visualEffectResource
        {
            get
            {
                return m_Owner;
            }
            set
            {
                if (m_Owner != value)
                {
                    m_Owner = value;
                    m_Owner.graph = this;
                    m_ExpressionGraphDirty = true;
                }
            }
        }
        [SerializeField]
        VFXUI m_UIInfos;

        public VFXUI UIInfos
        {
            get
            {
                if (m_UIInfos == null)
                {
                    m_UIInfos = ScriptableObject.CreateInstance<VFXUI>();
                }
                return m_UIInfos;
            }
        }

        public VFXParameterInfo[] m_ParameterInfo;

        public void BuildParameterInfo()
        {
            m_ParameterInfo = VFXParameterInfo.BuildParameterInfo(this);
        }

        public override bool AcceptChild(VFXModel model, int index = -1)
        {
            return !(model is VFXGraph); // Can hold any model except other VFXGraph
        }

        public string Backup()
        {
            Profiler.BeginSample("VFXGraph.Backup");
            var dependencies = new HashSet<ScriptableObject>();

            dependencies.Add(this);
            CollectDependencies(dependencies);


            var result = VFXMemorySerializer.StoreObjects(dependencies.Cast<ScriptableObject>().ToArray());

            Profiler.EndSample();

            return result;
        }

        public void Restore(string str)
        {
            Profiler.BeginSample("VFXGraph.Restore");
            var scriptableObject = VFXMemorySerializer.ExtractObjects(str, false);

            Profiler.BeginSample("VFXGraph.Restore SendUnknownChange");
            foreach (var model in scriptableObject.OfType<VFXModel>())
            {
                model.OnUnknownChange();
            }
            Profiler.EndSample();
            Profiler.EndSample();
        }

        public override void CollectDependencies(HashSet<ScriptableObject> objs)
        {
            Profiler.BeginSample("VFXEditor.CollectDependencies");
            try
            {
                if (m_UIInfos != null)
                    objs.Add(m_UIInfos);
                base.CollectDependencies(objs);
            }
            finally
            {
                Profiler.EndSample();
            }
        }

        public void OnSaved()
        {
            try
            {
                EditorUtility.DisplayProgressBar("Saving...", "Rebuild", 0);
                RecompileIfNeeded();
                float currentStep = 0;
                float stepCount = 1;
                m_saved = true;
            }
            catch (Exception e)
            {
                Debug.LogErrorFormat("Save failed : {0}", e);
            }
            EditorUtility.ClearProgressBar();
        }

        public void SanitizeGraph()
        {
            if (m_GraphSanitized)
                return;

            var objs = new HashSet<ScriptableObject>();
            CollectDependencies(objs);

            //Compute safe order of SlotContainer to minimize invalidation while removing old operators
            //TODO : this behavior is only needed, simply replace with a objs.OfType<VFXModel> once FloatN has been completely remove
            var remainingSlotContainers = objs.Where(o => o is IVFXSlotContainer).ToList();
            var sortedSlotContainers = new List<ScriptableObject>();
            while (remainingSlotContainers.Any())
            {
                int i = 0;
                bool hasRemoveSomething = false;
                while (i < remainingSlotContainers.Count)
                {
                    var obj = remainingSlotContainers[i];

                    var allLinkedOutputSlot = (obj as IVFXSlotContainer).outputSlots.SelectMany(o => o.allChildrenWhere(s => s.HasLink()).SelectMany(s => s.LinkedSlots));
                    var slotContainersOutputOwners = allLinkedOutputSlot.Select(o => o.owner as ScriptableObject).Where(o => o != null).Distinct().ToArray();

                    if (slotContainersOutputOwners.All(o => sortedSlotContainers.Contains(o)))
                    {
                        sortedSlotContainers.Add(remainingSlotContainers[i]);
                        remainingSlotContainers.RemoveAt(i);
                        hasRemoveSomething = true;
                    }
                    else
                    {
                        i++;
                    }
                }

                if (!hasRemoveSomething)
                {
                    Debug.Log("SanitizeGraph fail (infinite loop)");
                    return;
                }
            }

            var correctOrder = sortedSlotContainers.Concat(objs.Where(o => !(o is IVFXSlotContainer)));
            foreach (var model in correctOrder.OfType<VFXModel>())
                try
                {
                    model.Sanitize(); // This can modify dependencies but newly created model are supposed safe so we dont care about retrieving new dependencies
                }
                catch (Exception e)
                {
                    Debug.LogError(string.Format("Exception while sanitizing model: {0} of type {1}: {2} {3}", model.name, model.GetType(), e, e.StackTrace));
                }

            if (m_UIInfos != null)
                try
                {
                    m_UIInfos.Sanitize(this);
                }
                catch (Exception e)
                {
                    Debug.LogError(string.Format("Exception while sanitizing VFXUI: : {0} {1}", e , e.StackTrace));
                }

            m_GraphSanitized = true;
        }

        public void ClearCompileData()
        {
            m_CompiledData = null;


            m_ExpressionValuesDirty = true;
        }

        public void UpdateSubAssets()
        {
            if (visualEffectResource == null)
                return;
            Profiler.BeginSample("VFXEditor.UpdateSubAssets");
            try
            {
                var currentObjects = new HashSet<ScriptableObject>();
                currentObjects.Add(this);
                CollectDependencies(currentObjects);

                visualEffectResource.SetContents(currentObjects.Cast<Object>().ToArray());
            }
            catch (Exception e)
            {
                Debug.LogError(e);
            }
            finally
            {
                Profiler.EndSample();
            }
        }

        protected override void OnInvalidate(VFXModel model, VFXModel.InvalidationCause cause)
        {
            m_saved = false;
            base.OnInvalidate(model, cause);

            if (model is VFXParameter)
            {
                BuildParameterInfo();
            }

            if (cause == VFXModel.InvalidationCause.kStructureChanged)
            {
                UpdateSubAssets();
            }

            if (cause != VFXModel.InvalidationCause.kExpressionInvalidated &&
                cause != VFXModel.InvalidationCause.kExpressionGraphChanged)
            {
                EditorUtility.SetDirty(this);
            }

            if (cause == VFXModel.InvalidationCause.kExpressionGraphChanged)
            {
                m_ExpressionGraphDirty = true;
            }

            if (cause == VFXModel.InvalidationCause.kParamChanged)
            {
                m_ExpressionValuesDirty = true;
            }
        }

        public uint FindReducedExpressionIndexFromSlotCPU(VFXSlot slot)
        {
            RecompileIfNeeded();
            return compiledData.FindReducedExpressionIndexFromSlotCPU(slot);
        }

        public void SetExpressionGraphDirty()
        {
            m_ExpressionGraphDirty = true;
        }

        public void RecompileIfNeeded(bool preventRecompilation = false)
        {
            SanitizeGraph();

            bool considerGraphDirty = m_ExpressionGraphDirty && !preventRecompilation;
            if (considerGraphDirty)
            {
                compiledData.Compile();
            }
            else if (m_ExpressionValuesDirty && !m_ExpressionGraphDirty)
            {
                compiledData.UpdateValues();
            }

            if (considerGraphDirty)
                m_ExpressionGraphDirty = false;
            m_ExpressionValuesDirty = false;
        }

        private VFXGraphCompiledData compiledData
        {
            get
            {
                if (m_CompiledData == null)
                    m_CompiledData = new VFXGraphCompiledData(this);
                return m_CompiledData;
            }
        }

        [NonSerialized]
        private bool m_GraphSanitized = false;
        [NonSerialized]
        private bool m_ExpressionGraphDirty = true;
        [NonSerialized]
        private bool m_ExpressionValuesDirty = true;

        [NonSerialized]
        private VFXGraphCompiledData m_CompiledData;

        [SerializeField]
        protected bool m_saved = false;

        public bool saved { get { return m_saved; } }

        private VisualEffectResource m_Owner;
    }
}
