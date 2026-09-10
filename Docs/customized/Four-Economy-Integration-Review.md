# Combined economy integration review

This branch assembles the accepted source for auction
[303](https://github.com/KeganHollern/aaemu-cluster/issues/303), player mail
[305](https://github.com/KeganHollern/aaemu-cluster/issues/305), trade
[306](https://github.com/KeganHollern/aaemu-cluster/issues/306), and housing
[308](https://github.com/KeganHollern/aaemu-cluster/issues/308).
The four operations share one persistence boundary: source items, containers,
wallets, mail, and auction state commit before success notifications. Housing
also enlists its property, tax offer, and furniture changes in that transaction.

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

## Completed integration

The character serializers now delete and acknowledge the same captured IDs only
when the enclosing transaction commits. Seven deletion queues across portals,
friends, blocked characters, skills, quests, and mates survive known rollback;
work queued after serialization remains pending for the next checkpoint.

The skill guard covers admission, final effects, and the entire asynchronous plot
lifetime. Trade reservations exclude those execution phases, and active execution
excludes new offers. No persistence lock is held while combat runs or a plot awaits.
`Cast` retains its existing behavior. Craft admission and final consumption use the
same reservation boundary, including cancellation and repeated crafting.

Trade uses an exact three-dimensional distance check. The legacy general distance
helper duplicated the Y coordinate and omitted height; only the trade call site
changes. Full and partial item exchanges retain item details, use frozen packet
counts, and restore both inventories and wallets on a known precommit failure.

The housing integration tests call the real purchase operation and save manager,
inject failures in the house row and late coffer-container deletion, retry the
purchase, and reload house, mail, items, and coffers through production readers.
Unit fixtures explicitly provide the world configuration and world registration
used by housing tax and coffer interactions.

The existing successful letter-return test moved from a mock unit fixture to the
real MySQL fixture because it now calls the durable player-send implementation.
Its existing post-send deletion behavior is preserved. This change does not
implement attachment-claim atomicity or expand the separate held claim work.

## Validation

The complete solution restores and builds in Release, including the integration
test project. Runtime Game script compilation passes with zero errors and warnings.
All 50 Content Studio tests pass. The focused suites pass: 88 skill cases,
53 trade settlement cases, 66 housing cases, 39 auction settlement cases,
76 player-send cases, 11 mail mutation cases, 29 inventory mutation cases,
and 23 remaining mailbox cases.

The final complete unit suite passes all 2,361 tests with no skips. The first
complete run identified only the subsequently corrected housing and mailbox
fixture failures. Hosted MySQL results are recorded in the pull request once
available. The existing GitHub workflow runs
all unit and Content Studio tests, runtime script compilation, and the
`Category=GameMySql` integration suite. All database failure-injection tests use
the disposable fixture; none run against live MySQL.

The tests cover SQL failure, retry, exact item identity and ownership, partial
stacks, reservation use, wallet limits, concurrent offers and purchases,
notification failures, uncertain commit handling, queued deletion acknowledgment,
and reload behavior. A thrown commit does not prove rollback, so prepared asset
state is preserved without sending success. Dirty bookkeeping is acknowledged
only after a successful commit.

This range changes no SQL schema or compact snapshot. The inspected live economy
and housing tables use InnoDB. No production database cleanup was required.
Signed client content, launcher selection, and Kubernetes manifests are outside
this source change. The deployment uses the ordinary paired image workflow and
always-running Keel controller; later human gameplay checks do not gate publication.

## Focused gameplay checks after deployment

- Auction: list an item with detailed attributes, cancel it as the seller, and
  verify the returned item; test a bid, an outbid refund, and a buyout with two
  characters. A different character must not cancel another seller's listing.
- Mail: send normal and express letters with copper and detailed items, then
  verify the fee, recipient, and contents after reconnecting.
- Trade: exchange partial stacks and copper, cancel once, and move out of range
  or to a different elevation before confirmation. Offered assets must remain
  reserved, and a rejected trade must leave both players' assets intact.
- Housing: list for a designated buyer, purchase once, and check seller proceeds,
  buyer tax ownership, retained furniture, and returned guest/bound coffer contents
  after reconnecting. Other characters must not list or cancel the property.
