using System;
using System.Collections.Generic;
using UnityEngine;

namespace MDSFramework.Assets3D
{
    internal static class ProceduralFallbackMeshBuilder
    {
        internal static GlbParsedModel Build(string key, string archetype, string? decalPngPath)
        {
            if (string.Equals(archetype, "eccm_hunter", StringComparison.OrdinalIgnoreCase))
            {
                return BuildEccmHunterMissile(key, decalPngPath);
            }

            if (string.Equals(archetype, "icbm_hunter", StringComparison.OrdinalIgnoreCase))
            {
                return BuildIcbmInterceptorMissile(key, decalPngPath);
            }

            if (string.Equals(archetype, "container", StringComparison.OrdinalIgnoreCase))
            {
                return BuildStealthIsoContainer(key, decalPngPath);
            }

            return BuildDefaultMissileOrBox(key, decalPngPath);
        }

        private static GlbParsedModel BuildStealthIsoContainer(string key, string? decalPngPath)
        {
            var model = new GlbParsedModel
            {
                SourceKey = key,
                FileName = $"procedural_{key}_container",
                LocalBounds = new Bounds(new Vector3(0f, 1.3f, 0f), new Vector3(2.438f, 2.591f, 6.058f))
            };

            model.Materials.Add(new GlbMaterialData
            {
                Name = "Olive_Main",
                BaseColor = new Color(0.22f, 0.25f, 0.20f, 1f),
                Metallic = 0.25f,
                Smoothness = 0.35f
            });
            model.Materials.Add(new GlbMaterialData
            {
                Name = "Dark_Metal",
                BaseColor = new Color(0.11f, 0.12f, 0.13f, 1f),
                Metallic = 0.55f,
                Smoothness = 0.45f
            });
            model.Materials.Add(new GlbMaterialData
            {
                Name = "Optics_Cyan",
                BaseColor = new Color(0.05f, 0.72f, 0.85f, 1f),
                HasEmissive = true,
                EmissiveColor = new Color(0.04f, 0.65f, 0.80f, 1f)
            });
            model.Materials.Add(new GlbMaterialData
            {
                Name = "Container_Decal",
                BaseColor = Color.white,
                AlphaMode = "BLEND",
                DoubleSided = false,
                ExternalTexturePath = decalPngPath
            });

            // Mesh 0: Base chassis (2.438m x 0.45m x 6.058m)
            model.Meshes.Add(CreateBoxMesh("Chassis", new Vector3(2.438f, 0.45f, 6.058f), new Vector3(0f, 0.225f, 0f), 0));
            // Mesh 1: Retracting Side Wall Left/Right
            model.Meshes.Add(CreateBoxMesh("Wall_L", new Vector3(0.12f, 2.10f, 5.95f), new Vector3(0f, 1.05f, 0f), 0));
            model.Meshes.Add(CreateBoxMesh("Wall_R", new Vector3(0.12f, 2.10f, 5.95f), new Vector3(0f, 1.05f, 0f), 0));
            // Mesh 3: Retracting Roof
            model.Meshes.Add(CreateBoxMesh("Roof_Panel", new Vector3(2.40f, 0.14f, 6.00f), new Vector3(0f, 0.07f, 0f), 1));
            // Mesh 4: Turret Base
            model.Meshes.Add(CreateBoxMesh("Turret_Base", new Vector3(1.15f, 0.55f, 1.35f), new Vector3(0f, 0.275f, 0f), 1));
            // Mesh 5: Turret Head / Barrel + Optics
            var headMesh = CreateBoxMesh("Turret_Head", new Vector3(0.65f, 0.45f, 1.55f), new Vector3(0f, 0f, 0.45f), 0);
            headMesh.Primitives.Add(CreateBoxPrimitive("Optics", new Vector3(0.35f, 0.20f, 0.18f), new Vector3(0f, 0.08f, 1.20f), 2));
            model.Meshes.Add(headMesh);
            // Mesh 6: Side Decal
            model.Meshes.Add(CreateDecalQuadMesh("Vanguard_Stencil", 2.1f, 0.55f, 3));

            // Nodes
            int nRoot = AddNode(model, "Container_Body", -1, Vector3.zero, Quaternion.identity);
            int nChassis = AddNode(model, "Chassis", 0, Vector3.zero, Quaternion.identity, nRoot);
            int nWallL = AddNode(model, "Wall_L", 1, new Vector3(-1.15f, 0.45f, 0f), Quaternion.identity, nRoot);
            int nWallR = AddNode(model, "Wall_R", 2, new Vector3(1.15f, 0.45f, 0f), Quaternion.identity, nRoot);
            int nRoof = AddNode(model, "Roof_Carrier", 3, new Vector3(0f, 2.45f, 0f), Quaternion.identity, nRoot);
            int nTurretBase = AddNode(model, "Turret_Base", 4, new Vector3(0f, 0.45f, 0f), Quaternion.identity, nRoot);
            int nTurretHead = AddNode(model, "Turret_Head", 5, new Vector3(0f, 0.55f, 0f), Quaternion.identity, nTurretBase);
            AddNode(model, "Weapon_Muzzle", -1, new Vector3(0f, 0f, 1.35f), Quaternion.identity, nTurretHead);

            if (!string.IsNullOrEmpty(decalPngPath))
            {
                AddNode(model, "Stencil_L", 6, new Vector3(-0.065f, 1.05f, 0f), Quaternion.Euler(0f, -90f, 0f), nWallL);
                AddNode(model, "Stencil_R", 6, new Vector3(0.065f, 1.05f, 0f), Quaternion.Euler(0f, 90f, 0f), nWallR);
            }

            _ = nChassis;
            model.RootNodeIndices.Add(nRoot);

            var deploy = new GlbAnimationClipData { Name = "Deploy", Duration = 5.0f };
            deploy.Channels.Add(MakeVec3Channel(nWallL, GlbAnimationPath.Translation, new[] { 0f, 5f },
                new[] { new Vector3(-1.15f, 0.45f, 0f), new Vector3(-1.15f, 0.05f, 0f) }));
            deploy.Channels.Add(MakeScaleChannel(nWallL, new[] { 0f, 5f },
                new[] { Vector3.one, new Vector3(1f, 0.18f, 1f) }));
            deploy.Channels.Add(MakeVec3Channel(nWallR, GlbAnimationPath.Translation, new[] { 0f, 5f },
                new[] { new Vector3(1.15f, 0.45f, 0f), new Vector3(1.15f, 0.05f, 0f) }));
            deploy.Channels.Add(MakeScaleChannel(nWallR, new[] { 0f, 5f },
                new[] { Vector3.one, new Vector3(1f, 0.18f, 1f) }));
            deploy.Channels.Add(MakeVec3Channel(nRoof, GlbAnimationPath.Translation, new[] { 0f, 5f },
                new[] { new Vector3(0f, 2.45f, 0f), new Vector3(0f, 2.45f, -5.85f) }));
            deploy.Channels.Add(MakeVec3Channel(nTurretBase, GlbAnimationPath.Translation, new[] { 0f, 5f },
                new[] { new Vector3(0f, 0.25f, 0f), new Vector3(0f, 0.65f, 0f) }));
            model.Animations.Add(deploy);

            return model;
        }

        private static GlbParsedModel BuildEccmHunterMissile(string key, string? decalPngPath)
        {
            var model = new GlbParsedModel
            {
                SourceKey = key,
                FileName = $"procedural_{key}_eccm_hunter",
                LocalBounds = new Bounds(Vector3.zero, new Vector3(1.6f, 0.42f, 4.6f))
            };

            model.Materials.Add(new GlbMaterialData
            {
                Name = "Stealth_Black",
                BaseColor = new Color(0.08f, 0.09f, 0.10f, 1f),
                Metallic = 0.22f,
                Smoothness = 0.25f
            });
            model.Materials.Add(new GlbMaterialData
            {
                Name = "Radome_Graphite",
                BaseColor = new Color(0.18f, 0.19f, 0.21f, 1f),
                Metallic = 0.35f,
                Smoothness = 0.4f
            });
            model.Materials.Add(new GlbMaterialData
            {
                Name = "HOJ_Cyan",
                BaseColor = new Color(0.04f, 0.68f, 0.85f, 1f),
                HasEmissive = true,
                EmissiveColor = new Color(0.04f, 0.68f, 0.85f, 1f)
            });
            model.Materials.Add(new GlbMaterialData
            {
                Name = "ECCM_Side_Decal",
                BaseColor = Color.white,
                AlphaMode = "BLEND",
                ExternalTexturePath = decalPngPath
            });

            // Nose is -Z before 180 deg ModelYawOffsetDeg (matches blender GLB orientation!)
            model.Meshes.Add(CreateBoxMesh("Body", new Vector3(0.38f, 0.34f, 3.50f), new Vector3(0f, 0f, 0.35f), 0));
            model.Meshes.Add(CreateBoxMesh("Radome", new Vector3(0.24f, 0.22f, 0.88f), new Vector3(0f, 0f, -1.84f), 1));
            model.Meshes.Add(CreateBoxMesh("HOJ_Sensors", new Vector3(0.40f, 0.05f, 0.22f), new Vector3(0f, 0.07f, -1.15f), 2));
            model.Meshes.Add(CreateBoxMesh("Wing_L", new Vector3(0.75f, 0.025f, 0.48f), new Vector3(-0.38f, 0f, 0.20f), 0));
            model.Meshes.Add(CreateBoxMesh("Wing_R", new Vector3(0.75f, 0.025f, 0.48f), new Vector3(0.38f, 0f, 0.20f), 0));
            model.Meshes.Add(CreateBoxMesh("Tail_Fin", new Vector3(0.22f, 0.02f, 0.32f), new Vector3(0.11f, 0f, 0f), 0));
            model.Meshes.Add(CreateDecalQuadMesh("SideDecal", 0.88f, 0.22f, 3));

            int nRoot = AddNode(model, "Missile_Root", -1, Vector3.zero, Quaternion.identity);
            AddNode(model, "Body", 0, Vector3.zero, Quaternion.identity, nRoot);
            AddNode(model, "Radome", 1, Vector3.zero, Quaternion.identity, nRoot);
            AddNode(model, "HOJ_Cyan", 2, Vector3.zero, Quaternion.identity, nRoot);

            Quaternion wingFoldL = Quaternion.Euler(0f, 68f, 0f);
            Quaternion wingFoldR = Quaternion.Euler(0f, -68f, 0f);
            int nWingL = AddNode(model, "Wing_L", 3, new Vector3(-0.12f, -0.18f, -0.15f), wingFoldL, nRoot);
            int nWingR = AddNode(model, "Wing_R", 4, new Vector3(0.12f, -0.18f, -0.15f), wingFoldR, nRoot);

            int nFin1 = AddNode(model, "Tail_Fin_1", 5, new Vector3(0.16f, 0.16f, 1.95f), Quaternion.Euler(0f, 0f, 45f), nRoot);
            int nFin2 = AddNode(model, "Tail_Fin_2", 5, new Vector3(-0.16f, 0.16f, 1.95f), Quaternion.Euler(0f, 0f, 135f), nRoot);
            int nFin3 = AddNode(model, "Tail_Fin_3", 5, new Vector3(-0.16f, -0.16f, 1.95f), Quaternion.Euler(0f, 0f, -135f), nRoot);
            int nFin4 = AddNode(model, "Tail_Fin_4", 5, new Vector3(0.16f, -0.16f, 1.95f), Quaternion.Euler(0f, 0f, -45f), nRoot);

            if (!string.IsNullOrEmpty(decalPngPath))
            {
                AddNode(model, "SideDecal_L", 6, new Vector3(-0.195f, 0f, -0.20f), Quaternion.Euler(0f, -90f, 0f), nRoot);
                AddNode(model, "SideDecal_R", 6, new Vector3(0.195f, 0f, -0.20f), Quaternion.Euler(0f, 90f, 0f), nRoot);
            }

            model.RootNodeIndices.Add(nRoot);

            var deploy = new GlbAnimationClipData { Name = "Deploy", Duration = 0.75f };
            deploy.Channels.Add(MakeRotChannel(nWingL, new[] { 0f, 0.75f }, new[] { wingFoldL, Quaternion.identity }));
            deploy.Channels.Add(MakeRotChannel(nWingR, new[] { 0f, 0.75f }, new[] { wingFoldR, Quaternion.identity }));
            deploy.Channels.Add(MakeScaleChannel(nFin1, new[] { 0f, 0.75f }, new[] { new Vector3(0.15f, 1f, 1f), Vector3.one }));
            deploy.Channels.Add(MakeScaleChannel(nFin2, new[] { 0f, 0.75f }, new[] { new Vector3(0.15f, 1f, 1f), Vector3.one }));
            deploy.Channels.Add(MakeScaleChannel(nFin3, new[] { 0f, 0.75f }, new[] { new Vector3(0.15f, 1f, 1f), Vector3.one }));
            deploy.Channels.Add(MakeScaleChannel(nFin4, new[] { 0f, 0.75f }, new[] { new Vector3(0.15f, 1f, 1f), Vector3.one }));
            model.Animations.Add(deploy);

            return model;
        }

        private static GlbParsedModel BuildIcbmInterceptorMissile(string key, string? decalPngPath)
        {
            var model = new GlbParsedModel
            {
                SourceKey = key,
                FileName = $"procedural_{key}_icbm_hunter",
                LocalBounds = new Bounds(Vector3.zero, new Vector3(0.52f, 0.52f, 5.5f))
            };

            model.Materials.Add(new GlbMaterialData
            {
                Name = "Interceptor_White",
                BaseColor = new Color(0.84f, 0.86f, 0.88f, 1f),
                Metallic = 0.15f,
                Smoothness = 0.45f
            });
            model.Materials.Add(new GlbMaterialData
            {
                Name = "KKV_Graphite",
                BaseColor = new Color(0.14f, 0.15f, 0.17f, 1f),
                Metallic = 0.5f,
                Smoothness = 0.5f
            });
            model.Materials.Add(new GlbMaterialData
            {
                Name = "Seeker_Cyan",
                BaseColor = new Color(0.05f, 0.75f, 0.90f, 1f),
                HasEmissive = true,
                EmissiveColor = new Color(0.05f, 0.75f, 0.90f, 1f)
            });
            model.Materials.Add(new GlbMaterialData
            {
                Name = "VLogo_Decal",
                BaseColor = Color.white,
                AlphaMode = "BLEND",
                ExternalTexturePath = decalPngPath
            });

            model.Meshes.Add(CreateBoxMesh("Booster_Body", new Vector3(0.45f, 0.45f, 4.10f), new Vector3(0f, 0f, 0.37f), 0));
            model.Meshes.Add(CreateBoxMesh("KKV_Nose", new Vector3(0.36f, 0.36f, 1.05f), new Vector3(0f, 0f, -2.20f), 1));
            model.Meshes.Add(CreateBoxMesh("Seeker_Window", new Vector3(0.32f, 0.06f, 0.16f), new Vector3(0f, 0f, -2.10f), 2));
            model.Meshes.Add(CreateBoxMesh("Fin", new Vector3(0.26f, 0.02f, 0.40f), new Vector3(0.13f, 0f, 0f), 1));
            model.Meshes.Add(CreateDecalQuadMesh("VLogo", 0.30f, 0.28f, 3));

            int nRoot = AddNode(model, "Missile_Root", -1, Vector3.zero, Quaternion.identity);
            AddNode(model, "Booster_Body", 0, Vector3.zero, Quaternion.identity, nRoot);
            AddNode(model, "KKV_Nose", 1, Vector3.zero, Quaternion.identity, nRoot);
            AddNode(model, "Seeker_Cyan", 2, Vector3.zero, Quaternion.identity, nRoot);

            int f1 = AddNode(model, "Fin_1", 3, new Vector3(0.22f, 0f, 2.25f), Quaternion.Euler(0f, 0f, 0f), nRoot);
            int f2 = AddNode(model, "Fin_2", 3, new Vector3(0f, 0.22f, 2.25f), Quaternion.Euler(0f, 0f, 90f), nRoot);
            int f3 = AddNode(model, "Fin_3", 3, new Vector3(-0.22f, 0f, 2.25f), Quaternion.Euler(0f, 0f, 180f), nRoot);
            int f4 = AddNode(model, "Fin_4", 3, new Vector3(0f, -0.22f, 2.25f), Quaternion.Euler(0f, 0f, -90f), nRoot);

            if (!string.IsNullOrEmpty(decalPngPath))
            {
                AddNode(model, "VLogo_L", 4, new Vector3(-0.23f, 0f, 0.80f), Quaternion.Euler(0f, -90f, 0f), nRoot);
                AddNode(model, "VLogo_R", 4, new Vector3(0.23f, 0f, 0.80f), Quaternion.Euler(0f, 90f, 0f), nRoot);
            }

            model.RootNodeIndices.Add(nRoot);

            var deploy = new GlbAnimationClipData { Name = "Deploy", Duration = 0.75f };
            deploy.Channels.Add(MakeScaleChannel(f1, new[] { 0f, 0.75f }, new[] { new Vector3(0.1f, 1f, 1f), Vector3.one }));
            deploy.Channels.Add(MakeScaleChannel(f2, new[] { 0f, 0.75f }, new[] { new Vector3(0.1f, 1f, 1f), Vector3.one }));
            deploy.Channels.Add(MakeScaleChannel(f3, new[] { 0f, 0.75f }, new[] { new Vector3(0.1f, 1f, 1f), Vector3.one }));
            deploy.Channels.Add(MakeScaleChannel(f4, new[] { 0f, 0.75f }, new[] { new Vector3(0.1f, 1f, 1f), Vector3.one }));
            model.Animations.Add(deploy);

            return model;
        }

        private static GlbParsedModel BuildDefaultMissileOrBox(string key, string? decalPngPath)
        {
            var model = new GlbParsedModel
            {
                SourceKey = key,
                FileName = $"procedural_{key}",
                LocalBounds = new Bounds(Vector3.zero, new Vector3(0.4f, 0.4f, 3.2f))
            };

            model.Materials.Add(new GlbMaterialData
            {
                Name = "Default_Olive",
                BaseColor = new Color(0.32f, 0.35f, 0.28f, 1f),
                Metallic = 0.25f,
                Smoothness = 0.35f
            });
            model.Meshes.Add(CreateBoxMesh("Body", new Vector3(0.32f, 0.32f, 3.2f), Vector3.zero, 0));
            int nRoot = AddNode(model, "Root", 0, Vector3.zero, Quaternion.identity);
            model.RootNodeIndices.Add(nRoot);
            _ = decalPngPath;
            return model;
        }

        private static int AddNode(
            GlbParsedModel model,
            string name,
            int meshIndex,
            Vector3 pos,
            Quaternion rot,
            int parentIdx = -1)
        {
            int idx = model.Nodes.Count;
            model.Nodes.Add(new GlbNodeData
            {
                Index = idx,
                Name = name,
                MeshIndex = meshIndex,
                LocalPosition = pos,
                LocalRotation = rot,
                LocalScale = Vector3.one
            });

            if (parentIdx >= 0 && parentIdx < idx)
            {
                model.Nodes[parentIdx].Children.Add(idx);
            }

            return idx;
        }

        private static GlbMeshData CreateBoxMesh(string name, Vector3 size, Vector3 offset, int materialIndex)
        {
            var mesh = new GlbMeshData { Name = name };
            mesh.Primitives.Add(CreateBoxPrimitive(name, size, offset, materialIndex));
            return mesh;
        }

        private static GlbPrimitiveData CreateBoxPrimitive(string name, Vector3 size, Vector3 offset, int materialIndex)
        {
            Vector3 h = size * 0.5f;
            Vector3[] p =
            {
                new Vector3(-h.x, -h.y, -h.z) + offset,
                new Vector3( h.x, -h.y, -h.z) + offset,
                new Vector3( h.x,  h.y, -h.z) + offset,
                new Vector3(-h.x,  h.y, -h.z) + offset,
                new Vector3(-h.x, -h.y,  h.z) + offset,
                new Vector3( h.x, -h.y,  h.z) + offset,
                new Vector3( h.x,  h.y,  h.z) + offset,
                new Vector3(-h.x,  h.y,  h.z) + offset
            };

            var verts = new Vector3[24];
            var norms = new Vector3[24];
            var uvs = new Vector2[24];
            var tris = new int[36];

            void AddFace(int f, int a, int b, int c, int d, Vector3 n)
            {
                int v = f * 4;
                verts[v + 0] = p[a];
                verts[v + 1] = p[b];
                verts[v + 2] = p[c];
                verts[v + 3] = p[d];
                norms[v + 0] = n;
                norms[v + 1] = n;
                norms[v + 2] = n;
                norms[v + 3] = n;
                uvs[v + 0] = new Vector2(0f, 0f);
                uvs[v + 1] = new Vector2(1f, 0f);
                uvs[v + 2] = new Vector2(1f, 1f);
                uvs[v + 3] = new Vector2(0f, 1f);
                int t = f * 6;
                tris[t + 0] = v + 0;
                tris[t + 1] = v + 2;
                tris[t + 2] = v + 1;
                tris[t + 3] = v + 0;
                tris[t + 4] = v + 3;
                tris[t + 5] = v + 2;
            }

            AddFace(0, 0, 1, 2, 3, Vector3.back);
            AddFace(1, 5, 4, 7, 6, Vector3.forward);
            AddFace(2, 4, 0, 3, 7, Vector3.left);
            AddFace(3, 1, 5, 6, 2, Vector3.right);
            AddFace(4, 3, 2, 6, 7, Vector3.up);
            AddFace(5, 4, 5, 1, 0, Vector3.down);

            return new GlbPrimitiveData
            {
                Name = name,
                Vertices = verts,
                Normals = norms,
                Uvs = uvs,
                Triangles = tris,
                MaterialIndex = materialIndex
            };
        }

        private static GlbMeshData CreateDecalQuadMesh(string name, float width, float height, int materialIndex)
        {
            float hw = width * 0.5f;
            float hh = height * 0.5f;
            var prim = new GlbPrimitiveData
            {
                Name = name,
                Vertices = new[]
                {
                    new Vector3(-hw, -hh, 0f),
                    new Vector3( hw, -hh, 0f),
                    new Vector3( hw,  hh, 0f),
                    new Vector3(-hw,  hh, 0f)
                },
                Normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back },
                Uvs = new[]
                {
                    new Vector2(0f, 0f),
                    new Vector2(1f, 0f),
                    new Vector2(1f, 1f),
                    new Vector2(0f, 1f)
                },
                Triangles = new[] { 0, 2, 1, 0, 3, 2 },
                MaterialIndex = materialIndex
            };

            var mesh = new GlbMeshData { Name = name };
            mesh.Primitives.Add(prim);
            return mesh;
        }

        private static GlbAnimationChannelData MakeVec3Channel(int nodeIdx, GlbAnimationPath path, float[] times, Vector3[] vals)
        {
            return new GlbAnimationChannelData
            {
                NodeIndex = nodeIdx,
                Path = path,
                Interpolation = GlbInterpolationMode.Linear,
                Times = times,
                Vec3Values = vals
            };
        }

        private static GlbAnimationChannelData MakeRotChannel(int nodeIdx, float[] times, Quaternion[] rots)
        {
            return new GlbAnimationChannelData
            {
                NodeIndex = nodeIdx,
                Path = GlbAnimationPath.Rotation,
                Interpolation = GlbInterpolationMode.Linear,
                Times = times,
                QuatValues = rots
            };
        }

        private static GlbAnimationChannelData MakeScaleChannel(int nodeIdx, float[] times, Vector3[] scales)
        {
            return new GlbAnimationChannelData
            {
                NodeIndex = nodeIdx,
                Path = GlbAnimationPath.Scale,
                Interpolation = GlbInterpolationMode.Linear,
                Times = times,
                Vec3Values = scales
            };
        }
    }
}
