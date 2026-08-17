using System.Collections.Generic;
using System.Numerics;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.CryEngine.Objects;
using Newtonsoft.Json;

namespace AAEmu.Game.Models.Game.World;

public class WaterBodies
{
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
    public float OceanLevel { get; set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.IgnoreAndPopulate)]
    public List<WaterBodyArea> Areas { get; set; } = [];

    [JsonIgnore] internal readonly object _lock = new();

    /// <summary>XY grid for <see cref="Areas"/> so river/lake queries above <see cref="OceanLevel"/> do not scan thousands of segments.</summary>
    private const float SpatialCellSize = 256f;

    /// <summary>Not readonly: hot reload can add this field to an existing instance (initializer does not run → null without lazy init).</summary>
    [JsonIgnore]
    private Dictionary<(int cx, int cy), List<uint>> _areaIndexByCell;

    /// <summary>When not equal to <see cref="Areas"/> count, spatial index is rebuilt on next query (ingest updates incrementally; tests/JSON may desync).</summary>
    [JsonIgnore]
    private int _indexedAreaCount;

    /// <summary>
    /// Static waterfall brushes are transition markers only. They must never participate in
    /// <see cref="IsWater"/> or <see cref="GetWaterSurface"/> as flat water volumes.
    /// </summary>
    [JsonIgnore]
    private List<WaterfallArea> _waterfalls = [];

    [JsonIgnore]
    private Dictionary<(int cx, int cy), List<uint>> _waterfallIndexByCell = new();

    // Max height (m) above world.xml sea: Cry Ocean rows with SurfaceHeight in this band are skipped (same open sea as IsWater for Z<=OceanLevel).
    private const float TemplateSeaDuplicateSurfaceMarginMeters = 1f;

    /// <summary>Skip water zones whose XY bbox area is below this (m²).</summary>
    public const float MinWaterBboxAreaSquareMeters = 5000f;

    /// <summary>
    /// Ingest threshold (m²) for instance (non-main) worlds. The 5000 m² cut exists to drop
    /// decorative-puddle noise, which is a main_world concern; dungeon pools are gameplay water
    /// (the Sharpwind Mines lake quad is ≈4420 m² and must survive ingest). aaemu-cluster#92 / #93.
    /// </summary>
    public const float MinInstanceWaterBboxAreaSquareMeters = 25f;

    /// <summary>Active ingest threshold for this world's water; set from <see cref="GetMinIngestBboxAreaSqm"/> at world init. aaemu-cluster#93.</summary>
    [JsonIgnore]
    public float MinIngestBboxAreaSquareMeters { get; set; } = MinWaterBboxAreaSquareMeters;

    /// <summary>Pure per-world ingest threshold decision (unit tested). aaemu-cluster#93.</summary>
    public static float GetMinIngestBboxAreaSqm(bool isMainWorld) =>
        isMainWorld ? MinWaterBboxAreaSquareMeters : MinInstanceWaterBboxAreaSquareMeters;

    internal static bool TryGetRiverLikePolygonMetrics(List<Vector3> points, out float lengthMeters,
        out float maxHalfWidthMeters, out float meanFullWidthMeters, out float areaSqm, out float aspect,
        out Vector2 principalAxisUnit)
    {
        lengthMeters = 0f;
        maxHalfWidthMeters = 0f;
        meanFullWidthMeters = 0f;
        areaSqm = 0f;
        aspect = 0f;
        principalAxisUnit = Vector2.UnitX;
        if (points is null || points.Count < 3)
            return false;

        // Ensure closed ring for metrics that need edges.
        var n = points.Count;
        if (n >= 2 && points[0] != points[^1])
        {
            points = new List<Vector3>(points);
            points.Add(points[0]);
            n = points.Count;
        }

        // Many callers already pass closed rings (last == first). Avoid double-counting the closing vertex in PCA metrics.
        var pcaCount = n >= 2 && points[0] == points[^1] ? n - 1 : n;

        // Area (shoelace) in XY.
        double sum = 0d;
        for (var i = 0; i + 1 < n; i++)
            sum += (double)points[i].X * points[i + 1].Y - (double)points[i + 1].X * points[i].Y;
        areaSqm = (float)Math.Abs(sum * 0.5d);
        if (areaSqm <= 1f)
            return false;

        // PCA axis in XY from covariance.
        var mean = GetMeanXY(points, pcaCount);
        GetCovarianceXY(points, pcaCount, mean, out var cxx, out var cxy, out var cyy);
        if (!float.IsFinite(cxx) || !float.IsFinite(cxy) || !float.IsFinite(cyy))
            return false;

        // principal eigenvector for 2x2 covariance
        var trace = cxx + cyy;
        var det = cxx * cyy - cxy * cxy;
        var disc = trace * trace - 4f * det;
        if (disc < 0f)
            disc = 0f;
        var s = MathF.Sqrt(disc);
        var lambda1 = 0.5f * (trace + s);
        var vx = cxy;
        var vy = lambda1 - cxx;
        if (MathF.Abs(vx) + MathF.Abs(vy) < 1e-12f)
        {
            vx = 1f;
            vy = 0f;
        }
        var v = Vector2.Normalize(new Vector2(vx, vy));
        principalAxisUnit = v;

        // Project points onto axis and its perpendicular to estimate length/width.
        var perp = new Vector2(-v.Y, v.X);
        var minT = float.PositiveInfinity;
        var maxT = float.NegativeInfinity;
        var minP = float.PositiveInfinity;
        var maxP = float.NegativeInfinity;
        for (var i = 0; i < points.Count; i++)
        {
            var d = new Vector2(points[i].X - mean.X, points[i].Y - mean.Y);
            var t = Vector2.Dot(d, v);
            var p = Vector2.Dot(d, perp);
            if (t < minT) minT = t;
            if (t > maxT) maxT = t;
            if (p < minP) minP = p;
            if (p > maxP) maxP = p;
        }

        lengthMeters = Math.Max(0f, maxT - minT);
        var fullWidth = Math.Max(0f, maxP - minP);
        maxHalfWidthMeters = fullWidth * 0.5f;
        meanFullWidthMeters = areaSqm / Math.Max(1e-3f, lengthMeters);
        aspect = lengthMeters / Math.Max(1e-3f, meanFullWidthMeters);
        return true;
    }

    private static Vector2 GetMeanXY(List<Vector3> points)
    {
        var n = points.Count;
        if (n >= 2 && points[0] == points[^1])
            n -= 1;
        return GetMeanXY(points, n);
    }

    private static Vector2 GetMeanXY(List<Vector3> points, int n)
    {
        double sx = 0d;
        double sy = 0d;
        for (var i = 0; i < n; i++)
        {
            sx += points[i].X;
            sy += points[i].Y;
        }

        var inv = 1f / n;
        return new Vector2((float)(sx * inv), (float)(sy * inv));
    }

    private static void GetCovarianceXY(List<Vector3> points, Vector2 mean, out float cxx, out float cxy, out float cyy)
    {
        var n = points.Count;
        if (n >= 2 && points[0] == points[^1])
            n -= 1;
        GetCovarianceXY(points, n, mean, out cxx, out cxy, out cyy);
    }

    private static void GetCovarianceXY(List<Vector3> points, int n, Vector2 mean, out float cxx, out float cxy, out float cyy)
    {
        double sxx = 0d;
        double sxy = 0d;
        double syy = 0d;
        for (var i = 0; i < n; i++)
        {
            var dx = points[i].X - mean.X;
            var dy = points[i].Y - mean.Y;
            sxx += dx * dx;
            sxy += dx * dy;
            syy += dy * dy;
        }

        var inv = 1f / Math.Max(1, n);
        cxx = (float)(sxx * inv);
        cxy = (float)(sxy * inv);
        cyy = (float)(syy * inv);
    }

    private bool IsWaterFootprintTooSmall(WaterBodyArea area)
    {
        var bboxArea = area.BoundingBox.Width * area.BoundingBox.Height;
        return bboxArea < MinIngestBboxAreaSquareMeters;
    }

    private void EnsureSpatialIndexUnderLock()
    {
        _areaIndexByCell ??= new();
        if (_indexedAreaCount == Areas.Count)
            return;
        _areaIndexByCell.Clear();
        foreach (var area in Areas)
            SpatialIndexAddUnderLock(area);
        _indexedAreaCount = Areas.Count;
    }

    /// <summary>Caller must hold <see cref="_lock"/>. Registers <paramref name="area"/> in every cell overlapped by its XY bbox.</summary>
    private void SpatialIndexAddUnderLock(WaterBodyArea area)
    {
        _areaIndexByCell ??= new();
        var id = area.Id;
        var bb = area.BoundingBox;
        var minCx = (int)MathF.Floor(bb.Left / SpatialCellSize);
        var maxCx = (int)MathF.Floor((bb.Left + bb.Width) / SpatialCellSize);
        var minCy = (int)MathF.Floor(bb.Top / SpatialCellSize);
        var maxCy = (int)MathF.Floor((bb.Top + bb.Height) / SpatialCellSize);

        for (var cx = minCx; cx <= maxCx; cx++)
        {
            for (var cy = minCy; cy <= maxCy; cy++)
            {
                var key = (cx, cy);
                if (!_areaIndexByCell.TryGetValue(key, out var list))
                {
                    list = [];
                    _areaIndexByCell[key] = list;
                }

                list.Add(id);
            }
        }
    }

    /// <summary>Caller must hold <see cref="_lock"/>.</summary>
    private void WaterfallSpatialIndexAddUnderLock(WaterfallArea waterfall)
    {
        _waterfallIndexByCell ??= new();
        var minCx = (int)MathF.Floor(waterfall.Min.X / SpatialCellSize);
        var maxCx = (int)MathF.Floor(waterfall.Max.X / SpatialCellSize);
        var minCy = (int)MathF.Floor(waterfall.Min.Y / SpatialCellSize);
        var maxCy = (int)MathF.Floor(waterfall.Max.Y / SpatialCellSize);

        for (var cx = minCx; cx <= maxCx; cx++)
        for (var cy = minCy; cy <= maxCy; cy++)
        {
            var key = (cx, cy);
            if (!_waterfallIndexByCell.TryGetValue(key, out var list))
            {
                list = [];
                _waterfallIndexByCell[key] = list;
            }

            list.Add(waterfall.Id);
        }
    }

    /// <summary>Clears ingested areas and the spatial index (e.g. <see cref="WorldInstance.ReloadWaterFromLoadedCells"/>).</summary>
    internal void ClearIngestedAreas()
    {
        lock (_lock)
        {
            Areas.Clear();
            _areaIndexByCell?.Clear();
            _indexedAreaCount = 0;
            _waterfalls ??= [];
            _waterfalls.Clear();
            _waterfallIndexByCell?.Clear();
        }
    }

    /// <summary>For tests or manual <see cref="Areas"/> edits outside <see cref="AddFromCellData"/>.</summary>
    internal void RebuildSpatialIndex()
    {
        lock (_lock)
        {
            _areaIndexByCell?.Clear();
            _indexedAreaCount = 0;
            EnsureSpatialIndexUnderLock();
        }
    }

    /// <summary>
    /// Returns a stable snapshot of <see cref="Areas"/> for debug/commands without exposing the internal lock.
    /// </summary>
    public List<WaterBodyArea> GetAreasSnapshot()
    {
        lock (_lock)
            return [.. Areas];
    }

    /// <summary>Returns a stable snapshot for diagnostics and tests.</summary>
    public List<WaterfallArea> GetWaterfallsSnapshot()
    {
        lock (_lock)
            return _waterfalls is null ? [] : [.. _waterfalls];
    }

    /// <summary>
    /// Finds waterfall transition brushes intersecting a world-space box. The spatial index keeps
    /// this query proportional to nearby scenery rather than all waterfall meshes in the world.
    /// </summary>
    internal List<WaterfallArea> GetWaterfallsIntersecting(
        float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
    {
        lock (_lock)
        {
            if (_waterfalls is null || _waterfalls.Count == 0 || _waterfallIndexByCell is null)
                return [];

            var minCx = (int)MathF.Floor(minX / SpatialCellSize);
            var maxCx = (int)MathF.Floor(maxX / SpatialCellSize);
            var minCy = (int)MathF.Floor(minY / SpatialCellSize);
            var maxCy = (int)MathF.Floor(maxY / SpatialCellSize);
            var seen = new HashSet<uint>();
            var result = new List<WaterfallArea>();

            for (var cx = minCx; cx <= maxCx; cx++)
            for (var cy = minCy; cy <= maxCy; cy++)
            {
                if (!_waterfallIndexByCell.TryGetValue((cx, cy), out var ids))
                    continue;
                foreach (var id in ids)
                {
                    if (!seen.Add(id))
                        continue;
                    var waterfall = _waterfalls[(int)id];
                    if (waterfall.Intersects(minX, minY, minZ, maxX, maxY, maxZ))
                        result.Add(waterfall);
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Finds the area whose XY bounding box is nearest to <paramref name="pos"/> (distance 0 when
    /// inside), or null when nothing is within <paramref name="maxDistance"/>. Used by
    /// DoodadFuncWaterVolume to locate the pool a valve doodad controls. aaemu-cluster#92 / #98.
    /// </summary>
    public WaterBodyArea GetNearestArea(Vector3 pos, float maxDistance)
    {
        lock (_lock)
        {
            WaterBodyArea best = null;
            var bestDist = maxDistance;
            foreach (var area in Areas)
            {
                var bb = area.BoundingBox;
                var dx = MathF.Max(MathF.Max(bb.Left - pos.X, 0f), pos.X - (bb.Left + bb.Width));
                var dy = MathF.Max(MathF.Max(bb.Top - pos.Y, 0f), pos.Y - (bb.Top + bb.Height));
                var d = MathF.Sqrt(dx * dx + dy * dy);
                if (d > bestDist)
                    continue;
                bestDist = d;
                best = area;
            }

            return best;
        }
    }

    /// <summary>
    /// Adds a synthetic square gameplay area (DoodadFuncWaterVolume with no ingested pool nearby):
    /// surface at center.Z. Deliberately bypasses the ingest footprint filter — this is explicit
    /// gameplay water, not cell noise. aaemu-cluster#92 / #98.
    /// </summary>
    public WaterBodyArea AddSquareArea(string name, Vector3 center, float sizeMeters, float depth)
    {
        var half = sizeMeters * 0.5f;
        var area = new WaterBodyArea(name, WaterBodyAreaType.Polygon) { Depth = depth };
        area.Points.Add(new Vector3(center.X - half, center.Y - half, center.Z));
        area.Points.Add(new Vector3(center.X + half, center.Y - half, center.Z));
        area.Points.Add(new Vector3(center.X + half, center.Y + half, center.Z));
        area.Points.Add(new Vector3(center.X - half, center.Y + half, center.Z));
        area.Points.Add(area.Points[0]);
        area.UpdateBounds();

        lock (_lock)
        {
            area.Id = (uint)Areas.Count;
            Areas.Add(area);
            SpatialIndexAddUnderLock(area);
            _indexedAreaCount = Areas.Count;
        }

        return area;
    }

    /// <summary>
    /// Shifts an area's surface by <paramref name="deltaZ"/>, thread-safe with the
    /// <see cref="IsWater(Vector3, out Vector3)"/>/<see cref="GetWaterSurface"/> readers. Depth grows by
    /// the same delta so the original bottom (surface − depth) stays wet while the surface rises.
    /// XY bounds are untouched, so the spatial index stays valid. aaemu-cluster#92 / #98.
    /// </summary>
    public bool RaiseAreaSurface(uint areaId, float deltaZ)
    {
        lock (_lock)
        {
            if (areaId >= Areas.Count)
                return false;

            var area = Areas[(int)areaId];
            for (var i = 0; i < area.Points.Count; i++)
                area.Points[i] = area.Points[i] with { Z = area.Points[i].Z + deltaZ };
            if (MathF.Abs(area.SurfacePlaneNormal.Z) > 1e-6f)
                area.SurfacePlaneD -= area.SurfacePlaneNormal.Z * deltaZ;
            area.Depth = MathF.Max(0f, area.Depth + deltaZ);
            area.UpdateBounds();
            return true;
        }
    }

    public bool IsWater(Vector3 point, out Vector3 flowDirection)
    {
        flowDirection = Vector3.Zero;

        lock (_lock)
        {
            EnsureSpatialIndexUnderLock();
            if (TrySelectAreaUnderLock(point, requireAtOrBelowSurface: true, out _, out flowDirection))
                return true;
        }

        flowDirection = Vector3.Zero;
        return point.Z <= OceanLevel;
    }

    public float GetWaterSurface(Vector3 point, out Vector3 flowDirection)
    {
        flowDirection = Vector3.Zero;

        lock (_lock)
        {
            EnsureSpatialIndexUnderLock();
            if (TrySelectAreaUnderLock(point, requireAtOrBelowSurface: false, out var surfacePoint,
                    out flowDirection))
                return surfacePoint.Z;
        }

        return OceanLevel;
    }

    /// <summary>
    /// Selects the smallest containing physical water area, matching CryPhysics overlap priority.
    /// Caller must hold <see cref="_lock"/> and have ensured the spatial index.
    /// </summary>
    private bool TrySelectAreaUnderLock(Vector3 point, bool requireAtOrBelowSurface, out Vector3 chosenSurface,
        out Vector3 chosenFlow)
    {
        chosenSurface = Vector3.Zero;
        chosenFlow = Vector3.Zero;

        var cx = (int)MathF.Floor(point.X / SpatialCellSize);
        var cy = (int)MathF.Floor(point.Y / SpatialCellSize);
        if (_areaIndexByCell == null || !_areaIndexByCell.TryGetValue((cx, cy), out var inCell))
            return false;

        var found = false;
        var smallestFootprint = float.PositiveInfinity;
        var nearestSurfaceDistance = float.PositiveInfinity;
        var chosenId = uint.MaxValue;

        foreach (var areaId in inCell)
        {
            var area = Areas[(int)areaId];
            if (!area.BoundingBox.Contains(point.X, point.Y) ||
                !area.GetSurface(point, out var surfacePoint, out var flow))
                continue;
            if (requireAtOrBelowSurface && point.Z > surfacePoint.Z)
                continue;
            if (point.Z < surfacePoint.Z - area.Depth)
                continue;

            var footprint = area.BoundingBox.Width * area.BoundingBox.Height;
            var surfaceDistance = MathF.Abs(surfacePoint.Z - point.Z);
            if (footprint > smallestFootprint ||
                (MathF.Abs(footprint - smallestFootprint) <= 1e-4f &&
                 (surfaceDistance > nearestSurfaceDistance ||
                  (MathF.Abs(surfaceDistance - nearestSurfaceDistance) <= 1e-4f && area.Id >= chosenId))))
                continue;

            found = true;
            smallestFootprint = footprint;
            nearestSurfaceDistance = surfaceDistance;
            chosenId = area.Id;
            chosenSurface = surfacePoint;
            chosenFlow = flow;
        }

        return found;
    }

    private static Vector3 WaterPointToWorld(Vector3 cellOffset, ObjectDataType11Water water, Vector3 filePoint)
    {
        const float localBand = WorldManager.CELL_SIZE * 2f;
        var xyCellLocal = filePoint.X <= localBand && filePoint.Y <= localBand &&
                          filePoint.X >= -512f && filePoint.Y >= -512f;
        var xy = xyCellLocal ? cellOffset + filePoint : filePoint;
        return xy with { Z = water.GetSurfaceHeight(filePoint.X, filePoint.Y) };
    }

    public void AddFromCellData(WorldCell worldCell)
    {
        if (worldCell == null)
            return;
        var cellOffset = worldCell.GetCellWorldOffset();

        var prefabIdx = 0;
        if (worldCell.LoadedObjectDat != null)
        {
            foreach (var prefab in worldCell.LoadedObjectDat.PrefabsList)
            {
                prefabIdx++;
                AddObjectDataFromWorldCell(prefab, cellOffset, worldCell, prefabIdx);
            }
        }

        // Gameplay water comes from object.dat only.
    }

    private void AddObjectDataFromWorldCell(ObjectDataBase prefab, Vector3 cellOffset, WorldCell worldCell, int prefabIdx)
    {
        if (prefab is ObjectDataType1Brush brush)
        {
            AddWaterfallBrush(brush, cellOffset, worldCell, prefabIdx);
            return;
        }
        if (prefab is ObjectDataType6Voxel)
            return;

        if (prefab is not ObjectDataType11Water water)
            return;

        switch (water.VolumeType)
        {
            case WaterObjectVolumeType.Area when water.PhysicsContourPointsList.Count >= 3:
                AddPolygonFromPhysicsContour(water, cellOffset,
                    $"Water_C{worldCell.CellX}-{worldCell.CellY}_{prefabIdx}");
                break;
            case WaterObjectVolumeType.River when water.PhysicsContourPointsList.Count >= 3:
                AddRiverFromPhysicsContour(water, cellOffset,
                    $"River_C{worldCell.CellX}-{worldCell.CellY}_{prefabIdx}");
                break;
            case WaterObjectVolumeType.Ocean
                when water.SurfaceHeight > worldCell.Template.OceanLevel + TemplateSeaDuplicateSurfaceMarginMeters &&
                     water.PhysicsContourPointsList.Count >= 3:
                AddPolygonFromPhysicsContour(water, cellOffset,
                    $"Ocean_C{worldCell.CellX}-{worldCell.CellY}_{prefabIdx}");
                break;
        }
    }

    private void AddWaterfallBrush(ObjectDataType1Brush brush, Vector3 cellOffset, WorldCell worldCell, int prefabIdx)
    {
        var assets = worldCell.LoadedObjectDat?.AssetPathsList;
        if (assets is null || brush.PathId < 0 || brush.PathId >= assets.Count)
            return;

        var assetPath = assets[brush.PathId]?.Name;
        if (string.IsNullOrWhiteSpace(assetPath) ||
            !assetPath.Contains("waterfall", StringComparison.OrdinalIgnoreCase))
            return;

        // Brush bounds in object.dat are cell-local. Preserve their full vertical extent: the top
        // aligns with the source river and the bottom identifies the receiving-water search band.
        var start = brush.StartPos + cellOffset;
        var end = brush.EndPos + cellOffset;
        var min = Vector3.Min(start, end);
        var max = Vector3.Max(start, end);
        if (!float.IsFinite(min.X) || !float.IsFinite(min.Y) || !float.IsFinite(min.Z) ||
            !float.IsFinite(max.X) || !float.IsFinite(max.Y) || !float.IsFinite(max.Z) ||
            max.X - min.X < 0.1f || max.Y - min.Y < 0.1f || max.Z - min.Z < 0.5f)
            return;

        lock (_lock)
        {
            _waterfalls ??= [];
            var waterfall = new WaterfallArea
            {
                Id = (uint)_waterfalls.Count,
                Name = $"Waterfall_C{worldCell.CellX}-{worldCell.CellY}_{prefabIdx}",
                AssetPath = assetPath,
                Min = min,
                Max = max
            };
            _waterfalls.Add(waterfall);
            WaterfallSpatialIndexAddUnderLock(waterfall);
        }
    }

    private void AddPolygonFromPhysicsContour(ObjectDataType11Water water, Vector3 cellOffset, string name)
    {
        var newLake = new WaterBodyArea(name, WaterBodyAreaType.Polygon)
        {
            Depth = water.Depth,
            FlowSpeedAbs = 0f,
            FlowSpeedSigned = 0f,
            Speed = 0f
        };
        foreach (var v3 in water.PhysicsContourPointsList)
        {
            var p = WaterPointToWorld(cellOffset, water, v3);
            if (!newLake.Points.Contains(p))
                newLake.Points.Add(p);
        }

        if (newLake.Points.Count < 3)
            return;

        SetWorldSurfacePlane(newLake, water.FogPlaneNormal);
        newLake.UpdateBounds();
        if (IsWaterFootprintTooSmall(newLake))
            return;

        RegisterArea(newLake);
    }

    private void AddRiverFromPhysicsContour(ObjectDataType11Water water, Vector3 cellOffset, string name)
    {
        var points = new List<Vector3>(water.PhysicsContourPointsList.Count);
        foreach (var point in water.PhysicsContourPointsList)
            points.Add(WaterPointToWorld(cellOffset, water, point));

        var river = new WaterBodyArea(name, WaterBodyAreaType.River)
        {
            Depth = water.Depth,
            Points = points,
            Speed = water.Speed,
            FlowSpeedAbs = MathF.Abs(water.Speed),
            FlowSpeedSigned = water.Speed
        };
        SetWorldSurfacePlane(river, water.FogPlaneNormal);
        river.FlowVelocity = GetNativeRiverFlowVelocity(water, cellOffset);
        var horizontalFlow = new Vector2(river.FlowVelocity.X, river.FlowVelocity.Y);
        if (horizontalFlow.LengthSquared() > 1e-12f)
            river.FlowAxis = Vector2.Normalize(horizontalFlow);
        river.InitializeNativeRiverFlow(water.Speed);
        river.UpdateBounds();

        // This contour is an explicit client physics area. Do not discard narrow/short river
        // sections using the decorative-puddle threshold used for ordinary area volumes.
        RegisterArea(river);
    }

    /// <summary>
    /// Fallback direction for malformed/legacy river contours. Valid native rivers use the
    /// per-vertex flow field initialized from their physics contour.
    /// </summary>
    private static Vector3 GetNativeRiverFlowVelocity(ObjectDataType11Water water, Vector3 cellOffset)
    {
        if (water.ShapePointsList.Count != 4 || water.Speed == 0f)
            return Vector3.Zero;

        var p0 = WaterPointToWorld(cellOffset, water, water.ShapePointsList[0]);
        var p1 = WaterPointToWorld(cellOffset, water, water.ShapePointsList[1]);
        var p2 = WaterPointToWorld(cellOffset, water, water.ShapePointsList[2]);
        var p3 = WaterPointToWorld(cellOffset, water, water.ShapePointsList[3]);
        var firstCrossSection = (p0 + p1) * 0.5f;
        var secondCrossSection = (p2 + p3) * 0.5f;
        return NormalizeOrZero(secondCrossSection - firstCrossSection) * water.Speed;
    }

    private static Vector3 NormalizeOrZero(Vector3 value)
    {
        var lengthSquared = value.LengthSquared();
        return lengthSquared > 1e-12f ? value / MathF.Sqrt(lengthSquared) : Vector3.Zero;
    }

    private static void SetWorldSurfacePlane(WaterBodyArea area, Vector3 normal)
    {
        if (area.Points.Count == 0 || MathF.Abs(normal.Z) <= 1e-6f)
            return;

        area.SurfacePlaneNormal = normal;
        area.SurfacePlaneD = -Vector3.Dot(normal, area.Points[0]);
    }

    private void RegisterArea(WaterBodyArea area)
    {
        lock (_lock)
        {
            area.Id = (uint)Areas.Count;
            Areas.Add(area);
            SpatialIndexAddUnderLock(area);
            _indexedAreaCount = Areas.Count;
        }
    }
}
