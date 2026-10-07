Shader "Hidden/IDAS3/Opaque Alpha Depth"
{
 Properties { _MainTex("Original texture",2D)="white"{} }
 SubShader {
  Pass {
   Cull Off ZWrite On ZTest LEqual ColorMask 0
   Blend Off
   HLSLPROGRAM
   #pragma target 5.0
   #pragma exclude_renderers metal
   #pragma vertex mainVS
   #pragma geometry showroomGeometry
   #pragma fragment opaqueAlphaDepthPS
   #define IDAS3_GEOMETRY_STAGE
   #include "Idas3SceneCommon.cginc"
   float4 opaqueAlphaDepthPS(P v):SV_TARGET {
    // Use the final original alpha, including material routing, source
    // lighting, texture filtering and clipping. Only float rounding near
    // alpha1 is tolerated; actual partial coverage never occludes here.
    clip(mainPS(v).a - .999999);
    return 0;
   }
   ENDHLSL
  }
 }
 // Metal has no geometry stage. Reproduce its triangle operations in VS.
 SubShader {
  Pass {
   Cull Off ZWrite On ZTest LEqual ColorMask 0
   Blend Off
   HLSLPROGRAM
   #pragma target 4.5
   #pragma only_renderers metal
   #pragma vertex mainVS
   #pragma fragment opaqueAlphaDepthPS
   #define IDAS3_VERTEX_TRIANGLES
   #include "Idas3SceneCommon.cginc"
   float4 opaqueAlphaDepthPS(P v):SV_TARGET {
    // Use the final original alpha, including material routing, source
    // lighting, texture filtering and clipping. Only float rounding near
    // alpha1 is tolerated; actual partial coverage never occludes here.
    clip(mainPS(v).a - .999999);
    return 0;
   }
   ENDHLSL
  }
 }
 Fallback Off
}
