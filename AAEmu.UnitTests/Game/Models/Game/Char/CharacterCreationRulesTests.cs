using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.C2G;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Char.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Models.Game.Char;

public class CharacterCreationRulesTests
{
    [Test]
    public async Task StartingAbilities_OnlyExactClientChoicesAreAllowed()
    {
        var expected = new HashSet<byte> { 1, 5, 6, 7, 8, 10 };
        for (var value = 0; value <= byte.MaxValue; value++)
            await Assert.That(CharacterCreationRules.IsStartingAbility((AbilityType)value)).IsEqualTo(expected.Contains((byte)value));
    }

    [Test]
    public async Task BodyItems_UseServerModelAndHairAssetWithoutChangingTemplate()
    {
        var rules = CreateRules();
        var template = Template();
        var model = Model();
        await Assert.That(rules.TrySelectBodyItems(template, model, out var items)).IsTrue();
        await Assert.That(items).IsEquivalentTo(new uint[] { 419, 24127, 0, 0, 0, 536, 0 });
        await Assert.That(template.Items[1]).IsEqualTo(700u);
    }

    [Test]
    [Arguments("noncreatable")]
    [Arguments("null")]
    [Arguments("type")]
    [Arguments("hair")]
    [Arguments("hairmodel")]
    [Arguments("npchair")]
    [Arguments("beautyshophair")]
    [Arguments("skin")]
    [Arguments("skinmodel")]
    [Arguments("npcskin")]
    [Arguments("model")]
    [Arguments("face")]
    [Arguments("diffuse")]
    [Arguments("normal")]
    [Arguments("normalmodel")]
    [Arguments("npcnormal")]
    [Arguments("eyelash")]
    [Arguments("movable")]
    [Arguments("movablemodel")]
    [Arguments("movablefixed")]
    [Arguments("fixed")]
    [Arguments("fixedmovable")]
    [Arguments("fixedweight")]
    [Arguments("normalweight")]
    [Arguments("scarweight")]
    [Arguments("scale")]
    [Arguments("scalenan")]
    [Arguments("rotate")]
    [Arguments("rotateinfinity")]
    [Arguments("modifierinactive")]
    [Arguments("modifierslider")]
    [Arguments("modifierlength")]
    [Arguments("modifierpositive")]
    [Arguments("modifiernegative")]
    public async Task Customization_InvalidFieldRejectsWholeSelection(string field)
    {
        var rules = CreateRules();
        var template = Template();
        var model = Model();
        switch (field)
        {
            case "noncreatable": template.Creatable = false; break;
            case "null": model = null; break;
            case "type": model.SetType((UnitCustomModelType)4); break;
            case "hair": model.SetHairColorId(uint.MaxValue); break;
            case "hairmodel": model.SetHairColorId(2); break;
            case "npchair": model.SetHairColorId(3); break;
            case "beautyshophair": model.SetHairColorId(4); break;
            case "skin": model.SetSkinColorId(uint.MaxValue); break;
            case "skinmodel": model.SetSkinColorId(2); break;
            case "npcskin": model.SetSkinColorId(3); break;
            case "model": model.SetModelId(11); break;
            case "face": model.SetFace(null); break;
            case "diffuse": model.Face.DiffuseMapId = 1; break;
            case "normal": model.Face.NormalMapId = uint.MaxValue; break;
            case "normalmodel": model.Face.NormalMapId = 2; break;
            case "npcnormal": model.Face.NormalMapId = 3; break;
            case "eyelash": model.Face.EyelashMapId = 1; break;
            case "movable": model.Face.MovableDecalAssetId = uint.MaxValue; break;
            case "movablemodel": model.Face.MovableDecalAssetId = 3; break;
            case "movablefixed": model.Face.MovableDecalAssetId = 2; break;
            case "fixed": model.Face.SetFixedDecalAsset(0, uint.MaxValue, 1); break;
            case "fixedmovable": model.Face.SetFixedDecalAsset(0, 1, 1); break;
            case "fixedweight": model.Face.SetFixedDecalAsset(0, 2, float.NaN); break;
            case "normalweight": model.Face.NormalMapWeight = float.PositiveInfinity; break;
            case "scarweight": model.Face.MovableDecalWeight = -0.01f; break;
            case "scale": model.Face.MovableDecalScale = 2.01f; break;
            case "scalenan": model.Face.MovableDecalScale = float.NaN; break;
            case "rotate": model.Face.MovableDecalRotate = 360.1f; break;
            case "rotateinfinity": model.Face.MovableDecalRotate = float.NegativeInfinity; break;
            case "modifierinactive": model.Face.Modifier[127] = 1; break;
            case "modifierslider": model.Face.Modifier[1] = 41; break;
            case "modifierlength": model.Face.Modifier = new byte[129]; break;
            case "modifierpositive": model.Face.Modifier[127] = 101; break;
            case "modifiernegative": model.Face.Modifier[0] = unchecked((byte)-101); break;
        }
        await Assert.That(rules.TrySelectBodyItems(template, model, out var items)).IsFalse();
        await Assert.That(items).IsNull();
    }

    [Test]
    public async Task Customization_CustomFaceWeightsAndPackedColorsDoNotNeedPresetEquality()
    {
        var model = Model();
        model.Face.Modifier[16] = unchecked((byte)-100);
        model.Face.Modifier[24] = 100;
        model.Face.LipColor = 0xAB123456;
        model.Face.MovableDecalMoveX = short.MinValue;
        model.Face.MovableDecalMoveY = short.MaxValue;
        model.Face.MovableDecalScale = 0.3f;
        model.Face.MovableDecalRotate = 360;
        model.Face.SetFixedDecalAsset(3, 2, 0.5f);
        await Assert.That(CreateRules().TrySelectBodyItems(Template(), model, out _)).IsTrue();
    }

    [Test]
    public async Task Customization_FaceLimitsFollowModelAndPermitPresetBlendsBeyondSliders()
    {
        var model = Model();
        model.Face.Modifier[60] = 100; // The real Nuian male login preset 303 exceeds the slider maximum of 60.
        await Assert.That(CreateRules().TrySelectBodyItems(Template(), model, out _)).IsFalse();
        var rules = CreateRules(model.Face.Modifier);
        model.Face.Modifier[60] = 80; // A blend with the preset is also a valid face.
        await Assert.That(rules.TrySelectBodyItems(Template(), model, out _)).IsTrue();
        model.Face.Modifier[60] = 101;
        await Assert.That(rules.TrySelectBodyItems(Template(), model, out _)).IsFalse();
    }

    [Test]
    public async Task CreatePacket_ConsumesButDoesNotUseClientLevelExtraAbilitiesOrItems()
    {
        var bytes = Body(Model());
        var stream = new PacketStream(bytes);
        await Assert.That(CSCreateCharacterPacket.TryReadRequest(stream, out var request)).IsTrue();
        await Assert.That(request.Name).IsEqualTo("Newchar");
        await Assert.That(request.Race).IsEqualTo(Race.Nuian);
        await Assert.That(request.Gender).IsEqualTo(Gender.Male);
        await Assert.That(request.Ability).IsEqualTo(AbilityType.Magic);
        await Assert.That(request.CustomModel.ModelId).IsEqualTo(0u);
        await Assert.That(stream.LeftBytes).IsEqualTo(0);
        await Assert.That(CreateRules().TrySelectBodyItems(Template(), request.CustomModel, out var body)).IsTrue();
        await Assert.That(body[1]).IsEqualTo(24127u);
    }

    [Test]
    public async Task CreatePacket_TruncationOrTrailingBytesNeverReturnsARequest()
    {
        var body = Body(Model());
        for (var length = 0; length < body.Length; length++)
        {
            await Assert.That(CSCreateCharacterPacket.TryReadRequest(new PacketStream(body[..length]), out var request)).IsFalse();
            await Assert.That(request).IsNull();
        }
        await Assert.That(CSCreateCharacterPacket.TryReadRequest(new PacketStream([.. body, 0]), out _)).IsFalse();
    }

    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(127)]
    [Arguments(129)]
    [Arguments(32767)]
    public async Task CreatePacket_InvalidModifierLengthRejectsBeforeAllocation(int length)
    {
        var body = Body(Model());
        var offset = body.Length - 8 - 128 - 2;
        body[offset] = (byte)length;
        body[offset + 1] = (byte)(length >> 8);
        await Assert.That(CSCreateCharacterPacket.TryReadRequest(new PacketStream(body), out _)).IsFalse();
    }

    [Test]
    [Arguments(-1)]
    [Arguments(129)]
    [Arguments(32767)]
    public async Task CreatePacket_NameLengthCannotExceedNativeBuffer(int length)
    {
        var body = Body(Model());
        body[0] = (byte)length;
        body[1] = (byte)(length >> 8);
        await Assert.That(CSCreateCharacterPacket.TryReadRequest(new PacketStream(body), out _)).IsFalse();
    }

    [Test]
    public async Task SlotAndFailurePackets_UseConfirmedOneByteBodies()
    {
        var slot = new SCGetSlotCountPacket(CharacterCreationSlots.ExpandedCharacterSlots).Write(new PacketStream());
        await Assert.That(slot.ReadByte()).IsEqualTo((byte)0);
        await Assert.That(slot.LeftBytes).IsEqualTo(0);
        await Assert.That(CharacterCreationSlots.MaximumCharacters).IsEqualTo(6);
        var failure = new SCCharacterCreationFailedPacket(CharacterCreateError.WorldCharacterLimit).Write(new PacketStream());
        await Assert.That(failure.ReadByte()).IsEqualTo((byte)9);
        await Assert.That(failure.LeftBytes).IsEqualTo(0);
    }

    private static byte[] Body(UnitCustomModelParams model)
    {
        var stream = new PacketStream();
        stream.Write("Newchar");
        stream.Write((byte)Race.Nuian);
        stream.Write((byte)Gender.Male);
        for (var i = 0; i < 7; i++)
            stream.Write(uint.MaxValue);
        model.Write(stream);
        stream.Write((byte)AbilityType.Magic);
        stream.Write((byte)AbilityType.Fight);
        stream.Write((byte)AbilityType.Love);
        stream.Write((byte)255);
        stream.Write(-1);
        return stream.GetBytes();
    }

    private static CharacterTemplate Template() => new()
    {
        ModelId = 10, Race = Race.Nuian, Gender = Gender.Male, Creatable = true,
        Items = [419, 700, 0, 0, 0, 536, 0]
    };

    private static UnitCustomModelParams Model()
    {
        var model = new UnitCustomModelParams(UnitCustomModelType.Face).SetModelId(0).SetHairColorId(1).SetSkinColorId(1);
        model.Face.NormalMapId = 1;
        model.Face.MovableDecalAssetId = 1;
        model.Face.MovableDecalScale = 1;
        model.Face.MovableDecalWeight = 1;
        model.Face.NormalMapWeight = 1;
        return model;
    }

    private static CharacterCreationRules CreateRules(byte[] loginPreset = null)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE hair_colors(id INTEGER, model_id INTEGER, asset_id INTEGER, npc_only TEXT);
            INSERT INTO hair_colors VALUES(1,10,10221,'f'),(2,11,10222,'f'),(3,10,10221,'t'),(4,10,10223,'f');
            CREATE TABLE skin_colors(id INTEGER, model_id INTEGER, npc_only TEXT);
            INSERT INTO skin_colors VALUES(1,10,'f'),(2,11,'f'),(3,10,'t');
            CREATE TABLE face_diffuse_maps(id INTEGER, model_id INTEGER, npc_only TEXT);
            CREATE TABLE face_eyelash_maps(id INTEGER, model_id INTEGER, npc_only TEXT);
            CREATE TABLE face_normal_maps(id INTEGER, model_id INTEGER, npc_only TEXT);
            INSERT INTO face_normal_maps VALUES(1,10,'f'),(2,11,'f'),(3,10,'t');
            CREATE TABLE face_decal_assets(id INTEGER, model_id INTEGER, movable TEXT, npc_only TEXT);
            INSERT INTO face_decal_assets VALUES(1,10,'t','f'),(2,10,'f','f'),(3,11,'t','f'),(4,10,'t','t');
            CREATE TABLE item_body_parts(id INTEGER,item_id INTEGER,slot_type_id INTEGER,model_id INTEGER,asset_id INTEGER,npc_only TEXT,beautyshop_only TEXT);
            INSERT INTO item_body_parts VALUES(1,24127,24,10,10221,'f','f'),(2,24128,24,11,10222,'f','f'),(3,24129,24,10,10223,'f','t');
            CREATE TABLE custom_face_presets(model_id INTEGER, modifier BLOB);
            INSERT INTO custom_face_presets VALUES(10,zeroblob(128)),(11,zeroblob(128));
            CREATE TABLE total_character_customs(model_id INTEGER, owner_type_id INTEGER, modifier BLOB);
            """;
        command.ExecuteNonQuery();
        if (loginPreset != null)
        {
            command.CommandText = "INSERT INTO total_character_customs VALUES(10,1,@modifier)";
            command.Parameters.AddWithValue("@modifier", loginPreset);
            command.ExecuteNonQuery();
        }
        var rules = new CharacterCreationRules();
        rules.Load(connection);
        return rules;
    }
}
