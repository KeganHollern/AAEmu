using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.CryEngine.Physics;

namespace AAEmu.Game.Models.ClientData;

/// <summary>Keeps the native heights for fall contact and the established quantized terrain for other consumers.</summary>
internal sealed class TerrainHeightSector
{
    private const int Units = WorldManager.SECTOR_HMAP_RESOLUTION;
    private readonly ushort[] _native;
    private readonly float _offset;
    private readonly float _range;
    private readonly int _nativeSize;
    private readonly int _step;
    private readonly ushort[] _legacy;
    private readonly float _flatNative;
    private readonly ushort _flatLegacy;
    private readonly int _height;
    private readonly float _legacyBaseHeight;
    private readonly int _legacyStep;

    internal static bool SupportsNativeNode(NodeCell node) =>
        node.Version == 5 && node.nSize > 1 && Units % (node.nSize - 1) == 0 &&
        float.IsFinite(node.fOffset) && float.IsFinite(node.fRange) && node.fRange >= 0;

    internal TerrainHeightSector(NodeCell node, double coefficient, int width, int height, bool nativeFormat,
        out float minimum, out float maximum)
    {
        _nativeSize = node.nSize;
        _step = _nativeSize > 1 ? Units / (_nativeSize - 1) : Units;
        _offset = node.fOffset;
        _range = node.fRange;
        _legacyBaseHeight = node.LegacyBaseHeight;
        _legacyStep = node.LegacyStep;
        var packed = node.pHMData;
        var first = packed[0] & 0xffe0;
        _flatNative = _offset + first * _range;
        if (nativeFormat && _range != 0f && packed.Any(value => (value & 0xffe0) != first))
        {
            _native = packed;
            if (_step != 1)
                node.pHMData = (ushort[])packed.Clone();
        }

        var usePackedVertex = _step == 1 && _native != null;
        if (!usePackedVertex)
            node.RescaleForLegacyHeightMap();
        _height = height;
        _flatLegacy = usePackedVertex ? LegacyHeight(0, 0, coefficient) : (ushort)(node.GetHeightAtUnit(0, 0) * coefficient);
        ushort low = _flatLegacy, high = _flatLegacy;
        for (ushort x = 0; x < width; x++)
        for (ushort y = 0; y < height; y++)
        {
            var value = usePackedVertex ? LegacyHeight(x, y, coefficient) :
                (ushort)(node.GetHeightAtUnit(x, y) * coefficient);
            if (!usePackedVertex && value != _flatLegacy && _legacy == null)
            {
                _legacy = new ushort[width * height];
                Array.Fill(_legacy, _flatLegacy);
            }
            if (_legacy != null)
                _legacy[x * height + y] = value;
            low = Math.Min(low, value);
            high = Math.Max(high, value);
        }
        minimum = (float)(low / coefficient);
        maximum = (float)(high / coefficient);
    }

    internal ushort LegacyHeight(int x, int y, double coefficient)
    {
        // Full-resolution sectors keep one original vertex for both measurements.
        if (_native == null || _step != 1)
            return _legacy == null ? _flatLegacy : _legacy[x * _height + y];
        var raw = _native[x * _nativeSize + y];
        var bin = NodeCell.GetLegacyBin(raw, _offset, _range, _legacyStep);
        return (ushort)(NodeCell.DecodeLegacyHeight(bin, _legacyBaseHeight, _legacyStep) * coefficient);
    }

    internal float NativeHeight(int x, int y)
    {
        if (_native == null)
            return _flatNative;
        if (_step == 1)
            return _offset + (_native[x * _nativeSize + y] & 0xffe0) * _range;

        var sx = Math.Min(x / _step, _nativeSize - 2);
        var sy = Math.Min(y / _step, _nativeSize - 2);
        var fx = x / (float)_step - sx;
        var fy = y / (float)_step - sy;
        return CryTerrainGrid.InterpolateNativeHeight(_native, _nativeSize, sx, sy, fx, fy,
            _offset, _range);
    }
}
