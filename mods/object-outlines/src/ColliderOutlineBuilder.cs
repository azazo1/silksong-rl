using System.Collections.Generic;
using UnityEngine;

namespace SilksongRL.ObjectOutlines
{
    // 把 Collider2D 转成世界坐标的线段, 顶点成对出现 (每两个顶点一条线).
    internal static class ColliderOutlineBuilder
    {
        private const int CircleSegments = 24;

        public static void AddCollider(Collider2D collider, List<Vector3> vertices)
        {
            if (collider == null || !collider.enabled)
            {
                return;
            }

            BoxCollider2D box = collider as BoxCollider2D;
            if (box != null)
            {
                AddRect(box.transform, box.offset, box.size, vertices);
                return;
            }

            CircleCollider2D circle = collider as CircleCollider2D;
            if (circle != null)
            {
                AddCircle(circle, vertices);
                return;
            }

            PolygonCollider2D polygon = collider as PolygonCollider2D;
            if (polygon != null)
            {
                AddPointPath(polygon.transform, polygon.points, polygon.offset, true, vertices);
                return;
            }

            EdgeCollider2D edge = collider as EdgeCollider2D;
            if (edge != null)
            {
                AddPointPath(edge.transform, edge.points, edge.offset, false, vertices);
                return;
            }

            // Capsule, Tilemap, Composite 等类型直接用世界包围盒近似.
            AddBounds(collider.bounds, vertices);
        }

        public static void AddBounds(Bounds bounds, List<Vector3> vertices)
        {
            float z = bounds.center.z;
            float minX = bounds.min.x;
            float maxX = bounds.max.x;
            float minY = bounds.min.y;
            float maxY = bounds.max.y;

            Vector3 a = new Vector3(minX, minY, z);
            Vector3 b = new Vector3(maxX, minY, z);
            Vector3 c = new Vector3(maxX, maxY, z);
            Vector3 d = new Vector3(minX, maxY, z);

            AddLine(vertices, a, b);
            AddLine(vertices, b, c);
            AddLine(vertices, c, d);
            AddLine(vertices, d, a);
        }

        private static void AddRect(Transform target, Vector2 offset, Vector2 size, List<Vector3> vertices)
        {
            float halfX = size.x * 0.5f;
            float halfY = size.y * 0.5f;

            Vector3 a = target.TransformPoint(new Vector3(offset.x - halfX, offset.y - halfY, 0f));
            Vector3 b = target.TransformPoint(new Vector3(offset.x + halfX, offset.y - halfY, 0f));
            Vector3 c = target.TransformPoint(new Vector3(offset.x + halfX, offset.y + halfY, 0f));
            Vector3 d = target.TransformPoint(new Vector3(offset.x - halfX, offset.y + halfY, 0f));

            AddLine(vertices, a, b);
            AddLine(vertices, b, c);
            AddLine(vertices, c, d);
            AddLine(vertices, d, a);
        }

        private static void AddCircle(CircleCollider2D circle, List<Vector3> vertices)
        {
            Transform target = circle.transform;
            Vector2 offset = circle.offset;
            float radius = circle.radius;

            Vector3 previous = PointOnCircle(target, offset, radius, 0f);
            for (int i = 1; i <= CircleSegments; i++)
            {
                float angle = (float)i / CircleSegments * Mathf.PI * 2f;
                Vector3 current = PointOnCircle(target, offset, radius, angle);
                AddLine(vertices, previous, current);
                previous = current;
            }
        }

        private static Vector3 PointOnCircle(Transform target, Vector2 offset, float radius, float angle)
        {
            float x = offset.x + Mathf.Cos(angle) * radius;
            float y = offset.y + Mathf.Sin(angle) * radius;
            return target.TransformPoint(new Vector3(x, y, 0f));
        }

        private static void AddPointPath(Transform target, Vector2[] points, Vector2 offset, bool closed, List<Vector3> vertices)
        {
            if (points == null || points.Length < 2)
            {
                return;
            }

            Vector3 first = ToWorld(target, points[0], offset);
            Vector3 previous = first;
            for (int i = 1; i < points.Length; i++)
            {
                Vector3 current = ToWorld(target, points[i], offset);
                AddLine(vertices, previous, current);
                previous = current;
            }

            if (closed)
            {
                AddLine(vertices, previous, first);
            }
        }

        private static Vector3 ToWorld(Transform target, Vector2 point, Vector2 offset)
        {
            return target.TransformPoint(new Vector3(point.x + offset.x, point.y + offset.y, 0f));
        }

        private static void AddLine(List<Vector3> vertices, Vector3 a, Vector3 b)
        {
            vertices.Add(a);
            vertices.Add(b);
        }
    }
}
