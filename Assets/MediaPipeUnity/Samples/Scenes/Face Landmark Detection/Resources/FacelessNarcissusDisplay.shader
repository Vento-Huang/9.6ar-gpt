Shader "Faceless/NarcissusDisplay"
{
    Properties
    {
        [PerRendererData] _MainTex ("Plants",2D)="white" {}
        _Color ("Tint",Color)=(1,1,1,1)
        _StencilComp ("Stencil comparison",Float)=8
        _Stencil ("Stencil ID",Float)=0
        _StencilOp ("Stencil operation",Float)=0
        _StencilWriteMask ("Stencil write mask",Float)=255
        _StencilReadMask ("Stencil read mask",Float)=255
        _ColorMask ("Color mask",Float)=15
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Alpha clip",Float)=0
    }
    SubShader
    {
        Tags {"Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True"}
        Stencil {Ref [_Stencil] Comp [_StencilComp] Pass [_StencilOp] ReadMask [_StencilReadMask] WriteMask [_StencilWriteMask]}
        Cull Off Lighting Off ZWrite Off ZTest [unity_GUIZTestMode]
        Blend One OneMinusSrcAlpha
        ColorMask [_ColorMask]
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"
            sampler2D _MainTex;
            float4 _Color,_ClipRect;
            struct appdata {float4 vertex:POSITION;float2 uv:TEXCOORD0;float4 color:COLOR;};
            struct v2f {float4 position:SV_POSITION;float2 uv:TEXCOORD0;float4 color:COLOR;float4 local:TEXCOORD1;};
            v2f vert(appdata v)
            {v2f o;o.local=v.vertex;o.position=UnityObjectToClipPos(v.vertex);o.uv=v.uv;o.color=v.color*_Color;return o;}
            float4 frag(v2f i):SV_Target
            {
                // The offscreen pass stores premultiplied plants plus additive firefly light.
                // Do not multiply alpha twice: soft particles must not develop dark rims.
                float4 c=tex2D(_MainTex,i.uv);
                c.rgb*=i.color.rgb*i.color.a;c.a*=i.color.a;
                #ifdef UNITY_UI_CLIP_RECT
                    c*=UnityGet2DClipping(i.local.xy,_ClipRect);
                #endif
                #ifdef UNITY_UI_ALPHACLIP
                    clip(c.a-.001);
                #endif
                return c;
            }
            ENDCG
        }
    }
    Fallback Off
}
