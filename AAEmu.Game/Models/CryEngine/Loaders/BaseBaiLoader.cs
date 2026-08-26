using System.IO;
using System.Numerics;
using AAEmu.Commons.Exceptions;
using NLog;
using AAEmu.Game.IO;
using AAEmu.Game.Models.CryEngine.Entities;
using AAEmu.Game.Models.CryEngine.Readers;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Models.CryEngine.Loaders;

public class BaseBaiLoader(WorldTemplate parentWorldTemplate)
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
    private WorldTemplate ParentWorldTemplate { get; } = parentWorldTemplate;
    public List<AreasMissionReader> AreasMissionReaders { get; } = [];
    public List<NetMissionReader> NetMissionReaders { get; } = [];
    public List<VertexMissionReader> VertexMissionReaders { get; } = [];
    public List<NetMissionReader> HideMissionReaders { get; } = [];

    /// <summary>
    /// Loads .bai files data from a given zone or path folder
    /// </summary>
    /// <param name="zoneOrPathsFolder"></param>
    /// <param name="additiveLoad"></param>
    /// <exception cref="GameException"></exception>
    public void LoadBaiFilesFromFolder(string zoneOrPathsFolder, bool additiveLoad = false)
    {
        var worldFolder = Path.Combine("game", "worlds", ParentWorldTemplate.Name);

        if (!additiveLoad)
            ClearData();

        Logger.Debug($"LoadBaiFilesFromFolder {zoneOrPathsFolder}");
        try
        {
            // AreasMission*.bai
            var areaFiles = GetFiles("areasmission*.bai", zoneOrPathsFolder);
            foreach (var areaFile in areaFiles)
            {
                // Try to get zone key from folder name
                var areaFolderName = Path.GetFileName(Path.GetDirectoryName(areaFile)) ?? "";

                if (string.IsNullOrWhiteSpace(areaFolderName))
                    continue;

                // Skip file if it doesn't exist anymore for whatever reason
                if (!ClientFileManager.FileExists(areaFile))
                    continue;

                //LabelLoading.Text = $"Areas: {fileIndex}/{areaFiles.Length}";
                //LabelLoading.Refresh();

                var (zoneKey, pathBlockX, pathBlockY) = GetZoneAndOffsetsByName(areaFolderName);
                var targetOffset = GetTargetOffsetByZoneOrPath(zoneKey, pathBlockX, pathBlockY);

                // Logger.Debug($"Areas File: {areaFile}");

                // Load all .bai files for data
                var fileStream = ClientFileManager.GetFileStream(areaFile);
                // Ignore files that are too small or null streams
                if (fileStream == null || fileStream.Length <= 20)
                {
                    fileStream?.Dispose();
                    continue;
                }

                try
                {
                    var area = new AreasMissionReader(fileStream, zoneKey);
                    area.ReaderPointOffset = targetOffset;
                    area.ReadFile();
                    AreasMissionReaders.Add(area);
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Areas File Exception: {ex}, in {areaFile}, area offset {targetOffset}, skipping the rest of this file");
                }
                finally
                {
                    fileStream.Dispose();
                }
            }

            // NetMission*.bai
            var netFiles = GetFiles("netmission*.bai", zoneOrPathsFolder);
            foreach (var netFile in netFiles)
            {
                // Try to get zone key from folder name
                var netFolderName = Path.GetFileName(Path.GetDirectoryName(netFile)) ?? "";

                if (string.IsNullOrWhiteSpace(netFolderName))
                    continue;

                //LabelLoading.Text = $"Net: {fileIndex}/{netFiles.Length}";
                //LabelLoading.Refresh();

                var (zoneKey, pathBlockX, pathBlockY) = GetZoneAndOffsetsByName(netFolderName);
                var targetOffset = GetTargetOffsetByZoneOrPath(zoneKey, pathBlockX, pathBlockY);

                // Logger.Debug($"Net File: {netFile}");

                using var fs = ClientFileManager.GetFileStream(netFile);
                var net = new NetMissionReader(fs, zoneKey) { SourceFileName = netFile };
                try
                {
                    net.ReaderPointOffset = targetOffset;
                    net.ReadFile();
                    NetMissionReaders.Add(net);
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Net File Exception: {ex}, in {netFile}");
                    // continue;
                }
            }

            // VertexMission*.bai
            var vertexFiles = GetFiles("vertsmission*.bai", zoneOrPathsFolder);
            foreach (var vertexFile in vertexFiles)
            {
                // Try to get zone key from folder name
                var vertexFolderName = Path.GetFileName(Path.GetDirectoryName(vertexFile)) ?? "";

                if (string.IsNullOrWhiteSpace(vertexFolderName))
                    continue;

                //LabelLoading.Text = $"Vertex: {fileIndex}/{vertexFiles.Length}";
                //LabelLoading.Refresh();

                var (zoneKey, pathBlockX, pathBlockY) = GetZoneAndOffsetsByName(vertexFolderName);
                var targetOffset = GetTargetOffsetByZoneOrPath(zoneKey, pathBlockX, pathBlockY);

                // Logger.Debug($"Vertex File: {vertexFile}");

                var fileStream = ClientFileManager.GetFileStream(vertexFile);
                if (fileStream == null)
                    continue;

                try
                {
                    var vertex = new VertexMissionReader(fileStream, zoneKey) { SourceFileName = vertexFile };
                    vertex.ReaderPointOffset = targetOffset;
                    vertex.ReadFile();
                    VertexMissionReaders.Add(vertex);
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Vertex File Exception: {ex}, in {vertexFile}");
                }
                finally
                {
                    fileStream.Dispose();
                }
            }

            // HideMission*.bai
            var hideFiles = GetFiles("hidemission*.bai", zoneOrPathsFolder);
            foreach (var hideFile in hideFiles)
            {
                // Try to get zone key from folder name
                var hideFolderName = Path.GetFileName(Path.GetDirectoryName(hideFile)) ?? "";

                if (string.IsNullOrWhiteSpace(hideFolderName))
                    continue;

                //LabelLoading.Text = $"Hide: {fileIndex}/{hideFiles.Length}";
                //LabelLoading.Refresh();

                var (zoneKey, pathBlockX, pathBlockY) = GetZoneAndOffsetsByName(hideFolderName);
                var targetOffset = GetTargetOffsetByZoneOrPath(zoneKey, pathBlockX, pathBlockY);

                // Logger.Debug($"Hide File: {hideFile}");

                using var fs = ClientFileManager.GetFileStream(hideFile);
                var hide = new NetMissionReader(fs, zoneKey);
                try
                {
                    hide.ReaderPointOffset = targetOffset;
                    hide.ReadFile();
                    HideMissionReaders.Add(hide);
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Hide File Exception: {ex}, in {hideFile}");
                    // continue;
                }
            }

            //LabelLoading.Text = "Done Loading .bai";
        }
        catch (Exception ex)
        {
            Logger.Error(ex.Message);
            throw new GameException($"Exception loading files from {zoneOrPathsFolder}: {ex.Message}");
        }

        return;

        // ZoneKey,PathX, PathY 
        (uint, uint, uint) GetZoneAndOffsetsByName(string folderName)
        {
            var pathBlockX = 0u;
            var pathBlockY = 0u;
            if (folderName.Contains("_"))
            {
                // This is a path folder, not a zone folder
                var sectorSplit = folderName.Split("_");
                if (sectorSplit.Length == 2)
                {
                    if (!uint.TryParse(sectorSplit[0], out pathBlockX))
                        pathBlockX = 0u;
                    if (!uint.TryParse(sectorSplit[1], out pathBlockY))
                        pathBlockY = 0u;
                }
            }

            if (!uint.TryParse(folderName, out var zoneKey))
                zoneKey = 0u;
            return (zoneKey, pathBlockX, pathBlockY);
        }

        string[] GetFiles(string searchPattern, string forZones)
        {
            var rootFolder = worldFolder;

            if (!string.IsNullOrWhiteSpace(forZones))
            {
                rootFolder = Path.Combine(rootFolder, forZones.Contains('_') ? "paths" : "zone", forZones);
            }

            return ClientFileManager.GetFilesInDirectory(rootFolder, searchPattern, true).ToArray();
        }

        Vector3 GetTargetOffsetByZoneOrPath(uint zoneKey, uint pathBlockX, uint pathBlockY)
        {
            if (zoneKey == 0 || !ParentWorldTemplate.XmlWorld.Zones.TryGetValue(zoneKey, out var xmlWorldZone))
                return new Vector3(pathBlockX * 256f, pathBlockY * 256f, 0f);
            return new Vector3(xmlWorldZone.OriginX * 1024f, xmlWorldZone.OriginY * 1024f, 0f);
        }
    }

    private void ClearData()
    {
        // New
        // AreasMissionReader.UsedAreaNames.Clear();
        AreasMissionReaders.Clear();
        NetMissionReaders.Clear();
        VertexMissionReaders.Clear();
        HideMissionReaders.Clear();
    }

    public NodeDescriptor FindClosestNetMissionNode(Vector3 pos)
    {
        return FindClosestNetMissionNode(pos, out _, out _);
    }

    /// <summary>
    /// Finds the navigation node that owns a world point. Triangular navigation nodes are selected by
    /// polygon containment and surface height before falling back to nearest-center distance.
    /// </summary>
    public NodeDescriptor FindClosestNetMissionNode(Vector3 pos, out bool containsPosition, out float matchDistance)
    {
        NodeDescriptor nearestNode = null;
        var nearestDistance = float.MaxValue;
        NodeDescriptor containingNode = null;
        var containingSurfaceDistance = float.MaxValue;
        var containingCenterDistance = float.MaxValue;
        foreach (var netMissionReader in NetMissionReaders)
        {
            foreach (var (_, nodeDescriptor) in netMissionReader.NodeDescriptorList)
            {
                var centerDistance = Vector3.Distance(nodeDescriptor.Pos, pos);
                if (centerDistance < nearestDistance)
                {
                    nearestNode = nodeDescriptor;
                    nearestDistance = centerDistance;
                }

                if (!ContainsPosition(nodeDescriptor, pos, out var surfaceDistance))
                {
                    continue;
                }

                if (surfaceDistance < containingSurfaceDistance ||
                    MathF.Abs(surfaceDistance - containingSurfaceDistance) <= 0.001f &&
                    centerDistance < containingCenterDistance)
                {
                    containingNode = nodeDescriptor;
                    containingSurfaceDistance = surfaceDistance;
                    containingCenterDistance = centerDistance;
                }
            }
        }

        if (containingNode != null)
        {
            containsPosition = true;
            matchDistance = containingSurfaceDistance;
            return containingNode;
        }

        containsPosition = false;
        matchDistance = nearestDistance;
        return nearestNode;
    }

    public bool ContainsPosition(NodeDescriptor node, Vector3 position, out float surfaceDistance)
    {
        surfaceDistance = float.MaxValue;
        if (!TryGetTriangleVertices(node, out var first, out var second, out var third) ||
            !TryGetTriangleSurfaceHeight(position, first, second, third, out var surfaceHeight))
        {
            return false;
        }

        surfaceDistance = MathF.Abs(surfaceHeight - position.Z);
        return true;
    }

    public bool TryGetTriangleVertices(NodeDescriptor node, out Vector3 first, out Vector3 second,
        out Vector3 third)
    {
        first = Vector3.Zero;
        second = Vector3.Zero;
        third = Vector3.Zero;
        if (node == null || (node.NavigationType & BaiNavigationType.Triangular) == 0 ||
            node.Obstacle is not { Length: 3 })
        {
            return false;
        }

        var vertexReader = FindVertexReader(node.NetMission);
        if (vertexReader == null || node.Obstacle.Any(index =>
                index < 0 || index >= vertexReader.ObstacleDataDescriptorList.Count))
        {
            return false;
        }

        first = vertexReader.ObstacleDataDescriptorList[node.Obstacle[0]].Pos;
        second = vertexReader.ObstacleDataDescriptorList[node.Obstacle[1]].Pos;
        third = vertexReader.ObstacleDataDescriptorList[node.Obstacle[2]].Pos;
        return true;
    }

    public bool TryGetPortal(LinkDescriptor link, float agentRadius, out Vector3 left, out Vector3 right)
    {
        left = Vector3.Zero;
        right = Vector3.Zero;
        if (link?.SourceNodeDescriptor == null || link.TargetNodeDescriptor == null ||
            !ReferenceEquals(link.SourceNodeDescriptor.NetMission, link.TargetNodeDescriptor.NetMission) ||
            !NetMissionReaders.Contains(link.SourceNodeDescriptor.NetMission))
        {
            return false;
        }

        var vertexReader = FindVertexReader(link.SourceNodeDescriptor.NetMission);
        if (vertexReader == null)
            return false;

        var sharedVertexIndexes = link.SourceNodeDescriptor.Obstacle
            .Intersect(link.TargetNodeDescriptor.Obstacle)
            .Distinct()
            .Where(index => index >= 0 && index < vertexReader.ObstacleDataDescriptorList.Count)
            .Take(2)
            .ToArray();
        if (sharedVertexIndexes.Length != 2)
            return false;

        var first = vertexReader.ObstacleDataDescriptorList[sharedVertexIndexes[0]].Pos;
        var second = vertexReader.ObstacleDataDescriptorList[sharedVertexIndexes[1]].Pos;
        var portalCenter = Vector3.Lerp(first, second, 0.5f);
        var travelDirection = link.TargetNodeDescriptor.Pos - link.SourceNodeDescriptor.Pos;
        var firstOffset = first - portalCenter;
        var cross = travelDirection.X * firstOffset.Y - travelDirection.Y * firstOffset.X;
        left = cross >= 0f ? first : second;
        right = cross >= 0f ? second : first;

        var portalWidth = Vector2.Distance(new Vector2(left.X, left.Y), new Vector2(right.X, right.Y));
        var clearance = Math.Max(0f, agentRadius);
        if (clearance <= 0f)
            return true;

        if (portalWidth <= clearance * 2f || portalWidth <= float.Epsilon)
        {
            left = portalCenter;
            right = portalCenter;
            return true;
        }

        var amount = clearance / portalWidth;
        var originalLeft = left;
        left = Vector3.Lerp(originalLeft, right, amount);
        right = Vector3.Lerp(right, originalLeft, amount);
        return true;
    }

    private VertexMissionReader FindVertexReader(NetMissionReader netMissionReader)
    {
        if (VertexMissionReaders.Count == 1)
            return VertexMissionReaders[0];

        var netSuffix = GetMissionFileSuffix(netMissionReader.SourceFileName, "netmission");
        if (!string.IsNullOrEmpty(netSuffix))
        {
            var matchingReader = VertexMissionReaders.FirstOrDefault(reader =>
                string.Equals(GetMissionFileSuffix(reader.SourceFileName, "vertsmission"), netSuffix,
                    StringComparison.OrdinalIgnoreCase));
            if (matchingReader != null)
                return matchingReader;
        }

        var netReaderIndex = NetMissionReaders.IndexOf(netMissionReader);
        return netReaderIndex >= 0 && netReaderIndex < VertexMissionReaders.Count
            ? VertexMissionReaders[netReaderIndex]
            : null;
    }

    private static string GetMissionFileSuffix(string fileName, string prefix)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? name[prefix.Length..]
            : string.Empty;
    }

    private static bool TryGetTriangleSurfaceHeight(Vector3 point, Vector3 first, Vector3 second,
        Vector3 third, out float surfaceHeight)
    {
        surfaceHeight = 0f;
        var denominator = (second.Y - third.Y) * (first.X - third.X) +
                          (third.X - second.X) * (first.Y - third.Y);
        if (MathF.Abs(denominator) <= float.Epsilon)
            return false;

        var firstWeight = ((second.Y - third.Y) * (point.X - third.X) +
                           (third.X - second.X) * (point.Y - third.Y)) / denominator;
        var secondWeight = ((third.Y - first.Y) * (point.X - third.X) +
                            (first.X - third.X) * (point.Y - third.Y)) / denominator;
        var thirdWeight = 1f - firstWeight - secondWeight;
        const float containmentTolerance = 0.0001f;
        if (firstWeight < -containmentTolerance || secondWeight < -containmentTolerance ||
            thirdWeight < -containmentTolerance)
        {
            return false;
        }

        surfaceHeight = firstWeight * first.Z + secondWeight * second.Z + thirdWeight * third.Z;
        return true;
    }
}
