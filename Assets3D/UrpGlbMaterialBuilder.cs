using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace MDSFramework.Assets3D
{
    internal static class UrpGlbMaterialBuilder
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int BumpMapId = Shader.PropertyToID("_BumpMap");
        private static readonly int BumpScaleId = Shader.PropertyToID("_BumpScale");
        private static readonly int MetallicGlossMapId = Shader.PropertyToID("_MetallicGlossMap");
        private static readonly int SmoothnessTextureChannelId = Shader.PropertyToID("_SmoothnessTextureChannel");
        private static readonly int OcclusionMapId = Shader.PropertyToID("_OcclusionMap");
        private static readonly int OcclusionStrengthId = Shader.PropertyToID("_OcclusionStrength");
        private static readonly int MetallicId = Shader.PropertyToID("_Metallic");
        private static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
        private static readonly int EmissionMapId = Shader.PropertyToID("_EmissionMap");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        private static readonly int SurfaceId = Shader.PropertyToID("_Surface");
        private static readonly int BlendId = Shader.PropertyToID("_Blend");
        private static readonly int SrcBlendId = Shader.PropertyToID("_SrcBlend");
        private static readonly int DstBlendId = Shader.PropertyToID("_DstBlend");
        private static readonly int ZWriteId = Shader.PropertyToID("_ZWrite");
        private static readonly int AlphaClipId = Shader.PropertyToID("_AlphaClip");
        private static readonly int CutoffId = Shader.PropertyToID("_Cutoff");
        private static readonly int CullId = Shader.PropertyToID("_Cull");

        private static readonly Dictionary<string, Texture2D> ExternalTextureCache =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

        private static Material? encyclopediaTemplate;

        internal static Shader? ResolveLitShader()
        {
            return Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Universal Render Pipeline/Simple Lit")
                ?? Shader.Find("Standard");
        }

        internal static Shader? ResolveUnlitShader()
        {
            return Shader.Find("Universal Render Pipeline/Unlit")
                ?? Shader.Find("Sprites/Default")
                ?? ResolveLitShader();
        }

        internal static Texture2D? LoadExternalPng(string? fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            {
                return null;
            }

            if (ExternalTextureCache.TryGetValue(fullPath!, out Texture2D? cached) && cached != null)
            {
                return cached;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(fullPath!);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true)
                {
                    name = Path.GetFileNameWithoutExtension(fullPath),
                    hideFlags = HideFlags.DontUnloadUnusedAsset,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
                if (!tex.LoadImage(bytes, markNonReadable: true))
                {
                    UnityEngine.Object.Destroy(tex);
                    return null;
                }

                ExternalTextureCache[fullPath!] = tex;
                return tex;
            }
            catch (Exception ex)
            {
                MdsLog.Warn($"Failed to load decal PNG '{fullPath}': {ex.Message}");
                return null;
            }
        }

        internal static Material CreateMaterial(
            GlbMaterialData data,
            string modelKey,
            List<Texture2D> ownedTextures)
        {
            Shader? shader = ResolveLitShader();
            Material mat;
            if (shader != null)
            {
                mat = new Material(shader);
            }
            else
            {
                Material? tpl = FindEncyclopediaTemplate();
                if (tpl != null)
                {
                    mat = new Material(tpl);
                }
                else
                {
                    Shader? fallback = Shader.Find("Hidden/Internal-Colored")
                        ?? Shader.Find("Sprites/Default")
                        ?? Shader.Find("Standard")
                        ?? Shader.Find("Hidden/InternalErrorShader");
                    if (fallback == null)
                    {
                        throw new InvalidOperationException("No fallback Unity shader found for GLB material.");
                    }

                    mat = new Material(fallback);
                }
            }

            mat.name = $"{modelKey}_{data.Name}";
            mat.hideFlags = HideFlags.DontUnloadUnusedAsset;

            TextureWrapMode wrapMode = (data.WrapRepeat &&
                !string.Equals(data.AlphaMode, "BLEND", StringComparison.OrdinalIgnoreCase))
                ? TextureWrapMode.Repeat
                : TextureWrapMode.Clamp;
            FilterMode pbrFilter = wrapMode == TextureWrapMode.Repeat ? FilterMode.Trilinear : FilterMode.Bilinear;
            int pbrAniso = wrapMode == TextureWrapMode.Repeat ? 4 : 1;

            Texture2D? tex = null;
            if (data.BaseColorTextureBytes != null && data.BaseColorTextureBytes.Length > 0)
            {
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true)
                {
                    name = $"{modelKey}_{data.Name}_tex",
                    hideFlags = HideFlags.DontUnloadUnusedAsset,
                    wrapMode = wrapMode,
                    filterMode = pbrFilter,
                    anisoLevel = pbrAniso
                };
                if (tex.LoadImage(data.BaseColorTextureBytes, markNonReadable: true))
                {
                    ownedTextures.Add(tex);
                }
                else
                {
                    UnityEngine.Object.Destroy(tex);
                    tex = null;
                }
            }
            else if (!string.IsNullOrWhiteSpace(data.ExternalTexturePath))
            {
                tex = LoadExternalPng(data.ExternalTexturePath);
            }

            if (mat.HasProperty(BaseColorId))
            {
                mat.SetColor(BaseColorId, data.BaseColor);
            }

            if (mat.HasProperty(ColorId))
            {
                mat.SetColor(ColorId, data.BaseColor);
            }

            mat.color = data.BaseColor;

            if (tex != null)
            {
                if (mat.HasProperty(BaseMapId))
                {
                    mat.SetTexture(BaseMapId, tex);
                }

                if (mat.HasProperty(MainTexId))
                {
                    mat.SetTexture(MainTexId, tex);
                }

                mat.mainTexture = tex;
            }

            if (data.NormalTextureBytes != null && data.NormalTextureBytes.Length > 0 && mat.HasProperty(BumpMapId))
            {
                Texture2D? normalTex = LoadAndConvertLinearRgba32(
                    data.NormalTextureBytes,
                    $"{modelKey}_{data.Name}_nrm",
                    wrapMode,
                    pixels =>
                    {
                        for (int i = 0; i < pixels.Length; i++)
                        {
                            Color32 p = pixels[i];
                            pixels[i] = new Color32(255, p.g, 255, p.r);
                        }
                    },
                    ownedTextures);
                if (normalTex != null)
                {
                    mat.SetTexture(BumpMapId, normalTex);
                    if (mat.HasProperty(BumpScaleId))
                    {
                        mat.SetFloat(BumpScaleId, data.NormalScale);
                    }

                    mat.EnableKeyword("_NORMALMAP");
                }
            }

            bool hasMrTex = false;
            if (data.MetallicRoughnessTextureBytes != null && data.MetallicRoughnessTextureBytes.Length > 0 && mat.HasProperty(MetallicGlossMapId))
            {
                float mFactor = Mathf.Clamp01(data.MetallicFactor);
                float rFactor = Mathf.Clamp01(data.RoughnessFactor);
                Texture2D? mrTex = LoadAndConvertLinearRgba32(
                    data.MetallicRoughnessTextureBytes,
                    $"{modelKey}_{data.Name}_mr",
                    wrapMode,
                    pixels =>
                    {
                        for (int i = 0; i < pixels.Length; i++)
                        {
                            Color32 p = pixels[i];
                            byte m = (byte)Mathf.Clamp(Mathf.RoundToInt((p.b / 255f) * mFactor * 255f), 0, 255);
                            byte s = (byte)Mathf.Clamp(Mathf.RoundToInt((1f - ((p.g / 255f) * rFactor)) * 255f), 0, 255);
                            pixels[i] = new Color32(m, 0, 0, s);
                        }
                    },
                    ownedTextures);
                if (mrTex != null)
                {
                    hasMrTex = true;
                    mat.SetTexture(MetallicGlossMapId, mrTex);
                    if (mat.HasProperty(MetallicId))
                    {
                        mat.SetFloat(MetallicId, 1f);
                    }

                    if (mat.HasProperty(SmoothnessId))
                    {
                        mat.SetFloat(SmoothnessId, 1f);
                    }

                    if (mat.HasProperty(SmoothnessTextureChannelId))
                    {
                        mat.SetFloat(SmoothnessTextureChannelId, 0f);
                    }

                    mat.DisableKeyword("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A");
                    mat.EnableKeyword("_METALLICSPECGLOSSMAP");
                }
            }

            if (!hasMrTex)
            {
                if (mat.HasProperty(MetallicId))
                {
                    mat.SetFloat(MetallicId, Mathf.Clamp01(data.Metallic));
                }

                if (mat.HasProperty(SmoothnessId))
                {
                    mat.SetFloat(SmoothnessId, Mathf.Clamp01(data.Smoothness));
                }
            }

            if (data.OcclusionTextureBytes != null && data.OcclusionTextureBytes.Length > 0 && mat.HasProperty(OcclusionMapId))
            {
                Texture2D? occTex = LoadAndConvertLinearRgba32(
                    data.OcclusionTextureBytes,
                    $"{modelKey}_{data.Name}_occ",
                    wrapMode,
                    pixels =>
                    {
                        for (int i = 0; i < pixels.Length; i++)
                        {
                            byte ao = pixels[i].r;
                            pixels[i] = new Color32(ao, ao, ao, 255);
                        }
                    },
                    ownedTextures);
                if (occTex != null)
                {
                    mat.SetTexture(OcclusionMapId, occTex);
                    if (mat.HasProperty(OcclusionStrengthId))
                    {
                        mat.SetFloat(OcclusionStrengthId, Mathf.Clamp01(data.OcclusionStrength));
                    }

                    mat.EnableKeyword("_OCCLUSIONMAP");
                }
            }

            if (data.EmissiveTextureBytes != null && data.EmissiveTextureBytes.Length > 0 && mat.HasProperty(EmissionMapId))
            {
                var emTex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true)
                {
                    name = $"{modelKey}_{data.Name}_em",
                    hideFlags = HideFlags.DontUnloadUnusedAsset,
                    wrapMode = wrapMode,
                    filterMode = pbrFilter,
                    anisoLevel = pbrAniso
                };
                if (emTex.LoadImage(data.EmissiveTextureBytes, markNonReadable: true))
                {
                    ownedTextures.Add(emTex);
                    mat.SetTexture(EmissionMapId, emTex);
                    mat.EnableKeyword("_EMISSION");
                    if (mat.HasProperty(EmissionColorId))
                    {
                        mat.SetColor(EmissionColorId, data.HasEmissive ? data.EmissiveColor : Color.white);
                    }

                    mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                }
                else
                {
                    UnityEngine.Object.Destroy(emTex);
                }
            }
            else if (data.HasEmissive && mat.HasProperty(EmissionColorId))
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetColor(EmissionColorId, data.EmissiveColor);
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }

            if (data.DoubleSided && mat.HasProperty(CullId))
            {
                mat.SetInt(CullId, (int)CullMode.Off);
            }

            ConfigureAlphaMode(mat, data);
            return mat;
        }

        internal static Material CreateBeamMaterial(Color color)
        {
            Shader? shader = ResolveUnlitShader()
                ?? Shader.Find("Hidden/Internal-Colored")
                ?? Shader.Find("Hidden/InternalErrorShader");
            if (shader == null)
            {
                throw new InvalidOperationException("No usable Unity shader found for beam material.");
            }

            var mat = new Material(shader);
            mat.name = "MDS_HelBeam_Mat";
            mat.hideFlags = HideFlags.DontUnloadUnusedAsset;

            if (mat.HasProperty(BaseColorId))
            {
                mat.SetColor(BaseColorId, color);
            }

            if (mat.HasProperty(ColorId))
            {
                mat.SetColor(ColorId, color);
            }

            mat.color = color;

            if (mat.HasProperty(EmissionColorId))
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetColor(EmissionColorId, color * 2.2f);
            }

            if (mat.HasProperty(SurfaceId))
            {
                mat.SetFloat(SurfaceId, 1f);
                mat.SetFloat(BlendId, 1f); // Additive glow
                mat.SetInt(SrcBlendId, (int)BlendMode.SrcAlpha);
                mat.SetInt(DstBlendId, (int)BlendMode.One);
                mat.SetInt(ZWriteId, 0);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.renderQueue = (int)RenderQueue.Transparent + 20;
            }

            return mat;
        }

        internal static void ClearExternalTextureCache()
        {
            foreach (Texture2D tex in ExternalTextureCache.Values)
            {
                if (tex != null)
                {
                    UnityEngine.Object.Destroy(tex);
                }
            }

            ExternalTextureCache.Clear();
            encyclopediaTemplate = null;
        }

        private static Texture2D? LoadAndConvertLinearRgba32(
            byte[] pngBytes,
            string texName,
            TextureWrapMode wrapMode,
            Action<Color32[]> transformPixels,
            List<Texture2D> ownedTextures)
        {
            var src = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false, linear: true);
            if (!src.LoadImage(pngBytes, markNonReadable: false))
            {
                UnityEngine.Object.Destroy(src);
                return null;
            }

            Color32[] pixels = src.GetPixels32();
            int width = src.width;
            int height = src.height;
            UnityEngine.Object.Destroy(src);

            transformPixels(pixels);

            var dst = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: true, linear: true)
            {
                name = texName,
                hideFlags = HideFlags.DontUnloadUnusedAsset,
                wrapMode = wrapMode,
                filterMode = wrapMode == TextureWrapMode.Repeat ? FilterMode.Trilinear : FilterMode.Bilinear,
                anisoLevel = wrapMode == TextureWrapMode.Repeat ? 4 : 1
            };
            dst.SetPixels32(pixels);
            dst.Apply(updateMipmaps: true, makeNoLongerReadable: true);
            ownedTextures.Add(dst);
            return dst;
        }

        private static void ConfigureAlphaMode(Material mat, GlbMaterialData data)
        {
            if (string.Equals(data.AlphaMode, "BLEND", StringComparison.OrdinalIgnoreCase))
            {
                if (mat.HasProperty(SurfaceId))
                {
                    mat.SetFloat(SurfaceId, 1f);
                }

                if (mat.HasProperty(BlendId))
                {
                    mat.SetFloat(BlendId, 0f);
                }

                if (mat.HasProperty(SrcBlendId))
                {
                    mat.SetInt(SrcBlendId, (int)BlendMode.SrcAlpha);
                }

                if (mat.HasProperty(DstBlendId))
                {
                    mat.SetInt(DstBlendId, (int)BlendMode.OneMinusSrcAlpha);
                }

                if (mat.HasProperty(ZWriteId))
                {
                    mat.SetInt(ZWriteId, 0);
                }

                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.renderQueue = (int)RenderQueue.Transparent + 10;
            }
            else if (string.Equals(data.AlphaMode, "MASK", StringComparison.OrdinalIgnoreCase))
            {
                if (mat.HasProperty(SurfaceId))
                {
                    mat.SetFloat(SurfaceId, 0f);
                }

                if (mat.HasProperty(AlphaClipId))
                {
                    mat.SetFloat(AlphaClipId, 1f);
                }

                if (mat.HasProperty(CutoffId))
                {
                    mat.SetFloat(CutoffId, Mathf.Clamp01(data.AlphaCutoff));
                }

                mat.EnableKeyword("_ALPHATEST_ON");
                mat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.SetOverrideTag("RenderType", "TransparentCutout");
                mat.renderQueue = (int)RenderQueue.AlphaTest;
            }
        }

        private static Material? FindEncyclopediaTemplate()
        {
            if (encyclopediaTemplate != null)
            {
                return encyclopediaTemplate;
            }

            Encyclopedia? enc = Encyclopedia.i;
            if (enc == null || enc.buildings == null)
            {
                return null;
            }

            for (int i = 0; i < enc.buildings.Count; i++)
            {
                BuildingDefinition? def = enc.buildings[i];
                if (def == null || def.unitPrefab == null)
                {
                    continue;
                }

                Renderer? renderer = def.unitPrefab.GetComponentInChildren<Renderer>(true);
                if (renderer != null && renderer.sharedMaterial != null)
                {
                    encyclopediaTemplate = renderer.sharedMaterial;
                    return encyclopediaTemplate;
                }
            }

            return null;
        }
    }
}
