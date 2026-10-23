Shader "Hidden/Narcissus/RecordedHead"
{
    Properties { _MainTex("Original model colour",2D)="white"{} _RecordedSkin("Unity reconstructed skin",2D)="black"{} _RecordedMask("Unity mask",2D)="black"{} }
    SubShader { Pass { Cull Off ZWrite On ZTest LEqual
        CGPROGRAM
        #pragma target 3.5
        #pragma vertex vert
        #pragma fragment frag
        #include "UnityCG.cginc"
        sampler2D _MainTex,_RecordedSkin,_RecordedMask;
        float4x4 _RecordingViewProjection,_RecordingRotation;
        float4 _Reference;
        float _Reveal,_SurfaceFrontZ;
        struct appdata { float4 vertex:POSITION;float2 uv:TEXCOORD0; };
        struct v2f { float4 position:SV_POSITION;float2 uv:TEXCOORD0;float2 projected:TEXCOORD1;float front:TEXCOORD2; };
        float2 referenceUV(float3 p){return (p.xy-_Reference.xy)/_Reference.z+.5;}
        v2f vert(appdata v){
            v2f o;o.uv=v.uv;o.projected=referenceUV(v.vertex.xyz);
            o.front=smoothstep(.10,.17,v.vertex.z);
            float m=tex2Dlod(_RecordedMask,float4(o.projected,0,0)).r*o.front*(1-_Reveal);
            float3 p=v.vertex.xyz;
            // This depth adaptation belongs only to the static model. The
            // coverage and reconstructed colour come from the original effect.
            float flat=_SurfaceFrontZ-1.8*p.x*p.x-.3*(p.y-.74)*(p.y-.74);
            p.z=lerp(p.z,flat,m);
            o.position=mul(_RecordingViewProjection,mul(_RecordingRotation,float4(p,1)));
            return o;
        }
        float4 frag(v2f i):SV_Target{
            float4 base=tex2D(_MainTex,i.uv);
            float m=tex2D(_RecordedMask,i.projected).r*i.front*(1-_Reveal);
            float3 skin=tex2D(_RecordedSkin,i.projected).rgb;
            // The baked composite already contains the original feather once.
            float mixWeight=i.front*(1-_Reveal)*step(.000001,m);
            base.rgb=lerp(base.rgb,skin,mixWeight);return float4(base.rgb,1);
        }
        ENDCG
    } }
    Fallback Off
}
