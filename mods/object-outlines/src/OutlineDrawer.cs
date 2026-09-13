using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SilksongRL.ObjectOutlines
{
    // 描边绘制. 默认把线段合成一个 Mesh 每帧提交一次 (开销小),
    // 也可以切换回逐顶点 GL 立即模式 (兼容性优先).
    internal sealed class OutlineDrawer
    {
        // 画在透明队列之后, 否则会被游戏的精灵 (透明队列) 盖住.
        private const int OverlayRenderQueue = 4000;

        private Material _material;
        private Mesh _mesh;

        private readonly List<Vector3> _vertices = new List<Vector3>(16384);
        private readonly List<Color> _colors = new List<Color>(16384);
        private int[] _indices = new int[0];
        private int _indexedVertexCount = -1;

        public int VertexCount
        {
            get { return _vertices.Count; }
        }

        public long LastRebuildMilliseconds { get; private set; }

        public Vector3 LastBoundsSize { get; private set; }

        // 在插件启动时调用, 提前准备材质与 Mesh, 便于把失败原因写进日志.
        public bool Prepare()
        {
            if (EnsureMaterial() == null)
            {
                return false;
            }

            if (_mesh == null)
            {
                _mesh = new Mesh();
                _mesh.name = "ObjectOutlinesMesh";
                _mesh.hideFlags = HideFlags.HideAndDontSave;
                // 顶点数超过 65535 时 UInt16 索引会出错, 这里直接用 UInt32.
                _mesh.indexFormat = IndexFormat.UInt32;
                _mesh.MarkDynamic();
            }

            return true;
        }

        // 每帧调用: 把当前各类别的线段合成 Mesh.
        public void Rebuild(OutlineScanner scanner, IList<OutlineCategory> categories, IDictionary<OutlineCategory, Color> colors)
        {
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

            _vertices.Clear();
            _colors.Clear();

            for (int i = 0; i < categories.Count; i++)
            {
                OutlineCategory category = categories[i];
                List<Vector3> source = scanner.Vertices(category);
                if (source.Count == 0)
                {
                    continue;
                }

                Color color;
                if (!colors.TryGetValue(category, out color))
                {
                    continue;
                }

                for (int v = 0; v < source.Count; v++)
                {
                    _vertices.Add(source[v]);
                    _colors.Add(color);
                }
            }

            UpdateMesh();

            watch.Stop();
            LastRebuildMilliseconds = watch.ElapsedMilliseconds;
        }

        // 每帧调用一次, 交给 Unity 渲染到所有能看到该图层的相机.
        public void RenderMesh()
        {
            if (_mesh == null || _material == null || _vertices.Count == 0)
            {
                return;
            }

            Graphics.DrawMesh(_mesh, Matrix4x4.identity, _material, 0);
        }

        // 备用路径: 在 OnRenderObject 期间用 GL 立即模式逐顶点绘制.
        public void RenderImmediate(Camera camera, OutlineScanner scanner, IList<OutlineCategory> categories, IDictionary<OutlineCategory, Color> colors)
        {
            if (camera == null || _material == null)
            {
                return;
            }

            _material.SetPass(0);

            GL.PushMatrix();
            GL.LoadProjectionMatrix(camera.projectionMatrix);
            GL.modelview = camera.worldToCameraMatrix;
            GL.Begin(GL.LINES);

            for (int i = 0; i < categories.Count; i++)
            {
                OutlineCategory category = categories[i];
                List<Vector3> vertices = scanner.Vertices(category);
                if (vertices.Count == 0)
                {
                    continue;
                }

                Color color;
                if (!colors.TryGetValue(category, out color))
                {
                    continue;
                }

                GL.Color(color);
                for (int v = 0; v < vertices.Count; v++)
                {
                    GL.Vertex(vertices[v]);
                }
            }

            GL.End();
            GL.PopMatrix();
        }

        private void UpdateMesh()
        {
            if (_mesh == null)
            {
                return;
            }

            if (_vertices.Count == 0)
            {
                if (_indexedVertexCount != 0)
                {
                    _mesh.Clear();
                    _indexedVertexCount = 0;
                }

                LastBoundsSize = Vector3.zero;
                return;
            }

            if (_vertices.Count != _indexedVertexCount)
            {
                // 顶点数变了就得重建索引 (线段索引就是顺序的 0..n-1).
                _mesh.Clear();
                _mesh.SetVertices(_vertices);
                _mesh.SetColors(_colors);
                EnsureIndices(_vertices.Count);
                _mesh.SetIndices(_indices, MeshTopology.Lines, 0);
                _indexedVertexCount = _vertices.Count;
            }
            else
            {
                _mesh.SetVertices(_vertices);
                _mesh.SetColors(_colors);
            }

            // 用 SetVertices 系列写入后不会自动更新包围盒, 不重算的话 bounds 是全零,
            // Mesh 会被视锥剔除掉, 表现就是"什么都不显示".
            _mesh.RecalculateBounds();
            LastBoundsSize = _mesh.bounds.size;
        }

        private void EnsureIndices(int vertexCount)
        {
            if (_indices.Length == vertexCount)
            {
                return;
            }

            _indices = new int[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                _indices[i] = i;
            }
        }

        private Material EnsureMaterial()
        {
            if (_material != null)
            {
                return _material;
            }

            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                shader = Shader.Find("Sprites/Default");
            }
            if (shader == null)
            {
                shader = Shader.Find("Unlit/Color");
            }
            if (shader == null)
            {
                return null;
            }

            _material = new Material(shader);
            _material.hideFlags = HideFlags.HideAndDontSave;
            _material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            _material.SetInt("_ZWrite", 0);
            _material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            _material.renderQueue = OverlayRenderQueue;
            return _material;
        }
    }
}
