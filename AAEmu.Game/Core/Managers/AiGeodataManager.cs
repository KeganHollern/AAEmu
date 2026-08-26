using System.Numerics;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.CryEngine.Entities;
using AAEmu.Game.Models.CryEngine.Loaders;
using AAEmu.Game.Models.CryEngine.Mission;
using AAEmu.Game.Models.CryEngine.Readers;
using AAEmu.Game.Models.Game.AI.AStar;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Utils;

using NLog;

#pragma warning disable IDE0079 // Remove unnecessary suppression

namespace AAEmu.Game.Core.Managers;

// GeoData AiNavigation
public class AiGeoDataManager(WorldTemplate worldTemplate)
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    public IReadOnlyList<LinkDescriptor> GetAvailablePoints(NodeDescriptor point)
    {
        return point.NetMission.GetOutgoingLinks(point.Id);
    }

    #region A point in a polygon

    /// <summary>
    /// Checks if point is inside a forbidden zone area
    /// </summary>
    /// <param name="point"></param>
    /// <returns></returns>
    public bool CheckImpossibleWalk(Vector3 point)
    {
        return CheckImpossibleWalk(0, point);
    }

    public bool CheckImpossibleWalk(uint zoneKey, Vector3 point)
    {
        var bai = worldTemplate.GetBaiByPos(zoneKey, point);
        if (bai != null)
        {
            foreach (var areaMission in bai.AreasMissionReaders)
            {
                foreach (var forbiddenArea in EnumerateForbiddenAreas(areaMission))
                {
                    if (IsInPolygon(point, forbiddenArea.Points))
                        return true;
                }
            }
        }
        return false;
    }

    private static bool IsInPolygon(Vector3 point, List<Vector3> polygon)
    {
        if (polygon.Count < 3)
            return false;

        var result = false;
        var a = polygon.Last();
        foreach (var b in polygon)
        {
            if (b.X.Equals(point.X) && b.Y.Equals(point.Y))
                return true;

            if (b.Y.Equals(a.Y) && point.Y.Equals(a.Y))
            {
                if (a.X <= point.X && point.X <= b.X)
                    return true;

                if (b.X <= point.X && point.X <= a.X)
                    return true;
            }

            if (b.Y < point.Y && a.Y >= point.Y || a.Y < point.Y && b.Y >= point.Y)
            {
                if (b.X + (point.Y - b.Y) / (a.Y - b.Y) * (a.X - b.X) <= point.X)
                    result = !result;
            }
            a = b;
        }
        return result;
    }

    /// <summary>
    /// Get the center of the triangle (intersection of the medians)
    /// </summary>
    /// <param name="point1"></param>
    /// <param name="point2"></param>
    /// <param name="point3"></param>
    /// <returns></returns>
    public static Vector3 TriangleCenter(Vector3 point1, Vector3 point2, Vector3 point3)
    {
        var x = (point1.X + point2.X + point3.X) / 3;
        var y = (point1.Y + point2.Y + point3.Y) / 3;
        var z = (point1.Z + point2.Z + point3.Z) / 3;

        return new Vector3(x, y, z);
    }

    #endregion A point in a polygon

    #region Path smoothing

    // https://www.codeproject.com/Articles/18936/A-C-Implementation-of-Douglas-Peucker-Line-Appro
    public static List<Vector3> DouglasPeuckerReduction(List<Vector3> points, double tolerance)
    {
        if (points == null || points.Count < 3)
            return points;

        var firstPointIndex = 0;
        var lastPointIndex = points.Count - 1;
        var pointIndexesToKeep = new List<int>();

        //The first and the last point cannot be the same
        while (points[firstPointIndex].Equals(points[lastPointIndex]))
        {
            lastPointIndex--;
        }

        //Add the first and last index to the keepers
        pointIndexesToKeep.Add(firstPointIndex);
        pointIndexesToKeep.Add(lastPointIndex);

        DouglasPeuckerReduction(points, firstPointIndex, lastPointIndex, tolerance, ref pointIndexesToKeep);

        var returnPoints = new List<Vector3>();
        pointIndexesToKeep.Sort();
        foreach (var index in pointIndexesToKeep)
        {
            returnPoints.Add(points[index]);
        }

        return returnPoints;
    }

    /// <summary>
    /// Douglas-Peucker reduction.
    /// </summary>
    /// <param name="points">The points.</param>
    /// <param name="firstPointIndex">The first point.</param>
    /// <param name="lastPointIndex">The last point.</param>
    /// <param name="tolerance">The tolerance.</param>
    /// <param name="pointIndexesToKeep">The point index to keep.</param>
    private static void DouglasPeuckerReduction(List<Vector3> points, int firstPointIndex, int lastPointIndex, double tolerance, ref List<int> pointIndexesToKeep)
    {
        double maxDistance = 0;
        var indexFarthest = 0;

        if (lastPointIndex - firstPointIndex > 1) // ADDITION: need to have more than two points in the set we are looking through
        {
            for (var index = firstPointIndex; index < lastPointIndex; index++)
            {
                var distance = PerpendicularDistance(points[firstPointIndex], points[lastPointIndex], points[index]);
                if (distance > maxDistance)
                {
                    maxDistance = distance;
                    indexFarthest = index;
                }
            }

            if (maxDistance > tolerance && indexFarthest != firstPointIndex) // CHANGE: condition was wrong.
            {
                //Add the largest point that exceeds the tolerance
                pointIndexesToKeep.Add(indexFarthest);

                DouglasPeuckerReduction(points, firstPointIndex, indexFarthest, tolerance, ref pointIndexesToKeep);
                DouglasPeuckerReduction(points, indexFarthest, lastPointIndex, tolerance, ref pointIndexesToKeep);
            }
        }
    }

    /// <summary>
    /// The distance of a point from a line made from point1 and point2.
    /// </summary>
    /// <param name="point1">The point1.</param>
    /// <param name="point2">The point2.</param>
    /// <param name="targetPoint">The point.</param>
    /// <returns></returns>
    private static double PerpendicularDistance(Vector3 point1, Vector3 point2, Vector3 targetPoint)
    {
        //Area = |(1/2)(x1y2 + x2y3 + x3y1 - x2y1 - x3y2 - x1y3)|   *Area of triangle
        //Base = v((x1-x2)²+(x1-x2)²)                               *Base of Triangle*
        //Area = .5*Base*H                                          *Solve for height
        //Height = Area/.5/Base

        var area = Math.Abs(.5 * (point1.X * point2.Y + point2.X * targetPoint.Y + targetPoint.X * point1.Y - point2.X * point1.Y - targetPoint.X * point2.Y - point1.X * targetPoint.Y));
        var bottom = Math.Sqrt(Math.Pow(point1.X - point2.X, 2) + Math.Pow(point1.Y - point2.Y, 2));
        var height = area / bottom * 2;

        return height;
    }

    #endregion Path smoothing

    #region Finding the closest point

    public NodeDescriptor FindClosestToCurrent(uint zoneKey, Vector3 pos)
    {
        NodeDescriptor closestPointFound = null;
        var minDist = float.MaxValue;
        
        var (sourceCellX, sourceCellY) = pos.ToCellIndex();
        var cell = worldTemplate.GetCell(sourceCellX, sourceCellY);
        if (cell == null)
            return null;

        cell.VerifyCellLoaded();

        List<BaseBaiLoader> toCheckChunkList = [];
        if (cell.Template.ZoneBaiLoader.Count > 0)
        {
            var zoneBai = worldTemplate.GetBaiByPos(zoneKey, pos);
            if (zoneBai != null)
                toCheckChunkList.Add(zoneBai);
        }
        else
        {
            // If no zone defined (main_world), the use the 4x4 chunk grid of the cell
            foreach (var bai in cell.BaiLoader)
            {
                if (bai != null)
                    toCheckChunkList.Add(bai);
            }
        }

        // Check all eligible chunks
        foreach (var bLoader in toCheckChunkList)
        {
            if (bLoader == null)
                continue;
            foreach (var netMission in bLoader.NetMissionReaders)
            {
                foreach (var (_, nodeDescriptor) in netMission.NodeDescriptorList)
                {
                    var distance = (nodeDescriptor.Pos - pos).Length();
                    if (distance < minDist)
                    {
                        closestPointFound = nodeDescriptor;
                        minDist = distance;
                    }
                }
            }
        }

        // Logger.Warn($"# Found near position index: {index}...");
        return closestPointFound;
    }

    // Kept for scripts compiled against the original misspelled API.
    public NodeDescriptor FindСlosestToTheCurrent(uint zoneKey, Vector3 pos)
    {
        return FindClosestToCurrent(zoneKey, pos);
    }

    /// <summary>
    /// Gets height using navmesh data
    /// </summary>
    /// <param name="pos"></param>
    /// <returns></returns>
    public float GetHeight(Vector3 pos)
    {
        return TryGetHeight(pos, out var height) ? height : 0f;
    }

    /// <summary>
    /// Tries to get height using navigation data, falling back to the rendered terrain surface.
    /// </summary>
    public bool TryGetHeight(Vector3 pos, out float height)
    {
        return TryResolveLegacyHeight(pos, out height);
    }

    /// <summary>
    /// Tries to get a ground height. Outdoor triangular BAI nodes and their obstacle vertices describe
    /// coarse two-dimensional navigation topology, so rendered terrain is authoritative at the query XY.
    /// </summary>
    public bool TryGetGroundHeight(Vector3 pos, out float height)
    {
        if (TryGetGroundSurface(pos, out var surface))
        {
            height = surface.Height;
            return true;
        }

        height = 0f;
        return false;
    }

    /// <summary>
    /// Tries to resolve a ground surface and reports the selected source and its BAI reference, when any.
    /// </summary>
    public bool TryGetGroundSurface(Vector3 pos, out GroundSurfaceResult surface)
    {
        surface = default;
        if (!float.IsFinite(pos.X) || !float.IsFinite(pos.Y) || !float.IsFinite(pos.Z))
        {
            surface = new GroundSurfaceResult(0f, GroundSurfaceSource.None, GroundSurfaceDecision.None,
                GroundSurfaceFailure.InvalidPosition, null);
            return false;
        }

        try
        {
            if (!TryGetLegacyBaiSample(pos, out var sample))
            {
                if (worldTemplate.TryGetHeight(pos.X, pos.Y, out var terrainOnlyHeight))
                {
                    surface = new GroundSurfaceResult(terrainOnlyHeight, GroundSurfaceSource.Terrain,
                        GroundSurfaceDecision.TerrainOnly, GroundSurfaceFailure.None, null);
                    return true;
                }

                surface = new GroundSurfaceResult(0f, GroundSurfaceSource.None, GroundSurfaceDecision.None,
                    GroundSurfaceFailure.Unavailable, null);
                return false;
            }

            var baiReference = sample.ToSurfaceReference();
            if (sample.UsesTerrainForGround &&
                worldTemplate.TryGetHeight(pos.X, pos.Y, out var terrainHeight))
            {
                var decision = sample.Kind == BaiSurfaceReferenceKind.ObstacleVertex
                    ? GroundSurfaceDecision.ObstacleRejected
                    : GroundSurfaceDecision.OutdoorTriangulation;
                surface = new GroundSurfaceResult(terrainHeight, GroundSurfaceSource.Terrain, decision,
                    GroundSurfaceFailure.None, baiReference);
                return true;
            }

            if (float.IsFinite(sample.Position.Z))
            {
                var decision = sample.UsesTerrainForGround
                    ? GroundSurfaceDecision.TerrainUnavailableFallback
                    : GroundSurfaceDecision.NavigationHeightPreserved;
                surface = new GroundSurfaceResult(sample.Position.Z, sample.Source, decision,
                    GroundSurfaceFailure.None, baiReference);
                return true;
            }

            surface = new GroundSurfaceResult(0f, GroundSurfaceSource.None, GroundSurfaceDecision.None,
                GroundSurfaceFailure.InvalidSample, baiReference);
            return false;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, $"Failed to get geodata height at {pos} in world {worldTemplate?.Name ?? "unknown"}");
            surface = new GroundSurfaceResult(0f, GroundSurfaceSource.None, GroundSurfaceDecision.None,
                GroundSurfaceFailure.ResolverError, null);
            return false;
        }
    }

    private bool TryResolveLegacyHeight(Vector3 pos, out float height)
    {
        height = 0f;
        if (!float.IsFinite(pos.X) || !float.IsFinite(pos.Y) || !float.IsFinite(pos.Z))
            return false;

        //var stopWatch = new Stopwatch();
        //stopWatch.Start();
        try
        {
            if (!TryGetLegacyBaiSample(pos, out var sample))
                return worldTemplate.TryGetHeight(pos.X, pos.Y, out height);

            height = sample.Position.Z;
            if (float.IsFinite(height))
                return true;

            height = 0f;
            return false;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, $"Failed to get geodata height at {pos} in world {worldTemplate?.Name ?? "unknown"}");
            height = 0f;
            return false;
        }
        //stopWatch.Stop();
        //Logger.Info($"GetHeight took {stopWatch.Elapsed}");
    }

    private bool TryGetLegacyBaiSample(Vector3 pos, out BaiHeightSample sample)
    {
        sample = default;
        var closestDistance = float.MaxValue;
        var sampleFound = false;

        var bai = worldTemplate.GetBaiByPos(pos);
        if (bai == null)
            return false;

        if (bai.NetMissionReaders.Count > 0)
        {
            foreach (var netMission in bai.NetMissionReaders)
            {
                foreach (var (_, nodeDescriptor) in netMission.NodeDescriptorList)
                {
                    var distance = (nodeDescriptor.Pos - pos).Length();
                    if (distance < closestDistance)
                    {
                        closestDistance = distance;
                        sample = new BaiHeightSample(nodeDescriptor.Pos, BaiSurfaceReferenceKind.NavigationNode,
                            netMission.ZoneId, nodeDescriptor.Id, nodeDescriptor.NavigationType);
                        sampleFound = true;
                        if (closestDistance < 0.01f)
                            return true;
                    }
                }
            }
        }

        if (bai.VertexMissionReaders.Count > 0)
        {
            foreach (var vertexMission in bai.VertexMissionReaders)
            {
                foreach (var obstacleDataDescriptor in vertexMission.ObstacleDataDescriptorList)
                {
                    var distance = (obstacleDataDescriptor.Pos - pos).Length();
                    if (distance < closestDistance)
                    {
                        closestDistance = distance;
                        sample = new BaiHeightSample(obstacleDataDescriptor.Pos, BaiSurfaceReferenceKind.ObstacleVertex,
                            vertexMission.ZoneId, null, BaiNavigationType.Unset);
                        sampleFound = true;
                        if (closestDistance < 0.01f)
                            return true;
                    }
                }
            }
        }

        return sampleFound;
    }

    private readonly record struct BaiHeightSample(Vector3 Position, BaiSurfaceReferenceKind Kind, uint ZoneId,
        long? NodeId, BaiNavigationType NavigationType)
    {
        public GroundSurfaceSource Source => Kind == BaiSurfaceReferenceKind.NavigationNode
            ? GroundSurfaceSource.NavigationNode
            : GroundSurfaceSource.ObstacleVertex;

        public bool UsesTerrainForGround => Kind == BaiSurfaceReferenceKind.ObstacleVertex ||
                                            (NavigationType & BaiNavigationType.Triangular) != 0;

        public BaiSurfaceReference ToSurfaceReference() =>
            new(Kind, ZoneId, NodeId, NavigationType, Position);
    }

    private static float DistanceBetweenPoints(Vector3 point, Vector3 compareTo)
    {
        return (compareTo.X - point.X) * (compareTo.X - point.X) +
               (compareTo.Y - point.Y) * (compareTo.Y - point.Y);
    }

    private static Vector3 FindClosest(List<AiNavigation> searchIn, Vector3 compareTo)
    {
        return searchIn
            .Select(p => new { point = p.Position, distance = DistanceBetweenPoints(p.Position, compareTo) })
            .OrderBy(distances => distances.distance)
            .First().point;
    }

    private static Vector3 FindClosest(List<Vector3> searchIn, Vector3 compareTo)
    {
        return searchIn
            .Select(p => new { point = p, distance = DistanceBetweenPoints(p, compareTo) })
            .OrderBy(distances => distances.distance)
            .First().point;
    }

    /// <summary>
    /// Find the nearest point
    /// </summary>
    /// <param name="searchIn"></param>
    /// <param name="compareTo"></param>
    /// <returns>returns the index of the found point</returns>
    public static uint FindClosestIndexPoint(List<Vector3> searchIn, Vector3 compareTo)
    {
        var minDistance = 0f;
        var pointN = 0u;

        for (var i = 0; i < searchIn.Count; i++)
        {
            var distance = DistanceBetweenPoints(searchIn[i], compareTo);
            if (distance > minDistance)
                continue;

            pointN = (uint)i;
            minDistance = distance;
        }

        return pointN;
    }

    #endregion Finding the closest point

    public void Load()
    {
        // Nothing to load here anymore, everything
    }

    public Queue<Vector3> ReducePath(List<Vector3> foundPath, int maxNodeSkipCount, uint zoneKey = 0)
    {
        var res = new Queue<Vector3>();
        if (foundPath.Count == 0)
            return res;

        var startNodeIndex = 0;
        res.Enqueue(foundPath[startNodeIndex]);
        while (startNodeIndex < foundPath.Count - 1)
        {
            var selectedEndNodeIndex = startNodeIndex + 1;
            var furthestEndNodeIndex = Math.Min(foundPath.Count - 1,
                startNodeIndex + Math.Max(1, maxNodeSkipCount));
            for (var endNodeIndex = furthestEndNodeIndex; endNodeIndex > startNodeIndex + 1; endNodeIndex--)
            {
                if (CanSkipPathNodes(foundPath, startNodeIndex, endNodeIndex, zoneKey))
                {
                    selectedEndNodeIndex = endNodeIndex;
                    break;
                }
            }

            res.Enqueue(foundPath[selectedEndNodeIndex]);
            startNodeIndex = selectedEndNodeIndex;
        }

        return res;
    }

    private const float MaxPathCorridorDeviation = 2f;

    private bool CanSkipPathNodes(List<Vector3> path, int startNodeIndex, int endNodeIndex, uint zoneKey)
    {
        var startNode = path[startNodeIndex];
        var endNode = path[endNodeIndex];
        for (var nodeIndex = startNodeIndex + 1; nodeIndex < endNodeIndex; nodeIndex++)
        {
            if (DistanceToSegment(path[nodeIndex], startNode, endNode) > MaxPathCorridorDeviation)
                return false;
        }

        return !LinePassesThroughForbiddenArea(startNode, endNode, zoneKey);
    }

    private static float DistanceToSegment(Vector3 point, Vector3 segmentStart, Vector3 segmentEnd)
    {
        var segment = segmentEnd - segmentStart;
        var lengthSquared = segment.LengthSquared();
        if (lengthSquared <= float.Epsilon)
            return Vector3.Distance(point, segmentStart);

        var amount = Math.Clamp(Vector3.Dot(point - segmentStart, segment) / lengthSquared, 0f, 1f);
        return Vector3.Distance(point, segmentStart + segment * amount);
    }

    /// <summary>
    /// Checks if a line passes through at least one of the edges of a AiShape
    /// </summary>
    /// <param name="startPos"></param>
    /// <param name="endPos"></param>
    /// <param name="shape"></param>
    /// <param name="closedLoop">Is the shape a closed loop</param>
    /// <param name="maxHeightOffset">Maximum height difference required for the intersection to count as valid</param>
    /// <returns></returns>
    private bool LinePassesThroughAiShape(Vector3 startPos, Vector3 endPos, AiShape shape, bool closedLoop, float maxHeightOffset)
    {
        if (shape.Points.Count < 2)
            return false;

        for (var index = 0; index < shape.Points.Count + (closedLoop ? 0 : -1); index++)
        {
            var lineStart = shape.Points[index];
            var lineEnd = index < shape.Points.Count-1 ? shape.Points[index + 1] : shape.Points[0];
            var intersectionPoint = FindLineIntersection(startPos, endPos, lineStart, lineEnd); 
            if (intersectionPoint != Vector3.Zero)
            {
                if (maxHeightOffset == 0f || MathF.Abs(intersectionPoint.Z - startPos.Z) <= maxHeightOffset || MathF.Abs(intersectionPoint.Z - endPos.Z) <= maxHeightOffset)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Check if a given line passes any of the defined ForbiddenAreas nearby (in 2D space)
    /// </summary>
    /// <param name="startNode"></param>
    /// <param name="endNode"></param>
    /// <returns></returns>
    private bool LinePassesThroughForbiddenArea(Vector3 startNode, Vector3 endNode, uint zoneKey)
    {
        foreach (var bai in GetBaiLoadersAlongSegment(startNode, endNode, zoneKey))
        {
            foreach (var areaMission in bai.AreasMissionReaders)
            {
                foreach (var aiShape in EnumerateForbiddenAreas(areaMission))
                {
                    if (IsInPolygon(startNode, aiShape.Points) || IsInPolygon(endNode, aiShape.Points) ||
                        LinePassesThroughAiShape(startNode, endNode, aiShape, true, 8f))
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private HashSet<BaseBaiLoader> GetBaiLoadersAlongSegment(Vector3 startNode, Vector3 endNode, uint zoneKey)
    {
        var result = new HashSet<BaseBaiLoader>(ReferenceEqualityComparer.Instance);
        var horizontalDistance = Vector2.Distance(new Vector2(startNode.X, startNode.Y),
            new Vector2(endNode.X, endNode.Y));
        var sampleCount = Math.Max(1, (int)MathF.Ceiling(horizontalDistance / WorldManager.REGION_SIZE));
        for (var sampleIndex = 0; sampleIndex <= sampleCount; sampleIndex++)
        {
            var position = Vector3.Lerp(startNode, endNode, sampleIndex / (float)sampleCount);
            var bai = worldTemplate.GetBaiByPos(zoneKey, position);
            if (bai != null)
                result.Add(bai);
        }

        return result;
    }

    private static IEnumerable<AiShape> EnumerateForbiddenAreas(AreasMissionReader areaMission)
    {
        return areaMission.ForbiddenAreasList
            .Concat(areaMission.ForbiddenBoundariesList)
            .Concat(areaMission.DesignerForbiddenAreasList);
    }

    /// <summary>
    /// Checks if two lines intersect with given starting and ending point in 2D space (Z is ignored here)
    /// </summary>
    /// <param name="start1"></param>
    /// <param name="end1"></param>
    /// <param name="start2"></param>
    /// <param name="end2"></param>
    /// <returns>Returns the intersection point of line 1 in 2D space, or Zero if none was found</returns>
    /// <remarks>Based on the answer of https://stackoverflow.com/questions/1119451/how-to-tell-if-a-line-intersects-a-polygon-in-c#1120126</remarks>
    private static Vector3 FindLineIntersection(Vector3 start1, Vector3 end1, Vector3 start2, Vector3 end2)
    {
        var denominator = (end1.X - start1.X) * (end2.Y - start2.Y) - (end1.Y - start1.Y) * (end2.X - start2.X);

        // AB & CD are parallel 
        if (denominator == 0)
            return Vector3.Zero;

        var numerator1 = (start1.Y - start2.Y) * (end2.X - start2.X) - (start1.X - start2.X) * (end2.Y - start2.Y);
        var r = numerator1 / denominator;
        var numerator2 = (start1.Y - start2.Y) * (end1.X - start1.X) - (start1.X - start2.X) * (end1.Y - start1.Y);
        var s = numerator2 / denominator;

        if (r < 0 || r > 1 || s < 0 || s > 1)
            return Vector3.Zero;

        // Find intersection point
        return new Vector3(start1.X + r * (end1.X - start1.X), start1.Y + r * (end1.Y - start1.Y), start1.Z + r * (end1.Z - start1.Z));
    }

    /// <summary>
    /// Changes Z positions if they are above the floor
    /// </summary>
    /// <param name="pointsList"></param>
    /// <returns></returns>
    public List<Vector3> StickToFloor(List<Vector3> pointsList)
    {
        var res = new List<Vector3>();
        foreach (var point in pointsList)
        {
            var floor = worldTemplate.GetHeight(point.X, point.Y);
            if (floor < point.Z)
                res.Add(point with { Z = floor });
            else
                res.Add(point);
        }
        return res;
    }

    public Vector3 StickToFloor(Vector3 point)
    {
        var floor = worldTemplate.GetHeight(point.X, point.Y);
        if (floor < point.Z)
            return point with { Z = floor };
        return point;
    }
}
