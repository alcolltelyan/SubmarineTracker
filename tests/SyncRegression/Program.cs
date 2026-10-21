using System.Collections;
using System.Data.SQLite;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MessagePack;
using Newtonsoft.Json;
using SubmarineTracker;
using SubmarineTracker.Data;
using SubmarineTracker.Manager;
using Loot = SubmarineTracker.Loot;

// Executes the built production assembly. Dalamud services and HTTP transport are
// replaced at the boundary; no game, live config, database, or webhook is modified.
internal static class Program
{
    const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    static readonly string Work = Path.Combine(Root, ".codex-dotnet/sync-validation/regression");
    static int passed;
    static readonly List<string> Errors = [];

    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
        passed++;
    }

    static object? Call(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, PrivateInstance)!.Invoke(target, args);
    static void SetService(string name, object value) => typeof(Plugin).GetProperty(name)!.SetValue(null, value);
    static T Proxy<T>(Func<MethodInfo, object?[]?, object?> invoke) where T : class
    {
        var proxy = DispatchProxy.Create<T, ServiceProxy>();
        ((ServiceProxy)(object)proxy).Handler = invoke;
        return proxy;
    }
    static object? Default(MethodInfo method) => method.ReturnType == typeof(void) ? null :
        method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
    static void SetDirectory(string path)
    {
        Directory.CreateDirectory(path);
        SetService("PluginInterface", Proxy<IDalamudPluginInterface>((m, _) => m.Name switch
        {
            "get_ConfigDirectory" => new DirectoryInfo(path),
            "get_ConfigFile" => new FileInfo(Path.Combine(path, "config.json")),
            _ => Default(m)
        }));
    }
    static Database OpenDatabase() => (Database)Activator.CreateInstance(typeof(Database), true)!;
    static SQLiteConnection Connect(string path)
    {
        var connection = new SQLiteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }
    static long Scalar(SQLiteConnection c, string sql)
    {
        using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    static async Task Main()
    {
        Directory.CreateDirectory(Work);
        SetService("Log", Proxy<IPluginLog>((m, a) =>
        {
            if (m.Name == "Error") Errors.Add(string.Join(" ", a ?? []));
            return Default(m);
        }));
        SetService("ClientState", Proxy<IClientState>((m, _) => m.Name == "get_IsLoggedIn" ? true : Default(m)));
        SetDirectory(Path.Combine(Work, Guid.NewGuid().ToString("N")));
        var config = new Configuration();
        SetService("Configuration", config);
        Check(config.CustomLootHours == 12 && !config.WebhookLootProcessed && config.WebhookLootProcessedProfile == "Default", "fork setting defaults");
        Check((int)DateLimit.CustomHours == 99, "persisted custom-hours enum");
        foreach (var hours in new[] { -1, 0, 1, 12, 48 })
        {
            config.CustomLootHours = hours;
            var before = DateTime.UtcNow.AddHours(-Math.Max(1, hours));
            var boundary = DateLimit.CustomHours.ToDate();
            var after = DateTime.UtcNow.AddHours(-Math.Max(1, hours));
            Check(boundary >= before && boundary <= after && boundary.Kind == DateTimeKind.Utc, $"rolling UTC boundary / clamp {hours}");
        }
        var first = DateLimit.CustomHours.ToDate();
        await Task.Delay(30);
        Check(DateLimit.CustomHours.ToDate() > first, "boundary advances without reselecting");
        var window = (SubmarineTracker.Windows.Loot.LootWindow)RuntimeHelpers.GetUninitializedObject(typeof(SubmarineTracker.Windows.Loot.LootWindow));
        config.DateLimit = DateLimit.CustomHours;
        var cutoff = DateLimit.CustomHours.ToDate();
        Check(!window.DateCompare(cutoff.AddSeconds(-1)) && window.DateCompare(cutoff.AddSeconds(1)), "production loot filter excludes older and includes newer custom-hour records");
        config.DateLimit = DateLimit.None;
        typeof(SubmarineTracker.Windows.Loot.LootWindow).GetField("CustomMinDate", PrivateInstance)!.SetValue(window, cutoff);
        typeof(SubmarineTracker.Windows.Loot.LootWindow).GetField("CustomMaxDate", PrivateInstance)!.SetValue(window, cutoff.AddHours(1));
        Check(window.DateCompare(cutoff) && window.DateCompare(cutoff.AddHours(1)) && !window.DateCompare(cutoff.AddTicks(-1)) && !window.DateCompare(cutoff.AddHours(1).AddTicks(1)), "production manual date boundaries stay inclusive");
        config.CustomLootHours = 37; config.DateLimit = DateLimit.CustomHours;
        config.WebhookLootProcessed = true; config.WebhookLootProcessedProfile = "Test";
        config.CustomLootProfiles["Test"] = new() { [1] = 2000000000, [2] = 3 };
        config.AccountFCs["Account"] = [42];
        config.Save();
        var roundTrip = JsonConvert.DeserializeObject<Configuration>(File.ReadAllText(Plugin.PluginInterface.ConfigFile.FullName), Configuration.SerializerSettings)!;
        Check(roundTrip.CustomLootHours == 37 && roundTrip.DateLimit == DateLimit.CustomHours && roundTrip.WebhookLootProcessed && roundTrip.WebhookLootProcessedProfile == "Test" && roundTrip.CustomLootProfiles["Test"][1] == 2000000000 && roundTrip.AccountFCs["Account"].Single() == 42, "real config Save and JSON round trip, fork plus upstream fields");
        var oldConfig = JsonConvert.DeserializeObject<Configuration>("{\"CustomLootHours\":24,\"WebhookLootProcessed\":true,\"WebhookLootProcessedProfile\":\"Existing\"}")!;
        Check(oldConfig.CustomLootHours == 24 && oldConfig.WebhookLootProcessed && oldConfig.AccountFCs.Count == 0 && oldConfig.ShowResourcesInAll && oldConfig.SwapResourcesInAll, "old config retains fork settings and initializes upstream defaults");
        var backedUpConfig = Path.Combine(Root, ".codex-dotnet/sync-validation/pre-runtime/SubmarineTracker.json");
        var existing = JsonConvert.DeserializeObject<Configuration>(File.ReadAllText(backedUpConfig), Configuration.SerializerSettings)!;
        var existingAgain = JsonConvert.DeserializeObject<Configuration>(JsonConvert.SerializeObject(existing, Configuration.SerializerSettings), Configuration.SerializerSettings)!;
        Check(existingAgain.CustomLootHours == existing.CustomLootHours && existingAgain.DateLimit == existing.DateLimit && existingAgain.WebhookLootProcessed == existing.WebhookLootProcessed && existingAgain.WebhookLootProcessedProfile == existing.WebhookLootProcessedProfile && JsonConvert.SerializeObject(existingAgain.CustomLootProfiles) == JsonConvert.SerializeObject(existing.CustomLootProfiles), "actual backed-up configuration retains fork settings and profiles across round trip");

        TestMigrations();
        await TestWebhooks(config);
        if (Errors.Count != 0) throw new Exception("Production error logs: " + string.Join(Environment.NewLine, Errors));
        Check(Errors.Count == 0, "no production error logs during regression checks");
        Console.WriteLine($"{passed} checks passed");
    }

    static void TestMigrations()
    {
        // Build pre-upgrade schemas using production migration methods, then
        // reopen through the real constructor/migration dispatcher.
        foreach (var version in new[] { 0, 1, 2, 3 })
        {
            var dir = Path.Combine(Work, "schema-" + version + "-" + Guid.NewGuid().ToString("N"));
            SetDirectory(dir);
            var path = Database.DatabasePath();
            using (var connection = Connect(path))
            {
                var uninitialized = (Database)RuntimeHelpers.GetUninitializedObject(typeof(Database));
                typeof(Database).GetField("<Connection>k__BackingField", PrivateInstance)!.SetValue(uninitialized, connection);
                for (var migration = 0; migration <= version; migration++) Call(uninitialized, "Migrate" + migration);
                // Insert sentinel data via actual write APIs where the schema supports it.
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "INSERT INTO freecompany (FreeCompanyId,FreeCompanyTag,World,CharacterName,UnlockedSectors,ExploredSectors) VALUES (X'2A','TEST','World','Character',X'80',X'80');";
                cmd.ExecuteNonQuery();
                Call(uninitialized, "UpsertSubmarine", new Submarine { FreeCompanyId = 42, Register = 123, Name = "Sentinel", Points = [1, 2] });
                Call(uninitialized, "InsertLootEntry", new Loot { FreeCompanyId = 42, Register = 123, Primary = 1, PrimaryCount = 2, Date = DateTime.UtcNow, Valid = true });
            }
            using (var database = OpenDatabase())
            {
                var fcs = ((IEnumerable)Call(database, "GetFreeCompanies")!).Cast<FreeCompany>().ToArray();
                Check(fcs.Single().FreeCompanyId == 42 && fcs.Single().ContentId == 0, $"schema {version}: FC data and ContentId default survive migration");
                Check(((IEnumerable)Call(database, "GetSubmarines")!).Cast<Submarine>().Single().Name == "Sentinel", $"schema {version}: submarine preserved");
                Check(((IEnumerable)Call(database, "GetLoot")!).Cast<Loot>().Single().PrimaryCount == 2, $"schema {version}: loot preserved");
                Call(database, "UpsertStorage", new StorageData { FreeCompanyId = 42, Items = new() { [101] = 20 } });
                Call(database, "UpsertStorage", new StorageData { FreeCompanyId = 42, Items = new() { [101] = 30 } });
            }
            using (var database = OpenDatabase())
                Check(((IEnumerable)Call(database, "GetStorage")!).Cast<StorageData>().Single().Items[101] == 30, $"schema {version}: storage upsert persists across reopen");
            using var finalConnection = Connect(path);
            var expectedCounter = version == 0 ? 0 : 1; // Schema 0 has no counter/trigger before migration 1.
            Check(Scalar(finalConnection, "PRAGMA user_version") == 3 && Scalar(finalConnection, "SELECT Count FROM counters WHERE Key='Loot'") == expectedCounter, $"schema {version}: migration version and existing counter semantics preserved");
            using (var database = OpenDatabase())
                Call(database, "InsertLootEntry", new Loot { FreeCompanyId = 42, Register = 123, Primary = 1, PrimaryCount = 1, Date = DateTime.UtcNow, Valid = true });
            Check(Scalar(finalConnection, "SELECT Count FROM counters WHERE Key='Loot'") == expectedCounter + 1, $"schema {version}: loot counter trigger still increments after upgrade");
        }
        // A fresh DB exercises the constructor's <= 0 migration route too.
        SetDirectory(Path.Combine(Work, "fresh-" + Guid.NewGuid().ToString("N")));
        using (var fresh = OpenDatabase()) { }
        using (var connection = Connect(Database.DatabasePath()))
            Check(Scalar(connection, "PRAGMA user_version") == 3, "fresh database reaches latest schema");

        // Migrate a copy of the stopped-plugin backup; never the live database.
        var backup = Path.Combine(Root, ".codex-dotnet/sync-validation/pre-runtime/SubmarineTracker/submarine-sqlite.db");
        if (!File.Exists(backup)) throw new Exception("Stopped-plugin database backup is required");
        var copyDir = Path.Combine(Work, "real-copy-" + Guid.NewGuid().ToString("N"));
        SetDirectory(copyDir);
        File.Copy(backup, Database.DatabasePath());
        var snapshots = new Dictionary<string, List<string>>();
        var originalColumns = new Dictionary<string, int>();
        using (var connection = Connect(Database.DatabasePath()))
            foreach (var table in new[] { "freecompany", "submarine", "loot", "counters" })
            {
                using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT * FROM " + table + " LIMIT 0";
                using var reader = cmd.ExecuteReader(); originalColumns[table] = reader.FieldCount;
                snapshots[table] = Rows(connection, table);
            }
        using (var migrated = OpenDatabase()) { }
        using (var connection = Connect(Database.DatabasePath()))
        {
            foreach (var table in snapshots.Keys) Check(snapshots[table].SequenceEqual(Rows(connection, table, originalColumns[table])), "real database copy: every existing " + table + " row preserved");
            using var cmd = connection.CreateCommand(); cmd.CommandText = "PRAGMA integrity_check";
            Check((string)cmd.ExecuteScalar()! == "ok", "real database copy integrity");
        }
        using (var reopened = OpenDatabase()) { }
    }

    static List<string> Rows(SQLiteConnection c, string table, int? columns = null)
    {
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT * FROM " + table;
        using var reader = cmd.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) rows.Add(JsonConvert.SerializeObject(Enumerable.Range(0, columns ?? reader.FieldCount).Select(reader.GetValue)));
        rows.Sort(StringComparer.Ordinal); return rows;
    }

    static async Task TestWebhooks(Configuration config)
    {
        var handler = new FakeHandler();
        typeof(HttpMessageInvoker).GetField("_handler", PrivateInstance)!.SetValue(Webhook.SharedClient, handler);
        var post = typeof(Webhook).GetMethod("PostMessageAsync", PrivateStatic)!;
        async Task Send() => await (Task)post.Invoke(null, [new Webhook.WebhookContent()])!;
        void Reset(params HttpStatusCode[] statuses)
        {
            handler.Statuses = new(statuses); handler.Bodies.Clear(); handler.Times.Clear();
            typeof(Webhook).GetField("NextSendAtUtc", PrivateStatic)!.SetValue(null, DateTimeOffset.MinValue);
        }
        config.WebhookUrl = "https://invalid.example/test";
        foreach (var success in new[] { HttpStatusCode.OK, HttpStatusCode.NoContent })
        {
            Reset(success); await Send(); Check(handler.Bodies.Count == 1, "C# webhook success " + success);
        }
        Reset(HttpStatusCode.TooManyRequests, HttpStatusCode.NoContent); await Send();
        Check(handler.Bodies.Count == 2 && handler.Bodies.Distinct().Count() == 1 && (handler.Times[1] - handler.Times[0]).TotalMilliseconds >= 240, "C# 429 retries same payload with safety delay");
        Reset(Enumerable.Repeat(HttpStatusCode.TooManyRequests, 4).ToArray()); await Send();
        Check(handler.Bodies.Count == 4, "C# retry exhaustion is four total attempts");
        Reset(HttpStatusCode.BadRequest); await Send(); Check(handler.Bodies.Count == 1, "C# terminal HTTP error does not retry");
        Reset(HttpStatusCode.TooManyRequests, HttpStatusCode.NoContent); handler.Malformed = true; await Send(); handler.Malformed = false;
        Check((handler.Times[1] - handler.Times[0]).TotalMilliseconds >= 950, "C# malformed 429 uses one-second fallback");
        Reset(HttpStatusCode.NoContent, HttpStatusCode.NoContent); await Task.WhenAll(Send(), Send());
        Check((handler.Times[1] - handler.Times[0]).TotalMilliseconds >= 480, "C# concurrent messages retain serialized 500ms throttle");

        SetDirectory(Path.Combine(Work, "reports-" + Guid.NewGuid().ToString("N")));
        // Avoid the cache constructor's game-sheet aggregation: the report and
        // interval checks need only controlled cache state and a disposable DB.
        using var cache = (DatabaseCache)RuntimeHelpers.GetUninitializedObject(typeof(DatabaseCache));
        typeof(DatabaseCache).GetField("Database")!.SetValue(cache, OpenDatabase());
        typeof(DatabaseCache).GetField("FreeCompanies", PrivateInstance)!.SetValue(cache, new Dictionary<ulong, FreeCompany>());
        Plugin.DatabaseCache = cache;
        typeof(DatabaseCache).GetField("FCRefresh", PrivateInstance)!.SetValue(cache, long.MaxValue);
        var lootWindow = (SubmarineTracker.Windows.Loot.LootWindow)RuntimeHelpers.GetUninitializedObject(typeof(SubmarineTracker.Windows.Loot.LootWindow));
        var beforeRefresh = Environment.TickCount64;
        Call(lootWindow, "BuildCache", new Dictionary<uint, int>());
        var refreshAt = (long)typeof(SubmarineTracker.Windows.Loot.LootWindow).GetField("LastRefreshTime", PrivateInstance)!.GetValue(lootWindow)!;
        Check(refreshAt >= beforeRefresh + 5000 && refreshAt <= Environment.TickCount64 + 5000, "production custom-profile cache schedules 5-second rebuild");
        Call(lootWindow, "BuildCache", new Dictionary<uint, int>());
        Check((long)typeof(SubmarineTracker.Windows.Loot.LootWindow).GetField("LastRefreshTime", PrivateInstance)!.GetValue(lootWindow)! == refreshAt, "custom-profile cache avoids premature rebuild");
        typeof(DatabaseCache).GetField("Submarines", PrivateInstance)!.SetValue(cache, new[] { new Submarine { FreeCompanyId = 42, Register = 123, Name = "TestSub" } });
        typeof(DatabaseCache).GetField("FreeCompanies", PrivateInstance)!.SetValue(cache, new Dictionary<ulong, FreeCompany> { [42] = new() { FreeCompanyId = 42, Tag = "Test", World = "World" } });
        foreach (var field in new[] { "FCRefresh", "SubRefresh" }) typeof(DatabaseCache).GetField(field, PrivateInstance)!.SetValue(cache, long.MaxValue);
        Check((long)typeof(DatabaseCache).GetField("LongDelay", PrivateStatic)!.GetRawConstantValue()! == 5000, "loot database cache retains 5-second interval");
        var report = typeof(HookManager).GetMethod("SendLootProcessedWebhook", PrivateStatic)!;
        var loot = new List<Loot> { new() { Primary = 1, PrimaryCount = 3, Additional = 2, AdditionalCount = 5 } };
        async Task Report(uint key)
        {
            report.Invoke(null, [loot, (ulong)42, (uint)123, key]);
            // The real method queues a background send; wait until its throttle settles.
            await Task.Delay(650);
        }
        Reset(); config.WebhookLootProcessedProfile = "Test"; await Report(1);
        Check(handler.Bodies.Single().Contains("6,000,000,015") || handler.Bodies.Single().Contains(6000000015L.ToString("N0")), "loot report uses 64-bit primary plus additional valuation");
        Reset(); config.CustomLootProfiles["Default"] = new() { [1] = 7 }; config.WebhookLootProcessedProfile = "Missing"; await Report(2);
        Check(handler.Bodies.Single().Contains("21 gil") && handler.Bodies.Single().Contains("Profile: Default"), "loot report missing profile falls back to Default");
        Reset(); config.CustomLootProfiles.Remove("Default"); await Report(3);
        Check(handler.Bodies.Single().Contains("0 gil"), "loot report missing Default yields zero");
        Reset(); config.WebhookOfflineMode = true; await Report(4); config.WebhookOfflineMode = false;
        Check(handler.Bodies.Count == 0, "offline mode suppresses loot report");
        Reset(); config.WebhookUrl = "http://invalid.example"; await Report(5); config.WebhookUrl = "https://invalid.example";
        Check(handler.Bodies.Count == 0, "non-HTTPS URL suppresses loot report");
        Reset(); SetService("ClientState", Proxy<IClientState>((m, _) => m.Name == "get_IsLoggedIn" ? false : Default(m))); await Report(6);
        Check(handler.Bodies.Count == 0, "logged-out client suppresses loot report");
        SetService("ClientState", Proxy<IClientState>((m, _) => m.Name == "get_IsLoggedIn" ? true : Default(m)));
        Reset(); await Report(3); Check(handler.Bodies.Count == 0, "duplicate voyage report suppressed within process");
    }

    sealed class FakeHandler : HttpMessageHandler
    {
        public Queue<HttpStatusCode> Statuses = new();
        public List<string> Bodies = [];
        public List<DateTimeOffset> Times = [];
        public bool Malformed;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken)); Times.Add(DateTimeOffset.UtcNow);
            var status = Statuses.TryDequeue(out var next) ? next : HttpStatusCode.NoContent;
            return new(status) { Content = new StringContent(Malformed ? "invalid" : "{\"retry_after\":0.001,\"message\":\"test\"}") };
        }
    }
}

public class ServiceProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
}
