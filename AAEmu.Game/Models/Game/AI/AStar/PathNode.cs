using System.Numerics;

using AAEmu.Game.Models.CryEngine.Entities;
using AAEmu.Game.Models.CryEngine.Loaders;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Models.Game.AI.AStar;

/// <summary>
/// Reusable A* pathfinder over the client's BAI navigation graph.
/// </summary>
public class PathNode
{
    private const int MaxExpandedNodes = 50_000;
    private const float DuplicatePointToleranceSquared = 0.0001f;
    private const float BaiSeamNodeToleranceSquared = 0.01f;
    private const float MinimumEdgeCost = 0.001f;

    /// <summary>
    /// Current zone key.
    /// </summary>
    public uint ZoneKey { get; set; }

    /// <summary>
    /// Current route point used by GM/debug scripts.
    /// </summary>
    public Vector3 CurrentTargetPos { get; set; }

    /// <summary>
    /// Coordinates of the start point on the map.
    /// </summary>
    public Vector3 StartPointPos { get; set; } = Vector3.Zero;

    /// <summary>
    /// Actual requested endpoint. This remains the target's world position rather than its snapped BAI node.
    /// </summary>
    public Vector3 EndPointPos { get; set; } = Vector3.Zero;

    /// <summary>
    /// Route points currently being followed by the NPC.
    /// </summary>
    public Queue<Vector3> FoundPath { get; set; } = [];

    /// <summary>
    /// First point in the most recently calculated route, retained for GM/debug scripts.
    /// </summary>
    public Vector3 Position { get; set; }

    public bool LastSearchSucceeded { get; private set; }
    public bool LastPathUsesNavigationFunnel { get; private set; }
    public int LastExpandedNodeCount { get; private set; }

    /// <summary>
    /// Returns whether a combat route needs to be recalculated for the supplied target position.
    /// </summary>
    public bool NeedsPathRefresh(Vector3 targetPosition, float movementThreshold, bool routeRequired)
    {
        if (!LastSearchSucceeded)
            return true;

        var threshold = Math.Max(0.1f, movementThreshold);
        if (Vector3.DistanceSquared(EndPointPos, targetPosition) > threshold * threshold)
            return true;

        return routeRequired && FoundPath.Count == 0;
    }

    /// <summary>
    /// Finds the shortest traversable BAI route between two world positions.
    /// </summary>
    /// <param name="world">World containing the BAI graph.</param>
    /// <param name="start">Actual start position.</param>
    /// <param name="goal">Actual goal position.</param>
    /// <param name="agentRadius">Radius required to pass a BAI link. Zero disables radius filtering.</param>
    public List<Vector3> FindPath(WorldInstance world, Vector3 start, Vector3 goal, float agentRadius = 0f)
    {
        StartPointPos = start;
        EndPointPos = goal;
        LastSearchSucceeded = false;
        LastPathUsesNavigationFunnel = false;
        LastExpandedNodeCount = 0;
        CurrentTargetPos = Vector3.Zero;

        var geoData = world?.Template?.GeoData;
        if (geoData == null)
            return [];

        var startNode = geoData.FindClosestToCurrent(ZoneKey, start);
        var goalNode = geoData.FindClosestToCurrent(ZoneKey, goal);
        if (startNode == null || goalNode == null)
            return [];

        startNode = NormalizeBaiSeamNode(world.Template, startNode);
        goalNode = NormalizeBaiSeamNode(world.Template, goalNode);

        var frontier = new PriorityQueue<NodeDescriptor, float>();
        var pathCost = new Dictionary<NodeDescriptor, float>(ReferenceEqualityComparer.Instance)
        {
            [startNode] = 0f
        };
        var cameFrom = new Dictionary<NodeDescriptor, NodeDescriptor>(ReferenceEqualityComparer.Instance);
        var cameVia = new Dictionary<NodeDescriptor, LinkDescriptor>(ReferenceEqualityComparer.Instance);
        frontier.Enqueue(startNode, GetHeuristic(startNode, goalNode));

        while (frontier.TryDequeue(out var currentNode, out var queuedPriority))
        {
            if (!pathCost.TryGetValue(currentNode, out var currentPathCost))
                continue;

            var currentPriority = currentPathCost + GetHeuristic(currentNode, goalNode);
            if (queuedPriority > currentPriority + float.Epsilon)
                continue;

            LastExpandedNodeCount++;
            if (ReferenceEquals(currentNode, goalNode))
            {
                var result = BuildPath(world.Template, cameFrom, cameVia, currentNode, start, goal,
                    agentRadius, out var usesNavigationFunnel);
                Position = result[0];
                CurrentTargetPos = result[0];
                LastPathUsesNavigationFunnel = usesNavigationFunnel;
                LastSearchSucceeded = true;
                return result;
            }

            if (LastExpandedNodeCount >= MaxExpandedNodes)
                return [];

            foreach (var linkDescriptor in geoData.GetAvailablePoints(currentNode))
            {
                if (!CanTraverseLink(linkDescriptor, agentRadius))
                    continue;

                var targetNode = NormalizeBaiSeamNode(world.Template, linkDescriptor.TargetNodeDescriptor);
                if (geoData.CheckImpossibleWalk(ZoneKey, targetNode.Pos))
                    continue;

                var edgeCost = Math.Max(MinimumEdgeCost, Vector3.Distance(currentNode.Pos, targetNode.Pos));
                var candidatePathCost = currentPathCost + edgeCost;
                if (pathCost.TryGetValue(targetNode, out var knownPathCost) && candidatePathCost >= knownPathCost)
                    continue;

                cameFrom[targetNode] = currentNode;
                cameVia[targetNode] = linkDescriptor;
                pathCost[targetNode] = candidatePathCost;
                frontier.Enqueue(targetNode, candidatePathCost + GetHeuristic(targetNode, goalNode));
            }
        }

        return [];
    }

    private static bool CanTraverseLink(LinkDescriptor linkDescriptor, float agentRadius)
    {
        if (linkDescriptor.SourceNodeDescriptor == null || linkDescriptor.TargetNodeDescriptor == null)
            return false;

        return agentRadius <= 0f || linkDescriptor.MaxPassRadius <= 0d ||
               agentRadius <= linkDescriptor.MaxPassRadius;
    }

    /// <summary>
    /// Path tiles overlap at their edges. Resolve an overlapping target node to the node owned by the tile
    /// selected for its position, while refusing a distant nearest-node snap.
    /// </summary>
    private NodeDescriptor NormalizeBaiSeamNode(WorldTemplate worldTemplate, NodeDescriptor node)
    {
        var positionBai = worldTemplate.GetBaiByPos(ZoneKey, node.Pos);
        var positionNode = positionBai?.FindClosestNetMissionNode(node.Pos);
        return positionNode != null && Vector3.DistanceSquared(positionNode.Pos, node.Pos) <= BaiSeamNodeToleranceSquared
            ? positionNode
            : node;
    }

    private static float GetHeuristic(NodeDescriptor from, NodeDescriptor goal)
    {
        return Vector3.Distance(from.Pos, goal.Pos);
    }

    private List<Vector3> BuildPath(
        WorldTemplate worldTemplate,
        Dictionary<NodeDescriptor, NodeDescriptor> cameFrom,
        Dictionary<NodeDescriptor, LinkDescriptor> cameVia,
        NodeDescriptor goalNode,
        Vector3 start,
        Vector3 goal,
        float agentRadius,
        out bool usesNavigationFunnel)
    {
        var nodes = new List<NodeDescriptor> { goalNode };
        var links = new List<LinkDescriptor>();
        var currentNode = goalNode;
        while (cameFrom.TryGetValue(currentNode, out var previousNode))
        {
            links.Add(cameVia[currentNode]);
            nodes.Add(previousNode);
            currentNode = previousNode;
        }

        nodes.Reverse();
        links.Reverse();
        var portals = new List<Portal>();
        usesNavigationFunnel = nodes.All(node =>
            (node.NavigationType & BaiNavigationType.Triangular) != 0) &&
            NodeContainsPosition(worldTemplate, nodes[0], start) &&
            NodeContainsPosition(worldTemplate, nodes[^1], goal);
        if (usesNavigationFunnel)
            usesNavigationFunnel = TryCreatePortals(worldTemplate, links, agentRadius, out portals);
        if (usesNavigationFunnel)
            return PullString(start, goal, portals);

        var result = new List<Vector3>(nodes.Count + 2);
        AddUniquePoint(result, start);
        foreach (var node in nodes)
            AddUniquePoint(result, node.Pos);

        AddUniquePoint(result, goal);
        return result;
    }

    private bool NodeContainsPosition(WorldTemplate worldTemplate, NodeDescriptor node, Vector3 position)
    {
        var ownerBai = FindBaiOwner(worldTemplate, node, position, node.Pos);
        return ownerBai != null && ownerBai.ContainsPosition(node, position, out _);
    }

    private bool TryCreatePortals(WorldTemplate worldTemplate, List<LinkDescriptor> links,
        float agentRadius, out List<Portal> portals)
    {
        portals = new List<Portal>(links.Count);
        foreach (var link in links)
        {
            var ownerBai = FindBaiOwner(worldTemplate, link.SourceNodeDescriptor,
                link.SourceNodeDescriptor.Pos, link.TargetNodeDescriptor.Pos, link.EdgeCenter);
            if (ownerBai == null || !ownerBai.TryGetPortal(link, agentRadius, out var left, out var right))
            {
                portals.Clear();
                return false;
            }

            portals.Add(new Portal(left, right));
        }

        return true;
    }

    private BaseBaiLoader FindBaiOwner(WorldTemplate worldTemplate, NodeDescriptor node,
        params Vector3[] candidatePositions)
    {
        foreach (var position in candidatePositions)
        {
            var candidate = worldTemplate.GetBaiByPos(ZoneKey, position);
            if (candidate?.NetMissionReaders.Contains(node.NetMission) == true)
                return candidate;
        }

        return worldTemplate.ZoneBaiLoader.Values
            .Concat(worldTemplate.PathBaiLoader.Values)
            .FirstOrDefault(candidate => candidate.NetMissionReaders.Contains(node.NetMission));
    }

    private static List<Vector3> PullString(Vector3 start, Vector3 goal, IReadOnlyList<Portal> routePortals)
    {
        var portals = new List<Portal>(routePortals.Count + 2)
        {
            new(start, start)
        };
        portals.AddRange(routePortals);
        portals.Add(new Portal(goal, goal));

        var result = new List<Vector3> { start };
        var portalApex = start;
        var portalLeft = start;
        var portalRight = start;
        var apexIndex = 0;
        var leftIndex = 0;
        var rightIndex = 0;

        for (var portalIndex = 1; portalIndex < portals.Count; portalIndex++)
        {
            var left = portals[portalIndex].Left;
            var right = portals[portalIndex].Right;

            if (TriangleArea2(portalApex, portalRight, right) <= 0f)
            {
                if (SamePoint2D(portalApex, portalRight) ||
                    TriangleArea2(portalApex, portalLeft, right) > 0f)
                {
                    portalRight = right;
                    rightIndex = portalIndex;
                }
                else
                {
                    AddUniquePoint(result, portalLeft);
                    portalApex = portalLeft;
                    apexIndex = leftIndex;
                    portalLeft = portalApex;
                    portalRight = portalApex;
                    leftIndex = apexIndex;
                    rightIndex = apexIndex;
                    portalIndex = apexIndex;
                    continue;
                }
            }

            if (TriangleArea2(portalApex, portalLeft, left) >= 0f)
            {
                if (SamePoint2D(portalApex, portalLeft) ||
                    TriangleArea2(portalApex, portalRight, left) < 0f)
                {
                    portalLeft = left;
                    leftIndex = portalIndex;
                }
                else
                {
                    AddUniquePoint(result, portalRight);
                    portalApex = portalRight;
                    apexIndex = rightIndex;
                    portalLeft = portalApex;
                    portalRight = portalApex;
                    leftIndex = apexIndex;
                    rightIndex = apexIndex;
                    portalIndex = apexIndex;
                }
            }
        }

        AddUniquePoint(result, goal);
        return result;
    }

    private static float TriangleArea2(Vector3 first, Vector3 second, Vector3 third)
    {
        return (third.X - first.X) * (second.Y - first.Y) -
               (second.X - first.X) * (third.Y - first.Y);
    }

    private static bool SamePoint2D(Vector3 first, Vector3 second)
    {
        var x = first.X - second.X;
        var y = first.Y - second.Y;
        return x * x + y * y <= DuplicatePointToleranceSquared;
    }

    private static void AddUniquePoint(List<Vector3> points, Vector3 point)
    {
        if (points.Count == 0 || Vector3.DistanceSquared(points[^1], point) > DuplicatePointToleranceSquared)
            points.Add(point);
    }

    private readonly record struct Portal(Vector3 Left, Vector3 Right);
}
