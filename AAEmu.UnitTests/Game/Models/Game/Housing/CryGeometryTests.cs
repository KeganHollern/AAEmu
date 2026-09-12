using System.Numerics;

using AAEmu.Game.Models.CryEngine.Physics;

namespace AAEmu.UnitTests.Game.Models.Game.Housing;

public sealed class CryGeometryTests
{
    [Test]
    public async Task ReadMesh_ForeignTrianglesAndVertexMap_UseAuthoredExternalGeometry()
    {
        var bytes = Payload(1, writer =>
        {
            writer.Write(4);
            writer.Write(1);
            writer.Write(3);
            writer.Write(0x40);
            writer.Write(true);
            foreach (var index in new ushort[] { 0, 2, 1, 3 })
                writer.Write(index);
            writer.Write(true);
            writer.Write((ushort)1);
            writer.Write(new byte[20]);
        });
        var mesh = (CryTriangleMesh)CryPhysicsDataReader.Read(bytes,
            [Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ], [0, 1, 2, 1, 2, 3], [7, 11]);
        await Assert.That(mesh.Indices.SequenceEqual(new ushort[] { 2, 1, 3 })).IsTrue();
        await Assert.That(mesh.MaterialIds[0]).IsEqualTo((byte)11);
        await Assert.That(mesh.Vertices[3]).IsEqualTo(Vector3.UnitZ);
    }

    [Test]
    public async Task ReadMesh_FullSerialization_IgnoresForeignMapForGeometry()
    {
        var bytes = MeshPayload(0x400040, true, false);
        var mesh = (CryTriangleMesh)CryPhysicsDataReader.Read(bytes);
        await Assert.That(mesh.Indices.Length).IsEqualTo(3);
        await Assert.That(mesh.Vertices[1]).IsEqualTo(Vector3.UnitX);
        await Assert.That(mesh.MaterialIds[0]).IsEqualTo((byte)12);
    }

    [Test]
    public async Task ReadMesh_EmptyNativeBranch_HasNoCollisionTriangles()
    {
        var mesh = (CryTriangleMesh)CryPhysicsDataReader.Read(MeshPayload(0x40, false, true));
        await Assert.That(mesh.Indices.Length).IsEqualTo(0);
    }

    [Test]
    public async Task ReadMesh_TruncatedPayload_Throws()
    {
        var bytes = MeshPayload(0x40, false, false);
        await Assert.That(() => CryPhysicsDataReader.Read(bytes[..^1])).Throws<EndOfStreamException>();
    }

    [Test]
    public async Task ReadBox_NativeBasisAndDuplicateTree_ReadsShape()
    {
        var bytes = Payload(0, writer =>
        {
            foreach (var value in new float[] { 0, 1, 0, -1, 0, 0, 0, 0, 1 })
                writer.Write(value);
            writer.Write(1);
            WriteVector(writer, new Vector3(2, 3, 4));
            WriteVector(writer, new Vector3(5, 6, 7));
            writer.Write(new byte[68]);
        });
        var box = (CryBox)CryPhysicsDataReader.Read(bytes);
        await Assert.That(box.Center).IsEqualTo(new Vector3(2, 3, 4));
        await Assert.That(Vector3.TransformNormal(Vector3.UnitX, box.Orientation)).IsEqualTo(Vector3.UnitY);
        await Assert.That(box.SurfaceIndex).IsEqualTo(23);
    }

    [Test]
    [Arguments(4)]
    [Arguments(5)]
    [Arguments(6)]
    public async Task ReadCurvedPrimitive_PreservesNativeDimensions(int kind)
    {
        var shape = CryPhysicsDataReader.Read(Payload(kind, writer =>
        {
            WriteVector(writer, new Vector3(1, 2, 3));
            if (kind != 4)
                WriteVector(writer, Vector3.UnitY);
            writer.Write(2f);
            if (kind != 4)
            {
                writer.Write(4f);
                writer.Write(16);
            }
            writer.Write(new byte[68]);
        }));
        await Assert.That(shape.SurfaceIndex).IsEqualTo(23);
        if (shape is CryCylinder cylinder)
        {
            await Assert.That(cylinder.IsCapsule).IsEqualTo(kind == 6);
            await Assert.That(cylinder.HalfHeight).IsEqualTo(4f);
            await Assert.That(cylinder.Axis).IsEqualTo(Vector3.UnitY);
        }
    }

    [Test]
    public async Task Raycast_TransformedTriangle_ReturnsDistanceNormalAndMaterial()
    {
        var part = Part(new CryTriangleMesh([Vector3.Zero, Vector3.UnitX * 4, Vector3.UnitY * 4], [0, 1, 2], [13]));
        var transform = Matrix4x4.CreateScale(2) * Matrix4x4.CreateTranslation(10, 20, 30);
        await Assert.That(CryGeometryQueries.Raycast(part, transform, new Vector3(12, 22, 40), -Vector3.UnitZ,
            20, true, out var hit)).IsTrue();
        await Assert.That(hit.Distance).IsEqualTo(10f);
        await Assert.That(hit.Normal).IsEqualTo(Vector3.UnitZ);
        await Assert.That(hit.MaterialId).IsEqualTo(13);
        await Assert.That(CryGeometryQueries.Raycast(part, transform, new Vector3(12, 22, 20), Vector3.UnitZ,
            20, true, out _)).IsFalse();
        await Assert.That(CryGeometryQueries.Raycast(part, transform, new Vector3(12, 22, 20), Vector3.UnitZ,
            20, false, out _)).IsTrue();
    }

    [Test]
    [Arguments(0, 4f)]
    [Arguments(1, 4f)]
    [Arguments(2, 4f)]
    [Arguments(3, 3f)]
    public async Task Raycast_Primitives_ReturnsAuthoredSurface(int kind, float expected)
    {
        CryPhysicsShape shape = kind switch
        {
            0 => new CryBox(Vector3.Zero, Vector3.One, Matrix4x4.Identity),
            1 => new CrySphere(Vector3.Zero, 1),
            2 => new CryCylinder(Vector3.Zero, Vector3.UnitZ, 1, 1, false),
            _ => new CryCylinder(Vector3.Zero, Vector3.UnitZ, 1, 1, true)
        };
        await Assert.That(CryGeometryQueries.Raycast(Part(shape), Matrix4x4.Identity, new Vector3(0, 0, 5),
            -Vector3.UnitZ, 10, true, out var hit)).IsTrue();
        await Assert.That(hit.Distance).IsEqualTo(expected);
        await Assert.That(hit.Normal).IsEqualTo(Vector3.UnitZ);
    }

    [Test]
    public async Task IntersectBox_TriangleInteriorAndBoundsGap_UsesTriangles()
    {
        var part = Part(new CryTriangleMesh([Vector3.Zero, Vector3.UnitX * 10, Vector3.UnitY * 10], [0, 1, 2], []));
        var box = new CryBox(Vector3.Zero, new Vector3(0.25f), Matrix4x4.Identity);
        await Assert.That(CryGeometryQueries.IntersectBox(part, Matrix4x4.Identity, box,
            Matrix4x4.CreateTranslation(2, 2, 0))).IsEqualTo(CryIntersection.Intersects);
        await Assert.That(CryGeometryQueries.IntersectBox(part, Matrix4x4.Identity, box,
            Matrix4x4.CreateTranslation(8, 8, 0))).IsEqualTo(CryIntersection.Clear);
        await Assert.That(CryGeometryQueries.IntersectBox(part, Matrix4x4.Identity, box,
            Matrix4x4.CreateTranslation(2, 2, 1))).IsEqualTo(CryIntersection.Clear);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task IntersectBox_ClosedPrimitive_HandlesSeparationAndContainment(int kind)
    {
        CryPhysicsShape shape = kind switch
        {
            0 => new CryBox(Vector3.Zero, Vector3.One, Matrix4x4.Identity),
            1 => new CrySphere(Vector3.Zero, 1),
            2 => new CryCylinder(Vector3.Zero, Vector3.UnitZ, 1, 1, false),
            _ => new CryCylinder(Vector3.Zero, Vector3.UnitZ, 1, 1, true)
        };
        var box = new CryBox(Vector3.Zero, new Vector3(0.2f), Matrix4x4.Identity);
        await Assert.That(CryGeometryQueries.IntersectBox(Part(shape), Matrix4x4.Identity, box,
            Matrix4x4.Identity)).IsEqualTo(CryIntersection.Intersects);
        await Assert.That(CryGeometryQueries.IntersectBox(Part(shape), Matrix4x4.Identity, box,
            Matrix4x4.CreateTranslation(5, 0, 0))).IsEqualTo(CryIntersection.Clear);
        await Assert.That(CryGeometryQueries.IntersectBox(Part(shape), Matrix4x4.Identity, box,
            Matrix4x4.CreateTranslation(0.9f, 0, 0))).IsEqualTo(CryIntersection.Intersects);
    }

    [Test]
    public async Task IntersectBox_CylinderCornerGap_DoesNotUseItsBoundingBox()
    {
        var part = Part(new CryCylinder(Vector3.Zero, Vector3.UnitZ, 1, 1, false));
        var box = new CryBox(Vector3.Zero, new Vector3(0.01f), Matrix4x4.Identity);
        await Assert.That(CryGeometryQueries.IntersectBox(part, Matrix4x4.Identity, box,
            Matrix4x4.CreateTranslation(0.9f, 0.9f, 0))).IsEqualTo(CryIntersection.Clear);
    }

    [Test]
    public async Task IntersectBox_RotatedThinBox_DoesNotUseWorldBounds()
    {
        var part = Part(new CryBox(Vector3.Zero, new Vector3(5, 0.1f, 0.1f), Matrix4x4.Identity));
        var box = new CryBox(Vector3.Zero, new Vector3(0.1f), Matrix4x4.Identity);
        await Assert.That(CryGeometryQueries.IntersectBox(part, Matrix4x4.CreateRotationZ(MathF.PI / 4), box,
            Matrix4x4.CreateTranslation(3, -3, 0))).IsEqualTo(CryIntersection.Clear);
        await Assert.That(CryGeometryQueries.IntersectBox(part, Matrix4x4.CreateRotationZ(MathF.PI / 4), box,
            Matrix4x4.CreateTranslation(3, 3, 0))).IsEqualTo(CryIntersection.Intersects);
    }

    private static CryGeometryPart Part(CryPhysicsShape shape) => new(shape, Matrix4x4.Identity, 0x1000, "", "test");

    private static byte[] MeshPayload(int flags, bool foreign, bool empty) => Payload(1, writer =>
    {
        writer.Write(3);
        writer.Write(1);
        writer.Write(3);
        writer.Write(flags);
        writer.Write(false);
        writer.Write(foreign);
        if (foreign)
            writer.Write((ushort)99);
        WriteVector(writer, Vector3.Zero);
        WriteVector(writer, Vector3.UnitX);
        WriteVector(writer, Vector3.UnitY);
        foreach (var index in new ushort[] { 0, 1, 2 })
            writer.Write(index);
        writer.Write(true);
        writer.Write((byte)12);
        writer.Write(new byte[16]);
        writer.Write(empty ? 1 : 0);
    });

    private static byte[] Payload(int kind, Action<BinaryWriter> body)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(1);
        writer.Write(new byte[52]);
        writer.Write(23);
        writer.Write(new byte[8]);
        writer.Write(kind);
        body(writer);
        return stream.ToArray();
    }

    private static void WriteVector(BinaryWriter writer, Vector3 vector)
    {
        writer.Write(vector.X);
        writer.Write(vector.Y);
        writer.Write(vector.Z);
    }
}
