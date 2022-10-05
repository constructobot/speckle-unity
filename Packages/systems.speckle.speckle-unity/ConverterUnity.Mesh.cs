﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Objects.Other;
using Objects.Utils;
using PlasticPipe.Certificates;
using Speckle.ConnectorUnity;
using Speckle.Core.Models;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Material = UnityEngine.Material;
using Mesh = UnityEngine.Mesh;
using Object = UnityEngine.Object;
using SMesh = Objects.Geometry.Mesh;
using SColor = System.Drawing.Color;
using Transform = UnityEngine.Transform;
using STransform = Objects.Other.Transform;

#nullable enable
namespace Objects.Converter.Unity
{
    public partial class ConverterUnity
    {

        protected static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");
        protected static readonly int Metallic = Shader.PropertyToID("_Metallic");
        protected static readonly int Glossiness = Shader.PropertyToID("_Glossiness");

        #region ToSpeckle

        public virtual List<SMesh>? MeshToSpeckle(MeshFilter meshFilter)
        {
            Material[]? materials = meshFilter.GetComponent<Renderer>()?.materials;
#if UNITY_EDITOR
            var nativeMesh = meshFilter.sharedMesh;
#else
            var nativeMesh = meshFilter.mesh;
#endif
            if (nativeMesh == null) return null;

            List<SMesh> convertedMeshes = new List<SMesh>(nativeMesh.subMeshCount);
            for (int i = 0; i < nativeMesh.subMeshCount; i++)
            {
                var subMesh = nativeMesh.GetSubMesh(i);
                SMesh converted;
                switch (subMesh.topology)
                {
                    // case MeshTopology.Points:
                    //     //TODO convert as pointcloud
                    //     continue;
                    case MeshTopology.Triangles:
                        converted = SubMeshToSpeckle(nativeMesh, meshFilter.transform, subMesh, i, 3);
                        convertedMeshes.Add(converted);
                        break;
                    case MeshTopology.Quads:
                        converted = SubMeshToSpeckle(nativeMesh, meshFilter.transform, subMesh, i, 4);
                        convertedMeshes.Add(converted);
                        break;
                    default:
                        Debug.LogError(
                            $"Failed to convert submesh {i} of {typeof(GameObject)} {meshFilter.gameObject.name} to Speckle, Unsupported Mesh Topography {subMesh.topology}. Submesh will be ignored.");
                        continue;
                }

                if (materials == null || materials.Length <= i) continue;

                Material mat = materials[i];
                if (mat != null) converted["renderMaterial"] = MaterialToSpeckle(mat);
            }

            return convertedMeshes;
        }


        protected virtual SMesh SubMeshToSpeckle(Mesh nativeMesh, Transform instanceTransform,
            SubMeshDescriptor subMesh, int subMeshIndex, int faceN)
        {
            var nFaces = nativeMesh.GetIndices(subMeshIndex, true);
            int numFaces = nFaces.Length / faceN;
            List<int> sFaces = new List<int>(numFaces * (faceN + 1));

            int indexOffset = subMesh.firstVertex;

            // int i = 0;
            // int j = 0;
            // while (i < nFaces.Length)
            // {
            //     if (j == 0)
            //     {
            //         sFaces.Add(faceN);
            //         j = faceN;
            //     }
            //     sFaces.Add(nFaces[i] - indexOffset);
            //     j--;
            //     i++;
            // }

            int i = nFaces.Length - 1;
            int j = 0;
            while (i >= 0) //Traverse backwards to ensure CCW face orientation
            {
                if (j == 0)
                {
                    //Add face cardinality indicator ever
                    sFaces.Add(faceN);
                    j = faceN;
                }

                sFaces.Add(nFaces[i] - indexOffset);
                j--;
                i--;
            }

            int vertexTake = subMesh.vertexCount;
            var nVertices = nativeMesh.vertices.Skip(indexOffset).Take(vertexTake);
            List<double> sVertices = new List<double>(subMesh.vertexCount * 3);
            foreach (var vertex in nVertices)
            {
                var p = instanceTransform.TransformPoint(vertex);
                sVertices.Add(p.x);
                sVertices.Add(p.z); //z and y swapped //TODO is this correct? LH -> RH
                sVertices.Add(p.y);
            }

            var nColors = nativeMesh.colors.Skip(indexOffset).Take(vertexTake).ToArray();
            ;
            List<int> sColors = new List<int>(nColors.Length);
            sColors.AddRange(nColors.Select(c => c.ToIntColor()));

            var nTexCoords = nativeMesh.uv.Skip(indexOffset).Take(vertexTake).ToArray();
            List<double> sTexCoords = new List<double>(nTexCoords.Length * 2);
            foreach (var uv in nTexCoords)
            {
                sTexCoords.Add(uv.x);
                sTexCoords.Add(uv.y);
            }

            var convertedMesh = new SMesh
            {
                vertices = sVertices,
                faces = sFaces,
                colors = sColors,
                textureCoordinates = sTexCoords,
                units = ModelUnits
            };

            return convertedMesh;
        }

        /// <summary>
        ///  List of officially supported shaders. Will attempt to convert shaders not on this list, but will throw warning.
        /// </summary>
        protected static HashSet<string> SupportedShadersToSpeckle = new()
        {
            "Legacy Shaders/Transparent/Diffuse", "Standard"
        };

        public virtual RenderMaterial MaterialToSpeckle(Material nativeMaterial)
        {
            //Warning message for unknown shaders
            if (!SupportedShadersToSpeckle.Contains(nativeMaterial.shader.name))
                Debug.LogWarning(
                    $"Material Shader \"{nativeMaterial.shader.name}\" is not explicitly supported, the resulting material may be incorrect");

            var color = nativeMaterial.color;
            var opacity = 1f;
            if (nativeMaterial.shader.name.ToLower().Contains("transparent"))
            {
                opacity = color.a;
                color.a = 255;
            }

            var emissive = nativeMaterial.IsKeywordEnabled("_EMISSION")
                ? nativeMaterial.GetColor(EmissionColor).ToIntColor()
                : SColor.Black.ToArgb();

            var materialName = !string.IsNullOrWhiteSpace(nativeMaterial.name)
                ? nativeMaterial.name.Replace("(Instance)", string.Empty).TrimEnd()
                : $"material-{Guid.NewGuid().ToString().Substring(0, 8)}";

            var metalness = nativeMaterial.HasProperty(Metallic)
                ? nativeMaterial.GetFloat(Metallic)
                : 0;

            var roughness = nativeMaterial.HasProperty(Glossiness)
                ? 1 - nativeMaterial.GetFloat(Glossiness)
                : 1;

            return new RenderMaterial
            {
                name = materialName,
                diffuse = color.ToIntColor(),
                opacity = opacity,
                metalness = metalness,
                roughness = roughness,
                emissive = emissive,
            };
        }

        #endregion


        #region ToNative

        /// <summary>
        /// Converts multiple <paramref name="meshes"/> (e.g. with different materials) into one native mesh
        /// </summary>
        /// <param name="element">Root element who's name/id is used to identify the mesh</param>
        /// <param name="meshes">Collection of <see cref="Objects.Geometry.Mesh"/>es that shall be converted</param>
        /// <returns>A <see cref="GameObject"/> with the converted <see cref="UnityEngine.Mesh"/>, <see cref="MeshFilter"/>, and <see cref="MeshRenderer"/></returns>
        public GameObject? MeshesToNative(Base element, IReadOnlyCollection<SMesh> meshes)
        {
            if (!meshes.Any())
            {
                Debug.Log($"Skipping {element.GetType()} {element.id}, zero {typeof(SMesh)} provided");
                return null;
            }
            
            Mesh nativeMesh;
            Material[] nativeMaterials = RenderMaterialsToNative(meshes);
            Vector3 center;
            
            if (LoadedAssets.TryGetValue(element.id, out var existingObj)
                && existingObj is Mesh existing)
            {
                nativeMesh = existing;
                MeshDataToNative(meshes,
                    out List<Vector3> verts,
                    out _,
                    out _,
                    out _);
                center = CalculateBounds(verts).center;
            }
            else
            {
                MeshToNativeMesh(meshes, out nativeMesh, out center);
                string name = GetAssetName(element);
                nativeMesh.name = name;
#if UNITY_EDITOR
                if (StreamManager.GenerateAssets) //TODO: I don't like how the converter is aware of StreamManager
                {
                    const string assetPath = "Assets/Resources/Meshes/Speckle Generated/";
                    CreateDirectoryFromAssetPath(assetPath);
                    AssetDatabase.CreateAsset(nativeMesh, $"{assetPath}/{name}");
                }
#endif
            }
            
            var go = new GameObject();
            go.transform.position = center;
            go.SafeMeshSet(nativeMesh, nativeMaterials);
            
            return go;
        }


        /// <summary>
        /// Converts <paramref name="speckleMesh"/> to a <see cref="GameObject"/> with a <see cref="MeshRenderer"/>
        /// </summary>
        /// <param name="speckleMesh">Mesh to convert</param>
        /// <returns></returns>
        public GameObject? MeshToNative(SMesh speckleMesh)
        {
            if (speckleMesh.vertices.Count == 0 || speckleMesh.faces.Count == 0)
            {
                Debug.Log($"Skipping mesh {speckleMesh.id}, mesh data was empty");
                return null;
            }

            GameObject? converted = MeshesToNative(speckleMesh, new[] {speckleMesh});

            // Raw meshes shouldn't have dynamic props to attach
            //if (converted != null) AttachSpeckleProperties(converted,speckleMesh.GetType(), GetProperties(speckleMesh, typeof(Mesh)));

            return converted;
        }

        /// <summary>
        /// Converts Speckle <see cref="SMesh"/>s as a native <see cref="Mesh"/> object 
        /// </summary>
        /// <param name="meshes">meshes to be converted as SubMeshes</param>
        /// <param name="nativeMesh">The converted native mesh</param>
        public void MeshToNativeMesh(IReadOnlyCollection<SMesh> meshes, out Mesh nativeMesh)
            => MeshToNativeMesh(meshes, out nativeMesh, out _, false);

        
        /// <inheritdoc cref="MeshDataToNative(System.Collections.Generic.IReadOnlyCollection{Objects.Geometry.Mesh},out UnityEngine.Mesh,out UnityEngine.Material[])"/>
        /// <param name="recenterVerts">when true, will recenter vertices</param>
        /// <param name="center">Center position for the mesh</param>
        public void MeshToNativeMesh(IReadOnlyCollection<SMesh> meshes,
            out Mesh nativeMesh,
            out Vector3 center,
            bool recenterVerts = true)
        {
            MeshDataToNative(meshes,
                out List<Vector3> verts,
                out List<Vector2> uvs,
                out List<Color> vertexColors,
                out List<List<int>> subMeshes);
            
            Debug.Assert(verts.Count >= 0);

            center = recenterVerts ? RecenterVertices(verts) : Vector3.zero;

            nativeMesh = new Mesh();
            nativeMesh.subMeshCount = subMeshes.Count;
            nativeMesh.SetVertices(verts);
            nativeMesh.SetUVs(0, uvs);
            nativeMesh.SetColors(vertexColors);


            int j = 0;
            foreach (var subMeshTriangles in subMeshes)
            {
                nativeMesh.SetTriangles(subMeshTriangles, j);
                j++;
            }

            if (nativeMesh.vertices.Length >= UInt16.MaxValue)
                nativeMesh.indexFormat = IndexFormat.UInt32;

            nativeMesh.Optimize();
            nativeMesh.RecalculateBounds();
            nativeMesh.RecalculateNormals();
            nativeMesh.RecalculateTangents();
        }
        
        public void MeshDataToNative(IReadOnlyCollection<SMesh> meshes, 
            out List<Vector3> verts,
            out List<Vector2> uvs,
            out List<Color> vertexColors,
            out List<List<int>> subMeshes)
        {
            verts = new List<Vector3>();
            uvs = new List<Vector2>();
            vertexColors = new List<Color>();
            subMeshes = new List<List<int>>(meshes.Count);

            foreach (SMesh m in meshes)
            {
                if (m.vertices.Count == 0 || m.faces.Count == 0) continue;
                
                List<int> tris = new List<int>();
                SubmeshToNative(m, verts, tris, uvs, vertexColors);
                subMeshes.Add(tris);
            }
        }


        protected void SubmeshToNative(SMesh speckleMesh, List<Vector3> verts, List<int> tris, List<Vector2> texCoords,
            List<Color> vertexColors)
        {
            speckleMesh.AlignVerticesWithTexCoordsByIndex();
            speckleMesh.TriangulateMesh();

            int indexOffset = verts.Count;

            // Convert Vertices
            verts.AddRange(ArrayToPoints(speckleMesh.vertices, speckleMesh.units));

            // Convert texture coordinates
            bool hasValidUVs = speckleMesh.TextureCoordinatesCount == speckleMesh.VerticesCount;
            if (speckleMesh.textureCoordinates.Count > 0 && !hasValidUVs)
                Debug.LogWarning(
                    $"Expected number of UV coordinates to equal vertices. Got {speckleMesh.TextureCoordinatesCount} expected {speckleMesh.VerticesCount}. \nID = {speckleMesh.id}");

            if (hasValidUVs)
            {
                texCoords.Capacity += speckleMesh.TextureCoordinatesCount;
                for (int j = 0; j < speckleMesh.TextureCoordinatesCount; j++)
                {
                    var (u, v) = speckleMesh.GetTextureCoordinate(j);
                    texCoords.Add(new Vector2((float) u, (float) v));
                }
            }
            else if (speckleMesh.bbox != null)
            {
                // Attempt to generate some crude UV coordinates using bbox
                texCoords.AddRange(GenerateUV(indexOffset, verts, (float) speckleMesh.bbox.xSize.Length,
                    (float) speckleMesh.bbox.ySize.Length));
            }
            else
            {
                texCoords.AddRange(Enumerable.Repeat(Vector2.zero, verts.Count - indexOffset));
            }

            // Convert vertex colors
            if (speckleMesh.colors != null)
            {
                if (speckleMesh.colors.Count == speckleMesh.VerticesCount)
                {
                    vertexColors.AddRange(speckleMesh.colors.Select(c => c.ToUnityColor()));
                }
                else if (speckleMesh.colors.Count != 0)
                {
                    //TODO what if only some submeshes have colors?
                    Debug.LogWarning(
                        $"{typeof(SMesh)} {speckleMesh.id} has invalid number of vertex {nameof(SMesh.colors)}. Expected 0 or {speckleMesh.VerticesCount}, got {speckleMesh.colors.Count}");
                }
            }

            // Convert faces
            tris.Capacity += (int) (speckleMesh.faces.Count / 4f) * 3;

            for (int i = 0; i < speckleMesh.faces.Count; i += 4)
            {
                // We can safely assume all faces are triangles since we called TriangulateMesh
                tris.Add(speckleMesh.faces[i + 1] + indexOffset);
                tris.Add(speckleMesh.faces[i + 3] + indexOffset);
                tris.Add(speckleMesh.faces[i + 2] + indexOffset);
            }
        }

        
        protected static IEnumerable<Vector2> GenerateUV(int indexOffset, IReadOnlyList<Vector3> verts, float xSize,
            float ySize)
        {
            var uv = new Vector2[verts.Count - indexOffset];
            for (int i = 0; i < verts.Count - indexOffset; i++)
            {

                var vert = verts[i];
                uv[i] = new Vector2(vert.x / xSize, vert.y / ySize);
            }

            return uv;
        }
        
        protected static Vector3 RecenterVertices(IList<Vector3> vertices)
        {
            if (!vertices.Any()) return Vector3.zero;
            
            Bounds meshBounds = CalculateBounds(vertices);
            
            for (int i = 0; i < vertices.Count; i++)
                vertices[i] -= meshBounds.center;

            return meshBounds.center;
        }
        
        protected static Bounds CalculateBounds(IList<Vector3> points)
        {
            Bounds b = new Bounds {center = points[0]};

            foreach (var p in points)
                b.Encapsulate(p);

            return b;
        }
        
        
        public Material[] RenderMaterialsToNative(IEnumerable<SMesh> meshes)
        {
            return meshes.Select(m => RenderMaterialToNative(m["renderMaterial"] as RenderMaterial)).ToArray();
        }
        
        public Material RenderMaterialToNative(RenderMaterial? renderMaterial)
        {
            //todo support more complex materials
            var shader = Shader.Find("Standard");
            Material mat = new Material(shader);

            //if a renderMaterial is passed use that, otherwise try get it from the mesh itself
            if (renderMaterial == null) return mat;

            // 1. match material by name, if any
            string materialName = string.IsNullOrWhiteSpace(renderMaterial.name)
                ? $"material-{renderMaterial.id}"
                : renderMaterial.name.Replace('/', '-');

            if (LoadedAssets.TryGetValue(materialName, out Object asset)
                && asset is Material loadedMaterial) return loadedMaterial;

            // 2. re-create material by setting diffuse color and transparency on standard shaders
            if (renderMaterial.opacity < 1)
            {
                shader = Shader.Find("Transparent/Diffuse");
                mat = new Material(shader);
            }

            var c = renderMaterial.diffuse.ToUnityColor();
            mat.color = new Color(c.r, c.g, c.b, (float) renderMaterial.opacity);
            mat.name = materialName;
            mat.SetFloat(Metallic, (float) renderMaterial.metalness);
            mat.SetFloat(Glossiness, 1 - (float) renderMaterial.roughness);

            if (renderMaterial.emissive != SColor.Black.ToArgb()) mat.EnableKeyword("_EMISSION");
            mat.SetColor(EmissionColor, renderMaterial.emissive.ToUnityColor());


#if UNITY_EDITOR
            if (StreamManager.GenerateAssets) //TODO: I don't like how the converter is aware of StreamManager
            {
                var invalidChars = Path.GetInvalidFileNameChars();
                string name = new(mat.name.Where(x => !invalidChars.Contains(x)).ToArray());

                const string assetPath = "Assets/Resources/Materials/Speckle Generated/";
                CreateDirectoryFromAssetPath(assetPath);

                if (AssetDatabase.LoadAllAssetsAtPath($"{assetPath}/{name}.mat")
                        .Length == 0)
                    AssetDatabase.CreateAsset(mat, $"{assetPath}/{name}.mat");

            }
#endif

            return mat;
            // 3. if not renderMaterial was passed, the default shader will be used 
        }
    
        protected static string GetAssetName(Base b, bool alwaysIncludeId = true)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            string id = b.id;
            foreach (var nameAlias in new[] {"name", "Name"})
            {
                string? rawName = b[nameAlias] as string;
                if (string.IsNullOrWhiteSpace(rawName)) continue;

                string name = new(rawName.Where(x => !invalidChars.Contains(x)).ToArray());

                return alwaysIncludeId ? $"{name} - {id}" : name;
            }

            return id;
        }
        
        #endregion

    }

}
