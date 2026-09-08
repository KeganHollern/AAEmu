# Combined economy integration review

This branch assembles the accepted source for auction
[303](https://github.com/KeganHollern/aaemu-cluster/issues/303), player mail
[305](https://github.com/KeganHollern/aaemu-cluster/issues/305), trade
[306](https://github.com/KeganHollern/aaemu-cluster/issues/306), and housing
[308](https://github.com/KeganHollern/aaemu-cluster/issues/308).
It is an incomplete review checkpoint, not a merge or release candidate.

## Integrated source

The branch starts at `18af2de0696d69d7568c37d19574ae9b9934fad8`. The following
source commits were cherry-picked in order without manual conflicts:

| Source commit | Combined commit | Change |
| --- | --- | --- |
| `0a6e6dfac15ee68ec8b853053c68d52e1f7edee5` | `e7faa4a9c` | Mail staging and notification boundary |
| `bbdbb39991961a91eb6d591449f12e3ee4a08f37` | `50196599d` | Checked player mail settlement |
| `907a52a50eac60bc072421882431f2204123a69c` | `acf32e95f` | Trade reservations and exact inventory exchange |
| `a06b3d67b8c431222b7e55e7a417cd7af1704441` | `a03195ba4` | Shared strict persistence foundation |
| `22aeb195a3872ad79f10d05743764985aa795943` | `561fee594` | Player mail MySQL tests |
| `785b2f8288f3ce1b84daef9271fb145451728af9` | `875d16263` | Public player mail send wiring |
| `038feca2fb4604ee0b2c4f40d869c29e88af2ae9` | `6ba37d490` | Housing sale authority and listing checks |
| `e02ad6cde359f551e9cc4e0bbdd5a71dcdf33d72` | `02d92ed16` | House ownership recheck during character deletion |
| `2f18cda3172b009e69ea561e60f5994648064397` | `abc9e2acd` | Auction settlement checkpoint |
| `90e2603c4a148b61e5c004fd9dbe15394c4c262c` | `5fcf818ec` | Auction reservation regression tests |
| `f243c21a974b1aa3e82092d1ae05c9128f344150` | `bfd05e254` | Craft admission and completion reservation guards |
| `7267ec763866d5c1814eaa0d7f383e62b32103fd` | `fdbe37be5` | Player trade settlement and tests |
| `8573b449d731398b16c35b9e7472cf2cb12d6b3c` | `d65a07adc` | Durable housing and furniture settlement |
| `fad3b1d57e701d1c6c1a1540b3dd71a45e827243` | `100f8c334` | Mail test fixture and public entry-point coverage |

The foundation and craft commits that were missing from individual feature
branches are present here. Their individual review records remain descriptions
of those earlier checkpoints; this combined record states the current integration
status.

Routine integration fixes update the economy persistence fixture to supply the
auction manager's mail and lazy save dependencies, narrow the trade tests'
`ShutdownTask` imports to avoid colliding with `System.Threading.Tasks.Task`, and
match reservation money assertions to the helper's integer result. These fixes
do not replace or bypass missing production APIs.

The mail follow-up moves the legacy payment and missing-recipient assertions into
the executor's real-inventory fixture. Its nine pending MySQL cases now call
`CharacterMails.SendMailToPlayer` with the actual save dependency and test that
forged attachment counts and extra header metadata are ignored.

## Remaining approval blockers

The six character serializers `CharacterPortals`, `CharacterFriends`,
`CharacterBlocked`, `CharacterSkills`, `CharacterQuests`, and `CharacterMates`
still require the proposed `Save(PersistenceSaveContext)` overloads. They currently
clear pending deletion queues before the surrounding transaction commits.
Automatic approval review rejected the proposed acknowledgment changes, and the
user has not yet answered the follow-up request. Those six files are unchanged
from the base commit.

Trade also requires `Skill.IsExecuting(Character)` and the corresponding atomic
skill execution guard. Automatic approval review rejected the proposed core skill
and asynchronous plot changes. `Skill.cs` is unchanged from the base commit.
The accepted craft guard is included, but its `CancelFromSkill` hook remains
unwired until the skill change is approved and implemented.

No rejected source changes, substitute implementations, or dependency stubs were
applied. These blockers must be resolved before compilation and integrated tests
can complete.

## Validation evidence and limits

The combined preflight command was:

```text
dotnet build AAEmu.UnitTests/AAEmu.UnitTests.csproj --nologo
```

Project restoration succeeded. The Game dependency compilation reported exactly
seven errors: six missing character `Save(PersistenceSaveContext)` overloads and
the missing `Skill.IsExecuting` method. No other Game compiler errors were
reported. Six analyzer warnings remain: two in `DoodadAreaTriggerRegistry`, two
in `DuelManager`, one in `ItemManager`, and one redundant connection-null check
in `HousingManager`. Unit-test project compilation was not reached.

Constructor and changed-call-site inspection found no remaining auction or trade
constructor mismatch after the fixture correction. The removed
`MailPlayerToPlayer` class has no remaining C# references, and trade cancellation
callers use the new character-identity signature. Issue links point to the cluster
repository. `git diff --check` passes.

Earlier focused runs provide limited provenance, not integrated release evidence:

| Scope | Prior evidence |
| --- | --- |
| Inventory exchange helpers | 29 focused tests passed before this combined build |
| Player mail and mailbox fixtures | 76 executor cases and 24 remaining mailbox cases passed using the previously built helper assembly; these runs do not validate the new public wrapper |
| Craft guard | Release build and 18 focused `CharacterCraft*Tests` passed |
| Housing authorization layer | 51 housing tests and runtime script compilation passed before durable settlement was added |
| Shared persistence, trade, auction, and durable housing | New feature and MySQL regression suites are authored; integrated compilation and execution remain pending |

The MySQL suites use the existing disposable GameMySql fixture. They cover
failure, retry, item identity and ownership, wallets, mail, auctions, housing,
furniture, deletion queues, and reload behavior. Local Docker's API is older than
the fixture's Testcontainers requirement; hosted fixture execution remains
pending after compilation is unblocked.

There are no compact updates, schema migrations, production database operations,
image publications, or deployments in this integration checkpoint. The held mail
attachment claim work remains separate. Human gameplay validation has not been
performed. After approval, complete the missing implementations, compile both test
projects, run the focused and hosted MySQL suites, and review the complete result
before any merge or publication.
