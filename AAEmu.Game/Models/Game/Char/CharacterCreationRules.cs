using System.Text.Json;

using AAEmu.Game.Models.Game.Char.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Utils.DB;

using Microsoft.Data.Sqlite;

namespace AAEmu.Game.Models.Game.Char;

internal sealed class CharacterCreationRules
{
    private readonly Dictionary<uint, (uint Model, uint Asset)> _hairColors = [];
    private readonly Dictionary<uint, uint> _skinColors = [];
    private readonly Dictionary<uint, uint> _diffuseMaps = [];
    private readonly Dictionary<uint, uint> _normalMaps = [];
    private readonly Dictionary<uint, uint> _eyelashMaps = [];
    private readonly Dictionary<uint, (uint Model, bool Movable)> _decals = [];
    private readonly Dictionary<(uint Model, uint Asset), uint> _hairItems = [];
    private readonly HashSet<uint> _faceModels = [];
    private readonly Dictionary<uint, FaceSliderLimits> _faceLimits = [];

    private sealed record FaceSliderLimits(uint ModelId, sbyte[] Minimum, sbyte[] Maximum);

    internal static bool IsStartingAbility(AbilityType ability)
    {
        // Exact r208022 X2UI loginstage/common.alb ABILITY_TYPE.
        return ability is AbilityType.Fight or AbilityType.Magic or AbilityType.Wild or
            AbilityType.Love or AbilityType.Death or AbilityType.Vocation;
    }

    internal void Load(SqliteConnection connection)
    {
        Read("SELECT id, model_id, asset_id FROM hair_colors WHERE npc_only='f'", reader =>
            _hairColors.Add(reader.GetUInt32("id"), (reader.GetUInt32("model_id"), reader.GetUInt32("asset_id"))));
        ReadMap("skin_colors", _skinColors);
        ReadMap("face_diffuse_maps", _diffuseMaps);
        ReadMap("face_normal_maps", _normalMaps);
        ReadMap("face_eyelash_maps", _eyelashMaps);
        Read("SELECT id, model_id, movable FROM face_decal_assets WHERE npc_only='f'", reader =>
            _decals.Add(reader.GetUInt32("id"), (reader.GetUInt32("model_id"), reader.GetBoolean("movable", true))));
        Read("SELECT item_id, model_id, asset_id FROM item_body_parts WHERE slot_type_id=24 AND npc_only='f' AND beautyshop_only='f' ORDER BY id", reader =>
            _hairItems.TryAdd((reader.GetUInt32("model_id"), reader.GetUInt32("asset_id")), reader.GetUInt32("item_id")));
        using var faceLimits = typeof(CharacterCreationRules).Assembly.GetManifestResourceStream("AAEmu.Game.CharacterFaceSliderLimits.json");
        foreach (var limits in JsonSerializer.Deserialize<FaceSliderLimits[]>(faceLimits))
            _faceLimits.Add(limits.ModelId, limits);
        // Preset blends can retain values outside the interactive slider limits.
        Read("SELECT model_id, modifier FROM custom_face_presets WHERE length(modifier)=128 UNION ALL " +
             "SELECT model_id, modifier FROM total_character_customs WHERE owner_type_id=1 AND length(modifier)=128", reader =>
        {
            var modelId = reader.GetUInt32("model_id");
            if (!_faceLimits.TryGetValue(modelId, out var limits))
                return;
            _faceModels.Add(modelId);
            var modifier = (byte[])reader.GetValue("modifier");
            for (var i = 0; i < modifier.Length; i++)
            {
                var value = unchecked((sbyte)modifier[i]);
                limits.Minimum[i] = Math.Min(limits.Minimum[i], value);
                limits.Maximum[i] = Math.Max(limits.Maximum[i], value);
            }
        });
        return;

        void ReadMap(string table, Dictionary<uint, uint> values)
        {
            Read($"SELECT id, model_id FROM {table} WHERE npc_only='f'", reader =>
                values.Add(reader.GetUInt32("id"), reader.GetUInt32("model_id")));
        }

        void Read(string sql, Action<SQLiteWrapperReader> read)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = new SQLiteWrapperReader(command.ExecuteReader());
            while (reader.Read())
                read(reader);
        }
    }

    internal bool TrySelectBodyItems(CharacterTemplate template, UnitCustomModelParams model, out uint[] bodyItems)
    {
        bodyItems = null;
        if (template is not { Creatable: true } || model == null || model.Type > UnitCustomModelType.Face)
            return false;

        var selectedItems = (uint[])template.Items.Clone();
        if (model.Type >= UnitCustomModelType.Hair)
        {
            if (!_hairColors.TryGetValue(model.HairColorId, out var hair) || hair.Model != template.ModelId ||
                !_hairItems.TryGetValue((hair.Model, hair.Asset), out var hairItem))
                return false;
            selectedItems[1] = hairItem;
        }
        // The normal creation producer leaves custom parameter +0x0c at zero.
        // The legacy ModelId name does not mean characters.model_id on this wire.
        if (model.Type >= UnitCustomModelType.Skin &&
            (model.ModelId != 0 || !Matches(_skinColors, model.SkinColorId, template.ModelId, false)))
            return false;
        if (model.Type == UnitCustomModelType.Face && !IsValidFace(template.ModelId, model.Face))
            return false;

        bodyItems = selectedItems;
        return true;
    }

    private bool IsValidFace(uint modelId, FaceModel face)
    {
        if (face == null || !_faceModels.Contains(modelId) || face.Modifier is not { Length: 128 } ||
            !IsWeight(face.MovableDecalWeight) || !IsWeight(face.NormalMapWeight) ||
            !float.IsFinite(face.MovableDecalScale) || !float.IsFinite(face.MovableDecalRotate) ||
            !Matches(_diffuseMaps, face.DiffuseMapId, modelId) ||
            !Matches(_normalMaps, face.NormalMapId, modelId) ||
            !Matches(_eyelashMaps, face.EyelashMapId, modelId) ||
            !MatchesDecal(face.MovableDecalAssetId, modelId, true))
            return false;

        // X2UI's scar sliders produce 0.3 + 0.02 * [0,85], and degrees [0,360].
        // An absent scar can also use zero scale.
        if (face.MovableDecalScale > 2f || face.MovableDecalScale < 0 ||
            face.MovableDecalAssetId != 0 && face.MovableDecalScale < 0.3f ||
            face.MovableDecalRotate < 0 || face.MovableDecalRotate > 360)
            return false;

        foreach (var decal in face.FixedDecals)
        {
            if (!IsWeight(decal.AssetWeight) || !MatchesDecal(decal.AssetId, modelId, false))
                return false;
        }

        // Signed weights must fit this model's exact-client sliders and valid preset blends.
        var limits = _faceLimits[modelId];
        for (var i = 0; i < face.Modifier.Length; i++)
        {
            var value = unchecked((sbyte)face.Modifier[i]);
            if (value < limits.Minimum[i] || value > limits.Maximum[i])
                return false;
        }

        // The client uses packed RGBA colors, not table IDs. All channel bytes are valid.
        // Movable decal positions are native signed 16-bit texture coordinates.
        return true;
    }

    private bool MatchesDecal(uint id, uint modelId, bool movable)
    {
        return id == 0 || _decals.TryGetValue(id, out var decal) && decal.Model == modelId && decal.Movable == movable;
    }

    private static bool Matches(Dictionary<uint, uint> values, uint id, uint modelId, bool allowZero = true)
    {
        return allowZero && id == 0 || values.TryGetValue(id, out var allowedModel) && allowedModel == modelId;
    }

    private static bool IsWeight(float value)
    {
        return float.IsFinite(value) && value is >= 0 and <= 1;
    }
}
