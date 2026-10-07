using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// CPU preparation is once per loaded imported mesh, not once per camera/frame.
// Native scene triangles instead use the existing 64-byte vertex ABI buffer.
public static class Idas3MetalSceneGeometry
{
    public static bool IsRequired => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Metal;

    public static void PrepareImportedMesh(Mesh mesh, bool forceForValidation = false)
    {
        if (!IsRequired && !forceForValidation) return;
        var flags = mesh.uv3;
        bool paired = false;
        foreach (var flag in flags) if (flag.x > .5f) { paired = true; break; }
        if (!paired) return;
        var positions = mesh.vertices; var normals = mesh.normals;
        var uv = mesh.uv; var uv2 = mesh.uv2; var colors = mesh.colors32;
        var indices = mesh.triangles; int count = indices.Length;
        if (count % 3 != 0 || flags.Length != positions.Length || normals.Length != positions.Length ||
            uv.Length != positions.Length || uv2.Length != positions.Length || colors.Length != positions.Length)
            throw new InvalidOperationException("Imported Metal triangle channels are incomplete.");
        // Shared indexed vertices may belong to different planes (Enna). Split
        // them before attaching primitive attributes; retain every authored UV,
        // baked color and normal. The existing tree meshes are already split.
        var points = new Vector3[count]; var ns = new Vector3[count];
        var ts = new Vector2[count]; var ts2 = new Vector2[count];
        var tags = new Vector2[count]; var cs = new Color32[count]; var ix = new int[count];
        var faces = new List<Vector3>(count); var origins = new List<Vector3>(count);
        for (int first = 0; first < count; first += 3) {
            int ia = indices[first], ib = indices[first + 1], ic = indices[first + 2];
            Vector3 origin = positions[ia], face = Vector3.Cross(positions[ib] - origin, positions[ic] - origin);
            // The GS tests input[0].treeFace for the entire primitive.
            float rejectedBack = flags[ia].x;
            for (int corner = 0; corner < 3; ++corner) {
                int destination = first + corner, source = indices[destination];
                points[destination] = positions[source]; ns[destination] = normals[source];
                ts[destination] = uv[source]; ts2[destination] = uv2[source]; cs[destination] = colors[source];
                tags[destination] = new Vector2(rejectedBack, flags[source].y); ix[destination] = destination;
                faces.Add(face); origins.Add(origin);
            }
        }
        mesh.Clear(false); mesh.indexFormat = IndexFormat.UInt32;
        mesh.vertices = points; mesh.normals = ns; mesh.uv = ts; mesh.uv2 = ts2;
        mesh.colors32 = cs; mesh.uv3 = tags;
        mesh.SetUVs(3, faces); mesh.SetUVs(4, origins); mesh.triangles = ix;
    }
}
