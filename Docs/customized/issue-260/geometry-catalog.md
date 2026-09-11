# Quest doodad geometry catalog

The catalog contains geometry facts from the exact r208022 client archive.
It does not contain model meshes or textures.
The native contract record explains the consumer and the distance comparison.

`r208022-doodad-interactions.json` covers 94 distinct model URIs.
The input query includes quest acceptance sources, report sources, and all their phase model overrides.
Each entry gives its model URI, affected template IDs, source SHA-256, and local spheres.
Prefab entries also give the SHA-256 of each referenced Brush or Entity model.
The `clientSha256` field identifies the native client dump used for this research.
It is not the hash of `game_pak`.
The `compactSha256` field identifies the exact client compact used for the source query.
The client and server snapshots contain the same relevant model and source data.
A set comparison found no differences in 213 accept-doodad acts or 27 report-doodad acts.
It also found no differences in 7,124 doodad models, 20,077 phase models, or 753 actor height and radius rows.

## Extraction rules

CGF versions `0x744` and `0x745` use Node chunks `0x823` or `0x824`.
Helper chunks `0x744` contain a type and 3 size components.
The parser follows the [engine chunk definitions](https://github.com/aws/lumberyard/blob/master/dev/Code/CryEngine/CryCommon/CryHeaders.h).
The [engine loader](https://github.com/aws/lumberyard/blob/master/dev/Code/CryEngine/Cry3DEngine/StatObjLoad.cpp) converts HP_DUMMY sizes from centimeters to meters.
Native `39152fc0` uses the helper X size and a factor of `0.5` for `$aimpoint` spheres.
The node translation supplies the center.
The extractor composes each helper's parent transforms before conversion to meters.
The [engine CGF loader](https://github.com/aws/lumberyard/blob/master/dev/Code/CryEngine/Cry3DEngine/CGF/CGFLoader.cpp) stores local node matrices and composes their parents.
The static object loader supplies that world matrix to each root statObj helper.
The extractor rejects box helpers until a reviewed implementation supports them.

Native `3989d420` reads prefab Comment objects with names that start with `aimPoint`.
The Comment value supplies the radius in meters, without a factor of `0.5`.
The object position supplies the center.
Prefab Brush helpers also enter the shape group through native `390fe8b0`.
Their object transform changes the local CGF helper center and radius.
Only an empty final shape group gets the native sphere with center `(0,0,0)` and radius `0.5`.

The only direct CGF exception is the gate used by doodad `1521`.
Its local sphere center is `(-0.094283447265625, 0.01097970485687256, 3.1563427734375)` meters.
Its radius is `2.6463327026367188` meters.
The prefab `quest.ferre_machine1` also contains a transformed CGF helper and an explicit Comment sphere.
Many phase models contain more than 1 Comment sphere.
The runtime must use the minimum distance to those spheres.

The animated Entity in `quest_prop.quest_case` uses the full static root model for quest geometry.
Exact `cryanimation.dll` function `315e2c33` assigns that model to `CCharacterModel + 0x5c`.
Function `3160c530` returns it to the quest callback `390ff010`.
The callback does not read an animation pose.
The catalog thus includes the CGA helper with its full parent transform.
The native contract records the exact binary hash and calls.

## Unsupported sources

The archive lacks 2 old CGF paths for template `272`.
The catalog records those paths under `unsupportedModels`.
The runtime must not replace an unknown model with a guessed sphere.
The extractor does not infer geometry for an empty model URI.

## Repeat the extraction

Run these commands from the cluster workspace root.
Set the output path to the AAEmu source checkout under review.

```sh
.tools-re/venv/bin/python k8s/vendor/AAEmu/Docs/customized/issue-260/extract-doodad-interactions.py \
  --pak client/game_pak \
  --compact compact/client.sqlite3 \
  --pak-reader .agents/skills/aaemu-client-pak/scripts/aapak.py \
  --output k8s/vendor/AAEmu/AAEmu.Game/Models/Game/Quests/Data/r208022-doodad-interactions.json
python -B -m unittest discover -s k8s/vendor/AAEmu/Docs/customized/issue-260
```

Compare the result with the reviewed catalog before a later content release.
New model URIs, helpers, or unsupported entries need native review.
