using System;
using System.Collections.Generic;
using UnityEngine;

namespace MDSFramework.Assets3D
{
    internal enum GlbInterpolationMode
    {
        Linear = 0,
        Step = 1,
        CubicSpline = 2
    }

    internal enum GlbAnimationPath
    {
        Translation = 0,
        Rotation = 1,
        Scale = 2
    }

    internal sealed class GlbMaterialData
    {
        internal string Name = "default";
        internal Color BaseColor = new Color(0.75f, 0.75f, 0.75f, 1f);
        internal float Metallic = 0.1f;
        internal float Smoothness = 0.35f;
        internal float MetallicFactor = 1f;
        internal float RoughnessFactor = 1f;
        internal bool WrapRepeat = true;
        internal string AlphaMode = "OPAQUE";
        internal float AlphaCutoff = 0.5f;
        internal bool DoubleSided;
        internal bool HasEmissive;
        internal Color EmissiveColor = Color.black;
        internal byte[]? BaseColorTextureBytes;
        internal byte[]? NormalTextureBytes;
        internal float NormalScale = 1f;
        internal byte[]? MetallicRoughnessTextureBytes;
        internal byte[]? OcclusionTextureBytes;
        internal float OcclusionStrength = 1f;
        internal byte[]? EmissiveTextureBytes;
        internal string? ExternalTexturePath;
    }

    internal sealed class GlbPrimitiveData
    {
        internal string Name = string.Empty;
        internal Vector3[] Vertices = Array.Empty<Vector3>();
        internal Vector3[] Normals = Array.Empty<Vector3>();
        internal Vector2[] Uvs = Array.Empty<Vector2>();
        internal Color32[] Colors = Array.Empty<Color32>();
        internal int[] Triangles = Array.Empty<int>();
        internal int MaterialIndex = -1;
    }

    internal sealed class GlbMeshData
    {
        internal string Name = string.Empty;
        internal readonly List<GlbPrimitiveData> Primitives = new List<GlbPrimitiveData>();
    }

    internal sealed class GlbNodeData
    {
        internal int Index;
        internal string Name = string.Empty;
        internal Vector3 LocalPosition = Vector3.zero;
        internal Quaternion LocalRotation = Quaternion.identity;
        internal Vector3 LocalScale = Vector3.one;
        internal int MeshIndex = -1;
        internal readonly List<int> Children = new List<int>();
    }

    internal sealed class GlbAnimationChannelData
    {
        internal int NodeIndex;
        internal GlbAnimationPath Path;
        internal GlbInterpolationMode Interpolation = GlbInterpolationMode.Linear;
        internal float[] Times = Array.Empty<float>();
        internal Vector3[] Vec3Values = Array.Empty<Vector3>();
        internal Quaternion[] QuatValues = Array.Empty<Quaternion>();
    }

    internal sealed class GlbAnimationClipData
    {
        internal string Name = "Deploy";
        internal float Duration;
        internal readonly List<GlbAnimationChannelData> Channels = new List<GlbAnimationChannelData>();
    }

    internal sealed class GlbParsedModel
    {
        internal string SourceKey = string.Empty;
        internal string FileName = string.Empty;
        internal readonly List<GlbNodeData> Nodes = new List<GlbNodeData>();
        internal readonly List<int> RootNodeIndices = new List<int>();
        internal readonly List<GlbMeshData> Meshes = new List<GlbMeshData>();
        internal readonly List<GlbMaterialData> Materials = new List<GlbMaterialData>();
        internal readonly List<GlbAnimationClipData> Animations = new List<GlbAnimationClipData>();
        internal Bounds LocalBounds;
    }

    internal sealed class GlbCompiledPrimitive
    {
        internal Mesh Mesh = null!;
        internal Material Material = null!;
    }

    internal sealed class GlbCompiledTemplate
    {
        internal GlbParsedModel Parsed = null!;
        internal readonly List<List<GlbCompiledPrimitive>> CompiledMeshes = new List<List<GlbCompiledPrimitive>>();
        internal readonly List<Material> OwnedMaterials = new List<Material>();
        internal readonly List<Texture2D> OwnedTextures = new List<Texture2D>();
    }

    internal sealed class GlbInstantiatedRig
    {
        internal GameObject RootObject = null!;
        internal Transform[] NodeTransforms = Array.Empty<Transform>();
        internal GlbAnimationClipData? DeployClip;
        internal GlbAnimationClipData? DestroyClip;
        internal Transform? TurretBase;
        internal Transform? TurretHead;
        internal Transform? MuzzleTransform;
        internal Transform? RadarDish;
        internal Transform? WingLeft;
        internal Transform? WingRight;
        internal Transform? BoosterStage;
        internal readonly List<Transform> WheelAxles = new List<Transform>();
        internal readonly List<Transform> VlsCells = new List<Transform>();
        internal readonly List<Transform> FoldingFins = new List<Transform>();
        internal readonly List<Transform> FireOrigins = new List<Transform>();
        internal readonly List<Transform> DebrisChunks = new List<Transform>();
        internal readonly List<Renderer> DecalRenderers = new List<Renderer>();
        internal Bounds Bounds;
    }
}
