using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;

public static class Idas3MetalGeometryChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    [MenuItem("Tools/IDAS3/Verify Metal Geometry")]
    public static void Run()
    {
        // Native buffer lanes must remain position.xyz/normal.xyz/color/uv/offset.
        Type vertex = typeof(Idas3SceneRenderer).GetNestedType("SceneVertex", BindingFlags.NonPublic);
        Check(vertex != null && Marshal.SizeOf(vertex) == 64, "Native vertex stride changed.");
        string[] members = { "position", "normal", "color", "uv", "offset" };
        int[] offsets = { 0, 12, 24, 40, 48 };
        for (int i = 0; i < members.Length; ++i)
            Check(Marshal.OffsetOf(vertex, members[i]).ToInt32() == offsets[i], "Metal packed lane mismatch: " + members[i]);
        var mesh = new Mesh();
        try {
            var positions = new[] { Vector3.zero, Vector3.right, Vector3.up, new Vector3(1,1,1) };
            var normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
            var colors = new[] { new Color32(10,20,30,40), new Color32(50,60,70,80), new Color32(90,100,110,120), new Color32(130,140,150,160) };
            int[] indices = { 0,1,2, 2,1,3 };
            mesh.vertices = positions; mesh.normals = normals; mesh.colors32 = colors;
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
            mesh.uv2 = mesh.uv; mesh.uv3 = new[] { Vector2.right, Vector2.right, Vector2.right, Vector2.right };
            mesh.triangles = indices;
            Idas3MetalSceneGeometry.PrepareImportedMesh(mesh, true);
            var faces = new List<Vector3>(); var origins = new List<Vector3>(); mesh.GetUVs(3, faces); mesh.GetUVs(4, origins);
            Check(mesh.vertexCount == 6 && faces.Count == 6 && origins.Count == 6, "Shared triangles were not split.");
            for (int i = 0; i < 6; ++i) {
                Check(mesh.triangles[i] == i && mesh.vertices[i] == positions[indices[i]], "Source triangle order changed.");
                Check(mesh.colors32[i].Equals(colors[indices[i]]), "Baked color changed.");
                Check(origins[i] == positions[indices[i - i % 3]], "Primitive anchor differs between vertices.");
            }
            Matrix4x4[] transforms = {
                Matrix4x4.identity,
                Matrix4x4.TRS(new Vector3(3,5,-7), Quaternion.Euler(20,40,60), new Vector3(2,.5f,3)),
                Matrix4x4.TRS(new Vector3(-4,8,2), Quaternion.Euler(-30,70,15), new Vector3(-2,3,.75f))
            };
            Vector3[] cameras = { new Vector3(10,9,12), new Vector3(-8,-6,-11) };
            foreach (var transform in transforms) for (int first = 0; first < 6; first += 3) {
                Vector3 a = transform.MultiplyPoint3x4(mesh.vertices[first]);
                Vector3 b = transform.MultiplyPoint3x4(mesh.vertices[first + 1]);
                Vector3 c = transform.MultiplyPoint3x4(mesh.vertices[first + 2]);
                Vector3 reference = Vector3.Cross(b-a,c-a);
                Vector3 shader = transform.inverse.transpose.MultiplyVector(faces[first]) * Mathf.Sign(transform.determinant);
                foreach (var camera in cameras) {
                    float expected = Vector3.Dot(reference,camera-a), actual = Vector3.Dot(shader,camera-transform.MultiplyPoint3x4(origins[first]));
                    Check(Mathf.Sign(expected) == Mathf.Sign(actual), "Main/mirror affine face rejection changed.");
                    foreach (int mode in new[] {2,3}) Check((mode==2 ? expected>=0 : expected<=0) == (mode==2 ? actual>=0 : actual<=0), "Native ISP culling sign changed.");
                }
            }
        } finally { UnityEngine.Object.DestroyImmediate(mesh); }
        Debug.Log("IDAS3_METAL_GEOMETRY_CHECKS_PASSED: native layout, indexed expansion, source winding, mirrored transforms.");
    }
}
