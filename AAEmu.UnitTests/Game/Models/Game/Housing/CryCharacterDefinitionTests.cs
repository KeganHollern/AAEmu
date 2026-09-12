using System.Text;
using System.Xml.Linq;

using AAEmu.Game.IO;
using AAEmu.Game.Models.CryEngine.Physics;

namespace AAEmu.UnitTests.Game.Models.Game.Housing;

public sealed class CryCharacterDefinitionTests
{
    [Test]
    public async Task Read_AuthoredModelAndMaterial_NormalizesAssetReferences()
    {
        using var stream = Xml("""
            <CharacterDefinition>
              <Model File="objects\Characters\animals\cat.chr" Material="objects/Characters/cat.mtl" />
              <ShapeDeformation COL0="0" COL1="0" COL2="0" COL3="0" COL4="0" COL5="0" COL6="0" COL7="0" />
            </CharacterDefinition>
            """);
        var definition = CryCharacterDefinition.Read(stream);
        await Assert.That(definition.ModelPath).IsEqualTo("objects/characters/animals/cat.chr");
        await Assert.That(definition.MaterialPath).IsEqualTo("objects/characters/cat.mtl");
    }

    [Test]
    [Arguments("<CharacterDefinition />")]
    [Arguments("<CharacterDefinition><Model File='a.chr'/><Model File='b.chr'/></CharacterDefinition>")]
    [Arguments("<Invalid><Model File='a.chr'/></Invalid>")]
    public async Task Read_MissingOrAmbiguousModel_Rejects(string xml)
    {
        using var stream = Xml(xml);
        await Assert.That(() => CryCharacterDefinition.Read(stream)).Throws<InvalidDataException>();
    }

    [Test]
    [Arguments("<AttachmentList><Attachment AName='physical-part' /></AttachmentList>")]
    [Arguments("<ShapeDeformation COL0='0.5' />")]
    [Arguments("<ShapeDeformation COL0='NaN' />")]
    public async Task Read_UnsampledGeometry_DoesNotReturnTheBaseModel(string element)
    {
        using var stream = Xml($"<CharacterDefinition><Model File='cat.chr' />{element}</CharacterDefinition>");
        await Assert.That(() => CryCharacterDefinition.Read(stream)).Throws<NotSupportedException>();
    }

    [Test]
    public async Task ExactClient_ShopPetDefinitions_UseTheirSkeletonAndMaterial()
    {
        var path = Environment.GetEnvironmentVariable("AAEMU_HOUSING_GAME_PAK");
        Skip.Unless(!string.IsNullOrEmpty(path), "Set AAEMU_HOUSING_GAME_PAK for the r208022 character definition check.");
        var source = new ClientSource { PathName = path, SourceType = ClientSourceType.GamePak };
        await Assert.That(source.Open()).IsTrue();
        try
        {
            Stream Open(string name) => source.FileExists(name) ? source.GetFileStream(name) : null;
            var resolver = new CryGeometryResolver(Open);
            using var file = Open("game/prefabs/doodad_animal.xml");
            var library = XDocument.Load(file);
            var names = new[] { "snowlion_baby.black_sell_normal", "snowlion_baby.white_sell_normal",
                "horse_baby.brown_sell_normal", "horse_baby.gray_sell_normal", "tare_baby.black_sell_normal",
                "tare_baby.white_sell_normal", "elk_baby.purple_sell_normal", "elk_baby.white_sell_normal",
                "wolf_baby.black_sell_normal", "wolf_baby.white_sell_normal", "cat_baby.stripes_sell_normal",
                "cat_baby.threecolors_sell_normal" };
            foreach (var name in names)
            {
                var prefab = library.Descendants("Prefab").Single(element => (string)element.Attribute("Name") == name);
                var modelPath = (string)prefab.Element("Objects").Elements("Object")
                    .Select(element => element.Element("Properties")).Single(element => element?.Attribute("object_Model") != null)
                    .Attribute("object_Model");
                using var definitionFile = Open(CryGeometryResolver.Normalize(modelPath));
                var definition = CryCharacterDefinition.Read(definitionFile);
                var character = resolver.Load(modelPath);
                var skeleton = resolver.Load(definition.ModelPath);
                await Assert.That(character.Parts.Count).IsGreaterThan(0);
                await Assert.That(character.CharacterBones.Count).IsEqualTo(skeleton.CharacterBones.Count);
                await Assert.That(character.Parts.Select(part => part.Shape).SequenceEqual(skeleton.Parts.Select(part => part.Shape))).IsTrue();
                await Assert.That(character.Parts.All(part => part.MaterialPath == "game/" + definition.MaterialPath)).IsTrue();
                await Assert.That(resolver.Load("prefab://prefabs/doodad_animal.xml/" + name).Parts.Count)
                    .IsGreaterThan(character.Parts.Count);
            }
        }
        finally { source.Close(); }
    }

    private static MemoryStream Xml(string xml) => new(Encoding.UTF8.GetBytes(xml));
}
