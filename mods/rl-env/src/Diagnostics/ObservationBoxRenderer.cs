using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RLEnv.Diagnostics
{
    // 把"观测里到底看到了什么"画成世界空间的线框, 方便肉眼核对观测与画面是否一致.
    //
    // 做法沿用 mods/object-outlines: 每帧把所有矩形拼成一个 Mesh, 用 Graphics.DrawMesh 提交,
    // 材质走 Hidden/Internal-Colored 并排在透明队列之后, 免得被游戏精灵盖住.
    internal sealed class ObservationBoxRenderer
    {
        private const int OverlayRenderQueue = 4000;

        private const float LineThickness = 0.06f;

        private readonly List<Vector3> _vertices = new List<Vector3>(1024);

        private readonly List<Color> _colors = new List<Color>(1024);

        private Material _material;

        private Mesh _mesh;

        private int[] _indices = new int[0];

        private int _indexedVertexCount = -1;

        internal bool Enabled { get; set; }

        internal int BoxCount { get; private set; }

        internal bool Prepare()
        {
            if (EnsureMaterial() == null)
            {
                return false;
            }

            if (_mesh == null)
            {
                _mesh = new Mesh();
                _mesh.name = "RLEnvObservationBoxes";
                _mesh.hideFlags = HideFlags.HideAndDontSave;
                _mesh.indexFormat = IndexFormat.UInt32;
                _mesh.MarkDynamic();
            }

            return true;
        }

        // 用当前这批矩形重建线框 (每个矩形 4 条边, 每条边用一段细长四边形会太啰嗦,
        // 这里直接画 4 条线段, 靠 LineTopology 渲染).
        internal void Rebuild(List<ObservationBox> boxes)
        {
            _vertices.Clear();
            _colors.Clear();
            BoxCount = boxes != null ? boxes.Count : 0;

            if (boxes == null)
            {
                UpdateMesh();
                return;
            }

            for (int i = 0; i < boxes.Count; i++)
            {
                ObservationBox box = boxes[i];
                float halfWidth = Mathf.Max(0.05f, box.Size.x * 0.5f);
                float halfHeight = Mathf.Max(0.05f, box.Size.y * 0.5f);
                float left = box.Center.x - halfWidth;
                float right = box.Center.x + halfWidth;
                float bottom = box.Center.y - halfHeight;
                float top = box.Center.y + halfHeight;
                float z = 0f;

                AddLine(new Vector3(left, bottom, z), new Vector3(right, bottom, z), box.Color);
                AddLine(new Vector3(right, bottom, z), new Vector3(right, top, z), box.Color);
                AddLine(new Vector3(right, top, z), new Vector3(left, top, z), box.Color);
                AddLine(new Vector3(left, top, z), new Vector3(left, bottom, z), box.Color);
            }

            UpdateMesh();
        }

        internal void Render()
        {
            if (!Enabled || _mesh == null || _material == null || _vertices.Count == 0)
            {
                return;
            }

            Graphics.DrawMesh(_mesh, Matrix4x4.identity, _material, 0);
        }

        private void AddLine(Vector3 from, Vector3 to, Color color)
        {
            _vertices.Add(from);
            _vertices.Add(to);
            _colors.Add(color);
            _colors.Add(color);
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

                return;
            }

            if (_vertices.Count != _indexedVertexCount)
            {
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

            // SetVertices 之后包围盒不会自动更新, 不重算会被视锥剔除, 表现为什么都不显示.
            _mesh.RecalculateBounds();
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
                return null;
            }

            _material = new Material(shader);
            _material.hideFlags = HideFlags.HideAndDontSave;
            _material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _material.SetInt("_Cull", (int)CullMode.Off);
            _material.SetInt("_ZWrite", 0);
            _material.SetInt("_ZTest", (int)CompareFunction.Always);
            _material.renderQueue = OverlayRenderQueue;
            return _material;
        }
    }

    // 一个待绘制的世界空间矩形.
    internal struct ObservationBox
    {
        internal Vector2 Center;

        internal Vector2 Size;

        internal Color Color;
    }
}
