using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace MDSFramework.Assets3D
{
    internal static class GlbRuntimeLoader
    {
        private const uint GlbMagic = 0x46546C67;     // "glTF"
        private const uint ChunkTypeJson = 0x4E4F534A; // "JSON"
        private const uint ChunkTypeBin = 0x004E4942;  // "BIN\0"

        private static readonly Dictionary<string, GlbCompiledTemplate> TemplateCache =
            new Dictionary<string, GlbCompiledTemplate>(StringComparer.OrdinalIgnoreCase);

        internal static GlbInstantiatedRig? Instantiate(
            string key,
            string? fullGlbPath,
            string fallbackArchetype,
            string? fallbackDecalPath,
            Transform? parent,
            float yawOffsetDeg)
        {
            GlbCompiledTemplate? template = GetOrLoadTemplate(key, fullGlbPath, fallbackArchetype, fallbackDecalPath);
            if (template == null)
            {
                return null;
            }

            GlbParsedModel parsed = template.Parsed;
            var rootGo = new GameObject($"MDS_Rig_{key}");
            if (parent != null)
            {
                rootGo.transform.SetParent(parent, false);
            }

            rootGo.transform.localPosition = Vector3.zero;
            rootGo.transform.localRotation = Quaternion.Euler(0f, yawOffsetDeg, 0f);
            rootGo.transform.localScale = Vector3.one;

            int nodeCount = parsed.Nodes.Count;
            var nodeTransforms = new Transform[nodeCount];
            var vlsCells = new List<(int index, Transform xform)>();
            var wheelAxles = new List<(int index, Transform xform)>();
            var rig = new GlbInstantiatedRig
            {
                RootObject = rootGo,
                NodeTransforms = nodeTransforms,
                Bounds = parsed.LocalBounds
            };

            for (int i = 0; i < nodeCount; i++)
            {
                GlbNodeData nodeData = parsed.Nodes[i];
                var go = new GameObject(string.IsNullOrEmpty(nodeData.Name) ? $"Node_{i}" : nodeData.Name);
                Transform tf = go.transform;
                tf.localPosition = nodeData.LocalPosition;
                tf.localRotation = nodeData.LocalRotation;
                tf.localScale = nodeData.LocalScale;
                nodeTransforms[i] = tf;

                if (nodeData.MeshIndex >= 0 && nodeData.MeshIndex < template.CompiledMeshes.Count)
                {
                    List<GlbCompiledPrimitive> primitives = template.CompiledMeshes[nodeData.MeshIndex];
                    for (int p = 0; p < primitives.Count; p++)
                    {
                        GlbCompiledPrimitive prim = primitives[p];
                        GameObject primTarget = p == 0 ? go : new GameObject($"{go.name}_p{p}");
                        if (p > 0)
                        {
                            primTarget.transform.SetParent(tf, false);
                        }

                        MeshFilter mf = primTarget.AddComponent<MeshFilter>();
                        mf.sharedMesh = prim.Mesh;

                        MeshRenderer mr = primTarget.AddComponent<MeshRenderer>();
                        mr.sharedMaterial = prim.Material;

                        bool isDecal = IsDecalName(go.name) || IsDecalName(prim.Material.name);
                        mr.shadowCastingMode = isDecal ? ShadowCastingMode.Off : ShadowCastingMode.On;
                        mr.receiveShadows = true;

                        if (isDecal)
                        {
                            rig.DecalRenderers.Add(mr);
                        }
                    }
                }
            }

            var hasParent = new bool[nodeCount];
            for (int i = 0; i < nodeCount; i++)
            {
                GlbNodeData nodeData = parsed.Nodes[i];
                for (int c = 0; c < nodeData.Children.Count; c++)
                {
                    int childIdx = nodeData.Children[c];
                    if (childIdx >= 0 && childIdx < nodeCount && childIdx != i && !hasParent[childIdx] &&
                        !nodeTransforms[i].IsChildOf(nodeTransforms[childIdx]))
                    {
                        nodeTransforms[childIdx].SetParent(nodeTransforms[i], false);
                        hasParent[childIdx] = true;
                    }
                }
            }

            if (parsed.RootNodeIndices.Count > 0)
            {
                for (int r = 0; r < parsed.RootNodeIndices.Count; r++)
                {
                    int rootIdx = parsed.RootNodeIndices[r];
                    if (rootIdx >= 0 && rootIdx < nodeCount && !hasParent[rootIdx])
                    {
                        nodeTransforms[rootIdx].SetParent(rootGo.transform, false);
                    }
                }
            }

            bool hasExactTurretBase = false;
            bool hasExactTurretHead = false;
            bool hasExactMuzzle = false;
            for (int i = 0; i < nodeCount; i++)
            {
                if (nodeTransforms[i].parent == null)
                {
                    nodeTransforms[i].SetParent(rootGo.transform, false);
                }

                ClassifySocket(
                    nodeTransforms[i],
                    parsed.Nodes[i],
                    rig,
                    vlsCells,
                    wheelAxles,
                    ref hasExactTurretBase,
                    ref hasExactTurretHead,
                    ref hasExactMuzzle);
            }

            if (vlsCells.Count > 0)
            {
                vlsCells.Sort((a, b) => a.index.CompareTo(b.index));
                for (int i = 0; i < vlsCells.Count; i++)
                {
                    rig.VlsCells.Add(vlsCells[i].xform);
                }
            }

            if (wheelAxles.Count > 0)
            {
                wheelAxles.Sort((a, b) => a.index.CompareTo(b.index));
                for (int i = 0; i < wheelAxles.Count; i++)
                {
                    rig.WheelAxles.Add(wheelAxles[i].xform);
                }
            }

            rig.DeployClip = SelectDeployClip(parsed.Animations);
            rig.DestroyClip = SelectClipByKeyword(parsed.Animations, "Destroy");
            return rig;
        }

        internal static void ClearCache()
        {
            foreach (GlbCompiledTemplate tpl in TemplateCache.Values)
            {
                for (int m = 0; m < tpl.CompiledMeshes.Count; m++)
                {
                    List<GlbCompiledPrimitive> prims = tpl.CompiledMeshes[m];
                    for (int p = 0; p < prims.Count; p++)
                    {
                        if (prims[p].Mesh != null)
                        {
                            UnityEngine.Object.Destroy(prims[p].Mesh);
                        }
                    }
                }

                for (int i = 0; i < tpl.OwnedMaterials.Count; i++)
                {
                    if (tpl.OwnedMaterials[i] != null)
                    {
                        UnityEngine.Object.Destroy(tpl.OwnedMaterials[i]);
                    }
                }

                for (int i = 0; i < tpl.OwnedTextures.Count; i++)
                {
                    if (tpl.OwnedTextures[i] != null)
                    {
                        UnityEngine.Object.Destroy(tpl.OwnedTextures[i]);
                    }
                }
            }

            TemplateCache.Clear();
            UrpGlbMaterialBuilder.ClearExternalTextureCache();
        }

        private static GlbCompiledTemplate? GetOrLoadTemplate(
            string key,
            string? fullGlbPath,
            string fallbackArchetype,
            string? fallbackDecalPath)
        {
            string cacheKey = !string.IsNullOrWhiteSpace(fullGlbPath) && File.Exists(fullGlbPath)
                ? fullGlbPath!
                : $"procedural::{key}::{fallbackArchetype}";

            if (TemplateCache.TryGetValue(cacheKey, out GlbCompiledTemplate? existing))
            {
                return existing;
            }

            GlbParsedModel? parsed = null;
            if (MdsConfig.EnableCustomGlbVisuals.Value &&
                !string.IsNullOrWhiteSpace(fullGlbPath) &&
                File.Exists(fullGlbPath))
            {
                try
                {
                    parsed = ParseGlbFile(key, fullGlbPath!);
                }
                catch (Exception ex)
                {
                    MdsLog.Warn($"Failed to parse GLB '{fullGlbPath}': {ex.Message}");
                    parsed = null;
                }
            }

            if (parsed == null)
            {
                parsed = ProceduralFallbackMeshBuilder.Build(key, fallbackArchetype, fallbackDecalPath);
                MdsLog.Info($"Using procedural fallback mesh '{fallbackArchetype}' for '{key}'.");
            }

            GlbCompiledTemplate compiled = CompileTemplate(parsed);
            TemplateCache[cacheKey] = compiled;
            return compiled;
        }

        private static GlbCompiledTemplate CompileTemplate(GlbParsedModel parsed)
        {
            var template = new GlbCompiledTemplate
            {
                Parsed = parsed
            };

            var builtMaterials = new List<Material>(parsed.Materials.Count);
            for (int i = 0; i < parsed.Materials.Count; i++)
            {
                Material mat = UrpGlbMaterialBuilder.CreateMaterial(
                    parsed.Materials[i],
                    parsed.SourceKey,
                    template.OwnedTextures);
                builtMaterials.Add(mat);
                template.OwnedMaterials.Add(mat);
            }

            Material? defaultMat = null;
            for (int m = 0; m < parsed.Meshes.Count; m++)
            {
                GlbMeshData meshData = parsed.Meshes[m];
                var compiledPrims = new List<GlbCompiledPrimitive>(meshData.Primitives.Count);

                for (int p = 0; p < meshData.Primitives.Count; p++)
                {
                    GlbPrimitiveData prim = meshData.Primitives[p];
                    var unityMesh = new Mesh
                    {
                        name = prim.Name,
                        hideFlags = HideFlags.DontUnloadUnusedAsset,
                        indexFormat = prim.Vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16,
                        vertices = prim.Vertices,
                        triangles = prim.Triangles
                    };

                    if (prim.Normals.Length == prim.Vertices.Length)
                    {
                        unityMesh.normals = prim.Normals;
                    }
                    else
                    {
                        unityMesh.RecalculateNormals();
                    }

                    if (prim.Uvs.Length == prim.Vertices.Length)
                    {
                        unityMesh.uv = prim.Uvs;
                        if (prim.Vertices.Length >= 3)
                        {
                            unityMesh.RecalculateTangents();
                        }
                    }

                    if (prim.Colors.Length == prim.Vertices.Length)
                    {
                        unityMesh.colors32 = prim.Colors;
                    }

                    unityMesh.RecalculateBounds();

                    Material mat;
                    if (prim.MaterialIndex >= 0 && prim.MaterialIndex < builtMaterials.Count)
                    {
                        mat = builtMaterials[prim.MaterialIndex];
                    }
                    else
                    {
                        if (defaultMat == null)
                        {
                            defaultMat = UrpGlbMaterialBuilder.CreateMaterial(
                                new GlbMaterialData(),
                                parsed.SourceKey,
                                template.OwnedTextures);
                            template.OwnedMaterials.Add(defaultMat);
                        }

                        mat = defaultMat;
                    }

                    compiledPrims.Add(new GlbCompiledPrimitive
                    {
                        Mesh = unityMesh,
                        Material = mat
                    });
                }

                template.CompiledMeshes.Add(compiledPrims);
            }

            return template;
        }

        private static GlbParsedModel? ParseGlbFile(string key, string glbPath)
        {
            byte[] bytes = File.ReadAllBytes(glbPath);
            if (bytes.Length < 20)
            {
                MdsLog.Warn($"GLB '{glbPath}' is too small ({bytes.Length} bytes).");
                return null;
            }

            using var ms = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(ms);

            uint magic = reader.ReadUInt32();
            uint version = reader.ReadUInt32();
            uint totalLength = reader.ReadUInt32();
            if (magic != GlbMagic || version != 2)
            {
                MdsLog.Warn($"GLB '{glbPath}' has unsupported header (magic=0x{magic:X8}, version={version}).");
                return null;
            }

            string? jsonText = null;
            byte[]? binChunk = null;
            long endPos = Math.Min(bytes.Length, (long)totalLength);
            while (ms.Position + 8 <= endPos)
            {
                uint chunkLen = reader.ReadUInt32();
                uint chunkType = reader.ReadUInt32();
                long remaining = endPos - ms.Position;
                if (chunkLen > remaining || chunkLen > int.MaxValue)
                {
                    break;
                }

                byte[] chunkData = reader.ReadBytes((int)chunkLen);
                if (chunkType == ChunkTypeJson && jsonText == null)
                {
                    jsonText = Encoding.UTF8.GetString(chunkData);
                }
                else if (chunkType == ChunkTypeBin && binChunk == null)
                {
                    binChunk = chunkData;
                }
            }

            if (string.IsNullOrEmpty(jsonText) || binChunk == null)
            {
                MdsLog.Warn($"GLB '{glbPath}' is missing JSON or BIN chunk.");
                return null;
            }

            JObject root = JObject.Parse(jsonText!);
            if (root["extensionsRequired"] is JArray extReq)
            {
                for (int i = 0; i < extReq.Count; i++)
                {
                    if (string.Equals(extReq[i].Value<string>(), "KHR_draco_mesh_compression", StringComparison.OrdinalIgnoreCase))
                    {
                        MdsLog.Warn($"GLB '{Path.GetFileName(glbPath)}' uses Draco compression (unsupported at runtime).");
                        return null;
                    }
                }
            }

            JArray? nodesArr = root["nodes"] as JArray;
            JArray? meshesArr = root["meshes"] as JArray;
            JArray? accessors = root["accessors"] as JArray;
            JArray? bufferViews = root["bufferViews"] as JArray;
            JArray? materialsArr = root["materials"] as JArray;
            JArray? texturesArr = root["textures"] as JArray;
            JArray? imagesArr = root["images"] as JArray;
            JArray? samplersArr = root["samplers"] as JArray;
            JArray? animsArr = root["animations"] as JArray;

            if (meshesArr == null || accessors == null || bufferViews == null)
            {
                MdsLog.Warn($"GLB '{glbPath}' contains no mesh/accessor data.");
                return null;
            }

            var model = new GlbParsedModel
            {
                SourceKey = key,
                FileName = Path.GetFileName(glbPath)
            };

            ParseMaterials(materialsArr, texturesArr, imagesArr, samplersArr, bufferViews, binChunk, model.Materials);
            ParseMeshes(meshesArr, accessors, bufferViews, binChunk, model);
            ParseNodes(nodesArr, model);
            ParseSceneRoots(root, model);
            ParseAnimations(animsArr, accessors, bufferViews, binChunk, model);

            model.LocalBounds = ComputeModelBounds(model);
            MdsLog.Debug(
                $"Parsed GLB '{model.FileName}': nodes={model.Nodes.Count}, meshes={model.Meshes.Count}, " +
                $"mats={model.Materials.Count}, anims={model.Animations.Count}.");
            return model;
        }

        private static void ParseMaterials(
            JArray? materials,
            JArray? textures,
            JArray? images,
            JArray? samplers,
            JArray bufferViews,
            byte[] binChunk,
            List<GlbMaterialData> output)
        {
            if (materials == null)
            {
                return;
            }

            for (int i = 0; i < materials.Count; i++)
            {
                JObject? matObj = materials[i] as JObject;
                var mat = new GlbMaterialData
                {
                    Name = matObj?["name"]?.Value<string>() ?? $"mat_{i}",
                    AlphaMode = matObj?["alphaMode"]?.Value<string>() ?? "OPAQUE",
                    AlphaCutoff = matObj?["alphaCutoff"]?.Value<float>() ?? 0.5f,
                    DoubleSided = matObj?["doubleSided"]?.Value<bool>() ?? false
                };

                float emissiveStrength = matObj?["extensions"]?["KHR_materials_emissive_strength"]?["emissiveStrength"]?.Value<float>() ?? 1f;
                if (matObj?["emissiveFactor"] is JArray ef && ef.Count >= 3)
                {
                    float eScale = Mathf.Max(0f, emissiveStrength);
                    float r = ef[0].Value<float>() * eScale;
                    float g = ef[1].Value<float>() * eScale;
                    float b = ef[2].Value<float>() * eScale;
                    if (r > 0.001f || g > 0.001f || b > 0.001f)
                    {
                        mat.HasEmissive = true;
                        mat.EmissiveColor = new Color(r, g, b, 1f);
                    }
                }

                bool hasBaseTex = false;
                bool baseWrap = true;
                bool hasMrTex = false;
                bool mrWrap = true;
                JObject? pbr = matObj?["pbrMetallicRoughness"] as JObject;
                if (pbr != null)
                {
                    if (pbr["baseColorFactor"] is JArray bcf && bcf.Count >= 4)
                    {
                        mat.BaseColor = new Color(
                            bcf[0].Value<float>(),
                            bcf[1].Value<float>(),
                            bcf[2].Value<float>(),
                            bcf[3].Value<float>());
                    }

                    hasBaseTex = TryExtractTexture(
                        pbr["baseColorTexture"] as JObject,
                        textures,
                        images,
                        samplers,
                        bufferViews,
                        binChunk,
                        out mat.BaseColorTextureBytes,
                        out baseWrap);

                    hasMrTex = TryExtractTexture(
                        pbr["metallicRoughnessTexture"] as JObject,
                        textures,
                        images,
                        samplers,
                        bufferViews,
                        binChunk,
                        out mat.MetallicRoughnessTextureBytes,
                        out mrWrap);

                    float metallicFactor = pbr["metallicFactor"] != null
                        ? Mathf.Clamp01(pbr["metallicFactor"]!.Value<float>())
                        : (hasMrTex ? 1f : 0.1f);
                    float roughnessFactor = pbr["roughnessFactor"] != null
                        ? Mathf.Clamp01(pbr["roughnessFactor"]!.Value<float>())
                        : (hasMrTex ? 1f : 0.65f);

                    mat.MetallicFactor = metallicFactor;
                    mat.RoughnessFactor = roughnessFactor;
                    mat.Metallic = metallicFactor;
                    mat.Smoothness = 1f - roughnessFactor;
                }

                JObject? normObj = matObj?["normalTexture"] as JObject;
                bool hasNormTex = TryExtractTexture(
                    normObj,
                    textures,
                    images,
                    samplers,
                    bufferViews,
                    binChunk,
                    out mat.NormalTextureBytes,
                    out bool normWrap);
                if (normObj?["scale"] != null)
                {
                    mat.NormalScale = Mathf.Clamp(normObj["scale"]!.Value<float>(), 0f, 4f);
                }

                JObject? occObj = matObj?["occlusionTexture"] as JObject;
                bool hasOccTex = TryExtractTexture(
                    occObj,
                    textures,
                    images,
                    samplers,
                    bufferViews,
                    binChunk,
                    out mat.OcclusionTextureBytes,
                    out bool occWrap);
                if (occObj?["strength"] != null)
                {
                    mat.OcclusionStrength = Mathf.Clamp01(occObj["strength"]!.Value<float>());
                }

                TryExtractTexture(
                    matObj?["emissiveTexture"] as JObject,
                    textures,
                    images,
                    samplers,
                    bufferViews,
                    binChunk,
                    out mat.EmissiveTextureBytes,
                    out _);

                bool isBlend = string.Equals(mat.AlphaMode, "BLEND", StringComparison.OrdinalIgnoreCase);
                mat.WrapRepeat = !isBlend && (hasBaseTex ? baseWrap : hasNormTex ? normWrap : hasMrTex ? mrWrap : hasOccTex ? occWrap : true);

                output.Add(mat);
            }
        }

        private static bool TryExtractTexture(
            JObject? texInfo,
            JArray? textures,
            JArray? images,
            JArray? samplers,
            JArray bufferViews,
            byte[] binChunk,
            out byte[]? bytes,
            out bool wrapRepeat)
        {
            bytes = null;
            wrapRepeat = true;
            if (texInfo == null || textures == null || images == null)
            {
                return false;
            }

            int? texIdx = texInfo["index"]?.Value<int?>();
            if (!texIdx.HasValue || texIdx.Value < 0 || texIdx.Value >= textures.Count)
            {
                return false;
            }

            JToken texObj = textures[texIdx.Value];
            int? samplerIdx = texObj?["sampler"]?.Value<int?>();
            if (samplerIdx.HasValue && samplers != null && samplerIdx.Value >= 0 && samplerIdx.Value < samplers.Count)
            {
                int wrapS = samplers[samplerIdx.Value]?["wrapS"]?.Value<int?>() ?? 10497;
                int wrapT = samplers[samplerIdx.Value]?["wrapT"]?.Value<int?>() ?? 10497;
                wrapRepeat = (wrapS != 33071) && (wrapT != 33071);
            }

            int? imgIdx = texObj?["source"]?.Value<int?>();
            if (!imgIdx.HasValue || imgIdx.Value < 0 || imgIdx.Value >= images.Count)
            {
                return false;
            }

            int? bvIdx = images[imgIdx.Value]?["bufferView"]?.Value<int?>();
            if (!bvIdx.HasValue || bvIdx.Value < 0 || bvIdx.Value >= bufferViews.Count)
            {
                return false;
            }

            if (bufferViews[bvIdx.Value] is not JObject bv)
            {
                return false;
            }

            int offset = bv["byteOffset"]?.Value<int>() ?? 0;
            int len = bv["byteLength"]?.Value<int>() ?? 0;
            if (offset < 0 || len <= 0 || len > 64 * 1024 * 1024 || (long)offset + len > binChunk.Length)
            {
                return false;
            }

            bytes = new byte[len];
            Buffer.BlockCopy(binChunk, offset, bytes, 0, len);
            return true;
        }

        private static void ParseMeshes(
            JArray meshesArr,
            JArray accessors,
            JArray bufferViews,
            byte[] binChunk,
            GlbParsedModel model)
        {
            for (int m = 0; m < meshesArr.Count; m++)
            {
                JObject? meshObj = meshesArr[m] as JObject;
                string meshName = meshObj?["name"]?.Value<string>() ?? $"mesh_{m}";
                var meshData = new GlbMeshData { Name = meshName };

                if (meshObj?["primitives"] is JArray prims)
                {
                    for (int p = 0; p < prims.Count; p++)
                    {
                        if (prims[p] is not JObject prim)
                        {
                            continue;
                        }

                        if (prim["extensions"]?["KHR_draco_mesh_compression"] != null)
                        {
                            continue;
                        }

                        int mode = prim["mode"]?.Value<int>() ?? 4;
                        if (mode != 4)
                        {
                            continue;
                        }

                        JObject? attrs = prim["attributes"] as JObject;
                        int? posAcc = attrs?["POSITION"]?.Value<int?>();
                        if (!posAcc.HasValue)
                        {
                            continue;
                        }

                        Vector3[] verts = ReadVec3Array(posAcc.Value, accessors, bufferViews, binChunk, negateX: true);
                        if (verts.Length == 0)
                        {
                            continue;
                        }

                        int? normAcc = attrs?["NORMAL"]?.Value<int?>();
                        Vector3[] norms = normAcc.HasValue
                            ? ReadVec3Array(normAcc.Value, accessors, bufferViews, binChunk, negateX: true)
                            : Array.Empty<Vector3>();

                        int? uvAcc = attrs?["TEXCOORD_0"]?.Value<int?>();
                        Vector2[] uvs = uvAcc.HasValue
                            ? ReadVec2Array(uvAcc.Value, accessors, bufferViews, binChunk)
                            : Array.Empty<Vector2>();

                        int? colorAcc = attrs?["COLOR_0"]?.Value<int?>();
                        Color32[] colors = colorAcc.HasValue
                            ? ReadColor32Array(colorAcc.Value, accessors, bufferViews, binChunk)
                            : Array.Empty<Color32>();

                        int? idxAcc = prim["indices"]?.Value<int?>();
                        int[] tris;
                        if (idxAcc.HasValue)
                        {
                            tris = ReadIndices(idxAcc.Value, accessors, bufferViews, binChunk);
                        }
                        else
                        {
                            tris = new int[verts.Length];
                            for (int i = 0; i < verts.Length; i++)
                            {
                                tris[i] = i;
                            }
                        }

                        if (tris.Length < 3)
                        {
                            continue;
                        }

                        int triCount = (tris.Length / 3) * 3;
                        if (triCount != tris.Length)
                        {
                            Array.Resize(ref tris, triCount);
                        }

                        bool invalidIndex = false;
                        for (int i = 0; i < tris.Length; i++)
                        {
                            if (tris[i] < 0 || tris[i] >= verts.Length)
                            {
                                invalidIndex = true;
                                break;
                            }
                        }

                        if (invalidIndex)
                        {
                            MdsLog.Warn($"GLB '{model.FileName}' primitive '{meshName}_p{p}' has out-of-range vertex index; skipping.");
                            continue;
                        }

                        // Reverse triangle winding after X negation.
                        for (int i = 0; i + 2 < tris.Length; i += 3)
                        {
                            int tmp = tris[i + 1];
                            tris[i + 1] = tris[i + 2];
                            tris[i + 2] = tmp;
                        }

                        int matIndex = prim["material"]?.Value<int>() ?? -1;
                        meshData.Primitives.Add(new GlbPrimitiveData
                        {
                            Name = $"{meshName}_p{p}",
                            Vertices = verts,
                            Normals = norms,
                            Uvs = uvs,
                            Colors = colors,
                            Triangles = tris,
                            MaterialIndex = matIndex
                        });
                    }
                }

                model.Meshes.Add(meshData);
            }
        }

        private static void ParseNodes(JArray? nodesArr, GlbParsedModel model)
        {
            if (nodesArr == null || nodesArr.Count == 0)
            {
                for (int m = 0; m < model.Meshes.Count; m++)
                {
                    model.Nodes.Add(new GlbNodeData
                    {
                        Index = m,
                        Name = model.Meshes[m].Name,
                        MeshIndex = m
                    });
                    model.RootNodeIndices.Add(m);
                }

                return;
            }

            for (int i = 0; i < nodesArr.Count; i++)
            {
                JObject? n = nodesArr[i] as JObject;
                var nodeData = new GlbNodeData
                {
                    Index = i,
                    Name = n?["name"]?.Value<string>() ?? $"Node_{i}",
                    MeshIndex = n?["mesh"]?.Value<int>() ?? -1
                };

                if (n != null)
                {
                    ReadNodeTrs(n, out nodeData.LocalPosition, out nodeData.LocalRotation, out nodeData.LocalScale);
                    if (n["children"] is JArray children)
                    {
                        for (int c = 0; c < children.Count; c++)
                        {
                            nodeData.Children.Add(children[c].Value<int>());
                        }
                    }
                }

                model.Nodes.Add(nodeData);
            }
        }

        private static void ParseSceneRoots(JObject root, GlbParsedModel model)
        {
            if (model.RootNodeIndices.Count > 0)
            {
                return;
            }

            JArray? scenes = root["scenes"] as JArray;
            int sceneIdx = root["scene"]?.Value<int>() ?? 0;
            if (scenes != null && sceneIdx >= 0 && sceneIdx < scenes.Count &&
                scenes[sceneIdx]?["nodes"] is JArray rootNodes)
            {
                for (int i = 0; i < rootNodes.Count; i++)
                {
                    model.RootNodeIndices.Add(rootNodes[i].Value<int>());
                }
            }
        }

        private static void ParseAnimations(
            JArray? animsArr,
            JArray accessors,
            JArray bufferViews,
            byte[] binChunk,
            GlbParsedModel model)
        {
            if (animsArr == null)
            {
                return;
            }

            for (int a = 0; a < animsArr.Count; a++)
            {
                if (animsArr[a] is not JObject animObj)
                {
                    continue;
                }

                JArray? channels = animObj["channels"] as JArray;
                JArray? samplers = animObj["samplers"] as JArray;
                if (channels == null || samplers == null)
                {
                    continue;
                }

                var clip = new GlbAnimationClipData
                {
                    Name = animObj["name"]?.Value<string>() ?? $"Clip_{a}"
                };

                for (int c = 0; c < channels.Count; c++)
                {
                    if (channels[c] is not JObject chanObj)
                    {
                        continue;
                    }

                    JObject? targetObj = chanObj["target"] as JObject;
                    int? nodeIdx = targetObj?["node"]?.Value<int?>();
                    string? pathStr = targetObj?["path"]?.Value<string>();
                    int? samplerIdx = chanObj["sampler"]?.Value<int?>();

                    if (!nodeIdx.HasValue || string.IsNullOrEmpty(pathStr) || !samplerIdx.HasValue)
                    {
                        continue;
                    }

                    if (samplerIdx.Value < 0 || samplerIdx.Value >= samplers.Count ||
                        samplers[samplerIdx.Value] is not JObject samplerObj)
                    {
                        continue;
                    }

                    int? inputAcc = samplerObj["input"]?.Value<int?>();
                    int? outputAcc = samplerObj["output"]?.Value<int?>();
                    if (!inputAcc.HasValue || !outputAcc.HasValue)
                    {
                        continue;
                    }

                    float[] times = ReadFloatScalarArray(inputAcc.Value, accessors, bufferViews, binChunk);
                    if (times.Length == 0)
                    {
                        continue;
                    }

                    string interpStr = samplerObj["interpolation"]?.Value<string>() ?? "LINEAR";
                    GlbInterpolationMode interp = string.Equals(interpStr, "STEP", StringComparison.OrdinalIgnoreCase)
                        ? GlbInterpolationMode.Step
                        : string.Equals(interpStr, "CUBICSPLINE", StringComparison.OrdinalIgnoreCase)
                            ? GlbInterpolationMode.CubicSpline
                            : GlbInterpolationMode.Linear;

                    var chanData = new GlbAnimationChannelData
                    {
                        NodeIndex = nodeIdx.Value,
                        Interpolation = interp,
                        Times = times
                    };

                    bool hasValues;
                    if (string.Equals(pathStr, "translation", StringComparison.OrdinalIgnoreCase))
                    {
                        chanData.Path = GlbAnimationPath.Translation;
                        chanData.Vec3Values = ReadVec3Array(outputAcc.Value, accessors, bufferViews, binChunk, negateX: true);
                        hasValues = chanData.Vec3Values.Length > 0;
                    }
                    else if (string.Equals(pathStr, "scale", StringComparison.OrdinalIgnoreCase))
                    {
                        chanData.Path = GlbAnimationPath.Scale;
                        chanData.Vec3Values = ReadVec3Array(outputAcc.Value, accessors, bufferViews, binChunk, negateX: false);
                        hasValues = chanData.Vec3Values.Length > 0;
                    }
                    else if (string.Equals(pathStr, "rotation", StringComparison.OrdinalIgnoreCase))
                    {
                        chanData.Path = GlbAnimationPath.Rotation;
                        chanData.QuatValues = ReadQuatArray(outputAcc.Value, accessors, bufferViews, binChunk);
                        hasValues = chanData.QuatValues.Length > 0;
                    }
                    else
                    {
                        continue;
                    }

                    if (!hasValues)
                    {
                        continue;
                    }

                    float clipEnd = times[times.Length - 1];
                    if (clipEnd > clip.Duration)
                    {
                        clip.Duration = clipEnd;
                    }

                    clip.Channels.Add(chanData);
                }

                if (clip.Channels.Count > 0)
                {
                    model.Animations.Add(clip);
                }
            }
        }

        private static void ReadNodeTrs(JObject node, out Vector3 pos, out Quaternion rot, out Vector3 scale)
        {
            if (node["matrix"] is JArray m && m.Count == 16)
            {
                var mat = new Matrix4x4();
                for (int col = 0; col < 4; col++)
                {
                    for (int row = 0; row < 4; row++)
                    {
                        mat[row, col] = m[(col * 4) + row].Value<float>();
                    }
                }

                Matrix4x4 flip = Matrix4x4.Scale(new Vector3(-1f, 1f, 1f));
                Matrix4x4 unityMat = flip * mat * flip;
                pos = unityMat.GetColumn(3);
                Vector3 sx = unityMat.GetColumn(0);
                Vector3 sy = unityMat.GetColumn(1);
                Vector3 sz = unityMat.GetColumn(2);
                scale = new Vector3(sx.magnitude, sy.magnitude, sz.magnitude);
                if (scale.x > 0.0001f && scale.y > 0.0001f && scale.z > 0.0001f)
                {
                    if (Vector3.Dot(Vector3.Cross(sx, sy), sz) < 0f)
                    {
                        scale.x = -scale.x;
                    }

                    rot = Quaternion.LookRotation(sz / scale.z, sy / scale.y);
                }
                else
                {
                    rot = Quaternion.identity;
                }

                return;
            }

            pos = Vector3.zero;
            if (node["translation"] is JArray ta && ta.Count >= 3)
            {
                pos = new Vector3(-ta[0].Value<float>(), ta[1].Value<float>(), ta[2].Value<float>());
            }

            rot = Quaternion.identity;
            if (node["rotation"] is JArray ra && ra.Count >= 4)
            {
                rot = new Quaternion(
                    ra[0].Value<float>(),
                    -ra[1].Value<float>(),
                    -ra[2].Value<float>(),
                    ra[3].Value<float>());
                if (rot.x * rot.x + rot.y * rot.y + rot.z * rot.z + rot.w * rot.w > 0.00001f)
                {
                    rot = Quaternion.Normalize(rot);
                }
                else
                {
                    rot = Quaternion.identity;
                }
            }

            scale = Vector3.one;
            if (node["scale"] is JArray sa && sa.Count >= 3)
            {
                scale = new Vector3(sa[0].Value<float>(), sa[1].Value<float>(), sa[2].Value<float>());
            }
        }

        private static Bounds ComputeModelBounds(GlbParsedModel model)
        {
            var bounds = new Bounds();
            bool hasBounds = false;
            var visited = new HashSet<int>();
            int nodeCount = model.Nodes.Count;
            var hasParent = new bool[nodeCount];
            for (int i = 0; i < nodeCount; i++)
            {
                GlbNodeData nodeData = model.Nodes[i];
                for (int c = 0; c < nodeData.Children.Count; c++)
                {
                    int childIdx = nodeData.Children[c];
                    if (childIdx >= 0 && childIdx < nodeCount && childIdx != i)
                    {
                        hasParent[childIdx] = true;
                    }
                }
            }

            var traversedRoots = new HashSet<int>();
            for (int r = 0; r < model.RootNodeIndices.Count; r++)
            {
                int rootIdx = model.RootNodeIndices[r];
                if (rootIdx >= 0 && rootIdx < nodeCount && !hasParent[rootIdx] && traversedRoots.Add(rootIdx))
                {
                    AccumulateNodeBounds(model, rootIdx, Matrix4x4.identity, visited, 0, ref bounds, ref hasBounds);
                }
            }

            for (int i = 0; i < nodeCount; i++)
            {
                if (!hasParent[i] && traversedRoots.Add(i))
                {
                    AccumulateNodeBounds(model, i, Matrix4x4.identity, visited, 0, ref bounds, ref hasBounds);
                }
            }

            if (!hasBounds)
            {
                return new Bounds(Vector3.zero, Vector3.one);
            }

            return bounds;
        }

        private static void AccumulateNodeBounds(
            GlbParsedModel model,
            int nodeIdx,
            Matrix4x4 parentMat,
            HashSet<int> visited,
            int depth,
            ref Bounds bounds,
            ref bool hasBounds)
        {
            if (depth > 128 || nodeIdx < 0 || nodeIdx >= model.Nodes.Count || !visited.Add(nodeIdx))
            {
                return;
            }

            try
            {
                GlbNodeData node = model.Nodes[nodeIdx];
                Matrix4x4 local = Matrix4x4.TRS(node.LocalPosition, node.LocalRotation, node.LocalScale);
                Matrix4x4 world = parentMat * local;

                if (node.MeshIndex >= 0 && node.MeshIndex < model.Meshes.Count)
                {
                    GlbMeshData mesh = model.Meshes[node.MeshIndex];
                    for (int p = 0; p < mesh.Primitives.Count; p++)
                    {
                        Vector3[] verts = mesh.Primitives[p].Vertices;
                        for (int v = 0; v < verts.Length; v++)
                        {
                            Vector3 wp = world.MultiplyPoint3x4(verts[v]);
                            if (!hasBounds)
                            {
                                bounds = new Bounds(wp, Vector3.zero);
                                hasBounds = true;
                            }
                            else
                            {
                                bounds.Encapsulate(wp);
                            }
                        }
                    }
                }

                for (int c = 0; c < node.Children.Count; c++)
                {
                    AccumulateNodeBounds(model, node.Children[c], world, visited, depth + 1, ref bounds, ref hasBounds);
                }
            }
            finally
            {
                visited.Remove(nodeIdx);
            }
        }

        private static GlbAnimationClipData? SelectDeployClip(List<GlbAnimationClipData> clips)
        {
            GlbAnimationClipData? match = SelectClipByKeyword(clips, "Deploy");
            if (match != null)
            {
                return match;
            }

            for (int i = 0; i < clips.Count; i++)
            {
                if (clips[i].Name.IndexOf("Destroy", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return clips[i];
                }
            }

            return null;
        }

        private static GlbAnimationClipData? SelectClipByKeyword(List<GlbAnimationClipData> clips, string keyword)
        {
            for (int i = 0; i < clips.Count; i++)
            {
                if (clips[i].Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return clips[i];
                }
            }

            return null;
        }

        private static void ClassifySocket(
            Transform tf,
            GlbNodeData nodeData,
            GlbInstantiatedRig rig,
            List<(int index, Transform xform)> vlsCells,
            List<(int index, Transform xform)> wheelAxles,
            ref bool hasExactTurretBase,
            ref bool hasExactTurretHead,
            ref bool hasExactMuzzle)
        {
            string name = tf.name;
            if (string.Equals(name, "Turret_Base", StringComparison.OrdinalIgnoreCase))
            {
                rig.TurretBase = tf;
                hasExactTurretBase = true;
            }
            else if (!hasExactTurretBase && rig.TurretBase == null &&
                     name.IndexOf("Turret_Yaw", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                rig.TurretBase = tf;
            }
            else if (string.Equals(name, "Turret_Head", StringComparison.OrdinalIgnoreCase))
            {
                rig.TurretHead = tf;
                hasExactTurretHead = true;
            }
            else if (!hasExactTurretHead && rig.TurretHead == null &&
                     (name.IndexOf("Weapon_Pitch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                      name.IndexOf("Launcher_Pitch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                      name.IndexOf("Sensor_Pitch", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                rig.TurretHead = tf;
            }
            else if (string.Equals(name, "Weapon_Muzzle", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(name, "Laser_Muzzle", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(name, "Launch_Origin", StringComparison.OrdinalIgnoreCase))
            {
                rig.MuzzleTransform = tf;
                hasExactMuzzle = true;
            }
            else if (!hasExactMuzzle && rig.MuzzleTransform == null && nodeData.MeshIndex < 0 &&
                     (name.EndsWith("_Muzzle", StringComparison.OrdinalIgnoreCase) ||
                      name.EndsWith("_Launch_Origin", StringComparison.OrdinalIgnoreCase) ||
                      name.IndexOf("_Muzzle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                      name.IndexOf("_Launch_Origin", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                rig.MuzzleTransform = tf;
            }
            else if (rig.RadarDish == null &&
                     string.Equals(name, "Radar_Dish", StringComparison.OrdinalIgnoreCase))
            {
                rig.RadarDish = tf;
            }
            else if (rig.WingLeft == null &&
                     string.Equals(name, "Wing_L", StringComparison.OrdinalIgnoreCase))
            {
                rig.WingLeft = tf;
            }
            else if (rig.WingRight == null &&
                     string.Equals(name, "Wing_R", StringComparison.OrdinalIgnoreCase))
            {
                rig.WingRight = tf;
            }
            else if (rig.BoosterStage == null &&
                     name.IndexOf("Booster_Stage", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                rig.BoosterStage = tf;
            }
            else if (name.StartsWith("axle", StringComparison.OrdinalIgnoreCase))
            {
                string suffix = name.Substring("axle".Length);
                if (!int.TryParse(suffix, out int axleIndex))
                {
                    axleIndex = wheelAxles.Count + 1;
                }

                wheelAxles.Add((axleIndex, tf));
            }
            else if (name.StartsWith("VLS_Cell_", StringComparison.OrdinalIgnoreCase))
            {
                string suffix = name.Substring("VLS_Cell_".Length);
                if (!int.TryParse(suffix, out int cellIndex))
                {
                    cellIndex = vlsCells.Count + 1;
                }

                vlsCells.Add((cellIndex, tf));
            }
            else if (name.StartsWith("Tail_Fin_", StringComparison.OrdinalIgnoreCase) ||
                     name.StartsWith("Fin_", StringComparison.OrdinalIgnoreCase))
            {
                rig.FoldingFins.Add(tf);
            }
            else if (name.StartsWith("Fire_Origin", StringComparison.OrdinalIgnoreCase) ||
                     name.StartsWith("Smoke_Origin", StringComparison.OrdinalIgnoreCase))
            {
                rig.FireOrigins.Add(tf);
            }
            else if (name.StartsWith("Debris_", StringComparison.OrdinalIgnoreCase))
            {
                rig.DebrisChunks.Add(tf);
            }
        }

        private static bool IsDecalName(string name)
        {
            return name.IndexOf("Decal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Stencil", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Logo", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TryResolveAccessor(
            int accessorIndex,
            JArray accessors,
            JArray bufferViews,
            byte[] binChunk,
            out int count,
            out int componentType,
            out string type,
            out int baseOffset,
            out int stride)
        {
            count = 0;
            componentType = 0;
            type = string.Empty;
            baseOffset = 0;
            stride = 0;

            if (accessorIndex < 0 || accessorIndex >= accessors.Count || accessors[accessorIndex] is not JObject acc)
            {
                return false;
            }

            int? bvIndex = acc["bufferView"]?.Value<int?>();
            if (!bvIndex.HasValue || bvIndex.Value < 0 || bvIndex.Value >= bufferViews.Count ||
                bufferViews[bvIndex.Value] is not JObject bv)
            {
                return false;
            }

            count = acc["count"]?.Value<int>() ?? 0;
            componentType = acc["componentType"]?.Value<int>() ?? 0;
            type = acc["type"]?.Value<string>() ?? string.Empty;
            int bvOffset = bv["byteOffset"]?.Value<int>() ?? 0;
            int accOffset = acc["byteOffset"]?.Value<int>() ?? 0;
            stride = bv["byteStride"]?.Value<int>() ?? 0;
            if (count <= 0 || count > 2_000_000 || bvOffset < 0 || accOffset < 0 || stride < 0)
            {
                return false;
            }

            long baseOffsetLong = (long)bvOffset + accOffset;
            if (baseOffsetLong < 0 || baseOffsetLong >= binChunk.Length)
            {
                return false;
            }

            baseOffset = (int)baseOffsetLong;
            return true;
        }

        private static float[] ReadFloatScalarArray(int accessorIndex, JArray accessors, JArray bufferViews, byte[] bin)
        {
            if (!TryResolveAccessor(accessorIndex, accessors, bufferViews, bin, out int count, out int compType, out _, out int baseOffset, out int stride) ||
                compType != 5126)
            {
                return Array.Empty<float>();
            }

            int elemStride = stride > 0 ? stride : 4;
            long endByte = (long)baseOffset + ((long)(count - 1) * elemStride) + 4;
            if (elemStride < 4 || endByte < 0 || endByte > bin.Length)
            {
                return Array.Empty<float>();
            }

            var result = new float[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = BitConverter.ToSingle(bin, baseOffset + (i * elemStride));
            }

            return result;
        }

        private static Vector3[] ReadVec3Array(int accessorIndex, JArray accessors, JArray bufferViews, byte[] bin, bool negateX)
        {
            if (!TryResolveAccessor(accessorIndex, accessors, bufferViews, bin, out int count, out int compType, out string type, out int baseOffset, out int stride) ||
                compType != 5126 ||
                (!string.IsNullOrEmpty(type) && !string.Equals(type, "VEC3", StringComparison.Ordinal)))
            {
                return Array.Empty<Vector3>();
            }

            int elemStride = stride > 0 ? stride : 12;
            long endByte = (long)baseOffset + ((long)(count - 1) * elemStride) + 12;
            if (elemStride < 12 || endByte < 0 || endByte > bin.Length)
            {
                return Array.Empty<Vector3>();
            }

            var result = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                int pos = baseOffset + (i * elemStride);
                float x = BitConverter.ToSingle(bin, pos);
                float y = BitConverter.ToSingle(bin, pos + 4);
                float z = BitConverter.ToSingle(bin, pos + 8);
                result[i] = new Vector3(negateX ? -x : x, y, z);
            }

            return result;
        }

        private static Quaternion[] ReadQuatArray(int accessorIndex, JArray accessors, JArray bufferViews, byte[] bin)
        {
            if (!TryResolveAccessor(accessorIndex, accessors, bufferViews, bin, out int count, out int compType, out string type, out int baseOffset, out int stride))
            {
                return Array.Empty<Quaternion>();
            }

            if (!string.IsNullOrEmpty(type) && !string.Equals(type, "VEC4", StringComparison.Ordinal))
            {
                return Array.Empty<Quaternion>();
            }

            int compBytes = compType switch
            {
                5120 => 1,
                5121 => 1,
                5122 => 2,
                5123 => 2,
                5126 => 4,
                _ => 0
            };
            if (compBytes == 0)
            {
                return Array.Empty<Quaternion>();
            }

            int elemBytes = 4 * compBytes;
            int elemStride = stride > 0 ? stride : elemBytes;
            long endByte = (long)baseOffset + ((long)(count - 1) * elemStride) + elemBytes;
            if (elemStride < elemBytes || endByte < 0 || endByte > bin.Length)
            {
                return Array.Empty<Quaternion>();
            }

            var result = new Quaternion[count];
            for (int i = 0; i < count; i++)
            {
                int pos = baseOffset + (i * elemStride);
                float x = ReadQuatComponent(bin, pos, compType);
                float y = ReadQuatComponent(bin, pos + compBytes, compType);
                float z = ReadQuatComponent(bin, pos + (2 * compBytes), compType);
                float w = ReadQuatComponent(bin, pos + (3 * compBytes), compType);
                var q = new Quaternion(x, -y, -z, w);
                float sqr = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
                result[i] = sqr > 0.00001f ? Quaternion.Normalize(q) : Quaternion.identity;
            }

            return result;
        }

        private static float ReadQuatComponent(byte[] bin, int pos, int compType)
        {
            return compType switch
            {
                5120 => Mathf.Max(unchecked((sbyte)bin[pos]) / 127f, -1f),
                5121 => bin[pos] / 255f,
                5122 => Mathf.Max(BitConverter.ToInt16(bin, pos) / 32767f, -1f),
                5123 => BitConverter.ToUInt16(bin, pos) / 65535f,
                5126 => BitConverter.ToSingle(bin, pos),
                _ => 0f
            };
        }

        private static Vector2[] ReadVec2Array(int accessorIndex, JArray accessors, JArray bufferViews, byte[] bin)
        {
            if (!TryResolveAccessor(accessorIndex, accessors, bufferViews, bin, out int count, out int compType, out _, out int baseOffset, out int stride) ||
                compType != 5126)
            {
                return Array.Empty<Vector2>();
            }

            int elemStride = stride > 0 ? stride : 8;
            long endByte = (long)baseOffset + ((long)(count - 1) * elemStride) + 8;
            if (elemStride < 8 || endByte < 0 || endByte > bin.Length)
            {
                return Array.Empty<Vector2>();
            }

            var result = new Vector2[count];
            for (int i = 0; i < count; i++)
            {
                int pos = baseOffset + (i * elemStride);
                float u = BitConverter.ToSingle(bin, pos);
                float v = BitConverter.ToSingle(bin, pos + 4);
                result[i] = new Vector2(u, 1f - v);
            }

            return result;
        }

        private static Color32[] ReadColor32Array(int accessorIndex, JArray accessors, JArray bufferViews, byte[] bin)
        {
            if (!TryResolveAccessor(accessorIndex, accessors, bufferViews, bin, out int count, out int compType, out string type, out int baseOffset, out int stride))
            {
                return Array.Empty<Color32>();
            }

            int components = type == "VEC4" ? 4 : type == "VEC3" ? 3 : 0;
            int compBytes = compType switch { 5121 => 1, 5123 => 2, 5126 => 4, _ => 0 };
            if (components == 0 || compBytes == 0)
            {
                return Array.Empty<Color32>();
            }

            int elemBytes = components * compBytes;
            int elemStride = stride > 0 ? stride : elemBytes;
            long endByte = (long)baseOffset + ((long)(count - 1) * elemStride) + elemBytes;
            if (elemStride < elemBytes || endByte < 0 || endByte > bin.Length)
            {
                return Array.Empty<Color32>();
            }

            var result = new Color32[count];
            for (int i = 0; i < count; i++)
            {
                int pos = baseOffset + (i * elemStride);
                float r = ReadNormalized(bin, pos, compType);
                float g = ReadNormalized(bin, pos + compBytes, compType);
                float b = ReadNormalized(bin, pos + (2 * compBytes), compType);
                float a = components == 4 ? ReadNormalized(bin, pos + (3 * compBytes), compType) : 1f;
                result[i] = new Color(r, g, b, a);
            }

            return result;
        }

        private static float ReadNormalized(byte[] bin, int pos, int compType)
        {
            return compType switch
            {
                5121 => bin[pos] / 255f,
                5123 => BitConverter.ToUInt16(bin, pos) / 65535f,
                5126 => Mathf.Clamp01(BitConverter.ToSingle(bin, pos)),
                _ => 1f
            };
        }

        private static int[] ReadIndices(int accessorIndex, JArray accessors, JArray bufferViews, byte[] bin)
        {
            if (!TryResolveAccessor(accessorIndex, accessors, bufferViews, bin, out int count, out int compType, out _, out int baseOffset, out int stride))
            {
                return Array.Empty<int>();
            }

            int bytesPerIdx = compType switch { 5121 => 1, 5123 => 2, 5125 => 4, _ => 0 };
            if (bytesPerIdx == 0)
            {
                return Array.Empty<int>();
            }

            int elemStride = stride > 0 ? stride : bytesPerIdx;
            long endByte = (long)baseOffset + ((long)(count - 1) * elemStride) + bytesPerIdx;
            if (elemStride < bytesPerIdx || endByte < 0 || endByte > bin.Length)
            {
                return Array.Empty<int>();
            }

            var indices = new int[count];
            for (int i = 0; i < count; i++)
            {
                int pos = baseOffset + (i * elemStride);
                switch (compType)
                {
                    case 5121:
                        indices[i] = bin[pos];
                        break;
                    case 5123:
                        indices[i] = BitConverter.ToUInt16(bin, pos);
                        break;
                    case 5125:
                        uint raw = BitConverter.ToUInt32(bin, pos);
                        indices[i] = raw <= int.MaxValue ? (int)raw : -1;
                        break;
                    default:
                        indices[i] = 0;
                        break;
                }
            }

            return indices;
        }
    }
}
