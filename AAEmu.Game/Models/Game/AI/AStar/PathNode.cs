using System.Numerics;

using AAEmu.Game.Models.CryEngine.Entities;
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
                var result = BuildPath(cameFrom, currentNode);
                Position = result[0];
                CurrentTargetPos = result[0];
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

    private static List<Vector3> BuildPath(
        Dictionary<NodeDescriptor, NodeDescriptor> cameFrom,
        NodeDescriptor goalNode)
    {
        var nodes = new List<NodeDescriptor> { goalNode };
        var currentNode = goalNode;
        while (cameFrom.TryGetValue(currentNode, out var previousNode))
        {
            nodes.Add(previousNode);
            currentNode = previousNode;
        }

        nodes.Reverse();
        var result = new List<Vector3>(nodes.Count);
        foreach (var node in nodes)
        {
            if (result.Count == 0 ||
                Vector3.DistanceSquared(result[^1], node.Pos) > DuplicatePointToleranceSquared)
            {
                result.Add(node.Pos);
            }
        }

        return result;
    }
}
