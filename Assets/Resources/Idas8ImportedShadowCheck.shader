// Diagnostic vertex stage isolates the production fragment from native cameras.
Shader "Hidden/IDAS3/Imported Shadow Check" {
 Properties {
  _MainTex("Base",2D)="white"{}
  _ImportedShadowTex("Visibility",2D)="white"{}
  _ImportedHasShadow("Baked shadow",Float)=0
  _ImportedShadowOnly("Overlay",Float)=0
  _ImportedUntexturedShadow("Untextured shadow visibility",Color)=(0,0,0,0)
  _ImportedShadowUv("UV set",Float)=1
  _ImportedSky("Skip lighting and fog",Float)=1
  _SrcBlend("Source",Float)=1
  _DstBlend("Destination",Float)=0
 }
 SubShader { Pass {
  Cull Off ZWrite Off ZTest Always Blend [_SrcBlend] [_DstBlend]
  HLSLPROGRAM
  #pragma target 4.5
  #pragma vertex checkVS
  #pragma fragment mainPS
  #define IDAS_IMPORTED_COURSE
  #include "Idas3SceneCommon.cginc"
  P checkVS(V v){P o=(P)0;o.p=float4(v.p,1);o.c=v.c;o.uv=v.uv;o.offsetColor=v.offsetColor;o.sponsorAxis=v.treeFace.y;return o;}
  ENDHLSL
 } }
}
