using System.Globalization;
using System.Xml.Linq;

namespace AAEmu.Game.Models.CryEngine.Physics;

/// <summary>The model and material references in an authored character definition.</summary>
public sealed record CryCharacterDefinition(string ModelPath, string MaterialPath)
{
    public static CryCharacterDefinition Read(System.IO.Stream stream)
    {
        var root = XDocument.Load(stream).Root;
        if (root?.Name != "CharacterDefinition")
            throw new InvalidDataException("Invalid character definition root.");
        var model = root.Element("Model");
        var path = (string)model?.Attribute("File");
        if (string.IsNullOrWhiteSpace(path) || root.Elements("Model").Count() != 1)
            throw new InvalidDataException("Character definition needs one model reference.");
        if (root.Element("AttachmentList")?.Elements().Any() == true)
            throw new NotSupportedException("Character attachments need their authored attachment physics.");
        if (root.Element("ShapeDeformation") is { } deformation)
            foreach (var component in deformation.Attributes())
                if (!float.TryParse(component.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value != 0)
                    throw new NotSupportedException("Character shape deformation needs its native bone adjustment.");
        return new CryCharacterDefinition(CryGeometryResolver.Normalize(path),
            CryGeometryResolver.Normalize((string)model.Attribute("Material") ?? ""));
    }
}
