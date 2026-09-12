using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AAEmu.Commons.Models;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Items;
using MySql.Data.MySqlClient;
using Xunit;

namespace AAEmu.IntegrationTests.Core.Manager;

public sealed partial class AuctionMailClaimStoreTests
{
    [Theory]
    [InlineData("sale", false)]
    [InlineData("sale", true)]
    [InlineData("buy", false)]
    [InlineData("buy", true)]
    [InlineData("merge", false)]
    [InlineData("merge", true)]
    public async Task ProcessCrash_BeforeOrAfterCommit_RetriesWithoutDuplicateAssets(string kind, bool afterCommit)
    {
        for (var cycle = 0; cycle < 2; cycle++)
        {
            if (cycle != 0)
                await InitializeAsync();
            var plan = CrashPlan(kind);
            await SeedMailAsync(plan.Mail);
            if (plan is AuctionBuyClaimPlan buy)
            {
                await ExecuteAsync("""
                    INSERT INTO items (id,type,template_id,slot_type,slot,count,lifespan_mins,owner,flags,grade)
                    VALUES (30001,'AAEmu.Game.Models.Game.Items.Item',20,5,0,5,0,7,0,2);
                    """);
                if (buy.DestinationStack != null)
                    await ExecuteAsync("""
                        INSERT INTO items (id,type,template_id,slot_type,slot,count,lifespan_mins,owner,flags,grade)
                        VALUES (30002,'AAEmu.Game.Models.Game.Items.Item',20,2,2,10,0,7,0,2);
                        """);
            }
            var marker = Path.Combine(AppContext.BaseDirectory, "TestResults", "auction-crash-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            var gateName = "auction_claim_" + Guid.NewGuid().ToString("N");
            using var gate = MySQL.CreateConnection();
            using var gateCommand = gate.CreateCommand();
            gateCommand.CommandText = "SELECT GET_LOCK(@gate, 0)";
            gateCommand.Parameters.AddWithValue("@gate", gateName);
            Assert.Equal(1, Convert.ToInt32(await gateCommand.ExecuteScalarAsync()));
            Process child = null;
            try
            {
                if (!afterCommit)
                {
                    // A nontransactional probe records entry into the real transaction.
                    // It is private test state and never appears in production SQL.
                    await ExecuteAsync("CREATE TABLE auction_claim_crash_probe (entered INT NOT NULL) ENGINE=MyISAM");
                    await ExecuteAsync($"""
                        CREATE TRIGGER pause_auction_claim BEFORE UPDATE ON mails FOR EACH ROW
                        BEGIN
                            INSERT INTO auction_claim_crash_probe VALUES (1);
                            DO GET_LOCK('{gateName}', 30);
                        END
                        """);
                }
                var connectionString = (string)typeof(MySQL)
                    .GetField("s_connectionString", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                var start = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    WorkingDirectory = AppContext.BaseDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                if (Path.GetFileNameWithoutExtension(Environment.ProcessPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(typeof(AuctionMailClaimStoreTests).Assembly.Location);
                start.Environment[AuctionClaimCrashWorker.EnvironmentKey] = JsonSerializer.Serialize(
                    new AuctionClaimCrashWorker.Request(connectionString, kind, marker));
                child = Process.Start(start)!;
                var output = child.StandardOutput.ReadToEndAsync();
                var errors = child.StandardError.ReadToEndAsync();
                var timeout = Stopwatch.StartNew();
                while (afterCommit ? !File.Exists(marker) : await ScalarAsync("SELECT COUNT(*) FROM auction_claim_crash_probe") == 0)
                {
                    Assert.False(child.HasExited, "The claim worker exited before the crash point.");
                    Assert.True(timeout.Elapsed < TimeSpan.FromSeconds(20), "The claim worker did not reach the crash point.");
                    await Task.Delay(25, TestContext.Current.CancellationToken);
                }
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(TestContext.Current.CancellationToken);
                await Task.WhenAll(output, errors);
                gateCommand.CommandText = "SELECT RELEASE_LOCK(@gate)";
                await gateCommand.ExecuteScalarAsync();
                if (!afterCommit)
                {
                    // This waits for the disconnected worker's statement to release its metadata lock.
                    await ExecuteAsync("DROP TRIGGER pause_auction_claim; DROP TABLE auction_claim_crash_probe");
                }
                var restarted = new MySqlAuctionMailClaimStore();
                Assert.Equal(afterCommit ? plan.Receipt : null, restarted.FindReceipt(plan.Mail.Id, 7));
                Assert.Equal(afterCommit ? 1 : 0, await ScalarAsync("SELECT COUNT(*) FROM auction_mail_claims"));
                if (kind == "sale")
                {
                    Assert.Equal(afterCommit ? 1250 : 1000, await ScalarAsync("SELECT money FROM characters WHERE id=7"));
                    Assert.Equal(afterCommit ? 9 : 10, await ScalarAsync("SELECT labor FROM accounts WHERE account_id=3"));
                }
                Assert.Equal(afterCommit ? AuctionMailClaimPersistenceResult.Replay : AuctionMailClaimPersistenceResult.Created,
                    restarted.Persist(plan, LoadAchievements(plan.Character)));
                Assert.Equal(AuctionMailClaimPersistenceResult.Replay,
                    new MySqlAuctionMailClaimStore().Persist(CrashPlan(kind), LoadAchievements(plan.Character)));
                Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM auction_mail_claims"));
                Assert.Equal(0, await ScalarAsync("SELECT attachment_count FROM mails"));
                if (kind == "sale")
                {
                    Assert.Equal(1250, await ScalarAsync("SELECT money FROM characters WHERE id=7"));
                    Assert.Equal(9, await ScalarAsync("SELECT labor FROM accounts WHERE account_id=3"));
                    Assert.Equal(1, await ScalarAsync("SELECT consumed_lp FROM characters WHERE id=7"));
                }
                else
                {
                    Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM items"));
                    Assert.Equal(kind == "merge" ? 15 : 5, await ScalarAsync("SELECT count FROM items"));
                    Assert.Equal((int)SlotType.Inventory, await ScalarAsync("SELECT slot_type FROM items"));
                }
            }
            finally
            {
                if (child != null)
                {
                    if (!child.HasExited)
                        child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                    child.Dispose();
                }
                gateCommand.CommandText = "SELECT RELEASE_LOCK(@gate)";
                await gateCommand.ExecuteScalarAsync();
                await ExecuteAsync("DROP TRIGGER IF EXISTS pause_auction_claim; DROP TABLE IF EXISTS auction_claim_crash_probe");
                File.Delete(marker);
            }
        }
    }

    private static AuctionMailClaimPlan CrashPlan(string kind) => kind == "sale" ? CreateSalePlan() : CreateBuyPlan(kind == "merge");

    internal static void RunCrashWorker(string kind, string marker)
    {
        var plan = CrashPlan(kind);
        new MySqlAuctionMailClaimStore().Persist(plan, LoadAchievements(plan.Character));
        File.WriteAllText(marker, "committed");
        Thread.Sleep(Timeout.Infinite);
    }
}

// The generated test executable doubles as a disposable claim worker. No production
// assembly or server entry point contains a crash switch.
internal static class AuctionClaimCrashWorker
{
    internal const string EnvironmentKey = "AAEMU_AUCTION_CLAIM_CRASH_WORKER";
    internal sealed record Request(string Connection, string Kind, string Marker);

#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Start()
#pragma warning restore CA2255
    {
        var input = Environment.GetEnvironmentVariable(EnvironmentKey);
        if (input == null)
            return;
        var request = JsonSerializer.Deserialize<Request>(input)!;
        var settings = new MySqlConnectionStringBuilder(request.Connection);
        MySQL.SetConfiguration(new MySqlConnectionSettings
        {
            Host = settings.Server, Port = (ushort)settings.Port, User = settings.UserID,
            Password = settings.Password, Database = settings.Database
        });
        AuctionMailClaimStoreTests.RunCrashWorker(request.Kind, request.Marker);
        Environment.Exit(0);
    }
}
