Shader "Hidden/Faceless/Narcissus"
{
    SubShader
    {
        Pass
        {
            Cull Off ZWrite On ZTest LEqual Blend Off
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _PlantBounds;
            float _DepthScale;
            struct appdata { float4 vertex:POSITION; float3 normal:NORMAL; float4 color:COLOR; float2 uv:TEXCOORD0; };
            struct v2f { float4 position:SV_POSITION; float3 normal:TEXCOORD0; float4 color:COLOR; float2 uv:TEXCOORD1; };
            v2f vert(appdata v)
            {
                v2f o;
                float2 p=(v.vertex.xy-_PlantBounds.xy)/_PlantBounds.zw*2-1;
                #if UNITY_UV_STARTS_AT_TOP
                    p.y=-p.y;
                #endif
                float depth=saturate(.5-v.vertex.z/_DepthScale);
                #if defined(UNITY_REVERSED_Z)
                    depth=1-depth;
                #else
                    depth=lerp(UNITY_NEAR_CLIP_VALUE,1.0,depth);
                #endif
                o.position=float4(p,depth,1); o.normal=v.normal; o.color=v.color; o.uv=v.uv; return o;
            }
            float4 frag(v2f i):SV_Target
            {
                float3 n=normalize(i.normal+float3(0,0,.00001));
                if(n.z<0) n=-n;
                float3 lightDirection=normalize(float3(-.45,.65,.8));
                float diffuse=saturate(dot(n,lightDirection));
                float specular=pow(saturate(dot(n,normalize(lightDirection+float3(0,0,1)))),22);
                float petal=smoothstep(.5,.6,i.color.a)*(1-smoothstep(.8,.9,i.color.a));
                float cup=smoothstep(.9,1,i.color.a);
                // Fine longitudinal fibres, subdued wax sheen, pale transmitted light.
                float veins=sin(i.uv.y*62.83185+i.uv.x*2)*.018*(1-cup);
                float translucence=petal*.10*(1-abs(n.z));
                float3 c=i.color.rgb*(.43+.57*diffuse+veins+translucence);
                c*=1-cup*.28*(1-i.uv.y);
                c+=specular*lerp(.055,.12,petal);
                return float4(saturate(c),1);
            }
            ENDCG
        }
        Pass
        {
            Cull Off ZWrite Off ZTest LEqual Blend One One, Zero One
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment fragPollen
            #include "UnityCG.cginc"
            float4 _PlantBounds;
            float _DepthScale;
            struct appdata { float4 vertex:POSITION; float3 normal:NORMAL; float4 color:COLOR; float2 uv:TEXCOORD0; };
            struct v2f { float4 position:SV_POSITION; float3 normal:TEXCOORD0; float4 color:COLOR; float2 uv:TEXCOORD1; };
            v2f vert(appdata v)
            {
                v2f o;
                float2 p=(v.vertex.xy-_PlantBounds.xy)/_PlantBounds.zw*2-1;
                #if UNITY_UV_STARTS_AT_TOP
                    p.y=-p.y;
                #endif
                float depth=saturate(.5-v.vertex.z/_DepthScale);
                #if defined(UNITY_REVERSED_Z)
                    depth=1-depth;
                #else
                    depth=lerp(UNITY_NEAR_CLIP_VALUE,1.0,depth);
                #endif
                o.position=float4(p,depth,1); o.normal=v.normal; o.color=v.color; o.uv=v.uv; return o;
            }
            float4 fragPollen(v2f i):SV_Target
            {
                float radial=length(i.uv*2-1);
                float halo=exp(-4*radial*radial)*(1-smoothstep(.65,1,radial));
                float core=exp(-65*radial*radial);
                float3 emission=(i.color.rgb*halo*.48+float3(1,1,.88)*core)*i.color.a;
                // Add light without changing the underlying plant coverage alpha.
                return float4(emission,0);
            }
            ENDCG
        }
    }
    Fallback Off
}
