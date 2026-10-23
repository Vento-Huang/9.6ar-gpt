#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

// The supplied Tripo GLB is a static, single-mesh, single-material export.
// Reject unsupported assets instead of silently dropping parts or transforms.
internal static class NarcissusGlbReader
{
    [Serializable] class Doc { public string[] extensionsRequired; public Node[] nodes; public MeshDef[] meshes; public Accessor[] accessors; public View[] bufferViews; public ImageDef[] images; public TextureDef[] textures; public MaterialDef[] materials; }
    [Serializable] class Node { public int mesh=-1; public float[] matrix,translation,rotation,scale; }
    [Serializable] class MeshDef { public Primitive[] primitives; }
    [Serializable] class Primitive { public int indices,material,mode=4; public Attributes attributes; }
    [Serializable] class Attributes { public int POSITION=-1,NORMAL=-1,TEXCOORD_0=-1; }
    [Serializable] class Accessor { public int bufferView,byteOffset,componentType,count; public string type; }
    [Serializable] class View { public int buffer,byteOffset,byteLength,byteStride; }
    [Serializable] class ImageDef { public int bufferView; public string mimeType; }
    [Serializable] class TextureDef { public int source; }
    [Serializable] class MaterialDef { public Pbr pbrMetallicRoughness; }
    [Serializable] class Pbr { public TextureInfo baseColorTexture; }
    [Serializable] class TextureInfo { public int index; }
    public static void Load(string path,out Mesh mesh,out Texture2D texture)
    {
        mesh=null;texture=null;
        try {
            byte[] data=File.ReadAllBytes(path),bin=null;Doc d=null;
            using(var r=new BinaryReader(new MemoryStream(data))) {
                if(r.ReadUInt32()!=0x46546c67 || r.ReadUInt32()!=2 || r.ReadUInt32()!=data.Length) throw new InvalidDataException("Invalid GLB 2 file.");
                while(r.BaseStream.Position<r.BaseStream.Length) {
                    int length=checked((int)r.ReadUInt32());uint type=r.ReadUInt32();byte[] chunk=r.ReadBytes(length);
                    if(chunk.Length!=length)throw new InvalidDataException("Truncated GLB.");
                    if(type==0x4e4f534a)d=JsonUtility.FromJson<Doc>(System.Text.Encoding.UTF8.GetString(chunk));
                    if(type==0x004e4942)bin=chunk;
                }
            }
            if(d==null||bin==null||d.meshes==null||d.meshes.Length!=1||d.meshes[0].primitives.Length!=1)throw new InvalidDataException("This recorder expects the supplied single-mesh Tripo GLB.");
            if(d.extensionsRequired!=null&&d.extensionsRequired.Length>0)throw new InvalidDataException("Compressed/extension-based GLB is not supported. Export uncompressed GLB.");
            foreach(var node in d.nodes)if(node.matrix!=null||node.translation!=null||node.rotation!=null||node.scale!=null)throw new InvalidDataException("Apply node transforms before exporting GLB.");
            var p=d.meshes[0].primitives[0];if(p.mode!=4||p.attributes.POSITION<0||p.attributes.TEXCOORD_0<0)throw new InvalidDataException("Triangle mesh with UVs required.");
            var positions=Floats(d,bin,p.attributes.POSITION,3);var uvs=Floats(d,bin,p.attributes.TEXCOORD_0,2);
            if(positions.Length/3!=uvs.Length/2)throw new InvalidDataException("UV count mismatch.");
            int count=positions.Length/3;var v=new Vector3[count];var uv=new Vector2[count];
            for(int i=0;i<count;i++){v[i]=new Vector3(positions[3*i],positions[3*i+1],positions[3*i+2]);uv[i]=new Vector2(uvs[2*i],1-uvs[2*i+1]);}
            var ia=d.accessors[p.indices];var iv=d.bufferViews[ia.bufferView];if(ia.type!="SCALAR"||(ia.componentType!=5123&&ia.componentType!=5125))throw new InvalidDataException("Unsupported index data.");
            int[] indices=new int[ia.count];int step=ia.componentType==5123?2:4;
            for(int i=0;i<indices.Length;i++){int off=checked(iv.byteOffset+ia.byteOffset+i*step);indices[i]=step==2?BitConverter.ToUInt16(bin,off):checked((int)BitConverter.ToUInt32(bin,off));if(indices[i]>=count)throw new InvalidDataException("Index outside mesh.");}
            mesh=new Mesh{name="Tripo source portrait",indexFormat=IndexFormat.UInt32,hideFlags=HideFlags.HideAndDontSave};
            mesh.vertices=v;mesh.uv=uv;mesh.triangles=indices;mesh.RecalculateNormals();mesh.RecalculateBounds();
            var info=d.materials[p.material].pbrMetallicRoughness.baseColorTexture;var img=d.images[d.textures[info.index].source];var bv=d.bufferViews[img.bufferView];
            byte[] bytes=new byte[bv.byteLength];Buffer.BlockCopy(bin,bv.byteOffset,bytes,0,bytes.Length);
            texture=new Texture2D(2,2,TextureFormat.RGB24,false){name="Tripo original colour",hideFlags=HideFlags.HideAndDontSave};
            if(!texture.LoadImage(bytes))throw new InvalidDataException("Base-colour texture cannot be decoded.");
            texture.wrapMode=TextureWrapMode.Clamp;
        } catch { if(mesh!=null)UnityEngine.Object.DestroyImmediate(mesh);if(texture!=null)UnityEngine.Object.DestroyImmediate(texture);mesh=null;texture=null;throw; }
    }
    static float[] Floats(Doc d,byte[] bin,int index,int width)
    {
        var a=d.accessors[index];var v=d.bufferViews[a.bufferView];if(a.componentType!=5126||a.type!="VEC"+width)throw new InvalidDataException("Float vertex attributes required.");
        int stride=v.byteStride>0?v.byteStride:width*4;var result=new float[checked(a.count*width)];
        for(int i=0;i<a.count;i++)for(int j=0;j<width;j++)result[i*width+j]=BitConverter.ToSingle(bin,checked(v.byteOffset+a.byteOffset+i*stride+j*4));
        return result;
    }
}
#endif
