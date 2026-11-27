using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AAEmu.Commons.Utils;
using AAEmu.Game.IO;
using AAEmu.Game.Models.CryEngine.Physics;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.Housing;
using Microsoft.Data.Sqlite;

const string archiveHash = "149962986700dca59573028097499b9b43d87495afe44c6c2fb4498cc6bfd25b";
const string compactHash = "636ca9ecfe777bc86542e4b828f930c7861d2b4639b1bcfff670c064fee9c1ac";
const string spawnManifestHash = "6c861a8e4e0f0dc66ee4944d38261e98a2e20977995fa7f935376f5bf5b31516";
const string sourceGame = "0742fe1d952fd1c2a7fc9bc1534f14e3235d7902";

if (args.Length != 4)
    throw new ArgumentException("Expected game_pak, server compact, Data/Worlds directory and a new output file.");

var (archive, compact, worlds, outputFile) = (args[0], args[1], args[2], args[3]);
CheckHash(archive, FileHash(archive), archiveHash);
CheckHash(compact, FileHash(compact), compactHash);
var spawnFiles = Directory.GetDirectories(worlds)
    .SelectMany(world => Directory.GetFiles(world, "doodad_spawns*.json"))
    .Order(StringComparer.Ordinal).ToArray();
var manifest = string.Concat(spawnFiles.Select(path =>
    FileHash(path) + "  " + Path.GetRelativePath(worlds, path).Replace('\\', '/') + "\n"));
CheckHash("spawn list", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))), spawnManifestHash);

var phasePaths = ReadPhasePaths(compact);
var scales = ReadPlacedScales(spawnFiles, phasePaths);
if (!ClientFileManager.AddSource(archive))
    throw new IOException("Game could not open the client archive.");
try
{
    var rows = new SortedDictionary<string, object[]>(StringComparer.Ordinal);
    var assets = new HousingGeometryAssets(ClientFileManager.GetFileStream, (_, _) => []);
    foreach (var (path, scale) in scales)
    {
        var archivePath = "game/" + path;
        if (!ClientFileManager.FileExists(archivePath))
            continue;

        var model = assets.Load("cgf://" + path);
        if (model.HasAnimatedCollision || model.PoseRequirements.Any(pose => pose.AffectsCollision))
            continue;
        var solids = model.Parts.Where(part => part.PhysicsType == 0x1000 &&
            part.Shape is CryBox or CryTriangleMesh or CryCylinder { IsCapsule: false });
        var boxes = solids.Select(part => CryGeometryQueries.GetBounds(part, Matrix4x4.Identity)).ToArray();
        if (boxes.Length == 0)
            continue;

        var bounds = boxes.Aggregate((first, second) => first.Union(second));
        // Rotation preserves distance from the origin. The largest stock scale covers every placed pose.
        var farCorner = Vector3.Max(Vector3.Abs(bounds.Min), Vector3.Abs(bounds.Max));
        if (farCorner.Length() * scale <= 2f)
            continue;

        using var raw = ClientFileManager.GetFileStream(archivePath);
        var hash = Convert.ToHexStringLower(SHA256.HashData(raw));
        rows.Add(path, [hash,
            LowerMm(bounds.Min.X), LowerMm(bounds.Min.Y), LowerMm(bounds.Min.Z),
            UpperMm(bounds.Max.X), UpperMm(bounds.Max.Y), UpperMm(bounds.Max.Z)]);
    }

    var bytes = JsonSerializer.SerializeToUtf8Bytes(new
    {
        schema = 1, archiveSha256 = archiveHash, serverCompactSha256 = compactHash,
        sourceGame, units = "millimetres", bounds = rows
    });
    using var output = new FileStream(outputFile, FileMode.CreateNew, FileAccess.Write);
    output.Write(bytes);
    Console.WriteLine($"{scales.Count} model paths, {rows.Count} bounds, {bytes.Length} bytes, SHA-256 {Convert.ToHexStringLower(SHA256.HashData(bytes))}");
}
finally
{
    ClientFileManager.ClearSources();
}

static Dictionary<uint, HashSet<string>> ReadPhasePaths(string path)
{
    var settings = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly };
    using var db = new SqliteConnection(settings.ToString());
    db.Open();
    using var query = db.CreateCommand();
    query.CommandText = """
        SELECT d.id, coalesce(nullif(g.model, ''), d.model)
        FROM doodad_almighties d JOIN doodad_func_groups g ON g.doodad_almighty_id = d.id
        WHERE d.model_kind_id = 1 AND d.load_model_from_world != 't'
          AND d.no_collision != 't' AND coalesce(d.growth_time, 0) = 0
          AND g.doodad_func_group_kind_id != 3
          AND NOT EXISTS (SELECT 1 FROM doodad_phase_funcs p WHERE p.doodad_func_group_id = g.id
                          AND p.actual_func_type != 'DoodadFuncTimer')
        """;
    using var reader = query.ExecuteReader();
    var paths = new Dictionary<uint, HashSet<string>>();
    while (reader.Read())
    {
        if (reader.IsDBNull(1))
            continue;
        var uri = CryGeometryResolver.Normalize(reader.GetString(1));
        if (!uri.StartsWith("cgf://", StringComparison.Ordinal) || !uri.EndsWith(".cgf", StringComparison.Ordinal))
            continue;
        var key = uri[6..].TrimStart('/');
        if (key.StartsWith("game/", StringComparison.Ordinal))
            key = key[5..];
        var id = reader.GetFieldValue<uint>(0);
        if (!paths.TryGetValue(id, out var models))
            paths.Add(id, models = new HashSet<string>(StringComparer.Ordinal));
        models.Add(key);
    }
    return paths;
}

static Dictionary<string, float> ReadPlacedScales(string[] files, Dictionary<uint, HashSet<string>> phasePaths)
{
    var scales = new Dictionary<string, float>(StringComparer.Ordinal);
    foreach (var world in files.GroupBy(Path.GetDirectoryName))
    {
        var positions = new Dictionary<uint, List<Vector3>>();
        foreach (var file in world.OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var spawns = JsonHelper.DeserializeObject<List<DoodadSpawner>>(File.ReadAllText(file));
            foreach (var spawn in spawns)
            {
                if (!phasePaths.TryGetValue(spawn.UnitId, out var keys))
                    continue;
                if (!positions.TryGetValue(spawn.UnitId, out var seen))
                    positions.Add(spawn.UnitId, seen = []);
                var at = new Vector3(spawn.Position.X, spawn.Position.Y, spawn.Position.Z);
                if (seen.Any(p => Math.Abs(p.X - at.X) < 0.01f && Math.Abs(p.Y - at.Y) < 0.01f && Math.Abs(p.Z - at.Z) < 0.01f))
                    continue;
                seen.Add(at);
                if (spawn.FollowNpc != 0 || !string.IsNullOrEmpty(spawn.FollowPath))
                    continue;
                var scale = spawn.Scale > 0 ? spawn.Scale : 1f;
                foreach (var key in keys)
                    scales[key] = Math.Max(scales.GetValueOrDefault(key), scale);
            }
        }
    }
    return scales;
}

// Use double arithmetic before rounding. A single-precision product can hide a fractional millimetre.
static int LowerMm(float coordinate) => checked((int)Math.Floor((double)coordinate * 1000));
static int UpperMm(float coordinate) => checked((int)Math.Ceiling((double)coordinate * 1000));

static string FileHash(string path)
{
    using var file = File.OpenRead(path);
    return Convert.ToHexStringLower(SHA256.HashData(file));
}

static void CheckHash(string input, string actual, string expected)
{
    if (actual != expected)
        throw new InvalidDataException($"Wrong SHA-256 for {input}. Expected {expected}, got {actual}.");
}
