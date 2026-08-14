using AAEmu.ContentStudio.Core.Models;

namespace AAEmu.ContentStudio.Core.Services;

/// <summary>Friendly labels for compact values that are enums rather than record relationships.</summary>
public static class DesignerValueCatalog
{
    private static readonly Dictionary<string, IReadOnlyList<DesignerValueOption>> s_options =
        new Dictionary<string, IReadOnlyList<DesignerValueOption>>(StringComparer.OrdinalIgnoreCase)
        {
            ["damage_type_id"] = Options(("1", "Melee"), ("2", "Magic"), ("3", "Siege"), ("4", "Ranged"), ("5", "Healing")),
            ["target_relation_id"] = Options(("0", "Any relationship"), ("1", "Friendly"), ("2", "Party"), ("3", "Raid"), ("4", "Hostile"), ("5", "Other units")),
            ["target_selection_id"] = Options(("1", "Caster"), ("2", "Selected target"), ("3", "Line"), ("4", "Location")),
            ["target_type_id"] = Options(
                ("0", "Self"), ("1", "Friendly unit"), ("2", "Party member"), ("3", "Raid member"), ("4", "Hostile unit"),
                ("5", "Any unit"), ("6", "Position"), ("7", "Line"), ("8", "World object"), ("9", "Item"), ("10", "Pet"),
                ("11", "Ballistic position"), ("12", "Summon position"), ("13", "Relative position"), ("14", "Caster position"),
                ("15", "Artillery position"), ("16", "Other units"), ("17", "Other friendly units"), ("18", "Cursor position"), ("19", "Building")),
            ["application_method_id"] = Options(("1", "Apply to target"), ("2", "Apply to caster"), ("3", "Apply to caster once"), ("4", "Apply from caster to position")),
            ["unit_modifier_type_id"] = Options(("0", "Flat amount"), ("1", "Percentage")),
            ["unit_attribute_id"] = UnitAttributes()
        };

    public static bool TryGet(string field, out IReadOnlyList<DesignerValueOption> options) => s_options.TryGetValue(field, out options!);

    private static List<DesignerValueOption> Options(params (string Value, string Name)[] values) =>
        values.Select(value => new DesignerValueOption(value.Value, value.Name)).ToList();

    private static List<DesignerValueOption> UnitAttributes() => Options(
        ("0", "Strength"), ("1", "Agility"), ("2", "Stamina"), ("3", "Intelligence"), ("4", "Spirit"), ("5", "Faith"),
        ("6", "Maximum health"), ("7", "Maximum mana"), ("8", "Physical defense"), ("10", "Movement speed"),
        ("11", "Health regeneration"), ("12", "Mana regeneration"), ("13", "Facet count"),
        ("16", "Melee critical chance"), ("17", "Melee critical bonus"), ("18", "Melee accuracy"), ("20", "Melee evasion"),
        ("21", "Melee block"), ("22", "Melee parry"), ("23", "Ranged accuracy"), ("25", "Ranged critical chance"),
        ("26", "Ranged critical bonus"), ("28", "Magic accuracy"), ("30", "Magic critical chance"), ("31", "Magic critical bonus"),
        ("33", "Melee damage"), ("34", "Ranged damage"), ("35", "Magic damage"), ("36", "Main-hand speed"),
        ("37", "Minimum main-hand damage"), ("38", "Maximum main-hand damage"), ("41", "Off-hand speed"),
        ("42", "Minimum off-hand damage"), ("43", "Maximum off-hand damage"), ("46", "Ranged weapon speed"),
        ("47", "Minimum ranged damage"), ("48", "Maximum ranged damage"), ("51", "Melee damage multiplier"),
        ("52", "Ranged damage multiplier"), ("53", "Magic damage multiplier"), ("54", "Melee speed multiplier"),
        ("55", "Ranged speed multiplier"), ("56", "Healing received"), ("57", "Defense penetration"),
        ("58", "Incoming damage"), ("59", "Ranged evasion"), ("60", "Ranged block"), ("61", "Aggro range"),
        ("62", "Incoming aggro"), ("63", "Hovering"), ("64", "Magic defense"), ("65", "Magic stability"),
        ("66", "Swimming speed"), ("67", "Persistent health regeneration"), ("68", "Persistent mana regeneration"),
        ("69", "Armor type"), ("70", "Armor coverage"), ("71", "Casting time"), ("72", "Turning speed"),
        ("73", "Gravity"), ("74", "Global cooldown"), ("75", "Two-handed speed"), ("77", "Melee critical multiplier"),
        ("78", "Melee accuracy multiplier"), ("79", "Melee evasion multiplier"), ("80", "Melee block multiplier"),
        ("81", "Melee parry multiplier"), ("82", "Ranged critical multiplier"), ("83", "Ranged accuracy multiplier"),
        ("84", "Ranged evasion multiplier"), ("85", "Ranged block multiplier"), ("86", "Magic critical multiplier"),
        ("87", "Magic DPS"), ("88", "Magic accuracy multiplier"), ("89", "Casting tolerance"), ("90", "Aggro multiplier"),
        ("91", "Breath capacity"), ("92", "Fall damage"), ("93", "Climbing speed"), ("94", "Stealth detection range"),
        ("95", "Experience gain"), ("96", "Main-hand DPS"), ("97", "Off-hand DPS"), ("98", "Ranged DPS"),
        ("99", "Alchemy proficiency"), ("100", "Construction proficiency"), ("101", "Cooking proficiency"),
        ("102", "Handicrafts proficiency"), ("103", "Husbandry proficiency"), ("104", "Farming proficiency"),
        ("105", "Fishing proficiency"), ("106", "Logging proficiency"), ("107", "Gathering proficiency"),
        ("108", "Machining proficiency"), ("109", "Metalwork proficiency"), ("110", "Printing proficiency"),
        ("111", "Mining proficiency"), ("112", "Masonry proficiency"), ("113", "Tailoring proficiency"),
        ("114", "Leatherwork proficiency"), ("115", "Weaponry proficiency"), ("116", "Carpentry proficiency"),
        ("117", "Larceny proficiency"), ("118", "Commerce proficiency"), ("119", "Attack animation speed"),
        ("120", "Healing power"), ("121", "Back-attack melee damage"), ("122", "Back-attack ranged damage"),
        ("123", "Back-attack magic damage"), ("124", "Friction"), ("125", "Honor loss"),
        ("126", "Battlefield honor gain"), ("127", "Battlefield honor multiplier"), ("128", "NPC-kill honor gain"),
        ("129", "NPC-kill honor multiplier"), ("130", "Trial honor gain"), ("131", "Trial honor multiplier"),
        ("132", "War honor gain"), ("133", "War honor multiplier"), ("134", "Quest honor gain"),
        ("135", "Quest honor multiplier"), ("136", "Vocation gain"), ("137", "Vocation gain multiplier"),
        ("138", "Composition proficiency"), ("139", "Underwater swimming speed"), ("140", "Item drop rate"),
        ("141", "Gold drop rate"), ("142", "Incoming melee damage reduction"), ("143", "Incoming melee damage multiplier"),
        ("144", "Incoming ranged damage reduction"), ("145", "Incoming ranged damage multiplier"),
        ("146", "Incoming magic damage reduction"), ("147", "Incoming magic damage multiplier"),
        ("148", "Incoming siege damage reduction"), ("149", "Incoming siege damage multiplier"),
        ("150", "Magic damage critical chance"), ("151", "Magic damage critical multiplier"),
        ("152", "Magic damage critical bonus"), ("153", "Ranged parry"), ("154", "Ranged parry multiplier"),
        ("173", "Healing DPS"), ("174", "Healing critical chance"), ("175", "Healing power bonus"),
        ("176", "Healing critical bonus"), ("177", "Block chance"), ("178", "Evasion"), ("179", "Block multiplier"),
        ("180", "Evasion multiplier"), ("181", "Bull's Eye"), ("182", "Battle resistance"), ("183", "Resilience"),
        ("184", "Magic penetration"), ("185", "Healing critical multiplier"), ("186", "Labor experience multiplier"));
}
